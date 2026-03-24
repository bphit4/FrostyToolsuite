using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Frosty.Sdk;
using Frosty.Sdk.Ebx;
using Frosty.Sdk.Interfaces;
using Frosty.Sdk.IO.Ebx;
using Frosty.Sdk.Managers;
using Frosty.Sdk.Managers.Entries;

namespace FrostyEditor.Managers;

public sealed record MeshVariationDatabaseLocation(
    EbxAssetEntry VariationDatabaseEntry,
    PointerRef VariationDatabase,
    int Index);

public sealed record MeshVariationTextureParameter(
    string ParameterName,
    PointerRef Texture,
    EbxAssetEntry? TextureEntry);

public sealed record MeshVariationMaterialRecord(
    Guid MaterialGuid,
    PointerRef MaterialVariation,
    IReadOnlyList<MeshVariationTextureParameter> TextureParameters);

public sealed record MeshVariationRecord(
    string Name,
    uint VariationAssetNameHash,
    object? VariationEntry,
    PointerRef Variation,
    IReadOnlyList<MeshVariationMaterialRecord> Materials,
    IReadOnlyList<MeshVariationDatabaseLocation> MeshVariationDbs)
{
    public bool IsDefault => VariationAssetNameHash == 0;
}

internal static class MeshVariationDatabaseManager
{
    private const int c_mvdbCacheVersion = 2;

    private sealed class MeshVariationBuilder
    {
        public required string Name { get; init; }
        public uint VariationAssetNameHash { get; init; }
        public object? VariationEntry { get; set; }
        public PointerRef Variation { get; set; }
        public List<MeshVariationMaterialRecord> Materials { get; } = [];
        public List<MeshVariationDatabaseLocation> Locations { get; } = [];
    }

    private static readonly object c_cacheLock = new();
    private static readonly object c_buildLock = new();
    private static readonly object c_cacheFileLock = new();
    private static readonly Dictionary<Guid, IReadOnlyList<MeshVariationRecord>> c_variationsByMeshGuid = [];
    private static IReadOnlyList<EbxAssetEntry>? c_variationDatabaseEntries;
    private static Dictionary<uint, EbxAssetEntry>? c_objectVariationEntriesByHash;
    private static int c_backgroundWarmupStarted;
    private static bool c_cacheFileLoaded;
    private static bool c_cacheLoadInProgress;
    private static int c_loadedCacheMeshCount;
    private static Task? c_fullCacheBuildTask;

    public static void Invalidate(Guid meshGuid)
    {
        lock (c_cacheLock)
        {
            c_variationsByMeshGuid.Remove(meshGuid);
        }
    }

    public static void StartBackgroundWarmup()
    {
        if (Interlocked.Exchange(ref c_backgroundWarmupStarted, 1) != 0)
        {
            return;
        }

        FrostyLogger.Logger?.LogInfo(
            "Loading MVDB cache metadata in the background. Mesh loading times will improve once the cache is fully ready.");
        _ = Task.Run(WarmCache);
    }

    public static void StartFullCacheBuildIfNeeded()
    {
        EnsureCacheLoaded();

        if (HasUsableCache())
        {
            return;
        }

        lock (c_cacheLock)
        {
            if (c_fullCacheBuildTask is { IsCompleted: false })
            {
                return;
            }
        }

        FrostyLogger.Logger?.LogInfo(
            "MVDB cache missing or stale, starting background build. Mesh loading times will improve once it completes.");

        Task buildTask = Task.Run(() =>
        {
            try
            {
                BuildFullCache();
            }
            catch (Exception ex)
            {
                FrostyLogger.Logger?.LogWarning($"Failed to build full MVDB cache: {ex.Message}");
            }
        });

        lock (c_cacheLock)
        {
            c_fullCacheBuildTask = buildTask;
        }
    }

    public static IReadOnlyList<MeshVariationRecord> GetVariations(EbxAssetEntry meshEntry)
    {
        EnsureCacheLoaded();

        lock (c_cacheLock)
        {
            if (c_variationsByMeshGuid.TryGetValue(meshEntry.Guid, out IReadOnlyList<MeshVariationRecord>? cached))
            {
                return cached;
            }
        }

        lock (c_buildLock)
        {
            lock (c_cacheLock)
            {
                if (c_variationsByMeshGuid.TryGetValue(meshEntry.Guid, out IReadOnlyList<MeshVariationRecord>? cached))
                {
                    return cached;
                }
            }

            IReadOnlyList<MeshVariationRecord> variations = BuildVariations(meshEntry);
            if (variations.Count > 0)
            {
                lock (c_cacheLock)
                {
                    c_variationsByMeshGuid[meshEntry.Guid] = variations;
                }
            }
            return variations;
        }
    }

    public static IReadOnlyList<MeshVariationRecord> GetDisplayVariations(MeshAssetLoadResult load)
    {
        IReadOnlyList<MeshVariationRecord> variations = GetVariations(load.Entry);
        if (variations.Count > 0)
        {
            return variations;
        }

        return [CreateSyntheticDefaultVariation(load)];
    }

    public static object? GetDefaultVariationEntry(EbxAssetEntry meshEntry)
    {
        IReadOnlyList<MeshVariationRecord> variations = GetVariations(meshEntry);
        return variations.FirstOrDefault(static variation => variation.IsDefault)?.VariationEntry ??
               variations.FirstOrDefault()?.VariationEntry;
    }

