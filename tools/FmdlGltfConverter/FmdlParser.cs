using System.Numerics;
using System.Text;

namespace FmdlGltfConverter;

internal sealed class FmdlParser
{
    private enum Section0BlockType : ushort
    {
        Bones = 0,
        MeshGroups = 1,
        MeshGroupEntries = 2,
        MeshInfo = 3,
        MaterialInstances = 4,
        BoneGroups = 5,
        Textures = 6,
        MaterialParameters = 7,
        Materials = 8,
        MeshFormatInfo = 9,
        MeshFormats = 10,
        VertexFormats = 11,
        StringInfo = 12,
        BoundingBoxes = 13,
        BufferOffsets = 14,
        LodInfo = 16,
        FaceInfo = 17,
        Type12 = 18,
        Type14 = 20,
        PathCode64s = 21,
        StrCode64s = 22,
    }

    private enum Section1BlockType : uint
    {
        MaterialParameterVectors = 0,
        Buffer = 2,
        Strings = 3,
    }

    public FmdlFile Read(string filePath)
    {
        using FileStream stream = File.OpenRead(filePath);
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: false);

        ParserState state = ReadHeader(reader, Path.GetFileNameWithoutExtension(filePath));
        ReadSectionInfos(reader, state);

        if (state.BonesIndex != -1) ReadBones(reader, state);
        if (state.MeshGroupsIndex != -1) ReadMeshGroups(reader, state);
        if (state.MeshGroupEntriesIndex != -1) ReadMeshGroupEntries(reader, state);
        if (state.MeshInfoIndex != -1) ReadMeshInfo(reader, state);
        if (state.MaterialInstancesIndex != -1) ReadMaterialInstances(reader, state);
        if (state.BoneGroupsIndex != -1) ReadBoneGroups(reader, state);
        if (state.TexturesIndex != -1) ReadTextures(reader, state);
        if (state.MaterialParametersIndex != -1) ReadMaterialParameters(reader, state);
        if (state.MaterialsIndex != -1) ReadMaterials(reader, state);
        if (state.MeshFormatInfoIndex != -1) ReadMeshFormatInfo(reader, state);
        if (state.MeshFormatsIndex != -1) ReadMeshFormats(reader, state);
        if (state.VertexFormatsIndex != -1) ReadVertexFormats(reader, state);
        if (state.StringInfoIndex != -1) ReadStringInfo(reader, state);
        if (state.BoundingBoxesIndex != -1) ReadBoundingBoxes(reader, state);
        if (state.BufferOffsetsIndex != -1) ReadBufferOffsets(reader, state);
        if (state.LodInfoIndex != -1) ReadLodInfo(reader, state);
        if (state.FaceInfoIndex != -1) ReadFaceInfo(reader, state);
        if (state.Type12Index != -1) ReadType12(reader, state);
        if (state.Type14Index != -1) ReadType14(reader, state);
        if (state.PathCode64sIndex != -1) ReadPathCode64s(reader, state);
        if (state.StrCode64sIndex != -1) ReadStrCode64s(reader, state);
        if (state.MaterialParameterVectorsIndex != -1) ReadMaterialParameterVectors(reader, state);
        if (state.BufferIndex != -1) ReadBuffer(reader, state);
        if (state.StringsIndex != -1) ReadStrings(reader, state);

