using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Frosty.Sdk.Ebx;
using Frosty.Sdk.Managers;
using Frosty.Sdk.Managers.Entries;
using Frosty.Sdk.Resources;
using FrostyEditor.Managers;
using FrostyEditor.Models;

namespace FrostyEditor.ViewModels;

public partial class TextureAssetEditorViewModel : AssetEditorViewModel, ISessionStateAwareDocument
{
    private TextureAssetLoadResult m_load;
    private readonly EbxAssetEntry m_ebxEntry;
    private readonly EbxPartition m_partition;
    private bool m_isRebuildingSelectors;
    private bool m_previewEnabled;
    private bool m_allowProgressivePreview;
    private CancellationTokenSource? m_previewCts;
    private CancellationTokenSource? m_filterCts;
    private double m_viewportWidth;
    private double m_viewportHeight;
    private int m_firstAvailableMip;
    private List<InspectorNodeModel> m_allNodes = [];
    private HashSet<string> m_expandedPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool m_hasManualInspectorNameColumnWidth;

    [ObservableProperty]
    private Bitmap? m_previewBitmap;

    [ObservableProperty]
    private int m_selectedMipLevel;

    [ObservableProperty]
    private int m_selectedSliceLevel;

    [ObservableProperty]
    private TextureChannelMask m_channels = TextureChannelMask.Rgba;

    [ObservableProperty]
    private bool m_showLuminance;

    [ObservableProperty]
    private bool m_showCheckerboard;

    [ObservableProperty]
    private string m_zoomText = "Fit";

    [ObservableProperty]
    private string m_textureFormatLabel = string.Empty;

    [ObservableProperty]
    private bool m_isModified;

    // Reduced from 180 → 140 so the auto-sized column starts narrower
    [ObservableProperty]
    private double m_inspectorNameColumnWidth = 140;

    [ObservableProperty]
    private string m_filterText = string.Empty;

    public ObservableCollection<string> AvailableMips { get; } = [];
    public ObservableCollection<string> AvailableSlices { get; } = [];
    public ObservableCollection<InspectorNodeModel> Nodes { get; } = [];
    public ObservableCollection<InspectorNodeModel> VisibleNodes { get; } = [];

    public Texture Texture => m_load.Texture;
    public string TextureWidth => Texture.Width.ToString(CultureInfo.InvariantCulture);
    public string TextureHeight => Texture.Height.ToString(CultureInfo.InvariantCulture);
    public string TextureDepth => Texture.Depth.ToString(CultureInfo.InvariantCulture);
    public string TextureGroup => Texture.TextureGroup;
    public string ResourceName => m_load.ResourceEntry.Name;
    public string ResourceType => m_load.ResourceEntry.Type;
    public string ResourceRid => $"0x{m_load.ResourceEntry.ResRid:X}";
    public string ResourceSize => $"{m_load.ResourceEntry.OriginalSize:N0} bytes";
    public string TextureFlagsText => Texture.Flags.ToString();
    public string SliceVisibility => AvailableSlices.Count > 1 ? "Visible" : "Collapsed";
    public bool HasSliceSelector => AvailableSlices.Count > 1;
    public bool ShowPreviewCheckerboard => ShowCheckerboard;
    public bool CanRevert => IsModified;

    public TextureAssetEditorViewModel(EbxAssetEntry entry)
        : base(entry)
    {
        m_ebxEntry = entry;
        m_partition = AssetManager.GetEbxPartition(entry);
        m_load = TextureAssetOperations.Load(entry);
        SyncLoadState(resetSelections: true);
    }

    partial void OnSelectedMipLevelChanged(int value)
    {
        if (!m_isRebuildingSelectors)
        {
            QueueRefreshPreview();
        }
    }

    partial void OnSelectedSliceLevelChanged(int value)
    {
        if (!m_isRebuildingSelectors)
        {
            QueueRefreshPreview();
        }
    }

    partial void OnChannelsChanged(TextureChannelMask value) => QueueRefreshPreview();
    partial void OnShowLuminanceChanged(bool value) => QueueRefreshPreview();
    partial void OnShowCheckerboardChanged(bool value) => OnPropertyChanged(nameof(ShowPreviewCheckerboard));
    partial void OnFilterTextChanged(string value) => DebounceApplyInspectorFilter();

    [RelayCommand]
    private void ToggleRed()
    {
        ShowLuminance = false;
        Channels ^= TextureChannelMask.Red;
        EnsureAtLeastOneChannel();
    }

    [RelayCommand]
    private void ToggleGreen()
    {
        ShowLuminance = false;
        Channels ^= TextureChannelMask.Green;
        EnsureAtLeastOneChannel();
    }

    [RelayCommand]
    private void ToggleBlue()
    {
        ShowLuminance = false;
        Channels ^= TextureChannelMask.Blue;
        EnsureAtLeastOneChannel();
    }

