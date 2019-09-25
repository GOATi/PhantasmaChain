using Phantasma.Core.Types;
using Phantasma.Cryptography;
using Phantasma.Numerics;

namespace Phantasma.Domain
{
    public enum EventKind
    {
        Unknown = 0,
        ChainCreate = 1,
        BlockCreate = 2,
        BlockClose = 3,
        TokenCreate = 4,
        TokenSend = 5,
        TokenReceive = 6,
        TokenMint = 7,
        TokenBurn = 8,
        TokenEscrow = 9,
        TokenStake = 10,
        TokenUnstake = 11,
        TokenClaim = 12,
        RoleDemote = 13,
        RolePromote = 14,
        AddressRegister = 15,
        AddressLink = 16,
        AddressUnlink = 17,
        GasEscrow = 18,
        GasPayment = 19,
        GasLoan = 20,
        OrderCreated = 21,
        OrderCancelled = 23,
        OrderFilled = 24,
        OrderClosed = 25,
        FeedCreate = 26,
        FeedUpdate = 27,
        FileCreate = 28,
        FileDelete = 29,
        ValidatorAdd = 30,
        ValidatorRemove = 31,
        ValidatorSwitch = 32,
        BrokerRequest = 33,
        ValueCreate = 34,
        ValueUpdate = 35,
        PollCreated = 36,
        PollClosed = 37,
        PollVote = 38,
        ChannelCreate = 39,
        ChannelRefill = 40,
        ChannelSettle = 41,
        Metadata = 47,
        Custom = 48,
    }

    public interface IEvent
    {
        EventKind Kind { get; }
        Address Address { get; }
        string Contract { get; }
        byte[] Data { get; }
    }

    public struct Metadata
    {
        public string key;
        public string value;
    }

    public struct TokenEventData
    {
        public string symbol;
        public BigInteger value;
        public Address chainAddress;
    }

    public struct RoleEventData
    {
        public string role;
        public Timestamp date;
    }

    public struct MetadataEventData
    {
        public string type;
        public Metadata metadata;
    }
}
