﻿using Microsoft.VisualStudio.TestTools.UnitTesting;

using Phantasma.API;
using Phantasma.Blockchain;
using Phantasma.Simulator;
using Phantasma.Cryptography;
using Phantasma.Numerics;
using Phantasma.VM.Utils;
using System;
using System.Linq;
using Phantasma.Contracts.Native;
using Phantasma.Core.Types;
using Phantasma.Domain;
using Phantasma.Blockchain.Tokens;
using Phantasma.Storage;
using Phantasma.VM;

namespace Phantasma.Tests
{
    [TestClass]
    public class ApiTests
    {
        public class TestData
        {
            public PhantasmaKeys owner;
            public Nexus nexus;
            public NexusSimulator simulator;
            public NexusAPI api;
        }

        private static readonly string testWIF = "Kx9Kr8MwQ9nAJbHEYNAjw5n99B2GpU6HQFf75BGsC3hqB1ZoZm5W";
        private static readonly string testAddress = "PSre3jAT22NLBwxS39fqGaZjNbywdaRMXzEtaRKPzpghF";

        private TestData CreateAPI(bool useMempool = false)
        {
            var owner = PhantasmaKeys.FromWIF(testWIF);
            var sim = new NexusSimulator(owner, 1234);
            var mempool = useMempool? new Mempool(owner, sim.Nexus, 2, 1, System.Text.Encoding.UTF8.GetBytes("TEST")) : null;
            var api = new NexusAPI(sim.Nexus);
            api.Mempool = mempool;

            var data = new TestData()
            {
                owner = owner,
                simulator = sim,
                nexus = sim.Nexus,
                api = api
            };

            mempool?.Start();

            return data;
        }

        [TestMethod]
        public void TestGetAccountValid()
        {
            var test = CreateAPI();

            var temp = test.api.GetAccount(testAddress);
            var account = (AccountResult)temp;
            Assert.IsTrue(account.address == testAddress);
            Assert.IsTrue(account.name == "genesis");
            Assert.IsTrue(account.balances.Length > 0);
        }

        [TestMethod]
        public void TestMultipleCallsOneRequest()
        {
            var test = CreateAPI();

            var randomKey = PhantasmaKeys.Generate();

            var script = new ScriptBuilder().
                CallContract("account", "LookUpAddress", test.owner.Address).
                CallContract("account", "LookUpAddress", randomKey.Address).
                EndScript();

            var temp = test.api.InvokeRawScript("main", Base16.Encode(script));
            var scriptResult = (ScriptResult)temp;
            Assert.IsTrue(scriptResult.results.Length == 2);

            var names = scriptResult.results.Select(x => Base16.Decode(x)).Select(bytes => Serialization.Unserialize<VMObject>(bytes)).Select(obj => obj.AsString()).ToArray();
            Assert.IsTrue(names.Length == 2);
            Assert.IsTrue(names[0] == "genesis");
            Assert.IsTrue(names[1] == ValidationUtils.ANONYMOUS);
        }

        [TestMethod]
        public void TestGetAccountInvalidAddress()
        {
            var test = CreateAPI();

            var result = (ErrorResult)test.api.GetAccount("blabla");
            Assert.IsTrue(!string.IsNullOrEmpty(result.error));
        }

        [TestMethod]
        public void TestTransactionError()
        {
            var test = CreateAPI(true);

            var contractName = "blabla";
            var script = new ScriptBuilder().CallContract(contractName, "bleble", 123).ToScript();

            var chainName = DomainSettings.RootChainName;
            test.simulator.CurrentTime = Timestamp.Now;
            var tx = new Transaction("simnet", chainName, script, test.simulator.CurrentTime + TimeSpan.FromHours(1));
            tx.Sign(PhantasmaKeys.FromWIF(testWIF));
            var txBytes = tx.ToByteArray(true);
            var temp = test.api.SendRawTransaction(Base16.Encode(txBytes));
            var result = (SingleResult)temp;
            Assert.IsTrue(result.value != null);
            var hash = result.value.ToString();
            Assert.IsTrue(hash == tx.Hash.ToString());

            var startTime = DateTime.Now;
            do
            {
                var timeDiff = DateTime.Now - startTime;
                if (timeDiff.Seconds > 20)
                {
                    throw new Exception("Test timeout");
                }

                var status = test.api.GetTransaction(hash);
                if (status is ErrorResult)
                {
                    var error = (ErrorResult)status;
                    var msg = error.error.ToLower();
                    if (msg != "pending")
                    {
                        Assert.IsTrue(msg.Contains(contractName));
                        break;
                    }
                }
            } while (true);
        }

