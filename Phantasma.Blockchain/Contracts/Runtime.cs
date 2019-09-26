using System;
using System.Collections.Generic;
using Phantasma.VM;
using Phantasma.Cryptography;
using Phantasma.Numerics;
using Phantasma.Core.Types;
using Phantasma.Storage.Context;
using Phantasma.Storage;
using Phantasma.Blockchain.Tokens;
using Phantasma.Domain;
using Phantasma.Contracts;
using System.Linq;

namespace Phantasma.Blockchain.Contracts
{
    public class RuntimeVM : VirtualMachine, IRuntime
    {
        public Timestamp Time { get; private set; }
        public Transaction Transaction { get; private set; }
        public Chain Chain { get; private set; }
        public Chain ParentChain { get; private set; }
        public OracleReader Oracle { get; private set; }
        public Nexus Nexus => Chain.Nexus;

        private List<Event> _events = new List<Event>();
        public IEnumerable<Event> Events => _events;

        public Address FeeTargetAddress { get; private set; }

        public StorageChangeSetContext ChangeSet { get; private set; }

        public BigInteger UsedGas { get; private set; }
        public BigInteger PaidGas { get; private set; }
        public BigInteger MaxGas { get; private set; }
        public BigInteger GasPrice { get; private set; }
        public Address GasTarget { get; private set; }
        public bool DelayPayment { get; private set; }
        public readonly bool readOnlyMode;

        private bool isBlockOperation;

        private bool randomized;
        private BigInteger seed;

        public BigInteger MinimumFee;

        public bool IsTrigger => DelayPayment;

        INexus IRuntime.Nexus => this.Nexus;

        IChain IRuntime.Chain => this.Chain;

        ITransaction IRuntime.Transaction => this.Transaction;

        public StorageContext Storage => this.ChangeSet;

        public RuntimeVM(byte[] script, Chain chain, Timestamp time, Transaction transaction, StorageChangeSetContext changeSet, OracleReader oracle, bool readOnlyMode, bool delayPayment = false) : base(script)
        {
            Core.Throw.IfNull(chain, nameof(chain));
            Core.Throw.IfNull(changeSet, nameof(changeSet));

            // NOTE: block and transaction can be null, required for Chain.InvokeContract
            //Throw.IfNull(block, nameof(block));
            //Throw.IfNull(transaction, nameof(transaction));

            this.MinimumFee = 1;
            this.GasPrice = 0;
            this.UsedGas = 0;
            this.PaidGas = 0;
            this.GasTarget = chain.Address;
            this.MaxGas = 10000;  // a minimum amount required for allowing calls to Gas contract etc
            this.DelayPayment = delayPayment;

            this.Time = time;
            this.Chain = chain;
            this.Transaction = transaction;
            this.Oracle = oracle;
            this.ChangeSet = changeSet;
            this.readOnlyMode = readOnlyMode;

            this.isBlockOperation = false;
            this.randomized = false;

            this.FeeTargetAddress = Address.Null;

            if (this.Chain != null && !Chain.IsRoot)
            {
                var parentName = chain.Nexus.GetParentChainByName(chain.Name);
                this.ParentChain = chain.Nexus.GetChainByName(parentName);
            }
            else
            {
                this.ParentChain = null;
            }

            Chain.RegisterExtCalls(this);
        }

        public override string ToString()
        {
            return $"Runtime.Context={CurrentContext}";
        }

        internal void RegisterMethod(string name, Func<RuntimeVM, ExecutionState> handler)
        {
            handlers[name] = handler;
        }

        private Dictionary<string, Func<RuntimeVM, ExecutionState>> handlers = new Dictionary<string, Func<RuntimeVM, ExecutionState>>();

        public override ExecutionState ExecuteInterop(string method)
        {
            // TODO blacklist some interops here
            //Expect(!isBlockOperation, "no interops available in block operations");

            if (handlers.ContainsKey(method))
            {
                return handlers[method](this);
            }

            return ExecutionState.Fault;
        }

        public override ExecutionState Execute()
        {
            var result = base.Execute();

            if (result == ExecutionState.Halt)
            {
                if (readOnlyMode)
                {
                    if (ChangeSet.Any())
                    {
#if DEBUG
                        throw new VMDebugException(this, "VM changeset modified in read-only mode");
#else
                        result = ExecutionState.Fault;
#endif
                    }
                }
                else
                if (PaidGas < UsedGas && Nexus.HasGenesis && !DelayPayment)
                {
#if DEBUG
                    throw new VMDebugException(this, "VM unpaid gas");
#else
                                        result = ExecutionState.Fault;
#endif
                }
            }

            return result;
        }

