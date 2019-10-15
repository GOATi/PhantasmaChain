﻿using Microsoft.VisualStudio.TestTools.UnitTesting;

using System;
using System.Linq;
using System.Collections.Generic;

using Phantasma.Cryptography;
using Phantasma.Blockchain.Contracts;
using Phantasma.Blockchain;
using Phantasma.Core.Types;
using Phantasma.VM.Utils;
using Phantasma.Numerics;
using Phantasma.Storage;
using Phantasma.Domain;

namespace Phantasma.Tests
{
    [TestClass]
    public class SerializationTests
    {
        [TestMethod]
        public void TransactionSerialization()
        {
            var keysA = PhantasmaKeys.Generate();
            var keysB = PhantasmaKeys.Generate();

            var script = ScriptUtils.BeginScript().
                AllowGas(keysA.Address, Address.Null, 1, 9999).
                TransferTokens(DomainSettings.FuelTokenSymbol, keysA.Address, keysB.Address, UnitConversion.ToBigInteger(25, DomainSettings.FuelTokenDecimals)).
                SpendGas(keysA.Address).
                EndScript();

            var tx = new Transaction("simnet", "main", script, Timestamp.Now);
            tx.Mine(3);

            var bytesUnsigned = tx.ToByteArray(false);
            Assert.IsTrue(bytesUnsigned != null);

            tx.Sign(keysA);
            var bytesSigned = tx.ToByteArray(true);

            Assert.IsTrue(bytesSigned != null);
            Assert.IsTrue(bytesSigned.Length != bytesUnsigned.Length);

            var tx2 = Transaction.Unserialize(bytesSigned);
            Assert.IsTrue(tx2 != null);

            Assert.IsTrue(tx.Hash == tx2.Hash);
        }

        [TestMethod]
        public void BlockSerialization()
        {
            var keysA = PhantasmaKeys.Generate();

            var txs = new List<Transaction>();
            var symbol = DomainSettings.FuelTokenSymbol;

            int count = 5;
            var amounts = new BigInteger[count];

            for (int i = 0; i<count; i++)
            {
                var keysB = PhantasmaKeys.Generate();
                amounts[i] = UnitConversion.ToBigInteger(20 + (i+1), DomainSettings.FuelTokenDecimals);

                var script = ScriptUtils.BeginScript().
                    AllowGas(keysA.Address, Address.Null, 1, 9999).
                    TransferTokens(symbol, keysA.Address, keysB.Address, amounts[i]).
                    SpendGas(keysA.Address).
                    EndScript();

                var tx = new Transaction("simnet", "main", script, Timestamp.Now - TimeSpan.FromMinutes(i));

                txs.Add(tx);
            }

            var chainKeys = PhantasmaKeys.Generate();
            var hashes = txs.Select(x => x.Hash);
            uint protocol = 42;
            var block = new Block(1, chainKeys.Address, Timestamp.Now, hashes, Hash.Null, protocol, chainKeys.Address, System.Text.Encoding.UTF8.GetBytes("TEST"));

            int index = 0;
            foreach (var hash in hashes)
            {
                var data = new TokenEventData(symbol, amounts[index], "main");
                var dataBytes = Serialization.Serialize(data);
                block.Notify(hash, new Event(EventKind.TokenSend, keysA.Address, "test", dataBytes));
                index++;
            }

            block.Sign(chainKeys);
            var bytes = block.ToByteArray(true);

            Assert.IsTrue(bytes != null);

            var block2 = Block.Unserialize(bytes);
            Assert.IsTrue(block2 != null);

            Assert.IsTrue(block.Hash == block2.Hash);
        }

        [TestMethod]
        public void TransactionMining()
        {
            var keysA = PhantasmaKeys.Generate();
            var keysB = PhantasmaKeys.Generate();

            var script = ScriptUtils.BeginScript().
                AllowGas(keysA.Address, Address.Null, 1, 9999).
                TransferTokens(DomainSettings.FuelTokenSymbol, keysA.Address, keysB.Address, UnitConversion.ToBigInteger(25, DomainSettings.FuelTokenDecimals)).
                SpendGas(keysA.Address).
                EndScript();

            var tx = new Transaction("simnet", "main", script, Timestamp.Now);
            var oldHash = tx.Hash;
            var expectedDiff = (int)ProofOfWork.Moderate;
            tx.Mine(expectedDiff);        
            var newHash = tx.Hash;

            Assert.IsTrue(oldHash != newHash);

            var diff = tx.Hash.GetDifficulty();
            Assert.IsTrue(diff >= expectedDiff);
        }

    }
}
