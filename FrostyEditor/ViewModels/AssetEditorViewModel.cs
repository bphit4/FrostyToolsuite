using Frosty.Sdk.Managers.Entries;

namespace FrostyEditor.ViewModels;

public interface IReloadableDocument
{
    void ReloadFromSource();
}

public interface ISessionStateAwareDocument
{
    void RefreshSessionState();
}

public class AssetEditorViewModel : ViewModelBase, IReloadableDocument
{
    public AssetEntry Entry => m_entry;

    public static string CreateDocumentKey(AssetEntry entry)
    {
        return $"{entry.Type}|{entry.Path}|{entry.Name}".ToLowerInvariant();
    }

    public string Header => m_entry.Filename;
    public string Name => m_entry.Name;
    public string FileName => m_entry.Filename;
    public string DocumentKey => CreateDocumentKey(m_entry);
    public string Path => string.IsNullOrWhiteSpace(m_entry.Path) ? "Root" : m_entry.Path;
    public string Type => string.IsNullOrWhiteSpace(m_entry.Type) ? "Unknown" : m_entry.Type;
    public string AssetType => string.IsNullOrWhiteSpace(m_entry.AssetType) ? "N/A" : m_entry.AssetType;
    public string BundleCount => $"{m_entry.Bundles.Count} bundle(s)";
    public string SizeText => $"{m_entry.OriginalSize:N0} bytes";
    public string Summary => "Property editing is not wired up yet, but the editor shell, metadata surface, and document workflow are now in place.";

    protected readonly AssetEntry m_entry;

    public AssetEditorViewModel(AssetEntry inEntry)
    {
        m_entry = inEntry;
    }

    public virtual void ReloadFromSource()
    {
    }
}
