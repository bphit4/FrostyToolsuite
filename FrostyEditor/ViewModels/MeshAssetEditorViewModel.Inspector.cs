using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Frosty.Sdk.Ebx;
using Frosty.Sdk.Managers;
using FrostyEditor.Managers;
using FrostyEditor.Models;

namespace FrostyEditor.ViewModels;

public sealed partial class MeshAssetEditorViewModel
{
    private enum MeshInspectorTab
    {
        Properties = 0,
        Variations = 1,
        Mesh = 2,
        Scene = 3
    }

    public sealed class MeshVariationDatabaseLinkViewModel
    {
        public PointerRef VariationDb { get; init; }
        public int Index { get; init; }
    }

    public sealed class MeshVariationTextureParameterViewModel
    {
        public string ParameterName { get; init; } = string.Empty;
        public PointerRef Texture { get; init; }
        public string TextureAsset { get; init; } = string.Empty;
    }

    public sealed class MeshVariationMaterialViewModel
    {
        public string MaterialGuid { get; init; } = string.Empty;
        public PointerRef MaterialVariation { get; init; }
        public ObservableCollection<MeshVariationTextureParameterViewModel> TextureParameters { get; } = [];
    }

    public sealed partial class MeshVariationDetailsViewModel : ObservableObject
    {
        public string Name { get; init; } = string.Empty;

        [ObservableProperty]
        private bool m_preview;

        public PointerRef Variation { get; init; }
        public ObservableCollection<MeshVariationMaterialViewModel> MaterialCollection { get; } = [];
        public ObservableCollection<MeshVariationDatabaseLinkViewModel> MeshVariationDbs { get; } = [];

        public override string ToString() => Name;
    }

    private sealed class MeshInspectorMeshTabModel
    {
        public ObservableCollection<MeshLodItemViewModel> Lods { get; } = [];
        public string Counts { get; init; } = string.Empty;
        public string Bounds { get; init; } = string.Empty;
    }

    private sealed class MeshInspectorCameraModel
    {
        public string View { get; init; } = string.Empty;
        public float Zoom { get; init; }
        public float PanX { get; init; }
        public float PanY { get; init; }
        public float OrbitYawDegrees { get; init; }
        public float OrbitPitchDegrees { get; init; }
    }

    private sealed class MeshInspectorRenderModel
    {
        public string Mode { get; init; } = string.Empty;
        public string Channels { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string Interaction { get; init; } = string.Empty;
    }

    private sealed class MeshInspectorSelectionModel
    {
        public string Lod { get; init; } = string.Empty;
        public string Section { get; init; } = string.Empty;
        public string DecodeDiagnostics { get; init; } = string.Empty;
    }

    private sealed class MeshInspectorSceneTabModel
    {
        public required MeshInspectorCameraModel Camera { get; init; }
        public required MeshInspectorRenderModel Render { get; init; }
        public required MeshInspectorSelectionModel Selection { get; init; }
    }

    private readonly List<InspectorNodeModel> m_allPropertyNodes = [];
    private readonly List<InspectorNodeModel> m_allVariationNodes = [];
    private readonly List<InspectorNodeModel> m_allMeshNodes = [];
    private readonly List<InspectorNodeModel> m_allSceneNodes = [];
    private readonly Dictionary<MeshVariationDetailsViewModel, MeshVariationRecord> m_variationRecords = [];
    private HashSet<string> m_propertyExpandedPaths = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> m_variationExpandedPaths = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> m_meshExpandedPaths = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> m_sceneExpandedPaths = new(StringComparer.OrdinalIgnoreCase);
    private EbxPartition m_meshPartition;
    private bool m_hasManualInspectorNameColumnWidth;
    private bool m_rebuildingVariations;
    private bool m_variationsLoaded;
    private bool m_variationsLoading;
    private MeshInspectorTab m_activeInspectorTab = MeshInspectorTab.Properties;
    private CancellationTokenSource? m_filterCts;

    [ObservableProperty]
    private double m_inspectorNameColumnWidth = 220;

