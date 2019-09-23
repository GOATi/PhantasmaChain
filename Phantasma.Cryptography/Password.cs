using Phantasma.Core;

namespace Phantasma.Cryptography
{
    public static class PasswordUtils
    {
        public static KeyPair DeriveKey(string username, string password)
        {
            Throw.If(string.IsNullOrEmpty(username), "Username is required");
            Throw.If(string.IsNullOrEmpty(password), "Password is required");

            Throw.If(password.Length < 8, "Password is too small");

            Throw.If(password.ToLowerInvariant().Contains(username.ToLowerInvariant()), "Password cannot be similar to username");

            bool hasSpecial = false;
            foreach (var c in password)
            {
                if (!char.IsLetter(c))
                {
                    hasSpecial = true;
                    break;
                }
            }

            Throw.If(!hasSpecial, "Password must contain at least a number or other special character");

            var buffer = new byte[username.Length + password.Length];
            int i = 0;
            int j = 0;
            int k = 0;

            // reshuffling of cypher content
            while (k < buffer.Length)
            {
                byte a = i < username.Length ? (byte)username[i] : (byte)0;
                byte b = j < password.Length ? (byte)password[j] : (byte)0;

                bool takeFirst = ((a > b) == (k % 2 == 0));

                if (takeFirst && i >= username.Length)
                {
                    takeFirst = false;
                }
                else
                if (!takeFirst && j >= password.Length)
                {
                    takeFirst = true;
                }

                byte c;
                int n;

                if (takeFirst)
                {
                    c = a;
                    n = i;
                    i++;
                }
                else
                {
                    c = b;
                    n = j;
                    j++;
                }

                if (char.IsLetter((char)c) && ((c + n) % 2 == k % 2))
                {
                    c = (byte)(char.ToUpper((char)c));
                }

                buffer[k] = c;
                k++;
            }

            var pKey = buffer.SHA256();
            return new KeyPair(pKey);
        }
    }
}