        [TestMethod]
        public void TestGetAccountNFT()
        {
            var test = CreateAPI();

            var chain = test.nexus.RootChain;

            var symbol = "COOL";

            var testUser = PhantasmaKeys.Generate();

            // Create the token CoolToken as an NFT
            test.simulator.BeginBlock();
            test.simulator.GenerateToken(test.owner, symbol, "CoolToken", DomainSettings.PlatformName, Hash.FromString(symbol), 0, 0, Domain.TokenFlags.None);
            test.simulator.EndBlock();

            var token = test.simulator.Nexus.GetTokenInfo(symbol);
            Assert.IsTrue(test.simulator.Nexus.TokenExists(symbol), "Can't find the token symbol");

            var tokenROM = new byte[] { 0x1, 0x3, 0x3, 0x7 };
            var tokenRAM = new byte[] { 0x1, 0x4, 0x4, 0x6 };

            // Mint a new CoolToken 
            var simulator = test.simulator;
            simulator.BeginBlock();
            simulator.MintNonFungibleToken(test.owner, testUser.Address, symbol, tokenROM, tokenRAM);
            simulator.EndBlock();

            // obtain tokenID
            var ownerships = new OwnershipSheet(symbol);
            var ownedTokenList = ownerships.Get(chain.Storage, testUser.Address);
            Assert.IsTrue(ownedTokenList.Count() == 1, "How does the sender not have one now?");
            var tokenID = ownedTokenList.First();

            var account = (AccountResult)test.api.GetAccount(testUser.Address.Text);
            Assert.IsTrue(account.address == testUser.Address.Text);
            Assert.IsTrue(account.name == ValidationUtils.ANONYMOUS);
            Assert.IsTrue(account.balances.Length == 1);

            var balance = account.balances[0];
            Assert.IsTrue(balance.symbol == symbol);
            Assert.IsTrue(balance.ids.Length == 1);

            var info = (TokenDataResult)test.api.GetTokenData(symbol, balance.ids[0]);
            Assert.IsTrue(info.ID == balance.ids[0]);
            var tokenStr = Base16.Encode(tokenROM);
            Assert.IsTrue(info.rom == tokenStr);
        }

        [TestMethod]
        public void TestGetABIFunction()
        {
            var test = CreateAPI();

            var result = (ABIContractResult)test.api.GetABI(test.nexus.RootChain.Name, "exchange");

            var methodCount = typeof(ExchangeContract).GetMethods();

            var method = methodCount.FirstOrDefault(x => x.Name == "GetOrderBook");

            Assert.IsTrue(method != null);

            var parameters = method.GetParameters();

            Assert.IsTrue(parameters.Length == 3);
            Assert.IsTrue(parameters.Count(x => x.ParameterType == typeof(string)) == 2);
            Assert.IsTrue(parameters.Count(x => x.ParameterType == typeof(ExchangeOrderSide)) == 1);

            var returnType = method.ReturnType;

            Assert.IsTrue(returnType == typeof(ExchangeOrder[]));
        }


        [TestMethod]
        public void TestGetABIMethod()
        {
            var test = CreateAPI();

            var result = (ABIContractResult)test.api.GetABI(test.nexus.RootChain.Name, "exchange");

            var methodCount = typeof(ExchangeContract).GetMethods();

            var method = methodCount.FirstOrDefault(x => x.Name == "OpenMarketOrder");

            Assert.IsTrue(method != null);

            var parameters = method.GetParameters();

            Assert.IsTrue(parameters.Length == 6);
            Assert.IsTrue(parameters.Count(x => x.ParameterType == typeof(string)) == 2);
            Assert.IsTrue(parameters.Count(x => x.ParameterType == typeof(ExchangeOrderSide)) == 1);
            Assert.IsTrue(parameters.Count(x => x.ParameterType == typeof(BigInteger)) == 1);
            Assert.IsTrue(parameters.Count(x => x.ParameterType == typeof(Address)) == 2);

            var returnType = method.ReturnType;

            Assert.IsTrue(returnType == typeof(void));
        }
    }
}
