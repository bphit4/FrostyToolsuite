using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using Frosty.Sdk.Managers;
using Frosty.Sdk.Managers.Entries;

namespace FrostyEditor.Models;

public partial class FolderTreeNodeModel : ObservableObject
{
    private static readonly Bitmap s_folderCollapsedIcon = LoadBitmap("avares://FrostyEditor/Assets/FolderCollapsed.png");
    private static readonly Bitmap s_folderExpandedIcon = LoadBitmap("avares://FrostyEditor/Assets/FolderExpanded.png");
    private readonly Dictionary<string, FolderTreeNodeModel> m_childrenMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<FolderTreeNodeModel> m_children = new();
    private readonly List<AssetModel> m_assets = new();
    private AssetModel[]? m_sortedAssets;

    public ObservableCollection<FolderTreeNodeModel> Children => m_children;
    public IReadOnlyList<AssetModel> Assets => m_assets;

    [ObservableProperty]
    private string m_name;

    [ObservableProperty]
    private bool m_hasChildren;

    [ObservableProperty]
    private bool m_isExpanded;

    public Bitmap FolderIcon => IsExpanded ? s_folderExpandedIcon : s_folderCollapsedIcon;
    public FolderTreeNodeModel? Parent { get; private set; }

    public FolderTreeNodeModel(string inName)
    {
        Name = inName;
    }

    public static FolderTreeNodeModel Create()
    {
        FolderTreeNodeModel root = new("ROOT") { IsExpanded = true };

        foreach (EbxAssetEntry entry in AssetManager.EnumerateEbxAssetEntries())
        {
            string path = entry.Name;
            string[] folders = path.Split('/');

            FolderTreeNodeModel current = root;
            for (int i = 0; i < folders.Length - 1; i++)
            {
                string name = folders[i];
                if (!current.m_childrenMap.TryGetValue(name, out FolderTreeNodeModel? folder))
                {
                    folder = new FolderTreeNodeModel(name);
                    folder.Parent = current;
                    current.m_childrenMap.Add(name, folder);
                    current.m_children.Add(folder);
                    current.HasChildren = true;
                }

                current = folder;
            }

            current.AddAsset(new AssetModel(entry));
        }

        return root;
    }

    public static FolderTreeNodeModel CreateFiltered(FolderTreeNodeModel source, Func<AssetModel, bool> predicate)
    {
        FolderTreeNodeModel clone = new(source.Name)
        {
            IsExpanded = source.IsExpanded
        };

        foreach (AssetModel asset in source.m_assets)
        {
            if (predicate(asset))
            {
                clone.AddAsset(asset);
            }
        }

        foreach (FolderTreeNodeModel child in source.m_children)
        {
            FolderTreeNodeModel filteredChild = CreateFiltered(child, predicate);
            if (filteredChild.m_assets.Count == 0 && filteredChild.m_children.Count == 0)
            {
                continue;
            }

            filteredChild.Parent = clone;
            clone.m_childrenMap.Add(filteredChild.Name, filteredChild);
            clone.m_children.Add(filteredChild);
            clone.HasChildren = true;
        }

        return clone;
    }

    public IReadOnlyList<AssetModel> GetSortedAssets()
    {
        m_sortedAssets ??= m_assets.OrderBy(static x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        return m_sortedAssets;
    }

    private void AddAsset(AssetModel asset)
    {
        m_assets.Add(asset);
        m_sortedAssets = null;
    }

    public static Comparison<FolderTreeNodeModel?> SortAscending<T>(Func<FolderTreeNodeModel, T> selector)
    {
        return (x, y) =>
        {
            if (x is null && y is null)
                return 0;
            if (x is null)
                return -1;
            if (y is null)
                return 1;

            return Comparer<T>.Default.Compare(selector(x), selector(y));
        };
    }

    public static Comparison<FolderTreeNodeModel?> SortDescending<T>(Func<FolderTreeNodeModel, T> selector)
    {
        return (x, y) =>
        {
            if (x is null && y is null)
                return 0;
            if (x is null)
                return 1;
            if (y is null)
                return -1;
            return Comparer<T>.Default.Compare(selector(y), selector(x));
        };
    }

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(FolderIcon));
    }

    private static Bitmap LoadBitmap(string uri)
    {
        using Stream stream = AssetLoader.Open(new Uri(uri));
        return new Bitmap(stream);
    }
}
