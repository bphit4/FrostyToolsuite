#nullable disable
#pragma warning disable CS8632
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Frosty.Sdk.IO;
using Frosty.Sdk.Managers;
using Frosty.Sdk.Managers.Entries;
using Frosty.Sdk.Utils;
using FrostyEditor.Managers.Sound.Tools;
using FrostyEditor.Models.Audio;

namespace FrostyEditor.Managers.Sound;

public sealed class NewWaveAssetHarmonySampleBankParser
{
	[Flags]
	public enum NewWaveSegmentOffsetFlags
	{
		ValidOffset = 1,
		IsStreaming = 2,
		Mask = 3
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

	private const int DataSetHeaderAlignment = 16;

	public const int DataSetHeaderSize = 72;

	public const uint DataSetHeaderTag = 1146307924u;

	public async Task ExportAssetAsync(Stream stream, string outputFilePath, bool rawSpsExport, bool extendedFormat = false, byte[]? resMeta = null, CancellationToken cancellationToken = default(CancellationToken))
	{
		ArgumentNullException.ThrowIfNull(stream, "stream");
		ArgumentNullException.ThrowIfNull(outputFilePath, "outputFilePath");
		List<Variation> variations = ParseNewWaveAssetBank(stream, extendedFormat, resMeta).Variations;
		int totalFiles = variations.Sum((Variation variation2) => variation2.Segments.Count);
		if (totalFiles == 0)
		{
			throw new InvalidDataException("The sound bank does not contain any segments.");
		}
		foreach (IGrouping<Guid, Variation> group in from variation2 in variations
			group variation2 by variation2.ChunkRef.ChunkId)
		{
			ChunkAssetEntry chunkEntry = AssetManager.GetChunkAssetEntry(group.Key);
			if (chunkEntry == null)
			{
				continue;
			}
			using MemoryStream chunkStream = OpenChunkStream(chunkEntry);
			using DataStream chunkReader = new DataStream(chunkStream);
			foreach (Variation variation in group)
			{
				foreach (Segment segment in variation.Segments)
				{
					cancellationToken.ThrowIfCancellationRequested();
					string finalOutputPath = ((totalFiles > 1) ? Path.Combine(Path.GetDirectoryName(outputFilePath) ?? string.Empty, $"{Path.GetFileNameWithoutExtension(outputFilePath)}_{segment.Index}_{variation.Index}{(rawSpsExport ? ".sps" : ".wav")}") : outputFilePath);
					await ExportSegmentInternalAsync(chunkReader, segment, finalOutputPath, rawSpsExport, cancellationToken);
				}
			}
		}
	}

	public async Task ExportSegmentAsync(Segment segment, Guid chunkId, string outputFilePath, bool rawSpsExport, bool originalData = false, CancellationToken cancellationToken = default(CancellationToken))
	{
		ArgumentNullException.ThrowIfNull(segment, "segment");
		ArgumentNullException.ThrowIfNull(outputFilePath, "outputFilePath");
		ChunkAssetEntry chunkEntry = AssetManager.GetChunkAssetEntry(chunkId);
		if (chunkEntry == null)
		{
			throw new InvalidOperationException($"Unable to resolve chunk {chunkId} for segment export.");
		}
		using MemoryStream chunkStream = OpenChunkStream(chunkEntry, originalData);
		using DataStream chunkReader = new DataStream(chunkStream);
		await ExportSegmentInternalAsync(chunkReader, segment, outputFilePath, rawSpsExport, cancellationToken);
	}

	public (byte[] SpsData, byte[]? SeekTableData) ExtractSegmentPayload(Segment segment, Guid chunkId, bool originalData = false)
	{
		ArgumentNullException.ThrowIfNull(segment, "segment");
		ChunkAssetEntry chunkAssetEntry = AssetManager.GetChunkAssetEntry(chunkId);
		if (chunkAssetEntry == null)
		{
			throw new InvalidOperationException($"Unable to resolve chunk {chunkId} for segment extraction.");
		}
		using MemoryStream inStream = OpenChunkStream(chunkAssetEntry, originalData);
		using DataStream chunkReader = new DataStream(inStream);
		return ExtractSegmentPayload(chunkReader, segment);
	}

	private static MemoryStream OpenChunkStream(ChunkAssetEntry chunkEntry, bool originalData = false)
	{
		byte[] buffer = (originalData ? AssetManager.GetRawAsset(chunkEntry).ToArray() : AssetManager.GetAsset(chunkEntry).ToArray());
		return new MemoryStream(buffer, writable: false);
	}

