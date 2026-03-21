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

// WALL NAMING CONVENTION (backend-Unity contract):
// Rooms are placed sequentially along +Z. Within each room:
// "back"  = lower-Z wall (entry side, omitted between connected rooms)
// "front" = higher-Z wall (exit side, omitted between connected rooms)
// "left"  = X=-halfWidth, runs along Z axis
// "right" = X=+halfWidth, runs along Z axis
//
// The backend should send hero placements on wall:"front" of the last room
// for terminus placement. Doorway walls are not registered for placement.
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

        // Pre-collect room dimensions so we can look ahead for transition walls
        List<(RoomConstraint constraint, RoomDimensions dims, float depth)> roomData
            = new List<(RoomConstraint, RoomDimensions, float)>();
        for (int i = 0; i < count; i++)
        {
            RoomConstraint rc = constraints.rooms[i];
            RoomDimensions dims = layoutPlan.GetRoom(rc.id);
            if (dims == null)
            {
                Debug.LogError($"[BranchingRoomsGenerator] Missing layout for room '{rc.id}'");
                continue;
            }
            float depth = dims.length > 0 ? dims.length : dims.depth;
            if (depth <= 0f)
            {
                Debug.LogWarning($"[BranchingRoomsGenerator] Room '{rc.id}' has no depth/length. Using 5m default.");
                depth = 5f;
            }
            roomData.Add((rc, dims, depth));
        }

        float currentZ = 0f;
        for (int i = 0; i < roomData.Count; i++)
        {
            var (roomConstraint, dims, depth) = roomData[i];
            float nextWidth = (i < roomData.Count - 1) ? roomData[i + 1].dims.width : dims.width;

            // Check doorways from layout_plan for connectivity
            bool hasBackConnection = i > 0 || HasDoorwayOnWall(dims.doorways, WallNames.Back);
            bool hasFrontConnection = i < roomData.Count - 1 || HasDoorwayOnWall(dims.doorways, WallNames.Front);

            // Transition walls to bridge width differences between adjacent rooms
            if (i > 0 && Mathf.Abs(roomData[i - 1].dims.width - dims.width) > 0.01f)
            {
                GenerateTransitionWalls(currentZ, roomData[i - 1].dims.width, dims.width,
                    Mathf.Max(roomData[i - 1].dims.height, dims.height));
            }

            GenerateRoom(roomConstraint.id, dims, currentZ, hasBackConnection, hasFrontConnection, nextWidth);

            // Add doorway frame at front connection between rooms
            if (hasFrontConnection && i < roomData.Count - 1)
            {
                float doorZ = currentZ + depth;
                float safeDoorWidth = Mathf.Min(doorwayWidth, dims.width - 0.4f);
                float safeDoorHeight = Mathf.Min(doorwayHeight, dims.height - 0.2f);
                GenerateDoorwayFrame(generatedRoot.transform, new Vector3(0f, 0f, doorZ), safeDoorWidth, safeDoorHeight, Vector3.back, 0f);
            }

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

    private void GenerateRoom(string roomId, RoomDimensions dims, float startZ, bool hasBackConnection, bool hasFrontConnection, float nextWidth)
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
        // Bug 4 fix: use wider of current/next room width so doorway wall spans both
        float frontWallWidth = hasFrontConnection ? Mathf.Max(width, nextWidth) : width;
        CreateWallWithDoorwaySegments(roomRoot.transform, "Wall_Front", frontWallWidth, height, startZ + depth, WallNames.Front, hasFrontConnection);
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

        // Offset trim walls to inner face (WallInfo startPoints are at wall centers; inner face = center + normal * t).
        float trimT = wallThickness / 2f;
        var trimWalls = new List<WallInfo>();
        foreach (var w in room.walls.Values)
            trimWalls.Add(new WallInfo { name = w.name, startPoint = w.startPoint + w.normal * trimT, endPoint = w.endPoint + w.normal * trimT, normal = w.normal, length = w.length, height = w.height, transform = w.transform });
        CreateArchitecturalTrim(roomRoot, trimWalls);

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

    /// <summary>
    /// Generate transition walls to fill gaps when adjacent rooms have different widths.
    /// Creates small wall segments on left and right to bridge the width difference.
    /// </summary>
    private void GenerateTransitionWalls(float zPosition, float prevWidth, float nextWidth, float height)
    {
        float prevHalfWidth = prevWidth / 2f;
        float nextHalfWidth = nextWidth / 2f;
        float widthDiff = Mathf.Abs(nextWidth - prevWidth) / 2f;

        if (widthDiff < 0.01f) return;

        GameObject transitionObj = new GameObject($"Transition_Z{zPosition}");
        transitionObj.transform.SetParent(generatedRoot.transform);
        transitionObj.transform.localPosition = Vector3.zero;

        float narrowHalfWidth = Mathf.Min(prevHalfWidth, nextHalfWidth);

        // Left transition wall
        GameObject leftTransition = GameObject.CreatePrimitive(PrimitiveType.Cube);
        leftTransition.name = "TransitionWall_Left";
        leftTransition.transform.SetParent(transitionObj.transform);
        leftTransition.transform.localPosition = new Vector3(
            -(narrowHalfWidth + widthDiff / 2f),
            height / 2f,
            zPosition
        );
        leftTransition.transform.localScale = new Vector3(widthDiff, height, wallThickness);
        leftTransition.GetComponent<Renderer>().sharedMaterial = wallMaterial;

        // Right transition wall
        GameObject rightTransition = GameObject.CreatePrimitive(PrimitiveType.Cube);
        rightTransition.name = "TransitionWall_Right";
        rightTransition.transform.SetParent(transitionObj.transform);
        rightTransition.transform.localPosition = new Vector3(
            narrowHalfWidth + widthDiff / 2f,
            height / 2f,
            zPosition
        );
        rightTransition.transform.localScale = new Vector3(widthDiff, height, wallThickness);
        rightTransition.GetComponent<Renderer>().sharedMaterial = wallMaterial;

        if (debugMode) Debug.Log($"[BranchingRoomsGenerator] Transition at z={zPosition}: {prevWidth}m -> {nextWidth}m");
    }

    private void GenerateLighting()
    {
        foreach (var room in generatedRooms.Values)
        {
            GameObject lightsParent = new GameObject($"Lights_{room.id}");
            lightsParent.transform.SetParent(generatedRoot.transform);
            lightsParent.transform.localPosition = Vector3.zero;

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

            float startX = room.center.x - width / 2f + inset;
            float startZ = room.center.z - depth / 2f + inset;
            float worldY = room.floorY + room.dimensions.height - 0.3f;
            
            int index = 0;
            for (int zi = 0; zi < along; zi++)
            {
                for (int xi = 0; xi < across; xi++)
                {
                    index++;
                    float x = across > 1 ? startX + stepX * xi : room.center.x;
                    float z = along > 1 ? startZ + stepZ * zi : room.center.z;

                    GameObject lightObj = new GameObject($"CeilingLight_{index}");
                    lightObj.transform.SetParent(lightsParent.transform);
                    lightObj.transform.position = new Vector3(x, worldY, z);
                    
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
}
