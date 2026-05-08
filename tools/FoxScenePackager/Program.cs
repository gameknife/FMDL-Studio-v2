namespace FoxScenePackager;

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
            ScenePackageBuilder builder = new(options);
            PackageBuildResult result = builder.Build();
            FoxSceneJson.Write(result.Scene, options.OutputPath);

            Console.WriteLine($"Wrote {options.OutputPath}");
            Console.WriteLine($"Assets: {result.Scene.Summary.AssetCount} total, {result.Scene.Summary.PlacedAssetCount} placed");
            Console.WriteLine($"Nodes: {result.Scene.Summary.NodeCount}, layers: {result.Scene.Summary.LayerCount}");

            if (!options.NoConvert)
            {
                Console.WriteLine($"GLB models: {result.ConvertedCount} converted, {result.ReusedCount} reused, {result.MissingCount} missing");
            }

            foreach (string warning in result.Warnings)
            {
                Console.Error.WriteLine($"warning: {warning}");
            }

            return result.MissingCount == 0 || options.AllowMissingAssets ? 0 : 1;
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
