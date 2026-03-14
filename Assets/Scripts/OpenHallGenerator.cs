using UnityEngine;
using System.Collections.Generic;

// Large open hall with freestanding partition walls for extra hanging surfaces.
//
// Schema expects:
//   - 1 room with id "hall" (or first room in constraints.rooms[])
//   - Dimensions from layout_plan["hall"] with width, depth/length, height
//   - Partitions from layout_plan["hall"].partitions[]
//   - Each partition has: id, x, z, rotation, length, height
//
// Placement queries:
//   - Standard walls: surface "wall_left", "wall_right", etc.
//   - Partitions: surface "partition_N" with side "front"|"back"
//   - Floor sculptures: floor_position with x, z
//
// Partition surfaces are registered as "{partition_id}_front" and "{partition_id}_back"

public class OpenHallGenerator : TopologyGenerator
{
    [Header("Partition Settings")]
    public float partitionThickness = 0.1f;
    public float partitionFloorGap = 0.02f;

    [Header("Entry Settings")]
    public float entryHeight = 2.4f;

    [Header("Lighting")]
    public bool generateLights = true;
    public int lightsAcross = 3;
    public int lightsAlong = 2;
    public float lightIntensity = 1.1f;

    public override void Generate(LockedConstraints constraints, LayoutPlanWrapper layoutPlan)
    {
        if (debugMode) Debug.Log("[OpenHallGenerator] Starting generation...");

        ClearGenerated();
        SetGenerationContext(constraints);
        string styleId = manifestContext != null ? manifestContext.GetGalleryStyle() : constraints?.gallery_style;
        ConfigureStyle(styleId);
        generatedRoot = new GameObject("GeneratedGallery");
        generatedRoot.transform.SetParent(transform);
        generatedRoot.transform.position = Vector3.zero;
        generatedRoot.transform.rotation = Quaternion.identity;

        if (constraints == null || constraints.rooms == null || constraints.rooms.Count == 0)
        {
            Debug.LogError("[OpenHallGenerator] No rooms defined in constraints!");
            return;
        }

        RoomConstraint hallConstraint = constraints.rooms[0];
        RoomDimensions hallDims = layoutPlan.GetRoom(hallConstraint.id);
        if (hallDims == null)
        {
            Debug.LogError("[OpenHallGenerator] Missing layout for hall");
            return;
        }

        GenerateHall(hallConstraint.id, hallDims);

        if (generateLights)
        {
            GenerateLighting(hallDims);
        }

        ApplyStyleEnhancements(includeSpotlights: false);

        if (debugMode) Debug.Log("[OpenHallGenerator] Generation complete!");
    }

