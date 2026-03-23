using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Frosty.Sdk.IO;
using Frosty.Sdk.Utils;
using FrostyEditor.Models.Audio;

namespace FrostyEditor.Managers.Sound;

public class NewWaveAssetSampleBankWriter
{
    private sealed class FileWriter : DataStream
    {
        public FileWriter(Stream stream)
            : base(stream)
        {
        }

        public Stream BaseStream => m_stream;

        public void WritePadding(int alignment)
        {
            while (Position % alignment != 0)
            {
                WriteByte(0);
            }
        }

        public void WriteBytes(byte[] buffer)
        {
            Write(buffer, 0, buffer.Length);
        }

        public void Write(byte value)
        {
            WriteByte(value);
        }

        public void Write(int value)
        {
            WriteByte((byte)value);
        }

        public void Write(uint value)
        {
            WriteUInt32(value, Endian.Little);
        }

        public void Write(long value)
        {
            WriteInt64(value, Endian.Little);
        }

        public void Write(ushort value)
        {
            WriteUInt16(value, Endian.Little);
        }

        public void WriteGuid(Guid value)
        {
            base.WriteGuid(value, Endian.Little);
        }

        public void WriteUInt32LittleEndian(uint value)
        {
            WriteUInt32(value, Endian.Little);
        }

        public void WriteInt32LittleEndian(int value)
        {
            WriteInt32(value, Endian.Little);
        }

        public void WriteUInt64LittleEndian(ulong value)
        {
            WriteUInt64(value, Endian.Little);
        }
    }

    private static class Djb2Hash
    {
        public static uint HashString32(string value)
        {
            return (uint)Frosty.Sdk.Utils.Utils.HashString(value, true);
        }
    }

	private class IndexKeyListComparer : IComparer<List<uint>>
	{
		public int Compare(List<uint>? x, List<uint>? y)
		{
			if (ReferenceEquals(x, y))
			{
				return 0;
			}
			if (x is null)
			{
				return -1;
			}
			if (y is null)
			{
				return 1;
			}
			if (x.Count < y.Count)
			{
				return -1;
			}
			if (x.Count > y.Count)
			{
				return 1;
			}
			for (int i = 0; i < x.Count; i++)
			{
				if (x[i] < y[i])
				{
					return -1;
				}
				if (x[i] > y[i])
				{
					return 1;
				}
			}
			return 0;
		}
	}

	[StructLayout(LayoutKind.Explicit)]
	private struct IntFloat
	{
		[FieldOffset(0)]
		public int Int32Value;

		[FieldOffset(0)]
		public uint UInt32Value;

		[FieldOffset(0)]
		public long Int64Value;

		[FieldOffset(0)]
		public ulong UInt64Value;

		[FieldOffset(0)]
		public float Float32Value;

		[FieldOffset(0)]
		public double Float64Value;
	}

	private class ParametricComparer : IComparer<int>
	{
		private readonly Field[] columns;

		private readonly Dictionary<uint, long[]> columnValues;

		public ParametricComparer(Field[] columns, Dictionary<uint, long[]> columnValues)
		{
			this.columns = columns ?? throw new ArgumentNullException("columns");
			this.columnValues = columnValues ?? throw new ArgumentNullException("columnValues");
		}

		public int Compare(int x, int y)
		{
			if (x == y)
			{
				return 0;
			}
			Field[] array = columns;
			foreach (Field field in array)
			{
				long num = columnValues[field.Id][x];
				long num2 = columnValues[field.Id][y];
				if (num < num2)
				{
					return -1;
				}
				if (num > num2)
				{
					return 1;
				}
			}
			return x.CompareTo(y);
		}
	}

	private static ushort GetDataTypeWidth(FieldType type)
	{
		switch (type)
		{
		case FieldType.Boolean:
			return 1;
		case FieldType.Int32:
		case FieldType.UInt32:
		case FieldType.Float32:
			return 4;
		case FieldType.Int64:
		case FieldType.UInt64:
		case FieldType.Float64:
		case FieldType.String:
		case FieldType.Pointer:
			return 8;
		default:
			throw new InvalidDataException("Unknown field type.");
		}
	}

