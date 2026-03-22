using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Frosty.Sdk;
using Frosty.Sdk.IO;
using Frosty.Sdk.Profiles;

namespace Frosty.Sdk.Resources;

public enum MeshType
{
    Rigid = 0,
    Skinned = 1,
    Composite = 2
}

public enum PrimitiveType
{
    PointList = 0,
    LineList = 1,
    LineStrip = 2,
    TriangleList = 3,
    TriangleStrip = 5,
    QuadList = 7,
    XenonRectList = 8,
    TrianglePatch = 9
}

[Flags]
public enum MeshLayoutFlags : uint
{
    None = 0,
    IsBaseLod = 1 << 0,
    StreamInstancingEnable = 1 << 4,
    StreamingEnable = 1 << 6,
    VertexAnimationEnable = 1 << 7,
    Deformation = 1 << 8,
    MultiStreamEnable = 1 << 9,
    SubsetSortingEnable = 1 << 10,
    Inline = 1 << 11,
    AlternateBatchSorting = 1 << 12,
    ProjectedDecalsEnable = 1 << 15,
    ClothEnabled = 1 << 16,
    SrvEnable = 1 << 17,
    IsMeshFront = 1u << 28,
    IsDataAvailable = 1u << 29
}

[Flags]
public enum MeshSetLayoutFlags : ulong
{
    None = 0,
    StreamingEnable = 1ul << 0,
    HalfResRenderEnable = 1ul << 1,
    StreamInstancingEnable = 1ul << 2,
    MovableParts = 1ul << 3,
    DrawProcessEnable = 1ul << 4,
    StreamingEnableAlways = 1ul << 5,
    DeformationEnable = 1ul << 6,
    UseLastLodForShadow = 1ul << 8,
    CastShadowLowEnable = 1ul << 9,
    CastShadowMediumEnable = 1ul << 10,
    CastShadowHighEnable = 1ul << 11,
    CastShadowUltraEnable = 1ul << 12,
    CastDynamicReflectionLowEnable = 1ul << 13,
    CastDynamicReflectionMediumEnable = 1ul << 14,
    CastDynamicReflectionHighEnable = 1ul << 15,
    CastDynamicReflectionUltraEnable = 1ul << 16,
    CastPlanarReflectionLowEnable = 1ul << 17,
    CastPlanarReflectionMediumEnable = 1ul << 18,
    CastPlanarReflectionHighEnable = 1ul << 19,
    CastPlanarReflectionUltraEnable = 1ul << 20,
    CastStaticReflectionLowEnable = 1ul << 21,
    CastStaticReflectionMediumEnable = 1ul << 22,
    CastStaticReflectionHighEnable = 1ul << 23,
    CastStaticReflectionUltraEnable = 1ul << 24,
    SubsetSortingEnable = 1ul << 26,
    LodFadeEnable = 1ul << 27,
    ProjectedDecalsEnable = 1ul << 28,
    ClothEnable = 1ul << 29,
    ZPassEnable = 1ul << 30,
    CastDistantShadowCache = 1ul << 31,
    CastPlanarShadowLowEnable = 1ul << 32,
    CastPlanarShadowMediumEnable = 1ul << 33,
    CastPlanarShadowHighEnable = 1ul << 34,
    CastPlanarShadowUltraEnable = 1ul << 35,
    CastShadowInBakedLowEnable = 1ul << 36,
    CastShadowInBakedMediumEnable = 1ul << 37,
    CastShadowInBakedHighEnable = 1ul << 38,
    CastShadowInBakedUltraEnable = 1ul << 39,
    ForwardDepthPassEnable = 1ul << 40
}

public readonly record struct Vec3(float X, float Y, float Z)
{
    public override string ToString()
    {
        return $"{X:0.###}, {Y:0.###}, {Z:0.###}";
    }
}

public readonly record struct AxisAlignedBox(Vec3 Min, Vec3 Max)
{
    public override string ToString()
    {
        return $"Min({Min}) Max({Max})";
    }
}

public readonly record struct LinearTransform(Vec3 Right, Vec3 Up, Vec3 Forward, Vec3 Translation);