    [ObservableProperty]
    private string m_propertiesFilterText = string.Empty;

    [ObservableProperty]
    private string m_variationsFilterText = string.Empty;

    [ObservableProperty]
    private string m_meshFilterText = string.Empty;

    [ObservableProperty]
    private string m_sceneFilterText = string.Empty;

    [ObservableProperty]
    private MeshVariationDetailsViewModel? m_selectedVariation;

    public ObservableCollection<InspectorNodeModel> PropertyNodes { get; } = [];
    public ObservableCollection<InspectorNodeModel> PropertyVisibleNodes { get; } = [];
    public ObservableCollection<InspectorNodeModel> VariationNodes { get; } = [];
    public ObservableCollection<InspectorNodeModel> VariationVisibleNodes { get; } = [];
    public ObservableCollection<InspectorNodeModel> MeshInspectorNodes { get; } = [];
    public ObservableCollection<InspectorNodeModel> MeshInspectorVisibleNodes { get; } = [];
    public ObservableCollection<InspectorNodeModel> SceneNodes { get; } = [];
    public ObservableCollection<InspectorNodeModel> SceneVisibleNodes { get; } = [];
    public ObservableCollection<MeshVariationDetailsViewModel> Variations { get; } = [];

    public bool HasVariations => Variations.Count > 0;
    internal MeshVariationRecord? SelectedVariationRecord =>
        SelectedVariation is not null && m_variationRecords.TryGetValue(SelectedVariation, out MeshVariationRecord? record)
            ? record
            : null;
    internal MeshVariationRecord? PreviewVariationRecord =>
        SelectedVariationRecord is { IsDefault: false } record ? record : null;

    partial void OnPropertiesFilterTextChanged(string value) => DebounceApplyFilter(() => ApplyFilter(PropertyNodes, PropertyVisibleNodes, m_allPropertyNodes, PropertiesFilterText));
    partial void OnVariationsFilterTextChanged(string value) => DebounceApplyFilter(() => ApplyFilter(VariationNodes, VariationVisibleNodes, m_allVariationNodes, VariationsFilterText));
    partial void OnMeshFilterTextChanged(string value) => DebounceApplyFilter(() => ApplyFilter(MeshInspectorNodes, MeshInspectorVisibleNodes, m_allMeshNodes, MeshFilterText));
    partial void OnSceneFilterTextChanged(string value) => DebounceApplyFilter(() => ApplyFilter(SceneNodes, SceneVisibleNodes, m_allSceneNodes, SceneFilterText));

    partial void OnSelectedVariationChanged(MeshVariationDetailsViewModel? value)
    {
        UpdateVariationPreviewFlags(value);
        RebuildVariationInspector();
        OnPropertyChanged(nameof(HasVariations));
        if (!m_rebuildingVariations)
        {
            InvalidateViewportScene();
        }
    }

    private void InitializeInspectorState()
    {
        m_meshPartition = AssetManager.GetEbxPartition(m_meshEntry);
        RebuildInspectorTabs();
    }

    private void RebuildInspectorTabs()
    {
        RebuildPropertyInspector();
        ClearVariationInspector();
        RebuildMeshInspector();
        RebuildSceneInspector();
        OnPropertyChanged(nameof(HasVariations));
    }

    internal void RequestVariationListLoad()
    {
        EnsureVariationListLoaded();
    }

