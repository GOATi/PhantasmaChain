﻿using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;

using Phantasma.Cryptography;
using Phantasma.Core;
using Phantasma.VM;
using Phantasma.Numerics;
using Phantasma.Blockchain.Contracts;
using Phantasma.Core.Types;
using Phantasma.Storage.Utils;
using Phantasma.Storage.Context;
using Phantasma.Storage;
using Phantasma.Cryptography.ECC;

namespace Phantasma.Blockchain
{
    public sealed class Transaction : ISerializable
    {
        public byte[] Script { get; private set; }

        public string NexusName { get; private set; }
        public string ChainName { get; private set; }

        public Timestamp Expiration { get; private set; }

        public byte[] Payload { get; private set; }

        public ECDsaSignature[] Signatures { get; private set; }
        public Hash Hash { get; private set; }

        public static Transaction Unserialize(byte[] bytes)
        {
            using (var stream = new MemoryStream(bytes))
            {
                using (var reader = new BinaryReader(stream))
                {
                    return Unserialize(reader);
                }
            }
        }

        public static Transaction Unserialize(BinaryReader reader)
        {
            var tx = new Transaction();
            tx.UnserializeData(reader);
            return tx;
        }

        public void Serialize(BinaryWriter writer, bool withSignature)
        {
            writer.WriteVarString(this.NexusName);
            writer.WriteVarString(this.ChainName);
            writer.WriteByteArray(this.Script);
            writer.Write(this.Expiration.Value);
            writer.WriteByteArray(this.Payload);

            if (withSignature)
            {
                writer.WriteVarInt(Signatures.Length);
                foreach (var signature in this.Signatures)
                {
                    writer.WriteSignature(signature);
                }
            }
        }

        public override string ToString()
        {
            return $"{Hash}";
        }

        internal bool Execute(Chain chain, Timestamp time, StorageChangeSetContext changeSet, Action<Hash, Event> onNotify, OracleReader oracle, BigInteger minimumFee, out byte[] result)
        {
            result = null;

            var runtime = new RuntimeVM(this.Script, chain, time, this, changeSet, oracle, false);
            runtime.MinimumFee = minimumFee;
            runtime.ThrowOnFault = true;

            var state = runtime.Execute();

            if (state != ExecutionState.Halt)
            {
                return false;
            }

            var cost = runtime.UsedGas;

            foreach (var evt in runtime.Events)
            {
                onNotify(this.Hash, evt);
            }

            if (runtime.Stack.Count > 0)
            {
                var obj = runtime.Stack.Pop();
                result = Serialization.Serialize(obj);
            }

            return true;
        }

        // required for deserialization
        public Transaction()
        {

        }

        public Transaction(string nexusName, string chainName, byte[] script, Timestamp expiration, IEnumerable<ECDsaSignature> signatures = null)
        {
            Throw.IfNull(script, nameof(script));

            this.NexusName = nexusName;
            this.ChainName = chainName;
            this.Script = script;
            this.Expiration = expiration;
            this.Payload = new byte[0];

            this.Signatures = signatures != null && signatures.Any() ? signatures.ToArray() : new ECDsaSignature[0];

            this.UpdateHash();
        }

        public byte[] ToByteArray(bool withSignature)
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream))
                {
                    Serialize(writer, withSignature);
                }

                return stream.ToArray();
            }
        }

        public bool HasSignatures => Signatures != null && Signatures.Length > 0;

        public void Sign(ECDsaSignature signature)
        {
            this.Signatures = this.Signatures.Union(new ECDsaSignature[] { signature }).ToArray();
        }

        public void Sign(KeyPair owner)
        {
            Throw.If(owner == null, "invalid keypair");

            var msg = this.ToByteArray(false);
            var sig = owner.Sign(msg);

            var sigs = new List<ECDsaSignature>();

            if (this.Signatures != null && this.Signatures.Length > 0)
            {
                sigs.AddRange(this.Signatures);
            }

            sigs.Add(sig);
            this.Signatures = sigs.ToArray();
        }

        public bool IsSignedBy(Address address)
        {
            if (!HasSignatures)
            {
                return false;
            }

            var msg = this.ToByteArray(false);

            foreach (var signature in this.Signatures)
            {
                if (signature.Verify(msg, address))
                {
                    return true;
                }
            }

            return false;
        }

        public bool IsValid(Chain chain)
        {
            return (chain.Name == this.ChainName && chain.Nexus.Name == this.NexusName);
        }

        private void UpdateHash()
        {
            var data = this.ToByteArray(false);
            var hash = CryptoExtensions.SHA256(data);
            this.Hash = new Hash(hash);
        }

        public void SerializeData(BinaryWriter writer)
        {
            this.Serialize(writer, true);
        }

        public void UnserializeData(BinaryReader reader)
        {
            this.NexusName = reader.ReadVarString();
            this.ChainName = reader.ReadVarString();
            this.Script = reader.ReadByteArray();
            this.Expiration = reader.ReadUInt32();
            this.Payload = reader.ReadByteArray();

            // check if we have some signatures attached
            try
            {
                var signatureCount = (int)reader.ReadVarInt();
                this.Signatures = new ECDsaSignature[signatureCount];
                for (int i = 0; i < signatureCount; i++)
                {
                    Signatures[i] = reader.ReadSignature();
                }
            }
            catch
            {
                this.Signatures = new ECDsaSignature[0];
            }

            this.UpdateHash();
        }

        public void Mine(ProofOfWork targetDifficulty)
        {
            Mine((int)targetDifficulty);
        }

        public void Mine(int targetDifficulty)
        {
            Throw.If(targetDifficulty < 0 || targetDifficulty > 256, "invalid difficulty");
            Throw.If(Signatures.Length > 0, "cannot be signed");

            if (targetDifficulty == 0)
            {
                return; // no mining necessary 
            }

            uint nonce = 0;

            while (true)
            {
                if (this.Hash.GetDifficulty() >= targetDifficulty)
                {
                    return;
                }

                if (nonce == 0)
                {
                    this.Payload = new byte[4];
                }

                nonce++;
                if (nonce == 0)
                {
                    throw new ChainException("Transaction mining failed");
                }

                Payload[0] = (byte)((nonce >> 0) & 0xFF);
                Payload[1] = (byte)((nonce >> 8) & 0xFF);
                Payload[2] = (byte)((nonce >> 16) & 0xFF);
                Payload[3] = (byte)((nonce >> 24) & 0xFF);
                UpdateHash();
            }
        }
    }
}
