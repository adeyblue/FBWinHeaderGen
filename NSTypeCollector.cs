using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;

namespace MetadataParser
{
    class NamespaceContent
    {
        public RawTypeEntries TypeEntries { get; init; }
        public string Name { get; init; }

        public NamespaceContent(string name)
        {
            TypeEntries = new RawTypeEntries();
            Name = name;
        }
    }

    struct TypeCollectionResults
    {
        public Dictionary<string, NamespaceContent> Contents { get; init; }
        public GlobalTypeRegistry TypeRegistry { get; init; }

        public TypeCollectionResults(Dictionary<string, NamespaceContent> nsBits, GlobalTypeRegistry registry)
        {
            Contents = nsBits;
            TypeRegistry = registry;
        }
    }

    class TypeCollector
    {
        private MetadataReader metaReader;
        private Dictionary<string, NamespaceDefinition> allNamespaces;
        private Dictionary<string, NamespaceDefinition> toProcess;
        private Dictionary<string, NamespaceDefinition> typedefsOnly;
        private HashSet<string> lookupTypedefNamespaces;
        private GlobalTypeRegistry globalTypes;
        private Dictionary<string, NamespaceContent> nsEntries;
        private CustomAttributeTypeProvider attrTypeProv;
        private SignatureTypeProvider typeProv;
        private NameChangeDB nameFixes;
        private const string GLOBALS_CLASS = "Apis";
        internal const string VARARG_PARAMETER = "__arglist";

        struct ObjectIdentity
        {
            public string ns;
            public string name;
            public CustomAttributeValues attrVals;

            public ObjectIdentity(string namesp, string objName, CustomAttributeValues attributeVals)
            {
                ns = namesp;
                name = objName;
                attrVals = attributeVals;
            }
        }

        public TypeCollector(
            MetadataReader mr, 
            Dictionary<string, NamespaceDefinition> nsList,
            Dictionary<string, NamespaceDefinition> selectedList,
            NameChangeDB nameFixDb
        )
        {
            metaReader = mr;
            allNamespaces = nsList;
            toProcess = selectedList ?? nsList;
            typedefsOnly = new Dictionary<string, NamespaceDefinition>();
            nsEntries = new Dictionary<string, NamespaceContent>(nsList.Count);
            lookupTypedefNamespaces = new HashSet<string>();
            globalTypes = new GlobalTypeRegistry();
            attrTypeProv = new CustomAttributeTypeProvider(nameFixDb);
            typeProv = new SignatureTypeProvider(attrTypeProv, lookupTypedefNamespaces, nameFixDb);
            nameFixes = nameFixDb;
        }

        public TypeCollectionResults DoWork()
        {
            bool onlyTypeDefs = false;
            HashSet<string> processed = new HashSet<string>();
            while (toProcess.Count > 0)
            {
                foreach (KeyValuePair<string, NamespaceDefinition> nsEntry in toProcess)
                {
                    string thisNS = nsEntry.Key;
                    if (!processed.Contains(thisNS))
                    {
                        if (!onlyTypeDefs)
                        {
                            Console.WriteLine("Processing {0}", thisNS);
                            ProcessNamespace(nsEntry.Value);
                        }
                        else
                        {
                            Console.WriteLine("Processing {0} for types", thisNS);
                            ProcessNamespaceTypedefs(nsEntry.Value);
                        }
                        processed.Add(thisNS);
                        foreach (string tdNS in lookupTypedefNamespaces)
                        {
                            if (!(processed.Contains(tdNS) || toProcess.ContainsKey(tdNS) || typedefsOnly.ContainsKey(tdNS)))
                            {
                                // there are WinRT types in the WinRT namespaces that aren't in this metadata 
                                // but live in other c# assemblies. We filter out those namespaces here
                                if (allNamespaces.ContainsKey(tdNS))
                                {
                                    typedefsOnly.Add(tdNS, allNamespaces[tdNS]);
                                }
                            }
                        }
                        lookupTypedefNamespaces.Clear();
                    }
                }
                toProcess = typedefsOnly;
                typedefsOnly = new Dictionary<string, NamespaceDefinition>();
                onlyTypeDefs = true;
            }
            return new TypeCollectionResults(nsEntries, globalTypes);
        }