	public void Write(Stream outputStream, NewWaveBank sampleBank, Endian endian)
	{
		ArgumentNullException.ThrowIfNull(outputStream, "outputStream");
		ArgumentNullException.ThrowIfNull(sampleBank, "sampleBank");
		List<DataSet> list = new List<DataSet>();
		Bank sampleBank2 = new Bank(sampleBank.Key, sampleBank.ProjectKey, list, Array.Empty<byte>());
		List<DataSet> list2 = sampleBank.UnknownDataSets.ToList();
		Segment[] array = (from s in sampleBank.Variations.SelectMany((Variation v) => v.Segments).Distinct()
			orderby s.SamplesOffset
			select s).ToArray();
		ChunkRef[] array2 = sampleBank.Variations.Select((Variation v) => v.ChunkRef).Distinct().ToArray();
		DataSet? dataSet = sampleBank.UnknownDataSets.FirstOrDefault((DataSet ds) => ds.Id == Djb2Hash.HashString32("SelectionParameters"));
		if (dataSet != null)
		{
			list.Add(dataSet);
			list2.Remove(dataSet);
		}
		DataSet? dataSet2 = sampleBank.UnknownDataSets.FirstOrDefault((DataSet ds) => ds.Id == Djb2Hash.HashString32("Selection"));
		if (dataSet2 != null)
		{
			list.Add(dataSet2);
			list2.Remove(dataSet2);
		}
		Field[] array3 = new Field[7];
		Field field = new Field(endian, Djb2Hash.HashString32("VariationId"), FieldType.UInt32, ColumnFormat.Constant, 0u, 0u, new ObservableCollection<object>());
		Field field2 = new Field(endian, Djb2Hash.HashString32("FirstLoopSegmentIndex"), FieldType.UInt32, ColumnFormat.Constant, 0u, 0u, new ObservableCollection<object>());
		Field field3 = new Field(endian, Djb2Hash.HashString32("LastLoopSegmentIndex"), FieldType.UInt32, ColumnFormat.Constant, 0u, 0u, new ObservableCollection<object>());
		Field field4 = new Field(endian, Djb2Hash.HashString32("MemoryChunkIndex"), FieldType.UInt32, ColumnFormat.Constant, 0u, 0u, new ObservableCollection<object>());
		Field field5 = new Field(endian, Djb2Hash.HashString32("StreamChunkIndex"), FieldType.UInt32, ColumnFormat.Constant, 0u, 0u, new ObservableCollection<object>());
		Field field6 = new Field(endian, Djb2Hash.HashString32("FirstSegmentIndex"), FieldType.UInt32, ColumnFormat.Constant, 0u, 0u, new ObservableCollection<object>());
		Field field7 = new Field(endian, Djb2Hash.HashString32("SegmentCount"), FieldType.UInt32, ColumnFormat.Constant, 0u, 0u, new ObservableCollection<object>());
		DataSet dataSet3 = sampleBank.AllDataSets.First((DataSet ds) => ds.Id == Djb2Hash.HashString32("Variations"));
		uint id = Djb2Hash.HashString32("Variations");
		uint sampleGroupId = dataSet3.SampleGroupId;
		int count = sampleBank.Variations.Count;
		Field[] indexColumns = dataSet3.IndexColumns;
		DataSetIndex[] array4 = new DataSetIndex[1];
		int num = 1;
		List<uint> list3 = new List<uint>(num);
		CollectionsMarshal.SetCount(list3, num);
		Span<uint> span = CollectionsMarshal.AsSpan(list3);
		int index = 0;
		span[index] = Djb2Hash.HashString32("VariationIndex");
		array4[0] = new DataSetIndex(list3);
		DataSet item = new DataSet(id, sampleGroupId, 0u, count, array3, indexColumns, array4);
		foreach (Variation variation in sampleBank.Variations)
		{
			field.Values.Add(variation.VariationId);
			field2.Values.Add(0);
			field3.Values.Add(0);
			field4.Values.Add(0);
			int num2 = Array.IndexOf(array2, variation.ChunkRef);
			field5.Values.Add((num2 << 1) | 1);
			int num3 = Array.IndexOf(array, variation.Segments[0]);
			field6.Values.Add(num3);
			ObservableCollection<object> values = field7.Values;
			List<Segment> segments = variation.Segments;
			values.Add(Array.IndexOf(array, segments[segments.Count - 1]) - num3 + 1);
		}
		array3[0] = field3;
		array3[1] = field4;
		array3[2] = field5;
		array3[3] = field2;
		array3[4] = field7;
		array3[5] = field6;
		array3[6] = field;
		list.Add(item);
		Field[] array5 = new Field[3];
		Field field8 = new Field(endian, Djb2Hash.HashString32("SamplesOffset"), FieldType.UInt32, ColumnFormat.Constant, 0u, 0u, new ObservableCollection<object>());
		Field field9 = new Field(endian, Djb2Hash.HashString32("SeekTableOffset"), FieldType.UInt32, ColumnFormat.Constant, 0u, 0u, new ObservableCollection<object>());
		Field field10 = new Field(endian, Djb2Hash.HashString32("Duration"), FieldType.Float32, ColumnFormat.Constant, 0u, 0u, new ObservableCollection<object>());
		DataSet dataSet4 = sampleBank.AllDataSets.First((DataSet ds) => ds.Id == Djb2Hash.HashString32("Segments"));
		uint id2 = Djb2Hash.HashString32("Segments");
		uint sampleGroupId2 = dataSet4.SampleGroupId;
		int numElems = array.Length;
		Field[] indexColumns2 = dataSet4.IndexColumns;
		DataSetIndex[] array6 = new DataSetIndex[1];
		index = 1;
		List<uint> list4 = new List<uint>(index);
		CollectionsMarshal.SetCount(list4, index);
		Span<uint> span2 = CollectionsMarshal.AsSpan(list4);
		num = 0;
		span2[num] = Djb2Hash.HashString32("SegmentIndex");
		array6[0] = new DataSetIndex(list4);
		DataSet item2 = new DataSet(id2, sampleGroupId2, 0u, numElems, array5, indexColumns2, array6);
		Segment[] array7 = array;
		foreach (Segment segment in array7)
		{
			field8.Values.Add((long)segment.SamplesOffset | 3L);
			field9.Values.Add(segment.SeekTableOffset);
			field10.Values.Add(segment.SegmentLength);
		}
		array5[0] = field10;
		array5[1] = field9;
		array5[2] = field8;
		list.Add(item2);
		Field[] array8 = new Field[2];
		Field field11 = new Field(endian, Djb2Hash.HashString32("ChunkId"), FieldType.Pointer, ColumnFormat.Constant, 0u, 0u, new ObservableCollection<object>());
		Field field12 = new Field(endian, Djb2Hash.HashString32("ChunkSize"), FieldType.UInt32, ColumnFormat.Constant, 0u, 0u, new ObservableCollection<object>());
		DataSet dataSet5 = sampleBank.AllDataSets.First((DataSet ds) => ds.Id == Djb2Hash.HashString32("Chunks"));
		uint id3 = Djb2Hash.HashString32("Chunks");
		uint sampleGroupId3 = dataSet5.SampleGroupId;
		int numElems2 = array2.Length;
		Field[] indexColumns3 = dataSet5.IndexColumns;
		DataSetIndex[] array9 = new DataSetIndex[1];
		num = 1;
		List<uint> list5 = new List<uint>(num);
		CollectionsMarshal.SetCount(list5, num);
		Span<uint> span3 = CollectionsMarshal.AsSpan(list5);
		index = 0;
		span3[index] = Djb2Hash.HashString32("ChunkIndex");
		array9[0] = new DataSetIndex(list5);
		DataSet item3 = new DataSet(id3, sampleGroupId3, 0u, numElems2, array8, indexColumns3, array9);
		ChunkRef[] array10 = array2;
		foreach (ChunkRef chunkRef in array10)
		{
			field11.Values.Add(chunkRef.ChunkId);
			field12.Values.Add(chunkRef.ChunkSize);
		}
		array8[0] = field12;
		array8[1] = field11;
		list.Add(item3);
		foreach (DataSet item4 in list2)
		{
			list.Add(item4);
		}
		Write(outputStream, sampleBank2, endian);
	}

