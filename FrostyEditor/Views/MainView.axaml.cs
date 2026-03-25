using System;
using System.Reactive.Disposables;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using FrostyEditor.Models;
using FrostyEditor.ViewModels;
using FrostyEditor.Utils;

namespace FrostyEditor.Views;

public partial class MainView : UserControl
{
    private const int ExplorerColumnIndex = 1;
    private const int ExplorerSplitterColumnIndex = 2;
    private const int MainWorkspaceColumnIndex = 3;
    private const int BottomDockSplitterRowIndex = 1;
    private const int BottomDockRowIndex = 2;
    private const double DefaultExplorerWidth = 340.0;
    private const double MinExplorerWidth = 280.0;
    private const double MaxExplorerWidth = 380.0;
    private const double LegacyLoggerHeight = 130.0;
    private const double DefaultLoggerHeight = 216.0;
    private const double MinLoggerHeight = 150.0;
    private const double MaxLoggerHeight = 300.0;
    private const double CollapsedLoggerHeight = 28.0;
    private const double DocumentTabWidth = 274.0;
    private const double SplitterThickness = 2.0;

    private bool m_isExplorerCollapsed;
    private bool m_isBottomDockCollapsed;
    private double m_lastExpandedExplorerWidth = DefaultExplorerWidth;
    private double m_lastExpandedBottomDockHeight = DefaultLoggerHeight;
    private Window? m_explorerWindow;
    private bool m_isExplorerFloating;
    private bool m_suppressExplorerWindowClosed;
    private Window? m_loggerWindow;
    private bool m_isLoggerFloating;
    private bool m_suppressLoggerWindowClosed;
    private IDisposable? m_windowStateSubscription;

    public MainView()
    {
        AvaloniaXamlLoader.Load(this);
        AttachExplorerToggleHandler(this.FindControl<DataExplorerView>("DataExplorerView"));
        AttachedToVisualTree += (_, _) =>
        {
            HookViewModelEvents();
            HookWindowState();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            m_windowStateSubscription?.Dispose();
            m_windowStateSubscription = null;
        };
    }

    public void ApplyLayoutSettings()
    {
        Grid? grid = this.FindControl<Grid>("ShellGrid");
        if (grid is null || grid.ColumnDefinitions.Count < 5 || grid.RowDefinitions.Count < 3)
        {
            return;
        }

        double explorerWidth = Config.Get("MainViewExplorerWidth", DefaultExplorerWidth);
        double loggerHeight = Config.Get("MainViewLoggerHeight", DefaultLoggerHeight);
        bool isExplorerCollapsed = Config.Get("MainViewExplorerCollapsed", false);
        bool isBottomDockCollapsed = Config.Get("MainViewBottomDockCollapsed", false);
        explorerWidth = Math.Clamp(explorerWidth, MinExplorerWidth, MaxExplorerWidth);
        if (loggerHeight <= 184.0)
        {
            loggerHeight = DefaultLoggerHeight;
        }
        loggerHeight = Math.Clamp(loggerHeight, MinLoggerHeight, MaxLoggerHeight);
        m_lastExpandedExplorerWidth = explorerWidth;
        m_lastExpandedBottomDockHeight = loggerHeight;

        SetExplorerCollapsed(isExplorerCollapsed, save: false);
        SetBottomDockCollapsed(isBottomDockCollapsed, save: false);
        if (!m_isExplorerCollapsed)
        {
            grid.ColumnDefinitions[ExplorerColumnIndex].Width = new GridLength(explorerWidth, GridUnitType.Pixel);
        }

        if (!m_isBottomDockCollapsed)
        {
            grid.RowDefinitions[BottomDockRowIndex].Height = new GridLength(loggerHeight, GridUnitType.Pixel);
        }

        GetDockedDataExplorerView()?.ApplyLayoutSettings();
    }

