using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Frosty.Sdk.Ebx;
using Frosty.Sdk.IO;
using Frosty.Sdk.Managers;
using Frosty.Sdk.Managers.Entries;
using Frosty.Sdk.Resources;
using FrostyEditor.Utils;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace FrostyEditor.Managers;

[Flags]
public enum TextureChannelMask
{
    None = 0,
    Red = 1,
    Green = 2,
    Blue = 4,
    Alpha = 8,
    Rgb = Red | Green | Blue,
    Rgba = Red | Green | Blue | Alpha
}

public sealed record TextureAssetLoadResult(EbxAssetEntry Entry, Texture Texture, ResAssetEntry ResourceEntry);
public sealed record TextureOperationResult(bool Success, string Message);

public static class TextureAssetOperations
{
    public static bool IsTextureAsset(EbxAssetEntry entry)
    {
        return entry.Type.Equals("TextureAsset", StringComparison.OrdinalIgnoreCase) ||
               entry.Type.Equals("TextureArrayAsset", StringComparison.OrdinalIgnoreCase) ||
               entry.Type.Equals("ImageLibraryTexture", StringComparison.OrdinalIgnoreCase) ||
               entry.Type.Equals("MovieTextureAsset", StringComparison.OrdinalIgnoreCase);
    }

    public static TextureAssetLoadResult Load(EbxAssetEntry entry)
    {
        EbxPartition partition = AssetManager.GetEbxPartition(entry);
        object rootObject = partition.PrimaryInstance;

        if (!TryGetPropertyValue(rootObject, "Resource", out object? resourceValue))
        {
            throw new InvalidOperationException($"Texture asset \"{entry.Name}\" does not expose a readable Resource property.");
        }

        if (!TryExtractResourceId(resourceValue, out ulong rid))
        {
            string resourceType = resourceValue?.GetType().FullName ?? "<null>";
            throw new InvalidOperationException($"Texture asset \"{entry.Name}\" returned an unsupported Resource value of type \"{resourceType}\".");
        }

        ResAssetEntry? resourceEntry = AssetManager.GetResAssetEntry(rid);
        if (resourceEntry is null)
        {
            throw new InvalidOperationException($"Texture resource 0x{rid:X} could not be resolved for \"{entry.Name}\".");
        }

        Texture texture = AssetManager.GetResAs<Texture>(resourceEntry);
        return new TextureAssetLoadResult(entry, texture, resourceEntry);
    }

    public static bool IsModified(EbxAssetEntry entry)
    {
        if (!TryLoad(entry, out TextureAssetLoadResult? load))
        {
            return false;
        }

        return AssetManager.IsResModified(load.ResourceEntry.ResRid) ||
               AssetManager.IsChunkModified(load.Texture.ChunkId);
    }

    public static TextureOperationResult Revert(EbxAssetEntry entry)
    {
        if (!TryLoad(entry, out TextureAssetLoadResult? load))
        {
            return new TextureOperationResult(false, $"Unable to resolve texture state for {entry.Filename}.");
        }

        bool revertedRes = AssetManager.RevertRes(load.ResourceEntry.ResRid);
        bool revertedChunk = AssetManager.RevertChunk(load.Texture.ChunkId);

        if (!revertedRes && !revertedChunk)
        {
            return new TextureOperationResult(false, $"{entry.Filename} has no pending texture edits to revert.");
        }

        AssetEditStateTracker.ClearDirty(entry.Name);
        AssetEditStateTracker.ClearModified(entry.Name);

        return new TextureOperationResult(true, $"Reverted {entry.Filename} to the original game texture.");
    }

    public static Bitmap CreatePreviewBitmap(Texture texture, int mipLevel, int sliceLevel, TextureChannelMask channels, bool luminance)
    {
        byte[] ddsData = BuildPreviewDds(texture, mipLevel, sliceLevel);
        BlobData blob = default;
        try
        {
            DxTexNative.ConvertDDSToImage(ddsData, ddsData.Length, TextureImageFormat.PNG, ref blob);
            byte[] pngData = blob.ToArray();
            byte[] processed = ApplyChannels(pngData, channels, luminance);
            return new Bitmap(new MemoryStream(processed));
        }
        finally
        {
            DxTexNative.ReleaseBlob(blob);
        }
    }