	public void WriteUnprocessed(Stream outputStream, NewWaveBank sampleBank, Endian endian)
	{
		ArgumentNullException.ThrowIfNull(outputStream, "outputStream");
		ArgumentNullException.ThrowIfNull(sampleBank, "sampleBank");
		List<DataSet> list = new List<DataSet>(5);
		Bank sampleBank2 = new Bank(sampleBank.Key, sampleBank.ProjectKey, list, Array.Empty<byte>());
		List<DataSet> list2 = sampleBank.AllDataSets.ToList();
		DataSet? dataSet = sampleBank.UnknownDataSets.FirstOrDefault((DataSet ds) => ds.Id == Djb2Hash.HashString32("SelectionParameters"));
		if (dataSet != null)
		{
			list.Add(dataSet);
			list2.Remove(dataSet);
		}
		DataSet? dataSet2 = sampleBank.UnknownDataSets.FirstOrDefault((DataSet ds) => ds.Id == Djb2Hash.HashString32("Selection"));
		if (dataSet2 != null)
		{
			list.Add(dataSet2);
			list2.Remove(dataSet2);
		}
		DataSet? dataSet3 = sampleBank.AllDataSets.FirstOrDefault((DataSet ds) => ds.Id == Djb2Hash.HashString32("Variations"));
		if (dataSet3 != null)
		{
			list.Add(dataSet3);
			list2.Remove(dataSet3);
		}
		DataSet? dataSet4 = sampleBank.AllDataSets.FirstOrDefault((DataSet ds) => ds.Id == Djb2Hash.HashString32("Segments"));
		if (dataSet4 != null)
		{
			list.Add(dataSet4);
			list2.Remove(dataSet4);
		}
		DataSet? dataSet5 = sampleBank.AllDataSets.FirstOrDefault((DataSet ds) => ds.Id == Djb2Hash.HashString32("Chunks"));
		if (dataSet5 != null)
		{
			list.Add(dataSet5);
			list2.Remove(dataSet5);
		}
		foreach (DataSet item in list2)
		{
			list.Add(item);
		}
		Write(outputStream, sampleBank2, endian);
	}

	public void WriteUnprocessed(Stream outputStream, NewWaveBank sampleBank, Endian endian, out byte[] resMeta)
	{
		ArgumentNullException.ThrowIfNull(outputStream, "outputStream");
		ArgumentNullException.ThrowIfNull(sampleBank, "sampleBank");
		WriteUnprocessed(outputStream, sampleBank, endian);
		resMeta = new byte[16];
		FileWriter fileWriter = new FileWriter(outputStream);
		uint value = (uint)fileWriter.Position;
		fileWriter.WritePadding(8);
		uint value2 = 0u;
		int[] selectionParameterIds = sampleBank.SelectionParameterIds;
		uint num = (uint)selectionParameterIds.Length;
		if (num != 0)
		{
			value2 = (uint)fileWriter.Position;
			for (int i = 0; i < selectionParameterIds.Length; i++)
			{
				fileWriter.WriteInt32LittleEndian(selectionParameterIds[i]);
			}
		}
		uint value3 = (uint)fileWriter.Position;
		fileWriter.WritePadding(8);
		uint value4 = (uint)fileWriter.Position;
		fileWriter.WriteGuid(sampleBank.EbxInstanceGuid);
		fileWriter.WriteUInt32LittleEndian(sampleBank.SelectionDatasetSampleGroupId);
		fileWriter.WriteUInt32LittleEndian(sampleBank.Key);
		fileWriter.WriteUInt32LittleEndian(value2);
		fileWriter.WriteUInt32LittleEndian(num);
		BinaryPrimitives.WriteUInt32LittleEndian(resMeta, value4);
		BinaryPrimitives.WriteUInt32LittleEndian(resMeta.AsSpan(4), value3);
		BinaryPrimitives.WriteUInt32LittleEndian(resMeta.AsSpan(8), value);
		BinaryPrimitives.WriteUInt32LittleEndian(resMeta.AsSpan(12), 0u);
	}