    public void SaveLayoutSettings()
    {
        Grid? grid = this.FindControl<Grid>("ShellGrid");
        if (grid is null || grid.ColumnDefinitions.Count < 5 || grid.RowDefinitions.Count < 3)
        {
            return;
        }

        if (!m_isExplorerCollapsed && grid.ColumnDefinitions[ExplorerColumnIndex].ActualWidth > 0)
        {
            m_lastExpandedExplorerWidth = Math.Clamp(grid.ColumnDefinitions[ExplorerColumnIndex].ActualWidth, MinExplorerWidth, MaxExplorerWidth);
            Config.Add("MainViewExplorerWidth", m_lastExpandedExplorerWidth);
        }

        if (!m_isBottomDockCollapsed && grid.RowDefinitions[BottomDockRowIndex].ActualHeight > 0)
        {
            m_lastExpandedBottomDockHeight = Math.Clamp(grid.RowDefinitions[BottomDockRowIndex].ActualHeight, MinLoggerHeight, MaxLoggerHeight);
            Config.Add("MainViewLoggerHeight", m_lastExpandedBottomDockHeight);
        }

        Config.Add("MainViewExplorerCollapsed", m_isExplorerCollapsed);
        Config.Add("MainViewBottomDockCollapsed", m_isBottomDockCollapsed);
        Config.Save(App.ConfigPath);

        GetDockedDataExplorerView()?.SaveLayoutSettings();
    }

    public void ResetLayoutSettings()
    {
        Config.Remove("MainViewExplorerWidth");
        Config.Remove("MainViewLoggerHeight");
        Config.Remove("MainViewExplorerCollapsed");
        Config.Remove("MainViewBottomDockCollapsed");
        GetDockedDataExplorerView()?.ResetLayoutSettings();
        ApplyLayoutSettings();
        Config.Save(App.ConfigPath);
    }

    private void CaptureCurrentBottomDockHeight()
    {
        Grid? grid = this.FindControl<Grid>("ShellGrid");
        if (grid is null || grid.RowDefinitions.Count <= BottomDockRowIndex)
        {
            return;
        }

        double currentHeight = grid.RowDefinitions[BottomDockRowIndex].ActualHeight;
        if (currentHeight > 0)
        {
            m_lastExpandedBottomDockHeight = Math.Clamp(currentHeight, MinLoggerHeight, MaxLoggerHeight);
        }
    }