    public static async Task<TextureOperationResult> ExportWithPickerAsync(EbxAssetEntry entry)
    {
        TextureAssetLoadResult load = Load(entry);
        FilePickerSaveOptions options = new()
        {
            Title = "Export texture",
            SuggestedFileName = entry.Filename,
            DefaultExtension = "dds",
            FileTypeChoices =
            [
                new FilePickerFileType("DDS (*.dds)") { Patterns = ["*.dds"] },
                new FilePickerFileType("PNG (*.png)") { Patterns = ["*.png"] },
                new FilePickerFileType("TGA (*.tga)") { Patterns = ["*.tga"] },
                new FilePickerFileType("HDR (*.hdr)") { Patterns = ["*.hdr"] }
            ]
        };

        IStorageFile? file = await FileService.SaveFilePickerAsync(options);
        if (file is null)
        {
            return new TextureOperationResult(false, "Texture export canceled.");
        }

        string extension = Path.GetExtension(file.Name).ToLowerInvariant();
        return await ExportAsync(load, file.Path.LocalPath, extension);
    }

    public static async Task<TextureOperationResult> ImportWithPickerAsync(EbxAssetEntry entry)
    {
        TextureAssetLoadResult load = Load(entry);
        FilePickerOpenOptions options = new()
        {
            Title = load.Texture.Type == TextureType.TT_2d ? "Import texture" : "Import textures",
            AllowMultiple = load.Texture.Type != TextureType.TT_2d,
            FileTypeFilter =
            [
                new FilePickerFileType("Texture files")
                {
                    Patterns = ["*.dds", "*.png", "*.tga", "*.hdr"]
                }
            ]
        };

        IReadOnlyList<IStorageFile>? files = await FileService.OpenFilesAsync(options);
        if (files is null || files.Count == 0)
        {
            return new TextureOperationResult(false, "Texture import canceled.");
        }

        return await ImportAsync(load, files);
    }