        public override ExecutionContext LoadContext(string contextName)
        {
            if (isBlockOperation && Nexus.HasGenesis)
            {
                throw new ChainException($"{contextName} context not available in block operations");
            }

            var contract = this.Nexus.AllocContract(contextName);
            if (contract != null)
            {
                return Chain.GetContractContext(contract);
            }

            return null;
        }

        public VMObject CallContext(string contextName, string methodName, params object[] args)
        {
            var previousContext = CurrentContext;

            var context = LoadContext(contextName);
            Expect(context != null, "could not call context: " + contextName);

            for (int i= args.Length - 1; i>=0; i--)
            {
                var obj = VMObject.FromObject(args[i]);
                this.Stack.Push(obj);
            }

            this.Stack.Push(VMObject.FromObject(methodName));

            CurrentContext = context;
            var temp = context.Execute(this.CurrentFrame, this.Stack);
            Expect(temp == ExecutionState.Halt, "expected call success");
            CurrentContext = previousContext;

            if (this.Stack.Count > 0)
            {
                var result = this.Stack.Pop();
                return result;
            }
            else
            {
                return new VMObject();
            }
        }


        public void Notify(EventKind kind, Address address, byte[] bytes)
        {
            var contract = CurrentContext.Name;

            switch (kind)
            {
                case EventKind.GasEscrow:
                    {
                        var gasContractName = NativeContractKind.Gas.GetName();
                        Expect(contract == gasContractName, $"event kind only in {gasContractName} contract");

                        var gasInfo = Serialization.Unserialize<GasEventData>(bytes);
                        Expect(gasInfo.price >= this.MinimumFee, "gas fee is too low");
                        this.MaxGas = gasInfo.amount;
                        this.GasPrice = gasInfo.price;
                        this.GasTarget = address;
                        break;
                    }

                case EventKind.GasPayment:
                    {
                        var gasContractName = NativeContractKind.Gas.GetName();
                        Expect(contract == gasContractName, $"event kind only in {gasContractName} contract");

                        var gasInfo = Serialization.Unserialize<GasEventData>(bytes);
                        this.PaidGas += gasInfo.amount;

                        if (address != this.Chain.Address)
                        {
                            this.FeeTargetAddress = address;
                        }

                        break;
                    }

                case EventKind.GasLoan:
                    {
                        var gasContractName = NativeContractKind.Gas.GetName();
                          Expect(contract == gasContractName, $"event kind only in {gasContractName} contract");
                      break;
                  }

                case EventKind.BlockCreate:
                case EventKind.BlockClose:
                    {
                        var blockContractName = NativeContractKind.Block.GetName();
                        Expect(contract == blockContractName, $"event kind only in {blockContractName} contract");

                        isBlockOperation = true;
                        UsedGas = 0;
                        break;
                    }

                case EventKind.ValidatorSwitch:
                    {
                        var blockContractName = NativeContractKind.Block.GetName();
                        Expect(contract == blockContractName, $"event kind only in {blockContractName} contract");
                        break;
                    }

                case EventKind.PollCreated:
                case EventKind.PollClosed:
                case EventKind.PollVote:
                    {
                        var consensusContractName = NativeContractKind.Consensus.GetName();
                        Expect(contract == consensusContractName, $"event kind only in {consensusContractName} contract");
                        break;
                    }

                case EventKind.ChainCreate:
                case EventKind.TokenCreate:
                case EventKind.FeedCreate:
                    {
                        var NexusContractName = NativeContractKind.Nexus.GetName();
                        Expect(contract == NexusContractName, $"event kind only in {NexusContractName} contract");
                        break;
                    }

                case EventKind.FileCreate:
                case EventKind.FileDelete:
                    {
                        var storageContractName = NativeContractKind.Storage.GetName();
                        Expect(contract == storageContractName, $"event kind only in {storageContractName} contract");
                        break;
                    }

                case EventKind.ValidatorAdd:
                case EventKind.ValidatorRemove:
                    {
                        var validatorContractName = NativeContractKind.Validator.GetName();
                        Expect(contract == validatorContractName, $"event kind only in {validatorContractName} contract");
                        break;
                    }

                case EventKind.BrokerRequest:
                    {
                        var interopContractName = NativeContractKind.Interop.GetName();
                        Expect(contract == interopContractName, $"event kind only in {interopContractName} contract");
                        break;
                    }

                case EventKind.ValueCreate:
                case EventKind.ValueUpdate:
                    {
                        var governanceContractName = NativeContractKind.Governance.GetName();
                        Expect(contract == governanceContractName, $"event kind only in {governanceContractName} contract");
                        break;
                    }
            }

            var evt = new Event(kind, address, contract, bytes);
            _events.Add(evt);
        }

