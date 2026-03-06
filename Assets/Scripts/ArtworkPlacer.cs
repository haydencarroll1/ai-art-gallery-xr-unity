using UnityEngine;
using System.Collections.Generic;

// Reads placement instructions from the manifest and turns them into
// GameObjects in the scene. For each instruction it:
//   1. Asks the TopologyGenerator for the world position on the correct wall
//   2. Creates a frame (for 2D art) or pedestal (for sculptures)
//   3. Attaches an ImageDisplay or SculptureDisplay so the asset can load later
//
// Works with any TopologyGenerator implementation.

public class ArtworkPlacer : MonoBehaviour
{
    [Header("Frame Settings")]
    [Tooltip("Border width around image frames")]
    public float frameBorderWidth = 0.08f;

    [Tooltip("Depth of the frame (how far it sticks out from wall)")]
    public float frameDepth = 0.05f;

    [Header("Pedestal Settings")]
    [Tooltip("Default pedestal height for sculptures")]
    public float pedestalHeight = 1.0f;

    [Tooltip("Pedestal diameter")]
    public float pedestalDiameter = 0.5f;

    [Header("Hero Piece Settings")]
    [Tooltip("Scale multiplier for hero pieces")]
    public float heroScaleMultiplier = 1.4f;

    [Tooltip("Add spotlight to hero pieces")]
    public bool spotlightHeroPieces = true;

    [Header("Gallery Spotlights")]
    [Tooltip("When true, adds a spotlight above each wall-mounted artwork")]
    public bool spotlightWallArtwork = true;

    [Tooltip("Hard cap for spotlight count to keep Quest performance stable")]
    public int maxArtworkSpotlights = 24;

    [Tooltip("Baseline distance from wall toward room center for artwork spotlights")]
    public float spotlightWallOffset = 1f;

    [Tooltip("Mount height below the ceiling for artwork spotlights")]
    public float spotlightHeightAboveFrame = 0.3f;

    [Header("Debug")]
    public bool debugMode = false;

    [Header("Evaluation")]
    [Tooltip("When true, ignores role/weight - uniform frames, no spotlights, sequential placement")]
    public bool useSequentialBaseline = false;

    // Set by PlaceAllArtwork, used by all placement methods
    private TopologyGenerator topologyGenerator;

    // Parent object for all placed artwork (destroyed on clear)
    private GameObject artworkParent;

    // We keep lists so the orchestrator can iterate them to trigger asset loading
    private List<ImageDisplay> imageDisplays = new List<ImageDisplay>();
    private List<SculptureDisplay> sculptureDisplays = new List<SculptureDisplay>();

    // Cached materials created once per placement pass
    private Material frameMaterial;
    private Material frameBorderMaterial;
    private Material pedestalMaterial;
    
    // Track center sculptures per room for auto-distribution
    // Key: room_id, Value: (total count, current index)
    private Dictionary<string, (int total, int current)> centerSculptureCounters;
    private string activeGalleryStyle = GalleryStyleIds.Contemporary;
    private int spawnedArtworkSpotlights;

    public IReadOnlyList<ImageDisplay> ImageDisplays => imageDisplays;
    public IReadOnlyList<SculptureDisplay> SculptureDisplays => sculptureDisplays;

    // Called by GalleryOrchestrator after topology generation is done.
    // Iterates every PlacementInstruction in the manifest and creates
    // the corresponding frame or pedestal at the right world position.
    public void PlaceAllArtwork(GalleryManifest manifest, TopologyGenerator generator)
    {
        if (manifest == null)
        {
            Debug.LogError("[ArtworkPlacer] Manifest is null!");
            return;
        }
        if (generator == null)
        {
            Debug.LogError("[ArtworkPlacer] TopologyGenerator is null!");
            return;
        }

        topologyGenerator = generator;
        activeGalleryStyle = manifest.GetGalleryStyle();
        spawnedArtworkSpotlights = 0;

        ClearAllArtwork();
        InitializeMaterials();

        // Parent sits at world origin so frame positions line up with wall positions
        artworkParent = new GameObject("PlacedArtwork");
        artworkParent.transform.SetParent(transform);
        artworkParent.transform.position = Vector3.zero;
        artworkParent.transform.rotation = Quaternion.identity;

        if (debugMode)
            Debug.Log($"[ArtworkPlacer] Placing {manifest.placement_plan?.Count ?? 0} artworks");

        if (manifest.placement_plan != null)
        {
            // Pre-count center sculptures per room for auto-distribution
            centerSculptureCounters = new Dictionary<string, (int total, int current)>();
            foreach (var placement in manifest.placement_plan)
            {
                ArtworkAsset asset = manifest.GetAssetById(placement.asset_id);
                if (asset != null && asset.type == "sculpture" && IsCenterPlacementWithoutPosition(placement))
                {
                    string roomId = placement.room_id ?? "main";
                    if (centerSculptureCounters.ContainsKey(roomId))
                    {
                        var counter = centerSculptureCounters[roomId];
                        centerSculptureCounters[roomId] = (counter.total + 1, 0);
                    }
                    else
                    {
                        centerSculptureCounters[roomId] = (1, 0);
                    }
                }
            }
            
            // Sort by role priority unless baseline mode is on
            var placements = new List<PlacementInstruction>(manifest.placement_plan);
            if (!useSequentialBaseline)
            {
                // Resolve backend keywords "auto" and "hero_wall" into concrete
                // wall names *before* hero/cluster arrangement runs, so that
                // TryResolveWallTarget and GetWallPlacement see real wall IDs.
                ResolveSpecialWalls(manifest, placements);

                AssignHeroToFocalWall(manifest, placements);
                ArrangeClusterPlacements(manifest, placements);

                placements.Sort((a, b) => {
                    var assetA = manifest.GetAssetById(a.asset_id);
                    var assetB = manifest.GetAssetById(b.asset_id);
                    int roleCompare = GetRoleOrder(assetA?.GetRole()) - GetRoleOrder(assetB?.GetRole());
                    if (roleCompare != 0) return roleCompare;
                    return (assetB?.GetVisualWeight() ?? 0f).CompareTo(assetA?.GetVisualWeight() ?? 0f);
                });
            }

            foreach (var placement in placements)
            {
                ArtworkAsset asset = manifest.GetAssetById(placement.asset_id);
                if (asset == null)
                {
                    Debug.LogWarning($"[ArtworkPlacer] Asset not found: {placement.asset_id}");
                    continue;
                }
                PlaceArtwork(placement, asset);
            }
        }

        if (debugMode)
            Debug.Log($"[ArtworkPlacer] Placed {imageDisplays.Count} images, {sculptureDisplays.Count} sculptures");
    }
    
