using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FrostyEditor.Models;
using FrostyEditor.ViewModels;
using FrostyEditor.Utils;

namespace FrostyEditor.Views;

public partial class DataExplorerView : UserControl
{
    private const double DefaultFolderPaneRatio = 2.25;
    private const double DefaultAssetPaneRatio = 1.0;
    public event EventHandler? ToggleDockRequested;

    public DataExplorerView()
    {
        InitializeComponent();
    }

    public void ApplyLayoutSettings()
    {
        Grid? grid = this.FindControl<Grid>("ExplorerGrid");
        if (DataContext is DataExplorerViewModel viewModel)
        {
            viewModel.SelectedExplorerTabIndex = Config.Get("DataExplorerSelectedTabIndex", 0);
        }

        if (grid is null || grid.RowDefinitions.Count < 5)
        {
            return;
        }

        double folderRatio = Config.Get("DataExplorerFolderPaneRatio", DefaultFolderPaneRatio);
        double assetRatio = Config.Get("DataExplorerAssetPaneRatio", DefaultAssetPaneRatio);

        grid.RowDefinitions[2].Height = new GridLength(folderRatio, GridUnitType.Star);
        grid.RowDefinitions[4].Height = new GridLength(assetRatio, GridUnitType.Star);
    }

    public void SaveLayoutSettings()
    {
        Grid? grid = this.FindControl<Grid>("ExplorerGrid");
        if (DataContext is DataExplorerViewModel viewModel)
        {
            Config.Add("DataExplorerSelectedTabIndex", viewModel.SelectedExplorerTabIndex);
        }

        if (grid is null || grid.RowDefinitions.Count < 5)
        {
            return;
        }

        double folderHeight = grid.RowDefinitions[2].ActualHeight;
        double assetHeight = grid.RowDefinitions[4].ActualHeight;
        if (folderHeight <= 0 || assetHeight <= 0)
        {
            return;
        }

        double assetBase = assetHeight == 0 ? 1.0 : assetHeight;
        Config.Add("DataExplorerFolderPaneRatio", folderHeight / assetBase);
        Config.Add("DataExplorerAssetPaneRatio", 1.0);
    }

    public void ResetLayoutSettings()
    {
        Config.Remove("DataExplorerFolderPaneRatio");
        Config.Remove("DataExplorerAssetPaneRatio");
        Config.Remove("DataExplorerSelectedTabIndex");

        if (DataContext is DataExplorerViewModel viewModel)
        {
            viewModel.SelectedExplorerTabIndex = 0;
        }

        ApplyLayoutSettings();
    }

    private void FolderTree_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (IsExpanderInteraction(e.Source))
        {
            return;
        }

