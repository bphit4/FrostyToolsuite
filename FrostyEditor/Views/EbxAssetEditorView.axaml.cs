using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.VisualTree;
using FrostyEditor.Managers;
using FrostyEditor.Models;
using FrostyEditor.Utils;
using FrostyEditor.ViewModels;

namespace FrostyEditor.Views;

public partial class EbxAssetEditorView : UserControl
{
    private bool m_isInspectorGripDragging;
    private Point m_inspectorGripStart;
    private double m_inspectorColumnStartWidth;
    private InspectorNodeModel? m_contextNode;

    public EbxAssetEditorView()
    {
        AvaloniaXamlLoader.Load(this);
        UpdatePropertiesPanelState();
    }

    private void OnInspectorContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (IsEmbeddedTextEditorInteraction(e.Source))
        {
            e.Handled = true;
            return;
        }

        if (sender is not Control { ContextFlyout: MenuFlyout flyout } row || flyout.Items.OfType<MenuItem>().FirstOrDefault(item => Equals(item.Tag, "copy")) is not { } copyItem)
        {
            return;
        }

        InspectorNodeModel? node = row.DataContext as InspectorNodeModel;
        if (node is null)
        {
            return;
        }

        m_contextNode = node;

        bool hasSelection = TryGetSelectedEditorText(node, out _);
        copyItem.Header = hasSelection ? "Copy" : node.HasChildren ? "Copy Values" : "Copy Value";

        if (flyout.Items.OfType<MenuItem>().FirstOrDefault(item => Equals(item.Tag, "paste")) is { } pasteItem)
        {
            pasteItem.IsEnabled = InspectorClipboard.Current.HasData && node.CanPasteObject;
        }

        bool showExpandActions = node.HasChildren;
        if (flyout.Items.OfType<Control>().FirstOrDefault(item => Equals(item.Tag, "expand-separator")) is { } expandSeparator)
        {
            expandSeparator.IsVisible = showExpandActions;
        }

        if (flyout.Items.OfType<MenuItem>().FirstOrDefault(item => Equals(item.Tag, "expand-one-level")) is { } expandOneLevelItem)
        {
            expandOneLevelItem.IsVisible = showExpandActions;
        }

        if (flyout.Items.OfType<MenuItem>().FirstOrDefault(item => Equals(item.Tag, "expand-all-levels")) is { } expandAllLevelsItem)
        {
            expandAllLevelsItem.IsVisible = showExpandActions;
        }

        if (flyout.Items.OfType<MenuItem>().FirstOrDefault(item => Equals(item.Tag, "collapse-one-level")) is { } collapseOneLevelItem)
        {
            collapseOneLevelItem.IsVisible = showExpandActions;
        }

        if (flyout.Items.OfType<MenuItem>().FirstOrDefault(item => Equals(item.Tag, "collapse-all-levels")) is { } collapseAllLevelsItem)
        {
            collapseAllLevelsItem.IsVisible = showExpandActions;
        }

        bool showReferenceActions = node.IsPointerRef;
        bool showReferenceSeparator = showReferenceActions && (!string.IsNullOrWhiteSpace(node.PointerGuidText) || node.CanOpenReferenceAsset || node.CanFindReferenceAsset);
        if (flyout.Items.OfType<Control>().FirstOrDefault(item => Equals(item.Tag, "ref-separator")) is { } separator)
        {
            separator.IsVisible = showReferenceSeparator;
        }

        if (flyout.Items.OfType<MenuItem>().FirstOrDefault(item => Equals(item.Tag, "copy-guid")) is { } copyGuidItem)
        {
            copyGuidItem.IsVisible = showReferenceActions && !string.IsNullOrWhiteSpace(node.PointerGuidText);
        }

        if (flyout.Items.OfType<MenuItem>().FirstOrDefault(item => Equals(item.Tag, "open-asset")) is { } openItem)
        {
            openItem.IsVisible = node.CanOpenReferenceAsset;
        }

