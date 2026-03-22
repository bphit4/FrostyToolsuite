using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace FrostyEditor.Utils;

public static class ClipboardService
{
    public static async Task<bool> SetTextAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if ((Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.Clipboard is not { } clipboard)
        {
            return false;
        }

        await clipboard.SetTextAsync(text);
        return true;
    }
}
