using System;
using System.Collections.Generic;
using UnityEngine;

// Abstract base class for all gallery topology generators.
//
// Each topology type (corridor, hub_and_spoke, branching_rooms, etc.) has its
// own subclass that overrides Generate() to build the actual room geometry.
//
// After generation, ArtworkPlacer calls GetWallPlacement() and GetFloorPlacement()
// to find the exact world position + rotation for each frame or pedestal. These
// methods look up the room and wall by ID, then walk along the wall surface to
// the requested position.

public enum GalleryStyle
{
    Contemporary,
    Classical,
    Industrial
}

public abstract class TopologyGenerator : MonoBehaviour
{
    [Header("Generation Settings")]
    [Tooltip("Wall thickness in meters")]
    public float wallThickness = 0.2f;

    [Tooltip("Offset from wall surface for artwork placement (match frame depth for flush placement)")]
    public float wallOffset = 0.05f;

    [Header("Debug")]
    public bool debugMode = false;

    // Root object containing all generated geometry. Destroyed on ClearGenerated().
    protected GameObject generatedRoot;

    // Current theme palette - used by subclasses to color walls/floors/ceilings.
    protected ThemePalette themePalette;

    // Cached materials so we don't create duplicates for every surface.
    protected Material wallMaterial;
    protected Material floorMaterial;
    protected Material ceilingMaterial;
    protected Material trimMaterial;

    // Optional manifest context used for style parsing and deterministic variation.
    protected GalleryManifest manifestContext;
    protected LockedConstraints generationConstraints;
    protected GalleryStyle galleryStyle = GalleryStyle.Contemporary;
    protected int styleSeed = 1;
    protected const float CeilingLightSpacingMultiplier = 1.5f;
    protected readonly List<Texture2D> generatedTextures = new List<Texture2D>();
    protected readonly HashSet<int> trimmedRoomIds = new HashSet<int>();

    // Every room the generator creates gets registered here.
    // Key is the room ID from the manifest (e.g. "main", "room_a", "alcove_1").
    // ArtworkPlacer and GalleryOrchestrator both read from this.
    protected Dictionary<string, GeneratedRoom> generatedRooms = new Dictionary<string, GeneratedRoom>();
    
    // Wall space manager for tracking openings and placed artwork
    protected WallSpaceManager wallSpaceManager = new WallSpaceManager();
    
    // Public accessor for the wall space manager
    public WallSpaceManager WallSpace => wallSpaceManager;

    // Returns the full dictionary of generated rooms (used by GalleryOrchestrator
    // to find the spawn point, and by ArtworkPlacer for placement lookups).
    public Dictionary<string, GeneratedRoom> GetGeneratedRooms()
    {
        return generatedRooms;
    }

    // Called by GalleryOrchestrator before Generate() so style/material choices can
    // respond to manifest-level fields without touching placement data.
    public void SetManifestContext(GalleryManifest manifest)
    {
        manifestContext = manifest;
    }

    protected void SetGenerationContext(LockedConstraints constraints)
    {
        generationConstraints = constraints;
        galleryStyle = ResolveGalleryStyle();
        styleSeed = Mathf.Max(1, ComputeStyleSeed());
    }

    // Explicit style configuration entrypoint used by all topology generators.
    // Safe to call with null/empty values; defaults to contemporary.
    protected void ConfigureStyle(string style)
    {
        galleryStyle = ParseGalleryStyle(style);
        styleSeed = Mathf.Max(1, ComputeStyleSeed());
        InitializeMaterials();

        if (debugMode)
        {
            Debug.Log($"[TopologyGenerator] Gallery style configured: {galleryStyle}");
        }
    }

    // Subclasses must implement this to build the actual room geometry.
    // Called by GalleryOrchestrator after the theme is applied.
    public abstract void Generate(LockedConstraints constraints, LayoutPlanWrapper layoutPlan);

    // Calculates the world position and rotation for hanging artwork on a wall.
    // Uses WallSpaceManager to avoid openings and other artwork.
    //
    // How it works:
    //   1. Look up the room by ID
    //   2. Look up the wall by name ("left", "right", "front", "back")
    //   3. Use WallSpaceManager to find valid position (avoids openings and other art)
    //   4. Start at the wall's startPoint and walk along it by positionAlongWall meters
    //   5. Push the position out from the wall by wallOffset so the frame doesn't clip
    //   6. Set the Y to the requested height above the floor
    //   7. Rotate the frame to face into the room (away from the wall)
    //   8. Register the placed artwork with WallSpaceManager
    public virtual PlacementResult GetWallPlacement(string roomId, string wallName, float positionAlongWall, float height, float artworkWidth = 1.2f, string assetId = null)
    {
        if (!generatedRooms.TryGetValue(roomId, out var room))
        {
            Debug.LogError($"[TopologyGenerator] Room not found: {roomId}");
            return null;
        }

        if (!room.walls.TryGetValue(wallName, out var wall))
        {
            Debug.LogError($"[TopologyGenerator] Wall '{wallName}' not found in room '{roomId}'");
            return null;
        }

        // Use WallSpaceManager for collision avoidance
        float adjustedPosition = positionAlongWall;
        float? validPosition = wallSpaceManager.FindValidPosition(roomId, wallName, positionAlongWall, artworkWidth, wall.length);
        
        if (validPosition.HasValue)
        {
            adjustedPosition = validPosition.Value;
            if (Mathf.Abs(adjustedPosition - positionAlongWall) > 0.01f && debugMode)
            {
                Debug.Log($"[TopologyGenerator] Position {positionAlongWall:F2} adjusted to {adjustedPosition:F2} for {artworkWidth}m artwork");
            }
        }
        else
        {
            // No gap large enough - skip this artwork to prevent overlapping
            if (debugMode) Debug.LogWarning($"[TopologyGenerator] No valid position for {artworkWidth}m artwork on wall '{wallName}' in room '{roomId}'");
            return null;
        }

        // Register this placement so future art won't overlap
        if (!string.IsNullOrEmpty(assetId))
        {
            wallSpaceManager.RegisterArtwork(roomId, wallName, adjustedPosition, artworkWidth, assetId);
        }

        // Walk along the wall from start to end
        Vector3 startPoint = wall.startPoint;
        Vector3 endPoint = wall.endPoint;
        Vector3 direction = (endPoint - startPoint).normalized;

        Vector3 position = startPoint + direction * adjustedPosition;

        // Push away from the wall surface so the frame sits flush
        position += wall.normal * wallOffset;
        position.y = room.floorY + height;

        // Face the frame into the room (looking along the wall normal)
        Quaternion rotation = Quaternion.LookRotation(wall.normal);

        if (debugMode)
        {
            Debug.Log($"[TopologyGenerator] Wall '{wallName}': start={startPoint}, end={endPoint}, normal={wall.normal}");
            Debug.Log($"[TopologyGenerator] Placement: pos={position}, rotation facing {wall.normal}, offset={wallOffset}m");
        }

        return new PlacementResult
        {
            position = position,
            rotation = rotation,
            wallNormal = wall.normal,
            roomId = roomId,
            wallName = wallName
        };
    }

    // Unified placement method that handles all placement types from schema.
    // This is the preferred entry point for ArtworkPlacer.
    public virtual PlacementResult GetPlacement(PlacementInstruction placement, LayoutPlanWrapper layoutPlan = null)
    {
        if (placement == null)
        {
            Debug.LogError("[TopologyGenerator] Null placement instruction");
            return null;
        }
        
        // Floor position for sculptures (absolute x/z in room coordinates)
        if (placement.floor_position != null)
        {
            return GetFloorPlacementAbsolute(placement.room_id, 
                placement.floor_position.x, placement.floor_position.z);
        }
        
        // Partition surface in open_hall (e.g., "partition_1")
        if (!string.IsNullOrEmpty(placement.surface))
        {
            if (placement.surface.StartsWith("partition_"))
            {
                return GetPartitionPlacement(placement.room_id, placement.surface, 
                    placement.side, placement.position_along, placement.height);
            }
            else if (placement.surface.StartsWith("wall_"))
            {
                // Handle "wall_left" -> "left" conversion
                string wallName = placement.surface.Replace("wall_", "");
                return GetWallPlacement(placement.room_id, wallName, 
                    placement.position_along, placement.height);
            }
        }
        
        // Sculpture at room center
        if (placement.wall == WallNames.Center)
        {
            Vector3 localPos = Vector3.zero;
            if (placement.local_position != null)
            {
                localPos = placement.local_position.ToVector3();
            }
            return GetFloorPlacement(placement.room_id, localPos);
        }
        
        // Standard wall placement
        if (!string.IsNullOrEmpty(placement.wall))
        {
            return GetWallPlacement(placement.room_id, placement.wall, 
                placement.position_along_wall, placement.height);
        }
        
        Debug.LogWarning($"[TopologyGenerator] Unknown placement type for {placement.asset_id}");
        return null;
    }
    
