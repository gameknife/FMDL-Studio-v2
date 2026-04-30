using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SceneGltfComposer;

internal static class SceneAssetDeduplicator
{
    public static byte[] Optimize(GltfRoot scene, byte[] bufferData)
    {
        DeduplicateSamplers(scene, out int[] samplerMap);
        DeduplicateImages(scene, bufferData, out int[] imageMap);
        DeduplicateTextures(scene, samplerMap, imageMap, out int[] textureMap);
        DeduplicateMaterials(scene, textureMap, out int[] materialMap);
        RemapMeshMaterials(scene, materialMap);
        return CompactBinaryStorage(scene, bufferData);
    }

    private static void DeduplicateSamplers(GltfRoot scene, out int[] samplerMap)
    {
        samplerMap = new int[scene.Samplers.Count];
        Dictionary<string, int> canonicalIndexBySignature = new(StringComparer.Ordinal);
        List<GltfSampler> samplers = new();

        for (int index = 0; index < scene.Samplers.Count; index++)
        {
            GltfSampler sampler = scene.Samplers[index];
            string signature = GetSamplerSignature(sampler);
            if (!canonicalIndexBySignature.TryGetValue(signature, out int canonicalIndex))
            {
                canonicalIndex = samplers.Count;
                canonicalIndexBySignature.Add(signature, canonicalIndex);
                samplers.Add(new GltfSampler
                {
                    MagFilter = sampler.MagFilter,
                    MinFilter = sampler.MinFilter,
                    WrapS = sampler.WrapS,
                    WrapT = sampler.WrapT,
                });
            }

            samplerMap[index] = canonicalIndex;
        }

        scene.Samplers = samplers;
    }

    private static void DeduplicateImages(GltfRoot scene, byte[] bufferData, out int[] imageMap)
    {
        imageMap = new int[scene.Images.Count];
        Dictionary<string, int> canonicalIndexBySignature = new(StringComparer.Ordinal);
        List<GltfImage> images = new();

        for (int index = 0; index < scene.Images.Count; index++)
        {
            GltfImage image = scene.Images[index];
            string signature = GetImageSignature(image, scene.BufferViews, bufferData);
            if (!canonicalIndexBySignature.TryGetValue(signature, out int canonicalIndex))
            {
                canonicalIndex = images.Count;
                canonicalIndexBySignature.Add(signature, canonicalIndex);
                images.Add(new GltfImage
                {
                    Name = image.Name,
                    BufferView = image.BufferView,
                    MimeType = image.MimeType,
                    Uri = image.Uri,
                });
            }

            imageMap[index] = canonicalIndex;
        }

        scene.Images = images;
    }

    private static void DeduplicateTextures(GltfRoot scene, IReadOnlyList<int> samplerMap, IReadOnlyList<int> imageMap, out int[] textureMap)
    {
        textureMap = new int[scene.Textures.Count];
        Dictionary<string, int> canonicalIndexBySignature = new(StringComparer.Ordinal);
        List<GltfTexture> textures = new();

        for (int index = 0; index < scene.Textures.Count; index++)
        {
            GltfTexture texture = scene.Textures[index];
            int remappedSource = imageMap[texture.Source];
            int? remappedSampler = texture.Sampler.HasValue ? samplerMap[texture.Sampler.Value] : null;
            string signature = GetTextureSignature(remappedSampler, remappedSource);
            if (!canonicalIndexBySignature.TryGetValue(signature, out int canonicalIndex))
            {
                canonicalIndex = textures.Count;
                canonicalIndexBySignature.Add(signature, canonicalIndex);
                textures.Add(new GltfTexture
                {
                    Name = texture.Name,
                    Sampler = remappedSampler,
                    Source = remappedSource,
                });
            }

            textureMap[index] = canonicalIndex;
        }

        scene.Textures = textures;
    }

    private static void DeduplicateMaterials(GltfRoot scene, IReadOnlyList<int> textureMap, out int[] materialMap)
    {
        materialMap = new int[scene.Materials.Count];
        Dictionary<string, int> canonicalIndexBySignature = new(StringComparer.Ordinal);
        List<GltfMaterial> materials = new();

        for (int index = 0; index < scene.Materials.Count; index++)
        {
            GltfMaterial material = CloneMaterialWithRemappedTextures(scene.Materials[index], textureMap);
            string signature = GetMaterialSignature(material);
            if (!canonicalIndexBySignature.TryGetValue(signature, out int canonicalIndex))
            {
                canonicalIndex = materials.Count;
                canonicalIndexBySignature.Add(signature, canonicalIndex);
                materials.Add(material);
            }

            materialMap[index] = canonicalIndex;
        }

        scene.Materials = materials;
    }

