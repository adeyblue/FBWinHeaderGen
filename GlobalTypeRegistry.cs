using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MetadataParser
{
    // this is used to resolve name-only type references to their actual types
    class GlobalTypeRegistry
    {
        class NSTypes
        {
            public SortedList<string, SimpleTypeHandleInfo> typeHandles;
            public SortedList<string, StructType<VarType>> structTypes;
            public SortedList<string, StructType<FunctionType>> interfaceTypes;
            public SortedList<string, FunctionType> functions;
            public SortedList<string, FunctionPointerType> functionPointers;
            public SortedList<string, StructType<ConstantValue>> enums;
            public SortedList<string, ConstantValue> constants;

            public NSTypes()
            {
                typeHandles = new SortedList<string, SimpleTypeHandleInfo>(500, StringComparer.InvariantCultureIgnoreCase);
                structTypes = new SortedList<string, StructType<VarType>>(500, StringComparer.InvariantCultureIgnoreCase);
                interfaceTypes = new SortedList<string, StructType<FunctionType>>(500, StringComparer.InvariantCultureIgnoreCase);
                functions = new SortedList<string, FunctionType>(500, StringComparer.InvariantCultureIgnoreCase);
                functionPointers = new SortedList<string, FunctionPointerType>(500, StringComparer.InvariantCultureIgnoreCase);
                enums = new SortedList<string, StructType<ConstantValue>>(50, StringComparer.InvariantCultureIgnoreCase);
                constants = new SortedList<string, ConstantValue>(500, StringComparer.InvariantCultureIgnoreCase);
            }
        }
        private Dictionary<string, NSTypes> nsTypeHandles;
        public GlobalTypeRegistry()
        {
            nsTypeHandles = new Dictionary<string, NSTypes>(350);
        }

        private NSTypes GetNamespaceTypes(string ns)
        {
            NSTypes nsEntries;
            if (!nsTypeHandles.TryGetValue(ns, out nsEntries))
            {
                nsEntries = new NSTypes();
                nsTypeHandles.Add(ns, nsEntries);
            }
            return nsEntries;
        }

        public void Add(string ns, string name, SimpleTypeHandleInfo typeInfo)
        {
            NSTypes nsEntries = GetNamespaceTypes(ns);
            nsEntries.typeHandles.Add(name, typeInfo);
        }

        public void Add(string ns, string name, StructType<VarType> structType)
        {
            NSTypes nsEntries = GetNamespaceTypes(ns);
            // there are structs that are different for different architectures
            // but with the same name eg Context. Right now we don't really care
            // which one gets added here, we're just bothered about the name existing
            nsEntries.structTypes.TryAdd(name, structType);
        }

        public void Add(string ns, string name, StructType<FunctionType> ifaceType)
        {
            NSTypes nsEntries = GetNamespaceTypes(ns);
            nsEntries.interfaceTypes.TryAdd(name, ifaceType);
        }

        public void Add(string ns, string name, FunctionType fnType)
        {
            NSTypes nsEntries = GetNamespaceTypes(ns);
            nsEntries.functions.TryAdd(name, fnType);
        }

        public void Add(string ns, string name, FunctionPointerType fnPtr)
        {
            NSTypes nsEntries = GetNamespaceTypes(ns);
            nsEntries.functionPointers.TryAdd(name, fnPtr);
        }

        public void Add(string ns, string name, StructType<ConstantValue> enumType)
        {
            NSTypes nsEntries = GetNamespaceTypes(ns);
            nsEntries.enums.TryAdd(name, enumType);
        }

        public void Add(string ns, string name, ConstantValue constant)
        {
            NSTypes nsEntries = GetNamespaceTypes(ns);
            nsEntries.constants.TryAdd(name, constant);
        }

        public SimpleTypeHandleInfo LookupHandle(string ns, string name)
        {
            NSTypes nsEntries;
            if (!nsTypeHandles.TryGetValue(ns, out nsEntries))
            {
                return null;
            }
            SimpleTypeHandleInfo hType;
            nsEntries.typeHandles.TryGetValue(name, out hType);
            return hType;
        }

        public StructType<FunctionType> LookupInterface(string ns, string name)
        {
            NSTypes nsEntries;
            if (!nsTypeHandles.TryGetValue(ns, out nsEntries))
            {
                return null;
            }
            StructType<FunctionType> intType;
            nsEntries.interfaceTypes.TryGetValue(name, out intType);
            return intType;
        }

        public StructType<VarType> LookupStruct(string ns, string name)
        {
            NSTypes nsEntries;
            if (!nsTypeHandles.TryGetValue(ns, out nsEntries))
            {
                return null;
            }
            StructType<VarType> strType;
            nsEntries.structTypes.TryGetValue(name, out strType);
            return strType;
        }

        public FunctionType LookupFunction(string ns, string name)
        {
            NSTypes nsEntries;
            if (!nsTypeHandles.TryGetValue(ns, out nsEntries))
            {
                return null;
            }
            FunctionType fnType;
            nsEntries.functions.TryGetValue(name, out fnType);
            return fnType;
        }

        public FunctionPointerType LookupFunctionPtr(string ns, string name)
        {
            NSTypes nsEntries;
            if (!nsTypeHandles.TryGetValue(ns, out nsEntries))
            {
                return null;
            }
            FunctionPointerType fnPtrType;
            nsEntries.functionPointers.TryGetValue(name, out fnPtrType);
            return fnPtrType;
        }

        public StructType<ConstantValue> LookupEnum(string ns, string name)
        {
            NSTypes nsEntries;
            if (!nsTypeHandles.TryGetValue(ns, out nsEntries))
            {
                return null;
            }
            StructType<ConstantValue> enumType;
            nsEntries.enums.TryGetValue(name, out enumType);
            return enumType;
        }

        public ConstantValue? LookupConstant(string ns, string name)
        {
            NSTypes nsEntries;
            if (!nsTypeHandles.TryGetValue(ns, out nsEntries))
            {
                return null;
            }
            ConstantValue enumType;
            nsEntries.constants.TryGetValue(name, out enumType);
            return enumType;
        }
    }
}