        public void Expect(bool condition, string description)
        {
#if DEBUG
            if (!condition)
            {
                throw new VMDebugException(this, description);
            }
#endif

            Core.Throw.If(!condition, $"contract assertion failed: {description}");
        }

        #region GAS
        public override ExecutionState ValidateOpcode(Opcode opcode)
        {
            // required for allowing transactions to occur pre-minting of native token
            if (readOnlyMode || !Nexus.HasGenesis)
            {
                return ExecutionState.Running;
            }

            var gasCost = GetGasCostForOpcode(opcode);
            return ConsumeGas(gasCost);
        }

        public ExecutionState ConsumeGas(BigInteger gasCost)
        {
            if (gasCost == 0 || isBlockOperation)
            {
                return ExecutionState.Running;
            }

            if (gasCost < 0)
            {
                Core.Throw.If(gasCost < 0, "invalid gas amount");
            }

            // required for allowing transactions to occur pre-minting of native token
            if (readOnlyMode || !Nexus.HasGenesis)
            {
                return ExecutionState.Running;
            }

            UsedGas += gasCost;

            if (UsedGas > MaxGas && !DelayPayment)
            {
#if DEBUG
                throw new VMDebugException(this, "VM gas limit exceeded");
#else
                                return ExecutionState.Fault;
#endif
            }

            return ExecutionState.Running;
        }

        public static BigInteger GetGasCostForOpcode(Opcode opcode)
        {
            switch (opcode)
            {
                case Opcode.GET:
                case Opcode.PUT:
                case Opcode.CALL:
                case Opcode.LOAD:
                    return 2;

                case Opcode.EXTCALL:
                    return 3;

                case Opcode.CTX:
                    return 5;

                case Opcode.SWITCH:
                    return 10;

                case Opcode.NOP:
                case Opcode.RET:
                    return 0;

                default: return 1;
            }
        }
        #endregion

        #region ORACLES
        // returns value in FIAT token
        public BigInteger GetTokenPrice(string symbol)
        {
            if (symbol == DomainSettings.FiatTokenSymbol)
            {
                return UnitConversion.GetUnitValue(DomainSettings.FiatTokenDecimals);
            }

            if (symbol == DomainSettings.FuelTokenSymbol)
            {
                var result = GetTokenPrice(DomainSettings.StakingTokenSymbol);
                result /= 5;
                return result;
            }

            Core.Throw.If(Oracle == null, "cannot read price from null oracle");

            Core.Throw.If(!Nexus.TokenExists(symbol), "cannot read price for invalid token");

            var bytes = Oracle.Read("price://" + symbol);
            var value = BigInteger.FromUnsignedArray(bytes, true);
            return value;
        }

        public BigInteger GetTokenQuote(string baseSymbol, string quoteSymbol, BigInteger amount)
        {
            if (baseSymbol == quoteSymbol)
                return amount;

            var basePrice = GetTokenPrice(baseSymbol);
            var quotePrice = GetTokenPrice(quoteSymbol);

            BigInteger result;

            var baseToken = Nexus.GetTokenInfo(baseSymbol);
            var quoteToken = Nexus.GetTokenInfo(quoteSymbol);

            result = basePrice * amount;
            result = UnitConversion.ConvertDecimals(result, baseToken.Decimals, DomainSettings.FiatTokenDecimals);

            result /= quotePrice;

            result = UnitConversion.ConvertDecimals(result, DomainSettings.FiatTokenDecimals, quoteToken.Decimals);

            return result;
        }
        #endregion

        #region RANDOM NUMBERS
        public static readonly uint RND_A = 16807;
        public static readonly uint RND_M = 2147483647;

