using System;
using Frosty.Sdk.IO;
using Frosty.Sdk.Resources;

namespace FrostyEditor.Utils;

public static class TextureUtils
{
    public enum DxgiFormat : uint
    {
        Unknown = 0,
        R32G32B32A32_Float = 2,
        R16G16B16A16_Float = 10,
        R16G16B16A16_UNorm = 11,
        R10G10B10A2_UNorm = 24,
        R8G8B8A8_UNorm = 28,
        R8G8B8A8_UNorm_SRgb = 29,
        R16G16_UNorm = 35,
        R16_UNorm = 56,
        R8_UNorm = 61,
        R9G9B9E5_Sharedexp = 67,
        B8G8R8A8_UNorm = 87,
        BC1_UNorm = 71,
        BC1_UNorm_SRgb = 72,
        BC2_UNorm = 74,
        BC2_UNorm_SRgb = 75,
        BC3_UNorm = 77,
        BC3_UNorm_SRgb = 78,
        BC4_UNorm = 80,
        BC5_UNorm = 83,
        BC6H_Uf16 = 95,
        BC7_UNorm = 98,
        BC7_UNorm_SRgb = 99
    }

    public enum ResourceDimension : uint
    {
        Unknown = 0,
        Buffer = 1,
        Texture1D = 2,
        Texture2D = 3,
        Texture3D = 4
    }

    [Flags]
    public enum DDSCaps
    {
        Complex = 0x08,
        Texture = 0x1000,
        MipMap = 0x400000
    }

    [Flags]
    public enum DDSCaps2
    {
        CubeMap = 0x200,
        CubeMapPositiveX = 0x400,
        CubeMapNegativeX = 0x800,
        CubeMapPositiveY = 0x1000,
        CubeMapNegativeY = 0x2000,
        CubeMapPositiveZ = 0x4000,
        CubeMapNegativeZ = 0x8000,
        Volume = 0x200000,
        CubeMapAllFaces = CubeMapPositiveX | CubeMapPositiveY | CubeMapPositiveZ | CubeMapNegativeX | CubeMapNegativeY | CubeMapNegativeZ
    }

    [Flags]
    public enum DDSFlags
    {
        Caps = 0x01,
        Height = 0x02,
        Width = 0x04,
        Pitch = 0x08,
        PixelFormat = 0x1000,
        MipMapCount = 0x20000,
        LinearSize = 0x80000,
        Depth = 0x800000,
        Required = Caps | Height | Width | PixelFormat
    }

    [Flags]
    public enum DDSPFFlags
    {
        AlphaPixels = 0x01,
        Alpha = 0x02,
        FourCC = 0x04,
        RGB = 0x40,
        YUV = 0x200,
        Luminance = 0x20000
    }

    public struct DDSHeaderDX10
    {
        public DxgiFormat DxgiFormat;
        public ResourceDimension ResourceDimension;
        public uint MiscFlag;
        public uint ArraySize;
        public uint MiscFlags2;
    }

    public struct DDSPixelFormat
    {
        public int Size;
        public DDSPFFlags Flags;
        public int FourCC;
        public int RGBBitCount;
        public uint RBitMask;
        public uint GBitMask;
        public uint BBitMask;
        public uint ABitMask;
    }

    public sealed class DDSHeader
    {
        public int Magic = 0x20534444;
        public int Size = 0x7C;
        public DDSFlags Flags = DDSFlags.Required;
        public int Height;
        public int Width;
        public int PitchOrLinearSize;
        public int Depth;
        public int MipMapCount;
        public int[] Reserved1 = new int[11];
        public DDSPixelFormat PixelFormat = new() { Size = 0x20, Flags = DDSPFFlags.FourCC };
        public DDSCaps Caps = DDSCaps.Texture;
        public DDSCaps2 Caps2;
        public int Caps3;
        public int Caps4;
        public int Reserved2;
        public bool HasExtendedHeader;
        public DDSHeaderDX10 ExtendedHeader;

