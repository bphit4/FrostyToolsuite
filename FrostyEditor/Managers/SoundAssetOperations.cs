#nullable disable
#pragma warning disable CS8632
using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Frosty.Sdk;
using Frosty.Sdk.Ebx;
using Frosty.Sdk.IO;
using Frosty.Sdk.Managers;
using Frosty.Sdk.Managers.Entries;
using Frosty.Sdk.Utils;
using FrostyEditor.Managers.Sound;
using FrostyEditor.Managers.Sound.Tools;
using FrostyEditor.Models.Audio;
using FrostyEditor.Utils;

namespace FrostyEditor.Managers;

public static class SoundAssetOperations
{
	private delegate bool TryParseDelegate<T>(string text, out T value) where T : struct;

	private sealed record BulkSoundImportCandidate(int SequenceNumber, string FilePath);

	private sealed record SoundSegmentLocation(int SequenceNumber, int VariationIndex, int SegmentIndex);

	private sealed record ImportedSoundPayload(Guid ChunkId, int ChunkSize, uint SeekTableOffset, uint SamplesOffset, float Duration, bool HasStreamPool);

	private const uint HarmonyBankFormatIdLittleEndian = 1701593683u;

	private const uint HarmonyBankFormatIdBigEndian = 1700938323u;

	private static readonly string[] s_importExtensions = new string[9] { ".sps", ".wav", ".mp3", ".flac", ".ogg", ".m4a", ".sek", ".sbr", ".sbs" };