    // Check if this is a center placement without specific position coordinates
    private bool IsCenterPlacementWithoutPosition(PlacementInstruction placement)
    {
        bool isCenter = placement.wall == "center" || string.IsNullOrEmpty(placement.wall);
        bool hasNoPosition = placement.local_position == null && placement.floor_position == null;
        return isCenter && hasNoPosition;
    }

    private void AssignHeroToFocalWall(GalleryManifest manifest, List<PlacementInstruction> placements)
    {
        // Guard: if the backend already placed a hero on a specific wall, respect it.
        bool backendHasPlacedHero = placements.Exists(p => p.is_hero && !string.IsNullOrEmpty(p.wall));
        if (backendHasPlacedHero)
        {
            if (debugMode) Debug.Log("[ArtworkPlacer] Respecting backend hero placement.");
            return;
        }

        if (manifest == null || placements == null || placements.Count == 0 || topologyGenerator == null)
        {
            return;
        }

        PlacementInstruction heroPlacement = null;
        ArtworkAsset heroAsset = null;
        float bestScore = float.MinValue;

        for (int i = 0; i < placements.Count; i++)
        {
            PlacementInstruction placement = placements[i];
            if (placement == null)
            {
                continue;
            }

            ArtworkAsset asset = manifest.GetAssetById(placement.asset_id);
            if (asset == null || !asset.Is2D)
            {
                continue;
            }

            bool isHero = placement.is_hero || NormalizeRole(asset.GetRole()) == "hero";
            if (!isHero)
            {
                continue;
            }

            float score = asset.GetVisualWeight();
            if (score > bestScore)
            {
                bestScore = score;
                heroPlacement = placement;
                heroAsset = asset;
            }
        }

        if (heroPlacement == null || heroAsset == null)
        {
            return;
        }

        float heroWidth = (heroAsset.width > 0f ? heroAsset.width : 1.2f) * heroScaleMultiplier;
        float requiredWidth = heroWidth + GetFrameBorderWidthForStyle() * 2f;

        if (!topologyGenerator.TryGetFocalWallPlacement(requiredWidth, out TopologyGenerator.FocalWallPlacement focalWall) ||
            focalWall == null ||
            string.IsNullOrEmpty(focalWall.roomId) ||
            string.IsNullOrEmpty(focalWall.wallName))
        {
            return;
        }

        string oldRoom = heroPlacement.room_id;
        string oldWall = heroPlacement.wall;
        float oldPosition = heroPlacement.position_along_wall;

        heroPlacement.room_id = focalWall.roomId;
        heroPlacement.wall = focalWall.wallName;
        heroPlacement.position_along_wall = focalWall.positionAlongWall;
        heroPlacement.surface = null;
        heroPlacement.side = null;
        heroPlacement.position_along = 0f;

        if (debugMode)
        {
            Debug.Log($"[ArtworkPlacer] Hero focal wall override: '{heroAsset.id}' {oldRoom}/{oldWall}@{oldPosition:F2} -> {focalWall.roomId}/{focalWall.wallName}@{focalWall.positionAlongWall:F2}");
        }
    }

    private class ClusterItem
    {
        public PlacementInstruction placement;
        public ArtworkAsset asset;
        public float spanWidth;
        public float sourcePositionAlong;
    }

    private class ResolvedWallTarget
    {
        public string roomId;
        public string wallKey;
        public string wallName;
        public bool useSurface;
        public string surface;
        public string side;
        public float positionAlong;
    }