        if (flyout.Items.OfType<MenuItem>().FirstOrDefault(item => Equals(item.Tag, "find-asset")) is { } findItem)
        {
            findItem.IsVisible = node.CanFindReferenceAsset;
        }
    }

    private async void OnCopyMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (!TryGetContextNode(sender, out InspectorNodeModel node))
        {
            return;
        }

        if (TryGetSelectedEditorText(node, out string? selectedText))
        {
            TopLevel? topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.Clipboard is not null)
            {
                await topLevel.Clipboard.SetTextAsync(selectedText);
            }

            return;
        }

        if (DataContext is EbxAssetEditorViewModel viewModel)
        {
            viewModel.CopyNode(node);

            if (!node.HasChildren)
            {
                TopLevel? topLevel = TopLevel.GetTopLevel(this);
                if (topLevel?.Clipboard is not null)
                {
                    await topLevel.Clipboard.SetTextAsync(node.GetCopyValue());
                }
            }
        }
    }

    private void OnPasteMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (!TryGetContextNode(sender, out InspectorNodeModel node) || DataContext is not EbxAssetEditorViewModel viewModel)
        {
            return;
        }

        if (viewModel.TryPasteNode(node, out string? error))
        {
            RefreshInspectorRows(node);
            return;
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            Frosty.Sdk.FrostyLogger.Logger?.LogWarning($"Unable to paste into {node.Name}: {error}");
        }
    }

    private async void OnCopyGuidMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (!TryGetContextNode(sender, out InspectorNodeModel node) || string.IsNullOrWhiteSpace(node.PointerGuidText))
        {
            return;
        }

        TopLevel? topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard is not null)
        {
            await topLevel.Clipboard.SetTextAsync(node.PointerGuidText);
        }
    }

    private void OnOpenReferenceAssetMenuItemClick(object? sender, RoutedEventArgs e)
    {
        NavigateReferenceAsset(sender, openAsset: true);
    }

    private void OnFindReferenceAssetMenuItemClick(object? sender, RoutedEventArgs e)
    {
        NavigateReferenceAsset(sender, openAsset: false);
    }

    private void OnInspectorRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (IsEmbeddedControlInteraction(e.Source))
        {
            return;
        }

        if (sender is not Control row || row.DataContext is not InspectorNodeModel node)
        {
            return;
        }

        if (node.HasChildren)
        {
            bool expandOneLevel = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            bool expandAllLevels = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            if (expandAllLevels)
            {
                node.ExpandAllDescendants();
                RefreshInspectorRows();
            }
            else if (expandOneLevel)
            {
                node.ExpandOneLevelProgressive();
                RefreshInspectorRows();
            }
            else
            {
                bool shouldExpand = !node.IsExpanded;
                if (shouldExpand)
                {
                    node.EnsureChildrenLoaded();
                }

                node.IsExpanded = shouldExpand;
                RefreshInspectorRows(node);
            }

            e.Handled = true;
            return;
        }

        if (node.IsBoolean)
        {
            e.Handled = true;
            return;
        }

        if (!node.BeginEdit())
        {
            return;
        }

        TextBox? editor = row.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
        if (editor is null)
        {
            return;
        }

        editor.Focus();
        editor.SelectAll();
        e.Handled = true;
    }

    private void OnInspectorGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not EbxAssetEditorViewModel viewModel || sender is not InputElement element)
        {
            return;
        }

        m_isInspectorGripDragging = true;
        m_inspectorGripStart = e.GetPosition(this);
        m_inspectorColumnStartWidth = viewModel.InspectorNameColumnWidth;
        e.Pointer.Capture(element);
        e.Handled = true;
    }

    private void OnInspectorEditorPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is TextBox editor)
        {
            TextBoxContextMenuHelper.AttachManagedContextFlyout(editor);
            TextBoxContextMenuHelper.HandlePointerPressed(editor, e);
        }
    }

    private void OnInspectorEditorContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is TextBox editor)
        {
            TextBoxContextMenuHelper.AttachManagedContextFlyout(editor);
            TextBoxContextMenuHelper.HandleContextRequested(editor, e);
        }
    }

    private void OnInspectorGripMoved(object? sender, PointerEventArgs e)
    {
        if (!m_isInspectorGripDragging || DataContext is not EbxAssetEditorViewModel viewModel)
        {
            return;
        }

        Point current = e.GetPosition(this);
        double maxWidth = Math.Max(140, Bounds.Width - 80);
        viewModel.SetManualInspectorNameColumnWidth(Math.Clamp(m_inspectorColumnStartWidth + (current.X - m_inspectorGripStart.X), 80, maxWidth));
        e.Handled = true;
    }

    private void OnInspectorGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is InputElement element)
        {
            e.Pointer.Capture(null);
        }

        m_isInspectorGripDragging = false;
        e.Handled = true;
    }

    private void OnInspectorEditorLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox editor && editor.DataContext is InspectorNodeModel node)
        {
            if (TextBoxContextMenuHelper.ShouldRetainEditorOnLostFocus(editor))
            {
                return;
            }

            CommitEditor(node);
        }
    }

    private void OnInspectorEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox editor || editor.DataContext is not InspectorNodeModel node)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitEditor(node);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            node.CancelEdit();
        }
    }

    private static void CommitEditor(InspectorNodeModel node)
    {
        if (node.CommitEdit(out string? error) || string.IsNullOrWhiteSpace(error))
        {
            return;
        }

        Frosty.Sdk.FrostyLogger.Logger?.LogWarning($"Unable to update {node.Name}: {error}");
    }

    private void OnAddCollectionItemClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: InspectorNodeModel node } || DataContext is not EbxAssetEditorViewModel viewModel)
        {
            return;
        }

        if (node.AddCollectionItem(out string? error))
        {
            viewModel.RebuildNodes();
            return;
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            Frosty.Sdk.FrostyLogger.Logger?.LogWarning($"Unable to add an item to {node.Name}: {error}");
        }
    }

    private void OnClearCollectionItemsClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: InspectorNodeModel node } || DataContext is not EbxAssetEditorViewModel viewModel)
        {
            return;
        }

        if (node.ClearCollectionItems(out string? error))
        {
            viewModel.RebuildNodes();
            return;
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            Frosty.Sdk.FrostyLogger.Logger?.LogWarning($"Unable to clear {node.Name}: {error}");
        }
    }

    private void OnRemoveCollectionEntryClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: InspectorNodeModel node } || DataContext is not EbxAssetEditorViewModel viewModel)
        {
            return;
        }

        if (node.RemoveCollectionEntry(out string? error))
        {
            viewModel.RebuildNodes();
            return;
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            Frosty.Sdk.FrostyLogger.Logger?.LogWarning($"Unable to remove {node.Name}: {error}");
        }
    }

    private void OnPointerOptionsClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: InspectorNodeModel node } button)
        {
            return;
        }

        m_contextNode = node;
        MenuFlyout flyout = new()
        {
            Placement = PlacementMode.BottomEdgeAlignedLeft
        };

        if (node.CanClearPointer)
        {
            MenuItem clearItem = new() { Header = "Clear assigned object", DataContext = node };
            clearItem.Click += OnClearPointerMenuItemClick;
            flyout.Items.Add(clearItem);
        }

        if (node.CanOpenReferenceAsset)
        {
            MenuItem openItem = new() { Header = "Open asset", DataContext = node };
            openItem.Click += OnOpenReferenceAssetMenuItemClick;
            flyout.Items.Add(openItem);
        }

        if (node.CanFindReferenceAsset)
        {
            MenuItem findItem = new() { Header = "Find in data explorer", DataContext = node };
            findItem.Click += OnFindReferenceAssetMenuItemClick;
            flyout.Items.Add(findItem);
        }

        if (node.CanCreatePointer)
        {
            MenuItem createItem = new() { Header = "Create new ...", DataContext = node };
            createItem.Click += OnCreatePointerMenuItemClick;
            flyout.Items.Add(createItem);
        }

        if (flyout.Items.Count == 0)
        {
            return;
        }

        flyout.ShowAt(button);
    }

    private void OnAssignPointerFromSelectedAssetClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: InspectorNodeModel node } button)
        {
            return;
        }

        if (!node.TryGetSelectedAssetPointerOptions(out IReadOnlyList<InspectorPointerAssignmentOption> options, out string? error))
        {
            if (!string.IsNullOrWhiteSpace(error))
            {
                Frosty.Sdk.FrostyLogger.Logger?.LogWarning($"Unable to assign {node.Name} from the selected asset: {error}");
            }

            return;
        }

        if (options.Count == 1)
        {
            AssignPointerFromSelectedAsset(node, options[0]);
            return;
        }

        MenuFlyout flyout = new()
        {
            Placement = PlacementMode.BottomEdgeAlignedLeft
        };

        foreach (InspectorPointerAssignmentOption option in options)
        {
            MenuItem item = new()
            {
                Header = option.MenuText
            };
            item.Click += (_, _) => AssignPointerFromSelectedAsset(node, option);
            flyout.Items.Add(item);
        }

        flyout.ShowAt(button);
    }

    private void OnClearPointerMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (!TryGetContextNode(sender, out InspectorNodeModel node))
        {
            return;
        }

        if (node.ClearPointer(out string? error))
        {
            RefreshInspectorRows(node);
            return;
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            Frosty.Sdk.FrostyLogger.Logger?.LogWarning($"Unable to clear {node.Name}: {error}");
        }
    }

    private void OnCreatePointerMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (!TryGetContextNode(sender, out InspectorNodeModel node))
        {
            return;
        }

        if (node.CreatePointerInstance(out string? error))
        {
            RefreshInspectorRows(node);
            return;
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            Frosty.Sdk.FrostyLogger.Logger?.LogWarning($"Unable to create {node.Name}: {error}");
        }
    }

    private void OnExpandOneLevelMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (!TryGetContextNode(sender, out InspectorNodeModel node))
        {
            return;
        }

        node.ExpandOneLevelProgressive();
        RefreshInspectorRows();
    }

    private void OnExpandAllLevelsMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (!TryGetContextNode(sender, out InspectorNodeModel node))
        {
            return;
        }

        node.ExpandAllDescendants();
        RefreshInspectorRows();
    }

    private void OnCollapseOneLevelMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (!TryGetContextNode(sender, out InspectorNodeModel node))
        {
            return;
        }

        node.CollapseOneLevelProgressive();
        RefreshInspectorRows();
    }

    private void OnCollapseAllLevelsMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (!TryGetContextNode(sender, out InspectorNodeModel node))
        {
            return;
        }

        node.CollapseAllDescendants();
        RefreshInspectorRows();
    }

    private static bool IsEmbeddedControlInteraction(object? source)
    {
        if (source is not Visual visual)
        {
            return false;
        }

        return visual.GetSelfAndVisualAncestors().Any(ancestor => ancestor is ToggleButton or Button or TextBox);
    }

    private static bool IsEmbeddedTextEditorInteraction(object? source)
    {
        if (source is not Visual visual)
        {
            return false;
        }

        return visual.GetSelfAndVisualAncestors().Any(ancestor => ancestor is TextBox);
    }

    private bool TryGetSelectedEditorText(InspectorNodeModel node, out string? selectedText)
    {
        selectedText = null;
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not TextBox textBox ||
            !ReferenceEquals(textBox.DataContext, node) ||
            string.IsNullOrEmpty(textBox.SelectedText))
        {
            return false;
        }

        selectedText = textBox.SelectedText;
        return true;
    }

    private bool TryGetContextNode(object? sender, out InspectorNodeModel node)
    {
        node = sender switch
        {
            MenuItem { DataContext: InspectorNodeModel menuNode } => menuNode,
            Control { DataContext: InspectorNodeModel controlNode } => controlNode,
            _ => m_contextNode!
        };

        return node is not null;
    }

    private void NavigateReferenceAsset(object? sender, bool openAsset)
    {
        if (!TryGetContextNode(sender, out InspectorNodeModel node))
        {
            Frosty.Sdk.FrostyLogger.Logger?.LogWarning("Unable to resolve the selected pointer row.");
            return;
        }

        if (!node.TryGetReferencedAssetEntry(out var entry) || entry is null)
        {
            Frosty.Sdk.FrostyLogger.Logger?.LogWarning($"Unable to resolve asset reference for {node.Name}.");
            return;
        }

        App.MainViewModel?.DataExplorer.RevealAsset(entry, openAsset);
    }

    private void AssignPointerFromSelectedAsset(InspectorNodeModel node, InspectorPointerAssignmentOption option)
    {
        if (node.AssignPointerFromSelectedAsset(option, out string? error))
        {
            RefreshInspectorRows(node);
            return;
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            Frosty.Sdk.FrostyLogger.Logger?.LogWarning($"Unable to assign {node.Name} from the selected asset: {error}");
        }
    }

    private void RefreshInspectorRows(InspectorNodeModel? node = null)
    {
        if (DataContext is not EbxAssetEditorViewModel viewModel)
        {
            return;
        }

        if (node is not null)
        {
            viewModel.RefreshNodeSubtree(node);
            return;
        }

        viewModel.RefreshVisibleNodes();
    }

    private void OnPropertiesToggleChanged(object? sender, RoutedEventArgs e)
    {
        UpdatePropertiesPanelState();
    }

    private void UpdatePropertiesPanelState()
    {
        ToggleButton? toggle = this.FindControl<ToggleButton>("PropertiesToggle");
        Border? details = this.FindControl<Border>("PropertiesDetails");
        Path? glyph = this.FindControl<Path>("PropertiesExpandGlyph");
        if (toggle is null || details is null || glyph is null)
        {
            return;
        }

        bool isExpanded = toggle.IsChecked == true;
        details.IsVisible = isExpanded;

        if (glyph.RenderTransform is not RotateTransform rotateTransform)
        {
            rotateTransform = new RotateTransform();
            glyph.RenderTransform = rotateTransform;
        }

        rotateTransform.Angle = isExpanded ? 90 : 0;
    }

}
