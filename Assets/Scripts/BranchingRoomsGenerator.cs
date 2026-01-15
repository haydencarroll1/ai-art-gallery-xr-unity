using UnityEngine;
using System.Collections.Generic;

// Sequential connected rooms with doorways, placed along +Z.
//
// Schema expects:
//   - All entries in rooms[] are actual rooms
//   - Dimensions from layout_plan[room.id] with width, length/depth, height
//   - Doorways from layout_plan[room.id].doorways (optional)
//   - Each doorway has: wall, position, width, connects_to
//
// Current implementation: places rooms sequentially along Z axis.
// Front/back walls are omitted between connected rooms to create openings.

public class BranchingRoomsGenerator : TopologyGenerator
{
    [Header("Doorway Settings")]
    [Tooltip("Default doorway height in meters")]
    public float doorwayHeight = 2.2f;
    
    [Tooltip("Default doorway width in meters")]
    public float doorwayWidth = 2.2f;

    [Header("Lighting")]
    public bool generateLights = true;
    public int lightsAcross = 2;
    public int lightsAlong = 2;
    public float lightIntensity = 1.2f;

    public override void Generate(LockedConstraints constraints, LayoutPlanWrapper layoutPlan)
    {
        if (debugMode) Debug.Log("[BranchingRoomsGenerator] Starting generation...");

        ClearGenerated();
        SetGenerationContext(constraints);
        string styleId = manifestContext != null ? manifestContext.GetGalleryStyle() : constraints?.gallery_style;
        ConfigureStyle(styleId);
        
        // Keep artwork offset clear of thicker walls
        wallOffset = Mathf.Max(wallOffset, wallThickness * 0.75f + 0.05f);

        generatedRoot = new GameObject("GeneratedGallery");
        generatedRoot.transform.SetParent(transform);
        generatedRoot.transform.position = Vector3.zero;
        generatedRoot.transform.rotation = Quaternion.identity;

        if (constraints == null || constraints.rooms == null || constraints.rooms.Count == 0)
        {
            Debug.LogError("[BranchingRoomsGenerator] No rooms defined in constraints!");
            return;
        }

        // All rooms in constraints.rooms[] are actual rooms (per schema)
        int count = Mathf.Min(5, constraints.rooms.Count); // Performance limit

        float currentZ = 0f;
        for (int i = 0; i < count; i++)
        {
            RoomConstraint roomConstraint = constraints.rooms[i];
            RoomDimensions dims = layoutPlan.GetRoom(roomConstraint.id);
            if (dims == null)
            {
                Debug.LogError($"[BranchingRoomsGenerator] Missing layout for room '{roomConstraint.id}'");
                continue;
            }

            float depth = dims.length > 0 ? dims.length : dims.depth;
            if (depth <= 0f)
            {
                Debug.LogWarning($"[BranchingRoomsGenerator] Room '{roomConstraint.id}' has no depth/length. Using 5m default.");
                depth = 5f;
            }

            // Check doorways from layout_plan for connectivity
            bool hasBackConnection = i > 0 || HasDoorwayOnWall(dims.doorways, WallNames.Back);
            bool hasFrontConnection = i < count - 1 || HasDoorwayOnWall(dims.doorways, WallNames.Front);
            
            GenerateRoom(roomConstraint.id, dims, currentZ, hasBackConnection, hasFrontConnection);
            currentZ += depth; // Rooms meet directly, no gaps
        }

        if (generateLights)
        {
            EnsureDirectionalLight();
            GenerateLighting();
        }

        ApplyStyleEnhancements(includeSpotlights: false);

        if (debugMode) Debug.Log("[BranchingRoomsGenerator] Generation complete!");
    }
    
    private bool HasDoorwayOnWall(List<Doorway> doorways, string wallName)
    {
        if (doorways == null) return false;
        foreach (var d in doorways)
        {
            if (d.wall == wallName) return true;
        }
        return false;
    }