    [RelayCommand]
    private void ToggleAlpha()
    {
        ShowLuminance = false;
        Channels ^= TextureChannelMask.Alpha;
        EnsureAtLeastOneChannel();
    }

    [RelayCommand]
    private void ToggleLuminance()
    {
        ShowLuminance = !ShowLuminance;
    }

    [RelayCommand]
    private void ToggleCheckerboard()
    {
        ShowCheckerboard = !ShowCheckerboard;
    }

    [RelayCommand]
    private async Task ExportTexture()
    {
        TextureOperationResult result = await TextureAssetOperations.ExportWithPickerAsync((EbxAssetEntry)m_entry);
        LogOperationResult(result);
    }

    [RelayCommand]
    private async Task ImportTexture()
    {
        TextureOperationResult result = await TextureAssetOperations.ImportWithPickerAsync((EbxAssetEntry)m_entry);
        LogOperationResult(result);
        if (result.Success)
        {
            App.MainViewModel?.DataExplorer.RefreshExplorerState(rebuildTree: false);
            ReloadFromSource();
        }
    }

    [RelayCommand]
    private Task RevertTexture()
    {
        TextureOperationResult result = TextureAssetOperations.Revert((EbxAssetEntry)m_entry);
        LogOperationResult(result);
        if (result.Success)
        {
            App.MainViewModel?.DataExplorer.RefreshExplorerState(rebuildTree: false);
            ReloadFromSource();
        }

        return Task.CompletedTask;
    }

    public void SetZoomText(double zoom)
    {
        ZoomText = zoom <= 0 ? "Fit" : $"{zoom:P0}";
    }

    public void EnablePreview()
    {
        if (m_previewEnabled)
        {
            return;
        }

        m_previewEnabled = true;
        QueueRefreshPreview();
    }

    public void UpdateViewportSize(double width, double height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        m_viewportWidth = width;
        m_viewportHeight = height;

        if (m_previewEnabled && PreviewBitmap is null)
        {
            QueueRefreshPreview();
        }
    }

    private void SyncLoadState(bool resetSelections)
    {
        TextureFormatLabel = Texture.PixelFormat;
        IsModified = AssetEditStateTracker.IsModified(m_ebxEntry.Name);
        m_allowProgressivePreview = true;
        RebuildSelectors(resetSelections);
        RebuildMetadataNodes();
        RaiseMetadataChanged();
        QueueRefreshPreview();
    }

    private void RebuildSelectors(bool resetSelections)
    {
        m_isRebuildingSelectors = true;

        int previousMip = SelectedMipLevel;
        int previousSlice = SelectedSliceLevel;

        AvailableMips.Clear();
        m_firstAvailableMip = TextureAssetOperations.GetPreviewFirstMip(Texture);
        int mipCount = Math.Max(1, TextureAssetOperations.GetPreviewMipCount(Texture));
        for (int i = 0; i < mipCount; i++)
        {
            int actualMipLevel = m_firstAvailableMip + i;
            int width = Math.Max(1, Texture.Width >> actualMipLevel);
            int height = Math.Max(1, Texture.Height >> actualMipLevel);
            AvailableMips.Add($"{actualMipLevel}: {width}x{height}");
        }

        AvailableSlices.Clear();
        int sliceCount = Texture.Type switch
        {
            TextureType.TT_Cube => 6,
            TextureType.TT_2dArray or TextureType.TT_3d => Math.Max(1, (int)(Texture.SliceCount > 0 ? Texture.SliceCount : Texture.Depth)),
            _ => 1
        };

        string[] cubeFaces = ["X+", "X-", "Y+", "Y-", "Z+", "Z-"];
        for (int i = 0; i < sliceCount; i++)
        {
            AvailableSlices.Add(Texture.Type == TextureType.TT_Cube ? cubeFaces[i] : i.ToString(CultureInfo.InvariantCulture));
        }

        SelectedMipLevel = resetSelections ? 0 : Math.Clamp(previousMip, 0, Math.Max(0, AvailableMips.Count - 1));
        SelectedSliceLevel = resetSelections ? 0 : Math.Clamp(previousSlice, 0, Math.Max(0, AvailableSlices.Count - 1));
        m_isRebuildingSelectors = false;
        OnPropertyChanged(nameof(HasSliceSelector));
        OnPropertyChanged(nameof(SliceVisibility));
    }

