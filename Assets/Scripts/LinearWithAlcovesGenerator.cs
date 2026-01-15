using UnityEngine;
using System.Collections.Generic;

// Corridor with side alcoves branching off left and right.
//
// Schema expects:
//   - Main corridor: rooms.Find(r => r.type == "corridor")
//   - Alcoves: rooms.Where(r => r.type == "alcove")
//   - Corridor dimensions from layout_plan[corridor.id]
//   - For each alcove: layout_plan[alcove.id] with parent, side, position_along_parent
//
// Main corridor runs along +Z (entry at Z=0, exit at Z=length).
// Left/right walls are segmented to create alcove openings.

public class LinearWithAlcovesGenerator : TopologyGenerator
{
    [Header("Lighting")]
    public bool generateLights = true;
    public int lightCount = 4;
    public float lightIntensity = 1.4f;

    public override void Generate(LockedConstraints constraints, LayoutPlanWrapper layoutPlan)
    {
        if (debugMode) Debug.Log("[LinearWithAlcovesGenerator] Starting generation...");

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
            Debug.LogError("[LinearWithAlcovesGenerator] No rooms defined in constraints!");
            return;
        }

        // Find the main corridor room (type == "corridor")
        RoomConstraint corridorConstraint = null;
        List<RoomConstraint> alcoveConstraints = new List<RoomConstraint>();
        
        foreach (var room in constraints.rooms)
        {
            if (room.type == RoomTypes.Corridor)
            {
                corridorConstraint = room;
            }
            else if (room.type == RoomTypes.Alcove)
            {
                alcoveConstraints.Add(room);
            }
        }
        
        // Fallback: first room is corridor if no explicit type
        if (corridorConstraint == null)
        {
            corridorConstraint = constraints.rooms[0];
        }

        RoomDimensions corridorDims = layoutPlan.GetRoom(corridorConstraint.id);
        if (corridorDims == null)
        {
            Debug.LogError($"[LinearWithAlcovesGenerator] No dimensions found for corridor '{corridorConstraint.id}'");
            return;
        }

        // Collect alcoves - check both rooms[] entries and layout_plan entries
        List<(string id, AlcoveDimensions dims)> alcoves = new List<(string, AlcoveDimensions)>();
        
        // First, check alcove entries from rooms[]
        foreach (var alcoveRoom in alcoveConstraints)
        {
            AlcoveDimensions alcoveDims = layoutPlan.GetAlcove(alcoveRoom.id);
            if (alcoveDims != null)
            {
                alcoves.Add((alcoveRoom.id, alcoveDims));
                if (debugMode) Debug.Log($"[LinearWithAlcovesGenerator] Found alcove '{alcoveRoom.id}' from rooms[]");
            }
        }
        
        // Also check legacy alcoves dictionary
        if (layoutPlan.alcoves != null)
        {
            foreach (var kvp in layoutPlan.alcoves)
            {
                // Skip if already added from rooms[]
                if (alcoves.Exists(a => a.id == kvp.Key)) continue;
                
                AlcoveDimensions alcove = kvp.Value;
                if (alcove != null && alcove.parent == corridorConstraint.id)
                {
                    alcoves.Add((kvp.Key, alcove));
                    if (debugMode) Debug.Log($"[LinearWithAlcovesGenerator] Found alcove '{kvp.Key}' from legacy alcoves dict");
                }
            }
        }

        GenerateCorridorWithOpenings(corridorConstraint.id, corridorDims, alcoves);
        GenerateAlcoves(corridorConstraint.id, corridorDims, alcoves);

        if (generateLights)
        {
            GenerateLighting(corridorDims, alcoves);
        }

        ApplyStyleEnhancements(includeSpotlights: false);

