using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public class LasHeaderLoader
{
    private string _filePath;

    private List<LasHeaderField> _fields = new List<LasHeaderField>();
    public List<LasHeaderField> Fields => _fields;



    public LasHeaderLoader(string filePath)
    {
        _filePath = filePath;
        InitFields();
        LoadHeader();
    }

    private void InitFields()
    {
        _fields.Add(new LasHeaderField(LasFieldType.FileSignature, LasDataType.CharArray4, 4));
        _fields.Add(new LasHeaderField(LasFieldType.FileSourceID, LasDataType.UShort, 2));
        _fields.Add(new LasHeaderField(LasFieldType.GlobalEncoding, LasDataType.UShort, 2));
        _fields.Add(new LasHeaderField(LasFieldType.ProjectID_GUID1, LasDataType.UInt, 4));
        _fields.Add(new LasHeaderField(LasFieldType.ProjectID_GUID2, LasDataType.UShort, 2));
        _fields.Add(new LasHeaderField(LasFieldType.ProjectID_GUID3, LasDataType.UShort, 2));
        _fields.Add(new LasHeaderField(LasFieldType.ProjectID_GUID4, LasDataType.ByteArray8, 8));
        _fields.Add(new LasHeaderField(LasFieldType.VersionMajor, LasDataType.Byte, 1));
        _fields.Add(new LasHeaderField(LasFieldType.VersionMinor, LasDataType.Byte, 1));
        _fields.Add(new LasHeaderField(LasFieldType.SystemIdentifier, LasDataType.CharArray32, 32));
        _fields.Add(new LasHeaderField(LasFieldType.GeneratingSoftware, LasDataType.CharArray32, 32));
        _fields.Add(new LasHeaderField(LasFieldType.FileCreationDayOfYear, LasDataType.UShort, 2));
        _fields.Add(new LasHeaderField(LasFieldType.FileCreationYear, LasDataType.UShort, 2));
        _fields.Add(new LasHeaderField(LasFieldType.HeaderSize, LasDataType.UShort, 2));
        _fields.Add(new LasHeaderField(LasFieldType.OffsetToPointData, LasDataType.UInt, 4));
        _fields.Add(new LasHeaderField(LasFieldType.NumberOfVariableLengthRecords, LasDataType.UInt, 4));
        _fields.Add(new LasHeaderField(LasFieldType.PointDataRecordFormat, LasDataType.Byte, 1));
        _fields.Add(new LasHeaderField(LasFieldType.PointDataRecordLength, LasDataType.UShort, 2));
        _fields.Add(new LasHeaderField(LasFieldType.LegacyNumberOfPointRecords, LasDataType.UInt, 4));
        _fields.Add(new LasHeaderField(LasFieldType.LegacyNumberOfPointByReturn, LasDataType.UIntArray5, 20));
        _fields.Add(new LasHeaderField(LasFieldType.XScaleFactor, LasDataType.Double, 8));
        _fields.Add(new LasHeaderField(LasFieldType.YScaleFactor, LasDataType.Double, 8));
        _fields.Add(new LasHeaderField(LasFieldType.ZScaleFactor, LasDataType.Double, 8));
        _fields.Add(new LasHeaderField(LasFieldType.XOffset, LasDataType.Double, 8));
        _fields.Add(new LasHeaderField(LasFieldType.YOffset, LasDataType.Double, 8));
        _fields.Add(new LasHeaderField(LasFieldType.ZOffset, LasDataType.Double, 8));
        _fields.Add(new LasHeaderField(LasFieldType.MaxX, LasDataType.Double, 8));
        _fields.Add(new LasHeaderField(LasFieldType.MinX, LasDataType.Double, 8));
        _fields.Add(new LasHeaderField(LasFieldType.MaxY, LasDataType.Double, 8));
        _fields.Add(new LasHeaderField(LasFieldType.MinY, LasDataType.Double, 8));
        _fields.Add(new LasHeaderField(LasFieldType.MaxZ, LasDataType.Double, 8));
        _fields.Add(new LasHeaderField(LasFieldType.MinZ, LasDataType.Double, 8));
        _fields.Add(new LasHeaderField(LasFieldType.StartOfWaveformDataPacketRecord, LasDataType.ULong, 8));
        _fields.Add(new LasHeaderField(LasFieldType.StartOfFirstExtendedVariableLengthRecord, LasDataType.ULong, 8));
        _fields.Add(new LasHeaderField(LasFieldType.NumberOfExtendedVariableLengthRecords, LasDataType.UInt, 4));
        _fields.Add(new LasHeaderField(LasFieldType.NumberOfPointRecords, LasDataType.ULong, 8));
        _fields.Add(new LasHeaderField(LasFieldType.NumberOfPointsByReturn, LasDataType.ULongArray15, 120));
    }

    private void LoadHeader()
    {
        using (BinaryReader reader = new BinaryReader(File.Open(_filePath, FileMode.Open)))
        {
            int offset = 0;
            foreach (var field in _fields)
            {
                reader.BaseStream.Seek(offset, SeekOrigin.Begin);
                field.Value = ReadField(reader, field);
                offset += field.Size;
            }
        }
    }

    private object ReadField(BinaryReader reader, LasHeaderField field)
    {
        switch (field.LasDataType)
        {
            case LasDataType.CharArray4:
                return new string(reader.ReadChars(4));

            case LasDataType.CharArray32:
                return new string(reader.ReadChars(32));

            case LasDataType.UShort:
                return reader.ReadUInt16();

            case LasDataType.UInt:
                return reader.ReadUInt32();

            case LasDataType.Byte:
                return reader.ReadByte();

            case LasDataType.ByteArray8:
                return BitConverter.ToString(reader.ReadBytes(8));

            case LasDataType.Double:
                return reader.ReadDouble();

            case LasDataType.ULong:
                return reader.ReadUInt64();

            case LasDataType.UIntArray5:
                {
                    uint[] u5 = new uint[5];
                    for (int i = 0; i < 5; i++) u5[i] = reader.ReadUInt32();
                    return string.Join(",", u5);
                }

            case LasDataType.ULongArray15:
                {
                    ulong[] u15 = new ulong[15];
                    for (int i = 0; i < 15; i++) u15[i] = reader.ReadUInt64();
                    return string.Join(",", u15);
                }

            default:
                throw new Exception($"未対応のデータ型: {field.LasDataType}");
        }
    }



    public T GetValue<T>(LasFieldType type)
    {
        var field = _fields.Find(f => f.LasFieldType == type);

        if (field == null) return default;

        if (field.Value is T value) return value;

        return default;
    }



    public Vector3 GetCenter(LasHeaderLoader header)
    {
        double minX = header.GetValue<double>(LasFieldType.MinX);
        double maxX = header.GetValue<double>(LasFieldType.MaxX);
        double minY = header.GetValue<double>(LasFieldType.MinY);
        double maxY = header.GetValue<double>(LasFieldType.MaxY);
        double minZ = header.GetValue<double>(LasFieldType.MinZ);
        double maxZ = header.GetValue<double>(LasFieldType.MaxZ);

        double cx = (minX + maxX) * 0.5;
        double cy = (minY + maxY) * 0.5;
        double cz = (minZ + maxZ) * 0.5;

        return new Vector3((float)cx, (float)cz, (float)cy);
    }
}

