# Unity Audit — Quest 3 VR Client

**Date:** 2026-02-20
**Scope:** All 17 `.cs` files in `Assets/Scripts/`
**Purpose:** Verify 16 recent bug fixes (C1-C10, I1-I6) and check for remaining issues

---

## Check 1: MaterialPropertyBlock (C8 verify)

**Verdict: PASS**

`ImageDisplay.cs` correctly uses `MaterialPropertyBlock` for per-renderer texture overrides instead of cloning materials.

- **Field declaration:** `ImageDisplay.cs:38` — `private MaterialPropertyBlock mpb;`
- **Initialization:** `ImageDisplay.cs:122` — `mpb = new MaterialPropertyBlock();`
- **Texture application:** `ImageDisplay.cs:219-220` — `mpb.SetTexture(texturePropertyName, texture); displayRenderer.SetPropertyBlock(mpb);`
- **Default property name:** `ImageDisplay.cs:20` — `public string texturePropertyName = "_BaseMap";` (correct for URP)
- **Smart fallback:** `ImageDisplay.cs:104-117` — If the shader doesn't have `_BaseMap`, it checks for `_BaseMap` again then `_MainTex`, so it handles both URP and Standard shaders.
- **ArtworkPlacer also sets it explicitly:** `ArtworkPlacer.cs:836` — `display.texturePropertyName = "_BaseMap";`

No instances of `new Material()` for texture assignment in `ImageDisplay.cs`.

---

## Check 2: Visual weight scaling

**Verdict: PASS (documented below)**

Visual weight has a very limited effect on artwork dimensions. The only dimension scaling is hero-based, not weight-based.

### All visual_weight usage in ArtworkPlacer.cs:

| Location | Usage | Formula |
|---|---|---|
| `ArtworkPlacer.cs:150` | Sort priority (descending) | `assetB.GetVisualWeight().CompareTo(assetA.GetVisualWeight())` |
| `ArtworkPlacer.cs:217` | Hero candidate scoring | `float score = asset.GetVisualWeight()` — highest weight wins hero |
| `ArtworkPlacer.cs:339` | Cluster sort (descending) | `b.asset.GetVisualWeight().CompareTo(a.asset.GetVisualWeight())` — heaviest piece becomes anchor |
| `ArtworkPlacer.cs:976` | Spotlight intensity | `Mathf.Lerp(0.95f, 1.05f, Mathf.Clamp01(visualWeight))` — +/-5% intensity nudge |

### Dimension scaling (all hero-based, NOT weight-based):

| Location | What | Formula |
|---|---|---|
| `ArtworkPlacer.cs:680-681` | Hero image width (collision) | `artworkWidth *= heroScaleMultiplier` where `heroScaleMultiplier = 1.4f` (line 30) |
| `ArtworkPlacer.cs:704-708` | Hero image final size | `imageWidth *= heroScaleMultiplier; imageHeight *= heroScaleMultiplier;` |
| `ArtworkPlacer.cs:231` | Hero focal wall width check | `(heroAsset.width > 0f ? heroAsset.width : 1.2f) * heroScaleMultiplier` |

### What's NOT scaled by visual_weight:
- Solo piece frame dimensions — uses `asset.width` / `asset.height` directly
- Cluster piece dimensions — uses `asset.width` directly (`ArtworkPlacer.cs:313`)
- No `Lerp(0.9, 1.15, visualWeight)` pattern exists anywhere

**Summary:** `visual_weight` only affects sort order (hero selection, cluster anchor) and a tiny spotlight intensity variation (+/-5%). Frame/image dimensions are only scaled by hero status (1.4x). When the backend takes over sizing, only the hero multiplier needs consideration.

---

## Check 3: Cluster handling

**Verdict: PASS (documented below)**

`ArtworkPlacer.cs` has full cluster-aware placement via `ArrangeClusterPlacements()` (lines 278-420).

