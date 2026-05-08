import * as THREE from "three";
import { OrbitControls } from "three/addons/controls/OrbitControls.js";
import { GLTFLoader } from "three/addons/loaders/GLTFLoader.js";
import * as SkeletonUtils from "three/addons/utils/SkeletonUtils.js";

const canvas = document.querySelector("#sceneCanvas");
const viewport = document.querySelector("#dropZone");
const sceneUrlInput = document.querySelector("#sceneUrl");
const loadUrlButton = document.querySelector("#loadUrlButton");
const fileInput = document.querySelector("#fileInput");
const statusText = document.querySelector("#statusText");
const progressBar = document.querySelector("#progressBar");
const statsGrid = document.querySelector("#statsGrid");
const layersList = document.querySelector("#layersList");
const layerFilterInput = document.querySelector("#layerFilter");
const loadVisibleLayersButton = document.querySelector("#loadVisibleLayersButton");
const unloadAllLayersButton = document.querySelector("#unloadAllLayersButton");
const logList = document.querySelector("#logList");
const frameButton = document.querySelector("#frameButton");
const resetButton = document.querySelector("#resetButton");
const selectedNodeLabel = document.querySelector("#selectedNodeLabel");
const renderProbe = document.querySelector("#renderProbe");

const renderer = new THREE.WebGLRenderer({ canvas, antialias: true, preserveDrawingBuffer: true });
renderer.outputColorSpace = THREE.SRGBColorSpace;
renderer.toneMapping = THREE.ACESFilmicToneMapping;
renderer.toneMappingExposure = 1.0;
renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));

const scene = new THREE.Scene();
scene.background = new THREE.Color(0x171916);

const camera = new THREE.PerspectiveCamera(45, 1, 0.05, 100000);
camera.position.set(8, 6, 8);

const controls = new OrbitControls(camera, renderer.domElement);
controls.enableDamping = true;
controls.dampingFactor = 0.06;
controls.target.set(0, 1, 0);

const rootGroup = new THREE.Group();
rootGroup.name = "foxscene-root";
scene.add(rootGroup);

const grid = new THREE.GridHelper(100, 50, 0x4b8f7a, 0x323730);
grid.material.transparent = true;
grid.material.opacity = 0.32;
scene.add(grid);

const hemiLight = new THREE.HemisphereLight(0xfffaf0, 0x36443d, 2.4);
scene.add(hemiLight);

const keyLight = new THREE.DirectionalLight(0xffffff, 3.2);
keyLight.position.set(8, 12, 6);
scene.add(keyLight);

const fillLight = new THREE.DirectionalLight(0xd7fff0, 1.4);
fillLight.position.set(-10, 5, -8);
scene.add(fillLight);

const loader = new GLTFLoader();
const raycaster = new THREE.Raycaster();
const LargeSceneAutoLoadAssetLimit = 300;
const LayerBatchAssetLimit = 260;
const AssetLoadConcurrency = 2;
let assetCache = new Map();
let layerGroups = new Map();
let sceneState = null;
let layerRows = [];
let activeSceneLabel = "";
let unsupportedTransformCount = 0;
let lastRenderProbeTime = 0;
let selectedNodeObject = null;
let selectionHelper = null;
let pointerDownPosition = null;

function resize() {
  const rect = viewport.getBoundingClientRect();
  const width = Math.max(1, Math.floor(rect.width));
  const height = Math.max(1, Math.floor(rect.height));
  renderer.setSize(width, height, false);
  camera.aspect = width / height;
  camera.updateProjectionMatrix();
}

function animate() {
  resize();
  controls.update();
  renderer.render(scene, camera);
  updateRenderProbe();
  requestAnimationFrame(animate);
}

function setStatus(text, progress = null) {
  statusText.textContent = text;
  if (progress !== null) {
    progressBar.style.width = `${Math.max(0, Math.min(100, progress))}%`;
  }
}

function log(message, level = "info") {
  const entry = document.createElement("div");
  entry.className = `log-entry ${level}`;
  entry.textContent = message;
  logList.prepend(entry);
}

function clearScene() {
  disposeAssetCache();
  clearSelection();
  rootGroup.clear();
  assetCache = new Map();
  layerGroups = new Map();
  sceneState = null;
  layerRows = [];
  unsupportedTransformCount = 0;
  layersList.innerHTML = "";
  logList.innerHTML = "";
  layerFilterInput.value = "";
  setStatus("Loading", 0);
}

