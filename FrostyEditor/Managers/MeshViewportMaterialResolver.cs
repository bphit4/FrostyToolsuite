using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Frosty.Sdk.Ebx;
using Frosty.Sdk.Interfaces;
using Frosty.Sdk.IO.Ebx;
using Frosty.Sdk.Managers;
using Frosty.Sdk.Managers.Entries;

namespace FrostyEditor.Managers;

internal sealed record MeshViewportMaterialBinding(
    int MaterialId,
    EbxAssetEntry? ColorTextureEntry,
    EbxAssetEntry? NormalTextureEntry,
    EbxAssetEntry? MaskTextureEntry);

internal readonly record struct MeshViewportTextureCandidate(string ParameterName, EbxAssetEntry Entry);
internal readonly record struct MeshViewportMaterialSlotInfo(int Index, Guid MaterialGuid, object? Slot, object? MaterialObject);

internal static class MeshViewportMaterialResolver
{
    private static readonly object c_resolutionLock = new();
    private static readonly object c_variationCacheLock = new();
    private static readonly Dictionary<Guid, object?> c_defaultVariationsByMeshGuid = [];
    private static readonly object c_metadataCacheLock = new();
    private static IReadOnlyList<EbxAssetEntry>? c_variationDatabaseEntries;
    private static Dictionary<string, IReadOnlyList<EbxAssetEntry>>? c_textureEntriesByDirectory;
    private static int c_backgroundWarmupStarted;

    public static void Invalidate(Guid meshGuid)
    {
        lock (c_variationCacheLock)
        {
            c_defaultVariationsByMeshGuid.Remove(meshGuid);
        }
    }

    public static void StartBackgroundWarmup()
    {
        if (Interlocked.Exchange(ref c_backgroundWarmupStarted, 1) != 0)
        {
            return;
        }

        MeshVariationDatabaseManager.StartBackgroundWarmup();
        _ = Task.Factory.StartNew(
            WarmLookupMetadataCache,
            CancellationToken.None,
            TaskCreationOptions.None,
            TaskScheduler.Default);
    }

    public static IReadOnlyDictionary<int, MeshViewportMaterialBinding> ResolveBindings(
        MeshAssetLoadResult load,
        MeshVariationRecord? selectedVariation = null)
    {
        lock (c_resolutionLock)
        {
            MeshVariationRecord? effectiveVariation = selectedVariation;
            MeshVariationRecord? defaultRecord = effectiveVariation is null
                ? MeshVariationDatabaseManager.GetDefaultVariationRecord(load.Entry)
                : null;
            IReadOnlyDictionary<int, MeshViewportMaterialBinding> baseMaterialBindings = ResolveBaseMaterialBindings(load, null);

            if (defaultRecord is not null)
            {
                IReadOnlyDictionary<int, MeshViewportMaterialBinding> defaultRecordBindings = ResolveVariationRecordBindings(load, defaultRecord);
                IReadOnlyDictionary<int, MeshViewportMaterialBinding> mergedDefaultBindings = MergeBindings(baseMaterialBindings, defaultRecordBindings);
                if (mergedDefaultBindings.Count > 0)
                {
                    return mergedDefaultBindings;
                }

                return baseMaterialBindings;
            }

            if (effectiveVariation is not null)
            {
                IReadOnlyDictionary<int, MeshViewportMaterialBinding> recordBindings = ResolveVariationRecordBindings(load, effectiveVariation);
                IReadOnlyDictionary<int, MeshViewportMaterialBinding> mergedBindings = MergeBindings(baseMaterialBindings, recordBindings);
                if (mergedBindings.Count > 0)
                {
                    return mergedBindings;
                }
            }

            if (baseMaterialBindings.Count > 0)
            {
                return baseMaterialBindings;
            }

            object? defaultVariation = effectiveVariation?.VariationEntry ?? GetDefaultVariation(load.Entry);
            IReadOnlyDictionary<int, MeshViewportMaterialBinding> fifaBindings = ResolveFifaStyleVariationBindings(load, defaultVariation);
            if (fifaBindings.Count > 0)
            {
                return fifaBindings;
            }

            IReadOnlyDictionary<int, MeshViewportMaterialBinding> structuredBindings = ResolveStructuredBindings(load, defaultVariation);
            if (structuredBindings.Count > 0)
            {
                return structuredBindings;
            }

            IReadOnlyDictionary<int, MeshViewportMaterialBinding> directVariationBindings = ResolveDirectVariationBindings(load, defaultVariation);
            if (directVariationBindings.Count > 0)
            {
                return directVariationBindings;
            }

            Dictionary<int, MeshViewportMaterialBinding> bindings = [];
            IReadOnlyList<object?> baseMaterials = GetMaterialSlots(load.RootObject);
            IReadOnlyList<object?> variationMaterials = GetVariationMaterialSlots(defaultVariation);
            int materialCount = Math.Max(baseMaterials.Count, variationMaterials.Count);

            for (int materialId = 0; materialId < materialCount; materialId++)
            {
                Dictionary<string, object?> parameters = new(StringComparer.OrdinalIgnoreCase);

                if (materialId < baseMaterials.Count)
                {
                    MergeMaterialParameters(parameters, baseMaterials[materialId]);
                }

                MergeVariationParameters(parameters, defaultVariation, materialId);

                List<MeshViewportTextureCandidate> textureCandidates = [];
                EbxAssetEntry? colorTexture = null;
                EbxAssetEntry? normalTexture = null;
                EbxAssetEntry? maskTexture = null;

                foreach ((string parameterName, object? value) in parameters)
                {
                    if (!TryResolveTextureEntryCandidate(value, out EbxAssetEntry? textureEntry))
                    {
                        continue;
                    }

                    string normalizedName = NormalizeParameterName(parameterName);
                    textureCandidates.Add(new MeshViewportTextureCandidate(parameterName, textureEntry!));
                    if (colorTexture is null && IsColorTextureMatch(normalizedName, textureEntry!.Path))
                    {
                        colorTexture = textureEntry;
                        continue;
                    }

                    if (normalTexture is null && IsNormalTextureMatch(normalizedName, textureEntry!.Path))
                    {
                        normalTexture = textureEntry;
                        continue;
                    }

                    if (maskTexture is null && IsMaskTextureMatch(normalizedName, textureEntry!.Path))
                    {
                        maskTexture = textureEntry;
                    }
                }

                colorTexture ??= PickFallbackColorTexture(textureCandidates);

                if (colorTexture is null && normalTexture is null && maskTexture is null)
                {
                    continue;
                }

                bindings[materialId] = new MeshViewportMaterialBinding(materialId, colorTexture, normalTexture, maskTexture);
            }

            if (bindings.Count > 0)
            {
                return bindings;
            }

            return ResolveMeshNamedBindings(load);
        }
    }

    private static IReadOnlyDictionary<int, MeshViewportMaterialBinding> MergeBindings(
        IReadOnlyDictionary<int, MeshViewportMaterialBinding> baseBindings,
        IReadOnlyDictionary<int, MeshViewportMaterialBinding> variationBindings)
    {
        if (baseBindings.Count == 0)
        {
            return variationBindings;
        }

        if (variationBindings.Count == 0)
        {
            return baseBindings;
        }

        Dictionary<int, MeshViewportMaterialBinding> merged = new(baseBindings);
        foreach ((int materialId, MeshViewportMaterialBinding variationBinding) in variationBindings)
        {
            if (merged.TryGetValue(materialId, out MeshViewportMaterialBinding? baseBinding))
            {
                merged[materialId] = new MeshViewportMaterialBinding(
                    materialId,
                    ChoosePreferredColorTexture(baseBinding.ColorTextureEntry, variationBinding.ColorTextureEntry),
                    variationBinding.NormalTextureEntry ?? baseBinding.NormalTextureEntry,
                    variationBinding.MaskTextureEntry ?? baseBinding.MaskTextureEntry);
                continue;
            }

            merged[materialId] = variationBinding;
        }

        return merged;
    }

    private static EbxAssetEntry? ChoosePreferredColorTexture(EbxAssetEntry? baseColorTexture, EbxAssetEntry? variationColorTexture)
    {
        if (variationColorTexture is null)
        {
            return baseColorTexture;
        }

        string variationPath = variationColorTexture.Path;
        if (baseColorTexture is not null &&
            (LooksLikeNormalTextureAssetPath(variationPath) || LooksLikeMaskTextureAssetPath(variationPath)))
        {
            return baseColorTexture;
        }

        return variationColorTexture;
    }

    private static IReadOnlyDictionary<int, MeshViewportMaterialBinding> ResolveVariationRecordBindings(
        MeshAssetLoadResult load,
        MeshVariationRecord? variationRecord)
    {
        Dictionary<int, MeshViewportMaterialBinding> bindings = [];
        if (variationRecord is null)
        {
            return bindings;
        }

        IReadOnlyList<object?> baseSlots = GetMaterialSlots(load.RootObject);
        List<MeshViewportMaterialSlotInfo> baseMaterialInfos = [];
        for (int index = 0; index < baseSlots.Count; index++)
        {
            object? slot = baseSlots[index];
            object? materialObject = ResolveMaterialObject(slot);
            Guid materialGuid = ReadMaterialGuid(slot);
            if (materialGuid == Guid.Empty)
            {
                materialGuid = ReadMaterialGuid(materialObject);
            }

            baseMaterialInfos.Add(new MeshViewportMaterialSlotInfo(index, materialGuid, slot, materialObject));
        }

        if (baseMaterialInfos.Count == 0)
        {
            for (int materialIndex = 0; materialIndex < variationRecord.Materials.Count; materialIndex++)
            {
                Dictionary<string, object?> parameters = new(StringComparer.OrdinalIgnoreCase);
                MergeVariationRecordParameters(parameters, variationRecord.Materials[materialIndex]);
                if (TryCreateBinding(materialIndex, parameters, out MeshViewportMaterialBinding? directBinding) &&
                    directBinding is not null)
                {
                    bindings[materialIndex] = directBinding;
                }
            }

            return bindings;
        }

        for (int index = 0; index < baseMaterialInfos.Count; index++)
        {
            MeshViewportMaterialSlotInfo materialInfo = baseMaterialInfos[index];
            Dictionary<string, object?> parameters = new(StringComparer.OrdinalIgnoreCase);
            MergeMaterialParameters(parameters, materialInfo.Slot);

            MeshVariationMaterialRecord? variationMaterial = FindMatchingVariationMaterial(variationRecord, materialInfo, index);
            if (variationMaterial is not null)
            {
                MergeVariationRecordParameters(parameters, variationMaterial);
            }

            if (TryCreateBinding(materialInfo.Index, parameters, out MeshViewportMaterialBinding? binding) &&
                binding is not null)
            {
                bindings[materialInfo.Index] = binding;
                continue;
            }

            if (TryCreateMaterialObjectBinding(materialInfo.Index, materialInfo.MaterialObject, out binding) &&
                binding is not null)
            {
                bindings[materialInfo.Index] = binding;
            }
        }

        return bindings;
    }