    private void ArrangeClusterPlacements(GalleryManifest manifest, List<PlacementInstruction> placements)
    {
        if (manifest == null || placements == null || placements.Count == 0 || topologyGenerator == null)
        {
            return;
        }

        Dictionary<string, List<ClusterItem>> clusters = new Dictionary<string, List<ClusterItem>>();
        float borderWidth = GetFrameBorderWidthForStyle();

        for (int i = 0; i < placements.Count; i++)
        {
            PlacementInstruction placement = placements[i];
            if (placement == null)
            {
                continue;
            }

            ArtworkAsset asset = manifest.GetAssetById(placement.asset_id);
            if (asset == null || !asset.Is2D || NormalizeRole(asset.GetRole()) != "cluster")
            {
                continue;
            }

            string clusterId = asset.GetClusterId();
            if (string.IsNullOrEmpty(clusterId))
            {
                continue;
            }

            if (!TryResolveWallTarget(placement, out var resolved) || !TryGetWallLength(resolved.roomId, resolved.wallKey, out _))
            {
                continue;
            }

            float baseWidth = asset.width > 0f ? asset.width : 1.2f;
            float spanWidth = Mathf.Max(0.55f, baseWidth + borderWidth * 2f);

            if (!clusters.TryGetValue(clusterId, out var clusterItems))
            {
                clusterItems = new List<ClusterItem>();
                clusters[clusterId] = clusterItems;
            }

            clusterItems.Add(new ClusterItem
            {
                placement = placement,
                asset = asset,
                spanWidth = spanWidth,
                sourcePositionAlong = resolved.positionAlong
            });
        }

        foreach (var clusterKvp in clusters)
        {
            List<ClusterItem> clusterItems = clusterKvp.Value;
            if (clusterItems == null || clusterItems.Count < 2 || clusterItems.Count > 6)
            {
                continue;
            }

            clusterItems.Sort((a, b) => b.asset.GetVisualWeight().CompareTo(a.asset.GetVisualWeight()));
            ClusterItem anchor = clusterItems[0];

            if (!TryResolveWallTarget(anchor.placement, out var anchorTarget))
            {
                continue;
            }
            if (!TryGetWallLength(anchorTarget.roomId, anchorTarget.wallKey, out float wallLength))
            {
                continue;
            }

            clusterItems.Sort((a, b) => a.sourcePositionAlong.CompareTo(b.sourcePositionAlong));

            float minEdgePadding = 0.3f;
            float availableWidth = Mathf.Max(0.4f, wallLength - minEdgePadding * 2f);
            float totalSpanWidths = 0f;
            for (int i = 0; i < clusterItems.Count; i++)
            {
                totalSpanWidths += clusterItems[i].spanWidth;
            }

            float gap = 0.28f;
            int gapCount = clusterItems.Count - 1;
            float needed = totalSpanWidths + gapCount * gap;

            if (needed > availableWidth && gapCount > 0)
            {
                float reducedGap = (availableWidth - totalSpanWidths) / gapCount;
                gap = Mathf.Max(0.1f, reducedGap);
                needed = totalSpanWidths + gapCount * gap;
            }

            if (needed > availableWidth)
            {
                float shrink = Mathf.Clamp(availableWidth / Mathf.Max(0.01f, totalSpanWidths), 0.65f, 1f);
                totalSpanWidths = 0f;
                for (int i = 0; i < clusterItems.Count; i++)
                {
                    clusterItems[i].spanWidth *= shrink;
                    totalSpanWidths += clusterItems[i].spanWidth;
                }
                needed = totalSpanWidths + gapCount * gap;
            }

            float clusterCenter = anchorTarget.positionAlong > 0.01f ? anchorTarget.positionAlong : wallLength * 0.5f;
            float minCenter = minEdgePadding + needed * 0.5f;
            float maxCenter = wallLength - minEdgePadding - needed * 0.5f;
            if (maxCenter < minCenter)
            {
                clusterCenter = wallLength * 0.5f;
            }
            else
            {
                clusterCenter = Mathf.Clamp(clusterCenter, minCenter, maxCenter);
            }

            float cursor = clusterCenter - needed * 0.5f;
            for (int i = 0; i < clusterItems.Count; i++)
            {
                ClusterItem item = clusterItems[i];
                float posAlong = cursor + item.spanWidth * 0.5f;
                float minPos = minEdgePadding + item.spanWidth * 0.5f;
                float maxPos = wallLength - minEdgePadding - item.spanWidth * 0.5f;
                if (maxPos < minPos)
                {
                    posAlong = wallLength * 0.5f;
                }
                else
                {
                    posAlong = Mathf.Clamp(posAlong, minPos, maxPos);
                }
                ApplyResolvedWallTarget(item.placement, anchorTarget, posAlong);
                cursor += item.spanWidth + gap;
            }

            if (debugMode)
            {
                Debug.Log($"[ArtworkPlacer] Cluster '{clusterKvp.Key}' grouped with {clusterItems.Count} pieces on {anchorTarget.roomId}/{anchorTarget.wallKey}");
            }
        }
    }

    private bool TryResolveWallTarget(PlacementInstruction placement, out ResolvedWallTarget target)
    {
        target = null;
        if (placement == null || string.IsNullOrEmpty(placement.room_id))
        {
            return false;
        }

        string roomId = placement.room_id;

        if (!string.IsNullOrEmpty(placement.surface))
        {
            if (placement.surface.StartsWith("wall_"))
            {
                string wall = placement.surface.Substring(5);
                target = new ResolvedWallTarget
                {
                    roomId = roomId,
                    wallKey = wall,
                    wallName = wall,
                    useSurface = false,
                    positionAlong = placement.position_along > 0f ? placement.position_along : placement.position_along_wall
                };
                return true;
            }

            string side = string.IsNullOrEmpty(placement.side) ? "front" : placement.side;
            target = new ResolvedWallTarget
            {
                roomId = roomId,
                wallKey = $"{placement.surface}_{side}",
                wallName = null,
                useSurface = true,
                surface = placement.surface,
                side = side,
                positionAlong = placement.position_along > 0f ? placement.position_along : placement.position_along_wall
            };
            return true;
        }

        if (!string.IsNullOrEmpty(placement.wall) && placement.wall != WallNames.Center)
        {
            target = new ResolvedWallTarget
            {
                roomId = roomId,
                wallKey = placement.wall,
                wallName = placement.wall,
                useSurface = false,
                positionAlong = placement.position_along_wall
            };
            return true;
        }

        return false;
    }

