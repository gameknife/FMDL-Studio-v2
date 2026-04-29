using System.Text;
using System.Text.RegularExpressions;

namespace Fox2SceneConverter;

internal enum FoxPropertyType : byte
{
    Int8 = 0,
    UInt8 = 1,
    Int16 = 2,
    UInt16 = 3,
    Int32 = 4,
    UInt32 = 5,
    Int64 = 6,
    UInt64 = 7,
    Float = 8,
    Double = 9,
    Bool = 10,
    String = 11,
    Path = 12,
    EntityPtr = 13,
    Vector3 = 14,
    Vector4 = 15,
    Quat = 16,
    Matrix3 = 17,
    Matrix4 = 18,
    Color = 19,
    FilePtr = 20,
    EntityHandle = 21,
    EntityLink = 22,
    PropertyInfo = 23,
    WideVector3 = 24,
}

internal enum FoxContainerType : byte
{
    StaticArray = 0,
    DynamicArray = 1,
    StringMap = 2,
    List = 3,
}

internal sealed record Fox2File(
    string SourcePath,
    string DisplayPath,
    IReadOnlyList<FoxEntity> Entities,
    IReadOnlyList<string> EmbeddedAssetPaths);

internal sealed class FoxEntity
{
    private readonly Dictionary<string, FoxProperty> propertyMap;

    public FoxEntity(ulong address, ulong id, string className, ushort version, IReadOnlyList<FoxProperty> properties)
    {
        Address = address;
        Id = id;
        ClassName = className;
        Version = version;
        Properties = properties;
        propertyMap = properties.ToDictionary(property => property.Name, StringComparer.Ordinal);
    }

    public ulong Address { get; }
    public ulong Id { get; }
    public string ClassName { get; }
    public ushort Version { get; }
    public IReadOnlyList<FoxProperty> Properties { get; }

    public string DisplayName
    {
        get
        {
            string? explicitName = TryGetStringProperty("name");
            if (!string.IsNullOrWhiteSpace(explicitName))
            {
                return explicitName;
            }

            string? referencePath = TryGetStringProperty("referenceFilePath");
            if (!string.IsNullOrWhiteSpace(referencePath))
            {
                return Path.GetFileNameWithoutExtension(referencePath.Replace('/', '\\'));
            }

            return $"{ClassName}@{FoxFormatting.FormatAddress(Address)}";
        }
    }

    public FoxProperty? FindProperty(string name)
    {
        return propertyMap.TryGetValue(name, out FoxProperty? property) ? property : null;
    }

    public string? TryGetStringProperty(string name)
    {
        if (!propertyMap.TryGetValue(name, out FoxProperty? property))
        {
            return null;
        }

        return property.Value switch
        {
            FoxSingleValue { Value: FoxStringValue stringValue } => stringValue.Value,
            _ => null,
        };
    }

    public ulong? TryGetEntityReference(string name)
    {
        if (!propertyMap.TryGetValue(name, out FoxProperty? property))
        {
            return null;
        }

        return property.Value switch
        {
            FoxSingleValue { Value: FoxEntityReferenceValue entityReference } when entityReference.Address != 0 => entityReference.Address,
            _ => null,
        };
    }

    public IReadOnlyList<ulong> GetEntityReferenceList(string name)
    {
        if (!propertyMap.TryGetValue(name, out FoxProperty? property))
        {
            return Array.Empty<ulong>();
        }

        return property.Value switch
        {
            FoxListValue listValue => listValue.Items
                .OfType<FoxEntityReferenceValue>()
                .Where(reference => reference.Address != 0)
                .Select(reference => reference.Address)
                .ToArray(),
            FoxSingleValue { Value: FoxEntityReferenceValue entityReference } when entityReference.Address != 0 => new[] { entityReference.Address },
            _ => Array.Empty<ulong>(),
        };
    }
}

internal sealed record FoxProperty(string Name, FoxPropertyType Type, FoxContainerType ContainerType, FoxPropertyContainer Value, bool IsDynamic);

internal abstract record FoxPropertyContainer;

internal sealed record FoxSingleValue(FoxValue Value) : FoxPropertyContainer;

internal sealed record FoxListValue(IReadOnlyList<FoxValue> Items) : FoxPropertyContainer;

internal sealed record FoxStringMapValue(IReadOnlyDictionary<string, FoxValue> Items) : FoxPropertyContainer;

internal abstract record FoxValue;

internal sealed record FoxNullValue() : FoxValue;

internal sealed record FoxScalarValue(object Value) : FoxValue;

internal sealed record FoxStringValue(string Value) : FoxValue;

internal sealed record FoxVector3Value(float X, float Y, float Z) : FoxValue;

internal sealed record FoxVector4Value(float X, float Y, float Z, float W) : FoxValue;

internal sealed record FoxQuaternionValue(float X, float Y, float Z, float W) : FoxValue;

internal sealed record FoxMatrixValue(IReadOnlyList<float> Values) : FoxValue;

internal sealed record FoxEntityReferenceValue(ulong Address) : FoxValue;

internal sealed record FoxEntityLinkValue(string? PackagePath, string? ArchivePath, string? NameInArchive, ulong Address) : FoxValue;

