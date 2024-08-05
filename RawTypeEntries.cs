using System;
using System.Collections.Generic;

namespace MetadataParser
{
    class RawTypeEntries
    {
        [Flags]
        public enum ObjectType
        {
            None = 0,
            Constant = 1,
            Function = 2,
            Struct = 4,
            Interface = 8,
            FunctionPointer = 16,
            Enum = 32
        }

        public List<ConstantValue> Constants { get; init; }
        public List<FunctionType> Functions { get; init; }
        public List<StructType<VarType>> Structs { get; init; }
        public List<StructType<FunctionType>> Interfaces { get; init; }
        public List<StructType<ConstantValue>> Enums { get; init; }
        public List<FunctionPointerType> FunctionPointers { get; init; }
        public Dictionary<string, ObjectType> NamesToTypeMapping { get { return lowerNamesToTypes; } }

        private Dictionary<string, ObjectType> lowerNamesToTypes;
        private Dictionary<string, List<StructType<VarType>>> multiArchStructs;
        private Dictionary<string, List<FunctionType>> multiArchFns;
        private Dictionary<string, List<FunctionPointerType>> multiArchFnPtrs;

        public RawTypeEntries()
        {
            Constants = new List<ConstantValue>(500);
            Functions = new List<FunctionType>(200);
            Interfaces = new List<StructType<FunctionType>>(100);
            Structs = new List<StructType<VarType>>(150);
            Enums = new List<StructType<ConstantValue>>(50);
            FunctionPointers = new List<FunctionPointerType>(50);
            lowerNamesToTypes = new Dictionary<string, ObjectType>(500);
            multiArchStructs = new Dictionary<string, List<StructType<VarType>>>(150);
            multiArchFns = new Dictionary<string, List<FunctionType>>(50);
            multiArchFnPtrs = new Dictionary<string, List<FunctionPointerType>>(50);
        }

        private void AddLowerTypeName(string name, ObjectType type)
        {
            string lowerName = name.ToLowerInvariant();
            if(!lowerNamesToTypes.TryAdd(lowerName, type))
            {
                lowerNamesToTypes[lowerName] |= type;
            }
        }

        public void Add(ConstantValue con)
        {
            Constants.Add(con);
            AddLowerTypeName(con.varType.Name, ObjectType.Constant);
        }

        private void AddMultiArchThing<T>(Dictionary<string, List<T>> cont, T thing, string name)
        {
            List<T> multiArchItems;
            if (!cont.TryGetValue(name, out multiArchItems))
            {
                multiArchItems = new List<T>();
                cont.Add(name, multiArchItems);
            }
            multiArchItems.Add(thing);
        }
        
        public void Add(FunctionType fun)
        {
            Functions.Add(fun);
            string funName = fun.Name;
            AddLowerTypeName(funName, ObjectType.Function);
            AddMultiArchThing(multiArchFns, fun, funName);
        }

        public void Add(StructType<VarType> theStruct)
        {
            Structs.Add(theStruct);
            string structName = theStruct.Name;
            AddLowerTypeName(structName, ObjectType.Struct);
            AddMultiArchThing(multiArchStructs, theStruct, structName);
        }

        public void Add(FunctionPointerType fptr)
        {
            FunctionPointers.Add(fptr);
            string fnPtrName = fptr.Name;
            AddLowerTypeName(fnPtrName, ObjectType.FunctionPointer);
            AddMultiArchThing(multiArchFnPtrs, fptr, fnPtrName);
        }

        public void Add(StructType<ConstantValue> enumType)
        {
            Enums.Add(enumType);
            AddLowerTypeName(enumType.Name, ObjectType.Enum);
        }

        public void Add(StructType<FunctionType> iface)
        {
            Interfaces.Add(iface);
            AddLowerTypeName(iface.Name, ObjectType.Interface);
        }

        public ObjectType IsNameSomethingElse(string name, ObjectType disregardType)
        {
            RawTypeEntries.ObjectType combinedTypes;
            if (lowerNamesToTypes.TryGetValue(name.ToLowerInvariant(), out combinedTypes))
            {
                combinedTypes &= ~disregardType;
            }
            return combinedTypes;
        }

        private Dictionary<string, List<T>> RemoveSingleArchItems<T>(Dictionary<string, List<T>> multiArchCont)
        {
            List<string> names = new List<string>(multiArchCont.Keys);
            // remove all the ones that don't have multiple definitions
            foreach (string iter in names)
            {
                if (multiArchCont[iter].Count < 2)
                {
                    multiArchCont.Remove(iter);
                }
            }
            return multiArchCont;
        }

        public Dictionary<string, List<StructType<VarType>>> GetMultiArchStructs()
        {
            return RemoveSingleArchItems(multiArchStructs);
        }

        public Dictionary<string, List<FunctionType>> GetMultiArchFns()
        {
            return RemoveSingleArchItems(multiArchFns);
        }

        public Dictionary<string, List<FunctionPointerType>> GetMultiArchFnPtrs()
        {
            return RemoveSingleArchItems(multiArchFnPtrs);
        }
    }
}
