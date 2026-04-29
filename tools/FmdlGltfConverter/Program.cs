namespace FmdlGltfConverter;

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
            int failureCount = 0;

            foreach (ConversionJob job in options.Jobs)
            {
                try
                {
                    FmdlFile fmdl = new FmdlParser().Read(job.InputPath);
                    new GltfExporter().Export(fmdl, job.OutputPath, hashLookup);
                    Console.WriteLine($"Wrote {job.OutputPath}");
                }
                catch (Exception exception)
                {
                    failureCount++;
                    Console.Error.WriteLine($"{job.InputPath}: {exception.Message}");
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
