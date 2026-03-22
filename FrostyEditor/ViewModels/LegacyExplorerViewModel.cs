using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Frosty.Sdk;
using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Controls.Selection;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FrostyEditor.Managers;
using FrostyEditor.Models;
using FrostyEditor.Utils;

namespace FrostyEditor.ViewModels;

public partial class LegacyExplorerViewModel : ViewModelBase
{
    private LegacyFolderTreeNodeModel m_root = new("ROOT") { IsExpanded = true };
    private LegacyFolderTreeNodeModel m_filteredRoot = new("ROOT") { IsExpanded = true };
    private IReadOnlyList<LegacyAssetModel> m_currentAssets = Array.Empty<LegacyAssetModel>();
    private LegacyFolderTreeNodeModel? m_selectedFolderNode;
    private LegacyAssetModel? m_selectedAsset;
    private CancellationTokenSource? m_filterCts;
    private readonly List<string> m_allAssetTypes = ["All"];
    private DateTime m_lastFolderTapUtc = DateTime.MinValue;
    private bool m_loadAttempted;
    private bool m_isLoading;
    private bool m_suppressImmediateFilterApply;
    private bool m_suppressAssetTypeSearchSync;

    [ObservableProperty]
    private HierarchicalTreeDataGridSource<LegacyFolderTreeNodeModel> m_folderSource;

    [ObservableProperty]
    private FlatTreeDataGridSource<LegacyAssetModel> m_assetsSource;

    [ObservableProperty]
    private MenuFlyout m_folderContextMenu;

    [ObservableProperty]
    private MenuFlyout m_assetContextMenu;

    [ObservableProperty]
    private string m_title = "Legacy Explorer";

    [ObservableProperty]
    private string m_filterText = string.Empty;

    [ObservableProperty]
    private string m_status = "Legacy assets will load when you open this tab.";

    [ObservableProperty]
    private string m_details = "The old Frosty legacy browser is being loaded directly from ChunkFileCollector data.";

    [ObservableProperty]
    private bool m_hasAssets;

    [ObservableProperty]
    private string m_selectedFolderName = "No folder selected";

    [ObservableProperty]
    private string m_selectedAssetName = "Nothing selected";

    [ObservableProperty]
    private string m_selectedAssetType = "Type: N/A";

    [ObservableProperty]
    private string m_selectedAssetPath = "Path: N/A";

    [ObservableProperty]
    private int m_folderCount;

    [ObservableProperty]
    private int m_assetCount;

    [ObservableProperty]
    private int m_totalAssetCount;

    [ObservableProperty]
    private string m_selectedAssetCountLabel = "0 Assets";

    [ObservableProperty]
    private string m_selectedAssetTypeFilter = "All";

    [ObservableProperty]
    private string m_assetTypeSearchText = string.Empty;

    [ObservableProperty]
    private bool m_showModifiedOnly;

    [ObservableProperty]
    private bool m_showUnmodifiedOnly;

    [ObservableProperty]
    private bool m_showUnsavedOnly;

    [ObservableProperty]
    private bool m_showAddedOnly;

    public List<string> AvailableAssetTypes { get; private set; } = new() { "All" };

    public bool ShowExplorer => HasAssets;
    public bool ShowMessagePanel => !HasAssets;