        public void Write(DataStream writer)
        {
            writer.WriteInt32(Magic);
            writer.WriteInt32(Size);
            writer.WriteInt32((int)Flags);
            writer.WriteInt32(Height);
            writer.WriteInt32(Width);
            writer.WriteInt32(PitchOrLinearSize);
            writer.WriteInt32(Depth);
            writer.WriteInt32(MipMapCount);
            for (int i = 0; i < 11; i++)
            {
                writer.WriteInt32(Reserved1[i]);
            }

            writer.WriteInt32(PixelFormat.Size);
            writer.WriteInt32((int)PixelFormat.Flags);
            writer.WriteInt32(PixelFormat.FourCC);
            writer.WriteInt32(PixelFormat.RGBBitCount);
            writer.WriteUInt32(PixelFormat.RBitMask);
            writer.WriteUInt32(PixelFormat.GBitMask);
            writer.WriteUInt32(PixelFormat.BBitMask);
            writer.WriteUInt32(PixelFormat.ABitMask);
            writer.WriteInt32((int)Caps);
            writer.WriteInt32((int)Caps2);
            writer.WriteInt32(Caps3);
            writer.WriteInt32(Caps4);
            writer.WriteInt32(Reserved2);

            if (HasExtendedHeader)
            {
                writer.WriteUInt32((uint)ExtendedHeader.DxgiFormat);
                writer.WriteUInt32((uint)ExtendedHeader.ResourceDimension);
                writer.WriteUInt32(ExtendedHeader.MiscFlag);
                writer.WriteUInt32(ExtendedHeader.ArraySize);
                writer.WriteUInt32(ExtendedHeader.MiscFlags2);
            }
        }

        public bool Read(DataStream reader)
        {
            Magic = reader.ReadInt32();
            if (Magic != 0x20534444)
            {
                return false;
            }

            Size = reader.ReadInt32();
            if (Size != 0x7C)
            {
                return false;
            }

            Flags = (DDSFlags)reader.ReadInt32();
            Height = reader.ReadInt32();
            Width = reader.ReadInt32();
            PitchOrLinearSize = reader.ReadInt32();
            Depth = reader.ReadInt32();
            MipMapCount = reader.ReadInt32();
            for (int i = 0; i < 11; i++)
            {
                Reserved1[i] = reader.ReadInt32();
            }

            PixelFormat.Size = reader.ReadInt32();
            PixelFormat.Flags = (DDSPFFlags)reader.ReadInt32();
            PixelFormat.FourCC = reader.ReadInt32();
            PixelFormat.RGBBitCount = reader.ReadInt32();
            PixelFormat.RBitMask = reader.ReadUInt32();
            PixelFormat.GBitMask = reader.ReadUInt32();
            PixelFormat.BBitMask = reader.ReadUInt32();
            PixelFormat.ABitMask = reader.ReadUInt32();
            Caps = (DDSCaps)reader.ReadInt32();
            Caps2 = (DDSCaps2)reader.ReadInt32();
            Caps3 = reader.ReadInt32();
            Caps4 = reader.ReadInt32();
            Reserved2 = reader.ReadInt32();

            HasExtendedHeader = PixelFormat.FourCC == 0x30315844;
            if (HasExtendedHeader)
            {
                ExtendedHeader.DxgiFormat = (DxgiFormat)reader.ReadUInt32();
                ExtendedHeader.ResourceDimension = (ResourceDimension)reader.ReadUInt32();
                ExtendedHeader.MiscFlag = reader.ReadUInt32();
                ExtendedHeader.ArraySize = reader.ReadUInt32();
                ExtendedHeader.MiscFlags2 = reader.ReadUInt32();
            }

            return true;
        }
    }

