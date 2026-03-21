using UnityEngine;
using System.Collections.Generic;

// Creates a simple rectangular hallway gallery - the simplest topology.
// 
// Schema expects:
//   - 1+ rooms in locked_constraints.rooms (typically one with id "main")
//   - Dimensions from layout_plan[room.id] with length, width, height
//   - Placements: wall "left"|"right", position_along_wall in meters
//
// Corridor extends along +Z (player walks forward), left wall at -X, right at +X,
// floor at Y=0, entry at Z=0, exit at Z=length.
// Multiple rooms are connected sequentially along Z axis.

// WALL NAMING CONVENTION (backend-Unity contract):
// "back"  = Z=0, the ENTRY end where the player spawns
// "front" = Z=length, the FAR end (hero terminus wall)
// "left"  = X=-halfWidth, runs along Z axis
// "right" = X=+halfWidth, runs along Z axis
//
// Multi-room corridors: "back" is on the first room only, "front" on the last.
// The backend should send hero placements on wall:"front" for terminus placement.
public class LinearCorridorGenerator : TopologyGenerator
{
    [Header("Corridor Settings")]
    [Tooltip("Generate ceiling lights")]
    public bool generateLights = true;
    
    [Tooltip("Number of lights along the corridor")]
    public int lightCount = 3;
    
    [Tooltip("Light intensity")]
    public float lightIntensity = 1.5f;
    
    // Called by GalleryOrchestrator to build the corridor based on manifest data.
    // Schema: expects rooms[] with corridor entries, dimensions from layout_plan[id]
    public override void Generate(LockedConstraints constraints, LayoutPlanWrapper layoutPlan)
    {
        if (debugMode) Debug.Log("[LinearCorridorGenerator] Starting generation...");
        
        // Clear any existing geometry
        ClearGenerated();

        SetGenerationContext(constraints);
        string styleId = manifestContext != null ? manifestContext.GetGalleryStyle() : constraints?.gallery_style;
        ConfigureStyle(styleId);
        
        // Create root object at WORLD ORIGIN (not relative to parent)
        // This ensures wall positions match the placement calculations
        generatedRoot = new GameObject("GeneratedGallery");
        generatedRoot.transform.SetParent(transform);
        generatedRoot.transform.position = Vector3.zero;
        generatedRoot.transform.rotation = Quaternion.identity;
        
        // Validate we have rooms to generate
        if (constraints.rooms == null || constraints.rooms.Count == 0)
        {
            Debug.LogError("[LinearCorridorGenerator] No rooms defined in constraints!");
            return;
        }
        
        // Track cumulative Z offset for sequential room placement
        float currentZOffset = 0f;
        int roomIndex = 0;
        int totalRooms = constraints.rooms.Count;
        float previousRoomWidth = 0f;
        float previousRoomHeight = 0f;
        
        // Generate all rooms sequentially along the Z axis
        foreach (RoomConstraint roomConstraint in constraints.rooms)
        {
            RoomDimensions dimensions = layoutPlan.GetRoom(roomConstraint.id);
            
            if (dimensions == null)
            {
                Debug.LogError($"[LinearCorridorGenerator] No dimensions found for room '{roomConstraint.id}'");
                continue;
            }
            
            if (debugMode)
            {
                Debug.Log($"[LinearCorridorGenerator] Generating corridor '{roomConstraint.id}'");
                Debug.Log($"[LinearCorridorGenerator] Dimensions: {dimensions.width}m wide x {dimensions.length}m long x {dimensions.height}m high");
            }
            
            // Determine which walls to generate based on room position in sequence
            bool isFirstRoom = (roomIndex == 0);
            bool isLastRoom = (roomIndex == totalRooms - 1);
            
            // Generate transition geometry if rooms have different widths
            if (!isFirstRoom && Mathf.Abs(previousRoomWidth - dimensions.width) > 0.01f)
            {
                GenerateTransitionWalls(currentZOffset, previousRoomWidth, dimensions.width, 
                                        Mathf.Max(previousRoomHeight, dimensions.height));
            }
            
            // Generate the corridor geometry at the current Z offset
            GenerateCorridor(roomConstraint.id, dimensions, currentZOffset, isFirstRoom, isLastRoom);
            
            // Generate lighting for this room
            if (generateLights)
            {
                GenerateLighting(dimensions, currentZOffset);
            }
            
            // Track this room's dimensions for the next transition
            previousRoomWidth = dimensions.width;
            previousRoomHeight = dimensions.height;
            
            // Move offset forward for the next room
            currentZOffset += dimensions.length;
            roomIndex++;
        }

        ApplyStyleEnhancements(includeSpotlights: false);
        
        if (debugMode) Debug.Log("[LinearCorridorGenerator] Generation complete!");
    }
    