    private static void RemapMeshMaterials(GltfRoot scene, IReadOnlyList<int> materialMap)
    {
        foreach (GltfMesh mesh in scene.Meshes)
        {
            foreach (GltfPrimitive primitive in mesh.Primitives)
            {
                if (primitive.Material.HasValue)
                {
                    primitive.Material = materialMap[primitive.Material.Value];
                }
            }
        }
    }

    private static byte[] CompactBinaryStorage(GltfRoot scene, byte[] bufferData)
    {
        HashSet<int> usedBufferViews = new();
        foreach (GltfAccessor accessor in scene.Accessors)
        {
            usedBufferViews.Add(accessor.BufferView);
        }

        foreach (GltfImage image in scene.Images)
        {
            if (image.BufferView.HasValue)
            {
                usedBufferViews.Add(image.BufferView.Value);
            }
        }

        MemoryStream compactedBuffer = new();
        int[] bufferViewMap = Enumerable.Repeat(-1, scene.BufferViews.Count).ToArray();
        List<GltfBufferView> compactedViews = new();

        for (int index = 0; index < scene.BufferViews.Count; index++)
        {
            if (!usedBufferViews.Contains(index))
            {
                continue;
            }

            GltfBufferView view = scene.BufferViews[index];
            int alignedOffset = GltfBinary.Align4(checked((int)compactedBuffer.Length));
            while (compactedBuffer.Length < alignedOffset)
            {
                compactedBuffer.WriteByte(0);
            }

            compactedBuffer.Write(bufferData, view.ByteOffset, view.ByteLength);
            bufferViewMap[index] = compactedViews.Count;
            compactedViews.Add(new GltfBufferView
            {
                Buffer = 0,
                ByteOffset = alignedOffset,
                ByteLength = view.ByteLength,
                ByteStride = view.ByteStride,
                Target = view.Target,
            });
        }

        foreach (GltfAccessor accessor in scene.Accessors)
        {
            accessor.BufferView = bufferViewMap[accessor.BufferView];
        }

        foreach (GltfImage image in scene.Images)
        {
            if (image.BufferView.HasValue)
            {
                image.BufferView = bufferViewMap[image.BufferView.Value];
            }
        }

        scene.BufferViews = compactedViews;
        if (scene.Buffers.Count == 0)
        {
            scene.Buffers.Add(new GltfBuffer { ByteLength = checked((int)compactedBuffer.Length) });
        }
        else
        {
            scene.Buffers[0].ByteLength = checked((int)compactedBuffer.Length);
        }

        return compactedBuffer.ToArray();
    }

    internal static GltfMaterial CloneMaterialWithRemappedTextures(GltfMaterial material, IReadOnlyList<int> textureMap)
    {
        return new GltfMaterial
        {
            Name = material.Name,
            PbrMetallicRoughness = material.PbrMetallicRoughness is null
                ? null
                : new GltfPbrMetallicRoughness
                {
                    BaseColorFactor = CloneArray(material.PbrMetallicRoughness.BaseColorFactor),
                    BaseColorTexture = RemapTextureInfo(material.PbrMetallicRoughness.BaseColorTexture, textureMap),
                    MetallicFactor = material.PbrMetallicRoughness.MetallicFactor,
                    RoughnessFactor = material.PbrMetallicRoughness.RoughnessFactor,
                    MetallicRoughnessTexture = RemapTextureInfo(material.PbrMetallicRoughness.MetallicRoughnessTexture, textureMap),
                },
            DoubleSided = material.DoubleSided,
            AlphaMode = material.AlphaMode,
            NormalTexture = RemapNormalTexture(material.NormalTexture, textureMap),
            OcclusionTexture = RemapOcclusionTexture(material.OcclusionTexture, textureMap),
            EmissiveTexture = RemapTextureInfo(material.EmissiveTexture, textureMap),
            EmissiveFactor = CloneArray(material.EmissiveFactor),
            Extras = material.Extras,
        };
    }

