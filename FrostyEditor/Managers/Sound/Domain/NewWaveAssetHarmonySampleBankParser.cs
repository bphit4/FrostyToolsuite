using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Editor.Lib.Configuration;
using Editor.Lib.Utilities;
using Sdk;
using Sdk.Hash;
using Sdk.Managers;
using Sdk.Utilities.Extensions;
using Serilog;

namespace Editor.Lib.Exporters.Sounds;

public class NewWaveAssetHarmonySampleBankParser
{
	[Flags]
	public enum NewWaveSegmentOffsetFlags
	{
		ValidOffset = 1,
		IsStreaming = 2,
		Mask = 3
	}

	[Flags]
	private enum NewWaveVariationChunkIndexFlags
	{
		IsUsed = 1,
		Shift = 1
	}

	public record ExtractionProgress(int TotalFiles, int CurrentFile, float Percentage);

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

	private const int DATASET_HEADER_ALIGNMENT = 16;

	public const int DataSetHeaderSize = 72;

	public const uint DataSetHeaderTag = 1146307924u;

	private static Guid GetGuid(FileReader dataBlock, Endian endian, Field field, int element)
	{
		long position = (long)Convert.ChangeType(field.Values[element], typeof(long)) - 1;
		dataBlock.Position = position;
		return dataBlock.ReadGuid(endian);
	}

	private static string GetString(FileReader dataBlock, Field field, int element)
	{
		long position = (long)(ulong)field.Values[element];
		dataBlock.Position = position;
		return dataBlock.ReadNullTerminatedString();
	}

	public NewWaveBank ParseNewWaveAssetBank(Stream stream, bool extendedFormat, byte[] resMeta)
	{
		ArgumentNullException.ThrowIfNull(stream, "stream");
		if (!extendedFormat)
		{
			return ParseNewWaveAssetBank(stream);
		}
		uint num = BinaryPrimitives.ReadUInt32LittleEndian(resMeta);
		BinaryPrimitives.ReadUInt32LittleEndian(resMeta.AsSpan(4));
		uint num2 = BinaryPrimitives.ReadUInt32LittleEndian(resMeta.AsSpan(8));
		FileReader fileReader = new FileReader(stream);
		if (num2 == 0)
		{
			fileReader.Position = fileReader.Length - 8;
			uint num3 = fileReader.ReadUInt32LittleEndian();
			uint num4 = fileReader.ReadUInt32LittleEndian();
			fileReader.Position = num;
			Guid ebxInstanceGuid = fileReader.ReadGuid();
			uint selectionDatasetSampleGroupId = fileReader.ReadUInt32LittleEndian();
			uint num5 = fileReader.ReadUInt32LittleEndian();
			int[] array = new int[num4];
			if (num4 != 0 && num3 != 0)
			{
				fileReader.Position = num3;
				for (int i = 0; i < num4; i++)
				{
					array[i] = fileReader.ReadInt32LittleEndian();
				}
			}
			return new NewWaveBank(num5, Djb2Hash.HashString32("FB.A"), new List<Variation>(), new List<DataSet>(), new List<DataSet>())
			{
				EbxInstanceGuid = ebxInstanceGuid,
				SelectionDatasetSampleGroupId = selectionDatasetSampleGroupId,
				SelectionParameterIds = array,
				SelectionParametersOnly = true,
				TrailerBankKey = num5
			};
		}
		NewWaveBank newWaveBank = ParseNewWaveAssetBank(stream);
		fileReader.Position = fileReader.Length - 8;
		uint num6 = fileReader.ReadUInt32LittleEndian();
		uint num7 = fileReader.ReadUInt32LittleEndian();
		if (num7 != 0 && num6 != 0)
		{
			fileReader.Position = num6;
			int[] array2 = new int[num7];
			for (int j = 0; j < num7; j++)
			{
				array2[j] = fileReader.ReadInt32LittleEndian();
			}
			newWaveBank.SelectionParameterIds = array2;
		}
		if (num != 0)
		{
			fileReader.Position = num;
			newWaveBank.EbxInstanceGuid = fileReader.ReadGuid();
			newWaveBank.SelectionDatasetSampleGroupId = fileReader.ReadUInt32LittleEndian();
			newWaveBank.TrailerBankKey = fileReader.ReadUInt32LittleEndian();
		}
		newWaveBank.SelectionParametersOnly = false;
		return newWaveBank;
	}

