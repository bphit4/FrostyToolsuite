using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FrostyEditor.Views;

public partial class LegacyAssetDocumentView : UserControl
{
    public LegacyAssetDocumentView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