    private void GenerateHall(string roomId, RoomDimensions dims)
    {
        float width = dims.width;
        float depth = dims.depth > 0 ? dims.depth : dims.length;
        float height = dims.height;
        float halfW = width / 2f;
        float halfD = depth / 2f;
        float innerHalfW = halfW - wallThickness / 2f;
        float innerHalfD = halfD - wallThickness / 2f;
        float innerWidth = width - wallThickness;
        float innerDepth = depth - wallThickness;

        GameObject hallRoot = new GameObject($"Room_{roomId}");
        hallRoot.transform.SetParent(generatedRoot.transform);
        hallRoot.transform.localPosition = Vector3.zero;

        // Floor
        GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        floor.name = "Floor";
        floor.transform.SetParent(hallRoot.transform);
        floor.transform.localPosition = new Vector3(0f, -0.1f, 0f);
        floor.transform.localScale = new Vector3(width, 0.2f, depth);
        floor.GetComponent<Renderer>().sharedMaterial = floorMaterial;

        // Ceiling
        GameObject ceiling = GameObject.CreatePrimitive(PrimitiveType.Cube);
        ceiling.name = "Ceiling";
        ceiling.transform.SetParent(hallRoot.transform);
        ceiling.transform.localPosition = new Vector3(0f, height + 0.1f, 0f);
        ceiling.transform.localScale = new Vector3(width, 0.2f, depth);
        ceiling.GetComponent<Renderer>().sharedMaterial = ceilingMaterial;

        // Outer walls (no entry doorway for open hall)
        CreateOuterWall(hallRoot.transform, "Wall_Back", width, height, -halfD, WallNames.Back, false, 0f, 0f);
        CreateOuterWall(hallRoot.transform, "Wall_Front", width, height, halfD, WallNames.Front, false, 0f, 0f);
        CreateOuterSideWall(hallRoot.transform, "Wall_Left", depth, height, -halfW, WallNames.Left, false, 0f, 0f);
        CreateOuterSideWall(hallRoot.transform, "Wall_Right", depth, height, halfW, WallNames.Right, false, 0f, 0f);

        // Partitions
        if (dims.partitions != null)
        {
            foreach (var p in dims.partitions)
            {
                if (p == null || string.IsNullOrEmpty(p.id)) continue;
                CreatePartition(hallRoot.transform, p, width, depth);
            }
        }

        // Register placement surfaces
        GeneratedRoom room = new GeneratedRoom
        {
            id = roomId,
            dimensions = dims,
            center = new Vector3(0f, height / 2f, 0f),
            floorY = 0f,
            transform = hallRoot.transform,
            walls = new Dictionary<string, WallInfo>()
        };

        // Outer walls for placement
        room.walls[WallNames.Left] = new WallInfo
        {
            name = WallNames.Left,
            startPoint = new Vector3(-innerHalfW, 0f, -innerHalfD),
            endPoint = new Vector3(-innerHalfW, 0f, innerHalfD),
            normal = Vector3.right,
            length = innerDepth,
            height = height,
            transform = hallRoot.transform
        };
        room.walls[WallNames.Right] = new WallInfo
        {
            name = WallNames.Right,
            startPoint = new Vector3(innerHalfW, 0f, -innerHalfD),
            endPoint = new Vector3(innerHalfW, 0f, innerHalfD),
            normal = Vector3.left,
            length = innerDepth,
            height = height,
            transform = hallRoot.transform
        };
        room.walls[WallNames.Back] = new WallInfo
        {
            name = WallNames.Back,
            startPoint = new Vector3(-innerHalfW, 0f, -innerHalfD),
            endPoint = new Vector3(innerHalfW, 0f, -innerHalfD),
            normal = Vector3.forward,
            length = innerWidth,
            height = height,
            transform = hallRoot.transform
        };
        room.walls[WallNames.Front] = new WallInfo
        {
            name = WallNames.Front,
            startPoint = new Vector3(-innerHalfW, 0f, innerHalfD),
            endPoint = new Vector3(innerHalfW, 0f, innerHalfD),
            normal = Vector3.back,
            length = innerWidth,
            height = height,
            transform = hallRoot.transform
        };

        // Partition surfaces for placement
        if (dims.partitions != null)
        {
            foreach (var p in dims.partitions)
            {
                if (p == null || string.IsNullOrEmpty(p.id)) continue;
                RegisterPartitionSurfaces(room, p, width, depth);
            }
        }

        generatedRooms[roomId] = room;
        CreateArchitecturalTrim(hallRoot, new List<WallInfo>(room.walls.Values));

        // Add ceiling beam grid (large open space benefits from overhead structure)
        GenerateCeilingBeamGrid(hallRoot.transform, room, 0.07f);

        // Add end caps to partition walls so they don't look like floating planes
        if (dims.partitions != null)
        {
            foreach (var p in dims.partitions)
            {
                if (p == null || string.IsNullOrEmpty(p.id)) continue;
                string frontKey = $"{p.id}_front";
                if (room.walls.TryGetValue(frontKey, out WallInfo frontWall))
                {
                    GeneratePartitionEndCaps(hallRoot.transform, frontWall.startPoint, frontWall.endPoint, frontWall.normal, 0f, p.height, partitionThickness);
                }
            }
        }
    }

