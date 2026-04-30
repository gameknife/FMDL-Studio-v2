# Scene export notes

## Purpose

This note captures the current working knowledge for exporting MGSV FOX2 scenes in this repository, especially:

- the validated `Fox2SceneConverter -> SceneGltfComposer` workflow,
- the scene-analysis patterns that were useful when debugging missing content,
- and the layered-export strategy used for very large scenes like `afgh_stage`.

It is intended as a handoff document for future sessions.

## Current toolchain

The scene pipeline is:

1. `tools\Fox2SceneConverter`  
   Reads a `.fox2` scene and writes:
   - `<scene>.scene.json`
   - `<scene>.scene.compact.json`

2. `tools\SceneGltfComposer`  
   Reads `.scene.compact.json` and writes the final `.glb`.

3. `tools\FmdlGltfConverter`  
   Used indirectly by `SceneGltfComposer` to convert referenced FMDLs into cached per-model GLBs.

Validated command pattern:

```powershell
dotnet .\tools\Fox2SceneConverter\bin\Release\net8.0\Fox2SceneConverter.dll `
  'D:\BOTW\MGSV\EXPORTED\Assets\tpp\level\location\cypr\cypr_stage.fox2' `
  --asset-root 'D:\BOTW\MGSV\EXPORTED'

dotnet .\tools\SceneGltfComposer\bin\Release\net8.0\SceneGltfComposer.dll `
  'D:\BOTW\MGSV\EXPORTED\Assets\tpp\level\location\cypr\cypr_stage.scene.compact.json' `
  --asset-root 'D:\BOTW\MGSV\EXPORTED'
```

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

Full-scene composition currently still fails for `afgh_stage` because the final scene is too large and eventually trips `Stream was too long`.

That means:

- `Fox2SceneConverter` is working for this map
- the memory ceiling is hit in final composition / write-out, not in FOX2 discovery

## Layered export strategy for huge scenes

For very large scenes, do **not** insist on a single monolithic GLB first.

Instead:

1. generate the full `.scene.compact.json`
2. split it into smaller compact scenes by `files[*].path` prefix
3. feed each smaller compact scene into `SceneGltfComposer`
4. inspect the resulting multiple GLBs in Blender

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

