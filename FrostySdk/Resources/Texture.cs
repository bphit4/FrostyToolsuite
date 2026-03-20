using System;
using System.IO;
using System.Text;
using Frosty.Sdk.Interfaces;
using Frosty.Sdk.IO;
using Frosty.Sdk.Managers;
using Frosty.Sdk.Managers.Entries;
using Frosty.Sdk.Profiles;

namespace Frosty.Sdk.Resources;

public enum TextureType
{
    TT_2d = 0x0,
    TT_Cube = 0x1,
    TT_2dArray = 0x3,
    TT_1dArray = 0x4,
    TT_1d = 0x5,
    TT_CubeArray = 0x6,
    TT_3d = 0xF2
}

[Flags]
public enum TextureFlags
{
    None = 0,
    Streaming = 1 << 0,
    SrgbGamma = 1 << 1,
    CpuResource = 1 << 2,
    OnDemandLoaded = 1 << 3,
    Mutable = 1 << 4,
    NoSkipmip = 1 << 5,
    XenonPackedMipmaps = 1 << 8,
    Ps3MemoryCell = 1 << 8,
    Ps3MemoryRsx = 1 << 9,
    StreamingAlways = 1 << 10,
    SwizzledData = 1 << 11
}

public sealed class Texture : Resource
{
    public uint FirstMipOffset
    {
        get => m_compressedMipOffsets[0];
        set => m_compressedMipOffsets[0] = value;
    }

    public uint SecondMipOffset
    {
        get => m_compressedMipOffsets[1];
        set => m_compressedMipOffsets[1] = value;
    }

    public string PixelFormat
    {
        get
        {
            string enumType = "RenderFormat";
            IType? type = TypeLibrary.GetType(enumType);
            if (type is null)
            {
                return "Unknown";
            }

            string name = Enum.Parse(type.Type, m_pixelFormat.ToString()).ToString();
            return name.Replace(enumType + "_", "", StringComparison.Ordinal);
        }
    }

    public TextureType Type { get; private set; }
    public TextureFlags Flags { get; set; }
    public ushort Width { get; private set; }
    public ushort Height { get; private set; }

    public ushort SliceCount
    {
        get => m_sliceCount;
        set
        {
            m_sliceCount = value;
            if (Type == TextureType.TT_2dArray || Type == TextureType.TT_3d)
            {
                Depth = m_sliceCount;
            }
        }
    }

    public ushort Depth { get; private set; }
    public byte MipCount { get; private set; }
    public byte FirstMip { get; set; }
    public uint[] MipSizes { get; } = new uint[15];
    public string TextureGroup { get; set; } = string.Empty;
    public uint AssetNameHash { get; set; }
    public byte[] Data { get; private set; } = Array.Empty<byte>();
    public uint LogicalOffset { get; set; }
    public uint LogicalSize { get; set; }
    public uint RangeStart { get; set; }
    public uint RangeEnd { get; set; }
    public uint[] Unknown3 { get; } = new uint[4];

    public Guid ChunkId
    {
        get => m_chunkId;
        set => m_chunkId = value;
    }

    public uint ChunkSize { get; private set; }
    public uint Version => m_version;
    public byte[] ResourceMeta { get; private set; } = Array.Empty<byte>();

    private uint m_version;
    private readonly uint[] m_compressedMipOffsets = new uint[2];
    private int m_pixelFormat;
    private uint m_customPoolId;
    private ushort m_sliceCount;
    private Guid m_chunkId;

    public Texture()
    {
    }

    public Texture(TextureType inType, string inFormat, ushort inWidth, ushort inHeight, ushort inDepth = 1, uint inVersion = 12, byte[]? inResMeta = null)
    {
        Type = inType;
        m_pixelFormat = GetTextureFormat(inFormat);
        Width = inWidth;
        Height = inHeight;
        Depth = inDepth;
        m_sliceCount = inDepth;
        m_customPoolId = 0;
        Flags = TextureFlags.None;
        Unknown3[0] = 0xFFFFFFFF;
        Unknown3[1] = 0xFFFFFFFF;
        Unknown3[2] = 0xFFFFFFFF;
        Unknown3[3] = 0xFFFFFFFF;
        m_version = inVersion;
        ResourceMeta = inResMeta is null ? new byte[16] : (byte[])inResMeta.Clone();
    }

