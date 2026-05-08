using System.Text.Json;
using System.Text.Json.Serialization;

namespace FoxScenePackager;

internal static class FoxSceneJson
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static void Write(FoxSceneDocument scene, string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory());
        File.WriteAllText(outputPath, JsonSerializer.Serialize(scene, JsonOptions));
    }
}

internal sealed class FoxSceneDocument
{
    public string Schema { get; init; } = "https://fmdl.studio/schemas/foxscene-1.json";
    public int Version { get; init; } = 1;
    public string Generator { get; init; } = "FMDL Studio v2 FoxScenePackager";
    public required FoxSceneSource Source { get; init; }
    public FoxSceneCoordinateSystem CoordinateSystem { get; init; } = new();
    public required FoxSceneSummary Summary { get; init; }
    public List<FoxSceneAsset> Assets { get; init; } = [];
    public List<FoxSceneLayer> Layers { get; init; } = [];
    public List<string> RootNodes { get; init; } = [];
    public List<FoxSceneNode> Nodes { get; init; } = [];
    public List<string>? Warnings { get; init; }
}

internal sealed class FoxSceneSource
{
    public required string CompactScene { get; init; }
    public string? Fox2File { get; init; }
    public string? Fox2AssetPath { get; init; }
    public string? AssetRootPath { get; init; }
}

internal sealed class FoxSceneCoordinateSystem
{
    public string UpAxis { get; init; } = "Y";
    public string TransformConvention { get; init; } = "glTF TRS passthrough; node transforms match the compact FOX2 scene and exported model GLBs.";
}

internal sealed class FoxSceneSummary
{
    public int LayerCount { get; init; }
    public int NodeCount { get; init; }
    public int AssetCount { get; init; }
    public int PlacedAssetCount { get; init; }
    public int PlacementCount { get; init; }
    public int WarningCount { get; init; }
}

internal sealed class FoxSceneAsset
{
    public required string Id { get; init; }
    public string Kind { get; init; } = "model/gltf-binary";
    public required string Name { get; init; }
    public required string SourceFmdl { get; init; }
    public string? Uri { get; set; }
    public string? ResolvedSourcePath { get; set; }
    public long? ByteLength { get; set; }
    public int PlacementCount { get; set; }
    public bool? Missing { get; set; }
}

internal sealed class FoxSceneLayer
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string SourceFox2 { get; init; }
    public string? DiscoveredFrom { get; init; }
    public List<string> RootNodes { get; init; } = [];
}

internal sealed class FoxSceneNode
{
    public required string Id { get; init; }
    public required string LayerId { get; init; }
    public string? Name { get; init; }
    public string? ClassName { get; init; }
    public FoxSceneTransform? Transform { get; init; }
    public List<FoxSceneNodeAssetBinding>? Assets { get; init; }
    public Dictionary<string, JsonElement>? Properties { get; init; }
    public List<string>? Children { get; set; }
}

internal sealed class FoxSceneTransform
{
    public float[]? Translation { get; init; }
    public float[]? Rotation { get; init; }
    public float[]? Scale { get; init; }
    public float[]? Shear { get; init; }
    public float[]? Pivot { get; init; }
    public float[]? PivotTranslation { get; init; }
}

internal sealed class FoxSceneNodeAssetBinding
{
    public required string AssetId { get; init; }
    public required string Source { get; init; }
}

internal sealed record PackageBuildResult(
    FoxSceneDocument Scene,
    int ConvertedCount,
    int ReusedCount,
    int MissingCount,
    IReadOnlyList<string> Warnings);
