using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public class LasPointLoader
{
    private string _filePath;
    private LasHeaderLoader _header;



    public LasPointLoader(string filePath, LasHeaderLoader headerLoader)
    {
        _filePath = filePath;
        _header = headerLoader;
    }

    public PointData[] LoadPoints(Vector3 centerOffset = default)
    {
        // ヘッダー情報の取得
        uint offset = _header.GetValue<uint>(LasFieldType.OffsetToPointData);
        ushort recordLength = _header.GetValue<ushort>(LasFieldType.PointDataRecordLength);
        uint pointCount = _header.GetValue<uint>(LasFieldType.LegacyNumberOfPointRecords);

        double xScale = _header.GetValue<double>(LasFieldType.XScaleFactor);
        double yScale = _header.GetValue<double>(LasFieldType.YScaleFactor);
        double zScale = _header.GetValue<double>(LasFieldType.ZScaleFactor);
        double xOffset = _header.GetValue<double>(LasFieldType.XOffset);
        double yOffset = _header.GetValue<double>(LasFieldType.YOffset);
        double zOffset = _header.GetValue<double>(LasFieldType.ZOffset);

        PointData[] points = new PointData[pointCount];

        // 一括でバイト配列に読み込む
        byte[] buffer = new byte[recordLength * pointCount];
        using (FileStream fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read))
        {
            fs.Seek(offset, SeekOrigin.Begin);
            fs.Read(buffer, 0, buffer.Length);
        }

        int pos = 0;
        for (int i = 0; i < pointCount; i++)
        {
            int x = BitConverter.ToInt32(buffer, pos);
            int y = BitConverter.ToInt32(buffer, pos + 4);
            int z = BitConverter.ToInt32(buffer, pos + 8);

            float fx = (float)(x * xScale + xOffset);
            float fy = (float)(y * yScale + yOffset);
            float fz = (float)(z * zScale + zOffset);

            // intensity～gpsTime（12～27バイト）をスキップ
            // [12 - 13]: Intensity (uint16)
            // [14]     : Flags (byte)
            // [15]     : Classification (byte)
            // [16]     : ScanAngleRank (sbyte)
            // [17]     : UserData (byte)
            // [18 - 19]: PointSourceId (uint16)
            // [20 - 27]: GpsTime (double)
            int rgbStart = pos + 28;

            // Color 16bit (0-65535)
            ushort r16 = BitConverter.ToUInt16(buffer, rgbStart);
            ushort g16 = BitConverter.ToUInt16(buffer, rgbStart + 2);
            ushort b16 = BitConverter.ToUInt16(buffer, rgbStart + 4);

            // Color 8bit (0-255)
            byte r8 = (byte)(r16 >> 8);
            byte g8 = (byte)(g16 >> 8);
            byte b8 = (byte)(b16 >> 8);

            // Color32を作成
            Color32 color = new Color32(r8, g8, b8, 255);

            Vector3 unityPos = new Vector3(fx, fz, fy);
            points[i] = new PointData(unityPos - centerOffset, color);

            pos += recordLength;
        }

        return points;
    }
}

public struct PointData
{
    public Vector3 Position;
    public Color32 Color;

    public PointData(Vector3 pos, Color32 col)
    {
        Position = pos;
        Color = col;
    }
}