public sealed class MeshSet : Resource
{
    public sealed class MeshSection
    {
        public int Index { get; internal set; }
        public string Name { get; internal set; } = string.Empty;
        public int MaterialId { get; internal set; }
        public uint PrimitiveCount { get; internal set; }
        public uint StartIndex { get; internal set; }
        public uint VertexOffset { get; internal set; }
        public uint VertexCount { get; internal set; }
        public byte VertexStride { get; internal set; }
        public byte BonesPerVertex { get; internal set; }
        public int BoneCount { get; internal set; }
        public PrimitiveType PrimitiveType { get; internal set; }
        public AxisAlignedBox? BoundingBox { get; internal set; }
        public IReadOnlyList<ushort> BoneList => m_boneList;
        public IReadOnlyList<GeometryDeclarationDesc> GeometryDeclarations => m_geometryDeclarations;

        private readonly List<ushort> m_boneList = [];
        private readonly List<GeometryDeclarationDesc> m_geometryDeclarations = [];

        internal void SetBones(IEnumerable<ushort> bones)
        {
            m_boneList.Clear();
            m_boneList.AddRange(bones);
        }

        internal void AddGeometryDeclaration(GeometryDeclarationDesc geometryDeclaration)
        {
            m_geometryDeclarations.Add(geometryDeclaration);
        }
    }

    public sealed class MeshLod
    {
        internal int ChunkIdOffset { get; set; } = -1;

        public int Index { get; internal set; }
        public MeshType Type { get; internal set; }
        public MeshLayoutFlags Flags { get; internal set; }
        public Guid ChunkId { get; set; }
        public uint InlineDataOffset { get; internal set; }
        public uint IndexBufferSize { get; internal set; }
        public uint VertexBufferSize { get; internal set; }
        public int IndexElementSize { get; internal set; } = sizeof(ushort);
        public string FullName { get; internal set; } = string.Empty;
        public string Name { get; internal set; } = string.Empty;
        public string ShortName { get; internal set; } = string.Empty;
        public int BoneCount { get; internal set; }
        public bool UsesExternalChunk => ChunkId != Guid.Empty;
        public bool UsesInlineData => !UsesExternalChunk && InlineDataOffset != 0xFFFFFFFF;
        public IReadOnlyList<MeshSection> Sections => m_sections;
        public IReadOnlyList<uint> BoneIndexArray => m_boneIndexArray;
        public IReadOnlyList<uint> BoneShortNameArray => m_boneShortNameArray;
        public IReadOnlyList<AxisAlignedBox> PartBoundingBoxes => m_partBoundingBoxes;
        public IReadOnlyList<LinearTransform> PartTransforms => m_partTransforms;
        public int PartCount => m_partTransforms.Count;

        private readonly List<MeshSection> m_sections = [];
        private readonly List<uint> m_boneIndexArray = [];
        private readonly List<uint> m_boneShortNameArray = [];
        private readonly List<AxisAlignedBox> m_partBoundingBoxes = [];
        private readonly List<LinearTransform> m_partTransforms = [];

        internal void AddSection(MeshSection section)
        {
            m_sections.Add(section);
        }

        internal void SetSharedBoneLayout(IEnumerable<uint> boneIndices, IEnumerable<uint> boneShortNameHashes)
        {
            m_boneIndexArray.Clear();
            m_boneIndexArray.AddRange(boneIndices);
            m_boneShortNameArray.Clear();
            m_boneShortNameArray.AddRange(boneShortNameHashes);
        }

        internal void SetSharedPartLayout(IEnumerable<LinearTransform> partTransforms, IEnumerable<AxisAlignedBox> partBoundingBoxes)
        {
            m_partTransforms.Clear();
            m_partTransforms.AddRange(partTransforms);
            m_partBoundingBoxes.Clear();
            m_partBoundingBoxes.AddRange(partBoundingBoxes);
        }
    }

