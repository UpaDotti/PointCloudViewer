    using System;
    using System.Collections.Generic;
    using System.IO;
    using UnityEngine;

    public class TpcLoader : MonoBehaviour
    {
        // 設定
        [SerializeField] private string _filePath = "Assets/sample.tpc"; // ファイルの保存場所
        [SerializeField] private Material _pointMaterial; // 点群のMaterial
        [SerializeField] private float _pointSize = 0.05f; // 点のサイズ
        const ulong _headerSIZE = 28;
        const Int32 _chunkSIZE = 48;

        // 状態
        [SerializeField] private List<TcpChunk> _chunks; // チャンクデータ
        private BinaryReader _reader;
        private ulong _chunkMetaSize;



        void Start()
        {
            string fullPath = Path.Combine(Application.dataPath, _filePath.Replace("Assets/", ""));
            LoadTPCHeader(fullPath);
        }

        void LoadTPCHeader(string fullPath)
        {
            _chunks = new List<TcpChunk>();
            _reader = new BinaryReader(File.OpenRead(fullPath));

            // ヘッダー読み込み
            string magic = new string(_reader.ReadChars(4));
            int version = _reader.ReadInt32();
            float gridSize = _reader.ReadSingle();
            int chunkCount = _reader.ReadInt32();
            Vector3 center = new Vector3(_reader.ReadSingle(), _reader.ReadSingle(), _reader.ReadSingle());

            _chunkMetaSize = (ulong)(chunkCount * _chunkSIZE);

            // チャンクメタ情報読み込み
            for (int i = 0; i < chunkCount; i++)
            {
                Vector3Int index = new Vector3Int(_reader.ReadInt32(), _reader.ReadInt32(), _reader.ReadInt32());
                Vector3 min = new Vector3(_reader.ReadSingle(), _reader.ReadSingle(), _reader.ReadSingle());
                Vector3 max = new Vector3(_reader.ReadSingle(), _reader.ReadSingle(), _reader.ReadSingle());
                ulong offset = _reader.ReadUInt64();
                int count = _reader.ReadInt32();

                _chunks.Add(new TcpChunk
                {
                    index = index,
                    min = min,
                    max = max,
                    offset = offset,
                    count = count
                });
            }
        }

        void Update()
        {
            Plane[] planes = GeometryUtility.CalculateFrustumPlanes(Camera.main);

            foreach (var chunk in _chunks)
            {
                // チャンク点群のBoundを作成
                Bounds bounds = new Bounds();
                bounds.SetMinMax(chunk.min, chunk.max);

                // 視界に入っているか
                bool isVisible = GeometryUtility.TestPlanesAABB(planes, bounds);

                // チャンク点群のロードとアンロードを切替
                if (isVisible)
                {
                    if (!chunk.isLoaded) LoadChunkPoints(chunk);
                }
                else
                {
                    if (chunk.isLoaded) UnloadChunk(chunk);
                }
            }
        }

        void LoadChunkPoints(TcpChunk chunk)
        {
            ulong seekPos = _headerSIZE + _chunkMetaSize + chunk.offset;
            _reader.BaseStream.Seek((long)seekPos, SeekOrigin.Begin);

            List<Vector3> vertices = new List<Vector3>();
            List<Color> colors = new List<Color>();
            List<int> indices = new List<int>();

            for (int i = 0; i < chunk.count; i++)
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

            GameObject go = new GameObject($"Chunk_{chunk.index}");
            go.transform.parent = this.transform;

            Mesh mesh = new Mesh();
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(vertices);
            mesh.SetIndices(indices.ToArray(), MeshTopology.Points, 0);
            mesh.SetColors(colors);

            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;

            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _pointMaterial;

            chunk.chunkObject = go;
        }

        void UnloadChunk(TcpChunk chunk)
        {
            if (chunk.chunkObject != null)
            {
                Destroy(chunk.chunkObject);
                chunk.chunkObject = null;
            }
        }
    }

    [Serializable]
    class TcpChunk
    {
        public Vector3Int index;
        public Vector3 min;
        public Vector3 max;
        public ulong offset;
        public int count;

        public GameObject chunkObject = null;
        public bool isLoaded => chunkObject != null;
    }
