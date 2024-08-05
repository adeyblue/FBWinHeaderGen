using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Text;

namespace MetadataParser
{
    class VarType
    {
        public CustomAttributeValues CustomAttributes { get; init; }
        public string Name { get; private set; }
        public SimpleTypeHandleInfo ParamType { get; init; }
        public FieldAttributes VarAttributes { get; init; }

        public VarType(string name, CustomAttributeValues customAttrs, SimpleTypeHandleInfo parType, FieldAttributes fieldAttrs)
        {
            Name = name;
            CustomAttributes = customAttrs;
            ParamType = parType;
            VarAttributes = fieldAttrs;
        }

        public void Rename(string newName)
        {
            Name = newName;
        }
    }

    class FunctionArgType : VarType
    {
        public ParameterAttributes Attributes { get; init; }

        public FunctionArgType(string name, CustomAttributeValues customAttrs, SimpleTypeHandleInfo parType, ParameterAttributes attr)
            : base(name, customAttrs, parType, 0)
        {
            Attributes = attr;
        }
    }

    class FunctionType
    {
        static readonly List<FunctionArgType> EmptyArgumentList = new List<FunctionArgType>();
        public List<FunctionArgType> Arguments { get; init; }
        public FunctionArgType ReturnType { get; init; }
        public string Name { get; private set; }
        public CustomAttributeValues AttributeValues { get; init; }
        public MethodAttributes FunctionAttributes { get; init; }

        public FunctionType(string name, int argCount, FunctionArgType retType, CustomAttributeValues attrVals, MethodAttributes funAttrs)
        {
            Arguments = (argCount > 0) ? new List<FunctionArgType>(argCount) : EmptyArgumentList;
            ReturnType = retType;
            Name = name;
            AttributeValues = attrVals;
            FunctionAttributes = funAttrs;
        }

        public void AddArgument(FunctionArgType arg)
        {
            Debug.Assert(!Arguments.Equals(EmptyArgumentList), "Adding arguments to function we said had no arguments");
            Arguments.Add(arg);
        }

        public void Rename(string newName)
        {
            Name = newName;
        }
    }

    class FunctionPointerType
    {
        public FunctionType Shape { get; init; }
        public string Name { get; private set; }
        public CustomAttributeValues AttributeValues { get; init; }

        public FunctionPointerType(string name, FunctionType funType, CustomAttributeValues attrVals)
        {
            Name = name;
            Shape = funType;
            AttributeValues = attrVals;
        }

        public void Rename(string newName)
        {
            Name = newName;
        }
    }

    class StructType<T>
    {
        public string Name { get; private set; }
        public CustomAttributeValues AttributeValues { get; init; }
        public List<T> Fields { get; init; }
        // this could be a union or otherwise have several fields at the same offset
        // hence why its int => List rather than just int => field
        public SortedList<int, List<T>> LocationToFields { get; private set; }
        public TypeAttributes TypeAttributes { get; init; }
        public List<HandleTypeHandleInfo> Bases { get; init; }
        public TypeLayout? Layout { get; init; }

        public List<StructType<T>> NestedTypes { get; init; }

        public StructType(
            string name, 
            CustomAttributeValues attrVal, 
            TypeAttributes typeAttrs, 
            List<HandleTypeHandleInfo> baseTypes, 
            List<StructType<T>> innerTypes,
            TypeLayout? layout
        )
        {
            Name = name;
            AttributeValues = attrVal;
            Fields = new List<T>(20);
            TypeAttributes = typeAttrs;
            Bases = baseTypes;
            LocationToFields = null;
            NestedTypes = innerTypes;
            Layout = layout;
        }

        public void AddMember(T member, int explicitOffset = -1)
        {
            Fields.Add(member);
            if(explicitOffset != -1)
            {
                if(LocationToFields == null)
                {
                    LocationToFields = new SortedList<int, List<T>>();
                }
                List<T> offsetFields;
                if(!LocationToFields.TryGetValue(explicitOffset, out offsetFields))
                {
                    offsetFields = new List<T>();
                    LocationToFields.Add(explicitOffset, offsetFields);
                }
                offsetFields.Add(member);
            }
        }

        public void Rename(string newName)
        {
            Name = newName;
        }
    }

    // FB SPECIFIC - this contains the formatting and parsing of constant
    // values like strings and hex numbers
    struct VarValue : IComparable<VarValue>
    {
        public Type TypeCode { get; init; }
        // this is masquerading as a poor mans variant
        private Tuple<int, ulong, long, string, char, double> tupValue;
        private int hexDigits;

        public int ValueByteLength { get; init; }

