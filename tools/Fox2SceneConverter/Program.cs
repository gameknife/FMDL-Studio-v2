namespace Fox2SceneConverter;

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
            SceneDiscoveryService discoveryService = new();
            SceneDescription description = discoveryService.Discover(options);
            CompactSceneDescription compactDescription = discoveryService.BuildCompactScene(description);
            discoveryService.WriteJson(description, options.OutputPath);
            discoveryService.WriteCompactJson(compactDescription, options.CompactOutputPath);
            Console.WriteLine($"Wrote {options.OutputPath}");
            Console.WriteLine($"Wrote {options.CompactOutputPath}");
            Console.WriteLine($"Discovered {description.Summary.FmdlCount} FMDL references across {description.Summary.FilesVisited} files.");
            return 0;
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
