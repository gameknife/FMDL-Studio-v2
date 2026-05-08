# Scene export notes

## Purpose

This note captures the current working knowledge for exporting MGSV FOX2 scenes in this repository, especially:

- the validated `Fox2SceneConverter -> SceneGltfComposer` workflow,
- the scene-analysis patterns that were useful when debugging missing content,
- and the layered-export strategy used for very large scenes like `afgh_stage`.

It is intended as a handoff document for future sessions.

## Current toolchain

The legacy merged-scene pipeline is:

1. `tools\Fox2SceneConverter`
   Reads a `.fox2` scene and writes:
   - `<scene>.scene.json`
   - `<scene>.scene.compact.json`

2. `tools\SceneGltfComposer`  
   Reads `.scene.compact.json` and writes the final `.glb`.

3. `tools\FmdlGltfConverter`  
   Used indirectly by `SceneGltfComposer` to convert referenced FMDLs into cached per-model GLBs.

The newer portable-scene pipeline is:

1. `tools\Fox2SceneConverter`
   Reads a `.fox2` scene and writes `<scene>.scene.compact.json`.

2. `tools\FoxScenePackager`
   Reads the compact scene and writes:
   - `<scene>.foxscene.json`
   - `<scene>.models\Assets\...\*.glb`

3. `tools\FoxSceneViewer` or another runtime
   Reads the foxscene JSON and instantiates independent GLBs from the asset manifest.

Validated command pattern:

```powershell
dotnet .\tools\Fox2SceneConverter\bin\Release\net8.0\Fox2SceneConverter.dll `
  'D:\BOTW\MGSV\EXPORTED\Assets\tpp\level\location\cypr\cypr_stage.fox2' `
  --asset-root 'D:\BOTW\MGSV\EXPORTED'

dotnet .\tools\SceneGltfComposer\bin\Release\net8.0\SceneGltfComposer.dll `
  'D:\BOTW\MGSV\EXPORTED\Assets\tpp\level\location\cypr\cypr_stage.scene.compact.json' `
  --asset-root 'D:\BOTW\MGSV\EXPORTED'
```

Portable scene command pattern:

```powershell
dotnet .\tools\FoxScenePackager\bin\Release\net8.0\FoxScenePackager.dll `
  'D:\BOTW\MGSV\EXPORTED\Assets\tpp\level\location\cypr\cypr_stage.scene.compact.json' `
  'D:\BOTW\MGSV\EXPORTED\Assets\tpp\level\location\cypr\cypr_stage.foxscene.json' `
  --asset-root 'D:\BOTW\MGSV\EXPORTED'
