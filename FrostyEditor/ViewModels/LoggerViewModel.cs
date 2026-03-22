using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Frosty.Sdk.Interfaces;
using Frosty.Sdk.Managers;
using Frosty.Sdk.Managers.Entries;
using FrostyEditor.Managers;
using Frosty.Sdk.Managers.Infos;
using FrostyEditor.Models;
using FrostyEditor.Utils;

namespace FrostyEditor.ViewModels;

public partial class LoggerViewModel : ViewModelBase, ILogger
{
    private static readonly string s_info = "INFO";
    private static readonly string s_warn = "WARN";
    private static readonly string s_error = "ERROR";
    private static readonly object s_referenceIndexSync = new();
    private static Task<Dictionary<Guid, List<EbxAssetEntry>>>? s_reverseReferenceIndexTask;
    private const string c_assetBookmarksContext = "Asset Bookmarks";
    private AssetEntry? m_explorerSelectedAsset;
    private AssetEntry? m_activeDocumentAsset;
    private AssetEntry? m_contextAsset;
    private CancellationTokenSource? m_contextLoadCts;

    [ObservableProperty]
    private string? m_text;

    [ObservableProperty]
    private string? m_lastEntry = "Waiting for editor activity.";

    [ObservableProperty]
    private int m_entryCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercentText))]
    private bool m_hasProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercentText))]
    private double m_progressValue;

    [ObservableProperty]
    private int m_selectedTabIndex;

    [ObservableProperty]
    private string m_contextAssetName = "No asset selected";

    [ObservableProperty]
    private string m_bundlesTitle = "Bundles";

    [ObservableProperty]
    private string m_bundlesText = "Select an asset to inspect its bundle membership.";

    [ObservableProperty]
    private string m_referencesLeftTitle = "References to asset";

    [ObservableProperty]
    private string m_referencesRightTitle = "References from asset";

    [ObservableProperty]
    private string m_referencesStatus = "Select an EBX asset to inspect references.";

    [ObservableProperty]
    private bool m_isReferencesLoading;

    [ObservableProperty]
    private string m_selectedBookmarkContext = c_assetBookmarksContext;

    [ObservableProperty]
    private BookmarkNodeModel? m_selectedBookmark;

    [ObservableProperty]
    private string m_bookmarkFilterText = string.Empty;

    public ObservableCollection<string> Lines { get; } = [];
    public ObservableCollection<LoggerReferenceItem> IncomingReferences { get; } = [];
    public ObservableCollection<LoggerReferenceItem> OutgoingReferences { get; } = [];
    public ObservableCollection<string> BookmarkContexts { get; } = [c_assetBookmarksContext];
    public ObservableCollection<BookmarkNodeModel> BookmarkRoots { get; } = [];
    public ObservableCollection<BookmarkNodeModel> FilteredBookmarkRoots { get; } = [];
    public string ProgressPercentText => HasProgress ? $"{ProgressValue:P0}" : "Working...";
    public bool HasContextAsset => m_contextAsset is not null;
    public bool HasBundles => !string.IsNullOrWhiteSpace(BundlesText);
    public bool HasReferenceAsset => m_contextAsset is EbxAssetEntry;
    public bool IsLogTabSelected => SelectedTabIndex == (int)LoggerTabSelectionContext.Log;
    public bool IsBookmarksTabSelected => SelectedTabIndex == (int)LoggerTabSelectionContext.Bookmarks;
    public bool IsBundlesTabSelected => SelectedTabIndex == (int)LoggerTabSelectionContext.Bundles;
    public bool IsReferencesTabSelected => SelectedTabIndex == (int)LoggerTabSelectionContext.References;
    public bool ShowReferencesStatus => IsReferencesLoading || m_contextAsset is null || m_contextAsset is not EbxAssetEntry;
    public bool CanAddBookmark => m_contextAsset is not null;
    public bool HasSelectedBookmark => SelectedBookmark is not null;
    public bool CanOpenSelectedBookmark => SelectedBookmark?.CanOpenAsset == true;
    public bool CanNavigateSelectedBookmark => SelectedBookmark?.CanNavigateToAsset == true;
    public string BookmarkFilterLabel => string.IsNullOrWhiteSpace(BookmarkFilterText) ? "(no filter)" : $"({BookmarkFilterText})";

    public LoggerViewModel()
    {
        LoadBookmarks();
    }

    public void LogInfo(string message)
    {
        Append(s_info, message);
    }

    public void LogWarning(string message)
    {
        Append(s_warn, message);
    }

    public void LogError(string message)
    {
        Append(s_error, message);
    }

    public void LogProgress(double progress)
    {
        ProgressValue = Math.Clamp(progress, 0.0, 1.0);
        HasProgress = true;
        LastEntry = $"PROGRESS - {ProgressValue:P0}";
    }

    public void SetExplorerSelectedAsset(AssetEntry? entry)
    {
        m_explorerSelectedAsset = entry;
        RefreshAssetContext();
    }

    public void SetActiveDocumentAsset(AssetEntry? entry)
    {
        m_activeDocumentAsset = entry;
        RefreshAssetContext();
    }

    partial void OnSelectedTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsLogTabSelected));
        OnPropertyChanged(nameof(IsBookmarksTabSelected));
        OnPropertyChanged(nameof(IsBundlesTabSelected));
        OnPropertyChanged(nameof(IsReferencesTabSelected));
    }

    partial void OnIsReferencesLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowReferencesStatus));
    }

    [RelayCommand]
    private void ShowLogTab()
    {
        SelectedTabIndex = (int)LoggerTabSelectionContext.Log;
    }

    [RelayCommand]
    private void ShowBundlesTab()
    {
        SelectedTabIndex = (int)LoggerTabSelectionContext.Bundles;
    }

    [RelayCommand]
    private void ShowBookmarksTab()
    {
        SelectedTabIndex = (int)LoggerTabSelectionContext.Bookmarks;
    }

    [RelayCommand]
    private void ShowReferencesTab()
    {
        SelectedTabIndex = (int)LoggerTabSelectionContext.References;
    }

    [RelayCommand]
    private void OpenReference(LoggerReferenceItem? item)
    {
        if (item?.Entry is null)
        {
            return;
        }

        App.MainViewModel?.DataExplorer.RevealAsset(item.Entry, openAsset: true);
    }

    [RelayCommand]
    private void FindReference(LoggerReferenceItem? item)
    {
        if (item?.Entry is null)
        {
            return;
        }

        App.MainViewModel?.DataExplorer.RevealAsset(item.Entry, openAsset: false);
    }

    [RelayCommand]
    private async Task CopyReferenceAssetNameAsync(LoggerReferenceItem? item)
    {
        string? name = item?.Entry?.Filename;
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (!await ClipboardService.SetTextAsync(name))
        {
            LogWarning("Unable to copy asset name because the clipboard is unavailable.");
        }
    }

    [RelayCommand]
    private async Task CopyReferenceAssetPathAsync(LoggerReferenceItem? item)
    {
        string? path = item?.Entry?.Name;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!await ClipboardService.SetTextAsync(path))
        {
            LogWarning("Unable to copy asset path because the clipboard is unavailable.");
        }
    }

    partial void OnSelectedBookmarkChanged(BookmarkNodeModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedBookmark));
        OnPropertyChanged(nameof(CanOpenSelectedBookmark));
        OnPropertyChanged(nameof(CanNavigateSelectedBookmark));
    }

    partial void OnBookmarkFilterTextChanged(string value)
    {
        OnPropertyChanged(nameof(BookmarkFilterLabel));
        RefreshFilteredBookmarks();
    }

    [RelayCommand]
    private async Task AddBookmarkAsync()
    {
        if (m_contextAsset is null)
        {
            return;
        }

        string defaultName = m_contextAsset.Filename;
        string? requestedName = await TextPromptDialog.ShowAsync("Add Bookmark", "Bookmark name", defaultName);
        if (requestedName is null)
        {
            return;
        }

        BookmarkNodeModel node = BookmarkNodeModel.CreateAsset(m_contextAsset, requestedName);
        BookmarkNodeModel? parent = GetBookmarkInsertionParent();
        if (parent is not null)
        {
            parent.AddChild(node);
        }
        else
        {
            BookmarkRoots.Add(node);
        }
        PersistBookmarks();
        RefreshFilteredBookmarks();
        SelectedBookmark = node;
    }

    [RelayCommand]
    private async Task CreateBookmarkFolderAsync()
    {
        string? folderName = await TextPromptDialog.ShowAsync("Create Folder", "Folder name", "New Folder");
        if (folderName is null)
        {
            return;
        }

        BookmarkNodeModel folder = BookmarkNodeModel.CreateFolder(folderName);
        folder.IsExpanded = true;
        BookmarkNodeModel? parent = GetBookmarkInsertionParent();
        if (parent is not null)
        {
            parent.AddChild(folder);
        }
        else
        {
            BookmarkRoots.Add(folder);
        }
        PersistBookmarks();
        RefreshFilteredBookmarks();
        SelectedBookmark = folder;
    }

    [RelayCommand]
    private async Task RenameBookmarkAsync()
    {
        if (SelectedBookmark is null)
        {
            return;
        }

        string? newName = await TextPromptDialog.ShowAsync("Edit Bookmark Name", "Name", SelectedBookmark.DisplayName);
        if (newName is null)
        {
            return;
        }

        SelectedBookmark.Name = newName;
        PersistBookmarks();
        RefreshFilteredBookmarks();
    }

    [RelayCommand]
    private void DeleteBookmark()
    {
        if (SelectedBookmark is null)
        {
            return;
        }

        BookmarkNodeModel? nextSelection = SelectedBookmark.Parent;
        if (SelectedBookmark.Parent is not null)
        {
            SelectedBookmark.Parent.RemoveChild(SelectedBookmark);
        }
        else
        {
            BookmarkRoots.Remove(SelectedBookmark);
        }

        PersistBookmarks();
        RefreshFilteredBookmarks();
        SelectedBookmark = nextSelection;
    }

    [RelayCommand]
    private void OpenBookmark()
    {
        NavigateBookmark(openAsset: true);
    }

    [RelayCommand]
    private void FindBookmark()
    {
        NavigateBookmark(openAsset: false);
    }

    [RelayCommand]
    private async Task CopyBookmarkAssetNameAsync()
    {
        string? name = SelectedBookmark?.ResolveEntry()?.Filename;
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (!await ClipboardService.SetTextAsync(name))
        {
            LogWarning("Unable to copy asset name because the clipboard is unavailable.");
        }
    }

    [RelayCommand]
    private async Task CopyBookmarkAssetPathAsync()
    {
        string? path = SelectedBookmark?.GetResolvedPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!await ClipboardService.SetTextAsync(path))
        {
            LogWarning("Unable to copy asset path because the clipboard is unavailable.");
        }
    }

    private void Append(string level, string message)
    {
        HasProgress = false;
        string line = $"{DateTime.Now:HH:mm:ss}  {level}  {message}";
        Text = string.IsNullOrWhiteSpace(Text) ? line : $"{Text}{Environment.NewLine}{line}";
        Lines.Add(line);
        LastEntry = line;
        EntryCount++;
    }

    private void RefreshAssetContext()
    {
        AssetEntry? nextAsset = m_explorerSelectedAsset ?? m_activeDocumentAsset;
        if (ReferenceEquals(m_contextAsset, nextAsset))
        {
            return;
        }

        m_contextAsset = nextAsset;
        OnPropertyChanged(nameof(HasContextAsset));
        OnPropertyChanged(nameof(HasReferenceAsset));
        OnPropertyChanged(nameof(ShowReferencesStatus));

        m_contextLoadCts?.Cancel();
        m_contextLoadCts = new CancellationTokenSource();
        CancellationToken token = m_contextLoadCts.Token;

        UpdateBundles(nextAsset);
        _ = RefreshReferencesAsync(nextAsset, token);
    }

    private void UpdateBundles(AssetEntry? asset)
    {
        if (asset is null)
        {
            ContextAssetName = "No asset selected";
            BundlesTitle = "Bundles";
            BundlesText = "Select an asset to inspect its bundle membership.";
            ReferencesLeftTitle = "References to asset";
            ReferencesRightTitle = "References from asset";
            return;
        }

        ContextAssetName = asset.Filename;
        BundlesTitle = $"Bundles for {asset.Filename}";
        ReferencesLeftTitle = $"References to {asset.Filename}";
        ReferencesRightTitle = $"References from {asset.Filename}";

        List<string> lines = asset.EnumerateBundles()
            .Select(bundleId =>
            {
                BundleInfo? bundle = AssetManager.GetBundleInfo(bundleId);
                return bundle is null
                    ? $"<unknown bundle 0x{bundleId:X8}>"
                    : $"{bundle.Parent.Name}/{bundle.Name}";
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        BundlesText = lines.Count == 0
            ? $"No bundles were found for {asset.Filename}."
            : string.Join(Environment.NewLine, lines);
    }

    private async Task RefreshReferencesAsync(AssetEntry? asset, CancellationToken token)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IncomingReferences.Clear();
            OutgoingReferences.Clear();
        });

        if (asset is not EbxAssetEntry ebxEntry)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsReferencesLoading = false;
                ReferencesStatus = asset is null
                    ? "Select an EBX asset to inspect references."
                    : "References are only available for EBX assets.";
                OnPropertyChanged(nameof(ShowReferencesStatus));
            });
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsReferencesLoading = true;
            ReferencesStatus = $"Loading references for {ebxEntry.Filename}...";
        });

        List<LoggerReferenceItem> outgoing = ebxEntry.EnumerateDependencies()
            .Select(AssetManager.GetEbxAssetEntry)
            .Where(static entry => entry is not null)
            .Select(entry => new LoggerReferenceItem { Entry = entry! })
            .OrderBy(static item => item.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Dictionary<Guid, List<EbxAssetEntry>> reverseIndex = await EnsureReverseReferenceIndexAsync(token).ConfigureAwait(false);
        if (token.IsCancellationRequested)
        {
            return;
        }

        List<LoggerReferenceItem> incoming = reverseIndex.TryGetValue(ebxEntry.Guid, out List<EbxAssetEntry>? referringAssets)
            ? referringAssets
                .Distinct()
                .Select(entry => new LoggerReferenceItem { Entry = entry })
                .OrderBy(static item => item.FullName, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            ReplaceCollection(IncomingReferences, incoming);
            ReplaceCollection(OutgoingReferences, outgoing);
            IsReferencesLoading = false;
            ReferencesStatus = $"References for {ebxEntry.Filename}";
            OnPropertyChanged(nameof(ShowReferencesStatus));
        });
    }

    private static Task<Dictionary<Guid, List<EbxAssetEntry>>> EnsureReverseReferenceIndexAsync(CancellationToken token)
    {
        lock (s_referenceIndexSync)
        {
            if (s_reverseReferenceIndexTask is null || s_reverseReferenceIndexTask.IsCanceled || s_reverseReferenceIndexTask.IsFaulted)
            {
                s_reverseReferenceIndexTask = Task.Run(() =>
                {
                    Dictionary<Guid, List<EbxAssetEntry>> reverse = new();
                    foreach (EbxAssetEntry source in AssetManager.EnumerateEbxAssetEntries())
                    {
                        foreach (Guid dependency in source.EnumerateDependencies())
                        {
                            if (!reverse.TryGetValue(dependency, out List<EbxAssetEntry>? dependents))
                            {
                                dependents = [];
                                reverse.Add(dependency, dependents);
                            }

                            dependents.Add(source);
                        }
                    }

                    return reverse;
                });
            }
        }

        return s_reverseReferenceIndexTask;
    }

    private void LoadBookmarks()
    {
        BookmarkRoots.Clear();
        foreach (BookmarkNodeModel item in BookmarkPersistenceManager.LoadBookmarks())
        {
            BookmarkRoots.Add(item);
        }

        RefreshFilteredBookmarks();
    }

    private void PersistBookmarks()
    {
        BookmarkPersistenceManager.SaveBookmarks(BookmarkRoots);
    }

    private void RefreshFilteredBookmarks()
    {
        FilteredBookmarkRoots.Clear();
        foreach (BookmarkNodeModel item in FilterBookmarks())
        {
            FilteredBookmarkRoots.Add(item);
        }
    }

    private IEnumerable<BookmarkNodeModel> FilterBookmarks()
    {
        if (string.IsNullOrWhiteSpace(BookmarkFilterText))
        {
            foreach (BookmarkNodeModel item in BookmarkRoots)
            {
                yield return item;
            }

            yield break;
        }

        foreach (BookmarkNodeModel item in BookmarkRoots)
        {
            if (item.MatchesFilter(BookmarkFilterText.Trim(), out BookmarkNodeModel? filtered))
            {
                yield return filtered!;
            }
        }
    }

    private BookmarkNodeModel? GetBookmarkInsertionParent()
    {
        if (SelectedBookmark?.IsFolder == true)
        {
            return SelectedBookmark;
        }

        return SelectedBookmark?.Parent;
    }

    private void NavigateBookmark(bool openAsset)
    {
        AssetEntry? entry = SelectedBookmark?.ResolveEntry();
        if (entry is null)
        {
            return;
        }

        App.MainViewModel?.DataExplorer.RevealAsset(entry, openAsset);
    }

    public void SelectBookmarkById(Guid bookmarkId)
    {
        SelectedBookmark = FindBookmarkById(BookmarkRoots, bookmarkId);
    }

    public void HandleBookmarkDoubleTapped(Guid bookmarkId)
    {
        SelectBookmarkById(bookmarkId);

        if (SelectedBookmark?.IsFolder == true)
        {
            SelectedBookmark.IsExpanded = !SelectedBookmark.IsExpanded;
            return;
        }

        NavigateBookmark(openAsset: true);
    }

    private static BookmarkNodeModel? FindBookmarkById(IEnumerable<BookmarkNodeModel> nodes, Guid bookmarkId)
    {
        foreach (BookmarkNodeModel node in nodes)
        {
            if (node.Id == bookmarkId)
            {
                return node;
            }

            BookmarkNodeModel? child = FindBookmarkById(node.Children, bookmarkId);
            if (child is not null)
            {
                return child;
            }
        }

        return null;
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> collection, IEnumerable<T> items)
    {
        collection.Clear();
        foreach (T item in items)
        {
            collection.Add(item);
        }
    }
}