internal static class FoxValueJsonConverter
{
    public static object? ToJsonObject(FoxPropertyContainer container, IReadOnlyDictionary<ulong, FoxEntity> entityLookup)
    {
        return container switch
        {
            FoxSingleValue singleValue => ToJsonObject(singleValue.Value, entityLookup),
            FoxListValue listValue => listValue.Items.Select(item => ToJsonObject(item, entityLookup)).ToArray(),
            FoxStringMapValue stringMapValue => stringMapValue.Items.ToDictionary(
                pair => pair.Key,
                pair => ToJsonObject(pair.Value, entityLookup),
                StringComparer.Ordinal),
            _ => null,
        };
    }

    public static object? ToJsonObject(FoxValue value, IReadOnlyDictionary<ulong, FoxEntity> entityLookup)
    {
        return value switch
        {
            FoxNullValue => null,
            FoxScalarValue scalarValue => scalarValue.Value,
            FoxStringValue stringValue => stringValue.Value,
            FoxVector3Value vector3Value => new Dictionary<string, float>
            {
                ["x"] = vector3Value.X,
                ["y"] = vector3Value.Y,
                ["z"] = vector3Value.Z,
            },
            FoxVector4Value vector4Value => new Dictionary<string, float>
            {
                ["x"] = vector4Value.X,
                ["y"] = vector4Value.Y,
                ["z"] = vector4Value.Z,
                ["w"] = vector4Value.W,
            },
            FoxQuaternionValue quaternionValue => new Dictionary<string, float>
            {
                ["x"] = quaternionValue.X,
                ["y"] = quaternionValue.Y,
                ["z"] = quaternionValue.Z,
                ["w"] = quaternionValue.W,
            },
            FoxMatrixValue matrixValue => matrixValue.Values.ToArray(),
            FoxEntityReferenceValue entityReferenceValue => ToEntityReferenceJson(entityReferenceValue.Address, entityLookup),
            FoxEntityLinkValue entityLinkValue => new Dictionary<string, object?>
            {
                ["type"] = "entityLink",
                ["packagePath"] = entityLinkValue.PackagePath,
                ["archivePath"] = entityLinkValue.ArchivePath,
                ["nameInArchive"] = entityLinkValue.NameInArchive,
                ["address"] = entityLinkValue.Address == 0 ? null : FoxFormatting.FormatAddress(entityLinkValue.Address),
            },
            _ => value.ToString(),
        };
    }

    private static object? ToEntityReferenceJson(ulong address, IReadOnlyDictionary<ulong, FoxEntity> entityLookup)
    {
        if (address == 0)
        {
            return null;
        }

        Dictionary<string, object?> json = new()
        {
            ["type"] = "entityRef",
            ["address"] = FoxFormatting.FormatAddress(address),
        };

        if (entityLookup.TryGetValue(address, out FoxEntity? entity))
        {
            json["className"] = entity.ClassName;
            json["name"] = entity.DisplayName;
        }

        return json;
    }
}

internal static class AssetPathScanner
{
    private static readonly Regex AssetPathRegex = new(
        @"/Assets/[A-Za-z0-9_./-]+\.[A-Za-z0-9_.-]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] ReferenceExtensions =
    {
        ".fox2",
        ".fstb",
        ".fpk",
        ".fpkd",
        ".fmdl",
        ".fmdlb",
    };

    public static IReadOnlyList<string> ExtractPaths(ReadOnlySpan<byte> data)
    {
        List<string> paths = new();
        StringBuilder builder = new();

        foreach (byte value in data)
        {
            if (value is >= 32 and <= 126)
            {
                builder.Append((char)value);
            }
            else
            {
                AppendMatches(builder, paths);
                builder.Clear();
            }
        }

        AppendMatches(builder, paths);
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<string> ExtractPathsFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        HashSet<string> results = new(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in AssetPathRegex.Matches(text))
        {
            results.Add(NormalizeReferenceText(match.Value));
        }

        string normalized = NormalizeReferenceText(text);
        if (LooksLikeReference(normalized))
        {
            results.Add(normalized);
        }

        return results.ToArray();
    }

    public static bool IsTerminalModelPath(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".fmdl", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".fmdlb", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsRecursiveContainer(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".fox2", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".fstb", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".fpk", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".fpkd", StringComparison.OrdinalIgnoreCase);
    }

    private static void AppendMatches(StringBuilder builder, List<string> paths)
    {
        if (builder.Length < 8)
        {
            return;
        }

        string candidate = builder.ToString();
        foreach (string path in ExtractPathsFromText(candidate))
        {
            paths.Add(path);
        }
    }

    private static bool LooksLikeReference(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return ReferenceExtensions.Any(extension => text.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeReferenceText(string text)
    {
        string normalized = text.Trim().Trim('"', '\'').Replace('\\', '/');
        int assetIndex = normalized.IndexOf("/Assets/", StringComparison.OrdinalIgnoreCase);
        if (assetIndex >= 0)
        {
            normalized = normalized[assetIndex..];
        }
        else if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "/" + normalized;
        }

        return normalized;
    }
}

internal static class FoxFormatting
{
    public static string FormatAddress(ulong address)
    {
        return FormattableString.Invariant($"0x{address:x16}");
    }
}

