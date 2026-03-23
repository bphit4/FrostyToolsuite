using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using FrostyEditor.Models;
using FrostyEditor.ViewModels;

namespace FrostyEditor.Views;

public partial class SoundAssetEditorView : UserControl
{
    public SoundAssetEditorView()
    {
        InitializeComponent();
    }

    private async void OnPlaySegmentClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: SoundSegmentItemModel segment } ||
            DataContext is not SoundAssetEditorViewModel viewModel)
        {
            return;
        }

        await viewModel.PlaySegmentCommand.ExecuteAsync(segment);
    }

    private void OnStopSegmentClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: SoundSegmentItemModel segment } ||
            DataContext is not SoundAssetEditorViewModel viewModel)
        {
            return;
        }

        viewModel.StopSegmentCommand.Execute(segment);
    }

    private async void OnExportSegmentClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: SoundSegmentItemModel segment } ||
            DataContext is not SoundAssetEditorViewModel viewModel)
        {
            return;
        }

        await viewModel.ExportSegmentCommand.ExecuteAsync(segment);
    }

    private async void OnImportSegmentClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: SoundSegmentItemModel segment } ||
            DataContext is not SoundAssetEditorViewModel viewModel)
        {
            return;
        }

        await viewModel.ImportSegmentCommand.ExecuteAsync(segment);
    }

    private void OnSegmentSeekPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Slider slider ||
            slider.DataContext is not SoundSegmentItemModel segment ||
            DataContext is not SoundAssetEditorViewModel viewModel)
        {
            return;
        }

        viewModel.SeekSegmentProgress(segment, slider.Value);
    }
}
