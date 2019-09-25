using Phantasma.Cryptography;
using Phantasma.Domain;
using Phantasma.Numerics;
using System;

namespace Phantasma.Contracts
{
    public sealed class NexusContract : NativeContract
    {
        public override NativeContractKind Kind => NativeContractKind.Nexus;

        public const int MAX_TOKEN_DECIMALS = 18;

        public NexusContract() : base()
        {
        }

        public void CreateToken(Address from, string symbol, string name, string platform, Hash hash, BigInteger maxSupply, BigInteger decimals, TokenFlags flags, byte[] script)
        {
            var pow = Runtime.Transaction.Hash.GetDifficulty();
            Runtime.Expect(pow >= (int)ProofOfWork.Minimal, "expected proof of work");

            Runtime.Expect(!string.IsNullOrEmpty(symbol), "token symbol required");
            Runtime.Expect(!string.IsNullOrEmpty(name), "token name required");
            Runtime.Expect(maxSupply >= 0, "token supply cant be negative");
            Runtime.Expect(decimals >= 0, "token decimals cant be negative");
            Runtime.Expect(decimals <= MAX_TOKEN_DECIMALS, $"token decimals cant exceed {MAX_TOKEN_DECIMALS}");

            Runtime.Expect(!Runtime.TokenExists(symbol), "token already exists");

            if (symbol == DomainSettings.FuelTokenSymbol)
            {
                Runtime.Expect(flags.HasFlag(TokenFlags.Fuel), "token should be native");
            }
            else
            {
                Runtime.Expect(!flags.HasFlag(TokenFlags.Fuel), "token can't be native");
            }

            if (symbol == DomainSettings.StakingTokenSymbol)
            {
                Runtime.Expect(flags.HasFlag(TokenFlags.Stakable), "token should be stakable");
            }

            if (symbol == DomainSettings.FiatTokenSymbol)
            {
                Runtime.Expect(flags.HasFlag(TokenFlags.Fiat), "token should be fiat");
            }

            Runtime.Expect(!string.IsNullOrEmpty(platform), "chain name required");

            if (flags.HasFlag(TokenFlags.External))
            {
                Runtime.Expect(from == Runtime.Nexus.GenesisAddress, "genesis address only");
                Runtime.Expect(platform != DomainSettings.PlatformName, "external token chain required");
                Runtime.Expect(Runtime.PlatformExists(platform), "platform not found");
            }
            else
            {
                Runtime.Expect(platform == DomainSettings.PlatformName, "chain name is invalid");
            }

            Runtime.Expect(IsWitness(from), "invalid witness");
            Runtime.Expect(from.IsUser, "owner address must be user address");

            Runtime.Expect(this.Runtime.CreateToken(symbol, name, platform, hash, maxSupply, (int)decimals, flags, script), "token creation failed");
            Runtime.Notify(EventKind.TokenCreate, from, symbol);
        }

        public void CreateChain(Address owner, string name, string parentName, string[] contracts)
        {
            var pow = Runtime.Transaction.Hash.GetDifficulty();
            Runtime.Expect(pow >= (int)ProofOfWork.Minimal, "expected proof of work");

            Runtime.Expect(!string.IsNullOrEmpty(name), "name required");
            Runtime.Expect(!string.IsNullOrEmpty(parentName), "parent chain required");

            Runtime.Expect(IsWitness(owner), "invalid witness");
            Runtime.Expect(owner.IsUser, "owner address must be user address");

            name = name.ToLowerInvariant();
            Runtime.Expect(!name.Equals(parentName, StringComparison.OrdinalIgnoreCase), "same name as parent");

            var parent = this.Runtime.GetChainByName(parentName);
            Runtime.Expect(parent != null, "invalid parent");

            Runtime.Expect(this.Runtime.CreateChain(owner, name, parentName, contracts), "chain creation failed");

            Runtime.Notify(EventKind.ChainCreate, owner, name);
        }

        public void CreateFeed(Address owner, string name, FeedMode mode)
        {
            var pow = Runtime.Transaction.Hash.GetDifficulty();
            Runtime.Expect(pow >= (int)ProofOfWork.Minimal, "expected proof of work");

            Runtime.Expect(!string.IsNullOrEmpty(name), "name required");

            Runtime.Expect(IsWitness(owner), "invalid witness");
            Runtime.Expect(owner.IsUser, "owner address must be user address");

            Runtime.Expect(Runtime.CreateFeed(owner, name, mode), "feed creation failed");

            Runtime.Notify(EventKind.FeedCreate, owner, name);
        }

        /*
        public bool IsPlatformSupported(string name)
        {
            var count = _platforms.Count();
            for (int i = 0; i < count; i++)
            {
                var entry = _platforms.Get<InteropPlatformInfo>(i);
                if (entry.Name == name)
                {
                    return true;
                }
            }

            return false;
        }

        public InteropPlatformInfo GetPlatformInfo(string name)
        {
            var count = _platforms.Count();
            for (int i = 0; i < count; i++)
            {
                var entry = _platforms.Get<InteropPlatformInfo>(i);
                if (entry.Name == name)
                {
                    return entry;
                }
            }

            Runtime.Expect(false, "invalid platform");
            return new InteropPlatformInfo();
        }

        public InteropPlatformInfo[] GetAvailablePlatforms()
        {
            return _platforms.All<InteropPlatformInfo>();
        }*/

        public void CreatePlatform(Address target, string fuelSymbol)
        {
            Runtime.Expect(IsWitness(Runtime.Nexus.GenesisAddress), "must be genesis");

            Runtime.Expect(target.IsInterop, "external address must be interop");

            string platformName;
            byte[] data;
            target.DecodeInterop(out platformName, out data, 0);

            Runtime.Expect(Validation.IsValidIdentifier(platformName), "invalid platform name");

            Runtime.Expect(Runtime.CreatePlatform(target, platformName, fuelSymbol), "creation of platform failed");

            Runtime.Notify(EventKind.AddressRegister, target, platformName);
        }
    }
}