    // Floor placement with absolute x/z coordinates (for open_hall sculptures)
    public virtual PlacementResult GetFloorPlacementAbsolute(string roomId, float x, float z)
    {
        if (!generatedRooms.TryGetValue(roomId, out var room))
        {
            Debug.LogError($"[TopologyGenerator] Room not found: {roomId}");
            return null;
        }
        
        Vector3 position = new Vector3(x, room.floorY, z);
        
        return new PlacementResult
        {
            position = position,
            rotation = Quaternion.identity,
            roomId = roomId
        };
    }
    
    // Placement on partition surface (for open_hall)
    public virtual PlacementResult GetPartitionPlacement(string roomId, string partitionSurface, 
        string side, float positionAlong, float height)
    {
        if (!generatedRooms.TryGetValue(roomId, out var room))
        {
            Debug.LogError($"[TopologyGenerator] Room not found: {roomId}");
            return null;
        }
        
        // Extract partition id (e.g., "partition_1" -> "partition_1")
        string partitionId = partitionSurface;
        
        // Look for partition wall info - stored as "partition_X_front" or "partition_X_back"
        string wallKey = $"{partitionId}_{side}";
        if (!room.walls.TryGetValue(wallKey, out var wall))
        {
            // Try without side suffix
            if (!room.walls.TryGetValue(partitionId, out wall))
            {
                Debug.LogError($"[TopologyGenerator] Partition '{wallKey}' not found in room '{roomId}'");
                return null;
            }
        }
        
        Vector3 startPoint = wall.startPoint;
        Vector3 endPoint = wall.endPoint;
        Vector3 direction = (endPoint - startPoint).normalized;
        
        Vector3 position = startPoint + direction * positionAlong;
        position += wall.normal * wallOffset;
        position.y = room.floorY + height;
        
        Quaternion rotation = Quaternion.LookRotation(wall.normal);
        
        return new PlacementResult
        {
            position = position,
            rotation = rotation,
            wallNormal = wall.normal,
            roomId = roomId,
            wallName = partitionSurface
        };
    }

    // Calculates the world position for a sculpture on the floor.
    // localPosition is relative to the room center (from the manifest).
    public virtual PlacementResult GetFloorPlacement(string roomId, Vector3 localPosition)
    {
        if (!generatedRooms.TryGetValue(roomId, out var room))
        {
            Debug.LogError($"[TopologyGenerator] Room not found: {roomId}");
            return null;
        }

        Vector3 position = room.center + localPosition;
        position.y = room.floorY;

        return new PlacementResult
        {
            position = position,
            rotation = Quaternion.identity,
            roomId = roomId
        };
    }

    // Returns the floor Y for a room (always 0 in our current generators,
    // but could differ if rooms were stacked vertically).
    public virtual float GetFloorY(string roomId)
    {
        if (generatedRooms.TryGetValue(roomId, out var room))
            return room.floorY;
        return 0f;
    }

    // Returns the dimensions of a room from the layout plan.
    public virtual RoomDimensions GetRoomDimensions(string roomId)
    {
        if (generatedRooms.TryGetValue(roomId, out var room))
            return room.dimensions;
        return null;
    }

    public enum FocalWallBias
    {
        Center,
        TowardStart,
        TowardEnd
    }

    public class FocalWallPlacement
    {
        public string roomId;
        public string wallName;
        public float positionAlongWall;
    }

    public virtual bool TryGetFocalWallPlacement(float artworkWidth, out FocalWallPlacement focalPlacement)
    {
        focalPlacement = null;
        return false;
    }

    protected bool TryGetWallInfo(string roomId, string wallName, out WallInfo wallInfo)
    {
        wallInfo = null;
        if (string.IsNullOrEmpty(roomId) || string.IsNullOrEmpty(wallName))
        {
            return false;
        }

        if (!generatedRooms.TryGetValue(roomId, out var room) || room == null || room.walls == null)
        {
            return false;
        }

        if (!room.walls.TryGetValue(wallName, out wallInfo) || wallInfo == null)
        {
            return false;
        }

        return true;
    }

    protected bool TryCreateFocalWallPlacement(string roomId, string wallName, float artworkWidth, FocalWallBias bias, out FocalWallPlacement focalPlacement)
    {
        if (!TryGetWallInfo(roomId, wallName, out var wall))
        {
            focalPlacement = null;
            return false;
        }

        float positionAlong = GetSafeWallPosition(wall.length, artworkWidth, bias);
        return TryCreateFocalWallPlacement(roomId, wallName, artworkWidth, positionAlong, out focalPlacement);
    }

    protected bool TryCreateFocalWallPlacement(string roomId, string wallName, float artworkWidth, float positionAlong, out FocalWallPlacement focalPlacement)
    {
        focalPlacement = null;
        if (!TryGetWallInfo(roomId, wallName, out var wall))
        {
            return false;
        }

        float minimumLength = Mathf.Max(artworkWidth + 0.5f, 1f);
        if (wall.length < minimumLength)
        {
            return false;
        }

        float safePosition = ClampWallPosition(wall.length, artworkWidth, positionAlong);
        focalPlacement = new FocalWallPlacement
        {
            roomId = roomId,
            wallName = wallName,
            positionAlongWall = safePosition
        };
        return true;
    }

    protected float GetSafeWallPosition(float wallLength, float artworkWidth, FocalWallBias bias)
    {
        float halfWidth = Mathf.Clamp(artworkWidth * 0.5f, 0.1f, wallLength * 0.45f);
        float edgePadding = 0.25f;
        float min = edgePadding + halfWidth;
        float max = wallLength - edgePadding - halfWidth;

        if (max < min)
        {
            return wallLength * 0.5f;
        }

        switch (bias)
        {
            case FocalWallBias.TowardStart:
                return min;
            case FocalWallBias.TowardEnd:
                return max;
            case FocalWallBias.Center:
            default:
                return (min + max) * 0.5f;
        }
    }

    protected float ClampWallPosition(float wallLength, float artworkWidth, float positionAlong)
    {
        float halfWidth = Mathf.Clamp(artworkWidth * 0.5f, 0.1f, wallLength * 0.45f);
        float edgePadding = 0.25f;
        float min = edgePadding + halfWidth;
        float max = wallLength - edgePadding - halfWidth;

        if (max < min)
        {
            return wallLength * 0.5f;
        }

        return Mathf.Clamp(positionAlong, min, max);
    }

    protected float GetRoomDepth(RoomDimensions dimensions)
    {
        if (dimensions == null)
        {
            return 0f;
        }

        if (dimensions.length > 0f)
        {
            return dimensions.length;
        }

        return dimensions.depth;
    }

    protected GeneratedRoom GetRoomWithGreatestForwardExtent()
    {
        GeneratedRoom best = null;
        float bestForwardExtent = float.MinValue;

        foreach (GeneratedRoom room in generatedRooms.Values)
        {
            if (room == null || room.dimensions == null)
            {
                continue;
            }

            float forwardExtent = room.center.z + GetRoomDepth(room.dimensions) * 0.5f;
            if (forwardExtent > bestForwardExtent)
            {
                bestForwardExtent = forwardExtent;
                best = room;
            }
        }

        return best;
    }

    protected bool TryGetWidestWallInRoom(string roomId, out WallInfo widestWall)
    {
        return TryGetWidestWallInRoom(roomId, null, out widestWall);
    }

    // Overload that can optionally exclude a set of wall names from the search.
    protected bool TryGetWidestWallInRoom(string roomId, HashSet<string> excludeWalls, out WallInfo widestWall)
    {
        widestWall = null;
        if (!generatedRooms.TryGetValue(roomId, out var room) || room == null || room.walls == null)
        {
            return false;
        }

        float widest = float.MinValue;
        foreach (var kvp in room.walls)
        {
            WallInfo wall = kvp.Value;
            if (wall == null || wall.length <= widest)
            {
                continue;
            }
            if (excludeWalls != null && excludeWalls.Contains(kvp.Key))
            {
                continue;
            }

            widest = wall.length;
            widestWall = wall;
        }

        return widestWall != null;
    }