async function loadSceneFromUrl(urlText) {
  const sceneUrl = new URL(urlText, window.location.href);
  activeSceneLabel = sceneUrl.href;
  setStatus("Fetching", 5);

  const response = await fetch(sceneUrl);
  if (!response.ok) {
    throw new Error(`Scene fetch failed: ${response.status} ${response.statusText}`);
  }

  const sceneDocument = await response.json();
  await loadSceneDocument(sceneDocument, sceneUrl);
}

async function loadSceneFromFile(file) {
  activeSceneLabel = file.name;
  const text = await file.text();
  const sceneDocument = JSON.parse(text);
  await loadSceneDocument(sceneDocument, null);
}

async function loadSceneDocument(sceneDocument, baseUrl) {
  clearScene();
  validateSceneDocument(sceneDocument);

  const assetsById = new Map((sceneDocument.assets ?? []).map((asset) => [asset.id, asset]));
  const nodesById = new Map((sceneDocument.nodes ?? []).map((node) => [node.id, node]));

  renderStats(sceneDocument);
  const layerStates = buildLayerStates(sceneDocument, nodesById, assetsById);
  sceneState = { sceneDocument, baseUrl, assetsById, nodesById, layerStates };
  renderLayerControls(layerStates);

  const uniqueAssetIds = [...new Set([...layerStates.values()].flatMap((state) => [...state.assetIds]))];
  if (uniqueAssetIds.length === 0) {
    setStatus("Ready", 100);
    frameScene();
    log(`Loaded ${activeSceneLabel}`);
    return;
  }

  const initialLayerIds = getInitialLayerIds(layerStates, uniqueAssetIds.length);
  if (initialLayerIds.length === 0) {
    setStatus("Ready", 100);
    log(`Loaded manifest ${activeSceneLabel}`);
    log(`Large scene mode: ${uniqueAssetIds.length.toLocaleString()} assets are available. Filter layers and press Load.`);
    return;
  }

  await loadLayers(initialLayerIds);
  setStatus("Ready", 100);
  frameScene();
  log(`Loaded ${activeSceneLabel}`);
}

function validateSceneDocument(sceneDocument) {
  if (!sceneDocument || sceneDocument.schema !== "https://fmdl.studio/schemas/foxscene-1.json") {
    throw new Error("Input is not a foxscene v1 JSON document.");
  }

  if (!Array.isArray(sceneDocument.nodes) || !Array.isArray(sceneDocument.assets) || !Array.isArray(sceneDocument.layers)) {
    throw new Error("Scene document is missing nodes, assets, or layers.");
  }
}

function createNodeObject(nodeId, nodesById, assetsById, placements, nodeObjects, visiting) {
  if (nodeObjects.has(nodeId)) {
    return nodeObjects.get(nodeId);
  }

  if (visiting.has(nodeId)) {
    log(`Skipped recursive node reference ${nodeId}`, "error");
    return null;
  }

  const node = nodesById.get(nodeId);
  if (!node) {
    log(`Missing node ${nodeId}`, "error");
    return null;
  }

  visiting.add(nodeId);

  const object = new THREE.Object3D();
  object.name = node.name || node.id;
  object.userData = {
    nodeId: node.id,
    className: node.className,
    layerId: node.layerId,
    properties: node.properties,
  };
  applyTransform(object, node.transform);
  nodeObjects.set(nodeId, object);

  for (const binding of node.assets ?? []) {
    const assetId = typeof binding === "string" ? binding : binding.assetId;
    const asset = assetsById.get(assetId);
    if (!asset) {
      log(`Missing asset ${assetId} on node ${node.id}`, "error");
      continue;
    }

    placements.push({ object, asset, source: binding.source || "direct", layerId: node.layerId, instance: null });
  }

  for (const childId of node.children ?? []) {
    const child = createNodeObject(childId, nodesById, assetsById, placements, nodeObjects, visiting);
    if (child) {
      object.add(child);
    }
  }

  visiting.delete(nodeId);
  return object;
}

