namespace Phantasma.Contracts
{
    public sealed class BombContract : IContract
    {
        public override string Name => Nexus.BombContractName;

        public BombContract() : base()
        {
        }
    }
}
