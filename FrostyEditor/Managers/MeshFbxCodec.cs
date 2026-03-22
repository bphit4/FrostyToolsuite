using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Frosty.Sdk.Ebx;
using Frosty.Sdk.Managers;
using Frosty.Sdk.Managers.Entries;
using Frosty.Sdk.Resources;
using MeshSetPlugin.Fbx;
using SharpDxVector3 = SharpDX.Vector3;
using SharpDxMatrix = SharpDX.Matrix;

namespace FrostyEditor.Managers;

public static class MeshFbxCodec
{
    private sealed class SkeletonBone
    {
        public required string Name { get; init; }
        public required int ParentIndex { get; init; }
        public required SharpDxVector3 Translation { get; init; }
        public required SharpDxVector3 Rotation { get; init; }
    }

    public static MeshOperationResult Export(MeshAssetLoadResult load, string path)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (!path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
            {
                path += ".fbx";
            }

            using FbxManager manager = new();
            FbxIOSettings settings = new(manager, FbxIOSettings.IOSROOT);
            settings.SetBoolProp(FbxIOSettings.EXP_FBX_MATERIAL, false);
            settings.SetBoolProp(FbxIOSettings.EXP_FBX_TEXTURE, false);
            settings.SetBoolProp(FbxIOSettings.EXP_FBX_GLOBAL_SETTINGS, true);
            manager.SetIOSettings(settings);

            FbxScene scene = new(manager, load.Entry.Filename);
            FbxDocumentInfo documentInfo = new(manager, "SceneInfo")
            {
                Title = "Frosty Editor 2.0 Mesh Export",
                Subject = load.Entry.Name,
                OriginalApplicationVendor = "Frosty Editor 2.0",
                OriginalApplicationName = "Frosty Editor 2.0",
                OriginalApplicationVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "2.0",
                LastSavedApplicationVendor = "Frosty Editor 2.0",
                LastSavedApplicationName = "Frosty Editor 2.0",
                LastSavedApplicationVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "2.0"
            };
            scene.SceneInfo = documentInfo;
            scene.GlobalSettings.SetSystemUnit(FbxSystemUnit.Centimeters);

            List<MeshObjCodec.MeshDecodedSection> sections = MeshObjCodec.DecodeSections(load);
            if (sections.Count == 0)
            {
                return new MeshOperationResult(false, $"{load.Entry.Filename} does not contain any triangle-list sections that Frosty 2.0 can export to FBX yet.");
            }

            FbxGeometryConverter converter = new(manager);
            try
            {
                List<FbxNode>? skeletonNodes = TryCreateSkeleton(scene, load);
                Dictionary<int, FbxNode> lodNodes = [];
                foreach (MeshObjCodec.MeshDecodedSection section in sections)
                {
                    if (!lodNodes.TryGetValue(section.Lod.Index, out FbxNode? lodNode))
                    {
                        lodNode = new FbxNode(scene, $"lod{section.Lod.Index:D2}");
                        scene.RootNode.AddChild(lodNode);
                        lodNodes.Add(section.Lod.Index, lodNode);
                    }

                    FbxNode meshNode = ExportSection(scene, converter, section, skeletonNodes);
                    lodNode.AddChild(meshNode);
                }

                using FbxExporter exporter = new(manager, "Exporter");
                int fileFormat = ResolveFbxWriterFormat(manager);
                if (!exporter.Initialize(path, fileFormat, settings))
                {
                    return new MeshOperationResult(false, $"Failed to initialize FBX exporter for {path}.");
                }

                exporter.Export(scene);
                return new MeshOperationResult(true, $"Exported {load.Entry.Filename} to {path}.");
            }
            finally
            {
                converter.Dispose();
            }
        }
        catch (Exception ex)
        {
            return new MeshOperationResult(false, $"Failed to export FBX mesh: {ex.Message}");
        }
    }

    private static FbxNode ExportSection(
        FbxScene scene,
        FbxGeometryConverter converter,
        MeshObjCodec.MeshDecodedSection section,
        IReadOnlyList<FbxNode>? skeletonNodes)
    {
        string nodeName = string.IsNullOrWhiteSpace(section.Section.Name)
            ? section.ObjectName
            : section.Section.Name;

        FbxNode meshNode = new(scene, nodeName);
        FbxMesh mesh = new(scene, nodeName);
        mesh.InitControlPoints(section.Vertices.Length);

        IntPtr controlPoints = mesh.GetControlPoints();
        for (int i = 0; i < section.Vertices.Length; i++)
        {
            MeshObjCodec.MeshDecodedVertex vertex = section.Vertices[i];
            WriteControlPoint(controlPoints, i, vertex);
        }

        FbxLayerElementNormal layerElementNormal = new(mesh, "Normals")
        {
            MappingMode = EMappingMode.eByControlPoint,
            ReferenceMode = EReferenceMode.eDirect
        };

        bool hasTangents = HasAnyUsage(section, VertexElementUsage.Tangent, VertexElementUsage.Binormal, VertexElementUsage.BinormalSign);
        bool[] uvChannels = GetUsageChannels(section, VertexElementUsage.TexCoord0, 8);
        bool[] colorChannels = GetUsageChannels(section, VertexElementUsage.Color0, 2);

        Dictionary<int, FbxLayerElementUV> layerElementUvs = [];
        Dictionary<int, FbxLayerElementVertexColor> layerElementColors = [];
        FbxLayerElementTangent? layerElementTangent = hasTangents ? new FbxLayerElementTangent(mesh, "Tangents")
        {
            MappingMode = EMappingMode.eByControlPoint,
            ReferenceMode = EReferenceMode.eDirect
        } : null;

        FbxLayerElementBinormal? layerElementBinormal = hasTangents ? new FbxLayerElementBinormal(mesh, "Binormals")
        {
            MappingMode = EMappingMode.eByControlPoint,
            ReferenceMode = EReferenceMode.eDirect
        } : null;

        for (int channelIndex = 0; channelIndex < uvChannels.Length; channelIndex++)
        {
            if (!uvChannels[channelIndex])
            {
                continue;
            }

            layerElementUvs[channelIndex] = new FbxLayerElementUV(mesh, $"UV{channelIndex}")
            {
                MappingMode = EMappingMode.eByControlPoint,
                ReferenceMode = EReferenceMode.eDirect
            };
        }

        for (int channelIndex = 0; channelIndex < colorChannels.Length; channelIndex++)
        {
            if (!colorChannels[channelIndex])
            {
                continue;
            }

            layerElementColors[channelIndex] = new FbxLayerElementVertexColor(mesh, $"Color{channelIndex}")
            {
                MappingMode = EMappingMode.eByControlPoint,
                ReferenceMode = EReferenceMode.eDirect
            };
        }

        for (int i = 0; i < section.Vertices.Length; i++)
        {
            MeshObjCodec.MeshDecodedVertex vertex = section.Vertices[i];
            Vector3 normal = NormalizeOrFallback(vertex.Normal, Vector3.UnitY);
            layerElementNormal.DirectArray!.Add(normal.X, normal.Y, normal.Z, 0.0);

            foreach ((int channelIndex, FbxLayerElementUV layerElementUv) in layerElementUvs)
            {
                Vector2 uv = vertex.TexCoords[channelIndex];
                layerElementUv.DirectArray!.Add(uv.X, 1.0 - uv.Y);
            }

            foreach ((int channelIndex, FbxLayerElementVertexColor layerElementColor) in layerElementColors)
            {
                Vector4 color = vertex.Colors[channelIndex];
                layerElementColor.DirectArray!.Add(color.X, color.Y, color.Z, color.W);
            }

            if (layerElementTangent is not null && layerElementBinormal is not null)
            {
                Vector3 tangent = NormalizeOrFallback(new Vector3(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z), Vector3.UnitX);
                Vector3 binormal = NormalizeOrFallback(new Vector3(vertex.Binormal.X, vertex.Binormal.Y, vertex.Binormal.Z), Vector3.Cross(normal, tangent));
                layerElementTangent.DirectArray!.Add(tangent.X, tangent.Y, tangent.Z, vertex.Tangent.W);
                layerElementBinormal.DirectArray!.Add(binormal.X, binormal.Y, binormal.Z, vertex.Binormal.W);
            }
        }

        for (int i = 0; i < section.Indices.Length; i += 3)
        {
            mesh.BeginPolygon();
            mesh.AddPolygon(section.Indices[i]);
            mesh.AddPolygon(section.Indices[i + 1]);
            mesh.AddPolygon(section.Indices[i + 2]);
            mesh.EndPolygon();
        }

        FbxLayer layer = GetOrCreateLayer(mesh, 0);
        layer.SetNormals(layerElementNormal);

        if (layerElementUvs.TryGetValue(0, out FbxLayerElementUV? primaryUv))
        {
            layer.SetUVs(primaryUv);
        }

        if (layerElementColors.TryGetValue(0, out FbxLayerElementVertexColor? primaryColor))
        {
            layer.SetVertexColors(primaryColor);
        }

        if (layerElementTangent is not null && layerElementBinormal is not null)
        {
            layer.SetTangents(layerElementTangent);
            layer.SetBinormals(layerElementBinormal);
        }

        for (int channelIndex = 1; channelIndex < uvChannels.Length; channelIndex++)
        {
            if (layerElementUvs.TryGetValue(channelIndex, out FbxLayerElementUV? layerElementUv))
            {
                GetOrCreateLayer(mesh, channelIndex).SetUVs(layerElementUv);
            }
        }

        for (int channelIndex = 1; channelIndex < colorChannels.Length; channelIndex++)
        {
            if (layerElementColors.TryGetValue(channelIndex, out FbxLayerElementVertexColor? layerElementColor))
            {
                GetOrCreateLayer(mesh, channelIndex).SetVertexColors(layerElementColor);
            }
        }

        mesh.BuildMeshEdgeArray();
        converter.ComputeEdgeSmoothingFromNormals(mesh);
        if (section.Lod.Type == MeshType.Skinned && skeletonNodes is not null)
        {
            AddSkin(scene, mesh, section, skeletonNodes);
        }

        meshNode.SetNodeAttribute(mesh);
        return meshNode;
    }

    private static List<FbxNode>? TryCreateSkeleton(FbxScene scene, MeshAssetLoadResult load)
    {
        if (load.Mesh.Type != MeshType.Skinned)
        {
            return null;
        }

        if (!MeshAssetOperations.TryResolveLinkedEbxAssetEntry(
                load.RootObject,
                out EbxAssetEntry? skeletonEntry,
                "SkeletonAsset",
                "Skeleton",
                "RigAsset",
                "Rig",
                "MeshSkeleton"))
        {
            return null;
        }

        List<SkeletonBone> bones = ReadSkeletonBones(skeletonEntry!);
        if (bones.Count == 0)
        {
            return null;
        }

        List<FbxNode> boneNodes = new(bones.Count);
        for (int boneIndex = 0; boneIndex < bones.Count; boneIndex++)
        {
            SkeletonBone bone = bones[boneIndex];
            FbxSkeleton skeletonAttribute = new(scene, bone.Name);
            skeletonAttribute.SetSkeletonType(bone.ParentIndex < 0 ? FbxSkeleton.EType.eRoot : FbxSkeleton.EType.eLimbNode);
            skeletonAttribute.Size = 1.0;

            FbxNode boneNode = new(scene, bone.Name)
            {
                LclTranslation = bone.Translation,
                LclRotation = bone.Rotation,
                LclScaling = new SharpDxVector3(1.0f, 1.0f, 1.0f)
            };
            boneNode.SetNodeAttribute(skeletonAttribute);
            boneNodes.Add(boneNode);
        }

        for (int boneIndex = 0; boneIndex < bones.Count; boneIndex++)
        {
            int parentIndex = bones[boneIndex].ParentIndex;
            if (parentIndex >= 0 && parentIndex < boneNodes.Count)
            {
                boneNodes[parentIndex].AddChild(boneNodes[boneIndex]);
            }
            else
            {
                scene.RootNode.AddChild(boneNodes[boneIndex]);
            }
        }

        return boneNodes;
    }

    private static List<SkeletonBone> ReadSkeletonBones(EbxAssetEntry skeletonEntry)
    {
        EbxPartition partition = AssetManager.GetEbxPartition(skeletonEntry);
        object rootObject = partition.PrimaryInstance;

        if (!MeshAssetOperations.TryGetMemberValue(rootObject, "BoneNames", out object? boneNamesValue) ||
            !TryEnumerateValues(boneNamesValue, out List<object?> boneNames))
        {
            return [];
        }

        if (!MeshAssetOperations.TryGetMemberValue(rootObject, "Hierarchy", out object? hierarchyValue) ||
            !TryEnumerateValues(hierarchyValue, out List<object?> hierarchy))
        {
            return [];
        }

        object? posesValue;
        if (!MeshAssetOperations.TryGetMemberValue(rootObject, "LocalPose", out posesValue) &&
            !MeshAssetOperations.TryGetMemberValue(rootObject, "ModelPose", out posesValue))
        {
            return [];
        }

        if (!TryEnumerateValues(posesValue, out List<object?> poses))
        {
            return [];
        }

        int boneCount = Math.Min(boneNames.Count, Math.Min(hierarchy.Count, poses.Count));
        List<SkeletonBone> bones = new(boneCount);
        for (int boneIndex = 0; boneIndex < boneCount; boneIndex++)
        {
            if (!TryReadPose(poses[boneIndex], out SharpDxVector3 translation, out SharpDxVector3 rotation))
            {
                translation = new SharpDxVector3();
                rotation = new SharpDxVector3();
            }

            bones.Add(new SkeletonBone
            {
                Name = boneNames[boneIndex]?.ToString() ?? $"bone_{boneIndex:D3}",
                ParentIndex = ConvertToInt(hierarchy[boneIndex]),
                Translation = translation,
                Rotation = rotation
            });
        }

        return bones;
    }

    private static void AddSkin(FbxScene scene, FbxMesh mesh, MeshObjCodec.MeshDecodedSection section, IReadOnlyList<FbxNode> skeletonNodes)
    {
        FbxSkin skin = new(scene, $"{section.ObjectName}_skin");
        FbxCluster?[] boneClusters = new FbxCluster[skeletonNodes.Count];
        int maxBoneIndex = section.Vertices
            .SelectMany(static vertex => vertex.BoneIndices)
            .DefaultIfEmpty()
            .Max();
        bool useSectionBoneList = section.Section.BoneList.Count > maxBoneIndex;

        for (int vertexIndex = 0; vertexIndex < section.Vertices.Length; vertexIndex++)
        {
            MeshObjCodec.MeshDecodedVertex vertex = section.Vertices[vertexIndex];
            for (int influenceIndex = 0; influenceIndex < vertex.BoneWeights.Length; influenceIndex++)
            {
                float weight = vertex.BoneWeights[influenceIndex];
                if (weight <= 0.0001f)
                {
                    continue;
                }

                int skeletonIndex = vertex.BoneIndices[influenceIndex];
                if (useSectionBoneList &&
                    skeletonIndex >= 0 &&
                    skeletonIndex < section.Section.BoneList.Count)
                {
                    skeletonIndex = section.Section.BoneList[skeletonIndex];
                }

                if (skeletonIndex < 0 || skeletonIndex >= skeletonNodes.Count)
                {
                    continue;
                }

                if (boneClusters[skeletonIndex] is null)
                {
                    FbxCluster cluster = new(scene, skeletonNodes[skeletonIndex].Name);
                    cluster.SetLink(skeletonNodes[skeletonIndex]);
                    cluster.SetLinkMode(FbxCluster.ELinkMode.eTotalOne);
                    cluster.SetTransformLinkMatrix(skeletonNodes[skeletonIndex].EvaluateGlobalTransform());
                    boneClusters[skeletonIndex] = cluster;
                    skin.AddCluster(cluster);
                }

                boneClusters[skeletonIndex]!.AddControlPointIndex(vertexIndex, weight);
            }
        }

        mesh.AddDeformer(skin);
    }

    private static bool HasAnyUsage(MeshObjCodec.MeshDecodedSection section, params VertexElementUsage[] usages)
    {
        return section.GeometryDeclaration.Elements
            .Take(section.GeometryDeclaration.ElementCount)
            .Any(element => usages.Contains(element.Usage));
    }

    private static bool[] GetUsageChannels(MeshObjCodec.MeshDecodedSection section, VertexElementUsage baseUsage, int count)
    {
        bool[] channels = new bool[count];
        foreach (GeometryDeclarationDesc.Element element in section.GeometryDeclaration.Elements.Take(section.GeometryDeclaration.ElementCount))
        {
            int usageIndex = (int)element.Usage - (int)baseUsage;
            if (usageIndex >= 0 && usageIndex < count)
            {
                channels[usageIndex] = true;
            }
        }

        return channels;
    }

    private static FbxLayer GetOrCreateLayer(FbxMesh mesh, int index)
    {
        FbxLayer? layer = mesh.GetLayer(index);
        while (layer is null)
        {
            mesh.CreateLayer();
            layer = mesh.GetLayer(index);
        }

        return layer;
    }

    private static Vector3 NormalizeOrFallback(Vector3 value, Vector3 fallback)
    {
        if (value.LengthSquared() < 0.000001f)
        {
            return fallback.LengthSquared() < 0.000001f ? Vector3.UnitY : Vector3.Normalize(fallback);
        }

        return Vector3.Normalize(value);
    }

    private static int ResolveFbxWriterFormat(FbxManager manager)
    {
        int fallback = -1;
        for (int i = 0; i < manager.IOPluginRegistry.WriterFormatCount; i++)
        {
            if (!manager.IOPluginRegistry.WriterIsFBX(i))
            {
                continue;
            }

            string description = manager.IOPluginRegistry.GetWriterFormatDescription(i);
            if (fallback == -1)
            {
                fallback = i;
            }

            if (description.Contains("binary", StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return fallback;
    }

    private static void WriteControlPoint(IntPtr controlPoints, int index, MeshObjCodec.MeshDecodedVertex vertex)
    {
        long offset = index * (sizeof(double) * 4L);
        WriteDouble(controlPoints, offset, vertex.Position.X);
        WriteDouble(controlPoints, offset + sizeof(double), vertex.Position.Y);
        WriteDouble(controlPoints, offset + (sizeof(double) * 2L), vertex.Position.Z);
        WriteDouble(controlPoints, offset + (sizeof(double) * 3L), 1.0);
    }

    private static void WriteDouble(IntPtr destination, long offset, double value)
    {
        long bits = BitConverter.DoubleToInt64Bits(value);
        System.Runtime.InteropServices.Marshal.WriteInt64(destination, (int)offset, bits);
    }

    private static bool TryEnumerateValues(object? value, out List<object?> items)
    {
        if (value is null || value is string || value is not IEnumerable enumerable)
        {
            items = [];
            return false;
        }

        items = [];
        foreach (object? item in enumerable)
        {
            items.Add(item);
        }

        return items.Count > 0;
    }

    private static bool TryReadPose(object? poseValue, out SharpDxVector3 translation, out SharpDxVector3 rotation)
    {
        translation = new SharpDxVector3();
        rotation = new SharpDxVector3();
        if (poseValue is null)
        {
            return false;
        }

        if (TryReadLinearTransform(poseValue, out SharpDxMatrix matrix))
        {
            translation = new SharpDxVector3(matrix.M41, matrix.M42, matrix.M43);
            rotation = ExtractEulerAngles(matrix);
            return true;
        }

        if (MeshAssetOperations.TryGetMemberValue(poseValue, "Translation", out object? translationValue) ||
            MeshAssetOperations.TryGetMemberValue(poseValue, "Trans", out translationValue))
        {
            translation = ReadVector3(translationValue);
        }

        if (MeshAssetOperations.TryGetMemberValue(poseValue, "Rotation", out object? rotationValue) ||
            MeshAssetOperations.TryGetMemberValue(poseValue, "Quaternion", out rotationValue))
        {
            Quaternion quaternion = ReadQuaternion(rotationValue);
            rotation = ExtractEulerAngles(SharpDxMatrix.RotationQuaternion(new SharpDX.Quaternion(quaternion.X, quaternion.Y, quaternion.Z, quaternion.W)));
            return true;
        }

        return false;
    }

    private static bool TryReadLinearTransform(object poseValue, out SharpDxMatrix matrix)
    {
        matrix = SharpDxMatrix.Identity;
        if (!MeshAssetOperations.TryGetMemberValue(poseValue, "Right", out object? rightValue) ||
            !MeshAssetOperations.TryGetMemberValue(poseValue, "Up", out object? upValue) ||
            !MeshAssetOperations.TryGetMemberValue(poseValue, "Forward", out object? forwardValue))
        {
            return false;
        }

        object? translationValue = null;
        if (!MeshAssetOperations.TryGetMemberValue(poseValue, "Trans", out translationValue) &&
            !MeshAssetOperations.TryGetMemberValue(poseValue, "Translation", out translationValue))
        {
            return false;
        }

        SharpDxVector3 right = ReadVector3(rightValue);
        SharpDxVector3 up = ReadVector3(upValue);
        SharpDxVector3 forward = ReadVector3(forwardValue);
        SharpDxVector3 translation = ReadVector3(translationValue);
        matrix = new SharpDxMatrix(
            right.X, right.Y, right.Z, 0.0f,
            up.X, up.Y, up.Z, 0.0f,
            forward.X, forward.Y, forward.Z, 0.0f,
            translation.X, translation.Y, translation.Z, 1.0f);
        return true;
    }

    private static SharpDxVector3 ReadVector3(object? value)
    {
        if (value is null)
        {
            return new SharpDxVector3();
        }

        float x = ReadFloatMember(value, "X", "x");
        float y = ReadFloatMember(value, "Y", "y");
        float z = ReadFloatMember(value, "Z", "z");
        return new SharpDxVector3(x, y, z);
    }

    private static Quaternion ReadQuaternion(object? value)
    {
        if (value is null)
        {
            return Quaternion.Identity;
        }

        float x = ReadFloatMember(value, "X", "x");
        float y = ReadFloatMember(value, "Y", "y");
        float z = ReadFloatMember(value, "Z", "z");
        float w = ReadFloatMember(value, "W", "w");
        if (Math.Abs(w) < 0.000001f && Math.Abs(x) < 0.000001f && Math.Abs(y) < 0.000001f && Math.Abs(z) < 0.000001f)
        {
            return Quaternion.Identity;
        }

        return Quaternion.Normalize(new Quaternion(x, y, z, w));
    }

    private static float ReadFloatMember(object value, params string[] names)
    {
        foreach (string name in names)
        {
            if (MeshAssetOperations.TryGetMemberValue(value, name, out object? memberValue))
            {
                return Convert.ToSingle(memberValue);
            }
        }

        return 0.0f;
    }

    private static int ConvertToInt(object? value)
    {
        if (value is null)
        {
            return -1;
        }

        try
        {
            return Convert.ToInt32(value);
        }
        catch
        {
            return -1;
        }
    }

    private static SharpDxVector3 ExtractEulerAngles(SharpDxMatrix matrix)
    {
        float piOver180 = (float)(Math.PI / 180.0);
        SharpDxVector3 eulerRotation = new();
        float yzMagnitude = (float)Math.Sqrt((matrix.M11 * matrix.M11) + (matrix.M12 * matrix.M12));

        if (yzMagnitude > 0.001f)
        {
            eulerRotation.X = (float)Math.Atan2(matrix.M23, matrix.M33);
            eulerRotation.Y = (float)Math.Atan2(-matrix.M13, yzMagnitude);
            eulerRotation.Z = (float)Math.Atan2(matrix.M12, matrix.M11);
        }
        else
        {
            eulerRotation.X = (float)Math.Atan2(-matrix.M32, matrix.M22);
            eulerRotation.Y = (float)Math.Atan2(-matrix.M13, yzMagnitude);
            eulerRotation.Z = 0.0f;
        }

        eulerRotation.X /= piOver180;
        eulerRotation.Y /= piOver180;
        eulerRotation.Z /= piOver180;
        return eulerRotation;
    }
}
