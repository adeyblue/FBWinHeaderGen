﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace MetadataParser
{
    // This entire class is very FB specific
    // This is the main place you want to be changing things for different languages
    // Search the code base for FB SPECIFIC as there are unavoidably a few others scattered around
    //
    // to do
    class OutputCreator
    {
        private TypeCollectionResults collectionResults;
        private HashSet<string> fbKeywords;

        public OutputCreator(TypeCollectionResults typeCollection)
        {
            fbKeywords = GetFBKeywords();
            collectionResults = typeCollection;
        }

        private HashSet<string> GetFBKeywords()
        {
            // fb keywords or built-ins that might be function parameter names or typedef names
            // which would cause compilation to fail if they appear in those places
            string[] keywords =
            {
                "len", "data", "true", "false", "object", "extends", "type", "union", "error", "err",
                "output", "dim", "redim", "as", "read", "declare", "name", "next", "step", "enum",
                "class", "public", "protected", "private", "do", "loop", "property", "end", "select", "new", 
                "delete", "string", "mod", "this", "import", "beep", "rgb", "continue", "out", "in", "call",
                "lock", "unlock", "export", "namespace", "base", "restore", "local", "is"
            };
            return new HashSet<string>(keywords);
        }

        public void WriteNamespaceHeaders(App.OptionSet options, Dictionary<string, Dictionary<string, string>> injections)
        {
            string baseDir = options.outputDirectory;
            Directory.CreateDirectory(baseDir);
            if (options.generateGuidFiles)
            {
                string guidDir = Path.Combine(baseDir, FileOutput.GUID_DIR);
                Directory.CreateDirectory(guidDir);
                string pkeyDir = Path.Combine(baseDir, FileOutput.PKEY_DIR);
                Directory.CreateDirectory(pkeyDir);
            }
            Dictionary<string, string> nullInjections = new Dictionary<string, string>();
            foreach(NamespaceContent ns in collectionResults.Contents.Values)
            {
                RawTypeEntries nsTypes = ns.TypeEntries;
                TypeOrderer orderer = new TypeOrderer(ns.Name, nsTypes);
                List<TypeOrderer.AddedObject> inOrderList = orderer.MakeOrderedList(nsTypes);
                // if we've moved all things from this namespace elsewhere
                // there might not be anything in this namespace any more
                if(inOrderList.Count == 0)
                {
                    Console.WriteLine("After name fixes, namespace '{0}' is empty. No file generated", ns.Name);
                    continue;
                }
                Dictionary<string, string> nsInjections;
                if(!injections.TryGetValue(ns.Name, out nsInjections))
                {
                    nsInjections = nullInjections;
                }
                Console.WriteLine("Outputting {0}", ns.Name);
                using (FileOutput file = new FileOutput(collectionResults.TypeRegistry, ns, fbKeywords, nsInjections, options))
                {
                    Trace.WriteLine("Outputting {0}", ns.Name);
                    foreach(TypeOrderer.AddedObject obj in inOrderList)
                    {
                        file.Output(obj);
                    }
                }
            }
        }
    }

    class ForwardDeclarationManager
    {
        public struct ForwardedType
        {
            public SimpleTypeHandleInfo BaseType { get; init; }

            public bool IsHandle { get; init; }

            public ForwardedType(SimpleTypeHandleInfo baseT, bool isAHandle)
            {
                BaseType = baseT;
                IsHandle = isAHandle;
            }

            public void Format(StringBuilder output, SimpleTypeHandleInfo originalType)
            {
                if (IsHandle)
                {
                    output.AppendFormat("Type {0} As {1}{2}", originalType, BaseType, Environment.NewLine);
                }
                else
                {
                    output.AppendFormat("Type {0} As {1}{2}", BaseType, originalType, Environment.NewLine);
                }
            }
        }

        public struct ForwardDeclaration
        {
            // this is the equivalent of the input, so it contains the same number of ptr levels etc
            public SimpleTypeHandleInfo Equivalent { get; init; }
            // the base type without any ptr levels
            public ForwardedType BaseType { get; init; }

            public ForwardDeclaration(SimpleTypeHandleInfo equiv, SimpleTypeHandleInfo baseT, bool isHandleType)
            {
                Equivalent = equiv;
                BaseType = new ForwardedType(baseT, isHandleType);
            }
        }
        private Dictionary<string, ForwardedType> forwardDeclaredTypes;

        public ForwardDeclarationManager()
        {
            forwardDeclaredTypes = new Dictionary<string, ForwardedType>();
        }

        public ForwardedType? Lookup(SimpleTypeHandleInfo type)
        {
            Debug.Assert(PointerTypeHandleInfo.StripAllPointers(type).Stripped == type, "Type passed should be the pointer stripped type");
            ForwardedType forwardedType;
            return forwardDeclaredTypes.TryGetValue(type.ToString(), out forwardedType) ? forwardedType : null;
        }

        public void ConcreteDeclaration(SimpleTypeHandleInfo typeToConcrete)
        {
            string typeName = typeToConcrete.ToString();
            ForwardedType concreted = new ForwardedType(typeToConcrete, false);
            if (!forwardDeclaredTypes.TryAdd(typeName, concreted))
            {
                forwardDeclaredTypes[typeName] = concreted;
            }
        }

        public void Add(SimpleTypeHandleInfo realType, ForwardedType forwardedName)
        {
            forwardDeclaredTypes.Add(realType.ToString(), forwardedName);
        }

        public void FormatAndAdd(StringBuilder output, SimpleTypeHandleInfo origType, ForwardedType forwarded)
        {
            Debug.Assert(PointerTypeHandleInfo.StripAllPointers(origType).Stripped == origType, "Type passed should be the stripped pointer type");
            if (Lookup(origType) == null)
            {
                forwarded.Format(output, origType);
                Add(origType, forwarded);
            }
        }

        public ForwardDeclaration CreateDeclForHandle(SimpleTypeHandleInfo handleType, SimpleTypeHandleInfo baseType)
        {
            return new ForwardDeclaration(handleType, baseType, true);
        }

        public ForwardDeclaration CreateDecl(SimpleTypeHandleInfo equivalent, SimpleTypeHandleInfo baseType)
        {
            return new ForwardDeclaration(equivalent, baseType, false);
        }
    }

    class FileOutput : IDisposable
    {
        class OutputStreams
        {
            public StringBuilder Main { get; init; }
            public StringBuilder AnsiDefs { get; init; }
            public StringBuilder UnicodeDefs { get; init; }
            public StringBuilder RaiiWrappers { get; init; }
            public StringBuilder Overloads { get; init; }

            public OutputStreams(int bufSize)
            {
                Main = new StringBuilder(bufSize);
                Overloads = new StringBuilder(bufSize);
                AnsiDefs = new StringBuilder(bufSize / 8);
                UnicodeDefs = new StringBuilder(bufSize / 8);
                RaiiWrappers = new StringBuilder(bufSize / 16);
            }

            public void Reset(bool includeMain)
            {
                AnsiDefs.Length = UnicodeDefs.Length = RaiiWrappers.Length = 0;
                if (includeMain)
                {
                    Main.Length = 0;
                }
            }

            public bool HasAnyContent()
            {
                return (Main.Length + AnsiDefs.Length + UnicodeDefs.Length + RaiiWrappers.Length) > 0;
            }
        }

        private OutputStreams outputStreams;
        private HashSet<string> headers;
        private HashSet<string> fbKeywords;
        private ForwardDeclarationManager forwardDecls;
        private GlobalTypeRegistry typeRegistry;
        private Dictionary<string, string> injections;
        private App.OptionSet options;
        private NamespaceContent ns;
        private string guidDir;
        private string propkeyDir;
        private HashSet<string> raiiWrappersCreated;
        private bool ForceForwardDecls { get; set; }

        private readonly static CustomAttributeValues dummyAttrVals = new CustomAttributeValues();
        private readonly static string nl = Environment.NewLine;
        const string INDENT = "    ";
        const string AUTOFREE_WRAP_SUFFIX = "Wrap";
        const string FB_KEYWORD_PREFIX = "__";
        internal const string GUID_DIR = "GuidFiles";
        internal const string PKEY_DIR = "PropKeyFiles";
        const string FORWARD_DECLARE_SUFFIX = "_fwd_";
        const string HANDLE_TYPE_SUFFIX = "__";
        const string x64Guard = "defined(__FB_64BIT__)";
        const string x86Guard = "not defined(__FB_64BIT__)";
        const string HEADER_INSERT_FORMAT = "End Extern{0}#include once \"{1}.bi\"{0}Extern \"Windows\"{0}";
        private readonly int NSPrefixLength;
        const SupportedArchitecture FB_ARCHS = SupportedArchitecture.X86 | SupportedArchitecture.X64;

        class IfDefGuard : IDisposable
        {
            private StringBuilder content;
            private string guardText;
            private static readonly string nl = Environment.NewLine;
            public IfDefGuard(StringBuilder sb, string guard)
            {
                content = sb;
                guardText = guard;
                if (guardText.Length > 0)
                {
                    content.AppendFormat("#If {0}{1}", guard, nl);
                }
            }
            public void Dispose()
            {
                if (guardText.Length > 0)
                {
                    content.AppendFormat("#Endif '' {0}{1}{1}", guardText, nl);
                }
            }
        }

        public FileOutput(
            GlobalTypeRegistry typedefs,
            NamespaceContent nsContent,
            HashSet<string> keywords,
            Dictionary<string, string> nsInjections,
            App.OptionSet optionSet
        )
        {
            const int oneMeg = 1024 * 1024;
            outputStreams = new OutputStreams(oneMeg);
            headers = new HashSet<string>(16);
            raiiWrappersCreated = new HashSet<string>(16);
            forwardDecls = new ForwardDeclarationManager();
            ns = nsContent;
            fbKeywords = keywords;
            typeRegistry = typedefs;
            injections = nsInjections;
            options = optionSet;
            ForceForwardDecls = false;
            NSPrefixLength = "windows.win32.".Length;
            if (options.generateGuidFiles)
            {
                guidDir = Path.Combine(options.outputDirectory, GUID_DIR);
                propkeyDir = Path.Combine(options.outputDirectory, PKEY_DIR);
            }
        }

        //private IfDefGuard GetArchOutput(CustomAttributeValues attrVals)
        //{
        //    string guard;
        //    if (attrVals == null || (attrVals.supportedArch == SupportedArchitecture.None))
        //    {
        //        guard = String.Empty;
        //    }
        //    else
        //    {
        //        SupportedArchitecture arch = attrVals.supportedArch;
        //        if (((arch & SupportedArchitecture.All) == FB_ARCHS))
        //        {
        //            guard = String.Empty;
        //        }
        //        else if (arch == SupportedArchitecture.X86)
        //        {
        //            guard = x86Guard;
        //        }
        //        else if (arch == SupportedArchitecture.ARM64)
        //        {
        //            guard = null;
        //        }
        //        else
        //        {
        //            guard = x64Guard;
        //        }
        //    }
        //    return guard == null ? null : new IfDefGuard(guard == String.Empty ? null : outputStreams.Main, guard);
        //}

        private string GetArchIfdef(CustomAttributeValues attrVals)
        {
            string guard;
            if (attrVals == null || (attrVals.supportedArch == SupportedArchitecture.None))
            {
                guard = String.Empty;
            }
            else
            {
                SupportedArchitecture arch = attrVals.supportedArch;
                if ((arch & SupportedArchitecture.All) == FB_ARCHS)
                {
                    guard = String.Empty;
                }
                else if((arch & SupportedArchitecture.X64) != 0)
                {
                    guard = x64Guard;
                }
                else if ((arch & SupportedArchitecture.X86) != 0)
                {
                    guard = x86Guard;
                }
                else // if (arch == SupportedArchitecture.ARM64)
                {
                    guard = null;
                }
            }
            return guard;
        }

        private void OutputInjectionType(StreamWriter sw, string injType)
        {
            string injText;
            if (injections.TryGetValue(injType, out injText))
            {
                sw.WriteLine(injText);
            }
        }

        public void Dispose()
        {
            headers.Remove(ns.Name);
            MergeStreams();
            string fileName = ns.Name.Remove(0, "windows.win32.".Length);
            string outputFile = Path.Combine(options.outputDirectory, fileName + ".bi");
            using (StreamWriter sw = new StreamWriter(outputFile))
            {
                string nsFBName = ns.Name.Replace(".", "_");
                sw.WriteLine("'' Autogenerated FB header by FBWindowsHeaderGen on {0}{1}", DateTime.UtcNow.ToString("O"), nl);
                sw.WriteLine("#Ifndef {0}{1}#define {0}{1}{1}", nsFBName, nl);
                //foreach (string header in headers)
                //{
                //    sw.WriteLine("#include once \"{0}.bi\"", header);
                //}
                sw.WriteLine();
                sw.WriteLine("Extern \"Windows\"");
                sw.WriteLine(outputStreams.Main.ToString());
                OutputInjectionType(sw, "enums");
                OutputInjectionType(sw, "structs");
                OutputInjectionType(sw, "constants");
                OutputInjectionType(sw, "functions");
                OutputInjectionType(sw, "functionptrs");
                OutputInjectionType(sw, "interfaces");
                sw.WriteLine("End Extern '' \"Windows\"" + nl);
                if (outputStreams.Overloads.Length > 0)
                {
                    sw.WriteLine(outputStreams.Overloads.ToString());
                }
                sw.WriteLine("#EndIf '' " + nsFBName);
            }
        }

        private void MergeStreams()
        {
            StringBuilder mainStream = outputStreams.Main;
            OutputStreams[] builders = { outputStreams };
            foreach (OutputStreams aob in builders)
            {
                if (aob.UnicodeDefs.Length > 0)
                {
                    mainStream.AppendLine("#Ifdef UNICODE");
                    mainStream.AppendLine(aob.UnicodeDefs.ToString());
                    mainStream.AppendLine("#Endif '' UNICODE");
                }
                if (aob.AnsiDefs.Length > 0)
                {
                    mainStream.AppendLine("#Ifndef UNICODE");
                    mainStream.AppendLine(aob.AnsiDefs.ToString());
                    mainStream.AppendLine("#Endif '' not UNICODE");
                }
                if (aob.RaiiWrappers.Length > 0)
                {
                    mainStream.AppendLine(aob.RaiiWrappers.ToString());
                }
            }
        }

        public void Output(TypeOrderer.AddedObject addedObj)
        {
            ForceForwardDecls = addedObj.ForceForwardDeclares;
            switch (addedObj.ObjType)
            {
                case RawTypeEntries.ObjectType.Constant: OutputConstant((ConstantValue)addedObj.TheObject); break;
                case RawTypeEntries.ObjectType.Function: OutputFunction((FunctionType)addedObj.TheObject); break;
                case RawTypeEntries.ObjectType.Struct: OutputStruct(null, null, null, (StructType<VarType>)addedObj.TheObject, options.niceWrappers, String.Empty, null, false); break;
                case RawTypeEntries.ObjectType.Interface: OutputInterface((StructType<FunctionType>)addedObj.TheObject); break;
                case RawTypeEntries.ObjectType.FunctionPointer: OutputFunctionPtr((FunctionPointerType)addedObj.TheObject); break;
                case RawTypeEntries.ObjectType.Enum: OutputEnum((StructType<ConstantValue>)addedObj.TheObject); break;
            }
        }

        //private void ResetFile()
        //{
        //    headers.Clear();
        //    allArchContent.Reset(true);
        //}

        //private void OutputStructs(List<StructType<VarType>> structs, bool niceWrappers, string indent, string guidDir)
        //{
        //    Dictionary<string, StringBuilder> unseenTypes = new Dictionary<string, StringBuilder>();
        //    foreach (StructType<VarType> structType in structs)
        //    {
        //        OutputStruct(structType, niceWrappers, indent, guidDir, false, unseenTypes);
        //    }
        //}

        private void OutputStruct(
            StringBuilder outputLoc, 
            StringBuilder forwardDeclOutput,
            StringBuilder headersOutput,
            StructType<VarType> structType, 
            bool niceWrappers,
            string indent, 
            HashSet<string> memberNames,
            bool forceFullType
        )
        {
            string memberIndent = indent + INDENT;

            CustomAttributeValues attrVals = structType.AttributeValues ?? dummyAttrVals;

            string archIfdef = GetArchIfdef(attrVals);
            if (archIfdef == null) return;
            StringBuilder content = outputLoc ?? outputStreams.Main;

            string structName = MangleFBKeyword(structType.Name);
            PrintTypeCommentPreamble(content, structName, attrVals);

            bool innerType = (memberNames != null);

            if (attrVals.guid.HasValue)
            {
                string guidName;
                if (!(structName.StartsWith("CLSID") || structName.StartsWith("IID") || structName.StartsWith("GUID") || structName.StartsWith("SID")))
                {
                    guidName = "CLSID_" + structName;
                }
                else
                {
                    guidName = structName;
                }
                if (!String.IsNullOrEmpty(guidDir))
                {
                    OutputGuidFile(guidDir, guidName, attrVals.guid.Value);
                }
                // this is where GUID is. we could lookup the guid type but since that means
                // we have to type out the namespace and typename to find it, might as well just type it out
                // to the file
                if (headers.Add("windows.win32.foundation"))
                {
                    content.AppendFormat(HEADER_INSERT_FORMAT, nl, "foundation");
                }
                content.AppendFormat("Extern {0} As Const GUID{1}", guidName, nl);
                // some of these are the metadata translations of things like:
                // class DECLSPEC_UUID("00021401-0000-0000-C000-000000000046")
                // ShellLink;
                // where the entry only exists to hang the coclass guid off
                // in those cases, there's no point going through the other contortions
                // just move on to the next one
                if (structType.Fields.Count == 0)
                {
                    return;
                }
            }

            string strType;
            if (structName.EndsWith("Union") || ((structType.TypeAttributes & TypeAttributes.ExplicitLayout) != 0))
            {
                strType = "Union";
            }
            else
            {
                strType = "Type";
            }
            List<VarType> structContents = structType.Fields;
            // there are some inner types / anonymous unions that have the required data to generate autosizing constructors
            // but in FB, anonymous unions can't have constructors
            bool suppressInitWrap = innerType;
            StringBuilder structBuffer = new StringBuilder();
            StringBuilder forwardDeclarations = forwardDeclOutput ?? new StringBuilder();
            StringBuilder headersOut = headersOutput ?? new StringBuilder();
            // AFAICT These empty types are restricted to the GdiPlus object types like GpGraphics, GpImage
            // Freebasic doesn't like empty types, so like handles, we fudge in a member so that
            // they are unique types (instead of just making them Any Ptrs) so that can't be accidentally passed to each others functions
            if (structContents.Count == 0)
            {
                Debug.Assert(structName.StartsWith("Gp"), "What is this type if not GdiPlus? Does this work for it?");
                structContents.Add(new VarType("__unused", dummyAttrVals, new PrimitiveTypeHandleInfo(System.Reflection.Metadata.PrimitiveTypeCode.UInt32), FieldAttributes.Public));
            }
            // typedefs aren't really a type, just a wrapper for the real type
            if (attrVals.nativeTypedef)
            {

                Debug.Assert(structContents.Count == 1, "Native typedef had more than 1 internal field!?");
                SimpleTypeHandleInfo asType = structContents[0].ParamType;
                // to increase type safety like in the Windows headers, instead of all the handles just been
                // typedefs to void* as they are in the metadata (and thus freely assignable to each other),
                // we create empty structs of each type and make the handle types pointers to those
                // (so you can't assign a HWND to a HANDLE)
                if (IsWinHandleType(structName, asType, typeRegistry))
                {
                    OutputWindowsHandle(structBuffer, structName);
                }
                else
                {
                    structBuffer.AppendFormat("{0}{1} {2} As {3}{4}", indent, strType, structName, MangleFBKeyword(asType.ToString()), nl);
                }
                AddHeaderForType(asType, headersOutput);
            }
            else
            {
                // Freebasic can handle anonymous inner types, so we take advantage of that
                // unfortunatly, the metadata defines the anonymous types first, and then places fields of them
                // at the correct offset. To do it properly, we need to hold off defining the anonymous structs and unions
                // until the correct offset in the type
                string packingSpace = structType.Layout.Value.IsDefault ? String.Empty : String.Format("Field={0}", structType.Layout.Value.PackingSize);
                if (forceFullType || !(structName.StartsWith("_Anonymous") || innerType))
                {
                    structBuffer.AppendFormat("{0}{1} {2} {3}{4}", indent, strType, structName, packingSpace, nl);
                }
                else
                {
                    structBuffer.AppendFormat("{0}{1} {2}{3}", indent, strType, packingSpace, nl);
                }
                HashSet<string> members = memberNames ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (VarType member in structContents)
                {
                    string name = MangleFBKeyword(member.Name);
                    name = UniquifyName(name, members);
                    CustomAttributeValues memVals = member.CustomAttributes ?? dummyAttrVals;
                    SimpleTypeHandleInfo memberType = member.ParamType;

                    // this has to be special cased as the dimensions attach to the variable name
                    // rather than the type, but it is by far the least common occurrance
                    ArrayTypeHandleInfo memberArray = (memberType as ArrayTypeHandleInfo);
                    SimpleTypeHandleInfo strippedType = PointerTypeHandleInfo.StripAllPointers(memberArray?.DataType ?? memberType).Stripped;

                    // can't do a nice initialisation if we don't know how long the struct is
                    if (memVals.flexibleArray)
                    {
                        suppressInitWrap = true;
                    }
                    if (memVals.bitfields != null)
                    {
                        long offsetSoFar = 0;
                        foreach (BitfieldMember bm in memVals.bitfields)
                        {
                            Debug.Assert(bm.offset == offsetSoFar, "There are gaps in this bitfield!");
                            string bmName = MangleFBKeyword(bm.name);
                            bmName = UniquifyName(bmName, members);
                            structBuffer.AppendFormat("{0}{1} : {2} As {3}{4}", memberIndent, bmName, bm.length, MangleFBKeyword(member.ParamType.ToString()), nl);
                            offsetSoFar += bm.length;
                        }
                    }
                    else if (!(name.StartsWith("Anonymous") || IsInnerType(strippedType, structType.NestedTypes)))
                    {
                        string memAttrStr = FormatMemberAttributeString(memVals, name);
                        if (memAttrStr.Length > 0)
                        {
                            structBuffer.AppendFormat("{0}'' {1}{2}", memberIndent, memAttrStr, nl);
                        }

                        SimpleTypeHandleInfo outputType = PtrifyInterfaceType(memberType);
                        ForwardDeclarationManager.ForwardDeclaration? fwdDeclType = null;
                        // we need to do this in the non-array case, and in the case the array isn't a string
                        if ((memberArray == null) || !IsStringArray(memberArray.DataType, name))
                        {
                            fwdDeclType = GetForwardDeclarationType(outputType);
                            if (fwdDeclType.HasValue)
                            {
                                outputType = fwdDeclType.Value.Equivalent;
                                forwardDecls.FormatAndAdd(forwardDeclarations, PointerTypeHandleInfo.StripAllPointers(memberArray?.DataType ?? memberType).Stripped, fwdDeclType.Value.BaseType);
                            }
                        }

                        if (memberArray == null)
                        {
                            structBuffer.AppendFormat(
                                "{0}{1} As {2}{3}{4}",
                                memberIndent,
                                name,
                                memVals.isConst ? "Const " : String.Empty,
                                outputType,
                                nl
                            );
                        }
                        else
                        {
                            SimpleTypeHandleInfo arrayDataType = memberArray.DataType;
                            // fixup array of chars to zstring * num instead (0 to x) as char
                            if (IsStringArray(arrayDataType, name))
                            {
                                Debug.Assert(memberArray.Bounds.Rank == 1);
                                int numElems = memberArray.Bounds.Sizes[0] - memberArray.Bounds.LowerBounds[0];
                                structBuffer.AppendFormat(
                                    "{0}{1} As {2}{3} * {4}{5}",
                                    memberIndent,
                                    name,
                                    memVals.isConst ? "Const " : String.Empty,
                                    (arrayDataType.TypeInfo == typeof(NonFBChar)) ? "WString" : "ZString",
                                    numElems,
                                    nl
                                );
                            }
                            else
                            {
                                structBuffer.AppendFormat(
                                    "{0}{1}{2} As {3}{4}{5}",
                                    memberIndent,
                                    name,
                                    memberArray.DimensionString(),
                                    memVals.isConst ? "Const " : String.Empty,
                                    outputType,
                                    nl
                                );
                            }
                        }
                        if (!fwdDeclType.HasValue)
                        {
                            AddHeaderForType(member.ParamType, headersOut);
                        }
                    }
                    else
                    {
                        HandleTypeHandleInfo hInf;
                        HashSet<string> membersToPass;
                        bool isPointer = (strippedType != memberType);
                        bool isArray = (memberArray != null);
                        bool isIndirect = isPointer | isArray;
                        if (isArray)
                        {
                            hInf = memberArray.DataType as HandleTypeHandleInfo;
                            membersToPass = null;
                        }
                        else
                        {
                            hInf = strippedType as HandleTypeHandleInfo;
                            membersToPass = isPointer ? null : members;
                        }
                        StructType<VarType> anonymousType = structType.NestedTypes.Find(
                            (x) =>
                            {
                                return x.Name == hInf.ActualName;
                            }
                        );
                        if (!isIndirect)
                        {
                            // Freebasic can only nest anonymous structs and unions if they alternate
                            bool hasAlternated = (
                                (strType == "Union") && ((anonymousType.TypeAttributes & TypeAttributes.ExplicitLayout) == 0) ||
                                (strType == "Type") && ((anonymousType.TypeAttributes & TypeAttributes.ExplicitLayout) != 0)
                            );
                            // if we haven't alternated it's FAR easier to synthesize the opposite type and put this inside that
                            // than it is to rename things, generate the type outside of this nesting level and then generate the definition
                            if (!hasAlternated)
                            {
                                string innerName = "_Anonymous_inner";
                                TypeAttributes newInnerTypeAttrs = (anonymousType.TypeAttributes ^ TypeAttributes.ExplicitLayout) & TypeAttributes.ExplicitLayout;
                                if (newInnerTypeAttrs == TypeAttributes.ExplicitLayout)
                                {
                                    innerName += "_Union";
                                }
                                List<StructType<VarType>> inner = new List<StructType<VarType>> { anonymousType };
                                StructType<VarType> innerTypeWrap = new StructType<VarType>(
                                    innerName,
                                    null,
                                    newInnerTypeAttrs,
                                    null,
                                    inner,
                                    new System.Reflection.Metadata.TypeLayout(0, 0)
                                );
                                innerTypeWrap.AddMember(member);
                                anonymousType = innerTypeWrap;
                            }
                        }
                        bool nonAnonymousOutput = isIndirect;
                        OutputStruct(structBuffer, forwardDeclarations, headersOut, anonymousType, niceWrappers, memberIndent, membersToPass, nonAnonymousOutput);
                        if (nonAnonymousOutput)
                        {
                            if (isArray)
                            {
                                structBuffer.AppendFormat(
                                    "{0}{1}{2} As {3}{4}{5}",
                                    memberIndent,
                                    name,
                                    memberArray.DimensionString(),
                                    memVals.isConst ? "Const " : String.Empty,
                                    memberArray,
                                    nl
                                );
                            }
                            else
                            {
                                Debug.Assert(isPointer);
                                string memberTypeName = memberType.ToString();
                                int dotPos = memberTypeName.IndexOf('.');
                                if(dotPos != -1)
                                {
                                    memberTypeName = memberTypeName.Substring(dotPos + 1);
                                }
                                structBuffer.AppendFormat(
                                    "{0}{1} As {2}{3}{4}",
                                    memberIndent,
                                    name,
                                    memVals.isConst ? "Const " : String.Empty,
                                    memberTypeName,
                                    nl
                                );
                            }
                        }
                    }
                }
                if (niceWrappers && (!suppressInitWrap) && (attrVals.sizeField.HasValue))
                {
                    ArrayMemorySizeAttribute sizeAttr = attrVals.sizeField.Value;
                    Debug.Assert(sizeAttr.which == ArrayMemorySizeAttribute.FieldToUse.Struct);
                    structBuffer.AppendFormat(
                        "{0}Declare Constructor(){1}" +
                        "End {3}{1}" +
                        "{1}" +
                        "Private Constructor {4}(){1}" +
                        "{0}{2} = Sizeof(This){1}" +
                        "End Constructor{1}",
                        memberIndent,
                        nl,
                        MangleFBKeyword(sizeAttr.details.Item2),
                        strType,
                        structName
                    );
                }
                else
                {
                    structBuffer.AppendFormat("{0}End {1}{2}", indent, strType, innerType ? String.Empty : nl);
                }
            }
            IfDefGuard guardWriter = null;
            if (!innerType)
            {
                if (headersOut.Length > 0)
                {
                    content.AppendLine(headersOut.ToString());
                }
                guardWriter = new IfDefGuard(content, archIfdef);
                if (forwardDeclarations.Length > 0)
                {
                    content.AppendLine(forwardDeclarations.ToString());
                }
            }
            content.AppendLine(structBuffer.ToString());
            if (attrVals.ansiApi)
            {
                string neutralName = structName.Remove(structName.Length - 1);
                if (typeRegistry.LookupStruct(ns.Name, neutralName) == null)
                {
                    outputStreams.AnsiDefs.AppendFormat("Type {0} As {1}{2}", neutralName, structName, nl);
                }
            }
            else if (attrVals.unicodeApi)
            {
                string neutralName = structName.Remove(structName.Length - 1);
                if (typeRegistry.LookupStruct(ns.Name, neutralName) == null)
                {
                    outputStreams.UnicodeDefs.AppendFormat("Type {0} As {1}{2}", neutralName, structName, nl);
                }
            }

            if (niceWrappers && !String.IsNullOrEmpty(attrVals.raiiFree))
            {
                FunctionType freeingFun = typeRegistry.LookupFunction(ns.Name, attrVals.raiiFree);
                OutputRAIIWrapper(outputStreams, structName, structName, freeingFun, attrVals);
            }
            // this is now defined, no need for forward decls now
            forwardDecls.ConcreteDeclaration(SignatureTypeProvider.CreateNameOnlyTypeInfo(ns.Name, structName));
            guardWriter?.Dispose();
        }

        //private void OutputFunctionPtrs(List<FunctionPointerType> funPtrs)
        //{
        //    foreach (FunctionPointerType fnPtr in funPtrs)
        //    {
        //        OutputFunctionPtr(fnPtr);
        //    }
        //}

        private void OutputFunctionPtr(FunctionPointerType fnPtr)
        {
            CustomAttributeValues ptrVals = fnPtr.AttributeValues ?? dummyAttrVals;

            string archIfDef = GetArchIfdef(ptrVals);
            if (archIfDef == null) return;
            StringBuilder content = outputStreams.Main;

            string ptrName = MangleFBKeyword(fnPtr.Name);
            PrintTypeCommentPreamble(content, ptrName, ptrVals);
            FunctionType fn = fnPtr.Shape;

            ArgListInformation argList = GatherArgList(fn.Arguments, INDENT);
            FunctionArgType functionRetType = fn.ReturnType;
            SimpleTypeHandleInfo retType = PtrifyInterfaceType(functionRetType.ParamType);
            ForwardDeclarationManager.ForwardDeclaration? fwdRetType = GetForwardDeclarationType(retType);
            if (fwdRetType.HasValue)
            {
                retType = fwdRetType.Value.Equivalent;
                forwardDecls.FormatAndAdd(
                    argList.forwardDeclares,
                    PointerTypeHandleInfo.StripAllPointers(functionRetType.ParamType).Stripped,
                    fwdRetType.Value.BaseType
                );
            }
            else
            {
                AddHeaderForType(retType, argList.headers);
            }

            if(argList.headers.Length > 0)
            {
                content.AppendLine(argList.headers.ToString());
            }
            using (IfDefGuard guardWriter = new IfDefGuard(content, archIfDef))
            {
                if (argList.forwardDeclares.Length > 0)
                {
                    content.AppendLine(argList.forwardDeclares.ToString());
                }

                bool isFunction = IsFunctionFromReturnType(fn.ReturnType);
                content.AppendFormat("Type {0} as {1}(", ptrName, (isFunction ? "Function " : "Sub"));
                if(argList.parameters > 1)
                {
                    content.AppendLine(" _");
                }
                content.Append(argList.argListOutput.ToString());
                content.Append(')');
                if (isFunction)
                {
                    content.AppendFormat(" As {0}", MangleFBKeyword(retType.ToString()));
                }
                content.AppendLine(nl);
                if (ptrVals.ansiApi)
                {
                    string neutralName = ptrName.Remove(ptrName.Length - 1);
                    if (typeRegistry.LookupFunctionPtr(ns.Name, neutralName) == null)
                    {
                        outputStreams.AnsiDefs.AppendFormat("Type {0} As {1}{2}", neutralName, ptrName, nl);
                    }
                }
                else if (ptrVals.unicodeApi)
                {
                    string neutralName = ptrName.Remove(ptrName.Length - 1);
                    if (typeRegistry.LookupFunctionPtr(ns.Name, neutralName) == null)
                    {
                        outputStreams.UnicodeDefs.AppendFormat("Type {0} As {1}{2}", neutralName, ptrName, nl);
                    }
                }
                // this is now defined, no need for forward decls now
                forwardDecls.ConcreteDeclaration(SignatureTypeProvider.CreateNameOnlyTypeInfo(ns.Name, ptrName));
            }
        }

        //private void OutputFunctions(List<FunctionType> funcs, bool niceWrappers)
        //{
        //    // https://github.com/microsoft/win32metadata/issues/436#issuecomment-1470947176
        //    foreach (FunctionType fn in funcs)
        //    {
        //        OutputFunction(fn, niceWrappers);
        //    }
        //}

        private void OutputFunction(FunctionType fn)
        {
            CustomAttributeValues fnVals = fn.AttributeValues ?? dummyAttrVals;

            string archIfDef = GetArchIfdef(fnVals);
            if (archIfDef == null) return;
            StringBuilder content = outputStreams.Main;

            // mainly for VarCmp (the function) and VARCMP (the enum of its results)
            // we fon't rename the enum because it's used as the result of several functions and it requires 
            // figuring out if parameter types/results, struct members etc need renaming when
            // renaming the function only affects that
            string fnName = MangleFBKeyword(fn.Name);
            //fnName = RenameAlreadyUsedName(fnName, RawTypeEntries.ObjectType.Function, ns.TypeEntries);
            PrintTypeCommentPreamble(content, fnName, fnVals);

            ArgListInformation argList = GatherArgList(fn.Arguments, INDENT);
            FunctionArgType functionRetType = fn.ReturnType;
            SimpleTypeHandleInfo retType = PtrifyInterfaceType(functionRetType.ParamType);
            ForwardDeclarationManager.ForwardDeclaration? fwdRetType = GetForwardDeclarationType(retType);
            if (fwdRetType.HasValue)
            {
                retType = fwdRetType.Value.Equivalent;
                forwardDecls.FormatAndAdd(
                    argList.forwardDeclares,
                    PointerTypeHandleInfo.StripAllPointers(functionRetType.ParamType).Stripped,
                    fwdRetType.Value.BaseType
                );
            }
            else
            {
                AddHeaderForType(retType, argList.headers);
            }
            if (argList.headers.Length > 0)
            {
                content.AppendLine(argList.headers.ToString());
            }
            using (IfDefGuard guard = new IfDefGuard(content, archIfDef))
            {
                // ifdef guard and headers
                if (argList.forwardDeclares.Length > 0)
                {
                    content.AppendLine(argList.forwardDeclares.ToString());
                }

                if (!String.IsNullOrEmpty(fnVals.constantValue))
                {
                    content.AppendFormat("Private Function {0} (_{1}", fnName, nl);
                    Debug.Assert(argList.parameters == 0, "Wasn't expecting constant function with parameters");
                    content.AppendFormat(
                        ") As {0}{1}" +
                        "{1}" +
                        "{2}Return cast({0}, {3}){1}" +
                        "{1}" +
                        "End Function{1}",
                        MangleFBKeyword(retType.ToString()),
                        nl,
                        INDENT,
                        fnVals.constantValue
                    );
                    return;
                }
                bool isFunction = IsFunctionFromReturnType(functionRetType);
                DllImportAttribute importAttr = fnVals.dllImport;
                CallingConvention callConv = CallingConvention.Winapi;
                if (importAttr != null)
                {
                    string dllName = importAttr.Value;
                    content.AppendFormat("'' Dll - {0}{1}", dllName, nl);
                    callConv = importAttr.CallingConvention;
                }
                string funType = (isFunction ? "Function" : "Sub");
                content.AppendFormat("Declare {0} {1} {2} Overload (", funType, fnName, CallConvToName(callConv));
                int numArgs = fn.Arguments.Count;
                if (numArgs > 0)
                {
                    content.AppendLine("_ ");
                }
                content.Append(argList.argListOutput.ToString());
                content.Append(')');
                bool retValWrap = false;
                CustomAttributeValues retAttrs = null;
                string mangledReturnType = String.Empty;
                if (isFunction)
                {
                    mangledReturnType = MangleFBKeyword(retType.ToString());
                    retAttrs = retType.TypeAttributes;
                    content.AppendFormat(" As {0}", mangledReturnType);
                    retValWrap = !String.IsNullOrEmpty(retAttrs?.raiiFree);
                }
                content.AppendLine(nl);
                if (fnVals.ansiApi)
                {
                    string neutralName = fnName.Remove(fnName.Length - 1);
                    if (typeRegistry.LookupFunction(ns.Name, neutralName) == null)
                    {
                        outputStreams.AnsiDefs.AppendFormat("#define {0} {1}{2}", neutralName, fnName, nl);
                    }
                }
                else if (fnVals.unicodeApi)
                {
                    string neutralName = fnName.Remove(fnName.Length - 1);
                    if (typeRegistry.LookupFunction(ns.Name, neutralName) == null)
                    {
                        outputStreams.UnicodeDefs.AppendFormat("#define {0} {1}{2}", neutralName, fnName, nl);
                    }
                }
                if (options.niceWrappers)
                {
                    if (retValWrap)
                    {
                        FunctionType freeingFun = typeRegistry.LookupFunction(ns.Name, retAttrs.raiiFree);
                        OutputRAIIWrapper(outputStreams, fnName + "Ret", mangledReturnType, freeingFun, retAttrs);
                    }
                    int whichArg = HasOutputThatIsntForwarded(argList);
                    int skipWrapperBitfield = 0;
                    if ((whichArg >= 0) && (mangledReturnType == "HRESULT"))
                    {
                        OutputNiceOutputReturnWrapper(content, outputStreams.Overloads, null, fnName, fn.Arguments, argList, whichArg, typeRegistry);
                        skipWrapperBitfield = ShouldSkipOptionalWrap(whichArg, fn.Arguments);
                    }
                    OutputOptionalFunctionParamLadder(argList, null, outputStreams.Overloads, isFunction, null, fnName, mangledReturnType, skipWrapperBitfield);
                }
            }
        }

        // this produces n-1 copies of a function with each requiring one more optional param
        // it doesn't create the whole matrix of optional param functions, just from left to right
        // so, for instance, if the base function is:
        // function DoThings(HWND a, optional HWND b, optional HWND c, DWORD d)
        // this will output
        // function DoThings(HWND a, DWORD d) { return DoThings(a, 0, 0, d); }
        // function DoThings(HWND a, HWND b, DWORD d) { return DoThings(a, b, 0, d);
        //
        // We skip producing it if there is only one arg and it is optional
        // that stops generation of dumb things like
        // function GlobalFree() { return GlobalFree(0);}
        private void OutputOptionalFunctionParamLadder(
            ArgListInformation argList, 
            StringBuilder declares, 
            StringBuilder content, 
            bool isFunction, 
            string iface,
            string fnName, 
            string returnType,
            int skipGenBitfield
        )
        {
            int optParams = argList.optionalParamBitfield & ~skipGenBitfield;
            if ((optParams != 0) && (argList.parameters > 1))
            {
                bool noIface = String.IsNullOrEmpty(iface);
                string contentFunctionName = noIface ? fnName : iface + "." + fnName;
                string declIndent = noIface ? String.Empty : INDENT;
                StringBuilder declaresToUse = declares ?? new StringBuilder();
                string funType = isFunction ? "Function" : "Sub";
                StringBuilder innerStatement = new StringBuilder();
                while (optParams != 0)
                {
                    bool seenFirstOptParam = false;
                    content.AppendFormat("Private {0} {1} Overload ( ", funType, contentFunctionName);
                    declaresToUse.AppendFormat("{0}Declare {1} {2} Overload ( ", declIndent, funType, fnName);
                    int paramNum = 0;
                    if (isFunction)
                    {
                        innerStatement.AppendFormat("{0}Return ", INDENT);
                    }
                    innerStatement.AppendFormat("{0}(", fnName);
                    bool didOneParamDef = false;
                    foreach (string paramDef in argList.parameterDefs)
                    {
                        int paramBit = 1 << paramNum;
                        if ((optParams & paramBit) != 0)
                        {
                            if (!seenFirstOptParam)
                            {
                                // make this param required next time
                                seenFirstOptParam = true;
                                optParams &= ~paramBit;
                            }
                            innerStatement.Append("0, ");
                        }
                        else
                        {
                            didOneParamDef = true;
                            content.AppendFormat("{0}, ", paramDef);
                            declaresToUse.AppendFormat("{0}, ", paramDef);
                            innerStatement.AppendFormat("{0}, ", argList.argNames[paramNum]);
                        }
                        ++paramNum;
                    }
                    // chop off the trailing commas
                    if (didOneParamDef)
                    {
                        content.Length -= 2;
                        declaresToUse.Length -= 2;
                    }
                    innerStatement.Length -= 2;
                    content.AppendFormat("){0}{1}", isFunction ? " As " + returnType : String.Empty, nl);
                    declaresToUse.AppendFormat("){0}{1}", isFunction ? " As " + returnType : String.Empty, nl);
                    innerStatement.Append(')');
                    content.AppendLine(innerStatement.ToString());
                    content.AppendFormat("End {0}{1}{1}", funType, nl);
                    innerStatement.Length = 0;
                }
            }
        }

        // theh parameters here are pre-mangled
        private void OutputRAIIWrapper(OutputStreams streams, string wrapTypeName, string wrappedTypeName, FunctionType closerFunc, CustomAttributeValues attrVals)
        {
            if ((closerFunc == null) || (closerFunc.Arguments.Count != 1))
            {
                // currently (2024-07-14), this is only the case for 2 user-implemented functions that are obsolete anyway
                Debug.WriteLine(String.Format("RAII Struct '{0}' couldn't find free function '{1}' in namespace '{2}' or it had more than one parameter", wrappedTypeName, attrVals.raiiFree, ns.Name));
                return;
            }
            if (raiiWrappersCreated.Contains(wrappedTypeName))
            {
                return;
            }
            raiiWrappersCreated.Add(wrappedTypeName);
            streams.RaiiWrappers.AppendFormat(
                "Type {0}_{4}{1}" +
                "{6}dt As {5}{1}" +
                "{6}Declare Constructor(){1}" +
                "{6}Declare Constructor(ByVal p as {5}){1}" +
                "{6}Declare Destructor(){1}" +
                "{6}Declare Operator @() as {0} Ptr{1}" +
                "End Type{1}" +
                "{1}" +
                "Private Constructor {0}_{4}(){1}" +
                "{6}{3}{1}" +
                "End Constructor{1}" +
                "{1}" +
                "Private Constructor {0}_{4}(ByVal p as {5}){1}" +
                "{6}dt = p{1}" +
                "End Constructor{1}" +
                "{1}" +
                "Private Destructor {0}_{4}(){1}" +
                "{6}{2}(cast({7}, dt)){1}" +
                "End Destructor{1}" +
                "{1}" +
                "Private Operator {0}_{4}.@() As {0} Ptr{1}" +
                "{6}Return @dt{1}" +
                "End Operator{1}{1}",
                wrapTypeName,
                nl,
                closerFunc.Name,
                attrVals.invalidHandleValue != null ?
                    String.Format("dt = Cast({0}, {1})", wrappedTypeName, attrVals.invalidHandleValue[0]) :
                    "Clear dt, 0, SizeOf(dt)",
                AUTOFREE_WRAP_SUFFIX,
                wrappedTypeName,
                INDENT,
                closerFunc.Arguments[0].ParamType
            );
            if (attrVals.ansiApi)
            {
                streams.AnsiDefs.AppendFormat("Type {0}_{1} As {2}_{1}{3}", wrapTypeName.Remove(wrapTypeName.Length - 1), AUTOFREE_WRAP_SUFFIX, wrapTypeName, nl);
            }
            else if (attrVals.unicodeApi)
            {
                streams.UnicodeDefs.AppendFormat("Type {0}_{1} As {2}_{1}{3}", wrapTypeName.Remove(wrapTypeName.Length - 1), AUTOFREE_WRAP_SUFFIX, wrapTypeName, nl);
            }
        }

        //private void OutputEnums(List<StructType<ConstantValue>> entries)
        //{
        //    foreach (StructType<ConstantValue> enumType in entries)
        //    {
        //        OutputEnum(enumType);
        //    }
        //}

        private string MakeRandomConstantName()
        {
            const string alphas = "qwertyuiopasdfghjklzxcvcbnm";
            Random r = new Random();
            int len = r.Next(8, 13);
            StringBuilder builder = new StringBuilder(len);
            for(int i = 0; i < len; ++i)
            {
                builder.Append(alphas[r.Next(alphas.Length)]);
            }
            return builder.ToString();
        }

        private void OutputEnum(StructType<ConstantValue> enumType)
        {
            CustomAttributeValues enumAttrVals = enumType.AttributeValues ?? dummyAttrVals;
            string archIfDef = GetArchIfdef(enumAttrVals);
            if (archIfDef == null) return;
            StringBuilder content = outputStreams.Main;
            using (IfDefGuard guard = new IfDefGuard(content, archIfDef))
            {
                string enumName = MangleFBKeyword(enumType.Name);
                enumName = RenameAlreadyUsedName(enumName, RawTypeEntries.ObjectType.Enum, ns.TypeEntries);
                PrintTypeCommentPreamble(content, enumName, enumAttrVals);
                content.AppendFormat("Enum '' {0}", enumName);
                enumType.Fields.Sort();
                int largestByteType = 4; // default to ulong
                if (enumType.Fields.Count != 0)
                {
                    foreach (ConstantValue cv in enumType.Fields)
                    {
                        string constantName = MangleFBKeyword(cv.varType.Name);
                        constantName = RenameAlreadyUsedName(constantName, RawTypeEntries.ObjectType.Constant, ns.TypeEntries);
                        VarValue? vv = cv.varValue;
                        if(vv.HasValue && (vv.Value.ValueByteLength > largestByteType))
                        {
                            largestByteType = vv.Value.ValueByteLength;
                        }
                        content.AppendFormat(
                            "{0}{1}{2}{3},",
                            nl,
                            INDENT,
                            constantName,
                            vv.HasValue ? " = " + vv.Value.ToString() : String.Empty
                        );
                    }
                    content.Length -= 1; // chop off last comma
                }
                else
                {
                    // oh yes, there are enums with no entries. 
                    // windows.win32.networking.windowswebservices WS_XML_BUFFER_PROPERTY_ID and another
                    // and they are in the same file, so we can't just invent a normal name as it'll be duplicated
                    // so make a random one instead
                    string contName = MakeRandomConstantName();
                    content.AppendFormat("{0}{1}{2} = 0 '' autogenerated name to prevent empty enum", nl, INDENT, contName);
                }
                content.AppendFormat("{0}End Enum '' {1}{0}", nl, enumName);
                content.AppendFormat("Type {0} As {1}{2}{2}", enumName, (largestByteType == 4 ? "ULong" : "ULongInt"), nl);
                // this is now defined, no need for forward decls now
                forwardDecls.ConcreteDeclaration(SignatureTypeProvider.CreateNameOnlyTypeInfo(ns.Name, enumName));
            }
        }

        //private void OutputConstants(List<ConstantValue> constants, string guidDir)
        //{
        //    foreach (ConstantValue con in constants)
        //    {
        //        OutputConstant(con, guidDir);
        //    }
        //}

        private void OutputConstant(ConstantValue con)
        {
            CustomAttributeValues conAttrVals = con.varType.CustomAttributes ?? dummyAttrVals;
            string archIfDef = GetArchIfdef(conAttrVals);
            if (archIfDef == null) return;
            IfDefGuard guard = null;
            StringBuilder content = outputStreams.Main;

            string constantName = MangleFBKeyword(con.varType.Name);
            constantName = RenameAlreadyUsedName(constantName, RawTypeEntries.ObjectType.Constant, ns.TypeEntries);

            PrintTypeCommentPreamble(content, constantName, conAttrVals);
            SimpleTypeHandleInfo conType = con.varType.ParamType;
            SimpleTypeHandleInfo realType = GetRealType(conType, typeRegistry) ?? conType;
            if (conAttrVals.guid.HasValue)
            {
                // guids are defined in here
                if (headers.Add("windows.win32.foundation"))
                {
                    content.AppendFormat(HEADER_INSERT_FORMAT, nl, "foundation");
                }
                if (!String.IsNullOrEmpty(guidDir))
                {
                    OutputGuidFile(guidDir, constantName, conAttrVals.guid.Value);
                }
                content.AppendFormat("Extern {0} As Const GUID{1}", constantName, nl);
            }
            else
            {
                AddHeaderForType(conType, content);
                guard = new IfDefGuard(content, archIfDef);
                if (String.IsNullOrEmpty(conAttrVals.constantValue))
                {
                    VarValue conValue = con.varValue.Value;
                    SimpleTypeHandleInfo realStringType = IsStringType(conType, typeRegistry);
                    // constant ints can be assigned to string constants
                    // Windows.Win32.UI.WindowsAndMessaging.RT_CURSOR and friends
                    // wnere 1 to 21 are assigned to PWSTRs
                    // we don't want those output as normal string assignations
                    if (realStringType == null || BasicFBTypes.IsPrimitive(conValue.TypeCode))
                    {
                        bool typeIsPrimitive = IsValueType(conType, typeRegistry) && (!IsWinHandleType(conType.ToString(), conType, typeRegistry));
                        string mangledType = MangleFBKeyword(conType.ToString());
                        content.AppendFormat(
                            "Const {0} As {1} = {2}{3}",
                            constantName,
                            mangledType,
                            (typeIsPrimitive && (realStringType == null)) ? conValue : String.Format("cast({0}, {1})", mangledType, conValue),
                            nl
                        );
                    }
                    else // strings
                    {
                        bool isNotUnicode = conAttrVals.ansiApi;
                        string valueTextFormat = isNotUnicode ? "Const {0} = !\"{1}\"{2}" : "Const {0} = WStr(!\"{1}\"){2}";
                        content.AppendFormat(
                            valueTextFormat,
                            constantName,
                            conValue,
                            nl
                        );
                    }
                }
                else // has a constant initialiser that's a struct or something
                {
                    // these have to be defines because you can't initialise const structs
                    string constInitialiser = conAttrVals.constantValue;
                    SimpleTypeHandleInfo realConType = GetRealType(conType, typeRegistry) ?? conType;
                    bool isNotPrimitive = !(BasicFBTypes.IsPrimitive(realConType.TypeInfo) || BasicFBTypes.IsStringPointer(realConType.TypeInfo));
                    string value = FixConstantValue(conAttrVals.constantValue, conType);
                    if ((constInitialiser[0] == '{') && (constInitialiser[constInitialiser.Length - 1] == '}') || isNotPrimitive)
                    {
                        string conTypeName = conType.ToString();
                        switch (conTypeName)
                        {
                            case "DEVPROPKEY":
                            case "PROPERTKEY":
                            {
                                if (!String.IsNullOrEmpty(propkeyDir))
                                {
                                    OutputPropertyKey(propkeyDir, conTypeName, constantName, value, content);
                                }
                            }
                            break;
                            default:
                            {
                                content.AppendFormat(
                                    "#define {0} Type({1}){2}",
                                    constantName,
                                    value,
                                    nl
                                );
                            }
                            break;
                        }
                    }
                    else
                    {
                        content.AppendFormat(
                            "Const {0} As {1} = Type({2}){3}",
                            constantName,
                            MangleFBKeyword(conType.ToString()),
                            value,
                            nl
                        );
                    }
                }
            }
            if (conAttrVals.ansiApi)
            {
                string neutralName = constantName.Remove(constantName.Length - 1);
                if (typeRegistry.LookupConstant(ns.Name, neutralName) == null)
                {
                    outputStreams.AnsiDefs.AppendFormat("#define {0} {1}{2}", neutralName, constantName, nl);
                }
            }
            else if (conAttrVals.unicodeApi)
            {
                string neutralName = constantName.Remove(constantName.Length - 1);
                if (typeRegistry.LookupConstant(ns.Name, neutralName) == null)
                {
                    outputStreams.UnicodeDefs.AppendFormat("#define {0} {1}{2}", neutralName, constantName, nl);
                }
            }
            guard?.Dispose();
        }

        //private void OutputInterfaces(List<StructType<FunctionType>> interfaces, bool niceWrappers, string guidDir)
        //{
        //    StringBuilder niceWrapperContent = new StringBuilder();
        //    foreach (StructType<FunctionType> iface in interfaces)
        //    {
        //        OutputInterface(iface, niceWrappers, guidDir);
        //    }
        //}

        private void OutputInterface(StructType<FunctionType> iface)
        {
            StringBuilder niceWrapperContent = new StringBuilder();
            CustomAttributeValues attrVals = iface.AttributeValues ?? dummyAttrVals;
            string archIfDef = GetArchIfdef(attrVals);
            
            if (archIfDef == null) return;
            StringBuilder content = outputStreams.Main;

            string ifaceName = MangleFBKeyword(iface.Name);
            string iidName = "IID_" + ifaceName;
            List<HandleTypeHandleInfo> ifaceBases = iface.Bases;
            // i think this is always the case anyway
            Debug.Assert((ifaceBases == null) || (ifaceBases.Count <= 1), String.Format("Interface {0} has multiple bases!", ifaceName));

            if (attrVals.guid.HasValue)
            {
                // guids are defined in here
                if (headers.Add("windows.win32.foundation"))
                {
                    content.AppendFormat(HEADER_INSERT_FORMAT, nl, "foundation");
                }
                if (!String.IsNullOrEmpty(guidDir))
                {
                    OutputGuidFile(guidDir, iidName, attrVals.guid.Value);
                }
                content.AppendFormat("Extern {0} As Const GUID{1}", iidName, nl);
            }

            StringBuilder includeText = new StringBuilder();

            string baseName;
            if ((ifaceBases == null) || (ifaceBases.Count == 0))
            {
                // things have to derive from Object (or something that does) to have abstract / virtual members in freebasic
                baseName = "Object";
            }
            else
            {
                // can't forward declare base types
                HandleTypeHandleInfo baseType = ifaceBases[0];
                baseName = MangleFBKeyword(baseType.ToString());
                AddHeaderForType(baseType, includeText);
                // don't need to use forwarded types for this since it'll be available in full
                forwardDecls.ConcreteDeclaration(baseType);
            }
            // any forward declararations  need to go before the interface definition
            // so we buffer the interface definition rather than dumping it straight into content
            StringBuilder allForwards = new StringBuilder();
            StringBuilder ifaceDef = new StringBuilder();
            ifaceDef.AppendFormat("Type {0} extends {1}{2}", ifaceName, baseName, nl);
            foreach (FunctionType fn in iface.Fields)
            {
                FunctionArgType retType = fn.ReturnType;
                List<FunctionArgType> args = fn.Arguments;
                bool isFunction = IsFunctionFromReturnType(retType);
                string mangledFnName = MangleFBKeyword(fn.Name);
                ifaceDef.AppendFormat("{0}Declare Abstract {1} {2} Overload (", INDENT, (isFunction ? "Function" : "Sub"), mangledFnName);
                if (fn.Arguments.Count > 0)
                {
                    ifaceDef.AppendLine("_");
                }
                ArgListInformation argInfo = GatherArgList(args, INDENT + INDENT);
                includeText.Append(argInfo.headers.ToString());
                ifaceDef.AppendFormat("{0}{1}) ", argInfo.argListOutput.ToString(), INDENT);
                allForwards.Append(argInfo.forwardDeclares.ToString());
                if (isFunction)
                {
                    string retTypeString = GetInterfaceFunctionReturnType(allForwards, retType);
                    OutputInterfaceFunctionReturnType(ifaceDef, retTypeString, retType.CustomAttributes);
                    // if there are any out only parameters, and this function just returns a status
                    // create a nice wrapper function that will turn the last output into a return value
                    // 
                    if (options.niceWrappers)
                    {
                        int whichArg = HasOutputThatIsntForwarded(argInfo);
                        int skipOptionalGeneration = -1;
                        if ((whichArg >= 0) && (retType.ParamType.ToString() == "HRESULT"))
                        {
                            OutputNiceOutputReturnWrapper(ifaceDef, niceWrapperContent, ifaceName, mangledFnName, args, argInfo, whichArg, typeRegistry);
                            skipOptionalGeneration = ShouldSkipOptionalWrap(whichArg, args);
                        }
                        // if the interface has its own overloads of this function, don't generate ours
                        // since they'll likely collide/cause ambiguous calls (ie they do, see graphics.directwrite - IDWriteFontSet1->GetFilteredFonts)
                        if (!InterfaceHasItsOwnOverloadsOf(iface.Fields, mangledFnName))
                        {
                            OutputOptionalFunctionParamLadder(argInfo, ifaceDef, niceWrapperContent, isFunction, ifaceName, mangledFnName, retTypeString, skipOptionalGeneration);
                        }
                    }
                }
                else
                {
                    ifaceDef.AppendLine();
                }
            }
            ifaceDef.AppendFormat("End Type{0}{0}{1}", nl, niceWrapperContent.ToString());
            content.AppendLine(includeText.ToString());
            using (IfDefGuard guard = new IfDefGuard(content, archIfDef))
            {
                content.AppendLine(allForwards.ToString());
                content.AppendLine(ifaceDef.ToString());
                if (attrVals.ansiApi)
                {
                    string neutralName = ifaceName.Remove(ifaceName.Length - 1);
                    if (typeRegistry.LookupInterface(ns.Name, neutralName) == null)
                    {
                        outputStreams.AnsiDefs.AppendFormat("Type {0} As {1}{2}", neutralName, ifaceName, nl);
                    }
                }
                else if (attrVals.unicodeApi)
                {
                    string neutralName = ifaceName.Remove(ifaceName.Length - 1);
                    if (typeRegistry.LookupInterface(ns.Name, neutralName) == null)
                    {
                        outputStreams.UnicodeDefs.AppendFormat("Type {0} As {1}{2}", neutralName, ifaceName, nl);
                    }
                }
                // this is now defined, no need for forward decls now
                forwardDecls.ConcreteDeclaration(SignatureTypeProvider.CreateNameOnlyTypeInfo(ns.Name, ifaceName));
            }
        }

        // these parameters are already mangled
        private void OutputNiceOutputReturnWrapper(StringBuilder content, StringBuilder wrapperContent, string ifaceName, string funName, List<FunctionArgType> args, ArgListInformation argListInfo, int whichArg, GlobalTypeRegistry typedefs)
        {
            List<string> noRetParamSpecs = new List<string>(argListInfo.parameterDefs);
            noRetParamSpecs.RemoveAt(whichArg);
            string paramSpecList = String.Join(", ", noRetParamSpecs);

            // if this was an out parameter, it had to be a pointer
            // as a return value it needs that pointer bit chopping off, so that's what we're doing here
            FunctionArgType newReturnType = args[whichArg];
            SimpleTypeHandleInfo returnType = PtrifyInterfaceType(newReturnType.ParamType);
            ForwardDeclarationManager.ForwardDeclaration? paramFwdDecType = GetForwardDeclarationType(returnType);
            if (paramFwdDecType.HasValue)
            {
                returnType = paramFwdDecType.Value.Equivalent;
            }
            PointerTypeHandleInfo pti = returnType as PointerTypeHandleInfo;
            if (pti == null)
            {
                Debug.WriteLine(String.Format("Output parameter {0}/{1} on interface method {2}.{3} wasn't a pointer!?", newReturnType.Name, returnType, ifaceName, funName));
                return;
            }
            SimpleTypeHandleInfo nonPtrType = pti.NakedType;
            bool hasIfaceName = !String.IsNullOrEmpty(ifaceName);
            if (hasIfaceName)
            {
                content.AppendFormat("{0}Declare Function {1} Overload ({2}) As {3}{4}{4}", INDENT, funName, paramSpecList, nonPtrType, nl);
            }

            string outputArgName = argListInfo.argNames[whichArg];
            List<string> funCallArgList = new List<string>(argListInfo.argNames);
            // turn the output parameter name into a pointer to pass the local
            funCallArgList[whichArg] = "@" + funCallArgList[whichArg];
            if (hasIfaceName)
            {
                wrapperContent.AppendFormat(
                    "Private Function {0}.{1} ({2}) As {3}{4}" +
                    "{5}Dim {6} As {3}{4}" +
                    "{5}Dim __hr As HRESULT = (@This)->{1}({7}){4}" +
                    "#If __INTERFACE_DEBUG{4}" +
                    "{5}If __hr < 0 Then{4}" +
                    "{5}{5}Print Using \"Interface call &.& failed with hr = &\"; \"{0}\"; \"{1}\"; Hex(__hr){4}" +
                    "{5}End If{4}" +
                    "#Endif{4}" +
                    "{5}Return {6}{4}" +
                    "End Function{4}{4}",
                    ifaceName,
                    funName,
                    paramSpecList,
                    nonPtrType,
                    nl,
                    INDENT,
                    outputArgName,
                    String.Join(", ", funCallArgList)
                );
            }
            else
            {
                wrapperContent.AppendFormat(
                    "Private Function {0} Overload ({2}) As {3}{4}" +
                    "{5}Dim {6} As {3}{4}" +
                    "{5}Dim __hr As HRESULT = {0}({7}){4}" +
                    "#If __INTERFACE_DEBUG{4}" +
                    "{5}If __hr < 0 Then{4}" +
                    "{5}{5}Print Using \"Function & failed with hr = &\"; \"{0}\"; Hex(__hr){4}" +
                    "{5}End If{4}" +
                    "#Endif{4}" +
                    "{5}Return {6}{4}" +
                    "End Function{4}{4}",
                    funName,
                    null,
                    paramSpecList,
                    nonPtrType,
                    nl,
                    INDENT,
                    outputArgName,
                    String.Join(", ", funCallArgList)
                );
            }
        }

        struct ArgListInformation
        {
            public int parameters;
            public List<string> parameterDefs;
            public List<string> argNames;
            public int inParameters;
            public int inOutParameters;
            public int outParameters;
            public int lastOutputParameter;
            public int outputParameterBitfield;
            public int inputParameterBitfield;
            public int inOutParamBitfield;
            public int optionalParamBitfield;
            public int forwardedParamTypeBitfield;
            public int outputWrapIneligibleBitfield;
            public StringBuilder argListOutput;
            public StringBuilder forwardDeclares;
            public StringBuilder headers;

            public ArgListInformation(int b)
            {
                parameterDefs = new List<string>();
                argNames = new List<string>();
                parameters = inParameters = outParameters = inOutParameters = optionalParamBitfield = forwardedParamTypeBitfield = 0;
                outputParameterBitfield = inputParameterBitfield = inOutParamBitfield = outputWrapIneligibleBitfield = 0;
                lastOutputParameter = -1;
                argListOutput = new StringBuilder();
                forwardDeclares = new StringBuilder();
                headers = new StringBuilder();
            }
        }

        private ArgListInformation GatherArgList(List<FunctionArgType> args, string listItemIndent)
        {
            ArgListInformation argInfo = new ArgListInformation(0);
            int paramNum = 0;
            int numArgs = args.Count;
            foreach (FunctionArgType arg in args)
            {
                ParameterAttributes pAttrs = arg.Attributes;
                CustomAttributeValues pCustomAttrs = arg.CustomAttributes;
                string sizeField = String.Empty;
                string typeQualifiers = String.Empty;
                bool reserved = false;
                int paramBit = 1 << paramNum;
                if (pCustomAttrs != null)
                {
                    if (pCustomAttrs.isConst)
                    {
                        typeQualifiers = "Const ";
                    }
                    if (pCustomAttrs.sizeField.HasValue)
                    {
                        ArrayMemorySizeAttribute sizeAttr = pCustomAttrs.sizeField.Value;
                        sizeField = String.Format(
                            ", the size of this parameter is {0}",
                            sizeAttr.which == ArrayMemorySizeAttribute.FieldToUse.Constant ?
                                String.Format("{0} bytes", sizeAttr.details.Item1) :
                                String.Format("given in '{0}'", args[sizeAttr.details.Item1].Name)
                        );
                    }
                    reserved = pCustomAttrs.reserved;
                }
                SimpleTypeHandleInfo argParamType = arg.ParamType;
                if (((pAttrs & ParameterAttributes.Optional) != 0) || reserved)
                {
                    int flagOn = (1 << paramNum);
                    // if this is an enum, we can't just dump 0 into the parameter in the overloads we generate
                    // because if 0 isn't a value in the enum, FB will complain of no matching overloads
                    if (!String.IsNullOrEmpty(argParamType.IncludeFile))
                    {
                        HandleTypeHandleInfo hType = argParamType as HandleTypeHandleInfo;
                        if ((hType != null) && (typeRegistry.LookupEnum(argParamType.IncludeFile, hType.ActualName) != null))
                        {
                            flagOn = 0;
                        }
                    }
                    argInfo.optionalParamBitfield |= flagOn;
                }
                SimpleTypeHandleInfo paramType = PtrifyInterfaceType(argParamType);
                ForwardDeclarationManager.ForwardDeclaration? paramFwdDecType = GetForwardDeclarationType(paramType);
                SimpleTypeHandleInfo argListType = paramType;
                if (paramFwdDecType.HasValue)
                {
                    argListType = paramFwdDecType.Value.Equivalent;
                    forwardDecls.FormatAndAdd(
                        argInfo.forwardDeclares,
                        PointerTypeHandleInfo.StripAllPointers(argParamType).Stripped,
                        paramFwdDecType.Value.BaseType
                    );
                    argInfo.forwardedParamTypeBitfield |= paramBit;
                    // can only output a wrap for a forwarded type if it has two or more levels of pointer
                    PtrStripResult forwardStrip = PointerTypeHandleInfo.StripAllPointers(argListType);
                    if (forwardStrip.PtrLevels <= 1)
                    {
                        argInfo.outputWrapIneligibleBitfield |= paramBit;
                    }
                }
                else
                {
                    AddHeaderForType(paramType, argInfo.headers);
                    // for concrete types, disallow single pointers that are any ptr (ie void*) or string ptrs
                    PtrStripResult paramStrip = PointerTypeHandleInfo.StripAllPointers(paramType);
                    if (
                        ((paramStrip.Stripped.TypeInfo == typeof(FBTypes.Any)) || BasicFBTypes.IsStringPointer(paramStrip.Stripped.TypeInfo)) && 
                        (paramStrip.PtrLevels <= 1)
                    )
                    {
                        argInfo.outputWrapIneligibleBitfield |= paramBit;
                    }
                }
                ParameterAttributes inOutStatus = (pAttrs & (ParameterAttributes.In | ParameterAttributes.Out));
                if (inOutStatus == (ParameterAttributes.In | ParameterAttributes.Out))
                {
                    ++argInfo.inOutParameters;
                    argInfo.inOutParamBitfield |= paramBit;
                }
                else if (inOutStatus == ParameterAttributes.In)
                {
                    ++argInfo.inParameters;
                    argInfo.inputParameterBitfield |= paramBit;
                }
                else if (inOutStatus == ParameterAttributes.Out)
                {
                    ++argInfo.outParameters;
                    if ((pCustomAttrs == null) || !pCustomAttrs.sizeField.HasValue)
                    {
                        argInfo.outputParameterBitfield |= paramBit;
                        argInfo.lastOutputParameter = paramNum;
                    }
                }
                // some parameters have the same name as enum values
                // in C# these have different namespaces so no problem.
                // In FB enums are global and it causes problems
                string paramDef = String.Format("ByVal {0}_ as {1}{2}", MangleFBKeyword(arg.Name), typeQualifiers, MangleFBKeyword(argListType.ToString()));
                argInfo.parameterDefs.Add(paramDef);
                argInfo.argNames.Add(MangleFBKeyword(arg.Name) + "_");
                argInfo.argListOutput.AppendFormat(
                    "{0}{1}{2} _ '' {3}{4}{5}",
                    listItemIndent,
                    paramDef,
                    (paramNum != (numArgs - 1)) ? ", " : String.Empty,
                    FormatParamAttributeString(pAttrs),
                    sizeField,
                    nl
                );
                ++paramNum;
            }
            argInfo.parameters = paramNum;
            return argInfo;
        }

        //private ArgListInformation OutputArgList(StringBuilder content, List<FunctionArgType> args, string listItemIndent)
        //{
        //    ArgListInformation argInfo = new ArgListInformation(0);
        //    int paramNum = 0;
        //    int numArgs = args.Count;
        //    foreach (FunctionArgType arg in args)
        //    {
        //        ParameterAttributes pAttrs = arg.Attributes;
        //        CustomAttributeValues pCustomAttrs = arg.CustomAttributes;
        //        string sizeField = String.Empty;
        //        string typeQualifiers = String.Empty;
        //        bool reserved = false;
        //        if(pCustomAttrs != null)
        //        {
        //            if(pCustomAttrs.isConst)
        //            {
        //                typeQualifiers = "Const ";
        //            }
        //            if (pCustomAttrs.sizeField.HasValue)
        //            {
        //                ArrayMemorySizeAttribute sizeAttr = pCustomAttrs.sizeField.Value;
        //                sizeField = String.Format(
        //                    ", the size of this parameter is {0}",
        //                    sizeAttr.which == ArrayMemorySizeAttribute.FieldToUse.Constant ? 
        //                        String.Format("{0} bytes", sizeAttr.details.Item1) : 
        //                        String.Format("given in '{0}'", args[sizeAttr.details.Item1].Name)
        //                );
        //            }
        //            reserved = pCustomAttrs.reserved;
        //        }
        //        if (((pAttrs & ParameterAttributes.Optional) != 0) || reserved)
        //        {
        //            argInfo.optionalParamBitfield |= 1 << paramNum;
        //        }
        //        ParameterAttributes inOutStatus = (pAttrs & (ParameterAttributes.In | ParameterAttributes.Out));
        //        if (inOutStatus == (ParameterAttributes.In | ParameterAttributes.Out))
        //        {
        //            ++argInfo.inOutParameters;
        //        }
        //        else if(inOutStatus == ParameterAttributes.In)
        //        {
        //            ++argInfo.inParameters;
        //        }
        //        else if(inOutStatus == ParameterAttributes.Out)
        //        {
        //            ++argInfo.outParameters;
        //            if ((pCustomAttrs == null) || !pCustomAttrs.sizeField.HasValue)
        //            {
        //                argInfo.lastOutputParameter = paramNum;
        //            }
        //        }
        //        SimpleTypeHandleInfo paramType = PtrifyInterfaceType(arg.ParamType);
        //        // some parameters have the same name as enum values
        //        // in C# these have different namespaces so no problem.
        //        // In FB enums are global and it causes problems
        //        string paramDef = String.Format("ByVal {0}_ as {1}{2}", MangleFBKeyword(arg.Name), typeQualifiers, MangleFBKeyword(paramType.ToString()));
        //        argInfo.parameterDefs.Add(paramDef);
        //        argInfo.argNames.Add(MangleFBKeyword(arg.Name) + "_");
        //        content.AppendFormat(
        //            "{0}{1}{2} _ '' {3}{4}{5}",
        //            listItemIndent, 
        //            paramDef,
        //            (paramNum != (numArgs - 1)) ? "," : String.Empty,
        //            FormatParamAttributeString(pAttrs),
        //            sizeField,
        //            nl
        //        );
        //        if (!String.IsNullOrEmpty(paramType.IncludeFile))
        //        {
        //            headers.Add(paramType.IncludeFile);
        //        }
        //        ++paramNum;
        //    }
        //    argInfo.parameters = paramNum;
        //    return argInfo;
        //}

        private string GetInterfaceFunctionReturnType(StringBuilder forwardDeclares, FunctionArgType fnRetType)
        {
            SimpleTypeHandleInfo retType = PtrifyInterfaceType(fnRetType.ParamType);
            ForwardDeclarationManager.ForwardDeclaration? fwdRetType = GetForwardDeclarationType(retType);
            if (fwdRetType.HasValue)
            {
                retType = fwdRetType.Value.Equivalent;
                forwardDecls.FormatAndAdd(
                    forwardDeclares,
                    PointerTypeHandleInfo.StripAllPointers(fnRetType.ParamType).Stripped,
                    fwdRetType.Value.BaseType
                );
            }
            else
            {
                AddHeaderForType(retType, forwardDeclares);
            }
            Debug.Assert(IsFunctionFromReturnType(fnRetType));
            return MangleFBKeyword(retType.ToString());
        }

        private void OutputInterfaceFunctionReturnType(StringBuilder content, string retType, CustomAttributeValues retAttrs)
        {
            content.AppendFormat("As {0}", retType);
            StringBuilder attrString = new StringBuilder();
            if (retAttrs != null)
            {
                if (!String.IsNullOrEmpty(retAttrs.docUrl))
                {
                    attrString.AppendFormat(" documentation at {0}, ", retAttrs.docUrl);
                }
                if (retAttrs.dontFreeValue)
                {
                    attrString.Append(" do not free this value, ");
                }
                if (attrString.Length > 0)
                {
                    attrString.Length -= 2;
                }
            }
            content.AppendFormat(
                "{0}{1}{1}",
                attrString.Length > 0 ? "'' " + attrString.ToString() : String.Empty,
                nl
            );
        }

        private void OutputGuidFile(string guidDir, string fileGuidName, Guid guid)
        {
            using (StreamWriter sw = new StreamWriter(Path.Combine(guidDir, fileGuidName + ".bas")))
            {
                string guidStr = guid.ToString("X").Replace("0x", "&h");
                sw.WriteLine(
                    "Type GUID{0}" +
                    "{1}a as ULong{0}" +
                    "{1}b as UShort{0}" +
                    "{1}c as UShort{0}" +
                    "{1}d(0 to 7) as UByte{0}" +
                    "End Type{0}" +
                    "{0}" +
                    "Extern \"Windows\"{0}" +
                    "Extern {2} As Const GUID{0}" +
                    "Static Shared {2} As Const GUID = ({3}){0}" +
                    "End Extern{0}",
                    nl,
                    INDENT,
                    fileGuidName,
                    // remove the opening and closing {} surrounding the value
                    guidStr.Substring(1, guidStr.Length - 2)
                );
            }
        }

        private void OutputPropertyKey(string pkeyDir, string typeName, string constName, string constantVal, StringBuilder declOutput)
        {
            using (StreamWriter sw = new StreamWriter(Path.Combine(pkeyDir, constName + ".bas")))
            {
                sw.WriteLine(
                    "Type GUID{0}" +
                    "{1}a as ULong{0}" +
                    "{1}b as UShort{0}" +
                    "{1}c as UShort{0}" +
                    "{1}d(0 to 7) as UByte{0}" +
                    "End Type{0}" +
                    "{0}" +
                    "Type {4}{0}" +
                    "{1}fmtId As GUID{0}" +
                    "{1}pid As ULong{0}" +
                    "End Type{0}" +
                    "{0}" +
                    "Extern \"Windows\"{0}" +
                    "Extern {2} As Const {4}{0}" +
                    "Static Shared {2} As Const {4} = ({3}){0}" +
                    "End Extern{0}",
                    nl,
                    INDENT,
                    constName,
                    // remove the opening and closing {} surrounding the value
                    constantVal,
                    typeName
                );
            }
            declOutput.AppendFormat("Extern {0} As Const {1}{2}", constName, typeName, nl);
        }

        private void PrintTypeCommentPreamble(StringBuilder sb, string name, CustomAttributeValues attrVals)
        {
            if (attrVals == null) return;
            if (!String.IsNullOrEmpty(attrVals.docUrl))
            {
                sb.AppendFormat("'' {0} documentation at {1}{2}", name, attrVals.docUrl, nl);
            }
            if (attrVals.obsolete)
            {
                sb.AppendFormat("'' {0} is obsolete, consider alternatives{1}", name, nl);
            }
        }

        private void OutputWindowsHandle(StringBuilder content, string handleTypeName)
        {
            string handleTypedef = handleTypeName + HANDLE_TYPE_SUFFIX;
            content.AppendFormat(
                "Type {3}{1}" +
                "{2}unused as Long{1}" +
                "End Type{1}" +
                "Type {0} As {3} Ptr{1}",
                handleTypeName,
                nl,
                INDENT,
                handleTypedef
            );
        }

        private string MangleFBKeyword(string name)
        {
            return fbKeywords.Contains(name.ToLowerInvariant()) ? FB_KEYWORD_PREFIX + name : name;
        }

        // interfaces aren't pointers in C#, so the metadata doesn't have interface parameters, returns, or members labelled as pointers
        // This does that
        private SimpleTypeHandleInfo PtrifyInterfaceType(SimpleTypeHandleInfo typeInfo)
        {
            ArrayTypeHandleInfo arrInf = typeInfo as ArrayTypeHandleInfo;
            SimpleTypeHandleInfo typeInfToUse = (arrInf != null) ? arrInf.DataType : typeInfo;
            PtrStripResult stripResult = PointerTypeHandleInfo.StripAllPointers(typeInfToUse);
            SimpleTypeHandleInfo pointersStripped = stripResult.Stripped;
            HandleTypeHandleInfo hName = pointersStripped as HandleTypeHandleInfo;
            if (hName == null) return typeInfo;

            if (typeRegistry.LookupInterface(hName.IncludeFile, hName.ActualName) != null)
            {
                PointerTypeHandleInfo pTypeInfToUse = new PointerTypeHandleInfo(typeInfToUse);
                return (arrInf != null) ? new ArrayTypeHandleInfo(pTypeInfToUse, arrInf.Bounds) : pTypeInfToUse;
            }
            else
            {
                return typeInfo;
            }
        }

        struct BracePosition
        {
            //  which element of the constant string this applies to
            public int Position { get; init; }
            // whether this should be an open brace before the element at Position {
            // or a closing brace } after it
            public bool Open { get; init; }

            public BracePosition(int num, bool isOpen)
            {
                Position = num;
                Open = isOpen;
            }

#if DEBUG
            public override string ToString()
            {
                return String.Format("Pos {0}, {1}", Position, Open ? "{" : "}");
            }
#endif

        }

        private void ParseTypeForBraces(StructType<VarType> type, List<BracePosition> braceLoc, ref int runningPositionIterator)
        {
            //braceLoc.Add(new BracePosition(runningPositionIterator, true));
            foreach (VarType t in type.Fields)
            {
                SimpleTypeHandleInfo tTypeInf = t.ParamType;
                if (tTypeInf is ArrayTypeHandleInfo)
                {
                    ArrayTypeHandleInfo tArray = (ArrayTypeHandleInfo)tTypeInf;
                    SimpleTypeHandleInfo arrayType = GetRealType(tArray.DataType, typeRegistry) ?? tArray.DataType;
                    braceLoc.Add(new BracePosition(runningPositionIterator, true));
                    // I'm being lazy here but think this is ever the case
                    Debug.Assert(tArray.Bounds.Rank == 1, "Not built for multilevel arrays in a constant");
                    Debug.Assert(BasicFBTypes.IsPrimitive(arrayType.TypeInfo), "Not build for arrays of structs in a constant");
                    int numElems = tArray.Bounds.Sizes[0] - 1;
                    runningPositionIterator += numElems;
                    braceLoc.Add(new BracePosition(runningPositionIterator, false));
                }
                else if (tTypeInf is HandleTypeHandleInfo)
                {
                    HandleTypeHandleInfo hType = (HandleTypeHandleInfo)tTypeInf;
                    StructType<VarType> nestedStruct = typeRegistry.LookupStruct(hType.IncludeFile, hType.ActualName);
                    if (nestedStruct != null)
                    {
                        // dont increment the iterator here, the first type of the nested struct might be an array
                        // or another struct in which case we'll need consecutive braces
                        ParseTypeForBraces(nestedStruct, braceLoc, ref runningPositionIterator);
                    }
                }
                else
                {
                    ++runningPositionIterator;
                }
            }
            //braceLoc.Add(new BracePosition(runningPositionIterator, false));
        }

        // all struct initializers in the metadata are in the normal C format (ie enclosed by {})
        // That's great, except Freebasic doesn't use {} to initialise a type, it uses, er, Type()
        // except, except, if the member being initialised is an array, or nested struct then it uses {} inside the Type()
        // so what this does is go through each member in the type of the constant
        // and figures out where the brackets should be. Then it strips all brackets from the C constant string
        // in the metadata and reconstitutes it with fixed brackets
        private string FixConstantValue(string constantVal, SimpleTypeHandleInfo type)
        {
            constantVal = constantVal.Replace("0x", "&h").Replace("0X", "&h").Replace("{", String.Empty).Replace("}", String.Empty);
            HandleTypeHandleInfo hType = type as HandleTypeHandleInfo;
            Debug.Assert(hType != null);
            StructType<VarType> dataStruct = typeRegistry.LookupStruct(hType.IncludeFile, hType.ActualName);
            List<BracePosition> bracePositions = new List<BracePosition>();
            int positionIterator = 0;
            ParseTypeForBraces(dataStruct, bracePositions, ref positionIterator);
            // freebasic doesn't need braces at the start and the end if they the only ones
            // so just return the modified constantval
            if ((bracePositions.Count == 2) && (bracePositions[0].Position == 0) && (bracePositions[1].Position == constantVal.Length - 1))
            {
                return constantVal;
            }
            //bracePositions.RemoveAt(0);
            //bracePositions.RemoveAt(bracePositions.Count - 1);
            bracePositions.Add(new BracePosition(Int32.MaxValue, true));
            string[] initBits = constantVal.Split(',');
            StringBuilder newConstant = new StringBuilder();
            int numInitBits = initBits.Length;
            int braceIter = 0;
            BracePosition nextBrace = bracePositions[0];
            const string openBrace = "{";
            const string closeBrace = "}";
            for (int i = 0; i < numInitBits; ++i)
            {
                string thisValue = initBits[i];
                while (i == nextBrace.Position)
                {
                    if (nextBrace.Open) thisValue = openBrace + thisValue;
                    else thisValue += closeBrace;
                    nextBrace = bracePositions[++braceIter];
                }
                newConstant.AppendFormat("{0},", thisValue);
            }
            newConstant.Length -= 1;
            return newConstant.ToString();
        }

        private ForwardDeclarationManager.ForwardDeclaration? GetForwardDeclarationType(SimpleTypeHandleInfo type)
        {
            // if their header is already included, no need to forward
            // likewise if this is in the same file/namespace, the collection process has already ordered them
            // so that dependencies come first
            if (headers.Contains(type.IncludeFile) || ((type.IncludeFile == ns.Name) && !ForceForwardDecls))
            {
                return null;
            }

            bool doForward = false;
            ArrayTypeHandleInfo arrType = type as ArrayTypeHandleInfo;
            SimpleTypeHandleInfo typeToForward = (arrType != null) ? arrType.DataType : type;
            PtrStripResult stripInfo = PointerTypeHandleInfo.StripAllPointers(typeToForward);
            SimpleTypeHandleInfo strippedType = stripInfo.Stripped;
            ForwardDeclarationManager.ForwardedType? existingForward = forwardDecls.Lookup(strippedType);
            ForwardDeclarationManager.ForwardDeclaration? forDecl = null;
            if (existingForward != null)
            {
                doForward = true;
            }
            else
            {
                // handle types
                // these are already typedefs of pointers, but of course, without the include
                // Freebasic won't know that. So we duplicate their typedef, instead of
                // type HANDLE_fwd_ as HANDLE
                // we're aiming to produce
                // type HANDLE as HANDLE__ ptr
                // this allows HANDLE to be used as a member or parameter without
                // the full definition
                if (IsWinHandleType(strippedType.ToString(), strippedType, typeRegistry))
                {
                    string handleTypedef = strippedType.ToString() + HANDLE_TYPE_SUFFIX;
                    SimpleTypeHandleInfo ptredForward;
                    SignatureTypeProvider.CreateBaseAndPointerTypes(
                        strippedType,
                        handleTypedef,
                        1,
                        out ptredForward
                    );
                    return forwardDecls.CreateDeclForHandle(type, ptredForward);
                }
                else
                {
                    // check for interfaces
                    HandleTypeHandleInfo hInfo = strippedType as HandleTypeHandleInfo;
                    if ((hInfo != null))
                    {
                        if (typeRegistry.LookupInterface(hInfo.IncludeFile, hInfo.ActualName) != null)
                        {
                            doForward = true;
                        }
                    }
                    if (!doForward)
                    {
                        // 'type' could be a typedef itself, 
                        SimpleTypeHandleInfo realType = strippedType.GetRealType(typeRegistry);
                        PtrStripResult realStripped = PointerTypeHandleInfo.StripAllPointers(realType);
                        // if type is a typedef for some level of pointer, we can just emit the typedef again
                        if (realType != null)
                        {
                            if (realStripped.PtrLevels > 0 && stripInfo.PtrLevels == 0)
                            {
                                // we need the forward decl to be
                                // realtype as type
                                // the handle decl will make it output that way even though this isn't a handle
                                forDecl = forwardDecls.CreateDeclForHandle(type, realType);
                            }
                            // pointed at types can be forward declared, but don't need to be if they are primitive types
                            else if (realStripped.Stripped != realType)
                            {
                                doForward = (!BasicFBTypes.IsType(realStripped.Stripped.TypeInfo)) || (headers.Contains(realStripped.Stripped.IncludeFile));
                            }
                        }
                        // this is a pointer to a non-typedef type, we can do this
                        else if (stripInfo.PtrLevels > 0)
                        {
                            doForward = true;
                        }
                    }
                }
            }
            if (doForward)
            {
                if (stripInfo.PtrLevels > 0)
                {
                    SimpleTypeHandleInfo ptredForward;
                    SimpleTypeHandleInfo forwardedBase = SignatureTypeProvider.CreateBaseAndPointerTypes(
                        strippedType,
                        (existingForward != null) ? existingForward.Value.BaseType.ToString() : strippedType + FORWARD_DECLARE_SUFFIX,
                        stripInfo.PtrLevels,
                        out ptredForward
                    );
                    if ((existingForward) != null && existingForward.Value.IsHandle)
                    {
                        forDecl = forwardDecls.CreateDeclForHandle(type, ptredForward);
                    }
                    else
                    {
                        forDecl = forwardDecls.CreateDecl(ptredForward, forwardedBase);
                    }
                }
                else if ((existingForward != null) && existingForward.Value.IsHandle)
                {
                    forDecl = forwardDecls.CreateDeclForHandle(type, existingForward.Value.BaseType);
                }
            }
            return forDecl;
        }

        private void AddHeaderForType(SimpleTypeHandleInfo type, StringBuilder headerOutput)
        {
            string incFile = type.IncludeFile;
            if (!String.IsNullOrEmpty(incFile))
            {
                if ((incFile != ns.Name) && headers.Add(incFile))
                {
                    headerOutput.AppendFormat(HEADER_INSERT_FORMAT, nl, incFile.Remove(0, NSPrefixLength));
                }
            }
        }

        private PtrStripResult FindLowestRealType(FunctionArgType argType)
        {
            PtrStripResult paramStrip = PointerTypeHandleInfo.StripAllPointers(argType.ParamType);
            SimpleTypeHandleInfo preParamType = paramStrip.Stripped;
            SimpleTypeHandleInfo preParamRealType = GetRealType(preParamType, typeRegistry);
            if (preParamRealType == null)
            {
                if (preParamType is HandleTypeHandleInfo)
                {
                    HandleTypeHandleInfo hType = (HandleTypeHandleInfo)preParamType;
                    StructType<ConstantValue> enumType = typeRegistry.LookupEnum(hType.IncludeFile, hType.ActualName);
                    if (enumType != null)
                    {
                        List<ConstantValue> enumVals = enumType.Fields;
                        enumVals.Sort();
                        preParamRealType =
                            (enumVals[enumVals.Count - 1].varValue.Value.ValueByteLength > 4) ?
                            new PrimitiveTypeHandleInfo(System.Reflection.Metadata.PrimitiveTypeCode.UInt64) :
                            new PrimitiveTypeHandleInfo(System.Reflection.Metadata.PrimitiveTypeCode.UInt32);
                    }
                }
                if(preParamRealType == null)
                {
                    preParamRealType = preParamType;
                }
            }
            return new PtrStripResult(preParamRealType, paramStrip.PtrLevels);
        }

        // check if the centreArg (which has been removed from the parameter list to wrap as a retirn value)
        // has the same type as the next or previous parameter and if that parameter is optional.
        // If so, skip generation of the optional wrap that removes that arg
        // as the function signatures for that optional wrap and the return value wrap of centreArg
        // will be the same (except for the return type) and that isn't allowed.
        private int ShouldSkipOptionalWrap(int centreArg, List<FunctionArgType> argList)
        {
            int whichToSkip = 0;
            FunctionArgType centreParamDef = argList[centreArg];
            PtrStripResult centreStrip = FindLowestRealType(centreParamDef);
            string centreStrippedType = centreStrip.Stripped.ToString();
            if (centreArg > 0)
            {
                FunctionArgType preParamDef = argList[centreArg - 1];
                PtrStripResult preParamStrip = FindLowestRealType(preParamDef);
                if ((centreStrippedType == preParamStrip.Stripped.ToString()) && (centreStrip.PtrLevels == preParamStrip.PtrLevels))
                {
                    whichToSkip |= (1 << (centreArg - 1));
                }
            }
            int additionalIter = 1;
            while ((centreArg + additionalIter) <= (argList.Count - 1))
            {
                FunctionArgType postParamDef = argList[centreArg + additionalIter];
                PtrStripResult postParamStrip = FindLowestRealType(postParamDef);
                if ((centreStrippedType == postParamStrip.Stripped.ToString()) && (centreStrip.PtrLevels == postParamStrip.PtrLevels))
                {
                    whichToSkip |= (1 << (centreArg + additionalIter));
                }
                else break;
                ++additionalIter;
            }
            return whichToSkip;
        }

        private bool InterfaceHasItsOwnOverloadsOf(List<FunctionType> functions, string fnName)
        {
            int countOf = 0;
            foreach(FunctionType f in functions)
            {
                countOf += ((MangleFBKeyword(f.Name) == fnName) ? 1 : 0);
            }
            return countOf > 1;
        }

        static private string FormatMemberAttributeString(CustomAttributeValues attrVals, string name)
        {
            string attrString = String.Empty;
            if (attrVals.nonNullTerm)
            {
                attrString += String.Format("{0} may not be null terminated, ", name);
            }
            if (attrVals.doubleNullTerm)
            {
                attrString += String.Format("{0} must be double null terminated, ", name);
            }
            if (attrVals.reserved)
            {
                attrString += String.Format("{0} is reserved, don't change it, ", name);
            }
            if (attrVals.flexibleArray)
            {
                attrString += String.Format("{0} is a variable sized array, this isn't the full size, ", name);
            }
            if (attrString.Length > 0)
            {
                // chop off the last comma + space
                attrString = attrString.Remove(attrString.Length - 2);
            }
            return attrString;
        }

        static private string FormatParamAttributeString(ParameterAttributes attrs)
        {
            string attrString = String.Empty;
            if ((attrs & (ParameterAttributes.In | ParameterAttributes.Out)) == (ParameterAttributes.In | ParameterAttributes.Out))
            {
                attrString += "In & Out, ";
            }
            else if ((attrs & ParameterAttributes.In) != 0)
            {
                attrString += " In, ";
            }
            else if ((attrs & ParameterAttributes.Out) != 0)
            {
                attrString += " Out, ";
            }
            if ((attrs & ParameterAttributes.Optional) != 0)
            {
                attrString += " Optional, ";
            }
            if (attrString.Length > 0)
            {
                attrString = attrString.Remove(attrString.Length - 2);
            }
            return attrString;
        }

        static private Version FixupVersion(Version origVer)
        {
            // Mostly this is a windows version number like 5.1.2600
            // but sometimes it's 8.0, which should really be 6.2
            // this fixes it to keep it consistent
            Version retVer = origVer;
            if (origVer.Major == 8)
            {
                // win 8 is 6.2, win 8.1 is 6.3
                retVer = new Version(6, (origVer.Minor == 0) ? 2 : 3);
            }
            return retVer;
        }

        // checks if the name begins with a capital H and the type info is void*
        //static private bool IsWinHandleType(string name, SimpleTypeHandleInfo typeInfo, GlobalTypeRegistry typedefs)
        //{
        //    bool isHandle = false;
        //    PointerTypeHandleInfo pti = typeInfo as PointerTypeHandleInfo;
        //    if(pti == null)
        //    {
        //        HandleTypeHandleInfo hType = typeInfo as HandleTypeHandleInfo;
        //        if (hType != null)
        //        {
        //            SimpleTypeHandleInfo realType = hType.GetRealType(typedefs);
        //            return IsWinHandleType(name, realType, typedefs);
        //        }
        //    }
        //    if((name[0] == 'H') && (pti != null))
        //    {
        //        SimpleTypeHandleInfo realType = pti.NakedType;
        //        isHandle = (realType is PrimitiveTypeHandleInfo) && (realType.TypeInfo == typeof(FBTypes.Any));
        //    }
        //    return isHandle;
        //}

        static private bool IsWinHandleType(string name, SimpleTypeHandleInfo typeInfo, GlobalTypeRegistry typedefs)
        {
            bool isHandle = false;
            PtrStripResult stripResult = PointerTypeHandleInfo.StripAllPointers(typeInfo);
            HandleTypeHandleInfo hType = stripResult.Stripped as HandleTypeHandleInfo;
            if (hType != null)
            {
                SimpleTypeHandleInfo realType = hType.GetRealType(typedefs);
                if (realType != null)
                {
                    return IsWinHandleType(name, realType, typedefs);
                }
            }

            if ((name[0] == 'H') && (stripResult.PtrLevels > 0))
            {
                SimpleTypeHandleInfo realType = stripResult.Stripped;
                isHandle = (realType is PrimitiveTypeHandleInfo) && (realType.TypeInfo == typeof(FBTypes.Any));
            }
            return isHandle;
        }

        static private bool IsFunctionFromReturnType(FunctionArgType retType)
        {
            bool isFunction = false;
            if (retType != null)
            {
                // Any is FB's surrogate for void, but if it's naked Any and not a pointer to it, then this is a sub, not a function
                isFunction = !((retType.ParamType is PrimitiveTypeHandleInfo) && (retType.ParamType.TypeInfo == typeof(FBTypes.Any)));
            }
            return isFunction;
        }

        static private bool IsValueType(SimpleTypeHandleInfo type1, GlobalTypeRegistry typedefs)
        {
            //bool vt = type1.SystemType || (BasicFBTypes.IsPrimitive(type1.TypeInfo) && type1 is PrimitiveTypeHandleInfo);
            bool vt = (BasicFBTypes.IsPrimitive(type1.TypeInfo) && type1 is PrimitiveTypeHandleInfo);
            if (!vt)
            {
                HandleTypeHandleInfo hTypeInfo = type1 as HandleTypeHandleInfo;
                if (hTypeInfo != null)
                {
                    SimpleTypeHandleInfo realInfo = typedefs.LookupHandle(hTypeInfo.IncludeFile, hTypeInfo.ActualName);
                    return IsValueType(realInfo, typedefs);
                }
                else
                {
                    PointerTypeHandleInfo pti = type1 as PointerTypeHandleInfo;
                    if (pti != null)
                    {
                        vt = BasicFBTypes.IsStringPointer(pti.NakedType.TypeInfo);
                    }
                }
            }
            return vt;
        }

        static private SimpleTypeHandleInfo IsStringType(SimpleTypeHandleInfo type1, GlobalTypeRegistry typedefs)
        {
            bool isString = BasicFBTypes.IsStringPointer(type1.TypeInfo);
            if (!isString)
            {
                SimpleTypeHandleInfo realType = GetRealType(type1, typedefs);
                if (realType != type1)
                {
                    return IsStringType(realType, typedefs);
                }
                type1 = null;
            }
            return type1;
        }

        static private bool IsStringArray(SimpleTypeHandleInfo arrayDataType, string memberName)
        {
            if (arrayDataType.ToString() == "CHAR" || (arrayDataType.TypeInfo == typeof(NonFBChar)))
            {
                return true;
            }
            else if (arrayDataType.TypeInfo == typeof(FBTypes.UByte))
            {
                string[] potentialStringNameContents = { "str", "sz" };
                memberName = memberName.ToLowerInvariant();
                foreach (string nameBit in potentialStringNameContents)
                {
                    if (memberName.Contains(nameBit))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        static private bool IsInnerType(SimpleTypeHandleInfo member, List<StructType<VarType>> nestedTypes)
        {
            if (nestedTypes == null) return false;
            HandleTypeHandleInfo hInf = member as HandleTypeHandleInfo;
            if (hInf == null)
            {
                // there can also be arrays of inner tyoes :-(
                ArrayTypeHandleInfo arrInf = member as ArrayTypeHandleInfo;
                return (arrInf != null) ? IsInnerType(arrInf.DataType, nestedTypes) : false;
            }
            return nestedTypes.Exists(
                (x) => {
                    return x.Name == hInf.ActualName;
                }
            );
        }

        static private SimpleTypeHandleInfo GetRealType(SimpleTypeHandleInfo curType, GlobalTypeRegistry typedefs)
        {
            HandleTypeHandleInfo hTypeInfo = curType as HandleTypeHandleInfo;
            if (hTypeInfo != null)
            {
                SimpleTypeHandleInfo realInfo = typedefs.LookupHandle(hTypeInfo.IncludeFile, hTypeInfo.ActualName);
                return GetRealType(realInfo, typedefs);
            }
            return curType;
        }

        static private string CallConvToName(CallingConvention conv)
        {
            switch (conv)
            {
                case CallingConvention.Winapi:
                case CallingConvention.StdCall: return "stdcall";
                case CallingConvention.Cdecl: return "cdecl";
                default:
                {
                    throw new NotImplementedException("Unexpected function calling convention");
                }
            }
        }

        static private string RenameAlreadyUsedName(string name, RawTypeEntries.ObjectType nameType, RawTypeEntries typeEntries)
        {
            // this name is also used by something else so needs even more mangling to avoid collisions
            // there are some constants (like in windows.win32.graphics.gdi) that have the same name as functions
            // like SETMITERLIMIT (this isn't allowed in FreeBasic and leads to errors)
            // upto windows.win32.system.ole from the bottom up
            RawTypeEntries.ObjectType retTypes;
            while ((retTypes = typeEntries.IsNameSomethingElse(name, nameType)) != 0)
            {
                name += "_";
            }
            // As well as colliding with bare function names, there can be <functionName>A & <functionName>W
            // which will cause a <functionName> macro to be produced... which can collide with the name
            if ((retTypes = typeEntries.IsNameSomethingElse(name + "A", nameType)) != 0)
            {
                name += "_";
            }
            if ((retTypes = typeEntries.IsNameSomethingElse(name + "W", nameType)) != 0)
            {
                name += "_";
            }
            return name;
        }

        static private string UniquifyName(string name, HashSet<string> usedEntries)
        {
            string newName = name;
            int suffixCount = 1;
            while (!usedEntries.Add(newName))
            {
                newName = name + suffixCount.ToString();
                ++suffixCount;
            }
            return newName;
        }

        static private int HasOutputThatIsntForwarded(ArgListInformation args)
        {
            int whichArg = -1;
            if (args.outParameters > 0)
            {
                int outputsNotForwardedBits = args.outputParameterBitfield & ~args.outputWrapIneligibleBitfield;
                // also disallow optional parameters
                int nonOptionalOutput = outputsNotForwardedBits & ~args.optionalParamBitfield;
                // if there's any left, use the last one#
                if (nonOptionalOutput > 0)
                {
                    whichArg = 0;
                    while ((nonOptionalOutput >>= 1) != 0)
                    {
                        ++whichArg;
                    }
                }
            }
            return whichArg;
        }
    }
}
