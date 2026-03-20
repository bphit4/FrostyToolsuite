using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FrostyEditor.Models;

public partial class LegacyFolderTreeNodeModel : ObservableObject
{
    private static readonly Bitmap s_folderCollapsedIcon = LoadBitmap("avares://FrostyEditor/Assets/FolderCollapsed.png");
    private static readonly Bitmap s_folderExpandedIcon = LoadBitmap("avares://FrostyEditor/Assets/FolderExpanded.png");
    private readonly Dictionary<string, LegacyFolderTreeNodeModel> m_childrenMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<LegacyFolderTreeNodeModel> m_children = new();
    private readonly List<LegacyAssetModel> m_assets = new();
    private LegacyAssetModel[]? m_sortedAssets;

    public ObservableCollection<LegacyFolderTreeNodeModel> Children => m_children;
    public IReadOnlyList<LegacyAssetModel> Assets => m_assets;

    [ObservableProperty]
    private string m_name;

    [ObservableProperty]
    private bool m_hasChildren;

    [ObservableProperty]
    private bool m_isExpanded;

    public Bitmap FolderIcon => IsExpanded ? s_folderExpandedIcon : s_folderCollapsedIcon;

    public LegacyFolderTreeNodeModel(string inName)
    {
        Name = inName;
    }

    public static LegacyFolderTreeNodeModel Create(IEnumerable<LegacyAssetModel> assets)
    {
        LegacyFolderTreeNodeModel root = new("ROOT") { IsExpanded = true };

        foreach (LegacyAssetModel asset in assets)
        {
            string normalizedPath = asset.FullName.Replace('\\', '/');
            string[] folders = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

            LegacyFolderTreeNodeModel current = root;
            for (int i = 0; i < folders.Length - 1; i++)
            {
                string name = folders[i];
                if (!current.m_childrenMap.TryGetValue(name, out LegacyFolderTreeNodeModel? folder))
                {
                    folder = new LegacyFolderTreeNodeModel(name);
                    current.m_childrenMap.Add(name, folder);
                    current.m_children.Add(folder);
                    current.HasChildren = true;
                }

                current = folder;
            }

            current.AddAsset(asset);
        }

        return root;
    }

    public static LegacyFolderTreeNodeModel CreateFiltered(LegacyFolderTreeNodeModel source, Func<LegacyAssetModel, bool> predicate)
    {
        LegacyFolderTreeNodeModel clone = new(source.Name)
        {
            IsExpanded = source.IsExpanded
        };

        foreach (LegacyAssetModel asset in source.m_assets)
        {
            if (predicate(asset))
            {
                clone.AddAsset(asset);
            }
        }

        foreach (LegacyFolderTreeNodeModel child in source.m_children)
        {
            LegacyFolderTreeNodeModel filteredChild = CreateFiltered(child, predicate);
            if (filteredChild.m_assets.Count == 0 && filteredChild.m_children.Count == 0)
            {
                continue;
            }

            clone.m_childrenMap.Add(filteredChild.Name, filteredChild);
            clone.m_children.Add(filteredChild);
            clone.HasChildren = true;
        }

        return clone;
    }

    public IReadOnlyList<LegacyAssetModel> GetSortedAssets()
    {
        m_sortedAssets ??= m_assets.OrderBy(static x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        return m_sortedAssets;
    }

    private void AddAsset(LegacyAssetModel asset)
    {
        m_assets.Add(asset);
        m_sortedAssets = null;
    }

    public static Comparison<LegacyFolderTreeNodeModel?> SortAscending<T>(Func<LegacyFolderTreeNodeModel, T> selector)
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

    public static Comparison<LegacyFolderTreeNodeModel?> SortDescending<T>(Func<LegacyFolderTreeNodeModel, T> selector)
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
