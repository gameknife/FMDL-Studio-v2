r"""
Blender importer for Fox2 compact scene JSON.

Usage from Blender:

    blender --python .\tools\Blender\import_fox2_scene.py -- ^
        --scene-json "D:\path\to\scene.compact.json"

The script reads the compact scene export, converts referenced FMDL files to GLB
through the standalone FmdlGltfConverter, imports each GLB once as a hidden
template, then duplicates the imported objects to build the scene hierarchy.
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, Iterable, List, Optional, Sequence

try:
    import bpy
except ModuleNotFoundError:  # pragma: no cover - only absent outside Blender
    bpy = None


SCRIPT_PATH = Path(__file__).resolve()
REPO_ROOT = SCRIPT_PATH.parents[2] if len(SCRIPT_PATH.parents) >= 3 else SCRIPT_PATH.parent


@dataclass(frozen=True)
class NodeTransform:
    translation: tuple[float, float, float]
    rotation_quaternion: tuple[float, float, float, float]
    scale: tuple[float, float, float]


class ConverterCommand:
    def __init__(self, command: Sequence[str]) -> None:
        self.command = list(command)

    def build(self, input_path: Path, output_path: Path) -> List[str]:
        return [*self.command, str(input_path), str(output_path)]


class Fox2SceneImporter:
    def __init__(self, args: argparse.Namespace) -> None:
        self.args = args
        self.scene_json_path = Path(args.scene_json).resolve()
        self.scene_data = self._read_json(self.scene_json_path)
        self.asset_root = self._resolve_asset_root(args.asset_root)
        self.output_root = self._resolve_output_root(args.output_root)
        self.cache_dir = self._resolve_cache_dir(args.cache_dir)
        self.converter = self._resolve_converter_command(args.converter)
        self.model_templates: Dict[str, List["bpy.types.Object"]] = {}
        self.file_model_lookup: Dict[str, List[str]] = {
            file_entry["path"]: list(file_entry.get("fmdlFiles") or [])
            for file_entry in self.scene_data.get("files", [])
        }
        self.source_assets_collection = self._ensure_collection(
            "_Fox2SourceAssets",
            bpy.context.scene.collection if bpy is not None else None,
        )
        if self.source_assets_collection is not None:
            self.source_assets_collection.hide_viewport = True
            self.source_assets_collection.hide_render = True

    def import_scene(self) -> None:
        if bpy is None:
            raise RuntimeError("This script must run inside Blender.")

        root_name = self.args.root_collection or f"Fox2 Scene - {self.scene_json_path.stem}"
        root_collection = self._ensure_collection(root_name, bpy.context.scene.collection)

        if self.args.clear_collection:
            self._clear_collection(root_collection)

        compact_files = self.scene_data.get("files", [])
        for file_entry in compact_files:
            file_collection_name = Path(file_entry["path"]).stem
            file_collection = self._ensure_collection(file_collection_name, root_collection)
            file_models = list(file_entry.get("fmdlFiles") or [])
            for node_entry in file_entry.get("rootNodes") or []:
                self._instantiate_node_tree(
                    node_entry=node_entry,
                    target_collection=file_collection,
                    parent_object=None,
                    file_models=file_models,
                )

        bpy.context.view_layer.update()

    def _instantiate_node_tree(
        self,
        node_entry: dict,
        target_collection: "bpy.types.Collection",
        parent_object: Optional["bpy.types.Object"],
        file_models: Sequence[str],
    ) -> Optional["bpy.types.Object"]:
        node_name = node_entry.get("name") or "Fox2Node"
        transform = self._parse_transform(node_entry.get("transform"))
        child_entries = node_entry.get("children") or []
        model_paths = self._resolve_node_models(node_entry, file_models)

        should_create_node = transform is not None or model_paths or child_entries or not self.args.skip_empty_leaves
        node_object: Optional["bpy.types.Object"] = None

        if should_create_node:
            node_object = bpy.data.objects.new(node_name, None)
            node_object.empty_display_type = "PLAIN_AXES"
            node_object.empty_display_size = 0.2
            target_collection.objects.link(node_object)
            if parent_object is not None:
                node_object.parent = parent_object
            self._apply_transform(node_object, transform)

            for model_path in model_paths:
                self._instantiate_model(
                    model_path=model_path,
                    target_collection=target_collection,
                    parent_object=node_object,
                )

        for child_entry in child_entries:
            self._instantiate_node_tree(
                node_entry=child_entry,
                target_collection=target_collection,
                parent_object=node_object or parent_object,
                file_models=file_models,
            )

        return node_object

    def _instantiate_model(
        self,
        model_path: str,
        target_collection: "bpy.types.Collection",
        parent_object: Optional["bpy.types.Object"],
    ) -> None:
        template_roots = self._ensure_model_template(model_path)
        instance_parent_name = Path(model_path).stem
        instance_parent = bpy.data.objects.new(instance_parent_name, None)
        instance_parent.empty_display_type = "CUBE"
        instance_parent.empty_display_size = 0.05
        target_collection.objects.link(instance_parent)
        if parent_object is not None:
            instance_parent.parent = parent_object

        for root_object in template_roots:
            self._duplicate_object_tree(root_object, target_collection, instance_parent, {})

    def _ensure_model_template(self, model_path: str) -> List["bpy.types.Object"]:
        if model_path in self.model_templates:
            return self.model_templates[model_path]

        fmdl_path = self._resolve_asset_path(model_path)
        if not fmdl_path.exists():
            raise FileNotFoundError(f"FMDL file not found for asset path '{model_path}': {fmdl_path}")

        glb_path = self._glb_cache_path(model_path)
        if self.args.rebuild_glb or not glb_path.exists():
            glb_path.parent.mkdir(parents=True, exist_ok=True)
            self._run_converter(fmdl_path, glb_path)

        asset_collection = self._ensure_collection(Path(model_path).stem, self.source_assets_collection)
        imported_objects = self._import_glb_into_collection(glb_path, asset_collection)
        root_objects = [
            obj for obj in imported_objects
            if obj.parent is None or obj.parent not in imported_objects
        ]

        for obj in imported_objects:
            obj.hide_set(True)
            obj.hide_render = True

        self.model_templates[model_path] = root_objects
        return root_objects

    def _import_glb_into_collection(
        self,
        glb_path: Path,
        target_collection: "bpy.types.Collection",
    ) -> List["bpy.types.Object"]:
        before_objects = set(bpy.data.objects)
        previous_layer_collection = bpy.context.view_layer.active_layer_collection
        layer_collection = self._find_layer_collection(bpy.context.view_layer.layer_collection, target_collection)
        if layer_collection is None:
            raise RuntimeError(f"Unable to activate Blender layer collection for '{target_collection.name}'.")

        bpy.context.view_layer.active_layer_collection = layer_collection
        try:
            bpy.ops.import_scene.gltf(filepath=str(glb_path))
        finally:
            bpy.context.view_layer.active_layer_collection = previous_layer_collection

        return [obj for obj in bpy.data.objects if obj not in before_objects]

    def _duplicate_object_tree(
        self,
        source_object: "bpy.types.Object",
        target_collection: "bpy.types.Collection",
        parent_object: Optional["bpy.types.Object"],
        duplicates: Dict["bpy.types.Object", "bpy.types.Object"],
    ) -> "bpy.types.Object":
        duplicate = source_object.copy()
        if source_object.data is not None:
            duplicate.data = source_object.data
        duplicate.animation_data_clear()
        duplicate.hide_set(False)
        duplicate.hide_render = False
        target_collection.objects.link(duplicate)
        duplicates[source_object] = duplicate

        if parent_object is not None:
            duplicate.parent = parent_object

        for child in source_object.children:
            self._duplicate_object_tree(child, target_collection, duplicate, duplicates)

        return duplicate

    def _run_converter(self, fmdl_path: Path, glb_path: Path) -> None:
        command = self.converter.build(fmdl_path, glb_path)
        print("[Fox2 Blender] Converting:", " ".join(command))
        subprocess.run(command, check=True)

    def _resolve_node_models(self, node_entry: dict, file_models: Sequence[str]) -> List[str]:
        direct_models = list(node_entry.get("fmdlPaths") or [])
        if direct_models:
            return list(dict.fromkeys(direct_models))

        if self.args.disable_name_fallback:
            return []

        node_name = node_entry.get("name")
        if not node_name:
            return []

        guessed = self._guess_models_from_name(node_name, file_models)
        return guessed

    def _guess_models_from_name(self, node_name: str, file_models: Sequence[str]) -> List[str]:
        primary = node_name.split("|", 1)[0]
        labels = self._build_name_candidates(primary)
        if not labels:
            return []

        best_score = 0
        best_match: Optional[str] = None
        ambiguous = False

        for model_path in file_models:
            stem = self._normalize_name(Path(model_path).stem)
            if not stem:
                continue

            score = self._score_model_name_match(labels, stem)
            if score > best_score:
                best_score = score
                best_match = model_path
                ambiguous = False
            elif score == best_score and score > 0:
                ambiguous = True

        if best_match is None or ambiguous:
            return []

        return [best_match]

    def _score_model_name_match(self, labels: Sequence[str], model_stem: str) -> int:
        best = 0
        for label in labels:
            if label == model_stem:
                best = max(best, 1000 + len(model_stem))
            elif label.startswith(model_stem) and len(model_stem) >= 8:
                best = max(best, 500 + len(model_stem))
            elif model_stem.startswith(label) and len(label) >= 8:
                best = max(best, 300 + len(label))
        return best

    def _build_name_candidates(self, node_name: str) -> List[str]:
        normalized = self._normalize_name(node_name)
        if not normalized:
            return []

        parts = [part for part in normalized.split("_") if part]
        candidates = [normalized]
        for length in range(len(parts), 1, -1):
            candidates.append("_".join(parts[:length]))
        return list(dict.fromkeys(candidate for candidate in candidates if candidate))

    def _normalize_name(self, value: str) -> str:
        normalized = re.sub(r"[^a-z0-9]+", "_", value.lower())
        return normalized.strip("_")

    def _apply_transform(self, obj: "bpy.types.Object", transform: Optional[NodeTransform]) -> None:
        if transform is None:
            return

        obj.location = transform.translation
        obj.rotation_mode = "QUATERNION"
        x, y, z, w = transform.rotation_quaternion
        obj.rotation_quaternion = (w, x, y, z)
        obj.scale = transform.scale

    def _parse_transform(self, data: Optional[dict]) -> Optional[NodeTransform]:
        if not data:
            return None

        translation = self._vector_from_dict(data.get("translation"), (0.0, 0.0, 0.0))
        rotation = self._vector_from_dict(data.get("rotationQuaternion"), (0.0, 0.0, 0.0, 1.0), four=True)
        scale = self._vector_from_dict(data.get("scale"), (1.0, 1.0, 1.0))
        return NodeTransform(translation=translation, rotation_quaternion=rotation, scale=scale)

    def _vector_from_dict(
        self,
        data: Optional[dict],
        default: Sequence[float],
        *,
        four: bool = False,
    ) -> tuple:
        if not data:
            return tuple(default)

        if four:
            return (
                float(data.get("x", default[0])),
                float(data.get("y", default[1])),
                float(data.get("z", default[2])),
                float(data.get("w", default[3])),
            )

        return (
            float(data.get("x", default[0])),
            float(data.get("y", default[1])),
            float(data.get("z", default[2])),
        )

    def _resolve_asset_root(self, override: Optional[str]) -> Path:
        if override:
            return Path(override).resolve()

        asset_root = self.scene_data.get("assetRootPath")
        if asset_root:
            return Path(asset_root).resolve()

        raise RuntimeError("Missing asset root. Pass --asset-root or use a compact scene JSON that includes assetRootPath.")

    def _resolve_output_root(self, override: Optional[str]) -> Path:
        return Path(override).resolve() if override else self.scene_json_path.parent

    def _resolve_cache_dir(self, override: Optional[str]) -> Path:
        return Path(override).resolve() if override else self.output_root / "_glb_cache"

    def _resolve_converter_command(self, override: Optional[str]) -> ConverterCommand:
        if override:
            override_path = Path(override).resolve()
            return self._command_from_path(override_path)

        candidates = [
            REPO_ROOT / "tools" / "FmdlGltfConverter" / "bin" / "Release" / "net8.0" / "FmdlGltfConverter.exe",
            REPO_ROOT / "tools" / "FmdlGltfConverter" / "bin" / "Release" / "net8.0" / "FmdlGltfConverter.dll",
            REPO_ROOT / "tools" / "FmdlGltfConverter" / "bin" / "Debug" / "net8.0" / "FmdlGltfConverter.exe",
            REPO_ROOT / "tools" / "FmdlGltfConverter" / "bin" / "Debug" / "net8.0" / "FmdlGltfConverter.dll",
        ]

        for candidate in candidates:
            if candidate.exists():
                return self._command_from_path(candidate)

        raise RuntimeError("Unable to locate FmdlGltfConverter. Pass --converter with the .exe or .dll path.")

    def _command_from_path(self, converter_path: Path) -> ConverterCommand:
        if converter_path.suffix.lower() == ".dll":
            return ConverterCommand(["dotnet", str(converter_path)])
        return ConverterCommand([str(converter_path)])

    def _resolve_asset_path(self, asset_path: str) -> Path:
        normalized = asset_path.lstrip("/").replace("/", "\\")
        return (self.asset_root / normalized).resolve()

    def _glb_cache_path(self, asset_path: str) -> Path:
        relative = Path(asset_path.lstrip("/").replace("/", "\\"))
        return (self.cache_dir / relative).with_suffix(".glb")

    def _ensure_collection(
        self,
        name: str,
        parent: Optional["bpy.types.Collection"],
    ) -> Optional["bpy.types.Collection"]:
        if bpy is None:
            return None

        collection = bpy.data.collections.get(name)
        if collection is None:
            collection = bpy.data.collections.new(name)

        if parent is not None and parent.children.get(collection.name) is None:
            parent.children.link(collection)

        return collection

    def _clear_collection(self, collection: "bpy.types.Collection") -> None:
        for obj in list(collection.objects):
            bpy.data.objects.remove(obj, do_unlink=True)
        for child in list(collection.children):
            collection.children.unlink(child)

    def _find_layer_collection(self, layer_collection: "bpy.types.LayerCollection", target: "bpy.types.Collection"):
        if layer_collection.collection == target:
            return layer_collection

        for child in layer_collection.children:
            found = self._find_layer_collection(child, target)
            if found is not None:
                return found

        return None

    def _read_json(self, path: Path) -> dict:
        with path.open("r", encoding="utf-8") as handle:
            return json.load(handle)


def parse_args(argv: Optional[Sequence[str]] = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Import a Fox2 compact scene JSON into Blender.")
    parser.add_argument("--scene-json", required=True, help="Path to the *.scene.compact.json file.")
    parser.add_argument("--asset-root", help="MGSV asset root. Defaults to assetRootPath from the compact JSON.")
    parser.add_argument("--converter", help="Explicit path to FmdlGltfConverter.exe or .dll.")
    parser.add_argument("--cache-dir", help="Directory used to cache generated GLB files.")
    parser.add_argument("--output-root", help="Directory used for generated sidecar files. Defaults to the scene JSON folder.")
    parser.add_argument("--root-collection", help="Top-level Blender collection name.")
    parser.add_argument("--rebuild-glb", action="store_true", help="Rebuild GLB files even when cached versions already exist.")
    parser.add_argument("--clear-collection", action="store_true", help="Clear the target root collection before importing.")
    parser.add_argument("--disable-name-fallback", action="store_true", help="Disable best-effort node-name matching when a node has no explicit fmdlPaths.")
    parser.add_argument("--skip-empty-leaves", action="store_true", help="Skip leaf nodes that have no transform and no model assignment.")

    effective_argv = list(argv) if argv is not None else list(sys.argv)
    if "--" in effective_argv:
        effective_argv = effective_argv[effective_argv.index("--") + 1 :]
    else:
        effective_argv = effective_argv[1:]

    return parser.parse_args(effective_argv)


def main(argv: Optional[Sequence[str]] = None) -> None:
    args = parse_args(argv)
    importer = Fox2SceneImporter(args)
    importer.import_scene()
    print("[Fox2 Blender] Import complete.")


if __name__ == "__main__":
    main([ "--scene-json",
r"C:\\Users\\kaimi\\.copilot\session-state\\477f27d8-f332-43e9-aad7-fdbe6b5c163d\\files\\mbqf_stage.scene.compact.json",
              "--converter",
         r"D:\\github\\FMDL-Studio-v2\\tools\\FmdlGltfConverter\\bin\\Release\\net8.0\\FmdlGltfConverter.exe",
                  "--cache-dir",
         r"D:\\BOTW\\MGSV\\Cache",    
         "--asset-root",
         r"D:\\BOTW\\MGSV\\Root",
         "--clear-collection"])