        // returns a next random number
        public BigInteger GenerateRandomNumber()
        {
            if (!randomized)
            {
                // calculates first initial pseudo random number seed
                byte[] bytes = Transaction != null ? Transaction.Hash.ToByteArray() : new byte[32];

                for (int i = 0; i < this.entryScript.Length; i++)
                {
                    var index = i % bytes.Length;
                    bytes[index] ^= entryScript[i];
                }

                var time = System.BitConverter.GetBytes(Time.Value);

                for (int i = 0; i < bytes.Length; i++)
                {
                    bytes[i] ^= time[i % time.Length];
                }

                seed = BigInteger.FromUnsignedArray(bytes, true);
                randomized = true;
            }
            else
            {
                seed = ((RND_A * seed) % RND_M);
            }

            return seed;
        }
        #endregion

        // fetches a chain-governed value
        public BigInteger GetGovernanceValue(string name)
        {
            var value = Nexus.RootChain.InvokeContract(this.Storage, NativeContractKind.Governance.GetName(), nameof(GovernanceContract.GetValue), name).AsNumber();
            return value;
        }

        public BigInteger GetBalance(string tokenSymbol, Address address)
        {
            Expect(TokenExists(tokenSymbol), "invalid token");

            var tokenInfo = Nexus.GetTokenInfo(tokenSymbol);
            if (tokenInfo.Flags.HasFlag(TokenFlags.Fungible))
            {
                var balances = new BalanceSheet(tokenSymbol);
                return balances.Get(this.ChangeSet, address);
            }
            else
            {
                var ownerships = new OwnershipSheet(tokenSymbol);
                var items = ownerships.Get(this.ChangeSet, address);
                return items.Length;
            }
        }

        public void Log(string description)
        {
            throw new NotImplementedException();
        }

        public void Throw(string description)
        {
            throw new ChainException(description);
        }

        public bool InvokeTrigger(byte[] script, string triggerName, params object[] args)
        {
            if (script == null || script.Length == 0)
            {
                return true;
            }

            var leftOverGas = (uint)(this.MaxGas - this.UsedGas);
            var runtime = new RuntimeVM(script, this.Chain, this.Time, this.Transaction, this.ChangeSet, this.Oracle, false, true);
            runtime.ThrowOnFault = true;

            for (int i = args.Length - 1; i >= 0; i--)
            {
                var obj = VMObject.FromObject(args[i]);
                runtime.Stack.Push(obj);
            }
            runtime.Stack.Push(VMObject.FromObject(triggerName));

            var state = runtime.Execute();
            // TODO catch VM exceptions?

            // propagate gas consumption
            // TODO this should happen not here but in real time during previous execution, to prevent gas attacks
            this.ConsumeGas(runtime.UsedGas);

            if (state == ExecutionState.Halt)
            {
                // propagate events to the other runtime
                foreach (var evt in runtime.Events)
                {
                    this.Notify(evt.Kind, evt.Address, evt.Data);
                }

                return true;
            }
            else
            {
                return false;
            }
        }

        public bool IsWitness(Address address)
        {
            /*if (address == this.Runtime.Chain.Address || address == this.Address)
            {
                var frame = Runtime.frames.Skip(1).FirstOrDefault();
                return frame != null && frame.Context.Admin;
            }*/

            if (address.IsInterop)
            {
                return false;
            }

            if (Transaction == null)
            {
                return false;
            }

            if (address.IsUser && this.HasAddressScript(address))
            {
                return this.InvokeTriggerOnAccount(address, AccountTrigger.OnWitness, address);
            }

            return Transaction.IsSignedBy(address);
        }

        public IBlock GetBlockByHash(Hash hash)
        {
            return this.Chain.FindBlockByHash(hash);
        }

        public IBlock GetBlockByHeight(BigInteger height)
        {
            return this.Chain.FindBlockByHeight(height);
        }

        public ITransaction GetTransaction(Hash hash)
        {
            return this.Chain.FindTransactionByHash(hash);
        }

        public IToken GetToken(string symbol)
        {
            return this.Nexus.GetTokenInfo(symbol);
        }

        public IFeed GetFeed(string name)
        {
            return this.Nexus.GetFeedInfo(name);
        }

        public IPlatform GetPlatform(string name)
        {
            return this.Nexus.GetPlatformInfo(name);
        }

        public IContract GetContract(string name)
        {
            throw new NotImplementedException();
        }

        public bool TokenExists(string symbol)
        {
            return this.Nexus.TokenExists(symbol);
        }

