﻿using Phantasma.Blockchain;
using Phantasma.Cryptography;
using Phantasma.Numerics;
using System;
using System.Numerics;

namespace Phantasma.Network.P2P
{
    // TODO those are unused for now
    [Flags]
    public enum PeerCaps
    {
        None = 0,
        Mempool = 0x1,
        Archive = 0x2,
        Relay = 0x4,
        Events = 0x8,
        RPC = 0x10,
        REST = 0x20,
    }

    public abstract class Peer
    {
        public Address Address { get; private set; }
        public readonly Endpoint Endpoint;

        public PeerCaps Capabilities { get; set; }

        public Status Status { get; protected set; }

        public BigInteger MinimumFee { get; set; }
        public int MinimumPoW { get; set; }

        public abstract void Send(Message msg);
        public abstract Message Receive();

        public Peer(Endpoint endpoint)
        {
            this.Endpoint = endpoint;
            this.Status = Status.Disconnected;
            this.MinimumFee = 1;
            this.MinimumPoW = 0;
        }

        public void SetAddress(Address address)
        {
            this.Address = address;
            this.Status = address.IsNull ? Status.Anonymous : Status.Identified;
        }
    }
}
