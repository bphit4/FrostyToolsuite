using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
using Frosty.Sdk;
using Frosty.Sdk.Ebx;
using Frosty.Sdk.IO;
using Frosty.Sdk.Managers;
using Frosty.Sdk.Managers.Entries;
using Frosty.Sdk.Utils;
using FrostyEditor.Managers;
using FrostyEditor.Models;
using FrostyEditor.Utils;

namespace FrostyEditor.ViewModels;

public partial class DataExplorerViewModel : ViewModelBase
{
    private readonly MenuItem m_revertAssetMenuItem;
    private readonly FolderTreeNodeModel m_root;
    private FolderTreeNodeModel m_filteredRoot;
    private IReadOnlyList<AssetModel> m_currentAssets = Array.Empty<AssetModel>();
    private FolderTreeNodeModel? m_selectedFolderNode;
    private AssetModel? m_selectedAsset;
    private CancellationTokenSource? m_filterCts;
    private DateTime m_lastFolderTapUtc = DateTime.MinValue;
    private bool m_suppressImmediateFilterApply;

    [ObservableProperty]
    private HierarchicalTreeDataGridSource<FolderTreeNodeModel> m_folderSource;

    [ObservableProperty]
    private string m_title = "Data Explorer";

    [ObservableProperty]
    private int m_selectedExplorerTabIndex;

    [ObservableProperty]
    private string m_filterText = string.Empty;

    [ObservableProperty]
    private FlatTreeDataGridSource<AssetModel> m_assetsSource;

    [ObservableProperty]
    private MenuFlyout m_assetContextMenu;

    [ObservableProperty]
    private MenuFlyout m_folderContextMenu;

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
    private bool m_showModifiedOnly;

    [ObservableProperty]
    private bool m_showUnmodifiedOnly;

    [ObservableProperty]
    private bool m_showUnsavedOnly;

    [ObservableProperty]
    private bool m_showAddedOnly;

    public ObservableCollection<string> AvailableAssetTypes { get; } = new() { "All" };

    public LegacyExplorerViewModel LegacyExplorer { get; } = new();
    public bool IsDataExplorerSelected => SelectedExplorerTabIndex == 0;
    public bool IsLegacyExplorerSelected => SelectedExplorerTabIndex == 1;
    public EbxAssetEntry? SelectedEbxAssetEntry => m_selectedAsset?.Entry as EbxAssetEntry;

    public DataExplorerViewModel()
    {
        m_root = FolderTreeNodeModel.Create();
        m_filteredRoot = m_root;
        FolderSource = CreateFolderSource(m_filteredRoot);

        AssetsSource = new FlatTreeDataGridSource<AssetModel>(Array.Empty<AssetModel>())
        {
            Columns =
            {
                new TemplateColumn<AssetModel>(
                    "Name",
                    "AssetNameCell",
                    null,
                    new GridLength(2, GridUnitType.Star),
                    options: new()
                    {
                        CompareAscending = AssetModel.SortAscending(x => x.Name ?? string.Empty),
                        CompareDescending = AssetModel.SortDescending(x => x.Name ?? string.Empty)
                    }),
                new TextColumn<AssetModel, string>(
                    "Type",
                    x => x.Type ?? string.Empty,
                    new GridLength(1, GridUnitType.Star),
                    new TextColumnOptions<AssetModel>()
                    {
                        CompareAscending = AssetModel.SortAscending(x => x.Type ?? string.Empty),
                        CompareDescending = AssetModel.SortDescending(x => x.Type ?? string.Empty)
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
                new MenuItem { Header = "Revert Folder", Command = RevertSelectedFolderCommand, Icon = CreateMenuIcon("avares://FrostyEditor/Assets/Legacy/FrostyEditorImages/Revert.png") }
            }
        };

        m_revertAssetMenuItem = new MenuItem
        {
            Header = "Revert",
            Command = RevertAssetCommand,
            Icon = CreateMenuIcon("avares://FrostyEditor/Assets/Legacy/FrostyEditorImages/Revert.png"),
            IsVisible = false
        };

        AssetContextMenu = new MenuFlyout()
        {
            Items =
            {
                new MenuItem { Header = "Open", Command = OpenAssetCommand, Icon = CreateMenuIcon("avares://FrostyEditor/Assets/Legacy/FrostyEditorImages/OpenAsset.png") },
                new MenuItem { Header = "Export", Command = ExportAssetCommand, Icon = CreateMenuIcon("avares://FrostyEditor/Assets/Legacy/FrostyEditorImages/Export.png") },
                new MenuItem { Header = "Import", Command = ImportAssetCommand, Icon = CreateMenuIcon("avares://FrostyEditor/Assets/Legacy/FrostyEditorImages/Import.png") },
                m_revertAssetMenuItem
            }
        };
        AssetContextMenu.Opening += (_, _) => UpdateAssetContextMenuVisibility();

        FolderCount = CountFolders(m_root);
        TotalAssetCount = CountAssets(m_root);
        PopulateAvailableAssetTypes();
        m_selectedFolderNode = m_filteredRoot;
        SelectedFolderName = m_filteredRoot.Name;
        m_currentAssets = m_filteredRoot.GetSortedAssets();
        RefreshAssetList();
    }

    partial void OnSelectedExplorerTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsDataExplorerSelected));
        OnPropertyChanged(nameof(IsLegacyExplorerSelected));

