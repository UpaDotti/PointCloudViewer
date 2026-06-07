using UnityEngine;

/// <summary>
/// LOD選択と描画に必要なカメラ情報のフレームスナップショット。
/// Cameraへの依存をここで切り、選択ロジック（NodeLodSelector）を純粋なデータ処理に保つ。
/// </summary>
public struct CameraState
{
    public Vector3 position;
    public Vector3 forward;

    // カメラの視錐台平面（6枚。呼び出し側が使い回す配列を参照する）
    public Plane[] frustumPlanes;

    // ワールド半径→画面ピクセル半径の変換係数（投影Y倍率 × 画面高さ/2）
    public float worldToPixel;

    // 奥行きの下限（背後/直近のクランプ用）
    public float minDepth;

    // 画面全体のピクセル数（面積クランプ用）
    public float maxAreaPx;

    /// <summary>カメラから1フレーム分の情報を取り込む（reusablePlanesは毎フレーム使い回す6要素の配列）</summary>
    public static CameraState Capture(Camera camera, Plane[] reusablePlanes)
    {
        GeometryUtility.CalculateFrustumPlanes(camera, reusablePlanes);

        Transform cameraTransform = camera.transform;
        return new CameraState
        {
            position = cameraTransform.position,
            forward = cameraTransform.forward,
            frustumPlanes = reusablePlanes,
            worldToPixel = camera.projectionMatrix.m11 * Screen.height * 0.5f,
            minDepth = camera.nearClipPlane + 1e-3f,
            maxAreaPx = (float)Screen.width * Screen.height,
        };
    }
}
