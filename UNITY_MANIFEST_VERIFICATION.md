# Unity Manifest Schema Verification Report

**Schema version audited:** v2.1 (backend finalised)
**Unity code version:** v2.0 (per ManifestModels.cs header comment)
**Deserialization engine:** Unity JsonUtility (NOT Newtonsoft)
**Date:** 2026-02-18
**Auditor:** Full code audit of all 14 C# scripts + 5 test manifests

---

## Executive Summary

**1 CRITICAL issue found.** The backend v2.1 schema sends `layout_plan` as a **dictionary keyed by room ID**, but Unity deserializes it as a **`List<RoomLayoutEntry>`** (array of objects with `room_id` + `data`). `JsonUtility` cannot deserialize dictionaries. If the backend is now sending the dict format described in the v2.1 spec, **`layout_plan` will be null/empty and manifest validation will fail**, meaning no gallery will render.

Everything else maps correctly. All 5 topology generators exist and read the right fields. All 6 placement types are handled. All positions are in meters (not normalized). All nullable fields are guarded. The asset URL pipeline through ImageDisplay/SculptureDisplay (glTFast) is correct.

**Counts:**
| Category | Count |
|----------|-------|
| Fields correctly mapped | 47 |
| Fields correctly ignored | 4 (semantic_analysis, _internal, interpretation, similarities) |
| Extra C# fields (harmless) | 8 |
| Type mismatches | **1 CRITICAL** (layout_plan dict vs array) |
| Missing fields | 0 |
| Topology generators | 5/5 complete |
| Placement types handled | 6/6 complete |
| Nullable crash risks | 0 |

---

## 1. Field Mapping Table

### Root Level (`GalleryManifest` — ManifestModels.cs:17)

