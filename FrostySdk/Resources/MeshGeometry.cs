namespace Frosty.Sdk.Resources;

public enum VertexElementUsage : byte
{
    Unknown = 0x00,
    Pos = 0x01,
    BoneIndices = 0x02,
    BoneIndices2 = 0x03,
    BoneWeights = 0x04,
    BoneWeights2 = 0x05,
    Normal = 0x06,
    Tangent = 0x07,
    Binormal = 0x08,
    BinormalSign = 0x09,
    Color0 = 0x1E,
    Color1 = 0x1F,
    TexCoord0 = 0x21,
    TexCoord1 = 0x22,
    TexCoord2 = 0x23,
    TexCoord3 = 0x24,
    TexCoord4 = 0x25,
    TexCoord5 = 0x26,
    TexCoord6 = 0x27,
    TexCoord7 = 0x28,
    DisplacementMapTexCoord = 0x29,
    RadiosityTexCoord = 0x2A,
    SubMaterialIndex = 0x33,
    TangentSpace = 0x34,
    RegionIds = 0x64,
    BlendWeights = 0x65,
    MaskUv = 0xBC,
    Delta = 0xD0
}

public enum VertexElementFormat : byte
{
    None = 0x00,
    Float = 0x01,
    Float2 = 0x02,
    Float3 = 0x03,
    Float4 = 0x04,
    Half = 0x05,
    Half2 = 0x06,
    Half3 = 0x07,
    Half4 = 0x08,
    Byte4 = 0x0A,
    Byte4N = 0x0B,
    UByte4 = 0x0C,
    UByte4N = 0x0D,
    Short = 0x0E,
    Short2 = 0x0F,
    Short3 = 0x10,
    Short4 = 0x11,
    ShortN = 0x12,
    Short2N = 0x13,
    Short3N = 0x14,
    Short4N = 0x15,
    UShort2 = 0x16,
    UShort4 = 0x17,
    UShort2N = 0x18,
    UShort4N = 0x19,
    Int = 0x1A,
    Int2 = 0x1B,
    Int4 = 0x1C,
    IntN = 0x1D,
    Int2N = 0x1E,
    Int4N = 0x1F,
    UInt = 0x20,
    UInt2 = 0x21,
    UInt4 = 0x22,
    UIntN = 0x23,
    UInt2N = 0x24,
    UInt4N = 0x25,
    Comp3_10_10_10 = 0x26,
    Comp3N_10_10_10 = 0x27,
    UComp3_10_10_10 = 0x28,
    UComp3N_10_10_10 = 0x29,
    Comp3_11_11_10 = 0x2A,
    Comp3N_11_11_10 = 0x2B,
    UComp3_11_11_10 = 0x2C,
    UComp3N_11_11_10 = 0x2D,
    Comp4_10_10_10_2 = 0x2E,
    Comp4N_10_10_10_2 = 0x2F,
    UComp4_10_10_10_2 = 0x30,
    UComp4N_10_10_10_2 = 0x31,
    UByteN = 0x32,
    Int3 = 0x33,
    UInt3 = 0x34
}

public enum VertexElementClassification : byte
{
    PerVertex = 0,
    PerInstance = 1
}

public readonly record struct GeometryDeclarationDesc
{
    public const int MaxElements = 16;
    public const int MaxStreams = 16;

    public readonly record struct Element(VertexElementUsage Usage, VertexElementFormat Format, byte Offset, byte StreamIndex)
    {
        public int Size => MeshGeometry.GetElementSize(Format);
    }

    public readonly record struct Stream(byte VertexStride, VertexElementClassification Classification);

    public required Element[] Elements { get; init; }
    public required Stream[] Streams { get; init; }
    public required byte ElementCount { get; init; }
    public required byte StreamCount { get; init; }
}

public static class MeshGeometry
{
    public static int GetElementSize(VertexElementFormat format)
    {
        return format switch
        {
            VertexElementFormat.None => 0,
            VertexElementFormat.Float => 4,
            VertexElementFormat.Float2 => 8,
            VertexElementFormat.Float3 => 12,
            VertexElementFormat.Float4 => 16,
            VertexElementFormat.Half => 2,
            VertexElementFormat.Half2 => 4,
            VertexElementFormat.Half3 => 6,
            VertexElementFormat.Half4 => 8,
            VertexElementFormat.Byte4 => 4,
            VertexElementFormat.Byte4N => 4,
            VertexElementFormat.UByte4 => 4,
            VertexElementFormat.UByte4N => 4,
            VertexElementFormat.Short => 2,
            VertexElementFormat.Short2 => 4,
            VertexElementFormat.Short3 => 6,
            VertexElementFormat.Short4 => 8,
            VertexElementFormat.ShortN => 2,
            VertexElementFormat.Short2N => 4,
            VertexElementFormat.Short3N => 6,
            VertexElementFormat.Short4N => 8,
            VertexElementFormat.UShort2 => 4,
            VertexElementFormat.UShort4 => 8,
            VertexElementFormat.UShort2N => 4,
            VertexElementFormat.UShort4N => 8,
            VertexElementFormat.Int => 4,
            VertexElementFormat.Int2 => 8,
            VertexElementFormat.Int3 => 12,
            VertexElementFormat.Int4 => 16,
            VertexElementFormat.IntN => 4,
            VertexElementFormat.Int2N => 8,
            VertexElementFormat.Int4N => 16,
            VertexElementFormat.UInt => 4,
            VertexElementFormat.UInt2 => 8,
            VertexElementFormat.UInt3 => 12,
            VertexElementFormat.UInt4 => 16,
            VertexElementFormat.UIntN => 4,
            VertexElementFormat.UInt2N => 8,
            VertexElementFormat.UInt4N => 16,
            VertexElementFormat.Comp3_10_10_10 => 4,
            VertexElementFormat.Comp3N_10_10_10 => 4,
            VertexElementFormat.UComp3_10_10_10 => 4,
            VertexElementFormat.UComp3N_10_10_10 => 4,
            VertexElementFormat.Comp3_11_11_10 => 4,
            VertexElementFormat.Comp3N_11_11_10 => 4,
            VertexElementFormat.UComp3_11_11_10 => 4,
            VertexElementFormat.UComp3N_11_11_10 => 4,
            VertexElementFormat.Comp4_10_10_10_2 => 4,
            VertexElementFormat.Comp4N_10_10_10_2 => 4,
            VertexElementFormat.UComp4_10_10_10_2 => 4,
            VertexElementFormat.UComp4N_10_10_10_2 => 4,
            VertexElementFormat.UByteN => 1,
            _ => 0
        };
    }
}
