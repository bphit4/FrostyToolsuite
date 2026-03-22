using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Frosty.Sdk.Managers;
using Frosty.Sdk.Managers.Entries;
using FrostyEditor.Managers;

namespace FrostyEditor.Models;

public partial class BookmarkNodeModel : ObservableObject
{
    [ObservableProperty]
    private string m_name = string.Empty;

    [ObservableProperty]
    private bool m_isExpanded;

    public Guid Id { get; init; } = Guid.NewGuid();
    public bool IsFolder { get; init; }
    public string? AssetKind { get; init; }
    public string? AssetKey { get; init; }
    public string? AssetPath { get; init; }
    public string? AssetTypeHint { get; init; }
    public ObservableCollection<BookmarkNodeModel> Children { get; } = [];
    public BookmarkNodeModel? Parent { get; private set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? GetFallbackName()
        : Name;

    public string SearchText => string.Join(" ", new[]
    {
        DisplayName,
        AssetPath ?? string.Empty,
        AssetTypeHint ?? string.Empty
    }.Where(static value => !string.IsNullOrWhiteSpace(value)));

    public Bitmap Icon => IsFolder
        ? AssetIconRegistry.GetLegacyIcon("avares://FrostyEditor/Assets/Legacy/FrostyEditorImages/OpenFolder.png")
        : AssetIconRegistry.GetIcon(GetResolvedType());

    public AssetEntry? ResolveEntry()
    {
        return AssetKind?.ToLowerInvariant() switch
        {
            "ebx" when !string.IsNullOrWhiteSpace(AssetKey) => AssetManager.GetEbxAssetEntry(AssetKey),
            "res" when !string.IsNullOrWhiteSpace(AssetKey) => AssetManager.GetResAssetEntry(AssetKey),
            "chunk" when Guid.TryParse(AssetKey, out Guid chunkId) => AssetManager.GetChunkAssetEntry(chunkId),
            _ => null
        };
    }

    public string? GetResolvedPath()
    {
        return ResolveEntry()?.Name ?? AssetPath;
    }

    public string GetResolvedType()
    {
        AssetEntry? entry = ResolveEntry();
        return entry?.Type ?? AssetTypeHint ?? "Unknown";
    }

    public bool CanOpenAsset => ResolveEntry() is EbxAssetEntry;
    public bool CanNavigateToAsset => !IsFolder && ResolveEntry() is not null;

    public void AddChild(BookmarkNodeModel child)
    {
        child.Parent = this;
        Children.Add(child);
    }

    public void InsertChild(int index, BookmarkNodeModel child)
    {
        child.Parent = this;
        Children.Insert(index, child);
    }

    public void RemoveChild(BookmarkNodeModel child)
    {
        if (Children.Remove(child))
        {
            child.Parent = null;
        }
    }

    public BookmarkNodeModel CloneWithFilteredChildren(string filter)
    {
        BookmarkNodeModel clone = new()
        {
            Id = Id,
            Name = Name,
            IsFolder = IsFolder,
            AssetKind = AssetKind,
            AssetKey = AssetKey,
            AssetPath = AssetPath,
            AssetTypeHint = AssetTypeHint,
            IsExpanded = true
        };

        foreach (BookmarkNodeModel child in Children)
        {
            if (child.MatchesFilter(filter, out BookmarkNodeModel? filteredChild))
            {
                clone.AddChild(filteredChild!);
            }
        }

        return clone;
    }

    public bool MatchesFilter(string filter, out BookmarkNodeModel? filteredNode)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            filteredNode = this;
            return true;
        }

        bool directMatch = SearchText.Contains(filter, StringComparison.OrdinalIgnoreCase);
        BookmarkNodeModel clone = CloneWithFilteredChildren(filter);
        if (directMatch || clone.Children.Count > 0)
        {
            filteredNode = directMatch && clone.Children.Count == 0 ? CloneShallow(expanded: IsExpanded) : clone;
            return true;
        }

        filteredNode = null;
        return false;
    }

    public BookmarkNodeModel CloneShallow(bool expanded)
    {
        return new BookmarkNodeModel
        {
            Id = Id,
            Name = Name,
            IsFolder = IsFolder,
            AssetKind = AssetKind,
            AssetKey = AssetKey,
            AssetPath = AssetPath,
            AssetTypeHint = AssetTypeHint,
            IsExpanded = expanded
        };
    }

    public static BookmarkNodeModel CreateFolder(string name)
    {
        return new BookmarkNodeModel
        {
            IsFolder = true,
            Name = name
        };
    }

    public static BookmarkNodeModel CreateAsset(AssetEntry entry, string? name = null)
    {
        string assetKind = entry switch
        {
            EbxAssetEntry => "ebx",
            ResAssetEntry => "res",
            ChunkAssetEntry => "chunk",
            _ => entry.AssetType
        };

        string assetKey = entry switch
        {
            ChunkAssetEntry chunkEntry => chunkEntry.Id.ToString(),
            _ => entry.Name
        };

        return new BookmarkNodeModel
        {
            IsFolder = false,
            Name = string.IsNullOrWhiteSpace(name) ? entry.Filename : name,
            AssetKind = assetKind,
            AssetKey = assetKey,
            AssetPath = entry.Name,
            AssetTypeHint = string.IsNullOrWhiteSpace(entry.Type) ? entry.AssetType : entry.Type
        };
    }

    private string GetFallbackName()
    {
        if (IsFolder)
        {
            return "Folder";
        }

        if (!string.IsNullOrWhiteSpace(AssetPath))
        {
            int index = AssetPath.LastIndexOf('/');
            return index >= 0 ? AssetPath[(index + 1)..] : AssetPath;
        }

        return "Bookmark";
    }
}
