using System.Collections.Generic;
using System.Net.Sockets;
using System.Linq;
using System.Net;
using System;
using System.Threading.Tasks;
using Phantasma.Cryptography;
using Phantasma.Core;
using Phantasma.Core.Log;
using Phantasma.Numerics;
using Phantasma.Blockchain;
using Phantasma.Network.P2P.Messages;
using Phantasma.Contracts.Native;
using Phantasma.Core.Utils;
using Phantasma.Domain;
using System.Threading;

namespace Phantasma.Network.P2P
{
    public enum EndpointStatus
    {
        Waiting,
        Disabled,
        Connected,
    }

    public class EndpointEntry
    {
        public readonly Endpoint endpoint;
        public DateTime lastPing;
        public int pingDelay;
        public EndpointStatus status;

        public EndpointEntry(Endpoint endpoint)
        {
            this.endpoint = endpoint;
            this.lastPing = DateTime.UtcNow;
            this.pingDelay = 32;
            this.status = EndpointStatus.Waiting;
        }
    }

    public sealed partial class Node: Runnable
    {
        public readonly static int MaxActiveConnections = 64;

        public readonly string Version;

        public readonly int Port;
        public Address Address => Keys.Address;

        public readonly PhantasmaKeys Keys;
        public readonly Logger Logger;

        public IEnumerable<Peer> Peers => _peers;

        private Mempool _mempool;

        private List<Peer> _peers = new List<Peer>();

        private TcpClient client;
        private TcpListener listener;

        private List<EndpointEntry> _knownEndpoints = new List<EndpointEntry>();

        private bool listening = false;

        public Nexus Nexus { get; private set; }

        public BigInteger MinimumFee => _mempool.MinimumFee;
        public uint MinimumPoW => _mempool.GettMinimumProofOfWork();

        private Dictionary<string, uint> _receipts = new Dictionary<string, uint>();
        private Dictionary<Address, Cache<Event>> _events = new Dictionary<Address, Cache<Event>>();

        public readonly string PublicIP;
        public readonly PeerCaps Capabilities;

        public Node(string version, Nexus nexus, Mempool mempool, PhantasmaKeys keys, int port, PeerCaps caps, IEnumerable<string> seeds, Logger log)
        {
            Throw.If(keys.Address != mempool.ValidatorAddress, "invalid mempool");

            this.Version = version;
            this.Nexus = nexus;
            this.Port = port;
            this.Keys = keys;
            this.Capabilities = caps;

            if (Capabilities.HasFlag(PeerCaps.Events))
            {
                this.Nexus.AddPlugin(new NodePlugin(this));
            }

            if (Capabilities.HasFlag(PeerCaps.Mempool))
            {
                Throw.IfNull(mempool, nameof(mempool));
                this._mempool = mempool;
            }
            else
            {
                this._mempool = null;
            }

            // obtains the public IP of the node. This might not be the most sane way to do it...
            this.PublicIP = new WebClient().DownloadString("http://icanhazip.com").Trim();
            Throw.IfNullOrEmpty(PublicIP, nameof(PublicIP));

            this.Logger = Logger.Init(log);

            QueueEndpoints(seeds.Select(seed => ParseEndpoint(seed)));

            // TODO this is a security issue, later change this to be configurable and default to localhost
            var bindAddress = IPAddress.Any;

            listener = new TcpListener(bindAddress, port);
            client = new TcpClient();
        }

        private void QueueEndpoints(IEnumerable<Endpoint> endpoints)
        {
            Throw.IfNull(endpoints, nameof(endpoints));

            if (!endpoints.Any())
            {
                return;
            }

            lock (_knownEndpoints)
            {
                foreach (var endpoint in endpoints)
                {
                    var entry = new EndpointEntry(endpoint);
                    _knownEndpoints.Add(entry);
                }
            }
        }

