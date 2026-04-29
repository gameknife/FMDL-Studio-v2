namespace FmdlGltfConverter;

internal sealed class FoxHashLookup
{
    private readonly Dictionary<ulong, string> stringLookup = new();
    private readonly Dictionary<ulong, string> pathLookup = new();

    public static FoxHashLookup Create(CliOptions options)
    {
        FoxHashLookup lookup = new();

        if (!options.UseDictionaries)
        {
            return lookup;
        }

        (string? stringDictionaryPath, string? pathDictionaryPath) = ResolveDictionaryPaths(options);
        lookup.LoadStringDictionary(stringDictionaryPath);
        lookup.LoadPathDictionary(pathDictionaryPath);

        return lookup;
    }

    public string ResolveStringHash(ulong hash)
    {
        return stringLookup.TryGetValue(hash, out string? value) ? value : hash.ToString("x");
    }

    public string ResolvePathHash(ulong hash)
    {
        ulong strippedHash = FoxHashing.StripPathCodePrefix(hash);
        return pathLookup.TryGetValue(strippedHash, out string? value) ? value : strippedHash.ToString("x");
    }

    private void LoadStringDictionary(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return;
        }

        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            stringLookup[FoxHashing.HashFileNameLegacy(line)] = line;
        }
    }

    private void LoadPathDictionary(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return;
        }

        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            pathLookup[FoxHashing.HashFileNameWithExtension(line)] = line;
        }
    }

    private static (string? StringDictionaryPath, string? PathDictionaryPath) ResolveDictionaryPaths(CliOptions options)
    {
        string? stringDictionaryPath = options.StringDictionaryPath;
        string? pathDictionaryPath = options.PathDictionaryPath;

        IEnumerable<string> roots = new[]
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory,
            Path.GetDirectoryName(options.InputPath) ?? Directory.GetCurrentDirectory(),
        }.Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (string root in roots)
        {
            stringDictionaryPath ??= FindDictionary(root, "fmdl_dictionary.txt");
            pathDictionaryPath ??= FindDictionary(root, "qar_dictionary.txt");

            if (stringDictionaryPath is not null && pathDictionaryPath is not null)
            {
                break;
            }
        }

        return (stringDictionaryPath, pathDictionaryPath);
    }

    private static string? FindDictionary(string startDirectory, string fileName)
    {
        DirectoryInfo? directory = new DirectoryInfo(startDirectory);

        while (directory is not null)
        {
            string[] candidates =
            {
                Path.Combine(directory.FullName, "Resources", fileName),
                Path.Combine(directory.FullName, "FMDL-Studio-v2", "Assets", "Fmdl Studio", fileName),
                Path.Combine(directory.FullName, "Assets", "Fmdl Studio", fileName),
            };

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            directory = directory.Parent;
        }

        return null;
    }
}