    public static bool IsCompressedFormat(string pixelFormat)
    {
        return pixelFormat is not ("R8_UNORM" or "R16G16B16A16_FLOAT" or "R16G16B16A16_UNORM" or
            "R32G32B32A32_FLOAT" or "R9G9B9E5_FLOAT" or "R8G8B8A8_UNORM" or "R8G8B8A8_SRGB" or
            "B8G8R8A8_UNORM" or "R10G10B10A2_UNORM" or "ARGB32F" or "R9G9B9E5F" or "L8" or "L16" or
            "ARGB8888" or "D16_UNORM" or "R16G16_UNORM");
    }

    public static int GetFormatBlockSize(string pixelFormat)
    {
        return pixelFormat switch
        {
            "L8" => 8,
            "BC3_UNORM" or "BC3_SRGB" or "BC5_UNORM" or "BC5_SRGB" or "BC6U_FLOAT" or "BC7_UNORM" or
                "BC7_SRGB" or "NormalDXN" or "BC2_UNORM" or "BC3A_UNORM" or "L16" or "D16_UNORM" or
                "R16G16_UNORM" => 16,
            "R9G9B9E5_FLOAT" or "R8G8B8A8_UNORM" or "R8G8B8A8_SRGB" or "B8G8R8A8_UNORM" or
                "R10G10B10A2_UNORM" or "R9G9B9E5F" or "ARGB8888" => 32,
            "R16G16B16A16_FLOAT" or "R16G16B16A16_UNORM" => 64,
            "R32G32B32A32_FLOAT" or "ARGB32F" => 128,
            _ => 8
        };
    }

    public static DxgiFormat ToShaderFormat(string pixelFormat)
    {
        return pixelFormat switch
        {
            "NormalDXT1" or "BC1A_UNORM" or "BC1_UNORM" => DxgiFormat.BC1_UNorm,
            "BC1A_SRGB" or "BC1_SRGB" => DxgiFormat.BC1_UNorm_SRgb,
            "BC2_UNORM" => DxgiFormat.BC2_UNorm,
            "BC2_SRGB" => DxgiFormat.BC2_UNorm_SRgb,
            "BC3_UNORM" or "BC3A_UNORM" => DxgiFormat.BC3_UNorm,
            "BC3_SRGB" or "BC3A_SRGB" => DxgiFormat.BC3_UNorm_SRgb,
            "BC4_UNORM" => DxgiFormat.BC4_UNorm,
            "NormalDXN" or "BC5_UNORM" => DxgiFormat.BC5_UNorm,
            "BC6U_FLOAT" => DxgiFormat.BC6H_Uf16,
            "BC7" or "BC7_UNORM" => DxgiFormat.BC7_UNorm,
            "BC7_SRGB" => DxgiFormat.BC7_UNorm_SRgb,
            "R8_UNORM" or "L8" => DxgiFormat.R8_UNorm,
            "R16G16B16A16_FLOAT" => DxgiFormat.R16G16B16A16_Float,
            "R16G16B16A16_UNORM" => DxgiFormat.R16G16B16A16_UNorm,
            "ARGB32F" or "R32G32B32A32_FLOAT" => DxgiFormat.R32G32B32A32_Float,
            "R9G9B9E5F" or "R9G9B9E5_FLOAT" => DxgiFormat.R9G9B9E5_Sharedexp,
            "R8G8B8A8_UNORM" or "ARGB8888" => DxgiFormat.R8G8B8A8_UNorm,
            "R8G8B8A8_SRGB" => DxgiFormat.R8G8B8A8_UNorm_SRgb,
            "B8G8R8A8_UNORM" => DxgiFormat.B8G8R8A8_UNorm,
            "R10G10B10A2_UNORM" => DxgiFormat.R10G10B10A2_UNorm,
            "L16" or "D16_UNORM" => DxgiFormat.R16_UNorm,
            "R16G16_UNORM" => DxgiFormat.R16G16_UNorm,
            _ => DxgiFormat.Unknown
        };
    }