	public void Write(Stream outputStream, Bank sampleBank, Endian endian)
	{
		ArgumentNullException.ThrowIfNull(outputStream, "outputStream");
		ArgumentNullException.ThrowIfNull(sampleBank, "sampleBank");
		if (sampleBank.DataSets.Count > 65535)
		{
			throw new ArgumentException("Sample bank cannot have more than ushort.MaxValue data sets.", "sampleBank");
		}
		Dictionary<long, object> referenceMap = new Dictionary<long, object>();
		Dictionary<object, long> writtenReferencesMap = new Dictionary<object, long>();
		Dictionary<Field, ColumnFormat> dictionary = new Dictionary<Field, ColumnFormat>();
		FileWriter writer;
		using (MemoryStream memoryStream = new MemoryStream())
		{
			using FileWriter fileWriter = new FileWriter(memoryStream);
			writer = new FileWriter(outputStream);
			long position = writer.Position;
			writer.WriteUInt32LittleEndian((endian == Endian.Little) ? 1701593683u : 1700938323u);
			writer.WriteUInt32(0u, endian);
			writer.Write((byte)4);
			writer.Write(0);
			writer.WriteUInt16((ushort)sampleBank.DataSets.Count, endian);
			writer.WriteUInt32(sampleBank.Key, endian);
			writer.WriteUInt32(sampleBank.ProjectKey, endian);
			writer.WriteInt32(0, endian);
			_ = writer.Position;
			WriteReference(sampleBank.DataSets);
			WriteReference("DataBlock");
			WriteReference(null);
			WriteReference(null);
			WriteReference(null);
			WriteReference(null);
			WriteReference(null);
			writer.WritePadding(16);
			long position2 = writer.Position - position;
			RecordReferencePosition(sampleBank.DataSets, position2);
			foreach (DataSet dataSet in sampleBank.DataSets)
			{
				WriteReference(dataSet);
			}
			foreach (DataSet dataSet2 in sampleBank.DataSets)
			{
				writer.WritePadding(16);
				long position3 = writer.Position;
				int num = 0;
				int num2 = 0;
				RecordReferencePosition(dataSet2, position3);
				writer.Position += 72L;
				if (dataSet2.IndexColumns.Length != 0 && dataSet2.NumElems > 0)
				{
					int[] array = new int[dataSet2.NumElems];
					for (int i = 0; i < dataSet2.NumElems; i++)
					{
						array[i] = i;
					}
					Field[] indexColumns = dataSet2.IndexColumns;
					Dictionary<uint, long[]> columnValues = indexColumns.ToDictionary((Field c) => c.Id, (Field c) => c.Values.Select((object v) => (long)ConvertValueToUInt64(v)).ToArray());
					Array.Sort(array, new ParametricComparer(indexColumns, columnValues));
					foreach (Field item12 in dataSet2.Fields.Concat(dataSet2.IndexColumns))
					{
						object[] array2 = item12.Values.ToArray();
						item12.Values.Clear();
						int[] array3 = array;
						foreach (int num4 in array3)
						{
							item12.Values.Add(array2[num4]);
						}
					}
				}
				Dictionary<Field, (int, byte[]?)> dictionary2 = new Dictionary<Field, (int, byte[]?)>();
				Dictionary<Field, (long, long)> dictionary3 = new Dictionary<Field, (long, long)>();
				Dictionary<Field, Dictionary<long, int>> dictionary4 = new Dictionary<Field, Dictionary<long, int>>();
				Dictionary<Field, byte[]> dictionary5 = new Dictionary<Field, byte[]>();
				List<(byte[]?, byte[]?)> list = new List<(byte[]?, byte[]?)>();
				Dictionary<uint, long[]> dictionary6 = new Dictionary<uint, long[]>();
				Dictionary<uint, long[]> dictionary7 = new Dictionary<uint, long[]>();
				Field[] fields = dataSet2.Fields;
				foreach (Field field in fields)
				{
					FieldType dataType = field.DataType;
					if ((uint)(dataType - 7) <= 1u)
					{
						dictionary6[field.Id] = new long[field.Values.Count];
						for (int num5 = 0; num5 < field.Values.Count; num5++)
						{
							object obj = field.Values[num5];
							if (obj is Guid value)
							{
								dictionary6[field.Id][num5] = fileWriter.Position + 1;
								fileWriter.WriteGuid(value, endian);
								continue;
							}
							if (obj is string value2)
							{
								dictionary6[field.Id][num5] = fileWriter.Position + 1;
								fileWriter.WriteNullTerminatedString(value2);
								continue;
							}
							throw new InvalidDataException($"Unable to handle column data type '{obj.GetType().Name}' as '{field.DataType}' format.");
						}
					}
					else
					{
						dictionary6[field.Id] = field.Values.Select((object v) => (long)ConvertValueToUInt64(v)).ToArray();
					}
					long[] array4 = (from v in dictionary6[field.Id].Distinct()
						orderby v
						select v).ToArray();
					dictionary7[field.Id] = array4;
					(long minValue, long maxValue, Dictionary<long, int> index, ulong usedBits) tuple = CalculateColumnMetrics(array4);
					long item = tuple.minValue;
					long item2 = tuple.maxValue;
					Dictionary<long, int> item3 = tuple.index;
					ulong item4 = tuple.usedBits;
					dictionary4[field] = item3;
					dictionary3[field] = (item, item2);
					(ushort formatParameter1, ulong formatParameter2, ColumnFormat columnFormat, byte[]? dataBlock) tuple2 = FormatColumnData(field, dictionary6[field.Id], array4, item, item2, item3, item4, endian);
					ushort item5 = tuple2.formatParameter1;
					ulong item6 = tuple2.formatParameter2;
					ColumnFormat item7 = tuple2.columnFormat;
					byte[]? item8 = tuple2.dataBlock;
					dictionary2[field] = CreateUniqueValueTree(array4, item, item2, endian);
					if (item8 != null)
					{
						dictionary5[field] = item8;
					}
					writer.WriteUInt32(field.Id, endian);
					writer.Write((byte)field.DataType);
					writer.Write((byte)item7);
					writer.WriteUInt16(item5, endian);
					writer.WriteUInt64(item6, endian);
					if (item7 <= ColumnFormat.IndexFormula)
					{
						WriteReference(null);
					}
					else
					{
						WriteReference(field);
					}
					dictionary[field] = item7;
				}
				if (dataSet2.Indexes.Length != 0)
				{
					num = (int)(writer.Position - position3);
					List<Field> list2 = new List<Field>();
					int num6 = 0;
					DataSetIndex[] indexes = dataSet2.Indexes;
					for (int num3 = 0; num3 < indexes.Length; num3++)
					{
						Field[] array5 = indexes[num3].ColumnKeys
							.Select(key => dataSet2.Get(key) ?? throw new InvalidDataException($"Missing indexed field 0x{key:X8} in dataset 0x{dataSet2.Id:X8}."))
							.ToArray();
						fields = array5;
						foreach (Field field2 in fields)
						{
							if (!dictionary4.ContainsKey(field2))
							{
								dictionary6[field2.Id] = field2.Values.Select((object v) => (long)ConvertValueToUInt64(v)).ToArray();
								long[] array6 = (from v in dictionary6[field2.Id].Distinct()
									orderby v
									select v).ToArray();
								dictionary7[field2.Id] = array6;
								(long minValue, long maxValue, Dictionary<long, int> index, ulong usedBits) tuple3 = CalculateColumnMetrics(array6);
								long item9 = tuple3.minValue;
								long item10 = tuple3.maxValue;
								Dictionary<long, int> item11 = tuple3.index;
								dictionary3[field2] = (item9, item10);
								dictionary4[field2] = item11;
								dictionary2[field2] = CreateUniqueValueTree(array6, item9, item10, endian);
							}
						}
						var (value3, array7, array8) = CreateIndexBlocks(array5, dictionary6, dictionary7, dictionary4, endian);
						list.Add((array7, array8));
						WriteReference(array7);
						WriteReference(null);
						WriteReference(array8);
						writer.WriteUInt32(value3, endian);
						writer.WriteUInt16((ushort)num6, endian);
						writer.Write(0);
						writer.Write((byte)array5.Length);
						list2.AddRange(array5);
						num6 += array5.Length;
					}
					if (list2.Count > 0)
					{
						num2 = (int)(writer.Position - position3);
						foreach (Field item13 in list2)
						{
							int num8 = dictionary7[item13.Id].Length;
							var (num9, reference) = dictionary2[item13];
							var (num10, num11) = dictionary3[item13];
							writer.WriteUInt32(item13.Id, endian);
							writer.WriteInt32((num9 << 24) | num8, endian);
							writer.WriteInt32((int)num10, endian);
							writer.WriteInt32((int)num11, endian);
							WriteReference(reference);
						}
					}
				}
				ushort num12 = (ushort)(writer.Position - position3);
				writer.Position = position3;
				writer.WriteUInt32(1146307924u, endian);
				writer.WriteUInt16(num12, endian);
				writer.WriteUInt16(0, endian);
				writer.WriteUInt32(dataSet2.Id, endian);
				writer.WriteUInt32(dataSet2.SampleGroupId, endian);
				WriteReference($"{dataSet2.Id}SampleBankOffset");
				RecordReferencePosition($"{dataSet2.Id}SampleBankOffset", 0L);
				WriteReference("DataBlock");
				WriteReference(null);
				WriteReference(null);
				WriteReference(null);
				writer.WriteInt32(dataSet2.NumElems, endian);
				writer.WriteUInt16((ushort)dataSet2.Fields.Length, endian);
				writer.WriteUInt16((ushort)dataSet2.Indexes.Length, endian);
				writer.WriteUInt16(72, endian);
				writer.WriteUInt16((ushort)num, endian);
				writer.WriteUInt16((ushort)num2, endian);
				writer.Write(0);
				writer.Write(0);
				writer.Position = position3 + num12;
				fields = dataSet2.Fields;
				foreach (Field field3 in fields)
				{
					ColumnFormat columnFormat = dictionary[field3];
					if (columnFormat != ColumnFormat.Constant && columnFormat != ColumnFormat.IndexFormula)
					{
						RecordReferencePosition(field3, writer.Position);
					}
					switch (dictionary[field3])
					{
					case ColumnFormat.ShiftedBase:
						writer.WriteBytes(dictionary5[field3]);
						break;
					case ColumnFormat.LookupTable:
						writer.WriteBytes(dictionary5[field3]);
						break;
					case ColumnFormat.Raw:
					{
						long[] array9 = dictionary6[field3.Id];
						foreach (long value4 in array9)
						{
							writer.WriteUInt64((ulong)value4, endian);
						}
						break;
					}
					}
				}
				foreach (KeyValuePair<Field, (int, byte[]?)> item14 in dictionary2)
				{
					(int uniqueValueTreeWidth, byte[]? uniqueValueTree) uniqueValueTree;
					uniqueValueTree = item14.Value;
					if (uniqueValueTree.uniqueValueTree != null && referenceMap.Any<KeyValuePair<long, object>>((KeyValuePair<long, object> kvp) => kvp.Value == uniqueValueTree.uniqueValueTree))
					{
						RecordReferencePosition(uniqueValueTree.uniqueValueTree, writer.Position);
						writer.WriteBytes(uniqueValueTree.uniqueValueTree);
					}
				}
				foreach (var (array10, array11) in list)
				{
					if (array10 != null)
					{
						RecordReferencePosition(array10, writer.Position);
						writer.WriteBytes(array10);
					}
					if (array11 != null)
					{
						RecordReferencePosition(array11, writer.Position);
						writer.WriteBytes(array11);
					}
				}
			}
			if (memoryStream.Length > 0)
			{
				writer.WritePadding(16);
				RecordReferencePosition("DataBlock", writer.Position);
				memoryStream.Position = 0L;
				memoryStream.CopyTo(writer.BaseStream);
			}
			else
			{
				long[] array9 = (from kvp in referenceMap
					where kvp.Value is string text && text == "DataBlock"
					select kvp.Key).ToArray();
				foreach (long key in array9)
				{
					referenceMap.Remove(key);
				}
			}
			long position4 = writer.Position;
			ResolveReferences();
			writer.Position = position + 4;
			writer.WriteUInt32((uint)(position4 - position), endian);
			writer.Position = position4;
		}
		void RecordReferencePosition(object key2, long value5)
		{
			writtenReferencesMap.Add(key2, value5);
		}
		void ResolveReferences()
		{
			long[] array12 = referenceMap.Keys.OrderBy((long v) => v).ToArray();
			for (int num13 = 0; num13 < array12.Length; num13++)
			{
				long num14 = array12[num13];
				long num15 = -1L;
				if (num13 + 1 < array12.Length)
				{
					num15 = array12[num13 + 1];
				}
				object key2 = referenceMap[num14];
				long num16 = writtenReferencesMap[key2];
				writer.Position = num14;
				writer.WriteUInt32((uint)num16, endian);
				writer.WriteUInt32((uint)num15, endian);
			}
		}
		void WriteReference(object? obj2)
		{
			if (obj2 != null)
			{
				referenceMap[writer.Position] = obj2;
			}
			writer.WriteInt64(0L, endian);
		}
	}

