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

Useful profiling / preview options:
- `--profile <csv>` writes per-model parse/export timing rows
- `--texture-profile <csv>` writes per-texture resolve, cache, FTEX/DDS decode, pixel transform, texture encode, and GLB embed timing rows
- `--no-textures` skips texture transcoding and embedding for fast geometry-only preview GLBs
- `--texture-cache-dir <path>` caches transcoded texture payloads and the hashed texture index across converter runs
- `--texture-format <png|webp-lossless|webp-lossy>` chooses the embedded texture container; WebP uses `EXT_texture_webp`
- `--fast-png` uses faster PNG settings for iteration; output GLBs are larger
- `--webp-quality <0-100>` and `--webp-method <0-6>` tune WebP size/speed

### Current output
- Meshes, triangle indices, normals, tangents, UV0-UV3
- Bone hierarchy, skinning, inverse bind matrices
- glTF PBR materials with embedded textures for:
  - `Base_Tex_SRGB` -> base color
  - `NormalMap_Tex_NRM` -> normal (Fox `HNM` alpha/green packing is converted to standard glTF tangent-space RGB)
  - `SpecularMap_Tex_LIN` -> roughness only (`G=roughness`; metallic/specular masks are not currently exported)
- Material names, Fox shader names, texture slots, and vector parameters in glTF `extras`

### Current limitations
- Texture embedding currently targets the common Fox texture paths used by the sampled static assets and decodes `.ftex` / `.ftexs`, `.dds`, and common image files into PNG or WebP payloads for glTF/GLB.
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

## Portable foxscene package and Web viewer
For very large scenes, a single merged GLB is often the wrong final artifact. The repository now includes `tools\FoxScenePackager`, which turns a compact FOX2 scene into a portable `*.foxscene.json` scene manifest plus a directory of independent per-model GLBs.

The manifest is intended as the stable interchange layer:
- `assets[]` maps stable asset IDs to original `.fmdl` paths and generated GLB URIs
- `layers[]` keeps the source FOX2 file boundaries
- `nodes[]` stores the scene hierarchy, transforms, node metadata, and asset bindings
- model data remains in separate GLBs, so Blender, a Web viewer, or another engine can instantiate the same assets without rebuilding one giant scene file

Scene packages default to WebP lossy model textures at quality `90` / method `0` to keep GLB size practical for Web delivery. Pass `--texture-format png` when a downstream tool needs only core glTF texture formats.

### Build
```powershell
dotnet build .\tools\FoxScenePackager\FoxScenePackager.csproj
```

### Run
```powershell
dotnet run --project .\tools\FoxScenePackager\FoxScenePackager.csproj -- `
  "D:\BOTW\MGSV\Root\Assets\tpp\level\location\mbqf\mbqf_stage.scene.compact.json" `
  "D:\BOTW\MGSV\Root\Assets\tpp\level\location\mbqf\mbqf_stage.foxscene.json" `
  --asset-root "D:\BOTW\MGSV\Root"
```

Useful optional arguments:
- `--models-dir <path>` to control where generated per-model GLBs are written
- `--rebuild-models` to force reconversion of per-model GLBs
- `--no-convert` to write only the scene manifest with expected GLB URIs
- `--placed-only` to omit discovered-but-unplaced model assets from the manifest
- `--disable-name-fallback` to disable best-effort node-name matching
- `--allow-missing-assets` to continue writing the manifest when source FMDLs are missing
- `--jobs <n>` to convert multiple independent model GLBs in parallel
- `--no-model-textures` to pass `--no-textures` to `FmdlGltfConverter` for fast geometry-only scene packages
- `--texture-cache-dir <path>` to pass a shared texture cache to `FmdlGltfConverter`
- `--texture-format <png|webp-lossless|webp-lossy>` to override the scene package texture format
- `--fast-png` to trade larger GLBs for faster texture encode during iteration
- `--webp-quality <0-100>` and `--webp-method <0-6>` to tune WebP size/speed

### Web demo
The static viewer in `tools\FoxSceneViewer` loads `*.foxscene.json` and reconstructs the scene with Three.js by loading the independent GLBs referenced by the manifest.

```powershell
python -m http.server 18080
```

Open:

```text
http://127.0.0.1:18080/tools/FoxSceneViewer/index.html
```

To load a generated scene package:

```text
http://127.0.0.1:18080/tools/FoxSceneViewer/index.html?scene=/Assets/tpp/level/location/mbqf/mbqf_stage.foxscene.json
```

Relative GLB URIs are resolved from the scene JSON URL, so serve the generated `*.foxscene.json` and its sibling model directory from the same HTTP root.

Large packages are opened incrementally. If the manifest references more than 300 unique assets, the viewer loads the JSON and layer list first, then waits for an explicit layer selection. Useful URL parameters:

- `autoload=none` loads only the manifest and layer controls.
- `layers=<name-or-id-substring>` loads matching layers after the manifest is ready.
- `autoload=all` forces every layer to load and should be reserved for small packages.

For a package outside the repository, serve a common filesystem root. Example for the exported AFGH package:

```powershell
python -m http.server 18081 --bind 127.0.0.1 --directory D:\
```

```text
http://127.0.0.1:18081/github/FMDL-Studio-v2/tools/FoxSceneViewer/index.html?scene=/BOTW/MGSV/EXPORTED/Assets/tpp/level/location/afgh/afgh_stage.foxscene.json&layers=afgh_village_asset
```

Current limitations:
- `FoxScenePackager` packages FMDL-backed model placements first. TerrainBlock metadata is preserved on nodes, but `.htre` terrain is not yet emitted as independent terrain GLBs.
- Name fallback remains heuristic when the compact scene lacks direct node-level `fmdlPaths`.

## Credits
BobDoleOwndU: Programming and reverse-engineering.

Joey35233: Programming.

youarebritish (sai): General help/debugging.

Tex: Testing.

Highflex: Reverse-engineering.

revel8n: Reverse-engineering.

Thanks to Jayveerk, cra0kalo and HeartlessSeph for their previous work on the .fmdl format.
