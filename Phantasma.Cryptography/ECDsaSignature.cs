using Phantasma.Cryptography.ECC;
using Phantasma.Storage;
using Phantasma.Storage.Utils;
using System.IO;
using System.Linq;

namespace Phantasma.Cryptography
{
    public class ECDsaSignature : ISerializable
    {
        public static readonly ECCurve Curve = ECCurve.Secp256r1;

        public byte[] Bytes { get; private set; }

        internal ECDsaSignature()
        {
            this.Bytes = null;
        }

        public ECDsaSignature(byte[] bytes)
        {
            this.Bytes = bytes;
        }

        public bool Verify(byte[] message, Address address)
        {
            if (address.IsUser)
            {
                var pubKeyBytes = address.PublicKey.Skip(1).ToArray();
                var pubKey = ECC.ECPoint.DecodePoint(pubKeyBytes, Curve);
                if (ECDsa.VerifySignature(message, this.Bytes, Curve, pubKey))
                {
                    return true;
                }
            }

            return false;
        }

        public void SerializeData(BinaryWriter writer)
        {
            writer.WriteByteArray(this.Bytes);
        }

        public void UnserializeData(BinaryReader reader)
        {
            this.Bytes = reader.ReadByteArray();
        }
    }
}
