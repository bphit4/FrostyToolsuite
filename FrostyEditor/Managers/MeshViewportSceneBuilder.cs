using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Frosty.Sdk;
using Frosty.Sdk.Managers;
using Frosty.Sdk.Managers.Entries;
using FrostyEditor.MeshViewportHost;
using FrostyEditor.ViewModels;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace FrostyEditor.Managers;

internal static class MeshViewportSceneBuilder
{
    internal enum BuildStage
    {
        GeometryOnly = 0,
        TexturedLowRes = 1,
        Textured = 2
    }

    private enum PreviewTextureQuality
    {
        LowRes = 0,
        HighRes = 1
    }

    private sealed record CachedSceneAssets(string ObjPath, IReadOnlyList<CachedLodData> Lods);
    private sealed record CachedLodData(int Index, IReadOnlyList<CachedSectionData> Sections);
    private sealed record CachedSectionData(string ObjectName, string SectionName, int SectionIndex, int MaterialId);
    private sealed record TexturePreloadRequest(string CacheKey, Func<byte[]?> Loader);
    private readonly record struct MeshBindingCacheKey(Guid EntryGuid, uint VariationHash, string VariationName);
    private readonly record struct DeferredTextureWarmKey(
        Guid EntryGuid,
        uint VariationHash,
        string VariationName,
        PreviewTextureQuality TextureQuality,
        TextureChannelMask Channels,
        bool IncludeNormalMaps);

    private static readonly object c_cacheLock = new();
    private static readonly Dictionary<Guid, CachedSceneAssets> c_sceneCache = [];
    private static readonly Dictionary<Guid, Task<CachedSceneAssets>> c_sceneBuildTasks = [];
    private static readonly object c_bindingCacheLock = new();
    private static readonly Dictionary<MeshBindingCacheKey, IReadOnlyDictionary<int, MeshViewportMaterialBinding>> c_bindingCache = [];
    private static readonly Dictionary<MeshBindingCacheKey, Task<IReadOnlyDictionary<int, MeshViewportMaterialBinding>>> c_bindingBuildTasks = [];
    private static readonly object c_textureCacheLock = new();
    private static readonly Dictionary<string, byte[]?> c_texturePngCache = [];
    private static readonly ConcurrentDictionary<DeferredTextureWarmKey, Task> c_deferredTextureWarmTasks = [];
    private static int c_backgroundWarmupStarted;
    private static readonly string c_eyeFallbackTexturePath = Path.Combine(AppContext.BaseDirectory, "Assets", "MeshViewport", "eye_fallback_color.png");

    public static void Prewarm(MeshAssetLoadResult load, string meshName, bool includeTextures)
    {
        ArgumentNullException.ThrowIfNull(load);
        ArgumentException.ThrowIfNullOrWhiteSpace(meshName);

        StartBackgroundWarmup();
        _ = GetOrCreateCachedSceneAssetsTask(load, meshName);
        if (includeTextures)
        {
            _ = GetOrCreateBindingsTask(load, null);
        }
    }

    public static MeshViewportSceneData Build(MeshAssetEditorViewModel viewModel, BuildStage stage = BuildStage.Textured)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        StartBackgroundWarmup();
        bool includeTextures = stage != BuildStage.GeometryOnly;
        int activeLodIndex = viewModel.SelectedLod?.Index ?? 0;
        TextureChannelMask activeChannels = ToTextureChannelMask(viewModel.ActiveChannelMask);
        PreviewTextureQuality textureQuality = stage == BuildStage.TexturedLowRes
            ? PreviewTextureQuality.LowRes
            : PreviewTextureQuality.HighRes;
        bool includeNormalMaps = stage == BuildStage.Textured;
        Dictionary<string, byte[]> textureCache = [];
        List<MeshViewportLodData> lods = [];
        MeshVariationRecord? selectedVariation = viewModel.PreviewVariationRecord;
        Task<CachedSceneAssets> cachedAssetsTask = GetOrCreateCachedSceneAssetsTask(viewModel.LoadResult, viewModel.MeshName);
        Task<IReadOnlyDictionary<int, MeshViewportMaterialBinding>>? bindingsTask = includeTextures
            ? GetOrCreateBindingsTask(viewModel.LoadResult, selectedVariation)
            : null;

        CachedSceneAssets cachedAssets = cachedAssetsTask.GetAwaiter().GetResult();
        IReadOnlyDictionary<int, MeshViewportMaterialBinding> bindings = bindingsTask is not null
            ? bindingsTask.GetAwaiter().GetResult()
            : new Dictionary<int, MeshViewportMaterialBinding>();

        if (includeTextures)
        {
            PreloadTextures(viewModel.LoadResult, cachedAssets, bindings, activeLodIndex, textureQuality, activeChannels, includeNormalMaps);
        }