    public string FullName { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public uint NameHash { get; private set; }
    public MeshType Type { get; private set; }
    public MeshSetLayoutFlags Flags { get; private set; }
    public AxisAlignedBox BoundingBox { get; private set; }
    public byte[] RawData { get; private set; } = [];
    public byte[] ResourceMeta { get; private set; } = [];
    public string? ParseError { get; private set; }
    public bool ParsedSuccessfully => string.IsNullOrEmpty(ParseError);
    public IReadOnlyList<MeshLod> Lods => m_lods;
    public int TotalSectionCount => m_lods.Sum(static lod => lod.Sections.Count);
    public int ExternalChunkCount => m_lods.Count(static lod => lod.UsesExternalChunk);

    private readonly List<MeshLod> m_lods = [];
    private readonly List<uint> m_sharedBoneIndices = [];
    private readonly List<uint> m_sharedBoneShortNameHashes = [];
    private readonly List<AxisAlignedBox> m_sharedPartBoundingBoxes = [];
    private readonly List<LinearTransform> m_sharedPartTransforms = [];
    private int m_payloadOffset;

    public override void Deserialize(DataStream inStream, ReadOnlySpan<byte> inResMeta)
    {
        ResourceMeta = inResMeta.ToArray();
        RawData = inStream.ReadBytes((int)(inStream.Length - inStream.Position));
        Parse();
    }

    public override void Serialize(DataStream inStream, Span<byte> resMeta)
    {
        byte[] buffer = (byte[])RawData.Clone();
        foreach (MeshLod lod in m_lods)
        {
            if (lod.ChunkIdOffset < 0)
            {
                continue;
            }

            WriteGuid(buffer, m_payloadOffset + lod.ChunkIdOffset, lod.ChunkId);
        }

        inStream.Write(buffer);
        resMeta.Clear();
        ResourceMeta.AsSpan(0, Math.Min(ResourceMeta.Length, resMeta.Length)).CopyTo(resMeta);
    }

    public IEnumerable<Guid> EnumerateExternalChunkIds()
    {
        foreach (MeshLod lod in m_lods)
        {
            if (lod.UsesExternalChunk)
            {
                yield return lod.ChunkId;
            }
        }
    }

    public byte[] GetInlineLodData(MeshLod lod)
    {
        if (!lod.UsesInlineData)
        {
            return [];
        }

        int start = GetInlineLodDataOffset(lod);
        int length = checked((int)(lod.VertexBufferSize + lod.IndexBufferSize));
        if (start < 0 || start + length > RawData.Length)
        {
            throw new InvalidOperationException($"Inline mesh data for LOD {lod.Index} falls outside the resource buffer.");
        }

        return RawData.AsSpan(start, length).ToArray();
    }

    public void ReplaceInlineLodData(MeshLod lod, ReadOnlySpan<byte> data)
    {
        if (!lod.UsesInlineData)
        {
            throw new InvalidOperationException($"LOD {lod.Index} does not use inline mesh data.");
        }

        int start = GetInlineLodDataOffset(lod);
        int length = checked((int)(lod.VertexBufferSize + lod.IndexBufferSize));
        if (data.Length != length)
        {
            throw new InvalidOperationException(
                $"Inline mesh data size mismatch for LOD {lod.Index}. Expected {length} bytes but received {data.Length}.");
        }

        data.CopyTo(RawData.AsSpan(start, length));
    }

    private int GetInlineLodDataOffset(MeshLod lod)
    {
        if (!lod.UsesInlineData)
        {
            throw new InvalidOperationException($"LOD {lod.Index} does not use inline mesh data.");
        }

        if (ResourceMeta.Length < 8)
        {
            throw new InvalidOperationException("Mesh resource metadata does not expose inline data offsets.");
        }

        uint inlineRegionOffset = BinaryPrimitives.ReadUInt32LittleEndian(ResourceMeta.AsSpan(0, sizeof(uint)));
        uint inlineRegionOrRelocSize = BinaryPrimitives.ReadUInt32LittleEndian(ResourceMeta.AsSpan(sizeof(uint), sizeof(uint)));

        int inlineBaseOffset;
        if (MeshProfile.HasExtendedHeader)
        {
            inlineBaseOffset = Align16(checked((int)(inlineRegionOffset + inlineRegionOrRelocSize)));
        }
        else
        {
            inlineBaseOffset = checked((int)inlineRegionOffset);
        }

        return checked(inlineBaseOffset + (int)lod.InlineDataOffset);
    }

    private void Parse()
    {
        m_lods.Clear();
        FullName = string.Empty;
        Name = string.Empty;
        NameHash = 0;
        Type = MeshType.Rigid;
        Flags = MeshSetLayoutFlags.None;
        BoundingBox = default;
        ParseError = null;
        m_payloadOffset = MeshProfile.HasExtendedHeader ? 0x10 : 0;

        if (RawData.Length <= m_payloadOffset)
        {
            ParseError = "Mesh resource data is empty.";
            return;
        }

        try
        {
            using MemoryStream stream = new(RawData, m_payloadOffset, RawData.Length - m_payloadOffset, false);
            using DataStream reader = new(stream);
            ParseCore(reader);
        }
        catch (Exception ex)
        {
            m_lods.Clear();
            ParseError = ex.Message;
        }
    }

    private void ParseCore(DataStream reader)
    {
        BoundingBox = reader.ReadAxisAlignedBox();

        long[] lodOffsets = new long[6];
        for (int i = 0; i < lodOffsets.Length; i++)
        {
            lodOffsets[i] = reader.ReadInt64();
        }

        reader.ReadInt64();
        long fullNameOffset = reader.ReadInt64();
        long nameOffset = reader.ReadInt64();
        NameHash = reader.ReadUInt32();

        if (MeshProfile.UsesByteMeshType)
        {
            Type = (MeshType)reader.ReadByte();
            reader.Position += MeshProfile.MeshTypeUnknownSize;
        }
        else
        {
            Type = (MeshType)reader.ReadUInt32();
        }

        reader.Position += 12 * sizeof(ushort);
        Flags = (MeshSetLayoutFlags)reader.ReadUInt64();

        if (MeshProfile.UsesShaderDrawOrder)
        {
            if (MeshProfile.UsesWideShaderDrawOrder)
            {
                reader.ReadUInt16();
                reader.ReadUInt16();
            }
            else
            {
                reader.ReadByte();
                reader.ReadByte();
            }

            reader.ReadInt16();
        }

        if (ProfilesLibrary.IsLoaded(ProfileVersion.Madden20))
        {
            reader.ReadUInt16();
        }

        ushort lodCount = reader.ReadUInt16();
        reader.ReadUInt16();

        if (MeshProfile.HasHeaderUnknownUShortBlock)
        {
            reader.Position += 6 * sizeof(ushort);
        }

        ReadSharedPartBoneLayout(reader);
        reader.Align(16);

        for (int i = 0; i < lodCount && i < lodOffsets.Length; i++)
        {
            if (lodOffsets[i] == 0)
            {
                continue;
            }

            MeshLod lod = ParseLod(reader, lodOffsets[i], i);
            if (MeshProfile.UsesNewPartBoneLayout)
            {
                if (Type == MeshType.Skinned)
                {
                    lod.SetSharedBoneLayout(m_sharedBoneIndices, m_sharedBoneShortNameHashes);
                }
                else if (Type == MeshType.Composite)
                {
                    lod.SetSharedPartLayout(m_sharedPartTransforms, m_sharedPartBoundingBoxes);
                }
            }

            m_lods.Add(lod);
        }

        FullName = ReadStringAt(reader, fullNameOffset);
        Name = ReadStringAt(reader, nameOffset);
    }

    private void ReadSharedPartBoneLayout(DataStream reader)
    {
        m_sharedBoneIndices.Clear();
        m_sharedBoneShortNameHashes.Clear();
        m_sharedPartBoundingBoxes.Clear();
        m_sharedPartTransforms.Clear();

        if (!MeshProfile.UsesNewPartBoneLayout || Type == MeshType.Rigid)
        {
            return;
        }

        if (Type == MeshType.Skinned)
        {
            ushort boneCount = reader.ReadUInt16();
            uint partCount = MeshProfile.UsesWideSkinnedPartCount ? reader.ReadUInt32() : reader.ReadUInt16();
            if (boneCount != 0 || partCount != 0)
            {
                long boneIndicesOffset = reader.ReadInt64();
                long boneBoundingBoxesOffset = reader.ReadInt64();
                long returnPosition = reader.Position;

                if (boneIndicesOffset != 0)
                {
                    reader.StepIn(boneIndicesOffset);
                    for (int i = 0; i < partCount; i++)
                    {
                        m_sharedBoneIndices.Add(reader.ReadUInt16());
                    }

                    reader.StepOut();
                }

                if (boneBoundingBoxesOffset != 0)
                {
                    reader.StepIn(boneBoundingBoxesOffset);
                    for (int i = 0; i < partCount; i++)
                    {
                        m_sharedPartBoundingBoxes.Add(reader.ReadAxisAlignedBox());
                    }

                    reader.StepOut();
                }

                reader.Position = returnPosition;
            }

            return;
        }

        ushort compositePartCount = reader.ReadUInt16();
        uint compositeBoneCount = MeshProfile.UsesWideCompositeBoneCount ? reader.ReadUInt32() : reader.ReadUInt16();
        if (compositePartCount != 0 || compositeBoneCount != 0)
        {
            long partTransformsOffset = reader.ReadInt64();
            if (MeshProfile.RequiresCompositePointerPadding)
            {
                reader.Position += sizeof(uint);
            }

            long partBoundingBoxesOffset = reader.ReadInt64();
            long returnPosition = reader.Position;

            if (partTransformsOffset != 0)
            {
                reader.StepIn(partTransformsOffset);
                for (int i = 0; i < compositePartCount; i++)
                {
                    m_sharedPartTransforms.Add(reader.ReadLinearTransform());
                }

                reader.StepOut();
            }

            if (partBoundingBoxesOffset != 0)
            {
                reader.StepIn(partBoundingBoxesOffset);
                for (int i = 0; i < compositePartCount; i++)
                {
                    m_sharedPartBoundingBoxes.Add(reader.ReadAxisAlignedBox());
                }

                reader.StepOut();
            }

            reader.Position = returnPosition;
        }
    }

    private MeshLod ParseLod(DataStream reader, long offset, int lodIndex)
    {
        reader.StepIn(offset);

        MeshLod lod = new()
        {
            Index = lodIndex,
            Type = (MeshType)reader.ReadUInt32()
        };

        reader.ReadUInt32();
        uint sectionCount = reader.ReadUInt32();
        long sectionOffset = reader.ReadInt64();

        for (int i = 0; i < 5; i++)
        {
            reader.ReadInt32();
            reader.ReadInt64();
        }

        if (MeshProfile.HasLegacyLodUnknownUInt)
        {
            reader.ReadUInt32();
        }

        lod.Flags = (MeshLayoutFlags)reader.ReadUInt32();
        int indexBufferFormat = reader.ReadInt32();
        lod.IndexBufferSize = reader.ReadUInt32();
        lod.VertexBufferSize = reader.ReadUInt32();

        if (MeshProfile.LodUnknownBlockSize > 0)
        {
            reader.Position += MeshProfile.LodUnknownBlockSize;
        }

        lod.ChunkIdOffset = (int)reader.Position;
        lod.ChunkId = reader.ReadGuid();
        lod.InlineDataOffset = reader.ReadUInt32();

        if (MeshProfile.HasLodTrailingUnknownUInt)
        {
            reader.ReadUInt32();
        }

        long fullNameOffset = reader.ReadInt64();
        long nameOffset = reader.ReadInt64();
        long shortNameOffset = reader.ReadInt64();
        reader.ReadUInt32();
        reader.ReadInt64();

        if (lod.Type == MeshType.Skinned)
        {
            lod.BoneCount = MeshProfile.UsesWideLodBoneCount
                ? reader.ReadInt32()
                : reader.ReadUInt16();

            if (!MeshProfile.UsesWideLodBoneCount)
            {
                reader.ReadUInt16();
            }

            reader.ReadInt64();
        }
        else if (lod.Type == MeshType.Composite)
        {
            reader.ReadInt64();
        }

        reader.Align(16);
        lod.FullName = ReadStringAt(reader, fullNameOffset);
        lod.Name = ReadStringAt(reader, nameOffset);
        lod.ShortName = ReadStringAt(reader, shortNameOffset);

        if (sectionCount > 0 && sectionOffset != 0)
        {
            reader.StepIn(sectionOffset);
            for (int i = 0; i < sectionCount; i++)
            {
                lod.AddSection(ParseSection(reader, i));
            }

            reader.StepOut();
        }

        lod.IndexElementSize = InferIndexElementSize(lod, indexBufferFormat);

        reader.StepOut();
        return lod;
    }

    private static MeshSection ParseSection(DataStream reader, int sectionIndex)
    {
        long sectionStart = reader.Position;
        long nameOffset = 0;
        long boneListOffset = 0;
        ushort boneCount = 0;
        int materialId = 0;
        uint primitiveCount = 0;
        uint startIndex = 0;
        uint vertexOffset = 0;
        uint vertexCount = 0;
        byte vertexStride = 0;
        byte bonesPerVertex = 0;
        PrimitiveType primitiveType = PrimitiveType.TriangleList;
        AxisAlignedBox? boundingBox = null;
        List<ushort> boneList = [];
        List<GeometryDeclarationDesc> geometryDeclarations = [];

        if (MeshProfile.IsFifa21)
        {
            reader.ReadInt64();
            nameOffset = reader.ReadInt64();
            materialId = reader.ReadInt32();
            reader.ReadUInt32();

            for (int i = 0; i < 6; i++)
            {
                reader.ReadSingle();
            }

            primitiveCount = reader.ReadUInt32();
            startIndex = reader.ReadUInt32();
            vertexOffset = reader.ReadUInt32();
            vertexCount = reader.ReadUInt32();
            boundingBox = reader.ReadAxisAlignedBox();
            vertexStride = reader.ReadByte();
            primitiveType = (PrimitiveType)reader.ReadByte();
            reader.ReadBoolean();
            reader.ReadBoolean();
            bonesPerVertex = (byte)reader.ReadUInt16();
            boneCount = reader.ReadUInt16();
            boneListOffset = reader.ReadInt64();
            reader.ReadUInt64();
            geometryDeclarations.Add(ReadGeometryDeclaration(reader));
            reader.Align(16);
        }
        else if (MeshProfile.UsesModernSectionLayout)
        {
            reader.ReadInt64();
            nameOffset = reader.ReadInt64();
            boneListOffset = reader.ReadInt64();
            boneCount = reader.ReadUInt16();
            bonesPerVertex = reader.ReadByte();
            reader.ReadByte();
            materialId = reader.ReadUInt16();
            vertexStride = reader.ReadByte();
            primitiveType = (PrimitiveType)reader.ReadByte();
            primitiveCount = reader.ReadUInt32();
            startIndex = reader.ReadUInt32();
            vertexOffset = reader.ReadUInt32();
            vertexCount = reader.ReadUInt32();
            reader.ReadUInt32();

            if (MeshProfile.IsMadden26)
            {
                reader.Position += 24;
            }
            else if (MeshProfile.IsMadden25)
            {
                reader.Position += 16;
            }
            else if (MeshProfile.IsFc24)
            {
                reader.Position += 16;
            }
            else if (MeshProfile.IsFifa23)
            {
                reader.Position += 8;
            }

            for (int i = 0; i < 6; i++)
            {
                reader.ReadSingle();
            }

            for (int i = 0; i < MeshProfile.SectionGeometryDeclCount; i++)
            {
                geometryDeclarations.Add(ReadGeometryDeclaration(reader));
            }

            if (MeshProfile.IsMadden25)
            {
                reader.Position += 104;
                reader.ReadInt64();
                reader.ReadUInt32();
                reader.Align(16);
                reader.Position -= 8;
                boundingBox = reader.ReadAxisAlignedBox();
            }
            else
            {
                if (MeshProfile.IsMadden26)
                {
                    reader.Align(16);
                }

                reader.ReadInt64();

                if (ProfilesLibrary.IsLoaded(ProfileVersion.Fifa22, ProfileVersion.Fifa23))
                {
                    reader.ReadInt64();
                }
                else if (ProfilesLibrary.IsLoaded(ProfileVersion.Madden22, ProfileVersion.Madden23, ProfileVersion.Madden25, ProfileVersion.Madden26))
                {
                    reader.ReadUInt32();
                }

                reader.Align(16);

                if (MeshProfile.IsMadden26)
                {
                    reader.Position += 16;
                }

                boundingBox = reader.ReadAxisAlignedBox();
            }

            reader.Align(16);
        }
        else
        {
            throw new InvalidOperationException(
                $"Mesh sections are not implemented for profile data version {ProfilesLibrary.DataVersion}.");
        }

        string name = ReadStringAt(reader, nameOffset);
        if (boneCount > 0 && boneListOffset > 0)
        {
            reader.StepIn(boneListOffset);
            for (int i = 0; i < boneCount; i++)
            {
                boneList.Add(reader.ReadUInt16());
            }

            reader.StepOut();
        }

        if (MeshProfile.IsMadden25)
        {
            reader.Position += 8;
        }

        MeshSection section = new()
        {
            Index = sectionIndex,
            Name = name,
            MaterialId = materialId,
            PrimitiveCount = primitiveCount,
            StartIndex = startIndex,
            VertexOffset = vertexOffset,
            VertexCount = vertexCount,
            VertexStride = vertexStride,
            BonesPerVertex = bonesPerVertex,
            BoneCount = boneCount,
            PrimitiveType = primitiveType,
            BoundingBox = boundingBox
        };
        section.SetBones(boneList);
        foreach (GeometryDeclarationDesc geometryDeclaration in geometryDeclarations)
        {
            section.AddGeometryDeclaration(geometryDeclaration);
        }

        return section;
    }

    private static GeometryDeclarationDesc ReadGeometryDeclaration(DataStream reader)
    {
        GeometryDeclarationDesc.Element[] elements = new GeometryDeclarationDesc.Element[GeometryDeclarationDesc.MaxElements];
        GeometryDeclarationDesc.Stream[] streams = new GeometryDeclarationDesc.Stream[GeometryDeclarationDesc.MaxStreams];

        for (int i = 0; i < elements.Length; i++)
        {
            elements[i] = new GeometryDeclarationDesc.Element(
                (VertexElementUsage)reader.ReadByte(),
                (VertexElementFormat)reader.ReadByte(),
                reader.ReadByte(),
                reader.ReadByte());
        }

        for (int i = 0; i < streams.Length; i++)
        {
            streams[i] = new GeometryDeclarationDesc.Stream(
                reader.ReadByte(),
                (VertexElementClassification)reader.ReadByte());
        }

        byte elementCount = reader.ReadByte();
        byte streamCount = reader.ReadByte();
        reader.ReadUInt16();

        return new GeometryDeclarationDesc
        {
            Elements = elements,
            Streams = streams,
            ElementCount = elementCount,
            StreamCount = streamCount
        };
    }

    private static string ReadStringAt(DataStream reader, long offset)
    {
        if (offset <= 0)
        {
            return string.Empty;
        }

        reader.StepIn(offset);
        string value = reader.ReadNullTerminatedString();
        reader.StepOut();
        return value;
    }

    private static void WriteGuid(byte[] buffer, int offset, Guid value)
    {
        if (offset < 0 || offset + 16 > buffer.Length)
        {
            throw new InvalidOperationException("Mesh chunk GUID offset falls outside the resource buffer.");
        }

        value.TryWriteBytes(buffer.AsSpan(offset, 16));
    }

    private static int InferIndexElementSize(MeshLod lod, int indexBufferFormat)
    {
        int? explicitIndexElementSize = TryResolveIndexElementSize(indexBufferFormat);
        if (explicitIndexElementSize.HasValue)
        {
            return explicitIndexElementSize.Value;
        }

        if (lod.Sections.Count > 0)
        {
            ulong totalTriangleIndices = 0;
            bool onlyTriangleLists = true;
            foreach (MeshSection section in lod.Sections)
            {
                if (section.PrimitiveType != PrimitiveType.TriangleList)
                {
                    onlyTriangleLists = false;
                    break;
                }

                totalTriangleIndices += (ulong)section.PrimitiveCount * 3;
            }

            if (onlyTriangleLists)
            {
                if ((ulong)lod.IndexBufferSize == totalTriangleIndices * sizeof(uint))
                {
                    return sizeof(uint);
                }

                if ((ulong)lod.IndexBufferSize == totalTriangleIndices * sizeof(ushort))
                {
                    return sizeof(ushort);
                }
            }

            if (lod.Sections.Any(static section => section.VertexCount > ushort.MaxValue))
            {
                return sizeof(uint);
            }
        }

        return indexBufferFormat == 0 ? sizeof(ushort) : sizeof(uint);
    }

    private static int? TryResolveIndexElementSize(int indexBufferFormat)
    {
        Type? renderFormatType = TypeLibrary.GetType("RenderFormat")?.Type;
        if (renderFormatType is null || !renderFormatType.IsEnum)
        {
            return null;
        }

        try
        {
            object? r32Value = Enum.Parse(renderFormatType, "RenderFormat_R32_UINT");
            return Convert.ToInt32(r32Value) == indexBufferFormat ? sizeof(uint) : sizeof(ushort);
        }
        catch
        {
            return null;
        }
    }

    private static int Align16(int value)
    {
        int remainder = value & 0x0F;
        return remainder == 0 ? value : checked(value + (16 - remainder));
    }
}

internal static class MeshProfile
{
    private const int c_fc24DataVersion = 20230929;