public enum LasFieldType
{
    FileSignature,
    FileSourceID,
    GlobalEncoding,
    ProjectID_GUID1,
    ProjectID_GUID2,
    ProjectID_GUID3,
    ProjectID_GUID4,
    VersionMajor,
    VersionMinor,
    SystemIdentifier,
    GeneratingSoftware,
    FileCreationDayOfYear,
    FileCreationYear,
    HeaderSize,
    OffsetToPointData,
    NumberOfVariableLengthRecords,
    PointDataRecordFormat,
    PointDataRecordLength,
    LegacyNumberOfPointRecords,
    LegacyNumberOfPointByReturn,
    XScaleFactor,
    YScaleFactor,
    ZScaleFactor,
    XOffset,
    YOffset,
    ZOffset,
    MaxX,
    MinX,
    MaxY,
    MinY,
    MaxZ,
    MinZ,
    StartOfWaveformDataPacketRecord,
    StartOfFirstExtendedVariableLengthRecord,
    NumberOfExtendedVariableLengthRecords,
    NumberOfPointRecords,
    NumberOfPointsByReturn
}

public enum LasDataType
{
    CharArray4,
    CharArray32,
    Byte,
    ByteArray8,
    UShort,
    UInt,
    ULong,
    Double,
    UIntArray5,
    ULongArray15
}

[Serializable]
public class LasHeaderField
{
    private LasFieldType _lasFieldType;
    public LasFieldType LasFieldType => _lasFieldType;

    private LasDataType _lasDataType;
    public LasDataType LasDataType => _lasDataType;

    private int _size;
    public int Size => _size;

    private object _value;
    public object Value
    {
        get => _value;
        set => _value = value;
    }

    public LasHeaderField(LasFieldType lasFieldType, LasDataType lasDataType, int size)
    {
        _lasFieldType = lasFieldType;
        _lasDataType = lasDataType;
        _size = size;
    }
}