    public LegacyExplorerViewModel()
    {
        FolderSource = CreateFolderSource(m_filteredRoot);

        AssetsSource = new FlatTreeDataGridSource<LegacyAssetModel>(Array.Empty<LegacyAssetModel>())
        {
            Columns =
            {
                new TemplateColumn<LegacyAssetModel>(
                    "Name",
                    "LegacyAssetNameCell",
                    null,
                    new GridLength(2, GridUnitType.Star),
                    options: new()
                    {
                        CompareAscending = LegacyAssetModel.SortAscending(x => x.Name),
                        CompareDescending = LegacyAssetModel.SortDescending(x => x.Name)
                    }),
                new TextColumn<LegacyAssetModel, string>(
                    "Type",
                    x => x.Type,
                    new GridLength(1, GridUnitType.Star),
                    new TextColumnOptions<LegacyAssetModel>()
                    {
                        CompareAscending = LegacyAssetModel.SortAscending(x => x.Type),
                        CompareDescending = LegacyAssetModel.SortDescending(x => x.Type)
                    })
            }
        };
        AssetsSource.RowSelection!.SelectionChanged += OnAssetSelectionChanged;

        FolderContextMenu = new MenuFlyout()
        {
            Items =
            {
                new MenuItem { Header = "Expand", Command = ExpandSelectedFolderCommand, Icon = CreateMenuIcon("avares://FrostyEditor/Assets/Legacy/FrostyEditorImages/OpenFolder.png") },
                new MenuItem { Header = "Collapse", Command = CollapseSelectedFolderCommand, Icon = CreateMenuIcon("avares://FrostyEditor/Assets/Legacy/FrostyEditorImages/CloseFolder.png") },
                new Separator(),
                new MenuItem { Header = "Copy Folder Path", Command = CopySelectedFolderPathCommand }
            }
        };

        AssetContextMenu = new MenuFlyout()
        {
            Items =
            {
                new MenuItem { Header = "Open", Command = OpenAssetCommand, Icon = CreateMenuIcon("avares://FrostyEditor/Assets/Legacy/FrostyEditorImages/OpenAsset.png") },
                new MenuItem { Header = "Export", Command = ExportAssetCommand, Icon = CreateMenuIcon("avares://FrostyEditor/Assets/Legacy/FrostyEditorImages/Export.png") },
                new MenuItem { Header = "Import", Command = ImportAssetCommand, Icon = CreateMenuIcon("avares://FrostyEditor/Assets/Legacy/FrostyEditorImages/Import.png") },
                new Separator(),
                new MenuItem { Header = "Copy Asset Name", Command = CopySelectedAssetNameCommand },
                new MenuItem { Header = "Copy Asset Path", Command = CopySelectedAssetPathCommand }
            }
        };

        m_selectedFolderNode = m_root;
        SelectedFolderName = m_root.Name;
        m_currentAssets = Array.Empty<LegacyAssetModel>();
        RefreshAssetList();
    }

    partial void OnFilterTextChanged(string value)
    {
        DebounceRebuildFilteredTree();
    }

    partial void OnSelectedAssetTypeFilterChanged(string value)
    {
        if (!m_suppressAssetTypeSearchSync)
        {
            AssetTypeSearchText = string.Equals(value, "All", StringComparison.OrdinalIgnoreCase) ? string.Empty : value;
        }

        if (!m_suppressImmediateFilterApply)
        {
            RebuildFilteredTree();
        }
    }

    partial void OnAssetTypeSearchTextChanged(string value)
    {
        ApplyAvailableAssetTypeFilter();

        if (!m_suppressImmediateFilterApply &&
            string.IsNullOrWhiteSpace(value) &&
            string.Equals(SelectedAssetTypeFilter, "All", StringComparison.OrdinalIgnoreCase))
        {
            RebuildFilteredTree();
        }
    }

    partial void OnShowModifiedOnlyChanged(bool value) => RebuildFilteredTree();
    partial void OnShowUnmodifiedOnlyChanged(bool value) => RebuildFilteredTree();
    partial void OnShowUnsavedOnlyChanged(bool value) => RebuildFilteredTree();
    partial void OnShowAddedOnlyChanged(bool value) => RebuildFilteredTree();
    partial void OnAssetCountChanged(int value) => SelectedAssetCountLabel = $"{value} Assets";

