﻿using Phantasma.Storage.Utils;
using Phantasma.VM;
using System.Collections.Generic;
using System.IO;

namespace Phantasma.Domain
{
    public interface IContract
    {
        string Name { get; }
        ContractInterface ABI { get; }
    }

    public enum NativeContractKind
    {
        Gas,
        Block,
        Nexus,
        Stake,
        Swap,
        Account,
        Consensus,
        Governance,
        Storage,
        Validator,
        Interop,
        Exchange,
        Privacy,
        Relay,
        Bomb,
        Ranking,
        Market,
        Friends,
        Mail,
    }

    public sealed class ContractInterface
    {
        private Dictionary<string, ContractMethod> _methods = new Dictionary<string, ContractMethod>();
        public IEnumerable<ContractMethod> Methods => _methods.Values;

        public ContractMethod this[string name]
        {
            get
            {
                return FindMethod(name);
            }
        }

        public ContractInterface(IEnumerable<ContractMethod> methods)
        {
            foreach (var entry in methods)
            {
                _methods[entry.name] = entry;
            }
        }

        public ContractMethod FindMethod(string name)
        {
            if (_methods.ContainsKey(name))
            {
                return _methods[name];
            }

            return null;
        }

        /// <summary>
        /// Checks if this ABI implements a specific method
        /// </summary>
        public bool Implements(ContractMethod method)
        {
            if (!_methods.ContainsKey(method.name))
            {
                return false;
            }

            var thisMethod = _methods[method.name];
            if (thisMethod.parameters.Length != method.parameters.Length)
            {
                return false;
            }

            for (int i = 0; i < method.parameters.Length; i++)
            {
                if (thisMethod.parameters[i].type != method.parameters[i].type)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Checks if this ABI implements of other ABI (eg: other is a subset of this)
        /// </summary>
        public bool Implements(ContractInterface other)
        {
            foreach (var method in other.Methods)
            {
                if (!this.Implements(method))
                {
                    return false;
                }
            }

            return true;
        }

        public static ContractInterface Unserialize(BinaryReader reader)
        {
            var len = reader.ReadByte();
            var methods = new ContractMethod[len];
            for (int i = 0; i < len; i++)
            {
                methods[i] = ContractMethod.Unserialize(reader);
            }

            return new ContractInterface(methods);
        }

        public void Serialize(BinaryWriter writer)
        {
            writer.Write((byte)_methods.Count);
            foreach (var method in _methods.Values)
            {
                method.Serialize(writer);
            }
        }
    }

    public struct ContractParameter
    {
        public readonly string name;
        public readonly VMType type;

        public ContractParameter(string name, VMType vmtype)
        {
            this.name = name;
            this.type = vmtype;
        }
    }

    public class ContractMethod
    {
        public readonly string name;
        public readonly VMType returnType;
        public readonly ContractParameter[] parameters;

        public ContractMethod(string name, VMType returnType, params ContractParameter[] parameters)
        {
            this.name = name;
            this.returnType = returnType;
            this.parameters = parameters;
        }

        public override string ToString()
        {
            return $"{name} => {returnType}";
        }

        public static ContractMethod Unserialize(BinaryReader reader)
        {
            var name = reader.ReadVarString();
            var returnType = (VMType)reader.ReadByte();
            var len = reader.ReadByte();
            var parameters = new ContractParameter[len];
            for (int i = 0; i < len; i++)
            {
                var pName = reader.ReadVarString();
                var pVMType = (VMType)reader.ReadByte();
                parameters[i] = new ContractParameter(pName, pVMType);
            }

            return new ContractMethod(name, returnType, parameters);
        }

        public void Serialize(BinaryWriter writer)
        {
            writer.WriteVarString(name);
            writer.Write((byte)returnType);
            writer.Write((byte)parameters.Length);
            foreach (var entry in parameters)
            {
                writer.WriteVarString(entry.name);
                writer.Write((byte)entry.type);
            }
        }

        public byte[] ToArray()
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream))
                {
                    Serialize(writer);
                    return stream.ToArray();
                }
            }
        }

        /*
        public T Invoke<T>(IContract contract, params object[] args)
        {
            return (T)Invoke(contract, args);
        }

        public object Invoke(IContract contract, params object[] args)
        {
            Throw.IfNull(contract, "null contract");
            Throw.IfNull(args, "null args");
            Throw.If(args.Length != this.parameters.Length, "invalid arg count");

            var type = contract.GetType();
            var method = type.GetMethod(this.name);
            Throw.IfNull(method, "ABI mismatch");

            return method.Invoke(contract, args);
        }*/
    }

}