	public NewWaveBank ParseNewWaveAssetBank(Stream stream)
	{
		ArgumentNullException.ThrowIfNull(stream, "stream");
		Bank bank = Parse(stream);
		FileReader dataBlock = new FileReader(new MemoryStream(bank.DataBlock));
		List<DataSet> list = bank.DataSets.ToList();
		DataSet dataSet = bank.Get("Chunks");
		for (int i = 0; i < (dataSet?.NumElems ?? 0); i++)
		{
			Field field = dataSet.Get("ChunkId");
			Guid guid = GetGuid(dataBlock, field.Endian, field, i);
			field.Values[i] = guid;
		}
		foreach (DataSet dataSet7 in bank.DataSets)
		{
			foreach (Field item in from column in dataSet7.Fields.Concat(dataSet7.IndexColumns)
				where column.DataType == FieldType.String
				select column)
			{
				for (int num = 0; num < item.Values.Count; num++)
				{
					item.Values[num] = GetString(dataBlock, item, num);
				}
			}
		}
		DataSet dataSet2 = bank.Get("SelectionParameters");
		for (int num2 = 0; num2 < (dataSet2?.NumElems ?? 0); num2++)
		{
			_ = (uint)dataSet2.Get("ParameterIndex").Values[num2];
			_ = (uint)dataSet2.Get("ParameterId").Values[num2];
		}
		DataSet dataSet3 = bank.Get("Selection");
		Dictionary<uint, Dictionary<uint, object>> dictionary = new Dictionary<uint, Dictionary<uint, object>>();
		int i2;
		for (i2 = 0; i2 < (dataSet3?.NumElems ?? 0); i2++)
		{
			_ = (uint)dataSet3.Get("VariationIndex").Values[i2];
			uint key = (uint)dataSet3.Get("VariationId").Values[i2];
			dictionary[key] = dataSet3.Fields.Concat(dataSet3.IndexColumns).ToDictionary((Field c) => c.Id, (Field c) => c.Values[i2]);
		}
		DataSet dataSet4 = bank.Get("Persistence");
		for (int num3 = 0; num3 < (dataSet4?.NumElems ?? 0); num3++)
		{
			_ = (uint)dataSet4.Get("RequiredVariationCount").Values[num3];
			_ = (uint)dataSet4.Get("DesiredVariationCount").Values[num3];
			_ = (uint)dataSet4.Get("SelectionParameterCount").Values[num3];
		}
		Dictionary<ulong, ChunkRef> dictionary2 = new Dictionary<ulong, ChunkRef>(dataSet?.NumElems ?? 0);
		if (dataSet != null)
		{
			list.Remove(dataSet);
		}
		for (int num4 = 0; num4 < (dataSet?.NumElems ?? 0); num4++)
		{
			ulong num5 = (ulong)dataSet.Get("ChunkIndex").Values[num4];
			Guid chunkId = (Guid)dataSet.Get("ChunkId").Values[num4];
			long chunkSize = (long)Convert.ChangeType(dataSet.Get("ChunkSize").Values[num4], typeof(long));
			dictionary2.Add(num5, new ChunkRef(num5, chunkId, chunkSize));
		}
		DataSet dataSet5 = bank.Get("Segments");
		if (dataSet5 != null)
		{
			list.Remove(dataSet5);
		}
		List<Segment> list2 = new List<Segment>(dataSet5?.NumElems ?? 0);
		for (int num6 = 0; num6 < (dataSet5?.NumElems ?? 0); num6++)
		{
			int index = (int)(ulong)dataSet5.Get("SegmentIndex").Values[num6];
			uint samplesOffset = (uint)Convert.ChangeType(dataSet5.Get("SamplesOffset").Values[num6], typeof(uint));
			uint seekTableOffset = (uint)Convert.ChangeType(dataSet5.Get("SeekTableOffset").Values[num6], typeof(uint));
			float segmentLength = (float)Convert.ChangeType(dataSet5.Get("Duration").Values[num6], typeof(float));
			list2.Add(new Segment(index, samplesOffset, seekTableOffset, segmentLength));
		}
		DataSet dataSet6 = bank.Get("Variations");
		if (dataSet6 != null)
		{
			list.Remove(dataSet6);
		}
		List<Variation> list3 = new List<Variation>(dataSet6?.NumElems ?? 0);
		for (int num7 = 0; num7 < (dataSet6?.NumElems ?? 0); num7++)
		{
			uint num8 = (uint)Convert.ChangeType(dataSet6.Get("VariationId").Values[num7], typeof(uint));
			long num9 = (long)Convert.ChangeType(dataSet6.Get("FirstLoopSegmentIndex").Values[num7], typeof(long));
			long num10 = (long)Convert.ChangeType(dataSet6.Get("LastLoopSegmentIndex").Values[num7], typeof(long));
			long num11 = (long)Convert.ChangeType(dataSet6.Get("MemoryChunkIndex").Values[num7], typeof(long));
			long num12 = (long)Convert.ChangeType(dataSet6.Get("StreamChunkIndex").Values[num7], typeof(long));
			long num13 = (long)Convert.ChangeType(dataSet6.Get("FirstSegmentIndex").Values[num7], typeof(long));
			long num14 = (long)Convert.ChangeType(dataSet6.Get("SegmentCount").Values[num7], typeof(long));
			if (num9 == 0L)
			{
			}
			List<Segment> range = list2.GetRange((int)num13, (int)num14);
			_ = 1;
			int? num15 = null;
			if ((num12 & 1) == 1)
			{
				num15 = (int)(num12 >> 1);
			}
			else
			{
				if ((num11 & 1) != 1)
				{
					continue;
				}
				num15 = (int)(num11 >> 1);
			}
			ChunkRef chunkRef = dictionary2[(ulong)num15.Value];
			if (!dictionary.TryGetValue(num8, out var value))
			{
				value = new Dictionary<uint, object>();
			}
			list3.Add(new Variation(num7, num8, chunkRef, range, value));
		}
		return new NewWaveBank(bank.Key, bank.ProjectKey, list3, bank.DataSets.ToList(), list);
	}

