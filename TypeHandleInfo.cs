using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Text;
using System.Threading.Tasks;

namespace MetadataParser
{
    // I've muddled through most of this, it's pretty messy
    // thanks to https://github.com/microsoft/cswin32
    // I really don't like that this groups actual types with handles/references to type name
    // since the thing that uses these can only return one type, this or something like it is unfortunately necessary.
    // It means there is upcasting and type checking out the wazoo in the output
    // because the type handles don't give you any concrete info
    // other than this exists, so if you need that info, you have to look it up elsewhere
    // and they can be contained within the pointer types ones so you have to decompose those to find them
    // and yeah, I kinda hate this. Obviously something like a visitor would've been much better than upcasing and if-ing
    // but I thought this'd be like a 5 minute job at the start. I didn't think it'd require all this machinery to make work
    // 
    // FB SPECIFIC - These are the FB primitive types, we just need their names, so the structs are empty
    namespace FBTypes
    {
        internal struct Byte { }
        internal struct UByte { }
        internal struct Short { }
        internal struct UShort { }
        internal struct Long { }
        internal struct ULong { }
        internal struct LongInt { }
        internal struct ULongInt { }
        internal struct Any { }
        internal struct Single { }
        internal struct Double { }
        internal struct Boolean { }
        internal struct Integer { }
        internal struct UInteger { }
        internal struct WString { }
        internal struct ZString { }
    }

    // Freebasic doesn't have a char type, but we need some way to tell
    // C strings from just normal ushort ptrs in the metadata
    internal struct NonFBChar
    {
    }

    internal struct Ptr
    {
        public SimpleTypeHandleInfo innerType;
    }

    static class BasicFBTypes
    {
        static List<Type> primitiveTypes;

        static BasicFBTypes()
        {
            primitiveTypes = new List<Type>();
            primitiveTypes.Add(typeof(FBTypes.Byte));
            primitiveTypes.Add(typeof(FBTypes.UByte));
            primitiveTypes.Add(typeof(FBTypes.Short));
            primitiveTypes.Add(typeof(FBTypes.UShort));
            primitiveTypes.Add(typeof(FBTypes.Long));
            primitiveTypes.Add(typeof(FBTypes.ULong));
            primitiveTypes.Add(typeof(FBTypes.LongInt));
            primitiveTypes.Add(typeof(FBTypes.ULongInt));
            primitiveTypes.Add(typeof(FBTypes.Single));
            primitiveTypes.Add(typeof(FBTypes.Double));
            primitiveTypes.Add(typeof(FBTypes.Boolean));
            primitiveTypes.Add(typeof(FBTypes.Integer));
            primitiveTypes.Add(typeof(FBTypes.UInteger));
        }

        static internal bool IsPrimitive(Type t)
        {
            return primitiveTypes.Exists((Type x) => { return x == t; });
        }

        static internal bool IsStringPointer(Type t)
        {
            return (t == typeof(FBTypes.ZString)) || (t == typeof(FBTypes.WString));
        }

        static internal bool IsType(Type t)
        {
            return IsPrimitive(t) || IsStringPointer(t);
        }
    }

    class SimpleTypeHandleInfo
    {
        internal Type TypeInfo { get; init; }
        internal string IncludeFile { get; init; }
        internal bool SystemType { get; init; }

        public CustomAttributeValues TypeAttributes { get; init; }


        public virtual SimpleTypeHandleInfo GetRealType(GlobalTypeRegistry typedefs)
        {
            return this;
        }

        public override string ToString()
        {
            // FB SPECIFIC - If we're a simple type of NonFBChar, then this was just a char
            // and not a char* type string, so instead of outputting NonFBChar, we just output UShort
            return TypeInfo != typeof(NonFBChar) ? TypeInfo.Name : typeof(FBTypes.UShort).Name;
        }
    }