	public NewWaveBank ParseNewWaveAssetBank(Stream stream, bool extendedFormat, byte[]? resMeta)
	{
		ArgumentNullException.ThrowIfNull(stream, "stream");
		if (!extendedFormat || resMeta == null || resMeta.Length < 16)
		{
			return ParseNewWaveAssetBank(stream);
		}
		uint num = BinaryPrimitives.ReadUInt32LittleEndian(resMeta);
		uint num2 = BinaryPrimitives.ReadUInt32LittleEndian(resMeta.AsSpan(4));
		uint num3 = BinaryPrimitives.ReadUInt32LittleEndian(resMeta.AsSpan(8));
		using MemoryStream memoryStream = new MemoryStream();
		stream.CopyTo(memoryStream);
		memoryStream.Position = 0L;
		if (num3 == 0)
		{
			using (DataStream dataStream = new DataStream(memoryStream))
			{
				dataStream.Position = dataStream.Length - 8;
				uint num4 = dataStream.ReadUInt32();
				uint num5 = dataStream.ReadUInt32();
				dataStream.Position = num;
				Guid ebxInstanceGuid = dataStream.ReadGuid();
				uint selectionDatasetSampleGroupId = dataStream.ReadUInt32();
				uint num6 = dataStream.ReadUInt32();
				int[] array = Array.Empty<int>();
				if (num5 != 0 && num4 != 0)
				{
					array = new int[num5];
					dataStream.Position = num4;
					for (int i = 0; i < num5; i++)
					{
						array[i] = dataStream.ReadInt32();
					}
				}
				return new NewWaveBank(num6, Hash("FB.A"), new List<Variation>(), new List<DataSet>(), new List<DataSet>())
				{
					EbxInstanceGuid = ebxInstanceGuid,
					SelectionDatasetSampleGroupId = selectionDatasetSampleGroupId,
					SelectionParameterIds = array,
					SelectionParametersOnly = true,
					TrailerBankKey = num6
				};
			}
		}
		NewWaveBank newWaveBank = ParseNewWaveAssetBank(memoryStream);
		using (DataStream dataStream2 = new DataStream(memoryStream))
		{
			dataStream2.Position = dataStream2.Length - 8;
			uint num7 = dataStream2.ReadUInt32();
			uint num8 = dataStream2.ReadUInt32();
			if (num8 != 0 && num7 != 0)
			{
				dataStream2.Position = num7;
				int[] array2 = new int[num8];
				for (int j = 0; j < num8; j++)
				{
					array2[j] = dataStream2.ReadInt32();
				}
				newWaveBank.SelectionParameterIds = array2;
			}
			if (num != 0)
			{
				dataStream2.Position = num;
				newWaveBank.EbxInstanceGuid = dataStream2.ReadGuid();
				newWaveBank.SelectionDatasetSampleGroupId = dataStream2.ReadUInt32();
				newWaveBank.TrailerBankKey = dataStream2.ReadUInt32();
			}
		}
		newWaveBank.SelectionParametersOnly = false;
		return newWaveBank;
	}

