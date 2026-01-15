using UnityEngine;
using System.Collections.Generic;

// Central hub room with spoke rooms extending in cardinal directions.
//
// Schema expects:
//   - Hub: rooms.Find(r => r.type == "hub")
//   - Spokes: rooms.Where(r => r.type != "hub")
//   - Hub dimensions from layout_plan[hub.id]
//   - Spoke dimensions from layout_plan[spoke.id]
//   - direction_from_hub from layout_plan[spoke.id]: "north", "south", "east", "west"
//
// Hub is generated at origin, spokes extend outward in their direction.

public class HubAndSpokeGenerator : TopologyGenerator
{
    [Header("Doorway Settings")]
    public float doorwayHeight = 2.2f;
    public float doorwayWidth = 2.5f;
    public float hubGap = 0f; // keep floors aligned between hub and spokes

    [Header("Lighting")]
    public bool generateLights = true;
    public int lightsAcross = 2;
    public int lightsAlong = 2;
    public float lightIntensity = 1.2f;

    public override void Generate(LockedConstraints constraints, LayoutPlanWrapper layoutPlan)
    {
        if (debugMode) Debug.Log("[HubAndSpokeGenerator] Starting generation...");

        ClearGenerated();
        SetGenerationContext(constraints);
        string styleId = manifestContext != null ? manifestContext.GetGalleryStyle() : constraints?.gallery_style;
        ConfigureStyle(styleId);
        wallOffset = Mathf.Max(wallOffset, wallThickness * 0.75f + 0.05f);

        generatedRoot = new GameObject("GeneratedGallery");
        generatedRoot.transform.SetParent(transform);
        generatedRoot.transform.position = Vector3.zero;
        generatedRoot.transform.rotation = Quaternion.identity;

        if (constraints == null || constraints.rooms == null || constraints.rooms.Count == 0)
        {
            Debug.LogError("[HubAndSpokeGenerator] No rooms defined in constraints!");
            return;
        }

        // Find hub room
        RoomConstraint hubConstraint = null;
        foreach (var room in constraints.rooms)
        {
            if (room.type == RoomTypes.Hub)
            {
                hubConstraint = room;
                break;
            }
        }
        if (hubConstraint == null)
        {
            Debug.LogError("[HubAndSpokeGenerator] No hub room found (type 'hub')");
            return;
        }

        RoomDimensions hubDims = layoutPlan.GetRoom(hubConstraint.id);
        if (hubDims == null)
        {
            Debug.LogError("[HubAndSpokeGenerator] Missing layout for hub");
            return;
        }

        // Generate hub at origin
        GenerateRoom(hubConstraint.id, hubDims, Vector3.zero, isHub: true, spokeDoorWall: null);

        // Generate spokes
        foreach (var room in constraints.rooms)
        {
            if (room.id == hubConstraint.id) continue;
            RoomDimensions dims = layoutPlan.GetRoom(room.id);
            if (dims == null) continue;

            Vector3 pos = GetSpokePosition(dims.direction_from_hub, hubDims, dims);
            string doorWall = GetSpokeDoorWall(dims.direction_from_hub);
            GenerateRoom(room.id, dims, pos, isHub: false, spokeDoorWall: doorWall);
        }

        if (generateLights)
        {
            EnsureDirectionalLight();
            GenerateLighting();
        }

        ApplyStyleEnhancements(includeSpotlights: false);

        if (debugMode) Debug.Log("[HubAndSpokeGenerator] Generation complete!");
    }

    private Vector3 GetSpokePosition(string direction, RoomDimensions hub, RoomDimensions spoke)
    {
        float hubHalfW = hub.width / 2f;
        float hubHalfD = hub.depth > 0 ? hub.depth / 2f : hub.length / 2f;
        float spokeHalfW = spoke.width / 2f;
        float spokeHalfD = (spoke.depth > 0 ? spoke.depth : spoke.length) / 2f;

        switch (direction)
        {
            case "north":
                return new Vector3(0f, 0f, hubHalfD + hubGap + spokeHalfD);
            case "south":
                return new Vector3(0f, 0f, -(hubHalfD + hubGap + spokeHalfD));
            case "east":
                return new Vector3(hubHalfW + hubGap + spokeHalfW, 0f, 0f);
            case "west":
                return new Vector3(-(hubHalfW + hubGap + spokeHalfW), 0f, 0f);
            default:
                return new Vector3(0f, 0f, hubHalfD + hubGap + spokeHalfD);
        }
    }

