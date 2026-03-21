using UnityEngine;
using System;
using System.Collections.Generic;

// Data classes that map directly to the v2.0 gallery manifest JSON.
// ManifestLoader parses the API response into these.
//
// The manifest has five top-level sections:
//   locked_constraints  - what the user picked (topology, rooms, theme)
//   derived_parameters  - backend-calculated values (mood, pacing, spacing)
//   layout_plan         - exact room dimensions in meters
//   placement_plan      - where each artwork goes (wall, position, height)
//   assets              - URLs + metadata for each artwork

// The root object we get back from the API (v2.0 schema).
[Serializable]
public class GalleryManifest
{
    public string gallery_id;
    public string schema_version;
    public string created_at;
    public string gallery_style; // optional top-level style override

    public LockedConstraints locked_constraints;
    public DerivedParameters derived_parameters;
    public List<RoomLayoutEntry> layout_plan;
    public List<PlacementInstruction> placement_plan;
    public List<ArtworkAsset> assets;

    // Cached wrapper built from the layout_plan list on first access.
    [NonSerialized] private LayoutPlanWrapper _layoutPlanCache;

    // Converts the List<RoomLayoutEntry> into a LayoutPlanWrapper with
    // separate dictionaries for rooms and alcoves, for fast typed lookup.
    // Used by TopologyGenerator.Generate() and other geometry code.
    public LayoutPlanWrapper GetLayoutPlanWrapper()
    {
        if (_layoutPlanCache != null) return _layoutPlanCache;

        _layoutPlanCache = new LayoutPlanWrapper();
        _layoutPlanCache.rooms = new Dictionary<string, RoomDimensions>();
        _layoutPlanCache.alcoves = new Dictionary<string, AlcoveDimensions>();

        if (layout_plan != null)
        {
            foreach (var entry in layout_plan)
            {
                if (string.IsNullOrEmpty(entry.room_id) || entry.data == null) continue;

                if (entry.IsAlcove)
                {
                    _layoutPlanCache.alcoves[entry.room_id] = entry.ToAlcoveDimensions();
                }
                else
                {
                    _layoutPlanCache.rooms[entry.room_id] = entry.ToRoomDimensions();
                }
            }
        }

        return _layoutPlanCache;
    }

    public string GetGalleryId() => gallery_id;

    // Returns topology, normalizing casing: "LinearCorridor" -> "linear_corridor"
    public string GetTopology()
    {
        if (locked_constraints == null || string.IsNullOrEmpty(locked_constraints.topology))
            return TopologyTypes.LinearCorridor; // safe default
        
        return NormalizeTopology(locked_constraints.topology);
    }
    
    // Converts "LinearCorridor" or "linear_corridor" to canonical "linear_corridor" format
    private string NormalizeTopology(string input)
    {
        if (string.IsNullOrEmpty(input)) return TopologyTypes.LinearCorridor;
        
        string lower = input.ToLowerInvariant();
        
        // Handle common variations
        if (lower == "linearcorridor" || lower == "linear_corridor" || lower == "corridor")
            return TopologyTypes.LinearCorridor;
        if (lower == "linearwithalcoves" || lower == "linear_with_alcoves")
            return TopologyTypes.LinearWithAlcoves;
        if (lower == "branchingrooms" || lower == "branching_rooms")
            return TopologyTypes.BranchingRooms;
        if (lower == "hubandspoke" || lower == "hub_and_spoke")
            return TopologyTypes.HubAndSpoke;
        if (lower == "openhall" || lower == "open_hall")
            return TopologyTypes.OpenHall;
        
        // Unknown - return as-is and let the switch statement fail gracefully
        return lower;
    }

    // Returns theme from locked_constraints
    public string GetTheme()
    {
        if (locked_constraints != null && !string.IsNullOrEmpty(locked_constraints.theme))
            return locked_constraints.theme;
        return "modern";
    }

    // Returns gallery style from locked_constraints.gallery_style first,
    // then top-level gallery_style. Defaults to contemporary.
    public string GetGalleryStyle()
    {
        if (locked_constraints != null && !string.IsNullOrEmpty(locked_constraints.gallery_style))
        {
            return NormalizeGalleryStyle(locked_constraints.gallery_style);
        }

        if (!string.IsNullOrEmpty(gallery_style))
        {
            return NormalizeGalleryStyle(gallery_style);
        }

        return GalleryStyleIds.Contemporary;
    }

