using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Frosty.Sdk.Managers;
using Frosty.Sdk.Managers.Entries;
using Frosty.Sdk.Resources;
using FrostyEditor.Managers;

namespace FrostyEditor.ViewModels;

public sealed partial class MeshAssetEditorViewModel : AssetEditorViewModel, ISessionStateAwareDocument
{
    public sealed class MeshLodItemViewModel
    {
        public int Index { get; init; }
        public string Name { get; init; } = string.Empty;
        public string ShortName { get; init; } = string.Empty;
        public string Type { get; init; } = string.Empty;
        public string DataSource { get; init; } = string.Empty;
        public string ChunkId { get; init; } = string.Empty;
        public string VertexBufferSize { get; init; } = string.Empty;
        public string IndexBufferSize { get; init; } = string.Empty;
        public ObservableCollection<MeshSectionItemViewModel> Sections { get; } = [];

        public override string ToString() => Name;
    }

    public sealed partial class MeshSectionItemViewModel : ObservableObject
    {
        public event Action? StateChanged;

        public int Index { get; init; }
        public string Name { get; init; } = string.Empty;
        public string PrimitiveType { get; init; } = string.Empty;
        public string MaterialId { get; init; } = string.Empty;
        public string PrimitiveCount { get; init; } = string.Empty;
        public string VertexCount { get; init; } = string.Empty;
        public string VertexStride { get; init; } = string.Empty;
        public string BoneCount { get; init; } = string.Empty;
        public string Bounds { get; init; } = string.Empty;
        public string DecodeDiagnostics { get; init; } = string.Empty;

        [ObservableProperty]
        private bool m_isVisible = true;

        [ObservableProperty]
        private bool m_isHighlighted;

        partial void OnIsVisibleChanged(bool value)
        {
            StateChanged?.Invoke();
        }

        partial void OnIsHighlightedChanged(bool value)
        {
            StateChanged?.Invoke();
        }
    }

    public sealed class MeshOutlineNodeViewModel
    {
        public required string Label { get; init; }
        public required string Summary { get; init; }
        public MeshLodItemViewModel? Lod { get; init; }
        public MeshSectionItemViewModel? Section { get; init; }
        public ObservableCollection<MeshOutlineNodeViewModel> Children { get; } = [];
        public bool HasSection => Section is not null;
        public bool HasLod => Lod is not null;
    }

    private readonly EbxAssetEntry m_meshEntry;
    private readonly Dictionary<int, IReadOnlyList<MeshObjCodec.MeshDecodedSection>> m_decodedSectionsByLod = [];
    private MeshAssetLoadResult m_load;
    private MeshPreviewCameraState m_previewCamera = MeshPreviewCameraState.Default;
    private int m_previewRequestId;
    private string? m_viewportFailureMessage;
    private int m_viewportSceneRevision;
    private int m_viewportSettingsRevision;
    private int m_viewportCameraRevision;
    private bool m_syncingChannelState;
    private bool m_syncingOutlineSelection;
    private bool m_syncingSelectionState;

    [ObservableProperty]
    private Bitmap? m_previewBitmap;

    [ObservableProperty]
    private MeshLodItemViewModel? m_selectedLod;

    [ObservableProperty]
    private MeshSectionItemViewModel? m_selectedSection;

    [ObservableProperty]
    private MeshOutlineNodeViewModel? m_selectedOutlineNode;

    [ObservableProperty]
    private MeshPreviewView m_previewView = MeshPreviewView.Front;

    [ObservableProperty]
    private MeshViewportRenderMode m_selectedRenderMode = MeshViewportRenderMode.Lit;

    [ObservableProperty]
    private bool m_redChannelEnabled = true;

    [ObservableProperty]
    private bool m_greenChannelEnabled = true;

    [ObservableProperty]
    private bool m_blueChannelEnabled = true;

    [ObservableProperty]
    private bool m_alphaChannelEnabled = true;

    [ObservableProperty]
    private bool m_isModified;

    [ObservableProperty]
    private string m_parseStatus = string.Empty;

    [ObservableProperty]
    private string m_previewStatus = "Preparing viewport...";

    [ObservableProperty]
    private string m_decodeStatus = "Collecting mesh decode diagnostics...";