    private static IReadOnlyDictionary<int, MeshViewportMaterialBinding> ResolveBaseMaterialBindings(
        MeshAssetLoadResult load,
        object? defaultVariation)
    {
        Dictionary<int, MeshViewportMaterialBinding> bindings = [];
        IReadOnlyList<object?> baseSlots = GetMaterialSlots(load.RootObject);
        if (baseSlots.Count == 0)
        {
            return bindings;
        }

        IReadOnlyList<object?> variationSlots = GetVariationMaterialSlots(defaultVariation);
        List<MeshViewportMaterialSlotInfo> baseMaterialInfos = [];
        for (int index = 0; index < baseSlots.Count; index++)
        {
            object? slot = baseSlots[index];
            object? materialObject = ResolveMaterialObject(slot);
            Guid materialGuid = ReadMaterialGuid(slot);
            if (materialGuid == Guid.Empty)
            {
                materialGuid = ReadMaterialGuid(materialObject);
            }

            baseMaterialInfos.Add(new MeshViewportMaterialSlotInfo(index, materialGuid, slot, materialObject));
        }

        foreach (MeshViewportMaterialSlotInfo materialInfo in baseMaterialInfos)
        {
            Dictionary<string, object?> parameters = new(StringComparer.OrdinalIgnoreCase);
            MergeMaterialParameters(parameters, materialInfo.Slot);

            int variationIndex = FindMatchingVariationIndex(variationSlots, baseMaterialInfos, materialInfo);
            if (variationIndex >= 0)
            {
                MergeVariationParameters(parameters, variationSlots[variationIndex]);
            }

            if (TryCreateBinding(materialInfo.Index, parameters, out MeshViewportMaterialBinding? binding) &&
                binding is not null)
            {
                bindings[materialInfo.Index] = binding;
                continue;
            }

            if (TryCreateMaterialObjectBinding(materialInfo.Index, materialInfo.MaterialObject, out binding) &&
                binding is not null)
            {
                bindings[materialInfo.Index] = binding;
            }
        }

        return bindings;
    }

    private static IReadOnlyDictionary<int, MeshViewportMaterialBinding> ResolveMeshNamedBindings(MeshAssetLoadResult load)
    {
        if (GetMaterialSlots(load.RootObject).Count > 1)
        {
            return new Dictionary<int, MeshViewportMaterialBinding>();
        }

        EbxAssetEntry? colorTexture = TryResolveTextureEntryByMeshPath(load.Entry, "_color", "_colorao", "_diffuse", "_albedo");
        EbxAssetEntry? normalTexture = TryResolveTextureEntryByMeshPath(load.Entry, "_normal", "_nsm", "_norm");
        EbxAssetEntry? maskTexture = TryResolveTextureEntryByMeshPath(load.Entry, "_rsm", "_mask", "_orm");
        if (colorTexture is null && normalTexture is null && maskTexture is null)
        {
            return new Dictionary<int, MeshViewportMaterialBinding>();
        }

        int[] materialIds = load.Mesh.Lods
            .SelectMany(lod => lod.Sections)
            .Select(section => section.MaterialId)
            .Distinct()
            .ToArray();

        if (materialIds.Length == 0)
        {
            materialIds = [0];
        }

        Dictionary<int, MeshViewportMaterialBinding> bindings = [];
        foreach (int materialId in materialIds)
        {
            bindings[materialId] = new MeshViewportMaterialBinding(materialId, colorTexture, normalTexture, maskTexture);
        }

        return bindings;
    }

    private static IReadOnlyDictionary<int, MeshViewportMaterialBinding> ResolveDirectVariationBindings(
        MeshAssetLoadResult load,
        object? defaultVariation)
    {
        Dictionary<int, MeshViewportMaterialBinding> bindings = [];
        IReadOnlyList<object?> variationSlots = GetVariationMaterialSlots(defaultVariation);
        for (int materialId = 0; materialId < variationSlots.Count; materialId++)
        {
            if (TryCreateDirectVariationBinding(materialId, variationSlots[materialId], out MeshViewportMaterialBinding? binding) &&
                binding is not null)
            {
                bindings[materialId] = binding;
            }
        }

        return bindings;
    }

    private static IReadOnlyDictionary<int, MeshViewportMaterialBinding> ResolveFifaStyleVariationBindings(
        MeshAssetLoadResult load,
        object? defaultVariation)
    {
        Dictionary<int, MeshViewportMaterialBinding> bindings = [];
        IReadOnlyList<object?> variationSlots = GetVariationMaterialSlots(defaultVariation);
        for (int materialId = 0; materialId < variationSlots.Count; materialId++)
        {
            if (TryCreateFifaStyleVariationBinding(materialId, variationSlots[materialId], out MeshViewportMaterialBinding? binding) &&
                binding is not null)
            {
                bindings[materialId] = binding;
            }
        }

        return bindings;
    }

    private static IReadOnlyDictionary<int, MeshViewportMaterialBinding> ResolveStructuredBindings(
        MeshAssetLoadResult load,
        object? defaultVariation)
    {
        Dictionary<int, MeshViewportMaterialBinding> bindings = [];
        IReadOnlyList<object?> baseSlots = GetMaterialSlots(load.RootObject);
        if (baseSlots.Count == 0)
        {
            return bindings;
        }

        List<MeshViewportMaterialSlotInfo> baseMaterialInfos = [];
        for (int index = 0; index < baseSlots.Count; index++)
        {
            object? slot = baseSlots[index];
            object? materialObject = ResolveMaterialObject(slot);
            Guid materialGuid = ReadMaterialGuid(slot);
            if (materialGuid == Guid.Empty)
            {
                materialGuid = ReadMaterialGuid(materialObject);
            }

            baseMaterialInfos.Add(new MeshViewportMaterialSlotInfo(index, materialGuid, slot, materialObject));
        }

        IReadOnlyList<object?> variationSlots = GetVariationMaterialSlots(defaultVariation);
        Dictionary<int, Dictionary<string, object?>> variationParametersByIndex = [];

        for (int variationIndex = 0; variationIndex < variationSlots.Count; variationIndex++)
        {
            object? variationSlot = variationSlots[variationIndex];
            if (variationSlot is null)
            {
                continue;
            }

            Guid materialGuid = ReadMaterialGuidFromVariationSlot(variationSlot);
            int targetIndex = FindBaseMaterialIndex(baseMaterialInfos, materialGuid, variationIndex);
            if (targetIndex < 0)
            {
                continue;
            }

            if (!variationParametersByIndex.TryGetValue(targetIndex, out Dictionary<string, object?>? parameters))
            {
                parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                variationParametersByIndex[targetIndex] = parameters;
            }

            MergeVariationParameters(parameters, variationSlot);
        }

        foreach (MeshViewportMaterialSlotInfo materialInfo in baseMaterialInfos)
        {
            Dictionary<string, object?> parameters = new(StringComparer.OrdinalIgnoreCase);
            MergeMaterialParameters(parameters, materialInfo.Slot);
            if (variationParametersByIndex.TryGetValue(materialInfo.Index, out Dictionary<string, object?>? variationParameters))
            {
                foreach ((string parameterName, object? value) in variationParameters)
                {
                    parameters[parameterName] = value;
                }
            }

            if (TryCreateBinding(materialInfo.Index, parameters, out MeshViewportMaterialBinding? binding) &&
                binding is not null)
            {
                bindings[materialInfo.Index] = binding;
            }
        }

        return bindings;
    }

    private static MeshVariationMaterialRecord? FindMatchingVariationMaterial(
        MeshVariationRecord variationRecord,
        MeshViewportMaterialSlotInfo materialInfo,
        int materialIndex)
    {
        bool hasVariationMaterialGuids = variationRecord.Materials.Any(static material => material.MaterialGuid != Guid.Empty);
        if (materialInfo.MaterialGuid != Guid.Empty)
        {
            MeshVariationMaterialRecord? matchByGuid = variationRecord.Materials
                .FirstOrDefault(material => material.MaterialGuid == materialInfo.MaterialGuid);
            if (matchByGuid is not null)
            {
                return matchByGuid;
            }

            if (hasVariationMaterialGuids)
            {
                return null;
            }
        }

        return (!hasVariationMaterialGuids || materialInfo.MaterialGuid == Guid.Empty) &&
               materialIndex >= 0 &&
               materialIndex < variationRecord.Materials.Count
            ? variationRecord.Materials[materialIndex]
            : null;
    }

    private static void MergeVariationRecordParameters(
        IDictionary<string, object?> parameters,
        MeshVariationMaterialRecord materialRecord)
    {
        foreach (MeshVariationTextureParameter textureParameter in materialRecord.TextureParameters)
        {
            string parameterName = string.IsNullOrWhiteSpace(textureParameter.ParameterName)
                ? textureParameter.TextureEntry?.Filename ?? "Texture"
                : textureParameter.ParameterName;
            object? value = textureParameter.TextureEntry is not null
                ? textureParameter.TextureEntry
                : textureParameter.Texture;
            if (value is not null)
            {
                parameters[parameterName] = value;
            }
        }
    }

    private static bool TryCreateBinding(
        int materialId,
        IReadOnlyDictionary<string, object?> parameters,
        out MeshViewportMaterialBinding? binding)
    {
        List<MeshViewportTextureCandidate> textureCandidates = [];
        EbxAssetEntry? colorTexture = null;
        EbxAssetEntry? normalTexture = null;
        EbxAssetEntry? maskTexture = null;

        foreach ((string parameterName, object? value) in parameters)
        {
            if (!TryResolveTextureEntryCandidate(value, out EbxAssetEntry? textureEntry))
            {
                continue;
            }

            string normalizedName = NormalizeParameterName(parameterName);
            textureCandidates.Add(new MeshViewportTextureCandidate(parameterName, textureEntry!));
            if (colorTexture is null && IsColorTextureMatch(normalizedName, textureEntry!.Path))
            {
                colorTexture = textureEntry;
                continue;
            }

            if (normalTexture is null && IsNormalTextureMatch(normalizedName, textureEntry!.Path))
            {
                normalTexture = textureEntry;
                continue;
            }

            if (maskTexture is null && IsMaskTextureMatch(normalizedName, textureEntry!.Path))
            {
                maskTexture = textureEntry;
            }
        }

        colorTexture ??= PickFallbackColorTexture(textureCandidates);
        if (colorTexture is null && normalTexture is null && maskTexture is null)
        {
            binding = default;
            return false;
        }

        binding = new MeshViewportMaterialBinding(materialId, colorTexture, normalTexture, maskTexture);
        return true;
    }