    public static async Task<TextureOperationResult> ExportAsync(TextureAssetLoadResult load, string path, string extension)
    {
        TextureImageFormat format = extension switch
        {
            ".png" => TextureImageFormat.PNG,
            ".tga" => TextureImageFormat.TGA,
            ".hdr" => TextureImageFormat.HDR,
            _ => TextureImageFormat.DDS
        };

        byte[] ddsData = BuildFullDds(load.Texture);

        if (format == TextureImageFormat.DDS)
        {
            await File.WriteAllBytesAsync(path, ddsData);
            return new TextureOperationResult(true, $"Exported {load.Entry.Filename} to {path}.");
        }

        if (load.Texture.Type == TextureType.TT_2d)
        {
            BlobData blob = default;
            try
            {
                DxTexNative.ConvertDDSToImage(ddsData, ddsData.Length, format, ref blob);
                await File.WriteAllBytesAsync(path, blob.ToArray());
                return new TextureOperationResult(true, $"Exported {load.Entry.Filename} to {path}.");
            }
            finally
            {
                DxTexNative.ReleaseBlob(blob);
            }
        }

        int sliceCount = GetSliceCount(load.Texture);
        string[] suffixes = load.Texture.Type == TextureType.TT_Cube
            ? ["px", "nx", "py", "ny", "pz", "nz"]
            : Enumerable.Range(0, sliceCount).Select(i => i.ToString("D3")).ToArray();

        BlobData[] blobs = new BlobData[sliceCount];
        try
        {
            DxTexNative.ConvertDDSToImages(ddsData, ddsData.Length, format, ref blobs, sliceCount);
            string basePath = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, Path.GetFileNameWithoutExtension(path));
            string fileExtension = Path.GetExtension(path);

            for (int i = 0; i < blobs.Length; i++)
            {
                string outPath = $"{basePath}_{suffixes[i]}{fileExtension}";
                await File.WriteAllBytesAsync(outPath, blobs[i].ToArray());
            }

            return new TextureOperationResult(true, $"Exported {sliceCount} texture slice(s) starting at {path}.");
        }
        finally
        {
            foreach (BlobData blob in blobs)
            {
                DxTexNative.ReleaseBlob(blob);
            }
        }
    }

    public static async Task<TextureOperationResult> ImportAsync(TextureAssetLoadResult load, IReadOnlyList<IStorageFile> files)
    {
        TextureImageFormat format = GetImageFormat(files[0].Name);
        if (format == TextureImageFormat.DDS && files.Count > 1)
        {
            return new TextureOperationResult(false, "DDS import expects a single file.");
        }

        int expectedFiles = load.Texture.Type switch
        {
            TextureType.TT_Cube => 6,
            TextureType.TT_2dArray => GetSliceCount(load.Texture),
            _ => 1
        };

        if (format != TextureImageFormat.DDS && load.Texture.Type != TextureType.TT_2d && files.Count < expectedFiles)
        {
            return new TextureOperationResult(false, $"This texture type expects {expectedFiles} source image(s).");
        }

        byte[] ddsData;
        if (format == TextureImageFormat.DDS)
        {
            await using Stream stream = await files[0].OpenReadAsync();
            using MemoryStream ms = new();
            await stream.CopyToAsync(ms);
            ddsData = ms.ToArray();
        }
        else
        {
            ddsData = await ConvertImagesToDdsAsync(load.Texture, files, format);
        }

        using MemoryStream memStream = new(ddsData);
        using DataStream reader = new(memStream);
        TextureUtils.DDSHeader header = new();
        if (!header.Read(reader))
        {
            return new TextureOperationResult(false, "The selected texture file is not a valid DDS payload.");
        }

        TextureType importedType = GetTextureType(header);
        if (importedType != load.Texture.Type)
        {
            return new TextureOperationResult(false, $"Imported texture type {importedType} does not match original type {load.Texture.Type}.");
        }

        bool currentTextureIsSrgb = load.Texture.PixelFormat.Contains("SRGB", StringComparison.OrdinalIgnoreCase) ||
                                    load.Texture.Flags.HasFlag(TextureFlags.SrgbGamma);
        if (currentTextureIsSrgb && (!header.HasExtendedHeader || !header.ExtendedHeader.DxgiFormat.ToString().Contains("SRgb", StringComparison.OrdinalIgnoreCase)))
        {
            return new TextureOperationResult(false, "Imported texture must use an SRGB-compatible format.");
        }

        if (!TextureUtils.TryGetPixelFormat(header, load.Texture.PixelFormat, out string pixelFormat, out TextureFlags baseFlags))
        {
            return new TextureOperationResult(false, "Imported DDS format is not supported by the current texture pipeline.");
        }

        if (TextureUtils.IsCompressedFormat(pixelFormat) &&
            Math.Max(1, header.MipMapCount) > 1 &&
            (header.Width % 4 != 0 || header.Height % 4 != 0))
        {
            return new TextureOperationResult(false, "Texture width and height must be divisible by 4 for compressed mipmapped imports.");
        }

        byte[] payload = new byte[memStream.Length - memStream.Position];
        reader.Read(payload, 0, payload.Length);

        ushort depth = (header.HasExtendedHeader && header.ExtendedHeader.ResourceDimension == TextureUtils.ResourceDimension.Texture2D)
            ? (ushort)Math.Max(1, header.ExtendedHeader.ArraySize)
            : (ushort)1;
        if ((header.Caps2 & TextureUtils.DDSCaps2.CubeMap) != 0)
        {
            depth = 6;
        }
        if ((header.Caps2 & TextureUtils.DDSCaps2.Volume) != 0)
        {
            depth = (ushort)header.Depth;
        }

        Texture newTexture = new(load.Texture.Type, pixelFormat, (ushort)header.Width, (ushort)header.Height, depth, load.Texture.Version, load.ResourceEntry.ResMeta)
        {
            FirstMip = header.MipMapCount <= load.Texture.FirstMip ? (byte)0 : load.Texture.FirstMip,
            TextureGroup = load.Texture.TextureGroup,
            Flags = baseFlags | (load.Texture.Flags & ~TextureFlags.SrgbGamma),
            AssetNameHash = (uint)Frosty.Sdk.Utils.Utils.HashString(load.ResourceEntry.Name),
            LogicalOffset = load.Texture.LogicalOffset,
            LogicalSize = load.Texture.LogicalSize,
            RangeStart = load.Texture.RangeStart,
            RangeEnd = load.Texture.RangeEnd,
            ChunkId = load.Texture.ChunkId
        };

        for (int i = 0; i < 4; i++)
        {
            newTexture.Unknown3[i] = load.Texture.Unknown3[i];
        }

        newTexture.CalculateMipData((byte)Math.Max(1, header.MipMapCount), TextureUtils.GetFormatBlockSize(pixelFormat), TextureUtils.IsCompressedFormat(pixelFormat), (uint)payload.Length);

        if (newTexture.Type is TextureType.TT_Cube or TextureType.TT_2dArray)
        {
            payload = ReorderSlicesAndMips(newTexture, payload);
        }

        newTexture.SetData(load.Texture.ChunkId, payload);
        AssetManager.ModifyChunk(load.Texture.ChunkId, payload, ((newTexture.Flags & TextureFlags.OnDemandLoaded) != 0 || newTexture.Type != TextureType.TT_2d) ? null : newTexture);
        AssetManager.ModifyRes(load.ResourceEntry.ResRid, newTexture);
        AssetEditStateTracker.MarkDirty(load.Entry.Name);
        AssetEditStateTracker.MarkModified(load.Entry.Name);

        return new TextureOperationResult(true, $"Imported texture data for {load.Entry.Filename}.");
    }

    private static TextureType GetTextureType(TextureUtils.DDSHeader header)
    {
        if (header.HasExtendedHeader)
        {
            if (header.ExtendedHeader.ResourceDimension == TextureUtils.ResourceDimension.Texture3D)
            {
                return TextureType.TT_3d;
            }

            if (header.ExtendedHeader.ResourceDimension == TextureUtils.ResourceDimension.Texture2D)
            {
                if ((header.ExtendedHeader.MiscFlag & 4) != 0)
                {
                    return TextureType.TT_Cube;
                }

                if (header.ExtendedHeader.ArraySize > 1)
                {
                    return TextureType.TT_2dArray;
                }
            }
        }

        if ((header.Caps2 & TextureUtils.DDSCaps2.CubeMap) != 0)
        {
            return TextureType.TT_Cube;
        }

        if ((header.Caps2 & TextureUtils.DDSCaps2.Volume) != 0)
        {
            return TextureType.TT_3d;
        }

        return TextureType.TT_2d;
    }

    private static async Task<byte[]> ConvertImagesToDdsAsync(Texture texture, IReadOnlyList<IStorageFile> files, TextureImageFormat format)
    {
        TextureImportOptions options = new()
        {
            Type = texture.Type,
            Format = TextureUtils.ToShaderFormat(texture.PixelFormat),
            GenerateMipmaps = texture.MipCount > 1,
            MipmapsFilter = 0,
            ResizeTexture = false,
            ResizeFilter = 0,
            ResizeWidth = 0,
            ResizeHeight = 0
        };

        BlobData blob = default;
        try
        {
            if (texture.Type == TextureType.TT_2d)
            {
                await using Stream imageStream = await files[0].OpenReadAsync();
                using MemoryStream memory = new();
                await imageStream.CopyToAsync(memory);
                DxTexNative.ConvertImageToDDS(memory.ToArray(), memory.Length, format, options, ref blob);
            }
            else
            {
                byte[] merged = Array.Empty<byte>();
                long[] sizes = new long[files.Count];
                for (int i = 0; i < files.Count; i++)
                {
                    await using Stream imageStream = await files[i].OpenReadAsync();
                    using MemoryStream memory = new();
                    await imageStream.CopyToAsync(memory);
                    byte[] bytes = memory.ToArray();
                    sizes[i] = bytes.Length;
                    int previousLength = merged.Length;
                    Array.Resize(ref merged, previousLength + bytes.Length);
                    Buffer.BlockCopy(bytes, 0, merged, previousLength, bytes.Length);
                }

                DxTexNative.ConvertImagesToDDS(merged, sizes, sizes.Length, format, options, ref blob);
            }

            return blob.ToArray();
        }
        finally
        {
            DxTexNative.ReleaseBlob(blob);
        }
    }

    private static byte[] BuildFullDds(Texture texture)
    {
        TextureUtils.DDSHeader header = CreateHeader(texture, 0, texture.MipCount);
        using MemoryStream memoryStream = new();
        using DataStream writer = new(memoryStream);
        header.Write(writer);
        byte[] ddsPayload = texture.Type is TextureType.TT_Cube or TextureType.TT_2dArray
            ? ReorderForDdsExport(texture)
            : texture.Data;
        writer.Write(ddsPayload);
        return memoryStream.ToArray();
    }

    private static byte[] BuildPreviewDds(Texture texture, int mipLevel, int sliceLevel)
    {
        int safeMipLevel = ClampMipLevel(texture, mipLevel);
        byte[] mipData = ExtractMipSliceData(texture, safeMipLevel, sliceLevel);
        TextureUtils.DDSHeader header = CreateHeader(texture, safeMipLevel, 1);
        header.Height = GetMipDimension(texture.Height, safeMipLevel);
        header.Width = GetMipDimension(texture.Width, safeMipLevel);
        header.Depth = 0;
        header.MipMapCount = 1;
        header.ExtendedHeader.ArraySize = 1;
        header.Caps &= ~TextureUtils.DDSCaps.MipMap;
        header.Caps2 = 0;
        header.PitchOrLinearSize = mipData.Length;

        using MemoryStream memoryStream = new();
        using DataStream writer = new(memoryStream);
        header.Write(writer);
        writer.Write(mipData);
        return memoryStream.ToArray();
    }

    private static TextureUtils.DDSHeader CreateHeader(Texture texture, int mipLevel, int mipCount)
    {
        int safeMipLevel = ClampMipLevel(texture, mipLevel);
        TextureUtils.DDSHeader header = new()
        {
            Height = GetMipDimension(texture.Height, safeMipLevel),
            Width = GetMipDimension(texture.Width, safeMipLevel),
            PitchOrLinearSize = (int)texture.MipSizes[safeMipLevel],
            MipMapCount = mipCount,
            Caps = TextureUtils.DDSCaps.Texture,
            HasExtendedHeader = true
        };

        if (mipCount > 1)
        {
            header.Flags |= TextureUtils.DDSFlags.MipMapCount;
            header.Caps |= TextureUtils.DDSCaps.MipMap | TextureUtils.DDSCaps.Complex;
        }

        header.PixelFormat.FourCC = 0x30315844;
        header.ExtendedHeader.DxgiFormat = TextureUtils.ToShaderFormat(texture.PixelFormat);

        switch (texture.Type)
        {
            case TextureType.TT_2d:
                header.ExtendedHeader.ResourceDimension = TextureUtils.ResourceDimension.Texture2D;
                header.ExtendedHeader.ArraySize = 1;
                break;
            case TextureType.TT_2dArray:
                header.ExtendedHeader.ResourceDimension = TextureUtils.ResourceDimension.Texture2D;
                header.ExtendedHeader.ArraySize = (uint)GetSliceCount(texture);
                break;
            case TextureType.TT_Cube:
                header.Caps2 = TextureUtils.DDSCaps2.CubeMap | TextureUtils.DDSCaps2.CubeMapAllFaces;
                header.ExtendedHeader.ResourceDimension = TextureUtils.ResourceDimension.Texture2D;
                header.ExtendedHeader.ArraySize = 1;
                header.ExtendedHeader.MiscFlag = 4;
                break;
            case TextureType.TT_3d:
                header.Flags |= TextureUtils.DDSFlags.Depth;
                header.Caps2 |= TextureUtils.DDSCaps2.Volume;
                header.Depth = GetMipDimension(texture.Depth, safeMipLevel);
                header.ExtendedHeader.ResourceDimension = TextureUtils.ResourceDimension.Texture3D;
                header.ExtendedHeader.ArraySize = 1;
                break;
            default:
                header.ExtendedHeader.ResourceDimension = TextureUtils.ResourceDimension.Texture2D;
                header.ExtendedHeader.ArraySize = 1;
                break;
        }

        return header;
    }

    private static byte[] ExtractMipSliceData(Texture texture, int mipLevel, int sliceLevel)
    {
        (int firstAvailableMip, int availableMipCount) = GetAvailableMipRange(texture);
        int safeMipLevel = Math.Clamp(mipLevel, firstAvailableMip, firstAvailableMip + availableMipCount - 1);
        int sliceCount = texture.Type switch
        {
            TextureType.TT_Cube => 6,
            TextureType.TT_2dArray => GetSliceCount(texture),
            _ => 1
        };

        long mipOffset = 0;
        for (int i = firstAvailableMip; i < safeMipLevel; i++)
        {
            mipOffset += GetMipStorageSize(texture, i, sliceCount);
        }

        int mipSize = (int)texture.MipSizes[safeMipLevel];
        if (mipSize <= 0)
        {
            throw new InvalidOperationException($"Texture mip {safeMipLevel} has an invalid size of {mipSize} bytes.");
        }

        if (sliceCount == 1)
        {
            EnsureSliceCopyFits(texture, mipOffset, mipSize, safeMipLevel, 0);
            byte[] buffer = new byte[mipSize];
            Buffer.BlockCopy(texture.Data, (int)mipOffset, buffer, 0, mipSize);
            return buffer;
        }

        int safeSliceLevel = Math.Clamp(sliceLevel, 0, Math.Max(0, sliceCount - 1));
        byte[] sliceBuffer = new byte[mipSize];
        long sliceOffset = mipOffset + ((long)mipSize * safeSliceLevel);
        EnsureSliceCopyFits(texture, sliceOffset, mipSize, safeMipLevel, safeSliceLevel);
        Buffer.BlockCopy(texture.Data, (int)sliceOffset, sliceBuffer, 0, mipSize);
        return sliceBuffer;
    }

    private static byte[] ReorderSlicesAndMips(Texture texture, byte[] buffer)
    {
        using MemoryStream srcStream = new(buffer);
        using MemoryStream dstStream = new(buffer.Length);

        int sliceCount = texture.Type == TextureType.TT_2dArray ? GetSliceCount(texture) : 6;
        uint[] mipOffsets = new uint[texture.MipCount];
        for (int i = 0; i < texture.MipCount - 1; i++)
        {
            mipOffsets[i + 1] = mipOffsets[i] + (uint)(texture.MipSizes[i] * sliceCount);
        }

        byte[] tmpBuffer = new byte[texture.MipSizes[0]];
        for (int slice = 0; slice < sliceCount; slice++)
        {
            for (int mip = 0; mip < texture.MipCount; mip++)
            {
                int mipSize = (int)texture.MipSizes[mip];
                srcStream.Read(tmpBuffer, 0, mipSize);
                dstStream.Position = mipOffsets[mip] + (mipSize * slice);
                dstStream.Write(tmpBuffer, 0, mipSize);
            }
        }

        return dstStream.ToArray();
    }

    private static byte[] ReorderForDdsExport(Texture texture)
    {
        using MemoryStream output = new(texture.Data.Length);

        int sliceCount = texture.Type switch
        {
            TextureType.TT_Cube => 6,
            TextureType.TT_2dArray => GetSliceCount(texture),
            _ => 1
        };

        uint[] mipOffsets = new uint[texture.MipCount];
        for (int i = 0; i < texture.MipCount - 1; i++)
        {
            mipOffsets[i + 1] = mipOffsets[i] + (texture.MipSizes[i] * (uint)sliceCount);
        }

        byte[] temp = new byte[texture.MipSizes[0]];
        for (int slice = 0; slice < sliceCount; slice++)
        {
            for (int mip = 0; mip < texture.MipCount; mip++)
            {
                int mipSize = (int)texture.MipSizes[mip];
                int sourceOffset = (int)mipOffsets[mip] + (mipSize * slice);
                Buffer.BlockCopy(texture.Data, sourceOffset, temp, 0, mipSize);
                output.Write(temp, 0, mipSize);
            }
        }

        return output.ToArray();
    }

    private static int GetMipDimension(int size, int mipLevel)
    {
        int value = size;
        for (int i = 0; i < mipLevel; i++)
        {
            value = Math.Max(1, value >> 1);
        }

        return value;
    }

    private static int ClampMipLevel(Texture texture, int mipLevel)
    {
        int maxMipIndex = Math.Max(0, GetDeclaredMipCount(texture) - 1);
        return Math.Clamp(mipLevel, 0, maxMipIndex);
    }

    internal static int GetPreviewFirstMip(Texture texture)
    {
        return GetAvailableMipRange(texture).FirstMip;
    }

    internal static int GetPreviewMipCount(Texture texture)
    {
        return GetAvailableMipRange(texture).MipCount;
    }

    private static int GetSliceCount(Texture texture)
    {
        if (texture.Type == TextureType.TT_Cube)
        {
            return 6;
        }

        if (texture.Type == TextureType.TT_2dArray || texture.Type == TextureType.TT_3d)
        {
            return Math.Max(1, (int)(texture.SliceCount > 0 ? texture.SliceCount : texture.Depth));
        }

        return 1;
    }

    private static (int FirstMip, int MipCount) GetAvailableMipRange(Texture texture)
    {
        int declaredMipCount = GetDeclaredMipCount(texture);
        if (declaredMipCount <= 0)
        {
            return (0, 1);
        }

        long dataLength = texture.Data?.LongLength ?? 0;
        if (dataLength <= 0)
        {
            return (0, 1);
        }

        int sliceCount = texture.Type switch
        {
            TextureType.TT_Cube => 6,
            TextureType.TT_2dArray => GetSliceCount(texture),
            _ => 1
        };

        int firstMip = 0;
        if (!DoesMipRangeFit(texture, 0, declaredMipCount, sliceCount, dataLength))
        {
            int preferredFirstMip = Math.Clamp((int)texture.FirstMip, 0, declaredMipCount - 1);
            firstMip = DoesMipRangeFit(texture, preferredFirstMip, declaredMipCount, sliceCount, dataLength)
                ? preferredFirstMip
                : FindFirstMipThatFits(texture, declaredMipCount, sliceCount, dataLength);
        }

        int mipCount = CountAvailableMips(texture, firstMip, declaredMipCount, sliceCount, dataLength);
        return (firstMip, Math.Max(1, mipCount));
    }

    private static int GetDeclaredMipCount(Texture texture)
    {
        int declaredMipCount = Math.Max(1, (int)texture.MipCount);
        int maxMipCount = Math.Min(declaredMipCount, texture.MipSizes?.Length ?? declaredMipCount);
        while (maxMipCount > 1 && texture.MipSizes[maxMipCount - 1] == 0)
        {
            maxMipCount--;
        }

        return Math.Max(1, maxMipCount);
    }

    private static int FindFirstMipThatFits(Texture texture, int declaredMipCount, int sliceCount, long dataLength)
    {
        for (int mip = 0; mip < declaredMipCount; mip++)
        {
            if (DoesMipRangeFit(texture, mip, declaredMipCount, sliceCount, dataLength))
            {
                return mip;
            }
        }

        for (int mip = 0; mip < declaredMipCount; mip++)
        {
            if (GetMipStorageSize(texture, mip, sliceCount) <= dataLength)
            {
                return mip;
            }
        }

        return Math.Max(0, declaredMipCount - 1);
    }

    private static int CountAvailableMips(Texture texture, int firstMip, int declaredMipCount, int sliceCount, long dataLength)
    {
        long bytesConsumed = 0;
        int mipCount = 0;
        for (int mip = firstMip; mip < declaredMipCount; mip++)
        {
            long mipSize = GetMipStorageSize(texture, mip, sliceCount);
            if (mipSize <= 0 || bytesConsumed + mipSize > dataLength)
            {
                break;
            }

            bytesConsumed += mipSize;
            mipCount++;
        }

        return mipCount;
    }

    private static bool DoesMipRangeFit(Texture texture, int firstMip, int declaredMipCount, int sliceCount, long dataLength)
    {
        long requiredBytes = 0;
        for (int mip = firstMip; mip < declaredMipCount; mip++)
        {
            long mipSize = GetMipStorageSize(texture, mip, sliceCount);
            if (mipSize <= 0)
            {
                return false;
            }

            requiredBytes += mipSize;
            if (requiredBytes > dataLength)
            {
                return false;
            }
        }

        return requiredBytes > 0;
    }

    private static long GetMipStorageSize(Texture texture, int mipLevel, int sliceCount)
    {
        return (long)texture.MipSizes[mipLevel] * Math.Max(1, sliceCount);
    }

    private static void EnsureSliceCopyFits(Texture texture, long sourceOffset, int mipSize, int mipLevel, int sliceLevel)
    {
        long dataLength = texture.Data?.LongLength ?? 0;
        if (sourceOffset < 0 || mipSize < 0 || sourceOffset + mipSize > dataLength)
        {
            throw new InvalidOperationException(
                $"Texture data does not contain mip {mipLevel} slice {sliceLevel}. " +
                $"DataLength={dataLength}, SourceOffset={sourceOffset}, MipSize={mipSize}, FirstMip={texture.FirstMip}, DeclaredMipCount={texture.MipCount}.");
        }
    }

    private static TextureImageFormat GetImageFormat(string fileName)
    {
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".png" => TextureImageFormat.PNG,
            ".tga" => TextureImageFormat.TGA,
            ".hdr" => TextureImageFormat.HDR,
            _ => TextureImageFormat.DDS
        };
    }

    private static byte[] ApplyChannels(byte[] pngData, TextureChannelMask channels, bool luminance)
    {
        using Image<Rgba32> image = Image.Load<Rgba32>(pngData);

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                Span<Rgba32> row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    Rgba32 pixel = row[x];
                    if (luminance)
                    {
                        byte yValue = (byte)Math.Clamp((pixel.R * 0.2126f) + (pixel.G * 0.7152f) + (pixel.B * 0.0722f), 0, 255);
                        row[x] = new Rgba32(yValue, yValue, yValue, 255);
                        continue;
                    }

                    bool red = channels.HasFlag(TextureChannelMask.Red);
                    bool green = channels.HasFlag(TextureChannelMask.Green);
                    bool blue = channels.HasFlag(TextureChannelMask.Blue);
                    bool alpha = channels.HasFlag(TextureChannelMask.Alpha);

                    if (!red && !green && !blue && alpha)
                    {
                        row[x] = new Rgba32(pixel.A, pixel.A, pixel.A, 255);
                        continue;
                    }

                    row[x] = new Rgba32(
                        red ? pixel.R : (byte)0,
                        green ? pixel.G : (byte)0,
                        blue ? pixel.B : (byte)0,
                        alpha ? pixel.A : (byte)255);
                }
            }
        });

        using MemoryStream output = new();
        image.Save(output, new PngEncoder());
        return output.ToArray();
    }

    private static bool TryGetPropertyValue(object instance, string propertyName, out object? value)
    {
        PropertyInfo? property = instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (property is null || !property.CanRead || property.GetIndexParameters().Length != 0)
        {
            value = null;
            return false;
        }

        value = property.GetValue(instance);
        return true;
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

        foreach (string propertyName in new[] { "ResourceId", "Value" })
        {
            PropertyInfo? property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (property is not null && property.CanRead && TryExtractResourceId(property.GetValue(resourceValue), out rid))
            {
                return true;
            }
        }

        foreach (string fieldName in new[] { "m_resourceId", "_resourceId", "m_value", "_value" })
        {
            FieldInfo? field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field is not null && TryExtractResourceId(field.GetValue(resourceValue), out rid))
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

        if (ulong.TryParse(resourceValue.ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rid))
        {
            return true;
        }

        return ulong.TryParse(resourceValue.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out rid);
    }

    private static bool TryLoad(EbxAssetEntry entry, out TextureAssetLoadResult? load)
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
}

