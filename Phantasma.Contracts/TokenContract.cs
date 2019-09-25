﻿using Phantasma.Blockchain.Tokens;
using Phantasma.Cryptography;
using Phantasma.Storage;
using Phantasma.Numerics;
using System.Linq;
using Phantasma.Storage.Context;
using Phantasma.Domain;

namespace Phantasma.Blockchain.Contracts.Native
{
    public enum TokenTrigger
    {
        OnMint, // address, symbol, amount
        OnBurn, // address, symbol, amount
        OnSend, // address, symbol, amount
        OnReceive, // address, symbol, amount
        OnMetadata // address, symbol, key, value
    }

    public sealed class TokenContract : SmartContract
    {
        public override string Name => Nexus.TokenContractName;

        public static readonly string TriggerMint = "OnMint";
        public static readonly string TriggerBurn = "OnBurn";
        public static readonly string TriggerSend = "OnSend";
        public static readonly string TriggerReceive = "OnReceive";
        public static readonly string TriggerMetadata = "OnMetadata";

        private StorageMap _metadata;

        #region FUNGIBLE TOKENS
        public void SendTokens(Address targetChainAddress, Address from, Address to, string symbol, BigInteger amount)
        {
            Runtime.Expect(IsWitness(from), "invalid witness");

            Runtime.Expect(IsAddressOfParentChain(targetChainAddress) || IsAddressOfChildChain(targetChainAddress), "target must be parent or child chain");

            Runtime.Expect(!to.IsInterop, "destination cannot be interop address");

            var targetChain = this.Runtime.Nexus.FindChainByAddress(targetChainAddress);

            Runtime.Expect(this.Runtime.Nexus.TokenExists(symbol), "invalid token");
            var tokenInfo = this.Runtime.Nexus.GetTokenInfo(symbol);
            Runtime.Expect(tokenInfo.Flags.HasFlag(TokenFlags.Fungible), "must be fungible token");

            if (tokenInfo.IsCapped())
            {
                var sourceSupplies = new SupplySheet(symbol, this.Runtime.Chain, Runtime.Nexus);
                var targetSupplies = new SupplySheet(symbol, targetChain, Runtime.Nexus);

                if (IsAddressOfParentChain(targetChainAddress))
                {
                    Runtime.Expect(sourceSupplies.MoveToParent(this.Storage, amount), "source supply check failed");
                }
                else // child chain
                {
                    Runtime.Expect(sourceSupplies.MoveToChild(this.Storage, targetChain.Name, amount), "source supply check failed");
                }
            }

            Runtime.Expect(Runtime.Nexus.BurnTokens(Runtime, symbol, from, amount, true), "burn failed");

            Runtime.Notify(EventKind.TokenBurn, from, new TokenEventData() { symbol = symbol, value = amount, chainAddress = Runtime.Chain.Address });
            Runtime.Notify(EventKind.TokenEscrow, to, new TokenEventData() { symbol = symbol, value = amount, chainAddress = targetChainAddress });
        }

        public void MintTokens(Address from, Address to, string symbol, BigInteger amount)
        {
            Runtime.Expect(IsWitness(from), "invalid witness");

            Runtime.Expect(amount > 0, "amount must be positive and greater than zero");

            Runtime.Expect(this.Runtime.Nexus.TokenExists(symbol), "invalid token");
            var tokenInfo = this.Runtime.Nexus.GetTokenInfo(symbol);
            Runtime.Expect(tokenInfo.Flags.HasFlag(TokenFlags.Fungible), "token must be fungible");
            Runtime.Expect(!tokenInfo.Flags.HasFlag(TokenFlags.Fiat), "token can't be fiat");

            Runtime.Expect(!to.IsInterop, "destination cannot be interop address");

            Runtime.Expect(Runtime.Nexus.MintTokens(Runtime, symbol, to, amount, false), "minting failed");

            Runtime.Notify(EventKind.TokenMint, to, new TokenEventData() { symbol = symbol, value = amount, chainAddress = this.Runtime.Chain.Address });
        }

        public void BurnTokens(Address from, string symbol, BigInteger amount)
        {
            Runtime.Expect(amount > 0, "amount must be positive and greater than zero");
            Runtime.Expect(IsWitness(from), "invalid witness");

            Runtime.Expect(this.Runtime.Nexus.TokenExists(symbol), "invalid token");
            var tokenInfo = this.Runtime.Nexus.GetTokenInfo(symbol);
            Runtime.Expect(tokenInfo.Flags.HasFlag(TokenFlags.Fungible), "token must be fungible");
            Runtime.Expect(tokenInfo.IsBurnable(), "token must be burnable");
            Runtime.Expect(!tokenInfo.Flags.HasFlag(TokenFlags.Fiat), "token can't be fiat");

            Runtime.Expect(this.Runtime.Nexus.BurnTokens(Runtime, symbol, from, amount, false), "burning failed");

            Runtime.Notify(EventKind.TokenBurn, from, new TokenEventData() { symbol = symbol, value = amount });
        }