    public ObservableCollection<MeshLodItemViewModel> Lods { get; } = [];
    public ObservableCollection<MeshSectionItemViewModel> VisibleSections { get; } = [];
    public ObservableCollection<MeshOutlineNodeViewModel> OutlineNodes { get; } = [];
    public IReadOnlyList<MeshViewportRenderMode> AvailableRenderModes { get; } = Enum.GetValues<MeshViewportRenderMode>();

    public MeshAssetEditorViewModel(EbxAssetEntry entry)
        : base(entry)
    {
        m_meshEntry = entry;
        MeshViewportSceneBuilder.StartBackgroundWarmup();
        m_meshPartition = AssetManager.GetEbxPartition(entry);
        m_load = MeshAssetOperations.Load(entry);
        SyncState();
    }

    public MeshSet Mesh => m_load.Mesh;
    public string MeshName => string.IsNullOrWhiteSpace(Mesh.Name) ? m_entry.Filename : Mesh.Name;
    public string MeshFullName => string.IsNullOrWhiteSpace(Mesh.FullName) ? m_entry.Name : Mesh.FullName;
    public string MeshType => Mesh.Type.ToString();
    public string MeshFlags => Mesh.Flags.ToString();
    public string MeshBounds => Mesh.BoundingBox.ToString();
    public string ResourceName => m_load.ResourceEntry.Name;
    public string ResourceRid => $"0x{m_load.ResourceEntry.ResRid:X16}";
    public string ResourceType => m_load.ResourceEntry.Type;
    public string LodCount => Mesh.Lods.Count.ToString(CultureInfo.InvariantCulture);
    public string SectionCount => Mesh.TotalSectionCount.ToString(CultureInfo.InvariantCulture);
    public string ExternalChunkCount => Mesh.ExternalChunkCount.ToString(CultureInfo.InvariantCulture);
    public string CountsSummary => string.Format(
        CultureInfo.InvariantCulture,
        "LODs: {0}, Sections: {1}, External Chunks: {2}",
        Mesh.Lods.Count,
        Mesh.TotalSectionCount,
        Mesh.ExternalChunkCount);
    public string DataStatus => Mesh.ParsedSuccessfully ? "Parsed" : "Partial";
    public bool HasParseWarning => !Mesh.ParsedSuccessfully;
    public bool CanRevert => IsModified;
    public bool ShowGpuViewport =>
        SelectedRenderMode is MeshViewportRenderMode.Lit or MeshViewportRenderMode.Base or MeshViewportRenderMode.Wireframe;
    public bool ShowSoftwarePreview => !ShowGpuViewport;
    public bool HasPreviewBitmap => PreviewBitmap is not null;
    public int ViewportSceneRevision => m_viewportSceneRevision;
    public int ViewportSettingsRevision => m_viewportSettingsRevision;
    public int ViewportCameraRevision => m_viewportCameraRevision;
    public MeshPreviewCameraState PreviewCameraState => m_previewCamera;
    public string PreviewViewLabel => PreviewView.ToString();
    public string RenderModeLabel => SelectedRenderMode.ToString();
    public string ChannelMaskLabel => BuildChannelMaskLabel();
    public bool HasSelectedLod => SelectedLod is not null;
    public bool HasSelectedSection => SelectedSection is not null;
    public string SelectedLodSummary => SelectedLod is null
        ? "Select a LOD."
        : $"{SelectedLod.Name} · {SelectedLod.Type} · {SelectedLod.DataSource} · {SelectedLod.VertexBufferSize} / {SelectedLod.IndexBufferSize}";
    public string SelectedSectionSummary => SelectedSection is null
        ? "Select a section."
        : $"{SelectedSection.Name} · {SelectedSection.PrimitiveType} · {SelectedSection.PrimitiveCount} · {SelectedSection.VertexCount}";
    public string PreviewInteractionSummary =>
        PreviewView == MeshPreviewView.Perspective
            ? "Perspective: left drag pans, right drag orbits, middle drag or the mouse wheel zooms, and double-click resets the view."
            : "Orthographic views stay fixed: drag pans, middle drag or the mouse wheel zooms, and double-click resets the view.";
    public string EditorModeSummary =>
        "The mesh tab now uses a single interactive 3D viewport with decoded geometry, direct mouse navigation, and render/debug channels.";
    internal MeshAssetLoadResult LoadResult => m_load;
    internal MeshViewportChannelMask ActiveChannelMask => BuildActiveChannelMask();