### Full cluster placement flow:

1. **Identification** (`ArtworkPlacer.cs:296-306`): Iterates all placements, filters for 2D assets with `role == "cluster"` and non-empty `cluster_id`. Groups into `Dictionary<string, List<ClusterItem>>`.

2. **Skip small/large clusters** (`ArtworkPlacer.cs:334`): Clusters with `< 2` or `> 6` items are skipped (treated as individual placements).

3. **Sort by visual_weight** (`ArtworkPlacer.cs:339`): Heaviest piece becomes the anchor that determines which wall the cluster goes on.

4. **Resolve target wall** (`ArtworkPlacer.cs:342-349`): All cluster pieces are placed on the same wall as the anchor piece. Uses `TryResolveWallTarget()` to determine `roomId/wallKey`.

5. **Re-sort by source position** (`ArtworkPlacer.cs:351`): After anchor selection, pieces are sorted by their original `position_along` to preserve the backend's intended ordering.

6. **Calculate spacing** (`ArtworkPlacer.cs:353-382`):
   - Base gap: `0.28f` meters between pieces (line 361)
   - If total needed width > available wall space, gap is reduced (min `0.1f`, line 368)
   - If still too wide, pieces are uniformly shrunk (min `0.65x`, line 374)

7. **Center on wall** (`ArtworkPlacer.cs:384-394`): Cluster is centered near the anchor's original position, clamped to wall edges with `0.3f` meter padding.

8. **Assign positions** (`ArtworkPlacer.cs:396-412`): Each piece gets a new `position_along_wall` via `ApplyResolvedWallTarget()`.

### What clusters do NOT do:
- **No height staggering** — all cluster pieces use whatever height the manifest specifies per placement
- **No special rotation** — pieces face the same direction as any other wall art
- **No visual grouping cues** — no shared border or background element

---

## Check 4: WallSpaceManager null handling (C2 verify)

**Verdict: PASS**

### TopologyGenerator.cs (lines 131-148):
- `FindValidPosition()` returns `float?` (nullable) at line 133
- When `validPosition.HasValue` is false (line 143), the method returns `null` at line 147
- Warning logged at line 146 when `debugMode` is on

### ArtworkPlacer.cs PlaceImage() (lines 688-695):
- Calls `topologyGenerator.GetWallPlacement(...)` at line 688
- Checks `if (result == null)` at line 691
- Logs error and `return`s — skips placement entirely (line 693-694)

### ArtworkPlacer.cs PlaceSculpture() (lines 786-790):
- Calls `topologyGenerator.GetFloorPlacement(...)` at line 786
- Checks `if (result == null)` at line 787
- Logs error and `return`s

Both callers handle null correctly.

---

## Check 5: Coroutine cleanup (C1 verify)

**Verdict: PASS**

### GalleryOrchestrator.cs:
- `ClearGallery()` at line 153: `StopAllCoroutines();` — called before clearing generators/artwork
- `Reload()` at line 144: calls `ClearGallery()` then `LoadGallery()`, so coroutines are stopped before new ones start

### ImageDisplay.cs (line 182-188):
- After `yield return request.SendWebRequest();` (line 180)
- Null guard: `if (this == null || displayRenderer == null)` (line 184)
- Sets `isLoading = false` and `yield break`s

### SculptureDisplay.cs (lines 90-97):
- After `yield return request.SendWebRequest();` (line 90)
- Null guard: `if (this == null)` (line 93)
- Sets `isLoading = false` and `yield break`s

All three coroutine sites are properly guarded.

---

## Check 6: .sharedMaterial consistency (I2-I4 verify)

**Verdict: PASS**

Searched all 17 `.cs` files for `.material =` (not `.sharedMaterial =`). **Zero matches found.**

Every material assignment across all scripts uses `.sharedMaterial`:

| File | Example Lines |
|---|---|
| `LinearCorridorGenerator.cs` | 145, 157, 204, 214, 222, 244, 267, 289, 309 |
| `LinearWithAlcovesGenerator.cs` | 144, 152, 160, 168, 309, 349, 357, 369, 380, 387 |
| `BranchingRoomsGenerator.cs` | 124, 132, 257 |
| `HubAndSpokeGenerator.cs` | 139, 147, 313 |
| `OpenHallGenerator.cs` | 95, 103, 268, 321 |
| `TopologyGenerator.cs` | 1444, 1568, 1571, 1576, 2041 |
| `ArtworkPlacer.cs` | 824, 879, 899, 907 |

No implicit material clones remain.

---

## Check 7: Texture memory cleanup (C6/C7 verify)

**Verdict: PASS**

### ImageDisplay.cs (C6):
- `downloadedTexture` field: `ImageDisplay.cs:44` — `private Texture2D downloadedTexture;`
- Previous texture cleanup on reload: `ImageDisplay.cs:213-216` — `if (downloadedTexture != null) { Destroy(downloadedTexture); }`
- `OnDestroy()`: `ImageDisplay.cs:236-243` — destroys `downloadedTexture` and nulls it

### ArtworkPlacer.cs (C7):
- `ClearAllArtwork()` at lines 634-636:
  ```
  if (frameMaterial != null) { Destroy(frameMaterial); frameMaterial = null; }
  if (frameBorderMaterial != null) { Destroy(frameBorderMaterial); frameBorderMaterial = null; }
  if (pedestalMaterial != null) { Destroy(pedestalMaterial); pedestalMaterial = null; }
  ```

### TopologyGenerator.cs (bonus):
- `ClearGenerated()` at lines 657-660: destroys `wallMaterial`, `floorMaterial`, `ceilingMaterial`, `trimMaterial` via `DestroyRuntimeMaterial()`
- Also destroys procedural textures: lines 1824-1841 via `DestroyGeneratedTextures()`

All runtime-created textures and materials are properly destroyed.

---

## Check 8: debugMode defaults (I1 verify)

**Verdict: PASS**

Every script with a `debugMode` field defaults to `false`:

| File | Line | Declaration |
|---|---|---|
| `ImageDisplay.cs` | 34 | `public bool debugMode = false;` |
| `ArtworkPlacer.cs` | 49 | `public bool debugMode = false;` |
| `TopologyGenerator.cs` | 32 | `public bool debugMode = false;` |
| `GalleryOrchestrator.cs` | 46 | `public bool debugMode = false;` |
| `SculptureDisplay.cs` | 34 | `public bool debugMode = false;` |
| `ManifestLoader.cs` | 22 | `public bool debugMode = false;` |

No scripts default `debugMode` to `true`.

---

## Check 9: SculptureDisplay task fault handling (I6 verify)

**Verdict: PASS**

### Load task (`SculptureDisplay.cs:116-126`):
```csharp
var loadTask = gltf.Load(glbData);
yield return new WaitUntil(() => loadTask.IsCompleted);

if (loadTask.IsFaulted || !loadTask.Result)  // line 119
{
    Debug.LogError(...);
    SetPlaceholderState(loading: false, error: true);
    isLoading = false;
    onComplete?.Invoke(false);
    yield break;
}
```

### Instantiate task (`SculptureDisplay.cs:137-149`):
```csharp
var instantiateTask = gltf.InstantiateSceneAsync(currentModel.transform);
yield return new WaitUntil(() => instantiateTask.IsCompleted);

if (instantiateTask.IsFaulted || !instantiateTask.Result)  // line 140
{
    Debug.LogError(...);
    Destroy(currentModel);
    currentModel = null;
    SetPlaceholderState(loading: false, error: true);
    isLoading = false;
    onComplete?.Invoke(false);
    yield break;
}
```

Both tasks check `IsFaulted` before accessing `.Result`, preventing unhandled exceptions.

---

## Check 10: Player spawn determinism (I5 verify)