        public void TransferTokens(Address source, Address destination, string symbol, BigInteger amount)
        {
            Runtime.Expect(amount > 0, "amount must be positive and greater than zero");
            Runtime.Expect(source != destination, "source and destination must be different");
            Runtime.Expect(IsWitness(source), "invalid witness");
            Runtime.Expect(!Runtime.IsTrigger, "not allowed inside a trigger");

            if (destination.IsInterop)
            {
                Runtime.Expect(Runtime.Chain.IsRoot, "interop transfers only allowed in main chain");
                Runtime.CallContext("interop", "WithdrawTokens", source, destination, symbol, amount);
                return;
            }

            Runtime.Expect(this.Runtime.Nexus.TokenExists(symbol), "invalid token");
            var tokenInfo = this.Runtime.Nexus.GetTokenInfo(symbol);
            Runtime.Expect(tokenInfo.Flags.HasFlag(TokenFlags.Fungible), "token must be fungible");
            Runtime.Expect(tokenInfo.Flags.HasFlag(TokenFlags.Transferable), "token must be transferable");

            Runtime.Expect(Runtime.Nexus.TransferTokens(Runtime, symbol, source, destination, amount), "transfer failed");

            Runtime.Notify(EventKind.TokenSend, source, new TokenEventData() { chainAddress = this.Runtime.Chain.Address, value = amount, symbol = symbol });
            Runtime.Notify(EventKind.TokenReceive, destination, new TokenEventData() { chainAddress = this.Runtime.Chain.Address, value = amount, symbol = symbol });
        }

        public BigInteger GetBalance(Address address, string symbol)
        {
            Runtime.Expect(this.Runtime.Nexus.TokenExists(symbol), "invalid token");
            var token = this.Runtime.Nexus.GetTokenInfo(symbol);
            Runtime.Expect(token.Flags.HasFlag(TokenFlags.Fungible), "token must be fungible");

            var balances = new BalanceSheet(symbol);
            return balances.Get(this.Storage, address);
        }
        #endregion

        #region NON FUNGIBLE TOKENS
        public BigInteger[] GetTokens(Address address, string symbol)
        {
            Runtime.Expect(this.Runtime.Nexus.TokenExists(symbol), "invalid token");
            var token = this.Runtime.Nexus.GetTokenInfo(symbol);
            Runtime.Expect(!token.IsFungible(), "token must be non-fungible");

            var ownerships = new OwnershipSheet(symbol);
            return ownerships.Get(this.Storage, address).ToArray();
        }

        // TODO minting a NFT will require a certain amount of KCAL that is released upon burning
        public BigInteger MintToken(Address from, Address to, string symbol, byte[] rom, byte[] ram, BigInteger value)
        {
            Runtime.Expect(this.Runtime.Nexus.TokenExists(symbol), "invalid token");
            var tokenInfo = this.Runtime.Nexus.GetTokenInfo(symbol);
            Runtime.Expect(!tokenInfo.IsFungible(), "token must be non-fungible");
            Runtime.Expect(IsWitness(from), "invalid witness");

            Runtime.Expect(!to.IsInterop, "destination cannot be interop address");
            Runtime.Expect(Runtime.Chain.Name == Nexus.RootChainName, "can only mint nft in root chain");

            Runtime.Expect(rom.Length <= TokenContent.MaxROMSize, "ROM size exceeds maximum allowed");
            Runtime.Expect(ram.Length <= TokenContent.MaxRAMSize, "RAM size exceeds maximum allowed");

            var tokenID = this.Runtime.Nexus.CreateNFT(symbol, Runtime.Chain.Address, rom, ram);
            Runtime.Expect(tokenID > 0, "invalid tokenID");

            Runtime.Expect(Runtime.Nexus.MintToken(Runtime, symbol, to, tokenID, false), "minting failed");

            if (tokenInfo.IsBurnable())
            {
                Runtime.Expect(value > 0, "token must have value");
                Runtime.Expect(Runtime.Nexus.TransferTokens(Runtime, Nexus.FuelTokenSymbol, from, Runtime.Chain.Address, tokenID), "minting escrow failed");
                Runtime.Notify(EventKind.TokenEscrow, to, new TokenEventData() { symbol = symbol, value = value, chainAddress = Runtime.Chain.Address });
            }
            else
            {
                Runtime.Expect(value == 0, "non-burnable must have value zero");
            }

            Runtime.Notify(EventKind.TokenMint, to, new TokenEventData() { symbol = symbol, value = tokenID, chainAddress = Runtime.Chain.Address });
            return tokenID;
        }

