using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

internal sealed partial class CanonicalApi
{
    private readonly Dictionary<EntityHandle, int> marshalRows = new();
    private readonly Dictionary<EntityHandle, int> importRows = new();
    private static bool VisibleMetadataType(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle); var parent = type.GetDeclaringType(); var access = type.Attributes & TypeAttributes.VisibilityMask;
        return parent.IsNil ? access == TypeAttributes.Public : VisibleMetadataType(reader, parent) && access is TypeAttributes.NestedPublic or TypeAttributes.NestedFamily or TypeAttributes.NestedFamORAssem;
    }
    private static void ValidateNullable(ImmutableArray<CustomAttributeTypedArgument<ContractMetadataType>> values)
    {
        foreach (var value in values)
        {
            if (value.Value is ImmutableArray<CustomAttributeTypedArgument<ContractMetadataType>> nested) { ValidateNullable(nested); continue; }
            if (value.Value is not byte flag || flag > 2) throw new BadImageFormatException("Invalid nullable transport flag.");
        }
    }
    private void ReadInteropTableCounts(PEReader pe, MetadataReader reader)
    {
        var metadata = pe.GetMetadata().GetContent();
        void Read(TableIndex table, int parentOffset, int tagBits, TableIndex otherTable, Dictionary<EntityHandle, int> result)
        {
            var count = reader.GetTableRowCount(table); var offset = reader.GetTableMetadataOffset(table); var size = reader.GetTableRowSize(table);
            var wide = Math.Max(reader.GetTableRowCount(TableIndex.Field), reader.GetTableRowCount(otherTable)) >= (1 << (16 - tagBits));
            for (var index = 0; index < count; index++)
            {
                var bytes = metadata.AsSpan(offset + index * size + parentOffset, wide ? 4 : 2);
                var coded = wide ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt16LittleEndian(bytes);
                var row = checked((int)(coded >> tagBits)); var tag = (int)(coded & ((1 << tagBits) - 1));
                var target = tag == 0 ? TableIndex.Field : otherTable;
                if (row == 0 || row > reader.GetTableRowCount(target)) throw new BadImageFormatException("Invalid interop metadata parent.");
                var handle = MetadataTokens.EntityHandle(target, row);
                result[handle] = result.GetValueOrDefault(handle) + 1;
            }
        }
        Read(TableIndex.FieldMarshal, 0, 1, TableIndex.Param, marshalRows);
        Read(TableIndex.ImplMap, 2, 1, TableIndex.MethodDef, importRows);
    }
    private static void ValidateInteropCount(EntityHandle handle, bool expected, Dictionary<EntityHandle, int> counts)
    {
        if (counts.GetValueOrDefault(handle) != (expected ? 1 : 0)) throw new InvalidDataException("Missing, duplicate, or unexpected public interop metadata row.");
    }
    private object PhysicalMarshal(MetadataReader reader, FieldDefinitionHandle handle)
    {
        var field = reader.GetFieldDefinition(handle);
        ValidateInteropCount(handle, (field.Attributes & FieldAttributes.HasFieldMarshal) != 0, marshalRows);
        var descriptor = field.GetMarshallingDescriptor();
        return descriptor.IsNil ? "" : new ContractMetadataTypes(this, reader).MarshalDescriptor(descriptor);
    }
    private sealed partial class ContractMetadataTypes
    {
        public object MarshalDescriptor(BlobHandle handle)
        {
            var blob = reader.GetBlobReader(handle);
            if (blob.RemainingBytes == 0) throw new BadImageFormatException("Empty marshalling descriptor.");
            var code = blob.ReadByte();
            var result = Row(("nativeType", code));
            int Integer(ref BlobReader value) => value.ReadCompressedInteger() is var number && number >= 0 ? number : throw new BadImageFormatException("Invalid marshalling integer.");
            void Element(ref BlobReader value)
            {
                var element = value.ReadByte();
                if (element != 0x50 && !SimpleNativeType(element)) throw new BadImageFormatException("Unsupported marshalling element type.");
                result.Add("element", element);
            }
            switch (code)
            {
                case 0x17: // ByValTStr
                    result.Add("count", Integer(ref blob)); break;
                case 0x19:
                case 0x1a:
                case 0x1c: // COM interface with optional IID parameter
                    if (blob.RemainingBytes > 0) result.Add("iidParameter", Integer(ref blob)); break;
                case 0x1d: // SafeArray: optional variant type and record subtype
                    if (blob.RemainingBytes > 0)
                    {
                        var variant = Integer(ref blob);
                        if (variant >= 0x1000 || !Enum.IsDefined((System.Runtime.InteropServices.VarEnum)variant)) throw new BadImageFormatException("Unknown SafeArray variant type.");
                        result.Add("variantType", variant);
                    }
                    if (blob.RemainingBytes > 0)
                    {
                        var name = blob.ReadSerializedString();
                        result.Add("userType", string.IsNullOrEmpty(name) ? null : GetTypeFromSerializedName(name).Key);
                    }
                    break;
                case 0x1e: // ByValArray
                    result.Add("count", Integer(ref blob));
                    if (blob.RemainingBytes > 0) Element(ref blob); break;
                case 0x2a: // LPArray: subtype, size parameter, element count, size-parameter flag
                    if (blob.RemainingBytes > 0) Element(ref blob);
                    if (blob.RemainingBytes > 0) result.Add("sizeParameter", Integer(ref blob));
                    if (blob.RemainingBytes > 0) result.Add("count", Integer(ref blob));
                    if (blob.RemainingBytes > 0)
                    {
                        var flag = Integer(ref blob);
                        if (flag > 1) throw new BadImageFormatException("Invalid marshalling size parameter flag.");
                        result.Add("sizeParameterSpecified", flag);
                    }
                    break;
                case 0x2c: // CustomMarshaler: resolve type identity instead of retaining assembly version text.
                    result.Add("guid", blob.ReadSerializedString());
                    result.Add("nativeTypeName", blob.ReadSerializedString());
                    var typeName = blob.ReadSerializedString();
                    if (string.IsNullOrEmpty(typeName)) throw new BadImageFormatException("Missing custom marshaler type.");
                    result.Add("managedType", GetTypeFromSerializedName(typeName).Key);
                    result.Add("cookie", blob.ReadSerializedString()); break;
                default:
                    if (!SimpleNativeType(code)) throw new BadImageFormatException("Unsupported marshalling type."); break;
            }
            if (blob.RemainingBytes != 0) throw new BadImageFormatException("Trailing marshalling descriptor data.");
            return result;
        }
        private static bool SimpleNativeType(byte value) => value is >= 0x02 and <= 0x0c or 0x0f or >= 0x13 and <= 0x16 or 0x19 or 0x1a or 0x1b or 0x1c or 0x1f or 0x20 or >= 0x22 and <= 0x26 or 0x28 or 0x2b or >= 0x2d and <= 0x30;
    }

}