        return state.ToFile();
    }

    private static ParserState ReadHeader(BinaryReader reader, string modelName)
    {
        return new ParserState(modelName)
        {
            Signature = reader.ReadUInt32(),
            Version = reader.ReadSingle(),
            SectionInfoOffset = reader.ReadUInt64(),
            Section0BlockFlags = reader.ReadUInt64(),
            Section1BlockFlags = reader.ReadUInt64(),
            Section0BlockCount = reader.ReadUInt32(),
            Section1BlockCount = reader.ReadUInt32(),
            Section0Offset = reader.ReadUInt32(),
            Section0Length = reader.ReadUInt32(),
            Section1Offset = reader.ReadUInt32(),
            Section1Length = reader.ReadUInt32(),
        };
    }

    private static void ReadSectionInfos(BinaryReader reader, ParserState state)
    {
        reader.BaseStream.Position = (long)state.SectionInfoOffset;
        state.Section0Infos = new Section0Info[state.Section0BlockCount];
        state.Section1Infos = new Section1Info[state.Section1BlockCount];

        for (int index = 0; index < state.Section0Infos.Length; index++)
        {
            Section0Info section0Info = new()
            {
                Type = reader.ReadUInt16(),
                EntryCount = reader.ReadUInt16(),
                Offset = reader.ReadUInt32(),
            };

            switch ((Section0BlockType)section0Info.Type)
            {
                case Section0BlockType.Bones:
                    state.BonesIndex = index;
                    state.Bones = new FmdlBone[section0Info.EntryCount];
                    break;
                case Section0BlockType.MeshGroups:
                    state.MeshGroupsIndex = index;
                    state.MeshGroups = new FmdlMeshGroup[section0Info.EntryCount];
                    break;
                case Section0BlockType.MeshGroupEntries:
                    state.MeshGroupEntriesIndex = index;
                    state.MeshGroupEntries = new FmdlMeshGroupEntry[section0Info.EntryCount];
                    break;
                case Section0BlockType.MeshInfo:
                    state.MeshInfoIndex = index;
                    state.MeshInfos = new FmdlMeshInfo[section0Info.EntryCount];
                    break;
                case Section0BlockType.MaterialInstances:
                    state.MaterialInstancesIndex = index;
                    state.MaterialInstances = new FmdlMaterialInstance[section0Info.EntryCount];
                    break;
                case Section0BlockType.BoneGroups:
                    state.BoneGroupsIndex = index;
                    state.BoneGroups = new FmdlBoneGroup[section0Info.EntryCount];
                    break;
                case Section0BlockType.Textures:
                    state.TexturesIndex = index;
                    state.Textures = new FmdlTexture[section0Info.EntryCount];
                    break;
                case Section0BlockType.MaterialParameters:
                    state.MaterialParametersIndex = index;
                    state.MaterialParameters = new FmdlMaterialParameter[section0Info.EntryCount];
                    break;
                case Section0BlockType.Materials:
                    state.MaterialsIndex = index;
                    state.Materials = new FmdlMaterial[section0Info.EntryCount];
                    break;
                case Section0BlockType.MeshFormatInfo:
                    state.MeshFormatInfoIndex = index;
                    state.MeshFormatInfos = new FmdlMeshFormatInfo[section0Info.EntryCount];
                    break;
                case Section0BlockType.MeshFormats:
                    state.MeshFormatsIndex = index;
                    state.MeshFormats = new FmdlMeshFormat[section0Info.EntryCount];
                    break;
                case Section0BlockType.VertexFormats:
                    state.VertexFormatsIndex = index;
                    state.VertexFormats = new FmdlVertexFormat[section0Info.EntryCount];
                    break;
                case Section0BlockType.StringInfo:
                    state.StringInfoIndex = index;
                    state.StringInfos = new FmdlStringInfo[section0Info.EntryCount];
                    break;
                case Section0BlockType.BoundingBoxes:
                    state.BoundingBoxesIndex = index;
                    state.BoundingBoxes = new FmdlBoundingBox[section0Info.EntryCount];
                    break;
                case Section0BlockType.BufferOffsets:
                    state.BufferOffsetsIndex = index;
                    state.BufferOffsets = new FmdlBufferOffset[section0Info.EntryCount];
                    break;
                case Section0BlockType.LodInfo:
                    state.LodInfoIndex = index;
                    state.LodInfos = new FmdlLodInfo[section0Info.EntryCount];
                    break;
                case Section0BlockType.FaceInfo:
                    state.FaceInfoIndex = index;
                    state.FaceInfos = new FmdlFaceInfo[section0Info.EntryCount];
                    break;
                case Section0BlockType.Type12:
                    state.Type12Index = index;
                    state.Type12s = new FmdlType12[section0Info.EntryCount];
                    break;
                case Section0BlockType.Type14:
                    state.Type14Index = index;
                    state.Type14s = new FmdlType14[section0Info.EntryCount];
                    break;
                case Section0BlockType.PathCode64s:
                    state.PathCode64sIndex = index;
                    state.PathCode64s = new ulong[section0Info.EntryCount];
                    break;
                case Section0BlockType.StrCode64s:
                    state.StrCode64sIndex = index;
                    state.StrCode64s = new ulong[section0Info.EntryCount];
                    break;
            }

            state.Section0Infos[index] = section0Info;
        }

        for (int index = 0; index < state.Section1Infos.Length; index++)
        {
            Section1Info section1Info = new()
            {
                Type = reader.ReadUInt32(),
                Offset = reader.ReadUInt32(),
                Length = reader.ReadUInt32(),
            };

            switch ((Section1BlockType)section1Info.Type)
            {
                case Section1BlockType.MaterialParameterVectors:
                    state.MaterialParameterVectorsIndex = index;
                    state.MaterialParameterVectors = new Vector4[section1Info.Length / 16];
                    break;
                case Section1BlockType.Buffer:
                    state.BufferIndex = index;
                    state.Meshes = new FmdlMeshData[state.MeshInfos.Length];
                    break;
                case Section1BlockType.Strings:
                    state.StringsIndex = index;
                    state.Strings = new string[state.StringInfos.Length];
                    break;
            }

            state.Section1Infos[index] = section1Info;
        }
    }

    private static void ReadBones(BinaryReader reader, ParserState state)
    {
        reader.BaseStream.Position = state.Section0Offset + state.Section0Infos[state.BonesIndex].Offset;

        for (int index = 0; index < state.Bones.Length; index++)
        {
            FmdlBone bone = new()
            {
                NameIndex = reader.ReadUInt16(),
                ParentIndex = reader.ReadInt16(),
                BoundingBoxIndex = reader.ReadUInt16(),
                Unknown0 = reader.ReadUInt16(),
            };

            reader.BaseStream.Position += 8;
            bone.LocalPosition = ReadVector4(reader);
            bone.WorldPosition = ReadVector4(reader);
            state.Bones[index] = bone;
        }
    }

    private static void ReadMeshGroups(BinaryReader reader, ParserState state)
    {
        reader.BaseStream.Position = state.Section0Offset + state.Section0Infos[state.MeshGroupsIndex].Offset;

        for (int index = 0; index < state.MeshGroups.Length; index++)
        {
            state.MeshGroups[index] = new FmdlMeshGroup
            {
                NameIndex = reader.ReadUInt16(),
                InvisibilityFlag = reader.ReadUInt16(),
                ParentIndex = reader.ReadInt16(),
                Unknown0 = reader.ReadInt16(),
            };
        }
    }

    private static void ReadMeshGroupEntries(BinaryReader reader, ParserState state)
    {
        reader.BaseStream.Position = state.Section0Offset + state.Section0Infos[state.MeshGroupEntriesIndex].Offset;

        for (int index = 0; index < state.MeshGroupEntries.Length; index++)
        {
            reader.BaseStream.Position += 4;

            FmdlMeshGroupEntry entry = new()
            {
                MeshGroupIndex = reader.ReadUInt16(),
                MeshCount = reader.ReadUInt16(),
                FirstMeshIndex = reader.ReadUInt16(),
                Index = reader.ReadUInt16(),
            };

            reader.BaseStream.Position += 4;
            entry.FirstFaceInfoIndex = reader.ReadUInt16();
            reader.BaseStream.Position += 0xE;
            state.MeshGroupEntries[index] = entry;
        }
    }

    private static void ReadMeshInfo(BinaryReader reader, ParserState state)
    {
        reader.BaseStream.Position = state.Section0Offset + state.Section0Infos[state.MeshInfoIndex].Offset;

        for (int index = 0; index < state.MeshInfos.Length; index++)
        {
            FmdlMeshInfo meshInfo = new()
            {
                AlphaEnum = reader.ReadByte(),
                ShadowEnum = reader.ReadByte(),
                Unknown0 = reader.ReadByte(),
                Unknown1 = reader.ReadByte(),
                MaterialInstanceIndex = reader.ReadUInt16(),
                BoneGroupIndex = reader.ReadUInt16(),
                Index = reader.ReadUInt16(),
                VertexCount = reader.ReadUInt16(),
            };

            reader.BaseStream.Position += 4;
            meshInfo.FirstFaceVertexIndex = reader.ReadUInt32();
            meshInfo.FaceVertexCount = reader.ReadUInt32();
            meshInfo.FirstFaceInfoIndex = reader.ReadUInt64();
            reader.BaseStream.Position += 0x10;
            state.MeshInfos[index] = meshInfo;
        }
    }

    private static void ReadMaterialInstances(BinaryReader reader, ParserState state)
    {
        reader.BaseStream.Position = state.Section0Offset + state.Section0Infos[state.MaterialInstancesIndex].Offset;

        for (int index = 0; index < state.MaterialInstances.Length; index++)
        {
            FmdlMaterialInstance materialInstance = new()
            {
                NameIndex = reader.ReadUInt16(),
            };

            reader.BaseStream.Position += 2;
            materialInstance.MaterialIndex = reader.ReadUInt16();
            materialInstance.TextureCount = reader.ReadByte();
            materialInstance.ParameterCount = reader.ReadByte();
            materialInstance.FirstTextureIndex = reader.ReadUInt16();
            materialInstance.FirstParameterIndex = reader.ReadUInt16();
            reader.BaseStream.Position += 4;

            state.MaterialInstances[index] = materialInstance;
        }
    }

    private static void ReadBoneGroups(BinaryReader reader, ParserState state)
    {
        reader.BaseStream.Position = state.Section0Offset + state.Section0Infos[state.BoneGroupsIndex].Offset;

        for (int index = 0; index < state.BoneGroups.Length; index++)
        {
            FmdlBoneGroup boneGroup = new()
            {
                Unknown0 = reader.ReadUInt16(),
                BoneIndexCount = reader.ReadUInt16(),
            };

            boneGroup.BoneIndices = new ushort[boneGroup.BoneIndexCount];
            for (int boneIndex = 0; boneIndex < boneGroup.BoneIndices.Length; boneIndex++)
            {
                boneGroup.BoneIndices[boneIndex] = reader.ReadUInt16();
            }

            reader.BaseStream.Position += 0x40 - boneGroup.BoneIndices.Length * 2;
            state.BoneGroups[index] = boneGroup;
        }
    }

    private static void ReadTextures(BinaryReader reader, ParserState state)
    {
        reader.BaseStream.Position = state.Section0Offset + state.Section0Infos[state.TexturesIndex].Offset;

        for (int index = 0; index < state.Textures.Length; index++)
        {
            state.Textures[index] = new FmdlTexture
            {
                NameIndex = reader.ReadUInt16(),
                PathIndex = reader.ReadUInt16(),
            };
        }
    }

    private static void ReadMaterialParameters(BinaryReader reader, ParserState state)
    {
        reader.BaseStream.Position = state.Section0Offset + state.Section0Infos[state.MaterialParametersIndex].Offset;

        for (int index = 0; index < state.MaterialParameters.Length; index++)
        {
            state.MaterialParameters[index] = new FmdlMaterialParameter
            {
                NameIndex = reader.ReadUInt16(),
                ReferenceIndex = reader.ReadUInt16(),
            };
        }
    }

    private static void ReadMaterials(BinaryReader reader, ParserState state)
    {
        reader.BaseStream.Position = state.Section0Offset + state.Section0Infos[state.MaterialsIndex].Offset;

        for (int index = 0; index < state.Materials.Length; index++)
        {
            state.Materials[index] = new FmdlMaterial
            {
                NameIndex = reader.ReadUInt16(),
                TypeIndex = reader.ReadUInt16(),
            };
        }
    }

    private static void ReadMeshFormatInfo(BinaryReader reader, ParserState state)
    {
        reader.BaseStream.Position = state.Section0Offset + state.Section0Infos[state.MeshFormatInfoIndex].Offset;

        for (int index = 0; index < state.MeshFormatInfos.Length; index++)
        {
            state.MeshFormatInfos[index] = new FmdlMeshFormatInfo
            {
                MeshFormatCount = reader.ReadByte(),
                VertexFormatCount = reader.ReadByte(),
                Unknown0 = reader.ReadByte(),
                UvCount = reader.ReadByte(),
                FirstMeshFormatIndex = reader.ReadUInt16(),
                FirstVertexFormatIndex = reader.ReadUInt16(),
            };
        }
    }

    private static void ReadMeshFormats(BinaryReader reader, ParserState state)
    {
        reader.BaseStream.Position = state.Section0Offset + state.Section0Infos[state.MeshFormatsIndex].Offset;

        for (int index = 0; index < state.MeshFormats.Length; index++)
        {
            state.MeshFormats[index] = new FmdlMeshFormat
            {
                BufferOffsetIndex = reader.ReadByte(),
                VertexFormatCount = reader.ReadByte(),
                Length = reader.ReadByte(),
                Type = reader.ReadByte(),
                Offset = reader.ReadUInt32(),
            };
        }
    }

    private static void ReadVertexFormats(BinaryReader reader, ParserState state)
    {
        reader.BaseStream.Position = state.Section0Offset + state.Section0Infos[state.VertexFormatsIndex].Offset;

        for (int index = 0; index < state.VertexFormats.Length; index++)
        {
            state.VertexFormats[index] = new FmdlVertexFormat
            {
                Type = reader.ReadByte(),
                DataType = reader.ReadByte(),
                Offset = reader.ReadUInt16(),
            };
        }
    }

    private static void ReadStringInfo(BinaryReader reader, ParserState state)
    {
        reader.BaseStream.Position = state.Section0Offset + state.Section0Infos[state.StringInfoIndex].Offset;

        for (int index = 0; index < state.StringInfos.Length; index++)
        {
            state.StringInfos[index] = new FmdlStringInfo
            {
                Section1BlockIndex = reader.ReadUInt16(),
                Length = reader.ReadUInt16(),
                Offset = reader.ReadUInt32(),
            };
        }
    }

    private static void ReadBoundingBoxes(BinaryReader reader, ParserState state)
    {
        reader.BaseStream.Position = state.Section0Offset + state.Section0Infos[state.BoundingBoxesIndex].Offset;

        for (int index = 0; index < state.BoundingBoxes.Length; index++)
        {
            state.BoundingBoxes[index] = new FmdlBoundingBox
            {
                Max = ReadVector4(reader),
                Min = ReadVector4(reader),
            };
        }
    }

    private static void ReadBufferOffsets(BinaryReader reader, ParserState state)
    {
        reader.BaseStream.Position = state.Section0Offset + state.Section0Infos[state.BufferOffsetsIndex].Offset;

        for (int index = 0; index < state.BufferOffsets.Length; index++)
        {
            state.BufferOffsets[index] = new FmdlBufferOffset
            {
                Unknown0 = reader.ReadUInt32(),
                Length = reader.ReadUInt32(),
                Offset = reader.ReadUInt32(),
            };

            reader.BaseStream.Position += 4;
        }
    }

    private static void ReadLodInfo(BinaryReader reader, ParserState state)
    {
        reader.BaseStream.Position = state.Section0Offset + state.Section0Infos[state.LodInfoIndex].Offset;

        for (int index = 0; index < state.LodInfos.Length; index++)
        {
            state.LodInfos[index] = new FmdlLodInfo
            {
                LodCount = reader.ReadUInt32(),
                Unknown0 = reader.ReadSingle(),
                Unknown1 = reader.ReadSingle(),
                Unknown2 = reader.ReadSingle(),
            };
        }
    }

    private static void ReadFaceInfo(BinaryReader reader, ParserState state)
    {
        reader.BaseStream.Position = state.Section0Offset + state.Section0Infos[state.FaceInfoIndex].Offset;

        for (int index = 0; index < state.FaceInfos.Length; index++)
        {
            state.FaceInfos[index] = new FmdlFaceInfo
            {
                FirstFaceVertexIndex = reader.ReadUInt32(),
                FaceVertexCount = reader.ReadUInt32(),
            };
        }
    }

    private static void ReadType12(BinaryReader reader, ParserState state)
    {
        reader.BaseStream.Position = state.Section0Offset + state.Section0Infos[state.Type12Index].Offset;

        for (int index = 0; index < state.Type12s.Length; index++)
        {
            state.Type12s[index] = new FmdlType12
            {
                Unknown0 = reader.ReadUInt64(),
            };
        }
    }

    private static void ReadType14(BinaryReader reader, ParserState state)
    {
        reader.BaseStream.Position = state.Section0Offset + state.Section0Infos[state.Type14Index].Offset;

        for (int index = 0; index < state.Type14s.Length; index++)
        {
            reader.BaseStream.Position += 4;

            FmdlType14 type14 = new()
            {
                Unknown0 = reader.ReadSingle(),
                Unknown1 = reader.ReadSingle(),
                Unknown2 = reader.ReadSingle(),
                Unknown3 = reader.ReadSingle(),
            };

            reader.BaseStream.Position += 8;
            type14.Unknown4 = reader.ReadUInt32();
            type14.Unknown5 = reader.ReadUInt32();
            reader.BaseStream.Position += 0x5C;
            state.Type14s[index] = type14;
        }
    }

    private static void ReadPathCode64s(BinaryReader reader, ParserState state)
    {
        reader.BaseStream.Position = state.Section0Offset + state.Section0Infos[state.PathCode64sIndex].Offset;

        for (int index = 0; index < state.PathCode64s.Length; index++)
        {
            state.PathCode64s[index] = reader.ReadUInt64();
        }
    }

    private static void ReadStrCode64s(BinaryReader reader, ParserState state)
    {
        reader.BaseStream.Position = state.Section0Offset + state.Section0Infos[state.StrCode64sIndex].Offset;

        for (int index = 0; index < state.StrCode64s.Length; index++)
        {
            state.StrCode64s[index] = reader.ReadUInt64();
        }
    }

    private static void ReadMaterialParameterVectors(BinaryReader reader, ParserState state)
    {
        reader.BaseStream.Position = state.Section1Offset + state.Section1Infos[state.MaterialParameterVectorsIndex].Offset;

        for (int index = 0; index < state.MaterialParameterVectors.Length; index++)
        {
            state.MaterialParameterVectors[index] = ReadVector4(reader);
        }
    }

    private static void ReadBuffer(BinaryReader reader, ParserState state)
    {
        for (int meshIndex = 0; meshIndex < state.MeshInfos.Length; meshIndex++)
        {
            FmdlMeshData mesh = new();
            FmdlMeshInfo meshInfo = state.MeshInfos[meshIndex];
            FmdlMeshFormatInfo meshFormatInfo = state.MeshFormatInfos[meshIndex];

            for (int vertexFormatIndex = meshFormatInfo.FirstVertexFormatIndex;
                 vertexFormatIndex < meshFormatInfo.FirstVertexFormatIndex + meshFormatInfo.VertexFormatCount;
                 vertexFormatIndex++)
            {
                switch (state.VertexFormats[vertexFormatIndex].Type)
                {
                    case 0:
                        mesh.Vertices = new Vector3[meshInfo.VertexCount];
                        break;
                    case 1:
                        mesh.BoneWeights = new Vector4[meshInfo.VertexCount];
                        break;
                    case 2:
                        mesh.Normals = new Vector4[meshInfo.VertexCount];
                        break;
                    case 3:
                        mesh.Colors = new Vector4[meshInfo.VertexCount];
                        break;
                    case 7:
                        mesh.BoneIndices = new Vector4[meshInfo.VertexCount];
                        break;
                    case 8:
                        mesh.Uv = new Vector2[meshInfo.VertexCount];
                        break;
                    case 9:
                        mesh.Uv2 = new Vector2[meshInfo.VertexCount];
                        break;
                    case 0xA:
                        mesh.Uv3 = new Vector2[meshInfo.VertexCount];
                        break;
                    case 0xB:
                        mesh.Uv4 = new Vector2[meshInfo.VertexCount];
                        break;
                    case 0xC:
                        mesh.UnknownWeights = new Vector4[meshInfo.VertexCount];
                        break;
                    case 0xD:
                        mesh.UnknownIndices = new Vector4[meshInfo.VertexCount];
                        break;
                    case 0xE:
                        mesh.Tangents = new Vector4[meshInfo.VertexCount];
                        break;
                }
            }

            reader.BaseStream.Position = state.Section1Offset +
                                         state.Section1Infos[state.BufferIndex].Offset +
                                         state.BufferOffsets[0].Offset +
                                         state.MeshFormats[meshFormatInfo.FirstMeshFormatIndex].Offset;

            for (int vertexIndex = 0; vertexIndex < mesh.Vertices.Length; vertexIndex++)
            {
                mesh.Vertices[vertexIndex] = ReadVector3(reader);
            }

            reader.BaseStream.Position = state.Section1Offset +
                                         state.Section1Infos[state.BufferIndex].Offset +
                                         state.BufferOffsets[1].Offset +
                                         state.MeshFormats[meshFormatInfo.FirstMeshFormatIndex + 1].Offset;

            for (int vertexIndex = 0; vertexIndex < meshInfo.VertexCount; vertexIndex++)
            {
                long vertexBasePosition = reader.BaseStream.Position;

                for (int vertexFormatIndex = meshFormatInfo.FirstVertexFormatIndex;
                     vertexFormatIndex < meshFormatInfo.FirstVertexFormatIndex + meshFormatInfo.VertexFormatCount;
                     vertexFormatIndex++)
                {
                    FmdlVertexFormat vertexFormat = state.VertexFormats[vertexFormatIndex];
                    reader.BaseStream.Position = vertexBasePosition + vertexFormat.Offset;

                    switch (vertexFormat.Type)
                    {
                        case 0:
                            break;
                        case 1:
                            mesh.BoneWeights![vertexIndex] = new Vector4(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
                            break;
                        case 2:
                            mesh.Normals![vertexIndex] = ReadHalfVector4(reader);
                            break;
                        case 3:
                            mesh.Colors![vertexIndex] = new Vector4(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
                            break;
                        case 7:
                            mesh.BoneIndices![vertexIndex] = new Vector4(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
                            break;
                        case 8:
                            mesh.Uv![vertexIndex] = ReadHalfVector2(reader);
                            break;
                        case 9:
                            mesh.Uv2![vertexIndex] = ReadHalfVector2(reader);
                            break;
                        case 0xA:
                            mesh.Uv3![vertexIndex] = ReadHalfVector2(reader);
                            break;
                        case 0xB:
                            mesh.Uv4![vertexIndex] = ReadHalfVector2(reader);
                            break;
                        case 0xC:
                            mesh.UnknownWeights![vertexIndex] = new Vector4(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
                            break;
                        case 0xD:
                            mesh.UnknownIndices![vertexIndex] = new Vector4(reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadUInt16());
                            break;
                        case 0xE:
                            mesh.Tangents![vertexIndex] = ReadHalfVector4(reader);
                            break;
                    }
                }

                reader.BaseStream.Position = vertexBasePosition + state.MeshFormats[meshFormatInfo.FirstMeshFormatIndex + 1].Length;
            }

            reader.BaseStream.Position = state.Section1Offset +
                                         state.Section1Infos[state.BufferIndex].Offset +
                                         state.BufferOffsets[2].Offset +
                                         meshInfo.FirstFaceVertexIndex * 2;

            mesh.Triangles = new ushort[meshInfo.FaceVertexCount];
            for (int triangleIndex = 0; triangleIndex < mesh.Triangles.Length; triangleIndex++)
            {
                mesh.Triangles[triangleIndex] = reader.ReadUInt16();
            }

            state.Meshes[meshIndex] = mesh;
        }
    }

    private static void ReadStrings(BinaryReader reader, ParserState state)
    {
        for (int index = 0; index < state.StringInfos.Length; index++)
        {
            FmdlStringInfo stringInfo = state.StringInfos[index];
            reader.BaseStream.Position = state.Section1Offset + state.Section1Infos[state.StringsIndex].Offset + stringInfo.Offset;

            byte[] data = reader.ReadBytes(stringInfo.Length);
            state.Strings[index] = Encoding.UTF8.GetString(data).TrimEnd('\0');
        }
    }

    private static Vector2 ReadHalfVector2(BinaryReader reader)
    {
        return new Vector2((float)BitConverter.UInt16BitsToHalf(reader.ReadUInt16()), (float)BitConverter.UInt16BitsToHalf(reader.ReadUInt16()));
    }

    private static Vector3 ReadVector3(BinaryReader reader)
    {
        return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    private static Vector4 ReadHalfVector4(BinaryReader reader)
    {
        return new Vector4(
            (float)BitConverter.UInt16BitsToHalf(reader.ReadUInt16()),
            (float)BitConverter.UInt16BitsToHalf(reader.ReadUInt16()),
            (float)BitConverter.UInt16BitsToHalf(reader.ReadUInt16()),
            (float)BitConverter.UInt16BitsToHalf(reader.ReadUInt16()));
    }

    private static Vector4 ReadVector4(BinaryReader reader)
    {
        return new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    private sealed class ParserState
    {
        public ParserState(string modelName)
        {
            Name = modelName;
        }

        public string Name { get; }
        public uint Signature { get; set; }
        public float Version { get; set; }
        public ulong SectionInfoOffset { get; set; }
        public ulong Section0BlockFlags { get; set; }
        public ulong Section1BlockFlags { get; set; }
        public uint Section0BlockCount { get; set; }
        public uint Section1BlockCount { get; set; }
        public uint Section0Offset { get; set; }
        public uint Section0Length { get; set; }
        public uint Section1Offset { get; set; }
        public uint Section1Length { get; set; }

        public Section0Info[] Section0Infos { get; set; } = Array.Empty<Section0Info>();
        public Section1Info[] Section1Infos { get; set; } = Array.Empty<Section1Info>();

        public int BonesIndex { get; set; } = -1;
        public int MeshGroupsIndex { get; set; } = -1;
        public int MeshGroupEntriesIndex { get; set; } = -1;
        public int MeshInfoIndex { get; set; } = -1;
        public int MaterialInstancesIndex { get; set; } = -1;
        public int BoneGroupsIndex { get; set; } = -1;
        public int TexturesIndex { get; set; } = -1;
        public int MaterialParametersIndex { get; set; } = -1;
        public int MaterialsIndex { get; set; } = -1;
        public int MeshFormatInfoIndex { get; set; } = -1;
        public int MeshFormatsIndex { get; set; } = -1;
        public int VertexFormatsIndex { get; set; } = -1;
        public int StringInfoIndex { get; set; } = -1;
        public int BoundingBoxesIndex { get; set; } = -1;
        public int BufferOffsetsIndex { get; set; } = -1;
        public int LodInfoIndex { get; set; } = -1;
        public int FaceInfoIndex { get; set; } = -1;
        public int Type12Index { get; set; } = -1;
        public int Type14Index { get; set; } = -1;
        public int PathCode64sIndex { get; set; } = -1;
        public int StrCode64sIndex { get; set; } = -1;
        public int MaterialParameterVectorsIndex { get; set; } = -1;
        public int BufferIndex { get; set; } = -1;
        public int StringsIndex { get; set; } = -1;

        public FmdlBone[] Bones { get; set; } = Array.Empty<FmdlBone>();
        public FmdlMeshGroup[] MeshGroups { get; set; } = Array.Empty<FmdlMeshGroup>();
        public FmdlMeshGroupEntry[] MeshGroupEntries { get; set; } = Array.Empty<FmdlMeshGroupEntry>();
        public FmdlMeshInfo[] MeshInfos { get; set; } = Array.Empty<FmdlMeshInfo>();
        public FmdlMaterialInstance[] MaterialInstances { get; set; } = Array.Empty<FmdlMaterialInstance>();
        public FmdlBoneGroup[] BoneGroups { get; set; } = Array.Empty<FmdlBoneGroup>();
        public FmdlTexture[] Textures { get; set; } = Array.Empty<FmdlTexture>();
        public FmdlMaterialParameter[] MaterialParameters { get; set; } = Array.Empty<FmdlMaterialParameter>();
        public FmdlMaterial[] Materials { get; set; } = Array.Empty<FmdlMaterial>();
        public FmdlMeshFormatInfo[] MeshFormatInfos { get; set; } = Array.Empty<FmdlMeshFormatInfo>();
        public FmdlMeshFormat[] MeshFormats { get; set; } = Array.Empty<FmdlMeshFormat>();
        public FmdlVertexFormat[] VertexFormats { get; set; } = Array.Empty<FmdlVertexFormat>();
        public FmdlStringInfo[] StringInfos { get; set; } = Array.Empty<FmdlStringInfo>();
        public FmdlBoundingBox[] BoundingBoxes { get; set; } = Array.Empty<FmdlBoundingBox>();
        public FmdlBufferOffset[] BufferOffsets { get; set; } = Array.Empty<FmdlBufferOffset>();
        public FmdlLodInfo[] LodInfos { get; set; } = Array.Empty<FmdlLodInfo>();
        public FmdlFaceInfo[] FaceInfos { get; set; } = Array.Empty<FmdlFaceInfo>();
        public FmdlType12[] Type12s { get; set; } = Array.Empty<FmdlType12>();
        public FmdlType14[] Type14s { get; set; } = Array.Empty<FmdlType14>();
        public ulong[] PathCode64s { get; set; } = Array.Empty<ulong>();
        public ulong[] StrCode64s { get; set; } = Array.Empty<ulong>();
        public Vector4[] MaterialParameterVectors { get; set; } = Array.Empty<Vector4>();
        public FmdlMeshData[] Meshes { get; set; } = Array.Empty<FmdlMeshData>();
        public string[] Strings { get; set; } = Array.Empty<string>();

        public FmdlFile ToFile()
        {
            return new FmdlFile(
                Name,
                Version,
                Bones,
                MeshGroups,
                MeshGroupEntries,
                MeshInfos,
                MaterialInstances,
                BoneGroups,
                Textures,
                MaterialParameters,
                Materials,
                MaterialParameterVectors,
                Meshes,
                Strings,
                PathCode64s,
                StrCode64s);
        }
    }
}

internal sealed class FmdlFile
{
    public FmdlFile(
        string name,
        float version,
        FmdlBone[] bones,
        FmdlMeshGroup[] meshGroups,
        FmdlMeshGroupEntry[] meshGroupEntries,
        FmdlMeshInfo[] meshInfos,
        FmdlMaterialInstance[] materialInstances,
        FmdlBoneGroup[] boneGroups,
        FmdlTexture[] textures,
        FmdlMaterialParameter[] materialParameters,
        FmdlMaterial[] materials,
        Vector4[] materialParameterVectors,
        FmdlMeshData[] meshes,
        string[] strings,
        ulong[] pathCode64s,
        ulong[] strCode64s)
    {
        Name = name;
        Version = version;
        Bones = bones;
        MeshGroups = meshGroups;
        MeshGroupEntries = meshGroupEntries;
        MeshInfos = meshInfos;
        MaterialInstances = materialInstances;
        BoneGroups = boneGroups;
        Textures = textures;
        MaterialParameters = materialParameters;
        Materials = materials;
        MaterialParameterVectors = materialParameterVectors;
        Meshes = meshes;
        Strings = strings;
        PathCode64s = pathCode64s;
        StrCode64s = strCode64s;
    }

    public string Name { get; }
    public float Version { get; }
    public bool IsGroundZeroesFormat => Math.Abs(Version - 2.03f) < 0.0001f;
    public FmdlBone[] Bones { get; }
    public FmdlMeshGroup[] MeshGroups { get; }
    public FmdlMeshGroupEntry[] MeshGroupEntries { get; }
    public FmdlMeshInfo[] MeshInfos { get; }
    public FmdlMaterialInstance[] MaterialInstances { get; }
    public FmdlBoneGroup[] BoneGroups { get; }
    public FmdlTexture[] Textures { get; }
    public FmdlMaterialParameter[] MaterialParameters { get; }
    public FmdlMaterial[] Materials { get; }
    public Vector4[] MaterialParameterVectors { get; }
    public FmdlMeshData[] Meshes { get; }
    public string[] Strings { get; }
    public ulong[] PathCode64s { get; }
    public ulong[] StrCode64s { get; }

    public string ResolveStringIndex(ushort index, FoxHashLookup hashLookup)
    {
        return IsGroundZeroesFormat ? Strings[index] : hashLookup.ResolveStringHash(StrCode64s[index]);
    }

    public string ResolveTextureReference(FmdlTexture texture, FoxHashLookup hashLookup)
    {
        if (IsGroundZeroesFormat)
        {
            string basePath = Strings[texture.PathIndex];
            string name = Strings[texture.NameIndex];
            int extensionIndex = name.IndexOf('.', StringComparison.Ordinal);
            string normalizedName = extensionIndex >= 0 ? name[..extensionIndex] + ".dds" : name + ".dds";
            return basePath + normalizedName;
        }

        string resolved = hashLookup.ResolvePathHash(PathCode64s[texture.PathIndex]);
        return resolved.EndsWith(".ftex", StringComparison.OrdinalIgnoreCase)
            ? Path.ChangeExtension(resolved, ".dds")
            : resolved + ".dds";
    }

    public int GetMeshGroupIndex(int meshIndex)
    {
        foreach (FmdlMeshGroupEntry entry in MeshGroupEntries)
        {
            if (entry.FirstMeshIndex <= meshIndex && entry.FirstMeshIndex + entry.MeshCount > meshIndex)
            {
                return entry.MeshGroupIndex;
            }
        }

        return -1;
    }
}

internal readonly record struct Section0Info(ushort Type, ushort EntryCount, uint Offset);
internal readonly record struct Section1Info(uint Type, uint Offset, uint Length);

internal struct FmdlBone
{
    public ushort NameIndex { get; set; }
    public short ParentIndex { get; set; }
    public ushort BoundingBoxIndex { get; set; }
    public ushort Unknown0 { get; set; }
    public Vector4 LocalPosition { get; set; }
    public Vector4 WorldPosition { get; set; }
}

internal struct FmdlMeshGroup
{
    public ushort NameIndex { get; set; }
    public ushort InvisibilityFlag { get; set; }
    public short ParentIndex { get; set; }
    public short Unknown0 { get; set; }
}

internal struct FmdlMeshGroupEntry
{
    public ushort MeshGroupIndex { get; set; }
    public ushort MeshCount { get; set; }
    public ushort FirstMeshIndex { get; set; }
    public ushort Index { get; set; }
    public ushort FirstFaceInfoIndex { get; set; }
}

internal struct FmdlMeshInfo
{
    public byte AlphaEnum { get; set; }
    public byte ShadowEnum { get; set; }
    public byte Unknown0 { get; set; }
    public byte Unknown1 { get; set; }
    public ushort MaterialInstanceIndex { get; set; }
    public ushort BoneGroupIndex { get; set; }
    public ushort Index { get; set; }
    public ushort VertexCount { get; set; }
    public uint FirstFaceVertexIndex { get; set; }
    public uint FaceVertexCount { get; set; }
    public ulong FirstFaceInfoIndex { get; set; }
}

internal struct FmdlMaterialInstance
{
    public ushort NameIndex { get; set; }
    public ushort MaterialIndex { get; set; }
    public byte TextureCount { get; set; }
    public byte ParameterCount { get; set; }
    public ushort FirstTextureIndex { get; set; }
    public ushort FirstParameterIndex { get; set; }
}

internal struct FmdlBoneGroup
{
    public ushort Unknown0 { get; set; }
    public ushort BoneIndexCount { get; set; }
    public ushort[] BoneIndices { get; set; }
}

internal struct FmdlTexture
{
    public ushort NameIndex { get; set; }
    public ushort PathIndex { get; set; }
}

internal struct FmdlMaterialParameter
{
    public ushort NameIndex { get; set; }
    public ushort ReferenceIndex { get; set; }
}

internal struct FmdlMaterial
{
    public ushort NameIndex { get; set; }
    public ushort TypeIndex { get; set; }
}

internal struct FmdlMeshFormatInfo
{
    public byte MeshFormatCount { get; set; }
    public byte VertexFormatCount { get; set; }
    public byte Unknown0 { get; set; }
    public byte UvCount { get; set; }
    public ushort FirstMeshFormatIndex { get; set; }
    public ushort FirstVertexFormatIndex { get; set; }
}

internal struct FmdlMeshFormat
{
    public byte BufferOffsetIndex { get; set; }
    public byte VertexFormatCount { get; set; }
    public byte Length { get; set; }
    public byte Type { get; set; }
    public uint Offset { get; set; }
}

internal struct FmdlVertexFormat
{
    public byte Type { get; set; }
    public byte DataType { get; set; }
    public ushort Offset { get; set; }
}

internal struct FmdlStringInfo
{
    public ushort Section1BlockIndex { get; set; }
    public ushort Length { get; set; }
    public uint Offset { get; set; }
}

internal struct FmdlBoundingBox
{
    public Vector4 Max { get; set; }
    public Vector4 Min { get; set; }
}

internal struct FmdlBufferOffset
{
    public uint Unknown0 { get; set; }
    public uint Length { get; set; }
    public uint Offset { get; set; }
}

internal struct FmdlLodInfo
{
    public uint LodCount { get; set; }
    public float Unknown0 { get; set; }
    public float Unknown1 { get; set; }
    public float Unknown2 { get; set; }
}

internal struct FmdlFaceInfo
{
    public uint FirstFaceVertexIndex { get; set; }
    public uint FaceVertexCount { get; set; }
}

internal struct FmdlType12
{
    public ulong Unknown0 { get; set; }
}

internal struct FmdlType14
{
    public float Unknown0 { get; set; }
    public float Unknown1 { get; set; }
    public float Unknown2 { get; set; }
    public float Unknown3 { get; set; }
    public uint Unknown4 { get; set; }
    public uint Unknown5 { get; set; }
}

internal sealed class FmdlMeshData
{
    public Vector3[] Vertices { get; set; } = Array.Empty<Vector3>();
    public Vector4[]? Normals { get; set; }
    public Vector4[]? Tangents { get; set; }
    public Vector4[]? Colors { get; set; }
    public Vector4[]? BoneWeights { get; set; }
    public Vector4[]? BoneIndices { get; set; }
    public Vector2[]? Uv { get; set; }
    public Vector2[]? Uv2 { get; set; }
    public Vector2[]? Uv3 { get; set; }
    public Vector2[]? Uv4 { get; set; }
    public Vector4[]? UnknownWeights { get; set; }
    public Vector4[]? UnknownIndices { get; set; }
    public ushort[] Triangles { get; set; } = Array.Empty<ushort>();
}
