using System;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FrostyEditor.Utils;
using FrostyEditor.ViewModels;
using FrostyEditor.Windows;

namespace FrostyEditor;

public partial class App : Application
{
    public static string ConfigPath = Path.Combine(AppContext.BaseDirectory, "editor_config.json");

    public static MainViewModel? MainViewModel = null;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Config.Load(ConfigPath);
        TextBoxContextMenuHelper.Initialize();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Startup += (_, _) => Debug.WriteLine("App startup");
            desktop.Exit += (_, _) => Debug.WriteLine("App exit");

            desktop.MainWindow = ViewWindow.Create<ProfileSelectViewModel>();
            Debug.WriteLine($"MainWindow assigned: {desktop.MainWindow.GetType().Name}");
        }

        base.OnFrameworkInitializationCompleted();
    }
}