        public VarValue(ConstantTypeCode type, BlobReader reader)
        {
            TypeCode = ConstantCodeToType(type);
            // which item in the tuple is this going into
            int which = 0;
            char ch = '\0';
            ulong unlong = 0;
            long slong = 0;
            string str = String.Empty;
            double dbl = 0.0;
            hexDigits = 0;
            switch(type)
            {
                case ConstantTypeCode.Char:
                {
                    ch = reader.ReadChar();
                    which = 4;
                    hexDigits = 2;
                }
                break;
                case ConstantTypeCode.SByte:
                {
                    slong = reader.ReadSByte();
                    which = 2;
                    hexDigits = 2;
                }
                break;
                case ConstantTypeCode.Int16:
                {
                    slong = reader.ReadInt16();
                    which = 2;
                    hexDigits = 4;
                }
                break;
                case ConstantTypeCode.Int32:
                {
                    slong = reader.ReadInt32();
                    which = 2;
                    hexDigits = 8;
                }
                break;
                case ConstantTypeCode.Int64:
                {
                    slong = reader.ReadInt64();
                    which = 2;
                    hexDigits = 16;
                }
                break;
                case ConstantTypeCode.Byte:
                {
                    unlong = reader.ReadByte();
                    which = 1;
                    hexDigits = 2;
                }
                break;
                case ConstantTypeCode.UInt16:
                {
                    unlong = reader.ReadUInt16();
                    which = 1;
                    hexDigits = 4;
                }
                break;
                case ConstantTypeCode.UInt32:
                {
                    unlong = reader.ReadUInt32();
                    which = 1;
                    hexDigits = 8;
                }
                break;
                case ConstantTypeCode.UInt64:
                {
                    unlong = reader.ReadUInt64();
                    which = 1;
                    hexDigits = 16;
                }
                break;
                case ConstantTypeCode.String:
                {
                    // ReadSerialisedString doesn't seem to always work
                    string temp = (string)reader.ReadConstant(type);
                    StringBuilder sb = new StringBuilder(temp.Length);
                    foreach (char strCh in temp)
                    {
                        if ((strCh < 0x20) || (strCh > 0x7f))
                        {
                            // FB SPECIFIC
                            // there are some strings which contain newlines and tabs and things
                            // these are escaped here
                            sb.AppendFormat("\\&u{0:x4}", Convert.ToUInt16(strCh));
                        }
                        else
                        {
                            sb.Append(strCh);
                            // if this is a slash, output 2 to escape
                            if (strCh == '\\') sb.Append(strCh);
                        }
                        // FB SPECIFIC
                        // constant strings are limited to 1024 characters before they're truncated, adding separate ones
                        // together is fine though
                        if(sb.Length == 1023 && temp.Length > 1023)
                        {
                            sb.Append("\" + !\"");
                        }
                    }
                    str = sb.ToString();
                    which = 3;
                }
                break;
                case ConstantTypeCode.Single:
                {
                    dbl = reader.ReadSingle();
                    which = 5;
                    hexDigits = 4;
                }
                break;
                case ConstantTypeCode.Double:
                {
                    dbl = reader.ReadDouble();
                    which = 5;
                    hexDigits = 8;
                }
                break;
            }
            ValueByteLength = hexDigits / 2;
            tupValue = new Tuple<int, ulong, long, string, char, double>(which, unlong, slong, str, ch, dbl);
        }

        public override string ToString()
        {
            switch (tupValue.Item1)
            {
                case 1:
                {
                    string hexString = tupValue.Item2.ToString("X");
                    if(hexString.Length > hexDigits)
                    {
                        hexString = hexString.Substring(hexString.Length - hexDigits);
                    }
                    return "&h" + hexString;
                }
                // FB SPECIFIC
                // negative values are left as decimal rather than hex like above
                // as most constants, even for those ptr-sized types (INVALID_HANDLE_VALUE for example)
                // are given as 32-bit values, casting 8 digit hex numbers (even signed ones) to ptr types
                // (again, like INVALID_HANDLE_VALUE) renders a value like (0x00000000FFFFFFFF) which is wrong.
                // the negative decimal number will correctly sign extend when cast
                case 2: return tupValue.Item3.ToString();
                case 3: return tupValue.Item4;
                case 4: return tupValue.Item5.ToString();
                default: return tupValue.Item6.ToString();
            }
        }

        static internal Type ConstantCodeToType(ConstantTypeCode consCode)
        {
            switch (consCode)
            {
                case ConstantTypeCode.Invalid:
                {
                    throw new NotImplementedException("Unexpected invalid constant type code?");
                }
                case ConstantTypeCode.NullReference:
                {
                    return typeof(void*);
                }
                // all the other values are happily shared between the enums
                default:
                {
                    return PrimitiveTypeHandleInfo.PrimitiveTypeCodeToType((PrimitiveTypeCode)consCode);
                }
            }
        }

        public int CompareTo(VarValue other)
        {
            if(tupValue.Item1 != other.tupValue.Item1)
            {
                throw new InvalidOperationException("Comparing different types of var values");
            }
            switch(tupValue.Item1)
            {
                case 1: return tupValue.Item2.CompareTo(other.tupValue.Item2);
                case 2: return tupValue.Item3.CompareTo(other.tupValue.Item3);
                case 3: return tupValue.Item4.CompareTo(other.tupValue.Item4);
                case 4: return tupValue.Item5.CompareTo(other.tupValue.Item5);
                default: return tupValue.Item6.CompareTo(other.tupValue.Item6);
            }
        }
    }

    struct ConstantValue : IComparable<ConstantValue>
    {
        // when housed in here, these types may not agree
        // for example, there are PWSTR values (ie strings) that have a constant value of
        // 0, 1, 0xwhatever, etc, which are represented in varValue as Int32 or Int64 types
        // any type conversions (eg casts) are left to the output stage
        public VarType varType;

        // this is optional, for instance Guids don't have an initialiser, their data is provided
        // by attributes that are part of varType, so they don't have a value
        public VarValue? varValue;

        public ConstantValue(VarType type, VarValue? value)
        {
            varType = type;
            varValue = value;
        }

        public int CompareTo(ConstantValue other)
        {
            if(!(varValue.HasValue && other.varValue.HasValue))
            {
                throw new InvalidOperationException("Comparing incomparable ConstantValues");
            }
            return varValue.Value.CompareTo(other.varValue.Value);
        }

        public void Rename(string newName)
        {
            varType.Rename(newName);
        }
    }
}