    private string NormalizeGalleryStyle(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return GalleryStyleIds.Contemporary;
        }

        string lower = input.Trim().ToLowerInvariant();

        if (lower == "classical")
            return GalleryStyleIds.Classical;
        if (lower == "industrial")
            return GalleryStyleIds.Industrial;
        if (lower == "contemporary" || lower == "white_cube" || lower == "whitecube" || lower == "moma")
            return GalleryStyleIds.Contemporary;

        return GalleryStyleIds.Contemporary;
    }

    // Look up an asset by its ID (used when matching placements to assets)
    public ArtworkAsset GetAssetById(string assetId)
    {
        if (assets == null || string.IsNullOrEmpty(assetId)) return null;
        return assets.Find(a => a.id == assetId);
    }

    // Look up room dimensions from the layout plan
    public RoomDimensions GetRoomDimensions(string roomId)
    {
        var wrapper = GetLayoutPlanWrapper();
        if (wrapper == null || string.IsNullOrEmpty(roomId)) return null;
        return wrapper.GetRoom(roomId);
    }

    // Look up alcove dimensions from the layout plan
    public AlcoveDimensions GetAlcoveDimensions(string roomId)
    {
        var wrapper = GetLayoutPlanWrapper();
        if (wrapper == null || string.IsNullOrEmpty(roomId)) return null;
        return wrapper.GetAlcove(roomId);
    }

    // Get every placement instruction targeting a specific room
    public List<PlacementInstruction> GetPlacementsForRoom(string roomId)
    {
        if (placement_plan == null) return new List<PlacementInstruction>();
        return placement_plan.FindAll(p => p.room_id == roomId);
    }

    // Checks all required fields exist. Returns null if valid, or an error message string.
    public string Validate()
    {
        if (string.IsNullOrEmpty(gallery_id))
            return "Missing gallery_id";

        if (locked_constraints == null)
            return "Missing locked_constraints";

        if (string.IsNullOrEmpty(locked_constraints.topology))
            return "Missing locked_constraints.topology";

        if (locked_constraints.rooms == null || locked_constraints.rooms.Count == 0)
            return "Missing locked_constraints.rooms";

        if (layout_plan == null || layout_plan.Count == 0)
            return "Missing layout_plan";

        // Every room in constraints must have matching dimensions in layout_plan
        foreach (var room in locked_constraints.rooms)
        {
            if (room.type == RoomTypes.Alcove)
            {
                if (GetAlcoveDimensions(room.id) == null)
                    return $"Missing layout_plan for alcove '{room.id}'";
            }
            else
            {
                if (GetRoomDimensions(room.id) == null)
                    return $"Missing layout_plan for room '{room.id}'";
            }
        }

        if (placement_plan == null || placement_plan.Count == 0)
            return "Missing placement_plan";

        if (assets == null || assets.Count == 0)
            return "Missing assets";

        // Every placement must point to a real asset
        foreach (var placement in placement_plan)
        {
            if (GetAssetById(placement.asset_id) == null)
                return $"Placement references unknown asset '{placement.asset_id}'";
        }

        return null;
    }
}

// What the user configured in the web app: which topology, which rooms, which theme.
[Serializable]
public class LockedConstraints
{
    // "linear_corridor", "linear_with_alcoves", "branching_rooms", "hub_and_spoke", "open_hall"
    public string topology;
    public List<RoomConstraint> rooms;
    // "classical", "modern", "gothic", "nature", etc.
    public string theme;
    // "contemporary", "classical", "industrial" (optional)
    public string gallery_style;
}

// One room or space inside the gallery (schema: RoomEntry).
// Contains metadata only - spatial data is in layout_plan[id].
[Serializable]
public class RoomConstraint
{
    public string id;           // unique, referenced by layout_plan and placement_plan
    public string type;         // "corridor", "alcove", "hub", "open_hall", "room_small", "room_medium"
    public string content_type; // "2d", "sculpture", "mixed"
    public int content_count;   // how many artworks go in this room
}

// Backend-calculated feel values (not user-configured).
[Serializable]
public class DerivedParameters
{
    public string mood;           // "serene", "dramatic", "playful", "contemplative", "energetic"
    public float pacing;          // 0 = sparse/slow, 1 = dense/fast
    public float target_spacing_m; // meters between artworks
}

