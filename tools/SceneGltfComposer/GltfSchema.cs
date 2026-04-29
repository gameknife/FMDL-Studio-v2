using System.Text.Json;
using System.Text.Json.Serialization;

namespace SceneGltfComposer;

internal sealed class GltfRoot
{
    public required GltfAsset Asset { get; set; }
    public int? Scene { get; set; }
    public List<GltfScene> Scenes { get; set; } = [];
    public List<GltfNode> Nodes { get; set; } = [];
    public List<GltfMesh> Meshes { get; set; } = [];
    public List<GltfAccessor> Accessors { get; set; } = [];
    public List<GltfBufferView> BufferViews { get; set; } = [];
    public List<GltfBuffer> Buffers { get; set; } = [];
    public List<GltfMaterial> Materials { get; set; } = [];
    public List<GltfImage> Images { get; set; } = [];
    public List<GltfTexture> Textures { get; set; } = [];
    public List<GltfSampler> Samplers { get; set; } = [];
    public List<GltfSkin> Skins { get; set; } = [];
}

internal sealed class GltfAsset
{
    public required string Generator { get; set; }
    public required string Version { get; set; }
}

internal sealed class GltfScene
{
    public List<int>? Nodes { get; set; }
}

internal sealed class GltfNode
{
    public string? Name { get; set; }
    public List<int>? Children { get; set; }
    public float[]? Translation { get; set; }
    public float[]? Rotation { get; set; }
    public float[]? Scale { get; set; }
    public int? Mesh { get; set; }
    public int? Skin { get; set; }
    public Dictionary<string, object?>? Extras { get; set; }
}

internal sealed class GltfMesh
{
    public string? Name { get; set; }
    public List<GltfPrimitive> Primitives { get; set; } = [];
}

internal sealed class GltfPrimitive
{
    public Dictionary<string, int> Attributes { get; set; } = new(StringComparer.Ordinal);
    public int Indices { get; set; }
    public int? Material { get; set; }
    public int Mode { get; set; }
}

internal sealed class GltfAccessor
{
    public int BufferView { get; set; }
    public int ComponentType { get; set; }
    public int Count { get; set; }
    public string Type { get; set; } = string.Empty;
    public int? ByteOffset { get; set; }
    public bool? Normalized { get; set; }
    public float[]? Min { get; set; }
    public float[]? Max { get; set; }
}

internal sealed class GltfBufferView
{
    public int Buffer { get; set; }
    public int ByteOffset { get; set; }
    public int ByteLength { get; set; }
    public int? ByteStride { get; set; }
    public int? Target { get; set; }
}

internal sealed class GltfBuffer
{
    public int ByteLength { get; set; }
    public string? Uri { get; set; }
}

internal sealed class GltfMaterial
{
    public string? Name { get; set; }
    public GltfPbrMetallicRoughness? PbrMetallicRoughness { get; set; }
    public bool? DoubleSided { get; set; }
    public string? AlphaMode { get; set; }
    public GltfNormalTextureInfo? NormalTexture { get; set; }
    public GltfOcclusionTextureInfo? OcclusionTexture { get; set; }
    public GltfTextureInfo? EmissiveTexture { get; set; }
    public float[]? EmissiveFactor { get; set; }
    public Dictionary<string, object?>? Extras { get; set; }
}

internal sealed class GltfPbrMetallicRoughness
{
    public float[]? BaseColorFactor { get; set; }
    public GltfTextureInfo? BaseColorTexture { get; set; }
    public float MetallicFactor { get; set; }
    public float RoughnessFactor { get; set; }
    public GltfTextureInfo? MetallicRoughnessTexture { get; set; }
}

internal sealed class GltfSkin
{
    public string? Name { get; set; }
    public int? InverseBindMatrices { get; set; }
    public List<int> Joints { get; set; } = [];
    public int? Skeleton { get; set; }
}

internal sealed class GltfImage
{
    public string? Name { get; set; }
    public int? BufferView { get; set; }
    public string? MimeType { get; set; }
    public string? Uri { get; set; }
}

internal sealed class GltfTexture
{
    public string? Name { get; set; }
    public int? Sampler { get; set; }
    public int Source { get; set; }
}

internal sealed class GltfSampler
{
    public int? MagFilter { get; set; }
    public int? MinFilter { get; set; }
    public int? WrapS { get; set; }
    public int? WrapT { get; set; }
}

internal class GltfTextureInfo
{
    public int Index { get; set; }
    public int? TexCoord { get; set; }
}

internal sealed class GltfNormalTextureInfo : GltfTextureInfo
{
    public float? Scale { get; set; }
}

internal sealed class GltfOcclusionTextureInfo : GltfTextureInfo
{
    public float? Strength { get; set; }
}

internal sealed record GltfDocument(GltfRoot Root, byte[] BinaryChunk);

internal static class GltfJson
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
}
