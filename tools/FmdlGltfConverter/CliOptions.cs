namespace FmdlGltfConverter;

internal sealed class CliOptions
{
    public required IReadOnlyList<ConversionJob> Jobs { get; init; }
    public string? StringDictionaryPath { get; init; }
    public string? PathDictionaryPath { get; init; }
    public bool UseDictionaries { get; init; } = true;

    public string InputPath => Jobs[0].InputPath;

    public static CliOptions Parse(string[] args)
    {
        if (args.Length == 0)
        {
            throw new CommandLineException("Missing required arguments.");
        }

        List<string> positional = new();
        string? stringDictionaryPath = null;
        string? pathDictionaryPath = null;
        bool useDictionaries = true;

        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];

            switch (argument)
            {
                case "--string-dict":
                    stringDictionaryPath = ReadValue(args, ref index, argument);
                    break;
                case "--path-dict":
                    pathDictionaryPath = ReadValue(args, ref index, argument);
                    break;
                case "--no-dictionaries":
                    useDictionaries = false;
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

        if (positional.Count < 1)
        {
            throw new CommandLineException("Expected one or more input FMDL paths and optionally a single explicit output path for one input.");
        }

        List<string> fullPositional = positional.Select(Path.GetFullPath).ToList();
        List<ConversionJob> jobs = BuildJobs(fullPositional);

        ValidateJobs(jobs);

        return new CliOptions
        {
            Jobs = jobs,
            StringDictionaryPath = stringDictionaryPath is null ? null : Path.GetFullPath(stringDictionaryPath),
            PathDictionaryPath = pathDictionaryPath is null ? null : Path.GetFullPath(pathDictionaryPath),
            UseDictionaries = useDictionaries,
        };
    }

    public static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Usage:");
        writer.WriteLine("  FmdlGltfConverter <input.fmdl> [output.gltf|output.glb] [--string-dict <path>] [--path-dict <path>] [--no-dictionaries]");
        writer.WriteLine("  FmdlGltfConverter <input1.fmdl> <input2.fmdl> [input3.fmdl ...] [--string-dict <path>] [--path-dict <path>] [--no-dictionaries]");
        writer.WriteLine();
        writer.WriteLine("If only input .fmdl paths are provided, the converter writes same-name .glb files beside each input file.");
        writer.WriteLine();
        writer.WriteLine("Examples:");
        writer.WriteLine("  FmdlGltfConverter player.fmdl");
        writer.WriteLine("  FmdlGltfConverter player_0.fmdl player_1.fmdl player_2.fmdl");
        writer.WriteLine("  FmdlGltfConverter player.fmdl player.glb");
        writer.WriteLine("  FmdlGltfConverter player.fmdl player.gltf --string-dict fmdl_dictionary.txt --path-dict qar_dictionary.txt");
    }

    private static List<ConversionJob> BuildJobs(IReadOnlyList<string> positional)
    {
        if (positional.Count == 1)
        {
            string inputPath = positional[0];
            return new List<ConversionJob> { new(inputPath, BuildDefaultOutputPath(inputPath)) };
        }

        string lastExtension = Path.GetExtension(positional[^1]);
        bool lastLooksLikeOutput = lastExtension.Equals(".gltf", StringComparison.OrdinalIgnoreCase) ||
                                   lastExtension.Equals(".glb", StringComparison.OrdinalIgnoreCase);

        if (positional.Count == 2 && lastLooksLikeOutput)
        {
            return new List<ConversionJob> { new(positional[0], positional[1]) };
        }

        if (lastLooksLikeOutput)
        {
            throw new CommandLineException("Explicit output paths are only supported when converting a single input FMDL.");
        }

        return positional.Select(inputPath => new ConversionJob(inputPath, BuildDefaultOutputPath(inputPath))).ToList();
    }

    private static void ValidateJobs(IEnumerable<ConversionJob> jobs)
    {
        foreach (ConversionJob job in jobs)
        {
            if (!File.Exists(job.InputPath))
            {
                throw new CommandLineException(FormattableString.Invariant($"Input file '{job.InputPath}' does not exist."));
            }

            string inputExtension = Path.GetExtension(job.InputPath);
            if (!inputExtension.Equals(".fmdl", StringComparison.OrdinalIgnoreCase))
            {
                throw new CommandLineException(FormattableString.Invariant($"Input path '{job.InputPath}' must point to a .fmdl file."));
            }

            string outputExtension = Path.GetExtension(job.OutputPath);
            if (!outputExtension.Equals(".gltf", StringComparison.OrdinalIgnoreCase) &&
                !outputExtension.Equals(".glb", StringComparison.OrdinalIgnoreCase))
            {
                throw new CommandLineException(FormattableString.Invariant($"Output path '{job.OutputPath}' must end with .gltf or .glb."));
            }
        }
    }

    private static string BuildDefaultOutputPath(string inputPath)
    {
        return Path.Combine(Path.GetDirectoryName(inputPath) ?? Directory.GetCurrentDirectory(), Path.GetFileNameWithoutExtension(inputPath) + ".glb");
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

internal sealed record ConversionJob(string InputPath, string OutputPath);
