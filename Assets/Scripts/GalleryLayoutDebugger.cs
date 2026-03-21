using UnityEngine;
using System.Collections.Generic;
using System.Linq;

public class GalleryLayoutDebugger : MonoBehaviour
{
    [Header("Data Source")]
    [Tooltip("Drag a .txt manifest file here")]
    public TextAsset manifestFile;

    [Tooltip("...OR paste your JSON text directly here")]
    [TextArea(10, 20)]
    public string jsonContent;

    [Header("Visualization Settings")]
    public bool showRoomBounds = true;
    public bool showWallTypes = true;
    public float wallHeight = 4.0f; 

    private Color colorHero = Color.red;       
    private Color colorCluster = Color.cyan;   
    private Color colorAmbient = Color.green;
    private Color colorPartition = new Color(1f, 0.5f, 0f); // Orange

    private void OnDrawGizmos()
    {
        string jsonToParse = "";

        if (!string.IsNullOrEmpty(jsonContent))
        {
            jsonToParse = jsonContent;
        }
        else if (manifestFile != null)
        {
            jsonToParse = manifestFile.text;
        }

        if (string.IsNullOrEmpty(jsonToParse)) return;

        GalleryManifest manifest = JsonUtility.FromJson<GalleryManifest>(jsonToParse);

        if (manifest == null || manifest.layout_plan == null || manifest.layout_plan.Count == 0) return;

        if (manifest.locked_constraints != null && manifest.locked_constraints.rooms != null)
        {
            int i = 0;
            foreach (var roomConstraint in manifest.locked_constraints.rooms)
            {
                RoomDimensions roomDims = manifest.GetRoomDimensions(roomConstraint.id);
                
                AlcoveDimensions alcoveDims = null;
                if (roomDims == null)
                {
                    alcoveDims = manifest.GetAlcoveDimensions(roomConstraint.id);
                }

                if (roomDims != null)
                {
                    VisualizeRoom(roomConstraint.id, roomDims, i);
                    i++;
                }
                else if (alcoveDims != null)
                {
                    VisualizeAlcove(roomConstraint.id, alcoveDims, i);
                    i++;
                }
            }
        }
    }

    private void VisualizeRoom(string roomId, RoomDimensions room, int index)
    {
        float roomLength = room.length > 0 ? room.length : room.depth;
        float xPos = index * (room.width + 5.0f);

        if (showRoomBounds)
        {
            Gizmos.color = Color.white;
            Vector3 center = new Vector3(xPos, wallHeight / 2, 0);
            Vector3 size = new Vector3(room.width, wallHeight, roomLength); 
            Gizmos.DrawWireCube(center, size);
        }

        if (showWallTypes)
        {
            string heroWallName = "front"; 
            DrawWall(xPos, room.width, roomLength, heroWallName, colorHero);

            var surfaces = new List<(string name, float area)>();
            surfaces.Add(("left", roomLength * wallHeight));
            surfaces.Add(("right", roomLength * wallHeight));
            if (heroWallName != "back") surfaces.Add(("back", room.width * wallHeight));

            var clusterWall = surfaces.OrderByDescending(s => s.area).FirstOrDefault();

            if (!string.IsNullOrEmpty(clusterWall.name))
            {
                DrawWall(xPos, room.width, roomLength, clusterWall.name, colorCluster);
            }

            foreach (var s in surfaces)
            {
                if (s.name != clusterWall.name)
                {
                    DrawWall(xPos, room.width, roomLength, s.name, colorAmbient);
                }
            }

            if (room.partitions != null)
            {
                foreach (var p in room.partitions)
                {
                    DrawPartition(xPos, room.width, roomLength, p);
                }
            }
        }
    }

    private void VisualizeAlcove(string roomId, AlcoveDimensions alcove, int index)
    {
         if (showRoomBounds)
         {
            Gizmos.color = Color.yellow;
            float xPos = index * (alcove.width + 5.0f);
            Vector3 center = new Vector3(xPos, wallHeight / 2, 5); 
            Vector3 size = new Vector3(alcove.width, wallHeight, alcove.depth);
            Gizmos.DrawWireCube(center, size);
         }
    }

    private void DrawWall(float xPos, float width, float length, string wallName, Color c)
    {
        Gizmos.color = new Color(c.r, c.g, c.b, 0.3f); 
        
        float cx = xPos;
        float cz = 0;
        float h = wallHeight;

        Vector3 pos = Vector3.zero;
        Vector3 size = Vector3.zero;

        switch (wallName)
        {
            case "left":
                pos = new Vector3(cx - width/2, h/2, cz);
                size = new Vector3(0.1f, h, length);
                break;
            case "right":
                pos = new Vector3(cx + width/2, h/2, cz);
                size = new Vector3(0.1f, h, length);
                break;
            case "front": 
                pos = new Vector3(cx, h/2, cz + length/2);
                size = new Vector3(width, h, 0.1f);
                break;
             case "back": 
                pos = new Vector3(cx, h/2, cz - length/2);
                size = new Vector3(width, h, 0.1f);
                break;
        }

        Gizmos.DrawCube(pos, size);
    }

    // Draws a freestanding partition wall inside an open hall.
    // Partition x/z are in room-local coords (origin = room corner),
    // so we offset them relative to the room's gizmo center.
    private void DrawPartition(float roomCenterX, float roomWidth, float roomLength, PartitionDefinition p)
    {
        Gizmos.color = new Color(colorPartition.r, colorPartition.g, colorPartition.b, 0.5f);

        // Convert from room-corner coords to room-center coords
        float localX = p.x - roomWidth / 2f;
        float localZ = p.z - roomLength / 2f;

        Vector3 pos = new Vector3(roomCenterX + localX, p.height / 2f, localZ);
        Quaternion rot = Quaternion.Euler(0, p.rotation, 0);

        Matrix4x4 prev = Gizmos.matrix;
        Gizmos.matrix = Matrix4x4.TRS(pos, rot, new Vector3(p.length, p.height, 0.2f));
        Gizmos.DrawCube(Vector3.zero, Vector3.one);
        Gizmos.matrix = prev;
    }
}