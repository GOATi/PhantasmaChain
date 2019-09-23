using System;
using System.Linq;
using Phantasma.Core;
using Phantasma.Core.Utils;
using Phantasma.Cryptography.ECC;

namespace Phantasma.Cryptography
{
    public sealed class KeyPair 
    {
        public readonly byte[] PrivateKey;
        public readonly Address Address;

        public const int PrivateKeyLength = 32;

        public KeyPair(byte[] privateKey)
        {
            Throw.If(privateKey.Length != PrivateKeyLength, $"privateKey should have length {PrivateKeyLength}");

            this.PrivateKey = new byte[PrivateKeyLength];
            ByteArrayUtils.CopyBytes(privateKey, 0, PrivateKey, 0, PrivateKeyLength); 

            var publicKey = ECDsaSignature.Curve.G * privateKey;
            var publicKeyBytes = publicKey.EncodePoint(true);

            publicKeyBytes = ByteArrayUtils.ConcatBytes(new byte[] { Address.UserOpcode }, publicKeyBytes);
            this.Address = new Address(publicKeyBytes);
        }

        public override string ToString()
        {
            return Address.Text;
        }

        public static KeyPair Generate()
        {
            do
            {
                var privateKey = Entropy.GetRandomBytes(PrivateKeyLength);
                var pair = new KeyPair(privateKey);
                if (pair.Address.IsUser)
                {
                    return pair;
                }
            } while (true);
        }

        public static KeyPair FromWIF(string wif)
        {
            Throw.If(wif == null, "WIF required");

            byte[] data = wif.Base58CheckDecode();
            Throw.If(data.Length != 34 || data[0] != 0x80 || data[33] != 0x01, "Invalid WIF format");

            byte[] privateKey = new byte[32];
            ByteArrayUtils.CopyBytes(data, 1, privateKey, 0, privateKey.Length); 
            Array.Clear(data, 0, data.Length);
            return new KeyPair(privateKey);
        }

        public string ToWIF()
        {
            byte[] data = new byte[34];
            data[0] = 0x80;
            ByteArrayUtils.CopyBytes(PrivateKey, 0, data, 1, 32); 
            data[33] = 0x01;
            string wif = data.Base58CheckEncode();
            Array.Clear(data, 0, data.Length);
            return wif;
        }

        private static byte[] XOR(byte[] x, byte[] y)
        {
            if (x.Length != y.Length) throw new ArgumentException();
            return x.Zip(y, (a, b) => (byte)(a ^ b)).ToArray();
        }

        public ECDsaSignature Sign(byte[] message)
        {
            var signer = new ECDsa(this.PrivateKey, ECDsaSignature.Curve);
            var signature = signer.GenerateSignature(message);
            return new ECDsaSignature(signature);
        }
    }
}
