using System.Buffers.Binary;

namespace Fox2SceneConverter;

internal sealed class Fox2Parser
{
    private static readonly uint[] SerializedPropertyStrideTable =
    {
        1,
        1,
        2,
        2,
        4,
        4,
        8,
        8,
        4,
        8,
        1,
        8,
        8,
        8,
        16,
        16,
        16,
        48,
        64,
        16,
        8,
        8,
        32,
        0,
        16,
    };

    public Fox2File Parse(string filePath, AssetPathResolver pathResolver)
    {
        byte[] data = File.ReadAllBytes(filePath);
        ReadOnlySpan<byte> span = data;

        if (span.Length < 0x20)
        {
            throw new InvalidDataException(FormattableString.Invariant($"File '{filePath}' is too small to be a valid .fox2 file."));
        }

        uint entityCount = ReadUInt32(span, 0x8);
        int stringTableOffset = ReadInt32(span, 0xC);
        int entitiesOffset = ReadInt32(span, 0x10);

        Dictionary<ulong, string?> stringTable = ReadStringTable(span, stringTableOffset);
        List<FoxEntity> entities = ReadEntities(span, entityCount, entitiesOffset, stringTable);
        List<string> embeddedAssetPaths = AssetPathScanner.ExtractPaths(span).ToList();

        return new Fox2File(
            filePath,
            pathResolver.ToDisplayPath(filePath),
            entities,
            embeddedAssetPaths);
    }

    private static Dictionary<ulong, string?> ReadStringTable(ReadOnlySpan<byte> data, int stringTableOffset)
    {
        Dictionary<ulong, string?> stringTable = new()
        {
            [0] = null,
        };

        if (stringTableOffset <= 0 || stringTableOffset >= data.Length)
        {
            return stringTable;
        }

        int cursor = stringTableOffset;
        while (cursor + 12 <= data.Length)
        {
            ulong hash = ReadUInt64(data, cursor);
            int length = ReadInt32(data, cursor + 8);
            if (hash == 0 || length <= 0 || cursor + 12 + length > data.Length)
            {
                break;
            }

            string literal = System.Text.Encoding.UTF8.GetString(data.Slice(cursor + 12, length));
            stringTable.TryAdd(hash, literal);
            cursor += 12 + length;
        }

        return stringTable;
    }

    private static List<FoxEntity> ReadEntities(ReadOnlySpan<byte> data, uint entityCount, int entitiesOffset, Dictionary<ulong, string?> stringTable)
    {
        List<FoxEntity> entities = new((int)entityCount);
        if (entityCount == 0 || entitiesOffset <= 0 || entitiesOffset >= data.Length)
        {
            return entities;
        }

        int entityCursor = entitiesOffset;
        for (uint index = 0; index < entityCount && entityCursor + 0x40 <= data.Length; index++)
        {
            ushort headerSize = ReadUInt16(data, entityCursor);
            uint signature = ReadUInt32(data, entityCursor + 0x6);
            if (headerSize != 0x40 || signature != 0x00746E65)
            {
                throw new InvalidDataException(FormattableString.Invariant($"Unexpected entity header at offset 0x{entityCursor:x}."));
            }

            ulong address = ReadUInt64(data, entityCursor + 0xA);
            ulong id = ReadUInt64(data, entityCursor + 0x12);
            ushort version = ReadUInt16(data, entityCursor + 0x1A);
            ulong classNameHash = ReadUInt64(data, entityCursor + 0x1C);
            ushort staticPropertyCount = ReadUInt16(data, entityCursor + 0x24);
            ushort dynamicPropertyCount = ReadUInt16(data, entityCursor + 0x26);
            uint staticPropertiesOffset = ReadUInt32(data, entityCursor + 0x28);
            uint dynamicPropertiesOffset = ReadUInt32(data, entityCursor + 0x2C);
            uint nextEntityOffset = ReadUInt32(data, entityCursor + 0x30);

            string className = ResolveString(stringTable, classNameHash);
            List<FoxProperty> properties = new(staticPropertyCount + dynamicPropertyCount);
            ReadProperties(data, entityCursor, staticPropertiesOffset, staticPropertyCount, stringTable, isDynamic: false, properties);
            ReadProperties(data, entityCursor, dynamicPropertiesOffset, dynamicPropertyCount, stringTable, isDynamic: true, properties);

            entities.Add(new FoxEntity(address, id, className, version, properties));

            if (nextEntityOffset == 0)
            {
                break;
            }

            entityCursor += checked((int)nextEntityOffset);
        }

        return entities;
    }