    private bool TryGetWallLength(string roomId, string wallKey, out float wallLength)
    {
        wallLength = 0f;
        if (topologyGenerator == null || string.IsNullOrEmpty(roomId) || string.IsNullOrEmpty(wallKey))
        {
            return false;
        }

        Dictionary<string, GeneratedRoom> rooms = topologyGenerator.GetGeneratedRooms();
        if (rooms == null || !rooms.TryGetValue(roomId, out var room) || room == null || room.walls == null)
        {
            return false;
        }

        if (!room.walls.TryGetValue(wallKey, out var wallInfo) || wallInfo == null)
        {
            return false;
        }

        wallLength = wallInfo.length;
        return wallLength > 0.4f;
    }

    private void ApplyResolvedWallTarget(PlacementInstruction placement, ResolvedWallTarget target, float positionAlong)
    {
        if (placement == null || target == null)
        {
            return;
        }

        placement.room_id = target.roomId;
        if (target.useSurface)
        {
            placement.surface = target.surface;
            placement.side = target.side;
            placement.position_along = Mathf.Max(0f, positionAlong);
            placement.wall = null;
            placement.position_along_wall = 0f;
        }
        else
        {
            placement.wall = target.wallName;
            placement.position_along_wall = Mathf.Max(0f, positionAlong);
            placement.surface = null;
            placement.side = null;
            placement.position_along = 0f;
        }
    }

    // ── Special wall keyword resolution ──────────────────────────────────
    //
    // The backend may send wall tags that are *semantic* rather than physical:
    //   "hero_wall" – place on the room's focal wall (end-wall, widest, etc.)
    //   "auto"      – pick the best available wall (longest non-hero wall)
    //
    // This method translates those tags into concrete wall names and positions
    // so downstream code (cluster arrangement, GetWallPlacement) never sees them.

    private const string WallTag_Auto = "auto";
    private const string WallTag_HeroWall = "hero_wall";

    private void ResolveSpecialWalls(GalleryManifest manifest, List<PlacementInstruction> placements)
    {
        if (placements == null || placements.Count == 0 || topologyGenerator == null)
            return;

        // First pass: resolve "hero_wall" so we know which wall name it maps to.
        // We track the resolved hero wall per room so "auto" can avoid it.
        Dictionary<string, string> heroWallPerRoom = new Dictionary<string, string>();

        foreach (var placement in placements)
        {
            if (placement == null || placement.wall != WallTag_HeroWall) continue;

            string roomId = placement.room_id ?? "main";
            ArtworkAsset asset = manifest.GetAssetById(placement.asset_id);
            float artworkWidth = (asset != null && asset.width > 0f) ? asset.width : 1.2f;
            float totalWidth = artworkWidth + GetFrameBorderWidthForStyle() * 2f;

            if (topologyGenerator.TryGetFocalWallPlacement(totalWidth, out var focalWall) && focalWall != null)
            {
                string oldWall = placement.wall;
                placement.wall = focalWall.wallName;
                placement.room_id = focalWall.roomId;
                placement.position_along_wall = focalWall.positionAlongWall;
                heroWallPerRoom[focalWall.roomId] = focalWall.wallName;

                if (debugMode)
                    Debug.Log($"[ArtworkPlacer] Resolved '{oldWall}' -> {focalWall.roomId}/{focalWall.wallName}@{focalWall.positionAlongWall:F2} for {placement.asset_id}");
            }
            else
            {
                // Fallback: treat as left wall, centered
                placement.wall = WallNames.Left;
                placement.position_along_wall = 0f; // will be clamped by WallSpaceManager
                Debug.LogWarning($"[ArtworkPlacer] Could not resolve 'hero_wall' for {placement.asset_id} in {roomId}, falling back to left wall");
            }
        }

        // Second pass: resolve "auto" — pick the longest wall that isn't the hero wall.
        foreach (var placement in placements)
        {
            if (placement == null || placement.wall != WallTag_Auto) continue;

            string roomId = placement.room_id ?? "main";
            ArtworkAsset asset = manifest.GetAssetById(placement.asset_id);
            float artworkWidth = (asset != null && asset.width > 0f) ? asset.width : 1.2f;
            float totalWidth = artworkWidth + GetFrameBorderWidthForStyle() * 2f;

            // Exclude the hero wall for this room so cluster/ambient pieces
            // don't end up overlapping the hero piece.
            string excludeWall = null;
            heroWallPerRoom.TryGetValue(roomId, out excludeWall);

            if (topologyGenerator.TryGetAutoWallPlacement(roomId, totalWidth, excludeWall, out var autoWall) && autoWall != null)
            {
                placement.wall = autoWall.wallName;
                placement.room_id = autoWall.roomId;
                // Keep the backend's position_along_wall if it sent one; otherwise center it
                if (placement.position_along_wall <= 0f)
                {
                    placement.position_along_wall = autoWall.positionAlongWall;
                }

                if (debugMode)
                    Debug.Log($"[ArtworkPlacer] Resolved 'auto' -> {autoWall.roomId}/{autoWall.wallName}@{placement.position_along_wall:F2} for {placement.asset_id}");
            }
            else
            {
                // Fallback: use right wall
                placement.wall = WallNames.Right;
                if (placement.position_along_wall <= 0f)
                    placement.position_along_wall = 2.5f;
                Debug.LogWarning($"[ArtworkPlacer] Could not resolve 'auto' for {placement.asset_id} in {roomId}, falling back to right wall");
            }
        }
    }

