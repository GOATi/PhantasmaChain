﻿using System;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using Phantasma.Core.Utils;
using Phantasma.Cryptography;
using Phantasma.Numerics;
using Phantasma.Core;
using Phantasma.VM;
using Phantasma.VM.Contracts;
using Phantasma.Storage.Context;
using Phantasma.Storage;
using System.Text;
using System.IO;
using Phantasma.Core.Types;
using Phantasma.Blockchain.Contracts.Native;
using Phantasma.Blockchain.Tokens;
using System.Numerics;

namespace Phantasma.Blockchain.Contracts
{
    public abstract class SmartContract : IContract
    {
        public ContractInterface ABI { get; private set; }
        public abstract string Name { get; }

        public BigInteger Order { get; internal set; } // TODO remove this?

        private readonly Dictionary<byte[], byte[]> _storage = new Dictionary<byte[], byte[]>(new ByteArrayComparer()); // TODO remove this?

        public RuntimeVM Runtime { get; private set; }
        public StorageContext Storage => Runtime.ChangeSet;

        private Dictionary<string, MethodInfo> _methodTable = new Dictionary<string, MethodInfo>();

        private Address _address;
        public Address Address
        {
            get
            {
                if (_address.IsNull)
                {
                   _address = GetAddressForName(Name);
                }

                return _address;
            }
        }

        public SmartContract()
        {
            this.Order = 0;

            BuildMethodTable();

            _address = Address.Null;
        }

        public static Address GetAddressForName(string name)
        {
            return Cryptography.Address.FromHash(name);
        }

        // here we auto-initialize any fields from storage
        internal void LoadRuntimeData(RuntimeVM VM)
        {
            if (this.Runtime != null && this.Runtime != VM)
            {
                throw new ChainException("runtime already set on this contract");
            }

            this.Runtime = VM;

            var contractType = this.GetType();
            FieldInfo[] fields = contractType.GetFields(BindingFlags.NonPublic | BindingFlags.Instance);

            foreach (var field in fields)
            {
                var baseKey = $"_{this.Name}.{field.Name}".AsByteArray();

                var isStorageField = typeof(IStorageCollection).IsAssignableFrom(field.FieldType);
                if (isStorageField)
                {
                    var args = new object[] { baseKey, (StorageContext)VM.ChangeSet };
                    var obj = Activator.CreateInstance(field.FieldType, args);

                    field.SetValue(this, obj);
                    continue;
                }

                if (typeof(ISerializable).IsAssignableFrom(field.FieldType))
                {
                    ISerializable obj;

                    if (VM.ChangeSet.Has(baseKey))
                    {
                        var bytes = VM.ChangeSet.Get(baseKey);
                        obj = (ISerializable)Activator.CreateInstance(field.FieldType);
                        using (var stream = new MemoryStream(bytes))
                        {
                            using (var reader = new BinaryReader(stream))
                            {
                                obj.UnserializeData(reader);
                            }
                        }

                        field.SetValue(this, obj);
                        continue;
                    }
                }

                if (VM.ChangeSet.Has(baseKey))
                {
                    var obj = VM.ChangeSet.Get(baseKey, field.FieldType);
                    field.SetValue(this, obj);
                    continue;
                }
            }
        }

        // here we persist any modifed fields back to storage
        internal void UnloadRuntimeData()
        {
            Throw.IfNull(this.Runtime, nameof(Runtime));

            if (Runtime.readOnlyMode)
            {
                return;
            }

            var contractType = this.GetType();
            FieldInfo[] fields = contractType.GetFields(BindingFlags.NonPublic | BindingFlags.Instance);

            foreach (var field in fields)
            {
                var baseKey = $"_{this.Name}.{field.Name}".AsByteArray();

                var isStorageField = typeof(IStorageCollection).IsAssignableFrom(field.FieldType);
                if (isStorageField)
                {
                    continue;
                }

                if (typeof(ISerializable).IsAssignableFrom(field.FieldType))
                {
                    var obj = (ISerializable)field.GetValue(this);
                    var bytes = obj.Serialize();
                    this.Runtime.ChangeSet.Put(baseKey, bytes);
                }
                else
                {
                    var obj = field.GetValue(this);
                    var bytes = Serialization.Serialize(obj);
                    this.Runtime.ChangeSet.Put(baseKey, bytes);
                }
            }
        }

