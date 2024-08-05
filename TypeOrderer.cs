using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace MetadataParser
{
    class TypeOrderer
    {
        private class MultiArchHoister
        {
            private HashSet<string> seenMulti;
            private HashSet<string> addedMulti;
            private Dictionary<string, List<StructType<VarType>>> multiArchStructs;
            private Dictionary<string, List<FunctionType>> multiArchFunctions;
            private Dictionary<string, List<FunctionPointerType>> multiArchFunctionPointers;

            public MultiArchHoister(
                Dictionary<string, List<StructType<VarType>>> structs,
                Dictionary<string, List<FunctionType>> funcs,
                Dictionary<string, List<FunctionPointerType>> fnPointers
            )
            {
                seenMulti = new HashSet<string>(structs.Count);
                addedMulti = new HashSet<string>(structs.Count);
                multiArchStructs = structs;
                multiArchFunctions = funcs;
                multiArchFunctionPointers = fnPointers;
            }

            public bool OutputAtAll(string name)
            {
                return !seenMulti.Contains(name);
            }

            private List<T> GetMultiArchThing<T>(Dictionary<string, List<T>> dict, string name)
            {
                List<T> theList;
                dict.TryGetValue(name, out theList);
                return theList;
            }

            public List<StructType<VarType>> GetAlternaArchStruct(string name)
            {
                return GetMultiArchThing(multiArchStructs, name);
            }

            public List<FunctionType> GetAlternaArchFunction(string name)
            {
                return GetMultiArchThing(multiArchFunctions, name);
            }

            public List<FunctionPointerType> GetAlternaArchFunctionPointer(string name)
            {
                return GetMultiArchThing(multiArchFunctionPointers, name);
            }

            public void MarkAsOutput(string name)
            {
                seenMulti.Add(name);
            }

            public bool AddAtAll(string name)
            {
                return !addedMulti.Contains(name);
            }

            public void MarkAsAdded(string name)
            {
                addedMulti.Add(name);
            }
        }

        public class AddedObject
        {
            public object TheObject { get; init; }
            public RawTypeEntries.ObjectType ObjType { get; init; }
            public bool ForceForwardDeclares { get; private set; }

            public AddedObject(object thing, RawTypeEntries.ObjectType thingType)
            {
                TheObject = thing;
                ObjType = thingType;
                ForceForwardDeclares = false;
            }

            public void RequireForwardDeclares()
            {
                ForceForwardDeclares = true;
            }

        }
        private enum DelayType
        {
            Default,
            Concrete,
            Pointer
        }

        struct Dependency
        {
            public string Name { get; init; }
            public DelayType Kind { get; init; }

            public Dependency(string name, DelayType type)
            {
                Name = name;
                Kind = type;
            }
#if DEBUG
            public override string ToString()
            {
                return String.Format("{0} - {1}", Name, Kind.ToString());
            }
#endif
        }

        struct DependencyComparer : IEqualityComparer<Dependency>
        {
            public bool Equals(Dependency x, Dependency y)
            {
                return x.Name == y.Name;
            }

            public int GetHashCode(Dependency obj)
            {
                return obj.Name.GetHashCode();
            }
        }

        class DelayedObject
        {
            public string Name { get; init; }
            public HashSet<Dependency> Dependencies { get; init; }
            public AddedObject TheObject { get; init; }

            public DelayedObject(string name, HashSet<Dependency> deps, AddedObject thing)
            {
                Name = name;
                Dependencies = deps;
                TheObject = thing;
            }

            public void ResolveDependency(string name)
            {
                HashSet<Dependency> deps = Dependencies;
                Dependency newDep = new Dependency(name, DelayType.Concrete);
                deps.Remove(newDep);
                //int numDeps = deps.Count;
                //for(int i = 0; i < numDeps; ++i)
                //{
                //    if (deps[i].Name == name)
                //    {
                //        deps.RemoveAt(i);
                //        --i;
                //        --numDeps;
                //    }
                //}
            }

#if DEBUG
            public override string ToString()
            {
                return String.Format("{0} - {1} dependencies", Name, Dependencies.Count);
            }
#endif
        }

        private Dictionary<string, RawTypeEntries.ObjectType> lowerNamesToTypes;
        // typename -> List of objects that are waiting for it before they're fully resolved
        // dependee to dependent, ie the keys are the type names that the values are waiting for
        private Dictionary<string, List<DelayedObject>> delayedDependencies;
        // list of each item with its list of what it's waiting for
        // dependent to dependee
        private List<DelayedObject> dependents;
        private HashSet<string> namesAdded;
        private string currentNamespace;
        private List<AddedObject> inOrderList;
        private MultiArchHoister multiArchTypes;

        public TypeOrderer(string curNs, RawTypeEntries nsEntries)
        {
            dependents = new List<DelayedObject>(500);
            delayedDependencies = new Dictionary<string, List<DelayedObject>>(500);
            namesAdded = new HashSet<string>(500);
            currentNamespace = curNs;
            lowerNamesToTypes = nsEntries.NamesToTypeMapping;
            inOrderList = new List<AddedObject>(500);
            // there are structures like MEMORY_BASIC_INFORMATION and CONTEXT which are different per architecture
            // to make in-order compilation work great in this case, we need to hoist all of them when any of them
            // is added to the inorderlist
            multiArchTypes = new MultiArchHoister(
                nsEntries.GetMultiArchStructs(),
                nsEntries.GetMultiArchFns(),
                nsEntries.GetMultiArchFnPtrs()
            );
        }

        private bool SeenThisType(string name)
        {
            return namesAdded.Contains(name);
        }
        private SimpleTypeHandleInfo GetPotentialArrayType(SimpleTypeHandleInfo type)
        {
            ArrayTypeHandleInfo arrInf = type as ArrayTypeHandleInfo;
            return (arrInf != null) ? arrInf.DataType : type;
        }

        private bool DelayForType(SimpleTypeHandleInfo typeInf, ref Dependency delType, DelayType forceType = DelayType.Default)
        {
            PtrStripResult strippedTypeResult = PointerTypeHandleInfo.StripAllPointers(typeInf);
            SimpleTypeHandleInfo basePtrType = GetPotentialArrayType(strippedTypeResult.Stripped);
            HandleTypeHandleInfo hTypeInfo = basePtrType as HandleTypeHandleInfo;
            if ((hTypeInfo != null) && (hTypeInfo.IncludeFile == currentNamespace))
            {
                string name = hTypeInfo.ActualName;
                if (!SeenThisType(name))
                {
                    if (forceType == DelayType.Default)
                    {
                        int ptrLevels = strippedTypeResult.PtrLevels;
                        RawTypeEntries.ObjectType objType;
                        // don't count interface dependencies as concrete, since they're always pointers
                        if ((ptrLevels == 0) && lowerNamesToTypes.TryGetValue(name.ToLowerInvariant(), out objType))
                        {
                            if ((objType & RawTypeEntries.ObjectType.Interface) != 0)
                            {
                                ++ptrLevels;
                            }
                        }
                        forceType = ptrLevels > 0 ? DelayType.Pointer : DelayType.Concrete;
                    }
                    delType = new Dependency(
                        hTypeInfo.ActualName,
                        forceType
                    );
                    return true;
                }
            }
            return false;
        }

        private bool DelayForTypeList(List<SimpleTypeHandleInfo> typeList, ref HashSet<Dependency> delayedTypes)
        {
            foreach (SimpleTypeHandleInfo type in typeList)
            {
                Dependency delType = new Dependency();
                if (DelayForType(type, ref delType))
                {
                    if (delayedTypes == null) delayedTypes = new HashSet<Dependency>(new DependencyComparer());
                    Dependency orig;
                    // if this already exists, save the harder dependency
                    if(delayedTypes.TryGetValue(delType, out orig))
                    {
                        if((orig.Kind != DelayType.Concrete) && (delType.Kind == DelayType.Concrete))
                        {
                            delayedTypes.Remove(orig);
                        }
                    }
                    delayedTypes.Add(delType);
                }
            }
            return delayedTypes != null;
        }

        private void AddDelayDependency(string name, HashSet<Dependency> typenames, AddedObject toAdd)
        {
            DelayedObject delObj = new DelayedObject(name, typenames, toAdd);
            foreach (Dependency dependent in typenames)
            {
                List<DelayedObject> waitingObjects;
                if (!delayedDependencies.TryGetValue(dependent.Name, out waitingObjects))
                {
                    waitingObjects = new List<DelayedObject>();
                    delayedDependencies.Add(dependent.Name, waitingObjects);
                }
                waitingObjects.Add(delObj);
            }
            dependents.Add(delObj);
        }

        private void ResolveTypeDelayedDependents(string name, bool requireForwards)
        {
            List<DelayedObject> waitingObjects;
            if (delayedDependencies.TryGetValue(name, out waitingObjects))
            {
                delayedDependencies.Remove(name);
                int numWaiting = waitingObjects.Count;
                //Trace.WriteLine("Removing {0} from delayedDependencies with {1} waiting for it", name, numWaiting);
                for (int i = numWaiting - 1; i >= 0; --i)
                {
                    DelayedObject delObj = waitingObjects[i];
                    delObj.ResolveDependency(name);
                    if (delObj.Dependencies.Count == 0)
                    {
                        dependents.Remove(delObj);
                        //Trace.WriteLine("Removing {0} from dependents list and adding", delObj.Name);
                        if (requireForwards)
                        {
                            delObj.TheObject.RequireForwardDeclares();
                        }
                        // if we're fixing up the mutually dependent types at the end
                        // we could get here trying to add a type that's already added
                        // check for that
                        //RawTypeEntries.ObjectType obType;
                        //if (!(lowerNamesToTypes.TryGetValue(delObj.Name.ToLowerInvariant(), out obType) && ((obType & delObj.TheObject.ObjType) != 0)))
                        if(!SeenThisType(delObj.Name))
                        {
                            AddToInOrderList(delObj.TheObject, delObj.Name);
                        }
                    }
                }
            }
        }

        private void AddNewName(string name, RawTypeEntries.ObjectType objType, bool fullyResolvedType)
        {
            namesAdded.Add(name);
            if (fullyResolvedType)
            {
                ResolveTypeDelayedDependents(name, false);
            }
        }

        private bool DelayAfterRemovingSelf(HashSet<Dependency> delayTypes, string self)
        {
            int delTypeCount = delayTypes.Count;
            Dependency selfDep = new Dependency(self, DelayType.Concrete);
            if(delayTypes.Remove(selfDep))
            {
                --delTypeCount;
            }
            //for(int i = 0; i < delTypeCount; ++i)
            //{
            //    if(delayTypes[i].Name == self)
            //    {
            //        --delTypeCount;
            //        delayTypes.RemoveAt(i);
            //        break;
            //    }
            //}
            return delTypeCount > 0;
        }

        private bool AddMultiArchThingsToInOrderList<T>(List<T> thingsToAdd, RawTypeEntries.ObjectType thingType, string name)
        {
            bool haveAdded = false;
            if (thingsToAdd != null)
            {
                foreach (T altArchThing in thingsToAdd)
                {
                    inOrderList.Add(new AddedObject(altArchThing, thingType));
                }
                multiArchTypes.MarkAsOutput(name);
                haveAdded = true;
            }
            return haveAdded;
        }

        public void AddToInOrderList(AddedObject obj, string name)
        {
            if (!multiArchTypes.OutputAtAll(name)) return;
            bool haveOutput = false;
            // yes, this is where the type thing started getting out of hand
            switch(obj.ObjType)
            {
                case RawTypeEntries.ObjectType.Function: haveOutput = AddMultiArchThingsToInOrderList(multiArchTypes.GetAlternaArchFunction(name), RawTypeEntries.ObjectType.Function, name); break;
                case RawTypeEntries.ObjectType.Struct: haveOutput = AddMultiArchThingsToInOrderList(multiArchTypes.GetAlternaArchStruct(name), RawTypeEntries.ObjectType.Struct, name); break;
                case RawTypeEntries.ObjectType.FunctionPointer: haveOutput = AddMultiArchThingsToInOrderList(multiArchTypes.GetAlternaArchFunctionPointer(name), RawTypeEntries.ObjectType.FunctionPointer, name); break;
            }
            if (!haveOutput)
            {
                inOrderList.Add(obj);
            }
            AddNewName(name, obj.ObjType, true);
        }

        // we don't delay these as we add because after we've added all the types from this namespace,
        // we may have name fixes/changes that put types from other namespaces in here, and those probably depend
        // on other things in here (which wiil be why we put them here)
        public List<AddedObject> MakeOrderedList(RawTypeEntries typeEntries)
        {
            foreach (StructType<ConstantValue> enumVal in typeEntries.Enums)
            {
                AddToInOrderList(new AddedObject(enumVal, RawTypeEntries.ObjectType.Enum), enumVal.Name);
            }
            foreach (ConstantValue cons in typeEntries.Constants)
            {
                AddWithDepedencyCheck(cons);
            }
            foreach (FunctionPointerType fptr in typeEntries.FunctionPointers)
            {
                AddWithDepedencyCheck(fptr);
            }
            foreach (StructType<VarType> structType in typeEntries.Structs)
            {
                AddWithDepedencyCheck(structType);
            }
            foreach (FunctionType fnType in typeEntries.Functions)
            {
                AddWithDepedencyCheck(fnType);
            }
            foreach (StructType<FunctionType> iface in typeEntries.Interfaces)
            {
                AddWithDepedencyCheck(iface);
            }
            // after the above, all the concrete types are ordered before the things that use them.
            // Now whatever is left in the delay list must be circularly referential / dependent.
            // The next step is to sweep the types with dependencies and add those who are only 
            // pointer dependent on their dependencies
            // No types can be concrete dependent on each other, so this allows to break the cycles
            // with forward declarations in the output
            dependents.Sort((x, y) => { return y.Dependencies.Count.CompareTo(x.Dependencies.Count); });
            int count = dependents.Count;
            for(int i = count - 1; i >= 0; --i)
            {
                // AddToInOrderList will modify dependents so we can't just iterate over it
                // in a foreach
                DelayedObject delObj = dependents[i];
                int numDeps = delObj.Dependencies.Count;
                int ptrDeps = 0;
                foreach (Dependency dep in delObj.Dependencies)
                {
                    if (dep.Kind == DelayType.Pointer)
                    {
                        ++ptrDeps;
                    }
                }
                if (numDeps == ptrDeps)
                {
                    delObj.TheObject.RequireForwardDeclares();
                    dependents.RemoveAt(i);
                    --count;
                    //Trace.WriteLine("Removing {0} from dependents list", delObj.Name);
                    AddToInOrderList(delObj.TheObject, delObj.Name);
                    int newDepCount = dependents.Count;
                    if(count != newDepCount)
                    {
                        count = i = newDepCount;
                    }
                }
            }
            return inOrderList;
        }

        private void AddWithDepedencyCheck(ConstantValue con)
        {
            AddedObject addedCon = new AddedObject(con, RawTypeEntries.ObjectType.Constant);
            Dependency dep = new Dependency();
            if (DelayForType(con.varType.ParamType, ref dep))
            {
                HashSet<Dependency> depList = new HashSet<Dependency>(new DependencyComparer());
                depList.Add(dep);
                AddDelayDependency(con.varType.Name, depList, addedCon);
            }
            else
            {
                AddToInOrderList(addedCon, con.varType.Name);
            }
        }

        private SimpleTypeHandleInfo VarToParamType(VarType t)
        {
            return t.ParamType;
        }

        private void AddWithDepedencyCheck(FunctionType fun)
        {
            string functionName = fun.Name;
            if (!multiArchTypes.AddAtAll(functionName)) return;
            List<FunctionType> multiArchFns = multiArchTypes.GetAlternaArchFunction(functionName);
            HashSet<Dependency> delayedTypes = null;
            AddedObject funObj = new AddedObject(fun, RawTypeEntries.ObjectType.Function);
            List<SimpleTypeHandleInfo> argTypeList;
            if (multiArchFns != null)
            {
                argTypeList = new List<SimpleTypeHandleInfo>();
                foreach (FunctionType multiFn in multiArchFns)
                {
                    argTypeList.AddRange(fun.Arguments.ConvertAll(VarToParamType));
                    argTypeList.Add(fun.ReturnType.ParamType);
                }
            }
            else
            {
                argTypeList = fun.Arguments.ConvertAll(VarToParamType);
                argTypeList.Add(fun.ReturnType.ParamType);
            }
            
            DelayForTypeList(argTypeList, ref delayedTypes);
            if ((delayedTypes != null) && DelayAfterRemovingSelf(delayedTypes, functionName))
            {
                AddDelayDependency(functionName, delayedTypes, funObj);
                //AddNewName(functionName, RawTypeEntries.ObjectType.Function, false);
            }
            else
            {
                AddToInOrderList(funObj, functionName);
            }
            multiArchTypes.MarkAsAdded(functionName);
        }

        private void CollectInnerTypes(StructType<VarType> aStruct, List<SimpleTypeHandleInfo> typeList)
        {
            if (aStruct.NestedTypes != null)
            {
                foreach (StructType<VarType> inner in aStruct.NestedTypes)
                {
                    CollectInnerTypes(inner, typeList);
                }
            }
            typeList.AddRange(aStruct.Fields.ConvertAll(VarToParamType));
        }

        private void AddWithDepedencyCheck(StructType<VarType> theStruct)
        {
            string structName = theStruct.Name;
            if (!multiArchTypes.AddAtAll(structName)) return;
            List<StructType<VarType>> multiArchStructs = multiArchTypes.GetAlternaArchStruct(structName);
            AddedObject structObj = new AddedObject(theStruct, RawTypeEntries.ObjectType.Struct);
            List<SimpleTypeHandleInfo> argTypeList = new List<SimpleTypeHandleInfo>();
            if (multiArchStructs != null)
            {
                foreach(StructType<VarType> structVer in multiArchStructs)
                {
                    CollectInnerTypes(structVer, argTypeList);
                }
            }
            else
            {
                CollectInnerTypes(theStruct, argTypeList);
            }
            HashSet<Dependency> delayedTypes = null;            
            if (DelayForTypeList(argTypeList, ref delayedTypes) && DelayAfterRemovingSelf(delayedTypes, structName))
            {
                AddDelayDependency(structName, delayedTypes, structObj);
                //AddNewName(structName, RawTypeEntries.ObjectType.Struct, false);
            }
            else
            {
                AddToInOrderList(structObj, structName);
            }
            multiArchTypes.MarkAsAdded(structName);
        }

        private void AddWithDepedencyCheck(FunctionPointerType fptr)
        {
            string fptrName = fptr.Name;
            if (!multiArchTypes.AddAtAll(fptrName)) return;
            AddedObject fnPtrObj = new AddedObject(fptr, RawTypeEntries.ObjectType.FunctionPointer);
            List<FunctionPointerType> multiArchFnPtrs = multiArchTypes.GetAlternaArchFunctionPointer(fptrName);
            List<SimpleTypeHandleInfo> paramListTypes;
            if (multiArchFnPtrs != null)
            {
                paramListTypes = new List<SimpleTypeHandleInfo>();
                foreach (FunctionPointerType multiFnPtr in multiArchFnPtrs)
                {
                    paramListTypes.AddRange(multiFnPtr.Shape.Arguments.ConvertAll(VarToParamType));
                    paramListTypes.Add(multiFnPtr.Shape.ReturnType.ParamType);
                }
            }
            else
            {
                paramListTypes = fptr.Shape.Arguments.ConvertAll(VarToParamType);
                paramListTypes.Add(fptr.Shape.ReturnType.ParamType);
            }
            HashSet<Dependency> delayedTypes = null;            
            if (DelayForTypeList(paramListTypes, ref delayedTypes) && DelayAfterRemovingSelf(delayedTypes, fptrName))
            {
                AddDelayDependency(fptrName, delayedTypes, fnPtrObj);
                //AddNewName(fptrName, RawTypeEntries.ObjectType.Interface, false);
            }
            else
            {
                AddToInOrderList(fnPtrObj, fptrName);
            }
            multiArchTypes.MarkAsAdded(fptrName);
        }

        private void AddWithDepedencyCheck(StructType<FunctionType> iface)
        {
            AddedObject ifaceObj = new AddedObject(iface, RawTypeEntries.ObjectType.Interface);
            HashSet<Dependency> delayedTypes = null;
            Dependency typeInfo = new Dependency();
            string interfaceName = iface.Name;
            foreach (HandleTypeHandleInfo hBase in iface.Bases)
            {
                // interfaces are usually treated as pointers, but when deriving from them, they need to be concrete
                if (DelayForType(hBase, ref typeInfo, DelayType.Concrete))
                {
                    if (delayedTypes == null) delayedTypes = new HashSet<Dependency>(new DependencyComparer());
                    delayedTypes.Add(typeInfo);
                }
            }
            foreach (FunctionType fn in iface.Fields)
            {
                List<SimpleTypeHandleInfo> paramListTypes = fn.Arguments.ConvertAll(VarToParamType);
                paramListTypes.Add(fn.ReturnType.ParamType);
                DelayForTypeList(paramListTypes, ref delayedTypes);
            }
            if ((delayedTypes != null) && DelayAfterRemovingSelf(delayedTypes, interfaceName))
            {
                //AddNewName(interfaceName, RawTypeEntries.ObjectType.Interface, false);
                AddDelayDependency(interfaceName, delayedTypes, ifaceObj);
            }
            else
            {
                AddToInOrderList(ifaceObj, interfaceName);
            }
        }
    }
}
