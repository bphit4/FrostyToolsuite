using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Frosty.Sdk.Ebx;
using Frosty.Sdk.Interfaces;
using Frosty.Sdk.IO;
using Frosty.Sdk.Managers.Entries;
using Frosty.Sdk.Managers.Infos;
using Frosty.Sdk.Managers.Loaders;
using Frosty.Sdk.Managers.Patch;
using Frosty.Sdk.Resources;
using Frosty.Sdk.Utils;

namespace Frosty.Sdk.Managers;

/// <summary>
/// Manages everything related to Assets from the game.
/// </summary>
public static class AssetManager
{
    private readonly record struct ChunkRestoreState
    {
        public long OriginalSize { get; }
        public uint LogicalOffset { get; }
        public uint LogicalSize { get; }

        public ChunkRestoreState(long inOriginalSize, uint inLogicalOffset, uint inLogicalSize)
        {
            OriginalSize = inOriginalSize;
            LogicalOffset = inLogicalOffset;
            LogicalSize = inLogicalSize;
        }
    }

    public static bool IsInitialized { get; private set; }

    private static readonly Dictionary<int, BundleInfo> s_bundleMapping = new();

    private static readonly Dictionary<string, EbxAssetEntry> s_ebxNameMapping = new();
    private static readonly Dictionary<Guid, EbxAssetEntry> s_ebxGuidMapping = new();
    private static readonly Dictionary<string, byte[]> s_modifiedEbxData = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, long> s_originalEbxSizes = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, ResAssetEntry> s_resNameMapping = new();
    private static readonly Dictionary<ulong, ResAssetEntry> s_resRidMapping = new();

    private static readonly Dictionary<Guid, ChunkAssetEntry> s_chunkGuidMapping = new();
    private static readonly Dictionary<ulong, byte[]> s_modifiedResData = new();
    private static readonly Dictionary<ulong, byte[]> s_modifiedResMeta = new();
    private static readonly Dictionary<Guid, byte[]> s_modifiedChunkData = new();
    private static readonly Dictionary<ulong, long> s_originalResSizes = new();
    private static readonly Dictionary<Guid, ChunkRestoreState> s_originalChunkStates = new();
    private static readonly HashSet<Guid> s_addedChunkIds = new();

    /// <summary>
    /// Cache Versions:
    /// <para>1 - Initial Version</para>
    /// <para>2 - Nothing changed in the format just bumped up that the cache gets regenerated, bc bundled chunks did not always had their logical offset/size stored</para>
    /// <para>3 - Completely changed what needs to be stored</para>
    /// <para>4 - Persist EBX dependency lists so references remain available from cache-backed loads</para>
    /// </summary>
    private const uint c_cacheVersion = 4;
    private const ulong c_cacheMagic = 0x02005954534F5246;

    /// <summary>
    /// Parses the games SuperBundles and creates lookups for all Assets, Bundles and SuperBundles.
    /// </summary>
    /// <param name="patchResult">The <see cref="PatchResult"/> that all the changes will get added to, if it is not null and a previous cache exists.</param>
    /// <returns>False if the initialization failed.</returns>
    private static void ResetState()
    {
        IsInitialized = false;
        TypeLibrary.ResetTypeInfoAssets();
        s_bundleMapping.Clear();
        s_ebxNameMapping.Clear();
        s_ebxGuidMapping.Clear();
        s_modifiedEbxData.Clear();
        s_originalEbxSizes.Clear();
        s_resNameMapping.Clear();
        s_resRidMapping.Clear();
        s_chunkGuidMapping.Clear();
        s_modifiedResData.Clear();
        s_modifiedResMeta.Clear();
        s_modifiedChunkData.Clear();
        s_originalResSizes.Clear();
        s_originalChunkStates.Clear();
        s_addedChunkIds.Clear();
    }

