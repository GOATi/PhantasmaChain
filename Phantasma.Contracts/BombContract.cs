using Phantasma.Domain;

namespace Phantasma.Contracts
{
    public sealed class BombContract : NativeContract
    {
        public override NativeContractKind Kind => NativeContractKind.Bomb;
    }
}