    partial void OnSelectedLodChanged(MeshLodItemViewModel? value)
    {
        VisibleSections.Clear();
        if (value is null)
        {
            SelectedSection = null;
            RaiseSelectionMetadata();
            InvalidateViewportSettings();
            return;
        }

        foreach (MeshSectionItemViewModel section in value.Sections)
        {
            VisibleSections.Add(section);
        }

        SelectedSection = value.Sections.FirstOrDefault(item => item.Index == SelectedSection?.Index);
        if (m_syncingSelectionState)
        {
            return;
        }

        RaiseSelectionMetadata();
        RebuildMeshInspector();
        RebuildSceneInspector();
        InvalidateViewportScene();
    }

    partial void OnSelectedSectionChanged(MeshSectionItemViewModel? value)
    {
        if (m_syncingSelectionState)
        {
            return;
        }

        RaiseSelectionMetadata();
        RebuildMeshInspector();
        RebuildSceneInspector();
        UpdatePreviewStatus();
    }

    partial void OnSelectedOutlineNodeChanged(MeshOutlineNodeViewModel? value)
    {
        if (m_syncingOutlineSelection || value is null)
        {
            return;
        }

        if (value.Section is not null)
        {
            if (!ReferenceEquals(SelectedLod, value.Lod))
            {
                SelectedLod = value.Lod;
            }

            if (!ReferenceEquals(SelectedSection, value.Section))
            {
                SelectedSection = value.Section;
            }

            return;
        }

        if (value.Lod is not null && !ReferenceEquals(SelectedLod, value.Lod))
        {
            SelectedLod = value.Lod;
        }

        if (SelectedSection is not null)
        {
            SelectedSection = null;
        }
    }

    partial void OnPreviewViewChanged(MeshPreviewView value)
    {
        m_previewCamera = MeshPreviewCameraState.Default;
        OnPropertyChanged(nameof(PreviewViewLabel));
        OnPropertyChanged(nameof(PreviewInteractionSummary));
        RebuildSceneInspector();
        InvalidateViewportCamera();
    }

    partial void OnSelectedRenderModeChanged(MeshViewportRenderMode value)
    {
        OnPropertyChanged(nameof(ShowGpuViewport));
        OnPropertyChanged(nameof(ShowSoftwarePreview));
        OnPropertyChanged(nameof(RenderModeLabel));
        RebuildSceneInspector();
        if (ShowGpuViewport)
        {
            ClearSoftwarePreview();
            InvalidateViewportScene();
            return;
        }

        QueueRefreshPreview();
    }

    partial void OnRedChannelEnabledChanged(bool value) => HandleChannelToggleChanged();
    partial void OnGreenChannelEnabledChanged(bool value) => HandleChannelToggleChanged();
    partial void OnBlueChannelEnabledChanged(bool value) => HandleChannelToggleChanged();
    partial void OnAlphaChannelEnabledChanged(bool value) => HandleChannelToggleChanged();

    [RelayCommand]
    private async Task ExportMesh()
    {
        MeshOperationResult result = await MeshAssetOperations.ExportWithPickerAsync(m_meshEntry);
        LogOperation(result);
    }

    [RelayCommand]
    private async Task ImportMesh()
    {
        MeshOperationResult result = await MeshAssetOperations.ImportWithPickerAsync(m_meshEntry);
        LogOperation(result);
        if (result.Success)
        {
            App.MainViewModel?.DataExplorer.RefreshExplorerState(rebuildTree: false);
            ReloadFromSource();
        }
    }

    [RelayCommand]
    private Task RevertMesh()
    {
        MeshOperationResult result = MeshAssetOperations.Revert(m_meshEntry);
        LogOperation(result);
        if (result.Success)
        {
            App.MainViewModel?.DataExplorer.RefreshExplorerState(rebuildTree: false);
            ReloadFromSource();
        }

        return Task.CompletedTask;
    }

