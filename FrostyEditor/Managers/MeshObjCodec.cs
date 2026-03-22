using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using Frosty.Sdk.Managers;
using Frosty.Sdk.Managers.Entries;
using Frosty.Sdk.Resources;
using Frosty.Sdk.Utils;

namespace FrostyEditor.Managers;

public static class MeshObjCodec
{
    private const float c_faceAreaEpsilon = 1e-12f;

    private enum VertexDecodeMode
    {
        SequentialStreamBlocks = 0,
        ElementOffsets = 1
    }

    internal sealed class MeshDecodedSection
    {
        public required string ObjectName { get; init; }
        public required MeshSet.MeshLod Lod { get; init; }
        public required MeshSet.MeshSection Section { get; init; }
        public required GeometryDeclarationDesc GeometryDeclaration { get; init; }
        public required int GeometryDeclarationIndex { get; init; }
        public required string DecodeMode { get; init; }
        public required int IndexElementSize { get; init; }
        public required int ValidTriangleCount { get; init; }
        public required int InvalidTriangleCount { get; init; }
        public required int DegenerateTriangleCount { get; init; }
        public required string DecodeDiagnostics { get; init; }
        public required MeshDecodedVertex[] Vertices { get; init; }
        public required int[] Indices { get; init; }
    }

    internal sealed class MeshImportedSection
    {
        public List<MeshDecodedVertex> Vertices { get; } = [];
        public List<int> Indices { get; } = [];
        public bool HasNormals { get; set; }
        public bool HasTangents { get; set; }
        public bool HasSkinWeights { get; set; }
        public bool[] HasTexCoords { get; } = new bool[8];
        public bool[] HasColors { get; } = new bool[2];
    }

    internal sealed class MeshDecodedVertex
    {
        public Vector3 Position { get; set; }
        public Vector3 Normal { get; set; }
        public Vector4 Tangent { get; set; }
        public Vector4 Binormal { get; set; }
        public Vector2 Uv
        {
            get => TexCoords[0];
            set => TexCoords[0] = value;
        }

        public Vector2[] TexCoords { get; } = new Vector2[8];
        public Vector4[] Colors { get; } = new Vector4[2];
        public ushort[] BoneIndices { get; } = new ushort[8];
        public float[] BoneWeights { get; } = new float[8];
    }

    private readonly record struct ObjVertexRef(int Position, int TexCoord, int Normal);

    public static MeshOperationResult Export(MeshAssetLoadResult load, string path)
    {
        return Export(load, path, DecodeSections(load));
    }

    internal static MeshOperationResult Export(MeshAssetLoadResult load, string path, IReadOnlyList<MeshDecodedSection> decodedSections)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (!path.EndsWith(".obj", StringComparison.OrdinalIgnoreCase))
            {
                path += ".obj";
            }

            if (decodedSections.Count == 0)
            {
                return new MeshOperationResult(false, $"{load.Entry.Filename} does not contain any triangle-list sections that Frosty 2.0 can export to OBJ yet.");
            }

            StringBuilder builder = new();
            builder.AppendLine("# Frosty Editor 2.0 OBJ export");
            builder.AppendLine($"# Asset: {load.Entry.Name}");
            builder.AppendLine($"# Resource: {load.ResourceEntry.Name}");

            int vertexBase = 1;
            foreach (MeshDecodedSection section in decodedSections)
            {
                builder.AppendLine();
                builder.AppendLine($"o {section.ObjectName}");

                foreach (MeshDecodedVertex vertex in section.Vertices)
                {
                    builder.AppendLine(FormattableString.Invariant(
                        $"v {vertex.Position.X:0.######} {vertex.Position.Y:0.######} {vertex.Position.Z:0.######}"));
                }

                foreach (MeshDecodedVertex vertex in section.Vertices)
                {
                    builder.AppendLine(FormattableString.Invariant(
                        $"vt {vertex.Uv.X:0.######} {1.0f - vertex.Uv.Y:0.######}"));
                }

                foreach (MeshDecodedVertex vertex in section.Vertices)
                {
                    Vector3 normal = vertex.Normal.LengthSquared() < 0.000001f
                        ? Vector3.UnitY
                        : Vector3.Normalize(vertex.Normal);
                    builder.AppendLine(FormattableString.Invariant(
                        $"vn {normal.X:0.######} {normal.Y:0.######} {normal.Z:0.######}"));
                }

                for (int i = 0; i < section.Indices.Length; i += 3)
                {
                    int a = vertexBase + section.Indices[i];
                    int b = vertexBase + section.Indices[i + 1];
                    int c = vertexBase + section.Indices[i + 2];
                    builder.AppendLine(FormattableString.Invariant(
                        $"f {a}/{a}/{a} {b}/{b}/{b} {c}/{c}/{c}"));
                }

                vertexBase += section.Vertices.Length;
            }

