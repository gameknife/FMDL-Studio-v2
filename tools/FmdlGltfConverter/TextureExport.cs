using System.IO.Compression;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Pfim;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace FmdlGltfConverter;

internal sealed class TextureExportContext
{
    private readonly GltfRoot gltf;
    private readonly GltfBufferBuilder bufferBuilder;
    private readonly FoxTextureResolver textureResolver;
    private readonly Dictionary<string, int> textureIndicesBySourcePath = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextureProfileCsvWriter? profiler;
    private readonly int samplerIndex;

    public TextureExportContext(GltfRoot gltf, GltfBufferBuilder bufferBuilder, string sourceModelPath, TextureExportOptions options)
    {
        this.gltf = gltf;
        this.bufferBuilder = bufferBuilder;
        profiler = options.Profiler;
        textureResolver = new FoxTextureResolver(sourceModelPath, options);

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
        Stopwatch resolveStopwatch = Stopwatch.StartNew();
        TextureAsset? textureAsset = textureResolver.Resolve(reference, usage);
        resolveStopwatch.Stop();
        if (textureAsset is null)
        {
            profiler?.Write(new TextureProfileEvent
            {
                SourcePath = string.Empty,
                Reference = reference,
                Usage = usage,
                Operation = "missing",
                Extension = string.Empty,
                ResolveMilliseconds = resolveStopwatch.ElapsedMilliseconds,
                TotalMilliseconds = resolveStopwatch.ElapsedMilliseconds,
            });
            return null;
        }

        string cacheKey = $"{textureAsset.SourcePath}|{usage}";
        if (textureIndicesBySourcePath.TryGetValue(cacheKey, out int existingTextureIndex))
        {
            profiler?.Write(new TextureProfileEvent
            {
                SourcePath = textureAsset.SourcePath,
                Reference = reference,
                Usage = usage,
                Operation = "gltf-reuse",
                Extension = Path.GetExtension(textureAsset.SourcePath),
                Format = textureAsset.Format.CacheKey,
                EncodedBytes = textureAsset.EncodedBytes.Length,
                ResolveMilliseconds = resolveStopwatch.ElapsedMilliseconds,
                TotalMilliseconds = resolveStopwatch.ElapsedMilliseconds,
            });
            return existingTextureIndex;
        }

        int bufferViewIndex = gltf.BufferViews.Count;
        Stopwatch embedStopwatch = Stopwatch.StartNew();
        int byteOffset = bufferBuilder.AddBytes(textureAsset.EncodedBytes);
        embedStopwatch.Stop();
        gltf.BufferViews.Add(new GltfBufferView
        {
            Buffer = 0,
            ByteOffset = byteOffset,
            ByteLength = textureAsset.EncodedBytes.Length,
        });

        int imageIndex = gltf.Images.Count;
        gltf.Images.Add(new GltfImage
        {
            Name = Path.GetFileNameWithoutExtension(textureAsset.SourcePath),
            BufferView = bufferViewIndex,
            MimeType = textureAsset.MimeType,
        });

        int textureIndex = gltf.Textures.Count;
        GltfTexture texture = new()
        {
            Name = Path.GetFileNameWithoutExtension(textureAsset.SourcePath),
            Sampler = samplerIndex,
        };
        if (textureAsset.Format.RequiresWebpExtension)
        {
            EnsureRequiredExtension("EXT_texture_webp");
            texture.Extensions = new Dictionary<string, object?>
            {
                ["EXT_texture_webp"] = new GltfTextureSourceExtension { Source = imageIndex },
            };
        }
        else
        {
            texture.Source = imageIndex;
        }

        gltf.Textures.Add(texture);

        textureIndicesBySourcePath.Add(cacheKey, textureIndex);
        profiler?.Write(new TextureProfileEvent
        {
            SourcePath = textureAsset.SourcePath,
            Reference = reference,
            Usage = usage,
            Operation = "gltf-embed",
            Extension = Path.GetExtension(textureAsset.SourcePath),
            Format = textureAsset.Format.CacheKey,
            EncodedBytes = textureAsset.EncodedBytes.Length,
            ResolveMilliseconds = resolveStopwatch.ElapsedMilliseconds,
            EmbedMilliseconds = embedStopwatch.ElapsedMilliseconds,
            TotalMilliseconds = resolveStopwatch.ElapsedMilliseconds + embedStopwatch.ElapsedMilliseconds,
        });
        return textureIndex;
    }