    public static bool IsFifa21 => ProfilesLibrary.IsLoaded(ProfileVersion.Fifa21);
    public static bool IsFifa23 => ProfilesLibrary.IsLoaded(ProfileVersion.Fifa23);
    public static bool IsFc24 => ProfilesLibrary.DataVersion == c_fc24DataVersion;
    public static bool IsMadden25 => ProfilesLibrary.IsLoaded(ProfileVersion.Madden25);
    public static bool IsMadden26 => ProfilesLibrary.IsLoaded(ProfileVersion.Madden26);
    public static bool HasExtendedHeader => IsMadden26;
    public static bool UsesByteMeshType => IsFifa23 || IsFc24 || IsMadden25 || IsMadden26;
    public static int MeshTypeUnknownSize => IsFifa23 ? 19 : UsesByteMeshType ? 11 : 0;

    public static bool UsesNewPartBoneLayout =>
        ProfilesLibrary.IsLoaded(
            ProfileVersion.Madden22,
            ProfileVersion.Fifa22,
            ProfileVersion.Madden23,
            ProfileVersion.Fifa23,
            ProfileVersion.Madden25,
            ProfileVersion.Madden26) ||
        IsFc24;

    public static bool UsesShaderDrawOrder => UsesNewPartBoneLayout;
    public static bool UsesWideShaderDrawOrder =>
        ProfilesLibrary.IsLoaded(ProfileVersion.Madden22, ProfileVersion.Madden23, ProfileVersion.Madden25, ProfileVersion.Madden26);