```

Profiling findings from `mbqf_stage`:

- FOX2 discovery / compact scene generation: about `2.6s`
- foxscene manifest generation without model conversion: about `2.3s`
- cached package rewrite with 190 existing GLBs: about `2.35s`
- textured model rebuild, old serial conversion: about `355s`
- textured model rebuild with `FoxScenePackager --jobs 4`: about `143.83s`
- textured model rebuild with `--jobs 4 --texture-cache-dir <path>` before hashed-index persistence:
  - cold shared PNG cache: about `98.12s`
  - warm shared PNG cache: about `82.39s`
- textured model rebuild with `--jobs 4 --texture-cache-dir <path>` after hashed-index persistence:
  - cold shared PNG cache: about `53.71s`
  - warm shared PNG cache: about `29.57s`
  - cache contents after MBQF: `498` PNG files, about `84.61MiB`, plus one hashed texture index file
- textured model rebuild with `--jobs 4 --texture-cache-dir <path> --texture-format webp-lossy --webp-quality 90 --webp-method 0`:
  - cold shared WebP cache: about `48.42s`
  - model GLBs total: `46,386,180` bytes, down from `261,084,160` bytes with PNG
  - cache contents after MBQF: `498` WebP files plus one hashed texture index file, about `17.22MiB`
- geometry-only model rebuild with `--jobs 4 --no-model-textures`: about `25.5s`

The dominant cost is texture transcoding and PNG embedding inside FMDL -> GLB conversion. In the largest MBQF model, parse time was about `15ms`; textured export was about `10.4s`; geometry-only export was about `0.11s`.

Detailed texture profiling on
`Assets\tpp\environ\object\mother_base\hospital\mtbs_hspt001\scenes\mtbs_hspt001_room003.fmdl`
shows the current bottleneck more precisely:

| Mode | Wall time | Output size | Texture rows | Texture-stage total | PNG encode | Notes |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| default PNG | `10.58s` | `18,392,708` bytes | `75` | `7.25s` | `6.35s` | encode dominates |
| `--fast-png` | `5.86s` | `21,893,656` bytes | `75` | `2.42s` | `1.35s` | about `19%` larger |
| `--texture-format webp-lossless --webp-method 0` | `11.70s` | `11,988,432` bytes | `75` | `9.10s` | `8.31s` | smaller but slower |
| `--texture-format webp-lossy --webp-quality 90 --webp-method 0` | `5.65s` | `2,511,980` bytes | `75` | `3.03s` | `2.19s` | chosen scene default |
| `--texture-format webp-lossy --webp-quality 85 --webp-method 0` | `5.23s` | `1,781,900` bytes | `75` | `2.66s` | `1.82s` | smaller, more quality risk |
| cold `--texture-cache-dir` | `11.91s` | `18,392,708` bytes | `75` | `8.31s` | `6.91s` | includes cache write |
| warm `--texture-cache-dir` | `3.33s` | `18,392,708` bytes | `75` | `0.02s` | `0s` | all texture payloads loaded from cache |
| warm PNG cache + warm hash index | `0.76s` | `18,392,708` bytes | `75` | `0.02s` | `0s` | path resolution falls to about `0.13s` |

For the default PNG run on that model, the non-encode texture costs were small by comparison:
`FTEX -> DDS` read/dechunk was about `0.25s`, DDS load/decode about `0.14s`,
RGBA conversion about `0.12s`, and Fox usage transforms about `0.24s`.

The warm-cache profile exposed a second texture-side cost: before the hashed texture index was persisted, each converter process could spend seconds scanning `Assets/**/sourceimages` to resolve hash-style texture references such as `1569c9906a24dc8f.dds`. The current resolver now checks hash-style references before broad candidate expansion and stores the hash index under `--texture-cache-dir`.

Practical interpretation:

- Use `--texture-cache-dir` by default for scene work. It preserves output size and avoids re-encoding duplicate textures across models and runs.
- For `FoxScenePackager`, use the current default `webp-lossy` at quality `90` / method `0`. It is much smaller than PNG and close to fast-PNG encode time on the MBQF sample.
- Add `--texture-format png --fast-png` only for compatibility iteration builds where larger GLBs are acceptable.
- Keep `--no-model-textures` for geometry/placement debugging.
- KTX2/Basis remains worth testing later for GPU-native texture delivery, but it needs an additional encoder/toolchain. WebP is the practical default with the current .NET pipeline.

## Important fixes already in this repo

These are already implemented in the current codebase and are easy to forget when debugging later:

- `StaticModelArray` content is expanded into scene nodes instead of being left only in entity data.
- redundant imported wrapper roots are flattened during scene composition.
- dangerous name fallback is suppressed for light/shared-gimmick semantic nodes.
- shared gimmick placement uses `.lba` locators.
- hashed texture lookup falls back to the asset tree when bundled dictionaries are incomplete.
- terrain support exists:
  - `TerrainBlock` nodes are preserved in compact scenes,
  - `.htre` terrain is converted to generated mesh geometry,
  - terrain sidecar files are written beside the scene output.
- scene composition now deduplicates imported GLB samplers, images, textures, and materials.
- portable foxscene export now exists as an alternative to monolithic scene GLB composition.
- LBA locator files with layout `3` are parsed as `translation + rotation + scale` records. Without this, `*_scl.lba` gimmick placements, such as cables in `afgh_148_130_asset`, are read with a 32-byte stride and every following instance is misplaced.

## How to read a scene quickly

For scene analysis, the most useful file is usually:

- `<scene>.scene.json` for raw discovery / entity / reference inspection
- `<scene>.scene.compact.json` for what the composer will actually consume

Fast checks that worked well:

1. look at `summary` first (`fox2FileCount`, `nodeCount`, `fmdlCount`)
2. inspect `files[*].path`, `files[*].references`, and `files[*].rootNodes`
3. inspect controller entities such as `StageBlockControllerData`
4. check whether missing content is:
   - absent from raw discovery,
   - present in `scene.json` but dropped from compact scene,
   - or present in compact scene but not composed correctly

Example warning sign:

- `afgh_luxury.fox2` exported to an almost empty scene because it only contained controller-type entities and no actual scene root nodes / FMDL-bearing content.

## Current discovery limitation to remember

`SceneDiscovery` currently auto-scans terrain using `pack_small` / `pack_large` paths, but it does **not** automatically enumerate all block layers such as:

- `pack_extraSmall`
- `block_extraLarge`
- mission overlay directories such as `block_mission2`

This matters for huge maps: some on-disk block layers can exist and be meaningful even if they are not currently pulled into the parsed scene graph automatically.

## afgh_stage structure

`afgh_stage.fox2` is controlled by `StageBlockControllerData` and divides the map into several layered block sets.

Key controller fields seen in `afgh_stage.scene.json`:

- `baseDirectoryPath = /Assets/tpp/pack/location/afgh/pack_small`
- `smallBlock1BaseDirectoryPath = /Assets/tpp/pack/location/afgh/pack_extraSmall`
- `stageBlockFile = /Assets/tpp/level/location/afgh/block_common/afgh_common_packages.fstb`
- `blockSizeX = 128`, `blockSizeZ = 128`
- `countX = 5`, `countZ = 5`
- `centerIndexX = 133`, `centerIndexZ = 133`

Practical interpretation of the block layers:

| Layer | Role | Typical pattern |
| --- | --- | --- |
| `block_common` | globally shared scene data | `afgh_common_*` |
| `block_small` | fine grid-based world tiles | `block_small\X\X_Z\afgh_X_Z_*` |
| `block_extraSmall` | even finer path-only layer | mostly `*_path.fox2` |
| `block_large` | named regional blocks / landmarks | `block_large\<area>\afgh_<area>_*` |
| `block_extraLarge` | coarse region-scale supplementation | `north`, `northEast`, `northWest`, `south` |
| `block_mission2` | mission gameplay overlays | `combat`, `item`, `route`, `trap`, `quiet`, `animal`, etc. |

Useful observed content patterns:

- `block_small` is highly regular and mostly contains `asset/effect/gimmick/light/nav/objBrush/path/terrain`
- `block_extraSmall` is almost entirely path data
- `block_large` is landmark-centric and carries the heavy visible scene content
- `block_mission2` is not base world geometry; it looks like per-mission overlay logic/content

## afgh_stage export status

Raw export stats from `afgh_stage.scene.compact.json`:

- `fox2FileCount = 4143`
- `nodeCount = 42322`
- `fmdlCount = 1653`

Portable foxscene package generated successfully:

- `D:\BOTW\MGSV\EXPORTED\Assets\tpp\level\location\afgh\afgh_stage.foxscene.json`
- `1297` independent GLBs under `afgh_stage.models`
- model GLBs total `2,727,511,120` bytes
- `4143` layers, `42322` nodes, `31472` placements, `0` package warnings
- largest GLB: `afgh_buld003.glb`, `42,378,712` bytes

Viewer validation:

- `autoload=none` loads the full AFGH manifest and layer list without materializing 42k Three.js nodes.
- `layers=afgh_147_130_asset` loaded `24` GLBs / `107` placements.
- `layers=afgh_village_asset` loaded `185` GLBs / `2479` placements and reached `Ready`.

Monolithic full-scene GLB composition still fails for `afgh_stage` because the final scene is too large and eventually trips `Stream was too long`.

That means:

- `Fox2SceneConverter` is working for this map
- the memory ceiling is hit in monolithic composition / write-out, not in FOX2 discovery or foxscene packaging

## Layered export strategy for huge scenes

For very large scenes, do **not** insist on a single monolithic GLB first.

The preferred long-term artifact is now a `*.foxscene.json` package with independent GLBs. Layered merged GLBs are still useful for Blender inspection, but foxscene avoids duplicating all geometry into one final file and preserves scene/file/layer boundaries for other runtimes.

Current foxscene limitation: it packages FMDL-backed model placements. TerrainBlock nodes and their metadata stay in the scene JSON, but generated `.htre` terrain geometry still needs a follow-up terrain-asset packager before the Web viewer can render terrain directly.

The Web viewer should be used in incremental mode for maps at AFGH scale. Serve the repo and exported assets from a common HTTP root, pass `autoload=none` to inspect the manifest only, or pass `layers=<layer name>` to load one area at a time. The viewer intentionally builds Three.js node objects only for loaded layers.

Instead:

1. generate the full `.scene.compact.json`
2. run `FoxScenePackager` to create the portable scene package
3. inspect the package in `tools\FoxSceneViewer`
4. only split into smaller compact scenes for Blender or when a target runtime cannot stream the full package

For Blender-only inspection, the old layered GLB path is still:

1. split the compact scene by `files[*].path` prefix
2. feed each smaller compact scene into `SceneGltfComposer`
3. inspect the resulting multiple GLBs in Blender

This worked for `afgh_stage`.

### Current split used for afgh_stage

Output directory:

- `D:\BOTW\MGSV\EXPORTED\Assets\tpp\level\location\afgh\afgh_stage_layered`

Compact-scene groups created:

- `block_common`
- `block_large.<area>` for each named large area
- `block_small`

The first successful pass focused on `block_common` and all `block_large.<area>` groups.

### Large-area GLBs already exported successfully

These exist under `afgh_stage_layered`:

- `afgh_stage.block_common.scene.glb`
- `afgh_stage.block_large.bridge.scene.glb`
- `afgh_stage.block_large.citadel.scene.glb`
- `afgh_stage.block_large.cliffTown.scene.glb`
- `afgh_stage.block_large.commFacility.scene.glb`
- `afgh_stage.block_large.enemyBase.scene.glb`
- `afgh_stage.block_large.field.scene.glb`
- `afgh_stage.block_large.fort.scene.glb`
- `afgh_stage.block_large.powerPlant.scene.glb`
- `afgh_stage.block_large.remnants.scene.glb`
- `afgh_stage.block_large.ruins.scene.glb`
- `afgh_stage.block_large.slopedTown.scene.glb`
- `afgh_stage.block_large.sovietBase.scene.glb`
- `afgh_stage.block_large.tent.scene.glb`
- `afgh_stage.block_large.village.scene.glb`
- `afgh_stage.block_large.waterway.scene.glb`

Observed file sizes were roughly in the few-hundred-MB range, which is large but still manageable compared with the full-scene export.

### block_small status

`block_small` was split out as its own compact scene and is a good next target because it is much lighter in unique FMDLs than `block_large`:

- `block_small`: `4109` files, `4765` nodes, `60` unique FMDLs
- `block_large` total: `30` files, `37556` nodes, `1310` unique FMDLs

If `block_small` still feels large in Blender, it can be split again by X/Z ranges or by directory bands (`101-110`, `111-120`, etc.).

## Practical recommendations for future sessions

1. **Use layered exports first** for giant maps.  
   They are already proving useful for `afgh_stage`.

2. **Treat `block_large` as the main visual world layer.**  
   If the goal is quick visual validation, start there.

3. **Add explicit discovery support later** for:
   - `block_extraSmall`
   - `block_extraLarge`
   - `block_mission2`

4. **Keep `scene.json` and `scene.compact.json` around** when debugging.  
   The JSON artifacts are often more valuable than the GLB when isolating missing layers.

5. **For `afgh_stage`, prefer incremental validation**:
   - common
   - each large area
   - then `block_small`
   - then optional extra/misson layers

## Suggested next steps

If work resumes on `afgh_stage`, the next sensible sequence is:

1. export `block_small` as one GLB
2. if needed, split `block_small` further into sub-grids
3. add explicit compact-scene generation for `block_extraSmall`
4. decide whether `block_mission2` should be exported as separate overlay GLBs rather than merged with the base map