	private static (long minValue, long maxValue, Dictionary<long, int> index, ulong usedBits) CalculateColumnMetrics(long[] uniqueValues)
	{
		long num = long.MaxValue;
		long num2 = long.MinValue;
		ulong num3 = 0uL;
		Dictionary<long, int> dictionary = new Dictionary<long, int>();
		for (int i = 0; i < uniqueValues.Length; i++)
		{
			long num4 = uniqueValues[i];
			dictionary[num4] = i;
			num3 |= (ulong)num4;
			if (num4 < num)
			{
				num = num4;
			}
			if (num4 > num2)
			{
				num2 = num4;
			}
		}
		return (minValue: num, maxValue: num2, index: dictionary, usedBits: num3);
	}

	private static (ushort formatParameter1, ulong formatParameter2, ColumnFormat columnFormat, byte[]? dataBlock) FormatColumnData(Field column, long[] values, long[] uniqueValues, long minValue, long maxValue, Dictionary<long, int> valueToIndexDictionary, ulong usedBits, Endian endian)
	{
		int num = values.Length;
		int dataTypeWidth;
		if (column.OriginalFormat == ColumnFormat.Raw)
		{
			dataTypeWidth = GetDataTypeWidth(column.DataType);
			return (formatParameter1: (ushort)dataTypeWidth, formatParameter2: 0uL, columnFormat: ColumnFormat.Raw, dataBlock: null);
		}
		if (num == 0)
		{
			return (formatParameter1: 0, formatParameter2: 0uL, columnFormat: ColumnFormat.Constant, dataBlock: null);
		}
		if (uniqueValues.Length == 1)
		{
			return (formatParameter1: 0, formatParameter2: (ulong)uniqueValues[0], columnFormat: ColumnFormat.Constant, dataBlock: null);
		}
		if (uniqueValues.Length == num)
		{
			long num2 = values[1] - values[0];
			bool flag = true;
			for (int i = 1; i < values.Length - 1; i++)
			{
				if (values[i + 1] - values[i] != num2)
				{
					flag = false;
					break;
				}
			}
			if (flag && num2 >= -32768 && num2 <= 32767)
			{
				return (formatParameter1: (ushort)num2, formatParameter2: (ulong)values[0], columnFormat: ColumnFormat.IndexFormula, dataBlock: null);
			}
		}
		int num3 = Math.Max(GetSignedWidth(minValue), GetSignedWidth(maxValue));
		int shiftableBits = GetShiftableBits(usedBits);
		int num4 = GetUnsignedWidth((ulong)(maxValue - minValue));
		int num5 = GetUnsignedWidth((ulong)((maxValue - minValue >> shiftableBits) + 1));
		if (num4 == 3)
		{
			num4 = 4;
		}
		if (num5 == 3)
		{
			num5 = 4;
		}
		int num6 = 0;
		dataTypeWidth = num4;
		if (num5 < num4)
		{
			num6 = shiftableBits;
			dataTypeWidth = num5;
		}
		int num7 = num * dataTypeWidth;
		if (uniqueValues.Length < 256)
		{
			int num8 = uniqueValues.Length * num3;
			int widthBits = GetWidthBits((uint)(uniqueValues.Length - 1));
			widthBits = GetClosestPow2(widthBits);
			int num9 = ConvertBitsToByte(widthBits * num);
			int num10 = num8 + num9;
			if (column.OriginalFormat != ColumnFormat.ShiftedBase && (column.OriginalFormat == ColumnFormat.LookupTable || num10 < num7))
			{
				byte[] array = new byte[num9];
				int num11 = 8 / widthBits;
				for (int j = 0; j < num9; j++)
				{
					for (int k = 0; k < num11; k++)
					{
						if (j * num11 + k < num)
						{
							int num12 = valueToIndexDictionary[values[j * num11 + k]];
							array[j] |= (byte)(num12 << k * widthBits);
						}
					}
				}
				byte[] array2 = new byte[uniqueValues.Length * num3 + array.Length];
				for (int l = 0; l < uniqueValues.Length; l++)
				{
					long num13 = uniqueValues[l];
					int num14 = l * num3;
					Span<byte> destination = array2.AsSpan(num14, num3);
					switch (num3)
					{
					case 1:
						array2[num14] = (byte)num13;
						break;
					case 2:
						if (endian == Endian.Little)
						{
							BinaryPrimitives.WriteInt16LittleEndian(destination, (short)num13);
						}
						else
						{
							BinaryPrimitives.WriteInt16BigEndian(destination, (short)num13);
						}
						break;
					case 4:
						if (endian == Endian.Little)
						{
							BinaryPrimitives.WriteInt32LittleEndian(destination, (int)num13);
						}
						else
						{
							BinaryPrimitives.WriteInt32BigEndian(destination, (int)num13);
						}
						break;
					case 8:
						if (endian == Endian.Little)
						{
							BinaryPrimitives.WriteInt64LittleEndian(destination, num13);
						}
						else
						{
							BinaryPrimitives.WriteInt64BigEndian(destination, num13);
						}
						break;
					default:
						throw new InvalidDataException("Unhandled data width.");
					}
				}
				Buffer.BlockCopy(array, 0, array2, uniqueValues.Length * num3, array.Length);
				return (formatParameter1: (ushort)((num3 << 8) | widthBits), formatParameter2: (ulong)uniqueValues.Length, columnFormat: ColumnFormat.LookupTable, dataBlock: array2);
			}
		}
		byte[] array3 = new byte[num * dataTypeWidth];
		for (int m = 0; m < num; m++)
		{
			long num15 = values[m];
			num15 = num15 - minValue >> num6;
			int num16 = m * dataTypeWidth;
			Span<byte> destination2 = array3.AsSpan(num16, dataTypeWidth);
			switch (dataTypeWidth)
			{
			case 1:
				array3[num16] = (byte)num15;
				break;
			case 2:
				if (endian == Endian.Little)
				{
					BinaryPrimitives.WriteUInt16LittleEndian(destination2, (ushort)num15);
				}
				else
				{
					BinaryPrimitives.WriteUInt16BigEndian(destination2, (ushort)num15);
				}
				break;
			case 4:
				if (endian == Endian.Little)
				{
					BinaryPrimitives.WriteUInt32LittleEndian(destination2, (uint)num15);
				}
				else
				{
					BinaryPrimitives.WriteUInt32BigEndian(destination2, (uint)num15);
				}
				break;
			case 8:
				if (endian == Endian.Little)
				{
					BinaryPrimitives.WriteUInt64LittleEndian(destination2, (ulong)num15);
				}
				else
				{
					BinaryPrimitives.WriteUInt64BigEndian(destination2, (ulong)num15);
				}
				break;
			default:
				throw new InvalidDataException("Unhandled data width.");
			}
		}
		return (formatParameter1: (ushort)((dataTypeWidth << 8) | num6), formatParameter2: (ulong)minValue, columnFormat: ColumnFormat.ShiftedBase, dataBlock: array3);
	}