        public bool IsWitness(Address address)
        {
            if (address == this.Runtime.Chain.Address || address == this.Address) 
            {
                var frame = Runtime.frames.Skip(1).FirstOrDefault();
                return frame != null && frame.Context.Admin;
            }

            if (address.IsInterop)
            {
                return false;
            }

            if (Runtime.Transaction == null)
            {
                return false;
            }

            if (address.IsUser && Runtime.Nexus.HasScript(address))
            {
                return InvokeTriggerOnAccount(Runtime, address, AccountTrigger.OnWitness, address);
            }

            return Runtime.Transaction.IsSignedBy(address);
        }

        #region METHOD TABLE
        private void BuildMethodTable()
        {
            var type = this.GetType();

            var srcMethods = type.GetMethods(BindingFlags.Public | BindingFlags.InvokeMethod | BindingFlags.Instance);
            var methods = new List<ContractMethod>();

            var ignore = new HashSet<string>(new string[] { "ToString", "GetType", "Equals", "GetHashCode", "CallMethod", "SetTransaction" });

            foreach (var srcMethod in srcMethods)
            {
                var parameters = new List<ContractParameter>();
                var srcParams = srcMethod.GetParameters();

                var methodName = srcMethod.Name;
                if (methodName.StartsWith("get_"))
                {
                    methodName = methodName.Substring(4);
                }

                if (ignore.Contains(methodName))
                {
                    continue;
                }

                var isVoid = srcMethod.ReturnType == typeof(void);
                var returnType = isVoid ? VMType.None : VMObject.GetVMType(srcMethod.ReturnType);

                bool isValid = isVoid || returnType != VMType.None;
                if (!isValid)
                {
                    continue;
                }

                foreach (var srcParam in srcParams)
                {
                    var paramType = srcParam.ParameterType;
                    var vmtype = VMObject.GetVMType(paramType);

                    if (vmtype != VMType.None)
                    {
                        parameters.Add(new ContractParameter(srcParam.Name, vmtype));
                    }
                    else
                    {
                        isValid = false;
                        break;
                    }
                }

                if (isValid)
                {
                    _methodTable[methodName] = srcMethod;
                    var method = new ContractMethod(methodName, returnType, parameters.ToArray());
                    methods.Add(method);
                }
            }

            this.ABI = new ContractInterface(methods);
        }

        internal bool HasInternalMethod(string methodName, out BigInteger gasCost)
        {
            gasCost = 10; // TODO make this depend on method
            return _methodTable.ContainsKey(methodName);
        }

        internal object CallInternalMethod(RuntimeVM runtime, string name, object[] args)
        {
            Throw.If(!_methodTable.ContainsKey(name), "unknowm internal method");

            var method = _methodTable[name];
            Throw.IfNull(method, nameof(method));

            var parameters = method.GetParameters();
            for (int i = 0; i < parameters.Length; i++)
            {
                args[i] = CastArgument(runtime, args[i], parameters[i].ParameterType);
            }

            return method.Invoke(this, args);
        }

        private object CastArgument(RuntimeVM runtime, object arg, Type expectedType)
        {
            if (arg == null)
            {
                if (expectedType.IsArray)
                {
                    var elementType = expectedType.GetElementType();
                    var result = Array.CreateInstance(elementType, 0);
                    return result;
                }
                throw new Exception("Invalid cast for null VM object");
            }

            var receivedType = arg.GetType();
            if (expectedType == receivedType)
            {
                return arg;
            }

            if (expectedType.IsArray)
            {
                if (expectedType == typeof(byte[]))
                {
                    if (receivedType == typeof(string))
                    {
                        return Encoding.UTF8.GetBytes((string)arg);
                    }

                    if (receivedType == typeof(BigInteger))
                    {
                        return ((BigInteger)arg).ToByteArray();
                    }

                    if (receivedType == typeof(Hash))
                    {
                        return ((Hash)arg).ToByteArray();
                    }

                    if (receivedType == typeof(Address))
                    {
                        return ((Address)arg).PublicKey;
                    }

                    throw new Exception("cannot cast this object to a byte array");
                }
                else
                {
                    var dic = (Dictionary<VMObject, VMObject>)arg;
                    var elementType = expectedType.GetElementType();
                    var array = Array.CreateInstance(elementType, dic.Count);
                    for (int i = 0; i < array.Length; i++)
                    {
                        var key = new VMObject();
                        key.SetValue(i);

                        var val = dic[key].Data;
                        val = CastArgument(runtime, val, elementType);
                        array.SetValue(val, i);
                    }
                    return array;
                }
            }
            