        static internal string BuildFullNamespaceName(MetadataReader metaReader, NamespaceDefinition ndDef)
        {
            string curName = metaReader.GetString(ndDef.Name).ToLowerInvariant();
            return
                ndDef.Parent.IsNil ?
                curName :
                BuildFullNamespaceName(metaReader, metaReader.GetNamespaceDefinition(ndDef.Parent)) + "." + curName
                ;
        }

        private CustomAttributeValues ParseAttributes(MetadataReader metaReader, CustomAttributeTypeProvider attrTypeProv, CustomAttributeHandleCollection attributes)
        {
            return CustomAttributeParser.Parser.ParseAttributes(metaReader, attrTypeProv, attributes);
        }

        [Conditional("DEBUG")]
        private void DebugPrintCustomAttribute(string name, CustomAttributeValues attrs, RawTypeEntries.ObjectType type)
        {
            // this is just a hook that's c alled for every type found, I've obviously used it to dump various
            // attribute related data
            //
            //if ((attrs != null) && (attrs.supportedArch != 0) && (attrs.supportedArch != SupportedArchitecture.All))
            //{
            //    Trace.WriteLine("{0} {1} has arch value of {2}", type, name, attrs.supportedArch);
            //}
            //
            //if ((attrs != null) && (!String.IsNullOrEmpty(attrs.constantValue)))
            //{
            //    Trace.WriteLine("{0} {1} has a constant value of {2}", type, name, attrs.constantValue);
            //}
        }

        private DllImportAttribute MethodImportToDllImport(MethodImport import)
        {
            string importName = metaReader.GetString(import.Name);
            ModuleReference exporter = metaReader.GetModuleReference(import.Module);
            string exporterName = metaReader.GetString(exporter.Name);
            DllImportAttribute dllImport = new DllImportAttribute(exporterName);
            dllImport.EntryPoint = importName;
            MethodImportAttributes funcAttributes = import.Attributes;
            if((funcAttributes & MethodImportAttributes.SetLastError) != 0)
            {
                dllImport.SetLastError = true;
            }
            if((funcAttributes & MethodImportAttributes.ExactSpelling) != 0)
            {
                dllImport.ExactSpelling = true;
            }
            Debug.Assert(((funcAttributes & MethodImportAttributes.CharSetAuto) != MethodImportAttributes.CharSetAuto), "DllImport has both charsets, somebody do something");
            if ((funcAttributes & MethodImportAttributes.CharSetUnicode) != 0)
            {
                dllImport.CharSet = CharSet.Unicode;
            }
            if ((funcAttributes & MethodImportAttributes.CharSetAnsi) != 0)
            {
                dllImport.CharSet = CharSet.Ansi;
            }
            MethodImportAttributes allCallingConv = MethodImportAttributes.CallingConventionCDecl | MethodImportAttributes.CallingConventionWinApi | MethodImportAttributes.CallingConventionThisCall;
            Debug.Assert(System.Numerics.BitOperations.PopCount((uint)(funcAttributes & allCallingConv)) <= 1, "DllImport has multiple calling conventions, who knows what to do");
            if ((funcAttributes & MethodImportAttributes.CallingConventionCDecl) != 0)
            {
                dllImport.CallingConvention = CallingConvention.Cdecl;
            }
            if ((funcAttributes & MethodImportAttributes.CallingConventionWinApi) != 0)
            {
                dllImport.CallingConvention = CallingConvention.Winapi;
            }
            if ((funcAttributes & MethodImportAttributes.CallingConventionThisCall) != 0)
            {
                dllImport.CallingConvention = CallingConvention.ThisCall;
            }
            return dllImport;
        }

        private ConstantValue ProcessConstantField(FieldDefinitionHandle hField)
        {
            FieldDefinition field = metaReader.GetFieldDefinition(hField);
            string fName = metaReader.GetString(field.Name);
            CustomAttributeHandleCollection fieldAttrs = field.GetCustomAttributes();
            CustomAttributeValues attrVals = ParseAttributes(metaReader, attrTypeProv, fieldAttrs);
            DebugPrintCustomAttribute(fName, attrVals, RawTypeEntries.ObjectType.Constant);
            // this is system.string for strings, use attrVals.ansiApi/attrVals.unicodeApi
            // to determine if it is a unicode or ansi string to output.
            // also const can be specified either as a type attribute of the field, or in attrvals.const
            //bool isConst = (((field.Attributes & FieldAttributes.Literal) != 0) || (attrVals != null && attrVals.isConst));
            SimpleTypeHandleInfo thi = field.DecodeSignature(typeProv, null);
            ConstantHandle hConstant = field.GetDefaultValue();
            VarType foundType = new VarType(fName, attrVals, thi, field.Attributes);
            ConstantValue nsConstant;
            if (!hConstant.IsNil)
            {
                Constant val = metaReader.GetConstant(hConstant);
                ConstantTypeCode code = val.TypeCode;
                BlobReader br = metaReader.GetBlobReader(val.Value);
                nsConstant = new ConstantValue(foundType, new VarValue(code, br));
            }
            else
            {
                // this is probably a guid which doesn't have an initialiser
                nsConstant = new ConstantValue(foundType, null);
            }
            return nsConstant;
        }