	private (uint matchIndexFormat, byte[]? matchIndexBlock, byte[]? rowLookupBlock) CreateIndexBlocks(Field[] columns, Dictionary<uint, long[]> columnValues, Dictionary<uint, long[]> columnUniqueValues, Dictionary<Field, Dictionary<long, int>> valueToIndexDictionary, Endian endian)
	{
		int num = 1;
		foreach (Field field in columns)
		{
			long[] array = columnUniqueValues[field.Id];
			num *= array.Length;
		}
		int num2 = columnValues[columns[0].Id].Length;
		int num3 = columns.Length;
		int[] array2 = new int[num3];
		array2[num3 - 1] = 1;
		for (int num4 = num3 - 2; num4 >= 0; num4--)
		{
			array2[num4] = array2[num4 + 1] * columnUniqueValues[columns[num4 + 1].Id].Length;
		}
		int[] array3 = new int[num2];
		for (int j = 0; j < num2; j++)
		{
			array3[j] = j;
		}
		Array.Sort(array3, new ParametricComparer(columns, columnValues));
		int[] array4 = new int[num];
		for (int k = 0; k < num2; k++)
		{
			int num5 = array3[k];
			int num6 = 0;
			for (int l = 0; l < num3; l++)
			{
				long key = columnValues[columns[l].Id][num5];
				int num7 = valueToIndexDictionary[columns[l]][key];
				num6 += num7 * array2[l];
			}
			array4[num6]++;
		}
		int[] array5 = new int[num + 1];
		int num8 = 0;
		for (int m = 0; m < num; m++)
		{
			array5[m] = num8;
			num8 += array4[m];
		}
		array5[num] = num8;
		if (num2 > 0)
		{
			(uint matchIndexFormat, byte[]? matchIndexBlock) tuple = CreateMatchIndexBlock(array5, endian);
			uint item = tuple.matchIndexFormat;
			byte[]? item2 = tuple.matchIndexBlock;
			byte[]? item3 = CreateRowLookupBlock(array3, endian);
			return (matchIndexFormat: item, matchIndexBlock: item2, rowLookupBlock: item3);
		}
		return (matchIndexFormat: 0u, matchIndexBlock: null, rowLookupBlock: null);
	}

