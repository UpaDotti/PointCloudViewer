using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

public class TpcLoader : MonoBehaviour
{
    // 設定
    [SerializeField]
    private string _filePath = "Assets/sample.tpc";

    [SerializeField]
    private Material _pointMaterial;

    [SerializeField]
    private int _maxChunkLoadsPerFrame = 50; // 1フレームでロードする最大チャンク数

    [SerializeField]
    private int _maxChunkUnloadsPerFrame = 100; // 1フレームでアンロードする最大チャンク数

    [SerializeField]
    private float _maxPointsPerPixel = 1.0f; // 1ピクセルあたりの許容点密度

    [SerializeField]
    private float _pointSize = 1.0f;

    // 状態
    [SerializeField]
    private TcpHeader _header;

    [SerializeField]
    private List<TcpChunk> _chunks;

    private List<TcpChunkRequest> _requests = new List<TcpChunkRequest>();

    private Camera _mainCamera;
    private BinaryReader _reader;
    private RenderParams _rp;
    private MaterialPropertyBlock _mpb;
    private GraphicsBuffer _quadIndex;
    private ulong _headerSize;
    private ulong _chunkSize;



    private void Start()
    {
        _mainCamera = Camera.main;
        string fullPath = Path.Combine(Application.dataPath, _filePath.Replace("Assets/", ""));
        LoadHeader(fullPath);
        SetupRendering();
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

    private void SetupRendering()
    {
        _rp = new RenderParams(_pointMaterial)
        {
            shadowCastingMode = ShadowCastingMode.Off,
            receiveShadows = false,
            lightProbeUsage = LightProbeUsage.Off,
            reflectionProbeUsage = ReflectionProbeUsage.Off,
            motionVectorMode = MotionVectorGenerationMode.ForceNoMotion,
        };

        _mpb = new MaterialPropertyBlock();

        uint[] index = { 0u, 1u, 2u, 2u, 1u, 3u };
        _quadIndex = new GraphicsBuffer(GraphicsBuffer.Target.Index, index.Length, sizeof(uint));
        _quadIndex.SetData(index);
    }

    private void Update()
    {
        Plane[] planes = GeometryUtility.CalculateFrustumPlanes(_mainCamera);

        // ロード／アンロードの判定
        foreach (var chunk in _chunks)
        {
            Bounds bounds = new Bounds();
            bounds.SetMinMax(chunk.min, chunk.max);

            // 視界に入っているか
            bool isVisible = GeometryUtility.TestPlanesAABB(planes, bounds);
            if (isVisible)
            {
                // LODを取得
                int desiredLod = ChooseLod(chunk, _mainCamera);

                // ロードされていない or LODが違うときはロードをリクエスト
                if (!chunk.isLoaded || chunk.loadedLod != desiredLod)
                {
                    EnqueueLoadRequest(chunk, desiredLod);
                }
            }
            else
            {
                // ロードされていて視界外のときはアンロード
                if (chunk.isLoaded)
                {
                    EnqueueUnloadRequest(chunk);
                }
            }
        }

        // ロード／アンロードの処理
        int loadsThisFrame = 0;
        int unloadsThisFrame = 0;
        for (int i = _requests.Count - 1; i >= 0; i--)
        {
            var req = _requests[i];

            if (!req.isLoad)
            {
                // フレーム毎のアンロード数を制限
                if (unloadsThisFrame >= _maxChunkUnloadsPerFrame)
                {
                    continue;
                }

                _requests.RemoveAt(i);
                UnloadChunkPoints(req.chunk);
                unloadsThisFrame++;
            }
            else
            {
                // フレーム毎のロード数を制限
                if (loadsThisFrame >= _maxChunkLoadsPerFrame)
                {
                    continue;
                }

                _requests.RemoveAt(i);
                LoadChunkPoints(req.chunk, req.lod);
                loadsThisFrame++;
            }
        }

        // 描画
        foreach (var chunk in _chunks)
        {
            if (!chunk.isLoaded) continue;

            Bounds bounds = new Bounds();
            bounds.SetMinMax(chunk.min, chunk.max);

            _mpb.Clear();
            _mpb.SetBuffer("_Points", chunk.pointBuffer);
            _mpb.SetVector("_ChunkMin", chunk.min);
            _mpb.SetVector("_ChunkMax", chunk.max);
            _mpb.SetFloat("_PointSize", _pointSize);

            _rp.matProps = _mpb;
            _rp.worldBounds = bounds;

            Graphics.RenderPrimitivesIndexedIndirect(_rp, MeshTopology.Triangles, _quadIndex, chunk.indirectArgs, 1, 0);
        }
    }

    /// <summary>
    /// ロードリクエストを追加
    /// </summary>
    private void EnqueueLoadRequest(TcpChunk chunk, int lod)
    {
        for (int i = 0; i < _requests.Count; i++)
        {
            // すでにこのチャンクのリクエストがあるか
            if (ReferenceEquals(_requests[i].chunk, chunk))
            {
                // 指示を上書き
                _requests[i] = new TcpChunkRequest { chunk = chunk, lod = lod, isLoad = true };
                return;
            }
        }

        // なければ新規追加
        _requests.Add(new TcpChunkRequest { chunk = chunk, lod = lod, isLoad = true });
    }

    /// <summary>
    /// アンロードリクエストを追加
    /// </summary>
    private void EnqueueUnloadRequest(TcpChunk chunk)
    {
        for (int i = 0; i < _requests.Count; i++)
        {
            // すでにこのチャンクのリクエストがあるか
            if (ReferenceEquals(_requests[i].chunk, chunk))
            {
                // 指示を上書き
                _requests[i] = new TcpChunkRequest { chunk = chunk, lod = _requests[i].lod, isLoad = false };
                return;
            }
        }

        // なければ新規追加
        _requests.Add(new TcpChunkRequest { chunk = chunk, lod = 0, isLoad = false });
    }

    /// <summary>
    /// 分割点群をロード
    /// </summary>
    private void LoadChunkPoints(TcpChunk chunk, int lod)
    {
        // すでにMeshがある場合はアンロード
        UnloadChunkPoints(chunk);

        // LOD別の情報を取得
        var info = chunk.lods[lod];

        // 読込の開始位置を設定
        ulong seekPos = _headerSize + _chunkSize + info.offset;
        _reader.BaseStream.Seek((long)seekPos, SeekOrigin.Begin);

        // 点群読込準備
        int count = info.count;
        var packed = new TcpPoint[count];

        // 点群読込
        for (int i = 0; i < count; i++)
        {
            ushort x16 = _reader.ReadUInt16();
            ushort y16 = _reader.ReadUInt16();
            ushort z16 = _reader.ReadUInt16();
            byte r8 = _reader.ReadByte();
            byte g8 = _reader.ReadByte();
            byte b8 = _reader.ReadByte();
            byte a8 = _reader.ReadByte();

            packed[i].xy = (uint)(x16 | (y16 << 16));
            packed[i].z = (uint)z16;
            packed[i].rgba = (uint)(r8 | (g8 << 8) | (b8 << 16) | (255 << 24));
        }

        // 点群バッファを作成
        chunk.pointBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 12);
        chunk.pointBuffer.SetData(packed);

        // 間接描画バッファを作成
        chunk.indirectArgs = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 1, GraphicsBuffer.IndirectDrawIndexedArgs.size);
        var args = new GraphicsBuffer.IndirectDrawIndexedArgs[1];
        args[0].indexCountPerInstance = 6u;
        args[0].instanceCount = (uint)count;
        args[0].startIndex = 0u;
        args[0].baseVertexIndex = 0u;
        chunk.indirectArgs.SetData(args);

