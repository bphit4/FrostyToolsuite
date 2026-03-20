using System;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia;
using FrostyEditor.Utils;
using FrostyEditor.Views;

namespace FrostyEditor.Windows;

public partial class MainWindow : Window
{
    private bool m_applyingSettings;

    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        Closing += OnClosing;
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

    private void OnOpened(object? sender, EventArgs e)
    {
        m_applyingSettings = true;
        try
        {
            if (Content is MainView mainView)
            {
                mainView.ApplyLayoutSettings();
            }

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
                if (posX != int.MinValue && posY != int.MinValue)
                {
                    Position = new PixelPoint(posX, posY);
                }
            }

            WindowState = state;
        }
        finally
        {
            m_applyingSettings = false;
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (m_applyingSettings)
        {
            return;
        }

        if (Content is MainView mainView)
        {
            mainView.SaveLayoutSettings();
        }

        Config.Add("MainWindowState", WindowState.ToString());

        if (WindowState == WindowState.Normal)
        {
            Config.Add("MainWindowWidth", Width);
            Config.Add("MainWindowHeight", Height);
            Config.Add("MainWindowPosX", Position.X);
            Config.Add("MainWindowPosY", Position.Y);
        }

        Config.Save(App.ConfigPath);
    }
}
