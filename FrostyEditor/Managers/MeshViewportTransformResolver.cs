using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Frosty.Sdk.Ebx;
using Frosty.Sdk.Interfaces;
using Frosty.Sdk.Managers;
using Frosty.Sdk.Managers.Entries;
using Frosty.Sdk.Resources;

namespace FrostyEditor.Managers;

internal static class MeshViewportTransformResolver
{
    public static Matrix4x4[]? ResolvePalette(MeshAssetLoadResult load, MeshObjCodec.MeshDecodedSection section)
    {
        return section.Lod.Type switch
        {
            MeshType.Composite => ResolveCompositePalette(section.Lod),
            MeshType.Skinned => ResolveSkinnedPalette(load),
            _ => null
        };
    }

    public static void TransformVertex(
        MeshObjCodec.MeshDecodedSection section,
        MeshObjCodec.MeshDecodedVertex vertex,
        Matrix4x4[]? palette,
        out Vector3 position,
        out Vector3 normal)
    {
        position = vertex.Position;
        normal = vertex.Normal.LengthSquared() > 0.000001f ? Vector3.Normalize(vertex.Normal) : Vector3.UnitZ;
        if (section.Lod.Type == MeshType.Skinned)
        {
            // Madden skinned mesh preview data already appears to decode in the usable rest pose.
            // Applying the viewport skin palette a second time twists limbs and head geometry.
            return;
        }

        if (palette is null || palette.Length == 0)
        {
            return;
        }

        Vector3 weightedPosition = Vector3.Zero;
        Vector3 weightedNormal = Vector3.Zero;
        float totalWeight = 0.0f;
        int fallbackIndex = -1;

        for (int influenceIndex = 0; influenceIndex < vertex.BoneIndices.Length; influenceIndex++)
        {
            ushort rawIndex = vertex.BoneIndices[influenceIndex];
            int paletteIndex = MapPaletteIndex(section, rawIndex, palette.Length);
            if (paletteIndex < 0 || paletteIndex >= palette.Length)
            {
                paletteIndex = rawIndex & 0x7FFF;
                if (paletteIndex < 0 || paletteIndex >= palette.Length)
                {
                    continue;
                }
            }

            fallbackIndex = fallbackIndex < 0 ? paletteIndex : fallbackIndex;
            float weight = Math.Max(0.0f, vertex.BoneWeights[influenceIndex]);
            if (weight <= 0.000001f)
            {
                continue;
            }

            Matrix4x4 transform = palette[paletteIndex];
            weightedPosition += Vector3.Transform(vertex.Position, transform) * weight;
            weightedNormal += Vector3.TransformNormal(normal, transform) * weight;
            totalWeight += weight;
        }

        if (totalWeight <= 0.000001f)
        {
            if (fallbackIndex < 0)
            {
                return;
            }

            Matrix4x4 fallbackTransform = palette[fallbackIndex];
            position = Vector3.Transform(vertex.Position, fallbackTransform);
            Vector3 fallbackNormal = Vector3.TransformNormal(normal, fallbackTransform);
            if (fallbackNormal.LengthSquared() > 0.000001f)
            {
                normal = Vector3.Normalize(fallbackNormal);
            }

            return;
        }

        position = weightedPosition / totalWeight;
        if (weightedNormal.LengthSquared() > 0.000001f)
        {
            normal = Vector3.Normalize(weightedNormal);
        }
    }