    private void GenerateRoom(string roomId, RoomDimensions dims, float startZ, bool hasBackConnection, bool hasFrontConnection)
    {
        float width = dims.width;
        float depth = dims.length > 0 ? dims.length : dims.depth;
        float height = dims.height;

        float halfW = width / 2f;
        float halfD = depth / 2f;

        GameObject roomRoot = new GameObject($"Room_{roomId}");
        roomRoot.transform.SetParent(generatedRoot.transform);
        roomRoot.transform.localPosition = Vector3.zero;

        Vector3 roomCenter = new Vector3(0f, height / 2f, startZ + halfD);

        // Floor (cube for reliable collision)
        GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        floor.name = "Floor";
        floor.transform.SetParent(roomRoot.transform);
        floor.transform.position = new Vector3(0f, -0.1f, startZ + halfD);
        floor.transform.localScale = new Vector3(width, 0.2f, depth);
        floor.GetComponent<Renderer>().sharedMaterial = floorMaterial;

        // Ceiling
        GameObject ceiling = GameObject.CreatePrimitive(PrimitiveType.Cube);
        ceiling.name = "Ceiling";
        ceiling.transform.SetParent(roomRoot.transform);
        ceiling.transform.position = new Vector3(0f, height + 0.1f, startZ + halfD);
        ceiling.transform.localScale = new Vector3(width, 0.2f, depth);
        ceiling.GetComponent<Renderer>().sharedMaterial = ceilingMaterial;

        // Walls with doorways (segmented cubes)
        if (!hasBackConnection)
        {
            CreateWallWithDoorwaySegments(roomRoot.transform, "Wall_Back", width, height, startZ, WallNames.Back, false);
        }
        CreateWallWithDoorwaySegments(roomRoot.transform, "Wall_Front", width, height, startZ + depth, WallNames.Front, hasFrontConnection);
        CreateSideWallSolid(roomRoot.transform, "Wall_Left", depth, height, -halfW, startZ + halfD);
        CreateSideWallSolid(roomRoot.transform, "Wall_Right", depth, height, halfW, startZ + halfD);

        // Register room for placement
        GeneratedRoom room = new GeneratedRoom
        {
            id = roomId,
            dimensions = dims,
            center = roomCenter,
            floorY = 0f,
            transform = roomRoot.transform,
            walls = new Dictionary<string, WallInfo>()
        };

        if (!hasBackConnection)
        {
            room.walls[WallNames.Back] = new WallInfo
            {
                name = WallNames.Back,
                startPoint = new Vector3(-halfW, 0f, startZ),
                endPoint = new Vector3(halfW, 0f, startZ),
                normal = Vector3.forward,
                length = width,
                height = height,
                transform = roomRoot.transform
            };
        }
        if (!hasFrontConnection)
        {
            room.walls[WallNames.Front] = new WallInfo
            {
                name = WallNames.Front,
                startPoint = new Vector3(-halfW, 0f, startZ + depth),
                endPoint = new Vector3(halfW, 0f, startZ + depth),
                normal = Vector3.back,
                length = width,
                height = height,
                transform = roomRoot.transform
            };
        }
        room.walls[WallNames.Left] = new WallInfo
        {
            name = WallNames.Left,
            startPoint = new Vector3(-halfW, 0f, startZ),
            endPoint = new Vector3(-halfW, 0f, startZ + depth),
            normal = Vector3.right,
            length = depth,
            height = height,
            transform = roomRoot.transform
        };
        room.walls[WallNames.Right] = new WallInfo
        {
            name = WallNames.Right,
            startPoint = new Vector3(halfW, 0f, startZ),
            endPoint = new Vector3(halfW, 0f, startZ + depth),
            normal = Vector3.left,
            length = depth,
            height = height,
            transform = roomRoot.transform
        };

        generatedRooms[roomId] = room;
        CreateArchitecturalTrim(roomRoot, new List<WallInfo>(room.walls.Values));

        if (debugMode) Debug.Log($"[BranchingRoomsGenerator] Created room '{roomId}' at z={startZ} depth={depth}");
    }

    private void CreateWallWithDoorwaySegments(Transform parent, string name, float wallWidth, float height, float zPos, string wallName, bool centeredDoorway)
    {
        if (!centeredDoorway)
        {
            CreateWallSegment(parent, name, new Vector3(0f, height / 2f, zPos), new Vector3(wallWidth, height, wallThickness));
            return;
        }

        float left = -wallWidth / 2f;
        float right = wallWidth / 2f;
        float safeDoorWidth = Mathf.Min(doorwayWidth, wallWidth - 0.4f);
        float safeDoorHeight = Mathf.Min(doorwayHeight, height - 0.2f);
        float openingStart = left + (wallWidth - safeDoorWidth) / 2f;
        float openingEnd = openingStart + safeDoorWidth;

        openingStart = Mathf.Clamp(openingStart, left, right);
        openingEnd = Mathf.Clamp(openingEnd, left, right);

        // Left segment
        if (openingStart > left)
        {
            float segWidth = openingStart - left;
            CreateWallSegment(parent, name + "_Left", new Vector3(left + segWidth / 2f, height / 2f, zPos), new Vector3(segWidth, height, wallThickness));
        }
        // Right segment
        if (openingEnd < right)
        {
            float segWidth = right - openingEnd;
            CreateWallSegment(parent, name + "_Right", new Vector3(openingEnd + segWidth / 2f, height / 2f, zPos), new Vector3(segWidth, height, wallThickness));
        }
        // Top segment (lintel)
        float lintelHeight = Mathf.Max(0.1f, height - safeDoorHeight);
        if (lintelHeight > 0.01f)
        {
            CreateWallSegment(parent, name + "_Top", new Vector3(0f, safeDoorHeight + lintelHeight / 2f, zPos), new Vector3(safeDoorWidth, lintelHeight, wallThickness));
        }
    }