function applyTransform(object, transform) {
  if (!transform) {
    return;
  }

  if (Array.isArray(transform.translation) && transform.translation.length >= 3) {
    object.position.fromArray(transform.translation);
  }

  if (Array.isArray(transform.rotation) && transform.rotation.length >= 4) {
    object.quaternion.fromArray(transform.rotation);
  }

  if (Array.isArray(transform.scale) && transform.scale.length >= 3) {
    object.scale.fromArray(transform.scale);
  }

  if (transform.shear || transform.pivot || transform.pivotTranslation) {
    unsupportedTransformCount++;
    object.userData.foxTransformExtras = {
      shear: transform.shear,
      pivot: transform.pivot,
      pivotTranslation: transform.pivotTranslation,
    };
  }
}

function buildLayerStates(sceneDocument, nodesById, assetsById) {
  const states = new Map();
  for (const layer of sceneDocument.layers ?? []) {
    const stats = collectLayerStats(layer, nodesById, assetsById);
    states.set(layer.id, {
      layer,
      assetIds: stats.assetIds,
      placementCount: stats.placementCount,
      nodeCount: stats.nodeCount,
      loaded: false,
      loading: false,
      visible: true,
      group: null,
      runtimePlacements: [],
      nodeObjects: null,
      row: null,
      actionButton: null,
      countElement: null,
    });
  }

  return states;
}

function collectLayerStats(layer, nodesById, assetsById) {
  const assetIds = new Set();
  const seen = new Set();
  const stack = [...(layer.rootNodes ?? [])];
  let placementCount = 0;
  let nodeCount = 0;

  while (stack.length > 0) {
    const nodeId = stack.pop();
    if (seen.has(nodeId)) {
      continue;
    }

    seen.add(nodeId);
    const node = nodesById.get(nodeId);
    if (!node) {
      log(`Missing node ${nodeId}`, "error");
      continue;
    }

    nodeCount++;

    for (const binding of node.assets ?? []) {
      const assetId = typeof binding === "string" ? binding : binding.assetId;
      placementCount++;
      if (assetsById.has(assetId)) {
        assetIds.add(assetId);
      }
    }

    for (const childId of node.children ?? []) {
      stack.push(childId);
    }
  }

  return { assetIds, placementCount, nodeCount };
}

function getInitialLayerIds(layerStates, totalAssetCount) {
  const params = new URL(window.location.href).searchParams;
  const autoload = (params.get("autoload") || "").toLowerCase();
  if (autoload === "none") {
    return [];
  }

  const allLayerIds = [...layerStates.values()]
    .filter((state) => state.assetIds.size > 0)
    .map((state) => state.layer.id);

  if (autoload === "all" || totalAssetCount <= LargeSceneAutoLoadAssetLimit) {
    return allLayerIds;
  }

  const layerQuery = params.get("layers") || params.get("layer");
  if (!layerQuery) {
    return [];
  }

  const tokens = layerQuery
    .split(",")
    .map((token) => token.trim().toLowerCase())
    .filter(Boolean);

  if (tokens.length === 0) {
    return [];
  }

  return [...layerStates.values()]
    .filter((state) => tokens.some((token) => layerMatchesFilter(state, token)))
    .map((state) => state.layer.id);
}

function layerMatchesFilter(state, query) {
  const layer = state.layer;
  return (layer.name || "").toLowerCase().includes(query) ||
         (layer.sourceFox2 || "").toLowerCase().includes(query) ||
         layer.id.toLowerCase().includes(query);
}

async function loadLayers(layerIds) {
  if (!sceneState) {
    return;
  }

  const states = layerIds
    .map((id) => sceneState.layerStates.get(id))
    .filter((state) => state && !state.loaded && !state.loading && state.assetIds.size > 0);

  if (states.length === 0) {
    return;
  }

  const assetIds = [...new Set(states.flatMap((state) => [...state.assetIds]))];
  for (const state of states) {
    state.loading = true;
    updateLayerRow(state);
  }

  try {
    await loadAssets(assetIds, sceneState.assetsById, sceneState.baseUrl);
    const previousUnsupportedTransformCount = unsupportedTransformCount;
    for (const state of states) {
      materializeLayer(state);
      instantiatePlacements(state.runtimePlacements);
      state.loaded = true;
      state.loading = false;
      updateLayerRow(state);
    }
    if (unsupportedTransformCount > previousUnsupportedTransformCount) {
      log(`${unsupportedTransformCount - previousUnsupportedTransformCount} node transforms include shear or pivot metadata; TRS was applied for rendering.`);
    }
    setStatus("Ready", 100);
    frameScene();
  } catch (error) {
    for (const state of states) {
      state.loading = false;
      updateLayerRow(state);
    }
    throw error;
  }
}

