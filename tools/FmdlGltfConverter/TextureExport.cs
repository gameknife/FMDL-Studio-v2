using System.IO.Compression;
using Pfim;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace FmdlGltfConverter;

internal sealed class TextureExportContext
{
    private readonly GltfRoot gltf;
    private readonly GltfBufferBuilder bufferBuilder;
    private readonly FoxTextureResolver textureResolver;
    private readonly Dictionary<string, int> textureIndicesBySourcePath = new(StringComparer.OrdinalIgnoreCase);
    private readonly int samplerIndex;

    public TextureExportContext(GltfRoot gltf, GltfBufferBuilder bufferBuilder, string sourceModelPath)
    {
        this.gltf = gltf;
        this.bufferBuilder = bufferBuilder;
        textureResolver = new FoxTextureResolver(sourceModelPath);

        samplerIndex = gltf.Samplers.Count;
        gltf.Samplers.Add(new GltfSampler
        {
            MagFilter = 9729,
            MinFilter = 9987,
            WrapS = 10497,
            WrapT = 10497,
        });
    }

    public int? AddTexture(string reference, FoxTextureUsage usage = FoxTextureUsage.Default)
    {
        TextureAsset? textureAsset = textureResolver.Resolve(reference, usage);
        if (textureAsset is null)
        {
            return null;
        }

        string cacheKey = $"{textureAsset.SourcePath}|{usage}";
        if (textureIndicesBySourcePath.TryGetValue(cacheKey, out int existingTextureIndex))
        {
            return existingTextureIndex;
        }

        int bufferViewIndex = gltf.BufferViews.Count;
        int byteOffset = bufferBuilder.AddBytes(textureAsset.PngBytes);
        gltf.BufferViews.Add(new GltfBufferView
        {
            Buffer = 0,
            ByteOffset = byteOffset,
            ByteLength = textureAsset.PngBytes.Length,
        });

        int imageIndex = gltf.Images.Count;
        gltf.Images.Add(new GltfImage
        {
            Name = Path.GetFileNameWithoutExtension(textureAsset.SourcePath),
            BufferView = bufferViewIndex,
            MimeType = "image/png",
        });

        int textureIndex = gltf.Textures.Count;
        gltf.Textures.Add(new GltfTexture
        {
            Name = Path.GetFileNameWithoutExtension(textureAsset.SourcePath),
            Sampler = samplerIndex,
            Source = imageIndex,
        });

        textureIndicesBySourcePath.Add(cacheKey, textureIndex);
        return textureIndex;
    }
}

internal sealed class FoxTextureResolver
{
    private static readonly string[] SupportedExtensions = [".ftex", ".dds", ".png", ".tga", ".jpg", ".jpeg", ".bmp"];

    private readonly string modelDirectory;
    private readonly string? assetsRoot;
    private readonly Dictionary<string, TextureAsset?> cache = new(StringComparer.OrdinalIgnoreCase);

    public FoxTextureResolver(string sourceModelPath)
    {
        modelDirectory = Path.GetDirectoryName(sourceModelPath) ?? Directory.GetCurrentDirectory();
        assetsRoot = FindAssetsRoot(sourceModelPath);
    }

    public TextureAsset? Resolve(string reference, FoxTextureUsage usage)
    {
        string cacheKey = $"{reference}|{usage}";

        if (cache.TryGetValue(cacheKey, out TextureAsset? cached))
        {
            return cached;
        }

        string? texturePath = FindTexturePath(reference);
        TextureAsset? textureAsset = texturePath is null ? null : LoadTexture(texturePath, usage);
        cache[cacheKey] = textureAsset;
        return textureAsset;
    }

    private TextureAsset? LoadTexture(string texturePath, FoxTextureUsage usage)
    {
        string extension = Path.GetExtension(texturePath);
        byte[] pngBytes = extension.ToLowerInvariant() switch
        {
            ".ftex" => TextureTranscoder.ConvertFtexToPng(texturePath, usage),
            ".dds" => TextureTranscoder.ConvertDdsToPng(File.ReadAllBytes(texturePath), usage),
            _ => TextureTranscoder.ConvertImageFileToPng(texturePath, usage),
        };

        return new TextureAsset(texturePath, pngBytes);
    }