    /// <summary>
    /// Generate transition walls to fill gaps when adjacent rooms have different widths.
    /// Creates small wall segments on left and right to bridge the width difference.
    /// </summary>
    private void GenerateTransitionWalls(float zPosition, float prevWidth, float nextWidth, float height)
    {
        float prevHalfWidth = prevWidth / 2f;
        float nextHalfWidth = nextWidth / 2f;
        
        // Calculate the gap on each side
        float widthDiff = Mathf.Abs(nextWidth - prevWidth) / 2f;
        
        if (widthDiff < 0.01f) return; // No significant difference
        
        GameObject transitionObj = new GameObject($"Transition_Z{zPosition}");
        transitionObj.transform.SetParent(generatedRoot.transform);
        transitionObj.transform.localPosition = Vector3.zero;
        
        // Determine which room is wider
        bool nextIsWider = nextWidth > prevWidth;
        float narrowHalfWidth = Mathf.Min(prevHalfWidth, nextHalfWidth);
        float wideHalfWidth = Mathf.Max(prevHalfWidth, nextHalfWidth);
        
        // Left transition wall (fills gap between narrow and wide room on left side)
        GameObject leftTransition = GameObject.CreatePrimitive(PrimitiveType.Cube);
        leftTransition.name = "TransitionWall_Left";
        leftTransition.transform.SetParent(transitionObj.transform);
        leftTransition.transform.localPosition = new Vector3(
            -(narrowHalfWidth + widthDiff / 2f),  // Center of the gap
            height / 2f,
            zPosition
        );
        leftTransition.transform.localScale = new Vector3(widthDiff, height, wallThickness);
        leftTransition.GetComponent<Renderer>().sharedMaterial = wallMaterial;
        
        // Right transition wall (fills gap between narrow and wide room on right side)
        GameObject rightTransition = GameObject.CreatePrimitive(PrimitiveType.Cube);
        rightTransition.name = "TransitionWall_Right";
        rightTransition.transform.SetParent(transitionObj.transform);
        rightTransition.transform.localPosition = new Vector3(
            narrowHalfWidth + widthDiff / 2f,  // Center of the gap
            height / 2f,
            zPosition
        );
        rightTransition.transform.localScale = new Vector3(widthDiff, height, wallThickness);
        rightTransition.GetComponent<Renderer>().sharedMaterial = wallMaterial;
        
        if (debugMode)
        {
            Debug.Log($"[LinearCorridorGenerator] Created transition walls at Z={zPosition}, bridging {prevWidth}m to {nextWidth}m (gap: {widthDiff}m per side)");
        }
    }
    
