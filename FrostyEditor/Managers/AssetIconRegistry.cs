using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Frosty.Sdk.Managers.Entries;

namespace FrostyEditor.Managers;

public static class AssetIconRegistry
{
    private const string AssetBasePath = "avares://FrostyEditor/Assets/AssetTypes/";
    private static readonly Dictionary<string, Bitmap> s_legacyBitmapCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> s_soundEntryIconCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> s_exactTypeMappings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AtlasTextureAsset"] = "ImageFileType.png",
        ["AST"] = "ArchiveFileType.png",
        ["BIG"] = "ArchiveFileType.png",
        ["DB"] = "DatabaseFileType.png",
        ["DDS"] = "Dds.png",
        ["XML"] = "TextFileType.png",
        ["TXT"] = "TextFileType.png",
        ["LUA"] = "TextFileType.png",
        ["CSV"] = "TextFileType.png",
        ["INI"] = "TextFileType.png",
        ["H"] = "TextFileType.png",
        ["JSON"] = "TextFileType.png",
        ["HTML"] = "TextFileType.png",
        ["PRE"] = "TextFileType.png",
        ["JS"] = "TextFileType.png",
        ["LESS"] = "TextFileType.png",
        ["CAM"] = "TextFileType.png",
        ["DIR"] = "TextFileType.png",
        ["RigidMeshAsset"] = "RigidMeshFileType.png",
        ["SkinnedMeshAsset"] = "SkinnedMeshFileType.png",
        ["CompositeMeshAsset"] = "CompositeMeshFileType.png",
        ["LuaFileDataAsset"] = "LuaFileType.png",
        ["LuaRunnerCompiledLua"] = "LuaFileType.png",
        ["LuaScriptAsset"] = "LuaFileType.png",
        ["MovieTexture2Asset"] = "MovieTextureFileType.png",
        ["SoundWaveAsset"] = "SoundFileType.png",
        ["NewWaveAsset"] = "SoundFileType.png",
        ["HarmonySampleBankAsset"] = "SoundFileType.png",
        ["OctaneAsset"] = "SoundFileType.png",
        ["ImpulseResponseAsset"] = "SoundFileType.png",
        ["SvgImage"] = "SvgFileType.png",
        ["VersionData"] = "VersionDataFileType.png",
        ["IesProfileAsset"] = "IesResourceFileType.png",
        ["DifficultyWeaponTableData"] = "SpreadsheetFileType.png",
        ["TextureAsset"] = "ImageFileType.png",
        ["TextureArrayAsset"] = "ImageFileType.png",
    };

    private static readonly (string Needle, string FileName)[] s_containsMappings =
    {
        ("BlueprintBundle", "BlueprintBundleFileType.png"),
        ("BundleRefTable", "BlueprintBundleFileType.png"),
        ("Blueprint", "BlueprintFileType.png"),
        ("Emitter", "EmitterFileType.png"),
        ("Particle", "EmitterFileType.png"),
        ("Encrypted", "EncryptedFileType.png"),
        ("MovieTexture", "MovieTextureFileType.png"),
        ("Texture", "ImageFileType.png"),
        ("Image", "ImageFileType.png"),
        ("Svg", "SvgFileType.png"),
        ("Lua", "LuaFileType.png"),
        ("Database", "DatabaseFileType.png"),
        ("Text", "TextFileType.png"),
        ("Mesh", "RigidMeshFileType.png"),
        ("Havok", "HavokFileType.png"),
        ("Rigid", "HavokFileType.png"),
        ("Internal", "InternalFileType.png"),
        ("LogicPrefab", "LogicPrefabFileType.png"),
        ("ObjectVariation", "ObjectVariationFileType.png"),
        ("ShaderPreset", "ShaderPresetFileType.png"),
        ("Shader", "ShaderFileType.png"),
        ("Material", "ShaderFileType.png"),
        ("Skeleton", "SkeletonFileType.png"),
        ("Rig", "SkeletonFileType.png"),
        ("Sound", "SoundFileType.png"),
        ("Audio", "SoundFileType.png"),
        ("Voice", "SoundFileType.png"),
        ("Spreadsheet", "SpreadsheetFileType.png"),
        ("Table", "SpreadsheetFileType.png"),
        ("Stat", "StatFileType.png"),
        ("SubWorld", "SubWorldFileType.png"),
        ("World", "SubWorldFileType.png"),
        ("Level", "SubWorldFileType.png"),
        ("Archive", "ArchiveFileType.png"),
        ("VersionData", "VersionDataFileType.png"),
        ("Ies", "IesResourceFileType.png"),
    };

    private static readonly Dictionary<string, Bitmap> s_bitmapCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> s_knownSoundDataTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AudioPatchInterfaceAsset",
        "BoxOfficeEventSystemDescription",
        "ContextDataFile",
        "ContextSystem",
        "FootballAudioDoNotPlayListCollection",
        "FootballAudioEATraxMetaDataCollection",
        "FootballAudioFmvVolumeMappingCollection",
        "FootballAudioUISoundStyleCollection",
        "MixerAsset",
        "MixerSystemAsset",
        "MusicAsset",
        "MusicGraphAsset",
        "MusicInterfaceAsset",
        "MusicPlaylistAsset",
        "SchematicChannelAsset",
        "SentenceDatabase",
        "SentenceKeywords",
        "SentencePlayerWaveCollection",
        "SentenceSampleMetadata",
        "SentenceSampleProbability",
        "SoundPatchAsset",
        "SoundPatchConfigurationAsset",
        "SoundSubPatchAsset",
        "UIAudioContext",
        "UIAudioInterface",
    };

    public static Bitmap GetIcon(string? assetType)
    {
        string fileName = ResolveIconFileName(assetType);
        return GetBitmap(fileName);
    }

    public static Bitmap GetIcon(AssetEntry? entry)
    {
        string fileName = ResolveIconFileName(entry);
        return GetBitmap(fileName);
    }

    private static Bitmap GetBitmap(string fileName)
    {
        if (s_bitmapCache.TryGetValue(fileName, out Bitmap? cached))
        {
            return cached;
        }

        Bitmap bitmap = LoadBitmap(fileName) ??
                        LoadBitmap("BlankFileType.png") ??
                        LoadBitmap("InternalFileType.png") ??
                        throw new FileNotFoundException("Could not load fallback asset icon.");
        s_bitmapCache[fileName] = bitmap;
        return bitmap;
    }

    private static string ResolveIconFileName(AssetEntry? entry)
    {
        if (entry is not EbxAssetEntry ebxEntry ||
            string.IsNullOrWhiteSpace(ebxEntry.Name) ||
            !ebxEntry.Name.StartsWith("sound/", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveIconFileName(entry?.Type);
        }

        if (s_knownSoundDataTypes.Contains(ebxEntry.Type))
        {
            return "Dat.png";
        }

        if (string.Equals(ebxEntry.Type, "SoundAsset", StringComparison.OrdinalIgnoreCase))
        {
            if (s_soundEntryIconCache.TryGetValue(ebxEntry.Name, out string? cachedIcon))
            {
                return cachedIcon;
            }

            string resolvedIcon;
            try
            {
                resolvedIcon = SoundAssetOperations.IsSoundAsset(ebxEntry)
                    ? "SoundFileType.png"
                    : "Dat.png";
            }
            catch
            {
                resolvedIcon = ebxEntry.Name.EndsWith("_nar", StringComparison.OrdinalIgnoreCase)
                    ? "Dat.png"
                    : "SoundFileType.png";
            }

            s_soundEntryIconCache[ebxEntry.Name] = resolvedIcon;
            return resolvedIcon;
        }

        return ResolveIconFileName(entry.Type);
    }

    private static string ResolveIconFileName(string? assetType)
    {
        if (string.IsNullOrWhiteSpace(assetType))
        {
            return "BlankFileType.png";
        }

        string normalized = assetType.Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase);

        if (s_exactTypeMappings.TryGetValue(normalized, out string? exactMatch))
        {
            return exactMatch;
        }

        foreach ((string needle, string fileName) in s_containsMappings)
        {
            if (normalized.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return fileName;
            }
        }

        return "BlankFileType.png";
    }

    private static Bitmap? LoadBitmap(string fileName)
    {
        string uri = AssetBasePath + fileName;
        if (!AssetLoader.Exists(new Uri(uri)))
        {
            return null;
        }

        using Stream stream = AssetLoader.Open(new Uri(uri));
        return new Bitmap(stream);
    }

    public static Bitmap GetLegacyIcon(string uri)
    {
        if (s_legacyBitmapCache.TryGetValue(uri, out Bitmap? cached))
        {
            return cached;
        }

        using Stream stream = AssetLoader.Open(new Uri(uri));
        Bitmap bitmap = new(stream);
        s_legacyBitmapCache[uri] = bitmap;
        return bitmap;
    }
}
