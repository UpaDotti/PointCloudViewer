using System;
using UnityEngine;

/// <summary>
/// 中心と直径を指定し、対象 Transform を中心の周囲を回りながら中心を見つめるように動かす。
/// Scene ビューでは中心・軌道・高さをギズモで可視化する。
/// </summary>
public class CameraOrbitMover : MonoBehaviour
{
    [Header("Target")]
    [SerializeField] private Transform _target;

    [Header("Orbit")]
    [SerializeField] private Vector3 _center = Vector3.zero;
    [SerializeField] private float _diameter = 10f;
    [SerializeField] private float _height = 5f;          // 中心からの高さ
    [SerializeField] private float _startAngleDeg = 0f;   // 開始角度（XZ 平面）
    [SerializeField] private int _revolutions = 1;        // 周回数
    [SerializeField] private bool _clockwise = false;

    [Header("Run")]
    [SerializeField] private float _durationSec = 30f;
    [SerializeField] private bool _loop = false;

    [Header("Gizmos")]
    [SerializeField] private Color _gizmoColor = new Color(0.2f, 0.9f, 1f, 0.9f);
    [SerializeField] private int _gizmoSegments = 64;

    private float _startTime;
    private bool _running;

    public event Action Started;
    public event Action Finished;

    public bool IsRunning => _running;

    private void Start()
    {
        if (_target == null) _target = transform;

        _startTime = Time.time;
        _running = _diameter > 0f && _durationSec > 0f;
        if (_running) Started?.Invoke();
    }

    private void Update()
    {
        if (!_running) return;

        float elapsed = Time.time - _startTime;
        float t = elapsed / _durationSec;

        if (t >= 1f)
        {
            if (_loop)
            {
                _startTime = Time.time;
                t = 0f;
            }
            else
            {
                ApplyPose(1f);
                _running = false;
                Finished?.Invoke();
                return;
            }
        }

        ApplyPose(t);
    }

    private void ApplyPose(float t01)
    {
        float radius = _diameter * 0.5f;
        float dir = _clockwise ? -1f : 1f;
        float angleRad = (_startAngleDeg + 360f * _revolutions * t01 * dir) * Mathf.Deg2Rad;

        Vector3 offset = new Vector3(Mathf.Cos(angleRad), 0f, Mathf.Sin(angleRad)) * radius;
        Vector3 pos = _center + offset + Vector3.up * _height;

        _target.position = pos;
        _target.rotation = Quaternion.LookRotation(_center - pos, Vector3.up);
    }

    private void OnDrawGizmos()
    {
        if (_diameter <= 0f) return;

        Gizmos.color = _gizmoColor;

        // 中心点
        Gizmos.DrawSphere(_center, _diameter * 0.02f);

        // 軌道円（カメラの高さで XZ 平面に描く）
        DrawOrbitRing();

        // 高さインジケータ（中心 → 軌道平面）
        Vector3 ringCenter = _center + Vector3.up * _height;
        Gizmos.DrawLine(_center, ringCenter);

        // 開始位置をマーク
        float radius = _diameter * 0.5f;
        float startRad = _startAngleDeg * Mathf.Deg2Rad;
        Vector3 startPos = ringCenter + new Vector3(Mathf.Cos(startRad), 0f, Mathf.Sin(startRad)) * radius;
        Gizmos.DrawSphere(startPos, _diameter * 0.015f);
        Gizmos.DrawLine(ringCenter, startPos);
    }

    private void DrawOrbitRing()
    {
        float radius = _diameter * 0.5f;
        Vector3 ringCenter = _center + Vector3.up * _height;

        int segments = Mathf.Max(8, _gizmoSegments);
        Vector3 prev = ringCenter + new Vector3(radius, 0f, 0f);
        for (int i = 1; i <= segments; i++)
        {
            float a = (i / (float)segments) * Mathf.PI * 2f;
            Vector3 next = ringCenter + new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);
            Gizmos.DrawLine(prev, next);
            prev = next;
        }
    }
}