    /// <summary>
    /// Generate corridor geometry for a single room.
    /// </summary>
    /// <param name="roomId">Unique identifier for the room</param>
    /// <param name="dims">Room dimensions</param>
    /// <param name="zOffset">Z offset to position this room after previous rooms</param>
    /// <param name="isFirstRoom">If true, generate back wall (entry)</param>
    /// <param name="isLastRoom">If true, generate front wall (exit)</param>
    private void GenerateCorridor(string roomId, RoomDimensions dims, float zOffset = 0f, bool isFirstRoom = true, bool isLastRoom = true)
    {
        // Create room container
        GameObject roomObj = new GameObject($"Room_{roomId}");
        roomObj.transform.SetParent(generatedRoot.transform);
        roomObj.transform.localPosition = new Vector3(0, 0, zOffset);
        
        float width = dims.width;
        float length = dims.length;
        float height = dims.height;
        float halfWidth = width / 2f;
        float halfLength = length / 2f;
        
        // Create GeneratedRoom tracking object
        // Note: wall positions are stored relative to world origin (including zOffset)
        GeneratedRoom room = new GeneratedRoom
        {
            id = roomId,
            dimensions = dims,
            center = new Vector3(0, height / 2f, zOffset + halfLength),
            floorY = 0f,
            transform = roomObj.transform,
            walls = new Dictionary<string, WallInfo>()
        };
        
        // Floor uses a cube primitive for reliable physics collisions
        GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        floor.name = "Floor";
        floor.transform.SetParent(roomObj.transform);
        floor.transform.localPosition = new Vector3(0, -0.1f, halfLength);
        floor.transform.localScale = new Vector3(width, 0.2f, length);
        floor.GetComponent<Renderer>().sharedMaterial = floorMaterial;
        
        if (debugMode) Debug.Log($"[LinearCorridorGenerator] Floor WORLD pos: ({0}, {-0.1f}, {zOffset + halfLength}), scale: {floor.transform.localScale}");
        
        // Ceiling
        GameObject ceiling = GameObject.CreatePrimitive(PrimitiveType.Cube);
        ceiling.name = "Ceiling";
        ceiling.transform.SetParent(roomObj.transform);
        ceiling.transform.localPosition = new Vector3(0, height + 0.1f, halfLength);
        ceiling.transform.localScale = new Vector3(width, 0.2f, length);
        ceiling.GetComponent<Renderer>().sharedMaterial = ceilingMaterial;
        
        // Left wall faces into corridor (+X normal)
        GameObject leftWall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        leftWall.name = "Wall_Left";
        leftWall.transform.SetParent(roomObj.transform);
        leftWall.transform.localPosition = new Vector3(-halfWidth - wallThickness/2f, height/2f, halfLength);
        leftWall.transform.localScale = new Vector3(wallThickness, height, length);
        leftWall.GetComponent<Renderer>().sharedMaterial = wallMaterial;
        
        if (debugMode) Debug.Log($"[LinearCorridorGenerator] Left wall WORLD pos: {leftWall.transform.position}, inner surface at x={-halfWidth}");
        
        room.walls[WallNames.Left] = new WallInfo
        {
            name = WallNames.Left,
            startPoint = new Vector3(-halfWidth, 0, zOffset),  // Wall surface position (world space)
            endPoint = new Vector3(-halfWidth, 0, zOffset + length),
            normal = Vector3.right, // Faces into corridor (+X)
            length = length,
            height = height,
            transform = leftWall.transform
        };
        
        // Right wall faces into corridor (-X normal)
        GameObject rightWall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        rightWall.name = "Wall_Right";
        rightWall.transform.SetParent(roomObj.transform);
        rightWall.transform.localPosition = new Vector3(halfWidth + wallThickness/2f, height/2f, halfLength);
        rightWall.transform.localScale = new Vector3(wallThickness, height, length);
        rightWall.GetComponent<Renderer>().sharedMaterial = wallMaterial;
        
        if (debugMode) Debug.Log($"[LinearCorridorGenerator] Right wall WORLD pos: {rightWall.transform.position}, inner surface at x={halfWidth}");

        
        room.walls[WallNames.Right] = new WallInfo
        {
            name = WallNames.Right,
            startPoint = new Vector3(halfWidth, 0, zOffset),  // Wall surface position (world space)
            endPoint = new Vector3(halfWidth, 0, zOffset + length),
            normal = Vector3.left, // Faces into corridor (-X)
            length = length,
            height = height,
            transform = rightWall.transform
        };
        
        // Back wall (entry end) - only for first room
        if (isFirstRoom)
        {
            GameObject backWall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            backWall.name = "Wall_Back";
            backWall.transform.SetParent(roomObj.transform);
            backWall.transform.localPosition = new Vector3(0, height/2f, -wallThickness/2f);
            backWall.transform.localScale = new Vector3(width + wallThickness*2, height, wallThickness);
            backWall.GetComponent<Renderer>().sharedMaterial = wallMaterial;
            
            room.walls[WallNames.Back] = new WallInfo
            {
                name = WallNames.Back,
                startPoint = new Vector3(-halfWidth, 0, zOffset),  // Wall surface position (world space)
                endPoint = new Vector3(halfWidth, 0, zOffset),
                normal = Vector3.forward, // Faces into corridor (+Z)
                length = width,
                height = height,
                transform = backWall.transform
            };
        }
        
        // Front wall (exit end) - only for last room
        if (isLastRoom)
        {
            GameObject frontWall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            frontWall.name = "Wall_Front";
            frontWall.transform.SetParent(roomObj.transform);
            frontWall.transform.localPosition = new Vector3(0, height/2f, length + wallThickness/2f);
            frontWall.transform.localScale = new Vector3(width + wallThickness*2, height, wallThickness);
            frontWall.GetComponent<Renderer>().sharedMaterial = wallMaterial;
            
            room.walls[WallNames.Front] = new WallInfo
            {
                name = WallNames.Front,
                startPoint = new Vector3(-halfWidth, 0, zOffset + length),  // Wall surface position (world space)
                endPoint = new Vector3(halfWidth, 0, zOffset + length),
                normal = Vector3.back, // Faces into corridor (-Z)
                length = width,
                height = height,
                transform = frontWall.transform
            };
        }
        
        // Register this room
        generatedRooms[roomId] = room;
        CreateArchitecturalTrim(roomObj, new List<WallInfo>(room.walls.Values));
        
        if (debugMode)
        {
            Debug.Log($"[LinearCorridorGenerator] Created room '{roomId}' with {room.walls.Count} walls");
            Debug.Log($"[LinearCorridorGenerator] Room center: {room.center}, FloorY: {room.floorY}");
        }
    }
    
    // Creates ceiling lights evenly spaced along the corridor, plus a directional fill.
    // Tracks light indices globally to avoid duplicate names across rooms.
    private int globalLightIndex = 0;
    
