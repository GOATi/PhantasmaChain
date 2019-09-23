﻿using Phantasma.Numerics;
using Phantasma.Cryptography;
using Phantasma.Storage.Context;
using System;
using Phantasma.Storage;
using System.IO;
using Phantasma.Storage.Utils;
using System.Numerics;

namespace Phantasma.Blockchain.Tokens
{
    [Flags]
    public enum TokenFlags
    {
        None = 0,
        Transferable = 1 << 0,
        Fungible = 1 << 1,
        Finite = 1 << 2,
        Divisible = 1 << 3,
        Fuel = 1 << 4,
        Stakable = 1 << 5,
        Fiat = 1 << 6,
        External = 1 << 7,
        Burnable = 1 << 8,
    }

    public struct TokenInfo : ISerializable
    {
        public string Symbol { get; private set; }
        public string Name { get; private set; }
        public string Platform { get; private set; }
        public Hash Hash { get; private set; }

        public TokenFlags Flags { get; private set; }

        public BigInteger MaxSupply { get; private set; }

        public bool IsFungible => Flags.HasFlag(TokenFlags.Fungible);
        public bool IsBurnable => Flags.HasFlag(TokenFlags.Burnable);
        public bool IsTransferable => Flags.HasFlag(TokenFlags.Transferable);
        public bool IsCapped => MaxSupply > 0; // equivalent to Flags.HasFlag(TokenFlags.Infinite)

        public int Decimals { get; private set; }

        public byte[] Script { get; private set; }

        internal TokenInfo(string symbol, string name, string platform, Hash hash, BigInteger maxSupply, int decimals, TokenFlags flags, byte[] script)
        {
            this.Symbol = symbol;
            this.Name = name;
            this.Platform = platform;
            this.Hash = hash;
            this.Flags = flags;
            this.Decimals = decimals;
            this.MaxSupply = maxSupply;
            this.Script = script;
        }

        public override string ToString()
        {
            return $"{Name} ({Symbol})";
        }

        public void SerializeData(BinaryWriter writer)
        {
            writer.WriteVarString(Symbol);
            writer.WriteVarString(Name);
            writer.WriteVarString(Platform);
            writer.WriteHash(Hash);
            writer.Write((uint)Flags);
            writer.Write(Decimals);
            writer.WriteBigInteger(MaxSupply);
            writer.WriteByteArray(Script);
        }

        public void UnserializeData(BinaryReader reader)
        {
            Symbol = reader.ReadVarString();
            Name = reader.ReadVarString();
            Platform = reader.ReadVarString();
            Hash = reader.ReadHash();
            Flags = (TokenFlags)reader.ReadUInt32();
            Decimals = reader.ReadInt32();
            MaxSupply = reader.ReadBigInteger();
            Script = reader.ReadByteArray();
        }
    }
}