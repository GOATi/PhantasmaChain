using Phantasma.Core;
using Phantasma.Cryptography.ECC;
using System.IO;

namespace Phantasma.Cryptography
{
    public static class IOExtensions
    {
        public static void WriteAddress(this BinaryWriter writer, Address address)
        {
            address.SerializeData(writer);
        }

        public static void WriteHash(this BinaryWriter writer, Hash hash)
        {
            hash.SerializeData(writer);
        }

        public static void WriteSignature(this BinaryWriter writer, ECDsaSignature signature)
        {
            Throw.IfNull(signature, nameof(signature));
            signature.SerializeData(writer);
        }

        public static Address ReadAddress(this BinaryReader reader)
        {
            var address = new Address();
            address.UnserializeData(reader);
            return address;
        }

        public static Hash ReadHash(this BinaryReader reader)
        {
            var hash = new Hash();
            hash.UnserializeData(reader);
            return hash;
        }

        public static ECDsaSignature ReadSignature(this BinaryReader reader)
        {
            var signature = new ECDsaSignature();
            signature.UnserializeData(reader);
            return signature;
        }
    }
}