	public NewWaveBank ParseNewWaveAssetBank(Stream stream)
	{
		ArgumentNullException.ThrowIfNull(stream, "stream");
		Bank bank = Parse(stream);
		using MemoryStream inStream = new MemoryStream(bank.DataBlock, writable: false);
		using DataStream dataStream = new DataStream(inStream);
		List<DataSet> list = bank.DataSets.ToList();
		List<Variation> list2 = new List<Variation>();
		DataSet dataSet = ResolveDataSet(bank, "Chunks", "ChunkId", "ChunkIndex", "ChunkSize");
		if (dataSet != null)
		{
			Field field = dataSet.Get("ChunkId") ?? throw new InvalidDataException("Missing ChunkId field.");
			for (int i = 0; i < dataSet.NumElems; i++)
			{
				long position = (long)Convert.ChangeType(field.Values[i], typeof(long)) - 1;
				dataStream.Position = position;
				field.Values[i] = dataStream.ReadGuid();
			}
		}
		foreach (DataSet dataSet6 in bank.DataSets)
		{
			foreach (Field item in from c in dataSet6.Fields.Concat(dataSet6.IndexColumns)
				where c.DataType == FieldType.String
				select c)
			{
				for (int num = 0; num < item.Values.Count; num++)
				{
					item.Values[num] = ReadString(dataStream, item, num);
				}
			}
		}
		DataSet dataSet2 = ResolveDataSet(bank, "SelectionParameters");
		if (dataSet2 != null)
		{
			for (int num2 = 0; num2 < dataSet2.NumElems; num2++)
			{
				_ = dataSet2.Get("ParameterIndex")?.Values[num2];
				_ = dataSet2.Get("ParameterId")?.Values[num2];
			}
		}
		DataSet dataSet3 = ResolveDataSet(bank, "Selection");
		Dictionary<uint, Dictionary<uint, object>> dictionary = new Dictionary<uint, Dictionary<uint, object>>();
		int i2 = 0;
		while (dataSet3 != null && i2 < dataSet3.NumElems)
		{
			uint key = (uint)Convert.ChangeType(dataSet3.Get("VariationId").Values[i2], typeof(uint));
			dictionary[key] = dataSet3.Fields.Concat(dataSet3.IndexColumns).ToDictionary((Field c) => c.Id, (Field c) => c.Values[i2]);
			i2++;
		}
		Dictionary<ulong, ChunkRef> dictionary2 = new Dictionary<ulong, ChunkRef>(dataSet?.NumElems ?? 0);
		if (dataSet != null)
		{
			list.Remove(dataSet);
			Field field2 = dataSet.Get("ChunkIndex") ?? throw new InvalidDataException("Missing ChunkIndex field.");
			Field field3 = dataSet.Get("ChunkSize") ?? throw new InvalidDataException("Missing ChunkSize field.");
			Field field4 = dataSet.Get("ChunkId") ?? throw new InvalidDataException("Missing ChunkId field.");
			for (int num3 = 0; num3 < dataSet.NumElems; num3++)
			{
				ulong num4 = (ulong)field2.Values[num3];
				Guid chunkId = (Guid)field4.Values[num3];
				long chunkSize = (long)Convert.ChangeType(field3.Values[num3], typeof(long));
				dictionary2[num4] = new ChunkRef(num4, chunkId, chunkSize);
			}
		}
		List<Segment> list3 = new List<Segment>();
		DataSet dataSet4 = ResolveDataSet(bank, "Segments", "SegmentIndex", "SamplesOffset", "Duration");
		if (dataSet4 != null)
		{
			list.Remove(dataSet4);
			for (int num5 = 0; num5 < dataSet4.NumElems; num5++)
			{
				int index = (int)(ulong)dataSet4.Get("SegmentIndex").Values[num5];
				uint samplesOffset = (uint)Convert.ChangeType(dataSet4.Get("SamplesOffset").Values[num5], typeof(uint));
				uint seekTableOffset = (uint)Convert.ChangeType(dataSet4.Get("SeekTableOffset").Values[num5], typeof(uint));
				float segmentLength = (float)Convert.ChangeType(dataSet4.Get("Duration").Values[num5], typeof(float));
				list3.Add(new Segment(index, samplesOffset, seekTableOffset, segmentLength));
			}
		}
		DataSet dataSet5 = ResolveDataSet(bank, "Variations", "VariationId", "FirstSegmentIndex", "SegmentCount");
		if (dataSet5 != null)
		{
			list.Remove(dataSet5);
			for (int num6 = 0; num6 < dataSet5.NumElems; num6++)
			{
				uint num7 = (uint)Convert.ChangeType(dataSet5.Get("VariationId").Values[num6], typeof(uint));
				long num8 = TryGetFieldLong(dataSet5, "FirstLoopSegmentIndex", num6);
				long num9 = TryGetFieldLong(dataSet5, "LastLoopSegmentIndex", num6);
				long memoryChunkIndex = TryGetFieldLong(dataSet5, "MemoryChunkIndex", num6);
				long streamChunkIndex = TryGetFieldLong(dataSet5, "StreamChunkIndex", num6);
				long num10 = (long)Convert.ChangeType(dataSet5.Get("FirstSegmentIndex").Values[num6], typeof(long));
				long num11 = (long)Convert.ChangeType(dataSet5.Get("SegmentCount").Values[num6], typeof(long));
				if (num11 > 0 && num10 >= 0 && num10 + num11 <= list3.Count)
				{
					List<Segment> range = list3.GetRange((int)num10, (int)num11);
					ulong? num12 = ResolveVariationChunkIndex(memoryChunkIndex, streamChunkIndex, dictionary2);
					if (num12.HasValue)
					{
						ChunkRef value;
						ChunkRef chunkRef = (dictionary2.TryGetValue(num12.Value, out value) ? value : new ChunkRef(num12.Value, Guid.Empty, 0L));
						Dictionary<uint, object> value2;
						Dictionary<uint, object> values = ((dictionary.TryGetValue(num7, out value2) && value2 != null) ? value2 : new Dictionary<uint, object>());
						list2.Add(new Variation(num6, num7, chunkRef, range, values));
					}
				}
			}
		}
		return new NewWaveBank(bank.Key, bank.ProjectKey, list2, bank.DataSets.ToList(), list);
	}