    // Public accessor: finds the best wall for "auto" placements in a room.
    // Picks the longest wall that isn't the focal/hero wall.
    // Returns a FocalWallPlacement with the wall centered for the given artwork width.
    public bool TryGetAutoWallPlacement(string roomId, float artworkWidth, string excludeWall, out FocalWallPlacement autoPlacement)
    {
        autoPlacement = null;
        if (string.IsNullOrEmpty(roomId)) return false;

        HashSet<string> excludeSet = null;
        if (!string.IsNullOrEmpty(excludeWall))
        {
            excludeSet = new HashSet<string> { excludeWall };
        }

        if (!TryGetWidestWallInRoom(roomId, excludeSet, out WallInfo bestWall) || bestWall == null)
        {
            // Fallback: try without exclusion
            if (!TryGetWidestWallInRoom(roomId, out bestWall) || bestWall == null)
                return false;
        }

        return TryCreateFocalWallPlacement(roomId, bestWall.name, artworkWidth, FocalWallBias.Center, out autoPlacement);
    }

    protected string FindRoomIdByType(string roomType)
    {
        if (generationConstraints?.rooms == null || string.IsNullOrEmpty(roomType))
        {
            return null;
        }

        for (int i = 0; i < generationConstraints.rooms.Count; i++)
        {
            RoomConstraint room = generationConstraints.rooms[i];
            if (room != null && room.type == roomType && !string.IsNullOrEmpty(room.id))
            {
                return room.id;
            }
        }

        return null;
    }

    protected virtual GalleryStyle ResolveGalleryStyle()
    {
        string style = null;

        if (manifestContext != null)
        {
            style = manifestContext.GetGalleryStyle();
        }

        if (string.IsNullOrEmpty(style))
        {
            style = generationConstraints?.gallery_style;
        }

        return ParseGalleryStyle(style);
    }

    protected GalleryStyle ParseGalleryStyle(string style)
    {
        if (string.IsNullOrEmpty(style))
        {
            return GalleryStyle.Contemporary;
        }

        string normalized = style.Trim().ToLowerInvariant();
        if (normalized == "classical")
        {
            return GalleryStyle.Classical;
        }
        if (normalized == "industrial")
        {
            return GalleryStyle.Industrial;
        }

        return GalleryStyle.Contemporary;
    }

    private int ComputeStyleSeed()
    {
        int seed = 17;
        seed = seed * 31 + HashString(manifestContext?.gallery_id);
        seed = seed * 31 + HashString(manifestContext?.created_at);
        seed = seed * 31 + HashString(generationConstraints?.theme);
        seed = seed * 31 + (int)galleryStyle * 97;
        return Mathf.Abs(seed);
    }

    private int HashString(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        unchecked
        {
            int hash = 23;
            for (int i = 0; i < value.Length; i++)
            {
                hash = hash * 31 + value[i];
            }
            return hash;
        }
    }

    protected static Color Rgb(int r, int g, int b)
    {
        return new Color(r / 255f, g / 255f, b / 255f);
    }

    // Destroys all generated geometry and clears the room dictionary.
    public virtual void ClearGenerated()
    {
        if (generatedRoot != null)
        {
            if (Application.isPlaying)
                Destroy(generatedRoot);
            else
                DestroyImmediate(generatedRoot);
        }

        DestroyRuntimeMaterial(ref wallMaterial);
        DestroyRuntimeMaterial(ref floorMaterial);
        DestroyRuntimeMaterial(ref ceilingMaterial);
        DestroyRuntimeMaterial(ref trimMaterial);
        DestroyGeneratedTextures();

        generatedRooms.Clear();
        wallSpaceManager.ClearAll();
        trimmedRoomIds.Clear();
        generatedRoot = null;
    }

    // Grabs the current theme palette from ThemeManager and creates wall/floor/ceiling
    // materials. Falls back to sensible defaults if ThemeManager isn't in the scene.
    protected virtual void InitializeMaterials()
    {
        themePalette = ThemeManager.Instance?.GetPalette() ?? new ThemePalette();

        switch (galleryStyle)
        {
            case GalleryStyle.Classical:
                InitializeClassicalMaterials();
                break;

            case GalleryStyle.Industrial:
                InitializeIndustrialMaterials();
                break;

            case GalleryStyle.Contemporary:
            default:
                InitializeContemporaryMaterials();
                break;
        }

        ApplyStyleLightingEnvironment();
    }

    private void InitializeContemporaryMaterials()
    {
        Color wallColor = Rgb(246, 246, 244);
        Color floorColor = Rgb(120, 120, 116);
        Color ceilingColor = Rgb(34, 34, 36);

        Texture2D wallTexture = CreateWallTexture(GalleryStyle.Contemporary, wallColor);
        Texture2D floorTexture = CreateConcreteTexture("ContemporaryConcreteFloorTex", Rgb(118, 118, 114), Rgb(95, 95, 92), 0.12f);
        Texture2D ceilingTexture = CreateCeilingTexture(GalleryStyle.Contemporary, ceilingColor);

        wallMaterial = CreateMaterial("Wall_Contemporary", wallColor, metallic: 0f, smoothness: 0.08f, baseMap: wallTexture, tiling: new Vector2(4f, 4f));
        floorMaterial = CreateMaterial("Floor_Contemporary", floorColor, metallic: 0f, smoothness: 0.18f, baseMap: floorTexture, tiling: new Vector2(5f, 5f));
        ceilingMaterial = CreateMaterial("Ceiling_Contemporary", ceilingColor, metallic: 0f, smoothness: 0.03f, baseMap: ceilingTexture, tiling: new Vector2(3f, 3f));
        trimMaterial = CreateMaterial("Trim_Contemporary", Rgb(235, 235, 232), metallic: 0f, smoothness: 0.14f);
    }

    private void InitializeClassicalMaterials()
    {
        Color wallColor = PickClassicalWallColor();
        Color floorColor = Rgb(110, 78, 52);
        Color ceilingColor = Rgb(250, 248, 243);
        Color trimColor = Rgb(244, 238, 226);

        Texture2D wallTexture = CreateWallTexture(GalleryStyle.Classical, wallColor);
        Texture2D floorTexture = CreateClassicalFloorTexture();
        Texture2D ceilingTexture = CreateCeilingTexture(GalleryStyle.Classical, ceilingColor);

        wallMaterial = CreateMaterial("Wall_Classical", wallColor, metallic: 0f, smoothness: 0.2f, baseMap: wallTexture, tiling: new Vector2(3.5f, 3.5f));
        floorMaterial = CreateMaterial("Floor_Classical", floorColor, metallic: 0f, smoothness: 0.28f, baseMap: floorTexture, tiling: new Vector2(8f, 8f));
        ceilingMaterial = CreateMaterial("Ceiling_Classical", ceilingColor, metallic: 0f, smoothness: 0.12f, baseMap: ceilingTexture, tiling: new Vector2(2.4f, 2.4f));
        trimMaterial = CreateMaterial("Trim_Classical", trimColor, metallic: 0.01f, smoothness: 0.34f);
    }

    private void InitializeIndustrialMaterials()
    {
        Color wallColor = Rgb(122, 84, 65);
        Color floorColor = Rgb(82, 82, 78);
        Color ceilingColor = Rgb(58, 58, 56);

        Texture2D wallTexture = CreateWallTexture(GalleryStyle.Industrial, wallColor);
        Texture2D floorTexture = CreateIndustrialFloorTexture();
        Texture2D ceilingTexture = CreateCeilingTexture(GalleryStyle.Industrial, ceilingColor);

        wallMaterial = CreateMaterial("Wall_Industrial", wallColor, metallic: 0f, smoothness: 0.07f, baseMap: wallTexture, tiling: new Vector2(5f, 5f));
        floorMaterial = CreateMaterial("Floor_Industrial", floorColor, metallic: 0.03f, smoothness: 0.2f, baseMap: floorTexture, tiling: new Vector2(4f, 4f));
        ceilingMaterial = CreateMaterial("Ceiling_Industrial", ceilingColor, metallic: 0f, smoothness: 0.04f, baseMap: ceilingTexture, tiling: new Vector2(3.2f, 3.2f));
        trimMaterial = CreateMaterial("Trim_Industrial", Rgb(74, 74, 73), metallic: 0.08f, smoothness: 0.18f);
    }

    private Color PickClassicalWallColor()
    {
        Color[] options =
        {
            Rgb(237, 229, 212),
            Rgb(233, 224, 205),
            Rgb(242, 234, 219)
        };

        int index = Mathf.Abs(styleSeed) % options.Length;
        return options[index];
    }

    private Texture2D CreateWallTexture(GalleryStyle style, Color baseColor)
    {
        switch (style)
        {
            case GalleryStyle.Classical:
                return CreateClassicalWallTexture(baseColor);
            case GalleryStyle.Industrial:
                return CreateIndustrialWallTexture();
            case GalleryStyle.Contemporary:
            default:
                return CreateContemporaryWallTexture(baseColor);
        }
    }