        public bool FeedExists(string name)
        {
            return this.Nexus.FeedExists(name);
        }

        public bool PlatformExists(string name)
        {
            return this.Nexus.PlatformExists(name);
        }

        public bool ContractExists(string name)
        {
            throw new NotImplementedException();
        }

        public bool ContractDeployed(string name)
        {
            throw new NotImplementedException();
        }

        public bool ArchiveExists(Hash hash)
        {
            return this.Nexus.ArchiveExists(hash);
        }

        public IArchive GetArchive(Hash hash)
        {
            return this.Nexus.FindArchive(hash);
        }

        public bool DeleteArchive(Hash hash)
        {
            var archive = Nexus.FindArchive(hash);
            if (archive == null)
            {
                return false;
            }
            return this.Nexus.DeleteArchive(archive);
        }

        public bool ChainExists(string name)
        {
            return Nexus.ChainExists(name);
        }

        public IChain GetChainByAddress(Address address)
        {
            return Nexus.GetChainByAddress(address);
        }

        public IChain GetChainByName(string name)
        {
            return Nexus.GetChainByName(name);
        }

        public int GetIndexOfChain(string name)
        {
            return Nexus.GetIndexOfChain(name);
        }

        public IChain GetChainParent(string name)
        {
            var parentName = Nexus.GetParentChainByName(name);
            return GetChainByName(parentName);
        }

        public Address LookUpName(string name)
        {
            return Nexus.LookUpName(RootStorage, name);
        }

        public bool HasAddressScript(Address from)
        {
            return Nexus.HasAddressScript(RootStorage, from);
        }

        public byte[] GetAddressScript(Address from)
        {
            return Nexus.LookUpAddressScript(RootStorage, from);
        }

        // TODO optimize this
        public IEvent[] GetTransactionEvents(ITransaction transaction)
        {
            var block = Chain.FindTransactionBlock(transaction.Hash);
            return block.GetEventsForTransaction(transaction.Hash).Cast<IEvent>().ToArray(); 
        }

        public Address GetValidatorForBlock(Hash hash)
        {
            return Chain.GetValidatorForBlock(hash);
        }

        public StorageContext RootStorage => this.Chain.IsRoot ? this.Storage : this.Nexus.RootChain.Storage;

        public ValidatorEntry GetValidatorByIndex(int index)
        {
            return Nexus.GetValidatorByIndex(RootStorage, index);
        }

        public ValidatorEntry[] GetValidators()
        {
            return Nexus.GetValidators(RootStorage);
        }

        public bool IsPrimaryValidator(Address address)
        {
            return Nexus.IsPrimaryValidator(RootStorage, address);
        }

        public bool IsSecondaryValidator(Address address)
        {
            return Nexus.IsSecondaryValidator(RootStorage, address);
        }

        public bool IsKnownValidator(Address address)
        {
            return Nexus.IsKnownValidator(RootStorage, address);
        }

        public int GetPrimaryValidatorCount()
        {
            return Nexus.GetPrimaryValidatorCount(RootStorage);
        }

        public int GetSecondaryValidatorCount()
        {
            return Nexus.GetSecondaryValidatorCount(RootStorage);
        }

        public bool IsStakeMaster(Address address)
        {
            return Nexus.IsStakeMaster(RootStorage, address);
        }

        public BigInteger GetStake(Address address)
        {
            return Nexus.GetStakeFromAddress(RootStorage, address);
        }

        public BigInteger GenerateUID()
        {
            throw new NotImplementedException();
        }

        public BigInteger[] GetOwnerships(string symbol, Address address)
        {
            var ownerships = new OwnershipSheet(symbol);
            return ownerships.Get(this.Storage, address);
        }

        public BigInteger GetTokenSupply(string symbol)
        {
            return Nexus.GetTokenSupply(this.Storage, symbol);
        }

        public bool CreateToken(string symbol, string name, string platform, Hash hash, BigInteger maxSupply, int decimals, TokenFlags flags, byte[] script)
        {
            return Nexus.CreateToken(symbol, name, platform, hash, maxSupply, decimals, flags, script);
        }

        public bool CreateChain(Address owner, string name, string parentChain, string[] contractNames)
        {
            return Nexus.CreateChain(this.Storage, owner, name, parentChain, contractNames);
        }