            File.WriteAllText(path, builder.ToString(), Encoding.UTF8);
            return new MeshOperationResult(true, $"Exported {load.Entry.Filename} to {path}.");
        }
        catch (Exception ex)
        {
            return new MeshOperationResult(false, $"Failed to export OBJ mesh: {ex.Message}");
        }
    }

    public static MeshOperationResult Import(MeshAssetLoadResult load, string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new MeshOperationResult(false, $"OBJ mesh \"{path}\" was not found.");
            }

            List<MeshDecodedSection> decodedSections = DecodeSections(load);
            if (decodedSections.Count == 0)
            {
                return new MeshOperationResult(false, $"{load.Entry.Filename} does not contain any triangle-list sections that Frosty 2.0 can import from OBJ yet.");
            }

            Dictionary<string, MeshImportedSection> importedSections = ParseObj(path);
            return ImportSections(load, importedSections, "OBJ");
        }
        catch (Exception ex)
        {
            return new MeshOperationResult(false, $"Failed to import OBJ mesh: {ex.Message}");
        }
    }

    internal static MeshOperationResult ImportSections(
        MeshAssetLoadResult load,
        IReadOnlyDictionary<string, MeshImportedSection> importedSections,
        string sourceName)
    {
        Dictionary<MeshSet.MeshLod, byte[]> lodBuffers = [];
        bool inlineDataChanged = false;

        foreach (MeshDecodedSection decodedSection in DecodeSections(load))
        {
            if (!importedSections.TryGetValue(decodedSection.ObjectName, out MeshImportedSection? importedSection))
            {
                return new MeshOperationResult(false, $"The {sourceName} file is missing object \"{decodedSection.ObjectName}\".");
            }

            if (importedSection.Vertices.Count != decodedSection.Vertices.Length)
            {
                return new MeshOperationResult(
                    false,
                    $"Object \"{decodedSection.ObjectName}\" changed vertex count from {decodedSection.Vertices.Length} to {importedSection.Vertices.Count}. Frosty 2.0 currently supports same-topology {sourceName} import only.");
            }

            if (!importedSection.Indices.SequenceEqual(decodedSection.Indices))
            {
                return new MeshOperationResult(
                    false,
                    $"Object \"{decodedSection.ObjectName}\" changed face topology. Frosty 2.0 currently supports same-topology {sourceName} import only.");
            }

            if (!lodBuffers.TryGetValue(decodedSection.Lod, out byte[]? lodBuffer))
            {
                lodBuffer = GetLodData(load.Mesh, decodedSection.Lod);
                lodBuffers.Add(decodedSection.Lod, lodBuffer);
            }

            WriteSectionVertices(lodBuffer, decodedSection.Section, decodedSection.GeometryDeclaration, importedSection);
        }

        foreach ((MeshSet.MeshLod lod, byte[] lodBuffer) in lodBuffers)
        {
            if (lod.UsesExternalChunk)
            {
                AssetManager.ModifyChunk(lod.ChunkId, lodBuffer);
                continue;
            }

            load.Mesh.ReplaceInlineLodData(lod, lodBuffer);
            inlineDataChanged = true;
        }

        if (inlineDataChanged)
        {
            AssetManager.ModifyRes(load.ResourceEntry.ResRid, load.Mesh);
        }

        return new MeshOperationResult(true, $"Imported {sourceName} mesh data for {load.Entry.Filename}.");
    }

    internal static List<MeshDecodedSection> DecodeSections(MeshAssetLoadResult load, int? lodIndex = null)
    {
        List<MeshDecodedSection> sections = [];

        foreach (MeshSet.MeshLod lod in load.Mesh.Lods)
        {
            if (lodIndex.HasValue && lod.Index != lodIndex.Value)
            {
                continue;
            }

            byte[] lodData = GetLodData(load.Mesh, lod);
            foreach (MeshSet.MeshSection section in lod.Sections)
            {
                if (section.PrimitiveCount == 0 || section.PrimitiveType != PrimitiveType.TriangleList)
                {
                    continue;
                }

                if (!section.GeometryDeclarations.Any(HasUsableGeometryDeclaration))
                {
                    continue;
                }

                sections.Add(BuildBestDecodedSection(lodData, lod, section));
            }
        }

        return sections;
    }

    private static byte[] GetLodData(MeshSet mesh, MeshSet.MeshLod lod)
    {
        if (lod.UsesExternalChunk)
        {
            ChunkAssetEntry? chunkEntry = AssetManager.GetChunkAssetEntry(lod.ChunkId);
            if (chunkEntry is null)
            {
                throw new InvalidOperationException($"Chunk {lod.ChunkId} could not be resolved.");
            }

            using Block<byte> data = AssetManager.GetAsset(chunkEntry);
            return data.ToArray();
        }

        return mesh.GetInlineLodData(lod);
    }

    private static MeshDecodedSection BuildBestDecodedSection(
        byte[] lodData,
        MeshSet.MeshLod lod,
        MeshSet.MeshSection section)
    {
        List<(MeshDecodedVertex[] Vertices, int[] Indices, DecodeQuality Quality, GeometryDeclarationDesc GeometryDeclaration, int GeometryDeclarationIndex, VertexDecodeMode DecodeMode, int IndexElementSize)> candidates = [];

        for (int geometryDeclarationIndex = 0; geometryDeclarationIndex < section.GeometryDeclarations.Count; geometryDeclarationIndex++)
        {
            GeometryDeclarationDesc geometryDeclaration = section.GeometryDeclarations[geometryDeclarationIndex];
            if (!HasUsableGeometryDeclaration(geometryDeclaration))
            {
                continue;
            }

            foreach (VertexDecodeMode decodeMode in Enum.GetValues<VertexDecodeMode>())
            {
                MeshDecodedVertex[] vertices = ReadVertices(lodData, section, geometryDeclaration, decodeMode);
                if (vertices.Length == 0)
                {
                    continue;
                }

                HashSet<int> candidateIndexSizes = [lod.IndexElementSize];
                candidateIndexSizes.Add(lod.IndexElementSize == sizeof(uint) ? sizeof(ushort) : sizeof(uint));

                foreach (int indexElementSize in candidateIndexSizes)
                {
                    if (!TryReadIndices(lodData, lod, section, indexElementSize, out int[]? indices))
                    {
                        continue;
                    }

                    candidates.Add((vertices, indices, EvaluateDecodeQuality(vertices, indices, section), geometryDeclaration, geometryDeclarationIndex, decodeMode, indexElementSize));
                }
            }
        }

        if (candidates.Count == 0)
        {
            return new MeshDecodedSection
            {
                ObjectName = BuildObjectName(lod, section),
                Lod = lod,
                Section = section,
                GeometryDeclaration = section.GeometryDeclarations.FirstOrDefault(),
                GeometryDeclarationIndex = 0,
                DecodeMode = VertexDecodeMode.SequentialStreamBlocks.ToString(),
                IndexElementSize = lod.IndexElementSize,
                ValidTriangleCount = 0,
                InvalidTriangleCount = 0,
                DegenerateTriangleCount = checked((int)section.PrimitiveCount),
                DecodeDiagnostics = "No geometry declaration candidate produced a usable preview decode.",
                Vertices = [],
                Indices = []
            };
        }

        (MeshDecodedVertex[] Vertices, int[] Indices, DecodeQuality Quality, GeometryDeclarationDesc GeometryDeclaration, int GeometryDeclarationIndex, VertexDecodeMode DecodeMode, int IndexElementSize) bestCandidate = candidates[0];
        for (int i = 1; i < candidates.Count; i++)
        {
            if (IsBetterDecode(candidates[i].Quality, bestCandidate.Quality))
            {
                bestCandidate = candidates[i];
            }
        }

        return new MeshDecodedSection
        {
            ObjectName = BuildObjectName(lod, section),
            Lod = lod,
            Section = section,
            GeometryDeclaration = bestCandidate.GeometryDeclaration,
            GeometryDeclarationIndex = bestCandidate.GeometryDeclarationIndex,
            DecodeMode = bestCandidate.DecodeMode.ToString(),
            IndexElementSize = bestCandidate.IndexElementSize,
            ValidTriangleCount = bestCandidate.Quality.ValidTriangles,
            InvalidTriangleCount = bestCandidate.Quality.InvalidTriangles,
            DegenerateTriangleCount = bestCandidate.Quality.DegenerateTriangles,
            DecodeDiagnostics = BuildDecodeDiagnostics(bestCandidate),
            Vertices = bestCandidate.Vertices,
            Indices = bestCandidate.Indices
        };
    }

    private static MeshDecodedVertex[] ReadVertices(
        byte[] lodData,
        MeshSet.MeshSection section,
        GeometryDeclarationDesc geometryDeclaration,
        VertexDecodeMode decodeMode)
    {
        MeshDecodedVertex[] vertices = Enumerable.Range(0, (int)section.VertexCount)
            .Select(static _ => new MeshDecodedVertex())
            .ToArray();
        int totalStride = 0;

        GeometryDeclarationDesc.Stream[] streams = geometryDeclaration.Streams ?? [];
        GeometryDeclarationDesc.Element[] elements = geometryDeclaration.Elements ?? [];
        int streamCount = streams.Length;
        int elementCount = elements.Length;
        for (int streamIndex = 0; streamIndex < streamCount; streamIndex++)
        {
            GeometryDeclarationDesc.Stream stream = streams[streamIndex];
            if (stream.VertexStride == 0)
            {
                continue;
            }

            for (int vertexIndex = 0; vertexIndex < vertices.Length; vertexIndex++)
            {
                int streamBaseOffset = checked((int)section.VertexOffset +
                    (totalStride * vertices.Length) +
                    (vertexIndex * stream.VertexStride));
                int currentStride = 0;

                MeshDecodedVertex vertex = vertices[vertexIndex];

                for (int elementIndex = 0; elementIndex < elementCount; elementIndex++)
                {
                    GeometryDeclarationDesc.Element element = elements[elementIndex];
                    if (element.Usage == VertexElementUsage.Unknown)
                    {
                        continue;
                    }

                    if (element.StreamIndex != streamIndex || currentStride >= stream.VertexStride)
                    {
                        continue;
                    }

                    int elementOffset = decodeMode == VertexDecodeMode.SequentialStreamBlocks
                        ? streamBaseOffset + currentStride
                        : streamBaseOffset + element.Offset;
                    switch (element.Usage)
                    {
                        case VertexElementUsage.Pos:
                            vertex.Position = ReadPosition(lodData, elementOffset, element.Format);
                            break;

                        case VertexElementUsage.Normal:
                            vertex.Normal = ReadNormal(lodData, elementOffset, element.Format);
                            break;

                        case VertexElementUsage.Tangent:
                            vertex.Tangent = ReadTangent(lodData, elementOffset, element.Format);
                            break;

                        case VertexElementUsage.Binormal:
                            vertex.Binormal = ReadTangent(lodData, elementOffset, element.Format);
                            break;

                        case VertexElementUsage.BinormalSign:
                            vertex.Tangent = new Vector4(
                                vertex.Tangent.X,
                                vertex.Tangent.Y,
                                vertex.Tangent.Z,
                                ReadBinormalSign(lodData, elementOffset, element.Format));
                            break;

                        case VertexElementUsage.TangentSpace:
                            if (vertex.Normal.LengthSquared() < 0.000001f)
                            {
                                vertex.Normal = ReadTangentSpaceNormal(lodData, elementOffset, element.Format);
                            }

                            break;

                        case VertexElementUsage.TexCoord0:
                        case VertexElementUsage.TexCoord1:
                        case VertexElementUsage.TexCoord2:
                        case VertexElementUsage.TexCoord3:
                        case VertexElementUsage.TexCoord4:
                        case VertexElementUsage.TexCoord5:
                        case VertexElementUsage.TexCoord6:
                        case VertexElementUsage.TexCoord7:
                            vertex.TexCoords[GetTexCoordIndex(element.Usage)] = ReadUv(lodData, elementOffset, element.Format);
                            break;

                        case VertexElementUsage.Color0:
                        case VertexElementUsage.Color1:
                            vertex.Colors[GetColorIndex(element.Usage)] = ReadColor(lodData, elementOffset, element.Format);
                            break;

                        case VertexElementUsage.BoneIndices:
                            ReadBoneIndices(lodData, elementOffset, element.Format, vertex.BoneIndices, 0);
                            break;

                        case VertexElementUsage.BoneIndices2:
                            ReadBoneIndices(lodData, elementOffset, element.Format, vertex.BoneIndices, 4);
                            break;

                        case VertexElementUsage.BoneWeights:
                            ReadBoneWeights(lodData, elementOffset, element.Format, vertex.BoneWeights, 0);
                            break;

                        case VertexElementUsage.BoneWeights2:
                            ReadBoneWeights(lodData, elementOffset, element.Format, vertex.BoneWeights, 4);
                            break;
                    }

                    currentStride += element.Size;
                }

                if (currentStride != stream.VertexStride)
                {
                    currentStride = stream.VertexStride;
                }

                if (vertex.Binormal == Vector4.Zero &&
                    vertex.Normal.LengthSquared() > 0.000001f &&
                    new Vector3(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z).LengthSquared() > 0.000001f)
                {
                    float sign = Math.Abs(vertex.Tangent.W) < 0.000001f ? 1.0f : MathF.Sign(vertex.Tangent.W);
                    Vector3 tangent = Vector3.Normalize(new Vector3(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z));
                    Vector3 binormal = Vector3.Normalize(Vector3.Cross(vertex.Normal, tangent) * sign);
                    vertex.Binormal = new Vector4(binormal, sign);
                }
            }

            totalStride += stream.VertexStride;
        }

        return vertices;
    }

    private static bool TryReadIndices(byte[] lodData, MeshSet.MeshLod lod, MeshSet.MeshSection section, int indexElementSize, out int[] indices)
    {
        int indexCount = checked((int)section.PrimitiveCount * 3);
        int indexOffset = checked((int)lod.VertexBufferSize + ((int)section.StartIndex * indexElementSize));
        int indexByteLength = checked(indexCount * indexElementSize);
        if (indexOffset < 0 || indexByteLength < 0 || indexOffset + indexByteLength > lodData.Length)
        {
            indices = [];
            return false;
        }

        indices = new int[indexCount];

        for (int i = 0; i < indices.Length; i++)
        {
            int offset = indexOffset + (i * indexElementSize);
            indices[i] = indexElementSize == sizeof(uint)
                ? checked((int)BinaryPrimitives.ReadUInt32LittleEndian(lodData.AsSpan(offset, sizeof(uint))))
                : BinaryPrimitives.ReadUInt16LittleEndian(lodData.AsSpan(offset, sizeof(ushort)));
        }

        return true;
    }

    private static void WriteSectionVertices(
        byte[] lodData,
        MeshSet.MeshSection section,
        GeometryDeclarationDesc geometryDeclaration,
        MeshImportedSection importedSection)
    {
        IReadOnlyList<MeshDecodedVertex> importedVertices = importedSection.Vertices;
        int totalStride = 0;
        GeometryDeclarationDesc.Stream[] streams = geometryDeclaration.Streams ?? [];
        GeometryDeclarationDesc.Element[] elements = geometryDeclaration.Elements ?? [];
        int streamCount = streams.Length;
        int elementCount = elements.Length;
        for (int streamIndex = 0; streamIndex < streamCount; streamIndex++)
        {
            GeometryDeclarationDesc.Stream stream = streams[streamIndex];
            if (stream.VertexStride == 0)
            {
                continue;
            }

            for (int vertexIndex = 0; vertexIndex < importedVertices.Count; vertexIndex++)
            {
                int streamBaseOffset = checked((int)section.VertexOffset +
                    (totalStride * importedVertices.Count) +
                    (vertexIndex * stream.VertexStride));
                int currentStride = 0;

                MeshDecodedVertex vertex = importedVertices[vertexIndex];

                for (int elementIndex = 0; elementIndex < elementCount; elementIndex++)
                {
                    GeometryDeclarationDesc.Element element = elements[elementIndex];
                    if (element.Usage == VertexElementUsage.Unknown)
                    {
                        continue;
                    }

                    if (element.StreamIndex != streamIndex || currentStride >= stream.VertexStride)
                    {
                        continue;
                    }

                    int elementOffset = streamBaseOffset + currentStride;
                    switch (element.Usage)
                    {
                        case VertexElementUsage.Pos:
                            WritePosition(lodData, elementOffset, element.Format, vertex.Position);
                            break;

                        case VertexElementUsage.Normal:
                            if (importedSection.HasNormals)
                            {
                                WriteNormal(lodData, elementOffset, element.Format, vertex.Normal);
                            }

                            break;

                        case VertexElementUsage.TexCoord0:
                        case VertexElementUsage.TexCoord1:
                        case VertexElementUsage.TexCoord2:
                        case VertexElementUsage.TexCoord3:
                        case VertexElementUsage.TexCoord4:
                        case VertexElementUsage.TexCoord5:
                        case VertexElementUsage.TexCoord6:
                        case VertexElementUsage.TexCoord7:
                            if (importedSection.HasTexCoords[GetTexCoordIndex(element.Usage)])
                            {
                                WriteUv(lodData, elementOffset, element.Format, vertex.TexCoords[GetTexCoordIndex(element.Usage)]);
                            }

                            break;

                        case VertexElementUsage.Color0:
                        case VertexElementUsage.Color1:
                            if (importedSection.HasColors[GetColorIndex(element.Usage)])
                            {
                                WriteColor(lodData, elementOffset, element.Format, vertex.Colors[GetColorIndex(element.Usage)]);
                            }

                            break;

                        case VertexElementUsage.Tangent:
                            if (importedSection.HasTangents)
                            {
                                WriteTangent(lodData, elementOffset, element.Format, vertex.Tangent);
                            }

                            break;

                        case VertexElementUsage.BinormalSign:
                            if (importedSection.HasTangents)
                            {
                                WriteBinormalSign(lodData, elementOffset, element.Format, vertex);
                            }

                            break;

                        case VertexElementUsage.BoneIndices:
                            if (importedSection.HasSkinWeights)
                            {
                                WriteBoneIndices(lodData, elementOffset, element.Format, vertex.BoneIndices, 0);
                            }

                            break;

                        case VertexElementUsage.BoneIndices2:
                            if (importedSection.HasSkinWeights)
                            {
                                WriteBoneIndices(lodData, elementOffset, element.Format, vertex.BoneIndices, 4);
                            }

                            break;

                        case VertexElementUsage.BoneWeights:
                            if (importedSection.HasSkinWeights)
                            {
                                WriteBoneWeights(lodData, elementOffset, element.Format, vertex.BoneWeights, 0);
                            }

                            break;

                        case VertexElementUsage.BoneWeights2:
                            if (importedSection.HasSkinWeights)
                            {
                                WriteBoneWeights(lodData, elementOffset, element.Format, vertex.BoneWeights, 4);
                            }

                            break;
                    }

                    currentStride += element.Size;
                }
            }

            totalStride += stream.VertexStride;
        }
    }

    private static Dictionary<string, MeshImportedSection> ParseObj(string path)
    {
        List<Vector3> positions = [];
        List<Vector2> texCoords = [];
        List<Vector3> normals = [];
        Dictionary<string, MeshImportedSection> sections = new(StringComparer.OrdinalIgnoreCase);

        string currentObjectName = string.Empty;
        MeshImportedSection? currentSection = null;

        foreach (string rawLine in File.ReadLines(path))
        {
            string line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("o ", StringComparison.Ordinal) || line.StartsWith("g ", StringComparison.Ordinal))
            {
                currentObjectName = line[2..].Trim();
                currentSection = new MeshImportedSection();
                sections[currentObjectName] = currentSection;
                continue;
            }

            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (parts[0])
            {
                case "v":
                    positions.Add(new Vector3(
                        ParseFloat(parts[1]),
                        ParseFloat(parts[2]),
                        ParseFloat(parts[3])));
                    break;

                case "vt":
                    texCoords.Add(new Vector2(
                        ParseFloat(parts[1]),
                        1.0f - ParseFloat(parts[2])));
                    break;

                case "vn":
                    normals.Add(Vector3.Normalize(new Vector3(
                        ParseFloat(parts[1]),
                        ParseFloat(parts[2]),
                        ParseFloat(parts[3]))));
                    break;

                case "f":
                    if (currentSection is null)
                    {
                        throw new InvalidOperationException("OBJ face data appeared before any object/group declaration.");
                    }

                    AddFace(currentSection, parts.Skip(1).ToArray(), positions, texCoords, normals);
                    break;
            }
        }

        return sections;
    }

    private static void AddFace(
        MeshImportedSection section,
        string[] faceParts,
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<Vector2> texCoords,
        IReadOnlyList<Vector3> normals)
    {
        if (faceParts.Length < 3)
        {
            throw new InvalidOperationException("OBJ faces must contain at least three vertices.");
        }

        Dictionary<ObjVertexRef, int> vertexMapping = [];
        for (int i = 0; i < section.Vertices.Count; i++)
        {
            MeshDecodedVertex vertex = section.Vertices[i];
            vertexMapping[new ObjVertexRef(i + 1, i + 1, i + 1)] = i;
        }

        List<int> faceIndices = [];
        foreach (string facePart in faceParts)
        {
            string[] indices = facePart.Split('/');
            int positionIndex = ParseObjIndex(indices.ElementAtOrDefault(0), positions.Count);
            int texCoordIndex = ParseObjIndex(indices.ElementAtOrDefault(1), texCoords.Count);
            int normalIndex = ParseObjIndex(indices.ElementAtOrDefault(2), normals.Count);

            MeshDecodedVertex vertex = new()
            {
                Position = positions[positionIndex],
                Normal = normalIndex >= 0 ? normals[normalIndex] : Vector3.UnitY,
                Uv = texCoordIndex >= 0 ? texCoords[texCoordIndex] : Vector2.Zero
            };
            section.HasNormals |= normalIndex >= 0;
            section.HasTexCoords[0] |= texCoordIndex >= 0;

            int existingIndex = section.Vertices.FindIndex(existing =>
                existing.Position == vertex.Position &&
                existing.Normal == vertex.Normal &&
                existing.Uv == vertex.Uv);

            if (existingIndex == -1)
            {
                existingIndex = section.Vertices.Count;
                section.Vertices.Add(vertex);
            }

            faceIndices.Add(existingIndex);
        }

        for (int i = 1; i < faceIndices.Count - 1; i++)
        {
            section.Indices.Add(faceIndices[0]);
            section.Indices.Add(faceIndices[i]);
            section.Indices.Add(faceIndices[i + 1]);
        }
    }

    private static string BuildObjectName(MeshSet.MeshLod lod, MeshSet.MeshSection section)
    {
        string sectionName = string.IsNullOrWhiteSpace(section.Name)
            ? $"section_{section.Index:D2}"
            : SanitizeName(section.Name);
        return $"lod{lod.Index:D2}_sec{section.Index:D2}_{sectionName}";
    }

    private static string SanitizeName(string value)
    {
        StringBuilder builder = new(value.Length);
        foreach (char character in value)
        {
            builder.Append(char.IsLetterOrDigit(character) || character == '_' ? character : '_');
        }

        return builder.ToString();
    }

    private static float ParseFloat(string value)
    {
        return float.Parse(value, CultureInfo.InvariantCulture);
    }

    private static int ParseObjIndex(string? value, int count)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return -1;
        }

        int index = int.Parse(value, CultureInfo.InvariantCulture);
        if (index < 0)
        {
            index = count + index;
        }
        else
        {
            index -= 1;
        }

        return index;
    }

    private readonly record struct DecodeQuality(
        int ValidTriangles,
        int InvalidTriangles,
        int DegenerateTriangles,
        float PositionExtent,
        float BoundsSimilarity);

    private static DecodeQuality EvaluateDecodeQuality(
        IReadOnlyList<MeshDecodedVertex> vertices,
        IReadOnlyList<int> indices,
        MeshSet.MeshSection section)
    {
        if (vertices.Count == 0)
        {
            return new DecodeQuality(0, int.MaxValue, int.MaxValue, 0.0f, float.NegativeInfinity);
        }

        Vector3 min = new(float.MaxValue, float.MaxValue, float.MaxValue);
        Vector3 max = new(float.MinValue, float.MinValue, float.MinValue);
        foreach (MeshDecodedVertex vertex in vertices)
        {
            min = Vector3.Min(min, vertex.Position);
            max = Vector3.Max(max, vertex.Position);
        }

        int validTriangles = 0;
        int invalidTriangles = 0;
        int degenerateTriangles = 0;
        for (int index = 0; index + 2 < indices.Count; index += 3)
        {
            int aIndex = indices[index];
            int bIndex = indices[index + 1];
            int cIndex = indices[index + 2];
            if ((uint)aIndex >= (uint)vertices.Count ||
                (uint)bIndex >= (uint)vertices.Count ||
                (uint)cIndex >= (uint)vertices.Count)
            {
                invalidTriangles++;
                continue;
            }

            Vector3 a = vertices[aIndex].Position;
            Vector3 b = vertices[bIndex].Position;
            Vector3 c = vertices[cIndex].Position;
            Vector3 faceNormal = Vector3.Cross(b - a, c - a);
            if (faceNormal.LengthSquared() < c_faceAreaEpsilon)
            {
                degenerateTriangles++;
                continue;
            }

            validTriangles++;
        }

        Vector3 extent = max - min;
        float positionExtent = extent.LengthSquared();
        float boundsSimilarity = float.NegativeInfinity;
        if (section.BoundingBox.HasValue)
        {
            Vector3 expectedExtent = new(
                section.BoundingBox.Value.Max.X - section.BoundingBox.Value.Min.X,
                section.BoundingBox.Value.Max.Y - section.BoundingBox.Value.Min.Y,
                section.BoundingBox.Value.Max.Z - section.BoundingBox.Value.Min.Z);
            boundsSimilarity = -Vector3.DistanceSquared(extent, expectedExtent);
        }

        return new DecodeQuality(validTriangles, invalidTriangles, degenerateTriangles, positionExtent, boundsSimilarity);
    }

    private static bool IsBetterDecode(DecodeQuality candidate, DecodeQuality best)
    {
        if (candidate.ValidTriangles != best.ValidTriangles)
        {
            return candidate.ValidTriangles > best.ValidTriangles;
        }

        if (candidate.InvalidTriangles != best.InvalidTriangles)
        {
            return candidate.InvalidTriangles < best.InvalidTriangles;
        }

        if (candidate.DegenerateTriangles != best.DegenerateTriangles)
        {
            return candidate.DegenerateTriangles < best.DegenerateTriangles;
        }

        if (!float.IsNegativeInfinity(candidate.BoundsSimilarity) ||
            !float.IsNegativeInfinity(best.BoundsSimilarity))
        {
            if (candidate.BoundsSimilarity != best.BoundsSimilarity)
            {
                return candidate.BoundsSimilarity > best.BoundsSimilarity;
            }
        }

        return candidate.PositionExtent > best.PositionExtent;
    }

    private static string BuildDecodeDiagnostics(
        (MeshDecodedVertex[] Vertices, int[] Indices, DecodeQuality Quality, GeometryDeclarationDesc GeometryDeclaration, int GeometryDeclarationIndex, VertexDecodeMode DecodeMode, int IndexElementSize) candidate)
    {
        GeometryDeclarationDesc.Element? positionElement = GetFirstElement(candidate.GeometryDeclaration, VertexElementUsage.Pos);
        return string.Format(
            CultureInfo.InvariantCulture,
            "Decl {0}, {1}, {2}-bit indices, Pos {3}, {4:N0} valid, {5:N0} degenerate, {6:N0} invalid triangles.",
            candidate.GeometryDeclarationIndex,
            candidate.DecodeMode,
            candidate.IndexElementSize * 8,
            positionElement.HasValue
                ? string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}/S{1}/O{2}",
                    positionElement.Value.Format,
                    positionElement.Value.StreamIndex,
                    positionElement.Value.Offset)
                : "n/a",
            candidate.Quality.ValidTriangles,
            candidate.Quality.DegenerateTriangles,
            candidate.Quality.InvalidTriangles);
    }

    private static bool HasUsableGeometryDeclaration(GeometryDeclarationDesc geometryDeclaration)
    {
        return geometryDeclaration.Elements is not null &&
               geometryDeclaration.Elements.Any(static element => element.Usage != VertexElementUsage.Unknown);
    }

    private static GeometryDeclarationDesc.Element? GetFirstElement(
        GeometryDeclarationDesc geometryDeclaration,
        VertexElementUsage usage)
    {
        if (geometryDeclaration.Elements is null)
        {
            return null;
        }

        foreach (GeometryDeclarationDesc.Element element in geometryDeclaration.Elements)
        {
            if (element.Usage == usage)
            {
                return element;
            }
        }

        return null;
    }

    private static Vector3 ReadPosition(byte[] data, int offset, VertexElementFormat format)
    {
        return format switch
        {
            VertexElementFormat.Float3 or VertexElementFormat.Float4 => new Vector3(
                ReadSingle(data, offset),
                ReadSingle(data, offset + 4),
                ReadSingle(data, offset + 8)),
            VertexElementFormat.Half3 or VertexElementFormat.Half4 => new Vector3(
                ReadHalf(data, offset),
                ReadHalf(data, offset + 2),
                ReadHalf(data, offset + 4)),
            _ => Vector3.Zero
        };
    }

    private static Vector3 ReadNormal(byte[] data, int offset, VertexElementFormat format)
    {
        return format switch
        {
            VertexElementFormat.Float3 or VertexElementFormat.Float4 => new Vector3(
                ReadSingle(data, offset),
                ReadSingle(data, offset + 4),
                ReadSingle(data, offset + 8)),
            VertexElementFormat.Half3 or VertexElementFormat.Half4 => new Vector3(
                ReadHalf(data, offset),
                ReadHalf(data, offset + 2),
                ReadHalf(data, offset + 4)),
            _ => Vector3.Zero
        };
    }

    private static Vector4 ReadTangent(byte[] data, int offset, VertexElementFormat format)
    {
        return format switch
        {
            VertexElementFormat.Float3 => new Vector4(
                ReadSingle(data, offset),
                ReadSingle(data, offset + 4),
                ReadSingle(data, offset + 8),
                1.0f),
            VertexElementFormat.Float4 => new Vector4(
                ReadSingle(data, offset),
                ReadSingle(data, offset + 4),
                ReadSingle(data, offset + 8),
                ReadSingle(data, offset + 12)),
            VertexElementFormat.Half3 => new Vector4(
                ReadHalf(data, offset),
                ReadHalf(data, offset + 2),
                ReadHalf(data, offset + 4),
                1.0f),
            VertexElementFormat.Half4 => new Vector4(
                ReadHalf(data, offset),
                ReadHalf(data, offset + 2),
                ReadHalf(data, offset + 4),
                ReadHalf(data, offset + 6)),
            VertexElementFormat.Byte4N => new Vector4(
                ReadSignedByteNormalized(data[offset]),
                ReadSignedByteNormalized(data[offset + 1]),
                ReadSignedByteNormalized(data[offset + 2]),
                ReadSignedByteNormalized(data[offset + 3])),
            VertexElementFormat.UByte4N => new Vector4(
                ReadUnsignedByteNormalized(data[offset]) * 2.0f - 1.0f,
                ReadUnsignedByteNormalized(data[offset + 1]) * 2.0f - 1.0f,
                ReadUnsignedByteNormalized(data[offset + 2]) * 2.0f - 1.0f,
                ReadUnsignedByteNormalized(data[offset + 3]) * 2.0f - 1.0f),
            VertexElementFormat.Short4N => new Vector4(
                ReadInt16(data, offset) / (float)short.MaxValue,
                ReadInt16(data, offset + 2) / (float)short.MaxValue,
                ReadInt16(data, offset + 4) / (float)short.MaxValue,
                ReadInt16(data, offset + 6) / (float)short.MaxValue),
            VertexElementFormat.UShort4N => new Vector4(
                (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2)) / 65535.0f) * 2.0f - 1.0f,
                (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 2, 2)) / 65535.0f) * 2.0f - 1.0f,
                (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 4, 2)) / 65535.0f) * 2.0f - 1.0f,
                (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 6, 2)) / 65535.0f) * 2.0f - 1.0f),
            _ => Vector4.Zero
        };
    }

    private static Vector3 ReadTangentSpaceNormal(byte[] data, int offset, VertexElementFormat format)
    {
        return format switch
        {
            VertexElementFormat.UByte4N => Vector3.Normalize(new Vector3(
                (data[offset] / 255.0f) * 2.0f - 1.0f,
                (data[offset + 1] / 255.0f) * 2.0f - 1.0f,
                (data[offset + 2] / 255.0f) * 2.0f - 1.0f)),
            VertexElementFormat.UShort4N => Vector3.Normalize(new Vector3(
                (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2)) / 65535.0f) * 2.0f - 1.0f,
                (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 2, 2)) / 65535.0f) * 2.0f - 1.0f,
                (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 4, 2)) / 65535.0f) * 2.0f - 1.0f)),
            _ => Vector3.Zero
        };
    }

    private static Vector2 ReadUv(byte[] data, int offset, VertexElementFormat format)
    {
        return format switch
        {
            VertexElementFormat.Float2 => new Vector2(
                ReadSingle(data, offset),
                ReadSingle(data, offset + 4)),
            VertexElementFormat.Half2 => new Vector2(
                ReadHalf(data, offset),
                ReadHalf(data, offset + 2)),
            VertexElementFormat.Short2N => new Vector2(
                (ReadInt16(data, offset) / (float)short.MaxValue) * 0.5f + 0.5f,
                (ReadInt16(data, offset + 2) / (float)short.MaxValue) * 0.5f + 0.5f),
            _ => Vector2.Zero
        };
    }

    private static Vector4 ReadColor(byte[] data, int offset, VertexElementFormat format)
    {
        return format switch
        {
            VertexElementFormat.Float4 => new Vector4(
                ReadSingle(data, offset),
                ReadSingle(data, offset + 4),
                ReadSingle(data, offset + 8),
                ReadSingle(data, offset + 12)),
            VertexElementFormat.Half4 => new Vector4(
                ReadHalf(data, offset),
                ReadHalf(data, offset + 2),
                ReadHalf(data, offset + 4),
                ReadHalf(data, offset + 6)),
            VertexElementFormat.Byte4 => new Vector4(
                ReadUnsignedByteNormalized(data[offset]),
                ReadUnsignedByteNormalized(data[offset + 1]),
                ReadUnsignedByteNormalized(data[offset + 2]),
                ReadUnsignedByteNormalized(data[offset + 3])),
            VertexElementFormat.Byte4N => new Vector4(
                ReadSignedByteNormalized(data[offset]) * 0.5f + 0.5f,
                ReadSignedByteNormalized(data[offset + 1]) * 0.5f + 0.5f,
                ReadSignedByteNormalized(data[offset + 2]) * 0.5f + 0.5f,
                ReadSignedByteNormalized(data[offset + 3]) * 0.5f + 0.5f),
            VertexElementFormat.UByte4 or VertexElementFormat.UByte4N => new Vector4(
                ReadUnsignedByteNormalized(data[offset]),
                ReadUnsignedByteNormalized(data[offset + 1]),
                ReadUnsignedByteNormalized(data[offset + 2]),
                ReadUnsignedByteNormalized(data[offset + 3])),
            _ => Vector4.One
        };
    }

    private static float ReadBinormalSign(byte[] data, int offset, VertexElementFormat format)
    {
        return format switch
        {
            VertexElementFormat.Float => ReadSingle(data, offset),
            VertexElementFormat.Half => ReadHalf(data, offset),
            VertexElementFormat.Byte4N => ReadSignedByteNormalized(data[offset]),
            VertexElementFormat.UByte4N => (ReadUnsignedByteNormalized(data[offset]) * 2.0f) - 1.0f,
            VertexElementFormat.Byte4 or VertexElementFormat.UByte4 => data[offset] >= 127 ? 1.0f : -1.0f,
            VertexElementFormat.ShortN => ReadInt16(data, offset) / (float)short.MaxValue,
            VertexElementFormat.UShort2N or VertexElementFormat.UShort4N => (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2)) / 65535.0f) * 2.0f - 1.0f,
            _ => 1.0f
        };
    }

    private static void ReadBoneIndices(byte[] data, int offset, VertexElementFormat format, ushort[] destination, int destinationOffset)
    {
        switch (format)
        {
            case VertexElementFormat.Byte4:
            case VertexElementFormat.Byte4N:
            case VertexElementFormat.UByte4:
            case VertexElementFormat.UByte4N:
                destination[destinationOffset + 0] = data[offset];
                destination[destinationOffset + 1] = data[offset + 1];
                destination[destinationOffset + 2] = data[offset + 2];
                destination[destinationOffset + 3] = data[offset + 3];
                break;

            case VertexElementFormat.UShort4:
            case VertexElementFormat.UShort4N:
                destination[destinationOffset + 0] = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
                destination[destinationOffset + 1] = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 2, 2));
                destination[destinationOffset + 2] = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 4, 2));
                destination[destinationOffset + 3] = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 6, 2));
                break;
        }
    }

    private static void ReadBoneWeights(byte[] data, int offset, VertexElementFormat format, float[] destination, int destinationOffset)
    {
        switch (format)
        {
            case VertexElementFormat.Byte4:
            case VertexElementFormat.Byte4N:
            case VertexElementFormat.UByte4:
            case VertexElementFormat.UByte4N:
                destination[destinationOffset + 0] = ReadUnsignedByteNormalized(data[offset]);
                destination[destinationOffset + 1] = ReadUnsignedByteNormalized(data[offset + 1]);
                destination[destinationOffset + 2] = ReadUnsignedByteNormalized(data[offset + 2]);
                destination[destinationOffset + 3] = ReadUnsignedByteNormalized(data[offset + 3]);
                break;

            case VertexElementFormat.Half4:
                destination[destinationOffset + 0] = ReadHalf(data, offset);
                destination[destinationOffset + 1] = ReadHalf(data, offset + 2);
                destination[destinationOffset + 2] = ReadHalf(data, offset + 4);
                destination[destinationOffset + 3] = ReadHalf(data, offset + 6);
                break;

            case VertexElementFormat.Float4:
                destination[destinationOffset + 0] = ReadSingle(data, offset);
                destination[destinationOffset + 1] = ReadSingle(data, offset + 4);
                destination[destinationOffset + 2] = ReadSingle(data, offset + 8);
                destination[destinationOffset + 3] = ReadSingle(data, offset + 12);
                break;
        }
    }

    private static int GetTexCoordIndex(VertexElementUsage usage)
    {
        return (int)usage - (int)VertexElementUsage.TexCoord0;
    }

    private static int GetColorIndex(VertexElementUsage usage)
    {
        return (int)usage - (int)VertexElementUsage.Color0;
    }

    private static float ReadUnsignedByteNormalized(byte value)
    {
        return value / 255.0f;
    }

    private static float ReadSignedByteNormalized(byte value)
    {
        return (sbyte)value / 127.0f;
    }

    private static void WritePosition(byte[] data, int offset, VertexElementFormat format, Vector3 value)
    {
        switch (format)
        {
            case VertexElementFormat.Float3:
            case VertexElementFormat.Float4:
                WriteSingle(data, offset, value.X);
                WriteSingle(data, offset + 4, value.Y);
                WriteSingle(data, offset + 8, value.Z);
                if (format == VertexElementFormat.Float4)
                {
                    WriteSingle(data, offset + 12, 1.0f);
                }

                break;

            case VertexElementFormat.Half3:
            case VertexElementFormat.Half4:
                WriteHalf(data, offset, value.X);
                WriteHalf(data, offset + 2, value.Y);
                WriteHalf(data, offset + 4, value.Z);
                if (format == VertexElementFormat.Half4)
                {
                    WriteHalf(data, offset + 6, 1.0f);
                }

                break;
        }
    }

    private static void WriteNormal(byte[] data, int offset, VertexElementFormat format, Vector3 value)
    {
        Vector3 normal = value.LengthSquared() < 0.000001f ? Vector3.UnitY : Vector3.Normalize(value);
        switch (format)
        {
            case VertexElementFormat.Float3:
            case VertexElementFormat.Float4:
                WriteSingle(data, offset, normal.X);
                WriteSingle(data, offset + 4, normal.Y);
                WriteSingle(data, offset + 8, normal.Z);
                if (format == VertexElementFormat.Float4)
                {
                    WriteSingle(data, offset + 12, 1.0f);
                }

                break;

            case VertexElementFormat.Half3:
            case VertexElementFormat.Half4:
                WriteHalf(data, offset, normal.X);
                WriteHalf(data, offset + 2, normal.Y);
                WriteHalf(data, offset + 4, normal.Z);
                if (format == VertexElementFormat.Half4)
                {
                    WriteHalf(data, offset + 6, 1.0f);
                }

                break;
        }
    }

    private static void WriteTangent(byte[] data, int offset, VertexElementFormat format, Vector4 value)
    {
        Vector3 tangent = new(value.X, value.Y, value.Z);
        if (tangent.LengthSquared() < 0.000001f)
        {
            tangent = Vector3.UnitX;
        }
        else
        {
            tangent = Vector3.Normalize(tangent);
        }

        float tangentW = Math.Abs(value.W) < 0.000001f ? 1.0f : value.W;
        switch (format)
        {
            case VertexElementFormat.Float3:
                WriteSingle(data, offset, tangent.X);
                WriteSingle(data, offset + 4, tangent.Y);
                WriteSingle(data, offset + 8, tangent.Z);
                break;

            case VertexElementFormat.Float4:
                WriteSingle(data, offset, tangent.X);
                WriteSingle(data, offset + 4, tangent.Y);
                WriteSingle(data, offset + 8, tangent.Z);
                WriteSingle(data, offset + 12, tangentW);
                break;

            case VertexElementFormat.Half3:
                WriteHalf(data, offset, tangent.X);
                WriteHalf(data, offset + 2, tangent.Y);
                WriteHalf(data, offset + 4, tangent.Z);
                break;

            case VertexElementFormat.Half4:
                WriteHalf(data, offset, tangent.X);
                WriteHalf(data, offset + 2, tangent.Y);
                WriteHalf(data, offset + 4, tangent.Z);
                WriteHalf(data, offset + 6, tangentW);
                break;

            case VertexElementFormat.Byte4N:
                data[offset] = WriteSignedByteNormalized(tangent.X);
                data[offset + 1] = WriteSignedByteNormalized(tangent.Y);
                data[offset + 2] = WriteSignedByteNormalized(tangent.Z);
                data[offset + 3] = WriteSignedByteNormalized(tangentW);
                break;

            case VertexElementFormat.UByte4N:
                data[offset] = WriteUnsignedByteSignedRange(tangent.X);
                data[offset + 1] = WriteUnsignedByteSignedRange(tangent.Y);
                data[offset + 2] = WriteUnsignedByteSignedRange(tangent.Z);
                data[offset + 3] = WriteUnsignedByteSignedRange(tangentW);
                break;

            case VertexElementFormat.Short4N:
                WriteInt16(data, offset, (short)Math.Clamp(tangent.X * short.MaxValue, short.MinValue, short.MaxValue));
                WriteInt16(data, offset + 2, (short)Math.Clamp(tangent.Y * short.MaxValue, short.MinValue, short.MaxValue));
                WriteInt16(data, offset + 4, (short)Math.Clamp(tangent.Z * short.MaxValue, short.MinValue, short.MaxValue));
                WriteInt16(data, offset + 6, (short)Math.Clamp(tangentW * short.MaxValue, short.MinValue, short.MaxValue));
                break;

            case VertexElementFormat.UShort4N:
                WriteUInt16(data, offset, WriteUnsignedShortSignedRange(tangent.X));
                WriteUInt16(data, offset + 2, WriteUnsignedShortSignedRange(tangent.Y));
                WriteUInt16(data, offset + 4, WriteUnsignedShortSignedRange(tangent.Z));
                WriteUInt16(data, offset + 6, WriteUnsignedShortSignedRange(tangentW));
                break;
        }
    }

    private static void WriteUv(byte[] data, int offset, VertexElementFormat format, Vector2 value)
    {
        switch (format)
        {
            case VertexElementFormat.Float2:
                WriteSingle(data, offset, value.X);
                WriteSingle(data, offset + 4, value.Y);
                break;

            case VertexElementFormat.Half2:
                WriteHalf(data, offset, value.X);
                WriteHalf(data, offset + 2, value.Y);
                break;

            case VertexElementFormat.Short2N:
                WriteInt16(data, offset, (short)Math.Clamp((value.X - 0.5f) * 2.0f * short.MaxValue, short.MinValue, short.MaxValue));
                WriteInt16(data, offset + 2, (short)Math.Clamp((value.Y - 0.5f) * 2.0f * short.MaxValue, short.MinValue, short.MaxValue));
                break;
        }
    }

    private static void WriteColor(byte[] data, int offset, VertexElementFormat format, Vector4 value)
    {
        Vector4 color = Vector4.Clamp(value, Vector4.Zero, Vector4.One);
        switch (format)
        {
            case VertexElementFormat.Float4:
                WriteSingle(data, offset, color.X);
                WriteSingle(data, offset + 4, color.Y);
                WriteSingle(data, offset + 8, color.Z);
                WriteSingle(data, offset + 12, color.W);
                break;

            case VertexElementFormat.Half4:
                WriteHalf(data, offset, color.X);
                WriteHalf(data, offset + 2, color.Y);
                WriteHalf(data, offset + 4, color.Z);
                WriteHalf(data, offset + 6, color.W);
                break;

            case VertexElementFormat.Byte4:
            case VertexElementFormat.UByte4:
            case VertexElementFormat.UByte4N:
                data[offset] = (byte)Math.Clamp(color.X * 255.0f, byte.MinValue, byte.MaxValue);
                data[offset + 1] = (byte)Math.Clamp(color.Y * 255.0f, byte.MinValue, byte.MaxValue);
                data[offset + 2] = (byte)Math.Clamp(color.Z * 255.0f, byte.MinValue, byte.MaxValue);
                data[offset + 3] = (byte)Math.Clamp(color.W * 255.0f, byte.MinValue, byte.MaxValue);
                break;

            case VertexElementFormat.Byte4N:
                data[offset] = WriteSignedByteNormalized((color.X * 2.0f) - 1.0f);
                data[offset + 1] = WriteSignedByteNormalized((color.Y * 2.0f) - 1.0f);
                data[offset + 2] = WriteSignedByteNormalized((color.Z * 2.0f) - 1.0f);
                data[offset + 3] = WriteSignedByteNormalized((color.W * 2.0f) - 1.0f);
                break;
        }
    }

    private static void WriteBoneIndices(byte[] data, int offset, VertexElementFormat format, ushort[] values, int sourceOffset)
    {
        switch (format)
        {
            case VertexElementFormat.Byte4:
            case VertexElementFormat.Byte4N:
            case VertexElementFormat.UByte4:
            case VertexElementFormat.UByte4N:
                data[offset] = (byte)Math.Clamp(values[sourceOffset + 0], byte.MinValue, byte.MaxValue);
                data[offset + 1] = (byte)Math.Clamp(values[sourceOffset + 1], byte.MinValue, byte.MaxValue);
                data[offset + 2] = (byte)Math.Clamp(values[sourceOffset + 2], byte.MinValue, byte.MaxValue);
                data[offset + 3] = (byte)Math.Clamp(values[sourceOffset + 3], byte.MinValue, byte.MaxValue);
                break;

            case VertexElementFormat.UShort4:
            case VertexElementFormat.UShort4N:
                WriteUInt16(data, offset, values[sourceOffset + 0]);
                WriteUInt16(data, offset + 2, values[sourceOffset + 1]);
                WriteUInt16(data, offset + 4, values[sourceOffset + 2]);
                WriteUInt16(data, offset + 6, values[sourceOffset + 3]);
                break;
        }
    }

    private static void WriteBoneWeights(byte[] data, int offset, VertexElementFormat format, float[] values, int sourceOffset)
    {
        switch (format)
        {
            case VertexElementFormat.Byte4:
            case VertexElementFormat.Byte4N:
            case VertexElementFormat.UByte4:
            case VertexElementFormat.UByte4N:
                data[offset] = (byte)Math.Clamp(Math.Round(values[sourceOffset + 0] * 255.0f), byte.MinValue, byte.MaxValue);
                data[offset + 1] = (byte)Math.Clamp(Math.Round(values[sourceOffset + 1] * 255.0f), byte.MinValue, byte.MaxValue);
                data[offset + 2] = (byte)Math.Clamp(Math.Round(values[sourceOffset + 2] * 255.0f), byte.MinValue, byte.MaxValue);
                data[offset + 3] = (byte)Math.Clamp(Math.Round(values[sourceOffset + 3] * 255.0f), byte.MinValue, byte.MaxValue);
                break;

            case VertexElementFormat.Half4:
                WriteHalf(data, offset, values[sourceOffset + 0]);
                WriteHalf(data, offset + 2, values[sourceOffset + 1]);
                WriteHalf(data, offset + 4, values[sourceOffset + 2]);
                WriteHalf(data, offset + 6, values[sourceOffset + 3]);
                break;

            case VertexElementFormat.Float4:
                WriteSingle(data, offset, values[sourceOffset + 0]);
                WriteSingle(data, offset + 4, values[sourceOffset + 1]);
                WriteSingle(data, offset + 8, values[sourceOffset + 2]);
                WriteSingle(data, offset + 12, values[sourceOffset + 3]);
                break;
        }
    }

    private static void WriteBinormalSign(byte[] data, int offset, VertexElementFormat format, MeshDecodedVertex vertex)
    {
        float sign = vertex.Binormal.W;
        if (Math.Abs(sign) < 0.000001f)
        {
            sign = vertex.Tangent.W;
        }

        if (Math.Abs(sign) < 0.000001f)
        {
            sign = 1.0f;
        }

        switch (format)
        {
            case VertexElementFormat.Float:
                WriteSingle(data, offset, sign);
                break;

            case VertexElementFormat.Half:
                WriteHalf(data, offset, sign);
                break;

            case VertexElementFormat.Byte4N:
                data[offset] = WriteSignedByteNormalized(sign);
                break;

            case VertexElementFormat.UByte4N:
                data[offset] = WriteUnsignedByteSignedRange(sign);
                break;

            case VertexElementFormat.Byte4:
            case VertexElementFormat.UByte4:
                data[offset] = sign >= 0.0f ? byte.MaxValue : byte.MinValue;
                break;

            case VertexElementFormat.ShortN:
                WriteInt16(data, offset, (short)Math.Clamp(sign * short.MaxValue, short.MinValue, short.MaxValue));
                break;

            case VertexElementFormat.UShort2N:
            case VertexElementFormat.UShort4N:
                WriteUInt16(data, offset, WriteUnsignedShortSignedRange(sign));
                break;
        }
    }

    private static short ReadInt16(byte[] data, int offset)
    {
        return BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset, sizeof(short)));
    }

    private static float ReadSingle(byte[] data, int offset)
    {
        return BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, sizeof(int))));
    }

    private static float ReadHalf(byte[] data, int offset)
    {
        ushort bits = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, sizeof(ushort)));
        return (float)BitConverter.UInt16BitsToHalf(bits);
    }

    private static void WriteInt16(byte[] data, int offset, short value)
    {
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(offset, sizeof(short)), value);
    }

    private static void WriteUInt16(byte[] data, int offset, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset, sizeof(ushort)), value);
    }

    private static void WriteSingle(byte[] data, int offset, float value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset, sizeof(int)), BitConverter.SingleToInt32Bits(value));
    }

    private static void WriteHalf(byte[] data, int offset, float value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(
            data.AsSpan(offset, sizeof(ushort)),
            BitConverter.HalfToUInt16Bits((Half)value));
    }

    private static byte WriteSignedByteNormalized(float value)
    {
        int normalized = (int)Math.Round(Math.Clamp(value, -1.0f, 1.0f) * 127.0f);
        return unchecked((byte)(sbyte)normalized);
    }

    private static byte WriteUnsignedByteSignedRange(float value)
    {
        return (byte)Math.Clamp(((Math.Clamp(value, -1.0f, 1.0f) + 1.0f) * 0.5f) * 255.0f, byte.MinValue, byte.MaxValue);
    }

    private static ushort WriteUnsignedShortSignedRange(float value)
    {
        return (ushort)Math.Clamp(((Math.Clamp(value, -1.0f, 1.0f) + 1.0f) * 0.5f) * ushort.MaxValue, ushort.MinValue, ushort.MaxValue);
    }
}
