using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FmdlGltfConverter;

internal sealed class GltfExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public void Export(
        FmdlFile fmdl,
        string sourceModelPath,
        string outputPath,
        FoxHashLookup hashLookup,
        bool includeTextures = true,
        TextureExportOptions? textureOptions = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory());

        GltfRoot gltf = new()
        {
            Asset = new GltfAsset
            {
                Generator = "FMDL Studio v2 standalone converter",
                Version = "2.0",
            },
            Scene = 0,
            Scenes = new List<GltfScene> { new() },
            Nodes = new List<GltfNode>(),
            Meshes = new List<GltfMesh>(),
            Accessors = new List<GltfAccessor>(),
            BufferViews = new List<GltfBufferView>(),
            Buffers = new List<GltfBuffer>(),
            Materials = new List<GltfMaterial>(),
            Images = new List<GltfImage>(),
            Textures = new List<GltfTexture>(),
            Samplers = new List<GltfSampler>(),
            Skins = new List<GltfSkin>(),
        };

        GltfBufferBuilder bufferBuilder = new();
        TextureExportContext textureContext = new(gltf, bufferBuilder, sourceModelPath, textureOptions ?? TextureExportOptions.Default);
        Dictionary<int, int> materialIndices = new();

        int rootNodeIndex = AddNode(gltf.Nodes, new GltfNode { Name = fmdl.Name });
        gltf.Scenes[0].Nodes = new List<int> { rootNodeIndex };

        Vector3[] boneWorldPositions = new Vector3[fmdl.Bones.Length];
        int[] boneNodeIndices = new int[fmdl.Bones.Length];

        for (int boneIndex = 0; boneIndex < fmdl.Bones.Length; boneIndex++)
        {
            boneWorldPositions[boneIndex] = ConvertPosition(ToVector3(fmdl.Bones[boneIndex].WorldPosition));
        }

        for (int boneIndex = 0; boneIndex < fmdl.Bones.Length; boneIndex++)
        {
            FmdlBone bone = fmdl.Bones[boneIndex];
            Vector3 localTranslation = bone.ParentIndex >= 0
                ? boneWorldPositions[boneIndex] - boneWorldPositions[bone.ParentIndex]
                : boneWorldPositions[boneIndex];

            boneNodeIndices[boneIndex] = AddNode(gltf.Nodes, new GltfNode
            {
                Name = fmdl.ResolveStringIndex(bone.NameIndex, hashLookup),
                Translation = ToArray(localTranslation),
            });
        }

        for (int boneIndex = 0; boneIndex < fmdl.Bones.Length; boneIndex++)
        {
            short parentIndex = fmdl.Bones[boneIndex].ParentIndex;

            if (parentIndex >= 0)
            {
                AddChild(gltf.Nodes[boneNodeIndices[parentIndex]], boneNodeIndices[boneIndex]);
            }
            else
            {
                AddChild(gltf.Nodes[rootNodeIndex], boneNodeIndices[boneIndex]);
            }
        }

        int[] meshGroupNodeIndices = new int[fmdl.MeshGroups.Length];

        for (int meshGroupIndex = 0; meshGroupIndex < fmdl.MeshGroups.Length; meshGroupIndex++)
        {
            FmdlMeshGroup meshGroup = fmdl.MeshGroups[meshGroupIndex];

            meshGroupNodeIndices[meshGroupIndex] = AddNode(gltf.Nodes, new GltfNode
            {
                Name = fmdl.ResolveStringIndex(meshGroup.NameIndex, hashLookup),
                Extras = new Dictionary<string, object?>
                {
                    ["visible"] = meshGroup.InvisibilityFlag == 0,
                },
            });
        }

        for (int meshGroupIndex = 0; meshGroupIndex < fmdl.MeshGroups.Length; meshGroupIndex++)
        {
            short parentIndex = fmdl.MeshGroups[meshGroupIndex].ParentIndex;

            if (parentIndex >= 0 && parentIndex < fmdl.MeshGroups.Length)
            {
                AddChild(gltf.Nodes[meshGroupNodeIndices[parentIndex]], meshGroupNodeIndices[meshGroupIndex]);
            }
            else
            {
                AddChild(gltf.Nodes[rootNodeIndex], meshGroupNodeIndices[meshGroupIndex]);
            }
        }

        for (int meshIndex = 0; meshIndex < fmdl.Meshes.Length; meshIndex++)
        {
            FmdlMeshData meshData = fmdl.Meshes[meshIndex];
            FmdlMeshInfo meshInfo = fmdl.MeshInfos[meshIndex];
            int vertexCount = meshData.Vertices.Length;

            Vector3[] positions = meshData.Vertices.Select(value => ConvertPosition(value)).ToArray();
            Vector3[]? normals = meshData.Normals?.Select(value => ConvertDirection(ToVector3(value))).ToArray();
            Vector4[]? tangents = meshData.Tangents?.Select(value => ConvertTangent(value)).ToArray();
            Vector2[]? uv0 = meshData.Uv?.Select(ConvertUv).ToArray();
            Vector2[]? uv1 = meshData.Uv2?.Select(ConvertUv).ToArray();
            Vector2[]? uv2 = meshData.Uv3?.Select(ConvertUv).ToArray();
            Vector2[]? uv3 = meshData.Uv4?.Select(ConvertUv).ToArray();
            ushort[] indices = BuildTriangleIndices(meshData.Triangles, vertexCount);

            Dictionary<int, int> jointRemap = new();
            List<int> usedBones = new();
            ushort[]? joints = null;
            Vector4[]? weights = null;

            if (meshData.BoneWeights is not null &&
                meshData.BoneIndices is not null &&
                meshInfo.BoneGroupIndex < fmdl.BoneGroups.Length)
            {
                (joints, weights, usedBones) = BuildSkinningData(meshData.BoneWeights, meshData.BoneIndices, fmdl.BoneGroups[meshInfo.BoneGroupIndex], jointRemap);
            }

            int? materialIndex = BuildMaterial(gltf, fmdl, meshInfo, hashLookup, textureContext, materialIndices, includeTextures);
            int gltfMeshIndex = BuildMesh(gltf, bufferBuilder, meshIndex, positions, normals, tangents, uv0, uv1, uv2, uv3, joints, weights, indices, materialIndex);
            int? skinIndex = BuildSkin(gltf, bufferBuilder, meshIndex, usedBones, fmdl.Bones, boneNodeIndices, boneWorldPositions);

            int meshGroupIndex = fmdl.GetMeshGroupIndex(meshIndex);
            string meshName = meshGroupIndex >= 0
                ? $"{meshIndex} - {fmdl.ResolveStringIndex(fmdl.MeshGroups[meshGroupIndex].NameIndex, hashLookup)}"
                : $"{meshIndex} - Mesh";

            int meshNodeIndex = AddNode(gltf.Nodes, new GltfNode
            {
                Name = meshName,
                Mesh = gltfMeshIndex,
                Skin = skinIndex,
                Extras = new Dictionary<string, object?>
                {
                    ["foxAlphaEnum"] = meshInfo.AlphaEnum,
                    ["foxShadowEnum"] = meshInfo.ShadowEnum,
                    ["foxMeshGroupIndex"] = meshGroupIndex,
                },
            });

            if (meshGroupIndex >= 0 && meshGroupIndex < meshGroupNodeIndices.Length)
            {
                AddChild(gltf.Nodes[meshGroupNodeIndices[meshGroupIndex]], meshNodeIndex);
            }
            else
            {
                AddChild(gltf.Nodes[rootNodeIndex], meshNodeIndex);
            }
        }

        byte[] bufferData = bufferBuilder.ToArray();
        gltf.Buffers.Add(new GltfBuffer { ByteLength = bufferData.Length });

        if (outputPath.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
        {
            WriteGlb(outputPath, gltf, bufferData);
        }
        else
        {
            WriteGltf(outputPath, gltf, bufferData);
        }
    }

    private static int BuildMesh(
        GltfRoot gltf,
        GltfBufferBuilder bufferBuilder,
        int meshIndex,
        Vector3[] positions,
        Vector3[]? normals,
        Vector4[]? tangents,
        Vector2[]? uv0,
        Vector2[]? uv1,
        Vector2[]? uv2,
        Vector2[]? uv3,
        ushort[]? joints,
        Vector4[]? weights,
        ushort[] indices,
        int? materialIndex)
    {
        Dictionary<string, int> attributes = new()
        {
            ["POSITION"] = AddVector3Accessor(gltf, bufferBuilder, positions, includeBounds: true, target: 34962),
        };

        if (normals is not null)
        {
            attributes["NORMAL"] = AddVector3Accessor(gltf, bufferBuilder, normals, includeBounds: false, target: 34962);
        }

        if (tangents is not null)
        {
            attributes["TANGENT"] = AddVector4Accessor(gltf, bufferBuilder, tangents, target: 34962);
        }

        if (uv0 is not null)
        {
            attributes["TEXCOORD_0"] = AddVector2Accessor(gltf, bufferBuilder, uv0, target: 34962);
        }

        if (uv1 is not null)
        {
            attributes["TEXCOORD_1"] = AddVector2Accessor(gltf, bufferBuilder, uv1, target: 34962);
        }

        if (uv2 is not null)
        {
            attributes["TEXCOORD_2"] = AddVector2Accessor(gltf, bufferBuilder, uv2, target: 34962);
        }

        if (uv3 is not null)
        {
            attributes["TEXCOORD_3"] = AddVector2Accessor(gltf, bufferBuilder, uv3, target: 34962);
        }

        if (joints is not null && weights is not null)
        {
            attributes["JOINTS_0"] = AddUShort4Accessor(gltf, bufferBuilder, joints, target: 34962);
            attributes["WEIGHTS_0"] = AddVector4Accessor(gltf, bufferBuilder, weights, target: 34962);
        }

        int indexAccessor = AddScalarAccessor(gltf, bufferBuilder, indices, 34963);

        int gltfMeshIndex = gltf.Meshes.Count;
        gltf.Meshes.Add(new GltfMesh
        {
            Name = $"Mesh_{meshIndex:D3}",
            Primitives = new List<GltfPrimitive>
            {
                new()
                {
                    Attributes = attributes,
                    Indices = indexAccessor,
                    Material = materialIndex,
                    Mode = 4,
                },
            },
        });

        return gltfMeshIndex;
    }

    private static int? BuildSkin(
        GltfRoot gltf,
        GltfBufferBuilder bufferBuilder,
        int meshIndex,
        List<int> usedBones,
        FmdlBone[] bones,
        int[] boneNodeIndices,
        Vector3[] boneWorldPositions)
    {
        if (usedBones.Count == 0)
        {
            return null;
        }

        Matrix4x4[] inverseBindMatrices = new Matrix4x4[usedBones.Count];
        int[] joints = new int[usedBones.Count];

        for (int index = 0; index < usedBones.Count; index++)
        {
            int boneIndex = usedBones[index];
            joints[index] = boneNodeIndices[boneIndex];
            inverseBindMatrices[index] = Matrix4x4.CreateTranslation(-boneWorldPositions[boneIndex]);
        }

        int inverseBindAccessor = AddMatrix4Accessor(gltf, bufferBuilder, inverseBindMatrices);
        int skeletonRootBoneIndex = FindSkeletonRootBoneIndex(usedBones[0], bones);
        int skinIndex = gltf.Skins.Count;
        gltf.Skins.Add(new GltfSkin
        {
            Name = $"Skin_{meshIndex:D3}",
            InverseBindMatrices = inverseBindAccessor,
            Joints = joints.ToList(),
            Skeleton = boneNodeIndices[skeletonRootBoneIndex],
        });

        return skinIndex;
    }

    private static int FindSkeletonRootBoneIndex(int boneIndex, FmdlBone[] bones)
    {
        int current = boneIndex;

        while (bones[current].ParentIndex >= 0)
        {
            current = bones[current].ParentIndex;
        }

        return current;
    }

    private static int? BuildMaterial(
        GltfRoot gltf,
        FmdlFile fmdl,
        FmdlMeshInfo meshInfo,
        FoxHashLookup hashLookup,
        TextureExportContext textureContext,
        Dictionary<int, int> materialIndices,
        bool includeTextures)
    {
        if (meshInfo.MaterialInstanceIndex >= fmdl.MaterialInstances.Length)
        {
            return null;
        }

        if (materialIndices.TryGetValue(meshInfo.MaterialInstanceIndex, out int existingMaterialIndex))
        {
            return existingMaterialIndex;
        }

        FmdlMaterialInstance materialInstance = fmdl.MaterialInstances[meshInfo.MaterialInstanceIndex];
        string materialName = fmdl.ResolveStringIndex(materialInstance.NameIndex, hashLookup);
        string shaderName = materialInstance.MaterialIndex < fmdl.Materials.Length
            ? fmdl.ResolveStringIndex(fmdl.Materials[materialInstance.MaterialIndex].TypeIndex, hashLookup)
            : "UnknownShader";

        Dictionary<string, object?> textureExtras = new(StringComparer.Ordinal);
        Dictionary<string, string> textureReferences = new(StringComparer.Ordinal);
        for (int index = materialInstance.FirstTextureIndex; index < materialInstance.FirstTextureIndex + materialInstance.TextureCount; index++)
        {
            if (index >= fmdl.MaterialParameters.Length)
            {
                break;
            }

            FmdlMaterialParameter materialParameter = fmdl.MaterialParameters[index];
            string slotName = fmdl.ResolveStringIndex(materialParameter.NameIndex, hashLookup);

            if (materialParameter.ReferenceIndex < fmdl.Textures.Length)
            {
                FmdlTexture texture = fmdl.Textures[materialParameter.ReferenceIndex];
                string reference = fmdl.ResolveTextureReference(texture, hashLookup);
                textureReferences[slotName] = reference;
                textureExtras[slotName] = new Dictionary<string, object?>
                {
                    ["reference"] = reference,
                };
            }
        }

        Dictionary<string, object?> parameterExtras = new(StringComparer.Ordinal);
        Dictionary<string, Vector4> parameterValues = new(StringComparer.Ordinal);
        for (int index = materialInstance.FirstParameterIndex; index < materialInstance.FirstParameterIndex + materialInstance.ParameterCount; index++)
        {
            if (index >= fmdl.MaterialParameters.Length)
            {
                break;
            }

            FmdlMaterialParameter materialParameter = fmdl.MaterialParameters[index];
            string parameterName = fmdl.ResolveStringIndex(materialParameter.NameIndex, hashLookup);

            if (materialParameter.ReferenceIndex < fmdl.MaterialParameterVectors.Length)
            {
                Vector4 parameterValue = fmdl.MaterialParameterVectors[materialParameter.ReferenceIndex];
                parameterValues[parameterName] = parameterValue;
                parameterExtras[parameterName] = new[] { parameterValue.X, parameterValue.Y, parameterValue.Z, parameterValue.W };
            }
        }

        string? alphaMode = meshInfo.AlphaEnum switch
        {
            0 => null,
            0x20 => null,
            _ => "BLEND",
        };

        GltfPbrMetallicRoughness pbrMetallicRoughness = new()
        {
            BaseColorFactor = FindBaseColorFactor(parameterValues),
            MetallicFactor = 0.0f,
            RoughnessFactor = FindRoughnessFactor(parameterValues),
        };

        GltfMaterial material = new()
        {
            Name = materialName,
            DoubleSided = meshInfo.AlphaEnum == 0x20,
            AlphaMode = alphaMode,
            PbrMetallicRoughness = pbrMetallicRoughness,
            EmissiveFactor = [0.0f, 0.0f, 0.0f],
            Extras = new Dictionary<string, object?>
            {
                ["foxShader"] = shaderName,
                ["foxTextureSlots"] = textureExtras,
                ["foxParameters"] = parameterExtras,
            },
        };

        if (includeTextures)
        {
            ApplyTextureAssignments(material, pbrMetallicRoughness, textureReferences, textureContext);
        }

        int materialIndex = gltf.Materials.Count;
        gltf.Materials.Add(material);
        materialIndices.Add(meshInfo.MaterialInstanceIndex, materialIndex);
        return materialIndex;
    }

    private static void ApplyTextureAssignments(
        GltfMaterial material,
        GltfPbrMetallicRoughness pbrMetallicRoughness,
        Dictionary<string, string> textureReferences,
        TextureExportContext textureContext)
    {
        foreach ((string slotName, string reference) in textureReferences)
        {
            FoxTextureUsage usage = GetTextureUsage(slotName);
            int? textureIndex = textureContext.AddTexture(reference, usage);
            if (textureIndex is null)
            {
                continue;
            }

            if (IsBaseColorSlot(slotName))
            {
                pbrMetallicRoughness.BaseColorTexture ??= new GltfTextureInfo { Index = textureIndex.Value };
            }
            else if (IsNormalSlot(slotName))
            {
                material.NormalTexture ??= new GltfNormalTextureInfo { Index = textureIndex.Value, Scale = 1.0f };
            }
            else if (IsRoughnessSlot(slotName))
            {
                pbrMetallicRoughness.MetallicRoughnessTexture ??= new GltfTextureInfo { Index = textureIndex.Value };
            }
            else if (IsEmissiveSlot(slotName))
            {
                material.EmissiveTexture ??= new GltfTextureInfo { Index = textureIndex.Value };
                material.EmissiveFactor = [1.0f, 1.0f, 1.0f];
            }
        }
    }

    private static bool IsBaseColorSlot(string slotName)
    {
        return slotName.Contains("Base_Tex", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNormalSlot(string slotName)
    {
        return slotName.Contains("NormalMap", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRoughnessSlot(string slotName)
    {
        return slotName.Contains("SpecularMap", StringComparison.OrdinalIgnoreCase) ||
               slotName.Contains("SRM", StringComparison.OrdinalIgnoreCase);
    }

    private static FoxTextureUsage GetTextureUsage(string slotName)
    {
        if (IsNormalSlot(slotName))
        {
            return FoxTextureUsage.Normal;
        }

        if (IsRoughnessSlot(slotName))
        {
            return FoxTextureUsage.Roughness;
        }

        return FoxTextureUsage.Default;
    }

    private static bool IsEmissiveSlot(string slotName)
    {
        return slotName.Contains("Emissive", StringComparison.OrdinalIgnoreCase);
    }

    private static float[] FindBaseColorFactor(Dictionary<string, Vector4> parameterValues)
    {
        foreach ((string name, Vector4 value) in parameterValues)
        {
            if (name.Contains("BaseColor", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("TempolaryBaseColor", StringComparison.OrdinalIgnoreCase))
            {
                return
                [
                    Clamp01(value.X),
                    Clamp01(value.Y),
                    Clamp01(value.Z),
                    Clamp01(value.W == 0 ? 1.0f : value.W),
                ];
            }
        }

        return [1.0f, 1.0f, 1.0f, 1.0f];
    }

    private static float FindRoughnessFactor(Dictionary<string, Vector4> parameterValues)
    {
        foreach ((string name, Vector4 value) in parameterValues)
        {
            if (name.Contains("roughness", StringComparison.OrdinalIgnoreCase))
            {
                return Clamp01(value.X);
            }

            if (name.Contains("gloss", StringComparison.OrdinalIgnoreCase))
            {
                return Clamp01(1.0f - value.X);
            }
        }

        return 1.0f;
    }

    private static float Clamp01(float value)
    {
        return Math.Clamp(value, 0.0f, 1.0f);
    }

    private static (ushort[] Joints, Vector4[] Weights, List<int> UsedBones) BuildSkinningData(
        Vector4[] sourceWeights,
        Vector4[] sourceIndices,
        FmdlBoneGroup boneGroup,
        Dictionary<int, int> jointRemap)
    {
        ushort[] joints = new ushort[sourceWeights.Length * 4];
        Vector4[] weights = new Vector4[sourceWeights.Length];
        List<int> usedBones = new();

        for (int vertexIndex = 0; vertexIndex < sourceWeights.Length; vertexIndex++)
        {
            float[] currentWeights =
            {
                sourceWeights[vertexIndex].X / 255.0f,
                sourceWeights[vertexIndex].Y / 255.0f,
                sourceWeights[vertexIndex].Z / 255.0f,
                sourceWeights[vertexIndex].W / 255.0f,
            };

            float totalWeight = currentWeights.Sum();
            if (totalWeight > 0)
            {
                for (int slot = 0; slot < currentWeights.Length; slot++)
                {
                    currentWeights[slot] /= totalWeight;
                }
            }

            int[] sourceJointIndices =
            {
                (int)sourceIndices[vertexIndex].X,
                (int)sourceIndices[vertexIndex].Y,
                (int)sourceIndices[vertexIndex].Z,
                (int)sourceIndices[vertexIndex].W,
            };

            for (int slot = 0; slot < 4; slot++)
            {
                int globalBoneIndex = 0;

                if (currentWeights[slot] > 0 &&
                    sourceJointIndices[slot] >= 0 &&
                    sourceJointIndices[slot] < boneGroup.BoneIndices.Length)
                {
                    globalBoneIndex = boneGroup.BoneIndices[sourceJointIndices[slot]];
                    if (!jointRemap.TryGetValue(globalBoneIndex, out int remappedIndex))
                    {
                        remappedIndex = usedBones.Count;
                        usedBones.Add(globalBoneIndex);
                        jointRemap.Add(globalBoneIndex, remappedIndex);
                    }

                    joints[vertexIndex * 4 + slot] = (ushort)jointRemap[globalBoneIndex];
                }
                else
                {
                    currentWeights[slot] = 0;
                    joints[vertexIndex * 4 + slot] = 0;
                }
            }

            weights[vertexIndex] = new Vector4(currentWeights[0], currentWeights[1], currentWeights[2], currentWeights[3]);
        }

        return (joints, weights, usedBones);
    }

    private static ushort[] BuildTriangleIndices(ushort[] sourceTriangles, int vertexCount)
    {
        ushort[] indices = new ushort[sourceTriangles.Length];

        for (int index = 0; index + 2 < sourceTriangles.Length; index += 3)
        {
            // Fox meshes use the opposite winding from glTF's default front-face
            // convention, so swap the last two vertices of each triangle.
            indices[index] = (ushort)(sourceTriangles[index] % vertexCount);
            indices[index + 1] = (ushort)(sourceTriangles[index + 2] % vertexCount);
            indices[index + 2] = (ushort)(sourceTriangles[index + 1] % vertexCount);
        }

        return indices;
    }

    private static int AddNode(List<GltfNode> nodes, GltfNode node)
    {
        int index = nodes.Count;
        nodes.Add(node);
        return index;
    }

    private static void AddChild(GltfNode parentNode, int childIndex)
    {
        parentNode.Children ??= new List<int>();
        parentNode.Children.Add(childIndex);
    }

    private static Vector3 ConvertPosition(Vector3 input)
    {
        return input;
    }

    private static Vector3 ConvertDirection(Vector3 input)
    {
        return input;
    }

    private static Vector4 ConvertTangent(Vector4 input)
    {
        return input;
    }

    private static Vector3 ToVector3(Vector4 input)
    {
        return new Vector3(input.X, input.Y, input.Z);
    }

    private static Vector2 ConvertUv(Vector2 input)
    {
        // FMDL Studio flips V for Unity's UV convention. glTF uses the original
        // top-left texture orientation, so the stored FMDL UVs can be emitted as-is.
        return input;
    }

    private static float[] ToArray(Vector3 input)
    {
        return new[] { input.X, input.Y, input.Z };
    }

    private static int AddVector3Accessor(GltfRoot gltf, GltfBufferBuilder bufferBuilder, IReadOnlyList<Vector3> values, bool includeBounds, int target)
    {
        int byteOffset = bufferBuilder.AddVector3(values);
        int bufferViewIndex = gltf.BufferViews.Count;
        gltf.BufferViews.Add(new GltfBufferView
        {
            Buffer = 0,
            ByteOffset = byteOffset,
            ByteLength = values.Count * sizeof(float) * 3,
            ByteStride = sizeof(float) * 3,
            Target = target,
        });

        float[]? min = null;
        float[]? max = null;

        if (includeBounds && values.Count > 0)
        {
            min = new[] { values.Min(value => value.X), values.Min(value => value.Y), values.Min(value => value.Z) };
            max = new[] { values.Max(value => value.X), values.Max(value => value.Y), values.Max(value => value.Z) };
        }

        int accessorIndex = gltf.Accessors.Count;
        gltf.Accessors.Add(new GltfAccessor
        {
            BufferView = bufferViewIndex,
            ComponentType = 5126,
            Count = values.Count,
            Type = "VEC3",
            Min = min,
            Max = max,
        });

        return accessorIndex;
    }

    private static int AddVector4Accessor(GltfRoot gltf, GltfBufferBuilder bufferBuilder, IReadOnlyList<Vector4> values, int target)
    {
        int byteOffset = bufferBuilder.AddVector4(values);
        int bufferViewIndex = gltf.BufferViews.Count;
        gltf.BufferViews.Add(new GltfBufferView
        {
            Buffer = 0,
            ByteOffset = byteOffset,
            ByteLength = values.Count * sizeof(float) * 4,
            ByteStride = sizeof(float) * 4,
            Target = target,
        });

        int accessorIndex = gltf.Accessors.Count;
        gltf.Accessors.Add(new GltfAccessor
        {
            BufferView = bufferViewIndex,
            ComponentType = 5126,
            Count = values.Count,
            Type = "VEC4",
        });

        return accessorIndex;
    }

    private static int AddVector2Accessor(GltfRoot gltf, GltfBufferBuilder bufferBuilder, IReadOnlyList<Vector2> values, int target)
    {
        int byteOffset = bufferBuilder.AddVector2(values);
        int bufferViewIndex = gltf.BufferViews.Count;
        gltf.BufferViews.Add(new GltfBufferView
        {
            Buffer = 0,
            ByteOffset = byteOffset,
            ByteLength = values.Count * sizeof(float) * 2,
            ByteStride = sizeof(float) * 2,
            Target = target,
        });

        int accessorIndex = gltf.Accessors.Count;
        gltf.Accessors.Add(new GltfAccessor
        {
            BufferView = bufferViewIndex,
            ComponentType = 5126,
            Count = values.Count,
            Type = "VEC2",
        });

        return accessorIndex;
    }

    private static int AddUShort4Accessor(GltfRoot gltf, GltfBufferBuilder bufferBuilder, IReadOnlyList<ushort> values, int target)
    {
        int byteOffset = bufferBuilder.AddUShort(values);
        int bufferViewIndex = gltf.BufferViews.Count;
        gltf.BufferViews.Add(new GltfBufferView
        {
            Buffer = 0,
            ByteOffset = byteOffset,
            ByteLength = values.Count * sizeof(ushort),
            ByteStride = sizeof(ushort) * 4,
            Target = target,
        });

        int accessorIndex = gltf.Accessors.Count;
        gltf.Accessors.Add(new GltfAccessor
        {
            BufferView = bufferViewIndex,
            ComponentType = 5123,
            Count = values.Count / 4,
            Type = "VEC4",
        });

        return accessorIndex;
    }

    private static int AddScalarAccessor(GltfRoot gltf, GltfBufferBuilder bufferBuilder, IReadOnlyList<ushort> values, int target)
    {
        int byteOffset = bufferBuilder.AddUShort(values);
        int bufferViewIndex = gltf.BufferViews.Count;
        gltf.BufferViews.Add(new GltfBufferView
        {
            Buffer = 0,
            ByteOffset = byteOffset,
            ByteLength = values.Count * sizeof(ushort),
            Target = target,
        });

        int accessorIndex = gltf.Accessors.Count;
        gltf.Accessors.Add(new GltfAccessor
        {
            BufferView = bufferViewIndex,
            ComponentType = 5123,
            Count = values.Count,
            Type = "SCALAR",
            Min = new[] { (float)values.Min() },
            Max = new[] { (float)values.Max() },
        });

        return accessorIndex;
    }

    private static int AddMatrix4Accessor(GltfRoot gltf, GltfBufferBuilder bufferBuilder, IReadOnlyList<Matrix4x4> values)
    {
        int byteOffset = bufferBuilder.AddMatrix4(values);
        int bufferViewIndex = gltf.BufferViews.Count;
        gltf.BufferViews.Add(new GltfBufferView
        {
            Buffer = 0,
            ByteOffset = byteOffset,
            ByteLength = values.Count * sizeof(float) * 16,
        });

        int accessorIndex = gltf.Accessors.Count;
        gltf.Accessors.Add(new GltfAccessor
        {
            BufferView = bufferViewIndex,
            ComponentType = 5126,
            Count = values.Count,
            Type = "MAT4",
        });

        return accessorIndex;
    }

    private static void WriteGltf(string outputPath, GltfRoot gltf, byte[] bufferData)
    {
        gltf.Buffers[0].Uri = Path.GetFileNameWithoutExtension(outputPath) + ".bin";
        string json = JsonSerializer.Serialize(gltf, JsonOptions);
        File.WriteAllText(outputPath, json, Encoding.UTF8);
        File.WriteAllBytes(Path.ChangeExtension(outputPath, ".bin"), bufferData);
    }

    private static void WriteGlb(string outputPath, GltfRoot gltf, byte[] bufferData)
    {
        gltf.Buffers[0].Uri = null;
        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(gltf, JsonOptions);

        int paddedJsonLength = Align4(jsonBytes.Length);
        int paddedBinLength = Align4(bufferData.Length);
        int totalLength = 12 + 8 + paddedJsonLength + 8 + paddedBinLength;

        using FileStream stream = File.Create(outputPath);
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: false);

        writer.Write(0x46546C67);
        writer.Write(2);
        writer.Write(totalLength);

        writer.Write(paddedJsonLength);
        writer.Write(0x4E4F534A);
        writer.Write(jsonBytes);
        for (int index = jsonBytes.Length; index < paddedJsonLength; index++)
        {
            writer.Write((byte)0x20);
        }

        writer.Write(paddedBinLength);
        writer.Write(0x004E4942);
        writer.Write(bufferData);
        for (int index = bufferData.Length; index < paddedBinLength; index++)
        {
            writer.Write((byte)0);
        }
    }

    private static int Align4(int value)
    {
        return (value + 3) & ~3;
    }
}

internal sealed class GltfBufferBuilder
{
    private readonly MemoryStream stream = new();
    private readonly BinaryWriter writer;

    public GltfBufferBuilder()
    {
        writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
    }

    public int AddVector2(IReadOnlyList<Vector2> values)
    {
        Align(4);
        int offset = (int)stream.Position;

        foreach (Vector2 value in values)
        {
            writer.Write(value.X);
            writer.Write(value.Y);
        }

        return offset;
    }

    public int AddVector3(IReadOnlyList<Vector3> values)
    {
        Align(4);
        int offset = (int)stream.Position;

        foreach (Vector3 value in values)
        {
            writer.Write(value.X);
            writer.Write(value.Y);
            writer.Write(value.Z);
        }

        return offset;
    }

    public int AddVector4(IReadOnlyList<Vector4> values)
    {
        Align(4);
        int offset = (int)stream.Position;

        foreach (Vector4 value in values)
        {
            writer.Write(value.X);
            writer.Write(value.Y);
            writer.Write(value.Z);
            writer.Write(value.W);
        }

        return offset;
    }

    public int AddUShort(IReadOnlyList<ushort> values)
    {
        Align(2);
        int offset = (int)stream.Position;

        foreach (ushort value in values)
        {
            writer.Write(value);
        }

        Align(4);
        return offset;
    }

    public int AddMatrix4(IReadOnlyList<Matrix4x4> values)
    {
        Align(4);
        int offset = (int)stream.Position;

        foreach (Matrix4x4 value in values)
        {
            writer.Write(value.M11);
            writer.Write(value.M12);
            writer.Write(value.M13);
            writer.Write(value.M14);
            writer.Write(value.M21);
            writer.Write(value.M22);
            writer.Write(value.M23);
            writer.Write(value.M24);
            writer.Write(value.M31);
            writer.Write(value.M32);
            writer.Write(value.M33);
            writer.Write(value.M34);
            writer.Write(value.M41);
            writer.Write(value.M42);
            writer.Write(value.M43);
            writer.Write(value.M44);
        }

        return offset;
    }

    public int AddBytes(IReadOnlyList<byte> values)
    {
        Align(4);
        int offset = (int)stream.Position;

        if (values is byte[] bytes)
        {
            writer.Write(bytes);
        }
        else
        {
            foreach (byte value in values)
            {
                writer.Write(value);
            }
        }

        Align(4);
        return offset;
    }

    public byte[] ToArray()
    {
        return stream.ToArray();
    }

    private void Align(int alignment)
    {
        while (stream.Position % alignment != 0)
        {
            writer.Write((byte)0);
        }
    }
}

internal sealed class GltfRoot
{
    public required GltfAsset Asset { get; init; }
    public required int Scene { get; init; }
    public required List<GltfScene> Scenes { get; init; }
    public required List<GltfNode> Nodes { get; init; }
    public required List<GltfMesh> Meshes { get; init; }
    public required List<GltfAccessor> Accessors { get; init; }
    public required List<GltfBufferView> BufferViews { get; init; }
    public required List<GltfBuffer> Buffers { get; init; }
    public required List<GltfMaterial> Materials { get; init; }
    public required List<GltfImage> Images { get; init; }
    public required List<GltfTexture> Textures { get; init; }
    public required List<GltfSampler> Samplers { get; init; }
    public required List<GltfSkin> Skins { get; init; }
    public List<string>? ExtensionsUsed { get; set; }
    public List<string>? ExtensionsRequired { get; set; }
}

internal sealed class GltfAsset
{
    public required string Generator { get; init; }
    public required string Version { get; init; }
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
    public int? Mesh { get; set; }
    public int? Skin { get; set; }
    public Dictionary<string, object?>? Extras { get; set; }
}

internal sealed class GltfMesh
{
    public string? Name { get; set; }
    public required List<GltfPrimitive> Primitives { get; init; }
}

internal sealed class GltfPrimitive
{
    public required Dictionary<string, int> Attributes { get; init; }
    public required int Indices { get; init; }
    public int? Material { get; init; }
    public int Mode { get; init; }
}

internal sealed class GltfAccessor
{
    public required int BufferView { get; init; }
    public required int ComponentType { get; init; }
    public required int Count { get; init; }
    public required string Type { get; init; }
    public float[]? Min { get; init; }
    public float[]? Max { get; init; }
}

internal sealed class GltfBufferView
{
    public required int Buffer { get; init; }
    public required int ByteOffset { get; init; }
    public required int ByteLength { get; init; }
    public int? ByteStride { get; init; }
    public int? Target { get; init; }
}

internal sealed class GltfBuffer
{
    public required int ByteLength { get; init; }
    public string? Uri { get; set; }
}

internal sealed class GltfMaterial
{
    public string? Name { get; init; }
    public GltfPbrMetallicRoughness? PbrMetallicRoughness { get; init; }
    public bool? DoubleSided { get; init; }
    public string? AlphaMode { get; init; }
    public GltfNormalTextureInfo? NormalTexture { get; set; }
    public GltfOcclusionTextureInfo? OcclusionTexture { get; set; }
    public GltfTextureInfo? EmissiveTexture { get; set; }
    public float[]? EmissiveFactor { get; set; }
    public Dictionary<string, object?>? Extras { get; init; }
}

internal sealed class GltfPbrMetallicRoughness
{
    public float[]? BaseColorFactor { get; init; }
    public GltfTextureInfo? BaseColorTexture { get; set; }
    public float MetallicFactor { get; init; }
    public float RoughnessFactor { get; init; }
    public GltfTextureInfo? MetallicRoughnessTexture { get; set; }
}

internal sealed class GltfSkin
{
    public string? Name { get; init; }
    public int? InverseBindMatrices { get; init; }
    public required List<int> Joints { get; init; }
    public int? Skeleton { get; init; }
}

internal sealed class GltfImage
{
    public string? Name { get; init; }
    public int? BufferView { get; init; }
    public string? MimeType { get; init; }
    public string? Uri { get; init; }
}

internal sealed class GltfTexture
{
    public string? Name { get; init; }
    public int? Sampler { get; init; }
    public int? Source { get; set; }
    public Dictionary<string, object?>? Extensions { get; set; }
}

internal sealed class GltfTextureSourceExtension
{
    public required int Source { get; init; }
}

internal sealed class GltfSampler
{
    public int? MagFilter { get; init; }
    public int? MinFilter { get; init; }
    public int? WrapS { get; init; }
    public int? WrapT { get; init; }
}

internal class GltfTextureInfo
{
    public required int Index { get; init; }
    public int? TexCoord { get; init; }
}

internal sealed class GltfNormalTextureInfo : GltfTextureInfo
{
    public float? Scale { get; init; }
}

internal sealed class GltfOcclusionTextureInfo : GltfTextureInfo
{
    public float? Strength { get; init; }
}
