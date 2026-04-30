using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fox2SceneConverter;

internal sealed class SceneDiscoveryService
{
    private static readonly HashSet<string> StructuralNodePropertyNames =
    [
        "parent",
        "children",
        "transform",
        "shearTransform",
        "pivotTransform",
    ];

    private static readonly HashSet<string> StaticModelArrayContainerExcludedProperties =
    [
        "modelFile",
        "geomFile",
        "transforms",
        "colors",
    ];

    private static readonly HashSet<string> SharedGimmickContainerExcludedProperties =
    [
        "modelFile",
        "geomFile",
        "breakedModelFile",
        "breakedGeomFile",
        "partsFile",
        "locaterFile",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly Fox2Parser fox2Parser = new();

    public SceneDescription Discover(CliOptions options)
    {
        AssetPathResolver pathResolver = AssetPathResolver.Create(options.InputPath, options.AssetRootPath);
        Queue<DiscoveryWorkItem> pending = new();
        HashSet<string> visitedFiles = new(StringComparer.OrdinalIgnoreCase);
        List<SceneFileDescription> files = new();
        Dictionary<string, HashSet<string>> fmdlSources = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, HashSet<string>> fmdlsByFox2Path = new(StringComparer.OrdinalIgnoreCase);

        Enqueue(options.InputPath, discoveredFrom: null, discoveryHint: "input", depth: 0, owningFox2Path: pathResolver.ToDisplayPath(options.InputPath));

        while (pending.Count > 0)
        {
            DiscoveryWorkItem workItem = pending.Dequeue();
            string extension = Path.GetExtension(workItem.FilePath);
            string kind = extension.TrimStart('.').ToLowerInvariant();

            if (extension.Equals(".fox2", StringComparison.OrdinalIgnoreCase))
            {
                Fox2File fox2File = fox2Parser.Parse(workItem.FilePath, pathResolver);
                IReadOnlyDictionary<ulong, FoxEntity> entityLookup = fox2File.Entities.ToDictionary(entity => entity.Address);
                List<FileReferenceDescription> references = ExtractFox2References(fox2File, entityLookup, pathResolver);
                SceneFileDescription fileDescription = new()
                {
                    Path = fox2File.DisplayPath,
                    Kind = kind,
                    DiscoveredFrom = workItem.DiscoveredFrom,
                    DiscoveryHint = workItem.DiscoveryHint,
                    References = references,
                    RootNodes = BuildRootNodes(fox2File.SourcePath, pathResolver, fox2File.Entities, entityLookup),
                    Entities = fox2File.Entities.Select(entity => BuildEntityDescription(entity, entityLookup)).ToList(),
                };
                files.Add(fileDescription);
                RegisterAndQueueReferences(references, fileDescription.Path, workItem.Depth, workItem.OwningFox2Path ?? fileDescription.Path);
                continue;
            }

            byte[] data = File.ReadAllBytes(workItem.FilePath);
            List<FileReferenceDescription> embeddedReferences = AssetPathScanner.ExtractPaths(data)
                .Select(path => CreateReference(path, $"embedded:{kind}", pathResolver, workItem.FilePath))
                .Distinct(FileReferenceDescriptionComparer.Instance)
                .ToList();

            files.Add(new SceneFileDescription
            {
                Path = pathResolver.ToDisplayPath(workItem.FilePath),
                Kind = kind,
                DiscoveredFrom = workItem.DiscoveredFrom,
                DiscoveryHint = workItem.DiscoveryHint,
                References = embeddedReferences,
            });
            RegisterAndQueueReferences(
                embeddedReferences,
                pathResolver.ToDisplayPath(workItem.FilePath),
                workItem.Depth,
                workItem.OwningFox2Path);
        }

        return new SceneDescription
        {
            SourceFile = options.InputPath,
            SourceAssetPath = pathResolver.ToDisplayPath(options.InputPath),
            AssetRootPath = pathResolver.AssetRootPath,
            Summary = new SceneSummary
            {
                FilesVisited = files.Count,
                Fox2Files = files.Count(file => file.Kind.Equals("fox2", StringComparison.OrdinalIgnoreCase)),
                ArchiveFiles = files.Count(file => !file.Kind.Equals("fox2", StringComparison.OrdinalIgnoreCase)),
                EntityCount = files.Where(file => file.Entities is not null).Sum(file => file.Entities!.Count),
                NodeCount = files.Where(file => file.RootNodes is not null).Sum(file => CountNodes(file.RootNodes!)),
                FmdlCount = fmdlSources.Count,
            },
            Files = files,
            FmdlFiles = fmdlSources
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new FmdlReferenceDescription
                {
                    Path = pair.Key,
                    ResolvedPath = pathResolver.TryResolveToFilePath(pair.Key, options.InputPath),
                    Sources = pair.Value.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList(),
                })
                .ToList(),
            Fox2FmdlFiles = fmdlsByFox2Path.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase),
        };

        void Enqueue(string filePath, string? discoveredFrom, string discoveryHint, int depth, string? owningFox2Path)
        {
            string fullPath = Path.GetFullPath(filePath);
            if (!visitedFiles.Add(fullPath))
            {
                return;
            }

            pending.Enqueue(new DiscoveryWorkItem(fullPath, discoveredFrom, discoveryHint, depth, owningFox2Path));
        }

