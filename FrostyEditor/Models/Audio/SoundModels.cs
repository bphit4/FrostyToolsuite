using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Frosty.Sdk.Utils;

namespace FrostyEditor.Models.Audio;

public enum FieldType : byte
{
    Boolean = 0,
    Int32 = 1,
    Int64 = 2,
    UInt32 = 3,
    UInt64 = 4,
    Float32 = 5,
    Float64 = 6,
    String = 7,
    Pointer = 8
}

public enum ColumnFormat : byte
{
    Constant = 0,
    IndexFormula = 1,
    ShiftedBase = 2,
    LookupTable = 3,
    Raw = 4
}

public sealed class Field
{
    public Field(Frosty.Sdk.IO.Endian endian, uint id, FieldType dataType, ColumnFormat originalFormat,
        uint tableOffset, uint nextReferenceOffset, ObservableCollection<object> values)
    {
        Endian = endian;
        Id = id;
        DataType = dataType;
        OriginalFormat = originalFormat;
        TableOffset = tableOffset;
        NextReferenceOffset = nextReferenceOffset;
        Values = values;
    }

    public Frosty.Sdk.IO.Endian Endian { get; }
    public uint Id { get; }
    public FieldType DataType { get; }
    public ColumnFormat OriginalFormat { get; }
    public uint TableOffset { get; }
    public uint NextReferenceOffset { get; }
    public ObservableCollection<object> Values { get; }
}

public sealed class DataSetIndex
{
    public DataSetIndex()
    {
    }

    public DataSetIndex(List<uint> columnKeys)
    {
        ColumnKeys = columnKeys;
    }

    public List<uint> ColumnKeys { get; } = [];
}

public sealed class DataSet
{
    public DataSet(uint id, uint sampleGroupId, uint dataOffset, int numElems, Field[] fields, Field[] indexColumns, DataSetIndex[] indexes)
    {
        Id = id;
        SampleGroupId = sampleGroupId;
        DataOffset = dataOffset;
        Fields = fields;
        IndexColumns = indexColumns;
        Indexes = indexes;
        NumElems = numElems;
    }

    public uint Id { get; }
    public uint SampleGroupId { get; }
    public uint DataOffset { get; }
    public Field[] Fields { get; }
    public Field[] IndexColumns { get; }
    public DataSetIndex[] Indexes { get; }
    public int NumElems { get; set; }

    public Field? Get(uint key)
    {
        return Fields.FirstOrDefault(f => f.Id == key) ?? IndexColumns.FirstOrDefault(f => f.Id == key);
    }

    public Field? Get(string name)
    {
        foreach (uint hash in KnownStringHashes.ResolveHashes(name))
        {
            Field? field = Get(hash);
            if (field is not null)
            {
                return field;
            }
        }

        return null;
    }
}

public sealed class Bank
{
    public Bank(uint key, uint projectKey, List<DataSet> dataSets, byte[] dataBlock)
    {
        Key = key;
        ProjectKey = projectKey;
        DataSets = dataSets;
        DataBlock = dataBlock;
    }

    public uint Key { get; }
    public uint ProjectKey { get; }
    public List<DataSet> DataSets { get; }
    public byte[] DataBlock { get; }

    public DataSet? Get(string name)
    {
        foreach (uint nameHash in KnownStringHashes.ResolveHashes(name))
        {
            DataSet? dataSet = DataSets.FirstOrDefault(ds => ds.Id == nameHash);
            if (dataSet is not null)
            {
                return dataSet;
            }
        }

        return null;
    }
}

public sealed class ChunkRef
{
    public ChunkRef(ulong chunkIndex, Guid chunkId, long chunkSize)
    {
        ChunkIndex = chunkIndex;
        ChunkId = chunkId;
        ChunkSize = chunkSize;
    }

    public ulong ChunkIndex { get; }
    public Guid ChunkId { get; }
    public long ChunkSize { get; }
}

public sealed class Segment
{
    public Segment(int index, uint samplesOffset, uint seekTableOffset, float segmentLength)
    {
        Index = index;
        SamplesOffset = samplesOffset;
        SeekTableOffset = seekTableOffset;
        SegmentLength = segmentLength;
    }

    public int Index { get; }
    public uint SamplesOffset { get; set; }
    public uint SeekTableOffset { get; set; }
    public float SegmentLength { get; set; }
}

public sealed class Variation
{
    public Variation(int index, uint variationId, ChunkRef chunkRef, List<Segment> segments, Dictionary<uint, object> values)
    {
        Index = index;
        VariationId = variationId;
        ChunkRef = chunkRef;
        Segments = segments;
        Values = values;
    }

    public int Index { get; }
    public uint VariationId { get; }
    public ChunkRef ChunkRef { get; }
    public List<Segment> Segments { get; }
    public Dictionary<uint, object> Values { get; }
}

public sealed class NewWaveBank
{
    public NewWaveBank(uint key, uint projectKey, List<Variation> variations, List<DataSet> allDataSets, List<DataSet> unknownDataSets)
    {
        Key = key;
        ProjectKey = projectKey;
        Variations = variations;
        AllDataSets = allDataSets;
        UnknownDataSets = unknownDataSets;
    }

    public uint Key { get; }
    public uint ProjectKey { get; }
    public List<Variation> Variations { get; }
    public List<DataSet> AllDataSets { get; }
    public List<DataSet> UnknownDataSets { get; }

    public Guid EbxInstanceGuid { get; set; }
    public uint SelectionDatasetSampleGroupId { get; set; }
    public int[] SelectionParameterIds { get; set; } = [];
    public bool SelectionParametersOnly { get; set; }
    public uint TrailerBankKey { get; set; }

    public DataSet? GetDataSet(string name)
    {
        foreach (uint hash in KnownStringHashes.ResolveHashes(name))
        {
            DataSet? dataSet = AllDataSets.FirstOrDefault(ds => ds.Id == hash);
            if (dataSet is not null)
            {
                return dataSet;
            }
        }

        return null;
    }
}
