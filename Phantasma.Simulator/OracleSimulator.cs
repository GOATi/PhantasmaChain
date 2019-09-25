﻿using System.Collections.Generic;
using Phantasma.Blockchain.Contracts.Native;
using Phantasma.Cryptography;
using Phantasma.Numerics;
using Phantasma.Storage;
using Phantasma.Blockchain;
using Phantasma.Pay;
using Phantasma.Blockchain.Contracts;
using Phantasma.Domain;

namespace Phantasma.Simulator
{
    public class OracleSimulator : OracleReader
    {
        private static List<SimulatorChainSwap> _swaps = new List<SimulatorChainSwap>();

        public OracleSimulator(Nexus nexus) : base(nexus)
        {

        }

        public static void CleanUp()
        {
            _swaps.Clear();
        }

        public static Hash SimulateExternalTransaction(string platformName, string address, string symbol, decimal amount)
        {
            var swap = new SimulatorChainSwap()
            {
                hash = Hash.FromString(platformName + address + symbol + amount),
                symbol = symbol,
                platformName = platformName,
                sourceAddress = address,
                amount = amount
            };

            _swaps.Add(swap);
            return swap.hash;
        }

        protected override byte[] PullData(string url)
        {
            throw new OracleException("invalid oracle url: " + url);
        }

        protected override InteropBlock PullPlatformBlock(string platformName, Hash hash)
        {
            throw new OracleException($"unknown block for {platformName} : {hash}");
        }

        protected override InteropTransaction PullPlatformTransaction(string platformName, Hash hash)
        {
            foreach (var swap in _swaps)
            if (swap.platformName == platformName && swap.hash == hash)
            {
                var info = Nexus.GetPlatformInfo(platformName);
                var platformAddress = info.Address;

                    string destAddress;
                    string temp;
                    WalletUtils.DecodePlatformAndAddress(info.Address, out temp, out destAddress);

                    var token = Nexus.GetTokenInfo(swap.symbol);
                    var amount = UnitConversion.ToBigInteger(swap.amount, token.Decimals);

                    return new InteropTransaction()
                    {
                        Platform = platformName,
                        Hash = hash,
                        Events = new IEvent[]
                        {
                            new Event(EventKind.TokenSend, WalletUtils.EncodeAddress(swap.sourceAddress, platformName), "swap", Serialization.Serialize(new TokenEventData(){ chainAddress =platformAddress, symbol = swap.symbol, value = amount } )),
                            new Event(EventKind.TokenReceive, WalletUtils.EncodeAddress(destAddress, platformName), "swap", Serialization.Serialize(new TokenEventData(){ chainAddress =platformAddress, symbol = swap.symbol, value = amount } ))
                        }
                    };
            }

            throw new OracleException($"unknown transaction for {platformName} : {hash}");
        }

        protected override decimal PullPrice(string baseSymbol, string quoteSymbol)
        {
            // some dummy values, only really used in the test suite ...
            decimal price;
            switch (baseSymbol)
            {
                case "SOUL": price = 100; break;
                case "KCAL": price = 20; break;
                case "NEO": price = 200; break;
                case "ETH": price = 4000; break;
                case "BTC": price = 80000; break;
                default: throw new OracleException("Unknown token: "+baseSymbol);
            }

            return price / 100;
        }
    }

}
