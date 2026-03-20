using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
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
        ApplyFilter();
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

    public void RefreshNodeSubtree(InspectorNodeModel node)
    {
        int nodeIndex = VisibleNodes.IndexOf(node);
        if (nodeIndex < 0)
        {
            RefreshVisibleNodes();
            return;
        }

        RemoveVisibleDescendants(nodeIndex, node.Depth);
        if (node.IsExpanded)
        {
            node.EnsureChildrenLoaded();
            InsertVisibleDescendants(nodeIndex + 1, node.Children.Where(static child => !child.IsPlaceholder));
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
        InspectorNameColumnWidth = Math.Clamp(width, 180, 560);
    }

    public void SetManualInspectorNameColumnWidth(double width)
    {
        m_hasManualInspectorNameColumnWidth = true;
        InspectorNameColumnWidth = Math.Max(80, width);
    }

    private static double CalculateInspectorNameColumnWidth(IEnumerable<InspectorNodeModel> nodes)
    {
        double width = 180;
        foreach (InspectorNodeModel node in nodes)
        {
            double nodeWidth = 28 + (node.Depth * 14) + ((node.Name.Length + node.NameSuffix.Length) * 7.2);
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

    private void RemoveVisibleDescendants(int nodeIndex, int parentDepth)
    {
        int removalIndex = nodeIndex + 1;
        while (removalIndex < VisibleNodes.Count && VisibleNodes[removalIndex].Depth > parentDepth)
        {
            VisibleNodes.RemoveAt(removalIndex);
        }
    }

    private void InsertVisibleDescendants(int insertIndex, IEnumerable<InspectorNodeModel> nodes)
    {
        foreach (InspectorNodeModel node in nodes)
        {
            VisibleNodes.Insert(insertIndex++, node);

            if (!node.IsExpanded)
            {
                continue;
            }

            node.EnsureChildrenLoaded();
            InsertVisibleDescendants(insertIndex, node.Children.Where(static child => !child.IsPlaceholder));
            insertIndex += CountVisibleDescendants(node);
        }
    }

    private static int CountVisibleDescendants(InspectorNodeModel node)
    {
        if (!node.IsExpanded)
        {
            return 0;
        }

        int count = 0;
        foreach (InspectorNodeModel child in node.Children.Where(static child => !child.IsPlaceholder))
        {
            count++;
            count += CountVisibleDescendants(child);
        }

        return count;
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



