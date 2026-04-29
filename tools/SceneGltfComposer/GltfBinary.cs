using System.Text;
using System.Text.Json;

namespace SceneGltfComposer;

internal static class GltfBinary
{
    private const uint GlbMagic = 0x46546C67;
    private const uint JsonChunkType = 0x4E4F534A;
    private const uint BinChunkType = 0x004E4942;

    public static GltfDocument ReadGlb(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: false);

        if (reader.ReadUInt32() != GlbMagic)
        {
            throw new InvalidDataException(FormattableString.Invariant($"File '{path}' is not a valid GLB."));
        }

        uint version = reader.ReadUInt32();
        if (version != 2)
        {
            throw new InvalidDataException(FormattableString.Invariant($"GLB '{path}' uses unsupported version {version}."));
        }

        _ = reader.ReadUInt32();

        byte[]? jsonBytes = null;
        byte[]? binBytes = null;
        while (stream.Position < stream.Length)
        {
            uint chunkLength = reader.ReadUInt32();
            uint chunkType = reader.ReadUInt32();
            byte[] chunkData = reader.ReadBytes(checked((int)chunkLength));

            if (chunkType == JsonChunkType)
            {
                jsonBytes = chunkData;
            }
            else if (chunkType == BinChunkType)
            {
                binBytes = chunkData;
            }
        }

        if (jsonBytes is null || binBytes is null)
        {
            throw new InvalidDataException(FormattableString.Invariant($"GLB '{path}' is missing required chunks."));
        }

        GltfRoot? root = JsonSerializer.Deserialize<GltfRoot>(jsonBytes, GltfJson.SerializerOptions);
        if (root is null)
        {
            throw new InvalidDataException(FormattableString.Invariant($"GLB '{path}' contains invalid glTF JSON."));
        }

        return new GltfDocument(root, binBytes);
    }

    public static void Write(string outputPath, GltfRoot gltf, byte[] bufferData)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory());
        if (outputPath.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
        {
            WriteGlb(outputPath, gltf, bufferData);
        }
        else
        {
            WriteGltf(outputPath, gltf, bufferData);
        }
    }

    private static void WriteGltf(string outputPath, GltfRoot gltf, byte[] bufferData)
    {
        EnsureSingleBuffer(gltf, bufferData.Length);
        gltf.Buffers[0].Uri = Path.GetFileNameWithoutExtension(outputPath) + ".bin";
        string json = JsonSerializer.Serialize(gltf, GltfJson.SerializerOptions);
        File.WriteAllText(outputPath, json, Encoding.UTF8);
        File.WriteAllBytes(Path.ChangeExtension(outputPath, ".bin"), bufferData);
    }

    private static void WriteGlb(string outputPath, GltfRoot gltf, byte[] bufferData)
    {
        EnsureSingleBuffer(gltf, bufferData.Length);
        gltf.Buffers[0].Uri = null;
        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(gltf, GltfJson.SerializerOptions);

        int paddedJsonLength = Align4(jsonBytes.Length);
        int paddedBinLength = Align4(bufferData.Length);
        int totalLength = 12 + 8 + paddedJsonLength + 8 + paddedBinLength;

        using FileStream stream = File.Create(outputPath);
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: false);

        writer.Write(GlbMagic);
        writer.Write(2u);
        writer.Write(totalLength);

        writer.Write(paddedJsonLength);
        writer.Write(JsonChunkType);
        writer.Write(jsonBytes);
        for (int index = jsonBytes.Length; index < paddedJsonLength; index++)
        {
            writer.Write((byte)0x20);
        }

        writer.Write(paddedBinLength);
        writer.Write(BinChunkType);
        writer.Write(bufferData);
        for (int index = bufferData.Length; index < paddedBinLength; index++)
        {
            writer.Write((byte)0);
        }
    }

    private static void EnsureSingleBuffer(GltfRoot gltf, int bufferLength)
    {
        if (gltf.Buffers.Count == 0)
        {
            gltf.Buffers.Add(new GltfBuffer { ByteLength = bufferLength });
        }
        else
        {
            gltf.Buffers[0].ByteLength = bufferLength;
        }
    }

    public static int Align4(int value)
    {
        return (value + 3) & ~3;
    }
}