    private static int MapPaletteIndex(MeshObjCodec.MeshDecodedSection section, ushort rawIndex, int paletteLength)
    {
        int logicalIndex = rawIndex & 0x7FFF;

        if (section.Lod.Type == MeshType.Composite)
        {
            if (section.Section.BoneList.Count > logicalIndex)
            {
                return NormalizeMappedBoneIndex(section.Section.BoneList[logicalIndex]);
            }

            if (section.Lod.PartTransforms.Count > logicalIndex)
            {
                return logicalIndex;
            }

            return -1;
        }

        if (section.Lod.Type == MeshType.Skinned)
        {
            // Old Frosty uses the section-local bone palette directly for skinned sections.
            // Falling through to the lod-wide bone array when a section bone list exists can
            // remap influences onto the wrong skeleton palette and twist body meshes.
            if (section.Section.BoneList.Count > 0)
            {
                return section.Section.BoneList.Count > logicalIndex
                    ? NormalizeMappedBoneIndex(section.Section.BoneList[logicalIndex])
                    : -1;
            }

            if (section.Lod.BoneIndexArray.Count > logicalIndex)
            {
                return NormalizeMappedBoneIndex((int)section.Lod.BoneIndexArray[logicalIndex]);
            }

            return NormalizeMappedBoneIndex(rawIndex);
        }

        return -1;
    }

    private static int NormalizeMappedBoneIndex(int mappedIndex)
    {
        if ((mappedIndex & 0x8000) != 0)
        {
            return mappedIndex & 0x7FFF;
        }

        return mappedIndex;
    }

    private static Matrix4x4[]? ResolveCompositePalette(MeshSet.MeshLod lod)
    {
        if (lod.PartTransforms.Count == 0)
        {
            return null;
        }

        return lod.PartTransforms.Select(ToMatrix).ToArray();
    }

    private static Matrix4x4[]? ResolveSkinnedPalette(MeshAssetLoadResult load)
    {
        if (!TryResolveSkeletonEntry(load.RootObject, out EbxAssetEntry? skeletonEntry) ||
            skeletonEntry is null)
        {
            return null;
        }

        object skeletonRoot = AssetManager.GetEbxPartition(skeletonEntry).PrimaryInstance;
        if (!TryEnumerateValues(GetMemberValueOrNull(skeletonRoot, "Hierarchy"), out List<object?> hierarchyValues))
        {
            return null;
        }

        List<Matrix4x4>? localPoses = ReadPoseMatrices(skeletonRoot, "LocalPose");
        List<Matrix4x4>? modelPoses = ReadPoseMatrices(skeletonRoot, "ModelPose");
        List<Matrix4x4>? inverseModelPoses = ReadPoseMatrices(skeletonRoot, "InverseModelPose");
        int boneCount = hierarchyValues.Count;
        if (boneCount == 0)
        {
            return null;
        }

        int[] parents = new int[boneCount];
        for (int boneIndex = 0; boneIndex < boneCount; boneIndex++)
        {
            parents[boneIndex] = ConvertToInt(hierarchyValues[boneIndex]);
        }

        Matrix4x4[] worldPoses = new Matrix4x4[boneCount];
        if (localPoses is not null && localPoses.Count > 0)
        {
            bool[] worldPoseReady = new bool[boneCount];
            for (int boneIndex = 0; boneIndex < boneCount; boneIndex++)
            {
                worldPoses[boneIndex] = GetWorldPose(boneIndex, localPoses!, modelPoses, parents, worldPoses, worldPoseReady);
            }
        }
        else
        {
            for (int boneIndex = 0; boneIndex < boneCount; boneIndex++)
            {
                worldPoses[boneIndex] = GetPoseOrIdentity(modelPoses, boneIndex);
            }
        }

        Matrix4x4[] palette = new Matrix4x4[boneCount];
        for (int boneIndex = 0; boneIndex < boneCount; boneIndex++)
        {
            Matrix4x4 inverseBindPose = Matrix4x4.Identity;
            if (inverseModelPoses is not null && boneIndex < inverseModelPoses.Count)
            {
                inverseBindPose = inverseModelPoses[boneIndex];
            }
            else if (modelPoses is not null && boneIndex < modelPoses.Count)
            {
                inverseBindPose = TryInvert(modelPoses[boneIndex]);
            }
            else
            {
                inverseBindPose = TryInvert(worldPoses[boneIndex]);
            }

            palette[boneIndex] = inverseBindPose * worldPoses[boneIndex];
        }

        return palette;
    }

