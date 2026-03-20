using System;
using System.Collections.Generic;
using Frosty.Sdk;
using Frosty.Sdk.Ebx;
using Frosty.Sdk.IO;
using Frosty.Sdk.Managers;
using Frosty.Sdk.Managers.Entries;
using Frosty.Sdk.Profiles;
using Frosty.Sdk.Utils;
using FrostyEditor.Models;

namespace FrostyEditor.Managers;

public static class LegacyAssetLoader
{
    public sealed record LoadResult(
        LegacyFolderTreeNodeModel Root,
        int AssetCount,
        int CollectorCount,
        bool IsSupported,
        string Status,
        string Details);

    private enum LegacyCollectorFormat
    {
        None,
        V1,
        V2,
    }

    public static LoadResult Load()
    {
        if (!TryGetCollectorFormat(out LegacyCollectorFormat format))
        {
            return new LoadResult(
                new LegacyFolderTreeNodeModel("ROOT") { IsExpanded = true },
                0,
                0,
                false,
                "Legacy assets are not supported for this profile.",
                "The active profile does not use Frosty's legacy ChunkFileCollector system.");
        }

        FrostyLogger.Logger?.LogInfo("Loading legacy explorer data");

        Dictionary<string, LegacyAssetModel> assets = new(StringComparer.OrdinalIgnoreCase);
        int collectorCount = 0;

        foreach (EbxAssetEntry entry in AssetManager.EnumerateEbxAssetEntries())
        {
            if (!entry.Type.Contains("ChunkFileCollector", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryGetCollectorChunkId(entry, out Guid chunkId))
            {
                FrostyLogger.Logger?.LogWarning($"Skipping legacy collector \"{entry.Name}\" because its manifest chunk could not be resolved.");
                continue;
            }

            ChunkAssetEntry? collectorChunk = AssetManager.GetChunkAssetEntry(chunkId);
            if (collectorChunk is null)
            {
                FrostyLogger.Logger?.LogWarning($"Skipping legacy collector \"{entry.Name}\" because chunk \"{chunkId}\" is missing.");
                continue;
            }

            collectorCount++;

            try
            {
                using Block<byte> data = AssetManager.GetAsset(collectorChunk);
                using BlockStream stream = new(data);

                switch (format)
                {
                    case LegacyCollectorFormat.V1:
                        ReadV1Collector(entry, stream, assets);
                        break;
                    case LegacyCollectorFormat.V2:
                        ReadV2Collector(entry, stream, assets);
                        break;
                }
            }
            catch (Exception ex)
            {
                FrostyLogger.Logger?.LogWarning($"Failed to parse legacy collector \"{entry.Name}\": {ex.Message}");
            }
        }

        LegacyFolderTreeNodeModel root = LegacyFolderTreeNodeModel.Create(assets.Values);
        if (assets.Count == 0)
        {
            return new LoadResult(
                root,
                0,
                collectorCount,
                true,
                "No legacy assets were found.",
                "No parsable ChunkFileCollector data was found for the active profile.");
        }

        string versionText = format == LegacyCollectorFormat.V1 ? "V1" : "V2";
        return new LoadResult(
            root,
            assets.Count,
            collectorCount,
            true,
            $"Loaded {assets.Count} legacy assets.",
            $"Parsed {collectorCount} ChunkFileCollector entries using the {versionText} legacy format. Legacy export is available from the asset context menu.");
    }

    public static byte[] ReadAssetBytes(LegacyAssetModel asset)
    {
        LegacyAssetModel.CollectorInstance instance = asset.PrimaryInstance;
        ChunkAssetEntry? chunkEntry = AssetManager.GetChunkAssetEntry(instance.ChunkId);
        if (chunkEntry is null)
        {
            throw new InvalidOperationException($"Legacy chunk \"{instance.ChunkId}\" could not be found for asset \"{asset.FullName}\".");
        }

        using Block<byte> chunkData = AssetManager.GetAsset(chunkEntry);
        using BlockStream stream = new(chunkData);
        stream.Position = instance.Offset;
        return stream.ReadBytes(checked((int)instance.Size));
    }

    private static bool TryGetCollectorFormat(out LegacyCollectorFormat format)
    {
        if (ProfilesLibrary.IsLoaded(
                ProfileVersion.Fifa17,
                ProfileVersion.Fifa18,
                ProfileVersion.Madden19,
                ProfileVersion.Fifa19,
                ProfileVersion.Madden20,
                ProfileVersion.Fifa20))
        {
            format = LegacyCollectorFormat.V1;
            return true;
        }

        if (ProfilesLibrary.IsLoaded(
                ProfileVersion.Fifa21,
                ProfileVersion.Madden22,
                ProfileVersion.Fifa22,
                ProfileVersion.Madden23,
                ProfileVersion.Fifa23))
        {
            format = LegacyCollectorFormat.V2;
            return true;
        }

        format = LegacyCollectorFormat.None;
        return false;
    }

    private static bool TryGetCollectorChunkId(EbxAssetEntry entry, out Guid chunkId)
    {
        chunkId = Guid.Empty;

        EbxPartition partition = AssetManager.GetEbxPartition(entry);
        object rootInstance = partition.PrimaryInstance;
        if (!rootInstance.TryGetProperty("Manifest", out object? manifest) || manifest is null)
        {
            return false;
        }

        if (!manifest.TryGetProperty("ChunkId", out Guid resolvedChunkId))
        {
            return false;
        }

        chunkId = resolvedChunkId;
        return chunkId != Guid.Empty;
    }

    private static void ReadV1Collector(
        EbxAssetEntry collectorEntry,
        DataStream reader,
        Dictionary<string, LegacyAssetModel> assets)
    {
        uint entryCount = reader.ReadUInt32();
        long entryOffset = reader.ReadInt64();

        reader.Position = entryOffset;
        for (uint i = 0; i < entryCount; i++)
        {
            string name = ReadCollectorString(reader);
            LegacyAssetModel asset = GetOrCreateAsset(assets, name);
            long compressedOffset = reader.ReadInt64();
            long compressedSize = reader.ReadInt64();
            long offset = reader.ReadInt64();
            long size = reader.ReadInt64();
            asset.AddInstance(new LegacyAssetModel.CollectorInstance(
                reader.ReadGuid(),
                offset,
                size,
                compressedOffset,
                compressedSize,
                collectorEntry.Name));
        }
    }

    private static void ReadV2Collector(
        EbxAssetEntry collectorEntry,
        DataStream reader,
        Dictionary<string, LegacyAssetModel> assets)
    {
        int pathCount = reader.ReadInt32();
        long pathOffset = reader.ReadInt64();

        int entryCount = reader.ReadInt32();
        long entryOffset = reader.ReadInt64();

        int cacheIndexCount = reader.ReadInt32();
        long cacheIndexOffset = reader.ReadInt64();

        int chunkCount = reader.ReadInt32();
        long chunkOffset = reader.ReadInt64();

        reader.Position = pathOffset;
        for (int i = 0; i < pathCount; i++)
        {
            _ = ReadCollectorString(reader);
        }

        reader.Position = entryOffset;
        for (int i = 0; i < entryCount; i++)
        {
            string name = ReadCollectorString(reader);
            LegacyAssetModel asset = GetOrCreateAsset(assets, name);
            long compressedOffset = reader.ReadInt64();
            long compressedSize = reader.ReadInt64();
            long offset = reader.ReadInt64();
            long size = reader.ReadInt64();
            asset.AddInstance(new LegacyAssetModel.CollectorInstance(
                reader.ReadGuid(),
                offset,
                size,
                compressedOffset,
                compressedSize,
                collectorEntry.Name));
        }

        reader.Position = cacheIndexOffset;
        for (int i = 0; i < cacheIndexCount; i++)
        {
            _ = ReadCollectorString(reader);
            reader.Position += sizeof(int) * 2;
        }

        reader.Position = chunkOffset;
        for (int i = 0; i < chunkCount; i++)
        {
            reader.Position += sizeof(long) + 16;
        }
    }

    private static string ReadCollectorString(DataStream reader)
    {
        long stringOffset = reader.ReadInt64();
        long currentPosition = reader.Position;
        reader.Position = stringOffset;
        string value = reader.ReadNullTerminatedString();
        reader.Position = currentPosition;
        return value;
    }

    private static LegacyAssetModel GetOrCreateAsset(Dictionary<string, LegacyAssetModel> assets, string name)
    {
        if (!assets.TryGetValue(name, out LegacyAssetModel? asset))
        {
            asset = new LegacyAssetModel(name);
            assets.Add(name, asset);
        }

        return asset;
    }
}