    private void GenerateLighting(RoomDimensions dims, float zOffset = 0f)
    {
        // Use a single lights parent for all rooms, create if doesn't exist
        Transform lightsParent = generatedRoot.transform.Find("Lights");
        if (lightsParent == null)
        {
            GameObject lightsObj = new GameObject("Lights");
            lightsObj.transform.SetParent(generatedRoot.transform);
            lightsObj.transform.localPosition = Vector3.zero;
            lightsParent = lightsObj.transform;
            globalLightIndex = 0; // Reset counter for new generation
        }
        
        float lightY = dims.height - 0.3f;
        int ceilingLightCount = GetCeilingLinearLightCount(lightCount);
        float spacing = dims.length / (ceilingLightCount + 1);
        
        Color lightColor = GetCeilingLightColor();
        float intensity = GetPointLightIntensity((themePalette?.lightIntensity ?? 1f) * lightIntensity);
        float lightRange = GetPointLightRange(Mathf.Max(dims.width, dims.height) * 1.5f);
        
        for (int i = 0; i < ceilingLightCount; i++)
        {
            float z = zOffset + spacing * (i + 1);
            
            GameObject lightObj = new GameObject($"CeilingLight_{globalLightIndex + 1}");
            lightObj.transform.SetParent(lightsParent);
            lightObj.transform.localPosition = new Vector3(0, lightY, z);
            
            Light light = lightObj.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = lightColor;
            light.intensity = intensity;
            light.range = lightRange;
            light.shadows = LightShadows.None; // Better VR performance
            
            globalLightIndex++;
        }
        
        // Only add one directional fill light for the first room
        if (zOffset == 0f)
        {
            // Add a subtle directional light for ambient fill
            GameObject dirLightObj = new GameObject("DirectionalFill");
            dirLightObj.transform.SetParent(lightsParent);
            dirLightObj.transform.localPosition = new Vector3(0, dims.height, dims.length / 2f);
            dirLightObj.transform.localRotation = Quaternion.Euler(90, 0, 0);
            
            Light dirLight = dirLightObj.AddComponent<Light>();
            dirLight.type = LightType.Directional;
            dirLight.color = GetStyleLightColor();
            dirLight.intensity = GetDirectionalFillIntensityForStyle();
            dirLight.shadows = LightShadows.None;
        }
        
        if (debugMode) Debug.Log($"[LinearCorridorGenerator] Created {ceilingLightCount} ceiling lights");
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

        if (TryGetWallInfo(lastRoom.id, WallNames.Right, out WallInfo rightWall))
        {
            float halfWidth = Mathf.Clamp(artworkWidth * 0.5f, 0.1f, rightWall.length * 0.45f);
            float min = Mathf.Max(0.25f + halfWidth, rightWall.length - 2f);
            float max = rightWall.length - 0.25f - halfWidth;
            if (max >= min)
            {
                float farEndPosition = Mathf.Clamp(rightWall.length - 0.9f, min, max);
                if (TryCreateFocalWallPlacement(lastRoom.id, WallNames.Right, artworkWidth, farEndPosition, out focalPlacement))
                {
                    return true;
                }
            }
        }

        if (TryGetWallInfo(lastRoom.id, WallNames.Left, out WallInfo leftWall))
        {
            float halfWidth = Mathf.Clamp(artworkWidth * 0.5f, 0.1f, leftWall.length * 0.45f);
            float min = Mathf.Max(0.25f + halfWidth, leftWall.length - 2f);
            float max = leftWall.length - 0.25f - halfWidth;
            if (max >= min)
            {
                float farEndPosition = Mathf.Clamp(leftWall.length - 0.9f, min, max);
                return TryCreateFocalWallPlacement(lastRoom.id, WallNames.Left, artworkWidth, farEndPosition, out focalPlacement);
            }
        }

        return false;
    }
    
    // Editor-only gizmos showing wall positions and normals for debugging.
    void OnDrawGizmosSelected()
    {
        if (generatedRooms == null) return;
        
        foreach (var room in generatedRooms.Values)
        {
            foreach (var wall in room.walls.Values)
            {
                // Draw wall line
                Gizmos.color = Color.green;
                Gizmos.DrawLine(wall.startPoint, wall.endPoint);
                
                // Draw normal at midpoint
                Vector3 midpoint = (wall.startPoint + wall.endPoint) / 2f;
                midpoint.y = room.dimensions.height / 2f;
                
                Gizmos.color = Color.blue;
                Gizmos.DrawRay(midpoint, wall.normal * 0.5f);
                
                // Draw start/end points
                Gizmos.color = Color.red;
                Gizmos.DrawSphere(wall.startPoint, 0.1f);
                Gizmos.DrawSphere(wall.endPoint, 0.1f);
            }
        }
    }
}