// All spatial data for one room or alcove, nested inside RoomLayoutEntry.
// JsonUtility serializes this as the "data" object inside each layout_plan element.
[Serializable]
public class LayoutData
{
    // Room/corridor fields
    public float length;
    public float width;
    public float height;
    public float depth;
    public List<Doorway> doorways;
    public string direction_from_hub;
    public string entry_wall;
    public float entry_position;
    public float entry_width;
    public List<PartitionDefinition> partitions;

    // Alcove fields (when present, this entry is an alcove)
    public string parent;
    public string side;
    public float position_along_parent;

    public bool IsAlcove => !string.IsNullOrEmpty(parent) || !string.IsNullOrEmpty(side);

    public RoomDimensions ToRoomDimensions()
    {
        return new RoomDimensions
        {
            length = length,
            width = width,
            height = height,
            depth = depth,
            doorways = doorways,
            direction_from_hub = direction_from_hub,
            entry_wall = entry_wall,
            entry_position = entry_position,
            entry_width = entry_width,
            partitions = partitions
        };
    }

    public AlcoveDimensions ToAlcoveDimensions()
    {
        return new AlcoveDimensions
        {
            parent = parent,
            side = side,
            position_along_parent = position_along_parent,
            width = width,
            depth = depth,
            height = height
        };
    }
}

// Serializable entry for a single room or alcove in the layout_plan array.
// The backend sends layout_plan as a JSON array where each element carries
// its own room_id plus a nested "data" object with dimensions.
// JsonUtility handles this natively — no custom parsing needed.
[Serializable]
public class RoomLayoutEntry
{
    public string room_id;
    public LayoutData data;

    public bool IsAlcove => data != null && data.IsAlcove;

    public RoomDimensions ToRoomDimensions() => data?.ToRoomDimensions();
    public AlcoveDimensions ToAlcoveDimensions() => data?.ToAlcoveDimensions();
}

// Wrapper around the layout_plan data.
// Built from List<RoomLayoutEntry> by GalleryManifest.GetLayoutPlanWrapper().
[Serializable]
public class LayoutPlanWrapper
{
    // All rooms, alcoves, etc. stored by their id
    [NonSerialized] public Dictionary<string, RoomDimensions> rooms = new Dictionary<string, RoomDimensions>();
    [NonSerialized] public Dictionary<string, AlcoveDimensions> alcoves = new Dictionary<string, AlcoveDimensions>();

    public RoomDimensions GetRoom(string roomId)
    {
        if (rooms != null && rooms.TryGetValue(roomId, out var dims)) return dims;
        return null;
    }

    public AlcoveDimensions GetAlcove(string roomId)
    {
        if (alcoves != null && alcoves.TryGetValue(roomId, out var dims)) return dims;
        return null;
    }

    public bool IsAlcove(string roomId)
    {
        return alcoves != null && alcoves.ContainsKey(roomId);
    }
}

// A door opening on one wall of a room.
[Serializable]
public class Doorway
{
    public string wall;       // "front", "back", "left", "right"
    public float position;    // meters from left edge of wall
    public float width;       // door width in meters
    public string connects_to; // room id on the other side
}

// A freestanding wall inside an open hall (used as extra hanging surface).
[Serializable]
public class PartitionDefinition
{
    public string id;
    public float x;
    public float z;
    public float rotation;
    public float length;
    public float height;
}

// Size of a room or corridor in meters.
// Different topology types use different subsets of these fields.
[Serializable]
public class RoomDimensions
{
    public float length; // corridor: along Z axis. rooms: depth on Z.
    public float width;  // X axis
    public float height; // Y axis
    public float depth;  // alcoves only: how far it extends from corridor

    public List<Doorway> doorways; // branching rooms: where the doors are

    // hub-and-spoke: which direction this room faces ("north", "south", "east", "west")
    public string direction_from_hub;

    // open hall entry opening
    public string entry_wall;    // "back", "front", "left", "right"
    public float entry_position; // meters from left edge
    public float entry_width;    // meters

    // open hall: freestanding partition walls
    public List<PartitionDefinition> partitions;

    public override string ToString() => $"[{width}m x {length}m x {height}m]";
}

// Size of an alcove branching off a corridor.
[Serializable]
public class AlcoveDimensions
{
    public string parent;              // id of the parent corridor
    public string side;                // "left" or "right"
    public float position_along_parent; // meters from corridor entry
    public float width;
    public float depth;
    public float height;

    public override string ToString() => $"[alcove {width}m x {depth}m x {height}m on {side} @ {position_along_parent}m]";
}

