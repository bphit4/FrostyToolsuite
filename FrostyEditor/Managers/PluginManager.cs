using System;
using System.Collections.Generic;
using Frosty.Sdk.Managers.Entries;
using FrostyEditor.ViewModels;

namespace FrostyEditor.Managers;

public static class PluginManager
{
    private static Dictionary<string, Type> s_ebxAssetEditors = new();

    public static AssetEditorViewModel GetEbxAssetEditor(EbxAssetEntry entry)
    {
        if (IsTextureAsset(entry.Type))
        {
            return new TextureAssetEditorViewModel(entry);
        }

        if (s_ebxAssetEditors.TryGetValue(entry.Type.ToLower(), out Type? type) &&
            Activator.CreateInstance(type, entry) is AssetEditorViewModel editor)
        {
            return editor;
        }

        return new EbxAssetEditorViewModel(entry);
    }

    private static bool IsTextureAsset(string type)
    {
        return type.Equals("TextureAsset", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("TextureArrayAsset", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("ImageLibraryTexture", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("MovieTextureAsset", StringComparison.OrdinalIgnoreCase);
    }
}
