using System;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia;
using Avalonia.Threading;
using Avalonia.Input;
using Avalonia.Platform;
using System.Runtime.InteropServices;
using FrostyEditor.Utils;
using FrostyEditor.Views;
using FrostyEditor.ViewModels;

namespace FrostyEditor.Windows;

public partial class MainWindow : Window
{
    private const int DwmWindowCornerPreferenceAttribute = 33;
    private const int DwmWindowCornerPreferenceDoNotRound = 1;
    private bool m_applyingSettings;
    private bool m_shellAttached;
    private bool m_allowClose;

    public MainWindow()
    {
        InitializeComponent();
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://FrostyEditor/Assets/FrostyApp.ico")));
        Opened += OnOpened;
        Opened += (_, _) => DisableRoundedCorners();
        Closing += OnClosing;
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
    }

    public void ResetWindowSettings()
    {
        Config.Remove("MainWindowState");
        Config.Remove("MainWindowWidth");
        Config.Remove("MainWindowHeight");
        Config.Remove("MainWindowPosX");
        Config.Remove("MainWindowPosY");

        if (Content is MainView mainView)
        {
            mainView.ResetLayoutSettings();
        }

        WindowState = WindowState.Maximized;
        Config.Save(App.ConfigPath);
    }

    public void AllowProgrammaticClose()
    {
        m_allowClose = true;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        ApplyWindowSettings();
        EnsureVisiblePlacement();
        BringToFront();
        if (m_shellAttached)
        {
            return;
        }

        Dispatcher.UIThread.Post(AttachShellContent, DispatcherPriority.Background);
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (m_applyingSettings)
        {
            return;
        }

        if (!m_allowClose && Content is MainView { DataContext: MainViewModel viewModel })
        {
            e.Cancel = true;
            bool canClose = await viewModel.ConfirmCloseAllDocumentsAsync();
            if (!canClose)
            {
                return;
            }

            m_allowClose = true;
            Close();
            return;
        }

        if (Content is MainView mainView)
        {
            mainView.SaveLayoutSettings();
        }

        Config.Add("MainWindowState", WindowState.ToString());

        if (WindowState == WindowState.Normal)
        {
            if (IsHiddenWindowPosition(Position.X, Position.Y))
            {
                Config.Remove("MainWindowPosX");
                Config.Remove("MainWindowPosY");
            }
            else
            {
            Config.Add("MainWindowWidth", Width);
            Config.Add("MainWindowHeight", Height);
            Config.Add("MainWindowPosX", Position.X);
            Config.Add("MainWindowPosY", Position.Y);
            }
        }

        Config.Save(App.ConfigPath);
    }

    private void AttachShellContent()
    {
        if (m_shellAttached)
        {
            return;
        }

        m_shellAttached = true;
        MainViewModel viewModel = new();
        MainView mainView = new()
        {
            DataContext = viewModel
        };

        Content = mainView;
        mainView.ApplyLayoutSettings();
        Dispatcher.UIThread.Post(BringToFront, DispatcherPriority.Background);
    }

    private void ApplyWindowSettings()
    {
        m_applyingSettings = true;
        try
        {
            string stateText = Config.Get("MainWindowState", nameof(WindowState.Maximized));
            if (!Enum.TryParse(stateText, out WindowState state))
            {
                state = WindowState.Maximized;
            }

            if (state == WindowState.Normal)
            {
                Width = Config.Get("MainWindowWidth", Width);
                Height = Config.Get("MainWindowHeight", Height);

                int posX = Config.Get("MainWindowPosX", int.MinValue);
                int posY = Config.Get("MainWindowPosY", int.MinValue);
                if (posX != int.MinValue && posY != int.MinValue && !IsHiddenWindowPosition(posX, posY))
                {
                    Position = new PixelPoint(posX, posY);
                }
                else if (IsHiddenWindowPosition(posX, posY))
                {
                    Config.Remove("MainWindowPosX");
                    Config.Remove("MainWindowPosY");
                }
            }

            WindowState = state;
        }
        finally
        {
            m_applyingSettings = false;
        }
    }

    private void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        EnsureVisiblePlacement();

        ShowInTaskbar = true;
        Activate();
        Topmost = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                Topmost = false;
                Activate();
            },
            DispatcherPriority.Background);
    }

    private void EnsureVisiblePlacement()
    {
        if (!IsHiddenWindowPosition(Position.X, Position.Y))
        {
            return;
        }

        WindowState = WindowState.Normal;
        Position = new PixelPoint(140, 140);
    }

    private static bool IsHiddenWindowPosition(int x, int y)
    {
        return x <= -30000 || y <= -30000;
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || Content is not MainView { DataContext: MainViewModel viewModel })
        {
            return;
        }

        switch (e.Key)
        {
            case Key.W:
                if (viewModel.CloseActiveDocumentCommand.CanExecute(null))
                {
                    viewModel.CloseActiveDocumentCommand.Execute(null);
                    e.Handled = true;
                }

                break;

            case Key.D:
                if (viewModel.CloseAllDocumentsCommand.CanExecute(null))
                {
                    viewModel.CloseAllDocumentsCommand.Execute(null);
                    e.Handled = true;
                }

                break;
        }
    }

    private void DisableRoundedCorners()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        IPlatformHandle? handle = TryGetPlatformHandle();
        if (handle is null)
        {
            return;
        }

        int preference = DwmWindowCornerPreferenceDoNotRound;
        DwmSetWindowAttribute(handle.Handle, DwmWindowCornerPreferenceAttribute, ref preference, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);
}
