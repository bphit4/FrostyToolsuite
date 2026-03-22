using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Frosty.Sdk;
using Frosty.Sdk.Interfaces;
using Frosty.Sdk.Ebx;
using Frosty.Sdk.IO.Ebx;
using Frosty.Sdk.Managers;
using Frosty.Sdk.Managers.Entries;
using Frosty.Sdk.Resources;
using FrostyEditor.Utils;

namespace FrostyEditor.Managers;

public sealed record MeshAssetLoadResult(EbxAssetEntry Entry, MeshSet Mesh, ResAssetEntry ResourceEntry, object RootObject);
public sealed record MeshOperationResult(bool Success, string Message);

public static class MeshAssetOperations
{
    private const string c_packageExtension = ".zip";
    private const string c_manifestEntryName = "manifest.json";
    private const string c_resourceEntryName = "resource.bin";
    private const string c_resourceMetaEntryName = "resource.meta";

    public static bool IsMeshAsset(EbxAssetEntry entry)
    {
        return TypeLibrary.IsSubClassOf(entry.Type, "MeshAsset") ||
               entry.Type.Equals("SkinnedMeshAsset", StringComparison.OrdinalIgnoreCase) ||
               entry.Type.Equals("RigidMeshAsset", StringComparison.OrdinalIgnoreCase) ||
               entry.Type.Equals("CompositeMeshAsset", StringComparison.OrdinalIgnoreCase);
    }

    public static MeshAssetLoadResult Load(EbxAssetEntry entry)
    {
        EbxPartition partition = AssetManager.GetEbxPartition(entry);
        object rootObject = partition.PrimaryInstance;

        if (!TryGetResourceValue(rootObject, out object? resourceValue))
        {
            throw new InvalidOperationException(
                $"Mesh asset \"{entry.Name}\" does not expose a supported mesh resource property.");
        }

        if (!TryExtractResourceId(resourceValue, out ulong rid))
        {
            string resourceType = resourceValue?.GetType().FullName ?? "<null>";
            throw new InvalidOperationException(
                $"Mesh asset \"{entry.Name}\" returned an unsupported resource value of type \"{resourceType}\".");
        }

        ResAssetEntry? resourceEntry = AssetManager.GetResAssetEntry(rid);
        if (resourceEntry is null)
        {
            throw new InvalidOperationException($"Mesh resource 0x{rid:X} could not be resolved for \"{entry.Name}\".");
        }

        MeshSet mesh = AssetManager.GetResAs<MeshSet>(resourceEntry);
        return new MeshAssetLoadResult(entry, mesh, resourceEntry, rootObject);
    }

    public static bool IsModified(EbxAssetEntry entry)
    {
        if (!TryLoad(entry, out MeshAssetLoadResult? load))
        {
            return false;
        }

        MeshAssetLoadResult resolvedLoad = load!;
        if (AssetManager.IsResModified(resolvedLoad.ResourceEntry.ResRid))
        {
            return true;
        }

        foreach (Guid chunkId in resolvedLoad.Mesh.EnumerateExternalChunkIds().Distinct())
        {
            if (AssetManager.IsChunkModified(chunkId))
            {
                return true;
            }
        }

        return false;
    }

    public static MeshOperationResult Revert(EbxAssetEntry entry)
    {
        if (!TryLoad(entry, out MeshAssetLoadResult? load))
        {
            return new MeshOperationResult(false, $"Unable to resolve mesh state for {entry.Filename}.");
        }

        MeshAssetLoadResult resolvedLoad = load!;
        bool revertedRes = AssetManager.RevertRes(resolvedLoad.ResourceEntry.ResRid);
        bool revertedChunks = false;
        foreach (Guid chunkId in resolvedLoad.Mesh.EnumerateExternalChunkIds().Distinct())
        {
            revertedChunks |= AssetManager.RevertChunk(chunkId);
        }

        if (!revertedRes && !revertedChunks)
        {
            return new MeshOperationResult(false, $"{entry.Filename} has no pending mesh edits to revert.");
        }

        AssetEditStateTracker.ClearDirty(entry.Name);
        AssetEditStateTracker.ClearModified(entry.Name);
        return new MeshOperationResult(true, $"Reverted {entry.Filename} to the original game mesh.");
    }