    private void CreateOuterWall(Transform parent, string name, float wallWidth, float height, float zPos, string wallName, bool hasEntry, float entryPos, float entryWidth)
    {
        if (!hasEntry || entryWidth <= 0f)
        {
            CreateWallSegment(parent, name, new Vector3(0f, height / 2f, zPos), new Vector3(wallWidth, height, wallThickness));
            return;
        }

        float left = -wallWidth / 2f;
        float safeWidth = Mathf.Min(entryWidth, wallWidth - 0.4f);
        float safeHeight = Mathf.Min(entryHeight, height - 0.2f);
        float pos = entryPos > 0 ? entryPos : wallWidth / 2f;
        float openingCenter = left + pos;
        float openingStart = openingCenter - safeWidth / 2f;
        float openingEnd = openingCenter + safeWidth / 2f;

        // Left segment
        float segLeft = openingStart - left;
        if (segLeft > 0.01f)
            CreateWallSegment(parent, name + "_Left", new Vector3(left + segLeft / 2f, height / 2f, zPos), new Vector3(segLeft, height, wallThickness));
        // Right segment
        float right = wallWidth / 2f;
        float segRight = right - openingEnd;
        if (segRight > 0.01f)
            CreateWallSegment(parent, name + "_Right", new Vector3(openingEnd + segRight / 2f, height / 2f, zPos), new Vector3(segRight, height, wallThickness));
        // Top segment
        float lintel = Mathf.Max(0.1f, height - safeHeight);
        CreateWallSegment(parent, name + "_Top", new Vector3(openingCenter, safeHeight + lintel / 2f, zPos), new Vector3(safeWidth, lintel, wallThickness));
    }

    private void CreateOuterSideWall(Transform parent, string name, float wallLength, float height, float xPos, string wallName, bool hasEntry, float entryPos, float entryWidth)
    {
        if (!hasEntry || entryWidth <= 0f)
        {
            CreateWallSegment(parent, name, new Vector3(xPos, height / 2f, 0f), new Vector3(wallThickness, height, wallLength));
            return;
        }

        float back = -wallLength / 2f;
        float safeWidth = Mathf.Min(entryWidth, wallLength - 0.4f);
        float safeHeight = Mathf.Min(entryHeight, height - 0.2f);
        float pos = entryPos > 0 ? entryPos : wallLength / 2f;
        float openingCenter = back + pos;
        float openingStart = openingCenter - safeWidth / 2f;
        float openingEnd = openingCenter + safeWidth / 2f;

        float segBack = openingStart - back;
        if (segBack > 0.01f)
            CreateWallSegment(parent, name + "_Back", new Vector3(xPos, height / 2f, back + segBack / 2f), new Vector3(wallThickness, height, segBack));

        float front = wallLength / 2f;
        float segFront = front - openingEnd;
        if (segFront > 0.01f)
            CreateWallSegment(parent, name + "_Front", new Vector3(xPos, height / 2f, openingEnd + segFront / 2f), new Vector3(wallThickness, height, segFront));

        float lintel = Mathf.Max(0.1f, height - safeHeight);
        CreateWallSegment(parent, name + "_Top", new Vector3(xPos, safeHeight + lintel / 2f, openingCenter), new Vector3(wallThickness, lintel, safeWidth));
    }

    private void CreatePartition(Transform parent, PartitionDefinition p, float hallWidth, float hallDepth)
    {
        Vector3 center = HallToWorld(p.x, p.z, hallWidth, hallDepth);
        float height = p.height;
        float length = p.length;

        GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wall.name = p.id;
        wall.transform.SetParent(parent);
        wall.transform.position = new Vector3(center.x, height / 2f + partitionFloorGap, center.z);
        wall.transform.rotation = Quaternion.Euler(0f, p.rotation, 0f);

        if (Mathf.Abs(Mathf.RoundToInt(p.rotation) % 180) == 90)
        {
            wall.transform.localScale = new Vector3(length, height, partitionThickness);
        }
        else
        {
            wall.transform.localScale = new Vector3(partitionThickness, height, length);
        }

        wall.GetComponent<Renderer>().sharedMaterial = wallMaterial;
    }