        public Endpoint ParseEndpoint(string src)
        {
            int port;

            if (src.Contains(":"))
            {
                var temp = src.Split(':');
                Throw.If(temp.Length != 2, "Invalid endpoint format");
                src = temp[0];
                port = int.Parse(temp[1]);
            }
            else
            {
                port = this.Port;
            }

            IPAddress ipAddress;

            if (!IPAddress.TryParse(src, out ipAddress))
            {
                if (Socket.OSSupportsIPv6)
                {
                    if (src == "localhost")
                    {
                        ipAddress = IPAddress.IPv6Loopback;
                    }
                    else
                    {
                        ipAddress = Endpoint.ResolveAddress(src, AddressFamily.InterNetworkV6);
                    }
                }
                if (ipAddress == null)
                {
                    ipAddress = Endpoint.ResolveAddress(src, AddressFamily.InterNetwork);
                }
            }

            if (ipAddress == null)
            {
                throw new Exception("Invalid address: " + src);
            }
            else
            {
                src = ipAddress.ToString();
            }

            return new Endpoint(PeerProtocol.TCP, src, port);
        }

        private DateTime _lastPeerConnect = DateTime.MinValue;

        protected override bool Run()
        {
            Thread.Sleep(1000);

            if (this.Capabilities.HasFlag(PeerCaps.Sync))
            {
                if (!listening)
                {
                    listening = true;
                    var accept = listener.BeginAcceptSocket(new AsyncCallback(DoAcceptSocketCallback), listener);
                }

                var now = DateTime.UtcNow;
                var diff = now - _lastPeerConnect;
                if (diff.TotalSeconds >= 1)
                {
                    ConnectToPeers();
                    _lastPeerConnect = now;
                }
            }

            return true;
        }

        protected override void OnStart()
        {
            Logger.Message($"Phantasma node listening on port {Port}, using address: {Address}");

            listener.Start();
        }

        protected override void OnStop()
        {
            listener.Stop();
        }