    public static MeshVariationRecord? GetDefaultVariationRecord(EbxAssetEntry meshEntry)
    {
        IReadOnlyList<MeshVariationRecord> variations = GetVariations(meshEntry);
        return variations.FirstOrDefault(static variation => variation.IsDefault) ??
               variations.FirstOrDefault();
    }

    public static MeshVariationRecord? TryGetCachedDefaultVariationRecord(EbxAssetEntry meshEntry)
    {
        EnsureCacheLoaded();

        lock (c_cacheLock)
        {
            if (!c_variationsByMeshGuid.TryGetValue(meshEntry.Guid, out IReadOnlyList<MeshVariationRecord>? variations))
            {
                return null;
            }

            return variations.FirstOrDefault(static variation => variation.IsDefault) ??
                   variations.FirstOrDefault();
        }
    }

    private static void WarmCache()
    {
        EnsureCacheLoaded();
        EnsureVariationDatabaseEntries();
        EnsureObjectVariationMapping();
    }

    private static void BuildFullCache()
    {
        EnsureCacheLoaded();

        lock (c_buildLock)
        {
            IReadOnlyList<EbxAssetEntry> variationDatabaseEntries = EnsureVariationDatabaseEntries();
            FrostyLogger.Logger?.LogInfo(
                $"Building full MVDB cache in the background ({variationDatabaseEntries.Count} variation databases)...");

            Dictionary<Guid, Dictionary<uint, MeshVariationBuilder>> buildersByMeshGuid = [];
            Dictionary<uint, EbxAssetEntry?> objectVariationEntryCache = [];
            Dictionary<uint, PointerRef> objectVariationPointerCache = [];
            Stopwatch progressTimer = Stopwatch.StartNew();

            for (int variationDatabaseIndex = 0; variationDatabaseIndex < variationDatabaseEntries.Count; variationDatabaseIndex++)
            {
                EbxAssetEntry variationDatabaseEntry = variationDatabaseEntries[variationDatabaseIndex];
                EbxPartition partition = AssetManager.GetEbxPartition(variationDatabaseEntry);
                PointerRef variationDatabasePointer = new(new EbxImportReference
                {
                    PartitionGuid = partition.PartitionGuid,
                    InstanceGuid = partition.PrimaryInstanceGuid
                });
                object rootObject = partition.PrimaryInstance;
                if (!MeshAssetOperations.TryGetMemberValue(rootObject, "Entries", out object? entriesValue) ||
                    entriesValue is null)
                {
                    continue;
                }

                int index = 0;
                foreach (object? candidate in EnumerateValues(entriesValue))
                {
                    if (candidate is null)
                    {
                        index++;
                        continue;
                    }

                    if (!TryReadMeshGuid(candidate, out Guid meshGuid) || meshGuid == Guid.Empty)
                    {
                        index++;
                        continue;
                    }

                    if (!buildersByMeshGuid.TryGetValue(meshGuid, out Dictionary<uint, MeshVariationBuilder>? variationsByHash))
                    {
                        variationsByHash = [];
                        buildersByMeshGuid.Add(meshGuid, variationsByHash);
                    }

                    uint variationHash = ReadHash(candidate, "VariationAssetNameHash");
                    if (!variationsByHash.TryGetValue(variationHash, out MeshVariationBuilder? builder))
                    {
                        if (!objectVariationEntryCache.TryGetValue(variationHash, out EbxAssetEntry? objectVariationEntry))
                        {
                            objectVariationEntry = ResolveObjectVariationEntry(variationHash);
                            objectVariationEntryCache.Add(variationHash, objectVariationEntry);
                        }

                        if (!objectVariationPointerCache.TryGetValue(variationHash, out PointerRef objectVariationPointer))
                        {
                            objectVariationPointer = CreatePointer(objectVariationEntry);
                            objectVariationPointerCache.Add(variationHash, objectVariationPointer);
                        }

                        builder = new MeshVariationBuilder
                        {
                            Name = objectVariationEntry?.Filename ?? (variationHash == 0 ? "Default" : $"0x{variationHash:X8}"),
                            VariationAssetNameHash = variationHash,
                            VariationEntry = candidate,
                            Variation = objectVariationPointer
                        };
                        builder.Materials.AddRange(BuildVariationMaterials(candidate));
                        variationsByHash.Add(variationHash, builder);
                    }
                    else
                    {
                        builder.VariationEntry ??= candidate;
                        if (builder.Variation.Type == PointerRefType.Null)
                        {
                            if (!objectVariationPointerCache.TryGetValue(variationHash, out PointerRef objectVariationPointer))
                            {
                                if (!objectVariationEntryCache.TryGetValue(variationHash, out EbxAssetEntry? objectVariationEntry))
                                {
                                    objectVariationEntry = ResolveObjectVariationEntry(variationHash);
                                    objectVariationEntryCache.Add(variationHash, objectVariationEntry);
                                }

                                objectVariationPointer = CreatePointer(objectVariationEntry);
                                objectVariationPointerCache.Add(variationHash, objectVariationPointer);
                            }

                            builder.Variation = objectVariationPointer;
                        }

                        if (builder.Materials.Count == 0)
                        {
                            builder.Materials.AddRange(BuildVariationMaterials(candidate));
                        }
                    }

                    builder.Locations.Add(new MeshVariationDatabaseLocation(
                        variationDatabaseEntry,
                        variationDatabasePointer,
                        index));

                    index++;
                }

                if (progressTimer.Elapsed >= TimeSpan.FromSeconds(3))
                {
                    FrostyLogger.Logger?.LogInfo(
                        $"MVDB cache progress: {variationDatabaseIndex + 1}/{variationDatabaseEntries.Count} variation databases processed.");
                    progressTimer.Restart();
                }
            }

            Dictionary<Guid, IReadOnlyList<MeshVariationRecord>> builtVariations = buildersByMeshGuid.ToDictionary(
                static pair => pair.Key,
                static pair => (IReadOnlyList<MeshVariationRecord>)pair.Value.Values
                    .Select(static builder => new MeshVariationRecord(
                        builder.Name,
                        builder.VariationAssetNameHash,
                        builder.VariationEntry,
                        builder.Variation,
                        builder.Materials.ToArray(),
                        builder.Locations
                            .OrderBy(static location => location.VariationDatabaseEntry.Name, StringComparer.OrdinalIgnoreCase)
                            .ThenBy(static location => location.Index)
                            .ToArray()))
                    .OrderBy(static variation => variation.IsDefault ? 0 : 1)
                    .ThenBy(static variation => variation.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray());

            lock (c_cacheLock)
            {
                foreach ((Guid meshGuid, IReadOnlyList<MeshVariationRecord> variations) in builtVariations)
                {
                    if (variations.Count > 0)
                    {
                        c_variationsByMeshGuid[meshGuid] = variations;
                    }
                }

                c_loadedCacheMeshCount = c_variationsByMeshGuid.Count;
            }

            PersistCache();
            FrostyLogger.Logger?.LogInfo($"Finished full MVDB cache build ({builtVariations.Count} meshes).");
        }
    }

    private static void EnsureCacheLoaded()
    {
        bool shouldLoad = false;
        lock (c_cacheLock)
        {
            if (c_cacheFileLoaded)
            {
                return;
            }

            if (c_cacheLoadInProgress)
            {
                while (!c_cacheFileLoaded && c_cacheLoadInProgress)
                {
                    Monitor.Wait(c_cacheLock);
                }

                return;
            }

            c_cacheLoadInProgress = true;
            shouldLoad = true;
        }

        if (!shouldLoad)
        {
            return;
        }

        string? cachePath = GetReadableCachePath();
        try
        {
            if (!string.IsNullOrWhiteSpace(cachePath) && File.Exists(cachePath))
            {
                Dictionary<Guid, IReadOnlyList<MeshVariationRecord>> cachedVariations = ReadCache(cachePath);
                lock (c_cacheLock)
                {
                    foreach ((Guid meshGuid, IReadOnlyList<MeshVariationRecord> variations) in cachedVariations)
                    {
                        if (variations.Count > 0)
                        {
                            c_variationsByMeshGuid[meshGuid] = variations;
                        }
                    }

                    c_loadedCacheMeshCount = c_variationsByMeshGuid.Count;
                }

                FrostyLogger.Logger?.LogInfo(
                    $"Loaded MVDB cache from \"{cachePath}\" ({cachedVariations.Count} meshes).");
            }
        }
        catch (Exception ex)
        {
            // Ignore stale or incompatible cache files and rebuild on demand.
            FrostyLogger.Logger?.LogWarning(
                $"Failed to read MVDB cache \"{cachePath}\": {ex.Message}");
        }
        finally
        {
            lock (c_cacheLock)
            {
                c_cacheFileLoaded = true;
                c_cacheLoadInProgress = false;
                Monitor.PulseAll(c_cacheLock);
            }
        }
    }

    private static bool HasUsableCache()
    {
        string? cachePath = GetReadableCachePath();
        if (string.IsNullOrWhiteSpace(cachePath) || !File.Exists(cachePath))
        {
            return false;
        }

        lock (c_cacheLock)
        {
            return c_cacheFileLoaded && c_loadedCacheMeshCount > 0;
        }
    }

    private static void PersistCache()
    {
        Dictionary<Guid, IReadOnlyList<MeshVariationRecord>> snapshot;
        int loadedCacheMeshCount;
        lock (c_cacheLock)
        {
            snapshot = c_variationsByMeshGuid
                .Where(static pair => pair.Value.Count > 0)
                .ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value);
            loadedCacheMeshCount = c_loadedCacheMeshCount;
        }

        if (loadedCacheMeshCount > 0 && snapshot.Count > 0 && snapshot.Count < loadedCacheMeshCount)
        {
            Frosty.Sdk.FrostyLogger.Logger?.LogWarning(
                $"Skipping MVDB cache write because the snapshot is smaller than the loaded cache ({snapshot.Count} < {loadedCacheMeshCount}).");
            return;
        }

        string cachePath = GetCachePath();
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);