        private void ProcessEnum(string nsName, TypeDefinition enumDef, CustomAttributeValues attrVals)
        {
            string name = metaReader.GetString(enumDef.Name);
            DebugPrintCustomAttribute(name, attrVals, RawTypeEntries.ObjectType.Enum);
            FieldDefinitionHandleCollection members = enumDef.GetFields();
            StructType<ConstantValue> thisEnum = new StructType<ConstantValue>(name, attrVals, enumDef.Attributes, null, null, null);
            foreach (FieldDefinitionHandle hField in members)
            {
                ConstantValue val = ProcessConstantField(hField);
                // screen out any backing/enum type fields
                if (val.varType.Name != "value__")
                {
                    ObjectIdentity conLoc = new ObjectIdentity(nsName, name + "." + val.varType.Name, val.varType.CustomAttributes);
                    if (FixupName(ref conLoc))
                    {
                        val.Rename(conLoc.name);
                    }
                    thisEnum.AddMember(val);
                    globalTypes.Add(nsName, val.varType.Name, val);
                }
            }
            ObjectIdentity enumLoc = new ObjectIdentity(nsName, name, thisEnum.AttributeValues);
            if (FixupName(ref enumLoc))
            {
                name = enumLoc.name;
                nsName = enumLoc.ns;
            }
            NamespaceContent nsContent = GetNSContent(nsName);
            nsContent.TypeEntries.Add(thisEnum);
            globalTypes.Add(nsName, name, thisEnum);
        }

        private StructType<VarType> ProcessDataStruct(string ns, string parent, TypeDefinition structDef, CustomAttributeValues structAttrVals, List<StructType<VarType>> innerTypes)
        {
            string name = metaReader.GetString(structDef.Name);
            TypeLayout layout = structDef.GetLayout();
            FieldDefinitionHandleCollection members = structDef.GetFields();
            StructType<VarType> dataStruct = new StructType<VarType>(name, structAttrVals, structDef.Attributes, null, innerTypes, layout);
            foreach (FieldDefinitionHandle hField in members)
            {
                FieldDefinition fDef = metaReader.GetFieldDefinition(hField);
                SimpleTypeHandleInfo fieldType = fDef.DecodeSignature(typeProv, null);
                CustomAttributeValues fieldAttrVals = CustomAttributeParser.Parser.ParseAttributes(metaReader, attrTypeProv, fDef.GetCustomAttributes());
                string fieldName = metaReader.GetString(fDef.Name);
                ObjectIdentity fieldLoc = new ObjectIdentity(ns, parent + name + "." + fieldName, fieldAttrVals);
                if(FixupName(ref fieldLoc))
                {
                    fieldName = fieldLoc.name;
                }
                dataStruct.AddMember(new VarType(fieldName, fieldAttrVals, fieldType, fDef.Attributes), fDef.GetOffset());
            }
            return dataStruct;
        }