	private static Bank Parse(Stream stream)
	{
		using MemoryStream memoryStream = new MemoryStream();
		stream.CopyTo(memoryStream);
		memoryStream.Position = 0L;
		using DataStream dataStream = new DataStream(memoryStream);
		uint num = dataStream.ReadUInt32();
		if (1 == 0)
		{
		}
		Endian endian = num switch
		{
			1701593683u => Endian.Little, 
			1700938323u => Endian.Big, 
			_ => throw new InvalidDataException($"Wrong format ID for harmony sample bank. Got '{num}'."), 
		};
		if (1 == 0)
		{
		}
		Endian endian2 = endian;
		dataStream.ReadUInt32(endian2);
		byte alignment = (byte)(1 << (int)dataStream.ReadByte());
		byte b = dataStream.ReadByte();
		if (b != 0)
		{
			throw new InvalidDataException($"Unknown sample bank version '{b}'.");
		}
		ushort num2 = dataStream.ReadUInt16(endian2);
		uint key = dataStream.ReadUInt32(endian2);
		uint projectKey = dataStream.ReadUInt32(endian2);
		dataStream.ReadInt32(endian2);
		uint item = ReadReference(dataStream, endian2).BlockOffset;
		uint item2 = ReadReference(dataStream, endian2).BlockOffset;
		ReadReference(dataStream, endian2);
		ReadReference(dataStream, endian2);
		ReadReference(dataStream, endian2);
		ReadReference(dataStream, endian2);
		ReadReference(dataStream, endian2);
		dataStream.Pad(alignment);
		if (dataStream.Position != item)
		{
			throw new InvalidDataException("Expected to be at the data set entry offset.");
		}
		List<DataSet> list = new List<DataSet>(num2);
		for (int i = 0; i < num2; i++)
		{
			dataStream.Position = item + (long)i * 8L;
			uint item3 = ReadReference(dataStream, endian2).BlockOffset;
			list.Add(ReadDataSet(dataStream, item3, endian2));
		}
		byte[] dataBlock = Array.Empty<byte>();
		if (item2 != 0)
		{
			dataStream.Position = item2;
			dataBlock = dataStream.ReadBytes((int)(dataStream.Length - dataStream.Position));
		}
		return new Bank(key, projectKey, list, dataBlock);
	}

	private static (uint BlockOffset, uint NextReferenceOffset) ReadReference(DataStream reader, Endian endian)
	{
		return (BlockOffset: reader.ReadUInt32(endian), NextReferenceOffset: reader.ReadUInt32(endian));
	}

