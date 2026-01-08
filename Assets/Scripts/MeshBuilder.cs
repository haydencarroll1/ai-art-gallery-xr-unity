using UnityEngine;
using System.Collections.Generic;

// Static utility for procedural mesh creation across topology generators.
// All meshes are created in local space and face +Z by default.

public static class MeshBuilder
{
    public static Mesh CreateQuad(float width, float height)
    {
        return CreateRect(width, height, 0f, 0f, width, height);
    }

    public static Mesh CreateFloor(float width, float depth, bool faceUp = true)
    {
        Mesh mesh = new Mesh();
        mesh.name = faceUp ? "Mesh_Floor" : "Mesh_Ceiling";

        float halfW = width / 2f;
        float halfD = depth / 2f;

        Vector3[] vertices = new Vector3[]
        {
            new Vector3(-halfW, 0f, -halfD),
            new Vector3(halfW, 0f, -halfD),
            new Vector3(halfW, 0f, halfD),
            new Vector3(-halfW, 0f, halfD)
        };

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

    /// <summary>
    /// Creates a wall mesh with a rectangular opening (doorway) cut out.
    /// Opening starts at openingStart (from left edge), has openingWidth,
    /// and openingHeight from the floor.
    /// </summary>
    public static Mesh CreateWallWithOpening(float width, float height, float openingStart, float openingWidth, float openingHeight)
    {
        Mesh mesh = new Mesh();
        mesh.name = "Mesh_WallWithOpening";

        float leftX = -width / 2f;
        float rightX = width / 2f;

        float openingX0 = leftX + openingStart;
        float openingX1 = openingX0 + openingWidth;

        openingX0 = Mathf.Clamp(openingX0, leftX, rightX);
        openingX1 = Mathf.Clamp(openingX1, leftX, rightX);

        openingHeight = Mathf.Clamp(openingHeight, 0f, height);

        List<Vector3> vertices = new List<Vector3>();
        List<int> triangles = new List<int>();
        List<Vector2> uvs = new List<Vector2>();
        List<Vector3> normals = new List<Vector3>();

        // Left segment
        if (openingX0 > leftX)
        {
            AppendRect(vertices, triangles, uvs, normals,
                leftX, openingX0, 0f, height);
        }

        // Right segment
        if (openingX1 < rightX)
        {
            AppendRect(vertices, triangles, uvs, normals,
                openingX1, rightX, 0f, height);
        }

        // Top segment (above the doorway)
        if (openingHeight < height && openingX1 > openingX0)
        {
            AppendRect(vertices, triangles, uvs, normals,
                openingX0, openingX1, openingHeight, height);
        }

        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.SetUVs(0, uvs);
        mesh.SetNormals(normals);
        return mesh;
    }

    private static Mesh CreateRect(float width, float height, float x0, float y0, float x1, float y1)
    {
        Mesh mesh = new Mesh();
        mesh.name = "Mesh_Rect";

        float halfW = width / 2f;
        float xLeft = -halfW + x0;
        float xRight = -halfW + x1;

        Vector3[] vertices = new Vector3[]
        {
            new Vector3(xLeft, y0, 0f),
            new Vector3(xRight, y0, 0f),
            new Vector3(xRight, y1, 0f),
            new Vector3(xLeft, y1, 0f)
        };

        int[] triangles = new int[] { 0, 2, 1, 0, 3, 2 };
        Vector2[] uvs = new Vector2[]
        {
            new Vector2(0, 0),
            new Vector2(1, 0),
            new Vector2(1, 1),
            new Vector2(0, 1)
        };
        Vector3[] normals = new Vector3[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward };

        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.uv = uvs;
        mesh.normals = normals;
        return mesh;
    }

    private static void AppendRect(List<Vector3> vertices, List<int> triangles, List<Vector2> uvs, List<Vector3> normals,
        float x0, float x1, float y0, float y1)
    {
        int start = vertices.Count;
        vertices.Add(new Vector3(x0, y0, 0f));
        vertices.Add(new Vector3(x1, y0, 0f));
        vertices.Add(new Vector3(x1, y1, 0f));
        vertices.Add(new Vector3(x0, y1, 0f));

        triangles.Add(start + 0);
        triangles.Add(start + 2);
        triangles.Add(start + 1);
        triangles.Add(start + 0);
        triangles.Add(start + 3);
        triangles.Add(start + 2);

        uvs.Add(new Vector2(0, 0));
        uvs.Add(new Vector2(1, 0));
        uvs.Add(new Vector2(1, 1));
        uvs.Add(new Vector2(0, 1));

        normals.Add(Vector3.forward);
        normals.Add(Vector3.forward);
        normals.Add(Vector3.forward);
        normals.Add(Vector3.forward);
    }
}