    public override void Deserialize(DataStream inStream, ReadOnlySpan<byte> inResMeta)
    {
        ResourceMeta = inResMeta.ToArray();
        m_version = inResMeta.Length >= 4 ? BitConverter.ToUInt32(inResMeta[..4]) : 0;

        if (m_version == 0)
        {
            m_version = inStream.ReadUInt32();
        }

        if (m_version >= 11)
        {
            m_compressedMipOffsets[0] = inStream.ReadUInt32();
            m_compressedMipOffsets[1] = inStream.ReadUInt32();
        }

        Type = (TextureType)inStream.ReadUInt32();
        m_pixelFormat = inStream.ReadInt32();

        if (m_version >= 12)
        {
            m_customPoolId = inStream.ReadUInt32();
        }

        Flags = (TextureFlags)(m_version >= 11 ? inStream.ReadUInt16() : inStream.ReadUInt32());

        Width = inStream.ReadUInt16();
        Height = inStream.ReadUInt16();
        Depth = inStream.ReadUInt16();
        m_sliceCount = inStream.ReadUInt16();

        if (m_version <= 10)
        {
            Unknown3[0] = inStream.ReadUInt16();
        }

        MipCount = inStream.ReadByte();
        FirstMip = inStream.ReadByte();

        if (ProfilesLibrary.IsLoaded(ProfileVersion.Madden25, ProfileVersion.Madden26))
        {
            Unknown3[0] = inStream.ReadUInt32();
            inStream.Position += 4;
        }

        m_chunkId = inStream.ReadGuid();

        for (int i = 0; i < 15; i++)
        {
            MipSizes[i] = inStream.ReadUInt32();
        }

        ChunkSize = inStream.ReadUInt32();

        if (m_version >= 13)
        {
            for (int i = 0; i < 4; i++)
            {
                Unknown3[i] = inStream.ReadUInt32();
            }
        }

        if (ProfilesLibrary.IsLoaded(ProfileVersion.Fifa18, ProfileVersion.Madden19))
        {
            Unknown3[0] = inStream.ReadUInt32();
        }

        AssetNameHash = inStream.ReadUInt32();

        if (ProfilesLibrary.IsLoaded(ProfileVersion.Madden23))
        {
            Unknown3[0] = inStream.ReadUInt32();
        }

        if (ProfilesLibrary.IsLoaded(ProfileVersion.Madden25, ProfileVersion.Madden26))
        {
            inStream.Position += 4;
            TextureGroup = inStream.ReadFixedSizedString(36);
        }
        else
        {
            TextureGroup = inStream.ReadFixedSizedString(16);
        }
    }

