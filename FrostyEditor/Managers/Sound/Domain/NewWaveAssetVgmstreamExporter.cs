using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using Editor.Lib.Configuration;
using Editor.Lib.Utilities;
using Sdk;
using Sdk.Managers;
using Serilog;

namespace Editor.Lib.Exporters.Sounds;

[Obsolete("Superceded by NewWaveAssetHarmonySampleBankParser")]
public class NewWaveAssetVgmstreamExporter
{
	public int GetChunkCount(EbxAsset ebxAsset)
	{
		ArgumentNullException.ThrowIfNull(ebxAsset, "ebxAsset");
		dynamic rootObject = ebxAsset.RootObject;
		return ((IList)rootObject.Chunks).Count;
	}

	public async Task ExportAsync(AssetManager assetManager, EbxAsset ebxAsset, int selectedIndex, string outputFile)
	{
		ArgumentNullException.ThrowIfNull(assetManager, "assetManager");
		ArgumentNullException.ThrowIfNull(ebxAsset, "ebxAsset");
		ArgumentNullException.ThrowIfNull(outputFile, "outputFile");
		dynamic rootObject = ebxAsset.RootObject;
		IList list = (IList)rootObject.Chunks;
		if (list.Count == 0)
		{
			throw new InvalidDataException("The specified asset does not contain any audio data.");
		}
		if (selectedIndex >= list.Count)
		{
			throw new ArgumentOutOfRangeException("selectedIndex", selectedIndex, "The asset does not contain a chunk with the specified index.");
		}
		Guid id = (Guid)rootObject.Chunks[selectedIndex].ChunkId;
		ChunkAssetEntry chunkEntry = assetManager.GetChunkEntry(id);
		using MemoryStream chunk = assetManager.GetChunk(chunkEntry);
		string tempAudioFile = Path.Combine(EditorConfiguration.Current.EditorDataFolderAbsolute, "tempaudio.sps");
		try
		{
			using FileStream tempAudioStream = new FileStream(tempAudioFile, new FileStreamOptions
			{
				Mode = FileMode.Create,
				Access = FileAccess.Write,
				Share = FileShare.Read,
				Options = FileOptions.Asynchronous,
				PreallocationSize = chunk.Length
			});
			await chunk.CopyToAsync(tempAudioStream).ConfigureAwait(continueOnCapturedContext: false);
			string directoryName = Path.GetDirectoryName(outputFile);
			string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(outputFile);
			string extension = Path.GetExtension(outputFile);
			string newOutputFile = Path.Combine(directoryName, fileNameWithoutExtension + "_?02s") + extension;
			await VgmstreamHelper.DecodeAsync(tempAudioFile, outputFile).ConfigureAwait(continueOnCapturedContext: false);
			await VgmstreamHelper.DecodeAsync(tempAudioFile, newOutputFile, subsongs: true).ConfigureAwait(continueOnCapturedContext: false);
		}
		finally
		{
			try
			{
				File.Delete(tempAudioFile);
			}
			catch (Exception exception)
			{
				Log.Error(exception, "Failed to delete the temporary audio file used in {ClassName}", "NewWaveAssetVgmstreamExporter");
			}
		}
	}
}
