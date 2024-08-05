using System;
using System.Collections.Generic;
using System.Reflection.Metadata;
using System.Text;

namespace MetadataParser
{
    internal class CustomAttributeTypeProvider : ICustomAttributeTypeProvider<SimpleTypeHandleInfo>
    {
        private NameChangeDB nameFixes;

        public CustomAttributeTypeProvider(NameChangeDB nameUpdates)
        {
            nameFixes = nameUpdates;
        }

        public SimpleTypeHandleInfo GetPrimitiveType(PrimitiveTypeCode typeCode)
        {
            return new PrimitiveTypeHandleInfo(typeCode);
        }

        public SimpleTypeHandleInfo GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            return new HandleTypeHandleInfo(reader, handle, rawTypeKind, this, nameFixes);
        }

        public SimpleTypeHandleInfo GetTypeFromSerializedName(string name)
        {
            return new SimpleTypeHandleInfo();
        }

        public PrimitiveTypeCode GetUnderlyingEnumType(SimpleTypeHandleInfo type)
        {
            return PrimitiveTypeCode.Int32; // an assumption that works for now.
        }

        public bool IsSystemType(SimpleTypeHandleInfo type)
        {
            return type.SystemType;
        }

        public SimpleTypeHandleInfo GetSystemType() => throw new NotImplementedException();

        public SimpleTypeHandleInfo GetSZArrayType(SimpleTypeHandleInfo elementType) => throw new NotImplementedException();

        public SimpleTypeHandleInfo GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => throw new NotImplementedException();
    }
}