        private bool IsKnown(Endpoint endpoint)
        {
            lock (_peers)
            {
                foreach (var peer in _peers)
                {
                    if (peer.Endpoint.Equals(endpoint))
                    {
                        return true;
                    }
                }
            }

            lock (_knownEndpoints)
            {
                foreach (var peer in _knownEndpoints)
                {
                    if (peer.endpoint.Equals(endpoint))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private void ConnectToPeers()
        {
            lock (_peers)
            {
                if (_peers.Count >= MaxActiveConnections)
                {
                    return;
                }
            }

            lock (_knownEndpoints)
            {
                _knownEndpoints.RemoveAll(x => x.endpoint.Protocol != PeerProtocol.TCP);

                var possibleTargets = new List<int>();
                for (int i=0; i<_knownEndpoints.Count; i++)
                {
                    if (_knownEndpoints[i].status == EndpointStatus.Waiting)
                    {
                        possibleTargets.Add(i);
                    }
                }

                if (possibleTargets.Count > 0)
                {
                    // adds a bit of pseudo randomness to connection order
                    var idx = Environment.TickCount % possibleTargets.Count;
                    idx = possibleTargets[idx];
                    var target = _knownEndpoints[idx];

                    var result = client.BeginConnect(target.endpoint.Host, target.endpoint.Port, null, null);

                    var signal = result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(3));

                    if (signal && client.Client != null && client.Client.Connected)
                    {
                        Logger.Debug("Connected to peer: " + target.endpoint);
                        target.status = EndpointStatus.Connected;

                        client.EndConnect(result);
                        Task.Run(() => { HandleConnection(client.Client); });
                        return;
                    }
                    else
                    {
                        Logger.Debug("Could not reach peer: " + target.endpoint);
                        target.status = EndpointStatus.Disabled;
                        return;
                    }
                }
            }

            var disabledConnections = _knownEndpoints.Where(x => x.status == EndpointStatus.Disabled);
            if (disabledConnections.Any())
            {
                lock (_knownEndpoints)
                {
                    var currentTime = DateTime.UtcNow;
                    foreach (var entry in disabledConnections)
                    {
                        var diff = currentTime - entry.lastPing;
                        if (diff.TotalSeconds >= entry.pingDelay)
                        {
                            entry.lastPing = currentTime;
                            entry.pingDelay *= 2;
                            entry.status = EndpointStatus.Waiting;
                        }
                    }
                }
            }
        }

        // Process the client connection.
        public void DoAcceptSocketCallback(IAsyncResult ar)
        {
            listening = false;

            // Get the listener that handles the client request.
            var listener = (TcpListener)ar.AsyncState;

            Socket socket;
            try
            {
                socket = listener.EndAcceptSocket(ar);
            }
            catch (ObjectDisposedException e)
            {
                return;
            }

            Logger.Debug("New connection accepted from " + socket.RemoteEndPoint.ToString());
            Task.Run(() => { HandleConnection(socket); });
        }

        private bool SendMessage(Peer peer, Message msg)
        {
            Throw.IfNull(peer, nameof(peer));
            Throw.IfNull(msg, nameof(msg));

            Logger.Debug("Sending "+msg.GetType().Name+" to  " + peer.Endpoint);

            msg.Sign(this.Keys);

            try
            {
                peer.Send(msg);
            }
            catch (Exception e)
            {
                return false;
            }

            return true;
        }

        private void HandleConnection(Socket socket)
        {
            var peer = new TCPPeer(socket);
            lock (_peers)
            {
                _peers.Add(peer);
            }

            // this initial message is not only used to fetch chains but also to verify identity of peers
            var requestKind = RequestKind.Chains | RequestKind.Peers;
            if (Capabilities.HasFlag(PeerCaps.Mempool))
            {
                requestKind |= RequestKind.Mempool;
            }

            var request = new RequestMessage(requestKind, Nexus.Name, this.Address);
            var active = SendMessage(peer, request);

            while (active)
            {
                var msg = peer.Receive();
                if (msg == null)
                {
                    break;
                }

                Logger.Debug($"Got {msg.GetType().Name} from: {msg.Address.Text}");
                foreach (var line in msg.GetDescription())
                {
                    Logger.Debug(line);
                }

                var answer = HandleMessage(peer, msg);
                if (answer != null)
                {
                    if (!SendMessage(peer, answer))
                    {
                        break;
                    }
                }
            }

            Logger.Debug("Disconnected from peer: " + peer.Endpoint);

            socket.Close();

            lock (_peers)
            {
                _peers.Remove(peer);
            }
        }

        private Message HandleMessage(Peer peer, Message msg)
        {
            if (msg.IsSigned && !msg.Address.IsNull)
            {
                if (msg.Address.IsUser)
                {
                    peer.SetAddress(msg.Address);
                }
                else
                {
                    return new ErrorMessage(Address, P2PError.InvalidAddress);
                }
            }
            else
            {
                return new ErrorMessage(Address, P2PError.MessageShouldBeSigned);
            }

            switch (msg.Opcode) {
                case Opcode.EVENT:
                    {
                        var evtMessage = (EventMessage)msg;
                        var evt = evtMessage.Event;
                        Logger.Message("New event: " + evt.ToString());
                        return null;
                    }

                case Opcode.REQUEST:
                    {
                        var request = (RequestMessage)msg;

                        if (request.NexusName != Nexus.Name)
                        {
                            return new ErrorMessage(Address, P2PError.InvalidNexus);
                        }

                        if (request.Kind == RequestKind.None)
                        {
                            return null;
                        }

                        var answer = new ListMessage(this.Address, request.Kind);

                        if (request.Kind.HasFlag(RequestKind.Peers))
                        {
                            answer.SetPeers(this.Peers.Where(x => x != peer).Select(x => x.Endpoint));
                        }

                        if (request.Kind.HasFlag(RequestKind.Chains))
                        {
                            var chainList = Nexus.GetChains(Nexus.RootStorage);
                            var chains = chainList.Select(x => Nexus.GetChainByName(x)).Select(x => new ChainInfo(x.Name, Nexus.GetParentChainByName(x.Name), x.Height));
                            answer.SetChains(chains);
                        }

                        if (request.Kind.HasFlag(RequestKind.Mempool) && Capabilities.HasFlag(PeerCaps.Mempool))
                        {
                            var txs = _mempool.GetTransactions().Select(x => Base16.Encode(x.ToByteArray(true)));
                            answer.SetMempool(txs);
                        }

                        if (request.Kind.HasFlag(RequestKind.Blocks))
                        {
                            foreach (var entry in request.Blocks)
                            {
                                var chain = this.Nexus.GetChainByName(entry.Key);
                                if (chain == null)
                                {
                                    continue;
                                }

                                var startBlock = entry.Value;
                                if (startBlock > chain.Height)
                                {
                                    continue;
                                }

                                var blockList = new List<string>();
                                var currentBlock = startBlock;
                                while (blockList.Count < 50 && currentBlock <= chain.Height)
                                {
                                    var blockHash = chain.GetBlockHashAtHeight(currentBlock);
                                    var block = chain.GetBlockByHash(blockHash);
                                    var bytes = block.ToByteArray(true);
                                    var str = Base16.Encode(bytes);

                                    foreach (var tx in chain.GetBlockTransactions(block))
                                    {
                                        var txBytes = tx.ToByteArray(true);
                                        str += "/" + Base16.Encode(txBytes);
                                    }

                                    blockList.Add(str);
                                    currentBlock++;
                                }

                                answer.AddBlockRange(chain.Name, startBlock, blockList);
                            }
                        }

                        return answer;
                    }

                case Opcode.LIST:
                    {
                        var listMsg = (ListMessage)msg;

                        var outKind = RequestKind.None;

                        if (listMsg.Kind.HasFlag(RequestKind.Peers))
                        {
                            var newPeers = listMsg.Peers.Where(x => !IsKnown(x));
                            foreach (var entry in listMsg.Peers)
                            {
                                Logger.Message("New peer: " + entry.ToString());
                            }
                            QueueEndpoints(newPeers);
                        }

                        var blockFetches = new Dictionary<string, BigInteger>();
                        if (listMsg.Kind.HasFlag(RequestKind.Chains))
                        {
                            foreach (var entry in listMsg.Chains)
                            {
                                var chain = Nexus.GetChainByName(entry.name);
                                // NOTE if we dont find this chain then it is too soon for ask for blocks from that chain
                                if (chain != null && chain.Height < entry.height)
                                {
                                    blockFetches[entry.name] = chain.Height + 1;
                                }
                            }
                        }

                        if (listMsg.Kind.HasFlag(RequestKind.Mempool) && Capabilities.HasFlag(PeerCaps.Mempool))
                        {
                            int submittedCount = 0;
                            foreach (var txStr in listMsg.Mempool)
                            {
                                var bytes = Base16.Decode(txStr);
                                var tx = Transaction.Unserialize(bytes);
                                try
                                {
                                    _mempool.Submit(tx);
                                    submittedCount++;
                                }
                                catch
                                {
                                }

                                Logger.Message(submittedCount + " new transactions");
                            }
                        }

                        if (listMsg.Kind.HasFlag(RequestKind.Blocks))
                        {
                            bool addedBlocks = false;

                            foreach (var entry in listMsg.Blocks)
                            {
                                var chain = Nexus.GetChainByName(entry.Key);
                                if (chain == null)
                                {
                                    continue;
                                }

                                var blockRange = entry.Value;
                                var currentBlock = blockRange.startHeight;
                                foreach (var rawBlock in blockRange.rawBlocks)
                                {
                                    var temp = rawBlock.Split('/');

                                    var block = Block.Unserialize(Base16.Decode(temp[0]));

                                    var transactions = new List<Transaction>();
                                    for (int i= 1; i<temp.Length; i++)
                                    {
                                        var tx = Transaction.Unserialize(Base16.Decode(temp[i]));
                                        transactions.Add(tx);
                                    }

                                    // TODO this wont work in the future...
                                    try
                                    {
                                        chain.AddBlock(block, transactions, 1);
                                    }
                                    catch (Exception e)
                                    {
                                        throw new Exception("block add failed");
                                    }

                                    Logger.Message($"Added block #{currentBlock} to {chain.Name}");
                                    addedBlocks = true;
                                    currentBlock++;
                                }
                            }

                            if (addedBlocks)
                            {
                                outKind |= RequestKind.Chains;
                            }
                        }

                        if (blockFetches.Count > 0)
                        {
                            outKind |= RequestKind.Blocks;
                        }

                        if (outKind != RequestKind.None)
                        {
                            var answer = new RequestMessage(outKind, Nexus.Name, this.Address);

                            if (blockFetches.Count > 0)
                            {
                                answer.SetBlocks(blockFetches);
                            }

                            return answer;
                        }

                        break;
                    }

                case Opcode.MEMPOOL_Add:
                    {
                        if (Capabilities.HasFlag(PeerCaps.Mempool))
                        {
                            var memtx = (MempoolAddMessage)msg;
                            int submissionCount = 0;
                            foreach (var tx in memtx.Transactions)
                            {
                                try
                                {
                                    if (_mempool.Submit(tx))
                                    {
                                        submissionCount++;
                                    }
                                }
                                catch
                                {
                                    // ignore
                                }
                            }

                            Logger.Message($"Added {submissionCount} txs to the mempool");
                        }
                        break;
                    }

                case Opcode.BLOCKS_List:
                    {
                        break;
                    }

                case Opcode.ERROR:
                    {
                        var errorMsg = (ErrorMessage)msg;
                        if (string.IsNullOrEmpty(errorMsg.Text))
                        {
                            Logger.Error($"ERROR: {errorMsg.Code}");
                        }
                        else
                        {
                            Logger.Error($"ERROR: {errorMsg.Code} ({errorMsg.Text})");
                        }
                        break;
                    }
            }

            Logger.Message("No answer sent.");
            return null;
        }

        private Dictionary<Address, List<RelayReceipt>> _messages = new Dictionary<Address, List<RelayReceipt>>();

        public IEnumerable<RelayReceipt> GetRelayReceipts(Address from)
        {
            if (_messages.ContainsKey(from))
            {
                return _messages[from];
            }

            return Enumerable.Empty<RelayReceipt>();
        }

        public void PostRelayMessage(RelayReceipt receipt)
        {
            List<RelayReceipt> list;

            var msg = receipt.message;

            if (_messages.ContainsKey(msg.receiver))
            {
                list = _messages[msg.receiver];
            }
            else
            {
                list = new List<RelayReceipt>();
                _messages[msg.receiver] = list;
            }

            BigInteger expectedMessageIndex = 0;

            foreach (var otherReceipt in list)
            {
                var temp = otherReceipt.message;
                if (temp.sender == msg.sender && temp.index >= expectedMessageIndex)
                {
                    expectedMessageIndex = temp.index + 1;
                }
            }

            if (expectedMessageIndex > msg.index)
            {
                throw new RelayException("unexpected message index, should be at least "+expectedMessageIndex+" but it's "+msg.index);
            }

            list.Add(receipt);
        }

        internal void AddEvent(Event evt)
        {
            if (!Capabilities.HasFlag(PeerCaps.Events))
            {
                return;
            }

            Cache<Event> cache;

            if (_events.ContainsKey(evt.Address))
            {
                cache = _events[evt.Address];
            }
            else
            {
                cache = new Cache<Event>(250, TimeSpan.FromMinutes(60)); // TODO make this configurable
                _events[evt.Address] = cache;
            }

            cache.Add(evt);

            foreach (var peer in _peers)
            {
                if (peer.Address == evt.Address)
                {
                    var msg = new EventMessage(evt.Address, evt);
                    SendMessage(peer, msg);
                }
            }
        }

        public IEnumerable<Event> GetEvents(Address address)
        {
            if (Capabilities.HasFlag(PeerCaps.Events))
            {
                if (_events.ContainsKey(address))
                {
                    return _events[address].Items;
                }
                else
                {
                    return Enumerable.Empty<Event>();
                }
            }
            else
            {
                return Enumerable.Empty<Event>();
            }
        }
    }
}