    private void EnsureRequiredExtension(string extensionName)
    {
        gltf.ExtensionsUsed ??= [];
        if (!gltf.ExtensionsUsed.Contains(extensionName, StringComparer.Ordinal))
        {
            gltf.ExtensionsUsed.Add(extensionName);
        }

        gltf.ExtensionsRequired ??= [];
        if (!gltf.ExtensionsRequired.Contains(extensionName, StringComparer.Ordinal))
        {
            gltf.ExtensionsRequired.Add(extensionName);
        }
    }
}

internal sealed class FoxTextureResolver
{
    private static readonly string[] SupportedExtensions = [".ftex", ".dds", ".png", ".tga", ".jpg", ".jpeg", ".bmp"];
    private static readonly object HashedTextureIndexLock = new();
    private static readonly Dictionary<string, IReadOnlyDictionary<ulong, string>> HashedTextureIndicesByAssetsRoot = new(StringComparer.OrdinalIgnoreCase);

    private readonly string modelDirectory;
    private readonly string? assetsRoot;
    private readonly TextureExportOptions options;
    private readonly Dictionary<string, TextureAsset?> cache = new(StringComparer.OrdinalIgnoreCase);

    public FoxTextureResolver(string sourceModelPath, TextureExportOptions options)
    {
        this.options = options;
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
        Stopwatch totalStopwatch = Stopwatch.StartNew();
        string? cachePath = TryBuildCachePath(texturePath, usage);
        if (cachePath is not null && File.Exists(cachePath))
        {
            try
            {
                Stopwatch cacheReadStopwatch = Stopwatch.StartNew();
                byte[] cachedTextureBytes = File.ReadAllBytes(cachePath);
                cacheReadStopwatch.Stop();
                totalStopwatch.Stop();
                options.Profiler?.Write(new TextureProfileEvent
                {
                    SourcePath = texturePath,
                    Usage = usage,
                    Operation = "cache-read",
                    Extension = Path.GetExtension(texturePath),
                    Format = options.Format.CacheKey,
                    SourceBytes = GetFileLength(texturePath),
                    EncodedBytes = cachedTextureBytes.Length,
                    CacheReadMilliseconds = cacheReadStopwatch.ElapsedMilliseconds,
                    TotalMilliseconds = totalStopwatch.ElapsedMilliseconds,
                });
                return new TextureAsset(texturePath, cachedTextureBytes, options.Format.MimeType, options.Format);
            }
            catch (IOException)
            {
                // Another converter may be replacing a stale cache file. Fall back
                // to conversion and rewrite below.
            }
        }

        string extension = Path.GetExtension(texturePath);
        TextureTranscodeResult transcodeResult = extension.ToLowerInvariant() switch
        {
            ".ftex" => TextureTranscoder.ConvertFtexToPng(texturePath, usage, options),
            ".dds" => TextureTranscoder.ConvertDdsFileToPng(texturePath, usage, options),
            _ => TextureTranscoder.ConvertImageFileToPng(texturePath, usage, options),
        };

        long cacheWriteMilliseconds = 0;
        if (cachePath is not null)
        {
            Stopwatch cacheWriteStopwatch = Stopwatch.StartNew();
            TryWriteCache(cachePath, transcodeResult.EncodedBytes);
            cacheWriteStopwatch.Stop();
            cacheWriteMilliseconds = cacheWriteStopwatch.ElapsedMilliseconds;
        }

        totalStopwatch.Stop();
        options.Profiler?.Write(new TextureProfileEvent
        {
            SourcePath = texturePath,
            Usage = usage,
            Operation = "transcode",
            Extension = extension,
            Format = options.Format.CacheKey,
            SourceBytes = GetFileLength(texturePath),
            EncodedBytes = transcodeResult.EncodedBytes.Length,
            Width = transcodeResult.Width,
            Height = transcodeResult.Height,
            CacheWriteMilliseconds = cacheWriteMilliseconds,
            SourceReadMilliseconds = transcodeResult.SourceReadMilliseconds,
            FtexReadMilliseconds = transcodeResult.FtexReadMilliseconds,
            DdsLoadMilliseconds = transcodeResult.DdsLoadMilliseconds,
            DdsDecompressMilliseconds = transcodeResult.DdsDecompressMilliseconds,
            RgbaConvertMilliseconds = transcodeResult.RgbaConvertMilliseconds,
            ImageLoadMilliseconds = transcodeResult.ImageLoadMilliseconds,
            UsageTransformMilliseconds = transcodeResult.UsageTransformMilliseconds,
            TextureEncodeMilliseconds = transcodeResult.TextureEncodeMilliseconds,
            TotalMilliseconds = totalStopwatch.ElapsedMilliseconds,
        });

        return new TextureAsset(texturePath, transcodeResult.EncodedBytes, options.Format.MimeType, options.Format);
    }