    [RelayCommand] private void SetPerspectiveView() => SetPreviewView(MeshPreviewView.Perspective);
    [RelayCommand] private void SetFrontView() => SetPreviewView(MeshPreviewView.Front);
    [RelayCommand] private void SetBackView() => SetPreviewView(MeshPreviewView.Back);
    [RelayCommand] private void SetLeftView() => SetPreviewView(MeshPreviewView.Left);
    [RelayCommand] private void SetRightView() => SetPreviewView(MeshPreviewView.Right);
    [RelayCommand] private void SetTopView() => SetPreviewView(MeshPreviewView.Top);
    [RelayCommand] private void SetBottomView() => SetPreviewView(MeshPreviewView.Bottom);

    [RelayCommand]
    private void ResetPreviewView()
    {
        if (PreviewView != MeshPreviewView.Front)
        {
            PreviewView = MeshPreviewView.Front;
            return;
        }

        m_previewCamera = MeshPreviewCameraState.Default;
        UpdatePreviewStatus();
        InvalidateViewportCamera();
    }

    [RelayCommand]
    private void RefreshPreview()
    {
        UpdatePreviewStatus();
        InvalidateViewportScene();
    }

    [RelayCommand]
    private void ShowAllSections()
    {
        foreach (MeshSectionItemViewModel section in VisibleSections)
        {
            section.IsVisible = true;
        }

        InvalidateViewportScene();
    }

    [RelayCommand]
    private void HideOtherSections()
    {
        if (SelectedSection is null)
        {
            return;
        }

        foreach (MeshSectionItemViewModel section in VisibleSections)
        {
            section.IsVisible = ReferenceEquals(section, SelectedSection);
        }

        InvalidateViewportScene();
    }

    [RelayCommand]
    private void ClearSectionHighlights()
    {
        foreach (MeshSectionItemViewModel section in VisibleSections)
        {
            section.IsHighlighted = false;
        }

        InvalidateViewportScene();
    }

    public override void ReloadFromSource()
    {
        MeshViewportSceneBuilder.Invalidate(m_meshEntry.Guid);
        m_meshPartition = AssetManager.GetEbxPartition(m_meshEntry);
        m_load = MeshAssetOperations.Load(m_meshEntry);
        m_decodedSectionsByLod.Clear();
        SyncState();
    }

    public void RefreshSessionState()
    {
        IsModified = MeshAssetOperations.IsModified(m_meshEntry);
        OnPropertyChanged(nameof(CanRevert));
    }

    public void OrbitPreview(double inDeltaX, double inDeltaY)
    {
        if (PreviewView != MeshPreviewView.Perspective)
        {
            PanPreview(inDeltaX, inDeltaY);
            return;
        }

        m_previewCamera = m_previewCamera with
        {
            OrbitYaw = m_previewCamera.OrbitYaw + ((float)inDeltaX * 0.0125f),
            OrbitPitch = Math.Clamp(m_previewCamera.OrbitPitch + ((float)inDeltaY * 0.01f), -1.35f, 1.35f)
        };
        UpdatePreviewStatus();
        InvalidateViewportCamera();
    }

    public void PanPreview(double inDeltaX, double inDeltaY)
    {
        m_previewCamera = m_previewCamera with
        {
            PanX = m_previewCamera.PanX + (float)inDeltaX,
            PanY = m_previewCamera.PanY + (float)inDeltaY
        };
        UpdatePreviewStatus();
        InvalidateViewportCamera();
    }

    public void ZoomPreview(float inWheelDelta)
    {
        if (Math.Abs(inWheelDelta) < float.Epsilon)
        {
            return;
        }

        m_previewCamera = m_previewCamera with
        {
            ZoomFactor = Math.Clamp(m_previewCamera.ZoomFactor * MathF.Pow(1.12f, inWheelDelta), 0.2f, 8.0f)
        };
        UpdatePreviewStatus();
        InvalidateViewportCamera();
    }

    public void ResetPreviewInteraction()
    {
        m_previewCamera = MeshPreviewCameraState.Default;
        UpdatePreviewStatus();
        InvalidateViewportCamera();
    }

    public bool TryHitTestPreviewSection(float inPreviewX, float inPreviewY, out int outSectionIndex)
    {
        outSectionIndex = -1;
        int? sectionIndex = MeshPreviewRenderer.HitTestSection(
            m_load,
            SelectedLod?.Index,
            BuildSectionStates(),
            PreviewView,
            m_previewCamera,
            inPreviewX,
            inPreviewY);

        if (!sectionIndex.HasValue)
        {
            return false;
        }

        outSectionIndex = sectionIndex.Value;
        return true;
    }

