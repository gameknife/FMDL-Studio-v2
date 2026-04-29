namespace SceneGltfComposer;

internal sealed class CliOptions
{
    public required string InputPath { get; init; }
    public required string OutputPath { get; init; }
    public string? AssetRootPath { get; init; }
    public string? ConverterPath { get; init; }
    public string? CacheDirectoryPath { get; init; }
    public bool RebuildModels { get; init; }
    public bool DisableNameFallback { get; init; }

    public static CliOptions Parse(string[] args)
    {
        if (args.Length == 0)
        {
            throw new CommandLineException("Missing required arguments.");
        }

        List<string> positional = new();
        string? assetRootPath = null;
        string? converterPath = null;
        string? cacheDirectoryPath = null;
        bool rebuildModels = false;
        bool disableNameFallback = false;

        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];

            switch (argument)
            {
                case "--asset-root":
                    assetRootPath = ReadValue(args, ref index, argument);
                    break;
                case "--converter":
                    converterPath = ReadValue(args, ref index, argument);
                    break;
                case "--cache-dir":
                    cacheDirectoryPath = ReadValue(args, ref index, argument);
                    break;
                case "--rebuild-models":
                    rebuildModels = true;
                    break;
                case "--disable-name-fallback":
                    disableNameFallback = true;
                    break;
                default:
                    if (argument.StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new CommandLineException(FormattableString.Invariant($"Unknown option '{argument}'."));
                    }

                    positional.Add(argument);
                    break;
            }
        }

        if (positional.Count is < 1 or > 2)
        {
            throw new CommandLineException("Expected an input compact scene JSON path and optionally an output .glb/.gltf path.");
        }

        string inputPath = Path.GetFullPath(positional[0]);
        if (!File.Exists(inputPath))
        {
            throw new CommandLineException(FormattableString.Invariant($"Input file '{inputPath}' does not exist."));
        }

        if (!inputPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new CommandLineException(FormattableString.Invariant($"Input path '{inputPath}' must point to a .json file."));
        }

        string outputPath = positional.Count == 2
            ? Path.GetFullPath(positional[1])
            : BuildDefaultOutputPath(inputPath);

        string outputExtension = Path.GetExtension(outputPath);
        if (!outputExtension.Equals(".glb", StringComparison.OrdinalIgnoreCase) &&
            !outputExtension.Equals(".gltf", StringComparison.OrdinalIgnoreCase))
        {
            throw new CommandLineException(FormattableString.Invariant($"Output path '{outputPath}' must end with .glb or .gltf."));
        }

        return new CliOptions
        {
            InputPath = inputPath,
            OutputPath = outputPath,
            AssetRootPath = assetRootPath is null ? null : Path.GetFullPath(assetRootPath),
            ConverterPath = converterPath is null ? null : Path.GetFullPath(converterPath),
            CacheDirectoryPath = cacheDirectoryPath is null ? null : Path.GetFullPath(cacheDirectoryPath),
            RebuildModels = rebuildModels,
            DisableNameFallback = disableNameFallback,
        };
    }

    public static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Usage:");
        writer.WriteLine("  SceneGltfComposer <input.scene.compact.json> [output.glb|output.gltf] [--asset-root <path>] [--converter <path>] [--cache-dir <path>] [--rebuild-models] [--disable-name-fallback]");
        writer.WriteLine();
        writer.WriteLine("If no output path is provided, the composer writes <input without .compact>.glb beside the compact scene JSON.");
        writer.WriteLine();
        writer.WriteLine("Examples:");
        writer.WriteLine(@"  SceneGltfComposer .\mafr_lab_asset_room.scene.compact.json");
        writer.WriteLine(@"  SceneGltfComposer .\mafr_lab_asset_room.scene.compact.json .\mafr_lab_asset_room.scene.glb --asset-root D:\BOTW\MGSV\Root");
        writer.WriteLine(@"  SceneGltfComposer .\mafr_lab_asset_room.scene.compact.json --rebuild-models");
    }

    private static string BuildDefaultOutputPath(string inputPath)
    {
        string directory = Path.GetDirectoryName(inputPath) ?? Directory.GetCurrentDirectory();
        string fileName = Path.GetFileName(inputPath);
        string outputFileName = fileName.EndsWith(".scene.compact.json", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^".compact.json".Length] + ".glb"
            : Path.GetFileNameWithoutExtension(fileName) + ".glb";
        return Path.Combine(directory, outputFileName);
    }

    private static string ReadValue(string[] args, ref int index, string optionName)
    {
        if (index + 1 >= args.Length)
        {
            throw new CommandLineException(FormattableString.Invariant($"Option '{optionName}' requires a value."));
        }

        index++;
        return args[index];
    }
}

internal sealed class CommandLineException : Exception
{
    public CommandLineException(string message)
        : base(message)
    {
    }
}