        public void BurnToken(Address from, string symbol, BigInteger tokenID)
        {
            Runtime.Expect(IsWitness(from), "invalid witness");

            Runtime.Expect(this.Runtime.Nexus.TokenExists(symbol), "invalid token");
            var tokenInfo = this.Runtime.Nexus.GetTokenInfo(symbol);
            Runtime.Expect(!tokenInfo.IsFungible(), "token must be non-fungible");
            Runtime.Expect(tokenInfo.IsBurnable(), "token must be burnable");

            var nft = Runtime.Nexus.GetNFT(symbol, tokenID);

            Runtime.Expect(Runtime.Nexus.BurnToken(Runtime, symbol, from, tokenID, false), "burn failed");

            Runtime.Notify(EventKind.TokenBurn, from, new TokenEventData() { symbol = symbol, value = tokenID, chainAddress = Runtime.Chain.Address });
        }

        public void TransferToken(Address source, Address destination, string symbol, BigInteger tokenID)
        {
            Runtime.Expect(IsWitness(source), "invalid witness");

            Runtime.Expect(source != destination, "source and destination must be different");

            Runtime.Expect(this.Runtime.Nexus.TokenExists(symbol), "invalid token");
            var tokenInfo = this.Runtime.Nexus.GetTokenInfo(symbol);
            Runtime.Expect(!tokenInfo.IsFungible(), "token must be non-fungible");

            Runtime.Expect(Runtime.Nexus.TransferToken(Runtime, symbol, source, destination, tokenID), "transfer failed");

            Runtime.Notify(EventKind.TokenSend, source, new TokenEventData() { chainAddress = this.Runtime.Chain.Address, value = tokenID, symbol = symbol });
            Runtime.Notify(EventKind.TokenReceive, destination, new TokenEventData() { chainAddress = this.Runtime.Chain.Address, value = tokenID, symbol = symbol });
        }

        public void SendToken(Address targetChainAddress, Address from, Address to, string symbol, BigInteger tokenID)
        {
            Runtime.Expect(IsWitness(from), "invalid witness");

            Runtime.Expect(IsAddressOfParentChain(targetChainAddress) || IsAddressOfChildChain(targetChainAddress), "source must be parent or child chain");

            Runtime.Expect(!to.IsInterop, "destination cannot be interop address");

            var targetChain = this.Runtime.Nexus.FindChainByAddress(targetChainAddress);

            Runtime.Expect(this.Runtime.Nexus.TokenExists(symbol), "invalid token");
            var tokenInfo = this.Runtime.Nexus.GetTokenInfo(symbol);
            Runtime.Expect(!tokenInfo.Flags.HasFlag(TokenFlags.Fungible), "must be non-fungible token");

            if (tokenInfo.IsCapped())
            {
                var supplies = new SupplySheet(symbol, this.Runtime.Chain, Runtime.Nexus);

                BigInteger amount = 1;

                if (IsAddressOfParentChain(targetChainAddress))
                {
                    Runtime.Expect(supplies.MoveToParent(this.Storage, amount), "source supply check failed");
                }
                else // child chain
                {
                    Runtime.Expect(supplies.MoveToChild(this.Storage, this.Runtime.Chain.Name, amount), "source supply check failed");
                }
            }

            Runtime.Expect(Runtime.Nexus.TransferToken(Runtime, symbol, from, targetChainAddress, tokenID), "take token failed");

            Runtime.Notify(EventKind.TokenBurn, from, new TokenEventData() { symbol = symbol, value = tokenID, chainAddress = Runtime.Chain.Address });
            Runtime.Notify(EventKind.TokenEscrow, to, new TokenEventData() { symbol = symbol, value = tokenID, chainAddress = targetChainAddress });
        }

        #endregion

        #region SETTLEMENTS
        // NOTE we should later prevent contracts from manipulating those
        private StorageMap _settledTransactions; //<Hash, bool>

        public bool IsSettled(Hash hash)
        {
            return _settledTransactions.ContainsKey(hash);
        }

        private void RegisterHashAsKnown(Hash hash)
        {
            _settledTransactions.Set(hash, true);
        }

