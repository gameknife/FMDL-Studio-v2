namespace FmdlGltfConverter;

using System.Diagnostics;
using System.Globalization;
using System.Text;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Any(IsHelpArgument))
        {
            CliOptions.PrintUsage(Console.Out);
            return 0;
        }

        try
        {
            CliOptions options = CliOptions.Parse(args);
            FoxHashLookup hashLookup = FoxHashLookup.Create(options);
            using ProfileCsvWriter? profileWriter = options.ProfilePath is null ? null : new ProfileCsvWriter(options.ProfilePath);
            using TextureProfileCsvWriter? textureProfileWriter = options.TextureProfilePath is null ? null : new TextureProfileCsvWriter(options.TextureProfilePath);
            TextureExportOptions textureOptions = new()
            {
                CacheDirectoryPath = options.TextureCacheDirectoryPath,
                Format = options.TextureFormat,
                FastPng = options.FastPng,
                WebpQuality = options.WebpQuality,
                WebpMethod = options.WebpMethod,
                Profiler = textureProfileWriter,
            };
            int failureCount = 0;

            foreach (ConversionJob job in options.Jobs)
            {
                Stopwatch totalStopwatch = Stopwatch.StartNew();
                long parseMilliseconds = 0;
                long exportMilliseconds = 0;
                string? error = null;

                try
                {
                    Stopwatch parseStopwatch = Stopwatch.StartNew();
                    FmdlFile fmdl = new FmdlParser().Read(job.InputPath);
                    parseStopwatch.Stop();
                    parseMilliseconds = parseStopwatch.ElapsedMilliseconds;

                    Stopwatch exportStopwatch = Stopwatch.StartNew();
                    new GltfExporter().Export(fmdl, job.InputPath, job.OutputPath, hashLookup, includeTextures: !options.NoTextures, textureOptions);
                    exportStopwatch.Stop();
                    exportMilliseconds = exportStopwatch.ElapsedMilliseconds;

                    Console.WriteLine($"Wrote {job.OutputPath}");
                }
                catch (Exception exception)
                {
                    failureCount++;
                    error = exception.Message;
                    Console.Error.WriteLine($"{job.InputPath}: {exception.Message}");
                }
                finally
                {
                    totalStopwatch.Stop();
                    profileWriter?.Write(job, parseMilliseconds, exportMilliseconds, totalStopwatch.ElapsedMilliseconds, error);
                }
            }

            return failureCount == 0 ? 0 : 1;
        }
        catch (CommandLineException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine();
            CliOptions.PrintUsage(Console.Error);
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static bool IsHelpArgument(string argument)
    {
        return argument.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
               argument.Equals("-h", StringComparison.OrdinalIgnoreCase) ||
               argument.Equals("/?", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class ProfileCsvWriter : IDisposable
{
    private readonly StreamWriter writer;

    public ProfileCsvWriter(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory());
        bool writeHeader = !File.Exists(path) || new FileInfo(path).Length == 0;
        writer = new StreamWriter(File.Open(path, FileMode.Append, FileAccess.Write, FileShare.Read), Encoding.UTF8);
        if (writeHeader)
        {
            writer.WriteLine("inputPath,outputPath,inputBytes,outputBytes,parseMs,exportMs,totalMs,status,error");
        }
    }

    public void Write(ConversionJob job, long parseMilliseconds, long exportMilliseconds, long totalMilliseconds, string? error)
    {
        long inputBytes = File.Exists(job.InputPath) ? new FileInfo(job.InputPath).Length : 0;
        long outputBytes = File.Exists(job.OutputPath) ? new FileInfo(job.OutputPath).Length : 0;
        writer.WriteLine(string.Join(
            ",",
            Csv(job.InputPath),
            Csv(job.OutputPath),
            inputBytes.ToString(CultureInfo.InvariantCulture),
            outputBytes.ToString(CultureInfo.InvariantCulture),
            parseMilliseconds.ToString(CultureInfo.InvariantCulture),
            exportMilliseconds.ToString(CultureInfo.InvariantCulture),
            totalMilliseconds.ToString(CultureInfo.InvariantCulture),
            Csv(error is null ? "ok" : "error"),
            Csv(error ?? string.Empty)));
        writer.Flush();
    }

    public void Dispose()
    {
        writer.Dispose();
    }

    private static string Csv(string value)
    {
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