    private void OnDocumentTabPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is StyledElement element && element.DataContext is DocumentModel document)
        {
            document.ActivateCommand?.Execute(null);
            Dispatcher.UIThread.Post(BringActiveDocumentIntoView, DispatcherPriority.Background);
        }
    }

    private void OnScrollTabsLeftClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ScrollTabsBy(-DocumentTabWidth);
    }

    private void OnScrollTabsRightClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ScrollTabsBy(DocumentTabWidth);
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.Source is Button)
        {
            return;
        }

        if (TopLevel.GetTopLevel(this) is Window window)
        {
            window.BeginMoveDrag(e);
        }
    }

    private void OnMinimizeWindowClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window window)
        {
            window.WindowState = WindowState.Minimized;
        }
    }

    private void OnToggleMaximizeWindowClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window window)
        {
            return;
        }

        window.WindowState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        UpdateWindowStateGlyph(window.WindowState);
    }

    private void OnCloseWindowClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel && viewModel.ExitApplicationCommand.CanExecute(null))
        {
            viewModel.ExitApplicationCommand.Execute(null);
        }
    }

    private void HookWindowState()
    {
        m_windowStateSubscription?.Dispose();
        m_windowStateSubscription = null;

        if (TopLevel.GetTopLevel(this) is not Window window)
        {
            return;
        }

        UpdateWindowStateGlyph(window.WindowState);
        m_windowStateSubscription = window.GetObservable(Window.WindowStateProperty)
            .Subscribe(UpdateWindowStateGlyph);
    }

    private void UpdateWindowStateGlyph(WindowState state)
    {
        if (this.FindControl<TextBlock>("MaximizeRestoreGlyph") is not TextBlock glyph)
        {
            return;
        }

        glyph.Text = state == WindowState.Maximized ? "\u2750" : "\u25A1";
    }

    private void OnToggleExplorerPaneClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SetExplorerCollapsed(!m_isExplorerCollapsed, save: true);
    }

    private void OnExplorerFloatButtonClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (m_isExplorerFloating)
        {
            DockExplorerPane();
            return;
        }

        FloatExplorerPane();
    }

    private void OnExplorerExpandMenuClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (m_isExplorerFloating)
        {
            return;
        }

        SetExplorerCollapsed(false, save: true);
    }

    private void OnExplorerCollapseMenuClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (m_isExplorerFloating)
        {
            DockExplorerPane();
        }

        SetExplorerCollapsed(true, save: true);
    }

    private void OnExplorerFloatMenuClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        FloatExplorerPane();
    }

    private void OnExplorerDockMenuClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        DockExplorerPane();
    }

    private void OnExplorerResetDockMenuClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        DockExplorerPane();
        m_lastExpandedExplorerWidth = DefaultExplorerWidth;
        SetExplorerCollapsed(false, save: false);
        Grid? grid = this.FindControl<Grid>("ShellGrid");
        if (grid is not null && grid.ColumnDefinitions.Count > ExplorerColumnIndex)
        {
            grid.ColumnDefinitions[ExplorerColumnIndex].Width = new GridLength(DefaultExplorerWidth, GridUnitType.Pixel);
        }

        SaveLayoutSettings();
    }

    private void OnToggleBottomDockClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SetBottomDockCollapsed(!m_isBottomDockCollapsed, save: true);
    }

    private void OnLoggerFloatButtonClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (m_isLoggerFloating)
        {
            DockLoggerPane();
            return;
        }

        FloatLoggerPane();
    }

    private void OnLoggerExpandMenuClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (m_isLoggerFloating)
        {
            return;
        }

        SetBottomDockCollapsed(false, save: true);
    }

    private void OnLoggerCollapseMenuClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (m_isLoggerFloating)
        {
            DockLoggerPane();
        }

        SetBottomDockCollapsed(true, save: true);
    }

    private void OnLoggerFloatMenuClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        FloatLoggerPane();
    }

    private void OnLoggerDockMenuClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        DockLoggerPane();
    }

    private void OnLoggerResetDockMenuClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        DockLoggerPane();
        m_lastExpandedBottomDockHeight = DefaultLoggerHeight;
        SetBottomDockCollapsed(false, save: false);
        Grid? grid = this.FindControl<Grid>("ShellGrid");
        if (grid is not null && grid.RowDefinitions.Count > BottomDockRowIndex)
        {
            grid.RowDefinitions[BottomDockRowIndex].Height = new GridLength(DefaultLoggerHeight, GridUnitType.Pixel);
        }

        SaveLayoutSettings();
    }

    private void OnProjectSelectorClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Control control || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        MenuFlyout flyout = new()
        {
            Items =
            {
                new MenuItem { Header = viewModel.CurrentProjectName, IsEnabled = false },
                new MenuItem { Header = viewModel.CurrentProjectPathDisplay, IsEnabled = false },
                new Separator(),
                new MenuItem { Header = "New Project", Command = viewModel.NewProjectCommand },
                new MenuItem { Header = "Open Project...", Command = viewModel.OpenProjectCommand },
                new MenuItem { Header = "Save", Command = viewModel.SaveProjectCommand },
                new MenuItem { Header = "Save As...", Command = viewModel.SaveProjectAsCommand }
            }
        };
        flyout.ShowAt(control);
    }

    private void OnProfileSelectorClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Control control || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        MenuFlyout flyout = new();
        flyout.Items.Add(new MenuItem { Header = viewModel.ActiveProfileDisplayName, IsEnabled = false });
        flyout.Items.Add(new MenuItem { Header = viewModel.ActiveProfileKey, IsEnabled = false });
        flyout.Items.Add(new Separator());

        foreach ((string key, string displayName) in viewModel.AvailableProfiles)
        {
            MenuItem item = new()
            {
                Header = displayName,
                Command = viewModel.SwitchProfileCommand,
                CommandParameter = key,
                InputGesture = string.Equals(key, viewModel.ActiveProfileKey, StringComparison.OrdinalIgnoreCase)
                    ? new KeyGesture(Key.Enter)
                    : null
            };
            flyout.Items.Add(item);
        }

        flyout.Items.Add(new Separator());
        flyout.Items.Add(new MenuItem { Header = "Manage / Add Profiles...", Command = viewModel.ReselectProfileCommand });
        flyout.ShowAt(control);
    }

    private void OnSaveLayoutClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SaveLayoutSettings();
    }

    private void OnResetLayoutClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ResetLayoutSettings();
    }

    private void HookViewModelEvents()
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        viewModel.Documents.CollectionChanged -= Documents_CollectionChanged;
        viewModel.Documents.CollectionChanged += Documents_CollectionChanged;
        viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    private void Documents_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(BringActiveDocumentIntoView, DispatcherPriority.Background);
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ActiveDocument))
        {
            Dispatcher.UIThread.Post(BringActiveDocumentIntoView, DispatcherPriority.Background);
        }
    }

    private void ScrollTabsBy(double delta)
    {
        ScrollViewer? scrollViewer = this.FindControl<ScrollViewer>("DocumentTabsScrollViewer");
        if (scrollViewer is null)
        {
            return;
        }

        double maxOffset = Math.Max(0, scrollViewer.Extent.Width - scrollViewer.Viewport.Width);
        double nextOffset = Math.Clamp(scrollViewer.Offset.X + delta, 0, maxOffset);
        scrollViewer.Offset = new Vector(nextOffset, 0);
    }

    private void BringActiveDocumentIntoView()
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        ScrollViewer? scrollViewer = this.FindControl<ScrollViewer>("DocumentTabsScrollViewer");
        if (scrollViewer is null || viewModel.ActiveDocument is null)
        {
            return;
        }

        int activeIndex = viewModel.Documents.IndexOf(viewModel.ActiveDocument);
        if (activeIndex < 0)
        {
            return;
        }

        double itemLeft = activeIndex * DocumentTabWidth;
        double itemRight = itemLeft + DocumentTabWidth;
        double viewportLeft = scrollViewer.Offset.X;
        double viewportRight = viewportLeft + scrollViewer.Viewport.Width;

        if (itemLeft < viewportLeft)
        {
            scrollViewer.Offset = new Vector(itemLeft, 0);
            return;
        }

        if (itemRight > viewportRight)
        {
            double targetOffset = Math.Max(0, itemRight - scrollViewer.Viewport.Width);
            scrollViewer.Offset = new Vector(targetOffset, 0);
        }
    }

    private void SetExplorerCollapsed(bool collapsed, bool save)
    {
        Grid? grid = this.FindControl<Grid>("ShellGrid");
        Border? explorerPane = this.FindControl<Border>("ExplorerPane");
        if (grid is null || explorerPane is null || grid.ColumnDefinitions.Count <= ExplorerSplitterColumnIndex)
        {
            return;
        }

        if (!collapsed && grid.ColumnDefinitions[ExplorerColumnIndex].ActualWidth > 0)
        {
            m_lastExpandedExplorerWidth = Math.Clamp(grid.ColumnDefinitions[ExplorerColumnIndex].ActualWidth, MinExplorerWidth, MaxExplorerWidth);
        }

        m_isExplorerCollapsed = collapsed;
        explorerPane.IsVisible = !collapsed;
        grid.ColumnDefinitions[ExplorerColumnIndex].Width = collapsed
            ? new GridLength(0)
            : new GridLength(m_lastExpandedExplorerWidth, GridUnitType.Pixel);
        grid.ColumnDefinitions[ExplorerSplitterColumnIndex].Width = collapsed
            ? new GridLength(0)
            : new GridLength(SplitterThickness, GridUnitType.Pixel);

        if (save)
        {
            SaveLayoutSettings();
        }
    }

    private void SetBottomDockCollapsed(bool collapsed, bool save)
    {
        Grid? grid = this.FindControl<Grid>("ShellGrid");
        Border? dockPane = this.FindControl<Border>("BottomDockPane");
        Button? restoreButton = this.FindControl<Button>("BottomDockRestoreButton");
        ContentControl? loggerDockHost = this.FindControl<ContentControl>("LoggerDockHost");
        TextBlock? collapseGlyph = this.FindControl<TextBlock>("BottomDockCollapseGlyph");
        Button? collapseButton = this.FindControl<Button>("BottomDockCollapseButton");
        if (grid is null || dockPane is null || grid.RowDefinitions.Count <= BottomDockRowIndex)
        {
            return;
        }

        if (!collapsed && grid.RowDefinitions[BottomDockRowIndex].ActualHeight > 0)
        {
            m_lastExpandedBottomDockHeight = Math.Clamp(grid.RowDefinitions[BottomDockRowIndex].ActualHeight, MinLoggerHeight, MaxLoggerHeight);
        }

        bool hasDockedLoggerContent = loggerDockHost?.Content is not null;
        bool keepCollapsedStripVisible = collapsed && hasDockedLoggerContent && !m_isLoggerFloating;

        m_isBottomDockCollapsed = collapsed;
        dockPane.IsVisible = !collapsed || keepCollapsedStripVisible;
        if (grid.RowDefinitions.Count > BottomDockSplitterRowIndex)
        {
            grid.RowDefinitions[BottomDockSplitterRowIndex].Height = dockPane.IsVisible
                ? new GridLength(SplitterThickness, GridUnitType.Pixel)
                : new GridLength(0);
        }
        grid.RowDefinitions[BottomDockRowIndex].Height = collapsed
            ? keepCollapsedStripVisible
                ? new GridLength(CollapsedLoggerHeight, GridUnitType.Pixel)
                : new GridLength(0)
            : new GridLength(m_lastExpandedBottomDockHeight, GridUnitType.Pixel);
        if (collapseGlyph is not null)
        {
            collapseGlyph.Text = collapsed ? "\u25B4" : "\u25BE";
        }
        if (collapseButton is not null)
        {
            ToolTip.SetTip(collapseButton, collapsed ? "Expand Panels" : "Collapse Panels");
        }
        if (restoreButton is not null)
        {
            restoreButton.IsVisible = false;
        }

        if (save)
        {
            SaveLayoutSettings();
        }
    }

    private void FloatLoggerPane()
    {
        if (m_isLoggerFloating || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        ContentControl? loggerDockHost = this.FindControl<ContentControl>("LoggerDockHost");
        if (loggerDockHost is null)
        {
            return;
        }

        CaptureCurrentBottomDockHeight();
        loggerDockHost.Content = null;
        SetBottomDockCollapsed(true, save: false);
        LoggerView floatingLoggerView = new()
        {
            DataContext = viewModel.Logger
        };

        Window loggerWindow = new()
        {
            Width = 780,
            Height = 260,
            MinWidth = 520,
            MinHeight = 180,
            Title = "Docked Panels",
            Icon = CreateAppIcon()
        };
        Grid floatingShell = new()
        {
            RowDefinitions = new RowDefinitions("Auto,*")
        };
        Border header = new()
        {
            Background = Avalonia.Media.Brush.Parse("#1B1B1F"),
            BorderBrush = Avalonia.Media.Brush.Parse("#303035"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 6)
        };
        MenuFlyout flyout = new();
        MenuItem dockItem = new() { Header = "Dock" };
        dockItem.Click += (_, _) => DockLoggerPane();
        MenuItem resetItem = new() { Header = "Reset to Default Dock Location" };
        resetItem.Click += (_, _) =>
        {
            DockLoggerPane();
            m_lastExpandedBottomDockHeight = DefaultLoggerHeight;
            SaveLayoutSettings();
        };
        flyout.Items.Add(dockItem);
        flyout.Items.Add(resetItem);
        header.ContextFlyout = flyout;
        Grid headerGrid = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        headerGrid.Children.Add(new TextBlock { Text = "Docked Panels", FontWeight = Avalonia.Media.FontWeight.SemiBold, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        Button dockButton = new()
        {
            Content = "Dock",
            Classes = { "toolbar" },
            Padding = new Thickness(10, 4),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
        };
        dockButton.Click += (_, _) => DockLoggerPane();
        Grid.SetColumn(dockButton, 1);
        headerGrid.Children.Add(dockButton);
        header.Child = headerGrid;
        floatingShell.Children.Add(header);
        Border contentBorder = new() { Padding = new Thickness(8), Child = floatingLoggerView };
        Grid.SetRow(contentBorder, 1);
        floatingShell.Children.Add(contentBorder);
        loggerWindow.Content = floatingShell;
        loggerWindow.Closed += OnLoggerWindowClosed;
        m_loggerWindow = loggerWindow;
        m_isLoggerFloating = true;
        loggerWindow.Show();
        loggerWindow.Activate();
    }

    private void DockLoggerPane()
    {
        if (!m_isLoggerFloating || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        ContentControl? loggerDockHost = this.FindControl<ContentControl>("LoggerDockHost");
        if (loggerDockHost is not null)
        {
            loggerDockHost.Content = viewModel.Logger;
        }

        Window? loggerWindow = m_loggerWindow;
        m_loggerWindow = null;
        m_isLoggerFloating = false;

        if (loggerWindow is not null)
        {
            loggerWindow.Closed -= OnLoggerWindowClosed;
            m_suppressLoggerWindowClosed = true;
            loggerWindow.Content = null;
            loggerWindow.Close();
            m_suppressLoggerWindowClosed = false;
        }

        SetBottomDockCollapsed(false, save: false);
        SaveLayoutSettings();
    }

    private void OnLoggerWindowClosed(object? sender, EventArgs e)
    {
        if (m_suppressLoggerWindowClosed || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        ContentControl? loggerDockHost = this.FindControl<ContentControl>("LoggerDockHost");
        if (loggerDockHost is not null)
        {
            loggerDockHost.Content = viewModel.Logger;
        }

        if (sender is Window loggerWindow)
        {
            loggerWindow.Closed -= OnLoggerWindowClosed;
        }

        m_loggerWindow = null;
        m_isLoggerFloating = false;
        SetBottomDockCollapsed(false, save: false);
        SaveLayoutSettings();
    }

    private void FloatExplorerPane()
    {
        if (m_isExplorerFloating || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        ContentControl? explorerDockHost = this.FindControl<ContentControl>("ExplorerDockHost");
        if (explorerDockHost is null)
        {
            return;
        }

        explorerDockHost.Content = null;
        SetExplorerCollapsed(true, save: false);
        DataExplorerView floatingExplorerView = new()
        {
            DataContext = viewModel.DataExplorer
        };
        AttachExplorerToggleHandler(floatingExplorerView);

        Grid floatingShell = new()
        {
            RowDefinitions = new RowDefinitions("Auto,*")
        };

        Border header = new()
        {
            Background = Avalonia.Media.Brush.Parse("#1B1B1F"),
            BorderBrush = Avalonia.Media.Brush.Parse("#303035"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 6)
        };
        Grid headerGrid = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        headerGrid.Children.Add(new TextBlock { Text = "Explorer", FontWeight = Avalonia.Media.FontWeight.SemiBold, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        Button dockButton = new()
        {
            Content = "Dock",
            Classes = { "toolbar" },
            Padding = new Thickness(10, 4),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
        };
        dockButton.Click += (_, _) => DockExplorerPane();
        Grid.SetColumn(dockButton, 1);
        headerGrid.Children.Add(dockButton);
        header.Child = headerGrid;

        floatingShell.Children.Add(header);
        Border contentBorder = new() { Padding = new Thickness(8), Child = floatingExplorerView };
        Grid.SetRow(contentBorder, 1);
        floatingShell.Children.Add(contentBorder);

        Window explorerWindow = new()
        {
            Width = Math.Max(m_lastExpandedExplorerWidth + 120, 460),
            Height = 820,
            MinWidth = 360,
            MinHeight = 420,
            Title = "Explorer",
            Icon = CreateAppIcon(),
            Content = floatingShell
        };
        explorerWindow.Closed += OnExplorerWindowClosed;
        m_explorerWindow = explorerWindow;
        m_isExplorerFloating = true;
        explorerWindow.Show();
        explorerWindow.Activate();
    }

    private void DockExplorerPane()
    {
        if (!m_isExplorerFloating || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        ContentControl? explorerDockHost = this.FindControl<ContentControl>("ExplorerDockHost");
        if (explorerDockHost is not null)
        {
            DataExplorerView dockedExplorerView = new()
            {
                DataContext = viewModel.DataExplorer
            };
            AttachExplorerToggleHandler(dockedExplorerView);
            explorerDockHost.Content = dockedExplorerView;
        }

        Window? explorerWindow = m_explorerWindow;
        m_explorerWindow = null;
        m_isExplorerFloating = false;

        if (explorerWindow is not null)
        {
            explorerWindow.Closed -= OnExplorerWindowClosed;
            m_suppressExplorerWindowClosed = true;
            explorerWindow.Content = null;
            explorerWindow.Close();
            m_suppressExplorerWindowClosed = false;
        }

        SetExplorerCollapsed(false, save: false);
        SaveLayoutSettings();
    }

    private void OnExplorerWindowClosed(object? sender, EventArgs e)
    {
        if (m_suppressExplorerWindowClosed || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        ContentControl? explorerDockHost = this.FindControl<ContentControl>("ExplorerDockHost");
        if (explorerDockHost is not null)
        {
            DataExplorerView dockedExplorerView = new()
            {
                DataContext = viewModel.DataExplorer
            };
            AttachExplorerToggleHandler(dockedExplorerView);
            explorerDockHost.Content = dockedExplorerView;
        }

        if (sender is Window explorerWindow)
        {
            explorerWindow.Closed -= OnExplorerWindowClosed;
        }

        m_explorerWindow = null;
        m_isExplorerFloating = false;
        SetExplorerCollapsed(false, save: false);
        SaveLayoutSettings();
    }

    private DataExplorerView? GetDockedDataExplorerView()
    {
        if (this.FindControl<ContentControl>("ExplorerDockHost")?.Content is DataExplorerView dockedExplorer)
        {
            return dockedExplorer;
        }

        return this.FindControl<DataExplorerView>("DataExplorerView");
    }

    private void AttachExplorerToggleHandler(DataExplorerView? explorerView)
    {
        if (explorerView is null)
        {
            return;
        }

        explorerView.ToggleDockRequested -= OnExplorerToggleRequested;
        explorerView.ToggleDockRequested += OnExplorerToggleRequested;
    }

    private void OnExplorerToggleRequested(object? sender, EventArgs e)
    {
        if (m_isExplorerFloating)
        {
            DockExplorerPane();
            return;
        }

        FloatExplorerPane();
    }

    private static WindowIcon CreateAppIcon()
    {
        return new WindowIcon(AssetLoader.Open(new Uri("avares://FrostyEditor/Assets/FrostyApp.ico")));
    }
}