    // Destroys every frame/pedestal we created and resets the lists.
    public void ClearAllArtwork()
    {
        foreach (var display in imageDisplays)
            if (display != null) Destroy(display.gameObject);
        imageDisplays.Clear();

        foreach (var display in sculptureDisplays)
            if (display != null) Destroy(display.gameObject);
        sculptureDisplays.Clear();

        if (artworkParent != null)
        {
            Destroy(artworkParent);
            artworkParent = null;
        }

        // Destroy cached runtime materials to prevent memory leaks on reload
        if (frameMaterial != null) { Destroy(frameMaterial); frameMaterial = null; }
        if (frameBorderMaterial != null) { Destroy(frameBorderMaterial); frameBorderMaterial = null; }
        if (pedestalMaterial != null) { Destroy(pedestalMaterial); pedestalMaterial = null; }
    }

    // Routes to PlaceImage or PlaceSculpture based on asset type.
    private void PlaceArtwork(PlacementInstruction placement, ArtworkAsset asset)
    {
        if (asset.Is2D)
            PlaceImage(placement, asset);
        else if (asset.IsSculpture)
            PlaceSculpture(placement, asset);
        else
            Debug.LogWarning($"[ArtworkPlacer] Unknown asset type: {asset.type}");
    }

    // Creates a frame on the specified wall and attaches an ImageDisplay.
    private void PlaceImage(PlacementInstruction placement, ArtworkAsset asset)
    {
        // Figure out which wall surface to use. Open hall placements use a
        // "surface" field like "wall_left" or "partition_1" instead of "wall".
        string wallName = placement.wall;
        float positionAlong = placement.position_along_wall;

        if (!string.IsNullOrEmpty(placement.surface))
        {
            if (placement.surface.StartsWith("wall_"))
            {
                wallName = placement.surface.Substring(5);
            }
            else
            {
                string side = string.IsNullOrEmpty(placement.side) ? "front" : placement.side;
                wallName = $"{placement.surface}_{side}";
            }
            if (placement.position_along > 0f)
                positionAlong = placement.position_along;
        }

        // Calculate artwork dimensions for collision avoidance
        float artworkWidth = asset.width > 0 ? asset.width : 1.2f;
        
        // Hero pieces get scaled up
        bool isHero = placement.is_hero || NormalizeRole(asset.GetRole()) == "hero";
        if (isHero && !useSequentialBaseline)
        {
            artworkWidth *= heroScaleMultiplier;
        }
        
        // Add frame border to total width for collision detection
        float totalWidth = artworkWidth + GetFrameBorderWidthForStyle() * 2f;

        // Ask the topology generator for the exact world position + rotation
        // Pass artwork dimensions and ID for collision detection
        PlacementResult result = topologyGenerator.GetWallPlacement(
            placement.room_id, wallName, positionAlong, placement.height, totalWidth, asset.id
        );
        if (result == null)
        {
            Debug.LogError($"[ArtworkPlacer] Failed to get placement for {asset.id}");
            return;
        }

        // Final image dimensions - start from asset size, apply scaling
        float imageWidth = asset.width;
        float imageHeight = asset.height;
        
        if (!useSequentialBaseline)
        {
            // Hero pieces get scaled up for emphasis
            if (isHero)
            {
                imageWidth *= heroScaleMultiplier;
                imageHeight *= heroScaleMultiplier;
            }
        }

        GameObject frame = CreateImageFrame(asset.id, result.position, result.rotation, imageWidth, imageHeight);

        if (!useSequentialBaseline)
        {
            AddArtworkSpotlight(frame.transform, result, imageWidth, imageHeight, isHero, asset.GetRole(), asset.GetVisualWeight());
        }

        ImageDisplay display = frame.GetComponent<ImageDisplay>();
        if (display != null)
        {
            display.gameObject.name = asset.id;
            imageDisplays.Add(display);
        }

        if (debugMode)
            Debug.Log($"[ArtworkPlacer] Placed image '{asset.id}' role={asset.GetRole()} cluster={asset.GetClusterId() ?? "-"} at {result.position} on {placement.wall} wall");
    }