    public bool SelectSectionByIndex(int inSectionIndex)
    {
        if (VisibleSections.Count == 0)
        {
            return false;
        }

        MeshSectionItemViewModel? section = VisibleSections.FirstOrDefault(item => item.Index == inSectionIndex);
        if (section is null)
        {
            return false;
        }

        SelectedSection = section;
        PreviewStatus = $"Selected section {section.Index}: {section.Name}";
        return true;
    }

    public void SetViewportFailureMessage(string? inFailureMessage)
    {
        string? normalizedMessage = string.IsNullOrWhiteSpace(inFailureMessage) ? null : inFailureMessage;
        if (string.Equals(m_viewportFailureMessage, normalizedMessage, StringComparison.Ordinal))
        {
            return;
        }

        m_viewportFailureMessage = normalizedMessage;
        if (!string.IsNullOrWhiteSpace(normalizedMessage))
        {
            PreviewStatus = normalizedMessage;
        }
    }

    internal MeshPreviewRenderResult CreateViewportRenderResult()
    {
        return MeshPreviewRenderer.CreatePreviewBitmap(
            m_load,
            SelectedLod?.Index,
            BuildSectionStates(),
            SelectedSection?.Index,
            PreviewView,
            m_previewCamera,
            SelectedRenderMode,
            ActiveChannelMask);
    }

    internal void SetViewportRenderedStatus(string inStatus)
    {
        if (!string.IsNullOrWhiteSpace(inStatus))
        {
            PreviewStatus = inStatus;
        }
    }

    internal IReadOnlyList<MeshObjCodec.MeshDecodedSection> GetDecodedSections(int lodIndex)
    {
        if (!m_decodedSectionsByLod.TryGetValue(lodIndex, out IReadOnlyList<MeshObjCodec.MeshDecodedSection>? sections))
        {
            sections = MeshObjCodec.DecodeSections(m_load, lodIndex);
            m_decodedSectionsByLod.Add(lodIndex, sections);
        }

        return sections;
    }

