using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FoxScenePackager;

internal sealed class ScenePackageBuilder
{
    private readonly CliOptions options;
    private readonly CompactScene compactScene;
    private readonly string? assetRootPath;
    private readonly ConverterCommand? converterCommand;
    private readonly Dictionary<string, List<string>> fileModelsByPath;
    private readonly Dictionary<string, FoxSceneAsset> assetsBySourcePath = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> assetIds = new(StringComparer.Ordinal);
    private readonly List<FoxSceneNode> nodes = [];
    private readonly List<FoxSceneLayer> layers = [];
    private readonly List<string> rootNodeIds = [];
    private readonly List<string> warnings = [];
    private int nextNodeIndex;
    private int convertedCount;
    private int reusedCount;
    private int missingCount;

    public ScenePackageBuilder(CliOptions options)
    {
        this.options = options;
        compactScene = CompactScene.Load(options.InputPath);
        assetRootPath = options.AssetRootPath ?? compactScene.AssetRootPath;
        converterCommand = options.NoConvert ? null : ResolveConverterCommand(options);
        fileModelsByPath = compactScene.Files.ToDictionary(
            file => file.Path,
            file => file.FmdlFiles ?? [],
            StringComparer.OrdinalIgnoreCase);
    }

    public PackageBuildResult Build()
    {
        foreach (CompactSceneFile file in compactScene.Files)
        {
            string layerId = BuildStableId("layer", file.Path);
            FoxSceneLayer layer = new()
            {
                Id = layerId,
                Name = Path.GetFileNameWithoutExtension(file.Path.Replace('/', Path.DirectorySeparatorChar)),
                SourceFox2 = file.Path,
                DiscoveredFrom = file.DiscoveredFrom,
            };

            foreach (CompactSceneNode rootNode in file.RootNodes ?? [])
            {
                string nodeId = BuildNode(rootNode, file, layerId);
                layer.RootNodes.Add(nodeId);
                rootNodeIds.Add(nodeId);
            }

            layers.Add(layer);
        }

        if (options.IncludeUnplacedAssets)
        {
            foreach (string modelPath in EnumerateAllKnownModelPaths())
            {
                GetOrCreateAsset(modelPath);
            }
        }

        MaterializeAssetUris();

        List<FoxSceneAsset> assets = assetsBySourcePath.Values
            .OrderBy(asset => asset.SourceFmdl, StringComparer.OrdinalIgnoreCase)
            .ToList();
        int placedAssetCount = assets.Count(asset => asset.PlacementCount > 0);
        int placementCount = assets.Sum(asset => asset.PlacementCount);

        FoxSceneDocument scene = new()
        {
            Source = new FoxSceneSource
            {
                CompactScene = options.InputPath,
                Fox2File = compactScene.SourceFile,
                Fox2AssetPath = compactScene.SourceAssetPath,
                AssetRootPath = assetRootPath,
            },
            Summary = new FoxSceneSummary
            {
                LayerCount = layers.Count,
                NodeCount = nodes.Count,
                AssetCount = assets.Count,
                PlacedAssetCount = placedAssetCount,
                PlacementCount = placementCount,
                WarningCount = warnings.Count,
            },
            Assets = assets,
            Layers = layers,
            RootNodes = rootNodeIds,
            Nodes = nodes,
            Warnings = warnings.Count == 0 ? null : warnings,
        };

        return new PackageBuildResult(scene, convertedCount, reusedCount, missingCount, warnings);
    }

    private string BuildNode(CompactSceneNode compactNode, CompactSceneFile file, string layerId)
    {
        string nodeId = FormattableString.Invariant($"node-{nextNodeIndex++:000000}");
        List<FoxSceneNodeAssetBinding> bindings = [];

        foreach (ResolvedModelBinding resolvedBinding in ResolveNodeModels(compactNode, file))
        {
            FoxSceneAsset asset = GetOrCreateAsset(resolvedBinding.ModelPath);
            asset.PlacementCount++;
            bindings.Add(new FoxSceneNodeAssetBinding
            {
                AssetId = asset.Id,
                Source = resolvedBinding.Source,
            });
        }

        List<string> childIds = [];
        FoxSceneNode sceneNode = new()
        {
            Id = nodeId,
            LayerId = layerId,
            Name = compactNode.Name,
            ClassName = compactNode.ClassName,
            Transform = BuildTransform(compactNode.Transform),
            Assets = bindings.Count == 0 ? null : bindings,
            Properties = compactNode.Properties is { Count: > 0 } ? compactNode.Properties : null,
            Children = childIds,
        };

        nodes.Add(sceneNode);

        foreach (CompactSceneNode child in compactNode.Children ?? [])
        {
            childIds.Add(BuildNode(child, file, layerId));
        }

        if (childIds.Count == 0)
        {
            sceneNode.Children = null;
        }

        return nodeId;
    }