        public bool CreateFeed(Address owner, string name, FeedMode mode)
        {
            return Nexus.CreateFeed(owner, name, mode);
        }

        public bool CreatePlatform(Address address, string name, string fuelSymbol)
        {
            return Nexus.CreatePlatform(address, name, fuelSymbol);
        }

        public bool CreateArchive(MerkleTree merkleTree, BigInteger size, ArchiveFlags flags, byte[] key)
        {
            return Nexus.CreateArchive(merkleTree, size, flags, key);
        }

        public bool MintTokens(string symbol, Address target, BigInteger amount, bool isSettlement)
        {
            var Runtime = this;
            Runtime.Expect(IsWitness(target), "invalid witness");

            Runtime.Expect(amount > 0, "amount must be positive and greater than zero");

            Runtime.Expect(Runtime.TokenExists(symbol), "invalid token");
            var tokenInfo = Runtime.GetToken(symbol);
            Runtime.Expect(tokenInfo.Flags.HasFlag(TokenFlags.Fungible), "token must be fungible");
            Runtime.Expect(!tokenInfo.Flags.HasFlag(TokenFlags.Fiat), "token can't be fiat");

            Runtime.Expect(!target.IsInterop, "destination cannot be interop address");

            return Nexus.MintTokens(this, symbol, target, amount, false);
        }

        public bool BurnTokens(string symbol, Address target, BigInteger amount, bool isSettlement)
        {
            var Runtime = this;
            Runtime.Expect(amount > 0, "amount must be positive and greater than zero");
            Runtime.Expect(IsWitness(target), "invalid witness");

            Runtime.Expect(Runtime.TokenExists(symbol), "invalid token");
            var tokenInfo = Runtime.GetToken(symbol);
            Runtime.Expect(tokenInfo.Flags.HasFlag(TokenFlags.Fungible), "token must be fungible");
            Runtime.Expect(tokenInfo.IsBurnable(), "token must be burnable");
            Runtime.Expect(!tokenInfo.Flags.HasFlag(TokenFlags.Fiat), "token can't be fiat");

            return Nexus.BurnTokens(this, symbol, target, amount, false);
        }

        public bool TransferTokens(string symbol, Address source, Address destination, BigInteger amount)
        {
            var Runtime = this;
            Runtime.Expect(amount > 0, "amount must be positive and greater than zero");
            Runtime.Expect(source != destination, "source and destination must be different");
            Runtime.Expect(IsWitness(source), "invalid witness");
            Runtime.Expect(!Runtime.IsTrigger, "not allowed inside a trigger");

            if (destination.IsInterop)
            {
                Runtime.Expect(Runtime.Chain.IsRoot, "interop transfers only allowed in main chain");
                Runtime.CallContext("interop", "WithdrawTokens", source, destination, symbol, amount);
                return true;
            }

            Runtime.Expect(Runtime.TokenExists(symbol), "invalid token");
            var tokenInfo = Runtime.GetToken(symbol);
            Runtime.Expect(tokenInfo.Flags.HasFlag(TokenFlags.Fungible), "token must be fungible");
            Runtime.Expect(tokenInfo.Flags.HasFlag(TokenFlags.Transferable), "token must be transferable");

            return Nexus.TransferTokens(this, symbol, source, destination, amount);
        }

        public bool SendTokens(Address targetChainAddress, Address from, Address to, string symbol, BigInteger amount)
        {
            var Runtime = this;
            Runtime.Expect(IsWitness(from), "invalid witness");

            Runtime.Expect(IsAddressOfParentChain(targetChainAddress) || IsAddressOfChildChain(targetChainAddress), "target must be parent or child chain");

            Runtime.Expect(!to.IsInterop, "destination cannot be interop address");

            var targetChain = Runtime.GetChainByAddress(targetChainAddress);

            Runtime.Expect(Runtime.TokenExists(symbol), "invalid token");
            var tokenInfo = Runtime.GetToken(symbol);
            Runtime.Expect(tokenInfo.Flags.HasFlag(TokenFlags.Fungible), "must be fungible token");

            /*if (tokenInfo.IsCapped())
            {
                var sourceSupplies = new SupplySheet(symbol, this.Runtime.Chain, Runtime.Nexus);
                var targetSupplies = new SupplySheet(symbol, targetChain, Runtime.Nexus);

                if (IsAddressOfParentChain(targetChainAddress))
                {
                    Runtime.Expect(sourceSupplies.MoveToParent(this.Storage, amount), "source supply check failed");
                }
                else // child chain
                {
                    Runtime.Expect(sourceSupplies.MoveToChild(this.Storage, targetChain.Name, amount), "source supply check failed");
                }
            }*/

            Runtime.Expect(Runtime.BurnTokens(symbol, from, amount, true), "burn failed");

            Runtime.Notify(EventKind.TokenBurn, from, new TokenEventData() { symbol = symbol, value = amount, chainAddress = Runtime.Chain.Address });
            Runtime.Notify(EventKind.TokenEscrow, to, new TokenEventData() { symbol = symbol, value = amount, chainAddress = targetChainAddress });

            return true;
        }