        if (value == 1)
        {
            _ = LegacyExplorer.EnsureLoadedAsync();
        }
    }

    partial void OnAssetCountChanged(int value)
    {
        SelectedAssetCountLabel = $"{value} Assets";
    }

    [RelayCommand]
    private async Task ExportAsset()
    {
        if (m_selectedAsset?.Entry is not EbxAssetEntry entry)
        {
            return;
        }

        if (TextureAssetOperations.IsTextureAsset(entry))
        {
            TextureOperationResult result = await TextureAssetOperations.ExportWithPickerAsync(entry);
            LogTextureOperationResult(result);
            return;
        }

        IStorageFile? file = await FileService.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save ebx as",
            SuggestedFileName = entry.Filename,
            DefaultExtension = "dbx",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("DBX files (*.dbx)") { Patterns = new[] { "dbx" } },
                new FilePickerFileType("EBX files (*.ebx)") { Patterns = new[] { "ebx" } }
            }
        });

        if (file is null)
        {
            return;
        }

        await using Stream stream = await file.OpenWriteAsync();
        string extension = Path.GetExtension(file.Name);

        switch (extension)
        {
            case ".dbx":
            {
                EbxPartition partition = AssetManager.GetEbxPartition(entry);
                using DbxWriter writer = new(stream);
                writer.Write(partition);
                break;
            }
            case ".ebx":
            {
                using Block<byte> data = AssetManager.GetAsset(entry);
                stream.Write(data);
                break;
            }
        }
    }

    [RelayCommand]
    private void OpenAsset()
    {
        if (m_selectedAsset?.Entry is not EbxAssetEntry entry)
        {
            return;
        }

        string documentKey = AssetEditorViewModel.CreateDocumentKey(entry);
        if (App.MainViewModel?.ActivateDocumentByKey(documentKey) == true)
        {
            return;
        }

        App.MainViewModel?.AddEditor(PluginManager.GetEbxAssetEditor(entry));
    }

    [RelayCommand]
    private async Task ImportAsset()
    {
        if (m_selectedAsset?.Entry is not EbxAssetEntry entry)
        {
            return;
        }

        if (TextureAssetOperations.IsTextureAsset(entry))
        {
            TextureOperationResult result = await TextureAssetOperations.ImportWithPickerAsync(entry);
            LogTextureOperationResult(result);
            if (result.Success)
            {
                App.MainViewModel?.RefreshDocumentByKey(AssetEditorViewModel.CreateDocumentKey(entry));
                UpdateAssetContextMenuVisibility();
                RefreshAssetList();
            }
            return;
        }

        const string message = "Import is only wired for texture assets right now. Other asset writeback editors still need to be ported.";
        FrostyLogger.Logger?.LogWarning(message);
    }

    [RelayCommand]
    private Task RevertAsset()
    {
        if (m_selectedAsset?.Entry is not EbxAssetEntry entry)
        {
            return Task.CompletedTask;
        }

        if (TextureAssetOperations.IsTextureAsset(entry))
        {
            TextureOperationResult result = TextureAssetOperations.Revert(entry);
            LogTextureOperationResult(result);

            if (!result.Success)
            {
                return Task.CompletedTask;
            }
        }
        else
        {
            bool reverted = AssetManager.RevertEbx(entry.Name);
            if (!reverted)
            {
                FrostyLogger.Logger?.LogWarning($"{entry.Filename} has no pending EBX edits to revert.");
                return Task.CompletedTask;
            }

            AssetEditStateTracker.ClearAsset(entry.Name);
            InspectorEditStateTracker.ClearAsset(entry.Name);
            FrostyLogger.Logger?.LogInfo($"Reverted {entry.Filename} to the original game data.");
        }

        App.MainViewModel?.RefreshDocumentByKey(AssetEditorViewModel.CreateDocumentKey(entry));
        UpdateAssetContextMenuVisibility();
        RefreshAssetList();
        UpdateSelectedAssetDetails(m_selectedAsset);
        return Task.CompletedTask;
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

    [RelayCommand]
    private void RevertSelectedFolder()
    {
        if (m_selectedFolderNode is null)
        {
            FrostyLogger.Logger?.LogWarning("No folder is selected to revert.");
            return;
        }

        int reverted = 0;
        int skipped = 0;
        int failed = 0;
        HashSet<string> refreshedDocumentKeys = [];

        foreach (AssetModel asset in EnumerateAssets(m_selectedFolderNode))
        {
            if (asset.Entry is not EbxAssetEntry entry)
            {
                continue;
            }

            if (!AssetEditStateTracker.IsModified(entry.Name))
            {
                continue;
            }

            bool success;
            if (TextureAssetOperations.IsTextureAsset(entry))
            {
                TextureOperationResult result = TextureAssetOperations.Revert(entry);
                success = result.Success;
                if (!success)
                {
                    FrostyLogger.Logger?.LogWarning(result.Message);
                }
            }
            else
            {
                success = AssetManager.RevertEbx(entry.Name);
                if (success)
                {
                    AssetEditStateTracker.ClearAsset(entry.Name);
                    InspectorEditStateTracker.ClearAsset(entry.Name);
                }
            }

            if (success)
            {
                reverted++;
                refreshedDocumentKeys.Add(AssetEditorViewModel.CreateDocumentKey(entry));
            }
            else
            {
                failed++;
            }
        }

        foreach (string documentKey in refreshedDocumentKeys)
        {
            App.MainViewModel?.RefreshDocumentByKey(documentKey);
        }

        RefreshAssetList();
        UpdateAssetContextMenuVisibility();
        UpdateSelectedAssetDetails(m_selectedAsset);

        if (reverted == 0 && skipped == 0 && failed == 0)
        {
            FrostyLogger.Logger?.LogInfo($"No modified assets were found under folder '{m_selectedFolderNode.Name}'.");
            return;
        }

        FrostyLogger.Logger?.LogInfo(
            $"Folder revert finished for '{m_selectedFolderNode.Name}': reverted {reverted}, skipped {skipped}, failed {failed}.");
    }

    partial void OnFilterTextChanged(string value)
    {
        DebounceRebuildFilteredTree();
    }

    partial void OnSelectedAssetTypeFilterChanged(string value)
    {
        if (!m_suppressImmediateFilterApply)
        {
            RebuildFilteredTree();
        }
    }

    partial void OnShowModifiedOnlyChanged(bool value) => RebuildFilteredTree();
    partial void OnShowUnmodifiedOnlyChanged(bool value) => RebuildFilteredTree();
    partial void OnShowUnsavedOnlyChanged(bool value) => RebuildFilteredTree();
    partial void OnShowAddedOnlyChanged(bool value) => RebuildFilteredTree();

    private void OnSelectionChanged(object? sender, TreeSelectionModelSelectionChangedEventArgs<FolderTreeNodeModel> e)
    {
        FolderTreeNodeModel? folder = e.SelectedItems.Count > 0 ? e.SelectedItems[0] : null;
        if (folder is null)
        {
            return;
        }

        m_selectedFolderNode = folder;
        SelectedFolderName = folder.Name;
        m_currentAssets = folder.GetSortedAssets();
        RefreshAssetList();
    }

    private void OnAssetSelectionChanged(object? sender, TreeSelectionModelSelectionChangedEventArgs<AssetModel> e)
    {
        AssetModel? asset = e.SelectedItems.Count > 0 ? e.SelectedItems[0] : null;
        if (asset?.Entry is null)
        {
            m_selectedAsset = null;
            UpdateSelectedAssetDetails(null);
            UpdateAssetContextMenuVisibility();
            return;
        }

        m_selectedAsset = asset;
        UpdateSelectedAssetDetails(asset);
        UpdateAssetContextMenuVisibility();
    }

    public void HandleFolderDoubleTapped(FolderTreeNodeModel? clickedNode)
    {
        if (clickedNode is null)
        {
            return;
        }

        SelectFolder(clickedNode);
        clickedNode.IsExpanded = !clickedNode.IsExpanded;
    }

    public void HandleFolderTapped(FolderTreeNodeModel? clickedNode)
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

    public void HandleAssetDoubleTapped()
    {
        if (OpenAssetCommand.CanExecute(null))
        {
            OpenAssetCommand.Execute(null);
        }
    }

    public void HandleAssetTapped(AssetModel? clickedAsset)
    {
        if (clickedAsset is null)
        {
            return;
        }

        m_selectedAsset = clickedAsset;
        UpdateSelectedAssetDetails(clickedAsset);
        UpdateAssetContextMenuVisibility();
    }

    [RelayCommand]
    private void ShowDataExplorerTab()
    {
        SelectedExplorerTabIndex = 0;
    }

    [RelayCommand]
    private void ShowLegacyExplorerTab()
    {
        SelectedExplorerTabIndex = 1;
    }

    private async void DebounceRebuildFilteredTree()
    {
        m_filterCts?.Cancel();
        CancellationTokenSource cts = new();
        m_filterCts = cts;

        try
        {
            await Task.Delay(120, cts.Token);
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

    private HierarchicalTreeDataGridSource<FolderTreeNodeModel> CreateFolderSource()
    {
        return CreateFolderSource(m_filteredRoot);
    }

    private HierarchicalTreeDataGridSource<FolderTreeNodeModel> CreateFolderSource(FolderTreeNodeModel root)
    {
        HierarchicalTreeDataGridSource<FolderTreeNodeModel> source = new(root)
        {
            Columns =
            {
                new HierarchicalExpanderColumn<FolderTreeNodeModel>(
                    new TemplateColumn<FolderTreeNodeModel>(
                        "Name",
                        "FolderNameCell",
                        null,
                        new GridLength(1, GridUnitType.Star),
                        options: new()
                        {
                            CanUserResizeColumn = false,
                            CanUserSortColumn = false,
                            CompareAscending = FolderTreeNodeModel.SortAscending(x => x.Name),
                            CompareDescending = FolderTreeNodeModel.SortDescending(x => x.Name)
                        }),
                    x => x.Children,
                    x => x.HasChildren,
                    x => x.IsExpanded),
            }
        };

        source.RowSelection!.SelectionChanged += OnSelectionChanged;
        source.Sort(FolderTreeNodeModel.SortAscending(x => x.Name));
        return source;
    }

    private void SelectFolder(FolderTreeNodeModel node)
    {
        m_selectedFolderNode = node;
        SelectedFolderName = node.Name;
        m_currentAssets = node.GetSortedAssets();
        RefreshAssetList();
    }

    private void RefreshAssetList()
    {
        Func<AssetModel, bool> predicate = BuildAssetPredicate();
        AssetModel[] items = m_currentAssets.Where(predicate).ToArray();
        AssetsSource.Items = items;
        AssetCount = items.Length;
    }

    private void UpdateAssetContextMenuVisibility()
    {
        bool canRevert = false;
        if (m_selectedAsset?.Entry is EbxAssetEntry entry)
        {
            canRevert = TextureAssetOperations.IsTextureAsset(entry)
                ? TextureAssetOperations.IsModified(entry)
                : AssetManager.IsEbxModified(entry.Name);
        }
        m_revertAssetMenuItem.IsVisible = canRevert;
    }

    private void UpdateSelectedAssetDetails(AssetModel? asset)
    {
        if (asset?.Entry is null)
        {
            SelectedAssetName = "Nothing selected";
            SelectedAssetType = "Type: N/A";
            SelectedAssetPath = "Path: N/A";
            return;
        }

        SelectedAssetName = asset.Entry.Filename;
        SelectedAssetType = $"Type: {asset.Entry.Type}";
        SelectedAssetPath = $"Path: {asset.Entry.Path}";
    }

    public void RefreshExplorerState(bool rebuildTree = true)
    {
        if (rebuildTree)
        {
            RebuildFilteredTree();
        }
        else
        {
            RefreshAssetList();
        }

        UpdateAssetContextMenuVisibility();
        UpdateSelectedAssetDetails(m_selectedAsset);
    }

    public void RevealAsset(EbxAssetEntry entry, bool openAsset = false)
    {
        ShowDataExplorerTab();

        FilterText = string.Empty;
        SelectedAssetTypeFilter = "All";
        ShowModifiedOnly = false;
        ShowUnmodifiedOnly = false;
        ShowUnsavedOnly = false;
        ShowAddedOnly = false;
        RebuildFilteredTree();

        FolderTreeNodeModel? folder = FindFolderByPath(m_filteredRoot, entry.Path);
        if (folder is null)
        {
            return;
        }

        SelectFolder(folder);
        AssetModel? asset = m_currentAssets.FirstOrDefault(model => string.Equals(model.Entry?.Name, entry.Name, StringComparison.OrdinalIgnoreCase));
        if (asset is null)
        {
            return;
        }

        HandleAssetTapped(asset);
        if (openAsset)
        {
            OpenAsset();
        }
    }

    private static void LogTextureOperationResult(TextureOperationResult result)
    {
        if (result.Success)
        {
            FrostyLogger.Logger?.LogInfo(result.Message);
        }
        else
        {
            FrostyLogger.Logger?.LogWarning(result.Message);
        }
    }

    private static IEnumerable<AssetModel> EnumerateAssets(FolderTreeNodeModel folder)
    {
        foreach (AssetModel asset in folder.Assets)
        {
            yield return asset;
        }

        foreach (FolderTreeNodeModel child in folder.Children)
        {
            foreach (AssetModel asset in EnumerateAssets(child))
            {
                yield return asset;
            }
        }
    }

    private void RebuildFilteredTree()
    {
        string[]? selectedPath = GetNodePath(m_filteredRoot, m_selectedFolderNode);
        Func<AssetModel, bool> predicate = BuildAssetPredicate();

        bool filterActive = IsFilterActive();

        m_filteredRoot = filterActive
            ? FolderTreeNodeModel.CreateFiltered(m_root, predicate)
            : m_root;
        m_selectedFolderNode = FindPreferredNode(m_filteredRoot, selectedPath, filterActive);
        ExpandPathToNode(m_filteredRoot, m_selectedFolderNode);
        FolderSource = CreateFolderSource(m_filteredRoot);
        SelectedFolderName = m_selectedFolderNode.Name;
        m_currentAssets = m_selectedFolderNode.GetSortedAssets();
        RefreshAssetList();
    }

    private static FolderTreeNodeModel? FindFolderByPath(FolderTreeNodeModel root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return root;
        }

        FolderTreeNodeModel current = root;
        foreach (string segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            FolderTreeNodeModel? child = current.Children.FirstOrDefault(node => string.Equals(node.Name, segment, StringComparison.OrdinalIgnoreCase));
            if (child is null)
            {
                return null;
            }

            current = child;
        }

        return current;
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

    private Func<AssetModel, bool> BuildAssetPredicate()
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
                !(asset.Name?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true ||
                  asset.Type?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true ||
                  asset.Entry?.Path.Contains(filter, StringComparison.OrdinalIgnoreCase) == true))
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
            AvailableAssetTypes.Clear();
            AvailableAssetTypes.Add("All");

            foreach (string type in EnumerateAssetTypes(m_root))
            {
                AvailableAssetTypes.Add(type);
            }

            SelectedAssetTypeFilter = "All";
        }
        finally
        {
            m_suppressImmediateFilterApply = false;
        }
    }

    private static IEnumerable<string> EnumerateAssetTypes(FolderTreeNodeModel node)
    {
        SortedSet<string> types = new(StringComparer.OrdinalIgnoreCase);
        CollectAssetTypes(node, types);
        return types;
    }

    private static void CollectAssetTypes(FolderTreeNodeModel node, SortedSet<string> types)
    {
        foreach (AssetModel asset in node.Assets)
        {
            if (!string.IsNullOrWhiteSpace(asset.Type))
            {
                types.Add(asset.Type);
            }
        }

        foreach (FolderTreeNodeModel child in node.Children)
        {
            CollectAssetTypes(child, types);
        }
    }

    private static string[]? GetNodePath(FolderTreeNodeModel root, FolderTreeNodeModel? target)
    {
        if (target is null)
        {
            return null;
        }

        List<string> segments = new();
        return TryBuildPath(root, target, segments) ? segments.ToArray() : null;
    }

    private static bool TryBuildPath(FolderTreeNodeModel current, FolderTreeNodeModel target, List<string> segments)
    {
        if (ReferenceEquals(current, target))
        {
            segments.Insert(0, current.Name);
            return true;
        }

        foreach (FolderTreeNodeModel child in current.Children)
        {
            if (TryBuildPath(child, target, segments))
            {
                segments.Insert(0, current.Name);
                return true;
            }
        }

        return false;
    }

    private static FolderTreeNodeModel? FindNodeByPath(FolderTreeNodeModel root, string[]? path)
    {
        if (path is null || path.Length == 0 || !string.Equals(root.Name, path[0], StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        FolderTreeNodeModel current = root;
        for (int i = 1; i < path.Length; i++)
        {
            FolderTreeNodeModel? next = current.Children.FirstOrDefault(child =>
                string.Equals(child.Name, path[i], StringComparison.OrdinalIgnoreCase));
            if (next is null)
            {
                break;
            }

            current = next;
        }

        return current;
    }

    private static FolderTreeNodeModel FindPreferredNode(FolderTreeNodeModel root, string[]? selectedPath, bool filterActive)
    {
        FolderTreeNodeModel? requestedNode = FindNodeByPath(root, selectedPath);
        if (requestedNode is not null && (!filterActive || requestedNode.Assets.Count > 0 || requestedNode == root))
        {
            return requestedNode;
        }

        return FindFirstNodeWithAssets(root) ?? root;
    }

    private static FolderTreeNodeModel? FindFirstNodeWithAssets(FolderTreeNodeModel node)
    {
        if (node.Assets.Count > 0)
        {
            return node;
        }

        foreach (FolderTreeNodeModel child in node.Children)
        {
            FolderTreeNodeModel? match = FindFirstNodeWithAssets(child);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static bool ExpandPathToNode(FolderTreeNodeModel current, FolderTreeNodeModel target)
    {
        if (ReferenceEquals(current, target))
        {
            current.IsExpanded = true;
            return true;
        }

        foreach (FolderTreeNodeModel child in current.Children)
        {
            if (ExpandPathToNode(child, target))
            {
                current.IsExpanded = true;
                return true;
            }
        }

        return false;
    }

    private static int CountFolders(FolderTreeNodeModel node)
    {
        int count = 0;
        foreach (FolderTreeNodeModel child in node.Children)
        {
            count++;
            count += CountFolders(child);
        }

        return count;
    }

    private static int CountAssets(FolderTreeNodeModel node)
    {
        int count = node.Assets.Count;
        foreach (FolderTreeNodeModel child in node.Children)
        {
            count += CountAssets(child);
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




