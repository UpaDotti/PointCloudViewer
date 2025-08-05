using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class PointMeshRender : MonoBehaviour
{
    public void Render(List<PointData> points)
    {
        Mesh mesh = new Mesh
        {
            indexFormat = UnityEngine.Rendering.IndexFormat.UInt32
        };

        List<Vector3> vertices = new();
        List<Color> colors = new();

        foreach (var p in points)
        {
            vertices.Add(p.Position);
            colors.Add(p.Color);
        }

        mesh.SetVertices(vertices);
        mesh.SetColors(colors);

        int[] indices = new int[vertices.Count];
        for (int i = 0; i < vertices.Count; i++) indices[i] = i;

        mesh.SetIndices(indices, MeshTopology.Points, 0);

        GetComponent<MeshFilter>().mesh = mesh;
    }

    public void Render(ChunkPointData chunkPointData)
    {
        Mesh mesh = new Mesh
        {
            indexFormat = UnityEngine.Rendering.IndexFormat.UInt32
        };

        List<Vector3> vertices = new();
        List<Color> colors = new();

        foreach (var p in chunkPointData.Points)
        {
            vertices.Add(p.Position);
            colors.Add(p.Color);
        }

        mesh.SetVertices(vertices);
        mesh.SetColors(colors);

        int[] indices = new int[vertices.Count];
        for (int i = 0; i < vertices.Count; i++) indices[i] = i;

        mesh.SetIndices(indices, MeshTopology.Points, 0);

        GetComponent<MeshFilter>().mesh = mesh;
    }
}
