using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SceneGltfComposer;

internal sealed class SceneComposer
{
    private readonly CliOptions options;
    private readonly CompactScene compactScene;
    private readonly string assetRootPath;
    private readonly string cacheDirectoryPath;
    private readonly ConverterCommand converterCommand;
    private readonly GltfRoot scene = new()
    {
        Asset = new GltfAsset
        {
            Generator = "FMDL Studio v2 scene composer",
            Version = "2.0",
        },
        Scene = 0,
        Scenes = new List<GltfScene> { new() },
        Nodes = [],
        Meshes = [],
        Accessors = [],
        BufferViews = [],
        Buffers = [new GltfBuffer { ByteLength = 0 }],
        Materials = [],
        Images = [],
        Textures = [],
        Samplers = [],
        Skins = [],
    };

    private readonly MemoryStream bufferStream = new();
    private readonly Dictionary<string, ImportedModelTemplate> importedModels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> fileModelsByPath;

    public SceneComposer(CliOptions options)
    {
        this.options = options;
        compactScene = CompactScene.Load(options.InputPath);
        assetRootPath = ResolveAssetRootPath(options, compactScene);
        cacheDirectoryPath = options.CacheDirectoryPath ?? Path.Combine(Path.GetDirectoryName(options.OutputPath) ?? Directory.GetCurrentDirectory(), "_model_cache");
        converterCommand = ResolveConverterCommand(options);
        fileModelsByPath = compactScene.Files.ToDictionary(
            file => file.Path,
            file => file.FmdlFiles ?? [],
            StringComparer.OrdinalIgnoreCase);
    }

    public void Compose(string outputPath)
    {
        string sceneRootName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(options.InputPath));
        int sceneRootNode = AddNode(new GltfNode { Name = sceneRootName });
        scene.Scenes[0].Nodes = new List<int> { sceneRootNode };

        foreach (CompactSceneFile file in compactScene.Files)
        {
            int fileNode = AddNode(new GltfNode { Name = Path.GetFileNameWithoutExtension(file.Path.Replace('/', '\\')) });
            AddChild(scene.Nodes[sceneRootNode], fileNode);

            foreach (CompactSceneNode rootNode in file.RootNodes ?? [])
            {
                BuildSceneNode(rootNode, file, fileNode);
            }
        }

