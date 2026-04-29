# FMDL Studio v2
Fox Engine model importer and exporter for Unity.

## Usage
See the [GitHub Wiki](https://github.com/BobDoleOwndU/FMDL-Studio-v2/wiki) for information on setting up and using Fmdl Studio v2.

## Standalone glTF/GLB converter
This repository now also includes a standalone .NET 8 converter at `tools\FmdlGltfConverter` that reads `.fmdl` files directly and writes `.gltf` or `.glb` without Unity.

### Build
```powershell
dotnet build .\tools\FmdlGltfConverter\FmdlGltfConverter.csproj
```

### Run
```powershell
dotnet run --project .\tools\FmdlGltfConverter\FmdlGltfConverter.csproj -- .\model.fmdl .\model.glb
```

If you only pass `.fmdl` path arguments, the converter writes same-name `.glb` files beside the input files. This also makes drag-and-drop onto the built `.exe` work naturally on Windows, including dropping multiple files at once:

```powershell
dotnet run --project .\tools\FmdlGltfConverter\FmdlGltfConverter.csproj -- .\model.fmdl
dotnet run --project .\tools\FmdlGltfConverter\FmdlGltfConverter.csproj -- .\model_0.fmdl .\model_1.fmdl .\model_2.fmdl
```

Optional dictionary arguments let the converter resolve MGSV hash-based names through the dictionaries already shipped in this repository:

```powershell
dotnet run --project .\tools\FmdlGltfConverter\FmdlGltfConverter.csproj -- .\model.fmdl .\model.gltf --string-dict ".\FMDL-Studio-v2\Assets\Fmdl Studio\fmdl_dictionary.txt" --path-dict ".\FMDL-Studio-v2\Assets\Fmdl Studio\qar_dictionary.txt"
```

### Current output
- Meshes, triangle indices, normals, tangents, UV0-UV3
- Bone hierarchy, skinning, inverse bind matrices
- glTF PBR materials with embedded textures for:
  - `Base_Tex_SRGB` -> base color
  - `NormalMap_Tex_NRM` -> normal (Fox `HNM` alpha/green packing is converted to standard glTF tangent-space RGB)
  - `SpecularMap_Tex_LIN` -> roughness only (`G=roughness`; metallic/specular masks are not currently exported)
- Material names, Fox shader names, texture slots, and vector parameters in glTF `extras`

### Current limitations
- Texture embedding currently targets the common Fox texture paths used by the sampled static assets and decodes `.ftex` / `.ftexs`, `.dds`, and common image files into PNG payloads for glTF/GLB.
- Output is currently focused on `.fmdl` geometry and skinning data; Unity-specific editor workflows remain unchanged.

## Standalone Fox2 scene JSON converter
This repository also includes a standalone .NET 8 converter at `tools\Fox2SceneConverter` that reads `.fox2` scene files, reconstructs readable entity and node-tree data, and recursively follows common indirect references (`.fstb`, `.fpk`, `.fpkd`) to discover downstream `.fmdl` files.

### Build
```powershell
dotnet build .\tools\Fox2SceneConverter\Fox2SceneConverter.csproj
```

### Run
```powershell
dotnet run --project .\tools\Fox2SceneConverter\Fox2SceneConverter.csproj -- .\scene.fox2
```

If you only pass the `.fox2` input path, the converter writes both `<input>.scene.json` and `<input>.scene.compact.json` beside the source file. When you pass an explicit output path such as `scene.json`, the compact companion file is written as `scene.compact.json`.

Use `--asset-root` when the input file is under an extracted MGSV root and you want `/Assets/...` references to resolve on disk for recursive discovery:

```powershell
dotnet run --project .\tools\Fox2SceneConverter\Fox2SceneConverter.csproj -- `
  "D:\BOTW\MGSV\Root\Assets\tpp\level\location\mbqf\mbqf_stage.fox2" `
  ".\mbqf_stage.scene.json" `
  --asset-root "D:\BOTW\MGSV\Root"
```

### Output
- JSON summary with visited files, entity count, node count, and discovered `.fmdl` count
- Per-file reference lists for `.fox2`, `.fstb`, `.fpk`, and `.fpkd`
- Reconstructed node trees from Fox transform relationships when present
- A deduplicated `fmdlFiles` list with provenance showing where each model path was discovered
- A compact companion JSON for fast scene loading that keeps only transform tree data plus discovered `.fmdl` paths per fox2 file and per node when available

### Current findings
- The sampled `mbqf_stage.fox2` file behaves like a stage/block entry file rather than a direct model list.
- In the sample chain, `mbqf_stage.fox2` points to `mbqf_common_packages.fstb`, which points to `mbqf_common.fpk`; the pack then exposes the downstream asset references, including `.fox2` and `.fmdl`.
- Running the converter on that sample with `--asset-root "D:\BOTW\MGSV\Root"` currently walks 5 files, reconstructs 2222 entities, produces 763 scene nodes in the full JSON, and discovers 210 `.fmdl` paths.
- The compact JSON is intended for Blender-side fast loading: it keeps per-fox2 `fmdlFiles`, node transform trees, and node-level `fmdlPaths` when a node directly carries a discoverable model path.

### Current limitations
- Exact node-to-model binding is best-effort. Many useful `.fmdl` paths are discovered through referenced fox2/archive files rather than a single explicit property on each node.
- Archive parsing is currently string-scan based for `.fstb`, `.fpk`, and `.fpkd`, which is good enough for discovery but not yet a full semantic parser for every Fox container format.

## Blender compact scene importer
For Blender-side scene assembly, the repository now includes `tools\Blender\import_fox2_scene.py`.

The script reads a `*.scene.compact.json` file, converts the required `.fmdl` files to cached `.glb` files through `FmdlGltfConverter`, imports each model once, and then duplicates the imported object trees to reconstruct the scene hierarchy using the recorded transforms.

### Run from Blender
```powershell
blender --python .\tools\Blender\import_fox2_scene.py -- `
  --scene-json "D:\BOTW\MGSV\Root\Assets\tpp\level\location\mbqf\mbqf_stage.scene.compact.json"
```

Useful optional arguments:
- `--asset-root <path>` to override the asset root from the compact JSON
- `--converter <path>` to point at an explicit `FmdlGltfConverter.exe` or `.dll`
- `--cache-dir <path>` to control where generated `.glb` files are stored
- `--rebuild-glb` to force reconversion even if cached `.glb` files already exist
- `--disable-name-fallback` to disable best-effort node-name-to-model matching when a node has no explicit `fmdlPaths`

### Import behavior
- Uses node-level `fmdlPaths` first when present in the compact JSON
- Falls back to best-effort node-name matching against the fox2 file's `fmdlFiles` list when direct node model paths are missing
- Preserves the compact scene transform hierarchy with Blender empties and attaches imported model instances under those nodes
- Reuses cached imported templates so repeated model placements do not re-run the converter

### Current limitations
- The Blender importer depends on Blender's Python API and is meant to run inside Blender, not plain CPython.
- When the compact JSON only provides file-level model discovery and no direct node-level binding, the importer uses name matching heuristics; these placements are useful for quick scene assembly but are not guaranteed to be exact.

## Compact scene glTF/GLB composer
For direct scene export without going through Blender, the repository also includes `tools\SceneGltfComposer`.

The composer reads a `*.scene.compact.json` file, converts each unique referenced `.fmdl` to a cached `.glb` through `FmdlGltfConverter`, merges those model assets into one final scene, and recreates the compact scene transform hierarchy as glTF nodes. Repeated placements of the same model reuse the same imported mesh/material/accessor data.

### Build
```powershell
dotnet build .\tools\SceneGltfComposer\SceneGltfComposer.csproj
```

### Run
```powershell
dotnet run --project .\tools\SceneGltfComposer\SceneGltfComposer.csproj -- `
  "D:\BOTW\MGSV\Root\Assets\tpp\level\location\mafr\block_large\lab\mafr_lab_asset_room.scene.compact.json" `
  ".\mafr_lab_asset_room.scene.glb" `
  --asset-root "D:\BOTW\MGSV\Root"
```

If you omit the output path, the composer writes `<input without .compact>.glb` beside the compact scene JSON. Use a `.gltf` output extension if you want a `.gltf + .bin` pair instead of a `.glb`.

Useful optional arguments:
- `--converter <path>` to point at an explicit `FmdlGltfConverter.exe` or `.dll`
- `--cache-dir <path>` to control where per-model cached `.glb` files are stored
- `--rebuild-models` to force reconversion even when cached `.glb` files already exist
- `--disable-name-fallback` to disable best-effort node-name-to-model matching when a node has no explicit `fmdlPaths`

### Composition behavior
- Uses node-level `fmdlPaths` first when present in the compact JSON
- Falls back to best-effort node-name matching against the fox2 file's `fmdlFiles` list when direct node model paths are missing
- Recreates the compact transform hierarchy as glTF nodes and instantiates the referenced model scenes under those nodes
- Imports each unique source model once, then reuses the merged mesh/material/buffer data for repeated placements

### Current limitations
- The output scene currently duplicates model node trees per placement; mesh/material/accessor data is shared, but node hierarchies themselves are not deduplicated because vanilla glTF has no generic scene-instancing primitive.
- Placement quality still depends on the compact JSON. When only file-level model discovery is available, the name fallback is heuristic and not guaranteed to bind the exact intended `.fmdl`.
- The current implementation expects cached source models as `.glb` produced by this repository's `FmdlGltfConverter`.

## Credits
BobDoleOwndU: Programming and reverse-engineering.

Joey35233: Programming.

youarebritish (sai): General help/debugging.

Tex: Testing.

Highflex: Reverse-engineering.

revel8n: Reverse-engineering.

Thanks to Jayveerk, cra0kalo and HeartlessSeph for their previous work on the .fmdl format.