**Verdict: PASS**

`GalleryOrchestrator.cs` `PositionPlayerAtSpawn()` at lines 337-344:

```csharp
string firstKey = null;
foreach (var key in rooms.Keys)
{
    if (firstKey == null || string.Compare(key, firstKey, System.StringComparison.Ordinal) < 0)
        firstKey = key;
}
var room = rooms[firstKey];
```

Uses deterministic lexicographic comparison (`StringComparison.Ordinal`) to pick the first room key. Does NOT iterate `Dictionary.Values` arbitrarily.

---

## Check 11: Manifest field coverage

**Verdict: PASS**

| Schema field | Unity type | Location | Match? |
|---|---|---|---|
| `layout_plan` (array format) | `List<RoomLayoutEntry>` | `ManifestModels.cs:26` | PASS — JsonUtility handles arrays natively |
| `placement_plan[].height` | `public float height` | `ManifestModels.cs:472` | PASS |
| `assets[].cluster_id` | `public string cluster_id` | `ManifestModels.cs:544` | PASS |
| `assets[].role` | `public string role` | `ManifestModels.cs:535` | PASS |
| `assets[].visual_weight` | `public float visual_weight` | `ManifestModels.cs:529` | PASS |

Additional fields present and correctly typed:
- `assets[].theme_alignment`: `float` at line 538
- `assets[].distinctiveness`: `float` at line 541
- `placement_plan[].surface/side/position_along/floor_position`: lines 479-484
- `placement_plan[].local_position`: `Vector3Serializable` at line 476

---

## Check 12: Topology generator room dimensions

### LinearCorridorGenerator
- **Fields read:** `length`, `width`, `height` from `LayoutPlanWrapper.GetRoom()`
- **Min/max validation:** None. No checks for zero, negative, or extremely large values.
- **Crash risk:** `width=0` produces walls at the same X position (overlapping). Negative `length` creates inverted corridor with reversed Z positions. Extremely large values would generate geometry normally but may cause floating-point precision issues or memory pressure from large procedural textures.

### LinearWithAlcovesGenerator
- **Fields read:** Corridor: `length`, `width`, `height`. Alcoves: `parent`, `side`, `position_along_parent`, `width`, `depth`, `height`
- **Min/max validation:** Alcove openings clamped to corridor bounds (`Mathf.Clamp` at lines 181-182). No validation on corridor dimensions themselves.
- **Crash risk:** Same as LinearCorridor. Additionally, alcove `position_along_parent` outside corridor range is clamped safely.

### BranchingRoomsGenerator
- **Fields read:** `width`, `length`/`depth`, `height`, `doorways`
- **Min/max validation:** Only check: if `depth <= 0`, defaults to `5f` (line 70-72). Room count capped at 5 (line 54).
- **Crash risk:** `width=0` produces degenerate walls. No height validation.

### HubAndSpokeGenerator
- **Fields read:** `width`, `depth`/`length`, `height`, `direction_from_hub`, `doorways`
- **Min/max validation:** None.
- **Crash risk:** Zero or negative dimensions produce degenerate/inverted geometry. Spoke position calculation uses `hub.depth/2 + spoke.depth/2` — negative values would overlap spokes with hub.

### OpenHallGenerator
- **Fields read:** `width`, `depth`/`length`, `height`, `partitions[].id/x/z/rotation/length/height`, `entry_wall/entry_position/entry_width`
- **Min/max validation:** None on main hall dimensions. Partition thickness hardcoded to `0.1f`.
- **Crash risk:** Same degenerate geometry risk. Partition `x`/`z` outside hall bounds would place geometry outside the visible room.

**Verdict: GAP** — No generator validates incoming dimensions. Zero, negative, or extreme values from the backend can produce degenerate geometry. Recommend adding a validation pass in `TopologyGenerator.Generate()` or in manifest validation: minimum width/length/height of ~1m, maximum of ~100m, and all values must be positive.

