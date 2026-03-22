using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FrostyEditor.Models;

namespace FrostyEditor.Managers;

public static class BookmarkPersistenceManager
{
    private sealed class BookmarkFileModel
    {
        public int Version { get; set; } = 1;
        public List<BookmarkNodeFileModel> Items { get; set; } = [];
    }

    private sealed class BookmarkNodeFileModel
    {
        public string Kind { get; set; } = "folder";
        public string Name { get; set; } = string.Empty;
        public string? AssetKind { get; set; }
        public string? AssetKey { get; set; }
        public string? AssetPath { get; set; }
        public string? AssetTypeHint { get; set; }
        public List<BookmarkNodeFileModel> Children { get; set; } = [];
    }

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true
    };

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "Bookmarks.json");

    public static IReadOnlyList<BookmarkNodeModel> LoadBookmarks()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return [];
            }

            BookmarkFileModel? file = JsonSerializer.Deserialize<BookmarkFileModel>(File.ReadAllText(FilePath), s_jsonOptions);
            if (file?.Items is null || file.Items.Count == 0)
            {
                return [];
            }

            return file.Items.Select(DeserializeNode).ToList();
        }
        catch
        {
            return [];
        }
    }

    public static void SaveBookmarks(IEnumerable<BookmarkNodeModel> items)
    {
        BookmarkFileModel file = new()
        {
            Items = items.Select(SerializeNode).ToList()
        };

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(file, s_jsonOptions));
    }

    private static BookmarkNodeFileModel SerializeNode(BookmarkNodeModel node)
    {
        return new BookmarkNodeFileModel
        {
            Kind = node.IsFolder ? "folder" : "asset",
            Name = node.Name,
            AssetKind = node.AssetKind,
            AssetKey = node.AssetKey,
            AssetPath = node.AssetPath,
            AssetTypeHint = node.AssetTypeHint,
            Children = node.Children.Select(SerializeNode).ToList()
        };
    }

    private static BookmarkNodeModel DeserializeNode(BookmarkNodeFileModel node)
    {
        BookmarkNodeModel model = new()
        {
            IsFolder = string.Equals(node.Kind, "folder", StringComparison.OrdinalIgnoreCase),
            Name = node.Name ?? string.Empty,
            AssetKind = node.AssetKind,
            AssetKey = node.AssetKey,
            AssetPath = node.AssetPath,
            AssetTypeHint = node.AssetTypeHint,
            IsExpanded = true
        };

        foreach (BookmarkNodeFileModel child in node.Children)
        {
            model.AddChild(DeserializeNode(child));
        }

        return model;
    }
}