	private static (int uniqueValueTreeWidth, byte[]? uniqueValueTree) CreateUniqueValueTree(long[] uniqueValues, long minValue, long maxValue, Endian endian)
	{
		bool flag = true;
		for (int i = 0; i < uniqueValues.Length; i++)
		{
			if (uniqueValues[i] != uniqueValues[0] + i)
			{
				flag = false;
				break;
			}
		}
		if (flag)
		{
			return (uniqueValueTreeWidth: 0, uniqueValueTree: null);
		}
		int unsignedWidth = GetUnsignedWidth((ulong)(maxValue - minValue));
		int num = uniqueValues.Length;
		long[] array = uniqueValues.Select((long v) => v - minValue).ToArray();
		byte[] array2 = new byte[num * unsignedWidth];
		for (int num2 = 0; num2 < num; num2++)
		{
			long num3 = array[num2];
			int num4 = num2 * unsignedWidth;
			Span<byte> destination = array2.AsSpan(num4, unsignedWidth);
			switch (unsignedWidth)
			{
			case 1:
				array2[num4] = (byte)num3;
				break;
			case 2:
				if (endian == Endian.Little)
				{
					BinaryPrimitives.WriteUInt16LittleEndian(destination, (ushort)num3);
				}
				else
				{
					BinaryPrimitives.WriteUInt16BigEndian(destination, (ushort)num3);
				}
				break;
			case 4:
				if (endian == Endian.Little)
				{
					BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)num3);
				}
				else
				{
					BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)num3);
				}
				break;
			case 8:
				if (endian == Endian.Little)
				{
					BinaryPrimitives.WriteUInt64LittleEndian(destination, (ulong)num3);
				}
				else
				{
					BinaryPrimitives.WriteUInt64BigEndian(destination, (ulong)num3);
				}
				break;
			default:
				throw new InvalidDataException("Unhandled data width.");
			}
		}
		return (uniqueValueTreeWidth: unsignedWidth, uniqueValueTree: array2);
	}

	private static (uint matchIndexFormat, byte[]? matchIndexBlock) CreateMatchIndexBlock(int[] matchIndexArray, Endian endian)
	{
		long num = matchIndexArray[1] - matchIndexArray[0];
		bool flag = true;
		for (int i = 0; i < matchIndexArray.Length - 1; i++)
		{
			if (matchIndexArray[i + 1] - matchIndexArray[i] != num)
			{
				flag = false;
				break;
			}
		}
		uint num2;
		if (flag)
		{
			num2 = 0u;
			uint num3 = (uint)(num & 0xFFFFFF);
			return (matchIndexFormat: num2 | num3, matchIndexBlock: null);
		}
		byte b = (byte)GetUnsignedWidth((uint)matchIndexArray[^1]);
		num2 = 0u;
		uint num4 = (uint)(b & 0xFFFFFF);
		byte[] array = new byte[matchIndexArray.Length * b];
		for (int j = 0; j < matchIndexArray.Length; j++)
		{
			int num5 = matchIndexArray[j];
			int num6 = j * b;
			Span<byte> destination = array.AsSpan(num6, b);
			switch (b)
			{
			case 1:
				array[num6] = (byte)num5;
				break;
			case 2:
				if (endian == Endian.Little)
				{
					BinaryPrimitives.WriteUInt16LittleEndian(destination, (ushort)num5);
				}
				else
				{
					BinaryPrimitives.WriteUInt16BigEndian(destination, (ushort)num5);
				}
				break;
			case 4:
				if (endian == Endian.Little)
				{
					BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)num5);
				}
				else
				{
					BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)num5);
				}
				break;
			case 8:
				if (endian == Endian.Little)
				{
					BinaryPrimitives.WriteUInt64LittleEndian(destination, (ulong)num5);
				}
				else
				{
					BinaryPrimitives.WriteUInt64BigEndian(destination, (ulong)num5);
				}
				break;
			default:
				throw new InvalidDataException("Unhandled data width.");
			}
		}
		return (matchIndexFormat: num2 | num4, matchIndexBlock: array);
	}

	private static byte[]? CreateRowLookupBlock(int[] rowLookupArray, Endian endian)
	{
		bool flag = true;
		for (int i = 0; i < rowLookupArray.Length; i++)
		{
			if (rowLookupArray[i] != i)
			{
				flag = false;
				break;
			}
		}
		if (flag)
		{
			return null;
		}
		int unsignedWidth = GetUnsignedWidth((uint)rowLookupArray.Length);
		byte[] array = new byte[rowLookupArray.Length * unsignedWidth];
		for (int j = 0; j < rowLookupArray.Length; j++)
		{
			int num = rowLookupArray[j];
			int num2 = j * unsignedWidth;
			Span<byte> destination = array.AsSpan(num2, unsignedWidth);
			switch (unsignedWidth)
			{
			case 1:
				array[num2] = (byte)num;
				break;
			case 2:
				if (endian == Endian.Little)
				{
					BinaryPrimitives.WriteUInt16LittleEndian(destination, (ushort)num);
				}
				else
				{
					BinaryPrimitives.WriteUInt16BigEndian(destination, (ushort)num);
				}
				break;
			case 4:
				if (endian == Endian.Little)
				{
					BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)num);
				}
				else
				{
					BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)num);
				}
				break;
			case 8:
				if (endian == Endian.Little)
				{
					BinaryPrimitives.WriteUInt64LittleEndian(destination, (ulong)num);
				}
				else
				{
					BinaryPrimitives.WriteUInt64BigEndian(destination, (ulong)num);
				}
				break;
			default:
				throw new InvalidDataException("Unhandled data width.");
			}
		}
		return array;
	}

	private static int ConvertBitsToByte(int bits)
	{
		return (bits + 7) / 8;
	}

	private static int GetClosestPow2(int value)
	{
		int i;
		for (i = 0; 1 << i < value && i < 31; i++)
		{
		}
		return 1 << i;
	}

	private static int GetWidthBits(uint value)
	{
		int i;
		for (i = 0; value >> i != 0; i++)
		{
		}
		return i;
	}

	private static int GetShiftableBits(ulong value)
	{
		int i = 1;
		for (ulong num = ulong.MaxValue; (value & (num << i)) == value; i++)
		{
		}
		return i - 1;
	}

	private static int GetSignedWidth(long value)
	{
		if (value != (int)value)
		{
			return 8;
		}
		if (value != (short)value)
		{
			return 4;
		}
		if (value == (byte)value)
		{
			return 1;
		}
		return 2;
	}

	internal static int GetUnsignedWidth(ulong value)
	{
		if (value <= 255)
		{
			return 1;
		}
		if (value <= 65535)
		{
			return 2;
		}
		if (value > uint.MaxValue)
		{
			return 8;
		}
		return 4;
	}

	private static ulong ConvertValueToUInt64(object value)
	{
		IntFloat intFloat;
		if (!(value is float float32Value))
		{
			if (!(value is double float64Value))
			{
				if (!(value is int int32Value))
				{
					if (!(value is uint uInt32Value))
					{
						if (!(value is long int64Value))
						{
							if (value is ulong result)
							{
								return result;
							}
							throw new InvalidDataException("Unknown data type.");
						}
						intFloat = new IntFloat
						{
							Int64Value = int64Value
						};
						return intFloat.UInt64Value;
					}
					intFloat = new IntFloat
					{
						UInt32Value = uInt32Value
					};
					return intFloat.UInt64Value;
				}
				intFloat = new IntFloat
				{
					Int32Value = int32Value
				};
				return intFloat.UInt64Value;
			}
			intFloat = new IntFloat
			{
				Float64Value = float64Value
			};
			return intFloat.UInt64Value;
		}
		intFloat = new IntFloat
		{
			Float32Value = float32Value
		};
		return intFloat.UInt64Value;
	}
}