function materializeLayer(state) {
  if (!sceneState || state.group) {
    return;
  }

  const layer = state.layer;
  const layerGroup = new THREE.Group();
  layerGroup.name = layer.name || layer.id;
  layerGroup.visible = state.visible;
  layerGroup.userData = { layerId: layer.id, sourceFox2: layer.sourceFox2 };
  rootGroup.add(layerGroup);
  layerGroups.set(layer.id, layerGroup);

  const nodeObjects = new Map();
  const visiting = new Set();
  const placements = [];

  for (const rootNodeId of layer.rootNodes ?? []) {
    const object = createNodeObject(rootNodeId, sceneState.nodesById, sceneState.assetsById, placements, nodeObjects, visiting);
    if (object) {
      layerGroup.add(object);
    }
  }

  state.group = layerGroup;
  state.nodeObjects = nodeObjects;
  state.runtimePlacements = placements;
}

function unloadLayer(layerId, shouldFrame = true) {
  const state = sceneState?.layerStates.get(layerId);
  if (!state || state.loading) {
    return;
  }

  if (selectedNodeObject && state.group?.getObjectById(selectedNodeObject.id)) {
    clearSelection();
  }

  if (state.group) {
    rootGroup.remove(state.group);
    state.group.clear();
    layerGroups.delete(layerId);
  }

  state.group = null;
  state.nodeObjects = null;
  state.runtimePlacements = [];
  state.loaded = false;
  updateLayerRow(state);
  if (shouldFrame) {
    frameScene();
  }
}

function unloadAllLayers() {
  if (!sceneState) {
    return;
  }

  for (const state of sceneState.layerStates.values()) {
    unloadLayer(state.layer.id, false);
  }

  disposeAssetCache();
  clearSelection();
  setStatus("Ready", 100);
  frameScene();
}

async function loadVisibleLayers() {
  if (!sceneState) {
    return;
  }

  const visibleUnloadedRows = layerRows.filter((entry) => !entry.row.hidden && !entry.state.loaded && entry.state.assetIds.size > 0);
  if (layerFilterInput.value.trim().length === 0 && visibleUnloadedRows.length > 200) {
    log("Filter layers before batch loading a large scene.");
    return;
  }

  let assetCount = 0;
  const layerIds = [];
  for (const entry of layerRows) {
    if (entry.row.hidden || entry.state.loaded || entry.state.loading || entry.state.assetIds.size === 0) {
      continue;
    }

    if (layerIds.length > 0 && assetCount + entry.state.assetIds.size > LayerBatchAssetLimit) {
      continue;
    }

    layerIds.push(entry.state.layer.id);
    assetCount += entry.state.assetIds.size;
    if (assetCount >= LayerBatchAssetLimit) {
      break;
    }
  }

  if (layerIds.length === 0) {
    log("No unloaded matching layers within the current batch limit.");
    return;
  }

  if (layerIds.length < visibleUnloadedRows.length) {
    log(`Loaded ${layerIds.length} matching layer(s); batch capped near ${LayerBatchAssetLimit} unique assets.`);
  }

  await loadLayers(layerIds);
}

async function loadAssets(uniqueAssetIds, assetsById, baseUrl) {
  const assets = uniqueAssetIds.map((id) => assetsById.get(id)).filter(Boolean);
  let completed = 0;

  await runLimited(assets, AssetLoadConcurrency, async (asset) => {
    if (assetCache.has(asset.id)) {
      completed++;
      setStatus("Loading GLB", 15 + (completed / Math.max(1, assets.length)) * 70);
      return;
    }

    setStatus("Loading GLB", 15 + (completed / Math.max(1, assets.length)) * 70);
    try {
      if (asset.missing) {
        throw new Error("asset is marked missing");
      }

      const assetUrl = resolveAssetUrl(asset.uri, baseUrl);
      const gltf = await loader.loadAsync(assetUrl);
      assetCache.set(asset.id, gltf);
    } catch (error) {
      assetCache.set(asset.id, { error });
      log(`Asset ${asset.name || asset.id}: ${error.message}`, "error");
    } finally {
      completed++;
      setStatus("Loading GLB", 15 + (completed / Math.max(1, assets.length)) * 70);
    }
  });
}

function disposeAssetCache() {
  for (const cached of assetCache.values()) {
    if (cached?.scene) {
      disposeObjectResources(cached.scene);
    }
  }

  assetCache = new Map();
}