    class ArrayTypeHandleInfo : SimpleTypeHandleInfo
    {
        internal ArrayShape Bounds { get; init; }
        internal SimpleTypeHandleInfo DataType { get; init; }

        public ArrayTypeHandleInfo(SimpleTypeHandleInfo thi, ArrayShape shape)
        {
            TypeInfo = thi.TypeInfo;
            IncludeFile = thi.IncludeFile;
            SystemType = thi.SystemType;
            Bounds = shape;
            DataType = thi;
        }

        public string DimensionString()
        {
            // FB SPECIFIC, output a naked array
            // in FB, the array dimensions attach to the variable name
            // not the type, so we can't just put this in a tostring override
            // and it has to be special cased :-(
            ImmutableArray<int> lowerBounds = Bounds.LowerBounds;
            ImmutableArray<int> rankSizes = Bounds.Sizes;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < Bounds.Rank; ++i)
            {
                sb.AppendFormat("({0} to {1})", lowerBounds[i], rankSizes[i] - 1);
            }
            return sb.ToString();
        }

        public override string ToString()
        {
            return DataType.ToString();
        }
    }

    class PrimitiveTypeHandleInfo : SimpleTypeHandleInfo
    {
        internal const PrimitiveTypeCode ZStringTypeCode = (PrimitiveTypeCode)255;
        internal static Type PrimitiveTypeCodeToType(PrimitiveTypeCode pti)
        {
            switch(pti)
            {
                case PrimitiveTypeCode.Void: return typeof(FBTypes.Any);
                case PrimitiveTypeCode.Boolean: return typeof(FBTypes.Boolean);
                case PrimitiveTypeCode.Char: return typeof(NonFBChar);
                case PrimitiveTypeCode.SByte: return typeof(FBTypes.Byte);
                case PrimitiveTypeCode.Byte: return typeof(FBTypes.UByte);
                case PrimitiveTypeCode.Int16: return typeof(FBTypes.Short);
                case PrimitiveTypeCode.UInt16: return typeof(FBTypes.UShort);
                case PrimitiveTypeCode.Int32: return typeof(FBTypes.Long);
                case PrimitiveTypeCode.UInt32: return typeof(FBTypes.ULong);
                case PrimitiveTypeCode.Int64: return typeof(FBTypes.LongInt);
                case PrimitiveTypeCode.UInt64: return typeof(FBTypes.ULongInt);
                case PrimitiveTypeCode.IntPtr: return typeof(FBTypes.Integer);
                case PrimitiveTypeCode.UIntPtr: return typeof(FBTypes.UInteger);
                case PrimitiveTypeCode.Single: return typeof(FBTypes.Single);
                case PrimitiveTypeCode.Double: return typeof(FBTypes.Double);
                case PrimitiveTypeCode.String: return typeof(FBTypes.WString);
                case ZStringTypeCode: return typeof(FBTypes.ZString);
            }
            throw new InvalidOperationException("Did we need this type?");
        }

        public PrimitiveTypeHandleInfo(PrimitiveTypeCode pti)
        {
            TypeInfo = PrimitiveTypeCodeToType(pti);
            SystemType = true;
        }
    }

    struct PtrStripResult
    {
        public SimpleTypeHandleInfo Stripped { get; init; }
        public int PtrLevels { get; init; }

        public PtrStripResult(SimpleTypeHandleInfo strippedType, int levels)
        {
            Stripped = strippedType;
            PtrLevels = levels;
        }
    }

    class PointerTypeHandleInfo : SimpleTypeHandleInfo
    {
        private Ptr typeInf;

        public PointerTypeHandleInfo(SimpleTypeHandleInfo thi)
        {
            typeInf = new Ptr();
            // FB SPECIFIC, turn the char pointers of raw string types into FB string types
            if (thi is PrimitiveTypeHandleInfo)
            {
                if (thi.TypeInfo == typeof(NonFBChar))
                {
                    typeInf.innerType = new PrimitiveTypeHandleInfo(PrimitiveTypeCode.String);
                }
                else if (thi.TypeInfo == typeof(FBTypes.UByte))
                {
                    typeInf.innerType = new PrimitiveTypeHandleInfo(PrimitiveTypeHandleInfo.ZStringTypeCode);
                }
                else
                {
                    typeInf.innerType = thi;
                }
            }
            else
            {
                typeInf.innerType = thi;
            }
            IncludeFile = thi.IncludeFile;
            SystemType = thi.SystemType;
            TypeAttributes = thi.TypeAttributes;
            TypeInfo = typeInf.innerType.TypeInfo;
        }

        public SimpleTypeHandleInfo NakedType { get { return typeInf.innerType; } }

        public static PtrStripResult StripAllPointers(SimpleTypeHandleInfo toStrip)
        {
            PointerTypeHandleInfo pType = toStrip as PointerTypeHandleInfo;
            if(pType == null)
            {
                return new PtrStripResult(toStrip, 0);
            }
            int ptrLevels = 1;
            SimpleTypeHandleInfo inner = pType.NakedType;
            PointerTypeHandleInfo nakedTypeAsPtr = inner as PointerTypeHandleInfo;
            while (nakedTypeAsPtr != null)
            {
                inner = nakedTypeAsPtr.NakedType;
                nakedTypeAsPtr = inner as PointerTypeHandleInfo;
                ++ptrLevels;
            }
            return new PtrStripResult(inner, ptrLevels);
        }

        public override string ToString()
        {
            return typeInf.innerType.ToString() + " " + typeof(Ptr).Name;
        }
    }

    struct TypeHandleCracker
    {
        public string actualName;
        public string parentType;
        public string ns;
        public bool isSystemType;
        public CustomAttributeValues attrVals;

        private TypeHandleCracker(string bType, string pare, string nspace, bool sysType, CustomAttributeValues typeAttrVals)
        {
            parentType = pare;
            isSystemType = sysType;
            attrVals = typeAttrVals;
            // since the metadata is a .net assembly, and Guid is a net type
            // it comes up as system.guid instead of there been a guid type in foundation
            if(bType == "Guid")
            {
                ns = "windows.win32.foundation";
                actualName = "GUID";
            }
            else if(sysType)
            {
                actualName = nspace + "." + bType;
                ns = null;
            }
            else
            {
                actualName = bType;
                ns = nspace;
            }
        }

        static public TypeHandleCracker FromReference(MetadataReader reader, TypeReferenceHandle tdh, CustomAttributeTypeProvider attrTypeProv)
        {
            TypeReference tr = reader.GetTypeReference(tdh);
            string baseName = reader.GetString(tr.Name);
            string IncludeFile = reader.GetString(tr.Namespace).ToLowerInvariant();
            string typeName = null;
            bool systemType = false;
            CustomAttributeValues typeAttrVals = null;
            if (!tr.ResolutionScope.IsNil)
            {
                switch (tr.ResolutionScope.Kind)
                {
                    case HandleKind.TypeReference:
                    {
                        TypeReference parent = reader.GetTypeReference((TypeReferenceHandle)tr.ResolutionScope);
                        typeName = reader.GetString(parent.Name);
                    }
                    break;
                    case HandleKind.TypeDefinition:
                    {
                        TypeDefinition parent = reader.GetTypeDefinition((TypeDefinitionHandle)tr.ResolutionScope);
                        typeName = reader.GetString(parent.Name);
                        CustomAttributeHandleCollection attrs = parent.GetCustomAttributes();
                        typeAttrVals = CustomAttributeParser.Parser.ParseAttributes(reader, attrTypeProv, attrs);
                    }
                    break;
                    case HandleKind.AssemblyReference:
                    {
                        AssemblyReference ass = reader.GetAssemblyReference((AssemblyReferenceHandle)tr.ResolutionScope);
                        string assName = reader.GetString(ass.Name).ToLowerInvariant();
                        systemType = ((assName == "system") || (assName == "mscorlib") || (assName == "netstandard"));
                    }
                    break;
                    case HandleKind.ModuleDefinition:
                    break;
#if DEBUG
                    default:
                    {
                        throw new NotImplementedException("Unknown type resolution scope");
                    }
#endif
                }
            }
            return new TypeHandleCracker(baseName, typeName, IncludeFile, systemType, typeAttrVals);
        }
    }

    class HandleTypeHandleInfo : SimpleTypeHandleInfo
    {
        internal string ActualName { get; init; }
        internal string TypeName { get; init; }

        public HandleTypeHandleInfo(string ns, string newName)
        {
            IncludeFile = ns;
            ActualName = newName;
        }

        public HandleTypeHandleInfo(SimpleTypeHandleInfo copyFrom, string newName)
        {
            ActualName = newName;
            // forward declarations don't need an include file and aren't referenced cross namespaces
            IncludeFile = null;
            TypeInfo = copyFrom.TypeInfo;
            TypeAttributes = copyFrom.TypeAttributes;
            SystemType = copyFrom.SystemType;
        }

        public HandleTypeHandleInfo(MetadataReader reader, TypeDefinitionHandle tdh, byte rawType, CustomAttributeTypeProvider attrTypeProv, NameChangeDB nameFixes)
        {
            TypeDefinition td = reader.GetTypeDefinition(tdh);
            string thingName = reader.GetString(td.Name);
            string incFile = reader.GetString(td.Namespace).ToLowerInvariant();
            bool sysType = false;
            // system references are for things like GUID that are built-in to net
            // but obviously not fb, in these generated headers they are in Windows.Win32.Foundation
            if(incFile.Equals("system", StringComparison.OrdinalIgnoreCase))
            {
                incFile = "windows.win32.foundation";
                sysType = true;
            }
            NameChangeDB.Change? changedName = nameFixes.FindNewAddress(incFile, thingName);
            if(changedName.HasValue)
            {
                incFile = changedName.Value.Namespace;
                thingName = changedName.Value.Name;
            }
            ActualName = thingName;
            IncludeFile = incFile;
            SystemType = sysType;
            TypeAttributes = CustomAttributeParser.Parser.ParseAttributes(reader, attrTypeProv, td.GetCustomAttributes());
            TypeDefinitionHandle parentTdh = td.GetDeclaringType();
            if(!parentTdh.IsNil)
            {
                TypeDefinition parent = reader.GetTypeDefinition(parentTdh);
                TypeName = reader.GetString(parent.Name);
            }
            else
            {
                TypeName = null;
            }
        }

        public HandleTypeHandleInfo(MetadataReader reader, TypeReferenceHandle trh, byte rawType, CustomAttributeTypeProvider attrTypeProv, NameChangeDB nameFixes)
        {
            TypeHandleCracker cracked = TypeHandleCracker.FromReference(reader, trh, attrTypeProv);
            if (!String.IsNullOrEmpty(cracked.ns))
            {
                NameChangeDB.Change? changedName = nameFixes.FindNewAddress(cracked.ns, cracked.actualName);
                if (changedName.HasValue)
                {
                    cracked.ns = changedName.Value.Namespace;
                    cracked.actualName = changedName.Value.Name;
                }
            }
            ActualName = cracked.actualName;
            IncludeFile = cracked.ns;
            TypeName = cracked.parentType;
            TypeAttributes = cracked.attrVals;
            SystemType = cracked.isSystemType;
        }

        public override SimpleTypeHandleInfo GetRealType(GlobalTypeRegistry typedefs)
        {
            return typedefs.LookupHandle(IncludeFile, ActualName);
        }

        public override string ToString()
        {
            string fullName = String.Empty;
            if (!String.IsNullOrEmpty(TypeName))
            {
                fullName += TypeName + ".";
            }
            return fullName + ActualName;
        }
    }
}
