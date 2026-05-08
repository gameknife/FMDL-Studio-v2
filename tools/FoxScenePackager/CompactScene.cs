using System.Text.Json;

namespace FoxScenePackager;

internal sealed class CompactScene
{
    public required string SourceFile { get; init; }
    public string? SourceAssetPath { get; init; }
    public string? AssetRootPath { get; init; }
    public CompactSceneSummary? Summary { get; init; }
    public required List<CompactSceneFile> Files { get; init; }
    public List<string>? FmdlFiles { get; init; }

    public static CompactScene Load(string path)
    {
        JsonSerializerOptions options = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        using FileStream stream = File.OpenRead(path);
        CompactScene? scene = JsonSerializer.Deserialize<CompactScene>(stream, options);
        if (scene is null)
        {
            throw new InvalidDataException(FormattableString.Invariant($"Unable to parse compact scene JSON '{path}'."));
        }

        return scene;
    }
}

internal sealed class CompactSceneSummary
{
    public int Fox2FileCount { get; init; }
    public int NodeCount { get; init; }
    public int FmdlCount { get; init; }
}

internal sealed class CompactSceneFile
{
    public required string Path { get; init; }
    public string? DiscoveredFrom { get; init; }
    public List<string>? FmdlFiles { get; init; }
    public List<CompactSceneNode>? RootNodes { get; init; }
}

internal sealed class CompactSceneNode
{
    public string? ClassName { get; init; }
    public string? Name { get; init; }
    public CompactNodeTransform? Transform { get; init; }
    public List<string>? FmdlPaths { get; init; }
    public Dictionary<string, JsonElement>? Properties { get; init; }
    public List<CompactSceneNode>? Children { get; init; }
}

internal sealed class CompactNodeTransform
{
    public CompactVector3? Translation { get; init; }
    public CompactQuaternion? RotationQuaternion { get; init; }
    public CompactVector3? Scale { get; init; }
    public CompactVector3? Shear { get; init; }
    public CompactVector3? Pivot { get; init; }
    public CompactVector3? PivotTranslation { get; init; }
}

internal sealed class CompactVector3
{
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }

    public float[] ToArray()
    {
        return [X, Y, Z];
    }
}

internal sealed class CompactQuaternion
{
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }
    public float W { get; init; }

    public float[] ToArray()
    {
        return [X, Y, Z, W];
    }
}
