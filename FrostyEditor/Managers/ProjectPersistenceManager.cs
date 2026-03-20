using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Frosty.ModSupport;
using Frosty.ModSupport.Mod;
using Frosty.ModSupport.Mod.Resources;
using Frosty.Sdk;
using Frosty.Sdk.Ebx;
using Frosty.Sdk.IO;
using Frosty.Sdk.Managers;
using Frosty.Sdk.Managers.Entries;
using Frosty.Sdk.Resources;
using Frosty.Sdk.Utils;

namespace FrostyEditor.Managers;

public sealed class ProjectOperationResult
{
    public bool Success { get; }
    public string Message { get; }
    public string? Path { get; }

    public ProjectOperationResult(bool inSuccess, string inMessage, string? inPath = null)
    {
        Success = inSuccess;
        Message = inMessage;
        Path = inPath;
    }
}

public static class ProjectPersistenceManager
{
    private const string ProjectFileName = "project.json";

    public static void ResetSession()
    {
        AssetManager.ResetModifiedAssets();
        AssetEditStateTracker.ClearAllDirty();
        AssetEditStateTracker.ClearAllModified();
        InspectorEditStateTracker.ClearAllDirty();
        InspectorEditStateTracker.ClearAllModified();
    }

    public static ProjectOperationResult SaveProject(string projectDirectory, string? projectName = null)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            return new ProjectOperationResult(false, "A project folder is required.");
        }

        Directory.CreateDirectory(projectDirectory);

        string ebxDirectory = Path.Combine(projectDirectory, "ebx");
        string textureDirectory = Path.Combine(projectDirectory, "textures");
        if (Directory.Exists(ebxDirectory))
        {
            Directory.Delete(ebxDirectory, true);
        }

        if (Directory.Exists(textureDirectory))
        {
            Directory.Delete(textureDirectory, true);
        }

        Directory.CreateDirectory(ebxDirectory);
        Directory.CreateDirectory(textureDirectory);

        EditorProjectFile project = new()
        {
            Profile = ProfilesLibrary.ProfileName,
            Name = string.IsNullOrWhiteSpace(projectName) ? Path.GetFileName(projectDirectory) : projectName,
            Details = CreateDefaultModDetails(string.IsNullOrWhiteSpace(projectName) ? Path.GetFileName(projectDirectory) : projectName)
        };

        int ebxIndex = 0;
        int textureIndex = 0;
        foreach (string assetName in AssetEditStateTracker.EnumerateModifiedAssets().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            EbxAssetEntry? entry = AssetManager.GetEbxAssetEntry(assetName);
            if (entry is null)
            {
                continue;
            }

            if (AssetManager.IsEbxModified(entry.Name))
            {
                string fileName = $"{ebxIndex:D4}_{MakeSafeFileStem(entry.Name)}.ebx";
                string relativePath = Path.Combine("ebx", fileName);
                using Block<byte> data = AssetManager.GetAsset(entry);
                File.WriteAllBytes(Path.Combine(projectDirectory, relativePath), data.ToArray());
                project.Ebx.Add(new EbxProjectAsset
                {
                    Name = entry.Name,
                    File = relativePath
                });
                ebxIndex++;
            }

            if (!TextureAssetOperations.IsTextureAsset(entry) || !TextureAssetOperations.IsModified(entry))
            {
                continue;
            }

            TextureAssetLoadResult load = TextureAssetOperations.Load(entry);
            ChunkAssetEntry? chunkEntry = AssetManager.GetChunkAssetEntry(load.Texture.ChunkId);
            if (chunkEntry is null)
            {
                continue;
            }

            string assetFolder = Path.Combine(textureDirectory, $"{textureIndex:D4}_{MakeSafeFileStem(entry.Name)}");
            Directory.CreateDirectory(assetFolder);

            string resRelativePath = Path.Combine("textures", $"{textureIndex:D4}_{MakeSafeFileStem(entry.Name)}", "resource.bin");
            string metaRelativePath = Path.Combine("textures", $"{textureIndex:D4}_{MakeSafeFileStem(entry.Name)}", "resource.meta");
            string chunkRelativePath = Path.Combine("textures", $"{textureIndex:D4}_{MakeSafeFileStem(entry.Name)}", "chunk.bin");

            using (Block<byte> resData = AssetManager.GetAsset(load.ResourceEntry))
            using (Block<byte> chunkData = AssetManager.GetAsset(chunkEntry))
            {
                File.WriteAllBytes(Path.Combine(projectDirectory, resRelativePath), resData.ToArray());
                File.WriteAllBytes(Path.Combine(projectDirectory, metaRelativePath), AssetManager.GetResMeta(load.ResourceEntry));
                File.WriteAllBytes(Path.Combine(projectDirectory, chunkRelativePath), chunkData.ToArray());
            }

            project.Textures.Add(new TextureProjectAsset
            {
                Name = entry.Name,
                ResRid = load.ResourceEntry.ResRid,
                ChunkId = load.Texture.ChunkId,
                ResourceFile = resRelativePath,
                ResourceMetaFile = metaRelativePath,
                ChunkFile = chunkRelativePath
            });
            textureIndex++;
        }

        string projectPath = Path.Combine(projectDirectory, ProjectFileName);
        File.WriteAllText(projectPath, JsonSerializer.Serialize(project, new JsonSerializerOptions
        {
            WriteIndented = true
        }));

        AssetEditStateTracker.ClearAllDirty();
        InspectorEditStateTracker.ClearAllDirty();

        return new ProjectOperationResult(true,
            $"Saved project '{project.Name}' with {project.Ebx.Count} EBX edit(s) and {project.Textures.Count} texture edit(s).",
            projectDirectory);
    }

    public static ProjectOperationResult LoadProject(string projectDirectory)
    {
        string projectPath = Path.Combine(projectDirectory, ProjectFileName);
        if (!File.Exists(projectPath))
        {
            return new ProjectOperationResult(false, "The selected folder does not contain a project.json file.");
        }

        EditorProjectFile? project;
        try
        {
            project = JsonSerializer.Deserialize<EditorProjectFile>(File.ReadAllText(projectPath));
        }
        catch (Exception ex)
        {
            return new ProjectOperationResult(false, $"Failed to load project.json: {ex.Message}");
        }

        if (project is null)
        {
            return new ProjectOperationResult(false, "Failed to deserialize project.json.");
        }

        ResetSession();

        int loadedEbx = 0;
        int loadedTextures = 0;

        foreach (EbxProjectAsset ebxAsset in project.Ebx)
        {
            EbxAssetEntry? entry = AssetManager.GetEbxAssetEntry(ebxAsset.Name);
            string path = Path.Combine(projectDirectory, ebxAsset.File);
            if (entry is null || !File.Exists(path))
            {
                continue;
            }

            AssetManager.ModifyEbx(entry, File.ReadAllBytes(path));
            AssetEditStateTracker.MarkModified(entry.Name);
            loadedEbx++;
        }

        foreach (TextureProjectAsset textureAsset in project.Textures)
        {
            EbxAssetEntry? entry = AssetManager.GetEbxAssetEntry(textureAsset.Name);
            string resPath = Path.Combine(projectDirectory, textureAsset.ResourceFile);
            string metaPath = Path.Combine(projectDirectory, textureAsset.ResourceMetaFile);
            string chunkPath = Path.Combine(projectDirectory, textureAsset.ChunkFile);
            if (entry is null || !File.Exists(resPath) || !File.Exists(metaPath) || !File.Exists(chunkPath))
            {
                continue;
            }

            TextureAssetLoadResult load = TextureAssetOperations.Load(entry);
            AssetManager.ModifyRes(textureAsset.ResRid, File.ReadAllBytes(resPath), File.ReadAllBytes(metaPath));
            Texture texture = AssetManager.GetResAs<Texture>(load.ResourceEntry);
            AssetManager.ModifyChunk(textureAsset.ChunkId, File.ReadAllBytes(chunkPath), texture);
            AssetEditStateTracker.MarkModified(entry.Name);
            loadedTextures++;
        }

        string profileWarning = string.Equals(project.Profile, ProfilesLibrary.ProfileName, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : $" Project was saved for profile '{project.Profile}' but '{ProfilesLibrary.ProfileName}' is currently loaded.";

        return new ProjectOperationResult(true,
            $"Loaded project '{project.Name}' with {loadedEbx} EBX edit(s) and {loadedTextures} texture edit(s).{profileWarning}",
            projectDirectory);
    }

    public static ProjectOperationResult ExportMod(string modPath, string? title = null)
    {
        if (string.IsNullOrWhiteSpace(modPath))
        {
            return new ProjectOperationResult(false, "A mod output path is required.");
        }

        List<BaseModResource> resources = [];
        List<Block<byte>> dataBlocks = [];
        HashSet<ulong> exportedRes = [];
        HashSet<Guid> exportedChunks = [];

        try
        {
            foreach (string assetName in AssetEditStateTracker.EnumerateModifiedAssets().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                EbxAssetEntry? entry = AssetManager.GetEbxAssetEntry(assetName);
                if (entry is null)
                {
                    continue;
                }

                if (AssetManager.IsEbxModified(entry.Name))
                {
                    AddEbxResource(entry, resources, dataBlocks);
                }

                if (!TextureAssetOperations.IsTextureAsset(entry) || !TextureAssetOperations.IsModified(entry))
                {
                    continue;
                }

                TextureAssetLoadResult load = TextureAssetOperations.Load(entry);
                if (exportedChunks.Add(load.Texture.ChunkId) && AssetManager.GetChunkAssetEntry(load.Texture.ChunkId) is ChunkAssetEntry chunkEntry)
                {
                    AddChunkResource(load, chunkEntry, resources, dataBlocks);
                }

                if (exportedRes.Add(load.ResourceEntry.ResRid))
                {
                    AddResResource(load, resources, dataBlocks);
                }
            }

            if (resources.Count == 0)
            {
                return new ProjectOperationResult(false, "There are no modified assets to export.");
            }

            string modTitle = string.IsNullOrWhiteSpace(title) ? "FrostyToolsuite Export" : title;
            FrostyMod.Save(modPath, resources.ToArray(), dataBlocks.ToArray(), CreateDefaultModDetails(modTitle));
            return new ProjectOperationResult(true, $"Exported mod to {modPath}.", modPath);
        }
        catch (Exception ex)
        {
            return new ProjectOperationResult(false, $"Failed to export mod: {ex.Message}");
        }
        finally
        {
            foreach (Block<byte> block in dataBlocks)
            {
                block.Dispose();
            }
        }
    }

    private static void AddEbxResource(EbxAssetEntry entry, ICollection<BaseModResource> resources, ICollection<Block<byte>> dataBlocks)
    {
        EbxPartition partition = AssetManager.GetEbxPartition(entry);
        using Block<byte> rawData = new(0);
        using (BlockStream stream = new(rawData, true))
        {
            EbxPartition.Serialize(stream, partition,
                ProfilesLibrary.EbxVersion == 6 ? EbxWriteFlags.DoNotSort : EbxWriteFlags.None);
        }

        Block<byte> compressedData = Cas.CompressData(rawData, ModExportUtilities.GetEbxCompression(), 0);
        dataBlocks.Add(compressedData);
        resources.Add(new EbxModResource(
            dataBlocks.Count - 1,
            entry.Name.ToLowerInvariant(),
            Frosty.Sdk.Utils.Utils.GenerateSha1(compressedData),
            rawData.Size,
            0,
            0,
            string.Empty,
            [],
            []));
    }

    private static void AddResResource(TextureAssetLoadResult load, ICollection<BaseModResource> resources, ICollection<Block<byte>> dataBlocks)
    {
        load.Texture.AssetNameHash = (uint)Frosty.Sdk.Utils.Utils.HashString(load.ResourceEntry.Name);

        byte[] meta = AssetManager.GetResMeta(load.ResourceEntry);
        using Block<byte> rawData = new(0);
        using (BlockStream stream = new(rawData, true))
        {
            load.Texture.Serialize(stream, meta);
        }

        Block<byte> compressedData = Cas.CompressData(rawData, ModExportUtilities.GetResCompression(), 0);
        dataBlocks.Add(compressedData);
        resources.Add(new ResModResource(
            dataBlocks.Count - 1,
            load.ResourceEntry.Name.ToLowerInvariant(),
            Frosty.Sdk.Utils.Utils.GenerateSha1(compressedData),
            rawData.Size,
            0,
            0,
            string.Empty,
            [],
            [],
            (uint)load.ResourceEntry.ResType,
            load.ResourceEntry.ResRid,
            meta));
    }

    private static void AddChunkResource(TextureAssetLoadResult load, ChunkAssetEntry entry, ICollection<BaseModResource> resources, ICollection<Block<byte>> dataBlocks)
    {
        load.Texture.AssetNameHash = (uint)Frosty.Sdk.Utils.Utils.HashString(load.ResourceEntry.Name);
        using Block<byte> rawData = AssetManager.GetAsset(entry);
        Block<byte> compressedData = ModExportUtilities.CompressTextureChunkData(load.Texture, rawData);
        load.Texture.RangeEnd = (uint)compressedData.Size;
        dataBlocks.Add(compressedData);
        resources.Add(new ChunkModResource(
            dataBlocks.Count - 1,
            entry.Id.ToString(),
            Frosty.Sdk.Utils.Utils.GenerateSha1(compressedData),
            rawData.Size,
            0,
            0,
            string.Empty,
            [],
            [],
            load.Texture.RangeStart,
            load.Texture.RangeEnd,
            load.Texture.LogicalOffset,
            load.Texture.LogicalSize,
            unchecked((int)load.Texture.AssetNameHash),
            load.Texture.FirstMip,
            [],
            []));
    }

    private static FrostyModDetails CreateDefaultModDetails(string title)
    {
        return new FrostyModDetails(
            title,
            Environment.UserName,
            "Editor",
            "1.0",
            "Exported from Frosty Editor 2.0",
            string.Empty);
    }

    private static string MakeSafeFileStem(string value)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        char[] chars = value.Select(ch => invalidChars.Contains(ch) || ch == '/' || ch == '\\' ? '_' : ch).ToArray();
        string result = new(chars);
        return string.IsNullOrWhiteSpace(result) ? "asset" : result;
    }

    private sealed class EditorProjectFile
    {
        public string Profile { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public FrostyModDetails Details { get; set; } = new();
        public List<EbxProjectAsset> Ebx { get; set; } = [];
        public List<TextureProjectAsset> Textures { get; set; } = [];
    }

    private sealed class EbxProjectAsset
    {
        public string Name { get; set; } = string.Empty;
        public string File { get; set; } = string.Empty;
    }

    private sealed class TextureProjectAsset
    {
        public string Name { get; set; } = string.Empty;
        public ulong ResRid { get; set; }
        public Guid ChunkId { get; set; }
        public string ResourceFile { get; set; } = string.Empty;
        public string ResourceMetaFile { get; set; } = string.Empty;
        public string ChunkFile { get; set; } = string.Empty;
    }
}