    private void SyncState()
    {
        int? preferredLodIndex = SelectedLod?.Index;
        int? preferredSectionIndex = SelectedSection?.Index;

        ParseStatus = Mesh.ParsedSuccessfully
            ? "Mesh resource parsed successfully."
            : $"Mesh metadata is only partially available: {Mesh.ParseError}";
        IsModified = MeshAssetOperations.IsModified(m_meshEntry);
        m_decodedSectionsByLod.Clear();

        Lods.Clear();
        OutlineNodes.Clear();
        foreach (MeshSet.MeshLod lod in Mesh.Lods)
        {
            MeshLodItemViewModel lodViewModel = new()
            {
                Index = lod.Index,
                Name = $"LOD {lod.Index}",
                ShortName = lod.ShortName,
                Type = lod.Type.ToString(),
                DataSource = lod.UsesExternalChunk ? "External Chunk" : "Inline Resource",
                ChunkId = lod.UsesExternalChunk ? lod.ChunkId.ToString() : "Inline",
                VertexBufferSize = $"{lod.VertexBufferSize:N0} bytes",
                IndexBufferSize = $"{lod.IndexBufferSize:N0} bytes"
            };

            foreach (MeshSet.MeshSection section in lod.Sections)
            {
                MeshSectionItemViewModel sectionViewModel = new()
                {
                    Index = section.Index,
                    Name = string.IsNullOrWhiteSpace(section.Name) ? $"Section {section.Index}" : section.Name,
                    PrimitiveType = section.PrimitiveType.ToString(),
                    MaterialId = section.MaterialId.ToString(CultureInfo.InvariantCulture),
                    PrimitiveCount = section.PrimitiveCount.ToString("N0", CultureInfo.InvariantCulture),
                    VertexCount = section.VertexCount.ToString("N0", CultureInfo.InvariantCulture),
                    VertexStride = section.VertexStride.ToString(CultureInfo.InvariantCulture),
                    BoneCount = section.BoneCount.ToString(CultureInfo.InvariantCulture),
                    Bounds = section.BoundingBox?.ToString() ?? "Unavailable",
                    DecodeDiagnostics = "Decode diagnostics load on demand."
                };
                sectionViewModel.StateChanged += InvalidateViewportScene;
                lodViewModel.Sections.Add(sectionViewModel);
            }

            Lods.Add(lodViewModel);
        }

        RebuildOutline();

        m_syncingSelectionState = true;
        try
        {
            SelectedLod = Lods.FirstOrDefault(item => item.Index == preferredLodIndex) ?? Lods.FirstOrDefault();
            if (SelectedLod is not null)
            {
                SelectedSection = SelectedLod.Sections.FirstOrDefault(item => item.Index == preferredSectionIndex) ??
                                  SelectedLod.Sections.FirstOrDefault();
            }
        }
        finally
        {
            m_syncingSelectionState = false;
        }

        m_previewCamera = MeshPreviewCameraState.Default;
        DecodeStatus = "Decode diagnostics load on demand.";
        m_meshPartition = AssetManager.GetEbxPartition(m_meshEntry);

        UpdatePreviewStatus();
        QueueViewportPrewarm();
        QueueRefreshPreview();

        OnPropertyChanged(nameof(Mesh));
        OnPropertyChanged(nameof(MeshName));
        OnPropertyChanged(nameof(MeshFullName));
        OnPropertyChanged(nameof(MeshType));
        OnPropertyChanged(nameof(MeshFlags));
        OnPropertyChanged(nameof(MeshBounds));
        OnPropertyChanged(nameof(ResourceName));
        OnPropertyChanged(nameof(ResourceRid));
        OnPropertyChanged(nameof(ResourceType));
        OnPropertyChanged(nameof(LodCount));
        OnPropertyChanged(nameof(SectionCount));
        OnPropertyChanged(nameof(ExternalChunkCount));
        OnPropertyChanged(nameof(CountsSummary));
        OnPropertyChanged(nameof(DataStatus));
        OnPropertyChanged(nameof(HasParseWarning));
        OnPropertyChanged(nameof(CanRevert));
        OnPropertyChanged(nameof(ShowGpuViewport));
        OnPropertyChanged(nameof(ShowSoftwarePreview));
        OnPropertyChanged(nameof(HasPreviewBitmap));
        OnPropertyChanged(nameof(PreviewViewLabel));
        OnPropertyChanged(nameof(RenderModeLabel));
        OnPropertyChanged(nameof(ChannelMaskLabel));
        OnPropertyChanged(nameof(PreviewInteractionSummary));
        OnPropertyChanged(nameof(EditorModeSummary));
        RaiseSelectionMetadata();
        RebuildInspectorTabs();

        InvalidateViewportScene();
    }

    private void QueueViewportPrewarm()
    {
        MeshAssetLoadResult load = m_load;
        string meshName = MeshName;
        bool includeTextures = SelectedRenderMode is MeshViewportRenderMode.Lit or MeshViewportRenderMode.Base;
        _ = Task.Run(() =>
        {
            try
            {
                MeshViewportSceneBuilder.Prewarm(load, meshName, includeTextures);
            }
            catch
            {
            }
        });
    }

    private static void LogOperation(MeshOperationResult result)
    {
        if (result.Success)
        {
            Frosty.Sdk.FrostyLogger.Logger?.LogInfo(result.Message);
        }
        else
        {
            Frosty.Sdk.FrostyLogger.Logger?.LogWarning(result.Message);
        }
    }

    private void RaiseSelectionMetadata()
    {
        OnPropertyChanged(nameof(HasSelectedLod));
        OnPropertyChanged(nameof(HasSelectedSection));
        OnPropertyChanged(nameof(SelectedLodSummary));
        OnPropertyChanged(nameof(SelectedSectionSummary));
        SyncOutlineSelection();
    }

    private void RebuildOutline()
    {
        OutlineNodes.Clear();
        foreach (MeshLodItemViewModel lod in Lods)
        {
            MeshOutlineNodeViewModel lodNode = new()
            {
                Label = $"LOD {lod.Index}",
                Summary = $"{lod.Name} · {lod.Type} · {lod.DataSource}",
                Lod = lod
            };

            foreach (MeshSectionItemViewModel section in lod.Sections)
            {
                lodNode.Children.Add(new MeshOutlineNodeViewModel
                {
                    Label = $"Section {section.Index}: {section.Name}",
                    Summary = $"{section.PrimitiveType} · {section.PrimitiveCount} prims · {section.VertexCount} verts",
                    Lod = lod,
                    Section = section
                });
            }

            OutlineNodes.Add(lodNode);
        }
    }