        private FunctionType ProcessFunction(string ns, string parent, MethodDefinitionHandle hMeth)
        {
            MethodDefinition methDef = metaReader.GetMethodDefinition(hMeth);
            MethodSignature<SimpleTypeHandleInfo> prototype = methDef.DecodeSignature(typeProv, null);
            string name = metaReader.GetString(methDef.Name);
            CustomAttributeHandleCollection funcAttrs = methDef.GetCustomAttributes();
            CustomAttributeValues attrVals = ParseAttributes(metaReader, attrTypeProv, funcAttrs);
            MethodImport methImport = methDef.GetImport();
            if (!methImport.Module.IsNil)
            {
                if (attrVals == null)
                {
                    attrVals = new CustomAttributeValues();
                }
                attrVals.dllImport = MethodImportToDllImport(methImport);
            }
            int numParams = prototype.ParameterTypes.Length;
            ParameterHandleCollection hParams = methDef.GetParameters();
            ParameterHandleCollection.Enumerator paramEnum = hParams.GetEnumerator();
            paramEnum.MoveNext();
            FunctionArgType returnType;
            if (hParams.Count > numParams)
            {
                // the first entry in hParams is for the return value
                returnType = ReadFunctionArg(ns, String.Empty, paramEnum.Current, prototype.ReturnType, attrTypeProv);
                paramEnum.MoveNext();
            }
            else
            {
                returnType = new FunctionArgType(String.Empty, null, prototype.ReturnType, ParameterAttributes.None);
            }
            FunctionType funType = new FunctionType(name, numParams, returnType, attrVals, methDef.Attributes);
            for (int i = 0; i < numParams; ++i, paramEnum.MoveNext())
            {
                funType.AddArgument(ReadFunctionArg(ns, parent + name + ".", paramEnum.Current, prototype.ParameterTypes[i], attrTypeProv));
            }
            if(prototype.Header.CallingConvention == SignatureCallingConvention.VarArgs)
            {
                funType.AddArgument(
                    new FunctionArgType(
                        VARARG_PARAMETER, 
                        null, 
                        SignatureTypeProvider.CreateNameOnlyTypeInfo("windows.win32.foundation", "VarArgs"),
                        ParameterAttributes.In
                    )
                );
            }
            return funType;
        }

        private HandleTypeHandleInfo GetBaseType(EntityHandle hEntity)
        {
            HandleTypeHandleInfo hTypeInfo;
            switch (hEntity.Kind)
            {
                case HandleKind.TypeReference:
                {
                    hTypeInfo = new HandleTypeHandleInfo(metaReader, (TypeReferenceHandle)hEntity, 0, attrTypeProv, nameFixes);
                }
                break;
                case HandleKind.TypeDefinition:
                {
                    hTypeInfo = new HandleTypeHandleInfo(metaReader, (TypeDefinitionHandle)hEntity, 0, attrTypeProv, nameFixes);
                }
                break;
                case HandleKind.TypeSpecification:
                {
                    TypeSpecification ts = metaReader.GetTypeSpecification((TypeSpecificationHandle)hEntity);
                    hTypeInfo = (HandleTypeHandleInfo)ts.DecodeSignature(typeProv, null);
                }
                break;
                default:
                {
                    // the intellisense for BaseType says it can only be the three types above
                    throw new NotImplementedException("Unknown base type handle type, this shouldn't happen");
                }
            }
            return hTypeInfo;
        }

        private StructType<FunctionType> ProcessInterface(string ns, TypeDefinition interDef, CustomAttributeValues structAttrVals)
        {
            string name = metaReader.GetString(interDef.Name);
            MethodDefinitionHandleCollection members = interDef.GetMethods();
            InterfaceImplementationHandleCollection hInterfaces = interDef.GetInterfaceImplementations();
            List<HandleTypeHandleInfo> baseInterfaces = new List<HandleTypeHandleInfo>(hInterfaces.Count);
            foreach (InterfaceImplementationHandle hInterface in hInterfaces)
            {
                InterfaceImplementation baseInterface = metaReader.GetInterfaceImplementation(hInterface);
                Debug.Assert(baseInterface.GetCustomAttributes().Count == 0);
                HandleTypeHandleInfo interType = GetBaseType(baseInterface.Interface);
                baseInterfaces.Add(interType);                
            }
            StructType<FunctionType> interfaceStruct = new StructType<FunctionType>(name, structAttrVals, interDef.Attributes, baseInterfaces, null, interDef.GetLayout());
            string parentName = name + ".";
            foreach (MethodDefinitionHandle hMeth in members)
            {
                FunctionType ifaceMember = ProcessFunction(ns, parentName, hMeth);
                ObjectIdentity loc = new ObjectIdentity(ns, parentName + ifaceMember.Name, ifaceMember.AttributeValues);
                if(FixupName(ref loc))
                {
                    ifaceMember.Rename(loc.name);
                }
                interfaceStruct.AddMember(ifaceMember);
            }
            return interfaceStruct;
        }