    private static bool TryCreateDirectVariationBinding(
        int materialId,
        object? variationSlot,
        out MeshViewportMaterialBinding? binding)
    {
        binding = default;
        if (!TryGetDirectVariationTextureParameters(variationSlot, out object? parametersValue))
        {
            return false;
        }

        List<MeshViewportTextureCandidate> textureCandidates = [];
        EbxAssetEntry? colorTexture = null;
        EbxAssetEntry? normalTexture = null;
        EbxAssetEntry? maskTexture = null;

        foreach (object? parameter in EnumerateValues(parametersValue))
        {
            if (parameter is null ||
                !TryResolveTextureEntryFromParameter(parameter, out EbxAssetEntry? textureEntry))
            {
                continue;
            }

            string parameterName = textureEntry!.Path;
            if (TryGetParameterName(parameter, out object? nameValue) &&
                TryReadParameterName(nameValue, out string resolvedName) &&
                !string.IsNullOrWhiteSpace(resolvedName))
            {
                parameterName = resolvedName;
            }

            string normalizedName = NormalizeParameterName(parameterName);
            textureCandidates.Add(new MeshViewportTextureCandidate(parameterName, textureEntry));
            if (colorTexture is null && IsColorTextureMatch(normalizedName, textureEntry.Path))
            {
                colorTexture = textureEntry;
                continue;
            }

            if (normalTexture is null && IsNormalTextureMatch(normalizedName, textureEntry.Path))
            {
                normalTexture = textureEntry;
                continue;
            }

            if (maskTexture is null && IsMaskTextureMatch(normalizedName, textureEntry.Path))
            {
                maskTexture = textureEntry;
            }
        }

        colorTexture ??= PickFallbackColorTexture(textureCandidates);
        if (colorTexture is null && normalTexture is null && maskTexture is null)
        {
            return false;
        }

        binding = new MeshViewportMaterialBinding(materialId, colorTexture, normalTexture, maskTexture);
        return true;
    }

    private static bool TryCreateFifaStyleVariationBinding(
        int materialId,
        object? variationSlot,
        out MeshViewportMaterialBinding? binding)
    {
        binding = null;
        object? materialObject = ResolveFifaStyleVariationMaterialObject(variationSlot);
        if (materialObject is null)
        {
            return false;
        }

        if (TryCreateBindingFromTextureSources(materialId, out binding, materialObject))
        {
            return true;
        }

        if (!TryResolveVariationShaderObject(materialObject, out object? shaderObject) ||
            shaderObject is null)
        {
            return false;
        }

        if (TryCreateBindingFromTextureSources(materialId, out binding, shaderObject))
        {
            return true;
        }

        object? nestedShader = null;
        if (MeshAssetOperations.TryGetMemberValue(shaderObject, "Shader", out object? shaderReference) &&
            shaderReference is not null)
        {
            nestedShader = ResolvePointerObject(shaderReference) ?? shaderReference;
            if (!ReferenceEquals(nestedShader, shaderObject) &&
                TryCreateBindingFromTextureSources(materialId, out binding, nestedShader))
            {
                return true;
            }
        }

        if (TryResolveShaderPresetTextureParameters(shaderObject, out object? presetParameters) &&
            TryCreateBindingFromParameterCollection(materialId, presetParameters, out binding))
        {
            return true;
        }

        if (nestedShader is not null &&
            TryResolveShaderPresetTextureParameters(nestedShader, out presetParameters) &&
            TryCreateBindingFromParameterCollection(materialId, presetParameters, out binding))
        {
            return true;
        }

        return false;
    }

    private static bool TryCreateMaterialObjectBinding(
        int materialId,
        object? materialObject,
        out MeshViewportMaterialBinding? binding)
    {
        binding = null;
        if (materialObject is null)
        {
            return false;
        }

        if (TryCreateBindingFromTextureSources(materialId, out binding, materialObject))
        {
            return true;
        }

        if (!TryResolveVariationShaderObject(materialObject, out object? shaderObject) ||
            shaderObject is null)
        {
            return false;
        }

        if (TryCreateBindingFromTextureSources(materialId, out binding, shaderObject))
        {
            return true;
        }

        object? nestedShader = null;
        if (MeshAssetOperations.TryGetMemberValue(shaderObject, "Shader", out object? shaderReference) &&
            shaderReference is not null)
        {
            nestedShader = ResolvePointerObject(shaderReference) ?? shaderReference;
            if (!ReferenceEquals(nestedShader, shaderObject) &&
                TryCreateBindingFromTextureSources(materialId, out binding, nestedShader))
            {
                return true;
            }
        }

        if (TryResolveShaderPresetTextureParameters(shaderObject, out object? presetParameters) &&
            TryCreateBindingFromParameterCollection(materialId, presetParameters, out binding))
        {
            return true;
        }

        if (nestedShader is not null &&
            TryResolveShaderPresetTextureParameters(nestedShader, out presetParameters) &&
            TryCreateBindingFromParameterCollection(materialId, presetParameters, out binding))
        {
            return true;
        }

        return false;
    }

    private static int FindBaseMaterialIndex(
        IReadOnlyList<MeshViewportMaterialSlotInfo> baseMaterialInfos,
        Guid materialGuid,
        int variationIndex)
    {
        bool hasBaseMaterialGuids = baseMaterialInfos.Any(static info => info.MaterialGuid != Guid.Empty);
        if (materialGuid != Guid.Empty)
        {
            for (int i = 0; i < baseMaterialInfos.Count; i++)
            {
                if (baseMaterialInfos[i].MaterialGuid == materialGuid)
                {
                    return baseMaterialInfos[i].Index;
                }
            }
        }

        return (!hasBaseMaterialGuids || materialGuid == Guid.Empty) &&
               variationIndex >= 0 &&
               variationIndex < baseMaterialInfos.Count
            ? variationIndex
            : -1;
    }

    private static int FindMatchingVariationIndex(
        IReadOnlyList<object?> variationSlots,
        IReadOnlyList<MeshViewportMaterialSlotInfo> baseMaterialInfos,
        MeshViewportMaterialSlotInfo materialInfo)
    {
        if (variationSlots.Count == 0)
        {
            return -1;
        }

        bool hasVariationMaterialGuids = false;
        for (int index = 0; index < variationSlots.Count; index++)
        {
            object? variationSlot = variationSlots[index];
            if (variationSlot is null)
            {
                continue;
            }

            if (ReadMaterialGuidFromVariationSlot(variationSlot) != Guid.Empty)
            {
                hasVariationMaterialGuids = true;
                break;
            }
        }

        if (materialInfo.MaterialGuid != Guid.Empty)
        {
            for (int index = 0; index < variationSlots.Count; index++)
            {
                object? variationSlot = variationSlots[index];
                if (variationSlot is null)
                {
                    continue;
                }

                Guid variationGuid = ReadMaterialGuidFromVariationSlot(variationSlot);
                if (variationGuid != Guid.Empty && variationGuid == materialInfo.MaterialGuid)
                {
                    return index;
                }
            }
        }

        return (!hasVariationMaterialGuids || materialInfo.MaterialGuid == Guid.Empty) &&
               materialInfo.Index >= 0 &&
               materialInfo.Index < variationSlots.Count
            ? materialInfo.Index
            : -1;
    }

    private static Guid ReadMaterialGuidFromVariationSlot(object variationSlot)
    {
        if (MeshAssetOperations.TryGetMemberValue(variationSlot, "Material", out object? materialReference))
        {
            Guid materialGuid = ReadMaterialGuid(materialReference);
            if (materialGuid != Guid.Empty)
            {
                return materialGuid;
            }
        }

        return ReadMaterialGuid(variationSlot);
    }

    private static Guid ReadMaterialGuid(object? candidate)
    {
        if (candidate is null)
        {
            return Guid.Empty;
        }

        if (candidate is PointerRef pointerRef)
        {
            if (pointerRef.Type == PointerRefType.Internal && pointerRef.Internal is IEbxInstance internalInstance)
            {
                AssetClassGuid internalGuid = internalInstance.GetInstanceGuid();
                if (internalGuid.IsExported)
                {
                    return internalGuid.ExportedGuid;
                }
            }

            if (pointerRef.Type == PointerRefType.External)
            {
                Guid classGuid = ReadGuid(pointerRef.External, "ClassGuid");
                if (classGuid != Guid.Empty)
                {
                    return classGuid;
                }

                Guid instanceGuid = pointerRef.External.InstanceGuid;
                if (instanceGuid != Guid.Empty)
                {
                    return instanceGuid;
                }
            }
        }

        if (candidate is IEbxInstance instance)
        {
            AssetClassGuid guid = instance.GetInstanceGuid();
            if (guid.IsExported)
            {
                return guid.ExportedGuid;
            }
        }

        Guid directInstanceGuid = ReadGuid(candidate, "InstanceGuid");
        if (directInstanceGuid != Guid.Empty)
        {
            return directInstanceGuid;
        }

        return ReadGuid(candidate, "ClassGuid");
    }

    private static object? GetDefaultVariation(EbxAssetEntry meshEntry)
    {
        object? managedVariation = MeshVariationDatabaseManager.GetDefaultVariationEntry(meshEntry);
        if (managedVariation is not null)
        {
            lock (c_variationCacheLock)
            {
                c_defaultVariationsByMeshGuid[meshEntry.Guid] = managedVariation;
            }

            return managedVariation;
        }

        lock (c_variationCacheLock)
        {
            if (c_defaultVariationsByMeshGuid.TryGetValue(meshEntry.Guid, out object? variationEntry))
            {
                return variationEntry;
            }
        }

        string expectedPrefix = GetMeshVariationPrefix(meshEntry);
        object? fallbackMatch = null;

        foreach (EbxAssetEntry entry in EnumerateVariationDatabaseEntries())
        {
            if (!TryGetDefaultVariation(entry, meshEntry, out object? variationEntry) ||
                variationEntry is null)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(expectedPrefix) &&
                !string.IsNullOrWhiteSpace(entry.Path) &&
                entry.Path.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                lock (c_variationCacheLock)
                {
                    c_defaultVariationsByMeshGuid[meshEntry.Guid] = variationEntry;
                }

                return variationEntry;
            }

            fallbackMatch ??= variationEntry;
        }

        lock (c_variationCacheLock)
        {
            c_defaultVariationsByMeshGuid[meshEntry.Guid] = fallbackMatch;
        }