        void RegisterAndQueueReferences(
            IEnumerable<FileReferenceDescription> references,
            string currentFileDisplayPath,
            int currentDepth,
            string? owningFox2Path)
        {
            foreach (FileReferenceDescription reference in references)
            {
                if (AssetPathScanner.IsTerminalModelPath(reference.Path))
                {
                    if (!fmdlSources.TryGetValue(reference.Path, out HashSet<string>? sources))
                    {
                        sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        fmdlSources.Add(reference.Path, sources);
                    }

                    sources.Add(reference.Source);

                    if (!string.IsNullOrWhiteSpace(owningFox2Path))
                    {
                        if (!fmdlsByFox2Path.TryGetValue(owningFox2Path, out HashSet<string>? fox2Models))
                        {
                            fox2Models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            fmdlsByFox2Path.Add(owningFox2Path, fox2Models);
                        }

                        fox2Models.Add(reference.Path);
                    }
                }

                if (!options.Recursive || currentDepth >= options.MaxDepth || !AssetPathScanner.IsRecursiveContainer(reference.Path))
                {
                    continue;
                }

                if (reference.ResolvedPath is null || !File.Exists(reference.ResolvedPath))
                {
                    continue;
                }

                string nextOwningFox2Path = reference.ReferenceType.Equals("fox2", StringComparison.OrdinalIgnoreCase)
                    ? reference.Path
                    : owningFox2Path ?? currentFileDisplayPath;
                Enqueue(reference.ResolvedPath, currentFileDisplayPath, reference.Source, currentDepth + 1, nextOwningFox2Path);
            }
        }
    }

    public void WriteJson(SceneDescription description, string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory());
        File.WriteAllText(outputPath, JsonSerializer.Serialize(description, JsonOptions));
    }

    public CompactSceneDescription BuildCompactScene(SceneDescription description)
    {
        List<CompactSceneFileDescription> files = description.Files
            .Where(file => file.Kind.Equals("fox2", StringComparison.OrdinalIgnoreCase))
            .Select(file =>
            {
                description.Fox2FmdlFiles.TryGetValue(file.Path, out List<string>? availableFmdls);
                List<CompactSceneNodeDescription>? rootNodes = file.RootNodes?
                    .Select(BuildCompactNode)
                    .Where(node => node is not null)
                    .Cast<CompactSceneNodeDescription>()
                    .ToList();

                return new CompactSceneFileDescription
                {
                    Path = file.Path,
                    DiscoveredFrom = file.DiscoveredFrom,
                    FmdlFiles = availableFmdls,
                    RootNodes = rootNodes,
                };
            })
            .ToList();

        return new CompactSceneDescription
        {
            SourceFile = description.SourceFile,
            SourceAssetPath = description.SourceAssetPath,
            AssetRootPath = description.AssetRootPath,
            Summary = new CompactSceneSummary
            {
                Fox2FileCount = files.Count,
                NodeCount = files.Where(file => file.RootNodes is not null).Sum(file => CountCompactNodes(file.RootNodes!)),
                FmdlCount = description.FmdlFiles.Count,
            },
            Files = files,
            FmdlFiles = description.FmdlFiles.Select(model => model.Path).ToList(),
        };
    }

    public void WriteCompactJson(CompactSceneDescription description, string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory());
        File.WriteAllText(outputPath, JsonSerializer.Serialize(description, JsonOptions));
    }

    private static List<FileReferenceDescription> ExtractFox2References(
        Fox2File fox2File,
        IReadOnlyDictionary<ulong, FoxEntity> entityLookup,
        AssetPathResolver pathResolver)
    {
        List<FileReferenceDescription> references = new();

        foreach (FoxEntity entity in fox2File.Entities)
        {
            foreach (FoxProperty property in entity.Properties)
            {
                foreach (string path in ExtractPaths(property.Value))
                {
                    references.Add(CreateReference(path, $"{entity.DisplayName}.{property.Name}", pathResolver, fox2File.SourcePath));
                }
            }
        }

        foreach (string path in fox2File.EmbeddedAssetPaths)
        {
            references.Add(CreateReference(path, "embedded:string-table", pathResolver, fox2File.SourcePath));
        }

        references.AddRange(DiscoverTerrainFox2References(fox2File.SourcePath, fox2File.Entities, pathResolver));
        return references.Distinct(FileReferenceDescriptionComparer.Instance).ToList();
    }

    private static IEnumerable<string> ExtractPaths(FoxPropertyContainer container)
    {
        return container switch
        {
            FoxSingleValue singleValue => ExtractPaths(singleValue.Value),
            FoxListValue listValue => listValue.Items.SelectMany(ExtractPaths),
            FoxStringMapValue stringMapValue => stringMapValue.Items.Values.SelectMany(ExtractPaths),
            _ => Array.Empty<string>(),
        };
    }

    private static IEnumerable<string> ExtractPaths(FoxValue value)
    {
        return value switch
        {
            FoxStringValue stringValue => AssetPathScanner.ExtractPathsFromText(stringValue.Value),
            FoxEntityLinkValue entityLinkValue => AssetPathScanner.ExtractPathsFromText(entityLinkValue.PackagePath)
                .Concat(AssetPathScanner.ExtractPathsFromText(entityLinkValue.ArchivePath)),
            _ => Array.Empty<string>(),
        };
    }

    private static FileReferenceDescription CreateReference(string path, string source, AssetPathResolver pathResolver, string currentFilePath)
    {
        string referenceType = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return new FileReferenceDescription
        {
            Path = path,
            ResolvedPath = pathResolver.TryResolveToFilePath(path, currentFilePath),
            ReferenceType = referenceType,
            Source = source,
        };
    }

    private static EntityDescription BuildEntityDescription(FoxEntity entity, IReadOnlyDictionary<ulong, FoxEntity> entityLookup)
    {
        return new EntityDescription
        {
            Address = FoxFormatting.FormatAddress(entity.Address),
            ClassName = entity.ClassName,
            Name = entity.DisplayName,
            Properties = entity.Properties.ToDictionary(
                property => property.Name,
                property => FoxValueJsonConverter.ToJsonObject(property.Value, entityLookup),
                StringComparer.Ordinal),
        };
    }

    private static List<SceneNodeDescription> BuildRootNodes(
        string sourceFilePath,
        AssetPathResolver pathResolver,
        IReadOnlyList<FoxEntity> entities,
        IReadOnlyDictionary<ulong, FoxEntity> entityLookup)
    {
        HashSet<ulong> candidates = new();
        Dictionary<ulong, HashSet<ulong>> childrenByParent = new();
        Dictionary<ulong, ulong> parentByChild = new();
        Dictionary<ulong, List<SceneNodeDescription>> syntheticChildrenByParent = new();

        foreach (FoxEntity entity in entities)
        {
            bool isCandidate = entity.FindProperty("parent") is not null ||
                               entity.FindProperty("children") is not null ||
                               entity.FindProperty("transform") is not null ||
                               entity.FindProperty("referenceFilePath") is not null;
            if (isCandidate)
            {
                candidates.Add(entity.Address);
            }

            ulong? explicitParent = entity.TryGetEntityReference("parent");
            if (explicitParent is ulong parentAddress && entityLookup.ContainsKey(parentAddress))
            {
                candidates.Add(parentAddress);
                candidates.Add(entity.Address);
                parentByChild[entity.Address] = parentAddress;
                GetChildren(parentAddress).Add(entity.Address);
            }

            foreach (ulong childAddress in entity.GetEntityReferenceList("children"))
            {
                if (!entityLookup.ContainsKey(childAddress))
                {
                    continue;
                }

                candidates.Add(entity.Address);
                candidates.Add(childAddress);
                parentByChild[childAddress] = entity.Address;
                GetChildren(entity.Address).Add(childAddress);
            }
        }

        List<SceneNodeDescription> syntheticRoots = BuildSyntheticRootNodes(sourceFilePath, pathResolver, entities, entityLookup, syntheticChildrenByParent);
        List<SceneNodeDescription> roots = new();
        foreach (ulong rootAddress in candidates.Where(address => !parentByChild.ContainsKey(address)).OrderBy(address => address))
        {
            if (!entityLookup.TryGetValue(rootAddress, out FoxEntity? entity))
            {
                continue;
            }

            roots.Add(BuildNode(entity, entityLookup, childrenByParent, syntheticChildrenByParent, new HashSet<ulong>()));
        }

        roots.AddRange(syntheticRoots.OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase).ThenBy(node => node.Address, StringComparer.Ordinal));
        return roots;

        HashSet<ulong> GetChildren(ulong parentAddress)
        {
            if (!childrenByParent.TryGetValue(parentAddress, out HashSet<ulong>? children))
            {
                children = new HashSet<ulong>();
                childrenByParent.Add(parentAddress, children);
            }

            return children;
        }
    }

    private static SceneNodeDescription BuildNode(
        FoxEntity entity,
        IReadOnlyDictionary<ulong, FoxEntity> entityLookup,
        IReadOnlyDictionary<ulong, HashSet<ulong>> childrenByParent,
        IReadOnlyDictionary<ulong, List<SceneNodeDescription>> syntheticChildrenByParent,
        HashSet<ulong> recursionGuard)
    {
        if (!recursionGuard.Add(entity.Address))
        {
            return new SceneNodeDescription
            {
                Address = FoxFormatting.FormatAddress(entity.Address),
                ClassName = entity.ClassName,
                Name = entity.DisplayName,
            };
        }

        List<SceneNodeDescription>? children = null;
        if (childrenByParent.TryGetValue(entity.Address, out HashSet<ulong>? childAddresses))
        {
            children = childAddresses
                .OrderBy(address => address)
                .Where(entityLookup.ContainsKey)
                .Select(address => BuildNode(entityLookup[address], entityLookup, childrenByParent, syntheticChildrenByParent, recursionGuard))
                .ToList();
        }

        if (syntheticChildrenByParent.TryGetValue(entity.Address, out List<SceneNodeDescription>? syntheticChildren))
        {
            children ??= new List<SceneNodeDescription>();
            children.AddRange(syntheticChildren);
        }

        recursionGuard.Remove(entity.Address);

        return new SceneNodeDescription
        {
            Address = FoxFormatting.FormatAddress(entity.Address),
            ClassName = entity.ClassName,
            Name = entity.DisplayName,
            Transform = BuildTransform(entity, entityLookup),
            Properties = BuildNodeProperties(entity, entityLookup),
            Children = children,
        };
    }

    private static List<SceneNodeDescription> BuildSyntheticRootNodes(
        string sourceFilePath,
        AssetPathResolver pathResolver,
        IReadOnlyList<FoxEntity> entities,
        IReadOnlyDictionary<ulong, FoxEntity> entityLookup,
        Dictionary<ulong, List<SceneNodeDescription>> syntheticChildrenByParent)
    {
        List<SceneNodeDescription> syntheticRoots = new();
        Dictionary<ulong, IReadOnlyDictionary<int, string>> instanceNamesByArrayAddress = BuildStaticModelArrayInstanceNames(entities);
        syntheticRoots.AddRange(BuildStaticModelArrayNodes(entities, entityLookup, syntheticChildrenByParent, instanceNamesByArrayAddress));
        syntheticRoots.AddRange(BuildSharedGimmickNodes(sourceFilePath, pathResolver, entities, entityLookup));
        syntheticRoots.AddRange(BuildTerrainBlockNodes(entities, entityLookup));
        return syntheticRoots;
    }

    private static List<FileReferenceDescription> DiscoverTerrainFox2References(
        string sourceFilePath,
        IReadOnlyList<FoxEntity> entities,
        AssetPathResolver pathResolver)
    {
        List<FileReferenceDescription> references = new();
        string? terrainDirectory = ResolveTerrainDirectoryPath(sourceFilePath, entities, pathResolver);
        if (terrainDirectory is null || !Directory.Exists(terrainDirectory))
        {
            return references;
        }

        foreach (string terrainFox2Path in Directory.EnumerateFiles(terrainDirectory, "*_terrain.fox2", SearchOption.AllDirectories))
        {
            string displayPath = pathResolver.ToDisplayPath(terrainFox2Path);
            references.Add(CreateReference(displayPath, "terrain:block-scan", pathResolver, sourceFilePath));
        }

        return references;
    }

    private static string? ResolveTerrainDirectoryPath(
        string sourceFilePath,
        IReadOnlyList<FoxEntity> entities,
        AssetPathResolver pathResolver)
    {
        foreach (FoxEntity entity in entities)
        {
            if (!entity.ClassName.Contains("BlockControllerData", StringComparison.Ordinal))
            {
                continue;
            }

            string? basePath = entity.TryGetStringProperty("baseDirectoryPath") ?? entity.TryGetStringProperty("basePath");
            if (string.IsNullOrWhiteSpace(basePath))
            {
                continue;
            }

            string? baseDirectoryPath = pathResolver.TryResolveToFilePath(basePath, sourceFilePath);
            if (string.IsNullOrWhiteSpace(baseDirectoryPath))
            {
                continue;
            }

            string normalized = Path.GetFullPath(baseDirectoryPath);
            normalized = normalized.Replace($"{Path.DirectorySeparatorChar}pack{Path.DirectorySeparatorChar}location{Path.DirectorySeparatorChar}",
                $"{Path.DirectorySeparatorChar}level{Path.DirectorySeparatorChar}location{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase);
            normalized = normalized.Replace($"{Path.DirectorySeparatorChar}pack_small", $"{Path.DirectorySeparatorChar}block_small", StringComparison.OrdinalIgnoreCase);
            normalized = normalized.Replace($"{Path.DirectorySeparatorChar}pack_large", $"{Path.DirectorySeparatorChar}block_large", StringComparison.OrdinalIgnoreCase);
            return normalized;
        }

        return null;
    }

    private static List<SceneNodeDescription> BuildTerrainBlockNodes(
        IReadOnlyList<FoxEntity> entities,
        IReadOnlyDictionary<ulong, FoxEntity> entityLookup)
    {
        List<SceneNodeDescription> roots = new();
        foreach (FoxEntity entity in entities)
        {
            if (!entity.ClassName.Equals("TerrainBlock", StringComparison.Ordinal))
            {
                continue;
            }

            roots.Add(new SceneNodeDescription
            {
                Address = FoxFormatting.FormatAddress(entity.Address),
                ClassName = entity.ClassName,
                Name = entity.DisplayName,
                Transform = BuildTerrainBlockTransform(entity),
                Properties = BuildNodeProperties(entity, entityLookup),
            });
        }

        return roots;
    }

    private static SceneNodeTransformDescription? BuildTerrainBlockTransform(FoxEntity entity)
    {
        Dictionary<string, float>? pos = GetVector3(entity, "pos");
        if (pos is null)
        {
            return null;
        }

        return new SceneNodeTransformDescription
        {
            Translation = pos,
            RotationQuaternion = new Dictionary<string, float>(StringComparer.Ordinal)
            {
                ["x"] = 0.0f,
                ["y"] = 0.0f,
                ["z"] = 0.0f,
                ["w"] = 1.0f,
            },
            Scale = new Dictionary<string, float>(StringComparer.Ordinal)
            {
                ["x"] = 1.0f,
                ["y"] = 1.0f,
                ["z"] = 1.0f,
            },
        };
    }

    private static List<SceneNodeDescription> BuildStaticModelArrayNodes(
        IReadOnlyList<FoxEntity> entities,
        IReadOnlyDictionary<ulong, FoxEntity> entityLookup,
        Dictionary<ulong, List<SceneNodeDescription>> syntheticChildrenByParent,
        IReadOnlyDictionary<ulong, IReadOnlyDictionary<int, string>> instanceNamesByArrayAddress)
    {
        List<SceneNodeDescription> syntheticRoots = new();

        foreach (FoxEntity entity in entities)
        {
            if (!entity.ClassName.Equals("StaticModelArray", StringComparison.Ordinal))
            {
                continue;
            }

            SceneNodeDescription? arrayNode = BuildStaticModelArrayNode(
                entity,
                entityLookup,
                instanceNamesByArrayAddress.GetValueOrDefault(entity.Address));

            if (arrayNode is null)
            {
                continue;
            }

            ulong? parentAddress = TryGetLinkedEntityAddress(entity, "parentLocator");
            if (parentAddress is ulong resolvedParentAddress && entityLookup.ContainsKey(resolvedParentAddress))
            {
                if (!syntheticChildrenByParent.TryGetValue(resolvedParentAddress, out List<SceneNodeDescription>? children))
                {
                    children = new List<SceneNodeDescription>();
                    syntheticChildrenByParent.Add(resolvedParentAddress, children);
                }

                children.Add(arrayNode);
                continue;
            }

            syntheticRoots.Add(arrayNode);
        }

        return syntheticRoots;
    }

    private static List<SceneNodeDescription> BuildSharedGimmickNodes(
        string sourceFilePath,
        AssetPathResolver pathResolver,
        IReadOnlyList<FoxEntity> entities,
        IReadOnlyDictionary<ulong, FoxEntity> entityLookup)
    {
        List<SceneNodeDescription> syntheticRoots = new();

        foreach (FoxEntity entity in entities)
        {
            if (!entity.ClassName.Equals("TppSharedGimmickData", StringComparison.Ordinal))
            {
                continue;
            }

            SceneNodeDescription? gimmickNode = BuildSharedGimmickNode(sourceFilePath, pathResolver, entity, entityLookup);
            if (gimmickNode is not null)
            {
                syntheticRoots.Add(gimmickNode);
            }
        }

        return syntheticRoots;
    }

    private static Dictionary<ulong, IReadOnlyDictionary<int, string>> BuildStaticModelArrayInstanceNames(IReadOnlyList<FoxEntity> entities)
    {
        Dictionary<ulong, Dictionary<int, string>> namesByArrayAddress = new();

        foreach (FoxEntity entity in entities)
        {
            if (!entity.ClassName.Equals("StaticModelArrayLinkTarget", StringComparison.Ordinal))
            {
                continue;
            }

            ulong? arrayAddress = entity.TryGetEntityReference("staticModelArray");
            int? arrayIndex = TryGetIntProperty(entity, "arrayIndex");
            if (arrayAddress is null || arrayIndex is null)
            {
                continue;
            }

            if (!namesByArrayAddress.TryGetValue(arrayAddress.Value, out Dictionary<int, string>? namesByIndex))
            {
                namesByIndex = new Dictionary<int, string>();
                namesByArrayAddress.Add(arrayAddress.Value, namesByIndex);
            }

            namesByIndex[arrayIndex.Value] = entity.DisplayName;
        }

        return namesByArrayAddress.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyDictionary<int, string>)pair.Value,
            EqualityComparer<ulong>.Default);
    }

    private static SceneNodeDescription? BuildStaticModelArrayNode(
        FoxEntity entity,
        IReadOnlyDictionary<ulong, FoxEntity> entityLookup,
        IReadOnlyDictionary<int, string>? instanceNames)
    {
        string? modelFilePath = entity.TryGetStringProperty("modelFile");
        List<SceneNodeDescription> instanceNodes = GetMatrixListProperty(entity, "transforms")
            .Select((matrix, index) => BuildStaticModelArrayInstanceNode(entity, modelFilePath, entity.TryGetStringProperty("geomFile"), matrix, index, instanceNames))
            .Where(node => node is not null)
            .Cast<SceneNodeDescription>()
            .ToList();

        if (instanceNodes.Count == 0)
        {
            return null;
        }

        return new SceneNodeDescription
        {
            Address = FoxFormatting.FormatAddress(entity.Address),
            ClassName = entity.ClassName,
            Name = entity.DisplayName,
            Properties = BuildNodeProperties(entity, entityLookup, StaticModelArrayContainerExcludedProperties),
            Children = instanceNodes,
        };
    }

    private static SceneNodeDescription? BuildStaticModelArrayInstanceNode(
        FoxEntity entity,
        string? modelFilePath,
        string? geomFilePath,
        FoxMatrixValue matrix,
        int index,
        IReadOnlyDictionary<int, string>? instanceNames)
    {
        SceneNodeTransformDescription? transform = BuildTransformFromMatrix(matrix);
        if (transform is null)
        {
            return null;
        }

        Dictionary<string, object?> properties = new(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(modelFilePath))
        {
            properties["modelFile"] = modelFilePath;
        }

        if (!string.IsNullOrWhiteSpace(geomFilePath))
        {
            properties["geomFile"] = geomFilePath;
        }

        properties["arrayIndex"] = index;
        properties["sourceStaticModelArray"] = entity.DisplayName;

        return new SceneNodeDescription
        {
            Address = $"{FoxFormatting.FormatAddress(entity.Address)}:instance:{index}",
            ClassName = $"{entity.ClassName}Instance",
            Name = instanceNames?.GetValueOrDefault(index) ?? $"{entity.DisplayName}_{index:D4}",
            Transform = transform,
            Properties = properties,
        };
    }

    private static SceneNodeDescription? BuildSharedGimmickNode(
        string sourceFilePath,
        AssetPathResolver pathResolver,
        FoxEntity entity,
        IReadOnlyDictionary<ulong, FoxEntity> entityLookup)
    {
        string? modelFilePath = entity.TryGetStringProperty("modelFile");
        string? locatorFilePath = entity.TryGetStringProperty("locaterFile");
        if (string.IsNullOrWhiteSpace(modelFilePath) || string.IsNullOrWhiteSpace(locatorFilePath))
        {
            return null;
        }

        string? resolvedLocatorFilePath = pathResolver.TryResolveToFilePath(locatorFilePath, sourceFilePath);
        if (resolvedLocatorFilePath is null || !File.Exists(resolvedLocatorFilePath))
        {
            return null;
        }

        List<LbaPlacement> placements = ReadLbaPlacements(resolvedLocatorFilePath);
        if (placements.Count == 0)
        {
            return null;
        }

        List<SceneNodeDescription> children = placements
            .Select((placement, index) => BuildSharedGimmickInstanceNode(entity, modelFilePath, entity.TryGetStringProperty("geomFile"), placement, index))
            .ToList();

        return new SceneNodeDescription
        {
            Address = FoxFormatting.FormatAddress(entity.Address),
            ClassName = entity.ClassName,
            Name = entity.DisplayName,
            Properties = BuildNodeProperties(entity, entityLookup, SharedGimmickContainerExcludedProperties),
            Children = children,
        };
    }

    private static SceneNodeDescription BuildSharedGimmickInstanceNode(
        FoxEntity entity,
        string modelFilePath,
        string? geomFilePath,
        LbaPlacement placement,
        int index)
    {
        Dictionary<string, object?> properties = new(StringComparer.Ordinal)
        {
            ["modelFile"] = modelFilePath,
            ["sourceSharedGimmick"] = entity.DisplayName,
            ["lbaIndex"] = index,
        };

        if (!string.IsNullOrWhiteSpace(geomFilePath))
        {
            properties["geomFile"] = geomFilePath;
        }

        return new SceneNodeDescription
        {
            Address = $"{FoxFormatting.FormatAddress(entity.Address)}:lba:{index}",
            ClassName = $"{entity.ClassName}Instance",
            Name = $"{entity.DisplayName}_{index:D4}",
            Transform = placement.Transform,
            Properties = properties,
        };
    }

    private static List<FoxMatrixValue> GetMatrixListProperty(FoxEntity entity, string propertyName)
    {
        if (entity.FindProperty(propertyName) is not FoxProperty property)
        {
            return [];
        }

        return property.Value switch
        {
            FoxListValue listValue => listValue.Items.OfType<FoxMatrixValue>().Where(value => value.Values.Count == 16).ToList(),
            FoxSingleValue { Value: FoxMatrixValue matrixValue } when matrixValue.Values.Count == 16 => [matrixValue],
            _ => [],
        };
    }

    private static SceneNodeTransformDescription? BuildTransformFromMatrix(FoxMatrixValue matrix)
    {
        if (matrix.Values.Count != 16)
        {
            return null;
        }

        Matrix4x4 sourceMatrix = new(
            matrix.Values[0], matrix.Values[1], matrix.Values[2], matrix.Values[3],
            matrix.Values[4], matrix.Values[5], matrix.Values[6], matrix.Values[7],
            matrix.Values[8], matrix.Values[9], matrix.Values[10], matrix.Values[11],
            matrix.Values[12], matrix.Values[13], matrix.Values[14], matrix.Values[15]);

        Vector3 translation = new(sourceMatrix.M41, sourceMatrix.M42, sourceMatrix.M43);
        Quaternion rotation = Quaternion.Identity;
        Vector3 scale = Vector3.One;

        if (!Matrix4x4.Decompose(sourceMatrix, out scale, out rotation, out translation))
        {
            rotation = Quaternion.Identity;
            scale = Vector3.One;
        }

        return new SceneNodeTransformDescription
        {
            Translation = new Dictionary<string, float>
            {
                ["x"] = translation.X,
                ["y"] = translation.Y,
                ["z"] = translation.Z,
            },
            RotationQuaternion = new Dictionary<string, float>
            {
                ["x"] = rotation.X,
                ["y"] = rotation.Y,
                ["z"] = rotation.Z,
                ["w"] = rotation.W,
            },
            Scale = new Dictionary<string, float>
            {
                ["x"] = scale.X,
                ["y"] = scale.Y,
                ["z"] = scale.Z,
            },
        };
    }

    private static List<LbaPlacement> ReadLbaPlacements(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        if (data.Length < 16)
        {
            return [];
        }

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, 4));
        int requiredLength = checked(16 + (int)count * 32);
        if (count == 0 || data.Length < requiredLength)
        {
            return [];
        }

        List<LbaPlacement> placements = new((int)count);
        for (int index = 0; index < count; index++)
        {
            int offset = 16 + index * 32;
            SceneNodeTransformDescription transform = new()
            {
                Translation = new Dictionary<string, float>
                {
                    ["x"] = BitConverter.ToSingle(data, offset),
                    ["y"] = BitConverter.ToSingle(data, offset + 4),
                    ["z"] = BitConverter.ToSingle(data, offset + 8),
                },
                RotationQuaternion = new Dictionary<string, float>
                {
                    ["x"] = BitConverter.ToSingle(data, offset + 16),
                    ["y"] = BitConverter.ToSingle(data, offset + 20),
                    ["z"] = BitConverter.ToSingle(data, offset + 24),
                    ["w"] = BitConverter.ToSingle(data, offset + 28),
                },
            };

            placements.Add(new LbaPlacement(index, transform));
        }

        return placements;
    }

    private static ulong? TryGetLinkedEntityAddress(FoxEntity entity, string propertyName)
    {
        if (entity.FindProperty(propertyName) is not FoxProperty property)
        {
            return null;
        }

        return property.Value switch
        {
            FoxSingleValue { Value: FoxEntityReferenceValue entityReference } when entityReference.Address != 0 => entityReference.Address,
            FoxSingleValue { Value: FoxEntityLinkValue entityLink } when entityLink.Address != 0 => entityLink.Address,
            _ => null,
        };
    }

    private static int? TryGetIntProperty(FoxEntity entity, string propertyName)
    {
        if (entity.FindProperty(propertyName) is not FoxProperty property)
        {
            return null;
        }

        return property.Value switch
        {
            FoxSingleValue { Value: FoxScalarValue { Value: sbyte value } } => value,
            FoxSingleValue { Value: FoxScalarValue { Value: byte value } } => value,
            FoxSingleValue { Value: FoxScalarValue { Value: short value } } => value,
            FoxSingleValue { Value: FoxScalarValue { Value: ushort value } } => value,
            FoxSingleValue { Value: FoxScalarValue { Value: int value } } => value,
            FoxSingleValue { Value: FoxScalarValue { Value: uint value } } when value <= int.MaxValue => (int)value,
            _ => null,
        };
    }

    private static SceneNodeTransformDescription? BuildTransform(FoxEntity entity, IReadOnlyDictionary<ulong, FoxEntity> entityLookup)
    {
        ulong? transformAddress = entity.TryGetEntityReference("transform");
        if (transformAddress is null || !entityLookup.TryGetValue(transformAddress.Value, out FoxEntity? transformEntity))
        {
            return null;
        }

        SceneNodeTransformDescription transform = new()
        {
            Translation = GetVector3(transformEntity, "translation") ?? GetVector3(transformEntity, "transform_translation"),
            RotationQuaternion = GetVector4(transformEntity, "rotQuat") ?? GetVector4(transformEntity, "transform_rotation_quat"),
            Scale = GetVector3(transformEntity, "scale") ?? GetVector3(transformEntity, "transform_scale"),
        };

        ulong? shearAddress = entity.TryGetEntityReference("shearTransform");
        if (shearAddress is ulong resolvedShearAddress && entityLookup.TryGetValue(resolvedShearAddress, out FoxEntity? shearEntity))
        {
            transform.Shear = GetVector3(shearEntity, "shear") ?? GetVector3(shearEntity, "shearTransform_shear");
        }

        ulong? pivotAddress = entity.TryGetEntityReference("pivotTransform");
        if (pivotAddress is ulong resolvedPivotAddress && entityLookup.TryGetValue(resolvedPivotAddress, out FoxEntity? pivotEntity))
        {
            transform.Pivot = GetVector3(pivotEntity, "pivot") ?? GetVector3(pivotEntity, "pivotTransform_pivot");
            transform.PivotTranslation = GetVector3(pivotEntity, "pivotTranslation") ?? GetVector3(pivotEntity, "pivotTransform_pivotTranslation");
        }

        if (transform.Translation is null &&
            transform.RotationQuaternion is null &&
            transform.Scale is null &&
            transform.Shear is null &&
            transform.Pivot is null &&
            transform.PivotTranslation is null)
        {
            return null;
        }

        return transform;
    }

    private static Dictionary<string, object?>? BuildNodeProperties(
        FoxEntity entity,
        IReadOnlyDictionary<ulong, FoxEntity> entityLookup,
        IReadOnlySet<string>? excludedProperties = null)
    {
        Dictionary<string, object?> properties = new(StringComparer.Ordinal);
        foreach (FoxProperty property in entity.Properties)
        {
            if (StructuralNodePropertyNames.Contains(property.Name) ||
                (excludedProperties is not null && excludedProperties.Contains(property.Name)))
            {
                continue;
            }

            properties[property.Name] = FoxValueJsonConverter.ToJsonObject(property.Value, entityLookup);
        }

        return properties.Count == 0 ? null : properties;
    }

    private static Dictionary<string, float>? GetVector3(FoxEntity entity, string propertyName)
    {
        if (entity.FindProperty(propertyName) is not FoxProperty property)
        {
            return null;
        }

        return property.Value switch
        {
            FoxSingleValue { Value: FoxVector3Value vector3 } => new Dictionary<string, float>
            {
                ["x"] = vector3.X,
                ["y"] = vector3.Y,
                ["z"] = vector3.Z,
            },
            _ => null,
        };
    }

    private static Dictionary<string, float>? GetVector4(FoxEntity entity, string propertyName)
    {
        if (entity.FindProperty(propertyName) is not FoxProperty property)
        {
            return null;
        }

        return property.Value switch
        {
            FoxSingleValue { Value: FoxQuaternionValue quaternion } => new Dictionary<string, float>
            {
                ["x"] = quaternion.X,
                ["y"] = quaternion.Y,
                ["z"] = quaternion.Z,
                ["w"] = quaternion.W,
            },
            FoxSingleValue { Value: FoxVector4Value vector4 } => new Dictionary<string, float>
            {
                ["x"] = vector4.X,
                ["y"] = vector4.Y,
                ["z"] = vector4.Z,
                ["w"] = vector4.W,
            },
            _ => null,
        };
    }

    private static CompactSceneNodeDescription? BuildCompactNode(SceneNodeDescription node)
    {
        List<CompactSceneNodeDescription>? children = node.Children?
            .Select(BuildCompactNode)
            .Where(child => child is not null)
            .Cast<CompactSceneNodeDescription>()
            .ToList();
        List<string> fmdlPaths = ExtractCompactFmdlPaths(node.Properties);

        if (node.Transform is null && fmdlPaths.Count == 0 && (children is null || children.Count == 0))
        {
            return null;
        }

        return new CompactSceneNodeDescription
        {
            ClassName = node.ClassName,
            Name = node.Name,
            Transform = node.Transform,
            FmdlPaths = fmdlPaths.Count == 0 ? null : fmdlPaths,
            Properties = node.Properties is { Count: > 0 } ? node.Properties : null,
            Children = children is { Count: > 0 } ? children : null,
        };
    }

    private static List<string> ExtractCompactFmdlPaths(Dictionary<string, object?>? properties)
    {
        if (properties is null)
        {
            return [];
        }

        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        foreach (object? value in properties.Values)
        {
            CollectFmdlPaths(value, paths);
        }

        return paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void CollectFmdlPaths(object? value, HashSet<string> paths)
    {
        switch (value)
        {
            case null:
                return;
            case string text:
                foreach (string path in AssetPathScanner.ExtractPathsFromText(text).Where(AssetPathScanner.IsTerminalModelPath))
                {
                    paths.Add(path);
                }
                return;
            case IDictionary<string, object?> dictionary:
                foreach (object? item in dictionary.Values)
                {
                    CollectFmdlPaths(item, paths);
                }
                return;
            case IDictionary<string, float>:
                return;
            case IEnumerable<object?> sequence:
                foreach (object? item in sequence)
                {
                    CollectFmdlPaths(item, paths);
                }
                return;
        }
    }

    private static int CountNodes(IEnumerable<SceneNodeDescription> nodes)
    {
        int count = 0;
        foreach (SceneNodeDescription node in nodes)
        {
            count++;
            if (node.Children is not null)
            {
                count += CountNodes(node.Children);
            }
        }

        return count;
    }

    private static int CountCompactNodes(IEnumerable<CompactSceneNodeDescription> nodes)
    {
        int count = 0;
        foreach (CompactSceneNodeDescription node in nodes)
        {
            count++;
            if (node.Children is not null)
            {
                count += CountCompactNodes(node.Children);
            }
        }

        return count;
    }

    private sealed record DiscoveryWorkItem(string FilePath, string? DiscoveredFrom, string DiscoveryHint, int Depth, string? OwningFox2Path);

    private sealed class FileReferenceDescriptionComparer : IEqualityComparer<FileReferenceDescription>
    {
        public static FileReferenceDescriptionComparer Instance { get; } = new();

        public bool Equals(FileReferenceDescription? x, FileReferenceDescription? y)
        {
            return x is not null &&
                   y is not null &&
                   x.Path.Equals(y.Path, StringComparison.OrdinalIgnoreCase) &&
                   x.Source.Equals(y.Source, StringComparison.Ordinal);
        }

        public int GetHashCode(FileReferenceDescription obj)
        {
            return HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Path),
                StringComparer.Ordinal.GetHashCode(obj.Source));
        }
    }
}

