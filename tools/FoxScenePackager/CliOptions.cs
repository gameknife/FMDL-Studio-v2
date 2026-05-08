namespace FoxScenePackager;

internal sealed class CliOptions
{
    public required string InputPath { get; init; }
    public required string OutputPath { get; init; }
    public required string ModelsDirectoryPath { get; init; }
    public string? AssetRootPath { get; init; }
    public string? ConverterPath { get; init; }
    public bool RebuildModels { get; init; }
    public bool NoConvert { get; init; }
    public bool DisableNameFallback { get; init; }
    public bool IncludeUnplacedAssets { get; init; } = true;
    public bool AllowMissingAssets { get; init; }
    public int Jobs { get; init; } = 1;
    public bool NoModelTextures { get; init; }
    public string? TextureCacheDirectoryPath { get; init; }
    public string TextureFormat { get; init; } = "webp-lossy";
    public bool FastPng { get; init; }
    public int WebpQuality { get; init; } = 90;
    public int WebpMethod { get; init; } = 0;

    public static CliOptions Parse(string[] args)
    {
        if (args.Length == 0)
        {
            throw new CommandLineException("Missing required arguments.");
        }

        List<string> positional = new();
        string? assetRootPath = null;
        string? converterPath = null;
        string? modelsDirectoryPath = null;
        bool rebuildModels = false;
        bool noConvert = false;
        bool disableNameFallback = false;
        bool includeUnplacedAssets = true;
        bool allowMissingAssets = false;
        int jobs = 1;
        bool noModelTextures = false;
        string? textureCacheDirectoryPath = null;
        string textureFormat = "webp-lossy";
        bool fastPng = false;
        int webpQuality = 90;
        int webpMethod = 0;

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
                case "--models-dir":
                    modelsDirectoryPath = ReadValue(args, ref index, argument);
                    break;
                case "--rebuild-models":
                    rebuildModels = true;
                    break;
                case "--no-convert":
                    noConvert = true;
                    break;
                case "--disable-name-fallback":
                    disableNameFallback = true;
                    break;
                case "--placed-only":
                    includeUnplacedAssets = false;
                    break;
                case "--allow-missing-assets":
                    allowMissingAssets = true;
                    break;
                case "--jobs":
                    string jobsText = ReadValue(args, ref index, argument);
                    if (!int.TryParse(jobsText, out jobs) || jobs < 1)
                    {
                        throw new CommandLineException(FormattableString.Invariant($"Option '{argument}' requires a positive integer value."));
                    }
                    break;
                case "--no-model-textures":
                    noModelTextures = true;
                    break;
                case "--texture-cache-dir":
                    textureCacheDirectoryPath = ReadValue(args, ref index, argument);
                    break;
                case "--texture-format":
                    textureFormat = ReadValue(args, ref index, argument);
                    if (!IsTextureFormatName(textureFormat))
                    {
                        throw new CommandLineException(FormattableString.Invariant($"Option '{argument}' must be one of: png, webp-lossless, webp-lossy."));
                    }
                    break;
                case "--fast-png":
                    fastPng = true;
                    break;
                case "--webp-quality":
                    string qualityText = ReadValue(args, ref index, argument);
                    if (!int.TryParse(qualityText, out int parsedQuality) || parsedQuality is < 0 or > 100)
                    {
                        throw new CommandLineException(FormattableString.Invariant($"Option '{argument}' requires an integer from 0 to 100."));
                    }
                    webpQuality = parsedQuality;
                    break;
                case "--webp-method":
                    string methodText = ReadValue(args, ref index, argument);
                    if (!int.TryParse(methodText, out int parsedMethod) || parsedMethod is < 0 or > 6)
                    {
                        throw new CommandLineException(FormattableString.Invariant($"Option '{argument}' requires an integer from 0 to 6."));
                    }
                    webpMethod = parsedMethod;
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
            throw new CommandLineException("Expected an input compact scene JSON path and optionally an output .foxscene.json path.");
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

        if (!outputPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new CommandLineException(FormattableString.Invariant($"Output path '{outputPath}' must end with .json."));
        }

        string outputDirectory = Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory();
        string resolvedModelsDirectoryPath = modelsDirectoryPath is null
            ? BuildDefaultModelsDirectoryPath(outputPath)
            : ResolveModelsDirectoryPath(outputDirectory, modelsDirectoryPath);

        return new CliOptions
        {
            InputPath = inputPath,
            OutputPath = outputPath,
            ModelsDirectoryPath = resolvedModelsDirectoryPath,
            AssetRootPath = assetRootPath is null ? null : Path.GetFullPath(assetRootPath),
            ConverterPath = converterPath is null ? null : Path.GetFullPath(converterPath),
            RebuildModels = rebuildModels,
            NoConvert = noConvert,
            DisableNameFallback = disableNameFallback,
            IncludeUnplacedAssets = includeUnplacedAssets,
            AllowMissingAssets = allowMissingAssets,
            Jobs = jobs,
            NoModelTextures = noModelTextures,
            TextureCacheDirectoryPath = textureCacheDirectoryPath is null ? null : Path.GetFullPath(textureCacheDirectoryPath),
            TextureFormat = textureFormat,
            FastPng = fastPng,
            WebpQuality = webpQuality,
            WebpMethod = webpMethod,
        };
    }