    public static async Task<MeshOperationResult> ExportWithPickerAsync(EbxAssetEntry entry)
    {
        FilePickerSaveOptions options = new()
        {
            Title = "Export mesh",
            SuggestedFileName = entry.Filename,
            DefaultExtension = "obj",
            FileTypeChoices =
            [
                new FilePickerFileType("Wavefront OBJ (*.obj)") { Patterns = ["*.obj"] },
                new FilePickerFileType("Autodesk FBX (*.fbx)") { Patterns = ["*.fbx"] }
            ]
        };

        IStorageFile? file = await FileService.SaveFilePickerAsync(options);
        if (file is null)
        {
            return new MeshOperationResult(false, "Mesh export canceled.");
        }

        MeshAssetLoadResult load = Load(entry);
        string extension = Path.GetExtension(file.Path.LocalPath);
        return ExportMesh(load, file.Path.LocalPath, extension);
    }

    public static async Task<MeshOperationResult> ImportWithPickerAsync(EbxAssetEntry entry)
    {
        FilePickerOpenOptions options = new()
        {
            Title = "Import mesh",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Mesh files")
                {
                    Patterns = ["*.fbx", "*.obj"]
                }
            ]
        };

        IReadOnlyList<IStorageFile>? files = await FileService.OpenFilesAsync(options);
        if (files is null || files.Count == 0)
        {
            return new MeshOperationResult(false, "Mesh import canceled.");
        }

        MeshAssetLoadResult load = Load(entry);
        MeshOperationResult result = ImportMesh(load, files[0].Path.LocalPath);
        if (result.Success)
        {
            AssetEditStateTracker.MarkDirty(entry.Name);
            AssetEditStateTracker.MarkModified(entry.Name);
        }