        lock (c_cacheFileLock)
        {
            using FileStream stream = new(cachePath, FileMode.Create, FileAccess.Write, FileShare.None);
            using BinaryWriter writer = new(stream);

            writer.Write(c_mvdbCacheVersion);
            writer.Write(FileSystemManager.CacheName ?? string.Empty);
            writer.Write(FileSystemManager.Head);
            writer.Write(snapshot.Count);

            foreach ((Guid meshGuid, IReadOnlyList<MeshVariationRecord> variations) in snapshot.OrderBy(static pair => pair.Key))
            {
                WriteGuid(writer, meshGuid);
                writer.Write(variations.Count);
                foreach (MeshVariationRecord variation in variations)
                {
                    writer.Write(variation.Name ?? string.Empty);
                    writer.Write(variation.VariationAssetNameHash);

                    writer.Write(variation.MeshVariationDbs.Count);
                    foreach (MeshVariationDatabaseLocation location in variation.MeshVariationDbs)
                    {
                        WriteGuid(writer, location.VariationDatabaseEntry.Guid);
                        writer.Write(location.Index);
                    }

                    writer.Write(variation.Materials.Count);
                    foreach (MeshVariationMaterialRecord material in variation.Materials)
                    {
                        WriteGuid(writer, material.MaterialGuid);
                        WritePointer(writer, material.MaterialVariation);

                        writer.Write(material.TextureParameters.Count);
                        foreach (MeshVariationTextureParameter texture in material.TextureParameters)
                        {
                            writer.Write(texture.ParameterName ?? string.Empty);
                            WriteGuid(writer, texture.TextureEntry?.Guid ?? Guid.Empty);
                            WritePointer(writer, texture.Texture);
                        }
                    }
                }
            }
        }