internal sealed class AssetPathResolver
{
    private AssetPathResolver(string? assetRootPath)
    {
        AssetRootPath = assetRootPath;
    }

    public string? AssetRootPath { get; }

    public static AssetPathResolver Create(string inputPath, string? explicitAssetRootPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitAssetRootPath))
        {
            return new AssetPathResolver(Path.GetFullPath(explicitAssetRootPath));
        }

        DirectoryInfo? directory = new FileInfo(inputPath).Directory;
        while (directory is not null)
        {
            if (directory.Name.Equals("Assets", StringComparison.OrdinalIgnoreCase))
            {
                return new AssetPathResolver(directory.Parent?.FullName);
            }

            directory = directory.Parent;
        }

        return new AssetPathResolver(null);
    }

    public string ToDisplayPath(string filePath)
    {
        string normalizedFilePath = Path.GetFullPath(filePath);
        if (AssetRootPath is null)
        {
            return normalizedFilePath;
        }

        string assetRoot = Path.GetFullPath(AssetRootPath);
        if (!normalizedFilePath.StartsWith(assetRoot, StringComparison.OrdinalIgnoreCase))
        {
            return normalizedFilePath;
        }

        string relativePath = Path.GetRelativePath(assetRoot, normalizedFilePath).Replace('\\', '/');
        return "/" + relativePath;
    }

    public string? TryResolveToFilePath(string reference, string currentFilePath)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        if (reference.StartsWith("/Assets/", StringComparison.OrdinalIgnoreCase) ||
            reference.StartsWith("Assets\\", StringComparison.OrdinalIgnoreCase) ||
            reference.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
        {
            if (AssetRootPath is null)
            {
                return null;
            }

            string relativeReference = reference.TrimStart('/').Replace('/', '\\');
            return Path.GetFullPath(Path.Combine(AssetRootPath, relativeReference));
        }

        string normalizedReference = reference.Replace('/', '\\');
        if (Path.IsPathRooted(normalizedReference))
        {
            return Path.GetFullPath(normalizedReference);
        }

        string currentDirectory = Path.GetDirectoryName(currentFilePath) ?? Directory.GetCurrentDirectory();
        return Path.GetFullPath(Path.Combine(currentDirectory, normalizedReference));
    }
}

