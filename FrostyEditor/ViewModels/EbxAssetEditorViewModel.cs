using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Frosty.Sdk.Ebx;
using Frosty.Sdk.Managers;
using Frosty.Sdk.Managers.Entries;
using CommunityToolkit.Mvvm.ComponentModel;
using FrostyEditor.Managers;
using FrostyEditor.Models;

namespace FrostyEditor.ViewModels;

public sealed partial class EbxAssetEditorViewModel : AssetEditorViewModel, ISessionStateAwareDocument
{
    private EbxPartition m_partition;
    private readonly EbxAssetEntry m_ebxEntry;
    private List<InspectorNodeModel> m_allNodes = [];
    private HashSet<string> m_expandedPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool m_hasManualInspectorNameColumnWidth;
    private CancellationTokenSource? m_filterCts;

    [ObservableProperty]
    private double m_inspectorNameColumnWidth = 220;
    public string AssetGuidText => (m_ebxEntry.Guid != Guid.Empty ? m_ebxEntry.Guid : m_partition.PartitionGuid).ToString();
    public string AssetNameText => Name;
    public string AssetPathText => Path;
    public string PartitionGuidText => m_partition.PartitionGuid.ToString();
    public string PrimaryInstanceGuidText => m_partition.PrimaryInstanceGuid.ToString();
    public string RootType => m_partition.PrimaryInstance.GetType().Name;
    public string InstanceCountText => m_partition.Instances.Count().ToString("N0", CultureInfo.InvariantCulture);
    public string DependencyCountText => m_partition.Dependencies.Count().ToString("N0", CultureInfo.InvariantCulture);
    public new string Summary => "Editable EBX inspector with the old Frosty-style property layout.";
    public ObservableCollection<InspectorNodeModel> Nodes { get; }
    public ObservableCollection<InspectorNodeModel> VisibleNodes { get; }

    [ObservableProperty]
    private string m_filterText = string.Empty;

    public EbxAssetEditorViewModel(EbxAssetEntry entry)
        : base(entry)
    {
        m_ebxEntry = entry;

        Stopwatch stopwatch = Stopwatch.StartNew();
        m_partition = AssetManager.GetEbxPartition(entry);
        long deserializeElapsedMs = stopwatch.ElapsedMilliseconds;
        Nodes = [];
        VisibleNodes = [];
        stopwatch.Restart();
        RebuildNodes();
        long buildElapsedMs = stopwatch.ElapsedMilliseconds;

        if (deserializeElapsedMs + buildElapsedMs >= 250)
        {
            Frosty.Sdk.FrostyLogger.Logger?.LogInfo(
                $"Opened EBX '{entry.Name}' in {deserializeElapsedMs + buildElapsedMs} ms " +
                $"(deserialize {deserializeElapsedMs} ms, inspector {buildElapsedMs} ms, visible rows {VisibleNodes.Count}).");
        }
    }

    partial void OnFilterTextChanged(string value)
    {
        DebounceApplyFilter();
    }

    public void RebuildNodes()
    {
        m_expandedPaths = CaptureExpandedPaths(Nodes);
        m_allNodes = BuildNodes().ToList();
        ApplyNodeStates(m_allNodes);
        ApplyFilter();
    }

    public void CopyNode(InspectorNodeModel node)
    {
        object? data = node.GetCopyObject();
        if (data is not null)
        {
            InspectorClipboard.Current.SetData(node.Name, data, node.HasChildren);
        }
    }

    public bool TryPasteNode(InspectorNodeModel node, out string? error)
    {
        error = null;
        if (!InspectorClipboard.Current.TryCreatePasteValue(node, m_partition, out object? value, out error))
        {
            return false;
        }

        if (!node.PasteObject(value, out error))
        {
            return false;
        }

        if (node.HasChildren)
        {
            m_expandedPaths = CaptureExpandedPaths(Nodes);
            RebuildNodes();
        }

        return true;
    }

    private IEnumerable<InspectorNodeModel> BuildNodes()
    {
        yield return new InspectorNodeModel(
            "Annotations",
            typeName: "Metadata",
            nodePath: "Annotations",
            initiallyExpanded: false,
            children:
            [
                new InspectorNodeModel("Guid", AssetGuidText, "Guid", "Annotations.Guid"),
                new InspectorNodeModel("Name", AssetNameText, "string", "Annotations.Name"),
                new InspectorNodeModel("Path", AssetPathText, "string", "Annotations.Path"),
                new InspectorNodeModel("PartitionGuid", PartitionGuidText, "Guid", "Annotations.PartitionGuid"),
                new InspectorNodeModel("PrimaryInstanceGuid", PrimaryInstanceGuidText, "Guid", "Annotations.PrimaryInstanceGuid")
            ]);

        yield return InspectorNodeBuilder.BuildNode(
            "Data",
            m_partition.PrimaryInstance,
            HandleInspectorValueCommitted,
            initiallyExpanded: true,
            getOwningAssetEntry: () => m_ebxEntry,
            getOwningPartition: () => m_partition);
    }

