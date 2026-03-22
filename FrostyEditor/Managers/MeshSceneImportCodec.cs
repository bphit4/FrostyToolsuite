using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Assimp;
using Frosty.Sdk.Managers;
using Frosty.Sdk.Managers.Entries;

namespace FrostyEditor.Managers;

public static class MeshSceneImportCodec
{
    public static MeshOperationResult Import(MeshAssetLoadResult load, string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new MeshOperationResult(false, $"Mesh file \"{path}\" was not found.");
            }

            using AssimpContext context = new();
            Scene scene = context.ImportFile(
                path,
                PostProcessSteps.Triangulate |
                PostProcessSteps.ValidateDataStructure |
                PostProcessSteps.FindInvalidData);

            Dictionary<string, MeshObjCodec.MeshImportedSection> importedSections = ReadSections(load, scene);
            if (importedSections.Count == 0)
            {
                return new MeshOperationResult(false, $"The mesh file \"{path}\" does not contain any importable mesh sections.");
            }

            string sourceName = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
            return MeshObjCodec.ImportSections(load, importedSections, sourceName);
        }
        catch (Exception ex)
        {
            return new MeshOperationResult(false, $"Failed to import scene mesh: {ex.Message}");
        }
    }

    private static Dictionary<string, MeshObjCodec.MeshImportedSection> ReadSections(MeshAssetLoadResult load, Scene scene)
    {
        Dictionary<string, MeshObjCodec.MeshImportedSection> sections = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, MeshObjCodec.MeshDecodedSection> existingSections = MeshObjCodec.DecodeSections(load)
            .ToDictionary(section => section.ObjectName, StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string> skeletonBoneNames = LoadSkeletonBoneNames(load.RootObject);

        for (int meshIndex = 0; meshIndex < scene.MeshCount; meshIndex++)
        {
            Assimp.Mesh mesh = scene.Meshes[meshIndex];
            if (mesh.FaceCount == 0)
            {
                continue;
            }

            string meshName = string.IsNullOrWhiteSpace(mesh.Name)
                ? $"mesh_{meshIndex:D2}"
                : mesh.Name;

            MeshObjCodec.MeshImportedSection section = new();
            existingSections.TryGetValue(meshName, out MeshObjCodec.MeshDecodedSection? existingSection);
            for (int vertexIndex = 0; vertexIndex < mesh.VertexCount; vertexIndex++)
            {
                MeshObjCodec.MeshDecodedVertex vertex = new()
                {
                    Position = ToVector3(mesh.Vertices[vertexIndex]),
                    Normal = mesh.HasNormals ? ToVector3(mesh.Normals[vertexIndex]) : Vector3.UnitY
                };
                section.HasNormals |= mesh.HasNormals;

                for (int uvChannelIndex = 0; uvChannelIndex < Math.Min(mesh.TextureCoordinateChannelCount, vertex.TexCoords.Length); uvChannelIndex++)
                {
                    if (!mesh.HasTextureCoords(uvChannelIndex))
                    {
                        continue;
                    }

                    Vector3D uv = mesh.TextureCoordinateChannels[uvChannelIndex][vertexIndex];
                    vertex.TexCoords[uvChannelIndex] = new Vector2(uv.X, uv.Y);
                    section.HasTexCoords[uvChannelIndex] = true;
                }

                for (int colorChannelIndex = 0; colorChannelIndex < Math.Min(mesh.VertexColorChannelCount, vertex.Colors.Length); colorChannelIndex++)
                {
                    if (!mesh.HasVertexColors(colorChannelIndex))
                    {
                        continue;
                    }

                    Color4D color = mesh.VertexColorChannels[colorChannelIndex][vertexIndex];
                    vertex.Colors[colorChannelIndex] = new Vector4(color.R, color.G, color.B, color.A);
                    section.HasColors[colorChannelIndex] = true;
                }

                if (mesh.HasTangentBasis)
                {
                    Vector3 tangent = ToVector3(mesh.Tangents[vertexIndex]);
                    Vector3 binormal = ToVector3(mesh.BiTangents[vertexIndex]);
                    float sign = 1.0f;
                    if (tangent.LengthSquared() > 0.000001f && binormal.LengthSquared() > 0.000001f && vertex.Normal.LengthSquared() > 0.000001f)
                    {
                        sign = MathF.Sign(Vector3.Dot(Vector3.Cross(vertex.Normal, tangent), binormal));
                        if (Math.Abs(sign) < 0.000001f)
                        {
                            sign = 1.0f;
                        }
                    }

                    vertex.Tangent = new Vector4(tangent, sign);
                    vertex.Binormal = new Vector4(binormal, sign);
                    section.HasTangents = true;
                }

                section.Vertices.Add(vertex);
            }

            if (mesh.BoneCount > 0 && existingSection is not null)
            {
                MapSkinning(mesh, existingSection, skeletonBoneNames, section);
            }

            foreach (Face face in mesh.Faces)
            {
                if (face.IndexCount != 3)
                {
                    throw new InvalidOperationException($"Mesh \"{meshName}\" contains a non-triangle face after triangulation.");
                }

                section.Indices.Add(face.Indices[0]);
                section.Indices.Add(face.Indices[1]);
                section.Indices.Add(face.Indices[2]);
            }

            sections[meshName] = section;
        }

        return sections;
    }

    private static void MapSkinning(
        Assimp.Mesh mesh,
        MeshObjCodec.MeshDecodedSection existingSection,
        IReadOnlyList<string> skeletonBoneNames,
        MeshObjCodec.MeshImportedSection importedSection)
    {
        if (existingSection.Lod.Type != Frosty.Sdk.Resources.MeshType.Skinned)
        {
            return;
        }

        Dictionary<string, int> skeletonBoneLookup = skeletonBoneNames
            .Select((name, index) => new { name, index })
            .ToDictionary(item => item.name, item => item.index, StringComparer.OrdinalIgnoreCase);
        Dictionary<int, int> sectionBoneLookup = existingSection.Section.BoneList
            .Select((boneIndex, localIndex) => new { boneIndex, localIndex })
            .ToDictionary(item => (int)item.boneIndex, item => item.localIndex);
        HashSet<string> unmappedBones = new(StringComparer.OrdinalIgnoreCase);

        List<(ushort BoneIndex, float Weight)>[] influencesPerVertex = Enumerable.Range(0, importedSection.Vertices.Count)
            .Select(static _ => new List<(ushort BoneIndex, float Weight)>())
            .ToArray();

        foreach (Bone bone in mesh.Bones)
        {
            if (!skeletonBoneLookup.TryGetValue(bone.Name, out int skeletonBoneIndex))
            {
                unmappedBones.Add(bone.Name);
                continue;
            }

            if (!sectionBoneLookup.TryGetValue(skeletonBoneIndex, out int localBoneIndex))
            {
                unmappedBones.Add(bone.Name);
                continue;
            }

            foreach (VertexWeight vertexWeight in bone.VertexWeights)
            {
                if (vertexWeight.VertexID < 0 || vertexWeight.VertexID >= influencesPerVertex.Length)
                {
                    continue;
                }

                if (vertexWeight.Weight <= 0.0001f)
                {
                    continue;
                }

                influencesPerVertex[vertexWeight.VertexID].Add(((ushort)localBoneIndex, vertexWeight.Weight));
            }
        }

        if (mesh.BoneCount > 0 && skeletonBoneNames.Count == 0)
        {
            throw new InvalidOperationException(
                $"Mesh \"{mesh.Name}\" contains skin weights, but Frosty 2.0 could not resolve a linked skeleton from the target mesh asset.");
        }

        if (unmappedBones.Count > 0)
        {
            string boneList = string.Join(", ", unmappedBones.OrderBy(static bone => bone).Take(8));
            if (unmappedBones.Count > 8)
            {
                boneList += ", ...";
            }

            throw new InvalidOperationException(
                $"Mesh \"{mesh.Name}\" references bones that are not part of the target Frosty mesh section: {boneList}.");
        }

        bool hasSkinning = false;
        for (int vertexIndex = 0; vertexIndex < influencesPerVertex.Length; vertexIndex++)
        {
            List<(ushort BoneIndex, float Weight)> influences = influencesPerVertex[vertexIndex]
                .OrderByDescending(static influence => influence.Weight)
                .Take(8)
                .ToList();
            if (influences.Count == 0)
            {
                continue;
            }

            hasSkinning = true;
            float totalWeight = influences.Sum(static influence => influence.Weight);
            if (totalWeight <= 0.0001f)
            {
                continue;
            }

            MeshObjCodec.MeshDecodedVertex vertex = importedSection.Vertices[vertexIndex];
            for (int influenceIndex = 0; influenceIndex < influences.Count; influenceIndex++)
            {
                vertex.BoneIndices[influenceIndex] = influences[influenceIndex].BoneIndex;
                vertex.BoneWeights[influenceIndex] = influences[influenceIndex].Weight / totalWeight;
            }
        }

        if (mesh.BoneCount > 0 && !hasSkinning)
        {
            throw new InvalidOperationException(
                $"Mesh \"{mesh.Name}\" contains FBX skin clusters, but none of the vertex weights could be mapped onto the target Frosty mesh section.");
        }

        importedSection.HasSkinWeights = hasSkinning;
    }

    private static IReadOnlyList<string> LoadSkeletonBoneNames(object rootObject)
    {
        if (!MeshAssetOperations.TryResolveLinkedEbxAssetEntry(
                rootObject,
                out EbxAssetEntry? skeletonEntry,
                "SkeletonAsset",
                "Skeleton",
                "RigAsset",
                "Rig",
                "MeshSkeleton"))
        {
            return [];
        }

        object skeletonRoot = AssetManager.GetEbxPartition(skeletonEntry!).PrimaryInstance;
        if (!MeshAssetOperations.TryGetMemberValue(skeletonRoot, "BoneNames", out object? boneNamesValue) ||
            boneNamesValue is not System.Collections.IEnumerable boneEnumerable)
        {
            return [];
        }

        List<string> names = [];
        foreach (object? item in boneEnumerable)
        {
            if (item is not null)
            {
                names.Add(item.ToString() ?? string.Empty);
            }
        }

        return names;
    }

    private static Vector3 ToVector3(Vector3D value)
    {
        return new Vector3(value.X, value.Y, value.Z);
    }
}
