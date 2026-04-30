using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace SceneGltfComposer;

internal sealed class TerrainComposer
{
    private const int ClusterGridSize = 32;
    private const float DefaultMetersPerPixel = 2.0f;
    private const int ArrayBufferTarget = 34962;
    private const int ElementArrayBufferTarget = 34963;

    private readonly GltfRoot scene;
    private readonly MemoryStream bufferStream;
    private readonly string assetRootPath;
    private readonly string terrainOutputDirectoryPath;
    private readonly Dictionary<string, TerrainBlockAttachment> terrainBlocksByAssetPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, object?>?> terrainRenderExtrasByAssetPath = new(StringComparer.OrdinalIgnoreCase);
    private int? terrainMaterialIndex;

    public TerrainComposer(GltfRoot scene, MemoryStream bufferStream, string assetRootPath, string outputPath)
    {
        this.scene = scene;
        this.bufferStream = bufferStream;
        this.assetRootPath = assetRootPath;
        string outputDirectory = Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory();
        string outputName = Path.GetFileNameWithoutExtension(outputPath);
        terrainOutputDirectoryPath = Path.Combine(outputDirectory, outputName + "_terrain");
    }

    public void ApplyToNode(GltfNode gltfNode, CompactSceneNode compactNode)
    {
        if (string.Equals(compactNode.ClassName, "TerrainBlock", StringComparison.Ordinal))
        {
            TerrainBlockAttachment? attachment = BuildTerrainBlockAttachment(compactNode);
            if (attachment is null)
            {
                return;
            }

            gltfNode.Mesh = attachment.MeshIndex;
            gltfNode.Extras = MergeExtras(gltfNode.Extras, attachment.Extras);
            return;
        }

        if (string.Equals(compactNode.ClassName, "TerrainRender", StringComparison.Ordinal))
        {
            Dictionary<string, object?>? extras = ExportTerrainRenderMaps(compactNode);
            if (extras is not null)
            {
                gltfNode.Extras = MergeExtras(gltfNode.Extras, extras);
            }
        }
    }

    private TerrainBlockAttachment? BuildTerrainBlockAttachment(CompactSceneNode compactNode)
    {
        string? assetPath = CompactNodePropertyReader.GetString(compactNode.Properties, "filePtr") ??
                            CompactNodePropertyReader.GetString(compactNode.Properties, "filePath");
        if (string.IsNullOrWhiteSpace(assetPath))
        {
            return null;
        }

        if (terrainBlocksByAssetPath.TryGetValue(assetPath, out TerrainBlockAttachment? cached))
        {
            return cached;
        }

        string sourcePath = ResolveAssetPath(assetPath);
        TerrainPatchData patch = TerrainFileParser.ReadPatch(sourcePath);
        ExportedTerrainMaps maps = ExportTerrainPatchMaps(compactNode.Name ?? Path.GetFileNameWithoutExtension(assetPath), patch, assetPath);
        int meshIndex = BuildTerrainMesh(compactNode.Name ?? Path.GetFileNameWithoutExtension(assetPath), patch);

        TerrainBlockAttachment attachment = new(
            meshIndex,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["foxTerrain"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["sourcePath"] = assetPath,
                    ["type"] = "terrainBlock",
                    ["width"] = patch.Width,
                    ["height"] = patch.Height,
                    ["metersPerPixel"] = patch.MetersPerPixel,
                    ["worldSize"] = patch.WorldSizeMeters,
                    ["minHeight"] = patch.MinHeight,
                    ["maxHeight"] = patch.MaxHeight,
                    ["heightMapPng"] = maps.HeightPreviewRelativePath,
                    ["heightMapRaw"] = maps.HeightRawRelativePath,
                    ["comboMapPng"] = maps.ComboRelativePath,
                    ["metadata"] = maps.MetadataRelativePath,
                },
            });