        private void ProcessFunctionPointer(string ns, TypeDefinition fnPtrDef, CustomAttributeValues fnPtrAttrVals)
        {
            foreach (MethodDefinitionHandle hMeth in fnPtrDef.GetMethods())
            {
                MethodDefinition methDef = metaReader.GetMethodDefinition(hMeth);
                if (metaReader.StringComparer.Equals(methDef.Name, "Invoke"))
                {
                    string fnPtrName = metaReader.GetString(fnPtrDef.Name);
                    FunctionType funType = ProcessFunction(ns, fnPtrName + ".", hMeth);
                    FunctionPointerType fpt = new FunctionPointerType(fnPtrName, funType, fnPtrAttrVals);
                    ObjectIdentity ptrLoc = new ObjectIdentity(ns, fnPtrName, fnPtrAttrVals);
                    if (FixupName(ref ptrLoc))
                    {
                        fpt.Rename(ptrLoc.name);
                    }
                    DebugPrintCustomAttribute(fnPtrName, fnPtrAttrVals, RawTypeEntries.ObjectType.FunctionPointer);
                    NamespaceContent nsContent = GetNSContent(ptrLoc.ns);
                    nsContent.TypeEntries.Add(fpt);
                    globalTypes.Add(ptrLoc.ns, ptrLoc.name, fpt);
                    break;
                }
            }
        }

        private void ProcessType(string ns, string parent, TypeDefinition typeDef, List<StructType<VarType>> innerTypeHolder)
        {
            string curTypeName = metaReader.GetString(typeDef.Name);
            ImmutableArray<TypeDefinitionHandle> hTypes = typeDef.GetNestedTypes();
            List<StructType<VarType>> innerTypes = null;
            foreach(TypeDefinitionHandle tdh in hTypes)
            {
                TypeDefinition innerType = metaReader.GetTypeDefinition(tdh);
                if(innerTypes == null)
                {
                    innerTypes = new List<StructType<VarType>>();
                }
                ProcessType(ns, parent + curTypeName + ".", innerType, innerTypes);
            }
            CustomAttributeHandleCollection funcAttrs = typeDef.GetCustomAttributes();
            CustomAttributeValues attrVals = ParseAttributes(metaReader, attrTypeProv, funcAttrs);

            if ((typeDef.Attributes & TypeAttributes.ClassSemanticsMask) == TypeAttributes.Interface)
            {
                StructType<FunctionType> iface = ProcessInterface(ns, typeDef, attrVals);
                ObjectIdentity ifLoc = new ObjectIdentity(ns, parent + iface.Name, attrVals);
                if(FixupName(ref ifLoc))
                {
                    iface.Rename(ifLoc.name);
                }
                DebugPrintCustomAttribute(ifLoc.name, attrVals, RawTypeEntries.ObjectType.Interface);
                NamespaceContent nsEntries = GetNSContent(ifLoc.ns);
                nsEntries.TypeEntries.Add(iface);
                globalTypes.Add(ifLoc.ns, ifLoc.name, iface);
            }
            else
            {
                EntityHandle baseTypeHandle = typeDef.BaseType;
                if (!baseTypeHandle.IsNil)
                {
                    HandleTypeHandleInfo baseType = GetBaseType(baseTypeHandle);
                    // seems like there should be a better way to figure this out
                    if (baseType.ActualName == "system.Enum")
                    {
                        ProcessEnum(ns, typeDef, attrVals);
                    }
                    else if (baseType.ActualName == "system.ValueType")
                    {
                        StructType<VarType> dataStruct = ProcessDataStruct(ns, parent, typeDef, attrVals, innerTypes);
                        if (innerTypeHolder != null)
                        {
                            innerTypeHolder.Add(dataStruct);
                        }
                        else
                        {
                            ObjectIdentity strLoc = new ObjectIdentity(ns, dataStruct.Name, attrVals);
                            if (FixupName(ref strLoc))
                            {
                                dataStruct.Rename(strLoc.name);
                            }
                            DebugPrintCustomAttribute(strLoc.name, attrVals, RawTypeEntries.ObjectType.Struct);
                            NamespaceContent nsEntries = GetNSContent(strLoc.ns);
                            nsEntries.TypeEntries.Add(dataStruct);
                            if ((attrVals != null) && attrVals.nativeTypedef)
                            {
                                globalTypes.Add(strLoc.ns, strLoc.name, dataStruct.Fields[0].ParamType);
                            }
                            else
                            {
                                globalTypes.Add(strLoc.ns, strLoc.name, dataStruct);
                            }
                        }
                    }
                    else if(baseType.ActualName == "system.MulticastDelegate")
                    {
                        ProcessFunctionPointer(ns, typeDef, attrVals);
                    }
                    else
                    {
                        Debug.Assert(false, "Unknown base type for type!");
                    }
                }
            }
        }