    // Creates a pedestal on the floor and attaches a SculptureDisplay.
    private void PlaceSculpture(PlacementInstruction placement, ArtworkAsset asset)
    {
        string roomId = placement.room_id ?? "main";
        
        // Work out the local position relative to room center.
        // The manifest can specify this as either local_position (Vector3)
        // or floor_position (x/z in hall coordinates).
        Vector3 localPos = Vector3.zero;
        
        if (placement.local_position != null)
        {
            localPos = placement.local_position.ToVector3();
        }
        else if (placement.floor_position != null)
        {
            RoomDimensions dims = topologyGenerator.GetRoomDimensions(roomId);
            if (dims != null)
            {
                float width = dims.width;
                float depth = dims.length > 0 ? dims.length : dims.depth;
                localPos = new Vector3(
                    placement.floor_position.x - width / 2f,
                    0f,
                    placement.floor_position.z - depth / 2f
                );
            }
            else
            {
                localPos = new Vector3(placement.floor_position.x, 0f, placement.floor_position.z);
            }
        }
        else if (IsCenterPlacementWithoutPosition(placement))
        {
            // Auto-distribute center sculptures along the room's center line
            // Evenly space them along the Z axis within the room
            RoomDimensions dims = topologyGenerator.GetRoomDimensions(roomId);
            if (dims != null && centerSculptureCounters.TryGetValue(roomId, out var counter))
            {
                int total = counter.total;
                int current = counter.current;
                
                // Calculate spacing along the room length
                float roomLength = dims.length > 0 ? dims.length : dims.depth;
                float spacing = roomLength / (total + 1);
                float zPos = spacing * (current + 1) - roomLength / 2f;  // Offset from room center
                
                localPos = new Vector3(0f, 0f, zPos);
                
                // Increment the counter for the next sculpture
                centerSculptureCounters[roomId] = (total, current + 1);
                
                if (debugMode)
                    Debug.Log($"[ArtworkPlacer] Auto-distributing sculpture {current + 1}/{total} in '{roomId}' at local Z={zPos:F2}");
            }
        }

        PlacementResult result = topologyGenerator.GetFloorPlacement(roomId, localPos);
        if (result == null)
        {
            Debug.LogError($"[ArtworkPlacer] Failed to get floor placement for {asset.id}");
            return;
        }

        GameObject pedestal = CreateSculpturePedestal(asset.id, result.position, asset.scale);

        SculptureDisplay display = pedestal.GetComponent<SculptureDisplay>();
        if (display != null)
            sculptureDisplays.Add(display);

        if (debugMode)
            Debug.Log($"[ArtworkPlacer] Placed sculpture '{asset.id}' at {result.position}");
    }

    // Builds a frame: a quad for the image + four border cubes around it.
    // The quad faces into the room (rotated 180 on Y because Unity quads default-face -Z).
    private GameObject CreateImageFrame(string name, Vector3 position, Quaternion rotation, float width, float height)
    {
        GameObject frame = new GameObject($"Frame_{name}");
        frame.transform.SetParent(artworkParent.transform);
        frame.transform.position = position;
        frame.transform.rotation = rotation;

        // Image surface quad
        GameObject surface = GameObject.CreatePrimitive(PrimitiveType.Quad);
        surface.name = "ImageSurface";
        surface.transform.SetParent(frame.transform);
        surface.transform.localPosition = Vector3.zero;
        surface.transform.localRotation = Quaternion.Euler(0, 180, 0);
        surface.transform.localScale = new Vector3(width, height, 1f);

        Renderer surfaceRenderer = surface.GetComponent<Renderer>();
        Material surfaceMaterial = EnsureMaterial(frameMaterial, "FrameSurfaceFallback", Rgb(235, 235, 235), 0f, 0.1f);
        if (surfaceMaterial != null)
        {
            surfaceRenderer.sharedMaterial = surfaceMaterial;
        }

        // The quad primitive comes with a collider we don't need
        Collider surfaceCollider = surface.GetComponent<Collider>();
        if (surfaceCollider != null) Destroy(surfaceCollider);

        CreateFrameBorder(frame.transform, width, height);

        // ImageDisplay will receive the downloaded texture later
        ImageDisplay display = frame.AddComponent<ImageDisplay>();
        display.targetRenderer = surfaceRenderer;
        display.texturePropertyName = "_BaseMap";

        return frame;
    }

    // Four thin cubes forming a border around the image quad.
    private void CreateFrameBorder(Transform parent, float width, float height)
    {
        float halfW = width / 2f;
        float halfH = height / 2f;
        float bw = GetFrameBorderWidthForStyle();
        float depth = frameDepth;

        // Slightly behind the quad (toward the wall)
        float borderZ = -depth / 2f;
        Material borderMat = EnsureMaterial(frameBorderMaterial, "FrameBorderFallback", Rgb(200, 200, 200), 0f, 0.2f);

        CreateBorderPiece(parent, "Border_Top",
            new Vector3(0, halfH + bw / 2f, borderZ),
            new Vector3(width + bw * 2, bw, depth), borderMat);

        CreateBorderPiece(parent, "Border_Bottom",
            new Vector3(0, -halfH - bw / 2f, borderZ),
            new Vector3(width + bw * 2, bw, depth), borderMat);

        CreateBorderPiece(parent, "Border_Left",
            new Vector3(-halfW - bw / 2f, 0, borderZ),
            new Vector3(bw, height, depth), borderMat);

        CreateBorderPiece(parent, "Border_Right",
            new Vector3(halfW + bw / 2f, 0, borderZ),
            new Vector3(bw, height, depth), borderMat);
    }

    private void CreateBorderPiece(Transform parent, string name, Vector3 localPos, Vector3 scale, Material mat)
    {
        GameObject piece = GameObject.CreatePrimitive(PrimitiveType.Cube);
        piece.name = name;
        piece.transform.SetParent(parent);
        piece.transform.localPosition = localPos;
        piece.transform.localRotation = Quaternion.identity;
        piece.transform.localScale = scale;
        Renderer renderer = piece.GetComponent<Renderer>();
        renderer.sharedMaterial = EnsureMaterial(mat, "FrameBorderNeutral", Rgb(200, 200, 200), 0f, 0.2f);

        Collider col = piece.GetComponent<Collider>();
        if (col != null) Destroy(col);
    }