    private void RebuildPropertyInspector()
    {
        m_propertyExpandedPaths = CaptureExpandedPaths(PropertyNodes);
        m_allPropertyNodes.Clear();
        m_allPropertyNodes.Add(new InspectorNodeModel(
            "Annotations",
            typeName: "Metadata",
            nodePath: "Annotations",
            initiallyExpanded: true,
            children:
            [
                new InspectorNodeModel("Name", MeshName, "string", "Annotations.Name"),
                new InspectorNodeModel("Path", MeshFullName, "string", "Annotations.Path"),
                new InspectorNodeModel("Resource", ResourceName, "string", "Annotations.Resource"),
                new InspectorNodeModel("ResourceRid", ResourceRid, "string", "Annotations.ResourceRid")
            ]));
        m_allPropertyNodes.Add(InspectorNodeBuilder.BuildNode(
            "Data",
            m_load.RootObject,
            HandleInspectorValueCommitted,
            initiallyExpanded: true,
            getOwningAssetEntry: () => m_meshEntry,
            getOwningPartition: () => m_meshPartition));
        ApplyNodeStates(m_allPropertyNodes, m_propertyExpandedPaths, m_meshEntry.Name);
        ApplyFilter(PropertyNodes, PropertyVisibleNodes, m_allPropertyNodes, PropertiesFilterText);
    }