    private static void ReadProperties(
        ReadOnlySpan<byte> data,
        int entityCursor,
        uint propertiesOffset,
        ushort propertyCount,
        Dictionary<ulong, string?> stringTable,
        bool isDynamic,
        List<FoxProperty> properties)
    {
        if (propertyCount == 0 || propertiesOffset == 0)
        {
            return;
        }

        int propertyCursor = entityCursor + checked((int)propertiesOffset);
        for (int index = 0; index < propertyCount && propertyCursor + 0x10 <= data.Length; index++)
        {
            ulong nameHash = ReadUInt64(data, propertyCursor);
            FoxPropertyType propertyType = (FoxPropertyType)data[propertyCursor + 0x8];
            FoxContainerType containerType = (FoxContainerType)data[propertyCursor + 0x9];
            ushort arraySize = ReadUInt16(data, propertyCursor + 0xA);
            ushort payloadOffset = ReadUInt16(data, propertyCursor + 0xC);
            ushort nextPropertyOffset = ReadUInt16(data, propertyCursor + 0xE);

            string propertyName = ResolveString(stringTable, nameHash);
            FoxPropertyContainer value = ReadPropertyContainer(data, propertyCursor, payloadOffset, propertyType, containerType, arraySize, stringTable);
            properties.Add(new FoxProperty(propertyName, propertyType, containerType, value, isDynamic));

            if (nextPropertyOffset == 0)
            {
                break;
            }

            propertyCursor += nextPropertyOffset;
        }
    }

    private static FoxPropertyContainer ReadPropertyContainer(
        ReadOnlySpan<byte> data,
        int propertyCursor,
        ushort payloadOffset,
        FoxPropertyType propertyType,
        FoxContainerType containerType,
        ushort arraySize,
        Dictionary<ulong, string?> stringTable)
    {
        if (containerType == FoxContainerType.StaticArray && arraySize == 1)
        {
            return new FoxSingleValue(ReadValue(data, propertyCursor, payloadOffset, propertyType, containerType, 0, stringTable));
        }

        if (containerType == FoxContainerType.StringMap)
        {
            Dictionary<string, FoxValue> values = new(StringComparer.Ordinal);
            for (ushort index = 0; index < arraySize; index++)
            {
                int stride = checked((int)Align(SerializedPropertyStrideTable[(int)propertyType] + 8, 16));
                int entryOffset = propertyCursor + payloadOffset + index * stride;
                ulong keyHash = ReadUInt64(data, entryOffset);
                string key = ResolveString(stringTable, keyHash);
                values[key] = ReadValue(data, propertyCursor, payloadOffset, propertyType, containerType, index, stringTable);
            }

            return new FoxStringMapValue(values);
        }

        List<FoxValue> items = new(arraySize);
        for (ushort index = 0; index < arraySize; index++)
        {
            items.Add(ReadValue(data, propertyCursor, payloadOffset, propertyType, containerType, index, stringTable));
        }

        return new FoxListValue(items);
    }

