using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Text;
using System.Threading.Tasks;

namespace MetadataParser
{
    // There's minimal documentation about what this is for
    // so I haven't really got a clue what I'm doing here
    // but you bet ya sweet bippy it's required
    // This is basically from https://github.com/microsoft/cswin32

    // Copyright (c) Microsoft Corporation. All rights reserved.
    // Licensed under the MIT license. See LICENSE file in the project root for full license information.

    internal class SignatureTypeProvider : ISignatureTypeProvider<SimpleTypeHandleInfo, SignatureTypeProvider.IGenericContext>
    {
        private CustomAttributeTypeProvider attrTypeProvider;
        private HashSet<string> nsReferences;
        private NameChangeDB nameFixes;
        internal SignatureTypeProvider(CustomAttributeTypeProvider attrProv, HashSet<string> referencedNamespaces, NameChangeDB nameFixDb)
        {
            attrTypeProvider = attrProv;
            nsReferences = referencedNamespaces;
            nameFixes = nameFixDb;
        }

        internal interface IGenericContext
        {
        }

        public static HandleTypeHandleInfo CreateNamedReferenceType(SimpleTypeHandleInfo copyFromType, string name)
        {
            return new HandleTypeHandleInfo(copyFromType, name);
        }

        public static SimpleTypeHandleInfo CreateBaseAndPointerTypes(SimpleTypeHandleInfo copyFromType, string name, int ptrLevels, out SimpleTypeHandleInfo ptredType)
        {
            HandleTypeHandleInfo newBaseType = CreateNamedReferenceType(copyFromType, name);
            SimpleTypeHandleInfo retType = newBaseType;
            for (int i = 0; i < ptrLevels; ++i)
            {
                retType = new PointerTypeHandleInfo(retType);
            }
            ptredType = retType;
            return newBaseType;
        }

        public static SimpleTypeHandleInfo CreateNameOnlyTypeInfo(string ns, string name)
        {
            return new HandleTypeHandleInfo(ns, name);
        }

        public SimpleTypeHandleInfo GetArrayType(SimpleTypeHandleInfo elementType, ArrayShape shape) => new ArrayTypeHandleInfo(elementType, shape);

        public SimpleTypeHandleInfo GetPointerType(SimpleTypeHandleInfo elementType) => new PointerTypeHandleInfo(elementType);

        public SimpleTypeHandleInfo GetPrimitiveType(PrimitiveTypeCode typeCode) => new PrimitiveTypeHandleInfo(typeCode);

        public SimpleTypeHandleInfo GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            HandleTypeHandleInfo hType = new HandleTypeHandleInfo(reader, handle, rawTypeKind, attrTypeProvider, nameFixes);
            if(!String.IsNullOrEmpty(hType.IncludeFile))
            {
                nsReferences.Add(hType.IncludeFile);
            }
            return hType;
        }

        public SimpleTypeHandleInfo GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            HandleTypeHandleInfo hType = new HandleTypeHandleInfo(reader, handle, rawTypeKind, attrTypeProvider, nameFixes);
            if (!String.IsNullOrEmpty(hType.IncludeFile))
            {
                nsReferences.Add(hType.IncludeFile);
            }
            return hType;
        }

        /// <inheritdoc/>
        public SimpleTypeHandleInfo GetSZArrayType(SimpleTypeHandleInfo elementType) => throw new NotImplementedException();

        /// <inheritdoc/>
        public SimpleTypeHandleInfo GetTypeFromSpecification(MetadataReader reader, IGenericContext genericContext, TypeSpecificationHandle handle, byte rawTypeKind) => throw new NotImplementedException();

        /// <inheritdoc/>
        public SimpleTypeHandleInfo GetByReferenceType(SimpleTypeHandleInfo elementType) => throw new NotImplementedException();

        /// <inheritdoc/>
        public SimpleTypeHandleInfo GetFunctionPointerType(MethodSignature<SimpleTypeHandleInfo> signature) => throw new NotImplementedException();

        /// <inheritdoc/>
        public SimpleTypeHandleInfo GetGenericInstantiation(SimpleTypeHandleInfo genericType, ImmutableArray<SimpleTypeHandleInfo> typeArguments) => throw new NotImplementedException();

        /// <inheritdoc/>
        public SimpleTypeHandleInfo GetGenericMethodParameter(IGenericContext genericContext, int index) => throw new NotImplementedException();

        /// <inheritdoc/>
        public SimpleTypeHandleInfo GetGenericTypeParameter(IGenericContext genericContext, int index) => throw new NotImplementedException();

        /// <inheritdoc/>
        public SimpleTypeHandleInfo GetModifiedType(SimpleTypeHandleInfo modifier, SimpleTypeHandleInfo unmodifiedType, bool isRequired) => throw new NotImplementedException();

        /// <inheritdoc/>
        public SimpleTypeHandleInfo GetPinnedType(SimpleTypeHandleInfo elementType) => throw new NotImplementedException();
    }
}