    private Texture2D CreateCeilingTexture(GalleryStyle style, Color baseColor)
    {
        switch (style)
        {
            case GalleryStyle.Classical:
                return CreateClassicalCeilingTexture(baseColor);
            case GalleryStyle.Industrial:
                return CreateIndustrialCeilingTexture(baseColor);
            case GalleryStyle.Contemporary:
            default:
                return CreateContemporaryCeilingTexture(baseColor);
        }
    }

    private Texture2D CreateContemporaryWallTexture(Color baseColor)
    {
        Texture2D texture = CreateProceduralTexture("ContemporaryWallTex", 256, 256);
        int w = texture.width, h = texture.height;
        Color32[] pixels = new Color32[w * h];

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float broadNoise = Mathf.PerlinNoise((x + styleSeed * 0.19f) * 0.055f, (y + styleSeed * 0.31f) * 0.055f);
                float fineNoise = Mathf.PerlinNoise((x + styleSeed * 0.43f) * 0.23f, (y + styleSeed * 0.37f) * 0.23f);
                float diagonal = Mathf.PerlinNoise((x + y + styleSeed * 0.11f) * 0.09f, (y + styleSeed * 0.17f) * 0.04f);
                float delta = (broadNoise - 0.5f) * 0.018f + (fineNoise - 0.5f) * 0.012f + (diagonal - 0.5f) * 0.007f;
                pixels[y * w + x] = OffsetColor(baseColor, delta);
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply();
        return texture;
    }

    private Texture2D CreateClassicalWallTexture(Color baseColor)
    {
        Texture2D texture = CreateProceduralTexture("ClassicalWallTex", 256, 256);
        int w = texture.width, h = texture.height;
        Color32[] pixels = new Color32[w * h];

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float u = x / (float)w;
                float v = y / (float)h;

                float weave = Mathf.PerlinNoise((x + styleSeed * 0.17f) * 0.16f, (y + styleSeed * 0.29f) * 0.16f) - 0.5f;
                float motifX = Mathf.Sin((u * 10f + styleSeed * 0.013f) * Mathf.PI * 2f);
                float motifY = Mathf.Sin((v * 7f + styleSeed * 0.019f) * Mathf.PI * 2f);
                float motifField = Mathf.PerlinNoise((x + styleSeed * 0.41f) * 0.06f, (y + styleSeed * 0.53f) * 0.06f);
                float patternMask = Mathf.SmoothStep(0.6f, 0.88f, (motifX * motifY * 0.5f + 0.5f) * motifField);

                float delta = weave * 0.018f - patternMask * 0.04f;
                pixels[y * w + x] = OffsetColor(baseColor, delta);
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply();
        return texture;
    }

    private Texture2D CreateContemporaryCeilingTexture(Color baseColor)
    {
        Texture2D texture = CreateProceduralTexture("ContemporaryCeilingTex", 256, 256);
        int w = texture.width, h = texture.height;
        Color32[] pixels = new Color32[w * h];
        int panelSize = 48;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                bool gridLine = (x % panelSize <= 1) || (y % panelSize <= 1);
                float broad = Mathf.PerlinNoise((x + styleSeed * 0.07f) * 0.05f, (y + styleSeed * 0.13f) * 0.05f) - 0.5f;
                float fine = Mathf.PerlinNoise((x + styleSeed * 0.23f) * 0.18f, (y + styleSeed * 0.31f) * 0.18f) - 0.5f;
                float delta = broad * 0.012f + fine * 0.007f;
                if (gridLine)
                {
                    delta -= 0.045f;
                }

                pixels[y * w + x] = OffsetColor(baseColor, delta);
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply();
        return texture;
    }

    private Texture2D CreateClassicalCeilingTexture(Color baseColor)
    {
        Texture2D texture = CreateProceduralTexture("ClassicalCeilingTex", 256, 256);
        int w = texture.width, h = texture.height;
        Color32[] pixels = new Color32[w * h];
        int cellSize = 56;
        int border = 2;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int cx = x % cellSize;
                int cy = y % cellSize;
                int invX = cellSize - cx;
                int invY = cellSize - cy;
                int edgeDistance = Mathf.Min(Mathf.Min(cx, cy), Mathf.Min(invX, invY));
                bool borderLine = edgeDistance <= border;

                float broad = Mathf.PerlinNoise((x + styleSeed * 0.05f) * 0.045f, (y + styleSeed * 0.11f) * 0.045f) - 0.5f;
                float detail = Mathf.PerlinNoise((x + styleSeed * 0.27f) * 0.14f, (y + styleSeed * 0.19f) * 0.14f) - 0.5f;
                float recess = Mathf.Clamp01((edgeDistance - border) / 10f) * 0.008f;
                float delta = broad * 0.011f + detail * 0.006f + recess;

                if (borderLine)
                {
                    delta -= 0.055f;
                }

                pixels[y * w + x] = OffsetColor(baseColor, delta);
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply();
        return texture;
    }

    private Texture2D CreateIndustrialCeilingTexture(Color baseColor)
    {
        Texture2D texture = CreateProceduralTexture("IndustrialCeilingTex", 256, 256);
        int w = texture.width, h = texture.height;
        Color32[] pixels = new Color32[w * h];
        int beamSpacing = 40;
        int beamWidth = 5;
        int crossSpacing = 84;
        int crossWidth = 4;
        int conduitSpacing = 96;
        int conduitWidth = 8;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                bool beam = x % beamSpacing < beamWidth;
                bool crossBeam = y % crossSpacing < crossWidth;
                bool conduit = (x + y / 2 + styleSeed) % conduitSpacing < conduitWidth;

                float broad = Mathf.PerlinNoise((x + styleSeed * 0.09f) * 0.05f, (y + styleSeed * 0.17f) * 0.05f) - 0.5f;
                float speckle = Mathf.PerlinNoise((x + styleSeed * 0.33f) * 0.21f, (y + styleSeed * 0.29f) * 0.21f) - 0.5f;
                float delta = broad * 0.022f + speckle * 0.01f;

                if (beam || crossBeam)
                {
                    delta -= 0.085f;
                }
                if (conduit)
                {
                    delta += 0.03f;
                }

                pixels[y * w + x] = OffsetColor(baseColor, delta);
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply();
        return texture;
    }

    private Texture2D CreateContemporaryFloorTexture()
    {
        Texture2D texture = CreateProceduralTexture("ContemporaryFloorTex", 256, 256);
        int w = texture.width, h = texture.height;
        Color32[] pixels = new Color32[w * h];

        Color baseA = Rgb(213, 199, 176);
        Color baseB = Rgb(199, 185, 163);
        int plankWidth = 24;
        int plankLength = 84;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int plankX = x / plankWidth;
                int plankY = y / plankLength;
                bool alternate = ((plankX + plankY) & 1) == 0;
                Color baseColor = alternate ? baseA : baseB;

                float grain = Mathf.PerlinNoise((x + styleSeed * 0.17f) * 0.055f, (y + plankX * 37f) * 0.035f) - 0.5f;
                float seam = (x % plankWidth <= 1 || y % plankLength <= 1) ? -0.07f : 0f;
                pixels[y * w + x] = OffsetColor(baseColor, grain * 0.11f + seam);
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply();
        return texture;
    }

    private Texture2D CreateClassicalFloorTexture()
    {
        Texture2D texture = CreateProceduralTexture("ClassicalParquetTex", 256, 256);
        int w = texture.width, h = texture.height;
        Color32[] pixels = new Color32[w * h];

        Color darkWood = Rgb(82, 56, 38);
        Color warmWood = Rgb(100, 72, 50);
        int tile = 16;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int tx = x / tile;
                int ty = y / tile;
                bool swapDirection = ((tx + ty) & 1) == 0;
                float blend = (tx & 1) == 0 ? 0.35f : 0.65f;
                Color baseColor = Color.Lerp(darkWood, warmWood, blend);

                float grain = swapDirection
                    ? Mathf.PerlinNoise((x + styleSeed * 0.23f) * 0.08f, (y + ty * 41f) * 0.03f) - 0.5f
                    : Mathf.PerlinNoise((x + tx * 29f) * 0.03f, (y + styleSeed * 0.11f) * 0.08f) - 0.5f;
                float seam = (x % tile == 0 || y % tile == 0) ? -0.1f : 0f;
                pixels[y * w + x] = OffsetColor(baseColor, grain * 0.12f + seam);
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply();
        return texture;
    }