---

## Check 13: glTFast shader inclusion

**Verdict: PASS (with caveat)**

- **Resources/ material found:** `Assets/Resources/glTF_shader_keeper.mat` — references the glTF-pbrMetallicRoughness shader via GUID (`b9d29dfa1474148e792ac720cbd45122`). This material exists solely to force shader inclusion in builds.
- **GraphicsSettings:** `ProjectSettings/GraphicsSettings.asset` `m_AlwaysIncludedShaders` does NOT list the glTF shader — only built-in Unity shaders (fileIDs 7, 15104, 15105, 15106, 10753, 10770, 10783).
- **Why it works:** Unity includes all shaders referenced by materials in `Resources/` folders in builds. The `glTF_shader_keeper.mat` trick ensures the shader is compiled into the Quest APK.

**Caveat:** This approach is fragile. If someone deletes `glTF_shader_keeper.mat` or moves it out of `Resources/`, sculptures will render magenta in Quest builds with no compile-time warning. Adding the shader to `GraphicsSettings.asset` `m_AlwaysIncludedShaders` would be more robust.

---

## Check 14: Frame rate cap

**Verdict: FAIL**

- **Location:** `GalleryOrchestrator.cs:77` — `Application.targetFrameRate = 30;` in `Awake()`
- **Runs early:** Yes, `Awake()` on the main orchestrator runs before `Start()`.

**Problem:** 30fps causes motion sickness in VR. Quest 3 supports 72Hz, 90Hz, and 120Hz refresh rates. At 30fps the display refreshes are not evenly divisible (72/30, 90/30), causing judder and reprojection artifacts. The Oculus/Meta runtime may compensate with ASW (Application SpaceWarp) but the visual quality will be poor and many users will experience nausea.

**Recommendation:** Either:
- Remove the line entirely (let the Quest runtime manage frame timing via its VSync)
- Set to `72` as a minimum baseline: `Application.targetFrameRate = 72;`
- Use the OVR performance API: `OVRManager.display.displayFrequency = 72f;`

---

## Summary

| # | Check | Verdict |
|---|---|---|
| 1 | MaterialPropertyBlock (C8) | **PASS** — Uses MPB with `_BaseMap`, smart fallback to `_MainTex` |
| 2 | Visual weight scaling | **PASS** — Documented: weight only affects sort order + 5% spotlight nudge, no frame sizing |
| 3 | Cluster handling | **PASS** — Full cluster grouping on same wall with tight spacing, no height stagger |
| 4 | WallSpaceManager null handling (C2) | **PASS** — Returns null, callers check and skip |
| 5 | Coroutine cleanup (C1) | **PASS** — StopAllCoroutines in ClearGallery, null guards after yields |
| 6 | .sharedMaterial consistency (I2-I4) | **PASS** — Zero `.material =` assignments remain |
| 7 | Texture memory cleanup (C6/C7) | **PASS** — downloadedTexture destroyed in OnDestroy, materials destroyed in ClearAllArtwork |
| 8 | debugMode defaults (I1) | **PASS** — All 6 scripts default to false |
| 9 | SculptureDisplay fault handling (I6) | **PASS** — IsFaulted checked before .Result on both tasks |
| 10 | Player spawn determinism (I5) | **PASS** — Lexicographic key sort, not arbitrary iteration |
| 11 | Manifest field coverage | **PASS** — All 5 queried fields exist with correct types |
| 12 | Topology generator dimensions | **GAP** — No validation on incoming dimensions; zero/negative/extreme values produce degenerate geometry |
| 13 | glTFast shader | **PASS** — Resources/glTF_shader_keeper.mat forces inclusion (fragile but functional) |
| 14 | Frame rate cap | **FAIL** — 30fps in VR causes motion sickness; must be 72+ or removed for Quest |

**Total: 12 PASS, 1 GAP, 1 FAIL**
