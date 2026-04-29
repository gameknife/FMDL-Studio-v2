namespace SceneGltfComposer;

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
            SceneComposer composer = new(options);
            composer.Compose(options.OutputPath);
            Console.WriteLine($"Wrote {options.OutputPath}");
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
