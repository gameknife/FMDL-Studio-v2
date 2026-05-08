using System.Globalization;
using System.Text;

namespace FmdlGltfConverter;

internal sealed class TextureProfileCsvWriter : IDisposable
{
    private readonly object gate = new();
    private readonly StreamWriter writer;

    public TextureProfileCsvWriter(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory());
        bool writeHeader = !File.Exists(path) || new FileInfo(path).Length == 0;
        writer = new StreamWriter(File.Open(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8);
        if (writeHeader)
        {
            writer.WriteLine(string.Join(
                ",",
                "sourcePath",
                "reference",
                "usage",
                "operation",
                "extension",
                "format",
                "sourceBytes",
                "encodedBytes",
                "width",
                "height",
                "cacheReadMs",
                "cacheWriteMs",
                "sourceReadMs",
                "ftexReadMs",
                "ddsLoadMs",
                "ddsDecompressMs",
                "rgbaConvertMs",
                "imageLoadMs",
                "usageTransformMs",
                "textureEncodeMs",
                "resolveMs",
                "embedMs",
                "totalMs"));
        }
    }

    public void Write(TextureProfileEvent profileEvent)
    {
        lock (gate)
        {
            writer.WriteLine(string.Join(
                ",",
                Csv(profileEvent.SourcePath),
                Csv(profileEvent.Reference),
                Csv(profileEvent.Usage.ToString()),
                Csv(profileEvent.Operation),
                Csv(profileEvent.Extension),
                Csv(profileEvent.Format),
                Number(profileEvent.SourceBytes),
                Number(profileEvent.EncodedBytes),
                Number(profileEvent.Width),
                Number(profileEvent.Height),
                Number(profileEvent.CacheReadMilliseconds),
                Number(profileEvent.CacheWriteMilliseconds),
                Number(profileEvent.SourceReadMilliseconds),
                Number(profileEvent.FtexReadMilliseconds),
                Number(profileEvent.DdsLoadMilliseconds),
                Number(profileEvent.DdsDecompressMilliseconds),
                Number(profileEvent.RgbaConvertMilliseconds),
                Number(profileEvent.ImageLoadMilliseconds),
                Number(profileEvent.UsageTransformMilliseconds),
                Number(profileEvent.TextureEncodeMilliseconds),
                Number(profileEvent.ResolveMilliseconds),
                Number(profileEvent.EmbedMilliseconds),
                Number(profileEvent.TotalMilliseconds)));
            writer.Flush();
        }
    }

    public void Dispose()
    {
        writer.Dispose();
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Csv(string value)
    {
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}

internal sealed record TextureProfileEvent
{
    public required string SourcePath { get; init; }
    public string Reference { get; init; } = string.Empty;
    public required FoxTextureUsage Usage { get; init; }
    public required string Operation { get; init; }
    public required string Extension { get; init; }
    public string Format { get; init; } = string.Empty;
    public long SourceBytes { get; init; }
    public long EncodedBytes { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public long CacheReadMilliseconds { get; init; }
    public long CacheWriteMilliseconds { get; init; }
    public long SourceReadMilliseconds { get; init; }
    public long FtexReadMilliseconds { get; init; }
    public long DdsLoadMilliseconds { get; init; }
    public long DdsDecompressMilliseconds { get; init; }
    public long RgbaConvertMilliseconds { get; init; }
    public long ImageLoadMilliseconds { get; init; }
    public long UsageTransformMilliseconds { get; init; }
    public long TextureEncodeMilliseconds { get; init; }
    public long ResolveMilliseconds { get; init; }
    public long EmbedMilliseconds { get; init; }
    public long TotalMilliseconds { get; init; }
}

internal sealed record TextureTranscodeResult
{
    public required byte[] EncodedBytes { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public long SourceReadMilliseconds { get; init; }
    public long FtexReadMilliseconds { get; init; }
    public long DdsLoadMilliseconds { get; init; }
    public long DdsDecompressMilliseconds { get; init; }
    public long RgbaConvertMilliseconds { get; init; }
    public long ImageLoadMilliseconds { get; init; }
    public long UsageTransformMilliseconds { get; init; }
    public long TextureEncodeMilliseconds { get; init; }
    public long TotalMilliseconds { get; init; }
}