            if (expectedType.IsEnum)
            {
                if (!receivedType.IsEnum)
                {
                    arg = Enum.Parse(expectedType, arg.ToString());
                    return arg;
                }
            }

            if (expectedType == typeof(Address))
            {
                if (receivedType == typeof(string))
                {
                    // when a string is passed instead of an address we do an automatic lookup and replace
                    var name = (string)arg;
                    var address = runtime.Nexus.LookUpName(name);
                    return address;
                }
            }

            /*
            if (expectedType == typeof(BigInteger))
            {
                if (receivedType == typeof(string))
                {
                    var value = (string)arg;
                    if (BigInteger.TryParse(value, out BigInteger number))
                    {
                        arg = number;
                    }
                }
            }*/
            
            if (typeof(ISerializable).IsAssignableFrom(expectedType))
            {
                if (receivedType == typeof(byte[]))
                {
                    var bytes = (byte[])arg;
                    arg = Serialization.Unserialize(bytes, expectedType);
                    return arg;
                }
            }

            return arg;
        }

        #endregion

        #region SIDE CHAINS
        public bool IsChain(Address address)
        {
            return Runtime.Nexus.FindChainByAddress(address) != null;
        }

        public bool IsRootChain(Address address)
        {
            var chain = Runtime.Nexus.FindChainByAddress(address);
            if (chain == null)
            {
                return false;
            }

            return chain.IsRoot;
        }

        public bool IsSideChain(Address address)
        {
            var chain = Runtime.Nexus.FindChainByAddress(address);
            if (chain == null)
            {
                return false;
            }

            return !chain.IsRoot;
        }

        public bool IsAddressOfParentChain(Address address)
        {
            if (Runtime.Chain.IsRoot)
            {
                return false;
            }

            return address == this.Runtime.ParentChain.Address;
        }

        public bool IsAddressOfChildChain(Address address)
        {
            var parentName = Runtime.Nexus.GetParentChainByAddress(address);
            var parent = Runtime.Nexus.FindChainByName(parentName);
            if (parent== null)
            {
                return false;
            }

            return parent.Address == this.Runtime.Chain.Address;
        }
        #endregion

        #region TRIGGERS
        public static bool InvokeTriggerOnAccount(RuntimeVM runtimeVM, Address address, AccountTrigger trigger, params object[] args)
        {
            if (address.IsNull)
            {
                return false;
            }

            if (address.IsUser)
            {
                var accountScript = runtimeVM.Nexus.LookUpAddressScript(address);
                return InvokeTrigger(runtimeVM, accountScript, trigger.ToString(), args);
            }

            return true;
        }

        public static bool InvokeTriggerOnToken(RuntimeVM runtimeVM, TokenInfo token, TokenTrigger trigger, params object[] args)
        {
            return InvokeTrigger(runtimeVM, token.Script, trigger.ToString(), args);
        }

        public static bool InvokeTrigger(RuntimeVM runtimeVM, byte[] script, string triggerName, params object[] args)
        {
            if (script == null || script.Length == 0)
            {
                return true;
            }

            var leftOverGas = (uint)(runtimeVM.MaxGas - runtimeVM.UsedGas);
            var runtime = new RuntimeVM(script, runtimeVM.Chain, runtimeVM.Time, runtimeVM.Transaction, runtimeVM.ChangeSet, runtimeVM.Oracle, false, true);
            runtime.ThrowOnFault = true;

            for (int i=args.Length - 1; i>=0; i--)
            {
                var obj = VMObject.FromObject(args[i]);
                runtime.Stack.Push(obj);
            }
            runtime.Stack.Push(VMObject.FromObject(triggerName));

            var state = runtime.Execute();
            // TODO catch VM exceptions?

            // propagate gas consumption
            // TODO this should happen not here but in real time during previous execution, to prevent gas attacks
            runtimeVM.ConsumeGas(runtime.UsedGas);

            if (state == ExecutionState.Halt)
            {
                // propagate events to the other runtime
                foreach (var evt in runtime.Events)
                {
                    runtimeVM.Notify(evt.Kind, evt.Address, evt.Data);
                }
              
                return true;
            }
            else
            {
                return false;
            }
        }
        #endregion
    }
}
