using System.Collections.Generic;
using UnityEngine;

public class ChunkController
{
    private PointData[] _pointDatas;

    public ChunkController(PointData[] pointDatas)
    {
        _pointDatas = pointDatas;
    }

    public List<ChunkPointData> Split(float chunkSize)
    {
        var map = new Dictionary<Vector3Int, List<int>>();

        for (int i = 0; i < _pointDatas.Length; i++)
        {
            var p = _pointDatas[i];
            var cell = new Vector3Int(
                Mathf.FloorToInt(p.Position.x / chunkSize),
                Mathf.FloorToInt(p.Position.y / chunkSize),
                Mathf.FloorToInt(p.Position.z / chunkSize)
            );

            if (!map.TryGetValue(cell, out var list))
            {
                list = new List<int>();
                map[cell] = list;
            }
            list.Add(i);
        }

        var result = new List<ChunkPointData>(map.Count);
        foreach (var kv in map)
        {
            var indices = kv.Value;
            var pts = new PointData[indices.Count];
            for (int i = 0; i < indices.Count; i++)
                pts[i] = _pointDatas[indices[i]];

            var cell = kv.Key;
            Vector3 centerWorld = ((Vector3)cell + Vector3.one * 0.5f) * chunkSize;
            var bounds = new Bounds(centerWorld, Vector3.one * chunkSize);

            result.Add(new ChunkPointData(cell, bounds, pts));
        }

        return result;
    }
}

public class ChunkPointData
{
    public Vector3Int GridCoord { get; }
    public Bounds ChunkBounds { get; }
    public PointData[] Points { get; }

    public ChunkPointData(Vector3Int coord, Bounds bounds, PointData[] points)
    {
        GridCoord = coord;
        ChunkBounds = bounds;
        Points = points;
    }
}