    public static bool Initialize(PatchResult? patchResult = null)
    {
        if (IsInitialized)
        {
            return true;
        }

        if (!FileSystemManager.IsInitialized)
        {
            FrostyLogger.Logger?.LogError("FileSystemManager not initialized yet");
            return false;
        }

        if (!ResourceManager.IsInitialized)
        {
            FrostyLogger.Logger?.LogError("ResourceManager not initialized yet");
            return false;
        }

        ResetState();

        if (!ReadCache(out List<EbxAssetEntry> prePatchEbx, out List<ResAssetEntry> prePatchRes,
                out List<ChunkAssetEntry> prePatchChunks))
        {
            DeleteMeshVariationCache();
            Stopwatch timer = new();

            if (FileSystemManager.BundleFormat == BundleFormat.Dynamic2018 || FileSystemManager.BundleFormat == BundleFormat.SuperBundleManifest)
            {
                FrostyLogger.Logger?.LogInfo("Loading FileInfos from catalogs");

                timer.Start();
                ResourceManager.LoadInstallChunks();
                timer.Stop();

                FrostyLogger.Logger?.LogInfo($"Loaded FileInfos from catalogs in {timer.Elapsed.TotalSeconds} seconds");
            }

            IAssetLoader assetLoader = GetAssetLoader();

            FrostyLogger.Logger?.LogInfo("Loading Assets from SuperBundles");

            timer.Restart();
            assetLoader.Load();
            timer.Stop();

            FrostyLogger.Logger?.LogInfo($"Loaded Assets from SuperBundles in {timer.Elapsed.TotalSeconds} seconds");

            ResourceManager.CLearInstallChunks();

            FrostyLogger.Logger?.LogInfo("Indexing Ebx");

            timer.Restart();
            DoEbxIndexing();
            timer.Stop();

            FrostyLogger.Logger?.LogInfo($"Indexed ebx in {timer.Elapsed.TotalSeconds} seconds");

            WriteCache();

            if (prePatchEbx.Count > 0 || prePatchRes.Count > 0 || prePatchChunks.Count > 0)
            {
                if (patchResult != null)
                {
                    // modified/added ebx
                    foreach (EbxAssetEntry ebxAssetEntry in s_ebxNameMapping.Values)
                    {
                        EbxAssetEntry? prePatch = prePatchEbx.Find(e =>
                            e.Name.Equals(ebxAssetEntry.Name, StringComparison.OrdinalIgnoreCase));
                        if (prePatch is not null)
                        {
                            if (prePatch.Sha1 != ebxAssetEntry.Sha1)
                            {
                                patchResult.Modified.Ebx.Add(ebxAssetEntry.Name);
                                prePatchEbx.Remove(prePatch);
                            }
                        }
                        else
                        {
                            patchResult.Added.Ebx.Add(ebxAssetEntry.Name);
                        }
                    }

                    // modified/added res
                    foreach (ResAssetEntry resAssetEntry in s_resNameMapping.Values)
                    {
                        ResAssetEntry? prePatch = prePatchRes.Find(e =>
                            e.Name.Equals(resAssetEntry.Name, StringComparison.OrdinalIgnoreCase));
                        if (prePatch is not null)
                        {
                            if (prePatch.Sha1 != resAssetEntry.Sha1)
                            {
                                patchResult.Modified.Res.Add(resAssetEntry.Name);
                                prePatchRes.Remove(prePatch);
                            }
                        }
                        else
                        {
                            patchResult.Added.Res.Add(resAssetEntry.Name);
                        }
                    }

                    // modified/added chunks
                    foreach (ChunkAssetEntry chunkAssetEntry in s_chunkGuidMapping.Values)
                    {
                        ChunkAssetEntry? prePatch = prePatchChunks.Find(e =>
                            e.Id.Equals(chunkAssetEntry.Id));
                        if (prePatch is not null)
                        {
                            if (prePatch.Sha1 != chunkAssetEntry.Sha1)
                            {
                                patchResult.Modified.Chunks.Add(chunkAssetEntry.Id);
                                prePatchChunks.Remove(prePatch);
                            }
                        }
                        else
                        {
                            patchResult.Added.Chunks.Add(chunkAssetEntry.Id);
                        }
                    }

                    // removed ebx
                    foreach (EbxAssetEntry ebxAssetEntry in prePatchEbx)
                    {
                        patchResult.Removed.Ebx.Add(ebxAssetEntry.Name);
                    }

                    // removed res
                    foreach (ResAssetEntry resAssetEntry in prePatchRes)
                    {
                        patchResult.Removed.Res.Add(resAssetEntry.Name);
                    }

                    // removed chunks
                    foreach (ChunkAssetEntry chunkAssetEntry in prePatchChunks)
                    {
                        patchResult.Removed.Chunks.Add(chunkAssetEntry.Id);
                    }
                }

                prePatchEbx.Clear();
                prePatchRes.Clear();
                prePatchChunks.Clear();
            }
        }

        FrostyLogger.Logger?.LogInfo("Finished initializing");

        IsInitialized = true;
        return true;
    }

