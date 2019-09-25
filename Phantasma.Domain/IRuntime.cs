using Phantasma.Core.Types;
using Phantasma.Cryptography;
using Phantasma.Numerics;
using Phantasma.Storage.Context;
using Phantasma.VM;

namespace Phantasma.Domain
{
    public interface IRuntime
    {
        INexus Nexus { get; }
        IChain Chain { get; }
        ITransaction Transaction { get; }
        Timestamp Time { get; }
        StorageContext Storage { get; }

        IBlock GetBlockByHash(IChain chain, Hash hash);
        IBlock GetBlockByHeight(IChain chain, BigInteger height);

        ITransaction GetTransaction(IChain chain, Hash hash);

        IToken GetToken(string symbol);
        IFeed GetFeed(string name);
        IPlatform GetPlatform(string name);
        IContract GetContract(string name);

        IChain GetChainByAddress(Address address);
        IChain GetChainByName(string name);
        int GetIndexOfChain(string name);

        void Log(string description);
        void Throw(string description);
        void Expect(bool condition, string description);
        void Notify(EventKind kind, Address address, byte[] data);
        VMObject CallContext(string contextName, string methodName, params object[] args);

        IEvent GetTransactionEvents(ITransaction transaction);

        Address GetValidatorForBlock(IChain chain, Hash hash);
        ValidatorEntry GetValidatorByIndex(int index);
        int GetPrimaryValidatorCount();
        int GetSecondaryValidatorCount();
        bool IsKnownValidator(Address address);

        BigInteger GetTokenPrice(string symbol);
        BigInteger GetTokenQuote(string baseSymbol, string quoteSymbol, BigInteger amount);
        BigInteger GetRandomNumber();
        BigInteger GetGovernanceValue(string name);
        BigInteger GetBalance(IChain chain, IToken token, Address address);

        bool InvokeTrigger(byte[] script, string triggerName, params object[] args);

        bool IsWitness(Address address);
    }
}