    private static long GetFileLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private string? TryBuildCachePath(string texturePath, FoxTextureUsage usage)
    {
        if (string.IsNullOrWhiteSpace(options.CacheDirectoryPath))
        {
            return null;
        }

        FileInfo fileInfo = new(texturePath);
        if (!fileInfo.Exists)
        {
            return null;
        }

        string signature = string.Join(
            "|",
            Path.GetFullPath(texturePath).ToLowerInvariant(),
            fileInfo.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            fileInfo.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            usage.ToString(),
            options.EncoderCacheKey);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature))).ToLowerInvariant();
        return Path.Combine(options.CacheDirectoryPath, hash[..2], hash + options.Format.FileExtension);
    }

    private static void TryWriteCache(string cachePath, byte[] textureBytes)
    {
        string directory = Path.GetDirectoryName(cachePath) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(directory);
        string tempPath = Path.Combine(directory, Path.GetFileName(cachePath) + "." + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            File.WriteAllBytes(tempPath, textureBytes);
            File.Move(tempPath, cachePath, overwrite: false);
        }
        catch (IOException)
        {
            // A parallel converter may have won the race. The cache is only an
            // acceleration path, so keep the current conversion result.
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private string? FindTexturePath(string reference)
    {
        bool hashReference = TryParseHashReference(reference, out _, out _);
        if (hashReference)
        {
            string? hashedMatch = FindHashedTexturePath(reference);
            if (hashedMatch is not null)
            {
                return hashedMatch;
            }
        }

        foreach (string candidate in ExpandCandidates(reference))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        if (!hashReference)
        {
            string? hashedMatch = FindHashedTexturePath(reference);
            if (hashedMatch is not null)
            {
                return hashedMatch;
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

    private string? FindHashedTexturePath(string reference)
    {
        if (assetsRoot is null)
        {
            return null;
        }

        if (!TryParseHashReference(reference, out ulong rawHash, out ulong? strippedHash))
        {
            return null;
        }

        IReadOnlyDictionary<ulong, string> hashedTextureIndex = GetHashedTextureIndex(assetsRoot, options.CacheDirectoryPath);
        if (hashedTextureIndex.TryGetValue(rawHash, out string? texturePath))
        {
            return texturePath;
        }

        return strippedHash is ulong stripped && hashedTextureIndex.TryGetValue(stripped, out texturePath)
            ? texturePath
            : null;
    }

    private static bool TryParseHashReference(string reference, out ulong rawHash, out ulong? strippedHash)
    {
        string fileStem = Path.GetFileNameWithoutExtension(reference);
        if (!ulong.TryParse(fileStem, System.Globalization.NumberStyles.HexNumber, null, out rawHash))
        {
            strippedHash = null;
            return false;
        }

        strippedHash = FoxHashing.HasPathCodePrefix(rawHash)
            ? FoxHashing.StripPathCodePrefix(rawHash)
            : null;

        return true;
    }

    private static IReadOnlyDictionary<ulong, string> GetHashedTextureIndex(string assetsRoot, string? cacheDirectoryPath)
    {
        lock (HashedTextureIndexLock)
        {
            if (!HashedTextureIndicesByAssetsRoot.TryGetValue(assetsRoot, out IReadOnlyDictionary<ulong, string>? index))
            {
                string? cachePath = TryBuildHashedTextureIndexCachePath(assetsRoot, cacheDirectoryPath);
                index = cachePath is not null ? TryReadHashedTextureIndexCache(assetsRoot, cachePath) : null;
                if (index is null)
                {
                    index = BuildHashedTextureIndex(assetsRoot);
                    if (cachePath is not null)
                    {
                        TryWriteHashedTextureIndexCache(assetsRoot, cachePath, index);
                    }
                }

                HashedTextureIndicesByAssetsRoot.Add(assetsRoot, index);
            }

            return index;
        }
    }

    private static string? TryBuildHashedTextureIndexCachePath(string assetsRoot, string? cacheDirectoryPath)
    {
        if (string.IsNullOrWhiteSpace(cacheDirectoryPath))
        {
            return null;
        }

        string signature = Path.GetFullPath(assetsRoot).ToLowerInvariant();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature))).ToLowerInvariant();
        return Path.Combine(cacheDirectoryPath, "_texture-index", hash + ".tsv");
    }

    private static IReadOnlyDictionary<ulong, string>? TryReadHashedTextureIndexCache(string assetsRoot, string cachePath)
    {
        if (!File.Exists(cachePath))
        {
            return null;
        }

        try
        {
            Dictionary<ulong, string> index = new();
            foreach (string line in File.ReadLines(cachePath))
            {
                string[] parts = line.Split('\t', 2);
                if (parts.Length != 2 ||
                    !ulong.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out ulong hash))
                {
                    return null;
                }

                string filePath = Path.GetFullPath(Path.Combine(assetsRoot, parts[1]));
                index.TryAdd(hash, filePath);
            }

            return index;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryWriteHashedTextureIndexCache(string assetsRoot, string cachePath, IReadOnlyDictionary<ulong, string> index)
    {
        string directory = Path.GetDirectoryName(cachePath) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(directory);
        string tempPath = Path.Combine(directory, Path.GetFileName(cachePath) + "." + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            using (StreamWriter writer = new(tempPath, append: false, Encoding.UTF8))
            {
                foreach ((ulong hash, string filePath) in index.OrderBy(item => item.Key))
                {
                    string relativePath = Path.GetRelativePath(assetsRoot, filePath);
                    writer.Write(hash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture));
                    writer.Write('\t');
                    writer.WriteLine(relativePath);
                }
            }

            File.Move(tempPath, cachePath, overwrite: false);
        }
        catch (IOException)
        {
            // Another converter may have written the same index first.
        }
        catch (UnauthorizedAccessException)
        {
            // Cache persistence is optional.
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static IReadOnlyDictionary<ulong, string> BuildHashedTextureIndex(string assetsRoot)
    {
        string assetsDirectory = Path.Combine(assetsRoot, "Assets");
        Dictionary<ulong, string> index = new();
        if (!Directory.Exists(assetsDirectory))
        {
            return index;
        }

        foreach (string sourceImagesDirectory in Directory.EnumerateDirectories(assetsDirectory, "sourceimages", SearchOption.AllDirectories))
        {
            foreach (string filePath in Directory.EnumerateFiles(sourceImagesDirectory, "*", SearchOption.AllDirectories))
            {
                if (!SupportedExtensions.Contains(Path.GetExtension(filePath), StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                AddHashedTextureCandidateForFile(index, assetsRoot, filePath);
            }
        }

        return index;
    }

    private static void AddHashedTextureCandidateForFile(Dictionary<ulong, string> index, string assetsRoot, string filePath)
    {
        string relativePath = Path.GetRelativePath(assetsRoot, filePath);
        string assetPath = "/" + relativePath.Replace('\\', '/');

        AddHashedTextureCandidate(index, assetPath, filePath);

        if (Path.GetExtension(assetPath).Equals(".ftex", StringComparison.OrdinalIgnoreCase))
        {
            AddHashedTextureCandidate(index, Path.ChangeExtension(assetPath, ".dds")!, filePath);
        }
    }

    private static void AddHashedTextureCandidate(Dictionary<ulong, string> index, string assetPath, string filePath)
    {
        ulong hash = FoxHashing.HashFileNameWithExtension(assetPath);
        index.TryAdd(hash, filePath);
    }
}

internal sealed record TextureAsset(string SourcePath, byte[] EncodedBytes, string MimeType, TextureContainerFormat Format);

internal sealed class TextureExportOptions
{
    public static TextureExportOptions Default { get; } = new();

    public string? CacheDirectoryPath { get; init; }
    public TextureContainerFormat Format { get; init; } = TextureContainerFormat.Png;
    public bool FastPng { get; init; }
    public int WebpQuality { get; init; } = 90;
    public int WebpMethod { get; init; } = 4;
    public TextureProfileCsvWriter? Profiler { get; init; }
    public string EncoderCacheKey => Format.Name switch
    {
        "png" => FastPng ? "png-best-speed-none-v1" : "png-default-v1",
        "webp-lossless" => FormattableString.Invariant($"webp-lossless-q{WebpQuality}-m{WebpMethod}-v1"),
        "webp-lossy" => FormattableString.Invariant($"webp-lossy-q{WebpQuality}-m{WebpMethod}-v1"),
        _ => Format.Name,
    };

    public IImageEncoder CreateTextureEncoder()
    {
        if (Format.Name == "png")
        {
            return FastPng
                ? new PngEncoder
                {
                    CompressionLevel = PngCompressionLevel.BestSpeed,
                    FilterMethod = PngFilterMethod.None,
                }
                : new PngEncoder();
        }

        return new WebpEncoder
        {
            FileFormat = Format.Name == "webp-lossless" ? WebpFileFormatType.Lossless : WebpFileFormatType.Lossy,
            Quality = Math.Clamp(WebpQuality, 0, 100),
            Method = (WebpEncodingMethod)Math.Clamp(WebpMethod, 0, 6),
        };
    }
}

internal sealed record TextureContainerFormat(
    string Name,
    string MimeType,
    string FileExtension,
    bool RequiresWebpExtension)
{
    public static TextureContainerFormat Png { get; } = new("png", "image/png", ".png", RequiresWebpExtension: false);
    public static TextureContainerFormat WebpLossless { get; } = new("webp-lossless", "image/webp", ".webp", RequiresWebpExtension: true);
    public static TextureContainerFormat WebpLossy { get; } = new("webp-lossy", "image/webp", ".webp", RequiresWebpExtension: true);

    public string CacheKey => Name;

    public static bool TryParse(string value, out TextureContainerFormat format)
    {
        switch (value.ToLowerInvariant())
        {
            case "png":
                format = Png;
                return true;
            case "webp":
            case "webp-lossy":
                format = WebpLossy;
                return true;
            case "webp-lossless":
                format = WebpLossless;
                return true;
            default:
                format = Png;
                return false;
        }
    }
}

internal enum FoxTextureUsage
{
    Default,
    Normal,
    Roughness,
}

internal static class TextureTranscoder
{
    private static readonly byte[] FtexMagic = [0x46, 0x54, 0x45, 0x58, 0x85, 0xEB, 0x01, 0x40];

    public static TextureTranscodeResult ConvertFtexToPng(string ftexPath, FoxTextureUsage usage, TextureExportOptions options)
    {
        Stopwatch totalStopwatch = Stopwatch.StartNew();
        Stopwatch ftexStopwatch = Stopwatch.StartNew();
        byte[] ddsBytes = ConvertFtexToDds(ftexPath);
        ftexStopwatch.Stop();
        TextureTranscodeResult ddsResult = ConvertDdsBytesToPng(ddsBytes, usage, options);
        totalStopwatch.Stop();

        return ddsResult with
        {
            FtexReadMilliseconds = ftexStopwatch.ElapsedMilliseconds,
            TotalMilliseconds = totalStopwatch.ElapsedMilliseconds,
        };
    }

    public static TextureTranscodeResult ConvertImageFileToPng(string imagePath, FoxTextureUsage usage, TextureExportOptions options)
    {
        Stopwatch totalStopwatch = Stopwatch.StartNew();
        Stopwatch imageLoadStopwatch = Stopwatch.StartNew();
        using Image<Rgba32> image = SixLabors.ImageSharp.Image.Load<Rgba32>(imagePath);
        imageLoadStopwatch.Stop();

        Stopwatch usageTransformStopwatch = Stopwatch.StartNew();
        ApplyUsageTransform(image, usage);
        usageTransformStopwatch.Stop();

        using MemoryStream textureStream = new();
        Stopwatch textureEncodeStopwatch = Stopwatch.StartNew();
        image.Save(textureStream, options.CreateTextureEncoder());
        textureEncodeStopwatch.Stop();
        totalStopwatch.Stop();

        return new TextureTranscodeResult
        {
            EncodedBytes = textureStream.ToArray(),
            Width = image.Width,
            Height = image.Height,
            ImageLoadMilliseconds = imageLoadStopwatch.ElapsedMilliseconds,
            UsageTransformMilliseconds = usageTransformStopwatch.ElapsedMilliseconds,
            TextureEncodeMilliseconds = textureEncodeStopwatch.ElapsedMilliseconds,
            TotalMilliseconds = totalStopwatch.ElapsedMilliseconds,
        };
    }

    public static TextureTranscodeResult ConvertDdsFileToPng(string ddsPath, FoxTextureUsage usage, TextureExportOptions options)
    {
        Stopwatch totalStopwatch = Stopwatch.StartNew();
        Stopwatch sourceReadStopwatch = Stopwatch.StartNew();
        byte[] ddsBytes = File.ReadAllBytes(ddsPath);
        sourceReadStopwatch.Stop();
        TextureTranscodeResult result = ConvertDdsBytesToPng(ddsBytes, usage, options);
        totalStopwatch.Stop();

        return result with
        {
            SourceReadMilliseconds = sourceReadStopwatch.ElapsedMilliseconds,
            TotalMilliseconds = totalStopwatch.ElapsedMilliseconds,
        };
    }

    private static TextureTranscodeResult ConvertDdsBytesToPng(byte[] ddsBytes, FoxTextureUsage usage, TextureExportOptions options)
    {
        Stopwatch totalStopwatch = Stopwatch.StartNew();
        using MemoryStream ddsStream = new(ddsBytes, writable: false);
        Stopwatch ddsLoadStopwatch = Stopwatch.StartNew();
        using IImage image = Pfimage.FromStream(ddsStream);
        ddsLoadStopwatch.Stop();

        long ddsDecompressMilliseconds = 0;
        if (image.Compressed)
        {
            Stopwatch ddsDecompressStopwatch = Stopwatch.StartNew();
            image.Decompress();
            ddsDecompressStopwatch.Stop();
            ddsDecompressMilliseconds = ddsDecompressStopwatch.ElapsedMilliseconds;
        }

        Stopwatch rgbaConvertStopwatch = Stopwatch.StartNew();
        byte[] rgbaBytes = ConvertToRgba32(image);
        using Image<Rgba32> rgbaImage = Image.LoadPixelData<Rgba32>(rgbaBytes, image.Width, image.Height);
        rgbaConvertStopwatch.Stop();

        Stopwatch usageTransformStopwatch = Stopwatch.StartNew();
        ApplyUsageTransform(rgbaImage, usage);
        usageTransformStopwatch.Stop();

        using MemoryStream textureStream = new();
        Stopwatch textureEncodeStopwatch = Stopwatch.StartNew();
        rgbaImage.Save(textureStream, options.CreateTextureEncoder());
        textureEncodeStopwatch.Stop();
        totalStopwatch.Stop();

        return new TextureTranscodeResult
        {
            EncodedBytes = textureStream.ToArray(),
            Width = image.Width,
            Height = image.Height,
            DdsLoadMilliseconds = ddsLoadStopwatch.ElapsedMilliseconds,
            DdsDecompressMilliseconds = ddsDecompressMilliseconds,
            RgbaConvertMilliseconds = rgbaConvertStopwatch.ElapsedMilliseconds,
            UsageTransformMilliseconds = usageTransformStopwatch.ElapsedMilliseconds,
            TextureEncodeMilliseconds = textureEncodeStopwatch.ElapsedMilliseconds,
            TotalMilliseconds = totalStopwatch.ElapsedMilliseconds,
        };
    }

    private static void ApplyUsageTransform(Image<Rgba32> image, FoxTextureUsage usage)
    {
        switch (usage)
        {
            case FoxTextureUsage.Normal:
                ApplyNormalTransform(image);
                break;
            case FoxTextureUsage.Roughness:
                ApplyRoughnessTransform(image);
                break;
        }
    }

    private static void ApplyNormalTransform(Image<Rgba32> image)
    {
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                Rgba32 pixel = image[x, y];

                float nx = pixel.A / 255.0f;
                float ny = pixel.G / 255.0f;

                image[x, y] = new Rgba32(
                    ToByte(nx),
                    ToByte(ny),
                    255,
                    255);
            }
        }
    }

    private static void ApplyRoughnessTransform(Image<Rgba32> image)
    {
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                byte roughness = image[x, y].G;
                image[x, y] = new Rgba32(255, roughness, 0, 255);
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
                    int sourceRowOffset = row * image.Stride;
                    int destinationRowOffset = row * image.Width * 4;

                    for (int column = 0; column < image.Width; column++)
                    {
                        int sourceOffset = sourceRowOffset + column * 4;
                        int destinationOffset = destinationRowOffset + column * 4;

                        // Pfim exposes decoded DDS pixels in BGRA byte order for 32-bit color.
                        rgbaBytes[destinationOffset] = sourceData[sourceOffset + 2];
                        rgbaBytes[destinationOffset + 1] = sourceData[sourceOffset + 1];
                        rgbaBytes[destinationOffset + 2] = sourceData[sourceOffset];
                        rgbaBytes[destinationOffset + 3] = sourceData[sourceOffset + 3];
                    }
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

                        rgbaBytes[destinationOffset] = sourceData[sourceOffset + 2];
                        rgbaBytes[destinationOffset + 1] = sourceData[sourceOffset + 1];
                        rgbaBytes[destinationOffset + 2] = sourceData[sourceOffset];
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
