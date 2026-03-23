using System;
using System.Collections;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Editor.Lib.Configuration;
using Editor.Lib.Utilities;
using Sdk;
using Sdk.Managers;
using Serilog;

namespace Editor.Lib.Exporters.Sounds;

public class HarmonySampleBankVgmstreamExporter
{
	public async Task ExportAsync(AssetManager assetManager, EbxAsset ebxAsset, string outputFile, CancellationToken cancellationToken = default(CancellationToken))
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
		int num = rootObject.RamChunkIndex;
		if (num == 255 || num < 0 || num >= list.Count)
		{
			throw new InvalidDataException("The RAM chunk index is incorrect.");
		}
		int num2 = rootObject.StreamChunkIndex;
		if (num2 < 0 || (num2 != 255 && num2 >= list.Count))
		{
			throw new InvalidDataException("The stream chunk index is incorrect.");
		}
		Guid id = (Guid)rootObject.Chunks[num].ChunkId;
		ChunkAssetEntry chunkEntry = assetManager.GetChunkEntry(id);
		using MemoryStream sbrChunk = assetManager.GetChunk(chunkEntry);
		Stream sbsChunk = null;
		if (num2 != 255)
		{
			Guid id2 = (Guid)rootObject.Chunks[num2].ChunkId;
			ChunkAssetEntry chunkEntry2 = assetManager.GetChunkEntry(id2);
			sbsChunk = assetManager.GetChunk(chunkEntry2);
		}
		string tempAudioSbrFile = Path.Combine(EditorConfiguration.Current.EditorDataFolderAbsolute, "tempaudio.sbr");
		string tempAudioSbsFile = Path.Combine(EditorConfiguration.Current.EditorDataFolderAbsolute, "tempaudio.sbs");
		try
		{
			using FileStream tempAudioSbrStream = new FileStream(tempAudioSbrFile, new FileStreamOptions
			{
				Mode = FileMode.Create,
				Access = FileAccess.Write,
				Share = FileShare.Read,
				Options = FileOptions.Asynchronous,
				PreallocationSize = sbrChunk.Length
			});
			await sbrChunk.CopyToAsync(tempAudioSbrStream, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			if (sbsChunk != null)
			{
				using FileStream tempAudioSbsStream = new FileStream(tempAudioSbsFile, new FileStreamOptions
				{
					Mode = FileMode.Create,
					Access = FileAccess.Write,
					Share = FileShare.Read,
					Options = FileOptions.Asynchronous,
					PreallocationSize = sbsChunk.Length
				});
				await sbsChunk.CopyToAsync(tempAudioSbsStream, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			}
			string? directoryName = Path.GetDirectoryName(outputFile);
			string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(outputFile);
			string outputFilePath = string.Concat(str1: Path.GetExtension(outputFile), str0: Path.Combine(directoryName, fileNameWithoutExtension + "_?02s"));
			cancellationToken.ThrowIfCancellationRequested();
			await VgmstreamHelper.DecodeAsync(tempAudioSbrFile, outputFilePath, subsongs: true).ConfigureAwait(continueOnCapturedContext: false);
		}
		finally
		{
			sbsChunk?.Dispose();
			try
			{
				File.Delete(tempAudioSbrFile);
			}
			catch (Exception exception)
			{
				Log.Error(exception, "Failed to delete the temporary audio file used in {ClassName}", "HarmonySampleBankVgmstreamExporter");
			}
			try
			{
				if (File.Exists(tempAudioSbsFile))
				{
					File.Delete(tempAudioSbsFile);
				}
			}
			catch (Exception exception2)
			{
				Log.Error(exception2, "Failed to delete the temporary audio file used in {ClassName}", "HarmonySampleBankVgmstreamExporter");
			}
		}
	}
}
