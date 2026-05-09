using UnityEngine;

/// <summary>
/// CameraOrbitMover の動きと BenchmarkSession の計測を結びつけるだけの管理クラス。
/// Mover と Session はお互いを知らないので、結線はここに集約する。
/// </summary>
public class BenchmarkController : MonoBehaviour
{
    [SerializeField] private CameraOrbitMover _routeMover;
    [SerializeField] private BenchmarkSession _session;

    private void Awake()
    {
        // Awake は全 Start より先に走るので、ここで購読すれば Started イベントを取り逃さない
        _routeMover.Started += _session.Begin;
        _routeMover.Finished += _session.End;
    }

    private void OnDestroy()
    {
        _routeMover.Started -= _session.Begin;
        _routeMover.Finished -= _session.End;
    }
}
