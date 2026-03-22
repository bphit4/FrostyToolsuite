using Avalonia.Media.Imaging;
using Frosty.Sdk.Managers.Entries;
using FrostyEditor.Managers;

namespace FrostyEditor.Models;

public sealed class LoggerReferenceItem
{
    public required EbxAssetEntry Entry { get; init; }

    public string Name => Entry.Filename;
    public string FullName => Entry.Name;
    public string Path => string.IsNullOrWhiteSpace(Entry.Path) ? "Root" : Entry.Path;
    public string Type => string.IsNullOrWhiteSpace(Entry.Type) ? Entry.AssetType : Entry.Type;
    public Bitmap Icon => AssetIconRegistry.GetIcon(Type);
    public string NameWithPath => string.IsNullOrWhiteSpace(Path) || Path == "Root"
        ? Name
        : $"{Name}  ({Path})";
}
