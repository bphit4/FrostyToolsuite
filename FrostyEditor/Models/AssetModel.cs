using System;
using System.Collections.Generic;
using System.Reflection;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Frosty.Sdk.Managers.Entries;
using FrostyEditor.Managers;

namespace FrostyEditor.Models;

public class AssetModel
{
    public string? Name => Entry?.Filename;
    public string? Type => Entry?.Type;
    public Bitmap Icon => AssetIconRegistry.GetIcon(Entry);
    public bool IsModified => AssetEditStateTracker.IsModified(Entry?.Name ?? string.Empty) ||
                              GetBoolProperty("IsModified") || HasProperty("ModifiedEntry");
    public bool IsAdded => GetBoolProperty("IsAdded") || HasEnumerableProperty("AddedBundles");
    public bool IsUnsaved => AssetEditStateTracker.IsDirty(Entry?.Name ?? string.Empty) || GetBoolProperty("IsUnsaved") || GetBoolProperty("IsDirty");
    public bool IsUnmodified => !IsModified && !IsAdded && !IsUnsaved;
    public string DirtySuffix => IsUnsaved ? "*" : string.Empty;
    public FontWeight NameFontWeight => (IsModified || IsUnsaved) ? FontWeight.SemiBold : FontWeight.Normal;

    public AssetEntry? Entry { get; }

    public AssetModel(AssetEntry inEntry)
    {
        Entry = inEntry;
    }

    private bool GetBoolProperty(string propertyName)
    {
        if (Entry is null)
        {
            return false;
        }

        PropertyInfo? property = Entry.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (property is null || property.PropertyType != typeof(bool))
        {
            return false;
        }

        return property.GetValue(Entry) as bool? ?? false;
    }

    private bool HasProperty(string propertyName)
    {
        if (Entry is null)
        {
            return false;
        }

        PropertyInfo? property = Entry.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        return property is not null && property.GetValue(Entry) is not null;
    }

    private bool HasEnumerableProperty(string propertyName)
    {
        if (Entry is null)
        {
            return false;
        }

        PropertyInfo? property = Entry.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (property?.GetValue(Entry) is not System.Collections.IEnumerable enumerable)
        {
            return false;
        }

        foreach (object? _ in enumerable)
        {
            return true;
        }

        return false;
    }

    public static Comparison<AssetModel?> SortAscending<T>(Func<AssetModel, T> selector)
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

    public static Comparison<AssetModel?> SortDescending<T>(Func<AssetModel, T> selector)
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
}