| Manifest Field | C# Field | Type Match | Status |
|---|---|---|---|
| `gallery_id` | `gallery_id` | string | ✅ Mapped |
| `schema_version` | `schema_version` | string | ✅ Mapped |
| `created_at` | `created_at` | string | ✅ Mapped |
| `locked_constraints` | `locked_constraints` | LockedConstraints | ✅ Mapped |
| `derived_parameters` | `derived_parameters` | DerivedParameters | ✅ Mapped |
| `layout_plan` | `layout_plan` | **Dict vs List** | ❌ **CRITICAL MISMATCH** |
| `placement_plan` | `placement_plan` | List\<PlacementInstruction\> | ✅ Mapped |
| `assets` | `assets` | List\<ArtworkAsset\> | ✅ Mapped |
| `semantic_analysis` | *(not in C#)* | — | ✅ Correctly ignored |
| `_internal` | *(not in C#)* | — | ✅ Correctly ignored |
| *(not in schema)* | `gallery_style` | string | ⚠️ Extra field (harmless) |

### layout_plan — THE CRITICAL MISMATCH

**Schema v2.1 (backend sends):**
```json
{
  "layout_plan": {
    "main": { "length": 15.0, "width": 5.0, "height": 2.8 },
    "alcove_1": { "width": 3.0, "depth": 2.5, "height": 2.8, "parent": "main", "side": "left", "position_along_parent": 5.0 }
  }
}
```

**Unity v2.0 (C# expects):**
```json
{
  "layout_plan": [
    { "room_id": "main", "data": { "length": 15.0, "width": 5.0, "height": 2.8 } },
    { "room_id": "alcove_1", "data": { "width": 3.0, "depth": 2.5, "height": 2.8, "parent": "main", "side": "left", "position_along_parent": 5.0 } }
  ]
}
```

**Why this breaks:** `GalleryManifest.layout_plan` is typed as `List<RoomLayoutEntry>` (ManifestModels.cs:26). Unity's `JsonUtility.FromJson` cannot parse a JSON object/dictionary into a C# List — it expects a JSON array `[...]`. When it encounters `{ "main": {...} }` instead of `[...]`, the field will deserialize as `null` or an empty list. The `Validate()` method (ManifestModels.cs:225) then returns `"Missing layout_plan"` and the gallery fails to load.

**Additionally:** Even if we changed the C# field to `Dictionary<string, LayoutData>`, `JsonUtility` does NOT support Dictionary deserialization at all. This is a fundamental limitation of Unity's built-in JSON parser.

### locked_constraints (`LockedConstraints` — ManifestModels.cs:262)

| Manifest Field | C# Field | Type Match | Status |
|---|---|---|---|
| `topology` | `topology` | string | ✅ Mapped |
| `rooms` | `rooms` | List\<RoomConstraint\> | ✅ Mapped |
| `theme` | `theme` | string | ✅ Mapped |
| *(not in schema)* | `gallery_style` | string | ⚠️ Extra (harmless) |

### locked_constraints.rooms[] (`RoomConstraint` — ManifestModels.cs:276)

| Manifest Field | C# Field | Type Match | Status |
|---|---|---|---|
| `id` | `id` | string | ✅ Mapped |
| `type` | `type` | string | ✅ Mapped |
| `content_type` | `content_type` | string | ✅ Mapped |
| `content_count` | `content_count` | int | ✅ Mapped |

**Note:** Schema room types include `"spoke"` and `"open_hall"` which are missing from `RoomTypes` constants (ManifestModels.cs:575). Not a runtime issue since generators compare strings directly, but the constants are incomplete.

### derived_parameters (`DerivedParameters` — ManifestModels.cs:294)

| Manifest Field | C# Field | Type Match | Status |
|---|---|---|---|
| `mood` | `mood` | string | ✅ Mapped |
| `pacing` | `pacing` | float | ✅ Mapped |
| `target_spacing_m` | `target_spacing_m` | float | ✅ Mapped |

### layout_plan values (`LayoutData` — ManifestModels.cs:304)

*These fields are correct IF the dict-vs-array parsing issue is resolved:*

| Manifest Field | C# Field | Type Match | Status |
|---|---|---|---|
| `length` | `length` | float | ✅ Mapped |
| `width` | `width` | float | ✅ Mapped |
| `height` | `height` | float | ✅ Mapped |
| `depth` | `depth` | float | ✅ Mapped |
| `parent` | `parent` | string | ✅ Mapped (alcoves) |
| `side` | `side` | string | ✅ Mapped (alcoves) |
| `position_along_parent` | `position_along_parent` | float | ✅ Mapped (alcoves) |
| `doorways` | `doorways` | List\<Doorway\> | ✅ Mapped |
| `direction_from_hub` | `direction_from_hub` | string | ✅ Mapped (spokes) |
| `partitions` | `partitions` | List\<PartitionDefinition\> | ✅ Mapped (open_hall) |
| `entry_wall` | `entry_wall` | string | ✅ Mapped |
| `entry_position` | `entry_position` | float | ✅ Mapped |
| `entry_width` | `entry_width` | float | ✅ Mapped |

### doorways[] (`Doorway` — ManifestModels.cs:401)

| Manifest Field | C# Field | Type Match | Status |
|---|---|---|---|
| `wall` | `wall` | string (north/east/south/west/front/back/left/right) | ✅ Mapped |
| `position` | `position` | float | ✅ Mapped |
| `width` | `width` | float | ✅ Mapped |
| `connects_to` | `connects_to` | string | ✅ Mapped |

### partitions[] (`PartitionDefinition` — ManifestModels.cs:411)

| Manifest Field | C# Field | Type Match | Status |
|---|---|---|---|
| `id` | `id` | string | ✅ Mapped |
| `x` | `x` | float | ✅ Mapped |
| `z` | `z` | float | ✅ Mapped |
| `rotation` | `rotation` | float | ✅ Mapped |
| `length` | `length` | float | ✅ Mapped |
| `height` | `height` | float | ✅ Mapped |

### placement_plan[] (`PlacementInstruction` — ManifestModels.cs:464)

| Manifest Field | C# Field | Type Match | Status |
|---|---|---|---|
| `asset_id` | `asset_id` | string | ✅ Mapped |
| `room_id` | `room_id` | string | ✅ Mapped |
| `wall` | `wall` | string | ✅ Mapped |
| `position_along_wall` | `position_along_wall` | float (meters) | ✅ Mapped |
| `height` | `height` | float (meters) | ✅ Mapped |
| `is_hero` | `is_hero` | bool | ✅ Mapped |
| `surface` | `surface` | string | ✅ Mapped (open_hall) |
| `side` | `side` | string | ✅ Mapped (partition front/back) |
| `position_along` | `position_along` | float (meters) | ✅ Mapped (open_hall) |
| `floor_position` | `floor_position` | FloorPosition {x, z} | ✅ Mapped (sculptures) |
| *(not in schema)* | `local_position` | Vector3Serializable | ⚠️ Extra legacy field |

### floor_position (`FloorPosition` — ManifestModels.cs:489)

| Manifest Field | C# Field | Type Match | Status |
|---|---|---|---|
| `x` | `x` | float | ✅ Mapped |
| `z` | `z` | float | ✅ Mapped |

### assets[] (`ArtworkAsset` — ManifestModels.cs:514)

| Manifest Field | C# Field | Type Match | Status |
|---|---|---|---|
| `id` | `id` | string | ✅ Mapped |
| `url` | `url` | string | ✅ Mapped |
| `type` | `type` | string ("2d" / "sculpture") | ✅ Mapped |
| `width` | `width` | float (meters, 2D only) | ✅ Mapped |
| `height` | `height` | float (meters, 2D only) | ✅ Mapped |
| `prompt` | `prompt` | string | ✅ Mapped |
| `visual_weight` | `visual_weight` | float (0-1) | ✅ Mapped |
| `role` | `role` | string | ✅ Mapped |
| `interpretation` | *(not in C#)* | — | ✅ Correctly ignored |
| `similarities` | *(not in C#)* | — | ✅ Correctly ignored |
| *(not in schema)* | `scale` | float | ⚠️ Extra (used for sculpture sizing, defaults to 0.5 if missing) |
| *(not in schema)* | `theme_alignment` | float | ⚠️ Extra (unused in placement) |
| *(not in schema)* | `distinctiveness` | float | ⚠️ Extra (unused in placement) |
| *(not in schema)* | `cluster_id` | string | ⚠️ Extra (used for cluster arrangement) |

---

## 2. Topology Coverage

| Topology | Generator Script | Exists? | Reads Correct Fields? | Key Fields Used |
|---|---|---|---|---|
| `linear_corridor` | `LinearCorridorGenerator.cs` | ✅ | ✅ | length, width, height |
| `linear_with_alcoves` | `LinearWithAlcovesGenerator.cs` | ✅ | ✅ | parent, side, position_along_parent, width, depth, height |
| `branching_rooms` | `BranchingRoomsGenerator.cs` | ✅ | ✅ | width, depth/length, height, doorways[] |
| `hub_and_spoke` | `HubAndSpokeGenerator.cs` | ✅ | ✅ | width, depth, height, doorways[], direction_from_hub |
| `open_hall` | `OpenHallGenerator.cs` | ✅ | ✅ | width, depth, height, partitions[] |

All 5 topologies are fully implemented. `GalleryOrchestrator.SetupTopologyGenerator()` (line 354) covers all 5 via `TopologyTypes` constants.

### Topology-specific details

- **linear_corridor** — Supports multi-room corridors chained along +Z. Generates width-transition walls between rooms of different widths. Registers left/right/back/front walls per room.
- **linear_with_alcoves** — Finds corridor by `type == "corridor"`, alcoves by `type == "alcove"`. Falls back to first room if no explicit corridor type. Segments corridor walls to create alcove openings. Registers openings with WallSpaceManager for artwork collision avoidance. Alcove rooms get back/left/right walls registered.
- **branching_rooms** — Places rooms sequentially along Z. Reads `doorways[]` to determine which walls get openings. Front/back walls omitted between connected rooms. Performance-capped at 5 rooms for Quest.
- **hub_and_spoke** — Finds hub by `type == "hub"`. Hub generated at world origin. Spokes positioned using `direction_from_hub` (north/south/east/west). Uses `GetSpokePosition()` to calculate offsets. Reads hub `doorways[]` to determine which hub walls get openings. Walls with doorways are excluded from placement surfaces.
- **open_hall** — Single room centered at origin. `depth` used if > 0, otherwise falls back to `length`. Generates 4 outer walls. Partitions built from `partitions[]` with rotation support. Both sides of each partition registered as `{partition_id}_front` and `{partition_id}_back` for artwork placement. Partition x/z converted from hall-local to world coordinates via `HallToWorld()`.

---

## 3. Placement Coverage

### Placement types handled

| Placement Type | Manifest Fields Used | C# Handler | Status |
|---|---|---|---|
| Wall-mounted 2D | `wall` + `position_along_wall` + `height` | `ArtworkPlacer.PlaceImage()` → `TopologyGenerator.GetWallPlacement()` | ✅ |
| Open hall wall surface | `surface: "wall_left"` etc. | `PlaceImage()` strips `wall_` prefix → standard wall placement | ✅ |
| Open hall partition | `surface: "partition_1"` + `side` + `position_along` + `height` | `PlaceImage()` builds `{surface}_{side}` key → `GetPartitionPlacement()` | ✅ |
| Floor sculpture (explicit) | `floor_position: {x, z}` | `PlaceSculpture()` converts hall coords to room-relative via `GetFloorPlacement()` | ✅ |
| Floor sculpture (center) | `wall: "center"` (no position) | `PlaceSculpture()` auto-distributes along room Z center line | ✅ |
| Floor sculpture (legacy) | `local_position: {x, y, z}` | `PlaceSculpture()` uses `ToVector3()` | ✅ (not in v2.1 schema) |

### Special wall keyword handling

| Keyword | Resolution | Code Location |
|---|---|---|
| `"hero_wall"` | Resolved to focal wall via `TryGetFocalWallPlacement()` | ArtworkPlacer.cs:541-577 |
| `"auto"` | Resolved to longest non-hero wall via `TryGetAutoWallPlacement()` | ArtworkPlacer.cs:579-616 |

### Position units verification

All position values confirmed to be in **meters** (not normalized 0-1):
- `position_along_wall` → `startPoint + direction * positionAlongWall` (TopologyGenerator.cs:155)
- `height` → `room.floorY + height` (TopologyGenerator.cs:159)
- `floor_position.x/z` → direct world coordinates via `HallToWorld()` (OpenHallGenerator.cs:307-311)
- `position_along` → `startPoint + direction * positionAlong` (TopologyGenerator.cs:284)

**No 0-1 normalization applied anywhere.** ✅

### Asset lookup and URL pipeline

1. `ArtworkPlacer.PlaceAllArtwork()` → `manifest.GetAssetById(placement.asset_id)` (ManifestModels.cs:183)
2. Frame/pedestal created with display component attached
3. `GalleryOrchestrator.LoadAllArtworkAssets()` matches display names to asset IDs
4. 2D images: `ImageDisplay.LoadImage(asset.url, ...)` → `UnityWebRequestTexture.GetTexture(url)` → texture applied to quad material
5. Sculptures: `SculptureDisplay.LoadSculpture(asset.url, ...)` → `UnityWebRequest.Get(url)` → `GltfImport().Load(glbData)` → `InstantiateSceneAsync()` → `NormalizeModel()` scales and centers on pedestal

### is_hero vs role handling

Both fields are checked consistently — no conflict:
- `PlaceImage()` (ArtworkPlacer.cs:674): `bool isHero = placement.is_hero || asset.GetRole() == "hero"`
- `AssignHeroToFocalWall()` (ArtworkPlacer.cs:211): `bool isHero = placement.is_hero || NormalizeRole(asset.GetRole()) == "hero"`
- Hero effects: 1.3x frame scale, focal wall assignment, spotlight intensity 2.8 (vs 1.35 ambient)
- If backend sets both `is_hero: true` and `role: "hero"`, no double-application — the boolean is just OR'd

---

## 4. Nullable Field Handling

| Optional Field | Null Guard Location | Default | Crash Risk |
|---|---|---|---|
| `placement.surface` | ArtworkPlacer.cs:434,655 | skipped if null | None |
| `placement.side` | ArtworkPlacer.cs:450,663 | `"front"` | None |
| `placement.position_along` | ArtworkPlacer.cs:445,459,666 | falls back to `position_along_wall` | None |
| `placement.floor_position` | ArtworkPlacer.cs:745 | null-check before use | None |
| `placement.local_position` | ArtworkPlacer.cs:741 | null-check before use | None |
| `placement.is_hero` | bool default | `false` | None |
| `asset.role` | ManifestModels.cs:553 | `"ambient"` | None |
| `asset.visual_weight` | ManifestModels.cs:554 | `0.3f` | None |
| `asset.cluster_id` | ManifestModels.cs:555 | `null` (callers check) | None |
| `asset.scale` | ArtworkPlacer.cs:920 | `0.5f` | None |
| `asset.width/height` | ArtworkPlacer.cs:671,314 | `1.2f` / context-dependent | None |
| `dims.doorways` | All generators | null-checked before iteration | None |
| `dims.partitions` | OpenHallGenerator.cs:112 | null-checked before iteration | None |
| `dims.direction_from_hub` | HubAndSpokeGenerator.cs:105 | defaults to north | None |
| `derived_parameters` | GalleryOrchestrator.cs:242 | `?.mood ?? "calm"` | None |

**No crash risks identified from null optional fields.** All nullable paths are properly guarded.

---

## 5. Recommendations (Ordered by Severity)

### CRITICAL — Must fix before v2.1 manifests will load

**1. Fix `layout_plan` deserialization for v2.1 dictionary format**

The backend v2.1 schema sends `layout_plan` as `{ "room_id": { ...dimensions } }` (JSON object/dictionary), but Unity expects `[ { "room_id": "...", "data": { ... } } ]` (JSON array). `JsonUtility` cannot deserialize dictionaries at all.

**Recommended fix — Option A: Switch to Newtonsoft.Json**
- Add package: `com.unity.nuget.newtonsoft-json`
- `ManifestModels.cs`: Change `layout_plan` to `Dictionary<string, LayoutData>`. Remove `RoomLayoutEntry` wrapper. Simplify `GetLayoutMap()` and `GetLayoutPlanWrapper()`.
- `ManifestLoader.cs`: Replace `JsonUtility.FromJson<GalleryManifest>(json)` with `JsonConvert.DeserializeObject<GalleryManifest>(json)`
- Scope: 2 files, ~50 lines changed

**Alternative — Option B: Manual pre-processing**
- In `ParseManifestJson()`, parse raw JSON to detect dict format, convert to array format, then feed to `JsonUtility`
- Avoids new dependency but is fragile

**Alternative — Option C: Backend compatibility**
- Have the backend continue to send v2.0 array format
- Fastest fix but creates tech debt

### MEDIUM — Should fix for completeness

**2. Add missing `RoomTypes` constants**
Add `Spoke = "spoke"` and `OpenHall = "open_hall"` to `RoomTypes` (ManifestModels.cs:575).

**3. Confirm `cluster_id` with backend team**
`ArtworkAsset.cluster_id` is used for cluster arrangement (ArtworkPlacer.cs:279-422) but is not in the v2.1 schema. If backend stops sending it, cluster grouping silently becomes a no-op. Verify status.

**4. Confirm `scale` for sculptures with backend team**
`ArtworkAsset.scale` is not in v2.1 schema but is used for sculpture sizing (ArtworkPlacer.cs:920). Default of 0.5m may be too small for some sculptures.

### LOW — Informational cleanup

**5. Update version comment** — ManifestModels.cs line 5 says "v2.0" — update to v2.1 after fix.

**6. Remove legacy `local_position`** — `PlacementInstruction.local_position` is not in v2.1 schema. Can be removed once v2.1 is stable.

**7. Clean up extra C# fields** — `gallery_style` (top-level), `theme_alignment`, `distinctiveness`, obsolete `RoomConstraint` spatial fields — harmless but could be removed for clarity.

**8. Add schema version validation** — `Validate()` does not check `schema_version`. Consider warning if version is not `"2.1"`.

---

## File Index

| File | Role | Key Classes |
|---|---|---|
| `Assets/Scripts/ManifestModels.cs` | Data model definitions | GalleryManifest, LockedConstraints, RoomConstraint, DerivedParameters, LayoutData, RoomLayoutEntry, LayoutPlanWrapper, PlacementInstruction, FloorPosition, ArtworkAsset, Doorway, PartitionDefinition, RoomDimensions, AlcoveDimensions |
| `Assets/Scripts/ManifestLoader.cs` | JSON loading/parsing | ManifestLoader, LoadResult |
| `Assets/Scripts/GalleryOrchestrator.cs` | 5-step pipeline controller | GalleryOrchestrator, GalleryLoadState |
| `Assets/Scripts/TopologyGenerator.cs` | Abstract base generator + placement | TopologyGenerator, PlacementResult, GeneratedRoom, WallInfo |
| `Assets/Scripts/LinearCorridorGenerator.cs` | linear_corridor topology | LinearCorridorGenerator |
| `Assets/Scripts/LinearWithAlcovesGenerator.cs` | linear_with_alcoves topology | LinearWithAlcovesGenerator |
| `Assets/Scripts/BranchingRoomsGenerator.cs` | branching_rooms topology | BranchingRoomsGenerator |
| `Assets/Scripts/HubAndSpokeGenerator.cs` | hub_and_spoke topology | HubAndSpokeGenerator |
| `Assets/Scripts/OpenHallGenerator.cs` | open_hall topology | OpenHallGenerator |
| `Assets/Scripts/ArtworkPlacer.cs` | Frame/pedestal creation + placement logic | ArtworkPlacer |
| `Assets/Scripts/ImageDisplay.cs` | 2D image download/display | ImageDisplay |
| `Assets/Scripts/SculptureDisplay.cs` | GLB download/display (glTFast) | SculptureDisplay |
| `Assets/Scripts/WallSpaceManager.cs` | Artwork collision avoidance | WallSpaceManager |
| `Assets/Scripts/ThemeManager.cs` | Theme palette + mood lighting | ThemeManager |
