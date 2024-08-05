using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace MetadataParser
{
    internal struct BitfieldMember
    {
        public string name;
        public long offset;
        public long length;

        public BitfieldMember(string n, long o, long len)
        {
            name = n;
            offset = o;
            length = len;
        }
    }

    [Flags]
    enum SupportedArchitecture
    {
        None = 0,
        X86 = 1,
        X64 = 2,
        ARM64 = 4,
        All = X86 | X64 | ARM64
    }

    struct ArrayMemorySizeAttribute
    {
        public Tuple<int, string> details;
        public enum FieldToUse
        {
            Struct = 1,
            Arg = 2,
            Constant = 3
        }
        public FieldToUse which;

        public ArrayMemorySizeAttribute(int arg, FieldToUse whichOne)
        {
            which = whichOne;
            details = new Tuple<int, string>(arg, null);
        }

        public ArrayMemorySizeAttribute(string field)
        {
            which = FieldToUse.Struct;
            details = new Tuple<int, string>(0, field);
        }
    }

    internal class CustomAttributeValues
    {
        // these are for the attributes defined in Windows.Win32.Foundation.Metadatanamespace
        public bool isConst;
        public bool flexibleArray;
        public bool dontFreeValue;
        public bool ansiApi;
        public bool unicodeApi;
        public bool doubleNullTerm;
        public bool nonNullTerm;
        public bool reserved;
        public bool nativeTypedef;
        public bool metadataTypedef;
        public bool fnPtr;
        public bool obsolete;
        public bool retVal;
        public bool comOutPtr;
        public Guid? guid;
        public string docUrl;
        public string raiiFree;
        //public Version minimumOs;
        public SupportedArchitecture supportedArch;
        public string freeWith;
        public ArrayMemorySizeAttribute? sizeField;
        public string alsoUsableFor;
        public string constantValue;
        public string associatedEnum;
        public List<BitfieldMember> bitfields;
        public long[] invalidHandleValue;
        public DllImportAttribute dllImport;
    }

    class CustomAttributeParser
    {
        delegate void AttributeProcessor(CustomAttributeTypeProvider attrTypeProv, CustomAttribute attribute, CustomAttributeValues values);
        private Dictionary<string, AttributeProcessor> attributeParsers;

        private CustomAttributeParser(Dictionary<string, AttributeProcessor> attrParsers)
        {
            attributeParsers = attrParsers;
        }

        internal CustomAttributeValues ParseAttributes(MetadataReader metaReader, CustomAttributeTypeProvider attrTypeProv, CustomAttributeHandleCollection attributes)
        {
            CustomAttributeValues values = null;
            if (attributes.Count > 0)
            {
                values = new CustomAttributeValues();
                foreach (CustomAttributeHandle hAttr in attributes)
                {
                    CustomAttribute ca = metaReader.GetCustomAttribute(hAttr);
                    string name;
                    string attrNS;
                    if (!ca.Constructor.IsNil)
                    {
                        switch (ca.Constructor.Kind)
                        {
                            case HandleKind.MethodDefinition:
                            {
                                MethodDefinition methDef = metaReader.GetMethodDefinition((MethodDefinitionHandle)ca.Constructor);
                                TypeDefinition nsDef = metaReader.GetTypeDefinition(methDef.GetDeclaringType());
                                name = metaReader.GetString(nsDef.Name);
                                attrNS = metaReader.GetString(nsDef.Namespace).ToLowerInvariant();
                            }
                            break;
                            case HandleKind.MemberReference:
                            {
                                MemberReference memRef = metaReader.GetMemberReference((MemberReferenceHandle)ca.Constructor);
                                TypeReference parentRef = metaReader.GetTypeReference((TypeReferenceHandle)memRef.Parent);
                                name = metaReader.GetString(parentRef.Name);
                                attrNS = metaReader.GetString(parentRef.Namespace).ToLowerInvariant();
                            }
                            break;
                            default:
                            {
                                throw new NotImplementedException("Unexpected custom attribute constructor kind");
                            }
                        }
                        string fullName = attrNS + "." + name;
                        AttributeProcessor processor;
                        if(attributeParsers.TryGetValue(fullName, out processor))
                        {
                            processor(attrTypeProv, ca, values);
                        }
                        else
                        {
                            //Debug.WriteLine(String.Format("Didn't recognise custom attribute {0}", fullName));
                        }
                    }
                }
            }
            return values;
        }

        private static void ParseConst(CustomAttributeTypeProvider attrTypeProv, CustomAttribute attribute, CustomAttributeValues values)
        {
            values.isConst = true;
        }

        private static void ParseDocLink(CustomAttributeTypeProvider attrTypeProv, CustomAttribute attribute, CustomAttributeValues values)
        {
            CustomAttributeValue<SimpleTypeHandleInfo> attrValue = attribute.DecodeValue(attrTypeProv);
            values.docUrl = (string)attrValue.FixedArguments[0].Value;
        }

        private static void ParseRAIIFree(CustomAttributeTypeProvider attrTypeProv, CustomAttribute attribute, CustomAttributeValues values)
        {
            CustomAttributeValue<SimpleTypeHandleInfo> attrValue = attribute.DecodeValue(attrTypeProv);
            values.raiiFree = (string)attrValue.FixedArguments[0].Value;
        }

        // these values range from ok to wildly wrong, so most things that use the metadata don't seem to use them
        // this will scrape them, but they're unused until it becomes more stable
        // https://github.com/microsoft/win32metadata/issues/738
        //
        // If this is ever re-enabled, version gueards will have ot be output for each type in FileOutput
        private static void ParseSupportedOS(CustomAttributeTypeProvider attrTypeProv, CustomAttribute attribute, CustomAttributeValues values)
        {
            //CustomAttributeValue<SimpleTypeHandleInfo> attrValue = attribute.DecodeValue(attrTypeProv);
            //string verString = (string)attrValue.FixedArguments[0].Value;
            //verString = verString.Remove(0, 7); // remove 'windows' from it
            //if (verString.StartsWith("server", StringComparison.OrdinalIgnoreCase))
            //{
            //    switch (verString.Substring(6))
            //    {
            //        case "2000": verString = "5.0"; break;
            //        case "2003": verString = "5.2"; break;
            //        case "2008": verString = "6.0"; break;
            //        case "2008r2": verString = "6.1"; break;
            //        case "2012": verString = "6.2"; break;
            //        case "2012r2": verString = "6.3"; break;
            //        case "2016":
            //        case "2019":
            //        case "2022": verString = "10.0"; break;
            //        default:
            //        {
            //            throw new NotImplementedException(String.Format("Unknown server supported os '{0}'", verString));
            //        }
            //    }
            //}
            //// this parses the 5.1.2600 type string to a version
            //values.minimumOs = new Version(verString);
        }

        private static void ParseFreeWith(CustomAttributeTypeProvider attrTypeProv, CustomAttribute attribute, CustomAttributeValues values)
        {
            CustomAttributeValue<SimpleTypeHandleInfo> attrValue = attribute.DecodeValue(attrTypeProv);
            values.freeWith = (string)attrValue.FixedArguments[0].Value;
        }

        private static void ParseInvalidHandle(CustomAttributeTypeProvider attrTypeProv, CustomAttribute attribute, CustomAttributeValues values)
        {
            CustomAttributeValue<SimpleTypeHandleInfo> attrValue = attribute.DecodeValue(attrTypeProv);
            if(values.invalidHandleValue == null)
            {
                values.invalidHandleValue = Array.Empty<long>();
            }
            int curLen = values.invalidHandleValue.Length;
            Array.Resize(ref values.invalidHandleValue, curLen + 1);
            values.invalidHandleValue[curLen] = (long)attrValue.FixedArguments[0].Value;
        }

        private static void ParseAnsiAPI(CustomAttributeTypeProvider attrTypeProv, CustomAttribute attribute, CustomAttributeValues values)
        {
            values.ansiApi = true;
        }

        private static void ParseUnicodeAPI(CustomAttributeTypeProvider attrTypeProv, CustomAttribute attribute, CustomAttributeValues values)
        {
            values.unicodeApi = true;
        }

        private static void ParseFlexibleArray(CustomAttributeTypeProvider attrTypeProv, CustomAttribute attribute, CustomAttributeValues values)
        {
            values.flexibleArray = true;
        }

        private static void ParseDoNotRelease(CustomAttributeTypeProvider attrTypeProv, CustomAttribute attribute, CustomAttributeValues values)
        {
            values.dontFreeValue = true;
        }

        private static void ParseDoubleNullTerm(CustomAttributeTypeProvider attrTypeProv, CustomAttribute attribute, CustomAttributeValues values)
        {
            values.doubleNullTerm = true;
        }

        private static void ParseNotNullTerm(CustomAttributeTypeProvider attrTypeProv, CustomAttribute attribute, CustomAttributeValues values)
        {
            values.nonNullTerm = true;
        }

        private static void ParseNativeTypedef(CustomAttributeTypeProvider attrTypeProv, CustomAttribute attribute, CustomAttributeValues values)
        {
            values.nativeTypedef = true;
        }

        private static void ParseMetadataTypedef(CustomAttributeTypeProvider attrTypeProv, CustomAttribute attribute, CustomAttributeValues values)
        {
            values.metadataTypedef = true;
        }

        private static void ParseReserved(CustomAttributeTypeProvider attrTypeProv, CustomAttribute attribute, CustomAttributeValues values)
        {
            values.reserved = true;
        }

        private static void ParseFnPtr(CustomAttributeTypeProvider attrTypeProv, CustomAttribute attribute, CustomAttributeValues values)
        {
            values.fnPtr = true;
        }

        private static void ParseObsolete(CustomAttributeTypeProvider attrTypeProv, CustomAttribute attribute, CustomAttributeValues values)
        {
            values.obsolete = true;
        }

        private static void ParseRetval(CustomAttributeTypeProvider attrTypeProv, CustomAttribute attribute, CustomAttributeValues values)
        {
            values.retVal = true;
        }

        private static void ParseComOutPtr(CustomAttributeTypeProvider attrTypeProv, CustomAttribute attribute, CustomAttributeValues values)
        {
            values.comOutPtr = true;
        }

        private static void ParseStructSizeField(CustomAttributeTypeProvider attrTypeProv, CustomAttribute attribute, CustomAttributeValues values)
        {
            CustomAttributeValue<SimpleTypeHandleInfo> attrValue = attribute.DecodeValue(attrTypeProv);
            values.sizeField = new ArrayMemorySizeAttribute((string)attrValue.FixedArguments[0].Value);
        }

        private static void ParseAlsoUsableFor(CustomAttributeTypeProvider attrTypeProv, CustomAttribute attribute, CustomAttributeValues values)
        {
            CustomAttributeValue<SimpleTypeHandleInfo> attrValue = attribute.DecodeValue(attrTypeProv);
            values.alsoUsableFor = (string)attrValue.FixedArguments[0].Value;
        }

        private static void ParseConstant(CustomAttributeTypeProvider attrTypeProv, CustomAttribute attribute, CustomAttributeValues values)
        {
            CustomAttributeValue<SimpleTypeHandleInfo> attrValue = attribute.DecodeValue(attrTypeProv);
            values.constantValue = (string)attrValue.FixedArguments[0].Value;
        }

        private static void ParseEnumValue(CustomAttributeTypeProvider attrTypeProv, CustomAttribute attribute, CustomAttributeValues values)
        {
            CustomAttributeValue<SimpleTypeHandleInfo> attrValue = attribute.DecodeValue(attrTypeProv);
            values.associatedEnum = (string)attrValue.FixedArguments[0].Value;
        }

        private static void ParseNativeArrayInfo(CustomAttributeTypeProvider attrTypeProv, CustomAttribute attribute, CustomAttributeValues values)
        {
            CustomAttributeValue<SimpleTypeHandleInfo> attrValue = attribute.DecodeValue(attrTypeProv);
            ImmutableArray<CustomAttributeNamedArgument<SimpleTypeHandleInfo>> namedArgs = attrValue.NamedArguments;
            // CfConnectSyncRoot in Windows.Win32.Storage.CloudFilters has this
            if (namedArgs.Length == 0)
            {
                return;
            }
            Debug.Assert(namedArgs.Length == 1, "Unexpected number of NativeArrayInfo array arguments");
            CustomAttributeNamedArgument<SimpleTypeHandleInfo> arg = namedArgs[0];
            switch(arg.Name)
            {
                case "CountConst":
                {
                    // for some reason, these values are sometimes short and sometimes int
                    // and casting to the wrong one causes exceptions, this does't happen with the heavyweight Convert.To()
                    values.sizeField = new ArrayMemorySizeAttribute(Convert.ToInt32(arg.Value), ArrayMemorySizeAttribute.FieldToUse.Constant);
                }
                break;
                case "CountFieldName":
                {
                    values.sizeField = new ArrayMemorySizeAttribute((string)arg.Value);
                }
                break;
                case "CountParamIndex":
                {
                    values.sizeField = new ArrayMemorySizeAttribute(Convert.ToInt32(arg.Value), ArrayMemorySizeAttribute.FieldToUse.Arg);
                }
                break;
                default:
                {
                    throw new NotImplementedException("Unexpected NativeArrayInfoAttribute argument");
                }
            }
        }

        private static void ParseMemorySize(CustomAttributeTypeProvider attrTypeProv, CustomAttribute attribute, CustomAttributeValues values)
        {
            CustomAttributeValue<SimpleTypeHandleInfo> attrValue = attribute.DecodeValue(attrTypeProv);
            ImmutableArray<CustomAttributeNamedArgument<SimpleTypeHandleInfo>> namedArgs = attrValue.NamedArguments;
            Debug.Assert(namedArgs.Length == 1, "Unexpected number of MemorySizeAttribute array arguments");
            CustomAttributeNamedArgument<SimpleTypeHandleInfo> arg = namedArgs[0];
            if(arg.Name == "BytesParamIndex")
            {
                values.sizeField = new ArrayMemorySizeAttribute(Convert.ToInt32(arg.Value), ArrayMemorySizeAttribute.FieldToUse.Arg);
            }
            else
            {
                throw new NotImplementedException("Unexpected MemorySizeAttribute argument");
            }
        }

        private static void ParseNativeEncoding(CustomAttributeTypeProvider attrTypeProv, CustomAttribute attribute, CustomAttributeValues values)
        {
            CustomAttributeValue<SimpleTypeHandleInfo> attrValue = attribute.DecodeValue(attrTypeProv);
            string type = (string)attrValue.FixedArguments[0].Value;
            if(type == "ansi")
            {
                values.ansiApi = true;
            }
            else
            {
                Debug.Assert((type == "unicode") || (type == "utf16"), "Unknown Native encoding attribute value");
                values.unicodeApi = true;
            }
        }

        private static void ParseSupportedArch(CustomAttributeTypeProvider attrTypeProv, CustomAttribute attribute, CustomAttributeValues values)
        {
            CustomAttributeValue<SimpleTypeHandleInfo> attrValue = attribute.DecodeValue(attrTypeProv);
            // it's far too faffy to parse this architecture enum out of the data and do this properly, so we don't
            //
            // this is Windows.Win32.Foundation.Metadata.Architecture
            int arch = (int)attrValue.FixedArguments[0].Value;
            values.supportedArch = (SupportedArchitecture)arch;
        }

        private static void ParseBitfields(CustomAttributeTypeProvider attrTypeProv, CustomAttribute attribute, CustomAttributeValues values)
        {
            CustomAttributeValue<SimpleTypeHandleInfo> attrValue = attribute.DecodeValue(attrTypeProv);
            if(values.bitfields == null)
            {
                values.bitfields = new List<BitfieldMember>();
            }
            BitfieldMember member = new BitfieldMember(
                (string)attrValue.FixedArguments[0].Value,
                (long)attrValue.FixedArguments[1].Value,
                (long)attrValue.FixedArguments[2].Value
            );
            values.bitfields.Add(member);
        }

        private static void ParseGuid(CustomAttributeTypeProvider attrTypeProv, CustomAttribute attribute, CustomAttributeValues values)
        {
            CustomAttributeValue<SimpleTypeHandleInfo> attrValue = attribute.DecodeValue(attrTypeProv);
            values.guid = new Guid(
                (uint)attrValue.FixedArguments[0].Value,
                (ushort)attrValue.FixedArguments[1].Value,
                (ushort)attrValue.FixedArguments[2].Value,
                (byte)attrValue.FixedArguments[3].Value,
                (byte)attrValue.FixedArguments[4].Value,
                (byte)attrValue.FixedArguments[5].Value,
                (byte)attrValue.FixedArguments[6].Value,
                (byte)attrValue.FixedArguments[7].Value,
                (byte)attrValue.FixedArguments[8].Value,
                (byte)attrValue.FixedArguments[9].Value,
                (byte)attrValue.FixedArguments[10].Value
            );
        }

        static internal void Init(MetadataReader metaReader, NamespaceDefinition attrNamespace)
        {
            string nsName = TypeCollector.BuildFullNamespaceName(metaReader, attrNamespace);
            Dictionary<string, AttributeProcessor> attrParsers = new Dictionary<string, AttributeProcessor>(50);
            foreach(TypeDefinitionHandle tdh in attrNamespace.TypeDefinitions)
            {
                TypeDefinition td = metaReader.GetTypeDefinition(tdh);
                string name = metaReader.GetString(td.Name);
                string fullName = nsName + '.' + name;
                AttributeProcessor procFunc;
                switch(name)
                {
                    // if any more cases are added, update the debug assert below
                    case "ConstAttribute":
                    {
                        procFunc = ParseConst;
                    }
                    break;
                    case "GuidAttribute":
                    {
                        procFunc = ParseGuid;
                    }
                    break;
                    case "DocumentationAttribute":
                    {
                        procFunc = ParseDocLink;
                    }
                    break;
                    case "RAIIFreeAttribute":
                    {
                        procFunc = ParseRAIIFree;
                    }
                    break;
                    case "SupportedOSPlatformAttribute":
                    {
                        procFunc = ParseSupportedOS;
                    }
                    break;
                    case "SupportedArchitectureAttribute":
                    {
                        procFunc = ParseSupportedArch;
                    }
                    break;
                    case "FreeWithAttribute":
                    {
                        procFunc = ParseFreeWith;
                    }
                    break;
                    case "InvalidHandleValueAttribute":
                    {
                        procFunc = ParseInvalidHandle;
                    }
                    break;
                    case "AnsiAttribute":
                    {
                        procFunc = ParseAnsiAPI;
                    }
                    break;
                    case "UnicodeAttribute":
                    {
                        procFunc = ParseUnicodeAPI;
                    }
                    break;
                    case "FlexibleArrayAttribute":
                    {
                        procFunc = ParseFlexibleArray;
                    }
                    break;
                    case "DoNotReleaseAttribute":
                    {
                        procFunc = ParseDoNotRelease;
                    }
                    break;
                    case "StructSizeFieldAttribute":
                    {
                        procFunc = ParseStructSizeField;
                    }
                    break;
                    case "AlsoUsableForAttribute":
                    {
                        procFunc = ParseAlsoUsableFor;
                    }
                    break;
                    case "ConstantAttribute":
                    {
                        procFunc = ParseConstant;
                    }
                    break;
                    case "NativeBitfieldAttribute":
                    {
                        procFunc = ParseBitfields;
                    }
                    break;
                    case "NullNullTerminatedAttribute":
                    {
                        procFunc = ParseDoubleNullTerm;
                    }
                    break;
                    case "NotNullTerminatedAttribute":
                    {
                        procFunc = ParseNotNullTerm;
                    }
                    break;
                    case "NativeEncodingAttribute":
                    {
                        procFunc = ParseNativeEncoding;
                    }
                    break;
                    case "NativeTypedefAttribute":
                    {
                        procFunc = ParseNativeTypedef;
                    }
                    break;
                    case "MetadataTypedefAttribute":
                    {
                        procFunc = ParseMetadataTypedef;
                    }
                    break;
                    case "ReservedAttribute":
                    {
                        procFunc = ParseReserved;
                    }
                    break;
                    case "AssociatedEnumAttribute":
                    {
                        procFunc = ParseEnumValue;
                    }
                    break;
                    case "NativeArrayInfoAttribute":
                    {
                        procFunc = ParseNativeArrayInfo;
                    }
                    break;
                    case "MemorySizeAttribute":
                    {
                        procFunc = ParseMemorySize;
                    }
                    break;
                    case "RetValAttribute":
                    {
                        procFunc = ParseRetval;
                    }
                    break;
                    case "ComOutPtrAttribute":
                    {
                        procFunc = ParseComOutPtr;
                    }
                    break;
                    // if any more cases are added, update the debug assert below
                    default:
                    {
                        procFunc = null;
                    }
                    break;
                }
                if(procFunc != null)
                {
                    attrParsers.Add(fullName, procFunc);
                }
            }
            Debug.Assert(attrParsers.Count == 27, "Didn't find all expected attributes, maybe some have been changed");
            // these are normal c# attributes
            attrParsers.Add(typeof(UnmanagedFunctionPointerAttribute).FullName, ParseFnPtr);
            attrParsers.Add(typeof(ObsoleteAttribute).FullName, ParseObsolete);
            Parser = new CustomAttributeParser(attrParsers);
        }

        static internal CustomAttributeParser Parser { get; private set; }
    }
}
