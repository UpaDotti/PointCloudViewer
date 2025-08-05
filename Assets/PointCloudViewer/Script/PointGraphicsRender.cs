using System.Collections.Generic;
using UnityEngine;

public class PointGraphicsRender : MonoBehaviour
{
    public Material material;

    private GraphicsBuffer _positionBuffer;
    private GraphicsBuffer _colorBuffer;
    private GraphicsBuffer _argsBuffer;

    private GraphicsBuffer.IndirectDrawArgs[] _args = new GraphicsBuffer.IndirectDrawArgs[1];
    private RenderParams _renderParams;

    private bool _initialized = false;



    private void Update()
    {
        if (!_initialized) return;
        if (_argsBuffer == null || _positionBuffer == null || _colorBuffer == null) return;

        var cam = Camera.main;
        if (cam == null) return;

        if (IsVisible(cam, _renderParams.worldBounds))
        {
            Graphics.RenderPrimitivesIndirect(_renderParams, MeshTopology.Points, _argsBuffer);
        }
    }

    public void Render(ChunkPointData chunk)
    {
        Release();

        int count = chunk.Points.Length;
        if (count == 0) return;

        Vector3[] posArray = new Vector3[count];
        Color32[] colArray = new Color32[count];
        for (int i = 0; i < count; i++)
        {
            posArray[i] = chunk.Points[i].Position;
            colArray[i] = chunk.Points[i].Color;
        }

        CreateBuffersAndArgs(count, posArray, colArray);

        _renderParams = new RenderParams(material)
        {
            worldBounds = chunk.ChunkBounds,
            matProps = new MaterialPropertyBlock()
        };
        _renderParams.matProps.SetBuffer("_Positions", _positionBuffer);
        _renderParams.matProps.SetBuffer("_Colors", _colorBuffer);

        _initialized = true;
    }

    private void CreateBuffersAndArgs(int count, Vector3[] posArray, Color32[] colArray)
    {
        // 点群情報
        _positionBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(float) * 3);
        _colorBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(uint));
        _positionBuffer.SetData(posArray);
        _colorBuffer.SetData(colArray);

        // 描画設定（どれだけ描くか）
        _argsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 1, GraphicsBuffer.IndirectDrawArgs.size);
        _args[0].vertexCountPerInstance = (uint)count;
        _args[0].instanceCount = 1;
        _args[0].startVertex = 0;
        _args[0].startInstance = 0;
        _argsBuffer.SetData(_args);
    }

    private void OnDisable()
    {
        Release();
    }

    private void OnDestroy()
    {
        Release();
    }

    private void Release()
    {
        _positionBuffer?.Release();
        _colorBuffer?.Release();
        _argsBuffer?.Release();
        _positionBuffer = null;
        _colorBuffer = null;
        _argsBuffer = null;
        _initialized = false;
    }

    private bool IsVisible(Camera cam, Bounds bounds)
    {
        if (cam == null) return false;
        Plane[] planes = GeometryUtility.CalculateFrustumPlanes(cam);
        return GeometryUtility.TestPlanesAABB(planes, bounds);
    }
}