    private IEnumerable<ResolvedModelBinding> ResolveNodeModels(CompactSceneNode node, CompactSceneFile file)
    {
        if (node.FmdlPaths is { Count: > 0 })
        {
            return node.FmdlPaths
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => new ResolvedModelBinding(path, "direct"));
        }

        if (options.DisableNameFallback ||
            string.IsNullOrWhiteSpace(node.Name) ||
            ShouldSuppressNameFallback(node.Name!))
        {
            return [];
        }

        IReadOnlyList<string> fileModels = fileModelsByPath.GetValueOrDefault(file.Path, []);
        string? match = GuessModelFromName(node.Name!, fileModels);
        return match is null ? [] : [new ResolvedModelBinding(match, "nameFallback")];
    }

    private string? GuessModelFromName(string nodeName, IReadOnlyList<string> fileModels)
    {
        string primary = nodeName.Split('|', 2)[0];
        List<string> labels = BuildNameCandidates(primary);
        if (labels.Count == 0)
        {
            return null;
        }

        int bestScore = 0;
        string? bestMatch = null;
        bool ambiguous = false;

        foreach (string modelPath in fileModels)
        {
            string stem = NormalizeName(Path.GetFileNameWithoutExtension(modelPath));
            if (string.IsNullOrEmpty(stem))
            {
                continue;
            }

            int score = ScoreModelNameMatch(labels, stem);
            if (score > bestScore)
            {
                bestScore = score;
                bestMatch = modelPath;
                ambiguous = false;
            }
            else if (score == bestScore && score > 0)
            {
                ambiguous = true;
            }
        }

        return bestMatch is not null && !ambiguous ? bestMatch : null;
    }

    private static List<string> BuildNameCandidates(string nodeName)
    {
        string normalized = NormalizeName(nodeName);
        if (string.IsNullOrEmpty(normalized))
        {
            return [];
        }

        string[] parts = normalized.Split('_', StringSplitOptions.RemoveEmptyEntries);
        List<string> candidates = new() { normalized };
        for (int length = parts.Length; length >= 2; length--)
        {
            candidates.Add(string.Join('_', parts.Take(length)));
        }

        return candidates.Distinct(StringComparer.Ordinal).ToList();
    }

    private static int ScoreModelNameMatch(IEnumerable<string> labels, string modelStem)
    {
        int best = 0;
        foreach (string label in labels)
        {
            if (label.Equals(modelStem, StringComparison.Ordinal))
            {
                best = Math.Max(best, 1000 + modelStem.Length);
            }
            else if (label.StartsWith(modelStem, StringComparison.Ordinal) && modelStem.Length >= 8)
            {
                best = Math.Max(best, 500 + modelStem.Length);
            }
            else if (modelStem.StartsWith(label, StringComparison.Ordinal) && label.Length >= 8)
            {
                best = Math.Max(best, 300 + label.Length);
            }
        }

        return best;
    }

    private static string NormalizeName(string value)
    {
        return Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
    }

    private static bool ShouldSuppressNameFallback(string nodeName)
    {
        int separatorIndex = nodeName.IndexOf('|');
        if (separatorIndex < 0 || separatorIndex == nodeName.Length - 1)
        {
            return false;
        }

        string suffix = nodeName[(separatorIndex + 1)..];
        return suffix.StartsWith("TppSharedGimmick", StringComparison.Ordinal) ||
               suffix.StartsWith("SL_", StringComparison.Ordinal) ||
               suffix.StartsWith("PL_", StringComparison.Ordinal) ||
               suffix.StartsWith("IP_SL_", StringComparison.Ordinal) ||
               suffix.StartsWith("IP_PL_", StringComparison.Ordinal) ||
               suffix.StartsWith("LA_SL_", StringComparison.Ordinal) ||
               suffix.StartsWith("LA_PL_", StringComparison.Ordinal);
    }

    private IEnumerable<string> EnumerateAllKnownModelPaths()
    {
        return (compactScene.FmdlFiles ?? [])
            .Concat(compactScene.Files.SelectMany(file => file.FmdlFiles ?? []))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private FoxSceneAsset GetOrCreateAsset(string sourceFmdl)
    {
        if (assetsBySourcePath.TryGetValue(sourceFmdl, out FoxSceneAsset? existing))
        {
            return existing;
        }

        string id = BuildUniqueAssetId(sourceFmdl);
        FoxSceneAsset asset = new()
        {
            Id = id,
            Name = Path.GetFileNameWithoutExtension(sourceFmdl.Replace('/', Path.DirectorySeparatorChar)),
            SourceFmdl = sourceFmdl,
        };
        assetsBySourcePath.Add(sourceFmdl, asset);
        return asset;
    }

    private string BuildUniqueAssetId(string sourceFmdl)
    {
        string id = BuildStableId("fmdl", sourceFmdl);
        if (assetIds.Add(id))
        {
            return id;
        }

        for (int suffix = 2; ; suffix++)
        {
            string candidate = FormattableString.Invariant($"{id}-{suffix}");
            if (assetIds.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private static string BuildStableId(string prefix, string value)
    {
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
        string text = Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
        return $"{prefix}-{text}";
    }

    private static FoxSceneTransform? BuildTransform(CompactNodeTransform? transform)
    {
        if (transform is null)
        {
            return null;
        }

        FoxSceneTransform sceneTransform = new()
        {
            Translation = transform.Translation?.ToArray(),
            Rotation = transform.RotationQuaternion?.ToArray(),
            Scale = transform.Scale?.ToArray(),
            Shear = transform.Shear?.ToArray(),
            Pivot = transform.Pivot?.ToArray(),
            PivotTranslation = transform.PivotTranslation?.ToArray(),
        };

        return sceneTransform.Translation is null &&
               sceneTransform.Rotation is null &&
               sceneTransform.Scale is null &&
               sceneTransform.Shear is null &&
               sceneTransform.Pivot is null &&
               sceneTransform.PivotTranslation is null
            ? null
            : sceneTransform;
    }

    private void MaterializeAssetUris()
    {
        List<ConversionWorkItem> conversionWorkItems = [];

        foreach (FoxSceneAsset asset in assetsBySourcePath.Values.OrderBy(asset => asset.SourceFmdl, StringComparer.OrdinalIgnoreCase))
        {
            string modelPath = BuildModelOutputPath(asset.SourceFmdl);
            asset.Uri = BuildRelativeUri(options.OutputPath, modelPath);

            if (options.NoConvert)
            {
                continue;
            }

            string? sourcePath = ResolveAssetPath(asset.SourceFmdl);
            asset.ResolvedSourcePath = sourcePath;
            if (sourcePath is null || !File.Exists(sourcePath))
            {
                asset.Missing = true;
                missingCount++;
                string warning = FormattableString.Invariant($"FMDL asset '{asset.SourceFmdl}' was not found on disk.");
                warnings.Add(warning);
                if (!options.AllowMissingAssets)
                {
                    throw new FileNotFoundException(warning, sourcePath ?? asset.SourceFmdl);
                }

                continue;
            }

            conversionWorkItems.Add(new ConversionWorkItem(asset, sourcePath, modelPath));
        }

        Parallel.ForEach(
            conversionWorkItems,
            new ParallelOptions { MaxDegreeOfParallelism = options.Jobs },
            workItem =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(workItem.OutputPath) ?? Directory.GetCurrentDirectory());
                if (options.RebuildModels || !File.Exists(workItem.OutputPath))
                {
                    RunConverter(workItem.SourcePath, workItem.OutputPath);
                    Interlocked.Increment(ref convertedCount);
                }
                else
                {
                    Interlocked.Increment(ref reusedCount);
                }

                workItem.Asset.ByteLength = new FileInfo(workItem.OutputPath).Length;
            });
    }

    private string? ResolveAssetPath(string assetPath)
    {
        string normalized = assetPath.Replace('/', Path.DirectorySeparatorChar);

        if (Path.IsPathRooted(normalized) && !assetPath.StartsWith("/Assets/", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(normalized);
        }

        if (assetRootPath is null)
        {
            return null;
        }

        string relativePath = assetPath.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(assetRootPath, relativePath));
    }

    private string BuildModelOutputPath(string assetPath)
    {
        string relativePath = assetPath.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar);
        relativePath = relativePath.Replace(':', '_');
        return Path.ChangeExtension(Path.Combine(options.ModelsDirectoryPath, relativePath), ".glb");
    }

    private static string BuildRelativeUri(string fromFilePath, string targetPath)
    {
        string fromDirectory = Path.GetDirectoryName(fromFilePath) ?? Directory.GetCurrentDirectory();
        string relativePath = Path.GetRelativePath(fromDirectory, targetPath).Replace('\\', '/');
        return string.Join("/", relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
    }

    private void RunConverter(string inputPath, string outputPath)
    {
        if (converterCommand is null)
        {
            throw new InvalidOperationException("FmdlGltfConverter command was not initialized.");
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = converterCommand.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (string argument in converterCommand.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (options.NoModelTextures)
        {
            startInfo.ArgumentList.Add("--no-textures");
        }

        if (options.FastPng)
        {
            startInfo.ArgumentList.Add("--fast-png");
        }

        if (!string.IsNullOrWhiteSpace(options.TextureCacheDirectoryPath))
        {
            startInfo.ArgumentList.Add("--texture-cache-dir");
            startInfo.ArgumentList.Add(options.TextureCacheDirectoryPath);
        }

        if (!string.IsNullOrWhiteSpace(options.TextureFormat))
        {
            startInfo.ArgumentList.Add("--texture-format");
            startInfo.ArgumentList.Add(options.TextureFormat);
        }

        if (options.TextureFormat.StartsWith("webp", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add("--webp-quality");
            startInfo.ArgumentList.Add(options.WebpQuality.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("--webp-method");
            startInfo.ArgumentList.Add(options.WebpMethod.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        startInfo.ArgumentList.Add(inputPath);
        startInfo.ArgumentList.Add(outputPath);

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start FmdlGltfConverter.");
        Task<string> stdOutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stdErrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        string stdOut = stdOutTask.GetAwaiter().GetResult();
        string stdErr = stdErrTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(FormattableString.Invariant(
                $"FmdlGltfConverter failed for '{inputPath}'.{Environment.NewLine}{stdOut}{Environment.NewLine}{stdErr}"));
        }
    }

    private static ConverterCommand ResolveConverterCommand(CliOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ConverterPath))
        {
            return ConverterCommand.FromPath(options.ConverterPath);
        }

        string? repositoryRoot = FindRepositoryRoot();
        if (repositoryRoot is null)
        {
            throw new CommandLineException("Unable to locate repository root to find FmdlGltfConverter. Pass --converter explicitly.");
        }

        string[] binaryCandidates =
        {
            Path.Combine(repositoryRoot, "tools", "FmdlGltfConverter", "bin", "Release", "net8.0", "FmdlGltfConverter.exe"),
            Path.Combine(repositoryRoot, "tools", "FmdlGltfConverter", "bin", "Release", "net8.0", "FmdlGltfConverter.dll"),
            Path.Combine(repositoryRoot, "tools", "FmdlGltfConverter", "bin", "Debug", "net8.0", "FmdlGltfConverter.exe"),
            Path.Combine(repositoryRoot, "tools", "FmdlGltfConverter", "bin", "Debug", "net8.0", "FmdlGltfConverter.dll"),
        };

        string? converterPath = binaryCandidates.FirstOrDefault(File.Exists);
        if (converterPath is not null)
        {
            return ConverterCommand.FromPath(converterPath);
        }

        string projectPath = Path.Combine(repositoryRoot, "tools", "FmdlGltfConverter", "FmdlGltfConverter.csproj");
        if (File.Exists(projectPath))
        {
            return new ConverterCommand("dotnet", ["run", "--project", projectPath, "--"]);
        }

        throw new CommandLineException("Unable to locate FmdlGltfConverter. Build it first or pass --converter explicitly.");
    }

    private static string? FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            bool hasReadme = File.Exists(Path.Combine(directory.FullName, "README.md"));
            bool hasTools = Directory.Exists(Path.Combine(directory.FullName, "tools"));
            if (hasReadme && hasTools)
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private sealed record ResolvedModelBinding(string ModelPath, string Source);

    private sealed record ConversionWorkItem(FoxSceneAsset Asset, string SourcePath, string OutputPath);
}

internal sealed record ConverterCommand(string FileName, IReadOnlyList<string> Arguments)
{
    public static ConverterCommand FromPath(string path)
    {
        if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return new ConverterCommand("dotnet", [path]);
        }

        return new ConverterCommand(path, []);
    }
}