        scene.Buffers[0].ByteLength = checked((int)bufferStream.Length);
        GltfBinary.Write(outputPath, scene, bufferStream.ToArray());
    }

    private void BuildSceneNode(CompactSceneNode compactNode, CompactSceneFile file, int parentIndex)
    {
        GltfNode node = new()
        {
            Name = compactNode.Name,
        };

        if (compactNode.Transform is not null)
        {
            node.Translation = compactNode.Transform.Translation?.ToArray();
            node.Rotation = compactNode.Transform.RotationQuaternion?.ToArray();
            node.Scale = compactNode.Transform.Scale?.ToArray();
        }

        int nodeIndex = AddNode(node);
        AddChild(scene.Nodes[parentIndex], nodeIndex);

        foreach (string modelPath in ResolveNodeModels(compactNode, file))
        {
            ImportedModelTemplate template = ImportModel(modelPath);
            InstantiateModel(template, nodeIndex);
        }

        foreach (CompactSceneNode child in compactNode.Children ?? [])
        {
            BuildSceneNode(child, file, nodeIndex);
        }
    }

    private IReadOnlyList<string> ResolveNodeModels(CompactSceneNode node, CompactSceneFile file)
    {
        if (node.FmdlPaths is { Count: > 0 })
        {
            return node.FmdlPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        if (options.DisableNameFallback || string.IsNullOrWhiteSpace(node.Name))
        {
            return Array.Empty<string>();
        }

        IReadOnlyList<string> fileModels = fileModelsByPath.GetValueOrDefault(file.Path, []);
        string? match = GuessModelFromName(node.Name!, fileModels);
        return match is null ? Array.Empty<string>() : new[] { match };
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

    private ImportedModelTemplate ImportModel(string assetPath)
    {
        if (importedModels.TryGetValue(assetPath, out ImportedModelTemplate? cached))
        {
            return cached;
        }

        string fmdlFilePath = ResolveAssetPath(assetPath);
        if (!File.Exists(fmdlFilePath))
        {
            throw new FileNotFoundException(FormattableString.Invariant($"FMDL file '{assetPath}' was not found on disk."), fmdlFilePath);
        }

        string cachePath = BuildCachePath(assetPath);
        if (options.RebuildModels || !File.Exists(cachePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath) ?? Directory.GetCurrentDirectory());
            RunConverter(fmdlFilePath, cachePath);
        }

        GltfDocument source = GltfBinary.ReadGlb(cachePath);
        ImportedModelTemplate template = MergeSourceModel(assetPath, source);
        importedModels.Add(assetPath, template);
        return template;
    }

    private ImportedModelTemplate MergeSourceModel(string assetPath, GltfDocument source)
    {
        int bufferBaseOffset = AppendBinary(source.BinaryChunk);
        int[] bufferViewMap = new int[source.Root.BufferViews.Count];
        for (int index = 0; index < source.Root.BufferViews.Count; index++)
        {
            GltfBufferView sourceView = source.Root.BufferViews[index];
            bufferViewMap[index] = scene.BufferViews.Count;
            scene.BufferViews.Add(new GltfBufferView
            {
                Buffer = 0,
                ByteOffset = bufferBaseOffset + sourceView.ByteOffset,
                ByteLength = sourceView.ByteLength,
                ByteStride = sourceView.ByteStride,
                Target = sourceView.Target,
            });
        }

        int[] accessorMap = new int[source.Root.Accessors.Count];
        for (int index = 0; index < source.Root.Accessors.Count; index++)
        {
            GltfAccessor sourceAccessor = source.Root.Accessors[index];
            accessorMap[index] = scene.Accessors.Count;
            scene.Accessors.Add(new GltfAccessor
            {
                BufferView = bufferViewMap[sourceAccessor.BufferView],
                ComponentType = sourceAccessor.ComponentType,
                Count = sourceAccessor.Count,
                Type = sourceAccessor.Type,
                ByteOffset = sourceAccessor.ByteOffset,
                Normalized = sourceAccessor.Normalized,
                Min = sourceAccessor.Min is null ? null : (float[])sourceAccessor.Min.Clone(),
                Max = sourceAccessor.Max is null ? null : (float[])sourceAccessor.Max.Clone(),
            });
        }

        int[] samplerMap = new int[source.Root.Samplers.Count];
        for (int index = 0; index < source.Root.Samplers.Count; index++)
        {
            GltfSampler sampler = source.Root.Samplers[index];
            samplerMap[index] = scene.Samplers.Count;
            scene.Samplers.Add(new GltfSampler
            {
                MagFilter = sampler.MagFilter,
                MinFilter = sampler.MinFilter,
                WrapS = sampler.WrapS,
                WrapT = sampler.WrapT,
            });
        }

        int[] imageMap = new int[source.Root.Images.Count];
        for (int index = 0; index < source.Root.Images.Count; index++)
        {
            GltfImage image = source.Root.Images[index];
            imageMap[index] = scene.Images.Count;
            scene.Images.Add(new GltfImage
            {
                Name = image.Name,
                BufferView = image.BufferView.HasValue ? bufferViewMap[image.BufferView.Value] : null,
                MimeType = image.MimeType,
                Uri = image.Uri,
            });
        }

        int[] textureMap = new int[source.Root.Textures.Count];
        for (int index = 0; index < source.Root.Textures.Count; index++)
        {
            GltfTexture texture = source.Root.Textures[index];
            textureMap[index] = scene.Textures.Count;
            scene.Textures.Add(new GltfTexture
            {
                Name = texture.Name,
                Sampler = texture.Sampler.HasValue ? samplerMap[texture.Sampler.Value] : null,
                Source = imageMap[texture.Source],
            });
        }

        int[] materialMap = new int[source.Root.Materials.Count];
        for (int index = 0; index < source.Root.Materials.Count; index++)
        {
            GltfMaterial material = source.Root.Materials[index];
            materialMap[index] = scene.Materials.Count;
            scene.Materials.Add(new GltfMaterial
            {
                Name = material.Name,
                PbrMetallicRoughness = RemapPbr(material.PbrMetallicRoughness, textureMap),
                DoubleSided = material.DoubleSided,
                AlphaMode = material.AlphaMode,
                NormalTexture = RemapNormalTexture(material.NormalTexture, textureMap),
                OcclusionTexture = RemapOcclusionTexture(material.OcclusionTexture, textureMap),
                EmissiveTexture = RemapTextureInfo(material.EmissiveTexture, textureMap),
                EmissiveFactor = material.EmissiveFactor is null ? null : (float[])material.EmissiveFactor.Clone(),
                Extras = material.Extras,
            });
        }

        int[] meshMap = new int[source.Root.Meshes.Count];
        for (int index = 0; index < source.Root.Meshes.Count; index++)
        {
            GltfMesh mesh = source.Root.Meshes[index];
            meshMap[index] = scene.Meshes.Count;
            scene.Meshes.Add(new GltfMesh
            {
                Name = mesh.Name,
                Primitives = mesh.Primitives.Select(primitive => new GltfPrimitive
                {
                    Attributes = primitive.Attributes.ToDictionary(
                        pair => pair.Key,
                        pair => accessorMap[pair.Value],
                        StringComparer.Ordinal),
                    Indices = accessorMap[primitive.Indices],
                    Material = primitive.Material.HasValue ? materialMap[primitive.Material.Value] : null,
                    Mode = primitive.Mode,
                }).ToList(),
            });
        }

        IReadOnlyList<int> sourceRootNodes = source.Root.Scene.HasValue &&
                                             source.Root.Scene.Value < source.Root.Scenes.Count &&
                                             source.Root.Scenes[source.Root.Scene.Value].Nodes is { } sceneNodes
            ? sceneNodes
            : Array.Empty<int>();

        return new ImportedModelTemplate(assetPath, source.Root, sourceRootNodes, accessorMap, meshMap);
    }

    private static GltfPbrMetallicRoughness? RemapPbr(GltfPbrMetallicRoughness? source, IReadOnlyList<int> textureMap)
    {
        if (source is null)
        {
            return null;
        }

        return new GltfPbrMetallicRoughness
        {
            BaseColorFactor = source.BaseColorFactor is null ? null : (float[])source.BaseColorFactor.Clone(),
            BaseColorTexture = RemapTextureInfo(source.BaseColorTexture, textureMap),
            MetallicFactor = source.MetallicFactor,
            RoughnessFactor = source.RoughnessFactor,
            MetallicRoughnessTexture = RemapTextureInfo(source.MetallicRoughnessTexture, textureMap),
        };
    }

    private static GltfTextureInfo? RemapTextureInfo(GltfTextureInfo? source, IReadOnlyList<int> textureMap)
    {
        if (source is null)
        {
            return null;
        }

        return new GltfTextureInfo
        {
            Index = textureMap[source.Index],
            TexCoord = source.TexCoord,
        };
    }

    private static GltfNormalTextureInfo? RemapNormalTexture(GltfNormalTextureInfo? source, IReadOnlyList<int> textureMap)
    {
        if (source is null)
        {
            return null;
        }

        return new GltfNormalTextureInfo
        {
            Index = textureMap[source.Index],
            TexCoord = source.TexCoord,
            Scale = source.Scale,
        };
    }

    private static GltfOcclusionTextureInfo? RemapOcclusionTexture(GltfOcclusionTextureInfo? source, IReadOnlyList<int> textureMap)
    {
        if (source is null)
        {
            return null;
        }

        return new GltfOcclusionTextureInfo
        {
            Index = textureMap[source.Index],
            TexCoord = source.TexCoord,
            Strength = source.Strength,
        };
    }

    private void InstantiateModel(ImportedModelTemplate template, int parentNodeIndex)
    {
        Dictionary<int, int> nodeMap = new();
        List<(int NewNodeIndex, int OldSkinIndex)> skinnedNodes = new();
        List<int> newRootNodes = new();

        foreach (int oldRootIndex in EnumerateAttachableRootNodes(template))
        {
            newRootNodes.Add(CloneModelNode(template, oldRootIndex, nodeMap, skinnedNodes));
        }

        Dictionary<int, int> skinMap = new();
        foreach (int oldSkinIndex in skinnedNodes.Select(entry => entry.OldSkinIndex).Distinct())
        {
            GltfSkin oldSkin = template.Source.Skins[oldSkinIndex];
            int newSkinIndex = scene.Skins.Count;
            scene.Skins.Add(new GltfSkin
            {
                Name = oldSkin.Name,
                InverseBindMatrices = oldSkin.InverseBindMatrices.HasValue ? template.AccessorMap[oldSkin.InverseBindMatrices.Value] : null,
                Joints = oldSkin.Joints.Select(jointIndex => nodeMap[jointIndex]).ToList(),
                Skeleton = oldSkin.Skeleton.HasValue ? nodeMap[oldSkin.Skeleton.Value] : null,
            });
            skinMap.Add(oldSkinIndex, newSkinIndex);
        }

        foreach ((int newNodeIndex, int oldSkinIndex) in skinnedNodes)
        {
            scene.Nodes[newNodeIndex].Skin = skinMap[oldSkinIndex];
        }

        foreach (int newRootNode in newRootNodes)
        {
            AddChild(scene.Nodes[parentNodeIndex], newRootNode);
        }
    }

    private static IEnumerable<int> EnumerateAttachableRootNodes(ImportedModelTemplate template)
    {
        foreach (int rootNodeIndex in template.SourceRootNodes)
        {
            GltfNode rootNode = template.Source.Nodes[rootNodeIndex];
            if (IsRedundantImportedRoot(rootNode))
            {
                foreach (int childIndex in rootNode.Children!)
                {
                    yield return childIndex;
                }

                continue;
            }

            yield return rootNodeIndex;
        }
    }

    private static bool IsRedundantImportedRoot(GltfNode node)
    {
        return node.Mesh is null &&
               node.Skin is null &&
               node.Children is { Count: > 0 } &&
               !HasMeaningfulExtras(node.Extras) &&
               HasIdentityTransform(node);
    }

    private static bool HasMeaningfulExtras(Dictionary<string, object?>? extras)
    {
        return extras is { Count: > 0 };
    }

    private static bool HasIdentityTransform(GltfNode node)
    {
        return IsIdentityTranslation(node.Translation) &&
               IsIdentityRotation(node.Rotation) &&
               IsIdentityScale(node.Scale);
    }

    private static bool IsIdentityTranslation(float[]? translation)
    {
        return translation is null || (translation.Length == 3 && IsApproximately(translation[0], 0.0f) && IsApproximately(translation[1], 0.0f) && IsApproximately(translation[2], 0.0f));
    }

    private static bool IsIdentityRotation(float[]? rotation)
    {
        return rotation is null || (rotation.Length == 4 && IsApproximately(rotation[0], 0.0f) && IsApproximately(rotation[1], 0.0f) && IsApproximately(rotation[2], 0.0f) && IsApproximately(rotation[3], 1.0f));
    }

    private static bool IsIdentityScale(float[]? scale)
    {
        return scale is null || (scale.Length == 3 && IsApproximately(scale[0], 1.0f) && IsApproximately(scale[1], 1.0f) && IsApproximately(scale[2], 1.0f));
    }

    private static bool IsApproximately(float left, float right)
    {
        return Math.Abs(left - right) <= 1e-6f;
    }

    private int CloneModelNode(
        ImportedModelTemplate template,
        int oldNodeIndex,
        Dictionary<int, int> nodeMap,
        List<(int NewNodeIndex, int OldSkinIndex)> skinnedNodes)
    {
        GltfNode oldNode = template.Source.Nodes[oldNodeIndex];
        GltfNode newNode = new()
        {
            Name = oldNode.Name,
            Translation = oldNode.Translation is null ? null : (float[])oldNode.Translation.Clone(),
            Rotation = oldNode.Rotation is null ? null : (float[])oldNode.Rotation.Clone(),
            Scale = oldNode.Scale is null ? null : (float[])oldNode.Scale.Clone(),
            Mesh = oldNode.Mesh.HasValue ? template.MeshMap[oldNode.Mesh.Value] : null,
            Extras = oldNode.Extras,
        };

        int newNodeIndex = AddNode(newNode);
        nodeMap.Add(oldNodeIndex, newNodeIndex);

        if (oldNode.Skin.HasValue)
        {
            skinnedNodes.Add((newNodeIndex, oldNode.Skin.Value));
        }

        foreach (int childIndex in oldNode.Children ?? Enumerable.Empty<int>())
        {
            int newChildIndex = CloneModelNode(template, childIndex, nodeMap, skinnedNodes);
            AddChild(scene.Nodes[newNodeIndex], newChildIndex);
        }

        return newNodeIndex;
    }

    private int AppendBinary(byte[] bytes)
    {
        int alignedOffset = GltfBinary.Align4(checked((int)bufferStream.Length));
        while (bufferStream.Length < alignedOffset)
        {
            bufferStream.WriteByte(0);
        }

        bufferStream.Write(bytes, 0, bytes.Length);
        return alignedOffset;
    }

    private string ResolveAssetPath(string assetPath)
    {
        string relativePath = assetPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(assetRootPath, relativePath));
    }

    private string BuildCachePath(string assetPath)
    {
        string relativePath = assetPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        return Path.ChangeExtension(Path.Combine(cacheDirectoryPath, relativePath), ".glb");
    }

    private void RunConverter(string inputPath, string outputPath)
    {
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

    private int AddNode(GltfNode node)
    {
        int index = scene.Nodes.Count;
        scene.Nodes.Add(node);
        return index;
    }

    private static void AddChild(GltfNode parentNode, int childIndex)
    {
        parentNode.Children ??= new List<int>();
        parentNode.Children.Add(childIndex);
    }

    private static string ResolveAssetRootPath(CliOptions options, CompactScene scene)
    {
        string? assetRootPath = options.AssetRootPath ?? scene.AssetRootPath;
        if (string.IsNullOrWhiteSpace(assetRootPath))
        {
            throw new CommandLineException("Missing asset root. Pass --asset-root or use a compact scene JSON that includes assetRootPath.");
        }

        return Path.GetFullPath(assetRootPath);
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

        string[] candidates =
        {
            Path.Combine(repositoryRoot, "tools", "FmdlGltfConverter", "bin", "Release", "net8.0", "FmdlGltfConverter.exe"),
            Path.Combine(repositoryRoot, "tools", "FmdlGltfConverter", "bin", "Release", "net8.0", "FmdlGltfConverter.dll"),
            Path.Combine(repositoryRoot, "tools", "FmdlGltfConverter", "bin", "Debug", "net8.0", "FmdlGltfConverter.exe"),
            Path.Combine(repositoryRoot, "tools", "FmdlGltfConverter", "bin", "Debug", "net8.0", "FmdlGltfConverter.dll"),
        };

        string? converterPath = candidates.FirstOrDefault(File.Exists);
        if (converterPath is null)
        {
            throw new CommandLineException("Unable to locate FmdlGltfConverter. Build it first or pass --converter explicitly.");
        }

        return ConverterCommand.FromPath(converterPath);
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
}

internal sealed record ImportedModelTemplate(
    string AssetPath,
    GltfRoot Source,
    IReadOnlyList<int> SourceRootNodes,
    IReadOnlyList<int> AccessorMap,
    IReadOnlyList<int> MeshMap);

internal sealed class ConverterCommand
{
    private ConverterCommand(string fileName, IReadOnlyList<string> arguments)
    {
        FileName = fileName;
        Arguments = arguments;
    }

    public string FileName { get; }
    public IReadOnlyList<string> Arguments { get; }

    public static ConverterCommand FromPath(string path)
    {
        if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return new ConverterCommand("dotnet", new[] { path });
        }

        return new ConverterCommand(path, Array.Empty<string>());
    }
}
