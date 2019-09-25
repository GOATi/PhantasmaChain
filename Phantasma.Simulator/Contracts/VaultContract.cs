﻿using Phantasma.Blockchain.Tokens;
using Phantasma.Cryptography;
using Phantasma.Domain;
using Phantasma.Numerics;
using Phantasma.Storage.Context;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Phantasma.Simulator.Contracts
{
    internal struct VaultEntry
    {
        public BigInteger amount;
        public uint unlockTime;
    }

    public sealed class VaultContract : SmartContract
    {
        public override string Name => "vault";

        private StorageMap _entries; //Dictionary<Address, List<VaultEntry>>();

        public VaultContract() : base()
        {
        }

        public void LockTokens(Address from, string symbol, BigInteger amount, uint duration)
        {
            Runtime.Expect(amount > 0, "amount must be greater than zero");
            Runtime.Expect(duration >= 86400, "minimum duration should be one day"); // minimum 1 day
            Runtime.Expect(IsWitness(from), "invalid witness");

            Runtime.Expect(Runtime.Nexus.TokenExists(symbol), "invalid token");
            var tokenInfo = this.Runtime.Nexus.GetTokenInfo(symbol);
            Runtime.Expect(tokenInfo.Flags.HasFlag(TokenFlags.Fungible), "token must be fungible");

            Runtime.Expect(Runtime.Nexus.TransferTokens(Runtime, symbol, from, this.Address, amount), "transfer failed");

            var list = _entries.Get<Address, StorageList>(from);

            var entry = new VaultEntry()
            {
                amount = amount,
                unlockTime = Runtime.Time + TimeSpan.FromSeconds(duration),
            };
            list.Add(entry);

            Runtime.Notify(EventKind.TokenEscrow, from, new TokenEventData() { symbol = symbol, value = amount, chainAddress = Runtime.Chain.Address });
        }

        public void UnlockTokens(Address from, string symbol)
        {
            Runtime.Expect(IsWitness(from), "invalid witness");

            Runtime.Expect(Runtime.Nexus.TokenExists(symbol), "invalid token");
            var tokenInfo = this.Runtime.Nexus.GetTokenInfo(symbol);
            Runtime.Expect(tokenInfo.Flags.HasFlag(TokenFlags.Fungible), "token must be fungible");

            Runtime.Expect(_entries.ContainsKey(from), "address not in vault");

            var list = _entries.Get<Address, StorageList>(from);

            BigInteger amount = 0;

            var count = list.Count();

            int i = 0;
            while (i<count)
            {
                var entry = list.Get<VaultEntry>(i);
                if (entry.unlockTime <= Runtime.Time)
                {
                    amount += entry.amount;
                    list.RemoveAt<VaultEntry>(i);
                }
                else
                {
                    i++;
                }
            }
            Runtime.Expect(amount > 0, "available amount must be greater than zero");

            Runtime.Expect(Runtime.Nexus.TransferTokens(Runtime, symbol, this.Address, from, amount), "transfer failed");

            Runtime.Notify(EventKind.TokenReceive, from, new TokenEventData() { symbol = symbol, value = amount, chainAddress = Runtime.Chain.Address });
        }
    }
}