    // Builds a pedestal from two cylinders (base + top disc) and an anchor
    // transform where the loaded GLB model will be parented.
    private GameObject CreateSculpturePedestal(string name, Vector3 position, float sculptureScale)
    {
        GameObject pedestal = new GameObject($"Pedestal_{name}");
        pedestal.transform.SetParent(artworkParent.transform);
        pedestal.transform.position = position;

        // Base cylinder
        GameObject baseObj = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        baseObj.name = "PedestalBase";
        baseObj.transform.SetParent(pedestal.transform);
        baseObj.transform.localPosition = new Vector3(0, pedestalHeight / 2f, 0);
        baseObj.transform.localScale = new Vector3(pedestalDiameter, pedestalHeight / 2f, pedestalDiameter);
        baseObj.GetComponent<Renderer>().sharedMaterial = pedestalMaterial;

        // Top disc (slightly wider than the base)
        GameObject topObj = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        topObj.name = "PedestalTop";
        topObj.transform.SetParent(pedestal.transform);
        topObj.transform.localPosition = new Vector3(0, pedestalHeight + 0.025f, 0);
        topObj.transform.localScale = new Vector3(pedestalDiameter * 1.2f, 0.025f, pedestalDiameter * 1.2f);
        topObj.GetComponent<Renderer>().sharedMaterial = pedestalMaterial;

        // Empty transform sitting on top of the pedestal - the loaded model parents here
        GameObject anchor = new GameObject("SculptureAnchor");
        anchor.transform.SetParent(pedestal.transform);
        anchor.transform.localPosition = new Vector3(0, pedestalHeight + 0.05f, 0);
        anchor.transform.localRotation = Quaternion.identity;
        anchor.transform.localScale = Vector3.one;

        SculptureDisplay display = pedestal.AddComponent<SculptureDisplay>();
        display.sculptureAnchor = anchor.transform;
        display.targetSize = sculptureScale > 0 ? sculptureScale : 0.5f;

        return pedestal;
    }

    // Adds an artwork-aligned spotlight mounted near the ceiling and aimed at the artwork center.
    private void AddArtworkSpotlight(Transform target, PlacementResult placementResult, float artworkWidth, float artworkHeight, bool isHero, string role, float visualWeight = 0.5f)
    {
        bool shouldLight = spotlightWallArtwork || (isHero && spotlightHeroPieces);
        if (!shouldLight)
        {
            return;
        }

        if (spawnedArtworkSpotlights >= Mathf.Max(0, maxArtworkSpotlights))
        {
            return;
        }

        string normalizedRole = NormalizeRole(role);
        (float roleIntensity, float roleAngle, float roleInnerAngle, float roleRange, float roleOffset) = GetRoleSpotlightSettings(normalizedRole, isHero);

        RoomDimensions roomDims = topologyGenerator.GetRoomDimensions(placementResult.roomId);
        float floorY = topologyGenerator.GetFloorY(placementResult.roomId);
        float roomHeight = roomDims != null && roomDims.height > 0f ? roomDims.height : 3f;
        float ceilingY = floorY + roomHeight;
        float mountBelowCeiling = Mathf.Clamp(spotlightHeightAboveFrame, 0.2f, 0.4f);
        float mountY = ceilingY - mountBelowCeiling;

        Vector3 wallNormal = placementResult.wallNormal.sqrMagnitude > 0.001f
            ? placementResult.wallNormal.normalized
            : target.forward;
        float wallOffset = Mathf.Clamp(spotlightWallOffset, 0.8f, 1.2f);

        Vector3 artworkCenter = target.position;
        Vector3 lightPosition = artworkCenter + wallNormal * Mathf.Clamp(wallOffset + roleOffset, 0.8f, 1.2f);
        float minimumMountHeight = artworkCenter.y + artworkHeight * 0.35f + 0.2f;
        lightPosition.y = Mathf.Max(mountY, minimumMountHeight);

        Vector3 direction = artworkCenter - lightPosition;
        if (direction.sqrMagnitude < 0.001f)
        {
            direction = -wallNormal + Vector3.down * 0.2f;
        }

        float sizeFactor = Mathf.Clamp(Mathf.Max(artworkWidth, artworkHeight) / 1.6f, 0.85f, 1.35f);
        float sizeT = Mathf.InverseLerp(0.85f, 1.35f, sizeFactor);
        float angleScale = Mathf.Lerp(0.95f, 1.08f, sizeT);
        float intensityScale = Mathf.Lerp(0.9f, 1.15f, sizeT);

        GameObject lightObj = new GameObject("ArtworkSpotlight");
        lightObj.transform.SetParent(artworkParent.transform, worldPositionStays: false);
        lightObj.transform.position = lightPosition;
        lightObj.transform.rotation = Quaternion.LookRotation(direction.normalized, Vector3.up);

        Light spotlight = lightObj.AddComponent<Light>();
        spotlight.type = LightType.Spot;
        spotlight.color = GetSpotlightColorForStyle(normalizedRole);
        spotlight.intensity = roleIntensity * intensityScale * GetSpotlightStyleMultiplier() * Mathf.Lerp(0.95f, 1.05f, Mathf.Clamp01(visualWeight));
        spotlight.range = roleRange * sizeFactor;
        spotlight.spotAngle = Mathf.Clamp(roleAngle * angleScale, 55f, 72f);
        spotlight.innerSpotAngle = Mathf.Min(roleInnerAngle * angleScale, spotlight.spotAngle - 5f);
        spotlight.shadows = LightShadows.None;

        spawnedArtworkSpotlights++;
    }