        lock (c_cacheLock)
        {
            c_loadedCacheMeshCount = Math.Max(c_loadedCacheMeshCount, snapshot.Count);
        }

        Frosty.Sdk.FrostyLogger.Logger?.LogInfo(
            $"Wrote MVDB cache to \"{cachePath}\" ({snapshot.Count} meshes).");
    }

    private static Dictionary<Guid, IReadOnlyList<MeshVariationRecord>> ReadCache(string cachePath)
    {
        Dictionary<Guid, IReadOnlyList<MeshVariationRecord>> variationsByMeshGuid = [];
        Dictionary<Guid, EbxAssetEntry?> entryCache = [];
        Dictionary<Guid, PointerRef> entryPointerCache = [];
        Dictionary<uint, EbxAssetEntry?> objectVariationEntryCache = [];
        Dictionary<uint, PointerRef> objectVariationPointerCache = [];

        EbxAssetEntry? ResolveEntry(Guid guid)
        {
            if (guid == Guid.Empty)
            {
                return null;
            }

            if (!entryCache.TryGetValue(guid, out EbxAssetEntry? entry))
            {
                entry = AssetManager.GetEbxAssetEntry(guid);
                entryCache.Add(guid, entry);
            }

            return entry;
        }

        PointerRef ResolveEntryPointer(Guid guid)
        {
            if (guid == Guid.Empty)
            {
                return new PointerRef();
            }

            if (!entryPointerCache.TryGetValue(guid, out PointerRef pointer))
            {
                pointer = CreatePointer(ResolveEntry(guid));
                entryPointerCache.Add(guid, pointer);
            }

            return pointer;
        }

        PointerRef ResolveObjectVariationPointer(uint variationHash)
        {
            if (variationHash == 0)
            {
                return new PointerRef();
            }

            if (!objectVariationPointerCache.TryGetValue(variationHash, out PointerRef pointer))
            {
                if (!objectVariationEntryCache.TryGetValue(variationHash, out EbxAssetEntry? entry))
                {
                    entry = ResolveObjectVariationEntry(variationHash);
                    objectVariationEntryCache.Add(variationHash, entry);
                }

                pointer = CreatePointer(entry);
                objectVariationPointerCache.Add(variationHash, pointer);
            }

            return pointer;
        }

        lock (c_cacheFileLock)
        {
            using FileStream stream = new(
                cachePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 131072,
                options: FileOptions.SequentialScan);
            using BinaryReader reader = new(stream);

            int version = reader.ReadInt32();
            if (version != c_mvdbCacheVersion)
            {
                throw new InvalidDataException("Unsupported MVDB cache version.");
            }

            string cacheName = reader.ReadString();
            if (!string.Equals(cacheName, FileSystemManager.CacheName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("MVDB cache profile mismatch.");
            }

            uint head = reader.ReadUInt32();
            if (head != FileSystemManager.Head)
            {
                throw new InvalidDataException("MVDB cache head mismatch.");
            }

            int meshCount = reader.ReadInt32();
            variationsByMeshGuid = new Dictionary<Guid, IReadOnlyList<MeshVariationRecord>>(meshCount);
            for (int meshIndex = 0; meshIndex < meshCount; meshIndex++)
            {
                Guid meshGuid = ReadGuid(reader);
                int variationCount = reader.ReadInt32();
                List<MeshVariationRecord> variations = new(variationCount);

                for (int variationIndex = 0; variationIndex < variationCount; variationIndex++)
                {
                    string name = reader.ReadString();
                    uint variationHash = reader.ReadUInt32();

                    int locationCount = reader.ReadInt32();
                    List<MeshVariationDatabaseLocation> locations = new(locationCount);
                    for (int locationIndex = 0; locationIndex < locationCount; locationIndex++)
                    {
                        Guid variationDbGuid = ReadGuid(reader);
                        int dbIndex = reader.ReadInt32();
                        EbxAssetEntry? variationDbEntry = ResolveEntry(variationDbGuid);
                        if (variationDbEntry is not null)
                        {
                            locations.Add(new MeshVariationDatabaseLocation(
                                variationDbEntry,
                                ResolveEntryPointer(variationDbGuid),
                                dbIndex));
                        }
                    }

                    int materialCount = reader.ReadInt32();
                    List<MeshVariationMaterialRecord> materials = new(materialCount);
                    for (int materialIndex = 0; materialIndex < materialCount; materialIndex++)
                    {
                        Guid materialGuid = ReadGuid(reader);
                        PointerRef materialVariation = ReadPointer(reader);

                        int textureCount = reader.ReadInt32();
                        List<MeshVariationTextureParameter> textures = new(textureCount);
                        for (int textureIndex = 0; textureIndex < textureCount; textureIndex++)
                        {
                            string parameterName = reader.ReadString();
                            Guid textureEntryGuid = ReadGuid(reader);
                            PointerRef texturePointer = ReadPointer(reader);
                            EbxAssetEntry? textureEntry = ResolveEntry(textureEntryGuid);
                            textures.Add(new MeshVariationTextureParameter(parameterName, texturePointer, textureEntry));
                        }

                        materials.Add(new MeshVariationMaterialRecord(
                            materialGuid,
                            materialVariation,
                            textures));
                    }

                    variations.Add(new MeshVariationRecord(
                        name,
                        variationHash,
                        null,
                        ResolveObjectVariationPointer(variationHash),
                        materials,
                        locations));
                }

                variationsByMeshGuid[meshGuid] = variations
                    .OrderBy(static variation => variation.IsDefault ? 0 : 1)
                    .ThenBy(static variation => variation.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
        }

        return variationsByMeshGuid;
    }

    private static string GetCachePath()
    {
        return Path.Combine(
            AppContext.BaseDirectory,
            "Caches",
            $"{ProfilesLibrary.InternalName}_mvdb.cache");
    }

    private static string? GetReadableCachePath()
    {
        foreach (string path in EnumerateCachePaths())
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateCachePaths()
    {
        string cacheFileName = $"{ProfilesLibrary.InternalName}_mvdb.cache";
        string primaryPath = Path.Combine(AppContext.BaseDirectory, "Caches", cacheFileName);

        yield return primaryPath;
        yield return Path.Combine(AppContext.BaseDirectory, cacheFileName);
        if (!string.IsNullOrWhiteSpace(FileSystemManager.CacheName))
        {
            yield return $"{FileSystemManager.CacheName}_mvdb.cache";
        }

        string currentDirectory = Environment.CurrentDirectory;
        if (!string.IsNullOrWhiteSpace(currentDirectory))
        {
            yield return Path.Combine(currentDirectory, "Caches", cacheFileName);
            yield return Path.Combine(currentDirectory, cacheFileName);
        }
    }

    private static void WriteGuid(BinaryWriter writer, Guid guid)
    {
        writer.Write(guid.ToByteArray());
    }

    private static Guid ReadGuid(BinaryReader reader)
    {
        return new Guid(reader.ReadBytes(16));
    }

    private static void WritePointer(BinaryWriter writer, PointerRef pointer)
    {
        bool hasExternal = pointer.Type == PointerRefType.External;
        writer.Write(hasExternal);
        if (!hasExternal)
        {
            return;
        }

        WriteGuid(writer, pointer.External.PartitionGuid);
        WriteGuid(writer, pointer.External.InstanceGuid);
    }

    private static PointerRef ReadPointer(BinaryReader reader)
    {
        if (!reader.ReadBoolean())
        {
            return new PointerRef();
        }

        return new PointerRef(new EbxImportReference
        {
            PartitionGuid = ReadGuid(reader),
            InstanceGuid = ReadGuid(reader)
        });
    }

    private static IReadOnlyList<MeshVariationRecord> BuildVariations(EbxAssetEntry meshEntry)
    {
        Dictionary<uint, MeshVariationBuilder> variationsByHash = [];

        foreach (EbxAssetEntry variationDatabaseEntry in EnsureVariationDatabaseEntries())
        {
            EbxPartition partition = AssetManager.GetEbxPartition(variationDatabaseEntry);
            object rootObject = partition.PrimaryInstance;
            if (!MeshAssetOperations.TryGetMemberValue(rootObject, "Entries", out object? entriesValue) ||
                entriesValue is null)
            {
                continue;
            }

            int index = 0;
            foreach (object? candidate in EnumerateValues(entriesValue))
            {
                if (candidate is null)
                {
                    index++;
                    continue;
                }

                if (TryReadMeshGuid(candidate, out Guid candidateMeshGuid) && candidateMeshGuid != Guid.Empty)
                {
                    if (candidateMeshGuid != meshEntry.Guid)
                    {
                        index++;
                        continue;
                    }
                }
                else if (!MatchesMesh(candidate, meshEntry))
                {
                    index++;
                    continue;
                }

                uint variationHash = ReadHash(candidate, "VariationAssetNameHash");
                if (!variationsByHash.TryGetValue(variationHash, out MeshVariationBuilder? builder))
                {
                    EbxAssetEntry? objectVariationEntry = ResolveObjectVariationEntry(variationHash);
                    builder = new MeshVariationBuilder
                    {
                        Name = objectVariationEntry?.Filename ?? (variationHash == 0 ? "Default" : $"0x{variationHash:X8}"),
                        VariationAssetNameHash = variationHash,
                        VariationEntry = candidate,
                        Variation = CreatePointer(objectVariationEntry)
                    };
                    builder.Materials.AddRange(BuildVariationMaterials(candidate));

                    variationsByHash.Add(variationHash, builder);
                }
                else
                {
                    builder.VariationEntry ??= candidate;
                    if (builder.Variation.Type == PointerRefType.Null)
                    {
                        builder.Variation = CreatePointer(ResolveObjectVariationEntry(variationHash));
                    }

                    if (builder.Materials.Count == 0)
                    {
                        builder.Materials.AddRange(BuildVariationMaterials(candidate));
                    }
                }

                builder.Locations.Add(new MeshVariationDatabaseLocation(
                    variationDatabaseEntry,
                    CreatePointer(variationDatabaseEntry),
                    index));

                index++;
            }
        }

        return variationsByHash.Values
            .Select(static builder => new MeshVariationRecord(
                builder.Name,
                builder.VariationAssetNameHash,
                builder.VariationEntry,
                builder.Variation,
                builder.Materials.ToArray(),
                builder.Locations
                    .OrderBy(static location => location.VariationDatabaseEntry.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static location => location.Index)
                    .ToArray()))
            .OrderBy(static variation => variation.IsDefault ? 0 : 1)
            .ThenBy(static variation => variation.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static MeshVariationRecord CreateSyntheticDefaultVariation(MeshAssetLoadResult load)
    {
        return new MeshVariationRecord(
            "Default",
            0,
            null,
            new PointerRef(),
            BuildBaseMaterials(load.RootObject),
            []);
    }

    private static IReadOnlyList<MeshVariationMaterialRecord> BuildBaseMaterials(object rootObject)
    {
        if (!TryGetMaterialSlots(rootObject, out object? materialsValue) || materialsValue is null)
        {
            return [];
        }

        List<MeshVariationMaterialRecord> materials = [];
        foreach (object? materialSlot in EnumerateValues(materialsValue))
        {
            if (materialSlot is null)
            {
                continue;
            }

            object? materialObject = ResolvePointerObject(materialSlot) ?? materialSlot;
            Guid materialGuid = ReadMaterialGuid(materialSlot);
            if (materialGuid == Guid.Empty)
            {
                materialGuid = ReadMaterialGuid(materialObject);
            }

            if (materialGuid == Guid.Empty)
            {
                continue;
            }

            materials.Add(new MeshVariationMaterialRecord(
                materialGuid,
                materialSlot is PointerRef pointer ? pointer : new PointerRef(),
                ExtractTextureParameters(materialObject)));
        }

        return materials;
    }

    private static IReadOnlyList<MeshVariationMaterialRecord> BuildVariationMaterials(object variationEntry)
    {
        if (!TryGetMaterialSlots(variationEntry, out object? materialsValue) || materialsValue is null)
        {
            return [];
        }

        List<MeshVariationMaterialRecord> materials = [];
        foreach (object? materialEntry in EnumerateValues(materialsValue))
        {
            if (materialEntry is null)
            {
                continue;
            }

            MeshAssetOperations.TryGetMemberValue(materialEntry, "Material", out object? materialValue);
            MeshAssetOperations.TryGetMemberValue(materialEntry, "MaterialVariation", out object? materialVariationValue);

            Guid materialGuid = ReadMaterialGuid(materialValue);
            if (materialGuid == Guid.Empty)
            {
                materialGuid = ReadMaterialGuid(materialEntry);
            }

            object? materialVariationObject = ResolvePointerObject(materialVariationValue) ?? materialVariationValue;
            materials.Add(new MeshVariationMaterialRecord(
                materialGuid,
                materialVariationValue is PointerRef pointer ? pointer : new PointerRef(),
                ExtractTextureParameters(materialVariationObject)));
        }

        return materials;
    }

    private static IReadOnlyList<MeshVariationTextureParameter> ExtractTextureParameters(object? source)
    {
        if (source is null)
        {
            return [];
        }

        if (!TryGetTextureParameters(source, out object? parametersValue) || parametersValue is null)
        {
            return [];
        }

        List<MeshVariationTextureParameter> textures = [];
        foreach (object? parameter in EnumerateValues(parametersValue))
        {
            if (parameter is null)
            {
                continue;
            }

            string parameterName = ReadParameterName(parameter);
            if (!MeshAssetOperations.TryGetMemberValue(parameter, "Value", out object? textureValue) ||
                textureValue is null)
            {
                continue;
            }

            PointerRef pointer = textureValue is PointerRef texturePointer ? texturePointer : new PointerRef();
            MeshAssetOperations.TryResolveLinkedEbxAssetEntry(textureValue, out EbxAssetEntry? textureEntry);
            textures.Add(new MeshVariationTextureParameter(parameterName, pointer, textureEntry));
        }

        return textures;
    }

    private static IReadOnlyList<EbxAssetEntry> EnsureVariationDatabaseEntries()
    {
        lock (c_cacheLock)
        {
            if (c_variationDatabaseEntries is not null)
            {
                return c_variationDatabaseEntries;
            }

            c_variationDatabaseEntries = AssetManager.EnumerateEbxAssetEntries()
                .Where(static entry => entry.Type.Equals("MeshVariationDatabase", StringComparison.OrdinalIgnoreCase))
                .OrderBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return c_variationDatabaseEntries;
        }
    }

    private static Dictionary<uint, EbxAssetEntry> EnsureObjectVariationMapping()
    {
        lock (c_cacheLock)
        {
            if (c_objectVariationEntriesByHash is not null)
            {
                return c_objectVariationEntriesByHash;
            }

            Dictionary<uint, EbxAssetEntry> mapping = [];
            foreach (EbxAssetEntry entry in AssetManager.EnumerateEbxAssetEntries()
                         .Where(static asset => asset.Type.Equals("ObjectVariation", StringComparison.OrdinalIgnoreCase)))
            {
                AddHashMapping(mapping, entry.Name, entry);
                AddHashMapping(mapping, entry.Filename, entry);
            }

            c_objectVariationEntriesByHash = mapping;
            return c_objectVariationEntriesByHash;
        }
    }

    private static void AddHashMapping(Dictionary<uint, EbxAssetEntry> mapping, string name, EbxAssetEntry entry)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        uint hash = unchecked((uint)Frosty.Sdk.Utils.Utils.HashString(name, true));
        mapping.TryAdd(hash, entry);
    }

    private static EbxAssetEntry? ResolveObjectVariationEntry(uint variationHash)
    {
        if (variationHash == 0)
        {
            return null;
        }

        return EnsureObjectVariationMapping().GetValueOrDefault(variationHash);
    }

    private static bool MatchesMesh(object candidate, EbxAssetEntry meshEntry)
    {
        return MeshAssetOperations.TryResolveLinkedEbxAssetEntry(
                   candidate,
                   out EbxAssetEntry? linkedMesh,
                   "Mesh",
                   "MeshAsset",
                   "MeshSet",
                   "MeshResource",
                   "RenderableMeshAsset",
                   "SourceMesh") &&
               linkedMesh?.Guid == meshEntry.Guid;
    }

    private static bool TryReadMeshGuid(object candidate, out Guid meshGuid)
    {
        meshGuid = Guid.Empty;
        if (!TryGetMemberValue(candidate, out object? meshValue, "Mesh", "MeshAsset", "MeshSet", "RenderableMeshAsset", "SourceMesh") ||
            meshValue is null)
        {
            return false;
        }

        if (MeshAssetOperations.TryResolveLinkedEbxAssetEntry(meshValue, out EbxAssetEntry? linkedMesh) &&
            linkedMesh is not null)
        {
            meshGuid = linkedMesh.Guid;
            return true;
        }

        if (meshValue is PointerRef pointer && pointer.Type == PointerRefType.External)
        {
            meshGuid = pointer.External.PartitionGuid != Guid.Empty
                ? pointer.External.PartitionGuid
                : pointer.External.InstanceGuid;
            return meshGuid != Guid.Empty;
        }

        return false;
    }

    private static IEnumerable<object?> EnumerateValues(object? value)
    {
        if (value is null)
        {
            yield break;
        }

        if (value is string)
        {
            yield return value;
            yield break;
        }

        if (value is IEnumerable enumerable)
        {
            foreach (object? item in enumerable)
            {
                yield return item;
            }

            yield break;
        }

        yield return value;
    }

    private static bool TryGetMaterialSlots(object source, out object? materialsValue)
    {
        foreach (string memberName in new[] { "Materials", "MaterialCollection", "MaterialCollections", "MeshMaterials" })
        {
            if (MeshAssetOperations.TryGetMemberValue(source, memberName, out materialsValue) &&
                materialsValue is not null)
            {
                return true;
            }
        }

        materialsValue = null;
        return false;
    }

    private static bool TryGetTextureParameters(object source, out object? parametersValue)
    {
        if (TryGetMemberValue(source, out parametersValue, "TextureParameters", "Parameters"))
        {
            return true;
        }

        if (MeshAssetOperations.TryGetMemberValue(source, "Shader", out object? shaderValue) &&
            shaderValue is not null)
        {
            object? shaderObject = ResolvePointerObject(shaderValue) ?? shaderValue;
            if (shaderObject is not null && TryGetMemberValue(shaderObject, out parametersValue, "TextureParameters", "Parameters"))
            {
                return true;
            }

            if (shaderObject is not null &&
                MeshAssetOperations.TryGetMemberValue(shaderObject, "ShaderPreset", out object? presetValue) &&
                presetValue is not null)
            {
                object? presetObject = ResolvePointerObject(presetValue) ?? presetValue;
                if (presetObject is not null && TryGetMemberValue(presetObject, out parametersValue, "TextureParameters", "Parameters"))
                {
                    return true;
                }
            }
        }

        parametersValue = null;
        return false;
    }

    private static bool TryGetMemberValue(object source, out object? value, params string[] memberNames)
    {
        foreach (string memberName in memberNames)
        {
            if (MeshAssetOperations.TryGetMemberValue(source, memberName, out value))
            {
                return true;
            }
        }

        value = null;
        return false;
    }

    private static string ReadParameterName(object parameter)
    {
        if (MeshAssetOperations.TryGetMemberValue(parameter, "ParameterName", out object? value) &&
            value is not null)
        {
            if (TryReadStringValue(value, out string text))
            {
                return text;
            }
        }

        return string.Empty;
    }

    private static bool TryReadStringValue(object? value, out string text, int depth = 0)
    {
        text = string.Empty;
        if (value is null || depth > 6)
        {
            return false;
        }

        if (value is string directText)
        {
            text = directText;
            return !string.IsNullOrWhiteSpace(text);
        }

        if (value is char[] chars)
        {
            text = new string(chars);
            return !string.IsNullOrWhiteSpace(text);
        }

        if (value is IEnumerable enumerable && value is not string)
        {
            List<char> charBuffer = [];
            foreach (object? item in enumerable)
            {
                if (item is char character)
                {
                    charBuffer.Add(character);
                }
                else
                {
                    charBuffer.Clear();
                    break;
                }
            }

            if (charBuffer.Count > 0)
            {
                text = new string(charBuffer.ToArray());
                return !string.IsNullOrWhiteSpace(text);
            }
        }

        foreach (string memberName in new[] { "Value", "Text", "String", "Name", "CString" })
        {
            if (!MeshAssetOperations.TryGetMemberValue(value, memberName, out object? memberValue) ||
                memberValue is null ||
                ReferenceEquals(memberValue, value))
            {
                continue;
            }

            if (TryReadStringValue(memberValue, out text, depth + 1))
            {
                return true;
            }
        }

        Type valueType = value.GetType();
        string fullTypeName = valueType.FullName ?? valueType.Name;
        if (fullTypeName.EndsWith("CString", StringComparison.Ordinal) ||
            fullTypeName.StartsWith("Frostbite.Reflection", StringComparison.Ordinal))
        {
            foreach (MethodInfo method in valueType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                if (!string.Equals(method.Name, "op_Implicit", StringComparison.Ordinal) ||
                    method.ReturnType != typeof(string))
                {
                    continue;
                }

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != 1 || parameters[0].ParameterType != valueType)
                {
                    continue;
                }

                try
                {
                    if (method.Invoke(null, [value]) is string convertedText &&
                        !string.IsNullOrWhiteSpace(convertedText))
                    {
                        text = convertedText;
                        return true;
                    }
                }
                catch
                {
                    // Ignore conversion failures and keep looking.
                }
            }
        }

        string fallbackText = value.ToString() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(fallbackText) &&
            !string.Equals(fallbackText, fullTypeName, StringComparison.Ordinal))
        {
            text = fallbackText;
            return true;
        }

        return false;
    }

    private static uint ReadHash(object candidate, string memberName)
    {
        if (!MeshAssetOperations.TryGetMemberValue(candidate, memberName, out object? value) ||
            value is null)
        {
            return 0;
        }

        try
        {
            return Convert.ToUInt32(value);
        }
        catch
        {
            return 0;
        }
    }

    private static Guid ReadMaterialGuid(object? candidate)
    {
        if (candidate is null)
        {
            return Guid.Empty;
        }

        if (candidate is PointerRef pointerRef)
        {
            if (pointerRef.Type == PointerRefType.Internal && pointerRef.Internal is IEbxInstance internalInstance)
            {
                AssetClassGuid internalGuid = internalInstance.GetInstanceGuid();
                if (internalGuid.IsExported)
                {
                    return internalGuid.ExportedGuid;
                }
            }

            if (pointerRef.Type == PointerRefType.External)
            {
                if (MeshAssetOperations.TryGetMemberValue(pointerRef.External, "ClassGuid", out object? externalClassGuidValue) &&
                    TryReadGuidValue(externalClassGuidValue, out Guid externalClassGuid) &&
                    externalClassGuid != Guid.Empty)
                {
                    return externalClassGuid;
                }

                Guid externalInstanceGuid = pointerRef.External.InstanceGuid;
                if (externalInstanceGuid != Guid.Empty)
                {
                    return externalInstanceGuid;
                }
            }
        }

        if (candidate is IEbxInstance instance)
        {
            AssetClassGuid guid = instance.GetInstanceGuid();
            if (guid.IsExported)
            {
                return guid.ExportedGuid;
            }
        }

        if (MeshAssetOperations.TryGetMemberValue(candidate, "InstanceGuid", out object? instanceGuidValue) &&
            TryReadGuidValue(instanceGuidValue, out Guid instanceGuid))
        {
            return instanceGuid;
        }

        if (MeshAssetOperations.TryGetMemberValue(candidate, "ClassGuid", out object? classGuidValue) &&
            TryReadGuidValue(classGuidValue, out Guid classGuid))
        {
            return classGuid;
        }

        return Guid.Empty;
    }

    private static bool TryReadGuidValue(object? value, out Guid guid)
    {
        guid = Guid.Empty;
        switch (value)
        {
            case Guid directGuid:
                guid = directGuid;
                return guid != Guid.Empty;

            case AssetClassGuid assetClassGuid when assetClassGuid.IsExported:
                guid = assetClassGuid.ExportedGuid;
                return guid != Guid.Empty;

            default:
                return Guid.TryParse(value?.ToString(), out guid) && guid != Guid.Empty;
        }
    }

    private static object? ResolvePointerObject(object? candidate)
    {
        if (candidate is not PointerRef pointerRef)
        {
            return candidate;
        }

        return pointerRef.Type switch
        {
            PointerRefType.Internal => pointerRef.Internal,
            PointerRefType.External => ResolveExternalPointer(pointerRef.External),
            _ => null
        };
    }

    private static object? ResolveExternalPointer(EbxImportReference importReference)
    {
        if (importReference.PartitionGuid == Guid.Empty || importReference.InstanceGuid == Guid.Empty)
        {
            return null;
        }

        EbxAssetEntry? entry = AssetManager.GetEbxAssetEntry(importReference.PartitionGuid);
        if (entry is null)
        {
            return null;
        }

        return AssetManager.GetEbxPartition(entry).GetObject(importReference.InstanceGuid);
    }

    private static PointerRef CreatePointer(EbxAssetEntry? entry)
    {
        if (entry is null)
        {
            return new PointerRef();
        }

        EbxPartition partition = AssetManager.GetEbxPartition(entry);
        return new PointerRef(new EbxImportReference
        {
            PartitionGuid = partition.PartitionGuid,
            InstanceGuid = partition.PrimaryInstanceGuid
        });
    }
}