internal sealed class SceneDescription
{
    public required string SourceFile { get; init; }
    public string? SourceAssetPath { get; init; }
    public string? AssetRootPath { get; init; }
    public required SceneSummary Summary { get; init; }
    public List<SceneFileDescription> Files { get; init; } = [];
    public List<FmdlReferenceDescription> FmdlFiles { get; init; } = [];
    internal Dictionary<string, List<string>> Fox2FmdlFiles { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class SceneSummary
{
    public int FilesVisited { get; init; }
    public int Fox2Files { get; init; }
    public int ArchiveFiles { get; init; }
    public int EntityCount { get; init; }
    public int NodeCount { get; init; }
    public int FmdlCount { get; init; }
}

internal sealed class SceneFileDescription
{
    public required string Path { get; init; }
    public required string Kind { get; init; }
    public string? DiscoveredFrom { get; init; }
    public string? DiscoveryHint { get; init; }
    public List<FileReferenceDescription> References { get; init; } = [];
    public List<SceneNodeDescription>? RootNodes { get; init; }
    public List<EntityDescription>? Entities { get; init; }
}

internal sealed class FileReferenceDescription
{
    public required string Path { get; init; }
    public string? ResolvedPath { get; init; }
    public required string ReferenceType { get; init; }
    public required string Source { get; init; }
}

internal sealed class FmdlReferenceDescription
{
    public required string Path { get; init; }
    public string? ResolvedPath { get; init; }
    public List<string> Sources { get; init; } = [];
}

internal sealed class SceneNodeDescription
{
    public required string Address { get; init; }
    public required string ClassName { get; init; }
    public string? Name { get; init; }
    public SceneNodeTransformDescription? Transform { get; init; }
    public Dictionary<string, object?>? Properties { get; init; }
    public List<SceneNodeDescription>? Children { get; init; }
}

internal sealed class SceneNodeTransformDescription
{
    public Dictionary<string, float>? Translation { get; set; }
    public Dictionary<string, float>? RotationQuaternion { get; set; }
    public Dictionary<string, float>? Scale { get; set; }
    public Dictionary<string, float>? Shear { get; set; }
    public Dictionary<string, float>? Pivot { get; set; }
    public Dictionary<string, float>? PivotTranslation { get; set; }
}

internal sealed class EntityDescription
{
    public required string Address { get; init; }
    public required string ClassName { get; init; }
    public string? Name { get; init; }
    public Dictionary<string, object?> Properties { get; init; } = [];
}

internal sealed class CompactSceneDescription
{
    public required string SourceFile { get; init; }
    public string? SourceAssetPath { get; init; }
    public string? AssetRootPath { get; init; }
    public required CompactSceneSummary Summary { get; init; }
    public List<CompactSceneFileDescription> Files { get; init; } = [];
    public List<string> FmdlFiles { get; init; } = [];
}

internal sealed class CompactSceneSummary
{
    public int Fox2FileCount { get; init; }
    public int NodeCount { get; init; }
    public int FmdlCount { get; init; }
}

internal sealed class CompactSceneFileDescription
{
    public required string Path { get; init; }
    public string? DiscoveredFrom { get; init; }
    public List<string>? FmdlFiles { get; init; }
    public List<CompactSceneNodeDescription>? RootNodes { get; init; }
}

internal sealed class CompactSceneNodeDescription
{
    public string? ClassName { get; init; }
    public string? Name { get; init; }
    public SceneNodeTransformDescription? Transform { get; init; }
    public List<string>? FmdlPaths { get; init; }
    public Dictionary<string, object?>? Properties { get; init; }
    public List<CompactSceneNodeDescription>? Children { get; init; }
}

internal sealed record LbaPlacement(int Index, SceneNodeTransformDescription Transform);