        terrainBlocksByAssetPath.Add(assetPath, attachment);
        return attachment;
    }

    private Dictionary<string, object?>? ExportTerrainRenderMaps(CompactSceneNode compactNode)
    {
        string? assetPath = CompactNodePropertyReader.GetString(compactNode.Properties, "filePtr") ??
                            CompactNodePropertyReader.GetString(compactNode.Properties, "filePath");
        if (string.IsNullOrWhiteSpace(assetPath))
        {
            return null;
        }

        if (terrainRenderExtrasByAssetPath.TryGetValue(assetPath, out Dictionary<string, object?>? cached))
        {
            return cached;
        }

        string sourcePath = ResolveAssetPath(assetPath);
        TerrainBaseLayoutData baseLayout = TerrainFileParser.ReadBaseLayout(sourcePath);
        ExportedTerrainMaps maps = ExportTerrainBaseLayoutMaps(compactNode.Name ?? Path.GetFileNameWithoutExtension(assetPath), baseLayout, assetPath);

        Dictionary<string, object?> extras = new(StringComparer.Ordinal)
        {
            ["foxTerrain"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["sourcePath"] = assetPath,
                ["type"] = "terrainRender",
                ["width"] = baseLayout.Width,
                ["height"] = baseLayout.Height,
                ["metersPerPixel"] = baseLayout.MetersPerPixel,
                ["worldSize"] = baseLayout.WorldSizeMeters,
                ["minHeight"] = baseLayout.MinHeight,
                ["maxHeight"] = baseLayout.MaxHeight,
                ["heightMapPng"] = maps.HeightPreviewRelativePath,
                ["heightMapRaw"] = maps.HeightRawRelativePath,
                ["comboMapPng"] = maps.ComboRelativePath,
                ["metadata"] = maps.MetadataRelativePath,
            },
        };

        terrainRenderExtrasByAssetPath.Add(assetPath, extras);
        return extras;
    }

    private ExportedTerrainMaps ExportTerrainPatchMaps(string nodeName, TerrainPatchData patch, string sourceAssetPath)
    {
        string baseName = SanitizeFileName(nodeName);
        Directory.CreateDirectory(terrainOutputDirectoryPath);

        string heightPreviewPath = Path.Combine(terrainOutputDirectoryPath, baseName + ".height.png");
        string heightRawPath = Path.Combine(terrainOutputDirectoryPath, baseName + ".height.r32");
        string comboPath = Path.Combine(terrainOutputDirectoryPath, baseName + ".combo.png");
        string metadataPath = Path.Combine(terrainOutputDirectoryPath, baseName + ".terrain.json");

        WriteHeightPreview(heightPreviewPath, patch.Width, patch.Height, patch.HeightSamples, patch.MinHeight, patch.MaxHeight);
        WriteHeightRaw(heightRawPath, patch.HeightSamples);
        WriteComboPreview(comboPath, patch.Width, patch.Height, patch.ComboTexture);
        WriteMetadata(metadataPath, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["sourcePath"] = sourceAssetPath,
            ["type"] = "terrainBlock",
            ["width"] = patch.Width,
            ["height"] = patch.Height,
            ["metersPerPixel"] = patch.MetersPerPixel,
            ["worldSize"] = patch.WorldSizeMeters,
            ["minHeight"] = patch.MinHeight,
            ["maxHeight"] = patch.MaxHeight,
            ["clusterMaterialIds"] = patch.MaterialIds,
            ["clusterConfigurationIds"] = patch.ConfigurationIds,
            ["clusterMinHeights"] = patch.ClusterMinHeights,
            ["clusterMaxHeights"] = patch.ClusterMaxHeights,
        });

        return CreateExportedMaps(heightPreviewPath, heightRawPath, comboPath, metadataPath);
    }

    private ExportedTerrainMaps ExportTerrainBaseLayoutMaps(string nodeName, TerrainBaseLayoutData baseLayout, string sourceAssetPath)
    {
        string baseName = SanitizeFileName(nodeName);
        Directory.CreateDirectory(terrainOutputDirectoryPath);

        string heightPreviewPath = Path.Combine(terrainOutputDirectoryPath, baseName + ".base.height.png");
        string heightRawPath = Path.Combine(terrainOutputDirectoryPath, baseName + ".base.height.r32");
        string comboPath = Path.Combine(terrainOutputDirectoryPath, baseName + ".base.combo.png");
        string metadataPath = Path.Combine(terrainOutputDirectoryPath, baseName + ".base.terrain.json");

        WriteHeightPreview(heightPreviewPath, baseLayout.Width, baseLayout.Height, baseLayout.HeightSamples, baseLayout.MinHeight, baseLayout.MaxHeight);
        WriteHeightRaw(heightRawPath, baseLayout.HeightSamples);
        WriteComboPreview(comboPath, baseLayout.Width, baseLayout.Height, baseLayout.ComboTexture);
        WriteMetadata(metadataPath, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["sourcePath"] = sourceAssetPath,
            ["type"] = "terrainRender",
            ["width"] = baseLayout.Width,
            ["height"] = baseLayout.Height,
            ["metersPerPixel"] = baseLayout.MetersPerPixel,
            ["worldSize"] = baseLayout.WorldSizeMeters,
            ["minHeight"] = baseLayout.MinHeight,
            ["maxHeight"] = baseLayout.MaxHeight,
            ["materialIds"] = baseLayout.MaterialIds,
            ["configurationIds"] = baseLayout.ConfigurationIds,
            ["clusterMinHeights"] = baseLayout.ClusterMinHeights,
            ["clusterMaxHeights"] = baseLayout.ClusterMaxHeights,
        });

        return CreateExportedMaps(heightPreviewPath, heightRawPath, comboPath, metadataPath);
    }

    private ExportedTerrainMaps CreateExportedMaps(string heightPreviewPath, string heightRawPath, string comboPath, string metadataPath)
    {
        string baseDirectory = Path.GetDirectoryName(terrainOutputDirectoryPath) ?? Directory.GetCurrentDirectory();
        return new ExportedTerrainMaps(
            Path.GetRelativePath(baseDirectory, heightPreviewPath).Replace('\\', '/'),
            Path.GetRelativePath(baseDirectory, heightRawPath).Replace('\\', '/'),
            Path.GetRelativePath(baseDirectory, comboPath).Replace('\\', '/'),
            Path.GetRelativePath(baseDirectory, metadataPath).Replace('\\', '/'));
    }

    private int BuildTerrainMesh(string meshName, TerrainPatchData patch)
    {
        int vertexCount = checked(patch.Width * patch.Height);
        float[] positions = new float[vertexCount * 3];
        float[] normals = new float[vertexCount * 3];
        float[] uvs = new float[vertexCount * 2];
        byte[] colors = new byte[vertexCount * 4];

        float worldSize = patch.WorldSizeMeters;
        float stepX = patch.Width > 1 ? worldSize / (patch.Width - 1) : 0.0f;
        float stepZ = patch.Height > 1 ? worldSize / (patch.Height - 1) : 0.0f;

        for (int z = 0; z < patch.Height; z++)
        {
            for (int x = 0; x < patch.Width; x++)
            {
                int vertexIndex = z * patch.Width + x;
                int positionOffset = vertexIndex * 3;
                positions[positionOffset] = x * stepX;
                positions[positionOffset + 1] = patch.HeightSamples[vertexIndex];
                positions[positionOffset + 2] = z * stepZ;

                int uvOffset = vertexIndex * 2;
                uvs[uvOffset] = patch.Width > 1 ? (float)x / (patch.Width - 1) : 0.0f;
                uvs[uvOffset + 1] = patch.Height > 1 ? 1.0f - (float)z / (patch.Height - 1) : 0.0f;

                int colorOffset = vertexIndex * 4;
                if (patch.ComboTexture.Length >= colorOffset + 4)
                {
                    colors[colorOffset] = patch.ComboTexture[colorOffset];
                    colors[colorOffset + 1] = patch.ComboTexture[colorOffset + 1];
                    colors[colorOffset + 2] = patch.ComboTexture[colorOffset + 2];
                    colors[colorOffset + 3] = patch.ComboTexture[colorOffset + 3];
                }
                else
                {
                    colors[colorOffset] = 255;
                    colors[colorOffset + 1] = 255;
                    colors[colorOffset + 2] = 255;
                    colors[colorOffset + 3] = 255;
                }
            }
        }

        for (int z = 0; z < patch.Height; z++)
        {
            for (int x = 0; x < patch.Width; x++)
            {
                int left = Math.Max(x - 1, 0);
                int right = Math.Min(x + 1, patch.Width - 1);
                int down = Math.Max(z - 1, 0);
                int up = Math.Min(z + 1, patch.Height - 1);

                float heightLeft = patch.HeightSamples[z * patch.Width + left];
                float heightRight = patch.HeightSamples[z * patch.Width + right];
                float heightDown = patch.HeightSamples[down * patch.Width + x];
                float heightUp = patch.HeightSamples[up * patch.Width + x];

                Vector3 normal = new(
                    heightLeft - heightRight,
                    2.0f * Math.Max(stepX, stepZ),
                    heightDown - heightUp);

                if (normal.LengthSquared() > 1e-12f)
                {
                    normal = Vector3.Normalize(normal);
                }
                else
                {
                    normal = Vector3.UnitY;
                }

                int normalOffset = (z * patch.Width + x) * 3;
                normals[normalOffset] = normal.X;
                normals[normalOffset + 1] = normal.Y;
                normals[normalOffset + 2] = normal.Z;
            }
        }

        int quadCount = checked((patch.Width - 1) * (patch.Height - 1));
        ushort[] indices = new ushort[quadCount * 6];
        int cursor = 0;
        for (int z = 0; z < patch.Height - 1; z++)
        {
            for (int x = 0; x < patch.Width - 1; x++)
            {
                ushort i0 = (ushort)(z * patch.Width + x);
                ushort i1 = (ushort)(i0 + 1);
                ushort i2 = (ushort)(i0 + patch.Width);
                ushort i3 = (ushort)(i2 + 1);

                indices[cursor++] = i0;
                indices[cursor++] = i2;
                indices[cursor++] = i1;
                indices[cursor++] = i1;
                indices[cursor++] = i2;
                indices[cursor++] = i3;
            }
        }

        float[] positionMin = [0.0f, patch.MinHeight, 0.0f];
        float[] positionMax = [worldSize, patch.MaxHeight, worldSize];

        int positionAccessor = AddFloatAccessor(positions, vertexCount, "VEC3", ArrayBufferTarget, positionMin, positionMax);
        int normalAccessor = AddFloatAccessor(normals, vertexCount, "VEC3", ArrayBufferTarget);
        int uvAccessor = AddFloatAccessor(uvs, vertexCount, "VEC2", ArrayBufferTarget);
        int colorAccessor = AddByteAccessor(colors, vertexCount, "VEC4", normalized: true, ArrayBufferTarget);
        int indexAccessor = AddUInt16Accessor(indices, indices.Length, "SCALAR", ElementArrayBufferTarget);

        int meshIndex = scene.Meshes.Count;
        scene.Meshes.Add(new GltfMesh
        {
            Name = meshName,
            Primitives =
            [
                new GltfPrimitive
                {
                    Attributes = new Dictionary<string, int>(StringComparer.Ordinal)
                    {
                        ["POSITION"] = positionAccessor,
                        ["NORMAL"] = normalAccessor,
                        ["TEXCOORD_0"] = uvAccessor,
                        ["COLOR_0"] = colorAccessor,
                    },
                    Indices = indexAccessor,
                    Material = EnsureTerrainMaterial(),
                    Mode = 4,
                },
            ],
        });

        return meshIndex;
    }

    private int EnsureTerrainMaterial()
    {
        if (terrainMaterialIndex.HasValue)
        {
            return terrainMaterialIndex.Value;
        }

        terrainMaterialIndex = scene.Materials.Count;
        scene.Materials.Add(new GltfMaterial
        {
            Name = "terrain_preview",
            DoubleSided = false,
            PbrMetallicRoughness = new GltfPbrMetallicRoughness
            {
                BaseColorFactor = [1.0f, 1.0f, 1.0f, 1.0f],
                MetallicFactor = 0.0f,
                RoughnessFactor = 1.0f,
            },
        });

        return terrainMaterialIndex.Value;
    }

    private int AddFloatAccessor(float[] values, int count, string type, int target, float[]? min = null, float[]? max = null)
    {
        byte[] bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        int bufferView = AddBufferView(bytes, target);
        int accessorIndex = scene.Accessors.Count;
        scene.Accessors.Add(new GltfAccessor
        {
            BufferView = bufferView,
            ComponentType = 5126,
            Count = count,
            Type = type,
            Min = min,
            Max = max,
        });
        return accessorIndex;
    }

    private int AddByteAccessor(byte[] values, int count, string type, bool normalized, int target)
    {
        int bufferView = AddBufferView(values, target);
        int accessorIndex = scene.Accessors.Count;
        scene.Accessors.Add(new GltfAccessor
        {
            BufferView = bufferView,
            ComponentType = 5121,
            Count = count,
            Type = type,
            Normalized = normalized,
        });
        return accessorIndex;
    }

    private int AddUInt16Accessor(ushort[] values, int count, string type, int target)
    {
        byte[] bytes = new byte[values.Length * sizeof(ushort)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        int bufferView = AddBufferView(bytes, target);
        int accessorIndex = scene.Accessors.Count;
        scene.Accessors.Add(new GltfAccessor
        {
            BufferView = bufferView,
            ComponentType = 5123,
            Count = count,
            Type = type,
        });
        return accessorIndex;
    }

    private int AddBufferView(byte[] bytes, int target)
    {
        int byteOffset = AppendBinary(bytes);
        int bufferViewIndex = scene.BufferViews.Count;
        scene.BufferViews.Add(new GltfBufferView
        {
            Buffer = 0,
            ByteOffset = byteOffset,
            ByteLength = bytes.Length,
            Target = target,
        });
        return bufferViewIndex;
    }

    private int AppendBinary(byte[] bytes)
    {
        int alignedOffset = GltfBinary.Align4(checked((int)bufferStream.Length));
        while (bufferStream.Length < alignedOffset)
        {
            bufferStream.WriteByte(0);
        }

        bufferStream.Write(bytes, 0, bytes.Length);
        return alignedOffset;
    }

    private string ResolveAssetPath(string assetPath)
    {
        string relativePath = assetPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(assetRootPath, relativePath));
    }

    private static Dictionary<string, object?> MergeExtras(
        Dictionary<string, object?>? existing,
        Dictionary<string, object?> extras)
    {
        if (existing is null || existing.Count == 0)
        {
            return extras;
        }

        Dictionary<string, object?> merged = new(existing, StringComparer.Ordinal);
        foreach (KeyValuePair<string, object?> pair in extras)
        {
            merged[pair.Key] = pair.Value;
        }

        return merged;
    }

    private static string SanitizeFileName(string value)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch));
    }

    private static void WriteHeightPreview(string outputPath, int width, int height, IReadOnlyList<float> values, float minValue, float maxValue)
    {
        float range = Math.Max(maxValue - minValue, 1e-6f);
        using Image<L16> image = new(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float normalized = Math.Clamp((values[y * width + x] - minValue) / range, 0.0f, 1.0f);
                ushort encoded = (ushort)Math.Round(normalized * ushort.MaxValue);
                image[x, y] = new L16(encoded);
            }
        }

        image.SaveAsPng(outputPath);
    }

    private static void WriteHeightRaw(string outputPath, float[] values)
    {
        byte[] bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        File.WriteAllBytes(outputPath, bytes);
    }

    private static void WriteComboPreview(string outputPath, int width, int height, byte[] rgba)
    {
        byte[] imageData = rgba.Length == width * height * 4
            ? rgba
            : ResizeOrPadRgba(rgba, width * height * 4);

        using Image<Rgba32> image = Image.LoadPixelData<Rgba32>(imageData, width, height);
        image.SaveAsPng(outputPath);
    }

    private static byte[] ResizeOrPadRgba(byte[] source, int requiredLength)
    {
        byte[] result = new byte[requiredLength];
        Array.Copy(source, result, Math.Min(source.Length, result.Length));
        for (int offset = 3; offset < result.Length; offset += 4)
        {
            if (result[offset] == 0)
            {
                result[offset] = 255;
            }
        }

        return result;
    }

    private static void WriteMetadata(string outputPath, Dictionary<string, object?> metadata)
    {
        File.WriteAllText(outputPath, JsonSerializer.Serialize(metadata, GltfJson.SerializerOptions));
    }
}