function disposeObjectResources(object) {
  object.traverse((child) => {
    if (child.geometry) {
      child.geometry.dispose();
    }

    const materials = Array.isArray(child.material) ? child.material : child.material ? [child.material] : [];
    for (const material of materials) {
      for (const value of Object.values(material)) {
        if (value?.isTexture) {
          value.dispose();
        }
      }
      material.dispose?.();
    }
  });
}

function instantiatePlacements(placements) {
  for (const placement of placements) {
    if (placement.instance) {
      continue;
    }

    const cached = assetCache.get(placement.asset.id);
    if (cached?.scene) {
      const clone = SkeletonUtils.clone(cached.scene);
      clone.name = placement.asset.name || placement.asset.id;
      clone.userData = {
        assetId: placement.asset.id,
        sourceFmdl: placement.asset.sourceFmdl,
        bindingSource: placement.source,
      };
      placement.object.add(clone);
      placement.instance = clone;
    } else {
      const placeholder = createMissingAssetPlaceholder(placement.asset);
      placement.object.add(placeholder);
      placement.instance = placeholder;
    }
  }
}

function resolveAssetUrl(uri, baseUrl) {
  if (!uri) {
    throw new Error("asset has no URI");
  }

  if (/^(data:|blob:|https?:\/\/)/i.test(uri)) {
    return uri;
  }

  if (!baseUrl) {
    throw new Error("relative asset URI requires loading the scene by URL");
  }

  return new URL(uri, baseUrl).href;
}

async function runLimited(items, limit, worker) {
  const queue = [...items];
  const workers = Array.from({ length: Math.min(limit, queue.length) }, async () => {
    while (queue.length > 0) {
      const item = queue.shift();
      await worker(item);
    }
  });
  await Promise.all(workers);
}

function createMissingAssetPlaceholder(asset) {
  const group = new THREE.Group();
  group.name = `missing:${asset.id}`;

  const geometry = new THREE.BoxGeometry(1, 1, 1);
  const material = new THREE.MeshBasicMaterial({ color: 0xd35d3f, wireframe: true });
  const mesh = new THREE.Mesh(geometry, material);
  mesh.position.y = 0.5;
  group.add(mesh);
  return group;
}

function renderStats(sceneDocument) {
  const summary = sceneDocument.summary ?? {};
  const values = [
    summary.layerCount ?? (sceneDocument.layers ?? []).length,
    summary.nodeCount ?? (sceneDocument.nodes ?? []).length,
    summary.assetCount ?? (sceneDocument.assets ?? []).length,
    summary.placementCount ?? countPlacements(sceneDocument.nodes ?? []),
  ];

  [...statsGrid.querySelectorAll("strong")].forEach((element, index) => {
    element.textContent = values[index].toLocaleString();
  });
}

function countPlacements(nodes) {
  return nodes.reduce((total, node) => total + (node.assets?.length ?? 0), 0);
}

function renderLayerControls(layerStates) {
  layersList.innerHTML = "";
  layerRows = [];

  const sortedStates = [...layerStates.values()].sort((left, right) => {
    const assetDelta = right.assetIds.size - left.assetIds.size;
    if (assetDelta !== 0) {
      return assetDelta;
    }

    return (left.layer.name || left.layer.id).localeCompare(right.layer.name || right.layer.id);
  });

  for (const state of sortedStates) {
    const layer = state.layer;
    const row = document.createElement("div");
    row.className = "layer-row";

    const checkbox = document.createElement("input");
    checkbox.type = "checkbox";
    checkbox.checked = state.visible;
    checkbox.addEventListener("change", () => {
      state.visible = checkbox.checked;
      if (state.group) {
        state.group.visible = state.visible;
      }
    });

    const name = document.createElement("span");
    name.title = layer.sourceFox2 || layer.id;
    name.textContent = layer.name || layer.id;

    const count = document.createElement("small");
    count.textContent = formatLayerCount(state);

    const actionButton = document.createElement("button");
    actionButton.type = "button";
    actionButton.addEventListener("click", async () => {
      try {
        if (state.loaded) {
          unloadLayer(layer.id);
        } else {
          await loadLayers([layer.id]);
        }
      } catch (error) {
        setStatus("Error", 0);
        log(error.message, "error");
      }
    });

    row.append(checkbox, name, count, actionButton);
    layersList.append(row);
    state.row = row;
    state.actionButton = actionButton;
    state.countElement = count;
    updateLayerRow(state);
    layerRows.push({ row, state });
  }

  applyLayerFilter();
}