    private (float intensity, float spotAngle, float innerSpotAngle, float range, float offset) GetRoleSpotlightSettings(string normalizedRole, bool isHero)
    {
        if (isHero || normalizedRole == "hero")
        {
            return (2.8f, 70f, 48f, 7f, 0.1f);
        }
        if (normalizedRole == "cluster")
        {
            return (2f, 60f, 45f, 6f, 0f);
        }
        return (1.35f, 55f, 42f, 5f, -0.1f);
    }

    private Color GetSpotlightColorForStyle(string normalizedRole)
    {
        if (activeGalleryStyle == GalleryStyleIds.Classical)
            return normalizedRole == "hero" ? Rgb(255, 230, 198) : Rgb(255, 235, 205);
        if (activeGalleryStyle == GalleryStyleIds.Industrial)
            return normalizedRole == "hero" ? Rgb(255, 246, 236) : Rgb(255, 251, 244);
        return normalizedRole == "hero" ? Rgb(255, 236, 214) : Rgb(255, 244, 229);
    }

    private float GetSpotlightStyleMultiplier()
    {
        if (activeGalleryStyle == GalleryStyleIds.Classical)
            return 0.85f;
        if (activeGalleryStyle == GalleryStyleIds.Industrial)
            return 1.15f;
        return 1f;
    }
    
    // Returns sort priority: heroes first (0), then clusters (1), then ambient (2)
    private int GetRoleOrder(string role)
    {
        return role switch
        {
            "hero" => 0,
            "cluster" => 1,
            _ => 2
        };
    }

    private string NormalizeRole(string role)
    {
        if (string.IsNullOrEmpty(role))
        {
            return "ambient";
        }

        string normalized = role.Trim().ToLowerInvariant();
        if (normalized == "hero" || normalized == "cluster")
        {
            return normalized;
        }
        return "ambient";
    }

    private float GetFrameBorderWidthForStyle()
    {
        float baseWidth = Mathf.Max(0.015f, frameBorderWidth);
        if (activeGalleryStyle == GalleryStyleIds.Classical)
        {
            return Mathf.Clamp(baseWidth, 0.03f, 0.08f);
        }
        if (activeGalleryStyle == GalleryStyleIds.Industrial)
        {
            return Mathf.Clamp(baseWidth * 0.7f, 0.02f, 0.04f);
        }
        return Mathf.Clamp(baseWidth * 0.55f, 0.015f, 0.035f);
    }

    // Creates style-driven frame and pedestal materials with hard fallbacks to avoid magenta materials.
    private void InitializeMaterials()
    {
        frameMaterial = EnsureMaterial(
            CreateMaterial("FrameSurface", Color.white, metallic: 0f, smoothness: 0.08f),
            "FrameSurfaceFallback",
            Rgb(235, 235, 235),
            0f,
            0.08f
        );

        Color borderColor = Rgb(240, 240, 240);
        float borderMetallic = 0f;
        float borderSmoothness = 0.25f;
        Color pedestalColor = Rgb(220, 220, 220);
        float pedestalSmoothness = 0.22f;

        if (activeGalleryStyle == GalleryStyleIds.Classical)
        {
            borderColor = Rgb(180, 150, 90);
            borderMetallic = 0.3f;
            borderSmoothness = 0.5f;
            pedestalColor = Rgb(214, 203, 185);
            pedestalSmoothness = 0.28f;
        }
        else if (activeGalleryStyle == GalleryStyleIds.Industrial)
        {
            borderColor = Rgb(40, 40, 40);
            borderMetallic = 0f;
            borderSmoothness = 0.2f;
            pedestalColor = Rgb(112, 112, 108);
            pedestalSmoothness = 0.18f;
        }

        frameBorderMaterial = EnsureMaterial(
            CreateMaterial("FrameBorder", borderColor, borderMetallic, borderSmoothness),
            "FrameBorderFallback",
            Rgb(200, 200, 200),
            0f,
            0.2f
        );

        pedestalMaterial = EnsureMaterial(
            CreateMaterial("Pedestal", pedestalColor, metallic: 0f, smoothness: pedestalSmoothness),
            "PedestalFallback",
            Rgb(180, 180, 180),
            0f,
            0.2f
        );
    }

    private Material EnsureMaterial(Material material, string fallbackName, Color fallbackColor, float fallbackMetallic, float fallbackSmoothness)
    {
        if (material != null)
        {
            return material;
        }

        Material fallback = CreateMaterial(fallbackName, fallbackColor, fallbackMetallic, fallbackSmoothness);
        if (fallback != null)
        {
            return fallback;
        }

        Shader emergencyShader = Shader.Find("Unlit/Color");
        if (emergencyShader == null)
        {
            emergencyShader = Shader.Find("Sprites/Default");
        }

        if (emergencyShader == null)
        {
            Debug.LogError("[ArtworkPlacer] Failed to create fallback material; this may render magenta.");
            return null;
        }

        Material emergency = new Material(emergencyShader);
        emergency.name = $"Placed_{fallbackName}_Emergency";
        if (emergency.HasProperty("_Color"))
        {
            emergency.SetColor("_Color", fallbackColor);
        }
        return emergency;
    }

    // Consolidated: delegates to MaterialUtility.CreateMaterial
    private Material CreateMaterial(string name, Color color, float metallic = 0f, float smoothness = 0.2f)
    {
        return MaterialUtility.CreateMaterial(name, color, metallic, smoothness);
    }

    // Consolidated: delegates to MaterialUtility.Rgb
    private static Color Rgb(int r, int g, int b) => MaterialUtility.Rgb(r, g, b);
}