    private string? FindTexturePath(string reference)
    {
        foreach (string candidate in ExpandCandidates(reference))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private IEnumerable<string> ExpandCandidates(string reference)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (string baseCandidate in GetBaseCandidates(reference))
        {
            foreach (string candidate in ExpandExtensions(baseCandidate))
            {
                if (seen.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }
    }

    private IEnumerable<string> GetBaseCandidates(string reference)
    {
        string normalizedReference = reference.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);

        if (Path.IsPathRooted(reference))
        {
            yield return reference;
        }

        if ((reference.StartsWith("/Assets/", StringComparison.OrdinalIgnoreCase) ||
             reference.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) &&
            assetsRoot is not null)
        {
            yield return Path.Combine(assetsRoot, normalizedReference);
        }

        yield return Path.Combine(modelDirectory, normalizedReference);

        string fileName = Path.GetFileName(normalizedReference);
        if (!string.IsNullOrEmpty(fileName))
        {
            yield return Path.Combine(modelDirectory, fileName);
            yield return Path.Combine(modelDirectory, "sourceimages", fileName);

            DirectoryInfo? directory = new(modelDirectory);
            while (directory is not null)
            {
                string sourceImagesPath = Path.Combine(directory.FullName, "sourceimages", fileName);
                yield return sourceImagesPath;

                directory = directory.Parent;
            }
        }
    }

    private static IEnumerable<string> ExpandExtensions(string path)
    {
        string extension = Path.GetExtension(path);

        if (!string.IsNullOrEmpty(extension))
        {
            yield return path;
        }

        string pathWithoutExtension = string.IsNullOrEmpty(extension) ? path : Path.ChangeExtension(path, null) ?? path;

        foreach (string supportedExtension in SupportedExtensions)
        {
            yield return pathWithoutExtension + supportedExtension;
        }
    }

    private static string? FindAssetsRoot(string sourceModelPath)
    {
        DirectoryInfo? directory = new(Path.GetDirectoryName(sourceModelPath) ?? Directory.GetCurrentDirectory());

        while (directory is not null)
        {
            if (directory.Name.Equals("Assets", StringComparison.OrdinalIgnoreCase))
            {
                return directory.Parent?.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}

internal sealed record TextureAsset(string SourcePath, byte[] PngBytes);

internal enum FoxTextureUsage
{
    Default,
    Normal,
}

internal static class TextureTranscoder
{
    private static readonly byte[] FtexMagic = [0x46, 0x54, 0x45, 0x58, 0x85, 0xEB, 0x01, 0x40];

    public static byte[] ConvertFtexToPng(string ftexPath, FoxTextureUsage usage = FoxTextureUsage.Default)
    {
        byte[] ddsBytes = ConvertFtexToDds(ftexPath);
        return ConvertDdsToPng(ddsBytes, usage);
    }

    public static byte[] ConvertImageFileToPng(string imagePath, FoxTextureUsage usage = FoxTextureUsage.Default)
    {
        using Image<Rgba32> image = SixLabors.ImageSharp.Image.Load<Rgba32>(imagePath);
        ApplyUsageTransform(image, usage);
        using MemoryStream pngStream = new();
        image.SaveAsPng(pngStream, new PngEncoder());
        return pngStream.ToArray();
    }

    public static byte[] ConvertDdsToPng(byte[] ddsBytes, FoxTextureUsage usage = FoxTextureUsage.Default)
    {
        using MemoryStream ddsStream = new(ddsBytes, writable: false);
        using IImage image = Pfimage.FromStream(ddsStream);

        if (image.Compressed)
        {
            image.Decompress();
        }

        byte[] rgbaBytes = ConvertToRgba32(image);

        using Image<Rgba32> rgbaImage = Image.LoadPixelData<Rgba32>(rgbaBytes, image.Width, image.Height);
        ApplyUsageTransform(rgbaImage, usage);
        using MemoryStream pngStream = new();
        rgbaImage.SaveAsPng(pngStream, new PngEncoder());
        return pngStream.ToArray();
    }

    private static void ApplyUsageTransform(Image<Rgba32> image, FoxTextureUsage usage)
    {
        if (usage != FoxTextureUsage.Normal)
        {
            return;
        }

        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                Rgba32 pixel = image[x, y];

                float nx = pixel.A / 255.0f;
                float ny = 1.0f - (pixel.G / 255.0f);

                float sx = nx * 2.0f - 1.0f;
                float sy = ny * 2.0f - 1.0f;
                float sz = MathF.Sqrt(MathF.Max(0.0f, 1.0f - (sx * sx + sy * sy)));
                float nz = (sz * 0.5f) + 0.5f;

                image[x, y] = new Rgba32(
                    ToByte(nx),
                    ToByte(ny),
                    ToByte(nz),
                    255);
            }
        }
    }

    private static byte ToByte(float value)
    {
        return (byte)Math.Clamp((int)MathF.Round(value * 255.0f), 0, 255);
    }

    private static byte[] ConvertFtexToDds(string ftexPath)
    {
        using FileStream ftexStream = File.OpenRead(ftexPath);
        using BinaryReader reader = new(ftexStream);

        byte[] magic = reader.ReadBytes(FtexMagic.Length);
        if (!magic.SequenceEqual(FtexMagic))
        {
            throw new InvalidDataException($"'{ftexPath}' is not a supported FTEX file.");
        }

        short pixelFormatType = reader.ReadInt16();
        short width = reader.ReadInt16();
        short height = reader.ReadInt16();
        short depth = reader.ReadInt16();
        byte mipMapCount = reader.ReadByte();
        reader.ReadByte();
        reader.ReadInt16();
        reader.ReadInt32();
        reader.ReadInt32();
        reader.ReadInt32();
        byte ftexsFileCount = reader.ReadByte();
        reader.ReadByte();
        reader.ReadByte();
        reader.ReadByte();
        reader.ReadInt32();
        reader.ReadInt32();
        reader.ReadInt32();
        reader.ReadBytes(16);

        List<FtexMipMapInfo> mipMapInfos = new(mipMapCount);
        for (int mipIndex = 0; mipIndex < mipMapCount; mipIndex++)
        {
            mipMapInfos.Add(new FtexMipMapInfo
            {
                Offset = reader.ReadInt32(),
                DecompressedSize = reader.ReadInt32(),
                Size = reader.ReadInt32(),
                Index = reader.ReadByte(),
                FileNumber = reader.ReadByte(),
                ChunkCount = reader.ReadInt16(),
            });
        }

        using MemoryStream textureData = new();
        foreach (FtexMipMapInfo mipMapInfo in mipMapInfos.OrderBy(info => info.Index))
        {
            byte[] mipData = ReadMipMapData(ftexPath, mipMapInfo, ftexsFileCount);
            textureData.Write(mipData, 0, mipData.Length);
        }

        return BuildDds(pixelFormatType, width, height, Math.Max(depth, (short)1), mipMapCount, textureData.ToArray());
    }

    private static byte[] ReadMipMapData(string ftexPath, FtexMipMapInfo mipMapInfo, byte ftexsFileCount)
    {
        string sourcePath = mipMapInfo.FileNumber == 0 && ftexsFileCount == 0
            ? ftexPath
            : GetChunkContainerPath(ftexPath, mipMapInfo.FileNumber);

        using FileStream sourceStream = File.OpenRead(sourcePath);
        using BinaryReader reader = new(sourceStream);

        sourceStream.Position = mipMapInfo.Offset;
        if (mipMapInfo.ChunkCount == 0)
        {
            return reader.ReadBytes(mipMapInfo.DecompressedSize);
        }

        List<FtexChunkInfo> chunks = new(mipMapInfo.ChunkCount);
        for (int chunkIndex = 0; chunkIndex < mipMapInfo.ChunkCount; chunkIndex++)
        {
            short compressedSize = reader.ReadInt16();
            short uncompressedSize = reader.ReadInt16();
            uint encodedOffset = reader.ReadUInt32();
            long dataOffset = encodedOffset > 0x80000000
                ? mipMapInfo.Offset + (encodedOffset - 0x80000000)
                : mipMapInfo.Offset + encodedOffset;

            chunks.Add(new FtexChunkInfo(compressedSize, uncompressedSize, dataOffset));
        }

        using MemoryStream mipStream = new(mipMapInfo.DecompressedSize);
        foreach (FtexChunkInfo chunk in chunks)
        {
            sourceStream.Position = chunk.DataOffset;
            byte[] chunkData = reader.ReadBytes(chunk.CompressedSize);

            if (chunk.CompressedSize != chunk.UncompressedSize)
            {
                using MemoryStream chunkStream = new(chunkData, writable: false);
                using ZLibStream zlibStream = new(chunkStream, CompressionMode.Decompress);
                zlibStream.CopyTo(mipStream);
            }
            else
            {
                mipStream.Write(chunkData, 0, chunkData.Length);
            }
        }

        return mipStream.ToArray();
    }

    private static string GetChunkContainerPath(string ftexPath, byte fileNumber)
    {
        if (fileNumber == 0)
        {
            return ftexPath;
        }

        string directory = Path.GetDirectoryName(ftexPath) ?? Directory.GetCurrentDirectory();
        string fileName = Path.GetFileNameWithoutExtension(ftexPath);
        return Path.Combine(directory, $"{fileName}.{fileNumber}.ftexs");
    }

    private static byte[] BuildDds(short pixelFormatType, short width, short height, short depth, byte mipMapCount, byte[] data)
    {
        using MemoryStream ddsStream = new();
        using BinaryWriter writer = new(ddsStream);

        writer.Write(0x20534444);
        writer.Write(124);

        uint headerFlags = 0x1 | 0x2 | 0x4 | 0x1000;
        if (pixelFormatType is 2 or 4)
        {
            headerFlags |= 0x80000;
        }
        else
        {
            headerFlags |= 0x8;
        }

        if (mipMapCount > 1)
        {
            headerFlags |= 0x20000;
        }

        if (depth > 1)
        {
            headerFlags |= 0x800000;
        }

        writer.Write(headerFlags);
        writer.Write((int)height);
        writer.Write((int)width);
        writer.Write(CalculateTopLevelSize(pixelFormatType, width, height, depth));
        writer.Write(depth > 1 ? depth : 0);
        writer.Write(mipMapCount > 1 ? mipMapCount : 0);

        for (int reservedIndex = 0; reservedIndex < 11; reservedIndex++)
        {
            writer.Write(0);
        }

        WritePixelFormat(writer, pixelFormatType);

        uint caps = 0x1000;
        if (mipMapCount > 1)
        {
            caps |= 0x8 | 0x400000;
        }

        writer.Write(caps);
        writer.Write(depth > 1 ? 0x200000u : 0u);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(data);

        return ddsStream.ToArray();
    }

    private static int CalculateTopLevelSize(short pixelFormatType, int width, int height, int depth)
    {
        return pixelFormatType switch
        {
            0 => width * height * depth * 4,
            1 => width * height * depth,
            2 => Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * depth * 8,
            4 => Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * depth * 16,
            _ => throw new InvalidDataException($"Unsupported FTEX pixel format type '{pixelFormatType}'."),
        };
    }

    private static void WritePixelFormat(BinaryWriter writer, short pixelFormatType)
    {
        writer.Write(32);

        switch (pixelFormatType)
        {
            case 0:
                writer.Write(0x41);
                writer.Write(0);
                writer.Write(32);
                writer.Write(0x00ff0000);
                writer.Write(0x0000ff00);
                writer.Write(0x000000ff);
                writer.Write(unchecked((int)0xff000000));
                break;
            case 1:
                writer.Write(0x20000);
                writer.Write(0);
                writer.Write(8);
                writer.Write(0x000000ff);
                writer.Write(0);
                writer.Write(0);
                writer.Write(0);
                break;
            case 2:
                writer.Write(0x4);
                writer.Write(0x31545844);
                writer.Write(0);
                writer.Write(0);
                writer.Write(0);
                writer.Write(0);
                writer.Write(0);
                break;
            case 4:
                writer.Write(0x4);
                writer.Write(0x35545844);
                writer.Write(0);
                writer.Write(0);
                writer.Write(0);
                writer.Write(0);
                writer.Write(0);
                break;
            default:
                throw new InvalidDataException($"Unsupported FTEX pixel format type '{pixelFormatType}'.");
        }
    }

    private static byte[] ConvertToRgba32(IImage image)
    {
        byte[] rgbaBytes = new byte[image.Width * image.Height * 4];
        byte[] sourceData = image.Data;

        switch (image.Format)
        {
            case ImageFormat.Rgba32:
                for (int row = 0; row < image.Height; row++)
                {
                    Buffer.BlockCopy(sourceData, row * image.Stride, rgbaBytes, row * image.Width * 4, image.Width * 4);
                }

                break;
            case ImageFormat.Rgb24:
                for (int row = 0; row < image.Height; row++)
                {
                    int sourceRowOffset = row * image.Stride;
                    int destinationRowOffset = row * image.Width * 4;

                    for (int column = 0; column < image.Width; column++)
                    {
                        int sourceOffset = sourceRowOffset + column * 3;
                        int destinationOffset = destinationRowOffset + column * 4;

                        rgbaBytes[destinationOffset] = sourceData[sourceOffset];
                        rgbaBytes[destinationOffset + 1] = sourceData[sourceOffset + 1];
                        rgbaBytes[destinationOffset + 2] = sourceData[sourceOffset + 2];
                        rgbaBytes[destinationOffset + 3] = 255;
                    }
                }

                break;
            case ImageFormat.Rgb8:
                for (int row = 0; row < image.Height; row++)
                {
                    int sourceRowOffset = row * image.Stride;
                    int destinationRowOffset = row * image.Width * 4;

                    for (int column = 0; column < image.Width; column++)
                    {
                        byte value = sourceData[sourceRowOffset + column];
                        int destinationOffset = destinationRowOffset + column * 4;
                        rgbaBytes[destinationOffset] = value;
                        rgbaBytes[destinationOffset + 1] = value;
                        rgbaBytes[destinationOffset + 2] = value;
                        rgbaBytes[destinationOffset + 3] = 255;
                    }
                }

                break;
            case ImageFormat.R5g6b5:
                return Convert16BitToRgba32(image, ConvertRgb565);
            case ImageFormat.R5g5b5:
                return Convert16BitToRgba32(image, ConvertRgb555);
            case ImageFormat.R5g5b5a1:
                return Convert16BitToRgba32(image, ConvertRgb5551);
            case ImageFormat.Rgba16:
                return Convert16BitToRgba32(image, ConvertRgba4444);
            default:
                throw new InvalidDataException($"Unsupported decoded DDS format '{image.Format}'.");
        }

        return rgbaBytes;
    }

    private static byte[] Convert16BitToRgba32(IImage image, Func<ushort, Rgba32> converter)
    {
        byte[] rgbaBytes = new byte[image.Width * image.Height * 4];
        byte[] sourceData = image.Data;

        for (int row = 0; row < image.Height; row++)
        {
            int sourceRowOffset = row * image.Stride;
            int destinationRowOffset = row * image.Width * 4;

            for (int column = 0; column < image.Width; column++)
            {
                ushort packed = BitConverter.ToUInt16(sourceData, sourceRowOffset + column * 2);
                Rgba32 pixel = converter(packed);
                int destinationOffset = destinationRowOffset + column * 4;

                rgbaBytes[destinationOffset] = pixel.R;
                rgbaBytes[destinationOffset + 1] = pixel.G;
                rgbaBytes[destinationOffset + 2] = pixel.B;
                rgbaBytes[destinationOffset + 3] = pixel.A;
            }
        }

        return rgbaBytes;
    }

    private static Rgba32 ConvertRgb565(ushort value)
    {
        byte r = Expand5To8((value >> 11) & 0x1F);
        byte g = Expand6To8((value >> 5) & 0x3F);
        byte b = Expand5To8(value & 0x1F);
        return new Rgba32(r, g, b, 255);
    }

    private static Rgba32 ConvertRgb555(ushort value)
    {
        byte r = Expand5To8((value >> 10) & 0x1F);
        byte g = Expand5To8((value >> 5) & 0x1F);
        byte b = Expand5To8(value & 0x1F);
        return new Rgba32(r, g, b, 255);
    }

    private static Rgba32 ConvertRgb5551(ushort value)
    {
        byte r = Expand5To8((value >> 10) & 0x1F);
        byte g = Expand5To8((value >> 5) & 0x1F);
        byte b = Expand5To8(value & 0x1F);
        byte a = (value & 0x8000) != 0 ? (byte)255 : (byte)0;
        return new Rgba32(r, g, b, a);
    }

    private static Rgba32 ConvertRgba4444(ushort value)
    {
        byte r = Expand4To8((value >> 8) & 0x0F);
        byte g = Expand4To8((value >> 4) & 0x0F);
        byte b = Expand4To8(value & 0x0F);
        byte a = Expand4To8((value >> 12) & 0x0F);
        return new Rgba32(r, g, b, a);
    }

    private static byte Expand4To8(int value) => (byte)((value << 4) | value);

    private static byte Expand5To8(int value) => (byte)((value << 3) | (value >> 2));

    private static byte Expand6To8(int value) => (byte)((value << 2) | (value >> 4));

    private sealed class FtexMipMapInfo
    {
        public required int Offset { get; init; }
        public required int DecompressedSize { get; init; }
        public required int Size { get; init; }
        public required byte Index { get; init; }
        public required byte FileNumber { get; init; }
        public required short ChunkCount { get; init; }
    }

    private sealed record FtexChunkInfo(short CompressedSize, short UncompressedSize, long DataOffset);
}
