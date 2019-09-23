using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Crypto.Parameters;
using Phantasma.Storage;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Paddings;
using System.Linq;

namespace Phantasma.Cryptography
{
    public static class EncryptionUtils
    {
        private static X9ECParameters curve = SecNamedCurves.GetByName("secp256r1");
        private static ECDomainParameters domain = new ECDomainParameters(curve.Curve, curve.G, curve.N, curve.H);

        public static byte[] GetSharedSecret(KeyPair local, Address remote)
        {
            var curve = ECDsaSignature.Curve;
            var remotePublicKeyBytes = remote.PublicKey.Skip(1).ToArray();
            var pubKey = ECC.ECPoint.DecodePoint(remotePublicKeyBytes, curve);
            var secret = (pubKey * local.PrivateKey).EncodePoint(true);
            return secret.Sha256();
        }

        public static byte[] Encrypt<T>(T message, KeyPair local, Address remote)
        {
            var secret = GetSharedSecret(local, remote);
            return Encrypt(message, secret);
        }

        public static T Decrypt<T>(byte[] input, KeyPair local, Address remote)
        {
            var secret = GetSharedSecret(local, remote);
            return Decrypt<T>(input, secret);
        }

        public static byte[] Encrypt<T>(T message, byte[] key)
        {
            byte[] iv = new byte[16];
            AesEngine engine = new AesEngine();
            CbcBlockCipher blockCipher = new CbcBlockCipher(engine); //CBC
            PaddedBufferedBlockCipher cipher = new PaddedBufferedBlockCipher(blockCipher); //Default scheme is PKCS5/PKCS7
            KeyParameter keyParam = new KeyParameter(key);
            ParametersWithIV keyParamWithIV = new ParametersWithIV(keyParam, iv, 0, 16);

            var inputBytes = Serialization.Serialize(message);

            cipher.Init(true, keyParamWithIV);
            byte[] outputBytes = new byte[cipher.GetOutputSize(inputBytes.Length)];
            int length = cipher.ProcessBytes(inputBytes, outputBytes, 0);
            cipher.DoFinal(outputBytes, length); //Do the final block
            return outputBytes;
            //throw new System.NotImplementedException();
        }

        public static T Decrypt<T>(byte[] input, byte[] key)
        {
            byte[] iv = new byte[16];
            AesEngine engine = new AesEngine();
            CbcBlockCipher blockCipher = new CbcBlockCipher(engine); //CBC
            PaddedBufferedBlockCipher cipher = new PaddedBufferedBlockCipher(blockCipher); //Default scheme is PKCS5/PKCS7
            KeyParameter keyParam = new KeyParameter(key);
            ParametersWithIV keyParamWithIV = new ParametersWithIV(keyParam, iv, 0, 16);

            cipher.Init(false, keyParamWithIV);
            byte[] comparisonBytes = new byte[cipher.GetOutputSize(input.Length)];
            var length = cipher.ProcessBytes(input, comparisonBytes, 0);
            cipher.DoFinal(comparisonBytes, length); //Do the final block       
            return Serialization.Unserialize<T>(comparisonBytes);
            //throw new System.NotImplementedException();
        }

    }

}