        return result;
    }

    public static MeshOperationResult ExportMesh(MeshAssetLoadResult load, string path, string extension)
    {
        string normalizedExtension = extension.ToLowerInvariant();
        if (normalizedExtension == ".obj")
        {
            return MeshObjCodec.Export(load, path);
        }

        if (normalizedExtension == ".fbx")
        {
            return MeshFbxCodec.Export(load, path);
        }

        return new MeshOperationResult(false, $"Unsupported mesh export format \"{extension}\".");
    }

    public static MeshOperationResult ImportMesh(MeshAssetLoadResult load, string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".obj")
        {
            return MeshObjCodec.Import(load, path);
        }

        if (extension == ".fbx")
        {
            return MeshSceneImportCodec.Import(load, path);
        }

        return new MeshOperationResult(false, $"Unsupported mesh import format \"{extension}\".");
    }

    public static MeshOperationResult ExportPackage(MeshAssetLoadResult load, string path)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (!path.EndsWith(c_packageExtension, StringComparison.OrdinalIgnoreCase))
            {
                path += c_packageExtension;
            }

            MeshPackageManifest manifest = CreateManifest(load);
            using FileStream stream = new(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            using ZipArchive archive = new(stream, ZipArchiveMode.Create);

            WriteBytes(archive, c_manifestEntryName, JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions));
            WriteBytes(archive, c_resourceEntryName, load.Mesh.RawData);
            WriteBytes(archive, c_resourceMetaEntryName, AssetManager.GetResMeta(load.ResourceEntry));

            foreach (MeshPackageChunk chunk in manifest.Chunks)
            {
                ChunkAssetEntry? chunkEntry = AssetManager.GetChunkAssetEntry(chunk.ChunkId);
                if (chunkEntry is null)
                {
                    throw new InvalidOperationException(
                        $"Chunk {chunk.ChunkId} referenced by {load.Entry.Filename} could not be resolved.");
                }

                using Frosty.Sdk.Utils.Block<byte> data = AssetManager.GetAsset(chunkEntry);
                WriteBytes(archive, chunk.File, data.ToArray());
            }

            return new MeshOperationResult(true, $"Exported mesh package for {load.Entry.Filename} to {path}.");
        }
        catch (Exception ex)
        {
            return new MeshOperationResult(false, $"Failed to export mesh package: {ex.Message}");
        }
    }

    public static MeshOperationResult ImportPackage(EbxAssetEntry entry, string path)
    {
        try
        {
            MeshAssetLoadResult currentLoad = Load(entry);
            if (!File.Exists(path))
            {
                return new MeshOperationResult(false, $"Mesh package \"{path}\" was not found.");
            }

            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using ZipArchive archive = new(stream, ZipArchiveMode.Read);

            MeshPackageManifest manifest = ReadManifest(archive);
            byte[] resourceBytes = ReadEntryBytes(archive, c_resourceEntryName);
            byte[] resourceMeta = ReadEntryBytes(archive, c_resourceMetaEntryName);
            MeshSet importedMesh = ReadMesh(resourceBytes, resourceMeta);

            ValidateCompatibility(currentLoad, importedMesh, manifest);
            Dictionary<Guid, Guid> chunkMapping = RemapImportedChunks(currentLoad.Mesh, importedMesh);

            AssetManager.ModifyRes(currentLoad.ResourceEntry.ResRid, importedMesh);

            foreach ((Guid sourceChunkId, Guid targetChunkId) in chunkMapping)
            {
                MeshPackageChunk manifestChunk = manifest.Chunks.FirstOrDefault(chunk => chunk.ChunkId == sourceChunkId)
                    ?? throw new InvalidOperationException($"The package is missing chunk data for {sourceChunkId}.");
                byte[] chunkBytes = ReadEntryBytes(archive, manifestChunk.File);
                AssetManager.ModifyChunk(targetChunkId, chunkBytes);
            }

            AssetEditStateTracker.MarkDirty(entry.Name);
            AssetEditStateTracker.MarkModified(entry.Name);
            return new MeshOperationResult(true, $"Imported mesh package data for {entry.Filename}.");
        }
        catch (Exception ex)
        {
            return new MeshOperationResult(false, $"Failed to import mesh package: {ex.Message}");
        }
    }

    private static bool TryGetResourceValue(object rootObject, out object? value)
    {
        foreach (string memberName in new[] { "MeshSetResource", "Resource", "MeshResource" })
        {
            if (TryGetMemberValue(rootObject, memberName, out value))
            {
                return true;
            }
        }

        value = null;
        return false;
    }

    internal static bool TryResolveLinkedEbxAssetEntry(object instance, out EbxAssetEntry? entry, params string[] memberNames)
    {
        foreach (string memberName in memberNames)
        {
            if (TryGetMemberValue(instance, memberName, out object? value) &&
                TryExtractEbxAssetEntry(value, out entry))
            {
                return true;
            }
        }

        entry = null;
        return false;
    }

    internal static bool TryResolveLinkedEbxAssetEntry(object? value, out EbxAssetEntry? entry)
    {
        return TryExtractEbxAssetEntry(value, out entry);
    }

    internal static bool TryGetMemberValue(object instance, string memberName, out object? value)
    {
        value = null;
        string normalizedTarget = NormalizeMemberName(memberName);

        for (Type? type = instance.GetType(); type is not null; type = type.BaseType)
        {
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (!property.CanRead || property.GetIndexParameters().Length != 0)
                {
                    continue;
                }

                if (!string.Equals(NormalizeMemberName(property.Name), normalizedTarget, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                value = property.GetValue(instance);
                return value is not null;
            }

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (!string.Equals(NormalizeMemberName(field.Name), normalizedTarget, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                value = field.GetValue(instance);
                return value is not null;
            }
        }

        return false;
    }

    private static bool TryExtractEbxAssetEntry(object? value, out EbxAssetEntry? entry, int depth = 0)
    {
        entry = null;
        if (value is null || depth > 4)
        {
            return false;
        }

        switch (value)
        {
            case EbxAssetEntry ebxAssetEntry:
                entry = ebxAssetEntry;
                return true;

            case string assetName when !string.IsNullOrWhiteSpace(assetName):
                entry = AssetManager.GetEbxAssetEntry(assetName);
                return entry is not null;

            case Guid guid when guid != Guid.Empty:
                entry = AssetManager.GetEbxAssetEntry(guid);
                return entry is not null;

            case AssetClassGuid assetClassGuid when assetClassGuid.IsExported:
                entry = AssetManager.GetEbxAssetEntry(assetClassGuid.ExportedGuid);
                return entry is not null;

            case PointerRef pointerRef:
                if (pointerRef.Type == PointerRefType.Internal && pointerRef.Internal is IEbxInstance internalInstance)
                {
                    AssetClassGuid internalGuid = internalInstance.GetInstanceGuid();
                    if (internalGuid.IsExported)
                    {
                        entry = AssetManager.GetEbxAssetEntry(internalGuid.ExportedGuid);
                        if (entry is not null)
                        {
                            return true;
                        }
                    }
                }

                if (pointerRef.Type == PointerRefType.External)
                {
                    Guid fileGuid = TryGetMemberValue(pointerRef.External, "FileGuid", out object? fileGuidValue)
                        ? ReadGuid(fileGuidValue)
                        : Guid.Empty;
                    entry = ResolveImportedEbxEntry(fileGuid, pointerRef.External.PartitionGuid, pointerRef.External.InstanceGuid);
                    if (entry is not null)
                    {
                        return true;
                    }
                }

                break;

            case EbxImportReference importReference:
                Guid importFileGuid = TryGetMemberValue(importReference, "FileGuid", out object? importFileGuidValue)
                    ? ReadGuid(importFileGuidValue)
                    : Guid.Empty;
                entry = ResolveImportedEbxEntry(importFileGuid, importReference.PartitionGuid, importReference.InstanceGuid);
                if (entry is not null)
                {
                    return true;
                }

                break;
        }

        foreach (string memberName in new[] { "FileGuid", "ClassGuid", "PartitionGuid", "InstanceGuid", "External", "Internal", "Value", "Reference", "Asset", "Name", "Path" })
        {
            if (TryGetMemberValue(value, memberName, out object? innerValue) &&
                TryExtractEbxAssetEntry(innerValue, out entry, depth + 1))
            {
                return true;
            }
        }

        return false;
    }

    private static EbxAssetEntry? ResolveImportedEbxEntry(Guid fileGuid, Guid partitionGuid, Guid instanceGuid)
    {
        if (fileGuid != Guid.Empty)
        {
            EbxAssetEntry? fileEntry = AssetManager.GetEbxAssetEntry(fileGuid);
            if (fileEntry is not null)
            {
                return fileEntry;
            }
        }

        if (partitionGuid != Guid.Empty)
        {
            EbxAssetEntry? partitionEntry = AssetManager.GetEbxAssetEntry(partitionGuid);
            if (partitionEntry is not null)
            {
                return partitionEntry;
            }
        }

        if (instanceGuid != Guid.Empty)
        {
            return AssetManager.GetEbxAssetEntry(instanceGuid);
        }

        return null;
    }

    private static Guid ReadGuid(object? value)
    {
        return value switch
        {
            Guid guid => guid,
            AssetClassGuid assetClassGuid when assetClassGuid.IsExported => assetClassGuid.ExportedGuid,
            _ => Guid.TryParse(value?.ToString(), out Guid parsedGuid) ? parsedGuid : Guid.Empty
        };
    }

    private static bool TryExtractResourceId(object? resourceValue, out ulong rid)
    {
        rid = 0;
        if (resourceValue is null)
        {
            return false;
        }

        switch (resourceValue)
        {
            case ResourceRef resourceRef:
                rid = resourceRef;
                return true;
            case ulong ulongValue:
                rid = ulongValue;
                return true;
            case uint uintValue:
                rid = uintValue;
                return true;
            case long longValue when longValue >= 0:
                rid = (ulong)longValue;
                return true;
            case int intValue when intValue >= 0:
                rid = (ulong)intValue;
                return true;
        }

        if (resourceValue is IConvertible convertible)
        {
            try
            {
                rid = convertible.ToUInt64(CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
            }
        }

        Type type = resourceValue.GetType();
        foreach (string memberName in new[] { "ResourceId", "Value", "ResRid" })
        {
            if (TryGetMemberValue(resourceValue, memberName, out object? innerValue) &&
                TryExtractResourceId(innerValue, out rid))
            {
                return true;
            }
        }

        MethodInfo? implicitOperator = type
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(method =>
                method.Name == "op_Implicit" &&
                method.ReturnType == typeof(ulong) &&
                method.GetParameters().Length == 1 &&
                method.GetParameters()[0].ParameterType.IsAssignableFrom(type));

        if (implicitOperator is not null)
        {
            object? result = implicitOperator.Invoke(null, new[] { resourceValue });
            if (result is ulong ulongResult)
            {
                rid = ulongResult;
                return true;
            }
        }

        return ulong.TryParse(resourceValue.ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rid) ||
               ulong.TryParse(resourceValue.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out rid);
    }

    private static string NormalizeMemberName(string name)
    {
        string trimmed = name.TrimStart('_');
        if (trimmed.StartsWith("m_", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..];
        }

        return trimmed.Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
    }

    private static bool TryLoad(EbxAssetEntry entry, out MeshAssetLoadResult? load)
    {
        try
        {
            load = Load(entry);
            return true;
        }
        catch
        {
            load = null;
            return false;
        }
    }

    private static void TryDeleteTempFiles(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            string materialPath = Path.ChangeExtension(path, ".mtl");
            if (File.Exists(materialPath))
            {
                File.Delete(materialPath);
            }
        }
        catch
        {
        }
    }

    private static MeshPackageManifest CreateManifest(MeshAssetLoadResult load)
    {
        MeshPackageManifest manifest = new()
        {
            Version = 1,
            Profile = ProfilesLibrary.ProfileName,
            AssetName = load.Entry.Name,
            ResourceName = load.ResourceEntry.Name,
            ResourceRid = $"0x{load.ResourceEntry.ResRid:X16}",
            ResourceType = load.ResourceEntry.Type,
            MeshName = load.Mesh.Name,
            MeshFullName = load.Mesh.FullName,
            Lods = load.Mesh.Lods.Select(lod => new MeshPackageLod
            {
                Index = lod.Index,
                Name = lod.Name,
                ShortName = lod.ShortName,
                ChunkId = lod.ChunkId,
                UsesExternalChunk = lod.UsesExternalChunk,
                SectionCount = lod.Sections.Count,
                VertexBufferSize = lod.VertexBufferSize,
                IndexBufferSize = lod.IndexBufferSize
            }).ToList()
        };

        int chunkIndex = 0;
        foreach (MeshSet.MeshLod lod in load.Mesh.Lods.Where(static lod => lod.UsesExternalChunk))
        {
            manifest.Chunks.Add(new MeshPackageChunk
            {
                LodIndex = lod.Index,
                ChunkId = lod.ChunkId,
                File = $"chunks/{chunkIndex:D2}_{lod.ChunkId:N}.bin"
            });
            chunkIndex++;
        }

        return manifest;
    }

    private static void ValidateCompatibility(MeshAssetLoadResult currentLoad, MeshSet importedMesh, MeshPackageManifest manifest)
    {
        if (!importedMesh.ParsedSuccessfully)
        {
            throw new InvalidOperationException(importedMesh.ParseError ?? "The imported mesh package could not be parsed.");
        }

        if (!currentLoad.Mesh.ParsedSuccessfully)
        {
            throw new InvalidOperationException(
                currentLoad.Mesh.ParseError ?? "The target mesh could not be parsed, so chunk remapping cannot be applied.");
        }

        if (currentLoad.Mesh.Lods.Count != importedMesh.Lods.Count)
        {
            throw new InvalidOperationException(
                $"LOD count mismatch. Target mesh has {currentLoad.Mesh.Lods.Count} LOD(s) but the package contains {importedMesh.Lods.Count}.");
        }

        for (int i = 0; i < currentLoad.Mesh.Lods.Count; i++)
        {
            MeshSet.MeshLod currentLod = currentLoad.Mesh.Lods[i];
            MeshSet.MeshLod importedLod = importedMesh.Lods[i];

            if (currentLod.UsesExternalChunk != importedLod.UsesExternalChunk)
            {
                throw new InvalidOperationException(
                    $"LOD {i} cannot be remapped because the target uses {(currentLod.UsesExternalChunk ? "external" : "inline")} data " +
                    $"while the imported mesh uses {(importedLod.UsesExternalChunk ? "external" : "inline")} data.");
            }
        }

        int manifestChunkCount = manifest.Chunks.Count;
        int importedChunkCount = importedMesh.Lods.Count(static lod => lod.UsesExternalChunk);
        if (manifestChunkCount != importedChunkCount)
        {
            throw new InvalidOperationException(
                $"Mesh package metadata is inconsistent. Manifest lists {manifestChunkCount} external chunk(s) but the imported resource references {importedChunkCount}.");
        }
    }

    private static Dictionary<Guid, Guid> RemapImportedChunks(MeshSet currentMesh, MeshSet importedMesh)
    {
        Dictionary<Guid, Guid> mapping = [];
        for (int i = 0; i < currentMesh.Lods.Count; i++)
        {
            MeshSet.MeshLod currentLod = currentMesh.Lods[i];
            MeshSet.MeshLod importedLod = importedMesh.Lods[i];
            if (!currentLod.UsesExternalChunk)
            {
                continue;
            }

            mapping[importedLod.ChunkId] = currentLod.ChunkId;
            importedLod.ChunkId = currentLod.ChunkId;
        }

        return mapping;
    }

    private static MeshSet ReadMesh(byte[] resourceBytes, byte[] resourceMeta)
    {
        using MemoryStream memoryStream = new(resourceBytes);
        using Frosty.Sdk.IO.DataStream dataStream = new(memoryStream);
        MeshSet mesh = new();
        mesh.Deserialize(dataStream, resourceMeta);
        return mesh;
    }

    private static MeshPackageManifest ReadManifest(ZipArchive archive)
    {
        ZipArchiveEntry entry = archive.GetEntry(c_manifestEntryName)
            ?? throw new InvalidOperationException("The mesh package is missing manifest.json.");

        using Stream stream = entry.Open();
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);

        MeshPackageManifest? manifest = JsonSerializer.Deserialize<MeshPackageManifest>(buffer.ToArray(), JsonOptions);
        return manifest ?? throw new InvalidOperationException("Failed to deserialize mesh package manifest.");
    }

    private static byte[] ReadEntryBytes(ZipArchive archive, string entryName)
    {
        ZipArchiveEntry entry = archive.GetEntry(entryName)
            ?? throw new InvalidOperationException($"The mesh package is missing \"{entryName}\".");

        using Stream stream = entry.Open();
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void WriteBytes(ZipArchive archive, string entryName, byte[] data)
    {
        ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using Stream stream = entry.Open();
        stream.Write(data, 0, data.Length);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private sealed class MeshPackageManifest
    {
        public int Version { get; set; }
        public string Profile { get; set; } = string.Empty;
        public string AssetName { get; set; } = string.Empty;
        public string ResourceName { get; set; } = string.Empty;
        public string ResourceRid { get; set; } = string.Empty;
        public string ResourceType { get; set; } = string.Empty;
        public string MeshName { get; set; } = string.Empty;
        public string MeshFullName { get; set; } = string.Empty;
        public List<MeshPackageLod> Lods { get; set; } = [];
        public List<MeshPackageChunk> Chunks { get; set; } = [];
    }

    private sealed class MeshPackageLod
    {
        public int Index { get; set; }
        public string Name { get; set; } = string.Empty;
        public string ShortName { get; set; } = string.Empty;
        public Guid ChunkId { get; set; }
        public bool UsesExternalChunk { get; set; }
        public int SectionCount { get; set; }
        public uint VertexBufferSize { get; set; }
        public uint IndexBufferSize { get; set; }
    }

    private sealed class MeshPackageChunk
    {
        public int LodIndex { get; set; }
        public Guid ChunkId { get; set; }
        public string File { get; set; } = string.Empty;
    }
}