    public static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Usage:");
        writer.WriteLine("  FoxScenePackager <input.scene.compact.json> [output.foxscene.json] [--asset-root <path>] [--converter <path>] [--models-dir <path>] [--rebuild-models] [--no-convert] [--disable-name-fallback] [--placed-only] [--allow-missing-assets] [--jobs <n>] [--no-model-textures] [--texture-cache-dir <path>] [--texture-format <png|webp-lossless|webp-lossy>] [--fast-png] [--webp-quality <0-100>] [--webp-method <0-6>]");
        writer.WriteLine();
        writer.WriteLine("The packager writes a portable foxscene JSON plus a directory of independent GLB model assets.");
        writer.WriteLine("Scene packages default to WebP lossy textures at quality 90 / method 0; pass --texture-format png for core glTF texture compatibility.");
        writer.WriteLine("If --no-convert is passed, the JSON still points to the expected GLB URIs but no model files are generated.");
        writer.WriteLine();
        writer.WriteLine("Examples:");
        writer.WriteLine(@"  FoxScenePackager .\mbqf_stage.scene.compact.json");
        writer.WriteLine(@"  FoxScenePackager .\mbqf_stage.scene.compact.json .\mbqf_stage.foxscene.json --asset-root D:\BOTW\MGSV\Root");
        writer.WriteLine(@"  FoxScenePackager .\mbqf_stage.scene.compact.json --no-convert --models-dir .\models");
        writer.WriteLine(@"  FoxScenePackager .\mbqf_stage.scene.compact.json --rebuild-models --jobs 4");
        writer.WriteLine(@"  FoxScenePackager .\mbqf_stage.scene.compact.json --rebuild-models --jobs 4 --no-model-textures");
        writer.WriteLine(@"  FoxScenePackager .\mbqf_stage.scene.compact.json --rebuild-models --jobs 4 --texture-cache-dir .\texture-cache --fast-png");
        writer.WriteLine(@"  FoxScenePackager .\mbqf_stage.scene.compact.json --rebuild-models --jobs 4 --texture-cache-dir .\texture-cache --texture-format png");
    }

    private static bool IsTextureFormatName(string value)
    {
        return value.Equals("png", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("webp", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("webp-lossless", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("webp-lossy", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildDefaultOutputPath(string inputPath)
    {
        string directory = Path.GetDirectoryName(inputPath) ?? Directory.GetCurrentDirectory();
        string fileName = Path.GetFileName(inputPath);
        string outputFileName = fileName.EndsWith(".scene.compact.json", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^".scene.compact.json".Length] + ".foxscene.json"
            : Path.GetFileNameWithoutExtension(fileName) + ".foxscene.json";
        return Path.Combine(directory, outputFileName);
    }

    private static string BuildDefaultModelsDirectoryPath(string outputPath)
    {
        string directory = Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory();
        string fileName = Path.GetFileName(outputPath);
        string stem = fileName.EndsWith(".foxscene.json", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^".foxscene.json".Length]
            : Path.GetFileNameWithoutExtension(fileName);
        return Path.Combine(directory, stem + ".models");
    }

    private static string ResolveModelsDirectoryPath(string outputDirectory, string modelsDirectoryPath)
    {
        return Path.IsPathRooted(modelsDirectoryPath)
            ? Path.GetFullPath(modelsDirectoryPath)
            : Path.GetFullPath(Path.Combine(outputDirectory, modelsDirectoryPath));
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