    private static bool TryResolveSkeletonEntry(object? rootObject, out EbxAssetEntry? skeletonEntry)
    {
        return TryResolveSkeletonEntry(rootObject, out skeletonEntry, new HashSet<object>(ReferenceEqualityComparer.Instance), 0);
    }

    private static bool TryResolveSkeletonEntry(
        object? rootObject,
        out EbxAssetEntry? skeletonEntry,
        HashSet<object> visited,
        int depth)
    {
        skeletonEntry = null;
        if (rootObject is null || depth > 6 || !ShouldInspectValue(rootObject))
        {
            return false;
        }

        if (!visited.Add(rootObject))
        {
            return false;
        }

        if (TryResolveSkeletonEntryFromCandidate(rootObject, out skeletonEntry))
        {
            return true;
        }

        if (TryResolveCandidateEntry(rootObject, out skeletonEntry))
        {
            return true;
        }

        foreach (object? candidate in EnumerateSkeletonSearchCandidates(rootObject, depth))
        {
            if (candidate is not null &&
                !ReferenceEquals(candidate, rootObject) &&
                TryResolveSkeletonEntry(candidate, out skeletonEntry, visited, depth + 1))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveCandidateEntry(object candidate, out EbxAssetEntry? skeletonEntry)
    {
        skeletonEntry = null;
        if (!MeshAssetOperations.TryResolveLinkedEbxAssetEntry(candidate, out EbxAssetEntry? directEntry) ||
            directEntry is null)
        {
            return false;
        }

        if (!IsSkeletonEntry(directEntry))
        {
            return false;
        }

        skeletonEntry = directEntry;
        return true;
    }

    private static bool TryResolveSkeletonEntryFromCandidate(object candidate, out EbxAssetEntry? skeletonEntry)
    {
        if (!MeshAssetOperations.TryResolveLinkedEbxAssetEntry(
                candidate,
                out EbxAssetEntry? resolvedEntry,
                "SkeletonAsset",
                "Skeleton",
                "RigAsset",
                "Rig",
                "MeshSkeleton") ||
            resolvedEntry is null ||
            !IsSkeletonEntry(resolvedEntry))
        {
            skeletonEntry = null;
            return false;
        }

        skeletonEntry = resolvedEntry;
        return true;
    }

    private static IEnumerable<object?> EnumerateSkeletonSearchCandidates(object rootObject, int depth)
    {
        foreach (MemberInfo member in EnumerateInstanceMembers(rootObject.GetType())
            .OrderByDescending(member => GetSearchPriority(member.Name)))
        {
            int priority = GetSearchPriority(member.Name);
            if (priority <= 0 || (depth > 0 && priority < 2))
            {
                continue;
            }

            object? value = GetMemberValueOrNull(rootObject, member);
            if (value is null || !ShouldInspectValue(value))
            {
                continue;
            }

            if (value is IEnumerable enumerable && value is not string)
            {
                int count = 0;
                foreach (object? item in enumerable)
                {
                    if (item is not null && ShouldInspectValue(item))
                    {
                        yield return item;
                    }

                    count++;
                    if (count >= 32)
                    {
                        break;
                    }
                }

                continue;
            }

            yield return value;
        }
    }

    private static bool IsSkeletonEntry(EbxAssetEntry entry)
    {
        if (entry.Type.Contains("Skeleton", StringComparison.OrdinalIgnoreCase) ||
            entry.Type.Contains("Rig", StringComparison.OrdinalIgnoreCase) ||
            entry.Name.Contains("skeleton", StringComparison.OrdinalIgnoreCase) ||
            entry.Name.Contains("rig", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            object root = AssetManager.GetEbxPartition(entry).PrimaryInstance;
            return LooksLikeSkeletonRoot(root);
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikeSkeletonRoot(object rootObject)
    {
        return MeshAssetOperations.TryGetMemberValue(rootObject, "Hierarchy", out object? hierarchyValue) &&
               hierarchyValue is not null &&
               (MeshAssetOperations.TryGetMemberValue(rootObject, "LocalPose", out object? localPoseValue) && localPoseValue is not null ||
                MeshAssetOperations.TryGetMemberValue(rootObject, "ModelPose", out object? modelPoseValue) && modelPoseValue is not null);
    }

    private static IEnumerable<MemberInfo> EnumerateInstanceMembers(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        HashSet<string> seenNames = new(StringComparer.OrdinalIgnoreCase);

        for (Type? current = type; current is not null; current = current.BaseType)
        {
            foreach (PropertyInfo property in current.GetProperties(flags))
            {
                if (!property.CanRead || property.GetIndexParameters().Length != 0 || !seenNames.Add(property.Name))
                {
                    continue;
                }

                yield return property;
            }

            foreach (FieldInfo field in current.GetFields(flags))
            {
                if (!seenNames.Add(field.Name))
                {
                    continue;
                }

                yield return field;
            }
        }
    }

    private static object? GetMemberValueOrNull(object instance, MemberInfo member)
    {
        try
        {
            return member switch
            {
                PropertyInfo property => property.GetValue(instance),
                FieldInfo field => field.GetValue(instance),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static int GetSearchPriority(string memberName)
    {
        string normalized = memberName
            .TrimStart('_')
            .Replace("m_", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        if (normalized.Contains("skeleton", StringComparison.Ordinal) ||
            normalized.Contains("meshskeleton", StringComparison.Ordinal) ||
            normalized.Contains("rigasset", StringComparison.Ordinal) ||
            normalized == "rig")
        {
            return 3;
        }

        if (normalized.Contains("pose", StringComparison.Ordinal) ||
            normalized.Contains("hierarchy", StringComparison.Ordinal) ||
            normalized.Contains("bone", StringComparison.Ordinal))
        {
            return 2;
        }

        if (normalized is "resource" or "meshsetresource" or "meshresource" or "mesh" or "value" or "reference" or "asset")
        {
            return 1;
        }

        return 0;
    }

    private static bool ShouldInspectValue(object value)
    {
        Type type = value.GetType();
        return !type.IsPrimitive &&
               !type.IsEnum &&
               value is not string &&
               value is not decimal &&
               value is not Guid;
    }

    private static Matrix4x4 GetPoseOrIdentity(IReadOnlyList<Matrix4x4>? poses, int index)
    {
        if (poses is not null && index >= 0 && index < poses.Count)
        {
            return poses[index];
        }

        return Matrix4x4.Identity;
    }

    private static Matrix4x4 GetPoseOrIdentity(
        IReadOnlyList<Matrix4x4>? primaryPoses,
        IReadOnlyList<Matrix4x4>? fallbackPoses,
        int index)
    {
        if (primaryPoses is not null && index >= 0 && index < primaryPoses.Count)
        {
            return primaryPoses[index];
        }

        if (fallbackPoses is not null && index >= 0 && index < fallbackPoses.Count)
        {
            return fallbackPoses[index];
        }

        return Matrix4x4.Identity;
    }

    private static Matrix4x4 GetWorldPose(
        int boneIndex,
        IReadOnlyList<Matrix4x4> localPoses,
        IReadOnlyList<Matrix4x4>? modelPoses,
        IReadOnlyList<int> parents,
        Matrix4x4[] worldPoses,
        bool[] worldPoseReady)
    {
        if (worldPoseReady[boneIndex])
        {
            return worldPoses[boneIndex];
        }

        Matrix4x4 worldPose = boneIndex < localPoses.Count ? localPoses[boneIndex] : GetPoseOrIdentity(modelPoses, boneIndex);
        int parentIndex = parents[boneIndex];
        if (parentIndex >= 0 && parentIndex < parents.Count)
        {
            worldPose *= GetWorldPose(parentIndex, localPoses, modelPoses, parents, worldPoses, worldPoseReady);
        }

        worldPoses[boneIndex] = worldPose;
        worldPoseReady[boneIndex] = true;
        return worldPose;
    }

    private static Matrix4x4 TryInvert(Matrix4x4 matrix)
    {
        return Matrix4x4.Invert(matrix, out Matrix4x4 inverse) ? inverse : Matrix4x4.Identity;
    }

    private static List<Matrix4x4>? ReadPoseMatrices(object rootObject, string memberName)
    {
        if (!MeshAssetOperations.TryGetMemberValue(rootObject, memberName, out object? posesValue) ||
            !TryEnumerateValues(posesValue, out List<object?> poses))
        {
            return null;
        }

        List<Matrix4x4> matrices = new(poses.Count);
        foreach (object? pose in poses)
        {
            matrices.Add(TryReadPoseMatrix(pose, out Matrix4x4 matrix) ? matrix : Matrix4x4.Identity);
        }

        return matrices;
    }

    private static bool TryReadPoseMatrix(object? poseValue, out Matrix4x4 matrix)
    {
        matrix = Matrix4x4.Identity;
        if (poseValue is null)
        {
            return false;
        }

        if (!MeshAssetOperations.TryGetMemberValue(poseValue, "Right", out object? rightValue) ||
            !MeshAssetOperations.TryGetMemberValue(poseValue, "Up", out object? upValue) ||
            !MeshAssetOperations.TryGetMemberValue(poseValue, "Forward", out object? forwardValue))
        {
            return false;
        }

        if (!MeshAssetOperations.TryGetMemberValue(poseValue, "Trans", out object? translationValue) &&
            !MeshAssetOperations.TryGetMemberValue(poseValue, "Translation", out translationValue))
        {
            return false;
        }

        Vector3 right = ReadVector3(rightValue);
        Vector3 up = ReadVector3(upValue);
        Vector3 forward = ReadVector3(forwardValue);
        Vector3 translation = ReadVector3(translationValue);
        matrix = new Matrix4x4(
            right.X, right.Y, right.Z, 0.0f,
            up.X, up.Y, up.Z, 0.0f,
            forward.X, forward.Y, forward.Z, 0.0f,
            translation.X, translation.Y, translation.Z, 1.0f);
        return true;
    }

    private static Vector3 ReadVector3(object? value)
    {
        if (value is null)
        {
            return Vector3.Zero;
        }

        float x = ReadComponent(value, "X", "x", "Right");
        float y = ReadComponent(value, "Y", "y", "Up");
        float z = ReadComponent(value, "Z", "z", "Forward");
        return new Vector3(x, y, z);
    }

    private static float ReadComponent(object instance, params string[] memberNames)
    {
        foreach (string memberName in memberNames)
        {
            if (MeshAssetOperations.TryGetMemberValue(instance, memberName, out object? value) &&
                value is not null)
            {
                try
                {
                    return Convert.ToSingle(value);
                }
                catch
                {
                }
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

    private static object? GetMemberValueOrNull(object instance, string memberName)
    {
        return MeshAssetOperations.TryGetMemberValue(instance, memberName, out object? value) ? value : null;
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

    private static Matrix4x4 ToMatrix(LinearTransform transform)
    {
        return new Matrix4x4(
            transform.Right.X, transform.Right.Y, transform.Right.Z, 0.0f,
            transform.Up.X, transform.Up.Y, transform.Up.Z, 0.0f,
            transform.Forward.X, transform.Forward.Y, transform.Forward.Z, 0.0f,
            transform.Translation.X, transform.Translation.Y, transform.Translation.Z, 1.0f);
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static ReferenceEqualityComparer Instance { get; } = new();

        public new bool Equals(object? x, object? y)
        {
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(object obj)
        {
            return RuntimeHelpers.GetHashCode(obj);
        }
    }
}