    private void RegisterPartitionSurfaces(GeneratedRoom room, PartitionDefinition p, float hallWidth, float hallDepth)
    {
        Vector3 center = HallToWorld(p.x, p.z, hallWidth, hallDepth);
        float halfLen = p.length / 2f;
        Quaternion rot = Quaternion.Euler(0f, p.rotation, 0f);

        // Match the scale swap in CreatePartition: at 90/270 degrees the
        // local X and Z axes are exchanged, so length runs along X not Z.
        bool isSwapped = Mathf.Abs(Mathf.RoundToInt(p.rotation) % 180) == 90;
        Vector3 lengthDir = rot * (isSwapped ? Vector3.right : Vector3.forward);
        Vector3 normal = rot * (isSwapped ? Vector3.forward : Vector3.right);

        Vector3 start = center - lengthDir * halfLen;
        Vector3 end = center + lengthDir * halfLen;

        Vector3 frontPos = center + normal * (partitionThickness / 2f);
        Vector3 backPos = center - normal * (partitionThickness / 2f);

        room.walls[$"{p.id}_front"] = new WallInfo
        {
            name = $"{p.id}_front",
            startPoint = new Vector3(start.x + normal.x * (partitionThickness / 2f), 0f, start.z + normal.z * (partitionThickness / 2f)),
            endPoint = new Vector3(end.x + normal.x * (partitionThickness / 2f), 0f, end.z + normal.z * (partitionThickness / 2f)),
            normal = normal,
            length = p.length,
            height = p.height,
            transform = room.transform
        };
        room.walls[$"{p.id}_back"] = new WallInfo
        {
            name = $"{p.id}_back",
            startPoint = new Vector3(start.x - normal.x * (partitionThickness / 2f), 0f, start.z - normal.z * (partitionThickness / 2f)),
            endPoint = new Vector3(end.x - normal.x * (partitionThickness / 2f), 0f, end.z - normal.z * (partitionThickness / 2f)),
            normal = -normal,
            length = p.length,
            height = p.height,
            transform = room.transform
        };
    }

    private Vector3 HallToWorld(float x, float z, float hallWidth, float hallDepth)
    {
        float worldX = -hallWidth / 2f + x;
        float worldZ = -hallDepth / 2f + z;
        return new Vector3(worldX, 0f, worldZ);
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

    public override bool TryGetFocalWallPlacement(float artworkWidth, out FocalWallPlacement focalPlacement)
    {
        focalPlacement = null;

        string hallRoomId = null;
        if (generationConstraints?.rooms != null && generationConstraints.rooms.Count > 0)
        {
            hallRoomId = generationConstraints.rooms[0]?.id;
        }

        if (string.IsNullOrEmpty(hallRoomId))
        {
            foreach (GeneratedRoom room in generatedRooms.Values)
            {
                if (room != null)
                {
                    hallRoomId = room.id;
                    break;
                }
            }
        }

        if (string.IsNullOrEmpty(hallRoomId))
        {
            return false;
        }

        if (TryCreateFocalWallPlacement(hallRoomId, WallNames.Back, artworkWidth, FocalWallBias.Center, out focalPlacement))
        {
            return true;
        }

        if (TryCreateFocalWallPlacement(hallRoomId, WallNames.Front, artworkWidth, FocalWallBias.Center, out focalPlacement))
        {
            return true;
        }

        if (TryGetWidestWallInRoom(hallRoomId, out WallInfo widestWall))
        {
            return TryCreateFocalWallPlacement(hallRoomId, widestWall.name, artworkWidth, FocalWallBias.Center, out focalPlacement);
        }

        return false;
    }

    private void GenerateLighting(RoomDimensions dims)
    {
        GameObject lightsParent = new GameObject("Lights");
        lightsParent.transform.SetParent(generatedRoot.transform);
        lightsParent.transform.localPosition = Vector3.zero;

        Color lightColor = GetCeilingLightColor();
        float intensity = GetPointLightIntensity((themePalette?.lightIntensity ?? 1f) * lightIntensity);

        float width = dims.width;
        float depth = dims.depth > 0 ? dims.depth : dims.length;
        float range = GetPointLightRange(Mathf.Max(width, depth) * 1.5f);
        float inset = 1.5f;
        float usableW = Mathf.Max(0.1f, width - inset * 2f);
        float usableD = Mathf.Max(0.1f, depth - inset * 2f);

        GetCeilingGridLightCounts(lightsAcross, lightsAlong, out int across, out int along);
        float stepX = across > 1 ? usableW / (across - 1) : 0f;
        float stepZ = along > 1 ? usableD / (along - 1) : 0f;

        float startX = -width / 2f + inset;
        float startZ = -depth / 2f + inset;
        float y = dims.height - 0.3f;

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