// Where to place one artwork. The backend decides all of these values;
// Unity just reads them and puts the frame/pedestal at the right spot.
[Serializable]
public class PlacementInstruction
{
    public string asset_id; // references an entry in the assets[] array
    public string room_id;  // references a room in locked_constraints.rooms[]

    // Standard wall placement
    public string wall;               // "left", "right", "front", "back", "center" (sculptures)
    public float position_along_wall; // meters from start of wall
    public float height;              // meters from floor (1.5 = eye level)
    public bool is_hero;              // hero pieces get bigger frames + spotlights

    // Sculpture floor position (relative to room center)
    public Vector3Serializable local_position;

    // Open hall placement (uses surface names like "wall_left", "partition_1")
    public string surface;
    public string side;           // "front" or "back" for partition surfaces
    public float position_along;  // meters along the surface

    // Open hall sculptures: absolute floor position in hall coordinates
    public FloorPosition floor_position;
}

// Simple x/z pair for floor positions.
[Serializable]
public class FloorPosition
{
    public float x;
    public float z;
}

// Unity's Vector3 doesn't serialize well with JsonUtility,
// so we use this wrapper and convert with ToVector3().
[Serializable]
public class Vector3Serializable
{
    public float x;
    public float y;
    public float z;

    public Vector3 ToVector3() => new Vector3(x, y, z);

    public static Vector3Serializable FromVector3(Vector3 v)
    {
        return new Vector3Serializable { x = v.x, y = v.y, z = v.z };
    }
}

// Metadata for one artwork (image or sculpture).
[Serializable]
public class ArtworkAsset
{
    public string id;   // unique, referenced by placement_plan
    public string url;  // download URL (R2 storage). images: jpg/png, sculptures: glb
    public string type; // "2d" or "sculpture"

    // 2D artwork dimensions in meters
    public float width;
    public float height;

    // Sculpture uniform scale factor
    public float scale;

    // How much visual attention this piece draws (0 = subtle, 1 = dominant).
    // Controls frame scale and spotlight intensity.
    public float visual_weight;

    // Placement role decided by the backend:
    //   "hero"    - focal point, gets biggest frame + spotlight
    //   "cluster" - grouped with other cluster pieces nearby
    //   "ambient" - fills remaining space, smaller frame, no spotlight
    public string role;

    // How well this artwork fits the gallery theme (0-1)
    public float theme_alignment;

    // How visually different this artwork is from the others (0-1)
    public float distinctiveness;

    // Group ID for clustered pieces (e.g. "cluster_1"). Null means ungrouped.
    public string cluster_id;

    // The prompt used to generate this artwork
    public string prompt;

    public bool Is2D => type == "2d";
    public bool IsSculpture => type == "sculpture";

    // Safe accessors that return sensible defaults when fields are missing/zero
    public string GetRole() => string.IsNullOrEmpty(role) ? "ambient" : role;
    public float GetVisualWeight() => visual_weight > 0f ? visual_weight : 0.3f;
    public string GetClusterId() => cluster_id;
}

// String constants for topology types so we don't scatter magic strings everywhere.
public static class TopologyTypes
{
    public const string LinearCorridor = "linear_corridor";
    public const string LinearWithAlcoves = "linear_with_alcoves";
    public const string BranchingRooms = "branching_rooms";
    public const string HubAndSpoke = "hub_and_spoke";
    public const string OpenHall = "open_hall";

    public static readonly string[] All = new[]
    {
        LinearCorridor, LinearWithAlcoves, BranchingRooms, HubAndSpoke, OpenHall
    };

    public static bool IsValid(string topology) => Array.Exists(All, t => t == topology);
}

public static class RoomTypes
{
    public const string Corridor = "corridor";
    public const string Alcove = "alcove";
    public const string RoomSmall = "room_small";
    public const string RoomMedium = "room_medium";
    public const string RoomLarge = "room_large";
    public const string Hub = "hub";
}

public static class ContentTypes
{
    public const string Art2D = "2d";
    public const string Sculpture = "sculpture";
    public const string Mixed = "mixed";
}

public static class GalleryStyleIds
{
    public const string Contemporary = "contemporary";
    public const string Classical = "classical";
    public const string Industrial = "industrial";
}

public static class WallNames
{
    public const string Left = "left";
    public const string Right = "right";
    public const string Front = "front";
    public const string Back = "back";
    public const string Center = "center"; // floor-placed sculptures
}
