using Phantasma.Cryptography;
using Phantasma.Storage;
using System;

namespace Phantasma.Domain
{
    public static class DomainExtensions
    {
        public static bool IsFungible(this IToken token)
        {
            return token.Flags.HasFlag(TokenFlags.Fungible);
        }

        public static bool IsBurnable(this IToken token)
        {
            return token.Flags.HasFlag(TokenFlags.Burnable);
        }

        public static bool IsTransferable(this IToken token)
        {
            return token.Flags.HasFlag(TokenFlags.Transferable);
        }

        public static bool IsCapped(this IToken token)
        {
            return token.MaxSupply > 0;
        }

        public static IContract GetContract(this IRuntime runtime, NativeContractKind nativeContract)
        {
            return runtime.GetContract(nativeContract.GetName());
        }

        public static Address GetContractAddress(this IRuntime runtime, string contractName)
        {
            return Address.FromHash(contractName);
        }

        public static Address GetContractAddress(this IRuntime runtime, NativeContractKind nativeContract)
        {
            return Address.FromHash(nativeContract.GetName());
        }

        public static string GetName(this NativeContractKind nativeContract)
        {
            return nativeContract.ToString().ToLower();
        }

        public static void Notify<T>(this IRuntime runtime, Enum kind, Address address, T content)
        {
            var intVal = (int)(object)kind;
            runtime.Notify<T>((EventKind)(EventKind.Custom + intVal), address, content);
        }

        public static void Notify<T>(this IRuntime runtime, EventKind kind, Address address, T content)
        {
            var bytes = content == null ? new byte[0] : Serialization.Serialize(content);
            runtime.Notify(kind, address, bytes);
        }
    }
}