    public static bool HasHeaderUnknownUShortBlock =>
        ProfilesLibrary.IsLoaded(
            ProfileVersion.Madden22,
            ProfileVersion.Fifa22,
            ProfileVersion.Madden23,
            ProfileVersion.Fifa23,
            ProfileVersion.Madden25,
            ProfileVersion.Madden26) ||
        IsFc24;

    public static bool UsesWideSkinnedPartCount =>
        ProfilesLibrary.IsLoaded(ProfileVersion.Madden20, ProfileVersion.Madden22, ProfileVersion.Madden23, ProfileVersion.Madden25, ProfileVersion.Madden26);

    public static bool UsesWideCompositeBoneCount =>
        ProfilesLibrary.IsLoaded(ProfileVersion.Madden20, ProfileVersion.Madden22, ProfileVersion.Madden23, ProfileVersion.Madden25, ProfileVersion.Madden26);

    public static bool RequiresCompositePointerPadding => true;

    public static bool HasLegacyLodUnknownUInt =>
        ProfilesLibrary.IsLoaded(ProfileVersion.Fifa18, ProfileVersion.Madden19);

    public static int LodUnknownBlockSize
    {
        get
        {
            if (IsMadden26)
            {
                return 20;
            }

            if (IsMadden25 || IsFc24)
            {
                return 12;
            }

            if (IsFifa23)
            {
                return 8;
            }

            return 0;
        }
    }