        public bool SendToken(Address targetChainAddress, Address from, Address to, string symbol, BigInteger tokenID)
        {
            var Runtime = this;
            Runtime.Expect(IsWitness(from), "invalid witness");

            Runtime.Expect(IsAddressOfParentChain(targetChainAddress) || IsAddressOfChildChain(targetChainAddress), "source must be parent or child chain");

            Runtime.Expect(!to.IsInterop, "destination cannot be interop address");

            var targetChain = Runtime.GetChainByAddress(targetChainAddress);

            Runtime.Expect(Runtime.TokenExists(symbol), "invalid token");
            var tokenInfo = Runtime.GetToken(symbol);
            Runtime.Expect(!tokenInfo.Flags.HasFlag(TokenFlags.Fungible), "must be non-fungible token");

            /*
            if (tokenInfo.IsCapped())
            {
                var supplies = new SupplySheet(symbol, this.Runtime.Chain, Runtime.Nexus);

                BigInteger amount = 1;

                if (IsAddressOfParentChain(targetChainAddress))
                {
                    Runtime.Expect(supplies.MoveToParent(this.Storage, amount), "source supply check failed");
                }
                else // child chain
                {
                    Runtime.Expect(supplies.MoveToChild(this.Storage, this.Runtime.Chain.Name, amount), "source supply check failed");
                }
            }*/

            Runtime.Expect(Runtime.TransferToken(symbol, from, targetChainAddress, tokenID), "take token failed");

            Runtime.Notify(EventKind.TokenBurn, from, new TokenEventData() { symbol = symbol, value = tokenID, chainAddress = Runtime.Chain.Address });
            Runtime.Notify(EventKind.TokenEscrow, to, new TokenEventData() { symbol = symbol, value = tokenID, chainAddress = targetChainAddress });
            return true;
        }

        public bool MintToken(string symbol, Address target, BigInteger tokenID, bool isSettlement)
        {
            return Nexus.MintToken(this, symbol, target, tokenID, isSettlement);
        }

        public bool BurnToken(string symbol, Address target, BigInteger tokenID, bool isSettlement)
        {
            return Nexus.BurnToken(this, symbol, target, tokenID, isSettlement);
        }

        public bool TransferToken(string symbol, Address source, Address destination, BigInteger tokenID)
        {
            return Nexus.TransferTokens(this, symbol, source, destination, tokenID);
        }

        public BigInteger CreateNFT(string tokenSymbol, Address address, byte[] rom, byte[] ram)
        {
            return Nexus.CreateNFT(tokenSymbol, this.Chain.Name, address, rom, ram);
        }

        public bool DestroyNFT(string tokenSymbol, BigInteger tokenID)
        {
            return Nexus.DestroyNFT(tokenSymbol, tokenID);
        }

        public bool EditNFTContent(string tokenSymbol, BigInteger tokenID, byte[] ram)
        {
            return Nexus.EditNFTContent(tokenSymbol, tokenID, ram);
        }

        public TokenContent GetNFT(string tokenSymbol, BigInteger tokenID)
        {
            return Nexus.GetNFT(tokenSymbol, tokenID);
        }

        public bool IsAddressOfParentChain(Address address)
        {
            if (this.Chain.IsRoot)
            {
                return false;
            }

            var parentChain = this.GetChainParent(this.Chain.Name);
            return address == parentChain.Address;
        }

        public bool IsAddressOfChildChain(Address address)
        {
            var targetChain = GetChainByAddress(address);
            var parentChain = GetChainParent(targetChain.Name);
            return parentChain.Name == this.Chain.Name;
        }

        public byte[] ReadOracle(string URL)
        {
            return this.Oracle.Read(URL);
        }
    }
}