	private static DataSet ReadDataSet(DataStream reader, uint dsetOffset, Endian endian)
	{
		reader.Pad(16);
		reader.Position = dsetOffset;
		if (reader.ReadUInt32(endian) != 1146307924)
		{
			throw new InvalidDataException("Wrong magic for dataset.");
		}
		reader.ReadUInt16(endian);
		reader.ReadUInt16(endian);
		uint id = reader.ReadUInt32(endian);
		uint sampleGroupId = reader.ReadUInt32(endian);
		ReadReference(reader, endian);
		uint item = ReadReference(reader, endian).BlockOffset;
		ReadReference(reader, endian);
		ReadReference(reader, endian);
		ReadReference(reader, endian);
		int numElems = reader.ReadInt32(endian);
		ushort num = reader.ReadUInt16(endian);
		ushort indexCount = reader.ReadUInt16(endian);
		ushort num2 = reader.ReadUInt16(endian);
		ushort indexEntryOffset = reader.ReadUInt16(endian);
		ushort indexParameterOffset = reader.ReadUInt16(endian);
		reader.ReadByte();
		reader.ReadByte();
		if (reader.Position - dsetOffset != 72)
		{
			throw new InvalidDataException("The data set header was not the expected size.");
		}
		if (reader.Position != dsetOffset + num2)
		{
			throw new InvalidDataException("Expected to be at the offset for the column entry array.");
		}
		Field[] array = new Field[num];
		List<Field> extraFields = new List<Field>();
		List<DataSetIndex> indexes = new List<DataSetIndex>();
		DataSet dataSet = new DataSet(id, sampleGroupId, item, numElems, array, Array.Empty<Field>(), Array.Empty<DataSetIndex>());
		for (int i = 0; i < num; i++)
		{
			reader.Position = dsetOffset + 72 + (long)i * 24L;
			Field field = ReadField(reader, dataSet, item, endian);
			array[i] = field;
			reader.Position = dsetOffset + 72 + (long)(i + 1) * 24L;
		}
		if (indexCount > 0)
		{
			try
			{
				ReadIndexes();
			}
			catch (Exception)
			{
				indexes.Clear();
				extraFields.Clear();
			}
		}
		return new DataSet(id, sampleGroupId, item, numElems, array.Concat(extraFields).ToArray(), Array.Empty<Field>(), indexes.ToArray());
		void ReadIndexes()
		{
			(ushort, byte, uint, uint, uint)[] array2 = new(ushort, byte, uint, uint, uint)[indexCount];
			if (reader.Position != dsetOffset + indexEntryOffset)
			{
				throw new InvalidDataException("Expected to be at the offset for the index entry array.");
			}
			for (int j = 0; j < indexCount; j++)
			{
				uint item2 = ReadReference(reader, endian).BlockOffset;
				ReadReference(reader, endian);
				uint item3 = ReadReference(reader, endian).BlockOffset;
				uint item4 = reader.ReadUInt32(endian);
				ushort item5 = reader.ReadUInt16(endian);
				reader.ReadByte();
				byte item6 = reader.ReadByte();
				array2[j] = (item5, item6, item4, item2, item3);
			}
			if (reader.Position != dsetOffset + indexParameterOffset)
			{
				throw new InvalidDataException("Expected to be at the offset for the index parameter entry array.");
			}
			for (int k = 0; k < array2.Length; k++)
			{
				(ushort, byte, uint, uint, uint) tuple = array2[k];
				ushort item7 = tuple.Item1;
				byte item8 = tuple.Item2;
				uint item9 = tuple.Item3;
				uint item10 = tuple.Item4;
				uint item11 = tuple.Item5;
				DataSetIndex dataSetIndex = new DataSetIndex();
				indexes.Add(dataSetIndex);
				Dictionary<uint, ulong[]> dictionary = new Dictionary<uint, ulong[]>();
				for (int l = 0; l < item8; l++)
				{
					uint num3 = reader.ReadUInt32(endian);
					int num4 = reader.ReadInt32(endian);
					byte b = (byte)((num4 >> 24) & 0xFF);
					int num5 = num4 & 0xFFFFFF;
					int minValue = reader.ReadInt32(endian);
					reader.ReadInt32(endian);
					uint item12 = ReadReference(reader, endian).BlockOffset;
					ulong[] array3;
					if (b > 0 && item12 != 0)
					{
						long position = reader.Position;
						reader.Position = item12;
						array3 = new ulong[num5];
						for (int m = 0; m < num5; m++)
						{
							if (1 == 0)
							{
							}
							long num6 = b switch
							{
								1 => reader.ReadByte(), 
								2 => reader.ReadUInt16(endian), 
								4 => reader.ReadUInt32(endian), 
								8 => (long)reader.ReadUInt64(endian), 
								_ => throw new InvalidDataException("Unhandled tree width."), 
							};
							if (1 == 0)
							{
							}
							long num7 = num6;
							array3[m] = (ulong)(num7 + minValue);
						}
						reader.Position = position;
					}
					else
					{
						array3 = (from v in Enumerable.Range(0, num5)
							select (ulong)(minValue + v)).ToArray();
					}
					dictionary[num3] = array3;
					dataSetIndex.ColumnKeys.Add(num3);
				}
				int num8 = 1;
				foreach (uint columnKey2 in dataSetIndex.ColumnKeys)
				{
					num8 *= dictionary[columnKey2].Length;
				}
				int[] array4 = new int[num8 + 1];
				if (item10 == 0)
				{
					for (int num9 = 0; num9 < array4.Length; num9++)
					{
						array4[num9] = checked((int)(item9 * (uint)num9));
					}
				}
				else
				{
					long position2 = reader.Position;
					reader.Position = item10;
					for (int num10 = 0; num10 < array4.Length; num10++)
					{
						int[] array5 = array4;
						int num11 = num10;
						if (1 == 0)
						{
						}
						int num12 = item9 switch
						{
							1u => reader.ReadByte(), 
							2u => reader.ReadUInt16(endian), 
							4u => (int)reader.ReadUInt32(endian), 
							8u => (int)reader.ReadUInt64(endian), 
							_ => throw new InvalidDataException("Unhandled match index array data width."), 
						};
						if (1 == 0)
						{
						}
						array5[num11] = num12;
					}
					reader.Position = position2;
				}
				int[] array6 = new int[numElems];
				if (item11 == 0)
				{
					for (int num13 = 0; num13 < numElems; num13++)
					{
						array6[num13] = num13;
					}
				}
				else
				{
					long position3 = reader.Position;
					reader.Position = item11;
					int unsignedWidth = NewWaveAssetSampleBankWriter.GetUnsignedWidth((ulong)numElems);
					for (int num14 = 0; num14 < numElems; num14++)
					{
						int[] array7 = array6;
						int num15 = num14;
						if (1 == 0)
						{
						}
						int num12 = unsignedWidth switch
						{
							1 => reader.ReadByte(), 
							2 => reader.ReadUInt16(endian), 
							4 => (int)reader.ReadUInt32(endian), 
							8 => (int)reader.ReadUInt64(endian), 
							_ => throw new InvalidDataException("Unhandled row lookup array data width."), 
						};
						if (1 == 0)
						{
						}
						array7[num15] = num12;
					}
					reader.Position = position3;
				}
				Dictionary<uint, List<ulong>> dictionary2 = new Dictionary<uint, List<ulong>>();
				int num16 = 0;
				List<ulong> value2;
				for (int num17 = 1; num17 < array4.Length; num17++)
				{
					int num18 = array4[num17];
					int num19 = num18 - num16;
					num16 = num18;
					for (int num20 = 0; num20 < dataSetIndex.ColumnKeys.Count; num20++)
					{
						uint key = dataSetIndex.ColumnKeys[num20];
						ulong[] array8 = dictionary[key];
						if (!dictionary2.TryGetValue(key, out var value))
						{
							value2 = (dictionary2[key] = new List<ulong>());
							value = value2;
						}
						int num21;
						if (num20 == dataSetIndex.ColumnKeys.Count - 1)
						{
							num21 = (num17 - 1) % array8.Length;
						}
						else
						{
							int num22 = 1;
							foreach (uint item13 in dataSetIndex.ColumnKeys.Skip(num20 + 1))
							{
								num22 *= dictionary[item13].Length;
							}
							num21 = (num17 - 1) / num22 % array8.Length;
						}
						for (int num23 = 0; num23 < num19; num23++)
						{
							value.Add(array8[num21]);
						}
					}
				}
				foreach (KeyValuePair<uint, List<ulong>> item14 in dictionary2)
				{
					item14.Deconstruct(out var key2, out value2);
					uint columnKey = key2;
					List<ulong> list2 = value2;
					if (dataSet.Get(columnKey) == null && !extraFields.Any((Field field2) => field2.Id == columnKey))
					{
						object[] array9 = new object[numElems];
						for (int num24 = 0; num24 < numElems; num24++)
						{
							array9[array6[num24]] = list2[num24];
						}
						extraFields.Add(new Field(endian, columnKey, FieldType.UInt64, ColumnFormat.Raw, 0u, 0u, new ObservableCollection<object>(array9)));
					}
				}
			}
		}
	}