internal static class CompactNodePropertyReader
{
    public static string? GetString(Dictionary<string, JsonElement>? properties, string propertyName)
    {
        if (properties is null || !properties.TryGetValue(propertyName, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}

internal static class TerrainFileParser
{
    private const int ClusterGridSize = 32;
    private const float DefaultMetersPerPixel = 2.0f;

    public static TerrainPatchData ReadPatch(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        FoxDataNodeInfo root = ReadRootNodeSequence(data);
        if (root.Children.Count < 2)
        {
            throw new InvalidDataException($"Terrain patch '{path}' does not contain enough nodes.");
        }

        FoxDataNodeInfo heightNode = root.Children[0];
        FoxDataNodeInfo comboNode = root.Children[1];

        uint pitch = GetRequiredUIntAttribute(heightNode, "pitch");
        int width = checked((int)(pitch * ClusterGridSize));
        int height = width;
        float[] heights = ReadClusteredFloatGrid(data, heightNode.DataAbsoluteOffset, heightNode.DataSize, checked((int)pitch));
        byte[] combo = ReadClusteredRgbaGrid(data, comboNode.DataAbsoluteOffset, comboNode.DataSize, checked((int)pitch));

        FoxDataNodeInfo? editNode = root.Children.FirstOrDefault(node => node.Name.Equals("editParam", StringComparison.Ordinal));
        uint[] materialIds = Array.Empty<uint>();
        uint[] configurationIds = Array.Empty<uint>();
        float[] clusterMinHeights = Array.Empty<float>();
        float[] clusterMaxHeights = Array.Empty<float>();
        if (editNode is not null)
        {
            materialIds = ReadChildUIntArray(data, editNode, "materialIds");
            configurationIds = ReadChildUIntArray(data, editNode, "configrationIds");
            clusterMinHeights = ReadChildFloatArray(data, editNode, "minHeight");
            clusterMaxHeights = ReadChildFloatArray(data, editNode, "maxHeight");
        }

        float minHeight = heights.Min();
        float maxHeight = heights.Max();
        float worldSize = pitch * ClusterGridSize * DefaultMetersPerPixel;

        return new TerrainPatchData(
            width,
            height,
            DefaultMetersPerPixel,
            worldSize,
            heights,
            combo,
            materialIds,
            configurationIds,
            clusterMinHeights,
            clusterMaxHeights,
            minHeight,
            maxHeight);
    }

    public static TerrainBaseLayoutData ReadBaseLayout(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        FoxDataNodeInfo root = ReadRootNodeSequence(data);
        FoxDataNodeInfo paramNode = root.Children.FirstOrDefault(node => node.Name.Equals("param", StringComparison.Ordinal))
            ?? throw new InvalidDataException($"Terrain layout '{path}' is missing a param node.");
        FoxDataNodeInfo heightNode = root.Children.FirstOrDefault(node => node.Name.Equals("heightMap", StringComparison.Ordinal))
            ?? throw new InvalidDataException($"Terrain layout '{path}' is missing a heightMap node.");
        FoxDataNodeInfo comboNode = root.Children.FirstOrDefault(node => node.Name.Equals("comboTexture", StringComparison.Ordinal))
            ?? throw new InvalidDataException($"Terrain layout '{path}' is missing a comboTexture node.");

        uint widthHigh = GetRequiredUIntAttribute(paramNode, "width");
        uint heightHigh = GetRequiredUIntAttribute(paramNode, "height");
        uint highPerLow = GetRequiredUIntAttribute(paramNode, "highPerLow");
        float gridDistance = GetRequiredFloatAttribute(paramNode, "gridDistance");

        int width = checked((int)(widthHigh / highPerLow));
        int height = checked((int)(heightHigh / highPerLow));

        float[] heights = ReadFloatArray(data, heightNode.DataAbsoluteOffset, heightNode.DataSize);
        byte[] combo = ReadBytes(data, comboNode.DataAbsoluteOffset, comboNode.DataSize);
        uint[] materialIds = ReadChildUIntArray(data, root, "materialIds");
        uint[] configurationIds = ReadChildUIntArray(data, root, "configrationIds");
        float[] clusterMinHeights = ReadChildFloatArray(data, root, "minHeight");
        float[] clusterMaxHeights = ReadChildFloatArray(data, root, "maxHeight");

        float minHeight = heights.Min();
        float maxHeight = heights.Max();
        float metersPerPixel = gridDistance;
        float worldSize = (width - 1) * metersPerPixel;

        return new TerrainBaseLayoutData(
            width,
            height,
            metersPerPixel,
            worldSize,
            heights,
            combo,
            materialIds,
            configurationIds,
            clusterMinHeights,
            clusterMaxHeights,
            minHeight,
            maxHeight);
    }

    private static FoxDataNodeInfo ReadRootNodeSequence(byte[] data)
    {
        if (data.Length < 8)
        {
            throw new InvalidDataException("FOX data file is too small.");
        }

        int nodesOffset = ReadInt32(data, 4);
        if (nodesOffset <= 0 || nodesOffset >= data.Length)
        {
            throw new InvalidDataException("FOX data node table offset is invalid.");
        }

        return new FoxDataNodeInfo("<root>", -1, 0, 0, [], ReadNodeSequence(data, nodesOffset));
    }

    private static List<FoxDataNodeInfo> ReadNodeSequence(byte[] data, int startOffset)
    {
        List<FoxDataNodeInfo> nodes = new();
        HashSet<int> visited = new();
        int offset = startOffset;
        while (offset > 0 && offset + 0x30 <= data.Length && visited.Add(offset))
        {
            string name = ReadRelativeString(data, offset);
            int dataOffset = ReadInt32(data, offset + 12);
            int dataSize = ReadInt32(data, offset + 16);
            int childOffset = ReadInt32(data, offset + 24);
            int nextOffset = ReadInt32(data, offset + 32);
            int attributeOffset = ReadInt32(data, offset + 36);

            List<FoxDataAttributeInfo> attributes = attributeOffset == 0
                ? []
                : ReadAttributes(data, offset + attributeOffset);
            List<FoxDataNodeInfo> children = childOffset == 0
                ? []
                : ReadNodeSequence(data, offset + childOffset);

            nodes.Add(new FoxDataNodeInfo(
                name,
                offset,
                dataOffset == 0 ? 0 : offset + dataOffset,
                dataSize,
                attributes,
                children));

            offset = nextOffset == 0 ? 0 : offset + nextOffset;
        }

        return nodes;
    }

    private static List<FoxDataAttributeInfo> ReadAttributes(byte[] data, int startOffset)
    {
        List<FoxDataAttributeInfo> attributes = new();
        HashSet<int> visited = new();
        int offset = startOffset;
        while (offset > 0 && offset + 0x10 <= data.Length && visited.Add(offset))
        {
            ushort type = ReadUInt16(data, offset);
            short nextOffset = (short)ReadUInt16(data, offset + 2);
            string name = ReadRelativeString(data, offset + 4);
            object? value = type switch
            {
                0 => (uint)ReadInt32(data, offset + 12),
                1 => ReadRelativeString(data, offset + 12),
                2 => ReadSingle(data, offset + 12),
                _ => null,
            };
            attributes.Add(new FoxDataAttributeInfo(name, type, value));
            offset = nextOffset == 0 ? 0 : offset + nextOffset;
        }

        return attributes;
    }

    private static string ReadRelativeString(byte[] data, int offset)
    {
        int stringOffset = ReadInt32(data, offset + 4);
        if (stringOffset == 0)
        {
            return string.Empty;
        }

        int absoluteOffset = offset + stringOffset;
        int terminatorIndex = Array.IndexOf<byte>(data, 0, absoluteOffset);
        if (terminatorIndex < 0)
        {
            terminatorIndex = data.Length;
        }

        return System.Text.Encoding.ASCII.GetString(data, absoluteOffset, terminatorIndex - absoluteOffset);
    }

    private static float[] ReadFloatArray(byte[] data, int offset, int size)
    {
        float[] values = new float[size / sizeof(float)];
        Buffer.BlockCopy(data, offset, values, 0, size);
        return values;
    }

    private static float[] ReadClusteredFloatGrid(byte[] data, int offset, int size, int pitch)
    {
        float[] clusteredValues = ReadFloatArray(data, offset, size);
        int clusterSampleCount = checked(ClusterGridSize * ClusterGridSize);
        int expectedSampleCount = checked(pitch * pitch * clusterSampleCount);
        if (clusteredValues.Length != expectedSampleCount)
        {
            return clusteredValues;
        }

        int width = checked(pitch * ClusterGridSize);
        float[] values = new float[expectedSampleCount];
        int sourceOffset = 0;
        for (int clusterZ = 0; clusterZ < pitch; clusterZ++)
        {
            for (int clusterX = 0; clusterX < pitch; clusterX++)
            {
                for (int localZ = 0; localZ < ClusterGridSize; localZ++)
                {
                    int destinationOffset = ((clusterZ * ClusterGridSize) + localZ) * width + (clusterX * ClusterGridSize);
                    Array.Copy(clusteredValues, sourceOffset + (localZ * ClusterGridSize), values, destinationOffset, ClusterGridSize);
                }

                sourceOffset += clusterSampleCount;
            }
        }

        return values;
    }

    private static uint[] ReadChildUIntArray(byte[] data, FoxDataNodeInfo parent, string childName)
    {
        FoxDataNodeInfo? child = parent.Children.FirstOrDefault(node => node.Name.Equals(childName, StringComparison.Ordinal));
        if (child is null || child.DataSize == 0)
        {
            return Array.Empty<uint>();
        }

        uint[] values = new uint[child.DataSize / sizeof(uint)];
        Buffer.BlockCopy(data, child.DataAbsoluteOffset, values, 0, child.DataSize);
        return values;
    }

    private static float[] ReadChildFloatArray(byte[] data, FoxDataNodeInfo parent, string childName)
    {
        FoxDataNodeInfo? child = parent.Children.FirstOrDefault(node => node.Name.Equals(childName, StringComparison.Ordinal));
        if (child is null || child.DataSize == 0)
        {
            return Array.Empty<float>();
        }

        return ReadFloatArray(data, child.DataAbsoluteOffset, child.DataSize);
    }

    private static byte[] ReadBytes(byte[] data, int offset, int size)
    {
        byte[] bytes = new byte[size];
        Buffer.BlockCopy(data, offset, bytes, 0, size);
        return bytes;
    }

    private static byte[] ReadClusteredRgbaGrid(byte[] data, int offset, int size, int pitch)
    {
        byte[] clusteredValues = ReadBytes(data, offset, size);
        int clusterPixelCount = checked(ClusterGridSize * ClusterGridSize);
        int expectedPixelCount = checked(pitch * pitch * clusterPixelCount);
        int expectedByteCount = checked(expectedPixelCount * 4);
        if (clusteredValues.Length != expectedByteCount)
        {
            return clusteredValues;
        }

        int width = checked(pitch * ClusterGridSize);
        int destinationStride = checked(width * 4);
        byte[] values = new byte[expectedByteCount];
        int sourceOffset = 0;
        for (int clusterZ = 0; clusterZ < pitch; clusterZ++)
        {
            for (int clusterX = 0; clusterX < pitch; clusterX++)
            {
                for (int localZ = 0; localZ < ClusterGridSize; localZ++)
                {
                    int destinationOffset = (((clusterZ * ClusterGridSize) + localZ) * destinationStride) + (clusterX * ClusterGridSize * 4);
                    Buffer.BlockCopy(clusteredValues, sourceOffset + (localZ * ClusterGridSize * 4), values, destinationOffset, ClusterGridSize * 4);
                }

                sourceOffset += clusterPixelCount * 4;
            }
        }

        return values;
    }

    private static uint GetRequiredUIntAttribute(FoxDataNodeInfo node, string attributeName)
    {
        FoxDataAttributeInfo attribute = node.Attributes.FirstOrDefault(attribute => attribute.Name.Equals(attributeName, StringComparison.Ordinal))
            ?? throw new InvalidDataException($"Node '{node.Name}' is missing required attribute '{attributeName}'.");
        return attribute.Value switch
        {
            uint unsigned => unsigned,
            int signed => checked((uint)signed),
            _ => throw new InvalidDataException($"Attribute '{attributeName}' on node '{node.Name}' is not an unsigned integer."),
        };
    }

    private static float GetRequiredFloatAttribute(FoxDataNodeInfo node, string attributeName)
    {
        FoxDataAttributeInfo attribute = node.Attributes.FirstOrDefault(attribute => attribute.Name.Equals(attributeName, StringComparison.Ordinal))
            ?? throw new InvalidDataException($"Node '{node.Name}' is missing required attribute '{attributeName}'.");
        return attribute.Value switch
        {
            float value => value,
            _ => throw new InvalidDataException($"Attribute '{attributeName}' on node '{node.Name}' is not a float."),
        };
    }

    private static int ReadInt32(byte[] data, int offset) => MemoryMarshal.Read<int>(data.AsSpan(offset, sizeof(int)));

    private static ushort ReadUInt16(byte[] data, int offset) => MemoryMarshal.Read<ushort>(data.AsSpan(offset, sizeof(ushort)));

    private static float ReadSingle(byte[] data, int offset) => MemoryMarshal.Read<float>(data.AsSpan(offset, sizeof(float)));
}

internal sealed record TerrainBlockAttachment(int MeshIndex, Dictionary<string, object?> Extras);

internal sealed record ExportedTerrainMaps(
    string HeightPreviewRelativePath,
    string HeightRawRelativePath,
    string ComboRelativePath,
    string MetadataRelativePath);

internal sealed record TerrainPatchData(
    int Width,
    int Height,
    float MetersPerPixel,
    float WorldSizeMeters,
    float[] HeightSamples,
    byte[] ComboTexture,
    uint[] MaterialIds,
    uint[] ConfigurationIds,
    float[] ClusterMinHeights,
    float[] ClusterMaxHeights,
    float MinHeight,
    float MaxHeight);

internal sealed record TerrainBaseLayoutData(
    int Width,
    int Height,
    float MetersPerPixel,
    float WorldSizeMeters,
    float[] HeightSamples,
    byte[] ComboTexture,
    uint[] MaterialIds,
    uint[] ConfigurationIds,
    float[] ClusterMinHeights,
    float[] ClusterMaxHeights,
    float MinHeight,
    float MaxHeight);

internal sealed record FoxDataNodeInfo(
    string Name,
    int Offset,
    int DataAbsoluteOffset,
    int DataSize,
    List<FoxDataAttributeInfo> Attributes,
    List<FoxDataNodeInfo> Children);

internal sealed record FoxDataAttributeInfo(string Name, ushort Type, object? Value);