function formatLayerCount(state) {
  const assetCount = state.assetIds.size;
  if (assetCount > 0 || state.placementCount > 0) {
    return `${assetCount.toLocaleString()} / ${state.placementCount.toLocaleString()}`;
  }

  return state.nodeCount.toLocaleString();
}

function updateLayerRow(state) {
  if (!state.row || !state.actionButton || !state.countElement) {
    return;
  }

  state.row.classList.toggle("loaded", state.loaded);
  state.countElement.textContent = formatLayerCount(state);
  state.actionButton.disabled = state.loading || state.assetIds.size === 0;
  state.actionButton.textContent = state.loading ? "..." : state.loaded ? "Unload" : "Load";
}

function applyLayerFilter() {
  const query = layerFilterInput.value.trim().toLowerCase();
  for (const { row, state } of layerRows) {
    row.hidden = query.length > 0 && !layerMatchesFilter(state, query);
  }
}

function frameScene() {
  frameObject(rootGroup, { fallbackToDefault: true });
}

function resetCamera() {
  camera.position.set(8, 6, 8);
  controls.target.set(0, 1, 0);
  camera.near = 0.05;
  camera.far = 100000;
  camera.updateProjectionMatrix();
  controls.update();
}

function frameObject(object, options = {}) {
  const { fallbackToDefault = false } = options;
  const box = new THREE.Box3().setFromObject(object);
  if (box.isEmpty()) {
    if (fallbackToDefault) {
      resetCamera();
    }
    return;
  }

  const size = box.getSize(new THREE.Vector3());
  const center = box.getCenter(new THREE.Vector3());
  const maxDim = Math.max(size.x, size.y, size.z, 1);
  const fov = THREE.MathUtils.degToRad(camera.fov);
  const distance = (maxDim / (2 * Math.tan(fov / 2))) * 1.45;

  camera.position.set(
    center.x + distance * 0.72,
    center.y + distance * 0.52,
    center.z + distance * 0.72,
  );
  camera.near = Math.max(distance / 1000, 0.01);
  camera.far = Math.max(distance * 80, 1000);
  camera.updateProjectionMatrix();
  controls.target.copy(center);
  controls.update();
}

function pickNodeObject(event) {
  const rect = renderer.domElement.getBoundingClientRect();
  if (rect.width <= 0 || rect.height <= 0) {
    return null;
  }

  const pointer = new THREE.Vector2(
    ((event.clientX - rect.left) / rect.width) * 2 - 1,
    -((event.clientY - rect.top) / rect.height) * 2 + 1,
  );

  raycaster.setFromCamera(pointer, camera);
  const intersections = raycaster.intersectObject(rootGroup, true);
  for (const hit of intersections) {
    const nodeObject = findAncestorNodeObject(hit.object);
    if (nodeObject) {
      return nodeObject;
    }
  }

  return null;
}

function findAncestorNodeObject(object) {
  let current = object;
  while (current) {
    if (current.userData?.nodeId) {
      return current;
    }
    current = current.parent;
  }

  return null;
}

function selectNodeObject(object) {
  selectedNodeObject = object;
  updateSelectionHelper();
}

function clearSelection() {
  selectedNodeObject = null;
  updateSelectionHelper();
}

function updateSelectionHelper() {
  if (selectionHelper) {
    scene.remove(selectionHelper);
    selectionHelper = null;
  }

  if (!selectedNodeObject) {
    selectedNodeLabel.textContent = "No selection";
    selectedNodeLabel.title = "";
    return;
  }

  selectedNodeLabel.textContent = selectedNodeObject.name || selectedNodeObject.userData?.nodeId || "Unnamed node";
  selectedNodeLabel.title = selectedNodeLabel.textContent;

  const box = new THREE.Box3().setFromObject(selectedNodeObject);
  if (box.isEmpty()) {
    return;
  }

  selectionHelper = new THREE.Box3Helper(box, 0xffb347);
  scene.add(selectionHelper);
}

