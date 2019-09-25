﻿using Phantasma.Cryptography;
using Phantasma.Domain;
using Phantasma.Numerics;
using Phantasma.Storage.Context;

namespace Phantasma.Contracts
{
    public sealed class AccountContract : NativeContract
    {
        public override NativeContractKind Kind => NativeContractKind.Account;

        internal StorageMap _addressMap; //<Address, string> 
        internal StorageMap _nameMap; //<string, Address> 
        internal StorageMap _scriptMap; //<Address, byte[]> 
        internal StorageMap _metadata;

        public static readonly BigInteger RegistrationCost = UnitConversion.ToBigInteger(0.1m, DomainSettings.FuelTokenDecimals);

        public void RegisterName(Address target, string name)
        {
            Runtime.Expect(target.IsUser, "must be user address");
            Runtime.Expect(target != Runtime.Nexus.GenesisAddress, "address must not be genesis");
            Runtime.Expect(Runtime.IsWitness(target), "invalid witness");
            Runtime.Expect(Validation.IsValidIdentifier(name), "invalid name");

            Runtime.Expect(!_addressMap.ContainsKey(target), "address already has a name");
            Runtime.Expect(!_nameMap.ContainsKey(name), "name already used");

            _addressMap.Set(target, name);
            _nameMap.Set(name, target);

            Runtime.Notify(EventKind.AddressRegister, target, name);
        }

        public void RegisterScript(Address target, byte[] script)
        {
            Runtime.Expect(target.IsUser, "must be user address");
            Runtime.Expect(target != Runtime.Nexus.GenesisAddress, "address must not be genesis");
            Runtime.Expect(Runtime.IsWitness(target), "invalid witness");

            Runtime.Expect(script.Length < 1024, "invalid script length");

            Runtime.Expect(!_scriptMap.ContainsKey(target), "address already has a script");

            var witnessCheck = Runtime.InvokeTrigger(script, AccountTrigger.OnWitness.ToString(), Address.Null);
            Runtime.Expect(!witnessCheck, "script does not handle OnWitness correctly");

            _scriptMap.Set(target, script);

            // TODO? Runtime.Notify(EventKind.AddressRegister, target, script);
        }

        public bool HasScript(Address address)
        {
            if (address.IsUser)
            {
                return _scriptMap.ContainsKey(address);
            }

            return false;
        }

        public void SetMetadata(Address target, string key, string value)
        {
            Runtime.Expect(target.IsUser, "must be user address");
            Runtime.Expect(Runtime.IsWitness(target), "invalid witness");

            var metadataEntries = _metadata.Get<Address, StorageList>(target);

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

            Runtime.Notify(EventKind.Metadata, target, new MetadataEventData() { type = "account", metadata = metadata });
        }

        public string GetMetadata(Address address, string key)
        {
            var metadataEntries = _metadata.Get<Address, StorageList>(address);

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

        public Metadata[] GetMetadataList(Address address)
        {
            var metadataEntries = _metadata.Get<Address, StorageList>(address);
            return metadataEntries.All<Metadata>();
        }

        public string LookUpAddress(Address target)
        {
            if (target == Runtime.Nexus.GenesisAddress)
            {
                return Validation.GENESIS;
            }

            if (_addressMap.ContainsKey(target))
            {
                return _addressMap.Get<Address, string>(target);
            }

            return Validation.ANONYMOUS;
        }

        public byte[] LookUpScript(Address target)
        {
            if (_scriptMap.ContainsKey(target))
            {
                return _scriptMap.Get<Address, byte[]>(target);
            }

            return new byte[0];
        }

        public Address LookUpName(string name)
        {
            if (name == Validation.ANONYMOUS)
            {
                return Address.Null;
            }

            if (name == Validation.GENESIS)
            {
                return Runtime.Nexus.GenesisAddress;
            }

            if (_nameMap.ContainsKey(name))
            {
                return _nameMap.Get<string, Address>(name);
            }

            return Address.Null;
        }
    }
}