    private static FoxValue ReadValue(
        ReadOnlySpan<byte> data,
        int propertyCursor,
        ushort payloadOffset,
        FoxPropertyType propertyType,
        FoxContainerType containerType,
        ushort index,
        Dictionary<ulong, string?> stringTable)
    {
        uint stride = SerializedPropertyStrideTable[(int)propertyType];
        int valueOffset = propertyCursor + payloadOffset;
        if (containerType == FoxContainerType.StringMap)
        {
            int stringMapStride = checked((int)Align(stride + 8, 16));
            valueOffset += index * stringMapStride + 8;
        }
        else
        {
            valueOffset += checked((int)(index * stride));
        }

        return propertyType switch
        {
            FoxPropertyType.Int8 => new FoxScalarValue((sbyte)data[valueOffset]),
            FoxPropertyType.UInt8 => new FoxScalarValue(data[valueOffset]),
            FoxPropertyType.Int16 => new FoxScalarValue(ReadInt16(data, valueOffset)),
            FoxPropertyType.UInt16 => new FoxScalarValue(ReadUInt16(data, valueOffset)),
            FoxPropertyType.Int32 => new FoxScalarValue(ReadInt32(data, valueOffset)),
            FoxPropertyType.UInt32 => new FoxScalarValue(ReadUInt32(data, valueOffset)),
            FoxPropertyType.Int64 => new FoxScalarValue(ReadInt64(data, valueOffset)),
            FoxPropertyType.UInt64 => new FoxScalarValue(ReadUInt64(data, valueOffset)),
            FoxPropertyType.Float => new FoxScalarValue(ReadSingle(data, valueOffset)),
            FoxPropertyType.Double => new FoxScalarValue(ReadDouble(data, valueOffset)),
            FoxPropertyType.Bool => new FoxScalarValue(data[valueOffset] != 0),
            FoxPropertyType.String => new FoxStringValue(ResolveString(stringTable, ReadUInt64(data, valueOffset))),
            FoxPropertyType.Path => new FoxStringValue(ResolveString(stringTable, ReadUInt64(data, valueOffset))),
            FoxPropertyType.EntityPtr => new FoxEntityReferenceValue(ReadUInt64(data, valueOffset)),
            FoxPropertyType.Vector3 => new FoxVector3Value(ReadSingle(data, valueOffset), ReadSingle(data, valueOffset + 4), ReadSingle(data, valueOffset + 8)),
            FoxPropertyType.Vector4 => new FoxVector4Value(ReadSingle(data, valueOffset), ReadSingle(data, valueOffset + 4), ReadSingle(data, valueOffset + 8), ReadSingle(data, valueOffset + 12)),
            FoxPropertyType.Quat => new FoxQuaternionValue(ReadSingle(data, valueOffset), ReadSingle(data, valueOffset + 4), ReadSingle(data, valueOffset + 8), ReadSingle(data, valueOffset + 12)),
            FoxPropertyType.Matrix3 => new FoxMatrixValue(ReadFloatArray(data, valueOffset, 12)),
            FoxPropertyType.Matrix4 => new FoxMatrixValue(ReadFloatArray(data, valueOffset, 16)),
            FoxPropertyType.Color => new FoxVector4Value(ReadSingle(data, valueOffset), ReadSingle(data, valueOffset + 4), ReadSingle(data, valueOffset + 8), ReadSingle(data, valueOffset + 12)),
            FoxPropertyType.FilePtr => new FoxStringValue(ResolveString(stringTable, ReadUInt64(data, valueOffset))),
            FoxPropertyType.EntityHandle => new FoxEntityReferenceValue(ReadUInt64(data, valueOffset)),
            FoxPropertyType.EntityLink => new FoxEntityLinkValue(
                ResolveOptionalString(stringTable, ReadUInt64(data, valueOffset)),
                ResolveOptionalString(stringTable, ReadUInt64(data, valueOffset + 8)),
                ResolveOptionalString(stringTable, ReadUInt64(data, valueOffset + 16)),
                ReadUInt64(data, valueOffset + 24)),
            FoxPropertyType.PropertyInfo => new FoxNullValue(),
            FoxPropertyType.WideVector3 => new FoxVector3Value(ReadSingle(data, valueOffset), ReadSingle(data, valueOffset + 4), ReadSingle(data, valueOffset + 8)),
            _ => new FoxNullValue(),
        };
    }

    private static string ResolveString(Dictionary<ulong, string?> stringTable, ulong hash)
    {
        return stringTable.TryGetValue(hash, out string? value) && value is not null
            ? value
            : FoxFormatting.FormatAddress(hash);
    }

    private static string? ResolveOptionalString(Dictionary<ulong, string?> stringTable, ulong hash)
    {
        return stringTable.TryGetValue(hash, out string? value) ? value : null;
    }

    private static uint Align(uint value, uint alignment)
    {
        return (value + alignment - 1) & ~(alignment - 1);
    }

    private static short ReadInt16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadInt16LittleEndian(data.Slice(offset, 2));
    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
    private static int ReadInt32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
    private static long ReadInt64(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadInt64LittleEndian(data.Slice(offset, 8));
    private static ulong ReadUInt64(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8));
    private static float ReadSingle(ReadOnlySpan<byte> data, int offset) => BitConverter.ToSingle(data.Slice(offset, 4));
    private static double ReadDouble(ReadOnlySpan<byte> data, int offset) => BitConverter.ToDouble(data.Slice(offset, 8));

    private static float[] ReadFloatArray(ReadOnlySpan<byte> data, int offset, int count)
    {
        float[] values = new float[count];
        for (int index = 0; index < count; index++)
        {
            values[index] = ReadSingle(data, offset + index * 4);
        }

        return values;
    }
}