        foreach (CachedLodData lod in cachedAssets.Lods)
        {
            List<MeshViewportSectionData> sections = [];
            foreach (CachedSectionData section in lod.Sections)
            {
                bool isActiveLod = lod.Index == activeLodIndex;
                MeshViewportMaterialBinding? binding = includeTextures
                    ? ResolveBinding(bindings, section.MaterialId, section.SectionIndex)
                    : null;
                bool isVisible = lod.Index != viewModel.SelectedLod?.Index ||
                                 viewModel.SelectedLod?.Sections.FirstOrDefault(item => item.Index == section.SectionIndex)?.IsVisible != false;
                bool shouldLoadTexturesForSection = includeTextures && isActiveLod && isVisible;

                byte[]? diffuseTexturePng = shouldLoadTexturesForSection
                    ? ResolvePreviewDiffuseTexturePng(viewModel.LoadResult, section, binding, textureCache, textureQuality, activeChannels)
                    : null;
                bool usePreviewNormalMap = shouldLoadTexturesForSection &&
                                           includeNormalMaps &&
                                           ShouldApplyPreviewNormalMap(viewModel.LoadResult.Entry.Path, binding);

                sections.Add(new MeshViewportSectionData
                {
                    ObjectName = section.ObjectName,
                    SectionIndex = section.SectionIndex,
                    MaterialId = section.MaterialId,
                    Visible = isVisible,
                    DiffuseTexturePng = diffuseTexturePng,
                    NormalTexturePng = usePreviewNormalMap
                        ? TryLoadTexturePng(binding?.NormalTextureEntry, textureCache, forceOpaqueAlpha: false, textureQuality, TextureChannelMask.Rgba, neutralizeEyelash: false)
                        : null
                });
            }

            lods.Add(new MeshViewportLodData
            {
                Index = lod.Index,
                Sections = sections
            });
        }