	private static DataSet? ResolveDataSet(Bank bank, string preferredName, params string[] signatureFields)
	{
		DataSet dataSet = bank.Get(preferredName);
		if (dataSet != null)
		{
			return dataSet;
		}
		if (signatureFields.Length == 0)
		{
			return null;
		}
		return bank.DataSets.FirstOrDefault((DataSet dataSet2) => signatureFields.All((string fieldName) => dataSet2.Get(fieldName) != null));
	}

	private static long TryGetFieldLong(DataSet dataSet, string fieldName, int rowIndex)
	{
		Field field = dataSet.Get(fieldName);
		if (field == null || rowIndex < 0 || rowIndex >= field.Values.Count)
		{
			return 0L;
		}
		return (long)Convert.ChangeType(field.Values[rowIndex], typeof(long));
	}

	private static ulong? ResolveVariationChunkIndex(long memoryChunkIndex, long streamChunkIndex, IReadOnlyDictionary<ulong, ChunkRef> chunkRefs)
	{
		long[] array = new long[2] { streamChunkIndex, memoryChunkIndex };
		foreach (long num in array)
		{
			if (num == 0)
			{
				continue;
			}
			if ((num & 1) == 1)
			{
				ulong num2 = (ulong)(num >> 1);
				if (chunkRefs.Count == 0 || chunkRefs.ContainsKey(num2))
				{
					return num2;
				}
			}
			if (num > 0)
			{
				ulong num3 = (ulong)num;
				if (chunkRefs.Count == 0 || chunkRefs.ContainsKey(num3))
				{
					return num3;
				}
			}
		}
		if (chunkRefs.Count == 1)
		{
			return chunkRefs.Keys.First();
		}
		return null;
	}