    private void CreateSideWallSolid(Transform parent, string name, float wallLength, float height, float xPos, float zCenter)
    {
        CreateWallSegment(parent, name, new Vector3(xPos, height / 2f, zCenter), new Vector3(wallThickness, height, wallLength));
    }

    private void CreateWallSegment(Transform parent, string name, Vector3 position, Vector3 scale)
    {
        GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wall.name = name;
        wall.transform.SetParent(parent);
        wall.transform.position = position;
        wall.transform.localScale = scale;
        wall.GetComponent<Renderer>().sharedMaterial = wallMaterial;
    }

    private void GenerateLighting()
    {
        foreach (var room in generatedRooms.Values)
        {
            GameObject lightsParent = new GameObject($"Lights_{room.id}");
            lightsParent.transform.SetParent(generatedRoot.transform);
            lightsParent.transform.position = room.center;

            Color lightColor = GetCeilingLightColor();
            float intensity = GetPointLightIntensity((themePalette?.lightIntensity ?? 1f) * lightIntensity);
            
            float width = room.dimensions.width;
            float depth = room.dimensions.length > 0 ? room.dimensions.length : room.dimensions.depth;
            float range = GetPointLightRange(Mathf.Max(width, depth) * 1.5f);
            float inset = 1.2f;
            float usableW = Mathf.Max(0.1f, width - inset * 2f);
            float usableD = Mathf.Max(0.1f, depth - inset * 2f);
            
            GetCeilingGridLightCounts(lightsAcross, lightsAlong, out int across, out int along);
            float stepX = across > 1 ? usableW / (across - 1) : 0f;
            float stepZ = along > 1 ? usableD / (along - 1) : 0f;
            
            float startX = -width / 2f + inset;
            float startZ = -depth / 2f + inset;
            float y = room.dimensions.height - 0.3f;
            
            int index = 0;
            for (int zi = 0; zi < along; zi++)
            {
                for (int xi = 0; xi < across; xi++)
                {
                    index++;
                    float x = across > 1 ? startX + stepX * xi : 0f;
                    float z = along > 1 ? startZ + stepZ * zi : 0f;
                    
                    GameObject lightObj = new GameObject($"CeilingLight_{index}");
                    lightObj.transform.SetParent(lightsParent.transform);
                    lightObj.transform.localPosition = new Vector3(x, y, z);
                    
                    Light light = lightObj.AddComponent<Light>();
                    light.type = LightType.Point;
                    light.color = lightColor;
                    light.intensity = intensity;
                    light.range = range;
                    light.shadows = LightShadows.None;
                }
            }
        }
    }

    public override bool TryGetFocalWallPlacement(float artworkWidth, out FocalWallPlacement focalPlacement)
    {
        focalPlacement = null;

        GeneratedRoom lastRoom = GetRoomWithGreatestForwardExtent();
        if (lastRoom == null)
        {
            return false;
        }

        if (TryCreateFocalWallPlacement(lastRoom.id, WallNames.Front, artworkWidth, FocalWallBias.Center, out focalPlacement))
        {
            return true;
        }

        if (TryCreateFocalWallPlacement(lastRoom.id, WallNames.Back, artworkWidth, FocalWallBias.Center, out focalPlacement))
        {
            return true;
        }

        if (TryGetWidestWallInRoom(lastRoom.id, out WallInfo widestWall))
        {
            return TryCreateFocalWallPlacement(lastRoom.id, widestWall.name, artworkWidth, FocalWallBias.Center, out focalPlacement);
        }

        return false;
    }

    private void EnsureDirectionalLight()
    {
        Light directional = null;
        Light[] sceneLights = FindObjectsByType<Light>(FindObjectsSortMode.None);
        for (int i = 0; i < sceneLights.Length; i++)
        {
            if (sceneLights[i] != null && sceneLights[i].type == LightType.Directional)
            {
                directional = sceneLights[i];
                break;
            }
        }

        if (directional == null)
        {
            GameObject lightObj = new GameObject("DirectionalFillLight");
            lightObj.transform.SetParent(generatedRoot.transform);
            lightObj.transform.position = new Vector3(0f, 5f, 0f);
            lightObj.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            directional = lightObj.AddComponent<Light>();
            directional.type = LightType.Directional;
        }

        directional.color = GetStyleLightColor();
        directional.intensity = GetDirectionalFillIntensityForStyle();
        directional.shadows = LightShadows.None;
    }
}
