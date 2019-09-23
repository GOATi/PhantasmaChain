using System;
using System.Numerics;

namespace Phantasma.Numerics
{
    public static class BigIntegerExtensions
    {
        public static int GetLowestSetBit(this BigInteger n)
        {
            if (n == 0)
            {
                return -1;
            }

            byte[] b = n.ToByteArray();
            int w = 0;
            while (b[w] == 0)
                w++;

            for (int x = 0; x < 8; x++)
                if ((b[w] & 1 << x) > 0)
                    return x + w * 8;

            throw new Exception();
        }

        /// <summary>
        /// Returns the modulo inverse of this
        /// </summary>
        /// <param name="mod">Modulo</param>
        /// <returns>Modulo inverse of this</returns>
        public static BigInteger ModInverse(this BigInteger T, BigInteger mod)
        {
            BigInteger i = mod, v = 0, d = 1;

            while (T > 0)
            {
                BigInteger t = i / T, x = T;
                T = i % x;
                i = x;
                x = d;
                d = v - t * x;
                v = x;
            }

            v %= mod;
            if (v < 0)
                v = (v + mod) % mod;

            return v;
        }

        public static bool TestBit(this BigInteger n, int index)
        {
            return (n & (BigInteger.One << index)) > BigInteger.Zero;
        }

        public static BigInteger FlipBit(this BigInteger n, int bit)
        {
            return n ^ (BigInteger.One << bit);
        }

        public static int GetBitLength(this BigInteger n)
         {
            var data = n.ToByteArray();
            return data.Length << 3;
        }
    }
}