    private void GenerateRoom(string roomId, RoomDimensions dims, Vector3 center, bool isHub, string spokeDoorWall)
    {
        float width = dims.width;
        float depth = dims.depth > 0 ? dims.depth : dims.length;
        float height = dims.height;

        float halfW = width / 2f;
        float halfD = depth / 2f;

        GameObject roomRoot = new GameObject($"Room_{roomId}");
        roomRoot.transform.SetParent(generatedRoot.transform);
        roomRoot.transform.position = center;

        // Floor
        GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        floor.name = "Floor";
        floor.transform.SetParent(roomRoot.transform);
        floor.transform.localPosition = new Vector3(0f, -0.1f, 0f);
        floor.transform.localScale = new Vector3(width, 0.2f, depth);
        floor.GetComponent<Renderer>().sharedMaterial = floorMaterial;

        // Ceiling
        GameObject ceiling = GameObject.CreatePrimitive(PrimitiveType.Cube);
        ceiling.name = "Ceiling";
        ceiling.transform.SetParent(roomRoot.transform);
        ceiling.transform.localPosition = new Vector3(0f, height + 0.1f, 0f);
        ceiling.transform.localScale = new Vector3(width, 0.2f, depth);
        ceiling.GetComponent<Renderer>().sharedMaterial = ceilingMaterial;

        bool doorBack = isHub ? HasDoorway(dims.doorways, WallNames.Back) : spokeDoorWall == WallNames.Back;
        bool doorFront = isHub ? HasDoorway(dims.doorways, WallNames.Front) : spokeDoorWall == WallNames.Front;
        bool doorLeft = isHub ? HasDoorway(dims.doorways, WallNames.Left) : spokeDoorWall == WallNames.Left;
        bool doorRight = isHub ? HasDoorway(dims.doorways, WallNames.Right) : spokeDoorWall == WallNames.Right;

        // Walls
        CreateWallWithCenteredDoorway(roomRoot.transform, "Wall_Back", width, height, -halfD, doorBack);
        CreateWallWithCenteredDoorway(roomRoot.transform, "Wall_Front", width, height, halfD, doorFront);
        CreateSideWallWithCenteredDoorway(roomRoot.transform, "Wall_Left", depth, height, -halfW, doorLeft);
        CreateSideWallWithCenteredDoorway(roomRoot.transform, "Wall_Right", depth, height, halfW, doorRight);

        // Register for placement (avoid doorway walls on hub to keep placement simple)
        GeneratedRoom room = new GeneratedRoom
        {
            id = roomId,
            dimensions = dims,
            center = new Vector3(center.x, height / 2f, center.z),
            floorY = 0f,
            transform = roomRoot.transform,
            walls = new Dictionary<string, WallInfo>()
        };

        if (!doorLeft)
        {
            room.walls[WallNames.Left] = new WallInfo
            {
                name = WallNames.Left,
                startPoint = new Vector3(center.x - halfW, 0f, center.z - halfD),
                endPoint = new Vector3(center.x - halfW, 0f, center.z + halfD),
                normal = Vector3.right,
                length = depth,
                height = height,
                transform = roomRoot.transform
            };
        }
        if (!doorRight)
        {
            room.walls[WallNames.Right] = new WallInfo
            {
                name = WallNames.Right,
                startPoint = new Vector3(center.x + halfW, 0f, center.z - halfD),
                endPoint = new Vector3(center.x + halfW, 0f, center.z + halfD),
                normal = Vector3.left,
                length = depth,
                height = height,
                transform = roomRoot.transform
            };
        }
        if (!doorBack)
        {
            room.walls[WallNames.Back] = new WallInfo
            {
                name = WallNames.Back,
                startPoint = new Vector3(center.x - halfW, 0f, center.z - halfD),
                endPoint = new Vector3(center.x + halfW, 0f, center.z - halfD),
                normal = Vector3.forward,
                length = width,
                height = height,
                transform = roomRoot.transform
            };
        }
        if (!doorFront)
        {
            room.walls[WallNames.Front] = new WallInfo
            {
                name = WallNames.Front,
                startPoint = new Vector3(center.x - halfW, 0f, center.z + halfD),
                endPoint = new Vector3(center.x + halfW, 0f, center.z + halfD),
                normal = Vector3.back,
                length = width,
                height = height,
                transform = roomRoot.transform
            };
        }

        generatedRooms[roomId] = room;
        CreateArchitecturalTrim(roomRoot, new List<WallInfo>(room.walls.Values));
    }

    private void CreateWallWithCenteredDoorway(Transform parent, string name, float wallWidth, float height, float zPos, bool hasDoor)
    {
        if (!hasDoor)
        {
            CreateWallSegment(parent, name, new Vector3(0f, height / 2f, zPos), new Vector3(wallWidth, height, wallThickness));
            return;
        }

        float left = -wallWidth / 2f;
        float safeDoorWidth = Mathf.Min(doorwayWidth, wallWidth - 0.4f);
        float safeDoorHeight = Mathf.Min(doorwayHeight, height - 0.2f);
        float openingStart = left + (wallWidth - safeDoorWidth) / 2f;
        float openingEnd = openingStart + safeDoorWidth;

        // Left segment
        float segLeftWidth = openingStart - left;
        CreateWallSegment(parent, name + "_Left", new Vector3(left + segLeftWidth / 2f, height / 2f, zPos), new Vector3(segLeftWidth, height, wallThickness));
        // Right segment
        float right = wallWidth / 2f;
        float segRightWidth = right - openingEnd;
        CreateWallSegment(parent, name + "_Right", new Vector3(openingEnd + segRightWidth / 2f, height / 2f, zPos), new Vector3(segRightWidth, height, wallThickness));
        // Top segment
        float lintelHeight = Mathf.Max(0.1f, height - safeDoorHeight);
        CreateWallSegment(parent, name + "_Top", new Vector3(0f, safeDoorHeight + lintelHeight / 2f, zPos), new Vector3(safeDoorWidth, lintelHeight, wallThickness));
    }

