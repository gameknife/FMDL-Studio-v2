namespace Fox2SceneConverter;

internal sealed class CliOptions
{
    public required string InputPath { get; init; }
    public required string OutputPath { get; init; }
    public required string CompactOutputPath { get; init; }
    public string? AssetRootPath { get; init; }
    public int MaxDepth { get; init; } = 8;
    public bool Recursive { get; init; } = true;

    public static CliOptions Parse(string[] args)
    {
        if (args.Length == 0)
        {
            throw new CommandLineException("Missing required arguments.");
        }

        List<string> positional = new();
        string? assetRootPath = null;
        int maxDepth = 8;
        bool recursive = true;

        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];

            switch (argument)
            {
                case "--asset-root":
                    assetRootPath = ReadValue(args, ref index, argument);
                    break;
                case "--max-depth":
                    string maxDepthText = ReadValue(args, ref index, argument);
                    if (!int.TryParse(maxDepthText, out maxDepth) || maxDepth < 0)
                    {
                        throw new CommandLineException(FormattableString.Invariant($"Option '{argument}' requires a non-negative integer value."));
                    }
                    break;
                case "--no-recursion":
                    recursive = false;
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
            throw new CommandLineException("Expected an input .fox2 path and optionally an output .json path.");
        }

        string inputPath = Path.GetFullPath(positional[0]);
        if (!File.Exists(inputPath))
        {
            throw new CommandLineException(FormattableString.Invariant($"Input file '{inputPath}' does not exist."));
        }

        if (!Path.GetExtension(inputPath).Equals(".fox2", StringComparison.OrdinalIgnoreCase))
        {
            throw new CommandLineException(FormattableString.Invariant($"Input path '{inputPath}' must point to a .fox2 file."));
        }

        string outputPath = positional.Count == 2
            ? Path.GetFullPath(positional[1])
            : BuildDefaultOutputPath(inputPath);

        if (!Path.GetExtension(outputPath).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new CommandLineException(FormattableString.Invariant($"Output path '{outputPath}' must end with .json."));
        }

        return new CliOptions
        {
            InputPath = inputPath,
            OutputPath = outputPath,
            CompactOutputPath = BuildCompactOutputPath(outputPath),
            AssetRootPath = assetRootPath is null ? null : Path.GetFullPath(assetRootPath),
            MaxDepth = maxDepth,
            Recursive = recursive,
        };
    }

    public static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Usage:");
        writer.WriteLine("  Fox2SceneConverter <input.fox2> [output.json] [--asset-root <path>] [--max-depth <n>] [--no-recursion]");
        writer.WriteLine();
        writer.WriteLine("If no output path is provided, the converter writes <input>.scene.json and <input>.scene.compact.json beside the source .fox2 file.");
        writer.WriteLine();
        writer.WriteLine("Examples:");
        writer.WriteLine(@"  Fox2SceneConverter .\mbqf_stage.fox2");
        writer.WriteLine(@"  Fox2SceneConverter .\mbqf_stage.fox2 .\mbqf_stage.scene.json --asset-root D:\BOTW\MGSV\Root");
        writer.WriteLine(@"  Fox2SceneConverter .\mbqf_stage.fox2 --max-depth 12");
    }

    private static string BuildDefaultOutputPath(string inputPath)
    {
        string directory = Path.GetDirectoryName(inputPath) ?? Directory.GetCurrentDirectory();
        string fileName = Path.GetFileNameWithoutExtension(inputPath) + ".scene.json";
        return Path.Combine(directory, fileName);
    }

    private static string BuildCompactOutputPath(string outputPath)
    {
        string directory = Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory();
        string fileName = Path.GetFileName(outputPath);
        string compactFileName = fileName.EndsWith(".scene.json", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^".json".Length] + ".compact.json"
            : Path.GetFileNameWithoutExtension(fileName) + ".compact" + Path.GetExtension(fileName);
        return Path.Combine(directory, compactFileName);
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