	private static Field ReadField(DataStream reader, DataSet dataSet, uint dataOffset, Endian endian)
	{
		uint id = reader.ReadUInt32(endian);
		FieldType dataType = (FieldType)reader.ReadByte();
		ColumnFormat columnFormat = (ColumnFormat)reader.ReadByte();
		ushort formatParameter1 = reader.ReadUInt16(endian);
		ulong formatParameter2 = reader.ReadUInt64(endian);
		(uint, uint) tuple = ReadReference(reader, endian);
		uint columnDataBlockOffset = tuple.Item1;
		uint item = tuple.Item2;
		ObservableCollection<object> values = new ObservableCollection<object>();
		switch (columnFormat)
		{
		case ColumnFormat.Constant:
		{
			for (int j = 0; j < dataSet.NumElems; j++)
			{
				values.Add(ConvertToType(formatParameter2, dataType));
			}
			break;
		}
		case ColumnFormat.IndexFormula:
			ReadIndexFormulaColumns();
			break;
		case ColumnFormat.ShiftedBase:
			ReadShiftedBaseColumns();
			break;
		case ColumnFormat.LookupTable:
			ReadLookupTableColumn();
			break;
		case ColumnFormat.Raw:
		{
			for (int i = 0; i < dataSet.NumElems; i++)
			{
				reader.Position = columnDataBlockOffset + 8L * (long)i;
				values.Add(ConvertToType(reader.ReadUInt64(endian), dataType));
			}
			break;
		}
		default:
			throw new InvalidDataException($"Unhandled column format '{columnFormat}'.");
		}
		return new Field(endian, id, dataType, columnFormat, columnDataBlockOffset, item, values);
		void ReadIndexFormulaColumns()
		{
			short num = (short)formatParameter1;
			long num2 = (long)formatParameter2;
			for (int k = 0; k < dataSet.NumElems; k++)
			{
				values.Add(ConvertToType((ulong)(num2 + num * k), dataType));
			}
		}
		void ReadLookupTableColumn()
		{
			byte b = (byte)(formatParameter1 & 0xFF);
			byte b2 = (byte)((formatParameter1 >> 8) & 0xFF);
			long num = (long)formatParameter2;
			long num2 = columnDataBlockOffset + num * b2;
			for (int k = 0; k < dataSet.NumElems; k++)
			{
				reader.Position = num2 + k * b / 8;
				byte b3 = reader.ReadByte();
				byte b4 = (byte)(k * b % 8);
				int num3 = (b3 >> (int)b4) & ((1 << (int)b) - 1);
				reader.Position = columnDataBlockOffset + b2 * num3;
				if (1 == 0)
				{
				}
				ulong num4 = b2 switch
				{
					1 => reader.ReadByte(), 
					2 => reader.ReadUInt16(endian), 
					4 => reader.ReadUInt32(endian), 
					8 => reader.ReadUInt64(endian), 
					_ => throw new InvalidDataException("Unhandled data width."), 
				};
				if (1 == 0)
				{
				}
				ulong value = num4;
				values.Add(ConvertToType(value, dataType));
			}
		}
		void ReadShiftedBaseColumns()
		{
			byte b = (byte)(formatParameter1 & 0xFF);
			byte b2 = (byte)((formatParameter1 >> 8) & 0xFF);
			ulong num = formatParameter2;
			for (int k = 0; k < dataSet.NumElems; k++)
			{
				reader.Position = columnDataBlockOffset + b2 * k;
				if (1 == 0)
				{
				}
				ulong num2 = b2 switch
				{
					1 => reader.ReadByte(), 
					2 => reader.ReadUInt16(endian), 
					4 => reader.ReadUInt32(endian), 
					8 => reader.ReadUInt64(endian), 
					_ => throw new InvalidDataException("Unhandled data width."), 
				};
				if (1 == 0)
				{
				}
				ulong num3 = num2;
				values.Add(ConvertToType((num3 << (int)b) + num, dataType));
			}
		}
	}

	private static string ReadString(DataStream dataBlock, Field field, int index)
	{
		long position = (long)Convert.ChangeType(field.Values[index], typeof(long));
		dataBlock.Position = position;
		return dataBlock.ReadNullTerminatedString();
	}

