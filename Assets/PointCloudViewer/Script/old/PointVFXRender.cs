using System.Collections.Generic;
using UnityEngine;
using UnityEngine.VFX;

public class PointVFXRender : MonoBehaviour
{
    private VisualEffect _vfx;
    private GraphicsBuffer _positionBuffer;
    private GraphicsBuffer _colorBuffer;

    private string _count = "pointCount";
    private string _position = "positionBuffer";
    private string _color = "colorBuffer";



    public void Render(List<PointData> points)
    {
        Vector3[] positions = new Vector3[points.Count];
        Color[] colors = new Color[points.Count];

        for (int i = 0; i < points.Count; i++)
        {
            positions[i] = points[i].Position;
            colors[i] = points[i].Color;
        }

        Release();

        _positionBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, points.Count, sizeof(float) * 3);
        _colorBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, points.Count, sizeof(float) * 4);

        _positionBuffer.SetData(positions);
        _colorBuffer.SetData(colors);

        _vfx = GetComponentInChildren<VisualEffect>();
        _vfx.SetInt(_count, points.Count);
        _vfx.SetGraphicsBuffer(_position, _positionBuffer);
        _vfx.SetGraphicsBuffer(_color, _colorBuffer);
    }

    private void OnDisable()
    {
        Release();
    }

    private void Release()
    {
        _positionBuffer?.Release();
        _colorBuffer?.Release();
    }
}