function updateRenderProbe() {
  const now = performance.now();
  if (now - lastRenderProbeTime < 2000) {
    return;
  }

  lastRenderProbeTime = now;

  try {
    const gl = renderer.getContext();
    const width = gl.drawingBufferWidth;
    const height = gl.drawingBufferHeight;
    if (width < 2 || height < 2) {
      return;
    }

    const sampleWidth = Math.min(width, 64);
    const sampleHeight = Math.min(height, 64);
    const sampleX = Math.floor((width - sampleWidth) / 2);
    const sampleY = Math.floor((height - sampleHeight) / 2);
    const pixels = new Uint8Array(sampleWidth * sampleHeight * 4);
    gl.readPixels(sampleX, sampleY, sampleWidth, sampleHeight, gl.RGBA, gl.UNSIGNED_BYTE, pixels);

    let litPixels = 0;
    let minBrightness = 255;
    let maxBrightness = 0;
    let samples = 0;
    const samplePixels = sampleWidth * sampleHeight;
    const stride = Math.max(1, Math.floor(samplePixels / 4096));
    for (let pixelIndex = 0; pixelIndex < samplePixels; pixelIndex += stride) {
      const index = pixelIndex * 4;
      const brightness = (pixels[index] + pixels[index + 1] + pixels[index + 2]) / 3;
      minBrightness = Math.min(minBrightness, brightness);
      maxBrightness = Math.max(maxBrightness, brightness);
      if (pixels[index + 3] > 0 && brightness > 10) {
        litPixels++;
      }
      samples++;
    }

    renderProbe.textContent = `Render ${litPixels}/${samples} ${Math.round(minBrightness)}-${Math.round(maxBrightness)}`;
    renderProbe.dataset.litPixels = String(litPixels);
    renderProbe.dataset.sampledPixels = String(samples);
    renderProbe.dataset.brightnessRange = `${Math.round(minBrightness)}-${Math.round(maxBrightness)}`;
  } catch (error) {
    renderProbe.textContent = "Render n/a";
    renderProbe.dataset.error = error.message;
  }
}

loadUrlButton.addEventListener("click", async () => {
  try {
    await loadSceneFromUrl(sceneUrlInput.value.trim());
  } catch (error) {
    setStatus("Error", 0);
    log(error.message, "error");
  }
});

fileInput.addEventListener("change", async () => {
  const file = fileInput.files?.[0];
  if (!file) {
    return;
  }

  try {
    await loadSceneFromFile(file);
  } catch (error) {
    setStatus("Error", 0);
    log(error.message, "error");
  } finally {
    fileInput.value = "";
  }
});

frameButton.addEventListener("click", frameScene);
resetButton.addEventListener("click", resetCamera);
layerFilterInput.addEventListener("input", applyLayerFilter);
loadVisibleLayersButton.addEventListener("click", async () => {
  try {
    await loadVisibleLayers();
  } catch (error) {
    setStatus("Error", 0);
    log(error.message, "error");
  }
});
unloadAllLayersButton.addEventListener("click", unloadAllLayers);
canvas.addEventListener("pointerdown", (event) => {
  pointerDownPosition = { x: event.clientX, y: event.clientY };
});
canvas.addEventListener("pointerup", (event) => {
  if (!pointerDownPosition) {
    return;
  }

  const movedDistance = Math.hypot(event.clientX - pointerDownPosition.x, event.clientY - pointerDownPosition.y);
  pointerDownPosition = null;
  if (movedDistance > 5) {
    return;
  }

  const nodeObject = pickNodeObject(event);
  if (nodeObject) {
    selectNodeObject(nodeObject);
  } else {
    clearSelection();
  }
});
canvas.addEventListener("dblclick", (event) => {
  const nodeObject = pickNodeObject(event);
  if (nodeObject) {
    selectNodeObject(nodeObject);
    frameObject(nodeObject);
  }
});

viewport.addEventListener("dragover", (event) => {
  event.preventDefault();
  viewport.classList.add("dragging");
});

viewport.addEventListener("dragleave", () => {
  viewport.classList.remove("dragging");
});

viewport.addEventListener("drop", async (event) => {
  event.preventDefault();
  viewport.classList.remove("dragging");
  const file = event.dataTransfer?.files?.[0];
  if (!file) {
    return;
  }

  try {
    await loadSceneFromFile(file);
  } catch (error) {
    setStatus("Error", 0);
    log(error.message, "error");
  }
});

const initialScene = new URL(window.location.href).searchParams.get("scene") || "sample/basic.foxscene.json";
sceneUrlInput.value = initialScene;
loadSceneFromUrl(initialScene).catch((error) => {
  setStatus("Error", 0);
  log(error.message, "error");
});

animate();