        private FunctionArgType ReadFunctionArg(string ns, string parent, ParameterHandle hParam, SimpleTypeHandleInfo typeInfo, CustomAttributeTypeProvider attrTypeProv)
        {
            Parameter par = metaReader.GetParameter(hParam);
            string parName = metaReader.GetString(par.Name);
            CustomAttributeHandleCollection paramCustomAttrColl = par.GetCustomAttributes();
            CustomAttributeValues paramCustomAttrs = CustomAttributeParser.Parser.ParseAttributes(metaReader, attrTypeProv, paramCustomAttrColl);
            ObjectIdentity loc = new ObjectIdentity(ns, parent + parName, paramCustomAttrs);
            if(FixupName(ref loc))
            {
                parName = loc.name;
            }
            ParameterAttributes paramAttrs = par.Attributes;
            ConstantHandle hConst = par.GetDefaultValue();
            Debug.Assert(hConst.IsNil, "Not expecting function argument to have a constant initialiser!");
            return new FunctionArgType(parName, paramCustomAttrs, typeInfo, paramAttrs);
        }

        private void ProcessFunctions(string ns, TypeDefinition typeDef)
        {
            MethodDefinitionHandleCollection methDefs = typeDef.GetMethods();
            foreach (MethodDefinitionHandle hMeth in methDefs)
            {
                FunctionType funType = ProcessFunction(ns, String.Empty, hMeth);
                ObjectIdentity fnLoc = new ObjectIdentity(ns, funType.Name, funType.AttributeValues);
                if(FixupName(ref fnLoc))
                {
                    funType.Rename(fnLoc.name);
                }
                DebugPrintCustomAttribute(fnLoc.name, funType.AttributeValues, RawTypeEntries.ObjectType.Function);
                NamespaceContent nsEntries = GetNSContent(fnLoc.ns);
                nsEntries.TypeEntries.Add(funType);
                globalTypes.Add(fnLoc.ns, fnLoc.name, funType);
            }
        }

        private void ProcessGlobals(string ns, TypeDefinition typeDef)
        {
            // these are defines, constants and free/top-level functions
            // Not here are types like structures or interfaces
            FieldDefinitionHandleCollection fields = typeDef.GetFields();
            foreach (FieldDefinitionHandle hField in fields)
            {
                ConstantValue val = ProcessConstantField(hField);
                ObjectIdentity conLoc = new ObjectIdentity(ns, val.varType.Name, val.varType.CustomAttributes);
                if(FixupName(ref conLoc))
                {
                    val.Rename(conLoc.name);
                }
                NamespaceContent nsEntries = GetNSContent(conLoc.ns);
                RawTypeEntries types = nsEntries.TypeEntries;
                types.Add(val);
                globalTypes.Add(conLoc.ns, conLoc.name, val);
            }
            ProcessFunctions(ns, typeDef);
        }

        private bool FixupName(ref ObjectIdentity origLoc)
        {
            NameChangeDB.Change? nameChange = nameFixes.FindNewAddress(origLoc.ns, origLoc.name);
            if(nameChange.HasValue)
            {
                origLoc.ns = nameChange.Value.Namespace;
                origLoc.name = nameChange.Value.Name;
                if(origLoc.attrVals != null)
                {
                    nameChange.Value.ApplyChanges(origLoc.attrVals);
                }
            }
            return nameChange.HasValue;
        }

        private NamespaceContent GetNSContent(string ns)
        {
            NamespaceContent nsContent;
            if (!nsEntries.TryGetValue(ns, out nsContent))
            {
                nsContent = new NamespaceContent(ns);
                nsEntries.Add(ns, nsContent);
            }
            return nsContent;
        }