        private void DoSettlement(Chain sourceChain, Address targetAddress, TokenEventData data)
        {
            var symbol = data.symbol;
            var value = data.value;

            Runtime.Expect(value > 0, "value must be greater than zero");
            Runtime.Expect(targetAddress.IsUser, "target must not user address");

            Runtime.Expect(this.Runtime.Nexus.TokenExists(symbol), "invalid token");
            var tokenInfo = this.Runtime.Nexus.GetTokenInfo(symbol);

            if (tokenInfo.IsCapped())
            {
                var supplies = new SupplySheet(symbol, this.Runtime.Chain, Runtime.Nexus);
                
                if (IsAddressOfParentChain(sourceChain.Address))
                {
                    Runtime.Expect(supplies.MoveFromParent(this.Storage, value), "target supply check failed");
                }
                else // child chain
                {
                    Runtime.Expect(supplies.MoveFromChild(this.Storage, sourceChain.Name, value), "target supply check failed");
                }
            }

            if (tokenInfo.Flags.HasFlag(TokenFlags.Fungible))
            {
                Runtime.Expect(Runtime.Nexus.MintTokens(Runtime, symbol, targetAddress, value, true), "mint failed");
            }
            else
            {
                Runtime.Expect(Runtime.Nexus.MintToken(Runtime, symbol, targetAddress, value, true), "mint failed");
            }

            Runtime.Notify(EventKind.TokenReceive, targetAddress, new TokenEventData() { symbol = symbol, value = value, chainAddress = sourceChain.Address });
        }

        public void SettleBlock(Address sourceChainAddress, Hash hash)
        {
            Runtime.Expect(IsAddressOfParentChain(sourceChainAddress) || IsAddressOfChildChain(sourceChainAddress), "source must be parent or child chain");

            Runtime.Expect(!IsSettled(hash), "hash already settled");

            var sourceChain = this.Runtime.Nexus.FindChainByAddress(sourceChainAddress);

            var block = sourceChain.FindBlockByHash(hash);
            Runtime.Expect(block != null, "invalid block");

            int settlements = 0;

            foreach (var txHash in block.TransactionHashes)
            {
                var evts = block.GetEventsForTransaction(txHash);

                foreach (var evt in evts)
                {
                    if (evt.Kind == EventKind.TokenEscrow)
                    {
                        var data = Serialization.Unserialize<TokenEventData>(evt.Data);
                        if (data.chainAddress == this.Runtime.Chain.Address)
                        {
                            DoSettlement(sourceChain, evt.Address, data);
                            settlements++;
                        }
                    }
                }
            }

            Runtime.Expect(settlements > 0, "no settlements in the block");
            RegisterHashAsKnown(hash);
        }
        #endregion

        #region METADATA
        public void SetMetadata(Address from, string symbol, string key, string value)
        {
            Runtime.Expect(Runtime.Nexus.TokenExists(symbol), "token not found");
            var tokenInfo = this.Runtime.Nexus.GetTokenInfo(symbol);

            Runtime.Expect(IsWitness(from), "invalid witness");

            var tokenTriggerResult = SmartContract.InvokeTrigger(Runtime, tokenInfo.Script, TokenContract.TriggerMetadata, from, symbol, key, value);
            Runtime.Expect(tokenTriggerResult, "trigger failed");

            var metadataEntries = _metadata.Get<string, StorageList>(symbol);

            int index = -1;

            var count = metadataEntries.Count();
            for (int i = 0; i < count; i++)
            {
                var temp = metadataEntries.Get<Metadata>(i);
                if (temp.key == key)
                {
                    index = i;
                    break;
                }
            }

            var metadata = new Metadata() { key = key, value = value };
            if (index >= 0)
            {
                metadataEntries.Replace<Metadata>(index, metadata);
            }
            else
            {
                metadataEntries.Add<Metadata>(metadata);
            }

            Runtime.Notify(EventKind.Metadata, from, new MetadataEventData() { type = "token", metadata = metadata });
        }

        public string GetMetadata(string symbol, string key)
        {
            Runtime.Expect(Runtime.Nexus.TokenExists(symbol), "token not found");
            var token = this.Runtime.Nexus.GetTokenInfo(symbol);

            var metadataEntries = _metadata.Get<string, StorageList>(symbol);

            var count = metadataEntries.Count();
            for (int i = 0; i < count; i++)
            {
                var temp = metadataEntries.Get<Metadata>(i);
                if (temp.key == key)
                {
                    return temp.value;
                }
            }

            return null;
        }

        public Metadata[] GetMetadataList(string symbol)
        {
            Runtime.Expect(Runtime.Nexus.TokenExists(symbol), "token not found");
            var token = this.Runtime.Nexus.GetTokenInfo(symbol);

            var metadataEntries = _metadata.Get<string, StorageList>(symbol);

            return metadataEntries.All<Metadata>();
        }
        #endregion
    }
}