	private static readonly Regex s_variationSegmentFilePattern = new Regex("_v(?<variation>\\d+)_s(?<segment>\\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly Regex s_chunkFilePattern = new Regex("_(?<chunk>\\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly string[] s_wrapperValueMemberNames = new string[6] { "Value", "_Value", "m_value", "InternalValue", "Data", "RawValue" };

	private static readonly Type[] s_scalarConversionTargets = new Type[7]
	{
		typeof(Guid),
		typeof(ulong),
		typeof(long),
		typeof(uint),
		typeof(int),
		typeof(bool),
		typeof(string)
	};

	private static readonly HashSet<string> s_soundEditorTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "HarmonySampleBankAsset", "ImpulseResponseAsset", "LocalizedWaveAsset", "NewWaveAsset", "OctaneAsset", "SoundAsset", "SoundWaveAsset" };

	public static bool IsModified(EbxAssetEntry entry)
	{
		try
		{
			SoundAssetState soundAssetState = Load(entry, logWarnings: false);
			if (AssetManager.IsEbxModified(entry.Name))
			{
				return true;
			}
			if (soundAssetState.ResEntry != null && AssetManager.IsResModified(soundAssetState.ResEntry.ResRid))
			{
				return true;
			}
			Guid? bankContainerChunkId = soundAssetState.BankContainerChunkId;
			if (bankContainerChunkId.HasValue)
			{
				Guid valueOrDefault = bankContainerChunkId.GetValueOrDefault();
				if (AssetManager.IsChunkModified(valueOrDefault))
				{
					return true;
				}
			}
			return soundAssetState.Chunks.Any((SoundChunkBinding chunk) => AssetManager.IsChunkModified(chunk.ChunkId));
		}
		catch
		{
			return AssetManager.IsEbxModified(entry.Name);
		}
	}

	public static bool IsSoundAsset(EbxAssetEntry entry)
	{
		if (!SupportsSoundEditor(entry))
		{
			return false;
		}
		if (string.Equals(entry.Type, "NewWaveAsset", StringComparison.OrdinalIgnoreCase) || string.Equals(entry.Type, "LocalizedWaveAsset", StringComparison.OrdinalIgnoreCase) || string.Equals(entry.Type, "HarmonySampleBankAsset", StringComparison.OrdinalIgnoreCase) || string.Equals(entry.Type, "ImpulseResponseAsset", StringComparison.OrdinalIgnoreCase) || string.Equals(entry.Type, "OctaneAsset", StringComparison.OrdinalIgnoreCase) || string.Equals(entry.Type, "SoundWaveAsset", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		try
		{
			return Load(entry, logWarnings: false).Kind != SoundAssetKind.Unknown;
		}
		catch (Exception ex)
		{
			FrostyLogger.Logger?.LogWarning("Sound probe failed for " + entry.Name + ": " + ex.Message);
			return false;
		}
	}

	public static bool SupportsSoundEditor(EbxAssetEntry entry)
	{
		ArgumentNullException.ThrowIfNull(entry, "entry");
		return s_soundEditorTypes.Contains(entry.Type);
	}

	public static SoundAssetState Load(EbxAssetEntry entry, bool logWarnings = true)
	{
		ArgumentNullException.ThrowIfNull(entry, "entry");
		EbxPartition ebxPartition = AssetManager.GetEbxPartition(entry);
		object rootObject = ebxPartition.RootInstances.FirstOrDefault() ?? ebxPartition.PrimaryInstance;
		IReadOnlyList<SoundChunkBinding> readOnlyList = ResolveChunks(rootObject);
		ResAssetEntry resAssetEntry = ResolveResEntry(rootObject, entry.Name);
		if (readOnlyList.Count == 0 && resAssetEntry != null)
		{
			readOnlyList = ResolveLinkedChunks(resAssetEntry);
		}
		byte[] array = ((resAssetEntry == null) ? null : AssetManager.GetResMeta(resAssetEntry));
		NewWaveBank newWaveBank = null;
		Guid? bankContainerChunkId = null;
		Endian? bankEndian = null;
		bool flag = resAssetEntry != null && !string.Equals(entry.Type, "HarmonySampleBankAsset", StringComparison.OrdinalIgnoreCase);
		if (flag && resAssetEntry != null)
		{
			try
			{
				byte[] array2 = AssetManager.GetAsset(resAssetEntry).ToArray();
				if (ShouldAttemptResBankParse(entry, readOnlyList, array2, array))
				{
					using MemoryStream memoryStream = new MemoryStream(array2, writable: false);
					bankEndian = (LooksLikeHarmonySampleBank(array2) ? DetectEndian(memoryStream) : Endian.Little);
					memoryStream.Position = 0L;
					newWaveBank = new NewWaveAssetHarmonySampleBankParser().ParseNewWaveAssetBank(memoryStream, array != null && array.Length >= 16, array);
				}
			}
			catch (Exception ex)
			{
				if (logWarnings)
				{
					FrostyLogger.Logger?.LogWarning("RES " + resAssetEntry.Name + " is not a supported sound bank: " + ex.Message);
				}
			}
		}
		if (newWaveBank == null && readOnlyList.Count > 0)
		{
			foreach (SoundChunkBinding item in EnumerateBankCandidates(rootObject, readOnlyList))
			{
				if (item.ChunkEntry == null)
				{
					continue;
				}
				try
				{
					byte[] array3 = AssetManager.GetAsset(item.ChunkEntry).ToArray();
					if (!LooksLikeHarmonySampleBank(array3))
					{
						continue;
					}
					using (MemoryStream memoryStream2 = new MemoryStream(array3, writable: false))
					{
						bankEndian = DetectEndian(memoryStream2);
						memoryStream2.Position = 0L;
						newWaveBank = new NewWaveAssetHarmonySampleBankParser().ParseNewWaveAssetBank(memoryStream2, array != null && array.Length >= 16, array);
						bankContainerChunkId = item.ChunkId;
					}
					break;
				}
				catch (Exception ex2)
				{
					if (logWarnings)
					{
						FrostyLogger.Logger?.LogWarning($"Chunk {item.ChunkId} is not a harmony sample bank: {ex2.Message}");
					}
				}
			}
		}
		if (newWaveBank == null && !flag && resAssetEntry != null)
		{
			try
			{
				byte[] array4 = AssetManager.GetAsset(resAssetEntry).ToArray();
				if (ShouldAttemptResBankParse(entry, readOnlyList, array4, array))
				{
					using MemoryStream memoryStream3 = new MemoryStream(array4, writable: false);
					bankEndian = (LooksLikeHarmonySampleBank(array4) ? DetectEndian(memoryStream3) : Endian.Little);
					memoryStream3.Position = 0L;
					newWaveBank = new NewWaveAssetHarmonySampleBankParser().ParseNewWaveAssetBank(memoryStream3, array != null && array.Length >= 16, array);
				}
			}
			catch (Exception ex3)
			{
				if (logWarnings)
				{
					FrostyLogger.Logger?.LogWarning("RES " + resAssetEntry.Name + " is not a supported sound bank: " + ex3.Message);
				}
			}
		}
		if (newWaveBank != null && !HasAnyUsableBankContent(newWaveBank) && readOnlyList.Count == 0)
		{
			newWaveBank = null;
			bankContainerChunkId = null;
			bankEndian = null;
		}
		if (readOnlyList.Count == 0 && newWaveBank != null)
		{
			readOnlyList = ResolveBankVariationChunks(newWaveBank);
		}
		if (newWaveBank != null && readOnlyList.Count > 0)
		{
			RepairBankChunkRefs(newWaveBank, readOnlyList);
		}
		SoundAssetKind soundAssetKind = ResolveKind(readOnlyList, newWaveBank);
		if (logWarnings && string.Equals(entry.Type, "NewWaveAsset", StringComparison.OrdinalIgnoreCase))
		{
			FrostyLogger.Logger?.LogInfo($"Sound load {entry.Name}: kind={soundAssetKind}, chunks={readOnlyList.Count}, bankVars={newWaveBank?.Variations.Count ?? 0}, dataSets={newWaveBank?.AllDataSets.Count ?? 0}, res={resAssetEntry?.Name ?? "<none>"}");
		}
		return new SoundAssetState(entry, ebxPartition, rootObject, readOnlyList, soundAssetKind, newWaveBank, bankContainerChunkId, bankEndian, resAssetEntry, array);
	}

	private static SoundAssetKind ResolveKind(IReadOnlyList<SoundChunkBinding> chunks, NewWaveBank? bank)
	{
		if (bank != null)
		{
			if (bank.Variations.Count > 0)
			{
				return SoundAssetKind.Bank;
			}
			if (chunks.Count > 0)
			{
				return SoundAssetKind.ChunkList;
			}
			if (HasAnyUsableBankContent(bank))
			{
				return SoundAssetKind.Bank;
			}
			return SoundAssetKind.Unknown;
		}
		return (chunks.Count > 0) ? SoundAssetKind.ChunkList : SoundAssetKind.Unknown;
	}

	private static bool HasAnyUsableBankContent(NewWaveBank bank)
	{
		if (bank.Variations.Count > 0)
		{
			return true;
		}
		if (!bank.SelectionParametersOnly)
		{
			return bank.AllDataSets.Any((DataSet dataSet) => dataSet.NumElems > 0);
		}
		return false;
	}

	private static bool ShouldAttemptResBankParse(EbxAssetEntry entry, IReadOnlyList<SoundChunkBinding> chunks, ReadOnlySpan<byte> raw, byte[]? resMeta)
	{
		if (raw.Length < 16)
		{
			return false;
		}
		if (IsZeroFilled(raw))
		{
			return false;
		}
		if (LooksLikeHarmonySampleBank(raw))
		{
			return true;
		}
		if (string.Equals(entry.Type, "NewWaveAsset", StringComparison.OrdinalIgnoreCase) && resMeta != null && resMeta.Length >= 16)
		{
			return true;
		}
		if (!string.Equals(entry.Type, "NewWaveAsset", StringComparison.OrdinalIgnoreCase) && chunks.Count > 0)
		{
			return false;
		}
		if (string.Equals(entry.Type, "SoundAsset", StringComparison.OrdinalIgnoreCase))
		{
			return raw.Length >= 256;
		}
		return chunks.Count == 0;
	}

	private static bool IsZeroFilled(ReadOnlySpan<byte> raw)
	{
		for (int i = 0; i < raw.Length; i++)
		{
			if (raw[i] != 0)
			{
				return false;
			}
		}
		return true;
	}

	public static bool TryResolveSource(EbxAssetEntry entry, EbxPartition partition, out AssetEntry? sourceEntry, out byte[]? resMeta, out string message)
	{
		try
		{
			SoundAssetState soundAssetState = Load(entry);
			sourceEntry = ((soundAssetState.ResEntry != null) ? ((AssetEntry)soundAssetState.ResEntry) : ((AssetEntry)(soundAssetState.Chunks.FirstOrDefault()?.ChunkEntry)));
			resMeta = soundAssetState.ResMeta;
			message = ((sourceEntry == null) ? "No sound source could be resolved." : "Sound source resolved.");
			return sourceEntry != null;
		}
		catch (Exception ex)
		{
			sourceEntry = null;
			resMeta = null;
			message = ex.Message;
			return false;
		}
	}

	public static SpsSoundHeader? TryReadChunkHeader(Guid chunkId)
	{
		try
		{
			ChunkAssetEntry chunkAssetEntry = AssetManager.GetChunkAssetEntry(chunkId);
			if (chunkAssetEntry == null)
			{
				return null;
			}
			byte[] chunkBytes = AssetManager.GetAsset(chunkAssetEntry).ToArray();
			IReadOnlyList<SpsCandidate> spsCandidates = GetSpsCandidates(chunkBytes);
			return (spsCandidates.Count > 0) ? spsCandidates[0].Header : null;
		}
		catch
		{
			return null;
		}
	}

	public static async Task<SoundOperationResult> ExportToPathAsync(EbxAssetEntry entry, string path, bool rawSpsExport)
	{
		try
		{
			SoundAssetState state = Load(entry);
			if (state.Kind == SoundAssetKind.Bank && state.ParsedBank != null)
			{
				if (state.ParsedBank.Variations.Count == 1 && state.ParsedBank.Variations[0].Segments.Count == 1)
				{
					await ExportSegmentAsync(state, 0, 0, path, rawSpsExport).ConfigureAwait(continueOnCapturedContext: false);
				}
				else
				{
					string outputDirectory = Path.ChangeExtension(path, null) ?? path;
					await ExportAllAsync(state, outputDirectory, rawSpsExport).ConfigureAwait(continueOnCapturedContext: false);
				}
			}
			else if (state.Chunks.Count == 1)
			{
				await ExportChunkAsync(state, 0, path, rawSpsExport).ConfigureAwait(continueOnCapturedContext: false);
			}
			else
			{
				string outputDirectory2 = Path.ChangeExtension(path, null) ?? path;
				await ExportAllAsync(state, outputDirectory2, rawSpsExport).ConfigureAwait(continueOnCapturedContext: false);
			}
			return new SoundOperationResult(Success: true, "Exported " + entry.Filename + ".");
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			return new SoundOperationResult(Success: false, "Sound export failed: " + ex2.Message);
		}
	}

	public static Task<SoundOperationResult> ImportAsync(EbxAssetEntry entry)
	{
		return ImportWithPickerAsync(entry);
	}

	public static async Task<SoundOperationResult> ExportWithPickerAsync(EbxAssetEntry entry)
	{
		FilePickerSaveOptions options = new FilePickerSaveOptions
		{
			Title = "Export sound",
			SuggestedFileName = entry.Filename,
			DefaultExtension = "wav",
			FileTypeChoices = new _003C_003Ez__ReadOnlyArray<FilePickerFileType>(new FilePickerFileType[2]
			{
				new FilePickerFileType("Wave (*.wav)")
				{
					Patterns = new _003C_003Ez__ReadOnlySingleElementList<string>("*.wav")
				},
				new FilePickerFileType("EA SPS (*.sps)")
				{
					Patterns = new _003C_003Ez__ReadOnlySingleElementList<string>("*.sps")
				}
			})
		};
		IStorageFile file = await FileService.SaveFilePickerAsync(options).ConfigureAwait(continueOnCapturedContext: false);
		if (file == null)
		{
			return new SoundOperationResult(Success: false, "Sound export canceled.");
		}
		return await ExportToPathAsync(rawSpsExport: string.Equals(Path.GetExtension(file.Name), ".sps", StringComparison.OrdinalIgnoreCase), entry: entry, path: file.Path.LocalPath).ConfigureAwait(continueOnCapturedContext: false);
	}

	public static async Task<SoundOperationResult> ExportSegmentWithPickerAsync(EbxAssetEntry entry, int variationIndex, int segmentIndex)
	{
		FilePickerSaveOptions options = new FilePickerSaveOptions
		{
			Title = "Export sound segment",
			SuggestedFileName = $"{entry.Filename}_v{variationIndex:0000}_s{segmentIndex:0000}",
			DefaultExtension = "wav",
			FileTypeChoices = new _003C_003Ez__ReadOnlyArray<FilePickerFileType>(new FilePickerFileType[2]
			{
				new FilePickerFileType("Wave (*.wav)")
				{
					Patterns = new _003C_003Ez__ReadOnlySingleElementList<string>("*.wav")
				},
				new FilePickerFileType("EA SPS (*.sps)")
				{
					Patterns = new _003C_003Ez__ReadOnlySingleElementList<string>("*.sps")
				}
			})
		};
		IStorageFile file = await FileService.SaveFilePickerAsync(options).ConfigureAwait(continueOnCapturedContext: false);
		if (file == null)
		{
			return new SoundOperationResult(Success: false, "Sound export canceled.");
		}
		try
		{
			await ExportSegmentAsync(Load(entry), rawSpsExport: string.Equals(Path.GetExtension(file.Name), ".sps", StringComparison.OrdinalIgnoreCase), variationIndex: variationIndex, segmentIndex: segmentIndex, outputFilePath: file.Path.LocalPath).ConfigureAwait(continueOnCapturedContext: false);
			return new SoundOperationResult(Success: true, $"Exported segment {segmentIndex} from {entry.Filename}.");
		}
		catch (Exception ex)
		{
			return new SoundOperationResult(Success: false, "Segment export failed: " + ex.Message);
		}
	}

	public static async Task<SoundOperationResult> ImportWithPickerAsync(EbxAssetEntry entry)
	{
		FilePickerOpenOptions options = new FilePickerOpenOptions
		{
			Title = "Import sound",
			AllowMultiple = false,
			FileTypeFilter = new _003C_003Ez__ReadOnlySingleElementList<FilePickerFileType>(new FilePickerFileType("Audio files")
			{
				Patterns = new _003C_003Ez__ReadOnlyArray<string>(new string[6] { "*.sps", "*.wav", "*.mp3", "*.flac", "*.ogg", "*.m4a" })
			})
		};
		IReadOnlyList<IStorageFile> files = await FileService.OpenFilesAsync(options).ConfigureAwait(continueOnCapturedContext: false);
		if (files == null || files.Count == 0)
		{
			return new SoundOperationResult(Success: false, "Sound import canceled.");
		}
		try
		{
			SoundAssetState state = Load(entry);
			string inputFilePath = files[0].Path.LocalPath;
			if (state.Kind == SoundAssetKind.Bank && state.ParsedBank != null)
			{
				int totalSegments = state.ParsedBank.Variations.Sum((Variation variation) => variation.Segments.Count);
				if (totalSegments != 1)
				{
					return new SoundOperationResult(Success: false, "This bank has multiple segments. Open the sound editor to choose which segment to replace.");
				}
				return await ImportSegmentAsync(entry, 0, 0, inputFilePath).ConfigureAwait(continueOnCapturedContext: false);
			}
			if (state.Chunks.Count == 1)
			{
				await ReplaceChunkAsync(state, 0, inputFilePath).ConfigureAwait(continueOnCapturedContext: false);
				return new SoundOperationResult(Success: true, "Imported sound data into " + entry.Filename + ".");
			}
			return new SoundOperationResult(Success: false, "This sound asset has multiple chunks. Open the sound editor to choose which chunk to replace.");
		}
		catch (Exception ex)
		{
			return new SoundOperationResult(Success: false, "Sound import failed: " + ex.Message);
		}
	}

	public static async Task<SoundOperationResult> AddWithPickerAsync(EbxAssetEntry entry)
	{
		FilePickerOpenOptions options = new FilePickerOpenOptions
		{
			Title = "Add sound",
			AllowMultiple = false,
			FileTypeFilter = new _003C_003Ez__ReadOnlySingleElementList<FilePickerFileType>(new FilePickerFileType("Audio files")
			{
				Patterns = new _003C_003Ez__ReadOnlyArray<string>(new string[6] { "*.sps", "*.wav", "*.mp3", "*.flac", "*.ogg", "*.m4a" })
			})
		};
		IReadOnlyList<IStorageFile> files = await FileService.OpenFilesAsync(options).ConfigureAwait(continueOnCapturedContext: false);
		if (files == null || files.Count == 0)
		{
			return new SoundOperationResult(Success: false, "Add sound canceled.");
		}
		return await AddSoundAsync(entry, files[0].Path.LocalPath).ConfigureAwait(continueOnCapturedContext: false);
	}

	public static async Task<SoundOperationResult> AddSoundAsync(EbxAssetEntry entry, string inputFilePath, CancellationToken cancellationToken = default(CancellationToken))
	{
		return await AddSoundAsync(entry, inputFilePath, null, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
	}

	public static async Task<SoundOperationResult> AddSoundAsync(EbxAssetEntry entry, string inputFilePath, int? templateVariationIndex, CancellationToken cancellationToken = default(CancellationToken))
	{
		try
		{
			SoundAssetState state = Load(entry);
			if (state.Kind != SoundAssetKind.Bank || state.ParsedBank == null || !state.BankEndian.HasValue)
			{
				return new SoundOperationResult(Success: false, "This sound asset does not expose an editable bank.");
			}
			int codec = ResolveReferenceCodec(state);
			ImportedSoundPayload imported = await ImportSoundPayloadAsync(state, inputFilePath, codec, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			DataSet variationsDataSet = RequireDataSet(state.ParsedBank, "Variations");
			ulong variationIndex = GetNextUInt64(variationsDataSet, "VariationIndex");
			ulong chunkIndex = (imported.HasStreamPool ? 1 : GetNextChunkIndex(state.ParsedBank));
			ulong segmentIndex = GetNextSegmentIndex(state.ParsedBank);
			uint variationId = ResolveNextVariationId(state.ParsedBank, variationIndex);
			Variation templateVariation = ResolveTemplateVariation(state.ParsedBank, templateVariationIndex);
			Variation variation = new Variation(values: (templateVariation == null) ? new Dictionary<uint, object>() : new Dictionary<uint, object>(templateVariation.Values), index: (int)variationIndex, variationId: variationId, chunkRef: new ChunkRef(chunkIndex, imported.ChunkId, imported.ChunkSize), segments: new List<Segment>(1)
			{
				new Segment((int)segmentIndex, imported.SamplesOffset, imported.SeekTableOffset, imported.Duration)
			});
			state.ParsedBank.Variations.Add(variation);
			SyncBankDataSetsFromVariations(state.ParsedBank);
			SyncRootChunkList(state.Partition, state.ParsedBank);
			PersistBankState(state, state.BankEndian.Value);
			return new SoundOperationResult(Success: true, "Added a new sound variation to " + entry.Filename + ".");
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			return new SoundOperationResult(Success: false, "Add sound failed: " + ex2.Message);
		}
	}

	public static Task<SoundOperationResult> SaveAsync(EbxAssetEntry entry)
	{
		try
		{
			return Task.FromResult(Save(Load(entry)));
		}
		catch (Exception ex)
		{
			return Task.FromResult(new SoundOperationResult(Success: false, "Sound save failed: " + ex.Message));
		}
	}

	public static Task<SoundOperationResult> SaveAsync(SoundAssetState state)
	{
		return Task.FromResult(Save(state));
	}

	public static SoundOperationResult Save(SoundAssetState state)
	{
		ArgumentNullException.ThrowIfNull(state, "state");
		try
		{
			if (state.Kind == SoundAssetKind.Bank && state.ParsedBank != null)
			{
				RebuildVariationsFromDataSets(state.ParsedBank);
				SyncBankDataSetsFromVariations(state.ParsedBank);
				SyncRootChunkList(state.Partition, state.ParsedBank);
				PersistBankState(state, state.BankEndian.GetValueOrDefault());
				return new SoundOperationResult(Success: true, "Saved sound data for " + state.Entry.Filename + ".");
			}
			AssetManager.ModifyEbx(state.Entry, state.Partition);
			AssetEditStateTracker.MarkDirty(state.Entry.Name);
			AssetEditStateTracker.MarkModified(state.Entry.Name);
			return new SoundOperationResult(Success: true, "Saved EBX sound state for " + state.Entry.Filename + ".");
		}
		catch (Exception ex)
		{
			return new SoundOperationResult(Success: false, "Sound save failed: " + ex.Message);
		}
	}

	public static Task<SoundOperationResult> AddAsync(EbxAssetEntry entry)
	{
		return AddWithPickerAsync(entry);
	}

	public static async Task<SoundOperationResult> ExportAllAsync(EbxAssetEntry entry, string outputDirectory, bool rawSpsExport = false, CancellationToken cancellationToken = default(CancellationToken))
	{
		try
		{
			SoundAssetState state = Load(entry);
			await ExportAllAsync(state, outputDirectory, rawSpsExport, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			return new SoundOperationResult(Success: true, "Exported all sounds from " + entry.Filename + ".");
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			return new SoundOperationResult(Success: false, "Bulk sound export failed: " + ex2.Message);
		}
	}

	public static async Task<SoundOperationResult> ExportAllWithPickerAsync(EbxAssetEntry entry, bool rawSpsExport = false)
	{
		FolderPickerOpenOptions options = new FolderPickerOpenOptions
		{
			Title = "Choose export folder",
			AllowMultiple = false
		};
		IReadOnlyList<IStorageFolder> folders = await FileService.OpenFoldersAsync(options).ConfigureAwait(continueOnCapturedContext: false);
		if (folders == null || folders.Count == 0)
		{
			return new SoundOperationResult(Success: false, "Bulk sound export canceled.");
		}
		string outputDirectory = Path.Combine(folders[0].Path.LocalPath, SafeName(entry.Name));
		return await ExportAllAsync(entry, outputDirectory, rawSpsExport).ConfigureAwait(continueOnCapturedContext: false);
	}

	public static async Task<SoundOperationResult> ImportAllAsync(EbxAssetEntry entry, string inputDirectory, bool addMissingVariations = false, CancellationToken cancellationToken = default(CancellationToken))
	{
		try
		{
			if (!Directory.Exists(inputDirectory))
			{
				return new SoundOperationResult(Success: false, "Import directory '" + inputDirectory + "' does not exist.");
			}
			List<string> inputFiles = EnumerateImportFiles(inputDirectory).ToList();
			if (inputFiles.Count == 0)
			{
				return new SoundOperationResult(Success: false, "No supported sound files were found in '" + inputDirectory + "'.");
			}
			SoundAssetState state = Load(entry);
			int importedCount = 0;
			if (state.Kind == SoundAssetKind.Bank && state.ParsedBank != null)
			{
				Dictionary<(int VariationIndex, int SegmentIndex), string> segmentImports = BuildSegmentImportMap(inputFiles);
				foreach (Variation variation in state.ParsedBank.Variations.OrderBy((Variation variation2) => variation2.Index))
				{
					foreach (Segment segment in variation.Segments.OrderBy((Segment segment2) => segment2.Index))
					{
						if (segmentImports.TryGetValue((variation.Index, segment.Index), out string path))
						{
							SoundOperationResult importResult = await ImportSegmentAsync(entry, variation.Index, segment.Index, path, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							if (!importResult.Success)
							{
								return (importedCount > 0) ? new SoundOperationResult(Success: false, $"{importResult.Message} Imported {importedCount} item(s) before the failure.") : importResult;
							}
							importedCount++;
							path = null;
						}
					}
				}
				if (addMissingVariations)
				{
					int maxExistingVariationIndex = ((state.ParsedBank.Variations.Count == 0) ? (-1) : state.ParsedBank.Variations.Max((Variation variation2) => variation2.Index));
					foreach (KeyValuePair<(int, int), string> item in from pair in segmentImports
						orderby pair.Key.VariationIndex, pair.Key.SegmentIndex
						select pair)
					{
						item.Deconstruct(out var key, out var value);
						(int, int) tuple = key;
						int variationIndex = tuple.Item1;
						int segmentIndex = tuple.Item2;
						string path2 = value;
						if (variationIndex > maxExistingVariationIndex && segmentIndex == 0)
						{
							SoundOperationResult addResult = await AddSoundAsync(entry, path2, Math.Max(maxExistingVariationIndex, 0), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							if (!addResult.Success)
							{
								return (importedCount > 0) ? new SoundOperationResult(Success: false, $"{addResult.Message} Imported {importedCount} item(s) before the failure.") : addResult;
							}
							importedCount++;
							maxExistingVariationIndex++;
						}
					}
				}
				if (importedCount == 0 && state.ParsedBank.Variations.Count == 1 && state.ParsedBank.Variations[0].Segments.Count == 1 && inputFiles.Count == 1)
				{
					return await ImportSegmentAsync(entry, 0, 0, inputFiles[0], cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
			}
			else
			{
				Dictionary<int, string> chunkImports = BuildChunkImportMap(inputFiles);
				foreach (SoundChunkBinding chunk in state.Chunks.OrderBy((SoundChunkBinding soundChunkBinding) => soundChunkBinding.Index))
				{
					if (chunkImports.TryGetValue(chunk.Index, out string path3))
					{
						SoundAssetState currentState = Load(entry);
						int index = chunk.Index;
						string inputFilePath = path3;
						CancellationToken cancellationToken2 = cancellationToken;
						await ReplaceChunkAsync(currentState, index, inputFilePath, addAsNewChunk: false, null, seekable: false, cancellationToken2).ConfigureAwait(continueOnCapturedContext: false);
						importedCount++;
						path3 = null;
					}
				}
				if (importedCount == 0 && state.Chunks.Count == 1 && inputFiles.Count == 1)
				{
					string inputFilePath2 = inputFiles[0];
					CancellationToken cancellationToken2 = cancellationToken;
					await ReplaceChunkAsync(state, 0, inputFilePath2, addAsNewChunk: false, null, seekable: false, cancellationToken2).ConfigureAwait(continueOnCapturedContext: false);
					importedCount = 1;
				}
			}
			return (importedCount == 0) ? new SoundOperationResult(Success: false, "No matching sound files were found for " + entry.Filename + ".") : new SoundOperationResult(Success: true, $"Imported {importedCount} sound item(s) into {entry.Filename}.");
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			return new SoundOperationResult(Success: false, "Bulk sound import failed: " + ex2.Message);
		}
	}

	public static async Task<SoundOperationResult> ImportAllWithPickerAsync(EbxAssetEntry entry, bool addMissingVariations = false)
	{
		FolderPickerOpenOptions options = new FolderPickerOpenOptions
		{
			Title = "Choose import folder",
			AllowMultiple = false
		};
		IReadOnlyList<IStorageFolder> folders = await FileService.OpenFoldersAsync(options).ConfigureAwait(continueOnCapturedContext: false);
		if (folders == null || folders.Count == 0)
		{
			return new SoundOperationResult(Success: false, "Bulk sound import canceled.");
		}
		return await ImportAllAsync(entry, folders[0].Path.LocalPath, addMissingVariations).ConfigureAwait(continueOnCapturedContext: false);
	}

	public static void SetDataSetValue(SoundAssetState state, string dataSetIdentifier, int rowIndex, string fieldIdentifier, object? value)
	{
		ArgumentNullException.ThrowIfNull(state, "state");
		ArgumentNullException.ThrowIfNull(state.ParsedBank, "state.ParsedBank");
		DataSet dataSet = ResolveDataSet(state.ParsedBank, dataSetIdentifier);
		Field field = ResolveField(dataSet, fieldIdentifier);
		SetFieldValue(dataSet, field.Id, rowIndex, value ?? GetDefaultFieldValue(field));
		ApplyDataSetMutation(state.ParsedBank, dataSet);
	}

	public static void SetDataSetValueText(SoundAssetState state, string dataSetIdentifier, int rowIndex, string fieldIdentifier, string? valueText)
	{
		ArgumentNullException.ThrowIfNull(state, "state");
		ArgumentNullException.ThrowIfNull(state.ParsedBank, "state.ParsedBank");
		DataSet dataSet = ResolveDataSet(state.ParsedBank, dataSetIdentifier);
		Field field = ResolveField(dataSet, fieldIdentifier);
		SetFieldValue(dataSet, field.Id, rowIndex, ConvertTextValue(field, valueText));
		ApplyDataSetMutation(state.ParsedBank, dataSet);
	}

	public static int AddDataSetRow(SoundAssetState state, string dataSetIdentifier, IReadOnlyDictionary<string, object?>? values = null)
	{
		ArgumentNullException.ThrowIfNull(state, "state");
		ArgumentNullException.ThrowIfNull(state.ParsedBank, "state.ParsedBank");
		DataSet dataSet = ResolveDataSet(state.ParsedBank, dataSetIdentifier);
		int num = AppendDefaultRow(dataSet);
		if (values != null)
		{
			foreach (KeyValuePair<string, object> value2 in values)
			{
				value2.Deconstruct(out var key, out var value);
				string identifier = key;
				object obj = value;
				Field field = ResolveField(dataSet, identifier);
				SetFieldValue(dataSet, field.Id, num, obj ?? GetDefaultFieldValue(field));
			}
		}
		ApplyDataSetMutation(state.ParsedBank, dataSet);
		return num;
	}

	public static void RemoveDataSetRow(SoundAssetState state, string dataSetIdentifier, int rowIndex)
	{
		ArgumentNullException.ThrowIfNull(state, "state");
		ArgumentNullException.ThrowIfNull(state.ParsedBank, "state.ParsedBank");
		DataSet dataSet = ResolveDataSet(state.ParsedBank, dataSetIdentifier);
		RemoveRow(dataSet, rowIndex);
		ApplyDataSetMutation(state.ParsedBank, dataSet);
	}

	public static Task<SoundOperationResult> RevertAsync(EbxAssetEntry entry)
	{
		return Task.FromResult(Revert(entry));
	}

	public static SoundOperationResult Revert(EbxAssetEntry entry)
	{
		bool flag = AssetManager.RevertEbx(entry.Name);
		try
		{
			SoundAssetState soundAssetState = Load(entry);
			if (soundAssetState.ResEntry != null)
			{
				flag |= AssetManager.RevertRes(soundAssetState.ResEntry.ResRid);
			}
			Guid? bankContainerChunkId = soundAssetState.BankContainerChunkId;
			if (bankContainerChunkId.HasValue)
			{
				Guid valueOrDefault = bankContainerChunkId.GetValueOrDefault();
				if (true)
				{
					flag |= AssetManager.RevertChunk(valueOrDefault);
				}
			}
			foreach (SoundChunkBinding chunk in soundAssetState.Chunks)
			{
				flag |= AssetManager.RevertChunk(chunk.ChunkId);
			}
		}
		catch
		{
		}
		if (!flag)
		{
			return new SoundOperationResult(Success: false, entry.Filename + " has no pending sound edits to revert.");
		}
		AssetEditStateTracker.ClearDirty(entry.Name);
		AssetEditStateTracker.ClearModified(entry.Name);
		return new SoundOperationResult(Success: true, "Reverted " + entry.Filename + " to the original game sound data.");
	}

	public static async Task ExportAllAsync(SoundAssetState state, string outputDirectory, bool rawSpsExport = false, CancellationToken cancellationToken = default(CancellationToken))
	{
		ArgumentNullException.ThrowIfNull(state, "state");
		ArgumentNullException.ThrowIfNull(outputDirectory, "outputDirectory");
		Directory.CreateDirectory(outputDirectory);
		if (state.Kind == SoundAssetKind.Bank && state.ParsedBank != null)
		{
			for (int variationIndex = 0; variationIndex < state.ParsedBank.Variations.Count; variationIndex++)
			{
				Variation variation = state.ParsedBank.Variations[variationIndex];
				for (int segmentIndex = 0; segmentIndex < variation.Segments.Count; segmentIndex++)
				{
					string filePath = Path.Combine(outputDirectory, $"{SafeName(state.Entry.Name)}_v{variationIndex:0000}_s{segmentIndex:0000}{(rawSpsExport ? ".sps" : ".wav")}");
					await ExportSegmentAsync(state, variationIndex, segmentIndex, filePath, rawSpsExport, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
			}
		}
		else
		{
			for (int i = 0; i < state.Chunks.Count; i++)
			{
				string filePath2 = Path.Combine(outputDirectory, $"{SafeName(state.Entry.Name)}_{i:0000}{(rawSpsExport ? ".sps" : ".wav")}");
				await ExportChunkAsync(state, i, filePath2, rawSpsExport, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			}
		}
	}

	public static async Task<SoundOperationResult> ExportBankSegmentsAsync(EbxAssetEntry entry, string outputDirectory, bool rawSpsExport = false, CancellationToken cancellationToken = default(CancellationToken))
	{
		try
		{
			SoundAssetState state = Load(entry);
			if (state.Kind != SoundAssetKind.Bank || state.ParsedBank == null)
			{
				return new SoundOperationResult(Success: false, "This sound asset does not expose bank segments.");
			}
			await ExportBankSegmentsAsync(state, outputDirectory, rawSpsExport, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			int segmentCount = EnumerateSegmentLocations(state.ParsedBank).Count;
			return new SoundOperationResult(Success: true, $"Exported {segmentCount:N0} bank segment(s) to '{outputDirectory}'.");
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			return new SoundOperationResult(Success: false, "Bulk export failed: " + ex2.Message);
		}
	}

	public static async Task<SoundOperationResult> ImportBankSegmentsAsync(EbxAssetEntry entry, IEnumerable<string> inputFilePaths, CancellationToken cancellationToken = default(CancellationToken))
	{
		try
		{
			SoundAssetState state = Load(entry);
			if (state.Kind != SoundAssetKind.Bank || state.ParsedBank == null || !state.BankEndian.HasValue)
			{
				return new SoundOperationResult(Success: false, "This sound asset does not expose an editable bank.");
			}
			string validationError;
			IReadOnlyList<BulkSoundImportCandidate> candidates = BuildBulkImportCandidates(inputFilePaths, out validationError);
			if (candidates.Count == 0)
			{
				return new SoundOperationResult(Success: false, validationError);
			}
			IReadOnlyList<SoundSegmentLocation> segmentLocations = EnumerateSegmentLocations(state.ParsedBank);
			if (!ValidateBulkImportSequenceSet(segmentLocations.Count, candidates, out validationError))
			{
				return new SoundOperationResult(Success: false, validationError);
			}
			int imported = 0;
			int added = 0;
			int errored = 0;
			int currentSegmentCount = segmentLocations.Count;
			foreach (BulkSoundImportCandidate candidate in candidates)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (candidate.SequenceNumber < currentSegmentCount)
				{
					SoundSegmentLocation location = segmentLocations[candidate.SequenceNumber];
					if ((await ImportSegmentAsync(entry, location.VariationIndex, location.SegmentIndex, candidate.FilePath, cancellationToken).ConfigureAwait(continueOnCapturedContext: false)).Success)
					{
						imported++;
					}
					else
					{
						errored++;
					}
				}
				else if (candidate.SequenceNumber == currentSegmentCount)
				{
					if ((await AddSoundAsync(entry, candidate.FilePath, cancellationToken).ConfigureAwait(continueOnCapturedContext: false)).Success)
					{
						added++;
						currentSegmentCount++;
					}
					else
					{
						errored++;
					}
				}
				else
				{
					errored++;
				}
			}
			bool success = imported > 0 || added > 0;
			return new SoundOperationResult(success, $"Bulk import completed: {imported:N0} imported, {added:N0} added, {errored:N0} errored.");
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			return new SoundOperationResult(Success: false, "Bulk import failed: " + ex2.Message);
		}
	}

	public static Task<SoundOperationResult> ImportBankSegmentsFromDirectoryAsync(EbxAssetEntry entry, string inputDirectory, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (string.IsNullOrWhiteSpace(inputDirectory))
		{
			return Task.FromResult(new SoundOperationResult(Success: false, "Input directory is required."));
		}
		if (!Directory.Exists(inputDirectory))
		{
			return Task.FromResult(new SoundOperationResult(Success: false, "Input directory '" + inputDirectory + "' does not exist."));
		}
		IEnumerable<string> inputFilePaths = Directory.EnumerateFiles(inputDirectory, "*", SearchOption.TopDirectoryOnly).Where(IsSupportedBulkImportFile);
		return ImportBankSegmentsAsync(entry, inputFilePaths, cancellationToken);
	}

	public static async Task ExportSegmentAsync(SoundAssetState state, int variationIndex, int segmentIndex, string outputFilePath, bool rawSpsExport = false, CancellationToken cancellationToken = default(CancellationToken))
	{
		ArgumentNullException.ThrowIfNull(state, "state");
		ArgumentNullException.ThrowIfNull(outputFilePath, "outputFilePath");
		if (state.Kind != SoundAssetKind.Bank || state.ParsedBank == null)
		{
			throw new InvalidOperationException("The selected asset does not expose bank segments.");
		}
		Variation variation = state.ParsedBank.Variations[variationIndex];
		Segment segment = variation.Segments[segmentIndex];
		ChunkAssetEntry chunkEntry = RequireChunkEntry(variation.ChunkRef.ChunkId);
		await using MemoryStream chunkStream = new MemoryStream(AssetManager.GetAsset(chunkEntry).ToArray(), writable: false);
		await ExportSegmentFromChunkAsync(chunkStream, segment, outputFilePath, rawSpsExport, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
	}

	public static async Task ExportChunkAsync(SoundAssetState state, int chunkIndex, string outputFilePath, bool rawSpsExport = false, CancellationToken cancellationToken = default(CancellationToken))
	{
		ArgumentNullException.ThrowIfNull(state, "state");
		ArgumentNullException.ThrowIfNull(outputFilePath, "outputFilePath");
		SoundChunkBinding chunk = state.Chunks[chunkIndex];
		ChunkAssetEntry chunkEntry = chunk.ChunkEntry ?? RequireChunkEntry(chunk.ChunkId);
		await using MemoryStream rawStream = new MemoryStream(AssetManager.GetAsset(chunkEntry).ToArray(), writable: false);
		if (state.Kind == SoundAssetKind.Bank && state.ParsedBank != null)
		{
			Variation variation = state.ParsedBank.Variations.FirstOrDefault((Variation v) => v.ChunkRef.ChunkId == chunk.ChunkId);
			if (variation != null)
			{
				await ExportVariationChunkAsync(rawStream, variation, outputFilePath, rawSpsExport, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				return;
			}
		}
		await ExportRawChunkAsync(rawStream, outputFilePath, rawSpsExport, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
	}

	public static async Task ReplaceChunkAsync(SoundAssetState state, int chunkIndex, string inputFilePath, bool addAsNewChunk = false, int? codec = null, bool seekable = false, CancellationToken cancellationToken = default(CancellationToken))
	{
		ArgumentNullException.ThrowIfNull(state, "state");
		ArgumentNullException.ThrowIfNull(inputFilePath, "inputFilePath");
		SoundChunkBinding chunk = state.Chunks[chunkIndex];
		byte[] payload = await LoadPayloadAsync(inputFilePath, codec, seekable, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		if (state.Kind == SoundAssetKind.Bank && state.ParsedBank != null && state.BankContainerChunkId.HasValue && state.BankEndian.HasValue)
		{
			Guid newChunkId;
			byte[] bankBytes = RewriteBank(state.ParsedBank, chunk.Index, payload, addAsNewChunk, state.BankEndian.Value, out newChunkId);
			if (addAsNewChunk)
			{
				AssetManager.AddChunk(payload, newChunkId);
			}
			ApplyBankBytes(state, bankBytes);
			AssetEditStateTracker.MarkDirty(state.Entry.Name);
			AssetEditStateTracker.MarkModified(state.Entry.Name);
		}
		else if (addAsNewChunk)
		{
			Guid newChunkId2 = Guid.NewGuid();
			AssetManager.AddChunk(payload, newChunkId2);
			if (!TryRelinkChunk(state.Partition, chunk.SourceObject, newChunkId2, payload.Length))
			{
				throw new InvalidOperationException("Unable to relink the EBX chunk reference.");
			}
			AssetManager.ModifyEbx(state.Entry, state.Partition);
			AssetEditStateTracker.MarkDirty(state.Entry.Name);
			AssetEditStateTracker.MarkModified(state.Entry.Name);
		}
		else
		{
			AssetManager.ModifyChunk(chunk.ChunkId, payload);
			AssetEditStateTracker.MarkDirty(state.Entry.Name);
			AssetEditStateTracker.MarkModified(state.Entry.Name);
		}
	}

	public static async Task ReplaceSegmentAsync(SoundAssetState state, int variationIndex, int segmentIndex, string inputFilePath, bool addAsNewChunk = false, int? codec = null, bool seekable = false, CancellationToken cancellationToken = default(CancellationToken))
	{
		ArgumentNullException.ThrowIfNull(state, "state");
		ArgumentNullException.ThrowIfNull(inputFilePath, "inputFilePath");
		if (state.Kind != SoundAssetKind.Bank || state.ParsedBank == null || !state.BankEndian.HasValue || !state.BankContainerChunkId.HasValue)
		{
			throw new InvalidOperationException("The selected asset does not expose a writable bank.");
		}
		_ = state.ParsedBank.Variations[variationIndex].Segments[segmentIndex];
		byte[] payload = await LoadPayloadAsync(inputFilePath, codec, seekable, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		Guid newChunkId;
		byte[] bankBytes = RewriteBank(state.ParsedBank, variationIndex, payload, addAsNewChunk, state.BankEndian.Value, out newChunkId);
		if (addAsNewChunk)
		{
			AssetManager.AddChunk(payload, newChunkId);
		}
		ApplyBankBytes(state, bankBytes);
		AssetEditStateTracker.MarkDirty(state.Entry.Name);
		AssetEditStateTracker.MarkModified(state.Entry.Name);
	}

	public static void ApplyResourceBytes(ulong resRid, byte[] buffer, byte[]? meta = null)
	{
		AssetManager.ModifyRes(resRid, buffer, meta);
	}

	public static void ApplyEbx(EbxAssetEntry entry, EbxPartition partition)
	{
		AssetManager.ModifyEbx(entry, partition);
	}

	public static async Task<SoundOperationResult> ImportSegmentAsync(EbxAssetEntry entry, int variationIndex, int segmentIndex, string inputFilePath, CancellationToken cancellationToken = default(CancellationToken))
	{
		try
		{
			SoundAssetState state = Load(entry);
			if (state.Kind != SoundAssetKind.Bank || state.ParsedBank == null || !state.BankEndian.HasValue)
			{
				return new SoundOperationResult(Success: false, "This sound asset does not expose an editable bank.");
			}
			Variation variation = state.ParsedBank.Variations[variationIndex];
			Segment segment = variation.Segments[segmentIndex];
			SpsSoundHeader currentHeader = ReadSpsHeader(variation.ChunkRef.ChunkId, segment);
			(byte[] SpsData, byte[]? SeekTableData) tuple = await LoadReplacementPayloadAsync(inputFilePath, currentHeader.Codec, (segment.SeekTableOffset & 1) != 0, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			byte[] spsData = tuple.SpsData;
			byte[] seekTableData = tuple.SeekTableData;
			(byte[], List<Segment>) tuple2 = RebuildVariationChunk(variation, segmentIndex, spsData, seekTableData);
			byte[] chunkBytes = tuple2.Item1;
			List<Segment> rewrittenSegments = tuple2.Item2;
			Guid replacementChunkId = Guid.NewGuid();
			CopyChunkPlacement(replacementChunk: AssetManager.AddChunk(chunkBytes, replacementChunkId), sourceChunkId: variation.ChunkRef.ChunkId);
			Variation replacementVariation = new Variation(variation.Index, variation.VariationId, new ChunkRef(variation.ChunkRef.ChunkIndex, replacementChunkId, chunkBytes.Length), rewrittenSegments, new Dictionary<uint, object>(variation.Values));
			state.ParsedBank.Variations[variationIndex] = replacementVariation;
			SyncBankDataSetsFromVariations(state.ParsedBank);
			SyncRootChunkList(state.Partition, state.ParsedBank);
			PersistBankState(state, state.BankEndian.Value);
			return new SoundOperationResult(Success: true, $"Imported segment {segmentIndex} for {entry.Filename}.");
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			return new SoundOperationResult(Success: false, "Sound import failed: " + ex2.Message);
		}
	}

	private static void ApplyBankBytes(SoundAssetState state, byte[] bankBytes)
	{
		if (state.BankContainerChunkId.HasValue)
		{
			AssetManager.ModifyChunk(state.BankContainerChunkId.Value, bankBytes);
			return;
		}
		if (state.ResEntry != null)
		{
			AssetManager.ModifyRes(state.ResEntry.ResRid, bankBytes, state.ResMeta);
			return;
		}
		throw new InvalidOperationException("Unable to resolve the bank payload target.");
	}

	private static async Task ExportBankSegmentsAsync(SoundAssetState state, string outputDirectory, bool rawSpsExport, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(state, "state");
		ArgumentNullException.ThrowIfNull(outputDirectory, "outputDirectory");
		if (state.Kind != SoundAssetKind.Bank || state.ParsedBank == null)
		{
			throw new InvalidOperationException("The selected asset does not expose bank segments.");
		}
		Directory.CreateDirectory(outputDirectory);
		IReadOnlyList<SoundSegmentLocation> segmentLocations = EnumerateSegmentLocations(state.ParsedBank);
		foreach (SoundSegmentLocation location in segmentLocations)
		{
			cancellationToken.ThrowIfCancellationRequested();
			await ExportSegmentAsync(outputFilePath: Path.Combine(outputDirectory, $"{location.SequenceNumber:0000}{(rawSpsExport ? ".sps" : ".wav")}"), state: state, variationIndex: location.VariationIndex, segmentIndex: location.SegmentIndex, rawSpsExport: rawSpsExport, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		}
	}

	private static void PersistBankState(SoundAssetState state, Endian endian)
	{
		using MemoryStream memoryStream = new MemoryStream();
		NewWaveAssetSampleBankWriter newWaveAssetSampleBankWriter = new NewWaveAssetSampleBankWriter();
		if (state.ResEntry != null)
		{
			newWaveAssetSampleBankWriter.WriteUnprocessed(memoryStream, state.ParsedBank, endian, out byte[] resMeta);
			AssetManager.ModifyRes(state.ResEntry.ResRid, memoryStream.ToArray(), resMeta);
			goto IL_009b;
		}
		Guid? bankContainerChunkId = state.BankContainerChunkId;
		if (bankContainerChunkId.HasValue)
		{
			Guid valueOrDefault = bankContainerChunkId.GetValueOrDefault();
			if (true)
			{
				newWaveAssetSampleBankWriter.WriteUnprocessed(memoryStream, state.ParsedBank, endian);
				AssetManager.ModifyChunk(valueOrDefault, memoryStream.ToArray());
				goto IL_009b;
			}
		}
		throw new InvalidOperationException("Unable to resolve the target bank payload.");
		IL_009b:
		AssetManager.ModifyEbx(state.Entry, state.Partition);
		AssetEditStateTracker.MarkDirty(state.Entry.Name);
		AssetEditStateTracker.MarkModified(state.Entry.Name);
	}

	private static IReadOnlyList<SoundChunkBinding> ResolveChunks(object rootObject)
	{
		List<SoundChunkBinding> list = new List<SoundChunkBinding>();
		object memberValue = GetMemberValue(rootObject, "Chunks");
		if (memberValue is IEnumerable enumerable && !(memberValue is string))
		{
			int num = 0;
			foreach (object item in enumerable)
			{
				if (item == null)
				{
					num++;
					continue;
				}
				Guid guid = TryGetGuid(item, "ChunkId", "Id", "Guid") ?? Guid.Empty;
				long valueOrDefault = TryGetLong(item, "ChunkSize", "Size", "LogicalSize").GetValueOrDefault();
				string name = TryGetString(item, "Name", "DisplayName") ?? $"Chunk {num}";
				list.Add(new SoundChunkBinding(num, guid, valueOrDefault, name, item, (guid == Guid.Empty) ? null : AssetManager.GetChunkAssetEntry(guid)));
				num++;
			}
		}
		if (list.Count == 0)
		{
			Guid? guid2 = TryGetGuid(rootObject, "ChunkId", "Id", "Guid");
			if (guid2.HasValue && guid2.Value != Guid.Empty)
			{
				ChunkAssetEntry chunkAssetEntry = AssetManager.GetChunkAssetEntry(guid2.Value);
				list.Add(new SoundChunkBinding(0, guid2.Value, chunkAssetEntry?.OriginalSize ?? 0, chunkAssetEntry?.Name ?? "Chunk 0", rootObject, chunkAssetEntry));
			}
		}
		return list;
	}

	private static IReadOnlyList<SoundChunkBinding> ResolveLinkedChunks(ResAssetEntry resEntry)
	{
		List<SoundChunkBinding> list = new List<SoundChunkBinding>();
		HashSet<Guid> hashSet = new HashSet<Guid>();
		object memberValue = GetMemberValue(resEntry, "LinkedAssets", "LinkedEntries", "Assets");
		if (!(memberValue is IEnumerable enumerable))
		{
			return list;
		}
		foreach (object item in enumerable)
		{
			if (item is ChunkAssetEntry chunkAssetEntry && hashSet.Add(chunkAssetEntry.Id))
			{
				list.Add(new SoundChunkBinding(list.Count, chunkAssetEntry.Id, chunkAssetEntry.OriginalSize, string.IsNullOrWhiteSpace(chunkAssetEntry.Name) ? $"Chunk {list.Count}" : chunkAssetEntry.Name, chunkAssetEntry, chunkAssetEntry));
			}
		}
		return list;
	}

	private static IReadOnlyList<SoundChunkBinding> ResolveBankVariationChunks(NewWaveBank bank)
	{
		List<SoundChunkBinding> list = new List<SoundChunkBinding>();
		HashSet<Guid> hashSet = new HashSet<Guid>();
		foreach (ChunkRef item in from variation in bank.Variations
			select variation.ChunkRef into chunk
			where chunk.ChunkId != Guid.Empty
			orderby chunk.ChunkIndex
			select chunk)
		{
			if (hashSet.Add(item.ChunkId))
			{
				ChunkAssetEntry chunkAssetEntry = AssetManager.GetChunkAssetEntry(item.ChunkId);
				list.Add(new SoundChunkBinding((int)item.ChunkIndex, item.ChunkId, item.ChunkSize, chunkAssetEntry?.Name ?? $"Chunk {item.ChunkIndex}", item, chunkAssetEntry));
			}
		}
		return list;
	}

	private static void RepairBankChunkRefs(NewWaveBank bank, IReadOnlyList<SoundChunkBinding> chunks)
	{
		Dictionary<ulong, SoundChunkBinding> dictionary = (from chunk in chunks
			where chunk.ChunkId != Guid.Empty
			group chunk by (ulong)Math.Max(chunk.Index, 0)).ToDictionary((IGrouping<ulong, SoundChunkBinding> group) => group.Key, (IGrouping<ulong, SoundChunkBinding> group) => group.First());
		if (dictionary.Count == 0 && chunks.Count == 1 && chunks[0].ChunkId != Guid.Empty)
		{
			dictionary[0uL] = chunks[0];
		}
		for (int num = 0; num < bank.Variations.Count; num++)
		{
			Variation variation = bank.Variations[num];
			ChunkRef chunkRef = variation.ChunkRef;
			if (chunkRef.ChunkId != Guid.Empty && chunkRef.ChunkSize > 0)
			{
				continue;
			}
			if (!dictionary.TryGetValue(chunkRef.ChunkIndex, out var value))
			{
				if (chunks.Count != 1 || chunks[0].ChunkId == Guid.Empty)
				{
					continue;
				}
				value = chunks[0];
			}
			if ((object)value != null)
			{
				bank.Variations[num] = new Variation(variation.Index, variation.VariationId, new ChunkRef(chunkRef.ChunkIndex, value.ChunkId, value.ChunkSize), variation.Segments, new Dictionary<uint, object>(variation.Values));
			}
		}
	}

	private static IEnumerable<SoundChunkBinding> EnumerateBankCandidates(object rootObject, IReadOnlyList<SoundChunkBinding> chunks)
	{
		int? ramIndex = TryGetInt(rootObject, "RamChunkIndex");
		int? streamIndex = TryGetInt(rootObject, "StreamChunkIndex");
		HashSet<int> order = new HashSet<int>();
		if (ramIndex.HasValue)
		{
			order.Add(ramIndex.Value);
		}
		if (streamIndex.HasValue)
		{
			order.Add(streamIndex.Value);
		}
		for (int i = 0; i < chunks.Count; i++)
		{
			order.Add(i);
		}
		foreach (int index in order)
		{
			if (index >= 0 && index < chunks.Count)
			{
				yield return chunks[index];
			}
		}
	}

	private static ResAssetEntry? ResolveResEntry(object rootObject, string? assetName = null, int depth = 0)
	{
		if (depth > 4)
		{
			return null;
		}
		ulong? num = ResolveResRid(rootObject);
		if (num.HasValue)
		{
			return AssetManager.GetResAssetEntry(num.Value);
		}
		object memberValue = GetMemberValue(rootObject, "SharedData");
		if (memberValue is PointerRef { Type: PointerRefType.External } pointerRef)
		{
			EbxAssetEntry ebxAssetEntry = AssetManager.GetEbxAssetEntry(pointerRef.External.PartitionGuid);
			if (ebxAssetEntry != null)
			{
				ResAssetEntry resAssetEntry = AssetManager.GetResAssetEntry(ebxAssetEntry.Name);
				if (resAssetEntry != null)
				{
					return resAssetEntry;
				}
			}
		}
		object obj = ResolvePointerTarget(memberValue);
		if (obj != null && obj != rootObject)
		{
			ResAssetEntry resAssetEntry2 = ResolveResEntry(obj, null, depth + 1);
			if (resAssetEntry2 != null)
			{
				return resAssetEntry2;
			}
		}
		return (depth == 0 && !string.IsNullOrWhiteSpace(assetName)) ? AssetManager.GetResAssetEntry(assetName) : null;
	}

	private static ulong? ResolveResRid(object rootObject)
	{
		ulong? num = TryGetULong(rootObject, "ResRid", "ResId", "ResourceRid", "Rid");
		ulong? num2 = num;
		if (!num2.HasValue)
		{
			num = TryGetULong(rootObject, "Resource");
		}
		if (num.HasValue)
		{
			return num;
		}
		object obj = ResolvePointerTarget(GetMemberValue(rootObject, "Resource"));
		return (obj == null) ? ((ulong?)null) : TryGetULong(obj, "ResRid", "ResId", "ResourceRid", "Rid", "Value");
	}

	private static ChunkAssetEntry RequireChunkEntry(Guid chunkId)
	{
		ChunkAssetEntry chunkAssetEntry = AssetManager.GetChunkAssetEntry(chunkId);
		if (chunkAssetEntry == null)
		{
			throw new InvalidOperationException($"Unable to resolve chunk '{chunkId}'.");
		}
		return chunkAssetEntry;
	}

	private static async Task ExportVariationChunkAsync(Stream chunkStream, Variation variation, string outputFilePath, bool rawSpsExport, CancellationToken cancellationToken)
	{
		await ExportSegmentFromChunkAsync(chunkStream, variation.Segments[0], outputFilePath, rawSpsExport, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
	}

	private static async Task ExportSegmentFromChunkAsync(Stream chunkStream, Segment segment, string outputFilePath, bool rawSpsExport, CancellationToken cancellationToken)
	{
		if (!chunkStream.CanSeek)
		{
			throw new InvalidOperationException("The source chunk stream must be seekable.");
		}
		chunkStream.Position = (uint)((int)segment.SamplesOffset & -4);
		if (ReadInt32BigEndian(chunkStream) >> 24 != 72)
		{
			throw new InvalidDataException("Wrong SPS header.");
		}
		if (rawSpsExport)
		{
			if ((segment.SeekTableOffset & 1) != 0)
			{
				uint seekOffset = segment.SeekTableOffset & 0xFFFFFFFCu;
				uint count = (segment.SamplesOffset & 0xFFFFFFFCu) - seekOffset;
				if (count != 0)
				{
					await using FileStream sek = new FileStream(Path.ChangeExtension(outputFilePath, ".sek"), new FileStreamOptions
					{
						Mode = FileMode.Create,
						Access = FileAccess.Write,
						Share = FileShare.Read,
						Options = FileOptions.Asynchronous
					});
					chunkStream.Position = seekOffset;
					await CopyCountAsync(chunkStream, sek, count, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
			}
			await using FileStream sps = new FileStream(outputFilePath, new FileStreamOptions
			{
				Mode = FileMode.Create,
				Access = FileAccess.Write,
				Share = FileShare.Read,
				Options = FileOptions.Asynchronous
			});
			await CopySpsAsync(chunkStream, segment.SamplesOffset, sps, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			return;
		}
		string tempSps = TempFile(".sps");
		try
		{
			await using FileStream sps2 = new FileStream(tempSps, new FileStreamOptions
			{
				Mode = FileMode.Create,
				Access = FileAccess.Write,
				Share = FileShare.Read,
				Options = FileOptions.Asynchronous
			});
			await CopySpsAsync(chunkStream, segment.SamplesOffset, sps2, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			await sps2.FlushAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			await VgmstreamHelper.DecodeAsync(tempSps, outputFilePath, subsongs: false, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		}
		finally
		{
			TryDelete(tempSps);
		}
	}

	private static async Task ExportRawChunkAsync(Stream chunkStream, string outputFilePath, bool rawSpsExport, CancellationToken cancellationToken)
	{
		byte[] chunkBytes = await ReadStreamBytesAsync(chunkStream, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		IReadOnlyList<SpsCandidate> candidates = GetSpsCandidates(chunkBytes);
		checked
		{
			if (rawSpsExport)
			{
				await using (FileStream output = new FileStream(outputFilePath, new FileStreamOptions
				{
					Mode = FileMode.Create,
					Access = FileAccess.Write,
					Share = FileShare.Read,
					Options = FileOptions.Asynchronous
				}))
				{
					int offset = ((candidates.Count > 0) ? ((int)candidates[0].Offset) : 0);
					await output.WriteAsync(chunkBytes.AsMemory(offset), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
					return;
				}
			}
			string tempSps = TempFile(".sps");
			Exception lastException = null;
			try
			{
				IEnumerable<long> offsets = candidates.Select((SpsCandidate candidate) => candidate.Offset);
				if (!offsets.Any())
				{
					offsets = new _003C_003Ez__ReadOnlySingleElementList<long>(0L);
				}
				foreach (long offset2 in offsets.Distinct())
				{
					await using FileStream sps = new FileStream(tempSps, new FileStreamOptions
					{
						Mode = FileMode.Create,
						Access = FileAccess.Write,
						Share = FileShare.Read,
						Options = FileOptions.Asynchronous
					});
					await sps.WriteAsync(chunkBytes.AsMemory((int)offset2), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
					await sps.FlushAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
					try
					{
						await VgmstreamHelper.DecodeAsync(tempSps, outputFilePath, subsongs: false, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
					}
					catch (Exception ex)
					{
						Exception ex2 = ex;
						lastException = ex2;
						goto end_IL_03f9;
					}
					return;
					end_IL_03f9:;
				}
				throw lastException ?? new InvalidDataException("Unable to locate a playable SPS payload in the chunk.");
			}
			finally
			{
				TryDelete(tempSps);
			}
		}
	}

	private static async Task<byte[]> LoadPayloadAsync(string inputFilePath, int? codec, bool seekable, CancellationToken cancellationToken)
	{
		string extension = Path.GetExtension(inputFilePath);
		if (extension.Equals(".sps", StringComparison.OrdinalIgnoreCase) || extension.Equals(".sek", StringComparison.OrdinalIgnoreCase) || extension.Equals(".sbr", StringComparison.OrdinalIgnoreCase) || extension.Equals(".sbs", StringComparison.OrdinalIgnoreCase))
		{
			return await File.ReadAllBytesAsync(inputFilePath, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		}
		string tempSps = TempFile(".sps");
		try
		{
			await SoundExchangeHelper.ConvertAsync(inputFilePath, tempSps, codec, seekable, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			return await File.ReadAllBytesAsync(tempSps, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		}
		finally
		{
			TryDelete(tempSps);
		}
	}

	private static byte[] RewriteBank(NewWaveBank bank, int variationIndex, byte[] payload, bool addAsNewChunk, Endian endian, out Guid newChunkId)
	{
		newChunkId = (addAsNewChunk ? Guid.NewGuid() : bank.Variations[variationIndex].ChunkRef.ChunkId);
		List<Variation> list = new List<Variation>();
		for (int i = 0; i < bank.Variations.Count; i++)
		{
			Variation variation = bank.Variations[i];
			ChunkRef chunkRef = variation.ChunkRef;
			if (i == variationIndex)
			{
				chunkRef = new ChunkRef(chunkRef.ChunkIndex, newChunkId, payload.Length);
			}
			list.Add(new Variation(variation.Index, variation.VariationId, chunkRef, variation.Segments, new Dictionary<uint, object>(variation.Values)));
		}
		NewWaveBank sampleBank = new NewWaveBank(bank.Key, bank.ProjectKey, list, bank.AllDataSets.ToList(), bank.UnknownDataSets.ToList())
		{
			EbxInstanceGuid = bank.EbxInstanceGuid,
			SelectionDatasetSampleGroupId = bank.SelectionDatasetSampleGroupId,
			SelectionParameterIds = (int[])bank.SelectionParameterIds.Clone(),
			SelectionParametersOnly = bank.SelectionParametersOnly,
			TrailerBankKey = bank.TrailerBankKey
		};
		using MemoryStream memoryStream = new MemoryStream();
		new NewWaveAssetSampleBankWriter().WriteUnprocessed(memoryStream, sampleBank, endian);
		return memoryStream.ToArray();
	}

	private static async Task CopySpsAsync(Stream source, uint offsetWithFlags, Stream destination, CancellationToken cancellationToken)
	{
		long currentOffset = (uint)((int)offsetWithFlags & -4);
		int blockId;
		do
		{
			source.Position = currentOffset;
			int header = ReadInt32BigEndian(source);
			blockId = (header >> 24) & 0xFF;
			int blockSize = header & 0xFFFFFF;
			source.Position = currentOffset;
			await CopyCountAsync(source, destination, (uint)blockSize, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			currentOffset += blockSize;
		}
		while (blockId != 69);
	}

	private static async Task CopyCountAsync(Stream source, Stream destination, uint count, CancellationToken cancellationToken)
	{
		byte[] buffer = new byte[81920];
		uint remaining = count;
		while (remaining != 0)
		{
			int read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			if (read == 0)
			{
				throw new EndOfStreamException("Unexpected end of stream.");
			}
			await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			remaining -= (uint)read;
		}
	}

	private static async Task<(byte[] SpsData, byte[]? SeekTableData)> LoadReplacementPayloadAsync(string inputFilePath, int codec, bool seekable, CancellationToken cancellationToken)
	{
		string extension = Path.GetExtension(inputFilePath);
		if (extension.Equals(".sps", StringComparison.OrdinalIgnoreCase))
		{
			byte[] spsData = await File.ReadAllBytesAsync(inputFilePath, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			byte[] seekTableData = null;
			if (seekable)
			{
				string sekPath = Path.ChangeExtension(inputFilePath, ".sek");
				if (File.Exists(sekPath))
				{
					seekTableData = await File.ReadAllBytesAsync(sekPath, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
			}
			return (SpsData: spsData, SeekTableData: seekTableData);
		}
		string tempOutput = TempFile(".sps");
		try
		{
			await SoundExchangeHelper.ConvertAsync(inputFilePath, tempOutput, codec, seekable, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			byte[] spsData2 = await File.ReadAllBytesAsync(tempOutput, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			byte[] seekTableData2 = null;
			string sekPath2 = Path.ChangeExtension(tempOutput, ".sek");
			if (seekable && File.Exists(sekPath2))
			{
				seekTableData2 = await File.ReadAllBytesAsync(sekPath2, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			}
			return (SpsData: spsData2, SeekTableData: seekTableData2);
		}
		finally
		{
			TryDelete(tempOutput);
			TryDelete(Path.ChangeExtension(tempOutput, ".sek"));
			TryDelete(Path.ChangeExtension(tempOutput, ".sph"));
		}
	}

	private static async Task<ImportedSoundPayload> ImportSoundPayloadAsync(SoundAssetState state, string inputFilePath, int codec, CancellationToken cancellationToken)
	{
		bool hasStreamPool = HasStreamPool(state.RootObject);
		bool seekable = TryGetBool(state.RootObject, "IsSeekable") == true;
		var (spsData, seekTableData) = await LoadReplacementPayloadAsync(inputFilePath, codec, seekable, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		if (!hasStreamPool)
		{
			using MemoryStream output = new MemoryStream();
			uint seekOffset = 0u;
			if (seekTableData != null && seekTableData.Length != 0)
			{
				seekOffset = (uint)(int)output.Position | BuildOffsetFlags(isValid: true, streaming: false);
				output.Write(seekTableData, 0, seekTableData.Length);
			}
			while ((output.Position & 3) != 0)
			{
				output.WriteByte(0);
			}
			uint sampleOffset = (uint)(int)output.Position | BuildOffsetFlags(isValid: true, streaming: false);
			output.Write(spsData, 0, spsData.Length);
			byte[] chunkBytes = output.ToArray();
			Guid chunkId = Guid.NewGuid();
			CopyChunkPlacement(replacementChunk: AssetManager.AddChunk(chunkBytes, chunkId), sourceChunkId: GetPlacementTemplateChunkId(state));
			return new ImportedSoundPayload(chunkId, chunkBytes.Length, seekOffset, sampleOffset, SpsSoundHeader.LoadFrom(spsData).DurationInSeconds, HasStreamPool: false);
		}
		DataSet chunksDataSet = RequireDataSet(state.ParsedBank, "Chunks");
		int streamRow = IndexOfValue(chunksDataSet, "ChunkIndex", 1uL);
		Guid guid2;
		if (streamRow >= 0)
		{
			Guid? guid = TryReadGuidField(chunksDataSet, "ChunkId", streamRow);
			if (guid.HasValue)
			{
				Guid existingChunkId = guid.GetValueOrDefault();
				guid2 = existingChunkId;
				goto IL_0318;
			}
		}
		guid2 = Guid.NewGuid();
		goto IL_0318;
		IL_0318:
		Guid chunkIdForPool = guid2;
		byte[] existingChunkBytes = Array.Empty<byte>();
		if (streamRow >= 0)
		{
			ChunkAssetEntry chunkEntry = AssetManager.GetChunkAssetEntry(chunkIdForPool);
			if (chunkEntry != null)
			{
				existingChunkBytes = AssetManager.GetAsset(chunkEntry).ToArray();
			}
		}
		int seekStart = existingChunkBytes.Length;
		int sampleStart = ((seekTableData == null || seekTableData.Length == 0) ? AlignTo(existingChunkBytes.Length, 4) : AlignTo(seekStart + seekTableData.Length, 4));
		int totalSize = sampleStart + spsData.Length;
		byte[] mergedChunk = new byte[totalSize];
		if (existingChunkBytes.Length != 0)
		{
			Buffer.BlockCopy(existingChunkBytes, 0, mergedChunk, 0, existingChunkBytes.Length);
		}
		if (seekTableData != null && seekTableData.Length != 0)
		{
			Buffer.BlockCopy(seekTableData, 0, mergedChunk, seekStart, seekTableData.Length);
		}
		Buffer.BlockCopy(spsData, 0, mergedChunk, sampleStart, spsData.Length);
		if (streamRow < 0 || AssetManager.GetChunkAssetEntry(chunkIdForPool) == null)
		{
			CopyChunkPlacement(replacementChunk: AssetManager.AddChunk(mergedChunk, chunkIdForPool), sourceChunkId: GetPlacementTemplateChunkId(state));
		}
		else
		{
			AssetManager.ModifyChunk(chunkIdForPool, mergedChunk);
		}
		return new ImportedSoundPayload(chunkIdForPool, mergedChunk.Length, (seekTableData != null && seekTableData.Length != 0) ? ((uint)seekStart | BuildOffsetFlags(isValid: true, streaming: true)) : (0 | BuildOffsetFlags(isValid: false, streaming: true)), (uint)sampleStart | BuildOffsetFlags(isValid: true, streaming: true), SpsSoundHeader.LoadFrom(spsData).DurationInSeconds, HasStreamPool: true);
	}

	private static (byte[] ChunkBytes, List<Segment> Segments) RebuildVariationChunk(Variation variation, int targetSegmentIndex, byte[] replacementSps, byte[]? replacementSeekTable)
	{
		NewWaveAssetHarmonySampleBankParser newWaveAssetHarmonySampleBankParser = new NewWaveAssetHarmonySampleBankParser();
		using MemoryStream memoryStream = new MemoryStream();
		List<Segment> list = new List<Segment>();
		Segment? segment = variation.Segments.FirstOrDefault();
		bool streaming = segment == null || (segment.SamplesOffset & 2) != 0;
		foreach (Segment segment2 in variation.Segments)
		{
			byte[] array;
			byte[] array2;
			if (segment2.Index != targetSegmentIndex)
			{
				(array, array2) = newWaveAssetHarmonySampleBankParser.ExtractSegmentPayload(segment2, variation.ChunkRef.ChunkId);
			}
			else
			{
				array = replacementSps;
				array2 = replacementSeekTable;
			}
			uint seekTableOffset = 0u;
			if (array2 != null && array2.Length != 0)
			{
				seekTableOffset = (uint)(int)memoryStream.Position | BuildOffsetFlags(isValid: true, streaming);
				memoryStream.Write(array2, 0, array2.Length);
			}
			while ((memoryStream.Position & 3) != 0)
			{
				memoryStream.WriteByte(0);
			}
			uint samplesOffset = (uint)(int)memoryStream.Position | BuildOffsetFlags(isValid: true, streaming);
			memoryStream.Write(array, 0, array.Length);
			SpsSoundHeader spsSoundHeader = SpsSoundHeader.LoadFrom(array);
			list.Add(new Segment(segment2.Index, samplesOffset, seekTableOffset, spsSoundHeader.DurationInSeconds));
		}
		return (ChunkBytes: memoryStream.ToArray(), Segments: list);
	}

	private static void UpdateDataSetsForVariationReplacement(NewWaveBank bank, Variation sourceVariation, Variation replacementVariation, int targetSegmentIndex)
	{
		DataSet dataSet = bank.GetDataSet("Chunks");
		if (dataSet != null)
		{
			int num = IndexOfValue(dataSet, "ChunkIndex", sourceVariation.ChunkRef.ChunkIndex);
			if (num >= 0)
			{
				SetFieldValue(dataSet, "ChunkId", num, replacementVariation.ChunkRef.ChunkId);
				SetFieldValue(dataSet, "ChunkSize", num, replacementVariation.ChunkRef.ChunkSize);
			}
		}
		DataSet dataSet2 = bank.GetDataSet("Segments");
		if (dataSet2 != null)
		{
			Segment segment = replacementVariation.Segments.First((Segment segment2) => segment2.Index == targetSegmentIndex);
			int num2 = IndexOfValue(dataSet2, "SegmentIndex", (ulong)segment.Index);
			if (num2 >= 0)
			{
				SetFieldValue(dataSet2, "SamplesOffset", num2, segment.SamplesOffset);
				SetFieldValue(dataSet2, "SeekTableOffset", num2, segment.SeekTableOffset);
				SetFieldValue(dataSet2, "Duration", num2, segment.SegmentLength);
			}
		}
	}

	private static void SyncRootChunkList(EbxPartition partition, NewWaveBank bank)
	{
		object instance = partition.RootInstances.FirstOrDefault() ?? partition.PrimaryInstance;
		object memberValue = GetMemberValue(instance, "Chunks");
		if (!(memberValue is IList list))
		{
			return;
		}
		Type type = list.GetType().GetGenericArguments().FirstOrDefault();
		if ((object)type == null)
		{
			return;
		}
		list.Clear();
		foreach (ChunkRef item in from variation in bank.Variations
			select variation.ChunkRef into chunk
			group chunk by chunk.ChunkId into @group
			select @group.First())
		{
			object obj = Activator.CreateInstance(type) ?? throw new InvalidOperationException("Unable to create " + type.Name + ".");
			SetMemberValue(obj, "ChunkId", item.ChunkId);
			SetMemberValue(obj, "ChunkSize", item.ChunkSize);
			SetMemberValue(obj, "Size", item.ChunkSize);
			list.Add(obj);
		}
	}

	private static IReadOnlyList<SoundSegmentLocation> EnumerateSegmentLocations(NewWaveBank bank)
	{
		List<SoundSegmentLocation> list = new List<SoundSegmentLocation>();
		int num = 0;
		for (int i = 0; i < bank.Variations.Count; i++)
		{
			Variation variation = bank.Variations[i];
			for (int j = 0; j < variation.Segments.Count; j++)
			{
				list.Add(new SoundSegmentLocation(num, i, j));
				num++;
			}
		}
		return list;
	}

	private static IReadOnlyList<BulkSoundImportCandidate> BuildBulkImportCandidates(IEnumerable<string> inputFilePaths, out string validationError)
	{
		validationError = "No supported audio files were found.";
		if (inputFilePaths == null)
		{
			return Array.Empty<BulkSoundImportCandidate>();
		}
		Dictionary<int, BulkSoundImportCandidate> dictionary = new Dictionary<int, BulkSoundImportCandidate>();
		foreach (string inputFilePath in inputFilePaths)
		{
			if (!string.IsNullOrWhiteSpace(inputFilePath) && IsSupportedBulkImportFile(inputFilePath))
			{
				if (!TryParseSequenceNumber(inputFilePath, out var sequenceNumber))
				{
					validationError = "File names must be numeric segment numbers.";
					return Array.Empty<BulkSoundImportCandidate>();
				}
				if (!dictionary.TryAdd(sequenceNumber, new BulkSoundImportCandidate(sequenceNumber, inputFilePath)))
				{
					validationError = "Duplicate segment numbers were found in the bulk import selection.";
					return Array.Empty<BulkSoundImportCandidate>();
				}
			}
		}
		List<BulkSoundImportCandidate> list = dictionary.Values.OrderBy((BulkSoundImportCandidate candidate) => candidate.SequenceNumber).ThenBy<BulkSoundImportCandidate, string>((BulkSoundImportCandidate candidate) => candidate.FilePath, StringComparer.OrdinalIgnoreCase).ToList();
		if (list.Count == 0)
		{
			return Array.Empty<BulkSoundImportCandidate>();
		}
		validationError = string.Empty;
		return list;
	}

	private static bool ValidateBulkImportSequenceSet(int existingSegmentCount, IReadOnlyList<BulkSoundImportCandidate> candidates, out string validationError)
	{
		HashSet<int> hashSet = new HashSet<int>(Enumerable.Range(0, Math.Max(0, existingSegmentCount)));
		foreach (BulkSoundImportCandidate candidate in candidates)
		{
			hashSet.Add(candidate.SequenceNumber);
		}
		if (hashSet.Count == 0)
		{
			validationError = "No supported audio files were found.";
			return false;
		}
		int num = hashSet.Min();
		int num2 = hashSet.Max();
		if (num != 0 || num2 != hashSet.Count - 1)
		{
			validationError = "The resulting segment list must have contiguous segment numbers and start with zero.";
			return false;
		}
		validationError = string.Empty;
		return true;
	}

	private static bool TryParseSequenceNumber(string filePath, out int sequenceNumber)
	{
		string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(filePath);
		return int.TryParse(fileNameWithoutExtension, NumberStyles.Integer, CultureInfo.InvariantCulture, out sequenceNumber) && sequenceNumber >= 0;
	}

	private static bool IsSupportedBulkImportFile(string filePath)
	{
		string extension = Path.GetExtension(filePath);
		return extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) || extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) || extension.Equals(".sps", StringComparison.OrdinalIgnoreCase) || extension.Equals(".flac", StringComparison.OrdinalIgnoreCase) || extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase);
	}

	private static IEnumerable<string> EnumerateImportFiles(string inputDirectory)
	{
		return (from path in Directory.EnumerateFiles(inputDirectory)
			where s_importExtensions.Contains<string>(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
			select path).OrderBy<string, string>((string path) => path, StringComparer.OrdinalIgnoreCase);
	}

	private static Dictionary<(int VariationIndex, int SegmentIndex), string> BuildSegmentImportMap(IEnumerable<string> inputFiles)
	{
		Dictionary<(int, int), string> dictionary = new Dictionary<(int, int), string>();
		foreach (string inputFile in inputFiles)
		{
			Match match = s_variationSegmentFilePattern.Match(Path.GetFileNameWithoutExtension(inputFile));
			if (match.Success)
			{
				int item = int.Parse(match.Groups["variation"].Value, CultureInfo.InvariantCulture);
				int item2 = int.Parse(match.Groups["segment"].Value, CultureInfo.InvariantCulture);
				dictionary[(item, item2)] = inputFile;
			}
		}
		return dictionary;
	}

	private static Dictionary<int, string> BuildChunkImportMap(IEnumerable<string> inputFiles)
	{
		Dictionary<int, string> dictionary = new Dictionary<int, string>();
		foreach (string inputFile in inputFiles)
		{
			Match match = s_chunkFilePattern.Match(Path.GetFileNameWithoutExtension(inputFile));
			if (match.Success)
			{
				int key = int.Parse(match.Groups["chunk"].Value, CultureInfo.InvariantCulture);
				dictionary[key] = inputFile;
			}
		}
		return dictionary;
	}

	private static int ResolveReferenceCodec(SoundAssetState state)
	{
		Variation variation = state.ParsedBank?.Variations.FirstOrDefault((Variation v) => v.Segments.Count > 0);
		if (variation == null)
		{
			return 14;
		}
		return ReadSpsHeader(variation.ChunkRef.ChunkId, variation.Segments[0]).Codec;
	}

	private static Guid GetPlacementTemplateChunkId(SoundAssetState state)
	{
		return state.ParsedBank?.Variations.FirstOrDefault()?.ChunkRef.ChunkId ?? state.Chunks.FirstOrDefault()?.ChunkId ?? Guid.Empty;
	}

	private static bool HasStreamPool(object rootObject)
	{
		object memberValue = GetMemberValue(rootObject, "StreamPool");
		if (memberValue == null)
		{
			return false;
		}
		int? num = TryGetInt(memberValue, "Type");
		return !num.HasValue || num.Value != 0;
	}

	private static DataSet RequireDataSet(NewWaveBank bank, string name)
	{
		return bank.GetDataSet(name) ?? throw new InvalidOperationException("Required sound dataset '" + name + "' was not found.");
	}

	private static DataSet ResolveDataSet(NewWaveBank bank, string identifier)
	{
		if (string.IsNullOrWhiteSpace(identifier))
		{
			throw new ArgumentException("A data set identifier is required.", "identifier");
		}
		DataSet dataSet = bank.GetDataSet(identifier);
		if (dataSet != null)
		{
			return dataSet;
		}
		if (TryParseHashedIdentifier(identifier, out var hash))
		{
			dataSet = bank.AllDataSets.Concat(bank.UnknownDataSets).FirstOrDefault((DataSet ds) => ds.Id == hash);
			if (dataSet != null)
			{
				return dataSet;
			}
		}
		throw new InvalidOperationException("Unable to resolve sound dataset '" + identifier + "'.");
	}

	private static Field ResolveField(DataSet dataSet, string identifier)
	{
		if (string.IsNullOrWhiteSpace(identifier))
		{
			throw new ArgumentException("A field identifier is required.", "identifier");
		}
		Field field = dataSet.Get(identifier);
		if (field != null)
		{
			return field;
		}
		if (TryParseHashedIdentifier(identifier, out var hash))
		{
			field = dataSet.Get(hash);
			if (field != null)
			{
				return field;
			}
		}
		throw new InvalidOperationException($"Unable to resolve sound field '{identifier}' in dataset 0x{dataSet.Id:X8}.");
	}

	private static bool TryParseHashedIdentifier(string identifier, out uint hash)
	{
		string text = identifier.Trim();
		if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
		{
			string text2 = text;
			text = text2.Substring(2, text2.Length - 2);
		}
		return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hash);
	}

	private static void ApplyDataSetMutation(NewWaveBank bank, DataSet dataSet)
	{
		uint num = HashFieldName("Chunks");
		uint num2 = HashFieldName("Segments");
		uint num3 = HashFieldName("Variations");
		uint num4 = HashFieldName("Selection");
		if (dataSet.Id == num || dataSet.Id == num2 || dataSet.Id == num3 || dataSet.Id == num4)
		{
			RebuildVariationsFromDataSets(bank);
		}
	}

	private static int AppendDefaultRow(DataSet dataSet)
	{
		foreach (Field item in dataSet.Fields.Concat(dataSet.IndexColumns))
		{
			item.Values.Add(GetDefaultFieldValue(item));
		}
		return dataSet.NumElems++;
	}

	private static void RemoveRow(DataSet dataSet, int rowIndex)
	{
		if (rowIndex < 0 || rowIndex >= dataSet.NumElems)
		{
			throw new ArgumentOutOfRangeException("rowIndex");
		}
		foreach (Field item in dataSet.Fields.Concat(dataSet.IndexColumns))
		{
			if (rowIndex < item.Values.Count)
			{
				item.Values.RemoveAt(rowIndex);
			}
		}
		dataSet.NumElems--;
	}

	private static void ResizeDataSet(DataSet dataSet, int rowCount)
	{
		if (rowCount < 0)
		{
			throw new ArgumentOutOfRangeException("rowCount");
		}
		foreach (Field item in dataSet.Fields.Concat(dataSet.IndexColumns))
		{
			while (item.Values.Count < rowCount)
			{
				item.Values.Add(GetDefaultFieldValue(item));
			}
			while (item.Values.Count > rowCount)
			{
				item.Values.RemoveAt(item.Values.Count - 1);
			}
		}
		dataSet.NumElems = rowCount;
	}

	private static Dictionary<ulong, Dictionary<uint, object>> CaptureRowMap(DataSet dataSet, string keyFieldName)
	{
		Dictionary<ulong, Dictionary<uint, object>> dictionary = new Dictionary<ulong, Dictionary<uint, object>>();
		Field field = dataSet.Get(keyFieldName);
		if (field == null)
		{
			return dictionary;
		}
		for (int i = 0; i < dataSet.NumElems; i++)
		{
			if (i >= field.Values.Count)
			{
				continue;
			}
			ulong key = ConvertToUInt64(field.Values[i]);
			Dictionary<uint, object> dictionary2 = new Dictionary<uint, object>();
			foreach (Field item in dataSet.Fields.Concat(dataSet.IndexColumns))
			{
				if (i < item.Values.Count)
				{
					dictionary2[item.Id] = item.Values[i];
				}
			}
			dictionary[key] = dictionary2;
		}
		return dictionary;
	}

	private static void ApplyRowSnapshot(DataSet dataSet, int rowIndex, Dictionary<uint, object>? snapshot)
	{
		foreach (Field item in dataSet.Fields.Concat(dataSet.IndexColumns))
		{
			object value2;
			object value = ((snapshot != null && snapshot.TryGetValue(item.Id, out value2)) ? value2 : GetDefaultFieldValue(item));
			SetFieldValue(dataSet, item.Id, rowIndex, value);
		}
	}

	private static void SyncBankDataSetsFromVariations(NewWaveBank bank)
	{
		SyncChunkDataSet(bank);
		SyncSegmentDataSet(bank);
		SyncVariationDataSet(bank);
		SyncSelectionDataSet(bank);
	}

	private static void RebuildVariationsFromDataSets(NewWaveBank bank)
	{
		DataSet dataSet = bank.GetDataSet("Variations");
		if (dataSet == null)
		{
			return;
		}
		Dictionary<ulong, ChunkRef> dictionary = BuildChunkLookup(bank.GetDataSet("Chunks"));
		Dictionary<int, Segment> dictionary2 = BuildSegmentLookup(bank.GetDataSet("Segments"));
		Dictionary<uint, Dictionary<uint, object>> dictionary3 = BuildSelectionRows(bank.GetDataSet("Selection"));
		List<Variation> list = new List<Variation>();
		for (int i = 0; i < dataSet.NumElems; i++)
		{
			uint num = (uint)ReadFieldUInt64(dataSet, "VariationId", i, (ulong)i);
			int index = (int)ReadFieldUInt64(dataSet, "VariationIndex", i, (ulong)i);
			ulong num2 = ReadFieldUInt64(dataSet, "FirstSegmentIndex", i, 0uL);
			int num3 = (int)ReadFieldUInt64(dataSet, "SegmentCount", i, 0uL);
			ulong num4 = DecodeChunkIndex(ReadField(dataSet, "StreamChunkIndex", i)) ?? DecodeChunkIndex(ReadField(dataSet, "MemoryChunkIndex", i)).GetValueOrDefault();
			List<Segment> list2 = new List<Segment>();
			for (int j = 0; j < num3; j++)
			{
				int key = checked((int)(num2 + (ulong)j));
				if (dictionary2.TryGetValue(key, out var value))
				{
					list2.Add(new Segment(value.Index, value.SamplesOffset, value.SeekTableOffset, value.SegmentLength));
				}
			}
			dictionary.TryGetValue(num4, out var value2);
			if (value2 == null)
			{
				value2 = new ChunkRef(num4, Guid.Empty, 0L);
			}
			Dictionary<uint, object> value3;
			Dictionary<uint, object> values = (dictionary3.TryGetValue(num, out value3) ? new Dictionary<uint, object>(value3) : new Dictionary<uint, object>());
			list.Add(new Variation(index, num, value2, list2, values));
		}
		bank.Variations.Clear();
		bank.Variations.AddRange(list.OrderBy((Variation variation) => variation.Index));
	}

	private static object GetDefaultFieldValue(Field field)
	{
		FieldType dataType = field.DataType;
		if (1 == 0)
		{
		}
		object result = dataType switch
		{
			FieldType.Boolean => false, 
			FieldType.Int32 => 0, 
			FieldType.Int64 => 0L, 
			FieldType.UInt32 => 0u, 
			FieldType.UInt64 => 0uL, 
			FieldType.Float32 => 0f, 
			FieldType.Float64 => 0.0, 
			FieldType.String => string.Empty, 
			FieldType.Pointer => Guid.Empty, 
			_ => throw new NotSupportedException($"Unsupported field type '{field.DataType}'."), 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	private static int IndexOfValue(DataSet dataSet, string fieldName, object expected)
	{
		Field field = dataSet.Get(fieldName);
		if (field == null)
		{
			return -1;
		}
		for (int i = 0; i < field.Values.Count; i++)
		{
			if (FieldValueEquals(field.Values[i], expected))
			{
				return i;
			}
		}
		return -1;
	}

	private static ulong GetNextUInt64(DataSet dataSet, string fieldName)
	{
		Field field = dataSet.Get(fieldName);
		if (field == null || field.Values.Count == 0)
		{
			return 0uL;
		}
		ulong num = 0uL;
		foreach (object value in field.Values)
		{
			ulong num2 = ConvertToUInt64(value);
			if (num2 > num)
			{
				num = num2;
			}
		}
		return num + 1;
	}

	private static Guid? TryReadGuidField(DataSet dataSet, string fieldName, int rowIndex)
	{
		Field field = dataSet.Get(fieldName);
		if (field == null || rowIndex < 0 || rowIndex >= field.Values.Count)
		{
			return null;
		}
		return TryConvertToGuid(field.Values[rowIndex]);
	}

	private static void SetFieldValue(DataSet dataSet, string fieldName, int rowIndex, object value)
	{
		Field field = dataSet.Get(fieldName) ?? throw new InvalidOperationException("Required dataset field '" + fieldName + "' was not found.");
		SetFieldValue(dataSet, field.Id, rowIndex, value);
	}

	private static void SetFieldValue(DataSet dataSet, uint fieldId, int rowIndex, object value)
	{
		Field field = dataSet.Get(fieldId) ?? throw new InvalidOperationException($"Required dataset field '0x{fieldId:X8}' was not found.");
		if (rowIndex < 0)
		{
			throw new ArgumentOutOfRangeException("rowIndex");
		}
		while (field.Values.Count <= rowIndex)
		{
			field.Values.Add(GetDefaultFieldValue(field));
		}
		field.Values[rowIndex] = ConvertFieldValue(field, value);
	}

	private static void SetFieldValueIfPresent(DataSet? dataSet, string fieldName, int rowIndex, object value)
	{
		if (dataSet?.Get(fieldName) != null)
		{
			SetFieldValue(dataSet, fieldName, rowIndex, value);
		}
	}

	private static object? ReadField(DataSet dataSet, string fieldName, int rowIndex)
	{
		Field field = dataSet.Get(fieldName);
		if (field == null || rowIndex < 0 || rowIndex >= field.Values.Count)
		{
			return null;
		}
		return field.Values[rowIndex];
	}

	private static ulong ReadFieldUInt64(DataSet dataSet, string fieldName, int rowIndex, ulong defaultValue)
	{
		object obj = ReadField(dataSet, fieldName, rowIndex);
		return (obj == null) ? defaultValue : ConvertToUInt64(obj);
	}

	private static object ConvertTextValue(Field field, string? valueText)
	{
		string text = valueText?.Trim() ?? string.Empty;
		if (text.Length == 0)
		{
			return GetDefaultFieldValue(field);
		}
		FieldType dataType = field.DataType;
		if (1 == 0)
		{
		}
		bool result2;
		object result = dataType switch
		{
			FieldType.Boolean => bool.TryParse(text, out result2) ? result2 : (text == "1"), 
			FieldType.Int32 => int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture), 
			FieldType.Int64 => long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture), 
			FieldType.UInt32 => ParseUnsigned(text), 
			FieldType.UInt64 => ParseUnsigned(text), 
			FieldType.Float32 => float.Parse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture), 
			FieldType.Float64 => double.Parse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture), 
			FieldType.String => text, 
			FieldType.Pointer => Guid.Parse(text), 
			_ => throw new NotSupportedException($"Unsupported field type '{field.DataType}'."), 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	private static object ConvertFieldValue(Field field, object value)
	{
		FieldType dataType = field.DataType;
		if (1 == 0)
		{
		}
		object result = dataType switch
		{
			FieldType.Boolean => (value is bool flag) ? flag : Convert.ToBoolean(value, CultureInfo.InvariantCulture), 
			FieldType.Int32 => (value is int num) ? num : Convert.ToInt32(value, CultureInfo.InvariantCulture), 
			FieldType.Int64 => (value is long num2) ? num2 : Convert.ToInt64(value, CultureInfo.InvariantCulture), 
			FieldType.UInt32 => (value is uint num3) ? num3 : Convert.ToUInt32(value, CultureInfo.InvariantCulture), 
			FieldType.UInt64 => (value is ulong num4) ? num4 : Convert.ToUInt64(value, CultureInfo.InvariantCulture), 
			FieldType.Float32 => (value is float num5) ? num5 : Convert.ToSingle(value, CultureInfo.InvariantCulture), 
			FieldType.Float64 => (value is double num6) ? num6 : Convert.ToDouble(value, CultureInfo.InvariantCulture), 
			FieldType.String => value.ToString() ?? string.Empty, 
			FieldType.Pointer => (value is Guid guid) ? guid : Guid.Parse(value.ToString() ?? Guid.Empty.ToString()), 
			_ => throw new NotSupportedException($"Unsupported field type '{field.DataType}'."), 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	private static bool FieldValueEquals(object? left, object? right)
	{
		if (left == null || right == null)
		{
			return object.Equals(left, right);
		}
		if (left is Guid guid)
		{
			return right is Guid guid2 && guid == guid2;
		}
		if (right is Guid)
		{
			return FieldValueEquals(right, left);
		}
		if (IsNumeric(left) && IsNumeric(right))
		{
			return ConvertToUInt64(left) == ConvertToUInt64(right);
		}
		return object.Equals(left, right);
	}

	private static bool IsNumeric(object value)
	{
		if (value is byte || value is sbyte || value is short || value is ushort || value is int || value is uint || value is long || value is ulong)
		{
			return true;
		}
		return false;
	}

	private static ulong ConvertToUInt64(object value)
	{
		if (1 == 0)
		{
		}
		ulong result;
		if (!(value is byte b))
		{
			if (!(value is sbyte b2))
			{
				if (!(value is short num))
				{
					if (!(value is ushort num2))
					{
						if (!(value is int num3))
						{
							if (!(value is uint num4))
							{
								if (!(value is long num5))
								{
									if (!(value is ulong num6))
									{
										goto IL_00fc;
									}
									result = num6;
								}
								else
								{
									if (num5 < 0)
									{
										goto IL_00fc;
									}
									result = (ulong)num5;
								}
							}
							else
							{
								result = num4;
							}
						}
						else
						{
							if (num3 < 0)
							{
								goto IL_00fc;
							}
							result = (ulong)num3;
						}
					}
					else
					{
						result = num2;
					}
				}
				else
				{
					if (num < 0)
					{
						goto IL_00fc;
					}
					result = (ulong)num;
				}
			}
			else
			{
				if (b2 < 0)
				{
					goto IL_00fc;
				}
				result = (ulong)b2;
			}
		}
		else
		{
			result = b;
		}
		goto IL_010b;
		IL_010b:
		if (1 == 0)
		{
		}
		return result;
		IL_00fc:
		result = Convert.ToUInt64(value, CultureInfo.InvariantCulture);
		goto IL_010b;
	}

	private static uint HashFieldName(string value)
	{
		return (uint)Frosty.Sdk.Utils.Utils.HashString(value, toLower: true);
	}

	private static ulong ParseUnsigned(string text)
	{
		if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
		{
			return ulong.Parse(text.Substring(2, text.Length - 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
		}
		return ulong.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
	}

	private static ulong? DecodeChunkIndex(object? encodedValue)
	{
		if (encodedValue == null)
		{
			return null;
		}
		ulong num = ConvertToUInt64(encodedValue);
		return ((num & 1) == 1) ? new ulong?(num >> 1) : ((ulong?)null);
	}

	private static Dictionary<ulong, ChunkRef> BuildChunkLookup(DataSet? chunksDataSet)
	{
		Dictionary<ulong, ChunkRef> dictionary = new Dictionary<ulong, ChunkRef>();
		if (chunksDataSet == null)
		{
			return dictionary;
		}
		for (int i = 0; i < chunksDataSet.NumElems; i++)
		{
			ulong num = ReadFieldUInt64(chunksDataSet, "ChunkIndex", i, (ulong)i);
			Guid chunkId = TryReadGuidField(chunksDataSet, "ChunkId", i) ?? Guid.Empty;
			long chunkSize = (long)ReadFieldUInt64(chunksDataSet, "ChunkSize", i, 0uL);
			dictionary[num] = new ChunkRef(num, chunkId, chunkSize);
		}
		return dictionary;
	}

	private static Dictionary<int, Segment> BuildSegmentLookup(DataSet? segmentsDataSet)
	{
		Dictionary<int, Segment> dictionary = new Dictionary<int, Segment>();
		if (segmentsDataSet == null)
		{
			return dictionary;
		}
		for (int i = 0; i < segmentsDataSet.NumElems; i++)
		{
			int num = (int)ReadFieldUInt64(segmentsDataSet, "SegmentIndex", i, (ulong)i);
			uint samplesOffset = (uint)ReadFieldUInt64(segmentsDataSet, "SamplesOffset", i, 0uL);
			uint seekTableOffset = (uint)ReadFieldUInt64(segmentsDataSet, "SeekTableOffset", i, 0uL);
			object obj = ReadField(segmentsDataSet, "Duration", i);
			if (1 == 0)
			{
			}
			float num2 = ((obj is float num3) ? num3 : ((obj is double num4) ? ((float)num4) : ((obj != null) ? Convert.ToSingle(obj, CultureInfo.InvariantCulture) : 0f)));
			if (1 == 0)
			{
			}
			float segmentLength = num2;
			dictionary[num] = new Segment(num, samplesOffset, seekTableOffset, segmentLength);
		}
		return dictionary;
	}

	private static Dictionary<uint, Dictionary<uint, object>> BuildSelectionRows(DataSet? selectionDataSet)
	{
		Dictionary<uint, Dictionary<uint, object>> dictionary = new Dictionary<uint, Dictionary<uint, object>>();
		if (selectionDataSet == null)
		{
			return dictionary;
		}
		for (int i = 0; i < selectionDataSet.NumElems; i++)
		{
			uint key = (uint)ReadFieldUInt64(selectionDataSet, "VariationId", i, (ulong)i);
			Dictionary<uint, object> dictionary2 = new Dictionary<uint, object>();
			foreach (Field item in selectionDataSet.Fields.Concat(selectionDataSet.IndexColumns))
			{
				if (i < item.Values.Count)
				{
					dictionary2[item.Id] = item.Values[i];
				}
			}
			dictionary[key] = dictionary2;
		}
		return dictionary;
	}

	private static void SyncChunkDataSet(NewWaveBank bank)
	{
		DataSet dataSet = bank.GetDataSet("Chunks");
		if (dataSet != null)
		{
			Dictionary<ulong, Dictionary<uint, object>> dictionary = CaptureRowMap(dataSet, "ChunkIndex");
			List<ChunkRef> list = (from variation in bank.Variations
				select variation.ChunkRef into chunk
				group chunk by chunk.ChunkIndex into @group
				select @group.First() into chunk
				orderby chunk.ChunkIndex
				select chunk).ToList();
			ResizeDataSet(dataSet, list.Count);
			for (int num = 0; num < list.Count; num++)
			{
				ChunkRef chunkRef = list[num];
				dictionary.TryGetValue(chunkRef.ChunkIndex, out var value);
				ApplyRowSnapshot(dataSet, num, value);
				SetFieldValueIfPresent(dataSet, "ChunkIndex", num, chunkRef.ChunkIndex);
				SetFieldValueIfPresent(dataSet, "ChunkId", num, chunkRef.ChunkId);
				SetFieldValueIfPresent(dataSet, "ChunkSize", num, chunkRef.ChunkSize);
			}
		}
	}

	private static void SyncSegmentDataSet(NewWaveBank bank)
	{
		DataSet dataSet = bank.GetDataSet("Segments");
		if (dataSet != null)
		{
			Dictionary<ulong, Dictionary<uint, object>> dictionary = CaptureRowMap(dataSet, "SegmentIndex");
			List<Segment> list = (from segment2 in bank.Variations.SelectMany((Variation variation) => variation.Segments)
				group segment2 by segment2.Index into @group
				select @group.First() into segment2
				orderby segment2.Index
				select segment2).ToList();
			ResizeDataSet(dataSet, list.Count);
			for (int num = 0; num < list.Count; num++)
			{
				Segment segment = list[num];
				dictionary.TryGetValue((ulong)segment.Index, out var value);
				ApplyRowSnapshot(dataSet, num, value);
				SetFieldValueIfPresent(dataSet, "SegmentIndex", num, (ulong)segment.Index);
				SetFieldValueIfPresent(dataSet, "SamplesOffset", num, segment.SamplesOffset);
				SetFieldValueIfPresent(dataSet, "SeekTableOffset", num, segment.SeekTableOffset);
				SetFieldValueIfPresent(dataSet, "Duration", num, segment.SegmentLength);
			}
		}
	}

	private static void SyncVariationDataSet(NewWaveBank bank)
	{
		DataSet dataSet = bank.GetDataSet("Variations");
		if (dataSet == null)
		{
			return;
		}
		Dictionary<ulong, Dictionary<uint, object>> dictionary = CaptureRowMap(dataSet, "VariationId");
		ResizeDataSet(dataSet, bank.Variations.Count);
		List<Segment> source = (from segment2 in bank.Variations.SelectMany((Variation variation2) => variation2.Segments)
			group segment2 by segment2.Index into @group
			select @group.First() into segment2
			orderby segment2.Index
			select segment2).ToList();
		Dictionary<int, int> dictionary2 = source.Select((Segment segment2, int index) => new { segment2.Index, index }).ToDictionary(pair => pair.Index, pair => pair.index);
		List<Variation> list = bank.Variations.OrderBy((Variation variation2) => variation2.Index).ToList();
		for (int num = 0; num < list.Count; num++)
		{
			Variation variation = list[num];
			dictionary.TryGetValue(variation.VariationId, out var value);
			ApplyRowSnapshot(dataSet, num, value);
			ulong num2 = (variation.ChunkRef.ChunkIndex << 1) | 1;
			int num3 = ((variation.Segments.Count != 0) ? dictionary2[variation.Segments.Min((Segment segment2) => segment2.Index)] : 0);
			Segment segment = variation.Segments.FirstOrDefault();
			bool flag = segment != null && (segment.SamplesOffset & 2) != 0;
			SetFieldValueIfPresent(dataSet, "VariationId", num, variation.VariationId);
			SetFieldValueIfPresent(dataSet, "VariationIndex", num, (ulong)variation.Index);
			SetFieldValueIfPresent(dataSet, "FirstSegmentIndex", num, (ulong)num3);
			SetFieldValueIfPresent(dataSet, "SegmentCount", num, (ulong)variation.Segments.Count);
			SetFieldValueIfPresent(dataSet, "FirstLoopSegmentIndex", num, 0uL);
			SetFieldValueIfPresent(dataSet, "LastLoopSegmentIndex", num, 0uL);
			SetFieldValueIfPresent(dataSet, "StreamChunkIndex", num, flag ? num2 : 0);
			SetFieldValueIfPresent(dataSet, "MemoryChunkIndex", num, flag ? 0 : num2);
		}
	}

	private static void SyncSelectionDataSet(NewWaveBank bank)
	{
		DataSet dataSet = bank.GetDataSet("Selection");
		if (dataSet == null)
		{
			return;
		}
		Dictionary<ulong, Dictionary<uint, object>> dictionary = CaptureRowMap(dataSet, "VariationId");
		List<Variation> list = bank.Variations.OrderBy((Variation variation2) => variation2.Index).ToList();
		ResizeDataSet(dataSet, list.Count);
		for (int num = 0; num < list.Count; num++)
		{
			Variation variation = list[num];
			dictionary.TryGetValue(variation.VariationId, out var value);
			ApplyRowSnapshot(dataSet, num, value);
			SetFieldValueIfPresent(dataSet, "VariationId", num, variation.VariationId);
			foreach (var (num3, value2) in variation.Values)
			{
				if (dataSet.Get(num3) != null)
				{
					SetFieldValue(dataSet, num3, num, value2);
				}
			}
		}
	}

	private static Variation? ResolveTemplateVariation(NewWaveBank bank, int? templateVariationIndex)
	{
		if (templateVariationIndex.HasValue)
		{
			return bank.Variations.FirstOrDefault((Variation variation) => variation.Index == templateVariationIndex.Value);
		}
		return bank.Variations.OrderBy((Variation variation) => variation.Index).FirstOrDefault();
	}

	private static ulong GetNextChunkIndex(NewWaveBank bank)
	{
		ulong num = ((bank.Variations.Count == 0) ? 0 : bank.Variations.Max((Variation variation) => variation.ChunkRef.ChunkIndex));
		return num + 1;
	}

	private static ulong GetNextSegmentIndex(NewWaveBank bank)
	{
		int num = bank.Variations.SelectMany((Variation variation) => variation.Segments).DefaultIfEmpty().Max((Segment segment) => segment?.Index ?? (-1));
		if (1 == 0)
		{
		}
		ulong result = (ulong)((num < 0) ? 0 : (num + 1));
		if (1 == 0)
		{
		}
		return result;
	}

	private static uint ResolveNextVariationId(NewWaveBank bank, ulong fallbackVariationIndex)
	{
		DataSet dataSet = bank.GetDataSet("Variations");
		if (dataSet?.Get("VariationId") != null)
		{
			return (uint)GetNextUInt64(dataSet, "VariationId");
		}
		return (uint)fallbackVariationIndex;
	}

	private static int AlignTo(int value, int alignment)
	{
		int num = value % alignment;
		return (num == 0) ? value : (value + (alignment - num));
	}

	private static void CopyChunkPlacement(Guid sourceChunkId, ChunkAssetEntry replacementChunk)
	{
		ChunkAssetEntry chunkAssetEntry = AssetManager.GetChunkAssetEntry(sourceChunkId);
		if (chunkAssetEntry != null)
		{
			replacementChunk.Bundles.UnionWith(chunkAssetEntry.Bundles);
			replacementChunk.SuperBundleInstallChunks.UnionWith(chunkAssetEntry.SuperBundleInstallChunks);
		}
	}

	private static SpsSoundHeader ReadSpsHeader(Guid chunkId, Segment segment)
	{
		ChunkAssetEntry entry = RequireChunkEntry(chunkId);
		using MemoryStream memoryStream = new MemoryStream(AssetManager.GetAsset(entry).ToArray(), writable: false);
		memoryStream.Position = (uint)((int)segment.SamplesOffset & -4);
		return SpsSoundHeader.LoadFrom(memoryStream);
	}

	private static uint BuildOffsetFlags(bool isValid, bool streaming)
	{
		if (isValid && streaming)
		{
			return 3u;
		}
		return isValid ? 1u : 0u;
	}

	private static int ReadInt32BigEndian(Stream stream)
	{
		Span<byte> span = stackalloc byte[4];
		stream.ReadExactly(span);
		return BinaryPrimitives.ReadInt32BigEndian(span);
	}

	private static bool TryRelinkChunk(EbxPartition partition, object? chunkObject, Guid newChunkId, long newSize)
	{
		if (chunkObject == null)
		{
			return false;
		}
		bool flag = SetMemberValue(chunkObject, "ChunkId", newChunkId);
		flag |= SetMemberValue(chunkObject, "ChunkSize", newSize);
		flag |= SetMemberValue(chunkObject, "Size", newSize);
		return flag | SetMemberValue(chunkObject, "LogicalSize", (uint)Math.Clamp(newSize, 0L, 4294967295L));
	}

	private static Endian DetectEndian(Stream stream)
	{
		Span<byte> span = stackalloc byte[4];
		stream.ReadExactly(span);
		uint num = BinaryPrimitives.ReadUInt32LittleEndian(span);
		if (1 == 0)
		{
		}
		Endian result = num switch
		{
			1701593683u => Endian.Little, 
			1700938323u => Endian.Big, 
			_ => Endian.Little, 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	private static bool LooksLikeHarmonySampleBank(ReadOnlySpan<byte> data)
	{
		if (data.Length < 4)
		{
			return false;
		}
		uint num = BinaryPrimitives.ReadUInt32LittleEndian(data);
		return num == 1701593683 || num == 1700938323;
	}

	private static IReadOnlyList<SpsCandidate> GetSpsCandidates(byte[] chunkBytes)
	{
		if (chunkBytes.Length < 12)
		{
			return Array.Empty<SpsCandidate>();
		}
		List<SpsCandidate> list = new List<SpsCandidate>();
		HashSet<long> hashSet = new HashSet<long>();
		using (MemoryStream stream = new MemoryStream(chunkBytes, writable: false))
		{
			if (TryFindEmbeddedSpsOffset(stream, out var offset))
			{
				SpsSoundHeader spsSoundHeader = TryReadPlausibleSpsHeader(chunkBytes, checked((int)offset));
				if (spsSoundHeader != null)
				{
					list.Add(new SpsCandidate(offset, spsSoundHeader, StrictMatch: true));
					hashSet.Add(offset);
				}
			}
		}
		for (int i = 0; i <= chunkBytes.Length - 12; i += 4)
		{
			if (list.Count >= 32)
			{
				break;
			}
			SpsSoundHeader spsSoundHeader2 = TryReadPlausibleSpsHeader(chunkBytes, i);
			if (spsSoundHeader2 != null && hashSet.Add(i))
			{
				list.Add(new SpsCandidate(i, spsSoundHeader2, StrictMatch: false));
			}
		}
		return (from candidate in list
			orderby candidate.StrictMatch descending, candidate.Offset
			select candidate).ToList();
	}

	private static SpsSoundHeader? TryReadPlausibleSpsHeader(ReadOnlySpan<byte> chunkBytes, int offset)
	{
		if (offset < 0 || offset + 12 > chunkBytes.Length)
		{
			return null;
		}
		try
		{
			SpsSoundHeader spsSoundHeader = SpsSoundHeader.LoadFrom(chunkBytes.Slice(offset, 12));
			byte codec = spsSoundHeader.Codec;
			if ((codec <= 0 || codec > 15) ? true : false)
			{
				return null;
			}
			codec = spsSoundHeader.ChannelConfig;
			if ((codec <= 0 || codec > 32) ? true : false)
			{
				return null;
			}
			int sampleRate = spsSoundHeader.SampleRate;
			bool flag = ((sampleRate < 4000 || sampleRate > 384000) ? true : false);
			if (flag || spsSoundHeader.SamplesCount <= 0)
			{
				return null;
			}
			return spsSoundHeader;
		}
		catch
		{
			return null;
		}
	}

	private static bool TryFindEmbeddedSpsOffset(Stream stream, out long offset)
	{
		offset = 0L;
		if (!stream.CanSeek || stream.Length < 12)
		{
			return false;
		}
		long position = stream.Position;
		Span<byte> span = stackalloc byte[12];
		try
		{
			for (long num = 0L; num <= stream.Length - span.Length; num += 4)
			{
				stream.Position = num;
				stream.ReadExactly(span);
				if (BinaryPrimitives.ReadInt32BigEndian(span.Slice(0, 4)) >> 24 != 72)
				{
					continue;
				}
				SpsSoundHeader spsSoundHeader = SpsSoundHeader.LoadFrom(span);
				byte codec = spsSoundHeader.Codec;
				if ((codec <= 0 || codec > 15) ? true : false)
				{
					continue;
				}
				codec = spsSoundHeader.ChannelConfig;
				if ((codec > 0 && codec <= 32) || 1 == 0)
				{
					int sampleRate = spsSoundHeader.SampleRate;
					bool flag = ((sampleRate < 4000 || sampleRate > 384000) ? true : false);
					if (!flag && spsSoundHeader.SamplesCount > 0 && TryValidateSpsChain(stream, num))
					{
						offset = num;
						return true;
					}
				}
			}
			return false;
		}
		finally
		{
			stream.Position = position;
		}
	}

	private static async Task<byte[]> ReadStreamBytesAsync(Stream stream, CancellationToken cancellationToken)
	{
		MemoryStream memoryStream = stream as MemoryStream;
		ArraySegment<byte> segment = default(ArraySegment<byte>);
		if (memoryStream?.TryGetBuffer(out segment) ?? false)
		{
			int count = checked((int)(memoryStream.Length - memoryStream.Position));
			byte[] copy = new byte[count];
			Buffer.BlockCopy(segment.Array, segment.Offset + checked((int)memoryStream.Position), copy, 0, count);
			return copy;
		}
		long originalPosition = (stream.CanSeek ? stream.Position : 0);
		if (stream.CanSeek)
		{
			stream.Position = originalPosition;
		}
		using MemoryStream output = new MemoryStream();
		await stream.CopyToAsync(output, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		if (stream.CanSeek)
		{
			stream.Position = originalPosition;
		}
		return output.ToArray();
	}

	private static bool TryValidateSpsChain(Stream stream, long offset)
	{
		long position = stream.Position;
		try
		{
			long num = offset;
			int num2 = 0;
			while (num <= stream.Length - 12)
			{
				stream.Position = num;
				int num3 = ReadInt32BigEndian(stream);
				int num4 = (num3 >> 24) & 0xFF;
				int num5 = num3 & 0xFFFFFF;
				if (num5 < 12)
				{
					return false;
				}
				long num6 = num + num5;
				if (num6 > stream.Length)
				{
					return false;
				}
				num2++;
				if (num4 == 69)
				{
					return num2 <= 1 || num6 == stream.Length || IsRemainingTailPadding(stream, num6);
				}
				num = num6;
			}
			return false;
		}
		catch
		{
			return false;
		}
		finally
		{
			stream.Position = position;
		}
	}

	private static bool IsRemainingTailPadding(Stream stream, long offset)
	{
		if (!stream.CanSeek)
		{
			return false;
		}
		if (offset >= stream.Length)
		{
			return true;
		}
		long position = stream.Position;
		try
		{
			stream.Position = offset;
			while (stream.Position < stream.Length)
			{
				int num = stream.ReadByte();
				if (num < 0)
				{
					break;
				}
				if (num != 0)
				{
					return false;
				}
			}
			return true;
		}
		finally
		{
			stream.Position = position;
		}
	}

	private static object? GetMemberValue(object instance, params string[] names)
	{
		Type type = instance.GetType();
		foreach (string name in names)
		{
			PropertyInfo property = type.GetProperty(name, BindingFlags.IgnoreCase | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if ((object)property != null && property.CanRead)
			{
				return property.GetValue(instance);
			}
			FieldInfo field = type.GetField(name, BindingFlags.IgnoreCase | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if ((object)field != null)
			{
				return field.GetValue(instance);
			}
		}
		return null;
	}

	private static object? ResolvePointerTarget(object? value)
	{
		if (value == null)
		{
			return null;
		}
		if (value is PointerRef pointerRef)
		{
			if (pointerRef.Type == PointerRefType.Internal)
			{
				return pointerRef.Internal;
			}
			if (pointerRef.Type == PointerRefType.External)
			{
				EbxAssetEntry ebxAssetEntry = AssetManager.GetEbxAssetEntry(pointerRef.External.PartitionGuid);
				if (ebxAssetEntry != null)
				{
					EbxPartition ebxPartition = AssetManager.GetEbxPartition(ebxAssetEntry);
					return ebxPartition.RootInstances.FirstOrDefault() ?? ebxPartition.PrimaryInstance;
				}
			}
			return null;
		}
		return GetMemberValue(value, "Internal") ?? value;
	}

	private static bool SetMemberValue(object instance, string name, object value)
	{
		Type type = instance.GetType();
		PropertyInfo property = type.GetProperty(name, BindingFlags.IgnoreCase | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		if ((object)property != null && property.CanWrite)
		{
			object value2 = ConvertValueForTargetType(property.PropertyType, value);
			property.SetValue(instance, value2);
			return true;
		}
		FieldInfo field = type.GetField(name, BindingFlags.IgnoreCase | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		if ((object)field != null)
		{
			object value3 = ConvertValueForTargetType(field.FieldType, value);
			field.SetValue(instance, value3);
			return true;
		}
		return false;
	}

	private static int? TryGetInt(object instance, params string[] names)
	{
		return TryConvertToInt(GetMemberValue(instance, names));
	}

	private static bool? TryGetBool(object instance, params string[] names)
	{
		return TryConvertToBool(GetMemberValue(instance, names));
	}

	private static long? TryGetLong(object instance, params string[] names)
	{
		return TryConvertToLong(GetMemberValue(instance, names));
	}

	private static ulong? TryGetULong(object instance, params string[] names)
	{
		object memberValue = GetMemberValue(instance, names);
		object obj = memberValue;
		if (1 == 0)
		{
		}
		ulong? result;
		if (!(obj is ulong value))
		{
			if (!(obj is long num))
			{
				if (!(obj is uint num2))
				{
					if (!(obj is int num3))
					{
						if (obj == null)
						{
							goto IL_00f3;
						}
					}
					else if (num3 >= 0)
					{
						result = (ulong)num3;
						goto IL_0101;
					}
					goto IL_00a6;
				}
				result = num2;
			}
			else
			{
				if (num < 0)
				{
					goto IL_00a6;
				}
				result = (ulong)num;
			}
		}
		else
		{
			result = value;
		}
		goto IL_0101;
		IL_00f3:
		result = null;
		goto IL_0101;
		IL_00a6:
		if (obj == null)
		{
			goto IL_00f3;
		}
		result = TryConvertToULong(obj) ?? TryGetULong(obj, "ResRid", "Rid", "ResourceId", "Value");
		goto IL_0101;
		IL_0101:
		if (1 == 0)
		{
		}
		return result;
	}

	private static Guid? TryGetGuid(object instance, params string[] names)
	{
		return TryConvertToGuid(GetMemberValue(instance, names));
	}

	private static string? TryGetString(object instance, params string[] names)
	{
		object memberValue = GetMemberValue(instance, names);
		if (memberValue == null)
		{
			return null;
		}
		if (memberValue is string result)
		{
			return result;
		}
		if (TryGetBestNestedScalarValue(memberValue, out object value) && value != null)
		{
			if (1 == 0)
			{
			}
			string result2 = ((!(value is string text)) ? Convert.ToString(value, CultureInfo.InvariantCulture) : text);
			if (1 == 0)
			{
			}
			return result2;
		}
		return Convert.ToString(memberValue, CultureInfo.InvariantCulture);
	}

	private static int? TryConvertToInt(object? value)
	{
		value = UnwrapPrimitiveValue(value);
		if (value == null)
		{
			return null;
		}
		object obj = value;
		if (1 == 0)
		{
		}
		int? result;
		if (!(obj is int value2))
		{
			if (!(obj is long num))
			{
				if (!(obj is uint num2))
				{
					if (!(obj is ulong num3))
					{
						if (!(obj is short value3))
						{
							if (!(obj is ushort value4))
							{
								if (!(obj is byte value5))
								{
									if (!(obj is sbyte value6))
									{
										goto IL_0176;
									}
									result = value6;
								}
								else
								{
									result = value5;
								}
							}
							else
							{
								result = value4;
							}
						}
						else
						{
							result = value3;
						}
					}
					else
					{
						if (num3 > int.MaxValue)
						{
							goto IL_0176;
						}
						result = (int)num3;
					}
				}
				else
				{
					if (num2 > int.MaxValue)
					{
						goto IL_0176;
					}
					result = (int)num2;
				}
			}
			else
			{
				if (num < int.MinValue || num > int.MaxValue)
				{
					goto IL_0176;
				}
				result = (int)num;
			}
		}
		else
		{
			result = value2;
		}
		goto IL_01ca;
		IL_01ca:
		if (1 == 0)
		{
		}
		return result;
		IL_0176:
		result = TryConvertFromString<int>(value, int.TryParse) ?? TryConvertFromNested(value, TryConvertToInt);
		goto IL_01ca;
	}

	private static bool? TryConvertToBool(object? value)
	{
		value = UnwrapPrimitiveValue(value);
		if (value == null)
		{
			return null;
		}
		if (1 == 0)
		{
		}
		bool? result;
		if (!(value is bool value2))
		{
			if (!(value is int num))
			{
				if (!(value is long num2))
				{
					if (!(value is uint num3))
					{
						if (!(value is ulong num4))
						{
							if (!(value is byte b))
							{
								if (!(value is sbyte b2))
								{
									if (!(value is short num5))
									{
										if (value is ushort num6)
										{
											result = num6 != 0;
										}
										else
										{
											bool? flag = TryConvertFromString<bool>(value, bool.TryParse);
											bool? flag2;
											if (!flag.HasValue)
											{
												int? num7 = TryConvertToInt(value);
												if (num7.HasValue)
												{
													int valueOrDefault = num7.GetValueOrDefault();
													flag2 = valueOrDefault != 0;
												}
												else
												{
													flag2 = TryConvertFromNested(value, TryConvertToBool);
												}
											}
											else
											{
												flag2 = flag;
											}
											result = flag2;
										}
									}
									else
									{
										result = num5 != 0;
									}
								}
								else
								{
									result = b2 != 0;
								}
							}
							else
							{
								result = b != 0;
							}
						}
						else
						{
							result = num4 != 0;
						}
					}
					else
					{
						result = num3 != 0;
					}
				}
				else
				{
					result = num2 != 0;
				}
			}
			else
			{
				result = num != 0;
			}
		}
		else
		{
			result = value2;
		}
		if (1 == 0)
		{
		}
		return result;
	}

	private static long? TryConvertToLong(object? value)
	{
		value = UnwrapPrimitiveValue(value);
		if (value == null)
		{
			return null;
		}
		object obj = value;
		if (1 == 0)
		{
		}
		long? result;
		if (!(obj is long value2))
		{
			if (!(obj is int num))
			{
				if (!(obj is uint num2))
				{
					if (!(obj is ulong num3))
					{
						if (!(obj is short num4))
						{
							if (!(obj is ushort num5))
							{
								if (!(obj is byte b))
								{
									if (!(obj is sbyte b2))
									{
										goto IL_015a;
									}
									result = b2;
								}
								else
								{
									result = b;
								}
							}
							else
							{
								result = num5;
							}
						}
						else
						{
							result = num4;
						}
					}
					else
					{
						if (num3 > long.MaxValue)
						{
							goto IL_015a;
						}
						result = (long)num3;
					}
				}
				else
				{
					result = num2;
				}
			}
			else
			{
				result = num;
			}
		}
		else
		{
			result = value2;
		}
		goto IL_01ae;
		IL_015a:
		result = TryConvertFromString<long>(value, long.TryParse) ?? TryConvertFromNested(value, TryConvertToLong);
		goto IL_01ae;
		IL_01ae:
		if (1 == 0)
		{
		}
		return result;
	}

	private static ulong? TryConvertToULong(object? value)
	{
		value = UnwrapPrimitiveValue(value);
		if (value == null)
		{
			return null;
		}
		object obj = value;
		if (1 == 0)
		{
		}
		ulong? result;
		if (!(obj is ulong value2))
		{
			if (!(obj is long num))
			{
				if (!(obj is uint num2))
				{
					if (!(obj is int num3))
					{
						if (!(obj is ushort num4))
						{
							if (!(obj is short num5))
							{
								if (!(obj is byte b))
								{
									if (!(obj is sbyte b2) || b2 < 0)
									{
										goto IL_0168;
									}
									result = (ulong)b2;
								}
								else
								{
									result = b;
								}
							}
							else
							{
								if (num5 < 0)
								{
									goto IL_0168;
								}
								result = (ulong)num5;
							}
						}
						else
						{
							result = num4;
						}
					}
					else
					{
						if (num3 < 0)
						{
							goto IL_0168;
						}
						result = (ulong)num3;
					}
				}
				else
				{
					result = num2;
				}
			}
			else
			{
				if (num < 0)
				{
					goto IL_0168;
				}
				result = (ulong)num;
			}
		}
		else
		{
			result = value2;
		}
		goto IL_01bc;
		IL_01bc:
		if (1 == 0)
		{
		}
		return result;
		IL_0168:
		result = TryConvertFromString<ulong>(value, ulong.TryParse) ?? TryConvertFromNested(value, TryConvertToULong);
		goto IL_01bc;
	}

	private static Guid? TryConvertToGuid(object? value)
	{
		value = UnwrapPrimitiveValue(value);
		if (value == null)
		{
			return null;
		}
		object obj = value;
		if (1 == 0)
		{
		}
		Guid? result2;
		if (!(obj is Guid guid))
		{
			if (obj is string text)
			{
				string input = text;
				if (Guid.TryParse(input, out var result))
				{
					result2 = result;
					goto IL_00c8;
				}
			}
			result2 = TryConvertFromString<Guid>(value, Guid.TryParse) ?? TryConvertFromNested(value, TryConvertToGuid);
		}
		else
		{
			Guid value2 = guid;
			result2 = value2;
		}
		goto IL_00c8;
		IL_00c8:
		if (1 == 0)
		{
		}
		return result2;
	}

	private static T? TryConvertFromString<T>(object value, TryParseDelegate<T> tryParse) where T : struct
	{
		string text = Convert.ToString(value, CultureInfo.InvariantCulture);
		T value2;
		return (text != null && tryParse(text, out value2)) ? new T?(value2) : ((T?)null);
	}

	private static T? TryConvertFromNested<T>(object value, Func<object?, T?> converter) where T : struct
	{
		object value2;
		return TryGetBestNestedScalarValue(value, out value2) ? converter(value2) : ((T?)null);
	}

	private static bool TryGetNestedMemberValue(object instance, string name, out object? value)
	{
		value = GetMemberValue(instance, name);
		return value != null;
	}

	private static bool TryGetBestNestedScalarValue(object instance, out object? value)
	{
		MethodInfo method = instance.GetType().GetMethod("ToActualType", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
		if ((object)method != null)
		{
			value = method.Invoke(instance, null);
			if (value != null)
			{
				return true;
			}
		}
		string[] array = new string[4] { "Value", "_Value", "m_value", "value" };
		foreach (string name in array)
		{
			if (TryGetNestedMemberValue(instance, name, out value))
			{
				return true;
			}
		}
		Type type = instance.GetType();
		FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		if (fields.Length == 1)
		{
			value = fields[0].GetValue(instance);
			return value != null;
		}
		PropertyInfo[] array2 = (from property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
			where property.CanRead && property.GetIndexParameters().Length == 0
			select property).ToArray();
		if (array2.Length == 1)
		{
			value = array2[0].GetValue(instance);
			return value != null;
		}
		value = null;
		return false;
	}

	private static object? UnwrapPrimitiveValue(object? value)
	{
		object obj = value;
		int num = 0;
		while (obj != null && num++ < 8)
		{
			Type type = obj.GetType();
			if (type.IsPrimitive || type.IsEnum || obj is string || obj is Guid || obj is decimal)
			{
				return obj;
			}
			if (!TryGetBestNestedScalarValue(obj, out object value2) || value2 == null || value2 == obj)
			{
				break;
			}
			obj = value2;
		}
		return obj;
	}

	private static object ConvertValueForTargetType(Type targetType, object value)
	{
		object obj = UnwrapPrimitiveValue(value) ?? value;
		if (targetType.IsInstanceOfType(obj))
		{
			return obj;
		}
		MethodInfo method = targetType.GetMethod("FromActualType", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[1] { typeof(object) }, null);
		if ((object)method != null)
		{
			object obj2 = Activator.CreateInstance(targetType) ?? throw new InvalidOperationException("Unable to create " + targetType.FullName + ".");
			object obj3 = obj;
			if (TryGetBestNestedScalarValue(obj2, out object value2) && value2 != null)
			{
				Type type = value2.GetType();
				if (!type.IsInstanceOfType(obj3))
				{
					obj3 = Convert.ChangeType(obj3, type, CultureInfo.InvariantCulture);
				}
			}
			method.Invoke(obj2, new object[1] { obj3 });
			return obj2;
		}
		return Convert.ChangeType(obj, targetType, CultureInfo.InvariantCulture);
	}

	private static string SafeName(string value)
	{
		char[] invalidFileNameChars = Path.GetInvalidFileNameChars();
		foreach (char oldChar in invalidFileNameChars)
		{
			value = value.Replace(oldChar, '_');
		}
		return string.IsNullOrWhiteSpace(value) ? "audio" : value;
	}

	private static string TempFile(string extension)
	{
		string text = Path.Combine(Path.GetTempPath(), "FrostyToolsuite", "SoundTools");
		Directory.CreateDirectory(text);
		return Path.Combine(text, $"{Guid.NewGuid():N}{extension}");
	}

	private static void TryDelete(string path)
	{
		try
		{
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
		catch
		{
		}
	}
}