    partial void OnHasAssetsChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowExplorer));
        OnPropertyChanged(nameof(ShowMessagePanel));
    }

    public async Task EnsureLoadedAsync()
    {
        if (m_loadAttempted || m_isLoading)
        {
            return;
        }

        m_isLoading = true;
        Status = "Loading legacy assets...";
        Details = "Scanning ChunkFileCollector entries and building the legacy folder tree.";

        try
        {
            LegacyAssetLoader.LoadResult result = await Task.Run(LegacyAssetLoader.Load);

            m_root = result.Root;
            m_filteredRoot = m_root;
            FolderSource = CreateFolderSource(m_filteredRoot);

            FolderCount = CountFolders(m_root);
            TotalAssetCount = result.AssetCount;
            PopulateAvailableAssetTypes();
            Status = result.Status;
            Details = result.Details;
            HasAssets = result.AssetCount > 0;

            SelectFolder(m_root);

            if (result.AssetCount > 0)
            {
                FrostyLogger.Logger?.LogInfo($"Legacy explorer loaded {result.AssetCount} assets.");
            }
        }
        catch (Exception ex)
        {
            HasAssets = false;
            Status = "Legacy asset loading failed.";
            Details = ex.Message;
            FrostyLogger.Logger?.LogError($"Legacy explorer failed: {ex}");
        }
        finally
        {
            m_loadAttempted = true;
            m_isLoading = false;
        }
    }

    [RelayCommand]
    private void OpenAsset()
    {
        if (m_selectedAsset is null)
        {
            return;
        }

        string documentKey = $"legacy|{m_selectedAsset.FullName}".ToLowerInvariant();
        if (App.MainViewModel?.ActivateDocumentByKey(documentKey) == true)
        {
            return;
        }

        App.MainViewModel?.AddDocument(
            documentKey,
            m_selectedAsset.FileNameWithExtension,
            m_selectedAsset.Path,
            new LegacyAssetDocumentViewModel(m_selectedAsset));
        FrostyLogger.Logger?.LogInfo($"Opened legacy asset \"{m_selectedAsset.FullName}\".");
    }

    [RelayCommand]
    private async Task ExportAsset()
    {
        if (m_selectedAsset is null)
        {
            return;
        }

        string extension = m_selectedAsset.Type.Equals("LEGACY", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : m_selectedAsset.Type.ToLowerInvariant();

        FilePickerSaveOptions options = new()
        {
            Title = "Save legacy asset as",
            SuggestedFileName = m_selectedAsset.FileNameWithExtension,
        };

        if (!string.IsNullOrWhiteSpace(extension))
        {
            options.DefaultExtension = extension;
            options.FileTypeChoices =
            [
                new FilePickerFileType($"{extension.ToUpperInvariant()} files (*.{extension})")
                {
                    Patterns = [$"*.{extension}"]
                }
            ];
        }

        IStorageFile? file = await FileService.SaveFilePickerAsync(options);
        if (file is null)
        {
            return;
        }

        byte[] data = await Task.Run(() => LegacyAssetLoader.ReadAssetBytes(m_selectedAsset));
        await using Stream output = await file.OpenWriteAsync();
        await output.WriteAsync(data);

        FrostyLogger.Logger?.LogInfo($"Exported legacy asset \"{m_selectedAsset.FullName}\".");
    }

    [RelayCommand]
    private async Task ImportAsset()
    {
        if (m_selectedAsset is null)
        {
            return;
        }

        const string message = "Legacy import still needs the old asset-modification pipeline ported over. Legacy open/export are available now, but writeback is not safe to expose yet.";
        FrostyLogger.Logger?.LogWarning(message);
    }

    [RelayCommand]
    private async Task CopySelectedFolderPath()
    {
        string? folderPath = GetFolderPathText(m_selectedFolderNode);
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        if (!await ClipboardService.SetTextAsync(folderPath))
        {
            FrostyLogger.Logger?.LogWarning("Unable to copy folder path because the clipboard is unavailable.");
        }
    }

    [RelayCommand]
    private async Task CopySelectedAssetName()
    {
        string? assetName = m_selectedAsset?.FileNameWithExtension;
        if (string.IsNullOrWhiteSpace(assetName))
        {
            return;
        }

        if (!await ClipboardService.SetTextAsync(assetName))
        {
            FrostyLogger.Logger?.LogWarning("Unable to copy asset name because the clipboard is unavailable.");
        }
    }

    [RelayCommand]
    private async Task CopySelectedAssetPath()
    {
        string? assetPath = m_selectedAsset?.FullName;
        if (string.IsNullOrWhiteSpace(assetPath))
        {
            return;
        }

        if (!await ClipboardService.SetTextAsync(assetPath))
        {
            FrostyLogger.Logger?.LogWarning("Unable to copy asset path because the clipboard is unavailable.");
        }
    }

    [RelayCommand]
    private void ExpandSelectedFolder()
    {
        if (m_selectedFolderNode is not null)
        {
            m_selectedFolderNode.IsExpanded = true;
        }
    }

    [RelayCommand]
    private void CollapseSelectedFolder()
    {
        if (m_selectedFolderNode is not null)
        {
            m_selectedFolderNode.IsExpanded = false;
        }
    }

    public void HandleFolderTapped(LegacyFolderTreeNodeModel? clickedNode)
    {
        if (clickedNode is null)
        {
            return;
        }

        SelectFolder(clickedNode);

        DateTime now = DateTime.UtcNow;
        if ((now - m_lastFolderTapUtc).TotalMilliseconds < 250)
        {
            return;
        }

        m_lastFolderTapUtc = now;
        if (!clickedNode.HasChildren)
        {
            return;
        }

        clickedNode.IsExpanded = !clickedNode.IsExpanded;
    }

    public void SelectFolderFromPointer(LegacyFolderTreeNodeModel? clickedNode)
    {
        if (clickedNode is null)
        {
            return;
        }

        SelectFolder(clickedNode);
    }

    public void HandleFolderDoubleTapped(LegacyFolderTreeNodeModel? clickedNode)
    {
        if (clickedNode is null)
        {
            return;
        }

        SelectFolder(clickedNode);
        clickedNode.IsExpanded = !clickedNode.IsExpanded;
    }

    public void HandleAssetDoubleTapped()
    {
        if (OpenAssetCommand.CanExecute(null))
        {
            OpenAssetCommand.Execute(null);
        }
    }

    public void HandleAssetTapped(LegacyAssetModel? clickedAsset)
    {
        if (clickedAsset is null)
        {
            return;
        }

        m_selectedAsset = clickedAsset;
        SelectedAssetName = clickedAsset.FileNameWithExtension;
        SelectedAssetType = $"Type: {clickedAsset.Type}";
        SelectedAssetPath = $"Path: {clickedAsset.Path}";
    }

    private void OnSelectionChanged(object? sender, TreeSelectionModelSelectionChangedEventArgs<LegacyFolderTreeNodeModel> e)
    {
        LegacyFolderTreeNodeModel? folder = e.SelectedItems.Count > 0 ? e.SelectedItems[0] : null;
        if (folder is not null)
        {
            SelectFolder(folder);
        }
    }

    private void OnAssetSelectionChanged(object? sender, TreeSelectionModelSelectionChangedEventArgs<LegacyAssetModel> e)
    {
        LegacyAssetModel? asset = e.SelectedItems.Count > 0 ? e.SelectedItems[0] : null;
        if (asset is null)
        {
            m_selectedAsset = null;
            SelectedAssetName = "Nothing selected";
            SelectedAssetType = "Type: N/A";
            SelectedAssetPath = "Path: N/A";
            return;
        }

        m_selectedAsset = asset;
        SelectedAssetName = asset.FileNameWithExtension;
        SelectedAssetType = $"Type: {asset.Type}";
        SelectedAssetPath = $"Path: {asset.Path}";
    }

    private async void DebounceRebuildFilteredTree()
    {
        m_filterCts?.Cancel();
        CancellationTokenSource cts = new();
        m_filterCts = cts;

        try
        {
            await Task.Delay(280, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (m_filterCts == cts)
                {
                    RebuildFilteredTree();
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

    private HierarchicalTreeDataGridSource<LegacyFolderTreeNodeModel> CreateFolderSource()
    {
        return CreateFolderSource(m_filteredRoot);
    }

    private HierarchicalTreeDataGridSource<LegacyFolderTreeNodeModel> CreateFolderSource(LegacyFolderTreeNodeModel root)
    {
        HierarchicalTreeDataGridSource<LegacyFolderTreeNodeModel> source = new(root)
        {
            Columns =
            {
                new HierarchicalExpanderColumn<LegacyFolderTreeNodeModel>(
                    new TemplateColumn<LegacyFolderTreeNodeModel>(
                        "Name",
                        "LegacyFolderNameCell",
                        null,
                        new GridLength(1, GridUnitType.Star),
                        options: new()
                        {
                            CanUserResizeColumn = false,
                            CanUserSortColumn = false,
                            CompareAscending = LegacyFolderTreeNodeModel.SortAscending(x => x.Name),
                            CompareDescending = LegacyFolderTreeNodeModel.SortDescending(x => x.Name)
                        }),
                    x => x.Children,
                    x => x.HasChildren,
                    x => x.IsExpanded),
            }
        };

        source.RowSelection!.SelectionChanged += OnSelectionChanged;
        source.Sort(LegacyFolderTreeNodeModel.SortAscending(x => x.Name));
        return source;
    }

    private void SelectFolder(LegacyFolderTreeNodeModel node)
    {
        m_selectedFolderNode = node;
        SelectedFolderName = node.Name;
        m_currentAssets = node.GetSortedAssets();
        RefreshAssetList();
    }

    private void RefreshAssetList()
    {
        Func<LegacyAssetModel, bool> predicate = BuildAssetPredicate();

        LegacyAssetModel[] items = m_currentAssets.Where(predicate).ToArray();
        AssetsSource.Items = items;
        AssetCount = items.Length;
    }

    private void RebuildFilteredTree()
    {
        string[]? selectedPath = GetNodePath(m_filteredRoot, m_selectedFolderNode);
        Func<LegacyAssetModel, bool> predicate = BuildAssetPredicate();
        bool filterActive = IsFilterActive();

        m_filteredRoot = filterActive
            ? LegacyFolderTreeNodeModel.CreateFiltered(m_root, predicate)
            : m_root;
        m_selectedFolderNode = FindPreferredNode(m_filteredRoot, selectedPath, filterActive);
        ExpandPathToNode(m_filteredRoot, m_selectedFolderNode);
        FolderSource = CreateFolderSource(m_filteredRoot);
        SelectedFolderName = m_selectedFolderNode.Name;
        m_currentAssets = m_selectedFolderNode.GetSortedAssets();
        RefreshAssetList();
    }

    private bool IsFilterActive()
    {
        return !string.IsNullOrWhiteSpace(FilterText) ||
               !string.Equals(SelectedAssetTypeFilter, "All", StringComparison.OrdinalIgnoreCase) ||
               ShowModifiedOnly ||
               ShowUnmodifiedOnly ||
               ShowUnsavedOnly ||
               ShowAddedOnly;
    }

    private Func<LegacyAssetModel, bool> BuildAssetPredicate()
    {
        return asset =>
        {
            if (!string.Equals(SelectedAssetTypeFilter, "All", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(asset.Type, SelectedAssetTypeFilter, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            bool anyStatusFilter = ShowModifiedOnly || ShowUnmodifiedOnly || ShowUnsavedOnly || ShowAddedOnly;
            if (anyStatusFilter &&
                !((ShowModifiedOnly && asset.IsModified) ||
                  (ShowUnmodifiedOnly && asset.IsUnmodified) ||
                  (ShowUnsavedOnly && asset.IsUnsaved) ||
                  (ShowAddedOnly && asset.IsAdded)))
            {
                return false;
            }

            string filter = FilterText.Trim();
            if (!string.IsNullOrWhiteSpace(filter) &&
                !(asset.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                  asset.Type.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                  asset.FullName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                  asset.Path.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            return true;
        };
    }

    private void PopulateAvailableAssetTypes()
    {
        m_suppressImmediateFilterApply = true;
        try
        {
            SetAvailableAssetTypes(EnumerateAssetTypes(m_root));
            OnPropertyChanged(nameof(AvailableAssetTypes));
            SelectedAssetTypeFilter = "All";
        }
        finally
        {
            m_suppressImmediateFilterApply = false;
        }
    }

    private void SetAvailableAssetTypes(IEnumerable<string> assetTypes)
    {
        m_allAssetTypes.Clear();
        m_allAssetTypes.Add("All");

        foreach (string type in assetTypes)
        {
            if (!m_allAssetTypes.Any(existing => string.Equals(existing, type, StringComparison.OrdinalIgnoreCase)))
            {
                m_allAssetTypes.Add(type);
            }
        }

        ApplyAvailableAssetTypeFilter();
    }

    private void ApplyAvailableAssetTypeFilter()
    {
        string filter = (AssetTypeSearchText ?? string.Empty).Trim();
        AvailableAssetTypes = m_allAssetTypes
            .Where(type =>
                string.Equals(type, "All", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(filter) ||
                type.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        OnPropertyChanged(nameof(AvailableAssetTypes));

        if (!AvailableAssetTypes.Contains(SelectedAssetTypeFilter ?? "All"))
        {
            m_suppressAssetTypeSearchSync = true;
            try
            {
                SelectedAssetTypeFilter = "All";
            }
            finally
            {
                m_suppressAssetTypeSearchSync = false;
            }
        }
    }

    private static IEnumerable<string> EnumerateAssetTypes(LegacyFolderTreeNodeModel node)
    {
        SortedSet<string> types = new(StringComparer.OrdinalIgnoreCase) { "All" };
        CollectAssetTypes(node, types);
        return types;
    }

    private static void CollectAssetTypes(LegacyFolderTreeNodeModel node, SortedSet<string> types)
    {
        foreach (LegacyAssetModel asset in node.Assets)
        {
            if (!string.IsNullOrWhiteSpace(asset.Type))
            {
                types.Add(asset.Type);
            }
        }

        foreach (LegacyFolderTreeNodeModel child in node.Children)
        {
            CollectAssetTypes(child, types);
        }
    }

    private string? GetFolderPathText(LegacyFolderTreeNodeModel? folder)
    {
        string[]? path = GetNodePath(m_root, folder);
        if (path is null || path.Length == 0)
        {
            return null;
        }

        if (path.Length == 1 && string.Equals(path[0], "ROOT", StringComparison.OrdinalIgnoreCase))
        {
            return "ROOT";
        }

        return string.Join('/', path.SkipWhile((segment, index) => index == 0 && string.Equals(segment, "ROOT", StringComparison.OrdinalIgnoreCase)));
    }

    private static string[]? GetNodePath(LegacyFolderTreeNodeModel root, LegacyFolderTreeNodeModel? target)
    {
        if (target is null)
        {
            return null;
        }

        List<string> segments = new();
        return TryBuildPath(root, target, segments) ? segments.ToArray() : null;
    }

    private static bool TryBuildPath(LegacyFolderTreeNodeModel current, LegacyFolderTreeNodeModel target, List<string> segments)
    {
        if (ReferenceEquals(current, target))
        {
            segments.Insert(0, current.Name);
            return true;
        }

        foreach (LegacyFolderTreeNodeModel child in current.Children)
        {
            if (TryBuildPath(child, target, segments))
            {
                segments.Insert(0, current.Name);
                return true;
            }
        }

        return false;
    }

    private static LegacyFolderTreeNodeModel? FindNodeByPath(LegacyFolderTreeNodeModel root, string[]? path)
    {
        if (path is null || path.Length == 0 || !string.Equals(root.Name, path[0], StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        LegacyFolderTreeNodeModel current = root;
        for (int i = 1; i < path.Length; i++)
        {
            LegacyFolderTreeNodeModel? next = current.Children.FirstOrDefault(child =>
                string.Equals(child.Name, path[i], StringComparison.OrdinalIgnoreCase));
            if (next is null)
            {
                break;
            }

            current = next;
        }

        return current;
    }

    private static LegacyFolderTreeNodeModel FindPreferredNode(LegacyFolderTreeNodeModel root, string[]? selectedPath, bool filterActive)
    {
        LegacyFolderTreeNodeModel? requestedNode = FindNodeByPath(root, selectedPath);
        if (requestedNode is not null && (!filterActive || requestedNode.Assets.Count > 0 || requestedNode == root))
        {
            return requestedNode;
        }

        return FindFirstNodeWithAssets(root) ?? root;
    }

    private static LegacyFolderTreeNodeModel? FindFirstNodeWithAssets(LegacyFolderTreeNodeModel node)
    {
        if (node.Assets.Count > 0)
        {
            return node;
        }

        foreach (LegacyFolderTreeNodeModel child in node.Children)
        {
            LegacyFolderTreeNodeModel? match = FindFirstNodeWithAssets(child);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static bool ExpandPathToNode(LegacyFolderTreeNodeModel current, LegacyFolderTreeNodeModel target)
    {
        if (ReferenceEquals(current, target))
        {
            current.IsExpanded = true;
            return true;
        }

        foreach (LegacyFolderTreeNodeModel child in current.Children)
        {
            if (ExpandPathToNode(child, target))
            {
                current.IsExpanded = true;
                return true;
            }
        }

        return false;
    }

    private static int CountFolders(LegacyFolderTreeNodeModel node)
    {
        int count = 0;
        foreach (LegacyFolderTreeNodeModel child in node.Children)
        {
            count++;
            count += CountFolders(child);
        }

        return count;
    }

    private static Image CreateMenuIcon(string uri)
    {
        using Stream stream = AssetLoader.Open(new Uri(uri));
        return new Image
        {
            Source = new Bitmap(stream),
            Width = 16,
            Height = 16,
            VerticalAlignment = VerticalAlignment.Center
        };
    }
}
