using System.Collections.Generic;
using System.IO;
using UnityEngine;

public class LasManager : MonoBehaviour
{
    [SerializeField]
    private GameObject _pcp_Mesh;

    [SerializeField]
    private GameObject _pcp_VFX;

    [SerializeField]
    private GameObject _pcp_Graphics;



    private void Start()
    {
        float start = Time.realtimeSinceStartup;

        // 描画するLasファイルを取得
        string fullPath = Path.Combine(Application.dataPath, "PointCloudViewer/Las");
        string[] lasFiles = Directory.GetFiles(fullPath, "*.las");

        // 点群の平均位置を計算
        Vector3 totalCenter = Vector3.zero;
        foreach (var filePath in lasFiles)
        {
            var headerLoader = new LasHeaderLoader(filePath);

            Vector3 center = headerLoader.GetCenter(headerLoader);
            totalCenter += center;
        }
        Vector3 averageCenter = totalCenter / lasFiles.Length;

        // オフセットして表示
        foreach (var filePath in lasFiles)
        {
            // ヘッダー読込
            var headerLoader = new LasHeaderLoader(filePath);

            // 点を読込
            var pointLoader = new LasPointLoader(filePath, headerLoader);
            PointData[] points = pointLoader.LoadPoints(averageCenter);

            Debug.Log($"1: {Time.realtimeSinceStartup - start} 秒");

            // チャンク分割
            var chunkController = new ChunkController(points);
            List<ChunkPointData> chunkPointDatas = chunkController.Split(30.0f);

            Debug.Log($"2: {Time.realtimeSinceStartup - start} 秒");

            foreach (var chunkPointData in chunkPointDatas)
            {
                GameObject pointCloud = Instantiate(_pcp_Graphics);
                pointCloud.transform.SetParent(this.transform);
                pointCloud.GetComponent<PointGraphicsRender>().Render(chunkPointData);
            }

            Debug.Log($"3: {Time.realtimeSinceStartup - start} 秒");
        }
    }
}