    private void ApplyFilter()
    {
        IEnumerable<InspectorNodeModel> filteredNodes = string.IsNullOrWhiteSpace(FilterText)
            ? m_allNodes
            : m_allNodes
                .Select(node => node.CreateFilteredClone(FilterText))
                .OfType<InspectorNodeModel>();

        Nodes.Clear();
        foreach (InspectorNodeModel node in filteredNodes)
        {
            Nodes.Add(node);
        }

        RefreshVisibleNodes();
    }

    public void RefreshVisibleNodes()
    {
        VisibleNodes.Clear();
        foreach (InspectorNodeModel node in EnumerateVisibleNodes(Nodes))
        {
            VisibleNodes.Add(node);
        }

        UpdateInspectorNameColumnWidth(VisibleNodes);
    }

    private async void DebounceApplyFilter()
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
                    ApplyFilter();
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

    public void RefreshNodeSubtree(InspectorNodeModel node)
    {
        int nodeIndex = VisibleNodes.IndexOf(node);
        if (nodeIndex < 0)
        {
            return;
        }

        RemoveVisibleDescendants(VisibleNodes, nodeIndex, node.Depth);

        if (node.IsExpanded)
        {
            node.EnsureChildrenLoaded();
            InsertVisibleDescendants(VisibleNodes, nodeIndex + 1, node.Children.Where(static child => !child.IsPlaceholder));
        }

        UpdateInspectorNameColumnWidth(VisibleNodes);
    }

    private void HandleInspectorValueCommitted(InspectorNodeModel node)
    {
        AssetManager.ModifyEbx(m_ebxEntry, m_partition);
        InspectorEditStateTracker.MarkDirty(m_ebxEntry.Name, node.NodePath);
        InspectorEditStateTracker.MarkModified(m_ebxEntry.Name, node.NodePath);
        AssetEditStateTracker.MarkDirty(m_ebxEntry.Name);
        AssetEditStateTracker.MarkModified(m_ebxEntry.Name);
        App.MainViewModel?.DataExplorer.RefreshExplorerState(rebuildTree: false);
    }

    private void ApplyNodeStates(IEnumerable<InspectorNodeModel> nodes)
    {
        foreach (InspectorNodeModel node in nodes)
        {
            node.IsDirty = InspectorEditStateTracker.IsDirty(m_ebxEntry.Name, node.NodePath);
            node.IsModified = InspectorEditStateTracker.IsModified(m_ebxEntry.Name, node.NodePath);
            node.IsExpanded = m_expandedPaths.Contains(node.NodePath) || node.IsExpanded;

            if (node.IsExpanded)
            {
                node.EnsureChildrenLoaded();
                if (node.Children.Count > 0)
                {
                    ApplyNodeStates(node.Children.Where(child => !child.IsPlaceholder));
                }
            }
        }
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
                foreach (string path in CaptureExpandedPaths(node.Children.Where(child => !child.IsPlaceholder)))
                {
                    expanded.Add(path);
                }
            }
        }

        return expanded;
    }

    private void UpdateInspectorNameColumnWidth(IEnumerable<InspectorNodeModel> nodes)
    {
        if (m_hasManualInspectorNameColumnWidth)
        {
            return;
        }

        double width = CalculateInspectorNameColumnWidth(nodes);
        InspectorNameColumnWidth = Math.Clamp(width, 140, 620);
    }

    public void SetManualInspectorNameColumnWidth(double width)
    {
        m_hasManualInspectorNameColumnWidth = true;
        InspectorNameColumnWidth = Math.Max(120, width);
    }

    private static double CalculateInspectorNameColumnWidth(IEnumerable<InspectorNodeModel> nodes)
    {
        double width = 140;
        foreach (InspectorNodeModel node in nodes)
        {
            int textLength = (node.Name?.Length ?? 0) + (node.NameSuffix?.Length ?? 0);
            double nodeWidth = 42 + (node.Depth * 18) + (textLength * 7.8);
            width = Math.Max(width, nodeWidth);
        }

        return width;
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

    public void RefreshSessionState()
    {
        ApplyNodeStates(m_allNodes);
        ApplyFilter();
    }

    public override void ReloadFromSource()
    {
        m_expandedPaths = CaptureExpandedPaths(Nodes);
        m_partition = AssetManager.GetEbxPartition(m_ebxEntry);
        RebuildNodes();
    }
}