    private Texture2D CreateIndustrialWallTexture()
    {
        Texture2D texture = CreateProceduralTexture("IndustrialWallTex", 256, 256);
        int w = texture.width, h = texture.height;
        Color32[] pixels = new Color32[w * h];
        Color brickA = Rgb(130, 88, 67);
        Color brickB = Rgb(118, 80, 61);
        Color mortar = Rgb(146, 142, 136);
        Color concretePatch = Rgb(102, 102, 97);
        int brickWidth = 20;
        int brickHeight = 10;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int row = y / brickHeight;
                int rowOffset = ((row & 1) == 0) ? 0 : brickWidth / 2;
                int shiftedX = x + rowOffset;
                int localX = shiftedX % brickWidth;
                int localY = y % brickHeight;
                int col = shiftedX / brickWidth;
                bool mortarJoint = localX <= 1 || localY <= 1;

                float largeNoise = Mathf.PerlinNoise((x + styleSeed * 0.19f) * 0.05f, (y + styleSeed * 0.27f) * 0.05f);
                float mediumNoise = Mathf.PerlinNoise((x + styleSeed * 0.09f) * 0.12f, (y + styleSeed * 0.17f) * 0.12f);
                float weatherNoise = Mathf.PerlinNoise((x + styleSeed * 0.47f) * 0.014f, (y + styleSeed * 0.59f) * 0.014f);

                Color pixelColor = ((col + row) & 1) == 0 ? brickA : brickB;
                float delta = (largeNoise - 0.5f) * 0.08f + (mediumNoise - 0.5f) * 0.03f;

                if (mortarJoint)
                {
                    pixelColor = mortar;
                    delta = (mediumNoise - 0.5f) * 0.02f;
                }
                else if (weatherNoise > 0.8f)
                {
                    pixelColor = Color.Lerp(pixelColor, concretePatch, 0.7f);
                    delta -= 0.05f;
                }

                pixels[y * w + x] = OffsetColor(pixelColor, delta);
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply();
        return texture;
    }

    private Texture2D CreateIndustrialFloorTexture()
    {
        return CreateConcreteTexture("IndustrialFloorTex", Rgb(90, 90, 85), Rgb(68, 68, 64), 0.08f);
    }

    private Texture2D CreateConcreteTexture(string name, Color baseColor, Color accentColor, float accentWeight)
    {
        Texture2D texture = CreateProceduralTexture(name, 256, 256);
        int w = texture.width, h = texture.height;
        Color32[] pixels = new Color32[w * h];

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float largeNoise = Mathf.PerlinNoise((x + styleSeed * 0.19f) * 0.045f, (y + styleSeed * 0.27f) * 0.045f);
                float mediumNoise = Mathf.PerlinNoise((x + styleSeed * 0.07f) * 0.13f, (y + styleSeed * 0.09f) * 0.13f);
                float accentNoise = Mathf.PerlinNoise((x + styleSeed * 0.31f) * 0.012f, (y + styleSeed * 0.37f) * 0.012f);

                float valueOffset = (largeNoise - 0.5f) * 0.12f + (mediumNoise - 0.5f) * 0.07f;
                float accentBlend = Mathf.Clamp01((accentNoise - (1f - accentWeight)) * 5f);
                Color blended = Color.Lerp(baseColor, accentColor, accentBlend * 0.35f);

                pixels[y * w + x] = OffsetColor(blended, valueOffset);
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply();
        return texture;
    }

    private Texture2D CreateProceduralTexture(string name, int width, int height)
    {
        Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: true)
        {
            name = name,
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Bilinear
        };