    private void SyncOutlineSelection()
    {
        MeshOutlineNodeViewModel? nextNode = FindOutlineNode(SelectedLod?.Index, SelectedSection?.Index);
        if (ReferenceEquals(SelectedOutlineNode, nextNode))
        {
            return;
        }

        m_syncingOutlineSelection = true;
        SelectedOutlineNode = nextNode;
        m_syncingOutlineSelection = false;
    }

    private MeshOutlineNodeViewModel? FindOutlineNode(int? lodIndex, int? sectionIndex)
    {
        if (!lodIndex.HasValue)
        {
            return null;
        }

        foreach (MeshOutlineNodeViewModel lodNode in OutlineNodes)
        {
            if (lodNode.Lod?.Index != lodIndex.Value)
            {
                continue;
            }

            if (!sectionIndex.HasValue)
            {
                return lodNode;
            }

            MeshOutlineNodeViewModel? sectionNode = lodNode.Children.FirstOrDefault(item => item.Section?.Index == sectionIndex.Value);
            return sectionNode ?? lodNode;
        }

        return null;
    }

    private void HandleChannelToggleChanged()
    {
        if (m_syncingChannelState)
        {
            return;
        }

        if (!RedChannelEnabled && !GreenChannelEnabled && !BlueChannelEnabled && !AlphaChannelEnabled)
        {
            m_syncingChannelState = true;
            RedChannelEnabled = true;
            GreenChannelEnabled = true;
            BlueChannelEnabled = true;
            AlphaChannelEnabled = true;
            m_syncingChannelState = false;
        }

        OnPropertyChanged(nameof(ChannelMaskLabel));
        OnPropertyChanged(nameof(ShowGpuViewport));
        OnPropertyChanged(nameof(ShowSoftwarePreview));
        RebuildSceneInspector();
        if (ShowGpuViewport)
        {
            ClearSoftwarePreview();
            InvalidateViewportScene();
            return;
        }

        QueueRefreshPreview();
    }

    private void InvalidateViewportScene()
    {
        UpdatePreviewStatus();
        Interlocked.Increment(ref m_viewportSceneRevision);
        Interlocked.Increment(ref m_viewportSettingsRevision);
        Interlocked.Increment(ref m_viewportCameraRevision);
        OnPropertyChanged(nameof(ViewportSceneRevision));
        OnPropertyChanged(nameof(ViewportSettingsRevision));
        OnPropertyChanged(nameof(ViewportCameraRevision));
    }

    private void InvalidateViewportSettings()
    {
        UpdatePreviewStatus();
        Interlocked.Increment(ref m_viewportSettingsRevision);
        OnPropertyChanged(nameof(ViewportSettingsRevision));
    }

    private void InvalidateViewportCamera()
    {
        UpdatePreviewStatus();
        Interlocked.Increment(ref m_viewportCameraRevision);
        OnPropertyChanged(nameof(ViewportCameraRevision));
    }

    private void SetPreviewView(MeshPreviewView inPreviewView)
    {
        if (PreviewView == inPreviewView)
        {
            ResetPreviewInteraction();
            return;
        }

        PreviewView = inPreviewView;
    }

    private static (float Pitch, float Yaw) GetPreviewBaseOrbit(MeshPreviewView inPreviewView)
    {
        return inPreviewView switch
        {
            MeshPreviewView.Front => (0.0f, 0.0f),
            MeshPreviewView.Back => (0.0f, MathF.PI),
            MeshPreviewView.Left => (0.0f, -MathF.PI * 0.5f),
            MeshPreviewView.Right => (0.0f, MathF.PI * 0.5f),
            MeshPreviewView.Top => (-MathF.PI * 0.5f, 0.0f),
            MeshPreviewView.Bottom => (MathF.PI * 0.5f, 0.0f),
            _ => (-0.38f, 0.72f)
        };
    }