    private static void DeleteMeshVariationCache()
    {
        string cacheFileName = $"{ProfilesLibrary.InternalName}_mvdb.cache";
        foreach (string path in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "Caches", cacheFileName),
                     Path.Combine(AppContext.BaseDirectory, cacheFileName),
                     Path.Combine(Environment.CurrentDirectory, "Caches", cacheFileName),
                     Path.Combine(Environment.CurrentDirectory, cacheFileName)
                 }.Distinct(StringComparer.OrdinalIgnoreCase))
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

    #region -- GetEntry --

    #region -- Bundle --

    /// <summary>
    /// Gets the <see cref="BundleInfo"/> by hash.
    /// </summary>
    /// <param name="inHash">The hash of the Bundle.</param>
    /// <returns>The <see cref="BundleInfo"/> or null if it doesn't exist.</returns>
    public static BundleInfo? GetBundleInfo(int inHash)
    {
        return s_bundleMapping.GetValueOrDefault(inHash);
    }

    #endregion

    #region -- AssetEntry --

    #region -- Ebx --

    /// <summary>
    /// Gets the <see cref="EbxAssetEntry"/> by name.
    /// </summary>
    /// <param name="name">The name of the Ebx.</param>
    /// <returns>The <see cref="EbxAssetEntry"/> or null if it doesn't exist.</returns>
    public static EbxAssetEntry? GetEbxAssetEntry(string name)
    {
        return s_ebxNameMapping.GetValueOrDefault(name.ToLower());
    }

    /// <summary>
    /// Gets the <see cref="EbxAssetEntry"/> by <see cref="Guid"/>.
    /// </summary>
    /// <param name="guid">The <see cref="Guid"/> of the Ebx.</param>
    /// <returns>The <see cref="EbxAssetEntry"/> or null if it doesn't exist.</returns>
    public static EbxAssetEntry? GetEbxAssetEntry(Guid guid)
    {
        return s_ebxGuidMapping.GetValueOrDefault(guid);
    }

    #endregion

    #region -- Res --

    /// <summary>
    /// Gets the <see cref="ResAssetEntry"/> by name.
    /// </summary>
    /// <param name="name">The name of the Res.</param>
    /// <returns>The <see cref="ResAssetEntry"/> or null if it doesn't exist.</returns>
    public static ResAssetEntry? GetResAssetEntry(string name)
    {
        return s_resNameMapping.GetValueOrDefault(name.ToLower());
    }

    /// <summary>
    /// Gets the <see cref="ResAssetEntry"/> by Rid.
    /// </summary>
    /// <param name="resRid">The Rid of the Res.</param>
    /// <returns>The <see cref="ResAssetEntry"/> or null if it doesn't exist.</returns>
    public static ResAssetEntry? GetResAssetEntry(ulong resRid)
    {
        return s_resRidMapping.GetValueOrDefault(resRid);
    }

    #endregion

    #region -- Chunk --

    /// <summary>
    /// Gets the <see cref="ChunkAssetEntry"/> by Id.
    /// </summary>
    /// <param name="chunkId">The Id of the Res.</param>
    /// <returns>The <see cref="ChunkAssetEntry"/> or null if it doesn't exist.</returns>
    public static ChunkAssetEntry? GetChunkAssetEntry(Guid chunkId)
    {
        return s_chunkGuidMapping.GetValueOrDefault(chunkId);
    }

    #endregion

    #endregion

    #endregion

    #region -- GetAsset --

    public static EbxPartition GetEbxPartition(EbxAssetEntry entry)
    {
        using (BlockStream stream = new(GetAsset(entry)))
        {
            return EbxPartition.Deserialize(stream);
        }
    }

    public static T GetResAs<T>(ResAssetEntry entry)
        where T : Resource, new()
    {
        using (BlockStream stream = new(GetAsset(entry)))
        {
            T retVal = new();
            ReadOnlySpan<byte> resMeta = s_modifiedResMeta.TryGetValue(entry.ResRid, out byte[]? modifiedMeta)
                ? modifiedMeta
                : entry.ResMeta;
            retVal.Deserialize(stream, resMeta);

            if (retVal is Texture texture && GetChunkAssetEntry(texture.ChunkId) is ChunkAssetEntry chunkEntry)
            {
                using Block<byte> chunkData = GetAsset(chunkEntry);
                texture.SetData(chunkEntry.Id, chunkData.ToArray());
            }

            return retVal;
        }
    }

    public static byte[] GetResMeta(ResAssetEntry entry)
    {
        return s_modifiedResMeta.TryGetValue(entry.ResRid, out byte[]? modifiedMeta)
            ? (byte[])modifiedMeta.Clone()
            : (byte[])entry.ResMeta.Clone();
    }

    public static Block<byte> GetAsset(AssetEntry entry)
    {
        if (entry is EbxAssetEntry ebxEntry && s_modifiedEbxData.TryGetValue(ebxEntry.Name, out byte[]? modifiedEbx))
        {
            return new Block<byte>(modifiedEbx);
        }

        if (entry is ResAssetEntry resEntry && s_modifiedResData.TryGetValue(resEntry.ResRid, out byte[]? modifiedRes))
        {
            return new Block<byte>(modifiedRes);
        }

        if (entry is ChunkAssetEntry chunkEntry && s_modifiedChunkData.TryGetValue(chunkEntry.Id, out byte[]? modifiedChunk))
        {
            return new Block<byte>(modifiedChunk);
        }

        return entry.FileInfo!.GetData((int)entry.OriginalSize);
    }

    public static bool ModifyEbx(EbxAssetEntry entry, EbxPartition partition)
    {
        using MemoryStream memoryStream = new();
        using DataStream stream = new(memoryStream);
        EbxPartition.Serialize(stream, partition, ProfilesLibrary.EbxVersion == 6 ? EbxWriteFlags.DoNotSort : EbxWriteFlags.None);
        return ModifyEbx(entry, memoryStream.ToArray());
    }

    public static bool ModifyEbx(EbxAssetEntry entry, byte[] buffer)
    {
        if (!s_ebxNameMapping.TryGetValue(entry.Name, out EbxAssetEntry? mappedEntry))
        {
            return false;
        }

        if (!s_originalEbxSizes.ContainsKey(entry.Name))
        {
            s_originalEbxSizes[entry.Name] = mappedEntry.OriginalSize;
        }

        s_modifiedEbxData[entry.Name] = (byte[])buffer.Clone();
        mappedEntry.OriginalSize = buffer.Length;
        return true;
    }

    public static bool IsEbxModified(string name)
    {
        return !string.IsNullOrWhiteSpace(name) && s_modifiedEbxData.ContainsKey(name);
    }

    public static bool RevertEbx(string name)
    {
        if (!s_ebxNameMapping.TryGetValue(name, out EbxAssetEntry? entry))
        {
            return false;
        }

        bool changed = s_modifiedEbxData.Remove(name);
        if (s_originalEbxSizes.Remove(name, out long originalSize))
        {
            entry.OriginalSize = originalSize;
            changed = true;
        }

        return changed;
    }

    public static bool ModifyChunk(Guid chunkId, byte[] buffer)
    {
        if (!s_chunkGuidMapping.TryGetValue(chunkId, out ChunkAssetEntry? entry))
        {
            return false;
        }

        if (!s_originalChunkStates.ContainsKey(chunkId))
        {
            s_originalChunkStates[chunkId] = new ChunkRestoreState(entry.OriginalSize, entry.LogicalOffset, entry.LogicalSize);
        }

        s_modifiedChunkData[chunkId] = (byte[])buffer.Clone();
        entry.OriginalSize = buffer.Length;
        entry.LogicalSize = (uint)buffer.Length;
        return true;
    }

    public static ChunkAssetEntry AddChunk(byte[] buffer, Guid? chunkId = null)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        Guid id = chunkId ?? Guid.NewGuid();
        if (s_chunkGuidMapping.TryGetValue(id, out ChunkAssetEntry? existing))
        {
            ModifyChunk(id, buffer);
            return existing;
        }

        ChunkAssetEntry entry = new(id, Frosty.Sdk.Utils.Utils.GenerateSha1(buffer), 0, (uint)buffer.Length);
        s_chunkGuidMapping.Add(id, entry);
        s_modifiedChunkData[id] = (byte[])buffer.Clone();
        s_addedChunkIds.Add(id);
        return entry;
    }

    public static bool ModifyChunk(Guid chunkId, byte[] buffer, Texture? texture)
    {
        if (!ModifyChunk(chunkId, buffer))
        {
            return false;
        }

        if (texture is not null && s_chunkGuidMapping.TryGetValue(chunkId, out ChunkAssetEntry? entry))
        {
            entry.LogicalOffset = texture.LogicalOffset;
            entry.LogicalSize = texture.LogicalSize;
        }

        return true;
    }

    public static void ModifyRes(ulong resRid, byte[] buffer, byte[]? meta = null)
    {
        if (!s_resRidMapping.TryGetValue(resRid, out ResAssetEntry? entry))
        {
            return;
        }

        if (!s_originalResSizes.ContainsKey(resRid))
        {
            s_originalResSizes[resRid] = entry.OriginalSize;
        }

        s_modifiedResData[resRid] = (byte[])buffer.Clone();
        s_modifiedResMeta[resRid] = (byte[])(meta?.Clone() ?? entry.ResMeta.Clone());
        entry.OriginalSize = buffer.Length;
    }

    public static void ModifyRes(ulong resRid, Resource resource)
    {
        if (!s_resRidMapping.TryGetValue(resRid, out ResAssetEntry? entry))
        {
            return;
        }

        byte[] meta = (byte[])entry.ResMeta.Clone();
        using MemoryStream memoryStream = new();
        using DataStream stream = new(memoryStream);
        resource.Serialize(stream, meta);
        ModifyRes(resRid, memoryStream.ToArray(), meta);
    }

    public static bool IsResModified(ulong resRid)
    {
        return s_modifiedResData.ContainsKey(resRid);
    }

    public static bool IsChunkModified(Guid chunkId)
    {
        return s_modifiedChunkData.ContainsKey(chunkId);
    }

    public static bool RevertRes(ulong resRid)
    {
        if (!s_resRidMapping.TryGetValue(resRid, out ResAssetEntry? entry))
        {
            return false;
        }

        bool changed = s_modifiedResData.Remove(resRid);
        s_modifiedResMeta.Remove(resRid);

        if (s_originalResSizes.Remove(resRid, out long originalSize))
        {
            entry.OriginalSize = originalSize;
            changed = true;
        }

        return changed;
    }

    public static bool RevertChunk(Guid chunkId)
    {
        if (s_addedChunkIds.Remove(chunkId))
        {
            bool removed = s_chunkGuidMapping.Remove(chunkId);
            removed |= s_modifiedChunkData.Remove(chunkId);
            s_originalChunkStates.Remove(chunkId);
            return removed;
        }

        if (!s_chunkGuidMapping.TryGetValue(chunkId, out ChunkAssetEntry? entry))
        {
            return false;
        }

        bool changed = s_modifiedChunkData.Remove(chunkId);

        if (s_originalChunkStates.Remove(chunkId, out ChunkRestoreState state))
        {
            entry.OriginalSize = state.OriginalSize;
            entry.LogicalOffset = state.LogicalOffset;
            entry.LogicalSize = state.LogicalSize;
            changed = true;
        }

        return changed;
    }

    public static void ResetModifiedAssets()
    {
        foreach ((string name, long originalSize) in s_originalEbxSizes)
        {
            if (s_ebxNameMapping.TryGetValue(name, out EbxAssetEntry? entry))
            {
                entry.OriginalSize = originalSize;
            }
        }

        foreach ((ulong resRid, long originalSize) in s_originalResSizes)
        {
            if (s_resRidMapping.TryGetValue(resRid, out ResAssetEntry? entry))
            {
                entry.OriginalSize = originalSize;
            }
        }

        foreach ((Guid chunkId, ChunkRestoreState state) in s_originalChunkStates)
        {
            if (s_chunkGuidMapping.TryGetValue(chunkId, out ChunkAssetEntry? entry))
            {
                entry.OriginalSize = state.OriginalSize;
                entry.LogicalOffset = state.LogicalOffset;
                entry.LogicalSize = state.LogicalSize;
            }
        }

        s_modifiedEbxData.Clear();
        s_originalEbxSizes.Clear();
        s_modifiedResData.Clear();
        s_modifiedResMeta.Clear();
        s_modifiedChunkData.Clear();
        s_originalResSizes.Clear();
        s_originalChunkStates.Clear();
        foreach (Guid chunkId in s_addedChunkIds)
        {
            s_chunkGuidMapping.Remove(chunkId);
        }

        s_addedChunkIds.Clear();
    }

    public static Block<byte> GetRawAsset(AssetEntry entry)
    {
        return entry.FileInfo!.GetRawData();
    }

    #endregion

    public static IEnumerable<string> GetEbxNames() => s_ebxNameMapping.Keys;
    public static IEnumerable<string> GetResNames() => s_resNameMapping.Keys;
    public static IEnumerable<Guid> GetChunkIds() => s_chunkGuidMapping.Keys;

    public static IEnumerable<BundleInfo> EnumerateBundleInfos()
    {
        foreach (BundleInfo bundle in s_bundleMapping.Values)
        {
            yield return bundle;
        }
    }

    public static IEnumerable<EbxAssetEntry> EnumerateEbxAssetEntries()
    {
        foreach (EbxAssetEntry entry in s_ebxNameMapping.Values)
        {
            yield return entry;
        }
    }

    public static IEnumerable<ResAssetEntry> EnumerateResAssetEntries()
    {
        foreach (ResAssetEntry entry in s_resNameMapping.Values)
        {
            yield return entry;
        }
    }

    public static IEnumerable<ChunkAssetEntry> EnumerateChunkAssetEntries()
    {
        foreach (ChunkAssetEntry entry in s_chunkGuidMapping.Values)
        {
            yield return entry;
        }
    }

    internal static BundleInfo AddBundle(string name, SuperBundleInstallChunk sbIc)
    {
        if (sbIc.BundleMapping.TryGetValue(name, out BundleInfo? existingBundle))
        {
            return existingBundle;
        }

        BundleInfo bundle = new(name, sbIc);
        if (s_bundleMapping.TryAdd(bundle.Id, bundle))
        {
            return bundle;
        }

        FrostyLogger.Logger?.LogWarning($"Duplicate bundle \"{name}\" in superbundle \"{sbIc.Name}\". Reusing the existing bundle mapping.");
        return s_bundleMapping[bundle.Id];
    }

    private static void UpdateBundle(string inName, BundleInfo inBundleInfo)
    {
        BundleInfo bundle = new(inName, inBundleInfo.Parent);
        s_bundleMapping.Remove(inBundleInfo.Id);
        if (!s_bundleMapping.TryAdd(bundle.Id, bundle))
        {
            FrostyLogger.Logger?.LogWarning($"Failed to remap bundle \"{inName}\" in superbundle \"{inBundleInfo.Parent.Name}\" because the target bundle id already exists.");
        }
    }

    private static IAssetLoader GetAssetLoader()
    {
        switch (FileSystemManager.BundleFormat)
        {
            case BundleFormat.Dynamic2018:
                return new Dynamic2018AssetLoader();
            case BundleFormat.Manifest2019:
                return new Manifest2019AssetLoader();
            case BundleFormat.Kelvin:
                return new KelvinAssetLoader();
            case BundleFormat.SuperBundleManifest:
                return new ManifestAssetLoader();
            default:
                throw new ArgumentException("Not valid AssetLoader.");
        }
    }

    #region -- AddingAssets --

    internal static void AddEbx(EbxAssetEntry entry, int bundleId)
    {
        if (s_ebxNameMapping.TryGetValue(entry.Name, out EbxAssetEntry? existing))
        {
            if (entry.Sha1 == existing.Sha1)
            {
                // assets coming from dlc in dai are always non cas, sometimes they are also in patch, then the sha1 doesnt match up anymore and the basesha1 is the one of the dlc
                // so my guess would be that it patches the asset from the dlc, hopefully no issues arise from using the asset from patch here

                existing.AddFileInfo(entry.FileInfo);
            }

            existing.Bundles.Add(bundleId);
        }
        else
        {
            entry.Bundles.Add(bundleId);
            s_ebxNameMapping.Add(entry.Name, entry);
        }
    }

    internal static void AddRes(ResAssetEntry entry, int bundleId)
    {
        if (s_resNameMapping.TryGetValue(entry.Name, out ResAssetEntry? existing))
        {
            if (entry.Sha1 == existing.Sha1)
            {
                // assets coming from dlc in dai are always non cas, sometimes they are also in patch, then the sha1 doesnt match up anymore and the basesha1 is the one of the dlc
                // so my guess would be that it patches the asset from the dlc, hopefully no issues arise from using the asset from patch here

                existing.AddFileInfo(entry.FileInfo);
            }

            existing.Bundles.Add(bundleId);
        }
        else
        {
            if (entry.ResRid != 0)
            {
                if (!s_resRidMapping.TryAdd(entry.ResRid, entry))
                {
                    FrostyLogger.Logger?.LogWarning($"Duplicate ResRid using {s_resRidMapping[entry.ResRid].Name} instead of {entry.Name}");
                    return;
                }
            }
            entry.Bundles.Add(bundleId);
            s_resNameMapping.Add(entry.Name, entry);
        }
    }

    internal static void AddChunk(ChunkAssetEntry entry, int bundleId)
    {
        if (s_chunkGuidMapping.TryGetValue(entry.Id, out ChunkAssetEntry? existing))
        {
            if (existing.LogicalSize == 0)
            {
                // this chunk was first added as a superbundle chunk, so add logical offset/size and sha1
                existing.Sha1 = entry.Sha1;
                existing.LogicalOffset = entry.LogicalOffset;
                existing.LogicalSize = entry.LogicalSize;
                existing.OriginalSize = entry.OriginalSize;
            }

            if (entry.Sha1 == existing.Sha1)
            {
                // assets coming from dlc in dai are always non cas, sometimes they are also in patch, then the sha1 doesnt match up anymore and the basesha1 is the one of the dlc
                // so my guess would be that it patches the asset from the dlc, hopefully no issues arise from using the asset from patch here

                existing.AddFileInfo(entry.FileInfo);
            }

            existing.Bundles.Add(bundleId);
        }
        else
        {
            entry.Bundles.Add(bundleId);
            s_chunkGuidMapping.Add(entry.Id, entry);
        }
    }

    /// <summary>
    /// Adds Chunk contained in the toc of a SuperBundle to the AssetManager.
    /// This will override any location of where an already processed chunk was stored,
    /// so that TextureChunks which are stored in Bundles have the correct data.
    /// </summary>
    /// <param name="entry">The <see cref="ChunkAssetEntry"/> of the Chunk.</param>
    internal static void AddSuperBundleChunk(ChunkAssetEntry entry)
    {
        if (s_chunkGuidMapping.TryGetValue(entry.Id, out ChunkAssetEntry? existing))
        {
            // add existing Bundles
            entry.Bundles.UnionWith(existing.Bundles);

            if (existing.FileInfo is not null)
            {
                entry.AddFileInfo(existing.FileInfo);
            }

            // add logicalOffset/Size, since those are only stored in bundles
            entry.LogicalOffset = existing.LogicalOffset;
            entry.LogicalSize = existing.LogicalSize;
            entry.OriginalSize = existing.OriginalSize;

            // add Sha1, since its only stored in bundles for some formats
            entry.Sha1 = existing.Sha1;

            // merge SuperBundleInstallChunks
            entry.SuperBundleInstallChunks.UnionWith(existing.SuperBundleInstallChunks);

            s_chunkGuidMapping[entry.Id] = entry;
        }
        else
        {
            s_chunkGuidMapping.Add(entry.Id, entry);
        }
    }

    #endregion

    #region -- Cache --

    private static void DoEbxIndexing()
    {
        if (s_ebxGuidMapping.Count > 0)
        {
            return;
        }

        foreach (EbxAssetEntry entry in s_ebxNameMapping.Values)
        {
            if (entry.FileInfo is null)
            {
                s_ebxNameMapping.Remove(entry.Name);
                FrostyLogger.Logger?.LogWarning($"Skipping ebx \"{entry.Name}\", bc it has no FileInfo!");
                continue;
            }

            using (BlockStream stream = new(GetAsset(entry)))
            {
                BaseEbxReader reader = BaseEbxReader.CreateReader(stream);
                entry.Type = reader.GetRootType();
                entry.Guid = reader.GetPartitionGuid();

                entry.DependentAssets.UnionWith(reader.GetDependencies());

                if (s_ebxGuidMapping.TryGetValue(entry.Guid, out EbxAssetEntry? other))
                {
                    // happens when they changed the name when patching it

                    // since we load patch superbundles first the first one should be correct most of the time, hopefully not too many issues arise bc of this
                    FrostyLogger.Logger?.LogWarning($"Removing ebx \"{entry.Name}\" with same guid as \"{other.Name}\"");

                    s_ebxNameMapping.Remove(entry.Name);
                }
                else
                {
                    s_ebxGuidMapping.Add(entry.Guid, entry);

                    if (TypeLibrary.IsSubClassOf(entry.Type, "TypeInfoAsset"))
                    {
                        EbxPartition asset = reader.ReadPartition<EbxPartition>();
                        TypeLibrary.AddTypeInfoAsset(asset.PrimaryInstanceGuid, asset.PrimaryInstance);
                    }
                }
            }

            // Manifest AssetLoader has stripped the bundle names, so we need to figure out the ui bundles, since the ebx are not in the bundle with the same name
            if (FileSystemManager.BundleFormat == BundleFormat.SuperBundleManifest &&
                (TypeLibrary.IsSubClassOf(entry.Type, "UIItemDescriptionAsset") ||
                 TypeLibrary.IsSubClassOf(entry.Type, "UIMetaDataAsset")))
            {
                string name = $"{FileSystemManager.GamePlatform}/{entry.Name}_bundle";
                string hash = Utils.Utils.HashString(name, true).ToString("X8");

                BundleInfo? bundle = s_bundleMapping.Values.FirstOrDefault(b => b.Name == hash);
                if (bundle is not null)
                {
                    UpdateBundle(name, bundle);
                }
            }
        }

        foreach (ResAssetEntry entry in s_resNameMapping.Values)
        {
            if (entry.FileInfo is null)
            {
                s_resNameMapping.Remove(entry.Name);
                FrostyLogger.Logger?.LogWarning($"Skipping res \"{entry.Name}\", bc it has no FileInfo!");
            }
        }

        int a = 0;
        foreach (ChunkAssetEntry entry in s_chunkGuidMapping.Values)
        {
            if (entry.FileInfo is null)
            {
                s_chunkGuidMapping.Remove(entry.Id);
                FrostyLogger.Logger?.LogWarning($"Skipping chunk {entry.Id}, bc it has no FileInfo!");
            }
            else if (entry.LogicalSize == 0)
            {
                a++;
                entry.OriginalSize = entry.FileInfo.GetOriginalSize();
                entry.LogicalSize = (uint)entry.OriginalSize;
            }
        }
        FrostyLogger.Logger?.LogInfo($"Had to resolve OriginalSize for {a} chunks");
    }

    private static bool ReadCache(out List<EbxAssetEntry> prePatchEbx, out List<ResAssetEntry> prePatchRes, out List<ChunkAssetEntry> prePatchChunks)
    {
        prePatchEbx = new List<EbxAssetEntry>();
        prePatchRes = new List<ResAssetEntry>();
        prePatchChunks = new List<ChunkAssetEntry>();

        if (!File.Exists($"{FileSystemManager.CacheName}.cache"))
        {
            return false;
        }

        bool isPatched = false;

        using (DataStream stream = new(new FileStream($"{FileSystemManager.CacheName}.cache", FileMode.Open, FileAccess.Read)))
        {
            ulong magic = stream.ReadUInt64();
            if (magic != c_cacheMagic)
            {
                return false;
            }

            uint version = stream.ReadUInt32();
            if (version != c_cacheVersion)
            {
                return false;
            }

            int profileNameHash = stream.ReadInt32();
            if (profileNameHash != Utils.Utils.HashString(ProfilesLibrary.ProfileName, true))
            {
                return false;
            }

            uint head = stream.ReadUInt32();
            if (head != FileSystemManager.Head)
            {
                isPatched = true;
            }

            int bundleCount = stream.ReadInt32();
            for (int i = 0; i < bundleCount; i++)
            {
                string name = stream.ReadNullTerminatedString();
                string sbIcName = stream.ReadNullTerminatedString();
                if (!isPatched)
                {
                    SuperBundleInstallChunk sbIc = FileSystemManager.GetSuperBundleInstallChunk(sbIcName);
                    AddBundle(name, sbIc);
                }
            }

            FrostyLogger.Logger?.LogInfo("Loading ebx from cache");
            int ebxCount = stream.ReadInt32();
            for (int i = 0; i < ebxCount; i++)
            {
                FrostyLogger.Logger?.LogProgress(i / (double)ebxCount);
                string name = stream.ReadNullTerminatedString();

                EbxAssetEntry entry = new(name, stream.ReadSha1(), stream.ReadInt64())
                {
                    Guid = stream.ReadGuid(),
                    Type = stream.ReadNullTerminatedString()
                };

                int dependencyCount = stream.ReadInt32();
                for (int j = 0; j < dependencyCount; j++)
                {
                    entry.DependentAssets.Add(stream.ReadGuid());
                }

                entry.AddFileInfo(IFileInfo.Deserialize(stream));

                int numBundles = stream.ReadInt32();
                for (int j = 0; j < numBundles; j++)
                {
                    entry.Bundles.Add(stream.ReadInt32());
                }

                if (isPatched)
                {
                    prePatchEbx.Add(entry);
                }
                else
                {
                    s_ebxGuidMapping.Add(entry.Guid, entry);
                    s_ebxNameMapping.Add(entry.Name, entry);
                }
            }

            FrostyLogger.Logger?.LogInfo("Loading res from cache");
            int resCount = stream.ReadInt32();
            for (int i = 0; i < resCount; i++)
            {
                FrostyLogger.Logger?.LogProgress(i / (double)resCount);
                string name = stream.ReadNullTerminatedString();

                ResAssetEntry entry = new(name, stream.ReadSha1(), stream.ReadInt64(),
                    stream.ReadUInt64(), stream.ReadUInt32(), stream.ReadBytes(stream.ReadInt32()));

                entry.AddFileInfo(IFileInfo.Deserialize(stream));

                int numBundles = stream.ReadInt32();
                for (int j = 0; j < numBundles; j++)
                {
                    entry.Bundles.Add(stream.ReadInt32());
                }

                if (isPatched)
                {
                    prePatchRes.Add(entry);
                }
                else
                {
                    if (entry.ResRid != 0)
                    {
                        s_resRidMapping.Add(entry.ResRid, entry);
                    }
                    s_resNameMapping.Add(name, entry);
                }
            }

            FrostyLogger.Logger?.LogInfo("Loading chunks from cache");
            int chunkCount = stream.ReadInt32();
            for (int i = 0; i < chunkCount; i++)
            {
                FrostyLogger.Logger?.LogProgress(i / (double)chunkCount);
                ChunkAssetEntry entry = new(stream.ReadGuid(), stream.ReadSha1(),
                    stream.ReadUInt32(), stream.ReadUInt32());

                entry.AddFileInfo(IFileInfo.Deserialize(stream));

                int numSuperBundles = stream.ReadInt32();
                for (int j = 0; j < numSuperBundles; j++)
                {
                    entry.SuperBundleInstallChunks.Add(stream.ReadInt32());
                }

                int numBundles = stream.ReadInt32();
                for (int j = 0; j < numBundles; j++)
                {
                    entry.Bundles.Add(stream.ReadInt32());
                }

                if (isPatched)
                {
                    prePatchChunks.Add(entry);
                }
                else
                {
                    s_chunkGuidMapping.Add(entry.Id, entry);
                }
            }

            TypeLibrary.ReadCache(stream);
        }

        return !isPatched;
    }

    private static void WriteCache()
    {
        FileInfo fi = new($"{FileSystemManager.CacheName}.cache");
        Directory.CreateDirectory(fi.DirectoryName!);

        using (DataStream stream = new(new FileStream(fi.FullName, FileMode.Create, FileAccess.Write)))
        {
            stream.WriteUInt64(c_cacheMagic);
            stream.WriteUInt32(c_cacheVersion);

            stream.WriteInt32(Utils.Utils.HashString(ProfilesLibrary.ProfileName, true));
            stream.WriteUInt32(FileSystemManager.Head);

            stream.WriteInt32(s_bundleMapping.Count);
            foreach (BundleInfo bundle in s_bundleMapping.Values)
            {
                stream.WriteNullTerminatedString(bundle.Name);
                stream.WriteNullTerminatedString(bundle.Parent.Name);
            }

            stream.WriteInt32(s_ebxNameMapping.Count);
            foreach (EbxAssetEntry entry in s_ebxNameMapping.Values)
            {
                stream.WriteNullTerminatedString(entry.Name);

                stream.WriteSha1(entry.Sha1);
                stream.WriteInt64(entry.OriginalSize);

                stream.WriteGuid(entry.Guid);
                stream.WriteNullTerminatedString(entry.Type);

                stream.WriteInt32(entry.DependentAssets.Count);
                foreach (Guid dependency in entry.DependentAssets)
                {
                    stream.WriteGuid(dependency);
                }

                IFileInfo.Serialize(stream, entry.FileInfo!);

                stream.WriteInt32(entry.Bundles.Count);
                foreach (int bundleId in entry.Bundles)
                {
                    stream.WriteInt32(bundleId);
                }
            }

            stream.WriteInt32(s_resNameMapping.Count);
            foreach (ResAssetEntry entry in s_resNameMapping.Values)
            {
                stream.WriteNullTerminatedString(entry.Name);

                stream.WriteSha1(entry.Sha1);
                stream.WriteInt64(entry.OriginalSize);

                stream.WriteUInt64(entry.ResRid);
                stream.WriteUInt32((uint)entry.ResType);
                stream.WriteInt32(entry.ResMeta.Length);
                stream.Write(entry.ResMeta, 0, entry.ResMeta.Length);

                IFileInfo.Serialize(stream, entry.FileInfo!);

                stream.WriteInt32(entry.Bundles.Count);
                foreach (int bundleId in entry.Bundles)
                {
                    stream.WriteInt32(bundleId);
                }
            }

            stream.WriteInt32(s_chunkGuidMapping.Count);
            foreach (ChunkAssetEntry entry in s_chunkGuidMapping.Values)
            {
                stream.WriteGuid(entry.Id);

                stream.WriteSha1(entry.Sha1);

                stream.WriteUInt32(entry.LogicalOffset);
                stream.WriteUInt32(entry.LogicalSize);

                IFileInfo.Serialize(stream, entry.FileInfo!);

                stream.WriteInt32(entry.SuperBundleInstallChunks.Count);
                foreach (int superBundleId in entry.SuperBundleInstallChunks)
                {
                    stream.WriteInt32(superBundleId);
                }

                stream.WriteInt32(entry.Bundles.Count);
                foreach (int bundleId in entry.Bundles)
                {
                    stream.WriteInt32(bundleId);
                }
            }

            TypeLibrary.WriteCache(stream);
        }
    }

    #endregion
}