        return new MeshViewportSceneData
        {
            Name = viewModel.MeshName,
            ObjPath = cachedAssets.ObjPath,
            CurrentLod = viewModel.SelectedLod?.Index ?? 0,
            TexturesEnabled = viewModel.SelectedRenderMode is MeshViewportRenderMode.Lit or MeshViewportRenderMode.Base,
            Wireframe = viewModel.SelectedRenderMode == MeshViewportRenderMode.Wireframe,
            ViewPreset = ToHostPreset(viewModel.PreviewView),
            Lods = lods
        };
    }

    public static void QueueDeferredTextureWarmup(MeshAssetEditorViewModel viewModel, BuildStage stage)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        if (stage == BuildStage.GeometryOnly ||
            viewModel.SelectedRenderMode is not MeshViewportRenderMode.Lit and not MeshViewportRenderMode.Base)
        {
            return;
        }

        PreviewTextureQuality textureQuality = stage == BuildStage.TexturedLowRes
            ? PreviewTextureQuality.LowRes
            : PreviewTextureQuality.HighRes;
        TextureChannelMask activeChannels = ToTextureChannelMask(viewModel.ActiveChannelMask);
        bool includeNormalMaps = stage == BuildStage.Textured;
        MeshVariationRecord? selectedVariation = viewModel.PreviewVariationRecord;
        DeferredTextureWarmKey warmKey = new(
            viewModel.LoadResult.Entry.Guid,
            selectedVariation?.VariationAssetNameHash ?? 0,
            selectedVariation?.Name ?? string.Empty,
            textureQuality,
            activeChannels,
            includeNormalMaps);

        c_deferredTextureWarmTasks.GetOrAdd(warmKey, _ => Task.Run(() =>
        {
            try
            {
                Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            }
            catch
            {
            }

            try
            {
                CachedSceneAssets cachedAssets = GetOrCreateCachedSceneAssets(viewModel.LoadResult, viewModel.MeshName);
                IReadOnlyDictionary<int, MeshViewportMaterialBinding> bindings = GetOrCreateBindings(
                    viewModel.LoadResult,
                    selectedVariation);
                int activeLodIndex = viewModel.SelectedLod?.Index ?? 0;
                PreloadTextures(
                    viewModel.LoadResult,
                    cachedAssets,
                    bindings,
                    lod => lod != activeLodIndex,
                    textureQuality,
                    activeChannels,
                    includeNormalMaps,
                    maxDegreeOfParallelism: 1);
            }
            finally
            {
                c_deferredTextureWarmTasks.TryRemove(warmKey, out Task? _);
            }
        }));
    }

    private static void PreloadTextures(
        MeshAssetLoadResult load,
        CachedSceneAssets cachedAssets,
        IReadOnlyDictionary<int, MeshViewportMaterialBinding> bindings,
        int activeLodIndex,
        PreviewTextureQuality textureQuality,
        TextureChannelMask activeChannels,
        bool includeNormalMaps)
    {
        PreloadTextures(
            load,
            cachedAssets,
            bindings,
            lodIndex => lodIndex == activeLodIndex,
            textureQuality,
            activeChannels,
            includeNormalMaps,
            maxDegreeOfParallelism: Math.Clamp(Environment.ProcessorCount / 2, 2, 4));
    }

    private static void PreloadTextures(
        MeshAssetLoadResult load,
        CachedSceneAssets cachedAssets,
        IReadOnlyDictionary<int, MeshViewportMaterialBinding> bindings,
        Func<int, bool> shouldIncludeLod,
        PreviewTextureQuality textureQuality,
        TextureChannelMask activeChannels,
        bool includeNormalMaps,
        int maxDegreeOfParallelism)
    {
        Dictionary<string, TexturePreloadRequest> requests = [];
        foreach (CachedLodData lod in cachedAssets.Lods)
        {
            if (!shouldIncludeLod(lod.Index))
            {
                continue;
            }

            foreach (CachedSectionData section in lod.Sections)
            {
                MeshViewportMaterialBinding? binding = ResolveBinding(bindings, section.MaterialId, section.SectionIndex);
                if (TryCreateDiffuseTexturePreloadRequest(load, section, binding, textureQuality, activeChannels, out TexturePreloadRequest? diffuseRequest))
                {
                    requests.TryAdd(diffuseRequest!.CacheKey, diffuseRequest);
                }

                if (!includeNormalMaps ||
                    !ShouldApplyPreviewNormalMap(load.Entry.Path, binding) ||
                    !TryCreateNormalTexturePreloadRequest(binding, textureQuality, out TexturePreloadRequest? normalRequest))
                {
                    continue;
                }

                requests.TryAdd(normalRequest!.CacheKey, normalRequest);
            }
        }

        if (requests.Count == 0)
        {
            return;
        }

        Parallel.ForEach(
            requests.Values,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, maxDegreeOfParallelism)
            },
            request =>
            {
                try
                {
                    _ = request.Loader();
                }
                catch
                {
                }
            });
    }

    public static void Invalidate(Guid entryGuid)
    {
        lock (c_cacheLock)
        {
            c_sceneCache.Remove(entryGuid);
            c_sceneBuildTasks.Remove(entryGuid);
        }

        lock (c_bindingCacheLock)
        {
            foreach (MeshBindingCacheKey key in c_bindingCache.Keys.Where(key => key.EntryGuid == entryGuid).ToArray())
            {
                c_bindingCache.Remove(key);
            }

            foreach (MeshBindingCacheKey key in c_bindingBuildTasks.Keys.Where(key => key.EntryGuid == entryGuid).ToArray())
            {
                c_bindingBuildTasks.Remove(key);
            }
        }

        MeshViewportMaterialResolver.Invalidate(entryGuid);
    }

    public static void StartBackgroundWarmup()
    {
        if (Interlocked.Exchange(ref c_backgroundWarmupStarted, 1) != 0)
        {
            return;
        }

        MeshViewportMaterialResolver.StartBackgroundWarmup();
    }

    private static CachedSceneAssets GetOrCreateCachedSceneAssets(MeshAssetLoadResult load, string meshName)
    {
        lock (c_cacheLock)
        {
            if (c_sceneCache.TryGetValue(load.Entry.Guid, out CachedSceneAssets? cached) &&
                File.Exists(cached.ObjPath))
            {
                return cached;
            }
        }

        return GetOrCreateCachedSceneAssetsTask(load, meshName).GetAwaiter().GetResult();
    }

    private static Task<CachedSceneAssets> GetOrCreateCachedSceneAssetsTask(MeshAssetLoadResult load, string meshName)
    {
        lock (c_cacheLock)
        {
            if (c_sceneCache.TryGetValue(load.Entry.Guid, out CachedSceneAssets? cached) &&
                File.Exists(cached.ObjPath))
            {
                return Task.FromResult(cached);
            }

            if (c_sceneBuildTasks.TryGetValue(load.Entry.Guid, out Task<CachedSceneAssets>? existingTask))
            {
                return existingTask;
            }

            Task<CachedSceneAssets> buildTask = Task.Run(() =>
            {
                try
                {
                    CachedSceneAssets rebuilt = CreateCachedSceneAssets(load, meshName);
                    lock (c_cacheLock)
                    {
                        c_sceneCache[load.Entry.Guid] = rebuilt;
                    }

                    return rebuilt;
                }
                finally
                {
                    lock (c_cacheLock)
                    {
                        c_sceneBuildTasks.Remove(load.Entry.Guid);
                    }
                }
            });

            c_sceneBuildTasks[load.Entry.Guid] = buildTask;
            return buildTask;
        }
    }

    private static CachedSceneAssets CreateCachedSceneAssets(MeshAssetLoadResult load, string meshName)
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "FrostyEditor2", "MeshViewport");
        Directory.CreateDirectory(tempDirectory);
        string safeName = string.Concat(meshName.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        string objPath = Path.Combine(tempDirectory, $"{safeName}_{load.Entry.Guid:N}.obj");
        List<MeshObjCodec.MeshDecodedSection> decodedSections = MeshObjCodec.DecodeSections(load);
        ExportViewportObj(load, objPath, decodedSections);

        List<CachedLodData> lods = [];

        foreach (IGrouping<int, MeshObjCodec.MeshDecodedSection> lodGroup in decodedSections.GroupBy(section => section.Lod.Index).OrderBy(group => group.Key))
        {
            List<CachedSectionData> sections = [];
            foreach (MeshObjCodec.MeshDecodedSection decodedSection in lodGroup)
            {
                sections.Add(new CachedSectionData(
                    decodedSection.ObjectName,
                    decodedSection.Section.Name ?? string.Empty,
                    decodedSection.Section.Index,
                    decodedSection.Section.MaterialId));
            }

            lods.Add(new CachedLodData(lodGroup.Key, sections));
        }

        return new CachedSceneAssets(objPath, lods);
    }

    private static void ExportViewportObj(
        MeshAssetLoadResult load,
        string objPath,
        IReadOnlyList<MeshObjCodec.MeshDecodedSection> decodedSections)
    {
        StringBuilder builder = new();
        builder.AppendLine("# Frosty Editor 2.0 viewport OBJ export");
        builder.AppendLine($"# Asset: {load.Entry.Name}");
        builder.AppendLine($"# Resource: {load.ResourceEntry.Name}");

        int vertexBase = 1;
        foreach (MeshObjCodec.MeshDecodedSection section in decodedSections)
        {
            Matrix4x4[]? palette = MeshViewportTransformResolver.ResolvePalette(load, section);
            builder.AppendLine();
            builder.AppendLine($"o {section.ObjectName}");

            foreach (MeshObjCodec.MeshDecodedVertex vertex in section.Vertices)
            {
                MeshViewportTransformResolver.TransformVertex(section, vertex, palette, out Vector3 position, out _);
                builder.AppendLine(FormattableString.Invariant(
                    $"v {position.X:0.######} {position.Y:0.######} {position.Z:0.######}"));
            }

            foreach (MeshObjCodec.MeshDecodedVertex vertex in section.Vertices)
            {
                builder.AppendLine(FormattableString.Invariant(
                    $"vt {vertex.Uv.X:0.######} {1.0f - vertex.Uv.Y:0.######}"));
            }

            foreach (MeshObjCodec.MeshDecodedVertex vertex in section.Vertices)
            {
                MeshViewportTransformResolver.TransformVertex(section, vertex, palette, out _, out Vector3 normal);
                Vector3 safeNormal = normal.LengthSquared() < 0.000001f
                    ? Vector3.UnitY
                    : Vector3.Normalize(normal);
                builder.AppendLine(FormattableString.Invariant(
                    $"vn {safeNormal.X:0.######} {safeNormal.Y:0.######} {safeNormal.Z:0.######}"));
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

        File.WriteAllText(objPath, builder.ToString(), Encoding.UTF8);
    }

    private static IReadOnlyDictionary<int, MeshViewportMaterialBinding> GetOrCreateBindings(
        MeshAssetLoadResult load,
        MeshVariationRecord? selectedVariation)
    {
        MeshBindingCacheKey cacheKey = CreateBindingCacheKey(load.Entry.Guid, selectedVariation);
        lock (c_bindingCacheLock)
        {
            if (c_bindingCache.TryGetValue(cacheKey, out IReadOnlyDictionary<int, MeshViewportMaterialBinding>? cached))
            {
                return cached;
            }
        }

        return GetOrCreateBindingsTask(load, selectedVariation).GetAwaiter().GetResult();
    }

    private static Task<IReadOnlyDictionary<int, MeshViewportMaterialBinding>> GetOrCreateBindingsTask(
        MeshAssetLoadResult load,
        MeshVariationRecord? selectedVariation)
    {
        MeshBindingCacheKey cacheKey = CreateBindingCacheKey(load.Entry.Guid, selectedVariation);
        lock (c_bindingCacheLock)
        {
            if (c_bindingCache.TryGetValue(cacheKey, out IReadOnlyDictionary<int, MeshViewportMaterialBinding>? cached))
            {
                return Task.FromResult(cached);
            }

            if (c_bindingBuildTasks.TryGetValue(cacheKey, out Task<IReadOnlyDictionary<int, MeshViewportMaterialBinding>>? existingTask))
            {
                return existingTask;
            }

            Task<IReadOnlyDictionary<int, MeshViewportMaterialBinding>> buildTask = Task.Run<IReadOnlyDictionary<int, MeshViewportMaterialBinding>>(() =>
            {
                try
                {
                    IReadOnlyDictionary<int, MeshViewportMaterialBinding> rebuilt = MeshViewportMaterialResolver.ResolveBindings(load, selectedVariation);
                    if (rebuilt.Count == 0)
                    {
                        MeshViewportMaterialResolver.Invalidate(load.Entry.Guid);
                        Thread.Sleep(75);
                        rebuilt = MeshViewportMaterialResolver.ResolveBindings(load, selectedVariation);
                        if (rebuilt.Count > 0)
                        {
                            FrostyLogger.Logger?.LogInfo(
                                $"Mesh viewport recovered material bindings for \"{load.Entry.Name}\" after invalidating stale mesh viewport caches.");
                        }
                    }

                    if (rebuilt.Count > 0)
                    {
                        lock (c_bindingCacheLock)
                        {
                            c_bindingCache[cacheKey] = rebuilt;
                        }
                    }

                    if (rebuilt.Count == 0)
                    {
                        FrostyLogger.Logger?.LogInfo($"Mesh viewport resolved no material bindings for \"{load.Entry.Name}\".");
                    }

                    return rebuilt;
                }
                finally
                {
                    lock (c_bindingCacheLock)
                    {
                        c_bindingBuildTasks.Remove(cacheKey);
                    }
                }
            });

            c_bindingBuildTasks[cacheKey] = buildTask;
            return buildTask;
        }
    }

    private static MeshBindingCacheKey CreateBindingCacheKey(Guid entryGuid, MeshVariationRecord? selectedVariation)
    {
        return new MeshBindingCacheKey(
            entryGuid,
            selectedVariation?.VariationAssetNameHash ?? 0,
            selectedVariation?.Name ?? string.Empty);
    }

    private static MeshViewportMaterialBinding? ResolveBinding(
        IReadOnlyDictionary<int, MeshViewportMaterialBinding> bindings,
        int materialId,
        int sectionIndex)
    {
        if (bindings.TryGetValue(materialId, out MeshViewportMaterialBinding? materialBinding))
        {
            return materialBinding;
        }

        return null;
    }

    private static byte[]? ResolvePreviewDiffuseTexturePng(
        MeshAssetLoadResult load,
        CachedSectionData section,
        MeshViewportMaterialBinding? binding,
        Dictionary<string, byte[]> cache,
        PreviewTextureQuality textureQuality,
        TextureChannelMask channels)
    {
        string? colorTexturePath = binding?.ColorTextureEntry?.Path;
        if (ShouldUseEyeFallbackTexture(load.Entry.Path, section.SectionName, colorTexturePath))
        {
            return TryLoadBundledTexturePng(c_eyeFallbackTexturePath, cache, forceOpaqueAlpha: true, textureQuality, channels);
        }

        if (binding?.ColorTextureEntry is not null)
        {
            bool neutralizeEyelash = IsEyelashSection(load.Entry.Path, section.SectionName, binding.ColorTextureEntry.Path);
            return TryLoadTexturePng(binding.ColorTextureEntry, cache, forceOpaqueAlpha: true, textureQuality, channels, neutralizeEyelash);
        }

        if (!IsPlayerHeadEyeSection(load.Entry.Path, section.SectionName))
        {
            return null;
        }

        return TryLoadBundledTexturePng(c_eyeFallbackTexturePath, cache, forceOpaqueAlpha: true, textureQuality, channels);
    }

    private static bool TryCreateDiffuseTexturePreloadRequest(
        MeshAssetLoadResult load,
        CachedSectionData section,
        MeshViewportMaterialBinding? binding,
        PreviewTextureQuality textureQuality,
        TextureChannelMask channels,
        out TexturePreloadRequest? request)
    {
        string? colorTexturePath = binding?.ColorTextureEntry?.Path;
        if (ShouldUseEyeFallbackTexture(load.Entry.Path, section.SectionName, colorTexturePath))
        {
            string eyeFallbackCacheKey = string.Concat(
                c_eyeFallbackTexturePath,
                "|",
                "opaque",
                "|",
                textureQuality == PreviewTextureQuality.LowRes ? "low" : "high",
                "|",
                channels);
            request = new TexturePreloadRequest(
                eyeFallbackCacheKey,
                () => TryLoadBundledTexturePng(
                    c_eyeFallbackTexturePath,
                    new Dictionary<string, byte[]>(),
                    forceOpaqueAlpha: true,
                    textureQuality,
                    channels));
            return true;
        }

        if (binding?.ColorTextureEntry is not null)
        {
            bool neutralizeEyelash = IsEyelashSection(load.Entry.Path, section.SectionName, binding.ColorTextureEntry.Path);
            string cacheKey = BuildTextureCacheKey(binding.ColorTextureEntry.Guid, forceOpaqueAlpha: true, textureQuality, channels, neutralizeEyelash);
            request = new TexturePreloadRequest(
                cacheKey,
                () => TryLoadTexturePng(
                    binding.ColorTextureEntry,
                    new Dictionary<string, byte[]>(),
                    forceOpaqueAlpha: true,
                    textureQuality,
                    channels,
                    neutralizeEyelash));
            return true;
        }

        if (!IsPlayerHeadEyeSection(load.Entry.Path, section.SectionName))
        {
            request = null;
            return false;
        }

        string bundledCacheKey = string.Concat(
            c_eyeFallbackTexturePath,
            "|",
            "opaque",
            "|",
            textureQuality == PreviewTextureQuality.LowRes ? "low" : "high",
            "|",
            channels);
        request = new TexturePreloadRequest(
            bundledCacheKey,
            () => TryLoadBundledTexturePng(
                c_eyeFallbackTexturePath,
                new Dictionary<string, byte[]>(),
                forceOpaqueAlpha: true,
                textureQuality,
                channels));
        return true;
    }

    private static bool TryCreateNormalTexturePreloadRequest(
        MeshViewportMaterialBinding? binding,
        PreviewTextureQuality textureQuality,
        out TexturePreloadRequest? request)
    {
        if (binding?.NormalTextureEntry is null)
        {
            request = null;
            return false;
        }

        string cacheKey = BuildTextureCacheKey(binding.NormalTextureEntry.Guid, forceOpaqueAlpha: false, textureQuality, TextureChannelMask.Rgba, neutralizeEyelash: false);
        request = new TexturePreloadRequest(
            cacheKey,
            () => TryLoadTexturePng(
                binding.NormalTextureEntry,
                new Dictionary<string, byte[]>(),
                forceOpaqueAlpha: false,
                textureQuality,
                TextureChannelMask.Rgba,
                neutralizeEyelash: false));
        return true;
    }

    private static bool IsPlayerHeadEyeSection(string meshPath, string sectionName)
    {
        if (string.IsNullOrWhiteSpace(meshPath) || string.IsNullOrWhiteSpace(sectionName))
        {
            return false;
        }

        string normalizedPath = meshPath.Replace('\\', '/');
        if (!normalizedPath.Contains("head", StringComparison.OrdinalIgnoreCase) &&
            !normalizedPath.Contains("player/players/", StringComparison.OrdinalIgnoreCase) &&
            !normalizedPath.Contains("playerhead", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string normalizedSectionName = sectionName.Replace("_", string.Empty, StringComparison.OrdinalIgnoreCase);
        if (normalizedSectionName.Contains("eyelash", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return normalizedSectionName.Contains("eyes", StringComparison.OrdinalIgnoreCase) ||
               normalizedSectionName.Contains("eye", StringComparison.OrdinalIgnoreCase) ||
               normalizedSectionName.Contains("iris", StringComparison.OrdinalIgnoreCase) ||
               normalizedSectionName.Contains("cornea", StringComparison.OrdinalIgnoreCase) ||
               normalizedSectionName.Contains("sclera", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldUseEyeFallbackTexture(string meshPath, string sectionName, string? texturePath)
    {
        if (!IsPlayerHeadEyeSection(meshPath, sectionName))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(texturePath))
        {
            return true;
        }

        string normalizedTexturePath = texturePath.Replace('\\', '/');
        if (normalizedTexturePath.Contains("eyelash", StringComparison.OrdinalIgnoreCase) ||
            normalizedTexturePath.Contains("lash", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (normalizedTexturePath.Contains("eye", StringComparison.OrdinalIgnoreCase) ||
            normalizedTexturePath.Contains("iris", StringComparison.OrdinalIgnoreCase) ||
            normalizedTexturePath.Contains("cornea", StringComparison.OrdinalIgnoreCase) ||
            normalizedTexturePath.Contains("sclera", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return normalizedTexturePath.Contains("normal", StringComparison.OrdinalIgnoreCase) ||
               normalizedTexturePath.Contains("nsm", StringComparison.OrdinalIgnoreCase) ||
               normalizedTexturePath.Contains("mask", StringComparison.OrdinalIgnoreCase) ||
               normalizedTexturePath.Contains("rsm", StringComparison.OrdinalIgnoreCase) ||
               normalizedTexturePath.Contains("skintones", StringComparison.OrdinalIgnoreCase) ||
               normalizedTexturePath.Contains("playerhead", StringComparison.OrdinalIgnoreCase) ||
               normalizedTexturePath.Contains("head", StringComparison.OrdinalIgnoreCase) ||
               normalizedTexturePath.Contains("face", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEyelashSection(string meshPath, string sectionName, string? texturePath)
    {
        string normalizedSectionName = sectionName.Replace("_", string.Empty, StringComparison.OrdinalIgnoreCase);
        if (normalizedSectionName.Contains("eyelash", StringComparison.OrdinalIgnoreCase) ||
            normalizedSectionName.Contains("lash", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(texturePath) &&
            (texturePath.Contains("eyelash", StringComparison.OrdinalIgnoreCase) ||
             texturePath.Contains("lash", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return meshPath.Contains("player/players/", StringComparison.OrdinalIgnoreCase) &&
               normalizedSectionName.Contains("lash", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldApplyPreviewNormalMap(string meshPath, MeshViewportMaterialBinding? binding)
    {
        if (binding?.NormalTextureEntry is null)
        {
            return false;
        }

        string normalizedMeshPath = meshPath.Replace('\\', '/');
        string normalPath = binding.NormalTextureEntry.Path;
        string diffusePath = binding.ColorTextureEntry?.Path ?? string.Empty;
        bool isPlayerSkinMesh = normalizedMeshPath.Contains("/characters/player/bodies/", StringComparison.OrdinalIgnoreCase) ||
                                normalizedMeshPath.Contains("/characters/player/players/", StringComparison.OrdinalIgnoreCase);
        bool isSkinTextureSet = normalPath.Contains("/skintones/", StringComparison.OrdinalIgnoreCase) ||
                                diffusePath.Contains("/skintones/", StringComparison.OrdinalIgnoreCase);

        return !(isPlayerSkinMesh && isSkinTextureSet);
    }

    private static MeshViewportViewPreset ToHostPreset(MeshPreviewView previewView)
    {
        return previewView switch
        {
            MeshPreviewView.Front => MeshViewportViewPreset.Front,
            MeshPreviewView.Back => MeshViewportViewPreset.Back,
            MeshPreviewView.Left => MeshViewportViewPreset.Left,
            MeshPreviewView.Right => MeshViewportViewPreset.Right,
            MeshPreviewView.Top => MeshViewportViewPreset.Top,
            MeshPreviewView.Bottom => MeshViewportViewPreset.Bottom,
            _ => MeshViewportViewPreset.Perspective
        };
    }

    private static byte[]? TryLoadTexturePng(
        Frosty.Sdk.Managers.Entries.EbxAssetEntry? textureEntry,
        Dictionary<string, byte[]> cache,
        bool forceOpaqueAlpha,
        PreviewTextureQuality textureQuality,
        TextureChannelMask channels,
        bool neutralizeEyelash)
    {
        if (textureEntry is null)
        {
            return null;
        }

        string cacheKey = BuildTextureCacheKey(textureEntry.Guid, forceOpaqueAlpha, textureQuality, channels, neutralizeEyelash);
        if (cache.TryGetValue(cacheKey, out byte[]? cachedBytes))
        {
            return cachedBytes;
        }

        lock (c_textureCacheLock)
        {
            if (c_texturePngCache.TryGetValue(cacheKey, out cachedBytes))
            {
                if (cachedBytes is not null)
                {
                    cache[cacheKey] = cachedBytes;
                }

                return cachedBytes;
            }
        }

        try
        {
            TextureAssetLoadResult load = TextureAssetOperations.Load(textureEntry);
            bool lowRes = textureQuality == PreviewTextureQuality.LowRes;
            int mipLevel = TextureAssetOperations.GetPreviewMip(load.Texture, lowRes);
            byte[] pngBytes = TextureAssetOperations.CreatePreviewPng(
                load.Texture,
                mipLevel,
                0,
                channels,
                false,
                forceOpaqueAlpha,
                neutralizeEyelash);
            cache[cacheKey] = pngBytes;
            lock (c_textureCacheLock)
            {
                c_texturePngCache[cacheKey] = pngBytes;
            }

            return pngBytes;
        }
        catch (Exception ex)
        {
            FrostyLogger.Logger?.LogWarning($"Mesh viewport failed to decode preview texture \"{textureEntry.Path}\": {ex.Message}");
            lock (c_textureCacheLock)
            {
                c_texturePngCache[cacheKey] = null;
            }

            return null;
        }
    }

    private static byte[]? TryLoadBundledTexturePng(
        string texturePath,
        Dictionary<string, byte[]> cache,
        bool forceOpaqueAlpha,
        PreviewTextureQuality textureQuality,
        TextureChannelMask channels)
    {
        if (string.IsNullOrWhiteSpace(texturePath) || !File.Exists(texturePath))
        {
            return null;
        }

        string cacheKey = string.Concat(
            texturePath,
            "|",
            forceOpaqueAlpha ? "opaque" : "raw",
            "|",
            textureQuality == PreviewTextureQuality.LowRes ? "low" : "high",
            "|",
            channels);
        if (cache.TryGetValue(cacheKey, out byte[]? cachedBytes))
        {
            return cachedBytes;
        }

        lock (c_textureCacheLock)
        {
            if (c_texturePngCache.TryGetValue(cacheKey, out cachedBytes))
            {
                if (cachedBytes is not null)
                {
                    cache[cacheKey] = cachedBytes;
                }

                return cachedBytes;
            }
        }

        try
        {
            using Image<Rgba32> image = Image.Load<Rgba32>(texturePath);
            ApplyChannels(image, channels);
            if (forceOpaqueAlpha)
            {
                image.ProcessPixelRows(accessor =>
                {
                    for (int y = 0; y < accessor.Height; y++)
                    {
                        Span<Rgba32> row = accessor.GetRowSpan(y);
                        for (int x = 0; x < row.Length; x++)
                        {
                            row[x].A = byte.MaxValue;
                        }
                    }
                });
            }

            using MemoryStream stream = new();
            image.Save(stream, new PngEncoder());
            byte[] pngBytes = stream.ToArray();
            cache[cacheKey] = pngBytes;
            lock (c_textureCacheLock)
            {
                c_texturePngCache[cacheKey] = pngBytes;
            }

            return pngBytes;
        }
        catch (Exception ex)
        {
            FrostyLogger.Logger?.LogWarning($"Mesh viewport failed to load bundled preview texture \"{texturePath}\": {ex.Message}");
            lock (c_textureCacheLock)
            {
                c_texturePngCache[cacheKey] = null;
            }

            return null;
        }
    }

    private static string BuildTextureCacheKey(Guid textureGuid, bool forceOpaqueAlpha, PreviewTextureQuality textureQuality)
    {
        return string.Concat(
            textureGuid.ToString("N"),
            forceOpaqueAlpha ? "|opaque" : "|raw",
            textureQuality == PreviewTextureQuality.LowRes ? "|low" : "|high");
    }

    private static string BuildTextureCacheKey(Guid textureGuid, bool forceOpaqueAlpha, PreviewTextureQuality textureQuality, TextureChannelMask channels, bool neutralizeEyelash)
    {
        return string.Concat(
            textureGuid.ToString("N"),
            forceOpaqueAlpha ? "|opaque" : "|raw",
            textureQuality == PreviewTextureQuality.LowRes ? "|low" : "|high",
            "|",
            channels,
            neutralizeEyelash ? "|eyelash" : string.Empty);
    }

    private static TextureChannelMask ToTextureChannelMask(MeshViewportChannelMask channels)
    {
        TextureChannelMask mask = TextureChannelMask.None;
        if (channels.HasFlag(MeshViewportChannelMask.Red))
        {
            mask |= TextureChannelMask.Red;
        }

        if (channels.HasFlag(MeshViewportChannelMask.Green))
        {
            mask |= TextureChannelMask.Green;
        }

        if (channels.HasFlag(MeshViewportChannelMask.Blue))
        {
            mask |= TextureChannelMask.Blue;
        }

        if (channels.HasFlag(MeshViewportChannelMask.Alpha))
        {
            mask |= TextureChannelMask.Alpha;
        }

        return mask == TextureChannelMask.None ? TextureChannelMask.Rgba : mask;
    }

    private static void ApplyChannels(Image<Rgba32> image, TextureChannelMask channels)
    {
        bool red = channels.HasFlag(TextureChannelMask.Red);
        bool green = channels.HasFlag(TextureChannelMask.Green);
        bool blue = channels.HasFlag(TextureChannelMask.Blue);
        bool alpha = channels.HasFlag(TextureChannelMask.Alpha);
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                Span<Rgba32> row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    Rgba32 pixel = row[x];
                    row[x] = new Rgba32(
                        red ? pixel.R : (byte)0,
                        green ? pixel.G : (byte)0,
                        blue ? pixel.B : (byte)0,
                        alpha ? pixel.A : byte.MaxValue);
                }
            }
        });
    }

    private static void ApplyNeutralEyelashTint(Image<Rgba32> image)
    {
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                Span<Rgba32> row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    Rgba32 pixel = row[x];
                    if (pixel.A == 0)
                    {
                        continue;
                    }

                    byte luminance = (byte)Math.Clamp((pixel.R * 0.299f) + (pixel.G * 0.587f) + (pixel.B * 0.114f), 0f, 255f);
                    byte neutral = (byte)Math.Clamp(luminance * 0.3f, 12f, 72f);
                    row[x] = new Rgba32(neutral, neutral, neutral, pixel.A);
                }
            }
        });
    }
}