        // ロードしているLODを記録
        chunk.loadedLod = lod;
    }

    /// <summary>
    /// LODを選択
    /// </summary>
    private int ChooseLod(TcpChunk chunk, Camera camera)
    {
        Bounds bounds = new Bounds();
        bounds.SetMinMax(chunk.min, chunk.max);

        // Bounds円の半径を取得
        float r = bounds.extents.magnitude;

        // Bounds円の半径をピクセル数で取得
        Vector3 center = bounds.center;
        Vector3 sc = camera.WorldToScreenPoint(center);
        Vector3 sx = camera.WorldToScreenPoint(center + camera.transform.right * r);
        float rPx = (sx - sc).magnitude;

        // Bounds円の面積をピクセル数で取得
        float areaPx = Mathf.Max(1f, Mathf.PI * rPx * rPx);

        // 閾値より低い場合にLODを返す
        for (int i = 0; i < chunk.lods.Length; i++)
        {
            int count = chunk.lods[i].count;
            float density = count / areaPx;
            if (density <= _maxPointsPerPixel) return i;
        }

        // 最も粗いLODを返す
        return chunk.lods.Length - 1;
    }

    /// <summary>
    /// 分割点群をアンロード
    /// </summary>
    private void UnloadChunkPoints(TcpChunk chunk)
    {
        chunk.pointBuffer?.Dispose();
        chunk.pointBuffer = null;

        chunk.indirectArgs?.Dispose();
        chunk.indirectArgs = null;

        chunk.loadedLod = -1;
    }

    private void OnDisable()
    {
        UnloadAllChunks();
        UnloadIndexBuffer();
        CloseReader();
    }

    private void OnDestroy()
    {
        UnloadAllChunks();
        UnloadIndexBuffer();
        CloseReader();
    }

    private void UnloadAllChunks()
    {
        foreach (var chunk in _chunks)
        {
            UnloadChunkPoints(chunk);
        }
    }

    private void UnloadIndexBuffer()
    {
        _quadIndex?.Dispose();
        _quadIndex = null;
    }

    private void CloseReader()
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

    public GraphicsBuffer pointBuffer;
    public GraphicsBuffer indirectArgs;

    public int loadedLod = -1;

    public bool isLoaded => pointBuffer != null;
}

[Serializable]
public struct TcpLod
{
    public ulong offset;
    public int count;
}

[Serializable]
public struct TcpPoint
{
    public uint xy;   // x,y 32bit
    public uint z;    // z,0 32 bit
    public uint rgba;  // r,g,b,a 32bit(8bit * 4)
}

[Serializable]
public struct TcpChunkRequest
{
    public TcpChunk chunk;
    public int lod;
    public bool isLoad;
}