        return fallbackMatch;
    }

    private static bool TryGetDefaultVariation(
        EbxAssetEntry variationDatabaseEntry,
        EbxAssetEntry meshEntry,
        out object? variationEntry)
    {
        object rootObject = AssetManager.GetEbxPartition(variationDatabaseEntry).PrimaryInstance;
        if (!MeshAssetOperations.TryGetMemberValue(rootObject, "Entries", out object? entriesValue))
        {
            variationEntry = null;
            return false;
        }

        object? fallbackMatch = null;
        foreach (object? candidate in EnumerateValues(entriesValue))
        {
            if (candidate is null || !MatchesMesh(candidate, meshEntry))
            {
                continue;
            }

            fallbackMatch ??= candidate;
            if (IsDefaultVariation(candidate))
            {
                variationEntry = candidate;
                return true;
            }
        }

        variationEntry = fallbackMatch;
        return variationEntry is not null;
    }

    private static bool MatchesMesh(object candidate, EbxAssetEntry meshEntry)
    {
        if (MeshAssetOperations.TryResolveLinkedEbxAssetEntry(candidate, out EbxAssetEntry? linkedMesh, "Mesh", "MeshAsset", "MeshSet", "MeshResource", "RenderableMeshAsset", "SourceMesh") &&
            linkedMesh?.Guid == meshEntry.Guid)
        {
            return true;
        }

        foreach (string memberName in new[] { "Mesh", "MeshAsset", "MeshSet", "MeshResource", "RenderableMeshAsset", "SourceMesh", "Asset", "Reference" })
        {
            if (MeshAssetOperations.TryGetMemberValue(candidate, memberName, out object? meshValue) &&
                MeshAssetOperations.TryResolveLinkedEbxAssetEntry(meshValue, out linkedMesh) &&
                linkedMesh?.Guid == meshEntry.Guid)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDefaultVariation(object candidate)
    {
        return IsZero(candidate, "SubVariationNameHash") &&
               IsZero(candidate, "VariationAssetNameHash");
    }

    private static bool IsZero(object instance, string memberName)
    {
        if (!MeshAssetOperations.TryGetMemberValue(instance, memberName, out object? value) ||
            value is null)
        {
            return false;
        }

        try
        {
            return Convert.ToUInt64(value) == 0;
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<object?> GetMaterialSlots(object rootObject)
    {
        if (TryGetCollectionValue(rootObject, out object? directMaterialsValue, "Materials", "MaterialCollection", "MaterialCollections", "MeshMaterials", "RenderableMaterials") &&
            directMaterialsValue is not null)
        {
            return EnumerateValues(directMaterialsValue).ToArray();
        }

        if (!TryFindMaterialSlotCollection(rootObject, out object? materialsValue))
        {
            return [];
        }

        return EnumerateValues(materialsValue).ToArray();
    }

    private static IReadOnlyList<object?> GetVariationMaterialSlots(object? variationEntry)
    {
        if (variationEntry is null)
        {
            return [];
        }

        if (TryGetCollectionValue(variationEntry, out object? directMaterialsValue, "Materials", "MaterialCollection", "MaterialCollections", "MaterialEntries", "VariationMaterials") &&
            directMaterialsValue is not null)
        {
            return EnumerateValues(directMaterialsValue).ToArray();
        }

        if (!TryFindMaterialSlotCollection(variationEntry, out object? materialsValue))
        {
            return [];
        }

        return EnumerateValues(materialsValue).ToArray();
    }

    private static bool TryGetDirectVariationTextureParameters(object? variationSlot, out object? parametersValue)
    {
        foreach (object? candidate in new[] { ResolveVariationMaterialObject(variationSlot), variationSlot })
        {
            if (candidate is null)
            {
                continue;
            }

            if (TryGetTextureParameters(candidate, out parametersValue))
            {
                return true;
            }

            if (MeshAssetOperations.TryGetMemberValue(candidate, "Shader", out object? shaderReference))
            {
                object? resolvedShader = ResolvePointerObject(shaderReference) ?? shaderReference;
                if (resolvedShader is not null && TryGetTextureParameters(resolvedShader, out parametersValue))
                {
                    return true;
                }

                if (resolvedShader is not null &&
                    MeshAssetOperations.TryGetMemberValue(resolvedShader, "ShaderPreset", out object? shaderPreset))
                {
                    object? resolvedPreset = ResolvePointerObject(shaderPreset) ?? shaderPreset;
                    if (resolvedPreset is not null && TryGetTextureParameters(resolvedPreset, out parametersValue))
                    {
                        return true;
                    }
                }
            }
        }

        parametersValue = null;
        return false;
    }

    private static bool TryResolveVariationShaderObject(object materialObject, out object? shaderObject)
    {
        shaderObject = null;
        if (!MeshAssetOperations.TryGetMemberValue(materialObject, "Shader", out object? shaderReference) ||
            shaderReference is null)
        {
            return false;
        }

        shaderObject = ResolvePointerObject(shaderReference) ?? shaderReference;
        return shaderObject is not null;
    }

    private static bool TryResolveShaderPresetTextureParameters(object shaderObject, out object? parametersValue)
    {
        parametersValue = null;
        if (!MeshAssetOperations.TryGetMemberValue(shaderObject, "Shader", out object? shaderReference) ||
            shaderReference is null)
        {
            return false;
        }

        object? resolvedShader = ResolvePointerObject(shaderReference) ?? shaderReference;
        if (resolvedShader is null)
        {
            return false;
        }

        if (MeshAssetOperations.TryGetMemberValue(resolvedShader, "ShaderPreset", out object? shaderPreset) &&
            shaderPreset is not null)
        {
            object? resolvedPreset = ResolvePointerObject(shaderPreset) ?? shaderPreset;
            if (resolvedPreset is not null && TryGetTextureParameters(resolvedPreset, out parametersValue))
            {
                return true;
            }
        }

        return TryGetTextureParameters(resolvedShader, out parametersValue);
    }

    private static bool TryGetTextureParameters(object source, out object? parametersValue)
    {
        return TryGetCollectionValue(source, out parametersValue, "TextureParameters", "Parameters", "MaterialParameters", "Textures", "TextureList") &&
               parametersValue is not null;
    }

    private static void MergeMaterialParameters(IDictionary<string, object?> targetParameters, object? materialSlot)
    {
        object? materialObject = ResolveMaterialObject(materialSlot);
        if (materialObject is null)
        {
            return;
        }

        MergeTextureContainers(targetParameters, materialObject);
        MergeTextureContainers(targetParameters, materialSlot);

        if (MeshAssetOperations.TryGetMemberValue(materialObject, "Shader", out object? shaderObject))
        {
            MergeShaderParameters(targetParameters, shaderObject, new HashSet<object>(ReferenceEqualityComparer.Instance));
        }

        MergeTextureMemberAliases(targetParameters, materialObject);
        if (!ReferenceEquals(materialObject, materialSlot))
        {
            MergeTextureMemberAliases(targetParameters, materialSlot);
        }
    }

    private static void MergeVariationParameters(IDictionary<string, object?> targetParameters, object? defaultVariation, int materialId)
    {
        IReadOnlyList<object?> materials = GetVariationMaterialSlots(defaultVariation);
        if (materialId < 0 || materialId >= materials.Count)
        {
            return;
        }

        MergeVariationParameters(targetParameters, materials[materialId]);
    }

    private static void MergeVariationParameters(IDictionary<string, object?> targetParameters, object? materialSlot)
    {
        if (materialSlot is null)
        {
            return;
        }

        object? materialObject = ResolveVariationMaterialObject(materialSlot);
        if (materialObject is not null)
        {
            MergeTextureContainers(targetParameters, materialObject);
            MergeTextureContainers(targetParameters, materialSlot);

            if (MeshAssetOperations.TryGetMemberValue(materialObject, "Shader", out object? shaderObject))
            {
                MergeShaderParameters(targetParameters, shaderObject, new HashSet<object>(ReferenceEqualityComparer.Instance));
            }

            MergeTextureMemberAliases(targetParameters, materialObject);
        }

        MergeTextureMemberAliases(targetParameters, materialSlot);
    }

    private static void MergeTextureContainers(IDictionary<string, object?> targetParameters, object? source)
    {
        if (source is null)
        {
            return;
        }

        foreach (string memberName in new[] { "TextureParameters", "Parameters", "MaterialParameters", "Textures", "TextureList" })
        {
            if (MeshAssetOperations.TryGetMemberValue(source, memberName, out object? memberValue))
            {
                MergeParameterCollection(targetParameters, memberValue);
            }
        }
    }

    private static void MergeTextureMemberAliases(IDictionary<string, object?> targetParameters, object? source)
    {
        if (source is null)
        {
            return;
        }

        AddTextureAliasGroup(targetParameters, source, "ColorTexture", "DiffuseTexture", "BaseColorTexture", "AlbedoTexture", "BaseTexture", "DiffuseMap", "AlbedoMap", "ColorMap", "Texture", "Texture0", "Texture1", "Texture2", "Texture3");
        AddTextureAliasGroup(targetParameters, source, "NormalTexture", "NormalMap", "BaseNormalTexture", "SeamPatternNormTexture", "NormalClampTexture", "NsmTexture", "BumpTexture", "BumpMap", "DetailNormalTexture");
        AddTextureAliasGroup(targetParameters, source, "MaskTexture", "SpecMaskTexture", "RsmTexture", "R_s_ssr_tTexture", "CoeffTexture", "SpecularTexture", "RoughnessTexture", "MetallicTexture", "OcclusionTexture", "AoTexture", "TintTexture");
    }

    private static void AddTextureAliasGroup(IDictionary<string, object?> targetParameters, object source, params string[] memberNames)
    {
        foreach (string memberName in memberNames)
        {
            if (targetParameters.ContainsKey(memberName))
            {
                continue;
            }

            if (!MeshAssetOperations.TryResolveLinkedEbxAssetEntry(source, out EbxAssetEntry? textureEntry, memberName) ||
                textureEntry is null ||
                !TextureAssetOperations.IsTextureAsset(textureEntry))
            {
                continue;
            }

            targetParameters[memberName] = textureEntry;
        }
    }

    private static void MergeShaderParameters(IDictionary<string, object?> targetParameters, object? shaderObject, HashSet<object> visitedShaders)
    {
        if (shaderObject is null)
        {
            return;
        }

        if (!visitedShaders.Add(shaderObject))
        {
            return;
        }

        if (MeshAssetOperations.TryGetMemberValue(shaderObject, "Shader", out object? shaderReference))
        {
            object? resolvedShader = ResolvePointerObject(shaderReference);
            if (resolvedShader is not null &&
                !ReferenceEquals(resolvedShader, shaderObject))
            {
                if (MeshAssetOperations.TryGetMemberValue(resolvedShader, "ShaderPreset", out object? resolvedPreset) &&
                    resolvedPreset is not null)
                {
                    MergeShaderParameters(targetParameters, resolvedPreset, visitedShaders);
                }
                else
                {
                    MergeShaderParameters(targetParameters, resolvedShader, visitedShaders);
                }
            }
        }

        if (MeshAssetOperations.TryGetMemberValue(shaderObject, "TextureParameters", out object? parametersValue))
        {
            MergeParameterCollection(targetParameters, parametersValue);
        }

        MergeTextureContainers(targetParameters, shaderObject);
        MergeTextureMemberAliases(targetParameters, shaderObject);
    }

    private static void MergeParameterCollection(IDictionary<string, object?> targetParameters, object? parametersValue)
    {
        foreach (object? parameter in EnumerateValues(parametersValue))
        {
            object? resolvedParameter = ResolveParameterObject(parameter) ?? parameter;
            if (resolvedParameter is null ||
                !TryGetParameterValue(resolvedParameter, out object? parameterValue))
            {
                continue;
            }

            string parameterName = string.Empty;
            if (TryGetParameterName(resolvedParameter, out object? nameValue))
            {
                TryReadParameterName(nameValue, out parameterName);
            }

            if (string.IsNullOrWhiteSpace(parameterName) &&
                MeshAssetOperations.TryResolveLinkedEbxAssetEntry(parameterValue, out EbxAssetEntry? textureEntry) &&
                textureEntry is not null)
            {
                parameterName = textureEntry.Path;
            }

            if (string.IsNullOrWhiteSpace(parameterName))
            {
                continue;
            }

            targetParameters[parameterName] = parameterValue;
        }
    }

    private static bool TryCreateBindingFromParameterCollection(
        int materialId,
        object? parametersValue,
        out MeshViewportMaterialBinding? binding)
    {
        binding = null;
        List<MeshViewportTextureCandidate> textureCandidates = [];
        EbxAssetEntry? colorTexture = null;
        EbxAssetEntry? normalTexture = null;
        EbxAssetEntry? maskTexture = null;

        foreach (object? parameter in EnumerateValues(parametersValue))
        {
            if (parameter is null ||
                !TryResolveTextureEntryFromParameter(parameter, out EbxAssetEntry? textureEntry))
            {
                continue;
            }

            string parameterName = textureEntry!.Path;
            if (TryGetParameterName(parameter, out object? nameValue) &&
                TryReadParameterName(nameValue, out string resolvedName) &&
                !string.IsNullOrWhiteSpace(resolvedName))
            {
                parameterName = resolvedName;
            }

            string normalizedName = NormalizeParameterName(parameterName);
            textureCandidates.Add(new MeshViewportTextureCandidate(parameterName, textureEntry));
            if (colorTexture is null && IsColorTextureMatch(normalizedName, textureEntry.Path))
            {
                colorTexture = textureEntry;
                continue;
            }

            if (normalTexture is null && IsNormalTextureMatch(normalizedName, textureEntry.Path))
            {
                normalTexture = textureEntry;
                continue;
            }

            if (maskTexture is null && IsMaskTextureMatch(normalizedName, textureEntry.Path))
            {
                maskTexture = textureEntry;
            }
        }

        colorTexture ??= PickFallbackColorTexture(textureCandidates);
        if (colorTexture is null && normalTexture is null && maskTexture is null)
        {
            return false;
        }

        binding = new MeshViewportMaterialBinding(materialId, colorTexture, normalTexture, maskTexture);
        return true;
    }

    private static bool TryCreateBindingFromTextureSources(
        int materialId,
        out MeshViewportMaterialBinding? binding,
        params object?[] sources)
    {
        binding = null;
        foreach (object? source in sources)
        {
            if (source is null ||
                !TryGetTextureParameters(source, out object? parametersValue))
            {
                continue;
            }

            if (TryCreateBindingFromParameterCollection(materialId, parametersValue, out binding))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetParameterName(object parameter, out object? nameValue)
    {
        return TryGetCollectionValue(parameter, out nameValue, "ParameterName", "Name", "TextureName");
    }

    private static bool TryGetParameterValue(object parameter, out object? parameterValue)
    {
        return TryGetCollectionValue(
            parameter,
            out parameterValue,
            "Value",
            "Texture",
            "TextureValue",
            "TextureRef",
            "ParameterValue",
            "Resource");
    }

    private static bool TryReadParameterName(object? value, out string parameterName)
    {
        if (TryReadStringValue(value, out parameterName))
        {
            return !string.IsNullOrWhiteSpace(parameterName);
        }

        parameterName = string.Empty;
        return false;
    }

    private static bool TryReadStringValue(object? value, out string text, int depth = 0)
    {
        text = string.Empty;
        if (value is null || depth > 6)
        {
            return false;
        }

        if (value is string directText)
        {
            text = directText;
            return !string.IsNullOrWhiteSpace(text);
        }

        if (value is IPrimitive primitive)
        {
            object? actualValue;
            try
            {
                actualValue = primitive.ToActualType();
            }
            catch
            {
                actualValue = null;
            }

            if (actualValue is string primitiveText)
            {
                text = primitiveText;
                return !string.IsNullOrWhiteSpace(text);
            }

            if (actualValue is not null &&
                !ReferenceEquals(actualValue, value) &&
                TryReadStringValue(actualValue, out text, depth + 1))
            {
                return true;
            }
        }

        string fallback = value.ToString() ?? string.Empty;
        string typeName = value.GetType().FullName ?? value.GetType().Name;
        if (!string.IsNullOrWhiteSpace(fallback) &&
            !string.Equals(fallback, typeName, StringComparison.Ordinal) &&
            !string.Equals(fallback, value.GetType().Name, StringComparison.Ordinal))
        {
            text = fallback;
            return true;
        }

        foreach (string memberName in new[] { "Value", "Text", "String", "Name", "CString" })
        {
            if (!MeshAssetOperations.TryGetMemberValue(value, memberName, out object? memberValue) ||
                memberValue is null ||
                ReferenceEquals(memberValue, value))
            {
                continue;
            }

            if (TryReadStringValue(memberValue, out text, depth + 1))
            {
                return true;
            }
        }

        Type valueType = value.GetType();
        string fullTypeName = valueType.FullName ?? valueType.Name;
        if (fullTypeName.EndsWith("CString", StringComparison.Ordinal) ||
            fullTypeName.StartsWith("Frostbite.Reflection", StringComparison.Ordinal))
        {
            foreach (MethodInfo method in valueType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                if (!string.Equals(method.Name, "op_Implicit", StringComparison.Ordinal) ||
                    method.ReturnType != typeof(string))
                {
                    continue;
                }

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != 1 || parameters[0].ParameterType != valueType)
                {
                    continue;
                }

                try
                {
                    if (method.Invoke(null, [value]) is string convertedText &&
                        !string.IsNullOrWhiteSpace(convertedText))
                    {
                        text = convertedText;
                        return true;
                    }
                }
                catch
                {
                }
            }

            foreach (PropertyInfo property in valueType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (!property.CanRead || property.GetIndexParameters().Length != 0)
                {
                    continue;
                }

                object? propertyValue;
                try
                {
                    propertyValue = property.GetValue(value);
                }
                catch
                {
                    continue;
                }

                if (propertyValue is null || ReferenceEquals(propertyValue, value))
                {
                    continue;
                }

                if (TryReadStringValue(propertyValue, out text, depth + 1))
                {
                    return true;
                }
            }

            foreach (FieldInfo field in valueType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (field.FieldType == typeof(string))
                {
                    try
                    {
                        if (field.GetValue(value) is string fieldText &&
                            !string.IsNullOrWhiteSpace(fieldText))
                        {
                            text = fieldText;
                            return true;
                        }
                    }
                    catch
                    {
                    }
                }

                object? fieldValue;
                try
                {
                    fieldValue = field.GetValue(value);
                }
                catch
                {
                    continue;
                }

                if (fieldValue is null || ReferenceEquals(fieldValue, value))
                {
                    continue;
                }

                if (TryReadStringValue(fieldValue, out text, depth + 1))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static object? ResolveParameterObject(object? candidate, int depth = 0)
    {
        if (candidate is null || depth > 8)
        {
            return null;
        }

        if (TryGetParameterName(candidate, out _))
        {
            return candidate;
        }

        if (TryResolveExternalObject(candidate, out object? externalObject) &&
            externalObject is not null)
        {
            object? resolvedExternal = ResolveParameterObject(externalObject, depth + 1);
            if (resolvedExternal is not null)
            {
                return resolvedExternal;
            }
        }

        foreach (string memberName in new[] { "Internal", "Value", "Reference", "Parameter", "Item", "TextureParameter" })
        {
            if (!MeshAssetOperations.TryGetMemberValue(candidate, memberName, out object? nextValue) ||
                nextValue is null ||
                ReferenceEquals(nextValue, candidate))
            {
                continue;
            }

            object? resolved = ResolveParameterObject(nextValue, depth + 1);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        return null;
    }

    private static object? ResolveMaterialObject(object? materialSlot)
    {
        return ResolvePointerObject(materialSlot);
    }

    private static object? ResolveVariationMaterialObject(object? materialSlot)
    {
        if (materialSlot is null)
        {
            return null;
        }

        if (MeshAssetOperations.TryGetMemberValue(materialSlot, "MaterialVariation", out object? materialVariation))
        {
            object? resolvedVariation = ResolvePointerObject(materialVariation);
            if (resolvedVariation is not null)
            {
                return resolvedVariation;
            }
        }

        if (MeshAssetOperations.TryGetMemberValue(materialSlot, "Material", out object? material))
        {
            object? resolvedMaterial = ResolvePointerObject(material);
            if (resolvedMaterial is not null)
            {
                return resolvedMaterial;
            }
        }

        return ResolvePointerObject(materialSlot);
    }

    private static object? ResolveFifaStyleVariationMaterialObject(object? materialSlot)
    {
        if (materialSlot is null)
        {
            return null;
        }

        if (MeshAssetOperations.TryGetMemberValue(materialSlot, "MaterialVariation", out object? materialVariation))
        {
            object? resolvedVariation = ResolvePointerObject(materialVariation);
            if (HasShader(resolvedVariation))
            {
                return resolvedVariation;
            }
        }

        if (MeshAssetOperations.TryGetMemberValue(materialSlot, "Material", out object? materialReference))
        {
            object? resolvedMaterial = ResolveExactMaterialObject(materialReference);
            if (HasShader(resolvedMaterial))
            {
                return resolvedMaterial;
            }
        }

        object? fallback = ResolveVariationMaterialObject(materialSlot);
        return HasShader(fallback) ? fallback : null;
    }

    private static object? ResolvePointerObject(object? candidate, int depth = 0)
    {
        if (candidate is null || depth > 8)
        {
            return null;
        }

        if (MeshAssetOperations.TryGetMemberValue(candidate, "Shader", out object? shaderValue) && shaderValue is not null)
        {
            return candidate;
        }

        if (candidate is IEbxInstance instance)
        {
            return instance;
        }

        if (TryResolveExternalObject(candidate, out object? externalObject) && externalObject is not null)
        {
            return ResolvePointerObject(externalObject, depth + 1);
        }

        foreach (string memberName in new[] { "Internal", "Value", "Reference", "MaterialVariation", "Material", "Asset" })
        {
            if (MeshAssetOperations.TryGetMemberValue(candidate, memberName, out object? nextValue))
            {
                object? resolved = ResolvePointerObject(nextValue, depth + 1);
                if (resolved is not null)
                {
                    return resolved;
                }
            }
        }

        return null;
    }

    private static bool TryResolveExternalObject(object candidate, out object? resolvedObject)
    {
        resolvedObject = null;

        if (candidate is PointerRef pointerRef)
        {
            if (pointerRef.Type == PointerRefType.Internal && pointerRef.Internal is not null)
            {
                resolvedObject = pointerRef.Internal;
                return true;
            }

            if (pointerRef.Type == PointerRefType.External)
            {
                resolvedObject = ResolveExternalObject(
                    ReadGuid(pointerRef.External, "FileGuid"),
                    ReadGuid(pointerRef.External, "ClassGuid"),
                    pointerRef.External.PartitionGuid,
                    pointerRef.External.InstanceGuid);
                return resolvedObject is not null;
            }
        }

        if (candidate is EbxImportReference importReference)
        {
            resolvedObject = ResolveExternalObject(
                ReadGuid(importReference, "FileGuid"),
                Guid.Empty,
                importReference.PartitionGuid,
                importReference.InstanceGuid);
            return resolvedObject is not null;
        }

        Guid fileGuid = ReadGuid(candidate, "FileGuid");
        Guid classGuid = ReadGuid(candidate, "ClassGuid");
        Guid partitionGuid = ReadGuid(candidate, "PartitionGuid");
        Guid instanceGuid = ReadGuid(candidate, "InstanceGuid");
        if (fileGuid != Guid.Empty || classGuid != Guid.Empty || partitionGuid != Guid.Empty || instanceGuid != Guid.Empty)
        {
            resolvedObject = ResolveExternalObject(fileGuid, classGuid, partitionGuid, instanceGuid);
            return resolvedObject is not null;
        }

        if (MeshAssetOperations.TryGetMemberValue(candidate, "External", out object? externalValue) &&
            externalValue is not null)
        {
            return TryResolveExternalObject(externalValue, out resolvedObject);
        }

        return false;
    }

    private static object? ResolveExternalObject(Guid fileGuid, Guid classGuid, Guid partitionGuid, Guid instanceGuid)
    {
        EbxAssetEntry? entry = null;
        if (fileGuid != Guid.Empty)
        {
            entry = AssetManager.GetEbxAssetEntry(fileGuid);
        }

        if (entry is null && partitionGuid != Guid.Empty)
        {
            entry = AssetManager.GetEbxAssetEntry(partitionGuid);
        }

        if (entry is null && instanceGuid != Guid.Empty)
        {
            entry = AssetManager.GetEbxAssetEntry(instanceGuid);
        }

        if (entry is null)
        {
            return null;
        }

        EbxPartition partition = AssetManager.GetEbxPartition(entry);
        IEbxInstance? instance = null;

        if (instanceGuid != Guid.Empty)
        {
            instance = partition.GetObject(instanceGuid);
        }

        if (instance is null && classGuid != Guid.Empty)
        {
            instance = partition.GetObject(classGuid);
        }

        return instance ?? partition.PrimaryInstance;
    }

    private static object? ResolveExactMaterialObject(object? candidate)
    {
        if (candidate is null)
        {
            return null;
        }

        if (candidate is PointerRef pointerRef)
        {
            if (pointerRef.Type == PointerRefType.Internal && pointerRef.Internal is not null)
            {
                return pointerRef.Internal;
            }

            if (pointerRef.Type == PointerRefType.External)
            {
                return ResolveExportedObject(
                    ReadGuid(pointerRef.External, "FileGuid"),
                    ReadGuid(pointerRef.External, "ClassGuid"),
                    pointerRef.External.PartitionGuid,
                    pointerRef.External.InstanceGuid);
            }
        }

        if (candidate is EbxImportReference importReference)
        {
            return ResolveExportedObject(
                ReadGuid(importReference, "FileGuid"),
                Guid.Empty,
                importReference.PartitionGuid,
                importReference.InstanceGuid);
        }

        Guid fileGuid = ReadGuid(candidate, "FileGuid");
        Guid classGuid = ReadGuid(candidate, "ClassGuid");
        Guid partitionGuid = ReadGuid(candidate, "PartitionGuid");
        Guid instanceGuid = ReadGuid(candidate, "InstanceGuid");
        if (fileGuid != Guid.Empty || classGuid != Guid.Empty || partitionGuid != Guid.Empty || instanceGuid != Guid.Empty)
        {
            return ResolveExportedObject(fileGuid, classGuid, partitionGuid, instanceGuid);
        }

        if (MeshAssetOperations.TryGetMemberValue(candidate, "External", out object? externalValue) &&
            externalValue is not null)
        {
            return ResolveExactMaterialObject(externalValue);
        }

        return null;
    }

    private static object? ResolveExportedObject(Guid fileGuid, Guid classGuid, Guid partitionGuid, Guid instanceGuid)
    {
        EbxAssetEntry? entry = null;
        if (fileGuid != Guid.Empty)
        {
            entry = AssetManager.GetEbxAssetEntry(fileGuid);
        }

        if (entry is null && partitionGuid != Guid.Empty)
        {
            entry = AssetManager.GetEbxAssetEntry(partitionGuid);
        }

        if (entry is null && instanceGuid != Guid.Empty)
        {
            entry = AssetManager.GetEbxAssetEntry(instanceGuid);
        }

        if (entry is null)
        {
            return null;
        }

        EbxPartition partition = AssetManager.GetEbxPartition(entry);
        if (classGuid != Guid.Empty)
        {
            IEbxInstance? classMatch = partition.ExportedObjects.FirstOrDefault(obj =>
            {
                AssetClassGuid guid = obj.GetInstanceGuid();
                return guid.IsExported && guid.ExportedGuid == classGuid;
            });

            if (classMatch is not null)
            {
                return classMatch;
            }
        }

        if (instanceGuid != Guid.Empty)
        {
            IEbxInstance? instanceMatch = partition.GetObject(instanceGuid);
            if (instanceMatch is not null)
            {
                return instanceMatch;
            }
        }

        if (classGuid != Guid.Empty || instanceGuid != Guid.Empty)
        {
            return null;
        }

        return partition.PrimaryInstance;
    }

    private static Guid ReadGuid(object instance, string memberName)
    {
        if (!MeshAssetOperations.TryGetMemberValue(instance, memberName, out object? value) ||
            value is null)
        {
            return Guid.Empty;
        }

        return value switch
        {
            Guid guid => guid,
            AssetClassGuid assetClassGuid when assetClassGuid.IsExported => assetClassGuid.ExportedGuid,
            _ => Guid.TryParse(value.ToString(), out Guid parsedGuid) ? parsedGuid : Guid.Empty
        };
    }

    private static IEnumerable<object?> EnumerateValues(object? collection)
    {
        if (collection is null || collection is string || collection is not IEnumerable enumerable)
        {
            yield break;
        }

        foreach (object? item in enumerable)
        {
            yield return item;
        }
    }

    private static string NormalizeParameterName(string parameterName)
    {
        return parameterName.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
    }

    private static bool TryGetCollectionValue(object source, out object? value, params string[] memberNames)
    {
        foreach (string memberName in memberNames)
        {
            if (MeshAssetOperations.TryGetMemberValue(source, memberName, out value))
            {
                return true;
            }
        }

        value = null;
        return false;
    }

    private static bool TryResolveTextureAssetEntry(object? value, out EbxAssetEntry? entry, int depth = 0)
    {
        entry = null;
        if (value is null || depth > 8)
        {
            return false;
        }

        if (value is EbxAssetEntry directEntry)
        {
            entry = TextureAssetOperations.IsTextureAsset(directEntry) ? directEntry : null;
            return entry is not null;
        }

        object? externalValue = null;
        if (MeshAssetOperations.TryGetMemberValue(value, "External", out object? resolvedExternal))
        {
            externalValue = resolvedExternal;
        }

        Guid fileGuid = ReadGuid(externalValue ?? value, "FileGuid");
        Guid partitionGuid = ReadGuid(externalValue ?? value, "PartitionGuid");
        Guid instanceGuid = ReadGuid(externalValue ?? value, "InstanceGuid");
        if (fileGuid != Guid.Empty || partitionGuid != Guid.Empty || instanceGuid != Guid.Empty)
        {
            entry = ResolveTextureAssetEntry(fileGuid, partitionGuid, instanceGuid);
            if (entry is not null)
            {
                return true;
            }
        }

        if (MeshAssetOperations.TryGetMemberValue(value, "Internal", out object? internalValue) &&
            internalValue is not null &&
            TryResolveTextureAssetEntry(internalValue, out entry, depth + 1))
        {
            return true;
        }

        foreach (string memberName in new[] { "FileGuid", "ClassGuid", "PartitionGuid", "InstanceGuid", "External", "Internal", "Value", "Reference", "Asset", "Texture", "Resource", "Mesh" })
        {
            if (MeshAssetOperations.TryGetMemberValue(value, memberName, out object? innerValue) &&
                innerValue is not null &&
                TryResolveTextureAssetEntry(innerValue, out entry, depth + 1))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveTextureEntryFromParameter(object parameter, out EbxAssetEntry? textureEntry)
    {
        textureEntry = null;
        if (MeshAssetOperations.TryResolveLinkedEbxAssetEntry(
                parameter,
                out EbxAssetEntry? directTextureEntry,
                "Value",
                "Texture",
                "TextureValue",
                "TextureRef",
                "ParameterValue",
                "Resource") &&
            directTextureEntry is not null &&
            TextureAssetOperations.IsTextureAsset(directTextureEntry))
        {
            textureEntry = directTextureEntry;
            return true;
        }

        if (!TryGetParameterValue(parameter, out object? parameterValue))
        {
            return false;
        }

        if (MeshAssetOperations.TryResolveLinkedEbxAssetEntry(parameterValue, out directTextureEntry) &&
            directTextureEntry is not null &&
            TextureAssetOperations.IsTextureAsset(directTextureEntry))
        {
            textureEntry = directTextureEntry;
            return true;
        }

        return TryResolveTextureAssetEntry(parameterValue, out textureEntry);
    }

    private static bool HasShader(object? candidate)
    {
        return candidate is not null &&
               MeshAssetOperations.TryGetMemberValue(candidate, "Shader", out object? shaderValue) &&
               shaderValue is not null;
    }

    private static EbxAssetEntry? ResolveTextureAssetEntry(Guid fileGuid, Guid partitionGuid, Guid instanceGuid)
    {
        EbxAssetEntry? entry = null;
        if (fileGuid != Guid.Empty)
        {
            entry = AssetManager.GetEbxAssetEntry(fileGuid);
        }

        if (entry is null && partitionGuid != Guid.Empty)
        {
            entry = AssetManager.GetEbxAssetEntry(partitionGuid);
        }

        if (entry is null && instanceGuid != Guid.Empty)
        {
            entry = AssetManager.GetEbxAssetEntry(instanceGuid);
        }

        return entry is not null && TextureAssetOperations.IsTextureAsset(entry) ? entry : null;
    }

    private static EbxAssetEntry? TryResolveTextureEntryByMeshPath(EbxAssetEntry meshEntry, params string[] suffixes)
    {
        foreach (string meshPath in EnumerateMeshTextureBasePaths(meshEntry))
        {
            foreach (string suffix in suffixes)
            {
                EbxAssetEntry? entry = AssetManager.GetEbxAssetEntry(meshPath + suffix);
                if (entry is not null && TextureAssetOperations.IsTextureAsset(entry))
                {
                    return entry;
                }
            }
        }

        Func<string, bool> classifier = suffixes.Any(static suffix => suffix.Contains("normal", StringComparison.OrdinalIgnoreCase) || suffix.Contains("nsm", StringComparison.OrdinalIgnoreCase) || suffix.Contains("norm", StringComparison.OrdinalIgnoreCase))
            ? LooksLikeNormalTextureAssetPath
            : suffixes.Any(static suffix => suffix.Contains("rsm", StringComparison.OrdinalIgnoreCase) || suffix.Contains("mask", StringComparison.OrdinalIgnoreCase) || suffix.Contains("orm", StringComparison.OrdinalIgnoreCase))
                ? LooksLikeMaskTextureAssetPath
                : LooksLikeColorTextureAssetPath;
        return TryResolveTextureEntryByDirectoryHeuristic(meshEntry, classifier);
    }

    private static string GetMeshVariationPrefix(EbxAssetEntry meshEntry)
    {
        string meshAssetPath = GetMeshAssetPath(meshEntry);
        if (string.IsNullOrWhiteSpace(meshAssetPath))
        {
            return string.Empty;
        }

        string meshBasePath = RemoveMeshSuffix(meshAssetPath);
        int separatorIndex = meshBasePath.LastIndexOf('/');
        if (separatorIndex < 0 || separatorIndex == meshBasePath.Length - 1)
        {
            return meshBasePath + "_";
        }

        string directoryPath = meshBasePath[..separatorIndex];
        string meshStem = meshBasePath[(separatorIndex + 1)..];
        return string.Concat(directoryPath, "/", meshStem, "_");
    }

    private static string? GetMeshTextureBasePath(EbxAssetEntry meshEntry)
    {
        string meshAssetPath = GetMeshAssetPath(meshEntry);
        if (!string.IsNullOrWhiteSpace(meshAssetPath))
        {
            return RemoveMeshSuffix(meshAssetPath);
        }

        if (!string.IsNullOrWhiteSpace(meshEntry.Path) &&
            !string.IsNullOrWhiteSpace(meshEntry.Filename))
        {
            return string.Concat(meshEntry.Path.TrimEnd('/'), "/", RemoveMeshSuffix(meshEntry.Filename));
        }

        return null;
    }

    private static IEnumerable<string> EnumerateMeshTextureBasePaths(EbxAssetEntry meshEntry)
    {
        HashSet<string> yielded = new(StringComparer.OrdinalIgnoreCase);
        string? meshBasePath = GetMeshTextureBasePath(meshEntry);
        if (string.IsNullOrWhiteSpace(meshBasePath))
        {
            yield break;
        }

        foreach (string candidate in ExpandTextureBasePathCandidates(meshBasePath))
        {
            if (!string.IsNullOrWhiteSpace(candidate) && yielded.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> ExpandTextureBasePathCandidates(string meshBasePath)
    {
        HashSet<string> yielded = new(StringComparer.OrdinalIgnoreCase);
        foreach (string stem in EnumerateStemVariants(Path.GetFileName(meshBasePath)))
        {
            foreach (string directory in EnumerateCandidateTextureDirectories(meshBasePath))
            {
                string candidate = string.Concat(directory.TrimEnd('/'), "/", stem);
                if (yielded.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateCandidateTextureDirectories(string meshBasePath)
    {
        string normalizedPath = meshBasePath.Replace('\\', '/');
        int separatorIndex = normalizedPath.LastIndexOf('/');
        if (separatorIndex < 0)
        {
            yield return normalizedPath;
            yield break;
        }

        string directory = normalizedPath[..separatorIndex];
        string parent = directory;
        if (!string.IsNullOrWhiteSpace(directory))
        {
            yield return directory;
        }

        string scenesMarker = "/scenes";
        int scenesIndex = directory.IndexOf(scenesMarker, StringComparison.OrdinalIgnoreCase);
        if (scenesIndex >= 0)
        {
            string beforeScenes = directory[..scenesIndex];
            string afterScenes = directory[(scenesIndex + scenesMarker.Length)..].Trim('/');
            yield return string.IsNullOrWhiteSpace(afterScenes)
                ? string.Concat(beforeScenes, "/textures")
                : string.Concat(beforeScenes, "/textures/", afterScenes);
            yield return string.Concat(beforeScenes, "/textures");
        }

        int exportScenesIndex = directory.IndexOf("/export_highendpc/scenes", StringComparison.OrdinalIgnoreCase);
        if (exportScenesIndex >= 0)
        {
            string root = directory[..exportScenesIndex];
            yield return string.Concat(root, "/textures");
            yield return string.Concat(root, "/export_highendpc/textures");
        }

        int lastSlash = directory.LastIndexOf('/');
        if (lastSlash >= 0)
        {
            parent = directory[..lastSlash];
            if (!string.IsNullOrWhiteSpace(parent))
            {
                yield return parent;
                yield return string.Concat(parent, "/textures");
            }

            int parentSlash = parent.LastIndexOf('/');
            if (parentSlash >= 0)
            {
                string grandParent = parent[..parentSlash];
                if (!string.IsNullOrWhiteSpace(grandParent))
                {
                    yield return grandParent;
                    yield return string.Concat(grandParent, "/textures");
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateStemVariants(string meshStem)
    {
        HashSet<string> yielded = new(StringComparer.OrdinalIgnoreCase) { meshStem };
        yield return meshStem;

        string trimmed = TrimTerminalVariantSuffix(meshStem);
        if (yielded.Add(trimmed))
        {
            yield return trimmed;
        }

        string deHighend = trimmed.EndsWith("_h", StringComparison.OrdinalIgnoreCase) ? trimmed[..^2] : trimmed;
        if (yielded.Add(deHighend))
        {
            yield return deHighend;
        }
    }

    private static string TrimTerminalVariantSuffix(string meshStem)
    {
        int separatorIndex = meshStem.LastIndexOf('_');
        if (separatorIndex < 0 || separatorIndex == meshStem.Length - 1)
        {
            return meshStem;
        }

        string tail = meshStem[(separatorIndex + 1)..];
        return tail.Length <= 2 && tail.All(char.IsLetterOrDigit)
            ? meshStem[..separatorIndex]
            : meshStem;
    }

    private static EbxAssetEntry? TryResolveTextureEntryByDirectoryHeuristic(EbxAssetEntry meshEntry, Func<string, bool> assetPathMatcher)
    {
        string? meshBasePath = GetMeshTextureBasePath(meshEntry);
        if (string.IsNullOrWhiteSpace(meshBasePath))
        {
            return null;
        }

        string[] normalizedStemVariants = EnumerateStemVariants(Path.GetFileName(meshBasePath))
            .Select(NormalizeLookupToken)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        EbxAssetEntry? bestEntry = null;
        int bestScore = int.MinValue;

        foreach (string directory in EnumerateCandidateTextureDirectories(meshBasePath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (EbxAssetEntry entry in GetTextureEntriesForDirectory(directory))
            {
                if (!assetPathMatcher(entry.Name))
                {
                    continue;
                }

                string normalizedEntryName = NormalizeLookupToken(entry.Filename);
                int score = 0;
                foreach (string normalizedStem in normalizedStemVariants)
                {
                    if (normalizedEntryName.StartsWith(normalizedStem, StringComparison.OrdinalIgnoreCase))
                    {
                        score = Math.Max(score, 5);
                    }
                    else if (normalizedEntryName.Contains(normalizedStem, StringComparison.OrdinalIgnoreCase))
                    {
                        score = Math.Max(score, 3);
                    }
                }

                if (entry.Path.Equals(directory, StringComparison.OrdinalIgnoreCase))
                {
                    score++;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestEntry = entry;
                }
            }
        }

        return bestScore > 0 ? bestEntry : null;
    }

    private static string NormalizeLookupToken(string value)
    {
        return new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }

    private static string GetMeshAssetPath(EbxAssetEntry meshEntry)
    {
        if (!string.IsNullOrWhiteSpace(meshEntry.Name) &&
            meshEntry.Name.Contains('/', StringComparison.Ordinal))
        {
            return meshEntry.Name;
        }

        if (!string.IsNullOrWhiteSpace(meshEntry.Path) &&
            !string.IsNullOrWhiteSpace(meshEntry.Filename))
        {
            return string.Concat(meshEntry.Path.TrimEnd('/'), "/", meshEntry.Filename);
        }

        return meshEntry.Name ?? string.Empty;
    }

    private static string RemoveMeshSuffix(string assetPath)
    {
        return assetPath.EndsWith("_mesh", StringComparison.OrdinalIgnoreCase)
            ? assetPath[..^5]
            : assetPath;
    }

    private static void WarmLookupMetadataCache()
    {
        try
        {
            try
            {
                Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            }
            catch
            {
            }

            _ = GetTextureDirectoryIndex().Count;
        }
        catch
        {
        }
    }

    private static bool TryFindMaterialSlotCollection(object? source, out object? materialsValue)
    {
        return TryFindMaterialSlotCollection(
            source,
            out materialsValue,
            new HashSet<object>(ReferenceEqualityComparer.Instance),
            0);
    }

    private static bool TryFindMaterialSlotCollection(
        object? source,
        out object? materialsValue,
        HashSet<object> visited,
        int depth)
    {
        materialsValue = null;
        if (source is null || depth > 5 || !ShouldInspectRecursiveValue(source))
        {
            return false;
        }

        if (!visited.Add(source))
        {
            return false;
        }

        if (TryGetCollectionValue(source, out materialsValue, "Materials", "MaterialSlots", "MaterialCollections", "MaterialEntries", "MeshMaterials", "RenderableMaterials", "VariationMaterials") &&
            LooksLikeMaterialSlotCollection(materialsValue))
        {
            return true;
        }

        foreach (object? candidate in EnumerateRecursiveSearchCandidates(source))
        {
            if (candidate is null || ReferenceEquals(candidate, source))
            {
                continue;
            }

            if (LooksLikeMaterialSlotCollection(candidate))
            {
                materialsValue = candidate;
                return true;
            }

            if (TryFindMaterialSlotCollection(candidate, out materialsValue, visited, depth + 1))
            {
                return true;
            }
        }

        materialsValue = null;
        return false;
    }

    private static IEnumerable<object?> EnumerateRecursiveSearchCandidates(object source)
    {
        foreach (string memberName in new[]
                 {
                     "Resource",
                     "MeshSetResource",
                     "MeshResource",
                     "Mesh",
                     "Meshes",
                     "Variation",
                     "Variations",
                     "DefaultVariation",
                     "DefaultVariations",
                     "Material",
                     "Materials",
                     "MeshMaterials",
                     "RenderableMaterials",
                     "VariationMaterials",
                     "MaterialSlots",
                     "MaterialCollections",
                     "MaterialEntries",
                     "TextureParameters",
                     "Parameters",
                     "Shader",
                     "ShaderPreset",
                     "Value",
                     "Reference",
                     "Internal",
                     "Asset"
                 })
        {
            if (MeshAssetOperations.TryGetMemberValue(source, memberName, out object? value))
            {
                yield return value;
            }
        }

        if (source is IEnumerable enumerable and not string)
        {
            foreach (object? item in enumerable)
            {
                yield return item;
            }
        }
    }

    private static bool LooksLikeMaterialSlotCollection(object? candidate)
    {
        if (candidate is null || candidate is string || candidate is not IEnumerable enumerable)
        {
            return false;
        }

        int inspected = 0;
        foreach (object? item in enumerable)
        {
            if (item is null)
            {
                continue;
            }

            inspected++;
            if (LooksLikeMaterialSlot(item))
            {
                return true;
            }

            if (inspected >= 8)
            {
                break;
            }
        }

        return false;
    }

    private static bool TryResolveTextureEntryCandidate(object? value, out EbxAssetEntry? textureEntry)
    {
        if (value is null)
        {
            textureEntry = null;
            return false;
        }

        if (TryResolveTextureEntryFromParameter(value, out textureEntry))
        {
            return true;
        }

        return MeshAssetOperations.TryResolveLinkedEbxAssetEntry(value, out textureEntry) &&
               textureEntry is not null &&
               TextureAssetOperations.IsTextureAsset(textureEntry);
    }

    private static IEnumerable<EbxAssetEntry> EnumerateVariationDatabaseEntries()
    {
        lock (c_metadataCacheLock)
        {
            c_variationDatabaseEntries ??= AssetManager.EnumerateEbxAssetEntries()
                .Where(entry => LooksLikeVariationDatabaseType(entry.Type))
                .ToArray();
            return c_variationDatabaseEntries;
        }
    }

    private static bool LooksLikeVariationDatabaseType(string type)
    {
        return type.Equals("MeshVariationDatabase", StringComparison.OrdinalIgnoreCase) ||
               type.Contains("VariationDatabase", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<EbxAssetEntry>> GetTextureDirectoryIndex()
    {
        lock (c_metadataCacheLock)
        {
            c_textureEntriesByDirectory ??= AssetManager.EnumerateEbxAssetEntries()
                .Where(TextureAssetOperations.IsTextureAsset)
                .GroupBy(entry => entry.Path ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<EbxAssetEntry>)group.ToArray(),
                    StringComparer.OrdinalIgnoreCase);
            return c_textureEntriesByDirectory;
        }
    }

    private static IReadOnlyList<EbxAssetEntry> GetTextureEntriesForDirectory(string directory)
    {
        return GetTextureDirectoryIndex().TryGetValue(directory, out IReadOnlyList<EbxAssetEntry>? entries)
            ? entries
            : Array.Empty<EbxAssetEntry>();
    }

    private static bool LooksLikeMaterialSlot(object candidate)
    {
        object? resolvedCandidate = ResolvePointerObject(candidate);
        if (resolvedCandidate is not null && !ReferenceEquals(resolvedCandidate, candidate))
        {
            return LooksLikeMaterialSlot(resolvedCandidate);
        }

        return MeshAssetOperations.TryGetMemberValue(candidate, "Material", out _) ||
               MeshAssetOperations.TryGetMemberValue(candidate, "MaterialVariation", out _) ||
               MeshAssetOperations.TryGetMemberValue(candidate, "TextureParameters", out _) ||
               MeshAssetOperations.TryGetMemberValue(candidate, "Parameters", out _) ||
               MeshAssetOperations.TryGetMemberValue(candidate, "Shader", out _);
    }

    private static bool ShouldInspectRecursiveValue(object value)
    {
        Type type = value.GetType();
        return type.IsClass || (value is IEnumerable and not string);
    }

    private static EbxAssetEntry? PickFallbackColorTexture(IReadOnlyList<MeshViewportTextureCandidate> textureCandidates)
    {
        if (textureCandidates.Count == 0)
        {
            return null;
        }

        foreach (MeshViewportTextureCandidate candidate in textureCandidates)
        {
            if (LooksLikeColorTextureParameter(NormalizeParameterName(candidate.ParameterName)))
            {
                return candidate.Entry;
            }
        }

        foreach (MeshViewportTextureCandidate candidate in textureCandidates)
        {
            if (LooksLikeColorTextureAssetPath(candidate.Entry.Path))
            {
                return candidate.Entry;
            }
        }

        foreach (MeshViewportTextureCandidate candidate in textureCandidates)
        {
            string normalizedName = NormalizeParameterName(candidate.ParameterName);
            if (!LooksLikeNormalTextureParameter(normalizedName) &&
                !LooksLikeMaskTextureParameter(normalizedName) &&
                !LooksLikeNormalTextureAssetPath(candidate.Entry.Path) &&
                !LooksLikeMaskTextureAssetPath(candidate.Entry.Path))
            {
                return candidate.Entry;
            }
        }

        return textureCandidates.Count == 1 ? textureCandidates[0].Entry : null;
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new();

        public new bool Equals(object? x, object? y)
        {
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(object obj)
        {
            return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }

    private static bool IsColorTextureParameter(string parameterName)
    {
        return LooksLikeColorTextureParameter(parameterName);
    }

    private static bool IsColorTextureMatch(string parameterName, string assetPath)
    {
        return IsColorTextureParameter(parameterName) || LooksLikeColorTextureAssetPath(assetPath);
    }

    private static bool IsNormalTextureParameter(string parameterName)
    {
        return LooksLikeNormalTextureParameter(parameterName);
    }

    private static bool IsNormalTextureMatch(string parameterName, string assetPath)
    {
        return IsNormalTextureParameter(parameterName) || LooksLikeNormalTextureAssetPath(assetPath);
    }

    private static bool IsMaskTextureParameter(string parameterName)
    {
        return LooksLikeMaskTextureParameter(parameterName);
    }

    private static bool IsMaskTextureMatch(string parameterName, string assetPath)
    {
        return IsMaskTextureParameter(parameterName) || LooksLikeMaskTextureAssetPath(assetPath);
    }

    private static bool LooksLikeColorTextureParameter(string parameterName)
    {
        return parameterName.StartsWith("colortexture", StringComparison.Ordinal) ||
               parameterName.StartsWith("diffusetexture", StringComparison.Ordinal) ||
               parameterName.StartsWith("basecolortexture", StringComparison.Ordinal) ||
               parameterName.StartsWith("albedotexture", StringComparison.Ordinal) ||
               parameterName.StartsWith("basetexture", StringComparison.Ordinal) ||
               parameterName.StartsWith("diffusemap", StringComparison.Ordinal) ||
               parameterName.StartsWith("albedomap", StringComparison.Ordinal) ||
               parameterName.StartsWith("colormap", StringComparison.Ordinal) ||
               parameterName.StartsWith("texture", StringComparison.Ordinal) ||
               parameterName.Contains("diffuse", StringComparison.Ordinal) ||
               parameterName.Contains("albedo", StringComparison.Ordinal) ||
               parameterName.Contains("basecolor", StringComparison.Ordinal);
    }

    private static bool LooksLikeColorTextureAssetPath(string assetPath)
    {
        string normalizedPath = NormalizeAssetHint(assetPath);
        return normalizedPath.Contains("color", StringComparison.Ordinal) ||
               normalizedPath.Contains("diffuse", StringComparison.Ordinal) ||
               normalizedPath.Contains("albedo", StringComparison.Ordinal) ||
               normalizedPath.Contains("basecolor", StringComparison.Ordinal);
    }

    private static bool LooksLikeNormalTextureParameter(string parameterName)
    {
        return parameterName.StartsWith("normal", StringComparison.Ordinal) ||
               parameterName.StartsWith("basenormal", StringComparison.Ordinal) ||
               parameterName.StartsWith("nsm", StringComparison.Ordinal) ||
               parameterName.StartsWith("seampatternnorm", StringComparison.Ordinal) ||
               parameterName.StartsWith("normalclamp", StringComparison.Ordinal) ||
               parameterName.StartsWith("bump", StringComparison.Ordinal) ||
               parameterName.StartsWith("normalmap", StringComparison.Ordinal) ||
               parameterName.Contains("normalmap", StringComparison.Ordinal) ||
               parameterName.Contains("bumpmap", StringComparison.Ordinal);
    }

    private static bool LooksLikeNormalTextureAssetPath(string assetPath)
    {
        string normalizedPath = NormalizeAssetHint(assetPath);
        return normalizedPath.Contains("normal", StringComparison.Ordinal) ||
               normalizedPath.Contains("basenormal", StringComparison.Ordinal) ||
               normalizedPath.Contains("nsm", StringComparison.Ordinal) ||
               normalizedPath.Contains("bump", StringComparison.Ordinal);
    }

    private static bool LooksLikeMaskTextureParameter(string parameterName)
    {
        return parameterName.StartsWith("rsm", StringComparison.Ordinal) ||
               parameterName.StartsWith("r_ssr", StringComparison.Ordinal) ||
               parameterName.StartsWith("r_s_ssr_t", StringComparison.Ordinal) ||
               parameterName.StartsWith("coeff", StringComparison.Ordinal) ||
               parameterName.StartsWith("specmask", StringComparison.Ordinal) ||
               parameterName.StartsWith("roughness", StringComparison.Ordinal) ||
               parameterName.StartsWith("metallic", StringComparison.Ordinal) ||
               parameterName.StartsWith("specular", StringComparison.Ordinal) ||
               parameterName.StartsWith("occlusion", StringComparison.Ordinal) ||
               parameterName.StartsWith("ao", StringComparison.Ordinal) ||
               parameterName.Contains("mask", StringComparison.Ordinal);
    }

    private static bool LooksLikeMaskTextureAssetPath(string assetPath)
    {
        string normalizedPath = NormalizeAssetHint(assetPath);
        return normalizedPath.Contains("rsm", StringComparison.Ordinal) ||
               normalizedPath.Contains("mask", StringComparison.Ordinal) ||
               normalizedPath.Contains("rough", StringComparison.Ordinal) ||
               normalizedPath.Contains("metal", StringComparison.Ordinal) ||
               normalizedPath.Contains("spec", StringComparison.Ordinal) ||
               normalizedPath.Contains("occlusion", StringComparison.Ordinal) ||
               normalizedPath.Contains("ao", StringComparison.Ordinal) ||
               normalizedPath.Contains("coeff", StringComparison.Ordinal);
    }

    private static string NormalizeAssetHint(string assetPath)
    {
        return assetPath.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("/", string.Empty, StringComparison.Ordinal)
            .Replace("\\", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
    }
}