    private void RebuildMetadataNodes()
    {
        object rootObject = m_partition.PrimaryInstance;
        List<InspectorNodeModel> annotationsChildren =
        [
            new InspectorNodeModel("Guid", GetAssetGuid().ToString(), "Guid", "Annotations.Guid"),
            new InspectorNodeModel("Name", m_ebxEntry.Name, "string", "Annotations.Name"),
            new InspectorNodeModel("Path", m_ebxEntry.Path, "string", "Annotations.Path")
        ];

        List<InspectorNodeModel> miscChildren = [];

        if (TryGetRootPropertyValue(rootObject, "Resource", out object? resourceValue))
        {
            miscChildren.Add(InspectorNodeBuilder.BuildNode("Resource", resourceValue, getOwningAssetEntry: () => m_ebxEntry, getOwningPartition: () => m_partition));
        }
        else
        {
            miscChildren.Add(new InspectorNodeModel("Resource", m_load.ResourceEntry.ResRid.ToString("X16", CultureInfo.InvariantCulture), "ResourceRef", "Misc.Resource"));
        }

        if (TryGetRootPropertyValue(rootObject, "CropInfo", out object? cropInfoValue))
        {
            miscChildren.Add(InspectorNodeBuilder.BuildNode("CropInfo", cropInfoValue, getOwningAssetEntry: () => m_ebxEntry, getOwningPartition: () => m_partition));
        }
        else
        {
            miscChildren.Add(BuildFallbackCropInfoNode());
        }

        if (TryGetRootPropertyValue(rootObject, "AuthoredWidth", out object? authoredWidth))
        {
            miscChildren.Add(InspectorNodeBuilder.BuildNode("AuthoredWidth", authoredWidth, getOwningAssetEntry: () => m_ebxEntry, getOwningPartition: () => m_partition));
        }
        else
        {
            miscChildren.Add(new InspectorNodeModel("AuthoredWidth", Texture.Width.ToString(CultureInfo.InvariantCulture), nameof(UInt16), "Misc.AuthoredWidth"));
        }

        if (TryGetRootPropertyValue(rootObject, "AuthoredHeight", out object? authoredHeight))
        {
            miscChildren.Add(InspectorNodeBuilder.BuildNode("AuthoredHeight", authoredHeight, getOwningAssetEntry: () => m_ebxEntry, getOwningPartition: () => m_partition));
        }
        else
        {
            miscChildren.Add(new InspectorNodeModel("AuthoredHeight", Texture.Height.ToString(CultureInfo.InvariantCulture), nameof(UInt16), "Misc.AuthoredHeight"));
        }

        m_allNodes =
        [
            new InspectorNodeModel(
                "Annotations",
                typeName: "Metadata",
                nodePath: "Annotations",
                initiallyExpanded: true,
                children: annotationsChildren),
            new InspectorNodeModel(
                "Misc",
                typeName: "Texture",
                nodePath: "Misc",
                initiallyExpanded: true,
                children: miscChildren)
        ];

        ApplyNodeStates(m_allNodes);
        ApplyInspectorFilter();
    }

    private void RaiseMetadataChanged()
    {
        OnPropertyChanged(nameof(Texture));
        OnPropertyChanged(nameof(TextureWidth));
        OnPropertyChanged(nameof(TextureHeight));
        OnPropertyChanged(nameof(TextureDepth));
        OnPropertyChanged(nameof(TextureGroup));
        OnPropertyChanged(nameof(ResourceName));
        OnPropertyChanged(nameof(ResourceType));
        OnPropertyChanged(nameof(ResourceRid));
        OnPropertyChanged(nameof(ResourceSize));
        OnPropertyChanged(nameof(TextureFlagsText));
        OnPropertyChanged(nameof(CanRevert));
    }

    private void QueueRefreshPreview()
    {
        if (!m_previewEnabled)
        {
            return;
        }

        m_previewCts?.Cancel();
        m_previewCts?.Dispose();
        m_previewCts = new CancellationTokenSource();
        CancellationToken token = m_previewCts.Token;
        _ = RefreshPreviewAsync(token);
    }

    private async Task RefreshPreviewAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(40, cancellationToken);

            int mipLevel = m_firstAvailableMip + SelectedMipLevel;
            int sliceLevel = SelectedSliceLevel;
            TextureChannelMask channels = Channels;
            bool luminance = ShowLuminance;
            bool progressive = m_allowProgressivePreview && mipLevel == m_firstAvailableMip;
            int quickMipLevel = progressive ? GetFastPreviewMipLevel() : mipLevel;

            Bitmap preview = await Task.Run(
                () => TextureAssetOperations.CreatePreviewBitmap(Texture, quickMipLevel, sliceLevel, channels, luminance),
                cancellationToken);