    public override void Serialize(DataStream inStream, Span<byte> resMeta)
    {
        if (m_version <= 10)
        {
            inStream.WriteUInt32(m_version);
        }
        else
        {
            inStream.WriteUInt32(m_compressedMipOffsets[0]);
            inStream.WriteUInt32(m_compressedMipOffsets[1]);
        }

        inStream.WriteUInt32((uint)Type);
        inStream.WriteInt32(m_pixelFormat);

        if (m_version >= 12)
        {
            inStream.WriteUInt32(m_customPoolId);
        }

        if (m_version <= 10)
        {
            inStream.WriteUInt32((uint)Flags);
        }
        else
        {
            inStream.WriteUInt16((ushort)Flags);
        }

        inStream.WriteUInt16(Width);
        inStream.WriteUInt16(Height);
        inStream.WriteUInt16(Depth);
        inStream.WriteUInt16(m_sliceCount);

        if (m_version <= 10)
        {
            inStream.WriteUInt16((ushort)Unknown3[0]);
        }

        inStream.WriteByte(MipCount);
        inStream.WriteByte(FirstMip);

        if (ProfilesLibrary.IsLoaded(ProfileVersion.Madden25, ProfileVersion.Madden26))
        {
            inStream.WriteUInt32(Unknown3[0]);
            inStream.WriteUInt32(0);
        }

        inStream.WriteGuid(m_chunkId);

        for (int i = 0; i < 15; i++)
        {
            inStream.WriteUInt32(MipSizes[i]);
        }

        inStream.WriteUInt32(ChunkSize);

        if (m_version >= 13)
        {
            for (int i = 0; i < 4; i++)
            {
                inStream.WriteUInt32(Unknown3[i]);
            }
        }

        if (ProfilesLibrary.IsLoaded(ProfileVersion.Fifa18, ProfileVersion.Madden19))
        {
            inStream.WriteUInt32(Unknown3[0]);
        }

        inStream.WriteUInt32(AssetNameHash);

        if (ProfilesLibrary.IsLoaded(ProfileVersion.Madden23))
        {
            inStream.WriteUInt32(Unknown3[0]);
        }

        if (ProfilesLibrary.IsLoaded(ProfileVersion.Madden25, ProfileVersion.Madden26))
        {
            inStream.WriteUInt32(0);
            inStream.WriteFixedSizedString(FitFixedString(TextureGroup, 36), 36);
        }
        else
        {
            inStream.WriteFixedSizedString(FitFixedString(TextureGroup, 16), 16);
        }

        if (m_version >= 11 && resMeta.Length >= 8)
        {
            BitConverter.TryWriteBytes(resMeta[..4], m_version);
            BitConverter.TryWriteBytes(resMeta.Slice(4, 4), (uint)Flags);
        }
    }

    public void SetData(byte[] inData)
    {
        Data = inData;
        ChunkSize = (uint)Data.Length;
    }

    public void SetData(Guid newChunkId, byte[] inData)
    {
        m_chunkId = newChunkId;
        SetData(inData);
    }

    public void SetData(Guid newChunkId)
    {
        if (AssetManager.GetChunkAssetEntry(newChunkId) is ChunkAssetEntry chunkEntry)
        {
            using Frosty.Sdk.Utils.Block<byte> data = AssetManager.GetAsset(chunkEntry);
            SetData(newChunkId, data.ToArray());
        }
    }

    public void CalculateMipData(byte inMipCount, int blockSize, bool isCompressed, uint dataSize)
    {
        if (isCompressed)
        {
            blockSize /= 4;
        }

        MipCount = inMipCount;

        int currentWidth = Width;
        int currentHeight = Height;
        int currentDepth = Depth;
        int minSize = isCompressed ? 4 : 1;

        for (int i = 0; i < MipCount; i++)
        {
            int pitch = isCompressed
                ? Math.Max(1, (currentWidth + 3) / 4) * blockSize
                : (currentWidth * blockSize + 7) / 8;

            MipSizes[i] = (uint)(pitch * currentHeight);
            if (Type == TextureType.TT_3d)
            {
                MipSizes[i] *= (uint)currentDepth;
            }

            currentWidth >>= 1;
            currentHeight >>= 1;
            currentDepth = Math.Max(1, currentDepth >> 1);
            currentHeight = currentHeight < minSize ? minSize : currentHeight;
            currentWidth = currentWidth < minSize ? minSize : currentWidth;
        }

        if (MipCount == 1)
        {
            LogicalOffset = 0;
            LogicalSize = dataSize;
            Flags &= ~TextureFlags.Streaming;
        }
        else
        {
            LogicalSize = 0;
            for (int i = 0; i < MipCount - FirstMip; i++)
            {
                LogicalSize |= (uint)(0x03 << (i * 2));
            }

            LogicalOffset = dataSize & ~LogicalSize;
            LogicalSize = dataSize & LogicalSize;
        }
    }

    private static int GetTextureFormat(string format)
    {
        const string enumType = "RenderFormat";
        IType? type = TypeLibrary.GetType(enumType);
        if (type is null)
        {
            throw new InvalidOperationException("RenderFormat enum was not found in the loaded SDK.");
        }

        return (int)Enum.Parse(type.Type, enumType + "_" + format);
    }

    private static string FitFixedString(string value, int size)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string current = value;
        while (current.Length > 0 && Encoding.UTF8.GetByteCount(current) >= size)
        {
            current = current[..^1];
        }

        return current;
    }
}
