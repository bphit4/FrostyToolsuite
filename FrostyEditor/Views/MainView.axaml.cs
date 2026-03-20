using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using FrostyEditor.Models;
using FrostyEditor.ViewModels;
using FrostyEditor.Utils;

namespace FrostyEditor.Views;

public partial class MainView : UserControl
{
    private const double DefaultExplorerWidth = 450.0;
    private const double DefaultLoggerHeight = 180.0;
    private const double DocumentTabWidth = 224.0;

    public MainView()
    {
        AvaloniaXamlLoader.Load(this);
        AttachedToVisualTree += (_, _) => HookViewModelEvents();
    }

    public void ApplyLayoutSettings()
    {
        Grid? grid = this.FindControl<Grid>("ShellGrid");
        if (grid is null || grid.ColumnDefinitions.Count < 3 || grid.RowDefinitions.Count < 3)
        {
            return;
        }

        double explorerWidth = Config.Get("MainViewExplorerWidth", DefaultExplorerWidth);
        double loggerHeight = Config.Get("MainViewLoggerHeight", DefaultLoggerHeight);

        grid.ColumnDefinitions[0].Width = new GridLength(explorerWidth, GridUnitType.Pixel);
        grid.RowDefinitions[2].Height = new GridLength(loggerHeight, GridUnitType.Pixel);

        this.FindControl<DataExplorerView>("DataExplorerView")?.ApplyLayoutSettings();
    }

    public void SaveLayoutSettings()
    {
        Grid? grid = this.FindControl<Grid>("ShellGrid");
        if (grid is null || grid.ColumnDefinitions.Count < 3 || grid.RowDefinitions.Count < 3)
        {
            return;
        }

        if (grid.ColumnDefinitions[0].ActualWidth > 0)
        {
            Config.Add("MainViewExplorerWidth", grid.ColumnDefinitions[0].ActualWidth);
        }

        if (grid.RowDefinitions[2].ActualHeight > 0)
        {
            Config.Add("MainViewLoggerHeight", grid.RowDefinitions[2].ActualHeight);
        }

        this.FindControl<DataExplorerView>("DataExplorerView")?.SaveLayoutSettings();
    }

    public void ResetLayoutSettings()
    {
        Config.Remove("MainViewExplorerWidth");
        Config.Remove("MainViewLoggerHeight");
        this.FindControl<DataExplorerView>("DataExplorerView")?.ResetLayoutSettings();
        ApplyLayoutSettings();
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
}