            if (cancellationToken.IsCancellationRequested)
            {
                preview.Dispose();
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Bitmap? previous = PreviewBitmap;
                PreviewBitmap = preview;
                previous?.Dispose();
            });

            if (!progressive || quickMipLevel == mipLevel || cancellationToken.IsCancellationRequested)
            {
                m_allowProgressivePreview = false;
                return;
            }

            Bitmap fullPreview = await Task.Run(
                () => TextureAssetOperations.CreatePreviewBitmap(Texture, mipLevel, sliceLevel, channels, luminance),
                cancellationToken);

            if (cancellationToken.IsCancellationRequested)
            {
                fullPreview.Dispose();
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Bitmap? previous = PreviewBitmap;
                PreviewBitmap = fullPreview;
                previous?.Dispose();
            });
            m_allowProgressivePreview = false;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Frosty.Sdk.FrostyLogger.Logger?.LogError($"Texture preview failed for \"{m_ebxEntry.Filename}\": {ex.Message}");
        }
    }

    private int GetFastPreviewMipLevel()
    {
        if (m_viewportWidth <= 0 || m_viewportHeight <= 0)
        {
            return m_firstAvailableMip + SelectedMipLevel;
        }

        int mip = m_firstAvailableMip + SelectedMipLevel;
        int maxMip = m_firstAvailableMip + Math.Max(0, AvailableMips.Count - 1);
        double width = Texture.Width;
        double height = Texture.Height;
        double targetWidth = Math.Max(1, m_viewportWidth * 1.5);
        double targetHeight = Math.Max(1, m_viewportHeight * 1.5);

        while (mip < maxMip && (width > targetWidth || height > targetHeight))
        {
            width = Math.Max(1, width / 2.0);
            height = Math.Max(1, height / 2.0);
            mip++;
        }

        return mip;
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

        m_expandedPaths = CaptureExpandedPaths(Nodes);
        RebuildMetadataNodes();
        return true;
    }

    private void ApplyInspectorFilter()
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

    private InspectorNodeModel BuildFallbackCropInfoNode()
    {
        return new InspectorNodeModel(
            "CropInfo",
            "Vec4s16",
            "Vec4s16",
            "Misc.CropInfo",
            [
                new InspectorNodeModel("X", "0", nameof(Int16), "Misc.CropInfo.X"),
                new InspectorNodeModel("Y", "0", nameof(Int16), "Misc.CropInfo.Y"),
                new InspectorNodeModel("Z", "0", nameof(Int16), "Misc.CropInfo.Z"),
                new InspectorNodeModel("W", "0", nameof(Int16), "Misc.CropInfo.W")
            ]);
    }

    public override void ReloadFromSource()
    {
        m_expandedPaths = CaptureExpandedPaths(Nodes);
        m_load = TextureAssetOperations.Load((EbxAssetEntry)m_entry);
        SyncLoadState(resetSelections: false);
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

    private async void DebounceApplyInspectorFilter()
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
                    ApplyInspectorFilter();
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
        InspectorNameColumnWidth = Math.Max(110, width);
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
            if (node.Children.Count <= 0)
            {
                continue;
            }

            foreach (string path in CaptureExpandedPaths(node.Children.Where(child => !child.IsPlaceholder)))
            {
                expanded.Add(path);
            }
        }

        return expanded;
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
        IsModified = AssetEditStateTracker.IsModified(m_ebxEntry.Name);
        ApplyNodeStates(m_allNodes);
        ApplyInspectorFilter();
        RaiseMetadataChanged();
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

    private static void LogOperationResult(TextureOperationResult result)
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

    private void EnsureAtLeastOneChannel()
    {
        if (Channels == TextureChannelMask.None)
        {
            Channels = TextureChannelMask.Rgba;
        }
    }

    private int GetSafeMipCount()
    {
        int declaredMipCount = Math.Max(1, (int)Texture.MipCount);
        int actualMipCount = Texture.MipSizes?.Length ?? 0;
        return actualMipCount > 0 ? Math.Min(declaredMipCount, actualMipCount) : declaredMipCount;
    }

    private Guid GetAssetGuid()
    {
        return m_ebxEntry.Guid != Guid.Empty ? m_ebxEntry.Guid : m_partition.PartitionGuid;
    }

    private static bool TryGetRootPropertyValue(object rootObject, string propertyName, out object? value)
    {
        value = null;

        try
        {
            string normalizedTarget = NormalizeMemberName(propertyName);
            for (Type? type = rootObject.GetType(); type is not null; type = type.BaseType)
            {
                foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (!property.CanRead || property.GetIndexParameters().Length != 0)
                    {
                        continue;
                    }

                    if (!string.Equals(NormalizeMemberName(property.Name), normalizedTarget, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    value = InspectorNodeBuilder.NormalizeValue(property.GetValue(rootObject));
                    return value is not null;
                }

                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (!string.Equals(NormalizeMemberName(field.Name), normalizedTarget, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    value = InspectorNodeBuilder.NormalizeValue(field.GetValue(rootObject));
                    return value is not null;
                }
            }
        }
        catch
        {
        }

        value = null;
        return false;
    }

    private static string NormalizeMemberName(string name)
    {
        string trimmed = name.TrimStart('_');
        if (trimmed.StartsWith("m_", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..];
        }

        return trimmed.Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
    }
}