    private void UpdatePreviewStatus()
    {
        string lodLabel = SelectedLod is null ? "No LOD selected" : $"LOD {SelectedLod.Index}";
        string sectionLabel = SelectedSection is null ? "all sections" : $"section {SelectedSection.Index} ({SelectedSection.Name})";
        int totalSections = SelectedLod?.Sections.Count ?? 0;
        int visibleSections = SelectedLod?.Sections.Count(item => item.IsVisible) ?? 0;
        string zoomLabel = $"{m_previewCamera.ZoomFactor * 100.0f:0}% zoom";
        string panLabel = $"pan {m_previewCamera.PanX:0},{m_previewCamera.PanY:0}";
        string orbitLabel = $", orbit {m_previewCamera.OrbitYaw * 57.29578f:0}/{m_previewCamera.OrbitPitch * 57.29578f:0}";
        const string pipelineLabel = "single viewport";

        PreviewStatus =
            $"{lodLabel}, {sectionLabel}, {visibleSections}/{totalSections} sections visible, {PreviewView} view, " +
            $"{RenderModeLabel}, channels {ChannelMaskLabel}, {zoomLabel}, {panLabel}{orbitLabel}, {pipelineLabel}";
    }

    private void QueueRefreshPreview()
    {
        if (ShowGpuViewport)
        {
            ClearSoftwarePreview();
            return;
        }

        int requestId = Interlocked.Increment(ref m_previewRequestId);
        _ = RefreshPreviewAsync(requestId);
    }

    private void ClearSoftwarePreview()
    {
        if (PreviewBitmap is null)
        {
            return;
        }

        Bitmap? previous = PreviewBitmap;
        PreviewBitmap = null;
        OnPropertyChanged(nameof(HasPreviewBitmap));
        previous?.Dispose();
    }

    private MeshPreviewRenderResult CreatePreviewRenderResult()
    {
        return CreateViewportRenderResult();
    }

    private async Task RefreshPreviewAsync(int inRequestId)
    {
        try
        {
            MeshPreviewRenderResult result = await Task.Run(CreatePreviewRenderResult);
            if (inRequestId != m_previewRequestId || ShowGpuViewport)
            {
                result.Bitmap?.Dispose();
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (inRequestId != m_previewRequestId || ShowGpuViewport)
                {
                    result.Bitmap?.Dispose();
                    return;
                }

                Bitmap? previous = PreviewBitmap;
                PreviewBitmap = result.Bitmap;
                OnPropertyChanged(nameof(HasPreviewBitmap));
                previous?.Dispose();
                PreviewStatus = result.Status;
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Bitmap? previous = PreviewBitmap;
                PreviewBitmap = null;
                OnPropertyChanged(nameof(HasPreviewBitmap));
                previous?.Dispose();
                PreviewStatus = $"Preview rendering failed: {ex.Message}";
            });
        }
    }

    private IReadOnlyDictionary<int, MeshPreviewSectionState> BuildSectionStates()
    {
        Dictionary<int, MeshPreviewSectionState> states = [];
        foreach (MeshSectionItemViewModel section in VisibleSections)
        {
            states[section.Index] = new MeshPreviewSectionState(section.IsVisible, section.IsHighlighted);
        }

        return states;
    }

    private MeshViewportChannelMask BuildActiveChannelMask()
    {
        MeshViewportChannelMask mask = MeshViewportChannelMask.None;
        if (RedChannelEnabled)
        {
            mask |= MeshViewportChannelMask.Red;
        }

        if (GreenChannelEnabled)
        {
            mask |= MeshViewportChannelMask.Green;
        }

        if (BlueChannelEnabled)
        {
            mask |= MeshViewportChannelMask.Blue;
        }

        if (AlphaChannelEnabled)
        {
            mask |= MeshViewportChannelMask.Alpha;
        }

        return mask == MeshViewportChannelMask.None ? MeshViewportChannelMask.Rgba : mask;
    }

    private string BuildChannelMaskLabel()
    {
        string label = string.Empty;
        if (RedChannelEnabled)
        {
            label += "R";
        }

        if (GreenChannelEnabled)
        {
            label += "G";
        }

        if (BlueChannelEnabled)
        {
            label += "B";
        }

        if (AlphaChannelEnabled)
        {
            label += "A";
        }

        return string.IsNullOrEmpty(label) ? "RGBA" : label;
    }
}