    public static bool HasLodTrailingUnknownUInt => IsMadden26;

    public static bool UsesWideLodBoneCount =>
        ProfilesLibrary.IsLoaded(ProfileVersion.Madden22, ProfileVersion.Madden23, ProfileVersion.Madden25, ProfileVersion.Madden26);

    public static bool UsesModernSectionLayout =>
        ProfilesLibrary.IsLoaded(
            ProfileVersion.Madden22,
            ProfileVersion.Fifa22,
            ProfileVersion.Madden23,
            ProfileVersion.Fifa23,
            ProfileVersion.Madden25,
            ProfileVersion.Madden26) ||
        IsFc24;

    public static int SectionLeadingUnknownBlockSize
    {
        get
        {
            if (IsMadden26)
            {
                return 24;
            }

            if (IsMadden25 || IsFc24)
            {
                return 16;
            }

            if (IsFifa23)
            {
                return 8;
            }

            return 0;
        }
    }

    public static int SectionGeometryDeclCount => IsMadden26 ? 2 : 1;

    public static int SectionTrailingUnknownBlockSize
    {
        get
        {
            if (IsMadden25)
            {
                return 0;
            }

            if (IsMadden26)
            {
                return 18;
            }

            if (IsFc24)
            {
                return 32;
            }

            if (IsFifa23)
            {
                return 24;
            }

            if (UsesModernSectionLayout)
            {
                return 16;
            }

            return 0;
        }
    }

    public static bool SectionHasBonesPerVertexTail => IsMadden26;
}

internal static class MeshDataStreamExtensions
{
    public static Vec3 ReadVec3(this DataStream reader)
    {
        float x = reader.ReadSingle();
        float y = reader.ReadSingle();
        float z = reader.ReadSingle();
        reader.ReadSingle();
        return new Vec3(x, y, z);
    }

    public static AxisAlignedBox ReadAxisAlignedBox(this DataStream reader)
    {
        return new AxisAlignedBox(reader.ReadVec3(), reader.ReadVec3());
    }

    public static LinearTransform ReadLinearTransform(this DataStream reader)
    {
        return new LinearTransform(
            reader.ReadVec3(),
            reader.ReadVec3(),
            reader.ReadVec3(),
            reader.ReadVec3());
    }

    public static void Align(this DataStream reader, int alignment)
    {
        long remainder = reader.Position % alignment;
        if (remainder != 0)
        {
            reader.Position += alignment - remainder;
        }
    }
}