        generatedTextures.Add(texture);
        return texture;
    }

    private Color OffsetColor(Color baseColor, float delta)
    {
        float r = Mathf.Clamp01(baseColor.r + delta);
        float g = Mathf.Clamp01(baseColor.g + delta);
        float b = Mathf.Clamp01(baseColor.b + delta);
        return new Color(r, g, b, 1f);
    }

    protected Color GetStyleLightColor()
    {
        switch (galleryStyle)
        {
            case GalleryStyle.Classical:
                return Rgb(255, 224, 191); // warm

            case GalleryStyle.Industrial:
                return Rgb(218, 230, 255); // cool

            case GalleryStyle.Contemporary:
            default:
                return Rgb(242, 244, 246); // neutral-cool
        }
    }

    protected Color GetCeilingLightColor()
    {
        switch (galleryStyle)
        {
            case GalleryStyle.Classical:
                return GetStyleLightColor();
            case GalleryStyle.Industrial:
                return Rgb(212, 220, 232);
            case GalleryStyle.Contemporary:
            default:
                return Rgb(238, 240, 244);
        }
    }

    protected int GetCeilingLinearLightCount(int configuredCount)
    {
        int safeCount = Mathf.Max(1, configuredCount);
        return Mathf.Max(1, Mathf.FloorToInt(safeCount / CeilingLightSpacingMultiplier));
    }

    protected void GetCeilingGridLightCounts(int configuredAcross, int configuredAlong, out int across, out int along)
    {
        across = Mathf.Max(1, configuredAcross);
        along = Mathf.Max(1, configuredAlong);

        int targetTotal = Mathf.Max(1, Mathf.FloorToInt((across * along) / CeilingLightSpacingMultiplier));
        while (across * along > targetTotal)
        {
            if (across >= along && across > 1)
            {
                across--;
            }
            else if (along > 1)
            {
                along--;
            }
            else if (across > 1)
            {
                across--;
            }
            else
            {
                break;
            }
        }
    }

    protected float GetAmbientIntensityForStyle()
    {
        switch (galleryStyle)
        {
            case GalleryStyle.Classical:
                return 0.14f;
            case GalleryStyle.Industrial:
                return 0.08f;
            case GalleryStyle.Contemporary:
            default:
                return 0.12f;
        }
    }

    protected float GetDirectionalFillIntensityForStyle()
    {
        switch (galleryStyle)
        {
            case GalleryStyle.Classical:
                return 0.18f;
            case GalleryStyle.Industrial:
                return 0.12f;
            case GalleryStyle.Contemporary:
            default:
                return 0.16f;
        }
    }

    protected float GetPointLightIntensity(float baseIntensity)
    {
        float multiplier;
        float maxIntensity;
        switch (galleryStyle)
        {
            case GalleryStyle.Classical:
                multiplier = 0.32f;
                maxIntensity = 0.38f;
                break;
            case GalleryStyle.Industrial:
                multiplier = 0.2f;
                maxIntensity = 0.25f;
                break;
            case GalleryStyle.Contemporary:
            default:
                multiplier = 0.24f;
                maxIntensity = 0.3f;
                break;
        }

        return Mathf.Clamp(baseIntensity * multiplier, 0.06f, maxIntensity);
    }

    protected float GetPointLightRange(float baseRange)
    {
        float multiplier = 1f;
        switch (galleryStyle)
        {
            case GalleryStyle.Classical:
                multiplier = 0.72f;
                break;
            case GalleryStyle.Industrial:
                multiplier = 0.68f;
                break;
            case GalleryStyle.Contemporary:
            default:
                multiplier = 0.75f;
                break;
        }

        return baseRange * multiplier;
    }

    protected virtual void ApplyStyleLightingEnvironment()
    {
        RenderSettings.ambientIntensity = GetAmbientIntensityForStyle();

        Light[] lights = FindObjectsByType<Light>(FindObjectsSortMode.None);
        float fillIntensity = GetDirectionalFillIntensityForStyle();
        Color fillColor = GetStyleLightColor();

        foreach (Light sceneLight in lights)
        {
            if (sceneLight == null || sceneLight.type != LightType.Directional)
            {
                continue;
            }

            sceneLight.intensity = fillIntensity;
            sceneLight.color = fillColor;
            sceneLight.shadows = LightShadows.None;
        }
    }

    protected void ApplyStyleEnhancements(bool includeSpotlights)
    {
        ApplyProceduralSurfaceVariation();
        GenerateArchitecturalTrim();
        if (includeSpotlights)
        {
            GenerateStyleSpotlights();
        }
        ApplyStyleLightingEnvironment();
    }

    protected virtual void GenerateArchitecturalTrim()
    {
        if (generatedRoot == null)
        {
            return;
        }

        foreach (GeneratedRoom room in generatedRooms.Values)
        {
            if (room?.transform == null || room.walls == null || room.walls.Count == 0)
            {
                continue;
            }

            CreateArchitecturalTrim(room.transform.gameObject, new List<WallInfo>(room.walls.Values));
        }
    }

    // Per-room trim hook used by all topology generators. This only decorates
    // canonical room walls and never mutates wall coordinate data used for placement.
    protected void CreateArchitecturalTrim(GameObject room, List<WallInfo> walls)
    {
        if (generatedRoot == null || room == null || walls == null || walls.Count == 0)
        {
            return;
        }

        int roomId = room.GetInstanceID();
        if (trimmedRoomIds.Contains(roomId))
        {
            return;
        }

        GeneratedRoom generatedRoom = FindGeneratedRoomByTransform(room.transform);
        float floorY = generatedRoom?.floorY ?? 0f;
        float defaultHeight = generatedRoom?.dimensions?.height ?? 3f;

        GameObject trimRoot = new GameObject("ArchitecturalTrim");
        trimRoot.transform.SetParent(room.transform);
        trimRoot.transform.localPosition = Vector3.zero;
        trimRoot.transform.localRotation = Quaternion.identity;

        bool addedTrim = false;
        for (int i = 0; i < walls.Count; i++)
        {
            WallInfo wall = walls[i];
            if (wall == null || wall.length < 0.4f || !ShouldGenerateTrimOnWall(wall.name))
            {
                continue;
            }

            float wallHeight = wall.height > 0f ? wall.height : defaultHeight;
            string suffix = $"{room.name}_{wall.name}";

            switch (galleryStyle)
            {
                case GalleryStyle.Classical:
                    GenerateBaseboard(trimRoot.transform, wall.startPoint, wall.endPoint, wall.normal, floorY, 0.12f, 0.025f, $"Baseboard_{suffix}");
                    GenerateCrownMolding(trimRoot.transform, wall.startPoint, wall.endPoint, wall.normal, floorY + wallHeight, 0.1f, 0.045f, $"Crown_{suffix}");
                    addedTrim = true;
                    break;
                case GalleryStyle.Industrial:
                    GenerateBaseboard(trimRoot.transform, wall.startPoint, wall.endPoint, wall.normal, floorY, 0.03f, 0.012f, $"Baseboard_{suffix}");
                    GenerateCeilingBeam(trimRoot.transform, wall.startPoint, wall.endPoint, wall.normal, floorY + wallHeight - 0.08f, 0.05f, $"Beam_{suffix}");
                    addedTrim = true;
                    break;
                case GalleryStyle.Contemporary:
                default:
                    GenerateBaseboard(trimRoot.transform, wall.startPoint, wall.endPoint, wall.normal, floorY, 0.045f, 0.012f, $"Baseboard_{suffix}");
                    GenerateShadowGap(trimRoot.transform, wall.startPoint, wall.endPoint, wall.normal, floorY + wallHeight - 0.02f, $"ShadowGap_{suffix}");
                    addedTrim = true;
                    break;
            }
        }

        if (!addedTrim)
        {
            if (Application.isPlaying)
                Destroy(trimRoot);
            else
                DestroyImmediate(trimRoot);
            return;
        }

        trimmedRoomIds.Add(roomId);
    }

    protected bool ShouldGenerateTrimOnWall(string wallName)
    {
        return wallName == WallNames.Left ||
               wallName == WallNames.Right ||
               wallName == WallNames.Front ||
               wallName == WallNames.Back;
    }

    protected void GenerateBaseboard(Transform parent, Vector3 start, Vector3 end, Vector3 inwardNormal, float floorY, float height, float depth, string name = "Baseboard")
    {
        CreateTrimStrip(parent, start, end, inwardNormal, floorY + height * 0.5f, height, depth, name);
    }

    protected void GenerateCrownMolding(Transform parent, Vector3 start, Vector3 end, Vector3 inwardNormal, float ceilingY, float height, float depth, string name = "CrownMolding")
    {
        CreateTrimStrip(parent, start, end, inwardNormal, ceilingY - height * 0.5f, height, depth, name);
    }

    protected void GenerateChairRail(Transform parent, Vector3 start, Vector3 end, Vector3 inwardNormal, float centerY, float height, float depth, string name = "ChairRail")
    {
        CreateTrimStrip(parent, start, end, inwardNormal, centerY, height, depth, name);
    }

    protected void GenerateShadowGap(Transform parent, Vector3 start, Vector3 end, Vector3 inwardNormal, float centerY, string name = "ShadowGap")
    {
        Material gapMaterial = ceilingMaterial != null ? ceilingMaterial : trimMaterial;
        CreateTrimStrip(parent, start, end, inwardNormal, centerY, 0.012f, 0.01f, name, gapMaterial);
    }

    protected void GenerateCeilingBeam(Transform parent, Vector3 start, Vector3 end, Vector3 inwardNormal, float centerY, float size, string name = "CeilingBeam")
    {
        Material beamMaterial = trimMaterial != null ? trimMaterial : wallMaterial;
        CreateTrimStrip(parent, start, end, inwardNormal, centerY, size, size, name, beamMaterial);
    }

    private GeneratedRoom FindGeneratedRoomByTransform(Transform roomTransform)
    {
        if (roomTransform == null)
        {
            return null;
        }

        foreach (GeneratedRoom room in generatedRooms.Values)
        {
            if (room?.transform == roomTransform)
            {
                return room;
            }
        }

        return null;
    }

    private void CreateTrimStrip(Transform parent, Vector3 start, Vector3 end, Vector3 inwardNormal, float centerY, float height, float depth, string name, Material materialOverride = null)
    {
        Vector3 direction = end - start;
        float length = direction.magnitude;
        if (length <= 0.05f)
        {
            return;
        }

        Vector3 normal = inwardNormal.normalized;
        if (normal.sqrMagnitude <= 0.001f)
        {
            normal = Vector3.forward;
        }

        GameObject trim = GameObject.CreatePrimitive(PrimitiveType.Cube);
        trim.name = name;
        trim.transform.SetParent(parent);
        trim.transform.position = (start + end) * 0.5f + normal * (depth * 0.5f);
        trim.transform.position = new Vector3(trim.transform.position.x, centerY, trim.transform.position.z);
        trim.transform.rotation = Quaternion.LookRotation(normal, Vector3.up);
        trim.transform.localScale = new Vector3(length, height, depth);

        Renderer renderer = trim.GetComponent<Renderer>();
        Material trimSurfaceMaterial = materialOverride != null ? materialOverride : trimMaterial;
        renderer.sharedMaterial = trimSurfaceMaterial != null ? trimSurfaceMaterial : wallMaterial;

        Collider collider = trim.GetComponent<Collider>();
        if (collider != null)
        {
            collider.enabled = false;
            if (Application.isPlaying)
                Destroy(collider);
            else
                DestroyImmediate(collider);
        }
    }

    protected virtual void GenerateStyleSpotlights()
    {
        if (generatedRoot == null || generatedRooms.Count == 0)
        {
            return;
        }

        GameObject spotRoot = new GameObject("ArtworkSpotlights");
        spotRoot.transform.SetParent(generatedRoot.transform);
        spotRoot.transform.localPosition = Vector3.zero;
        spotRoot.transform.localRotation = Quaternion.identity;

        float spacing = galleryStyle == GalleryStyle.Classical ? 2.5f : 2.8f;
        int maxSpotlights = galleryStyle == GalleryStyle.Industrial ? 18 : 24;
        int created = 0;

        foreach (GeneratedRoom room in generatedRooms.Values)
        {
            if (room == null || room.walls == null)
            {
                continue;
            }

            foreach (WallInfo wall in room.walls.Values)
            {
                if (created >= maxSpotlights)
                {
                    return;
                }

                if (wall == null || wall.length < 1f || wall.height < 1.8f || wall.name == WallNames.Center)
                {
                    continue;
                }

                int lightsOnWall = Mathf.Clamp(Mathf.RoundToInt(wall.length / spacing), 1, 4);
                float step = wall.length / (lightsOnWall + 1);
                Vector3 wallDir = (wall.endPoint - wall.startPoint).normalized;

                for (int i = 0; i < lightsOnWall && created < maxSpotlights; i++)
                {
                    float distanceAlong = step * (i + 1);
                    Vector3 wallPoint = wall.startPoint + wallDir * distanceAlong;
                    float targetY = Mathf.Clamp(room.floorY + 1.55f, room.floorY + 1.25f, room.floorY + wall.height - 0.6f);
                    float lightY = Mathf.Clamp(targetY + 0.65f, room.floorY + 1.8f, room.floorY + wall.height - 0.22f);
                    Vector3 lightPos = new Vector3(wallPoint.x, lightY, wallPoint.z) + wall.normal * 0.3f;
                    Vector3 targetPos = new Vector3(wallPoint.x, targetY, wallPoint.z) + wall.normal * 0.02f;

                    GameObject lightObj = new GameObject($"WallSpotlight_{created + 1}");
                    lightObj.transform.SetParent(spotRoot.transform);
                    lightObj.transform.position = lightPos;
                    lightObj.transform.rotation = Quaternion.LookRotation((targetPos - lightPos).normalized, Vector3.up);

                    Light spot = lightObj.AddComponent<Light>();
                    spot.type = LightType.Spot;
                    spot.color = GetStyleLightColor();
                    spot.intensity = GetStyleSpotlightIntensity();
                    spot.range = GetStyleSpotlightRange();
                    spot.spotAngle = GetStyleSpotlightAngle();
                    spot.shadows = LightShadows.None;

                    created++;
                }
            }
        }
    }

    private void ApplyProceduralSurfaceVariation()
    {
        if (generatedRoot == null)
        {
            return;
        }

        Renderer[] renderers = generatedRoot.GetComponentsInChildren<Renderer>(includeInactive: true);
        if (renderers == null || renderers.Length == 0)
        {
            return;
        }

        Dictionary<Transform, GeneratedRoom> roomLookup = new Dictionary<Transform, GeneratedRoom>();
        foreach (GeneratedRoom room in generatedRooms.Values)
        {
            if (room?.transform != null && !roomLookup.ContainsKey(room.transform))
            {
                roomLookup.Add(room.transform, room);
            }
        }

        MaterialPropertyBlock propertyBlock = new MaterialPropertyBlock();

        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer == null)
            {
                continue;
            }

            string name = renderer.gameObject.name.ToLowerInvariant();
            bool isWall = IsWallRendererName(name);
            bool isFloor = name.Contains("floor");
            bool isCeiling = name.Contains("ceiling");

            if (!isWall && !isFloor && !isCeiling)
            {
                continue;
            }

            if (isWall && wallMaterial != null)
            {
                renderer.sharedMaterial = wallMaterial;
            }
            else if (isFloor && floorMaterial != null)
            {
                renderer.sharedMaterial = floorMaterial;
            }
            else if (isCeiling && ceilingMaterial != null)
            {
                renderer.sharedMaterial = ceilingMaterial;
            }

            Color? overrideColor = null;
            float smoothness = -1f;
            float metallic = -1f;
            GeneratedRoom room = FindOwningRoom(renderer.transform, roomLookup);

            if (isWall)
            {
                switch (galleryStyle)
                {
                    case GalleryStyle.Classical:
                        overrideColor = GetClassicalWallVariation(renderer);
                        smoothness = Mathf.Lerp(0.3f, 0.4f, GetVariationNoise(renderer.bounds.center, 0.6f));
                        metallic = 0f;
                        break;
                    case GalleryStyle.Industrial:
                        overrideColor = GetIndustrialWallVariation(renderer, room);
                        smoothness = Mathf.Lerp(0.05f, 0.1f, GetVariationNoise(renderer.bounds.center, 0.75f));
                        metallic = 0f;
                        break;
                    case GalleryStyle.Contemporary:
                    default:
                        overrideColor = GetContemporaryWallVariation(renderer);
                        smoothness = Mathf.Lerp(0.1f, 0.2f, GetVariationNoise(renderer.bounds.center, 0.7f));
                        metallic = 0f;
                        break;
                }
            }
            else if (isCeiling)
            {
                switch (galleryStyle)
                {
                    case GalleryStyle.Classical:
                        overrideColor = OffsetColor(Rgb(250, 248, 243), (GetVariationNoise(renderer.bounds.center, 0.4f) - 0.5f) * 0.014f);
                        smoothness = 0.12f;
                        metallic = 0f;
                        break;
                    case GalleryStyle.Industrial:
                        overrideColor = OffsetColor(Rgb(58, 58, 56), (GetVariationNoise(renderer.bounds.center, 0.5f) - 0.5f) * 0.018f);
                        smoothness = 0.04f;
                        metallic = 0f;
                        break;
                    case GalleryStyle.Contemporary:
                    default:
                        overrideColor = OffsetColor(Rgb(34, 34, 36), (GetVariationNoise(renderer.bounds.center, 0.5f) - 0.5f) * 0.02f);
                        smoothness = 0.03f;
                        metallic = 0f;
                        break;
                }
            }

            if (!overrideColor.HasValue && smoothness < 0f && metallic < 0f)
            {
                continue;
            }

            propertyBlock.Clear();
            if (overrideColor.HasValue)
            {
                propertyBlock.SetColor("_BaseColor", overrideColor.Value);
                propertyBlock.SetColor("_Color", overrideColor.Value);
            }
            if (smoothness >= 0f)
            {
                propertyBlock.SetFloat("_Smoothness", smoothness);
            }
            if (metallic >= 0f)
            {
                propertyBlock.SetFloat("_Metallic", metallic);
            }

            renderer.SetPropertyBlock(propertyBlock);
        }
    }

    private bool IsWallRendererName(string lowerName)
    {
        if (string.IsNullOrEmpty(lowerName))
        {
            return false;
        }

        return lowerName.Contains("wall") ||
               lowerName.Contains("partition") ||
               lowerName.Contains("segment") ||
               lowerName.Contains("transitionwall");
    }

    private GeneratedRoom FindOwningRoom(Transform rendererTransform, Dictionary<Transform, GeneratedRoom> roomLookup)
    {
        Transform cursor = rendererTransform;
        while (cursor != null)
        {
            if (roomLookup.TryGetValue(cursor, out var room))
            {
                return room;
            }
            cursor = cursor.parent;
        }

        return null;
    }

    private Color GetContemporaryWallVariation(Renderer renderer)
    {
        Color[] shades =
        {
            Rgb(247, 247, 245),
            Rgb(245, 245, 243),
            Rgb(243, 243, 241)
        };

        float noise = GetVariationNoise(renderer.bounds.center, 0.8f);
        int index = Mathf.Clamp(Mathf.FloorToInt(noise * shades.Length), 0, shades.Length - 1);
        return shades[index];
    }

    private Color GetClassicalWallVariation(Renderer renderer)
    {
        Color baseColor = GetMaterialBaseColor(wallMaterial, Rgb(237, 229, 212));
        float delta = (GetVariationNoise(renderer.bounds.center, 0.9f) - 0.5f) * 0.024f;
        return OffsetColor(baseColor, delta);
    }

    private Color GetIndustrialWallVariation(Renderer renderer, GeneratedRoom room)
    {
        if (IsIndustrialAccentWall(renderer, room))
        {
            float delta = (GetVariationNoise(renderer.bounds.center, 0.95f) - 0.5f) * 0.02f;
            return OffsetColor(Rgb(136, 94, 72), delta);
        }

        Color a = Rgb(124, 84, 64);
        Color b = Rgb(112, 76, 58);
        float blend = GetVariationNoise(renderer.bounds.center, 0.85f);
        return Color.Lerp(a, b, blend);
    }

    private bool IsIndustrialAccentWall(Renderer renderer, GeneratedRoom room)
    {
        string name = renderer.gameObject.name.ToLowerInvariant();
        if (name.Contains("left") || name.Contains("back"))
        {
            return true;
        }

        if (room == null)
        {
            return false;
        }

        Bounds bounds = renderer.bounds;
        float halfWidth = room.dimensions != null ? room.dimensions.width * 0.5f : 0f;
        float halfDepth = room.dimensions != null ? GetRoomDepth(room.dimensions) * 0.5f : 0f;
        float tolerance = wallThickness + 0.35f;

        float expectedLeftX = room.center.x - halfWidth;
        float expectedBackZ = room.center.z - halfDepth;

        bool nearLeft = Mathf.Abs(bounds.center.x - expectedLeftX) <= tolerance;
        bool nearBack = Mathf.Abs(bounds.center.z - expectedBackZ) <= tolerance;
        return nearLeft || nearBack;
    }

    private float GetVariationNoise(Vector3 position, float frequency)
    {
        float x = (position.x + styleSeed * 0.173f) * frequency;
        float z = (position.z + styleSeed * 0.257f) * frequency;
        return Mathf.PerlinNoise(x, z);
    }

    private Color GetMaterialBaseColor(Material material, Color fallback)
    {
        if (material == null)
        {
            return fallback;
        }

        if (material.HasProperty("_BaseColor"))
        {
            return material.GetColor("_BaseColor");
        }
        if (material.HasProperty("_Color"))
        {
            return material.GetColor("_Color");
        }
        return fallback;
    }

    protected float GetStyleSpotlightIntensity()
    {
        switch (galleryStyle)
        {
            case GalleryStyle.Classical:
                return 1.5f;
            case GalleryStyle.Industrial:
                return 2.5f;
            case GalleryStyle.Contemporary:
            default:
                return 2f;
        }
    }

    protected float GetStyleSpotlightRange()
    {
        switch (galleryStyle)
        {
            case GalleryStyle.Classical:
                return 4.2f;
            case GalleryStyle.Industrial:
                return 5f;
            case GalleryStyle.Contemporary:
            default:
                return 4.6f;
        }
    }

    protected float GetStyleSpotlightAngle()
    {
        switch (galleryStyle)
        {
            case GalleryStyle.Classical:
                return 45f;
            case GalleryStyle.Industrial:
                return 55f;
            case GalleryStyle.Contemporary:
            default:
                return 50f;
        }
    }

    private void DestroyRuntimeMaterial(ref Material material)
    {
        if (material == null)
        {
            return;
        }

        if (Application.isPlaying)
            Destroy(material);
        else
            DestroyImmediate(material);

        material = null;
    }

    private void DestroyGeneratedTextures()
    {
        for (int i = 0; i < generatedTextures.Count; i++)
        {
            Texture2D tex = generatedTextures[i];
            if (tex == null)
            {
                continue;
            }

            if (Application.isPlaying)
                Destroy(tex);
            else
                DestroyImmediate(tex);
        }

        generatedTextures.Clear();
    }

    // Creates a URP Lit material with style-controlled metallic/smoothness settings.
    // Falls back to Standard or Diffuse shader if URP isn't available.
    protected Material CreateMaterial(string name, Color color, float metallic = 0f, float smoothness = 0.1f, Texture2D baseMap = null, Vector2? tiling = null)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        if (shader == null) shader = Shader.Find("Diffuse");

        if (shader == null)
        {
            Debug.LogError("[TopologyGenerator] No shader found!");
            return null;
        }

        Material mat = new Material(shader);
        mat.name = $"Generated_{name}";

        if (mat.HasProperty("_BaseColor"))
            mat.SetColor("_BaseColor", color);
        else if (mat.HasProperty("_Color"))
            mat.SetColor("_Color", color);

        if (mat.HasProperty("_Smoothness"))
            mat.SetFloat("_Smoothness", smoothness);
        if (mat.HasProperty("_Metallic"))
            mat.SetFloat("_Metallic", metallic);

        if (baseMap != null)
        {
            if (mat.HasProperty("_BaseMap"))
                mat.SetTexture("_BaseMap", baseMap);
            if (mat.HasProperty("_MainTex"))
                mat.SetTexture("_MainTex", baseMap);

            Vector2 textureTiling = tiling ?? Vector2.one;
            if (mat.HasProperty("_BaseMap"))
                mat.SetTextureScale("_BaseMap", textureTiling);
            if (mat.HasProperty("_MainTex"))
                mat.SetTextureScale("_MainTex", textureTiling);
        }

        return mat;
    }

    // Creates a quad mesh (4 vertices, 2 triangles) facing in the given direction.
    // Used by some generators to build walls from mesh instead of cubes.
    protected Mesh CreateQuadMesh(float width, float height, Vector3 facing)
    {
        Mesh mesh = new Mesh();
        mesh.name = "GeneratedQuad";

        Vector3 forward = facing.normalized;
        Vector3 right, up;

        if (Mathf.Abs(Vector3.Dot(forward, Vector3.up)) > 0.9f)
        {
            // Facing up or down - use world right as reference
            right = Vector3.right;
            up = Vector3.Cross(right, forward).normalized;
            right = Vector3.Cross(forward, up).normalized;
        }
        else
        {
            // Facing horizontally - use world up as reference
            right = Vector3.Cross(Vector3.up, forward).normalized;
            up = Vector3.up;
        }

        float halfW = width / 2f;
        float halfH = height / 2f;

        Vector3[] vertices = new Vector3[]
        {
            -right * halfW - up * halfH,
            right * halfW - up * halfH,
            right * halfW + up * halfH,
            -right * halfW + up * halfH
        };

        int[] triangles = new int[] { 0, 2, 1, 0, 3, 2 };

        Vector2[] uvs = new Vector2[]
        {
            new Vector2(0, 0),
            new Vector2(1, 0),
            new Vector2(1, 1),
            new Vector2(0, 1)
        };

        Vector3[] normals = new Vector3[] { forward, forward, forward, forward };

        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.uv = uvs;
        mesh.normals = normals;

        return mesh;
    }

    // Creates a horizontal quad at y=0 (floor or ceiling depending on faceUp).
    protected Mesh CreateFloorMesh(float width, float depth, bool faceUp = true)
    {
        Mesh mesh = new Mesh();
        mesh.name = faceUp ? "GeneratedFloor" : "GeneratedCeiling";

        float halfW = width / 2f;
        float halfD = depth / 2f;

        Vector3[] vertices = new Vector3[]
        {
            new Vector3(-halfW, 0, -halfD),
            new Vector3(halfW, 0, -halfD),
            new Vector3(halfW, 0, halfD),
            new Vector3(-halfW, 0, halfD)
        };

        // Winding order flipped for ceiling so the face points downward
        int[] triangles = faceUp
            ? new int[] { 0, 2, 1, 0, 3, 2 }
            : new int[] { 0, 1, 2, 0, 2, 3 };

        Vector2[] uvs = new Vector2[]
        {
            new Vector2(0, 0),
            new Vector2(1, 0),
            new Vector2(1, 1),
            new Vector2(0, 1)
        };

        Vector3 normal = faceUp ? Vector3.up : Vector3.down;
        Vector3[] normals = new Vector3[] { normal, normal, normal, normal };

        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.uv = uvs;
        mesh.normals = normals;

        return mesh;
    }

    // Creates a vertical wall mesh. UVs are scaled to real-world size (1 UV unit = 2 meters)
    // so textures tile at a consistent density regardless of wall dimensions.
    protected Mesh CreateWallMesh(float length, float height, Vector3 normal)
    {
        Mesh mesh = new Mesh();
        mesh.name = "GeneratedWall";

        Vector3 right = Vector3.Cross(Vector3.up, normal).normalized;
        if (right.magnitude < 0.01f)
        {
            right = Vector3.forward;
        }

        float halfL = length / 2f;

        Vector3[] vertices = new Vector3[]
        {
            -right * halfL + Vector3.zero,
            right * halfL + Vector3.zero,
            right * halfL + Vector3.up * height,
            -right * halfL + Vector3.up * height
        };

        int[] triangles = new int[] { 0, 2, 1, 0, 3, 2 };

        float uScale = length / 2f;
        float vScale = height / 2f;
        Vector2[] uvs = new Vector2[]
        {
            new Vector2(0, 0),
            new Vector2(uScale, 0),
            new Vector2(uScale, vScale),
            new Vector2(0, vScale)
        };

        Vector3[] normals = new Vector3[] { normal, normal, normal, normal };

        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.uv = uvs;
        mesh.normals = normals;

        return mesh;
    }

    // Creates a GameObject with a MeshFilter, MeshRenderer, and MeshCollider.
    // Used by generators that build walls from procedural meshes instead of cubes.
    protected GameObject CreateMeshObject(string name, Mesh mesh, Material material, Transform parent)
    {
        GameObject obj = new GameObject(name);
        obj.transform.SetParent(parent);
        obj.transform.localPosition = Vector3.zero;
        obj.transform.localRotation = Quaternion.identity;

        MeshFilter filter = obj.AddComponent<MeshFilter>();
        filter.mesh = mesh;

        MeshRenderer renderer = obj.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;

        MeshCollider collider = obj.AddComponent<MeshCollider>();
        collider.sharedMesh = mesh;

        return obj;
    }
}

// Result of a placement calculation. Contains everything ArtworkPlacer needs
// to position a frame or pedestal in the scene.
public class PlacementResult
{
    public Vector3 position;
    public Quaternion rotation;
    public Vector3 wallNormal;
    public string roomId;
    public string wallName;
}

// Tracks a generated room so we can look it up later for placement.
// Stores the room center, floor height, and all wall surfaces.
public class GeneratedRoom
{
    public string id;
    public RoomDimensions dimensions;
    public Vector3 center;
    public float floorY;
    public Transform transform;
    public Dictionary<string, WallInfo> walls = new Dictionary<string, WallInfo>();
}

// Describes one wall surface inside a room. startPoint and endPoint define
// the two ends of the wall at floor level. The normal points into the room
// (the direction artwork should face).
public class WallInfo
{
    public string name;
    public Vector3 startPoint;
    public Vector3 endPoint;
    public Vector3 normal;
    public float length;
    public float height;
    public Transform transform;
}