        if (debugMode) Debug.Log("[LinearWithAlcovesGenerator] Generation complete!");
    }

    private void GenerateCorridorWithOpenings(string roomId, RoomDimensions dims, List<(string id, AlcoveDimensions dims)> alcoves)
    {
        GameObject roomObj = new GameObject($"Room_{roomId}");
        roomObj.transform.SetParent(generatedRoot.transform);
        roomObj.transform.localPosition = Vector3.zero;

        float width = dims.width;
        float length = dims.length;
        float height = dims.height;
        float halfWidth = width / 2f;
        float halfLength = length / 2f;

        // Track room for placement
        GeneratedRoom room = new GeneratedRoom
        {
            id = roomId,
            dimensions = dims,
            center = new Vector3(0, height / 2f, halfLength),
            floorY = 0f,
            transform = roomObj.transform,
            walls = new Dictionary<string, WallInfo>()
        };

        // Floor
        GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        floor.name = "Floor";
        floor.transform.SetParent(roomObj.transform);
        floor.transform.localPosition = new Vector3(0, -0.1f, halfLength);
        floor.transform.localScale = new Vector3(width, 0.2f, length);
        floor.GetComponent<Renderer>().sharedMaterial = floorMaterial;

        // Ceiling
        GameObject ceiling = GameObject.CreatePrimitive(PrimitiveType.Cube);
        ceiling.name = "Ceiling";
        ceiling.transform.SetParent(roomObj.transform);
        ceiling.transform.localPosition = new Vector3(0, height + 0.1f, halfLength);
        ceiling.transform.localScale = new Vector3(width, 0.2f, length);
        ceiling.GetComponent<Renderer>().sharedMaterial = ceilingMaterial;

        // Back wall (entry)
        GameObject backWall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        backWall.name = "Wall_Back";
        backWall.transform.SetParent(roomObj.transform);
        backWall.transform.localPosition = new Vector3(0, height / 2f, -wallThickness / 2f);
        backWall.transform.localScale = new Vector3(width + wallThickness * 2, height, wallThickness);
        backWall.GetComponent<Renderer>().sharedMaterial = wallMaterial;

        // Front wall (far end)
        GameObject frontWall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        frontWall.name = "Wall_Front";
        frontWall.transform.SetParent(roomObj.transform);
        frontWall.transform.localPosition = new Vector3(0, height / 2f, length + wallThickness / 2f);
        frontWall.transform.localScale = new Vector3(width + wallThickness * 2, height, wallThickness);
        frontWall.GetComponent<Renderer>().sharedMaterial = wallMaterial;

        // Collect alcove openings for left and right walls
        List<(float start, float end)> leftOpenings = new List<(float, float)>();
        List<(float start, float end)> rightOpenings = new List<(float, float)>();
        
        foreach (var alcoveEntry in alcoves)
        {
            AlcoveDimensions alcove = alcoveEntry.dims;
            if (alcove == null) continue;
            
            float startZ = alcove.position_along_parent - alcove.width / 2f;
            float endZ = alcove.position_along_parent + alcove.width / 2f;
            startZ = Mathf.Clamp(startZ, 0f, length);
            endZ = Mathf.Clamp(endZ, 0f, length);
            
            if (alcove.side == WallNames.Left)
            {
                leftOpenings.Add((startZ, endZ));
                if (debugMode) Debug.Log($"[LinearWithAlcovesGenerator] Left wall opening at Z={startZ:F1} to Z={endZ:F1}");
            }
            else if (alcove.side == WallNames.Right)
            {
                rightOpenings.Add((startZ, endZ));
                if (debugMode) Debug.Log($"[LinearWithAlcovesGenerator] Right wall opening at Z={startZ:F1} to Z={endZ:F1}");
            }
        }
        
        // Sort openings by position
        leftOpenings.Sort((a, b) => a.start.CompareTo(b.start));
        rightOpenings.Sort((a, b) => a.start.CompareTo(b.start));
        
        // Register openings with WallSpaceManager for robust collision detection
        wallSpaceManager.RegisterOpenings(roomId, WallNames.Left, leftOpenings);
        wallSpaceManager.RegisterOpenings(roomId, WallNames.Right, rightOpenings);

        // Corridor wall segments (left/right) with gaps for alcoves
        CreateCorridorWallSegments(roomObj.transform, halfWidth, height, length, alcoves, isLeft: true);
        CreateCorridorWallSegments(roomObj.transform, halfWidth, height, length, alcoves, isLeft: false);

        // Register corridor walls for placement
        room.walls[WallNames.Left] = new WallInfo
        {
            name = WallNames.Left,
            startPoint = new Vector3(-halfWidth, 0, 0),
            endPoint = new Vector3(-halfWidth, 0, length),
            normal = Vector3.right,
            length = length,
            height = height,
            transform = roomObj.transform
        };
        room.walls[WallNames.Right] = new WallInfo
        {
            name = WallNames.Right,
            startPoint = new Vector3(halfWidth, 0, 0),
            endPoint = new Vector3(halfWidth, 0, length),
            normal = Vector3.left,
            length = length,
            height = height,
            transform = roomObj.transform
        };
        room.walls[WallNames.Back] = new WallInfo
        {
            name = WallNames.Back,
            startPoint = new Vector3(-halfWidth, 0, 0),
            endPoint = new Vector3(halfWidth, 0, 0),
            normal = Vector3.forward,
            length = width,
            height = height,
            transform = backWall.transform
        };
        room.walls[WallNames.Front] = new WallInfo
        {
            name = WallNames.Front,
            startPoint = new Vector3(-halfWidth, 0, length),
            endPoint = new Vector3(halfWidth, 0, length),
            normal = Vector3.back,
            length = width,
            height = height,
            transform = frontWall.transform
        };

        generatedRooms[roomId] = room;
        CreateArchitecturalTrim(roomObj, new List<WallInfo>(room.walls.Values));
    }

    private void CreateCorridorWallSegments(Transform parent, float halfWidth, float height, float length, List<(string id, AlcoveDimensions dims)> alcoves, bool isLeft)
    {
        List<(float start, float end)> openings = new List<(float, float)>();
        foreach (var alcoveEntry in alcoves)
        {
            AlcoveDimensions alcove = alcoveEntry.dims;
            if (alcove == null) continue;

            bool matchesSide = isLeft ? alcove.side == WallNames.Left : alcove.side == WallNames.Right;
            if (!matchesSide) continue;

            float startZ = alcove.position_along_parent - alcove.width / 2f;
            float endZ = alcove.position_along_parent + alcove.width / 2f;

            // Clamp to corridor bounds
            startZ = Mathf.Clamp(startZ, 0f, length);
            endZ = Mathf.Clamp(endZ, 0f, length);

            if (endZ > startZ)
            {
                openings.Add((startZ, endZ));
            }
        }

        openings.Sort((a, b) => a.start.CompareTo(b.start));

        float x = isLeft ? -halfWidth - wallThickness / 2f : halfWidth + wallThickness / 2f;
        float currentZ = 0f;

        for (int i = 0; i < openings.Count; i++)
        {
            var opening = openings[i];
            if (opening.start > currentZ)
            {
                CreateWallSegment(parent, x, height, currentZ, opening.start);
            }
            currentZ = Mathf.Max(currentZ, opening.end);
        }

        if (currentZ < length)
        {
            CreateWallSegment(parent, x, height, currentZ, length);
        }
    }

    private void CreateWallSegment(Transform parent, float x, float height, float startZ, float endZ)
    {
        float segmentLength = endZ - startZ;
        if (segmentLength <= 0.01f) return;

        GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wall.name = "Wall_Segment";
        wall.transform.SetParent(parent);
        wall.transform.localPosition = new Vector3(x, height / 2f, startZ + segmentLength / 2f);
        wall.transform.localScale = new Vector3(wallThickness, height, segmentLength);
        wall.GetComponent<Renderer>().sharedMaterial = wallMaterial;
    }

    private void GenerateAlcoves(string corridorId, RoomDimensions corridorDims, List<(string id, AlcoveDimensions dims)> alcoves)
    {
        float halfWidth = corridorDims.width / 2f;

        foreach (var entry in alcoves)
        {
            string alcoveId = entry.id;
            AlcoveDimensions alcove = entry.dims;
            if (alcove == null) continue;

            // Build a generated room entry for placement
            RoomDimensions dims = new RoomDimensions
            {
                width = alcove.width,
                length = alcove.depth,
                height = alcove.height,
                depth = alcove.depth
            };

            float height = alcove.height;
            float centerZ = alcove.position_along_parent;

            bool isLeft = alcove.side == WallNames.Left;
            float centerX = isLeft
                ? -halfWidth - alcove.depth / 2f
                : halfWidth + alcove.depth / 2f;

            GameObject alcoveRoot = new GameObject($"Alcove_{alcoveId}");
            alcoveRoot.transform.SetParent(generatedRoot.transform);
            alcoveRoot.transform.position = Vector3.zero;

            // Floor
            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "Floor";
            floor.transform.SetParent(alcoveRoot.transform);
            floor.transform.localPosition = new Vector3(centerX, -0.1f, centerZ);
            floor.transform.localScale = new Vector3(alcove.depth, 0.2f, alcove.width);
            floor.GetComponent<Renderer>().sharedMaterial = floorMaterial;

            // Ceiling
            GameObject ceiling = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ceiling.name = "Ceiling";
            ceiling.transform.SetParent(alcoveRoot.transform);
            ceiling.transform.localPosition = new Vector3(centerX, height + 0.1f, centerZ);
            ceiling.transform.localScale = new Vector3(alcove.depth, 0.2f, alcove.width);
            ceiling.GetComponent<Renderer>().sharedMaterial = ceilingMaterial;

            // Back wall (faces corridor)
            float backX = isLeft
                ? -halfWidth - alcove.depth - wallThickness / 2f
                : halfWidth + alcove.depth + wallThickness / 2f;

            GameObject backWall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            backWall.name = "Wall_Back";
            backWall.transform.SetParent(alcoveRoot.transform);
            backWall.transform.localPosition = new Vector3(backX, height / 2f, centerZ);
            backWall.transform.localScale = new Vector3(wallThickness, height, alcove.width);
            backWall.GetComponent<Renderer>().sharedMaterial = wallMaterial;

            // Side walls
            float sideZ1 = centerZ - alcove.width / 2f - wallThickness / 2f;
            float sideZ2 = centerZ + alcove.width / 2f + wallThickness / 2f;

            GameObject sideWall1 = GameObject.CreatePrimitive(PrimitiveType.Cube);
            sideWall1.name = "Wall_Side_A";
            sideWall1.transform.SetParent(alcoveRoot.transform);
            sideWall1.transform.localPosition = new Vector3(centerX, height / 2f, sideZ1);
            sideWall1.transform.localScale = new Vector3(alcove.depth, height, wallThickness);
            sideWall1.GetComponent<Renderer>().sharedMaterial = wallMaterial;

            GameObject sideWall2 = GameObject.CreatePrimitive(PrimitiveType.Cube);
            sideWall2.name = "Wall_Side_B";
            sideWall2.transform.SetParent(alcoveRoot.transform);
            sideWall2.transform.localPosition = new Vector3(centerX, height / 2f, sideZ2);
            sideWall2.transform.localScale = new Vector3(alcove.depth, height, wallThickness);
            sideWall2.GetComponent<Renderer>().sharedMaterial = wallMaterial;

            // Register alcove room for placement
            GeneratedRoom room = new GeneratedRoom
            {
                id = alcoveId,
                dimensions = dims,
                center = new Vector3(centerX, height / 2f, centerZ),
                floorY = 0f,
                transform = alcoveRoot.transform,
                walls = new Dictionary<string, WallInfo>()
            };

            float backWallSurfaceX = isLeft
                ? -halfWidth - alcove.depth
                : halfWidth + alcove.depth;

            // Back wall (good for a featured piece)
            room.walls[WallNames.Back] = new WallInfo
            {
                name = WallNames.Back,
                startPoint = new Vector3(backWallSurfaceX, 0, centerZ - alcove.width / 2f),
                endPoint = new Vector3(backWallSurfaceX, 0, centerZ + alcove.width / 2f),
                normal = isLeft ? Vector3.right : Vector3.left,
                length = alcove.width,
                height = height,
                transform = backWall.transform
            };

            // Side walls (optional placement)
            room.walls[WallNames.Left] = new WallInfo
            {
                name = WallNames.Left,
                startPoint = new Vector3(centerX - alcove.depth / 2f, 0, centerZ - alcove.width / 2f),
                endPoint = new Vector3(centerX + alcove.depth / 2f, 0, centerZ - alcove.width / 2f),
                normal = Vector3.forward,
                length = alcove.depth,
                height = height,
                transform = sideWall1.transform
            };
            room.walls[WallNames.Right] = new WallInfo
            {
                name = WallNames.Right,
                startPoint = new Vector3(centerX - alcove.depth / 2f, 0, centerZ + alcove.width / 2f),
                endPoint = new Vector3(centerX + alcove.depth / 2f, 0, centerZ + alcove.width / 2f),
                normal = Vector3.back,
                length = alcove.depth,
                height = height,
                transform = sideWall2.transform
            };

            generatedRooms[alcoveId] = room;
            CreateArchitecturalTrim(alcoveRoot, new List<WallInfo>(room.walls.Values));
        }
    }

    private void GenerateLighting(RoomDimensions dims, List<(string id, AlcoveDimensions dims)> alcoves)
    {
        GameObject lightsParent = new GameObject("Lights");
        lightsParent.transform.SetParent(generatedRoot.transform);
        lightsParent.transform.localPosition = Vector3.zero;

        float lightY = dims.height - 0.3f;
        int ceilingLightCount = GetCeilingLinearLightCount(lightCount);
        float spacing = dims.length / (ceilingLightCount + 1);

        Color lightColor = GetCeilingLightColor();
        float intensity = GetPointLightIntensity((themePalette?.lightIntensity ?? 1f) * lightIntensity);
        float range = GetPointLightRange(Mathf.Max(dims.width, dims.height) * 1.5f);

        for (int i = 0; i < ceilingLightCount; i++)
        {
            float z = spacing * (i + 1);
            GameObject lightObj = new GameObject($"CeilingLight_{i + 1}");
            lightObj.transform.SetParent(lightsParent.transform);
            lightObj.transform.localPosition = new Vector3(0, lightY, z);

            Light light = lightObj.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = lightColor;
            light.intensity = intensity;
            light.range = range;
            light.shadows = LightShadows.None;
        }

        GenerateAlcoveLighting(lightsParent.transform, alcoves);
    }

    private void GenerateAlcoveLighting(Transform lightsParent, List<(string id, AlcoveDimensions dims)> alcoves)
    {
        if (alcoves == null || alcoves.Count == 0)
        {
            return;
        }

        Color lightColor = GetCeilingLightColor();
        float baseIntensity = GetPointLightIntensity((themePalette?.lightIntensity ?? 1f) * lightIntensity * 0.9f);
        int lightIndex = 0;

        foreach (var entry in alcoves)
        {
            if (entry.dims == null || string.IsNullOrEmpty(entry.id))
            {
                continue;
            }

            if (!generatedRooms.TryGetValue(entry.id, out GeneratedRoom alcoveRoom) || alcoveRoom == null)
            {
                continue;
            }

            float alcoveDepth = Mathf.Max(1f, entry.dims.depth);
            float alcoveWidth = Mathf.Max(1f, entry.dims.width);
            float y = alcoveRoom.floorY + Mathf.Max(2f, entry.dims.height - 0.3f);

            GameObject lightObj = new GameObject($"AlcoveLight_{++lightIndex}");
            lightObj.transform.SetParent(lightsParent);
            lightObj.transform.position = new Vector3(alcoveRoom.center.x, y, alcoveRoom.center.z);

            Light light = lightObj.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = lightColor;
            light.intensity = baseIntensity;
            light.range = GetPointLightRange(Mathf.Max(alcoveDepth, alcoveWidth) * 1.8f);
            light.shadows = LightShadows.None;
        }
    }

    public override bool TryGetFocalWallPlacement(float artworkWidth, out FocalWallPlacement focalPlacement)
    {
        focalPlacement = null;

        string corridorId = FindRoomIdByType(RoomTypes.Corridor);
        if (string.IsNullOrEmpty(corridorId) && generationConstraints?.rooms != null && generationConstraints.rooms.Count > 0)
        {
            corridorId = generationConstraints.rooms[0].id;
        }

        if (!string.IsNullOrEmpty(corridorId))
        {
            if (TryCreateFocalWallPlacement(corridorId, WallNames.Front, artworkWidth, FocalWallBias.Center, out focalPlacement))
            {
                return true;
            }
            if (TryCreateFocalWallPlacement(corridorId, WallNames.Back, artworkWidth, FocalWallBias.Center, out focalPlacement))
            {
                return true;
            }
        }

        if (!string.IsNullOrEmpty(corridorId) && TryGetWidestWallInRoom(corridorId, out WallInfo corridorWall))
        {
            return TryCreateFocalWallPlacement(corridorId, corridorWall.name, artworkWidth, FocalWallBias.Center, out focalPlacement);
        }

        GeneratedRoom fallbackRoom = GetRoomWithGreatestForwardExtent();
        if (fallbackRoom != null)
        {
            if (TryCreateFocalWallPlacement(fallbackRoom.id, WallNames.Front, artworkWidth, FocalWallBias.Center, out focalPlacement))
            {
                return true;
            }
            if (TryCreateFocalWallPlacement(fallbackRoom.id, WallNames.Back, artworkWidth, FocalWallBias.Center, out focalPlacement))
            {
                return true;
            }
            if (TryGetWidestWallInRoom(fallbackRoom.id, out WallInfo widestWall))
            {
                return TryCreateFocalWallPlacement(fallbackRoom.id, widestWall.name, artworkWidth, FocalWallBias.Center, out focalPlacement);
            }
        }

        return false;
    }
}