    public static bool TryGetPixelFormat(DDSHeader header, string originalPixelFormat, out string pixelFormat, out TextureFlags flags)
    {
        pixelFormat = "Unknown";
        flags = TextureFlags.None;

        if (header.PixelFormat.FourCC == 0)
        {
            if (header.PixelFormat.RBitMask == 0x000000FF &&
                header.PixelFormat.GBitMask == 0x0000FF00 &&
                header.PixelFormat.BBitMask == 0x00FF0000 &&
                header.PixelFormat.ABitMask == 0xFF000000)
            {
                pixelFormat = "R8G8B8A8_UNORM";
                return true;
            }
        }
        else if (header.PixelFormat.FourCC == 0x31545844)
        {
            pixelFormat = originalPixelFormat == "BC1A_UNORM" ? "BC1A_UNORM" : "BC1_UNORM";
            return true;
        }
        else if (header.PixelFormat.FourCC == 0x35545844)
        {
            pixelFormat = "BC3_UNORM";
            return true;
        }
        else if (header.PixelFormat.FourCC == 0x31495441)
        {
            pixelFormat = "BC4_UNORM";
            return true;
        }
        else if (header.PixelFormat.FourCC == 0x32495441 || header.PixelFormat.FourCC == 0x55354342)
        {
            pixelFormat = "BC5_UNORM";
            return true;
        }

        if (!header.HasExtendedHeader)
        {
            return pixelFormat != "Unknown";
        }

        pixelFormat = header.ExtendedHeader.DxgiFormat switch
        {
            DxgiFormat.BC1_UNorm when originalPixelFormat == "BC1A_UNORM" => "BC1A_UNORM",
            DxgiFormat.BC1_UNorm => "BC1_UNORM",
            DxgiFormat.BC1_UNorm_SRgb when originalPixelFormat == "BC1A_SRGB" => "BC1A_SRGB",
            DxgiFormat.BC1_UNorm_SRgb => "BC1_SRGB",
            DxgiFormat.BC2_UNorm => "BC2_UNORM",
            DxgiFormat.BC2_UNorm_SRgb => "BC2_SRGB",
            DxgiFormat.BC3_UNorm => "BC3_UNORM",
            DxgiFormat.BC3_UNorm_SRgb => "BC3_SRGB",
            DxgiFormat.BC4_UNorm => "BC4_UNORM",
            DxgiFormat.BC5_UNorm => "BC5_UNORM",
            DxgiFormat.BC6H_Uf16 => "BC6U_FLOAT",
            DxgiFormat.BC7_UNorm => "BC7_UNORM",
            DxgiFormat.BC7_UNorm_SRgb => "BC7_SRGB",
            DxgiFormat.R8_UNorm => "R8_UNORM",
            DxgiFormat.R16G16B16A16_Float => "R16G16B16A16_FLOAT",
            DxgiFormat.R16G16B16A16_UNorm => "R16G16B16A16_UNORM",
            DxgiFormat.R32G32B32A32_Float => "R32G32B32A32_FLOAT",
            DxgiFormat.R9G9B9E5_Sharedexp => "R9G9B9E5_FLOAT",
            DxgiFormat.R8G8B8A8_UNorm => "R8G8B8A8_UNORM",
            DxgiFormat.R8G8B8A8_UNorm_SRgb => "R8G8B8A8_SRGB",
            DxgiFormat.R10G10B10A2_UNorm => "R10G10B10A2_UNORM",
            DxgiFormat.R16_UNorm when originalPixelFormat == "D16_UNORM" => "D16_UNORM",
            DxgiFormat.R16_UNorm => "L16",
            DxgiFormat.R16G16_UNorm => "R16G16_UNORM",
            _ => "Unknown"
        };

        if (pixelFormat.Contains("SRGB", StringComparison.OrdinalIgnoreCase))
        {
            flags |= TextureFlags.SrgbGamma;
        }

        return pixelFormat != "Unknown";
    }
}
