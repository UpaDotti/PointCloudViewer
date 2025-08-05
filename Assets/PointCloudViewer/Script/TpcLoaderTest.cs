using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public class TpcLoaderTest : MonoBehaviour
{
    public string filePath = "Assets/sample.tpc";

    struct ChunkInfo
    {
        public Vector3Int index;
        public Vector3 min;
        public Vector3 max;
        public ulong offset;
        public int count;
    }

    void Start()
    {
        string fullPath = Path.Combine(Application.dataPath, filePath.Replace("Assets/", ""));
        DumpTPCHeaderChunksAndSamplePoints(fullPath);
    }

    void DumpTPCHeaderChunksAndSamplePoints(string fullPath)
    {
        List<ChunkInfo> chunkInfos = new List<ChunkInfo>();

        using (BinaryReader reader = new BinaryReader(File.OpenRead(fullPath)))
        {
            // ヘッダー読み取り
            string magic = new string(reader.ReadChars(4)); // "TPC\0"
            int version = reader.ReadInt32();
            float gridSize = reader.ReadSingle();
            int chunkCount = reader.ReadInt32();
            Vector3 center = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

            Debug.Log($"📄 ヘッダー情報:");
            Debug.Log($"Magic: {magic}, Version: {version}, GridSize: {gridSize}, ChunkCount: {chunkCount}, Center: {center}");

            // チャンク情報読み取り
            for (int i = 0; i < chunkCount; i++)
            {
                Vector3Int index = new Vector3Int(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
                Vector3 min = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                Vector3 max = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                ulong offset = reader.ReadUInt64();
                int count = reader.ReadInt32();

                chunkInfos.Add(new ChunkInfo
                {
                    index = index,
                    min = min,
                    max = max,
                    offset = offset,
                    count = count
                });
            }

            Debug.Log($"✅ チャンク数: {chunkInfos.Count}");

            const int HEADER_SIZE = 4 + 4 + 4 + 4 + 12; // 4byte * 4 + Vector3
            const int CHUNK_META_SIZE = 48;
            ulong chunkMetaSize = (ulong)(chunkInfos.Count * CHUNK_META_SIZE);

            // 各チャンクの最初の20点だけ読み取り
            for (int chunkIdx = 0; chunkIdx < chunkInfos.Count; chunkIdx++)
            {
                var chunk = chunkInfos[chunkIdx];
                ulong seekPos = (ulong)HEADER_SIZE + chunkMetaSize + chunk.offset;

                reader.BaseStream.Seek((long)seekPos, SeekOrigin.Begin);
                int maxPointsToRead = Mathf.Min(chunk.count, 20);

                Debug.Log($"📦 Chunk[{chunkIdx}] Index: {chunk.index}, 点数: {chunk.count} → 最初の{maxPointsToRead}点:");

                for (int i = 0; i < maxPointsToRead; i++)
                {
                    float x = reader.ReadSingle();
                    float y = reader.ReadSingle();
                    float z = reader.ReadSingle();
                    byte r = reader.ReadByte();
                    byte g = reader.ReadByte();
                    byte b = reader.ReadByte();

                    Debug.Log($"  [{i}] Pos=({x:F3}, {y:F3}, {z:F3}) Color=({r}, {g}, {b})");
                }

                // 残りの点を読み飛ばす（無駄に読み込まない）
                long skipBytes = (chunk.count - maxPointsToRead) * (3 * 4 + 3); // float*3 + byte*3
                reader.BaseStream.Seek(skipBytes, SeekOrigin.Current);
            }

            Debug.Log("✅ 各チャンクから最大20点の出力が完了しました。");
        }
    }
}