	private static object ConvertToType(ulong value, FieldType type)
	{
		if (1 == 0)
		{
		}
		object result;
		switch (type)
		{
		case FieldType.Boolean:
			result = value == 1;
			break;
		case FieldType.Int32:
		{
			IntFloat intFloat = new IntFloat
			{
				UInt64Value = value
			};
			result = intFloat.Int32Value;
			break;
		}
		case FieldType.UInt32:
		{
			IntFloat intFloat = new IntFloat
			{
				UInt64Value = value
			};
			result = intFloat.UInt32Value;
			break;
		}
		case FieldType.Int64:
		{
			IntFloat intFloat = new IntFloat
			{
				UInt64Value = value
			};
			result = intFloat.Int64Value;
			break;
		}
		case FieldType.UInt64:
			result = value;
			break;
		case FieldType.Float32:
		{
			IntFloat intFloat = new IntFloat
			{
				UInt64Value = value
			};
			result = intFloat.Float32Value;
			break;
		}
		case FieldType.Float64:
		{
			IntFloat intFloat = new IntFloat
			{
				UInt64Value = value
			};
			result = intFloat.Float64Value;
			break;
		}
		case FieldType.String:
			result = value;
			break;
		case FieldType.Pointer:
			result = value;
			break;
		default:
			throw new InvalidDataException("Unknown data type.");
		}
		if (1 == 0)
		{
		}
		return result;
	}

	private static uint Hash(string value)
	{
		return (uint)Frosty.Sdk.Utils.Utils.HashString(value, toLower: true);
	}

	private static async Task ExportSegmentInternalAsync(DataStream chunkReader, Segment segment, string outputFilePath, bool rawSpsExport, CancellationToken cancellationToken)
	{
		var (spsData, seekTableData) = ExtractSegmentPayload(chunkReader, segment);
		if (rawSpsExport)
		{
			string directory = Path.GetDirectoryName(outputFilePath);
			if (!string.IsNullOrWhiteSpace(directory))
			{
				Directory.CreateDirectory(directory);
			}
			await File.WriteAllBytesAsync(outputFilePath, spsData, cancellationToken);
			if (seekTableData != null && seekTableData.Length != 0)
			{
				await File.WriteAllBytesAsync(Path.ChangeExtension(outputFilePath, ".sek"), seekTableData, cancellationToken);
			}
			return;
		}
		string tempSpsPath = Path.Combine(Path.GetTempPath(), "FrostyToolsuite", $"{Guid.NewGuid():N}.sps");
		Directory.CreateDirectory(Path.GetDirectoryName(tempSpsPath));
		try
		{
			await File.WriteAllBytesAsync(tempSpsPath, spsData, cancellationToken);
			if (seekTableData != null && seekTableData.Length != 0)
			{
				await File.WriteAllBytesAsync(Path.ChangeExtension(tempSpsPath, ".sek"), seekTableData, cancellationToken);
			}
			await VgmstreamHelper.DecodeAsync(tempSpsPath, outputFilePath, subsongs: false, cancellationToken);
		}
		finally
		{
			TryDeleteTempFile(tempSpsPath);
			TryDeleteTempFile(Path.ChangeExtension(tempSpsPath, ".sek"));
		}
	}

	private static (byte[] SpsData, byte[]? SeekTableData) ExtractSegmentPayload(DataStream chunkReader, Segment segment)
	{
		uint num = segment.SamplesOffset & 0xFFFFFFFCu;
		chunkReader.Position = num;
		if (chunkReader.ReadByte() != 72)
		{
			throw new InvalidDataException("Wrong SPS header.");
		}
		using MemoryStream memoryStream = new MemoryStream();
		CopySpsToDestination(chunkReader, segment.SamplesOffset, memoryStream);
		byte[] item = memoryStream.ToArray();
		byte[] array = null;
		if ((segment.SeekTableOffset & 1) != 0)
		{
			uint num2 = segment.SeekTableOffset & 0xFFFFFFFCu;
			uint num3 = num - num2;
			if (num3 != 0)
			{
				array = new byte[num3];
				chunkReader.Position = num2;
				chunkReader.Read(array, 0, array.Length);
			}
		}
		return (SpsData: item, SeekTableData: array);
	}

	private static void CopySpsToDestination(DataStream chunkReader, uint spsOffsetWithFlags, Stream outputStream)
	{
		long num = (uint)((int)spsOffsetWithFlags & -4);
		int num3;
		do
		{
			chunkReader.Position = num;
			int num2 = chunkReader.ReadInt32(Endian.Big);
			num3 = (num2 & -16777216) >> 24;
			int num4 = num2 & 0xFFFFFF;
			chunkReader.Position = num;
			byte[] array = chunkReader.ReadBytes(num4);
			outputStream.Write(array, 0, array.Length);
			num += num4;
		}
		while (num3 != 69);
	}

	private static void TryDeleteTempFile(string? path)
	{
		try
		{
			if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
			{
				File.Delete(path);
			}
		}
		catch
		{
		}
	}
}
