using System;
using System.Collections.Generic;

namespace FrostyEditor.Managers;

public static class AssetEditStateTracker
{
    private static readonly HashSet<string> s_dirtyAssets = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> s_modifiedAssets = new(StringComparer.OrdinalIgnoreCase);

    public static void MarkDirty(string assetName)
    {
        if (!string.IsNullOrWhiteSpace(assetName))
        {
            s_dirtyAssets.Add(assetName);
        }
    }

    public static void ClearDirty(string assetName)
    {
        if (!string.IsNullOrWhiteSpace(assetName))
        {
            s_dirtyAssets.Remove(assetName);
        }
    }

    public static void MarkModified(string assetName)
    {
        if (!string.IsNullOrWhiteSpace(assetName))
        {
            s_modifiedAssets.Add(assetName);
        }
    }

    public static void ClearModified(string assetName)
    {
        if (!string.IsNullOrWhiteSpace(assetName))
        {
            s_modifiedAssets.Remove(assetName);
        }
    }

    public static void ClearAsset(string assetName)
    {
        if (string.IsNullOrWhiteSpace(assetName))
        {
            return;
        }

        s_dirtyAssets.Remove(assetName);
        s_modifiedAssets.Remove(assetName);
    }

    public static void ClearAllDirty()
    {
        s_dirtyAssets.Clear();
    }

    public static void ClearAllModified()
    {
        s_modifiedAssets.Clear();
    }

    public static bool IsDirty(string assetName)
    {
        return !string.IsNullOrWhiteSpace(assetName) && s_dirtyAssets.Contains(assetName);
    }

    public static bool IsModified(string assetName)
    {
        return !string.IsNullOrWhiteSpace(assetName) && s_modifiedAssets.Contains(assetName);
    }

    public static IEnumerable<string> EnumerateDirtyAssets()
    {
        foreach (string assetName in s_dirtyAssets)
        {
            yield return assetName;
        }
    }

    public static IEnumerable<string> EnumerateModifiedAssets()
    {
        foreach (string assetName in s_modifiedAssets)
        {
            yield return assetName;
        }
    }
}