        private void AddGuidToFoundation(string nsName, NamespaceContent entries)
        {
            if(nsName == "windows.win32.foundation")
            {
                StructType<VarType> guidStruct = new StructType<VarType>("GUID", null, TypeAttributes.Public, null, null, new TypeLayout(0, 0));
                VarType firstLong = new VarType("a", null, new PrimitiveTypeHandleInfo(PrimitiveTypeCode.UInt32), FieldAttributes.Public);
                guidStruct.AddMember(firstLong);
                VarType firstShort = new VarType("b", null, new PrimitiveTypeHandleInfo(PrimitiveTypeCode.UInt16), FieldAttributes.Public);
                guidStruct.AddMember(firstShort);
                VarType secondShort = new VarType("c", null, new PrimitiveTypeHandleInfo(PrimitiveTypeCode.UInt16), FieldAttributes.Public);
                guidStruct.AddMember(secondShort);
                PrimitiveTypeHandleInfo ubyte = new PrimitiveTypeHandleInfo(PrimitiveTypeCode.Byte);
                ArrayShape arrayElems = new ArrayShape(1, ImmutableArray.Create<int>(8), ImmutableArray.Create<int>(0));
                VarType byteArray = new VarType("d", null, new ArrayTypeHandleInfo(ubyte, arrayElems), FieldAttributes.Public);
                guidStruct.AddMember(byteArray);
                entries.TypeEntries.Add(guidStruct);
                globalTypes.Add(nsName, guidStruct.Name, guidStruct);
                // iid and clsid typedefs
                CustomAttributeValues typedefVals = new CustomAttributeValues();
                typedefVals.nativeTypedef = true;
                StructType<VarType> clsidTypedef = new StructType<VarType>("CLSID", typedefVals, TypeAttributes.Public, null, null, new TypeLayout(0, 0));
                SimpleTypeHandleInfo guidRef = SignatureTypeProvider.CreateNameOnlyTypeInfo(nsName, "GUID");
                clsidTypedef.AddMember(new VarType("GUID", null, guidRef, FieldAttributes.Public));
                entries.TypeEntries.Add(clsidTypedef);
                globalTypes.Add(nsName, clsidTypedef.Name, clsidTypedef);
                StructType<VarType> iidTypedef = new StructType<VarType>("IID", typedefVals, TypeAttributes.Public, null, null, new TypeLayout(0, 0));
                iidTypedef.AddMember(new VarType("GUID", null, guidRef, FieldAttributes.Public));
                entries.TypeEntries.Add(iidTypedef);
                globalTypes.Add(nsName, iidTypedef.Name, iidTypedef);
            }
        }

        private void ProcessNamespace(NamespaceDefinition nsDef)
        {
            string nsName = BuildFullNamespaceName(metaReader, nsDef);
            Trace.WriteLine(String.Format("Processing namespace {0}", nsName));
            NamespaceContent entries = new NamespaceContent(nsName);
            nsEntries.Add(nsName, entries);
            foreach (TypeDefinitionHandle typedef in nsDef.TypeDefinitions)
            {
                TypeDefinition td = metaReader.GetTypeDefinition(typedef);
                if (metaReader.StringComparer.Equals(td.Name, GLOBALS_CLASS))
                {
                    ProcessGlobals(nsName, td);
                }
                else
                {
                    ProcessType(nsName, String.Empty, td, null);
                }
            }
            AddGuidToFoundation(nsName, entries);
            lookupTypedefNamespaces.Remove(nsName);
        }

        private void ProcessNamespaceTypedefs(NamespaceDefinition nsDef)
        {
            string nsName = BuildFullNamespaceName(metaReader, nsDef);
            Trace.WriteLine(String.Format("Processing namespace {0} for typedefs", nsName));
            NamespaceContent entries = new NamespaceContent(nsName);
            Dictionary<string, NamespaceContent> tempEntries = new Dictionary<string, NamespaceContent>();
            Dictionary<string, NamespaceContent> origEntries = nsEntries;
            nsEntries = tempEntries;
            // this namespace may have renames in the fixes file that cause it to already to be here
            nsEntries.Add(nsName, entries);
            foreach (TypeDefinitionHandle typedef in nsDef.TypeDefinitions)
            {
                TypeDefinition td = metaReader.GetTypeDefinition(typedef);
                if (!metaReader.StringComparer.Equals(td.Name, GLOBALS_CLASS))
                {
                    ProcessType(nsName, String.Empty, td, null);
                }
            }
            AddGuidToFoundation(nsName, entries);
            nsEntries = origEntries;
        }
    }
}
