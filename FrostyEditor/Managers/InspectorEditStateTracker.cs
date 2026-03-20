using System;
using System.Collections.Generic;

namespace FrostyEditor.Managers;

public static class InspectorEditStateTracker
{
    private static readonly HashSet<string> s_dirtyRows = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> s_modifiedRows = new(StringComparer.OrdinalIgnoreCase);

    public static void MarkDirty(string assetName, string nodePath)
    {
        string key = BuildKey(assetName, nodePath);
        if (!string.IsNullOrWhiteSpace(key))
        {
            s_dirtyRows.Add(key);
        }
    }

    public static void MarkModified(string assetName, string nodePath)
    {
        string key = BuildKey(assetName, nodePath);
        if (!string.IsNullOrWhiteSpace(key))
        {
            s_modifiedRows.Add(key);
        }
    }

    public static bool IsDirty(string assetName, string nodePath)
    {
        return s_dirtyRows.Contains(BuildKey(assetName, nodePath));
    }

    public static bool IsModified(string assetName, string nodePath)
    {
        return s_modifiedRows.Contains(BuildKey(assetName, nodePath));
    }

    public static void ClearAllDirty()
    {
        s_dirtyRows.Clear();
    }

    public static void ClearAllModified()
    {
        s_modifiedRows.Clear();
    }

    public static void ClearDirty(string assetName, string nodePath)
    {
        string key = BuildKey(assetName, nodePath);
        if (!string.IsNullOrWhiteSpace(key))
        {
            s_dirtyRows.Remove(key);
        }
    }

    public static void ClearModified(string assetName, string nodePath)
    {
        string key = BuildKey(assetName, nodePath);
        if (!string.IsNullOrWhiteSpace(key))
        {
            s_modifiedRows.Remove(key);
        }
    }

    public static void ClearAsset(string assetName)
    {
        if (string.IsNullOrWhiteSpace(assetName))
        {
            return;
        }

        string prefix = assetName + "|";
        s_dirtyRows.RemoveWhere(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        s_modifiedRows.RemoveWhere(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildKey(string assetName, string nodePath)
    {
        if (string.IsNullOrWhiteSpace(assetName) || string.IsNullOrWhiteSpace(nodePath))
        {
            return string.Empty;
        }

        return $"{assetName}|{nodePath}";
    }
}
