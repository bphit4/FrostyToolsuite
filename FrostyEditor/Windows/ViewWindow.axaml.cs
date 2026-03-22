using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using FrostyEditor.Utils;
using FrostyEditor.ViewModels;

namespace FrostyEditor.Windows;

public partial class ViewWindow : Window
{
    private string? m_settingsPrefix;

    public ViewWindow()
    {
        InitializeComponent();
        Opened += (_, _) => Debug.WriteLine($"{GetType().Name} opened. Title='{Title}', Size={Width}x{Height}");
        Closed += (_, _) => Debug.WriteLine($"{GetType().Name} closed. Title='{Title}'");
    }

    public ViewWindow(WindowViewModel inViewModel)
        : this()
    {
        DataContext = inViewModel;
        Content = new ViewLocator().Build(inViewModel) ?? new TextBlock { Text = inViewModel.GetType().Name };
        m_settingsPrefix = GetSettingsPrefix(inViewModel);
        ApplySavedWindowPosition();
        Opened += (_, _) => EnsureVisiblePlacement();

        inViewModel.CloseWindow = () =>
        {
            Debug.WriteLine($"CloseWindow invoked for {inViewModel.GetType().Name}");
            Debug.WriteLine(new StackTrace(true).ToString());
            Close();
        };

        PositionChanged += (_, _) => SaveWindowPosition();
        Closing += (_, _) => SaveWindowPosition();
        Debug.WriteLine($"ViewWindow created for {inViewModel.GetType().Name}");
    }

    public static ViewWindow Create<T>()
        where T : WindowViewModel, new()
    {
        return new ViewWindow(new T());
    }

    public static ViewWindow Create<T>(out T outViewModel)
        where T : WindowViewModel, new()
    {
        outViewModel = new T();
        return new ViewWindow(outViewModel);
    }

    private static string? GetSettingsPrefix(WindowViewModel viewModel)
    {
        return viewModel switch
        {
            ProfileSelectViewModel => "ProfileSelectWindow",
            _ => null
        };
    }

    private void ApplySavedWindowPosition()
    {
        if (string.IsNullOrWhiteSpace(m_settingsPrefix))
        {
            return;
        }

        int posX = Config.Get($"{m_settingsPrefix}PosX", int.MinValue);
        int posY = Config.Get($"{m_settingsPrefix}PosY", int.MinValue);
        if (posX == int.MinValue || posY == int.MinValue)
        {
            return;
        }

        if (IsHiddenWindowPosition(posX, posY))
        {
            ClearSavedWindowPosition();
            return;
        }

        Position = new PixelPoint(posX, posY);
    }

    private void SaveWindowPosition()
    {
        if (string.IsNullOrWhiteSpace(m_settingsPrefix))
        {
            return;
        }

        if (IsHiddenWindowPosition(Position.X, Position.Y))
        {
            ClearSavedWindowPosition();
            return;
        }

        Config.Add($"{m_settingsPrefix}PosX", Position.X);
        Config.Add($"{m_settingsPrefix}PosY", Position.Y);
        Config.Save(App.ConfigPath);
    }

    private void EnsureVisiblePlacement()
    {
        if (!IsHiddenWindowPosition(Position.X, Position.Y))
        {
            return;
        }

        Position = new PixelPoint(120, 120);
    }

    private void ClearSavedWindowPosition()
    {
        if (string.IsNullOrWhiteSpace(m_settingsPrefix))
        {
            return;
        }

        Config.Remove($"{m_settingsPrefix}PosX");
        Config.Remove($"{m_settingsPrefix}PosY");
        Config.Save(App.ConfigPath);
    }

    private static bool IsHiddenWindowPosition(int x, int y)
    {
        return x <= -30000 || y <= -30000;
    }
}
