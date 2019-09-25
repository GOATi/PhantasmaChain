﻿using Phantasma.Core.Types;
using Phantasma.Cryptography;

namespace Phantasma.Domain
{
    public enum ValidatorType
    {
        Invalid,
        Primary,
        Secondary, // aka StandBy
    }

    public struct ValidatorEntry
    {
        public Address address;
        public Timestamp election;
        public ValidatorType type;
    }

    public static class Validation
    {
        public static readonly string ANONYMOUS = "anonymous";
        public static readonly string GENESIS = "genesis";

        public static bool IsValidIdentifier(string name)
        {
            if (name == null)
            {
                return false;
            }

            if (name.Length < 3 || name.Length > 15)
            {
                return false;
            }

            if (name == ANONYMOUS || name == GENESIS)
            {
                return false;
            }

            int index = 0;
            while (index < name.Length)
            {
                var c = name[index];
                index++;

                if (c >= 97 && c <= 122) continue; // lowercase allowed
                if (c == 95) continue; // underscore allowed
                if (c >= 48 && c <= 57) continue; // numbers allowed

                return false;
            }

            return true;
        }
    }
}
