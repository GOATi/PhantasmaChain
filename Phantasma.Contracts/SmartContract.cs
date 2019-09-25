using System;
using System.Text;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using Phantasma.Cryptography;
using Phantasma.Numerics;
using Phantasma.Core;
using Phantasma.VM;
using Phantasma.Storage.Context;
using Phantasma.Storage;
using Phantasma.Domain;

namespace Phantasma.Contracts
{
    public abstract class NativeContract : SmartContract
    {
        public override string Name => Kind.GetName();

        public abstract NativeContractKind Kind { get; }
    }

    public abstract class SmartContract : IContract
    {
        public ContractInterface ABI { get; private set; }
        public abstract string Name { get; }

        public IRuntime Runtime { get; private set; }

        public void SetRuntime(IRuntime runtime)
        {
            this.Runtime = runtime;
        }

        private Dictionary<string, MethodInfo> _methodTable = new Dictionary<string, MethodInfo>();

        private Address _address;
        public Address Address
        {
            get
            {
                if (_address.IsNull)
                {
                   _address = DomainExtensions.GetContractAddress(null, Name);
                }

                return _address;
            }
        }

        public SmartContract()
        {
            BuildMethodTable(this.GetType());

            _address = Address.Null;
        }

        // here we auto-initialize any fields from storage
        internal void LoadRuntimeData(IRuntime VM)
        {
            if (this.Runtime != null && this.Runtime != VM)
            {
                Runtime.Throw("runtime already set on this contract");
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

                    if (VM.Storage.Has(baseKey))
                    {
                        var bytes = VM.Storage.Get(baseKey);
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

                if (VM.Storage.Has(baseKey))
                {
                    var obj = VM.Storage.Get(baseKey, field.FieldType);
                    field.SetValue(this, obj);
                    continue;
                }
            }
        }

        // here we persist any modifed fields back to storage
        internal void UnloadRuntimeData()
        {
            Throw.IfNull(this.Runtime, nameof(Runtime));

            if (Runtime.IsReadOnlyMode())
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
                    this.Runtime.Storage.Put(baseKey, bytes);
                }
                else
                {
                    var obj = field.GetValue(this);
                    var bytes = Serialization.Serialize(obj);
                    this.Runtime.Storage.Put(baseKey, bytes);
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

            if (address.IsUser && Runtime.HasAddressScript(address))
            {
                return Runtime.InvokeTriggerOnAccount(address, AccountTrigger.OnWitness, address);
            }

            return Runtime.Transaction.IsSignedBy(address);
        }

        #region METHOD TABLE
        private void BuildMethodTable(Type type)
        {
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

        public bool HasInternalMethod(string methodName, out BigInteger gasCost)
        {
            gasCost = 10; // TODO make this depend on method
            return _methodTable.ContainsKey(methodName);
        }

        public object CallInternalMethod(IRuntime runtime, string name, object[] args)
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

        private object CastArgument(IRuntime runtime, object arg, Type expectedType)
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
                        return ((BigInteger)arg).ToSignedByteArray();
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
                    var address = runtime.LookUpName(name);
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
            return Runtime.GetChainByAddress(address) != null;
        }

        public bool IsRootChain(Address address)
        {
            var chain = Runtime.GetChainByAddress(address);
            if (chain == null)
            {
                return false;
            }

            return chain.IsRoot;
        }

        public bool IsSideChain(Address address)
        {
            var chain = Runtime.GetChainByAddress(address);
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

            var parentChain = this.Runtime.GetChainParent(this.Runtime.Chain.Name);
            return address == parentChain.Address;
        }

        public bool IsAddressOfChildChain(Address address)
        {
            var targetChain = Runtime.GetChainByAddress(address);
            var parentChain = Runtime.GetChainParent(targetChain.Name);
            return parentChain.Name == this.Runtime.Chain.Name;
        }
        #endregion
    }
}
