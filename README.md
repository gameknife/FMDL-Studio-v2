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

## Credits
BobDoleOwndU: Programming and reverse-engineering.

Joey35233: Programming.

youarebritish (sai): General help/debugging.

Tex: Testing.

Highflex: Reverse-engineering.

revel8n: Reverse-engineering.

Thanks to Jayveerk, cra0kalo and HeartlessSeph for their previous work on the .fmdl format.