    private void CreateSideWallWithCenteredDoorway(Transform parent, string name, float wallLength, float height, float xPos, bool hasDoor)
    {
        if (!hasDoor)
        {
            CreateWallSegment(parent, name, new Vector3(xPos, height / 2f, 0f), new Vector3(wallThickness, height, wallLength));
            return;
        }

        float back = -wallLength / 2f;
        float safeDoorWidth = Mathf.Min(doorwayWidth, wallLength - 0.4f);
        float safeDoorHeight = Mathf.Min(doorwayHeight, height - 0.2f);
        float openingStart = back + (wallLength - safeDoorWidth) / 2f;
        float openingEnd = openingStart + safeDoorWidth;

        float segBackLen = openingStart - back;
        CreateWallSegment(parent, name + "_Back", new Vector3(xPos, height / 2f, back + segBackLen / 2f), new Vector3(wallThickness, height, segBackLen));

        float front = wallLength / 2f;
        float segFrontLen = front - openingEnd;
        CreateWallSegment(parent, name + "_Front", new Vector3(xPos, height / 2f, openingEnd + segFrontLen / 2f), new Vector3(wallThickness, height, segFrontLen));

        float lintelHeight = Mathf.Max(0.1f, height - safeDoorHeight);
        CreateWallSegment(parent, name + "_Top", new Vector3(xPos, safeDoorHeight + lintelHeight / 2f, 0f), new Vector3(wallThickness, lintelHeight, safeDoorWidth));
    }

    private bool HasDoorway(List<Doorway> doorways, string wallName)
    {
        if (doorways == null) return false;
        foreach (var d in doorways)
        {
            if (d != null && d.wall == wallName) return true;
        }
        return false;
    }

    private string GetSpokeDoorWall(string direction)
    {
        switch (direction)
        {
            case "north":
                return WallNames.Back;
            case "south":
                return WallNames.Front;
            case "east":
                return WallNames.Left;
            case "west":
                return WallNames.Right;
            default:
                return WallNames.Back;
        }
    }

    private void CreateWallSegment(Transform parent, string name, Vector3 localPos, Vector3 scale)
    {
        GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wall.name = name;
        wall.transform.SetParent(parent);
        wall.transform.localPosition = localPos;
        wall.transform.localScale = scale;
        wall.GetComponent<Renderer>().sharedMaterial = wallMaterial;
    }

    private void GenerateLighting()
    {
        string hubRoomId = FindRoomIdByType(RoomTypes.Hub);

        foreach (var room in generatedRooms.Values)
        {
            GameObject lightsParent = new GameObject($"Lights_{room.id}");
            lightsParent.transform.SetParent(generatedRoot.transform);
            lightsParent.transform.position = room.center;

            Color lightColor = GetCeilingLightColor();
            float roomIntensity = (themePalette?.lightIntensity ?? 1f) * lightIntensity;
            bool isHubRoom = !string.IsNullOrEmpty(hubRoomId) && room.id == hubRoomId;
            roomIntensity *= isHubRoom ? 1.15f : 0.9f;
            float intensity = GetPointLightIntensity(roomIntensity);
            
            float width = room.dimensions.width;
            float depth = room.dimensions.depth > 0 ? room.dimensions.depth : room.dimensions.length;
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

        string hubRoomId = FindRoomIdByType(RoomTypes.Hub);
        if (string.IsNullOrEmpty(hubRoomId))
        {
            foreach (GeneratedRoom room in generatedRooms.Values)
            {
                if (room != null && (room.center.x * room.center.x + room.center.z * room.center.z) < 0.01f)
                {
                    hubRoomId = room.id;
                    break;
                }
            }
        }

        if (string.IsNullOrEmpty(hubRoomId))
        {
            return false;
        }

        if (TryCreateFocalWallPlacement(hubRoomId, WallNames.Back, artworkWidth, FocalWallBias.Center, out focalPlacement))
        {
            return true;
        }
        if (TryCreateFocalWallPlacement(hubRoomId, WallNames.Front, artworkWidth, FocalWallBias.Center, out focalPlacement))
        {
            return true;
        }

        if (TryGetWidestWallInRoom(hubRoomId, out WallInfo widestWall))
        {
            return TryCreateFocalWallPlacement(hubRoomId, widestWall.name, artworkWidth, FocalWallBias.Center, out focalPlacement);
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
