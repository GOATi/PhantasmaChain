using Phantasma.Domain;

namespace Phantasma.Contracts
{
    public sealed class SaleContract : NativeContract
    {
        public override NativeContractKind Kind => NativeContractKind.Sale;
    }
}