	public async Task ExportAsync(Stream stream, AssetManager assetManager, string outputFilePath, bool rawSpsExport, IProgress<ExtractionProgress> progressReporter, bool extendedFormat = false, byte[] resMeta = null, CancellationToken cancellationToken = default(CancellationToken))
	{
		ArgumentNullException.ThrowIfNull(stream, "stream");
		ArgumentNullException.ThrowIfNull(assetManager, "assetManager");
		ArgumentNullException.ThrowIfNull(outputFilePath, "outputFilePath");
		cancellationToken.ThrowIfCancellationRequested();
		List<Variation> variations = ParseNewWaveAssetBank(stream, extendedFormat, resMeta).Variations;
		int totalFiles = variations.Sum((Variation v) => v.Segments.Count);
		int currentFileIndex = 0;
		foreach (IGrouping<Guid, Variation> item in from v in variations
			group v by v.ChunkRef.ChunkId)
		{
			Guid key = item.Key;
			ChunkAssetEntry chunkEntry = assetManager.GetChunkEntry(key);
			if (chunkEntry == null)
			{
				Log.Warning("Unable to find chunk with GUID {GUID}.", key);
				continue;
			}
			using MemoryStream chunkStream = assetManager.GetChunk(chunkEntry);
			FileReader chunkReader = new FileReader(chunkStream);
			foreach (Variation variation in item)
			{
				for (int i = 0; i < variation.Segments.Count; i++)
				{
					Segment segment = variation.Segments[i];
					currentFileIndex++;
					cancellationToken.ThrowIfCancellationRequested();
					progressReporter?.Report(new ExtractionProgress(totalFiles, currentFileIndex, 1f / (float)totalFiles * (float)(currentFileIndex - 1)));
					uint num = segment.SamplesOffset & 0xFFFFFFFCu;
					chunkReader.Position = num;
					if (chunkReader.ReadByte() != 72)
					{
						throw new InvalidDataException("Wrong SPS header.");
					}
					string finalOutputPath = ((totalFiles > 1) ? Path.Combine(Path.GetDirectoryName(outputFilePath), $"{Path.GetFileNameWithoutExtension(outputFilePath)}_{segment.Index}_{variation.Index}{(rawSpsExport ? ".sps" : ".wav")}") : outputFilePath);
					string tempOutputPath = (rawSpsExport ? finalOutputPath : Path.Combine(EditorConfiguration.Current.EditorDataFolderAbsolute, "tempaudio.sps"));
					try
					{
						if (rawSpsExport && (segment.SeekTableOffset & 1) != 0)
						{
							uint num2 = segment.SeekTableOffset & 0xFFFFFFFCu;
							uint num3 = num - num2;
							if (num3 != 0)
							{
								string path = Path.ChangeExtension(finalOutputPath, ".sek");
								using FileStream sekStream = new FileStream(path, new FileStreamOptions
								{
									Mode = FileMode.Create,
									Access = FileAccess.Write,
									Options = FileOptions.Asynchronous,
									PreallocationSize = num3
								});
								chunkReader.Position = num2;
								await chunkReader.BaseStream.CopyCountToAsync(sekStream, num3, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								await sekStream.FlushAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
						}
						using (FileStream spsStream = new FileStream(tempOutputPath, new FileStreamOptions
						{
							Mode = FileMode.Create,
							Access = FileAccess.Write,
							Options = FileOptions.Asynchronous
						}))
						{
							await CopySpsToDestination(chunkReader, segment.SamplesOffset, spsStream, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							await spsStream.FlushAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							if (rawSpsExport)
							{
								goto end_IL_04f0;
							}
							await VgmstreamHelper.DecodeAsync(tempOutputPath, finalOutputPath).ConfigureAwait(continueOnCapturedContext: false);
							goto end_IL_02fc;
							end_IL_04f0:;
						}
						end_IL_02fc:;
					}
					finally
					{
						if (!rawSpsExport)
						{
							try
							{
								File.Delete(tempOutputPath);
							}
							catch (Exception)
							{
							}
						}
					}
				}
			}
		}
	}

	public async Task ExportAsync(Segment segment, Guid chunkId, AssetManager assetManager, string outputFilePath, bool rawSpsExport, bool originalData, IProgress<ExtractionProgress> progressReporter, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(segment, "segment");
		ArgumentNullException.ThrowIfNull(assetManager, "assetManager");
		ArgumentNullException.ThrowIfNull(outputFilePath, "outputFilePath");
		cancellationToken.ThrowIfCancellationRequested();
		ChunkAssetEntry chunkEntry = assetManager.GetChunkEntry(chunkId);
		if (chunkEntry == null)
		{
			Log.Warning("Unable to find chunk with GUID {GUID}.", chunkId);
			return;
		}
		using MemoryStream chunkStream = assetManager.GetChunk(chunkEntry, originalData);
		FileReader chunkReader = new FileReader(chunkStream);
		cancellationToken.ThrowIfCancellationRequested();
		progressReporter?.Report(new ExtractionProgress(1, 1, 0f));
		uint num = segment.SamplesOffset & 0xFFFFFFFCu;
		chunkReader.Position = num;
		if (chunkReader.ReadByte() != 72)
		{
			throw new InvalidDataException("Wrong SPS header.");
		}
		string tempOutputPath = Path.Combine(EditorConfiguration.Current.EditorDataFolderAbsolute, "tempaudio.sps");
		string spsOutputPath = (rawSpsExport ? outputFilePath : tempOutputPath);
		try
		{
			if (rawSpsExport && (segment.SeekTableOffset & 1) != 0)
			{
				uint num2 = segment.SeekTableOffset & 0xFFFFFFFCu;
				uint num3 = num - num2;
				if (num3 != 0)
				{
					string path = Path.ChangeExtension(outputFilePath, ".sek");
					using FileStream sekStream = new FileStream(path, new FileStreamOptions
					{
						Mode = FileMode.Create,
						Access = FileAccess.Write,
						Options = FileOptions.Asynchronous,
						PreallocationSize = num3
					});
					chunkReader.Position = num2;
					await chunkReader.BaseStream.CopyCountToAsync(sekStream, num3, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
					await sekStream.FlushAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
			}
			using FileStream spsStream = new FileStream(spsOutputPath, new FileStreamOptions
			{
				Mode = FileMode.Create,
				Access = FileAccess.Write,
				Options = FileOptions.Asynchronous
			});
			await CopySpsToDestination(chunkReader, segment.SamplesOffset, spsStream, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			await spsStream.FlushAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			if (rawSpsExport)
			{
				return;
			}
			await VgmstreamHelper.DecodeAsync(tempOutputPath, outputFilePath).ConfigureAwait(continueOnCapturedContext: false);
		}
		finally
		{
			try
			{
				File.Delete(tempOutputPath);
			}
			catch (Exception)
			{
			}
		}
	}

	private static async Task CopySpsToDestination(FileReader chunkReader, uint spsOffsetWithFlags, Stream outputStream, CancellationToken cancellationToken)
	{
		long currentOffset = (uint)((int)spsOffsetWithFlags & -4);
		int blockId;
		do
		{
			chunkReader.Position = currentOffset;
			int num = chunkReader.ReadInt32BigEndian();
			blockId = (int)((num & 0xFF000000u) >> 24);
			int blockSize = num & 0xFFFFFF;
			chunkReader.Position = currentOffset;
			await chunkReader.BaseStream.CopyCountToAsync(outputStream, blockSize, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			currentOffset += blockSize;
		}
		while (blockId != 69);
	}

	private Bank Parse(Stream stream)
	{
		ArgumentNullException.ThrowIfNull(stream, "stream");
		FileReader fileReader = new FileReader(stream);
		uint num = fileReader.ReadUInt32LittleEndian();
		Endian endian = num switch
		{
			1701593683u => Endian.Little, 
			1700938323u => Endian.Big, 
			_ => throw new InvalidDataException($"Wrong format ID for harmony sample bank. Got \"{num}\"."), 
		};
		fileReader.ReadUInt32(endian);
		byte alignment = (byte)(1 << (int)fileReader.ReadByte());
		byte b = fileReader.ReadByte();
		if (b != 0)
		{
			throw new InvalidDataException($"Unknown sample bank version '{b}'.");
		}
		ushort num2 = fileReader.ReadUInt16(endian);
		uint key = fileReader.ReadUInt32(endian);
		uint projectKey = fileReader.ReadUInt32(endian);
		fileReader.ReadInt32(endian);
		uint item = ReadReference(fileReader, endian).BlockOffset;
		uint item2 = ReadReference(fileReader, endian).BlockOffset;
		ReadReference(fileReader, endian);
		ReadReference(fileReader, endian);
		ReadReference(fileReader, endian);
		ReadReference(fileReader, endian);
		ReadReference(fileReader, endian);
		fileReader.Pad(alignment);
		if (fileReader.Position != item)
		{
			throw new InvalidDataException("Expected to be at the data set entry offset.");
		}
		Dictionary<uint, DataSet> dictionary = new Dictionary<uint, DataSet>(num2);
		List<DataSet> list = new List<DataSet>(num2);
		for (int i = 0; i < num2; i++)
		{
			fileReader.Position = item + i * 8;
			uint item3 = ReadReference(fileReader, endian).BlockOffset;
			DataSet dataSet = ReadDataSet(fileReader, item3, endian);
			dictionary[dataSet.Id] = dataSet;
			list.Add(dataSet);
		}
		byte[] dataBlock = Array.Empty<byte>();
		if (item2 != 0)
		{
			fileReader.Position = item2;
			dataBlock = fileReader.ReadBytes((int)(fileReader.Length - fileReader.Position));
		}
		return new Bank(key, projectKey, list, dataBlock);
	}

	private static (uint BlockOffset, uint NextReferenceOffset) ReadReference(FileReader reader, Endian endian)
	{
		uint item = reader.ReadUInt32(endian);
		uint item2 = reader.ReadUInt32(endian);
		return (BlockOffset: item, NextReferenceOffset: item2);
	}

	private DataSet ReadDataSet(FileReader reader, uint dsetOffset, Endian endian)
	{
		ArgumentNullException.ThrowIfNull(reader, "reader");
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
		int num = reader.ReadInt32(endian);
		ushort num2 = reader.ReadUInt16(endian);
		ushort num3 = reader.ReadUInt16(endian);
		ushort num4 = reader.ReadUInt16(endian);
		ushort num5 = reader.ReadUInt16(endian);
		ushort num6 = reader.ReadUInt16(endian);
		reader.ReadByte();
		reader.ReadByte();
		if (reader.Position - dsetOffset != 72)
		{
			throw new InvalidDataException("The data set header was not the expected size.");
		}
		if (reader.Position != dsetOffset + num4)
		{
			throw new InvalidDataException("Expected to be at the offset for the column entry array.");
		}
		Dictionary<uint, Field> dictionary = new Dictionary<uint, Field>(num2);
		List<Field> list = new List<Field>();
		List<DataSetIndex> list2 = new List<DataSetIndex>();
		Field[] array = new Field[num2];
		DataSet dataSet = new DataSet(id, sampleGroupId, item, num, array, list.ToArray(), list2.ToArray());
		for (int i = 0; i < num2; i++)
		{
			reader.Position = dsetOffset + 72 + i * 24;
			Field field = ReadField(reader, dataSet, item, endian);
			dictionary[field.Id] = field;
			array[i] = field;
			reader.Position = dsetOffset + 72 + (i + 1) * 24;
		}
		if (num3 > 0)
		{
			(ushort, byte, uint, uint, uint)[] array2 = new(ushort, byte, uint, uint, uint)[num3];
			if (reader.Position != dsetOffset + num5)
			{
				throw new InvalidDataException("Expected to be at the offset for the index entry array.");
			}
			for (int j = 0; j < num3; j++)
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
			if (reader.Position != dsetOffset + num6)
			{
				throw new InvalidDataException("Expected to be at the offset for the index parameter entry array.");
			}
			for (int k = 0; k < array2.Length; k++)
			{
				(ushort, byte, uint, uint, uint) tuple = array2[k];
				byte item7 = tuple.Item2;
				uint item8 = tuple.Item3;
				uint item9 = tuple.Item4;
				uint item10 = tuple.Item5;
				DataSetIndex dataSetIndex = new DataSetIndex(new List<uint>());
				list2.Add(dataSetIndex);
				Dictionary<uint, ulong[]> columnUniqueValues = new Dictionary<uint, ulong[]>();
				for (int l = 0; l < item7; l++)
				{
					uint num7 = reader.ReadUInt32(endian);
					int num8 = reader.ReadInt32(endian);
					byte b = (byte)((num8 >> 24) & 0xFF);
					int num9 = num8 & 0xFFFFFF;
					int minValue = reader.ReadInt32(endian);
					reader.ReadInt32(endian);
					uint item11 = ReadReference(reader, endian).BlockOffset;
					ulong[] array3;
					if (b > 0 && item11 != 0)
					{
						long position = reader.Position;
						reader.Position = item11;
						array3 = new ulong[num9];
						for (int m = 0; m < num9; m++)
						{
							array3[m] = (ulong)(b switch
							{
								1 => reader.ReadByte(), 
								2 => reader.ReadUInt16(endian), 
								4 => reader.ReadUInt32(endian), 
								8 => (long)reader.ReadUInt64(endian), 
								_ => throw new InvalidDataException("Unhandled tree width."), 
							} + minValue);
						}
						reader.Position = position;
					}
					else
					{
						array3 = (from num28 in Enumerable.Range(0, num9)
							select (ulong)(minValue + num28)).ToArray();
					}
					columnUniqueValues[num7] = array3;
					dataSetIndex.ColumnKeys.Add(num7);
				}
				int num10 = 1;
				foreach (uint columnKey2 in dataSetIndex.ColumnKeys)
				{
					num10 *= columnUniqueValues[columnKey2].Length;
				}
				int num11 = num10 + 1;
				int[] array4 = new int[num11];
				if (item9 == 0)
				{
					uint num12 = item8;
					for (int num13 = 0; num13 < num11; num13++)
					{
						array4[num13] = (int)(num12 * num13);
					}
				}
				else
				{
					long position2 = reader.Position;
					reader.Position = item9;
					uint num14 = item8;
					for (int num15 = 0; num15 < num11; num15++)
					{
						array4[num15] = (int)(num14 switch
						{
							1u => reader.ReadByte(), 
							2u => reader.ReadUInt16(endian), 
							4u => reader.ReadUInt32(endian), 
							8u => (long)reader.ReadUInt64(endian), 
							_ => throw new InvalidDataException("Unhandled match index array data width."), 
						});
					}
					reader.Position = position2;
				}
				int[] array5 = new int[num];
				if (item10 == 0)
				{
					for (int num16 = 0; num16 < num; num16++)
					{
						array5[num16] = num16;
					}
				}
				else
				{
					long position3 = reader.Position;
					reader.Position = item10;
					int unsignedWidth = NewWaveAssetSampleBankWriter.GetUnsignedWidth((ulong)num);
					for (int num17 = 0; num17 < num; num17++)
					{
						array5[num17] = (int)(unsignedWidth switch
						{
							1 => reader.ReadByte(), 
							2 => reader.ReadUInt16(endian), 
							4 => reader.ReadUInt32(endian), 
							8 => (long)reader.ReadUInt64(endian), 
							_ => throw new InvalidDataException("Unhandled row lookup array data width."), 
						});
					}
					reader.Position = position3;
				}
				Dictionary<uint, List<ulong>> dictionary2 = new Dictionary<uint, List<ulong>>();
				int num18 = 0;
				for (int num19 = 1; num19 < num11; num19++)
				{
					int num20 = array4[num19];
					int num21 = num20 - num18;
					num18 = num20;
					for (int num22 = 0; num22 < dataSetIndex.ColumnKeys.Count; num22++)
					{
						uint key = dataSetIndex.ColumnKeys[num22];
						ulong[] array6 = columnUniqueValues[key];
						if (!dictionary2.TryGetValue(key, out var value))
						{
							value = (dictionary2[key] = new List<ulong>());
						}
						int num23;
						if (num22 == dataSetIndex.ColumnKeys.Count - 1)
						{
							num23 = (num19 - 1) % array6.Length;
						}
						else
						{
							num23 = (num19 - 1) / (from cki in dataSetIndex.ColumnKeys.Skip(num22 + 1)
								select columnUniqueValues[cki]).Aggregate(1, (int total, ulong[] next) => total * next.Length);
							num23 %= array6.Length;
						}
						for (int num24 = 0; num24 < num21; num24++)
						{
							value.Add(array6[num23]);
						}
					}
				}
				foreach (KeyValuePair<uint, List<ulong>> item12 in dictionary2)
				{
					uint columnKey = item12.Key;
					List<ulong> list5 = item12.Value;
					if (!dictionary.ContainsKey(columnKey) && !list.Any((Field ic) => ic.Id == columnKey))
					{
						object[] array7 = new object[num];
						for (int num26 = 0; num26 < num; num26++)
						{
							ulong num27 = list5[num26];
							array7[array5[num26]] = num27;
						}
						list.Add(new Field(endian, columnKey, FieldType.UInt64, ColumnFormat.Raw, 0u, 0u, new ObservableCollection<object>(array7)));
					}
				}
			}
		}
		return new DataSet(id, sampleGroupId, item, num, array, list.ToArray(), list2.ToArray());
	}

	private static Field ReadField(FileReader reader, DataSet dataSet, uint dataOffset, Endian endian)
	{
		ArgumentNullException.ThrowIfNull(reader, "reader");
		ArgumentNullException.ThrowIfNull(dataSet, "dataSet");
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
				reader.Position = columnDataBlockOffset + 8 * i;
				ulong value = reader.ReadUInt64(endian);
				values.Add(ConvertToType(value, dataType));
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
				long value2 = num2 + num * k;
				values.Add(ConvertToType((ulong)value2, dataType));
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
				byte num3 = reader.ReadByte();
				byte b3 = (byte)(k * b % 8);
				int num4 = (byte)(num3 >> (int)b3) & ((1 << (int)b) - 1);
				ulong value2 = 0uL;
				reader.Position = columnDataBlockOffset + b2 * num4;
				switch (b2)
				{
				case 1:
					value2 = reader.ReadByte();
					break;
				case 2:
					value2 = reader.ReadUInt16(endian);
					break;
				case 4:
					value2 = reader.ReadUInt32(endian);
					break;
				case 8:
					value2 = reader.ReadUInt64(endian);
					break;
				}
				values.Add(ConvertToType(value2, dataType));
			}
		}
		void ReadShiftedBaseColumns()
		{
			byte b = (byte)(formatParameter1 & 0xFF);
			byte b2 = (byte)((formatParameter1 >> 8) & 0xFF);
			ulong num = formatParameter2;
			ulong num2 = 0uL;
			for (int k = 0; k < dataSet.NumElems; k++)
			{
				reader.Position = columnDataBlockOffset + b2 * k;
				switch (b2)
				{
				case 1:
					num2 = reader.ReadByte();
					break;
				case 2:
					num2 = reader.ReadUInt16(endian);
					break;
				case 4:
					num2 = reader.ReadUInt32(endian);
					break;
				case 8:
					num2 = reader.ReadUInt64(endian);
					break;
				}
				num2 <<= (int)b;
				num2 += num;
				values.Add(ConvertToType(num2, dataType));
			}
		}
	}

	private static object ConvertToType(ulong value, FieldType type)
	{
		switch (type)
		{
		case FieldType.Boolean:
			return value == 1;
		case FieldType.Int32:
		{
			IntFloat intFloat = new IntFloat
			{
				UInt64Value = value
			};
			return intFloat.Int32Value;
		}
		case FieldType.UInt32:
		{
			IntFloat intFloat = new IntFloat
			{
				UInt64Value = value
			};
			return intFloat.UInt32Value;
		}
		case FieldType.Int64:
		{
			IntFloat intFloat = new IntFloat
			{
				UInt64Value = value
			};
			return intFloat.Int64Value;
		}
		case FieldType.UInt64:
			return value;
		case FieldType.Float32:
		{
			IntFloat intFloat = new IntFloat
			{
				UInt64Value = value
			};
			return intFloat.Float32Value;
		}
		case FieldType.Float64:
		{
			IntFloat intFloat = new IntFloat
			{
				UInt64Value = value
			};
			return intFloat.Float64Value;
		}
		case FieldType.String:
		case FieldType.Pointer:
			return value;
		default:
			throw new InvalidDataException("Unknown field type.");
		}
	}
}