        if (DataContext is DataExplorerViewModel viewModel)
        {
            viewModel.HandleFolderDoubleTapped(FindFolderNode(e.Source), e.KeyModifiers);
        }
    }

    private void FolderTree_OnTapped(object? sender, TappedEventArgs e)
    {
        if (IsExpanderInteraction(e.Source))
        {
            return;
        }

        if (DataContext is DataExplorerViewModel viewModel)
        {
            viewModel.HandleFolderTapped(FindFolderNode(e.Source), e.KeyModifiers);
        }
    }

    private void FolderTree_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is DataExplorerViewModel viewModel &&
            e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            viewModel.SelectFolderFromPointer(FindFolderNode(e.Source));
        }
    }

    private void AssetsTree_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is DataExplorerViewModel viewModel)
        {
            viewModel.HandleAssetDoubleTapped();
        }
    }

    private void AssetsTree_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not DataExplorerViewModel viewModel)
        {
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            viewModel.SelectAssetFromPointer(FindAssetNode(e.Source));
        }
    }

    private void LegacyFolderTree_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (IsExpanderInteraction(e.Source))
        {
            return;
        }

        if (DataContext is DataExplorerViewModel viewModel)
        {
            viewModel.LegacyExplorer.HandleFolderDoubleTapped(FindLegacyFolderNode(e.Source));
        }
    }

    private void LegacyFolderTree_OnTapped(object? sender, TappedEventArgs e)
    {
        if (IsExpanderInteraction(e.Source))
        {
            return;
        }

        if (DataContext is DataExplorerViewModel viewModel)
        {
            viewModel.LegacyExplorer.HandleFolderTapped(FindLegacyFolderNode(e.Source));
        }
    }

    private void LegacyFolderTree_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is DataExplorerViewModel viewModel &&
            e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            viewModel.LegacyExplorer.SelectFolderFromPointer(FindLegacyFolderNode(e.Source));
        }
    }

    private void LegacyAssetsTree_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is DataExplorerViewModel viewModel)
        {
            viewModel.LegacyExplorer.HandleAssetDoubleTapped();
        }
    }

    private void LegacyAssetsTree_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is DataExplorerViewModel viewModel)
        {
            viewModel.LegacyExplorer.HandleAssetTapped(FindLegacyAssetNode(e.Source));
        }
    }

    private void TypeFilterTextBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        OpenTypeComboIfNeeded(sender as TextBox, "DataTypeCombo");
    }

    private void LegacyTypeFilterTextBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        OpenTypeComboIfNeeded(sender as TextBox, "LegacyTypeCombo");
    }

    private void OnToggleDockClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ToggleDockRequested?.Invoke(this, EventArgs.Empty);
    }

    private void TypeCombo_OnDropDownOpened(object? sender, EventArgs e)
    {
        if (sender is not ComboBox combo)
        {
            return;
        }

        string rowName = combo.Name == "LegacyTypeCombo" ? "LegacyTypeFilterRow" : "DataTypeFilterRow";
        Grid? row = this.FindControl<Grid>(rowName);
        if (row is null)
        {
            return;
        }

        Popup? popup = combo.GetVisualDescendants().OfType<Popup>().FirstOrDefault();
        if (popup is null)
        {
            return;
        }

        popup.Placement = PlacementMode.BottomEdgeAlignedLeft;
        popup.HorizontalOffset = 0;
        popup.Width = Math.Max(combo.Bounds.Width, row.Bounds.Width);
    }

    private static bool IsExpanderInteraction(object? source)
    {
        if (source is not StyledElement element)
        {
            return false;
        }

        if (element is ToggleButton)
        {
            return true;
        }

        if (element is Visual visualElement)
        {
            foreach (Visual visual in visualElement.GetVisualAncestors())
            {
                if (visual is ToggleButton)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void OpenTypeComboIfNeeded(TextBox? textBox, string comboName)
    {
        if (textBox is null || !textBox.IsKeyboardFocusWithin)
        {
            return;
        }

        string filterText = textBox.Text?.Trim() ?? string.Empty;
        ComboBox? combo = this.FindControl<ComboBox>(comboName);
        if (combo is null || !combo.IsEnabled)
        {
            return;
        }

        if (string.IsNullOrEmpty(filterText))
        {
            if (combo.IsDropDownOpen)
            {
                combo.IsDropDownOpen = false;
            }

            return;
        }

        if (combo.IsDropDownOpen)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (textBox.IsKeyboardFocusWithin && !string.IsNullOrWhiteSpace(textBox.Text) && !combo.IsDropDownOpen)
            {
                combo.IsDropDownOpen = true;
            }
        }, DispatcherPriority.Background);
    }

    private static FolderTreeNodeModel? FindFolderNode(object? source)
    {
        if (source is StyledElement element)
        {
            if (element.DataContext is FolderTreeNodeModel directNode)
            {
                return directNode;
            }

            if (element is Visual visualElement)
            {
                foreach (Visual visual in visualElement.GetVisualAncestors())
                {
                    if (visual is StyledElement styledElement && styledElement.DataContext is FolderTreeNodeModel node)
                    {
                        return node;
                    }
                }
            }
        }

        return null;
    }

    private static LegacyFolderTreeNodeModel? FindLegacyFolderNode(object? source)
    {
        if (source is StyledElement element)
        {
            if (element.DataContext is LegacyFolderTreeNodeModel directNode)
            {
                return directNode;
            }

            if (element is Visual visualElement)
            {
                foreach (Visual visual in visualElement.GetVisualAncestors())
                {
                    if (visual is StyledElement styledElement && styledElement.DataContext is LegacyFolderTreeNodeModel node)
                    {
                        return node;
                    }
                }
            }
        }

        return null;
    }

    private static AssetModel? FindAssetNode(object? source)
    {
        if (source is StyledElement element)
        {
            if (element.DataContext is AssetModel directAsset)
            {
                return directAsset;
            }

            if (element is Visual visualElement)
            {
                foreach (Visual visual in visualElement.GetVisualAncestors())
                {
                    if (visual is StyledElement styledElement && styledElement.DataContext is AssetModel asset)
                    {
                        return asset;
                    }
                }
            }
        }

        return null;
    }

    private static LegacyAssetModel? FindLegacyAssetNode(object? source)
    {
        if (source is StyledElement element)
        {
            if (element.DataContext is LegacyAssetModel directAsset)
            {
                return directAsset;
            }

            if (element is Visual visualElement)
            {
                foreach (Visual visual in visualElement.GetVisualAncestors())
                {
                    if (visual is StyledElement styledElement && styledElement.DataContext is LegacyAssetModel asset)
                    {
                        return asset;
                    }
                }
            }
        }

        return null;
    }
}
