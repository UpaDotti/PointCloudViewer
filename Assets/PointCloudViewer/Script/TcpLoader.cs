using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public class TpcLoader : MonoBehaviour
{
    // 設定
    [SerializeField]
    private string _filePath = "Assets/sample.tpc";

    [SerializeField]
    private Material _pointMaterial;

    [SerializeField]
    private float _maxPointsPerPixel = 1.0f; // 1ピクセルあたりの許容点密度

    [SerializeField]
    private float _pointSize = 0.05f;

    // 状態
    [SerializeField]
    private TcpHeader _header;

    [SerializeField]
    private List<TcpChunk> _chunks;

    private BinaryReader _reader;
    private ulong _headerSize;
    private ulong _chunkSize;



    private void Start()
    {
        string fullPath = Path.Combine(Application.dataPath, _filePath.Replace("Assets/", ""));
        LoadHeader(fullPath);
    }

    /// <summary>
    /// ヘッダーとチャンクを読込
    /// </summary>
    private void LoadHeader(string fullPath)
    {
        _header = new TcpHeader();
        _chunks = new List<TcpChunk>();
        _reader = new BinaryReader(File.OpenRead(fullPath));

        long start = _reader.BaseStream.Position;

        // ヘッダー読込
        _header.magic = new string(_reader.ReadChars(4));
        _header.versionMajor = _reader.ReadByte();
        _header.versionMinor = _reader.ReadByte();
        _header.versionPatch = _reader.ReadByte();
        _header.lodLevels = _reader.ReadByte();
        _header.gridSize = _reader.ReadSingle();
        _header.chunkCount = _reader.ReadInt32();
        _header.center = new Vector3(_reader.ReadSingle(), _reader.ReadSingle(), _reader.ReadSingle());

        // ヘッダーサイズを取得
        long headerEnd = _reader.BaseStream.Position;
        _headerSize = (ulong)(headerEnd - start);

        // チャンク読込
        for (int i = 0; i < _header.chunkCount; i++)
        {
            Vector3Int index = new Vector3Int(_reader.ReadInt32(), _reader.ReadInt32(), _reader.ReadInt32());
            Vector3 min = new Vector3(_reader.ReadSingle(), _reader.ReadSingle(), _reader.ReadSingle());
            Vector3 max = new Vector3(_reader.ReadSingle(), _reader.ReadSingle(), _reader.ReadSingle());

            var lods = new TcpLod[_header.lodLevels];
            for (int l = 0; l < _header.lodLevels; l++)
            {
                lods[l].offset = _reader.ReadUInt64();
                lods[l].count = _reader.ReadInt32();
            }

            _chunks.Add(new TcpChunk { index = index, min = min, max = max, lods = lods });
        }

        // チャンクサイズを取得
        long chunkEnd = _reader.BaseStream.Position;
        _chunkSize = (ulong)(chunkEnd - headerEnd);
    }

    private void Update()
    {
        var planes = GeometryUtility.CalculateFrustumPlanes(Camera.main);

        foreach (var chunk in _chunks)
        {
            Bounds bounds = new Bounds();
            bounds.SetMinMax(chunk.min + transform.position, chunk.max + transform.position);

            // 視界に入っているか
            bool isVisible = GeometryUtility.TestPlanesAABB(planes, bounds);
            if (isVisible)
            {
                // LODを取得
                int desiredLod = ChooseLod(chunk);

                // ロードされていない or LODが違うときは 読込
                if (!chunk.isLoaded || chunk.loadedLod != desiredLod)
                {
                    LoadChunkPoints(chunk, desiredLod);
                }
            }
            else
            {
                if (chunk.isLoaded) UnloadChunk(chunk);
            }
        }
    }

    /// <summary>
    /// 分割点群をロード
    /// </summary>
    private void LoadChunkPoints(TcpChunk chunk, int lod)
    {
        // すでにオブジェクトがある場合はアンロード
        if (chunk.chunkObject != null)
        {
            UnloadChunk(chunk);
        }

        // LOD別の情報を取得
        var info = chunk.lods[lod];

        // 読込の開始位置を設定
        ulong seekPos = _headerSize + _chunkSize + info.offset;
        _reader.BaseStream.Seek((long)seekPos, SeekOrigin.Begin);

        // 点群読込準備
        int count = info.count;
        var vertices = new List<Vector3>(count);
        var colors = new List<Color>(count);
        var indices = new List<int>(count);

        // 点群読込
        for (int i = 0; i < count; i++)
        {
            float x = _reader.ReadSingle();
            float y = _reader.ReadSingle();
            float z = _reader.ReadSingle();
            byte r = _reader.ReadByte();
            byte g = _reader.ReadByte();
            byte b = _reader.ReadByte();

            vertices.Add(new Vector3(x, y, z));
            colors.Add(new Color32(r, g, b, 255));
            indices.Add(i);
        }

        // GameObjectを作成
        var go = new GameObject($"Chunk_{chunk.index}");
        go.transform.SetParent(this.transform);
        chunk.meshFilter = go.AddComponent<MeshFilter>();
        chunk.meshRenderer = go.AddComponent<MeshRenderer>();
        chunk.meshRenderer.sharedMaterial = _pointMaterial;
        chunk.chunkObject = go;

        // Meshを設定
        chunk.mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        chunk.mesh.MarkDynamic();
        chunk.mesh.SetVertices(vertices);
        chunk.mesh.SetColors(colors);
        chunk.mesh.SetIndices(indices, MeshTopology.Points, 0);
        chunk.meshFilter.sharedMesh = chunk.mesh;

        // ロードしているLODを記録
        chunk.loadedLod = lod;
    }

    /// <summary>
    /// LODを選択
    /// </summary>
    private int ChooseLod(TcpChunk chunk)
    {
        var cam = Camera.main;

        Bounds b = new Bounds();
        b.SetMinMax(chunk.min + transform.position, chunk.max + transform.position);

        // Bounds円の半径を取得
        float r = b.extents.magnitude;

        // Bounds円の半径をピクセル数で取得
        Vector3 center = b.center;
        Vector3 sc = cam.WorldToScreenPoint(center);
        Vector3 sx = cam.WorldToScreenPoint(center + cam.transform.right * r);
        float rPx = (sx - sc).magnitude;

        // Bounds円の面積をピクセル数で取得
        float areaPx = Mathf.Max(1f, Mathf.PI * rPx * rPx);

        // 閾値より低い場合にLODを返す
        for (int l = 0; l < chunk.lods.Length; l++)
        {
            int count = chunk.lods[l].count;
            float density = count / areaPx;
            if (density <= _maxPointsPerPixel) return l;
        }

        // 最も粗いLODを返す
        return chunk.lods.Length - 1;
    }

    /// <summary>
    /// 分割点群をアンロード
    /// </summary>
    private void UnloadChunk(TcpChunk chunk)
    {
        if (chunk.chunkObject != null)
        {
            Destroy(chunk.chunkObject);
            chunk.chunkObject = null;
            chunk.mesh = null;
            chunk.meshFilter = null;
            chunk.meshRenderer = null;
            chunk.loadedLod = -1;
        }
    }

    void OnDisable()
    {
        CloseReader();
    }

    void OnDestroy()
    {
        CloseReader();
    }

    void CloseReader()
    {
        if (_reader != null)
        {
            _reader.Close();
            _reader.Dispose();
            _reader = null;
        }
    }
}

[Serializable]
public class TcpHeader
{
    public string magic;
    public int versionMajor;
    public int versionMinor;
    public int versionPatch;
    public int lodLevels;
    public float gridSize;
    public int chunkCount;
    public Vector3 center;
}

[Serializable]
public class TcpChunk
{
    public Vector3Int index;
    public Vector3 min;
    public Vector3 max;
    public TcpLod[] lods;

    public GameObject chunkObject = null;
    public Mesh mesh;
    public MeshFilter meshFilter;
    public MeshRenderer meshRenderer;

    public int loadedLod = -1;
    public bool isLoaded => chunkObject != null;
}

[Serializable]
public struct TcpLod
{
    public ulong offset;
    public int count;
}