    private static float[]? CloneArray(float[]? values)
    {
        return values is null ? null : (float[])values.Clone();
    }

    private static GltfTextureInfo? RemapTextureInfo(GltfTextureInfo? source, IReadOnlyList<int> textureMap)
    {
        if (source is null)
        {
            return null;
        }

        return new GltfTextureInfo
        {
            Index = textureMap[source.Index],
            TexCoord = source.TexCoord,
        };
    }

    private static GltfNormalTextureInfo? RemapNormalTexture(GltfNormalTextureInfo? source, IReadOnlyList<int> textureMap)
    {
        if (source is null)
        {
            return null;
        }

        return new GltfNormalTextureInfo
        {
            Index = textureMap[source.Index],
            TexCoord = source.TexCoord,
            Scale = source.Scale,
        };
    }

    private static GltfOcclusionTextureInfo? RemapOcclusionTexture(GltfOcclusionTextureInfo? source, IReadOnlyList<int> textureMap)
    {
        if (source is null)
        {
            return null;
        }

        return new GltfOcclusionTextureInfo
        {
            Index = textureMap[source.Index],
            TexCoord = source.TexCoord,
            Strength = source.Strength,
        };
    }

    internal static string GetSamplerSignature(GltfSampler sampler)
    {
        return FormattableString.Invariant($"{sampler.MagFilter}|{sampler.MinFilter}|{sampler.WrapS}|{sampler.WrapT}");
    }

    internal static string GetTextureSignature(int? samplerIndex, int sourceIndex)
    {
        return FormattableString.Invariant($"{samplerIndex}|{sourceIndex}");
    }

    internal static string GetImageSignature(GltfImage image, IReadOnlyList<GltfBufferView> bufferViews, byte[] bufferData)
    {
        if (!string.IsNullOrWhiteSpace(image.Uri))
        {
            return "uri:" + image.MimeType + ":" + image.Uri;
        }

        if (!image.BufferView.HasValue)
        {
            return "empty:" + image.MimeType;
        }

        GltfBufferView view = bufferViews[image.BufferView.Value];
        ReadOnlySpan<byte> payload = bufferData.AsSpan(view.ByteOffset, view.ByteLength);
        string hash = Convert.ToHexString(SHA256.HashData(payload));
        return FormattableString.Invariant($"buffer:{image.MimeType}:{view.ByteLength}:{hash}");
    }

    internal static string GetMaterialSignature(GltfMaterial material)
    {
        JsonElement element = JsonSerializer.SerializeToElement(new
        {
            pbrMetallicRoughness = material.PbrMetallicRoughness,
            doubleSided = material.DoubleSided,
            alphaMode = material.AlphaMode,
            normalTexture = material.NormalTexture,
            occlusionTexture = material.OcclusionTexture,
            emissiveTexture = material.EmissiveTexture,
            emissiveFactor = material.EmissiveFactor,
            extras = material.Extras,
        }, GltfJson.SerializerOptions);
        return CanonicalizeJson(element);
    }

    private static string CanonicalizeJson(JsonElement element)
    {
        StringBuilder builder = new();
        AppendCanonicalJson(builder, element);
        return builder.ToString();
    }

    private static void AppendCanonicalJson(StringBuilder builder, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                bool firstProperty = true;
                foreach (JsonProperty property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    if (!firstProperty)
                    {
                        builder.Append(',');
                    }

                    firstProperty = false;
                    builder.Append(JsonSerializer.Serialize(property.Name));
                    builder.Append(':');
                    AppendCanonicalJson(builder, property.Value);
                }

                builder.Append('}');
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                bool firstItem = true;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    if (!firstItem)
                    {
                        builder.Append(',');
                    }

                    firstItem = false;
                    AppendCanonicalJson(builder, item);
                }

                builder.Append(']');
                break;
            case JsonValueKind.String:
                builder.Append(JsonSerializer.Serialize(element.GetString()));
                break;
            case JsonValueKind.Number:
                builder.Append(element.GetRawText());
                break;
            case JsonValueKind.True:
                builder.Append("true");
                break;
            case JsonValueKind.False:
                builder.Append("false");
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                builder.Append("null");
                break;
            default:
                builder.Append(JsonSerializer.Serialize(element.GetRawText()));
                break;
        }
    }
}