    private void EnsureVariationListLoaded()
    {
        if (m_variationsLoaded || m_variationsLoading)
        {
            return;
        }

        m_variationsLoading = true;
        _ = Task.Run(() => MeshVariationDatabaseManager.GetDisplayVariations(m_load))
            .ContinueWith(
                task =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        m_variationsLoading = false;
                        if (task.Status != TaskStatus.RanToCompletion)
                        {
                            return;
                        }

                        ApplyVariationRecords(task.Result);
                    });
                },
                TaskScheduler.Default);
    }

    private void ApplyVariationRecords(IReadOnlyList<MeshVariationRecord> variations)
    {
        string? preferredVariationName = SelectedVariation?.Name;
        m_rebuildingVariations = true;
        try
        {
            m_variationsLoaded = true;
            m_variationRecords.Clear();
            Variations.Clear();
            foreach (MeshVariationRecord variation in variations)
            {
                MeshVariationDetailsViewModel variationViewModel = new()
                {
                    Name = variation.Name,
                    Preview = false,
                    Variation = variation.Variation
                };
                m_variationRecords[variationViewModel] = variation;

                foreach (MeshVariationMaterialRecord material in variation.Materials)
                {
                    MeshVariationMaterialViewModel materialViewModel = new()
                    {
                        MaterialGuid = material.MaterialGuid == Guid.Empty ? "Unknown" : material.MaterialGuid.ToString(),
                        MaterialVariation = material.MaterialVariation
                    };

                    foreach (MeshVariationTextureParameter texture in material.TextureParameters.OrderBy(static item => item.ParameterName, StringComparer.OrdinalIgnoreCase))
                    {
                        materialViewModel.TextureParameters.Add(new MeshVariationTextureParameterViewModel
                        {
                            ParameterName = string.IsNullOrWhiteSpace(texture.ParameterName) ? "Texture" : texture.ParameterName,
                            Texture = texture.Texture,
                            TextureAsset = texture.TextureEntry?.Name ?? string.Empty
                        });
                    }

                    variationViewModel.MaterialCollection.Add(materialViewModel);
                }

                foreach (MeshVariationDatabaseLocation location in variation.MeshVariationDbs)
                {
                    variationViewModel.MeshVariationDbs.Add(new MeshVariationDatabaseLinkViewModel
                    {
                        VariationDb = location.VariationDatabase,
                        Index = location.Index
                    });
                }

                Variations.Add(variationViewModel);
            }

            SelectedVariation = Variations.FirstOrDefault(item => string.Equals(item.Name, preferredVariationName, StringComparison.OrdinalIgnoreCase))
                               ?? Variations.FirstOrDefault();
            UpdateVariationPreviewFlags(SelectedVariation);
            RebuildVariationInspector();
            OnPropertyChanged(nameof(HasVariations));
        }
        finally
        {
            m_rebuildingVariations = false;
        }
    }

    private void ClearVariationInspector()
    {
        m_variationExpandedPaths = CaptureExpandedPaths(VariationNodes);
        m_allVariationNodes.Clear();
        ApplyFilter(VariationNodes, VariationVisibleNodes, m_allVariationNodes, VariationsFilterText);
    }

    private void RebuildVariationInspector()
    {
        m_variationExpandedPaths = CaptureExpandedPaths(VariationNodes);
        m_allVariationNodes.Clear();
        if (SelectedVariation is not null)
        {
            m_allVariationNodes.Add(InspectorNodeBuilder.BuildNode(
                "Default",
                SelectedVariation,
                initiallyExpanded: true));
        }

        ApplyNodeStates(m_allVariationNodes, m_variationExpandedPaths, m_meshEntry.Name);
        ApplyFilter(VariationNodes, VariationVisibleNodes, m_allVariationNodes, VariationsFilterText);
    }

    private void RebuildMeshInspector()
    {
        m_meshExpandedPaths = CaptureExpandedPaths(MeshInspectorNodes);
        MeshInspectorMeshTabModel model = new()
        {
            Counts = CountsSummary,
            Bounds = MeshBounds
        };

        foreach (MeshLodItemViewModel lod in Lods)
        {
            model.Lods.Add(lod);
        }

        m_allMeshNodes.Clear();
        m_allMeshNodes.Add(InspectorNodeBuilder.BuildNode(
            "Mesh",
            model,
            initiallyExpanded: true));
        ApplyNodeStates(m_allMeshNodes, m_meshExpandedPaths, m_meshEntry.Name);
        ApplyFilter(MeshInspectorNodes, MeshInspectorVisibleNodes, m_allMeshNodes, MeshFilterText);
    }

    private void RebuildSceneInspector()
    {
        m_sceneExpandedPaths = CaptureExpandedPaths(SceneNodes);
        MeshInspectorSceneTabModel model = new()
        {
            Camera = new MeshInspectorCameraModel
            {
                View = PreviewViewLabel,
                Zoom = m_previewCamera.ZoomFactor,
                PanX = m_previewCamera.PanX,
                PanY = m_previewCamera.PanY,
                OrbitYawDegrees = m_previewCamera.OrbitYaw * 57.29578f,
                OrbitPitchDegrees = m_previewCamera.OrbitPitch * 57.29578f
            },
            Render = new MeshInspectorRenderModel
            {
                Mode = RenderModeLabel,
                Channels = ChannelMaskLabel,
                Status = PreviewStatus,
                Interaction = PreviewInteractionSummary
            },
            Selection = new MeshInspectorSelectionModel
            {
                Lod = SelectedLodSummary,
                Section = SelectedSectionSummary,
                DecodeDiagnostics = DecodeStatus
            }
        };

        m_allSceneNodes.Clear();
        m_allSceneNodes.Add(InspectorNodeBuilder.BuildNode(
            "Scene",
            model,
            initiallyExpanded: true));
        ApplyNodeStates(m_allSceneNodes, m_sceneExpandedPaths, m_meshEntry.Name);
        ApplyFilter(SceneNodes, SceneVisibleNodes, m_allSceneNodes, SceneFilterText);
    }

    private void UpdateVariationPreviewFlags(MeshVariationDetailsViewModel? selectedVariation)
    {
        foreach (MeshVariationDetailsViewModel variation in Variations)
        {
            variation.Preview = ReferenceEquals(variation, selectedVariation);
        }
    }

    private void HandleInspectorValueCommitted(InspectorNodeModel node)
    {
        AssetManager.ModifyEbx(m_meshEntry, m_meshPartition);
        InspectorEditStateTracker.MarkDirty(m_meshEntry.Name, node.NodePath);
        InspectorEditStateTracker.MarkModified(m_meshEntry.Name, node.NodePath);
        AssetEditStateTracker.MarkDirty(m_meshEntry.Name);
        AssetEditStateTracker.MarkModified(m_meshEntry.Name);
        App.MainViewModel?.DataExplorer.RefreshExplorerState(rebuildTree: false);
    }

    public void CopyNode(InspectorNodeModel node)
    {
        object? data = node.GetCopyObject();
        if (data is not null)
        {
            InspectorClipboard.Current.SetData(node.Name, data, node.HasChildren);
        }
    }

    public bool TryRefreshInspectorRows(InspectorNodeModel node)
    {
        if (TryRefreshNodeSubtree(PropertyNodes, PropertyVisibleNodes, node))
        {
            return true;
        }

        if (TryRefreshNodeSubtree(VariationNodes, VariationVisibleNodes, node))
        {
            return true;
        }

        if (TryRefreshNodeSubtree(MeshInspectorNodes, MeshInspectorVisibleNodes, node))
        {
            return true;
        }

        if (TryRefreshNodeSubtree(SceneNodes, SceneVisibleNodes, node))
        {
            return true;
        }

        return false;
    }

    public void RefreshAllInspectorRows()
    {
        RefreshVisibleNodes(PropertyNodes, PropertyVisibleNodes);
        RefreshVisibleNodes(VariationNodes, VariationVisibleNodes);
        RefreshVisibleNodes(MeshInspectorNodes, MeshInspectorVisibleNodes);
        RefreshVisibleNodes(SceneNodes, SceneVisibleNodes);
    }

    public void RebuildInspectorPanels()
    {
        RebuildInspectorTabs();
    }

    public void SetManualInspectorNameColumnWidth(double width)
    {
        m_hasManualInspectorNameColumnWidth = true;
        InspectorNameColumnWidth = Math.Max(120, width);
    }

    public void SetActiveInspectorTab(string? header)
    {
        MeshInspectorTab nextTab = header?.Trim() switch
        {
            "Variations" => MeshInspectorTab.Variations,
            "Mesh" => MeshInspectorTab.Mesh,
            "Scene" => MeshInspectorTab.Scene,
            _ => MeshInspectorTab.Properties
        };

        if (m_activeInspectorTab == nextTab)
        {
            return;
        }

        m_activeInspectorTab = nextTab;
        UpdateInspectorNameColumnWidth(GetActiveInspectorVisibleNodes());
    }

    private void ApplyFilter(
        ObservableCollection<InspectorNodeModel> nodes,
        ObservableCollection<InspectorNodeModel> visibleNodes,
        IReadOnlyList<InspectorNodeModel> sourceNodes,
        string filterText)
    {
        IEnumerable<InspectorNodeModel> filteredNodes = string.IsNullOrWhiteSpace(filterText)
            ? sourceNodes
            : sourceNodes
                .Select(node => node.CreateFilteredClone(filterText))
                .OfType<InspectorNodeModel>();

        nodes.Clear();
        foreach (InspectorNodeModel node in filteredNodes)
        {
            nodes.Add(node);
        }

        RefreshVisibleNodes(nodes, visibleNodes);
    }

    private async void DebounceApplyFilter(Action applyAction)
    {
        m_filterCts?.Cancel();
        CancellationTokenSource cts = new();
        m_filterCts = cts;

        try
        {
            await Task.Delay(320, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (m_filterCts == cts)
                {
                    applyAction();
                }
            });
        }
        catch (TaskCanceledException)
        {
        }
        finally
        {
            if (m_filterCts == cts)
            {
                m_filterCts = null;
            }

            cts.Dispose();
        }
    }

    private void RefreshVisibleNodes(
        ObservableCollection<InspectorNodeModel> nodes,
        ObservableCollection<InspectorNodeModel> visibleNodes)
    {
        visibleNodes.Clear();
        foreach (InspectorNodeModel node in EnumerateVisibleNodes(nodes))
        {
            visibleNodes.Add(node);
        }

        UpdateInspectorNameColumnWidth(GetActiveInspectorVisibleNodes());
    }

    private bool TryRefreshNodeSubtree(
        ObservableCollection<InspectorNodeModel> nodes,
        ObservableCollection<InspectorNodeModel> visibleNodes,
        InspectorNodeModel node)
    {
        int nodeIndex = visibleNodes.IndexOf(node);
        if (nodeIndex < 0)
        {
            return false;
        }

        RemoveVisibleDescendants(visibleNodes, nodeIndex, node.Depth);

        if (node.IsExpanded)
        {
            node.EnsureChildrenLoaded();
            InsertVisibleDescendants(visibleNodes, nodeIndex + 1, node.Children.Where(static child => !child.IsPlaceholder));
        }

        UpdateInspectorNameColumnWidth(GetActiveInspectorVisibleNodes());
        return true;
    }

    private void ApplyNodeStates(IEnumerable<InspectorNodeModel> nodes, HashSet<string> expandedPaths, string assetName)
    {
        foreach (InspectorNodeModel node in nodes)
        {
            node.IsDirty = InspectorEditStateTracker.IsDirty(assetName, node.NodePath);
            node.IsModified = InspectorEditStateTracker.IsModified(assetName, node.NodePath);
            node.IsExpanded = expandedPaths.Contains(node.NodePath) || node.IsExpanded;
            if (node.IsExpanded)
            {
                node.EnsureChildrenLoaded();
                if (node.Children.Count > 0)
                {
                    ApplyNodeStates(node.Children.Where(static child => !child.IsPlaceholder), expandedPaths, assetName);
                }
            }
        }
    }

    private void UpdateInspectorNameColumnWidth(IEnumerable<InspectorNodeModel> nodes)
    {
        if (m_hasManualInspectorNameColumnWidth)
        {
            return;
        }

        double width = 140;
        foreach (InspectorNodeModel node in nodes)
        {
            int textLength = (node.Name?.Length ?? 0) + (node.NameSuffix?.Length ?? 0);
            double nodeWidth = 42 + (node.Depth * 18) + (textLength * 7.8);
            width = Math.Max(width, nodeWidth);
        }

        InspectorNameColumnWidth = Math.Clamp(width, 140, 260);
    }

    private IEnumerable<InspectorNodeModel> GetActiveInspectorVisibleNodes()
    {
        return m_activeInspectorTab switch
        {
            MeshInspectorTab.Variations => VariationVisibleNodes,
            MeshInspectorTab.Mesh => MeshInspectorVisibleNodes,
            MeshInspectorTab.Scene => SceneVisibleNodes,
            _ => PropertyVisibleNodes
        };
    }

    private static HashSet<string> CaptureExpandedPaths(IEnumerable<InspectorNodeModel> nodes)
    {
        HashSet<string> expanded = new(StringComparer.OrdinalIgnoreCase);
        foreach (InspectorNodeModel node in nodes)
        {
            if (!node.IsExpanded)
            {
                continue;
            }

            expanded.Add(node.NodePath);
            node.EnsureChildrenLoaded();
            if (node.Children.Count > 0)
            {
                foreach (string path in CaptureExpandedPaths(node.Children.Where(static child => !child.IsPlaceholder)))
                {
                    expanded.Add(path);
                }
            }
        }

        return expanded;
    }

    private static IEnumerable<InspectorNodeModel> EnumerateVisibleNodes(IEnumerable<InspectorNodeModel> nodes)
    {
        foreach (InspectorNodeModel node in nodes)
        {
            yield return node;
            if (!node.IsExpanded)
            {
                continue;
            }

            node.EnsureChildrenLoaded();
            foreach (InspectorNodeModel child in EnumerateVisibleNodes(node.Children.Where(static child => !child.IsPlaceholder)))
            {
                yield return child;
            }
        }
    }

    private static void RemoveVisibleDescendants(ObservableCollection<InspectorNodeModel> visibleNodes, int nodeIndex, int parentDepth)
    {
        int removeIndex = nodeIndex + 1;
        while (removeIndex < visibleNodes.Count && visibleNodes[removeIndex].Depth > parentDepth)
        {
            visibleNodes.RemoveAt(removeIndex);
        }
    }

    private static void InsertVisibleDescendants(
        ObservableCollection<InspectorNodeModel> visibleNodes,
        int insertIndex,
        IEnumerable<InspectorNodeModel> children)
    {
        foreach (InspectorNodeModel child in EnumerateVisibleNodes(children))
        {
            visibleNodes.Insert(insertIndex++, child);
        }
    }

}
