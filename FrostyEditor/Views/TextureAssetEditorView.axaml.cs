using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FrostyEditor.Managers;
using FrostyEditor.Models;
using FrostyEditor.Utils;
using FrostyEditor.ViewModels;

namespace FrostyEditor.Views;

public partial class TextureAssetEditorView : UserControl
{
    // ── Viewport / zoom ───────────────────────────────────────────
    private Border? m_viewportHost;
    private Canvas? m_previewCanvas;
    private Image? m_previewImage;
    private ScaleTransform? m_scaleTransform;
    private TranslateTransform? m_translateTransform;
    private Point? m_panStart;
    private Point m_panOrigin;
    private double m_zoom = 1.0;

    // ── Inspector column resize ───────────────────────────────────
    private bool m_isInspectorGripDragging;
    private Point m_inspectorGripStart;
    private double m_inspectorColumnStartWidth;

    // ── Inspector split-list scroll sync ─────────────────────────
    private ListBox? m_inspectorNameList;
    private ListBox? m_inspectorValueList;
    private ScrollViewer? m_nameScrollViewer;
    private ScrollViewer? m_valueScrollViewer;
    private bool m_isSyncingScroll;

    // ── Context menu ─────────────────────────────────────────────
    private InspectorNodeModel? m_contextNode;

    public TextureAssetEditorView()
    {
        AvaloniaXamlLoader.Load(this);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        m_viewportHost = this.FindControl<Border>("ViewportHost");
        m_previewCanvas = this.FindControl<Canvas>("PreviewCanvas");
        m_previewImage = this.FindControl<Image>("PreviewImage");

        if (m_previewImage is not null)
        {
            m_scaleTransform = new ScaleTransform(1, 1);
            m_translateTransform = new TranslateTransform();
            m_previewImage.RenderTransform = new TransformGroup
            {
                Children = new Transforms { m_scaleTransform, m_translateTransform }
            };
            m_previewImage.Opacity = 0;
            m_previewImage.PointerWheelChanged += OnPreviewWheelChanged;
            m_previewImage.PointerPressed += OnPreviewPointerPressed;
            m_previewImage.PointerReleased += OnPreviewPointerReleased;
            m_previewImage.PointerMoved += OnPreviewPointerMoved;
        }

        if (m_viewportHost is not null)
        {
            m_viewportHost.SizeChanged += OnViewportSizeChanged;
        }

        // Locate both inspector list controls
        m_inspectorNameList = this.FindControl<ListBox>("InspectorNameList");
        m_inspectorValueList = this.FindControl<ListBox>("InspectorValueList");

        // Wire up scroll sync after the list templates have been applied
        Dispatcher.UIThread.Post(InitializeScrollSync, DispatcherPriority.Loaded);

        if (DataContext is TextureAssetEditorViewModel vm)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
            if (m_viewportHost is not null)
            {
                vm.UpdateViewportSize(m_viewportHost.Bounds.Width, m_viewportHost.Bounds.Height);
            }
            vm.EnablePreview();
        }

        SyncViewportSurface();
        ResetView();
    }

    private void OnUnloaded(object? sender, EventArgs e)
    {
        if (DataContext is TextureAssetEditorViewModel vm)
        {
            vm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        // Unsubscribe scroll sync to avoid leaks
        if (m_nameScrollViewer is not null)
        {
            m_nameScrollViewer.ScrollChanged -= OnInspectorScrollChanged;
        }

        if (m_valueScrollViewer is not null)
        {
            m_valueScrollViewer.ScrollChanged -= OnInspectorScrollChanged;
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Scroll sync — keeps the frozen name column aligned with the
    // scrollable value column at all times.
    // ─────────────────────────────────────────────────────────────

    private void InitializeScrollSync()
    {
        if (m_inspectorNameList is not null)
        {
            m_nameScrollViewer = m_inspectorNameList.GetVisualDescendants()
                .OfType<ScrollViewer>()
                .FirstOrDefault();
        }

        if (m_inspectorValueList is not null)
        {
            m_valueScrollViewer = m_inspectorValueList.GetVisualDescendants()
                .OfType<ScrollViewer>()
                .FirstOrDefault();
        }

        if (m_nameScrollViewer is not null)
        {
            m_nameScrollViewer.ScrollChanged += OnInspectorScrollChanged;
        }

        if (m_valueScrollViewer is not null)
        {
            m_valueScrollViewer.ScrollChanged += OnInspectorScrollChanged;
        }
    }

    /// <summary>
    /// Keeps both lists at the same vertical offset.
    /// Whichever list the user scrolls, the other follows.
    /// Horizontal offset is intentionally NOT synced — only the
    /// value list scrolls horizontally.
    /// </summary>
    private void OnInspectorScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (m_isSyncingScroll || e.OffsetDelta.Y == 0)
        {
            return;
        }

        ScrollViewer? source;
        ScrollViewer? target;

        if (ReferenceEquals(sender, m_valueScrollViewer))
        {
            source = m_valueScrollViewer;
            target = m_nameScrollViewer;
        }
        else if (ReferenceEquals(sender, m_nameScrollViewer))
        {
            source = m_nameScrollViewer;
            target = m_valueScrollViewer;
        }
        else
        {
            return;
        }

        if (target is null || source is null)
        {
            return;
        }

        double targetY = source.Offset.Y;

        // Skip if already in sync (guards against event re-entry)
        if (Math.Abs(target.Offset.Y - targetY) < 0.5)
        {
            return;
        }

        m_isSyncingScroll = true;
        target.Offset = new Vector(target.Offset.X, targetY);
        m_isSyncingScroll = false;
    }

    // ─────────────────────────────────────────────────────────────
    // Viewport / preview handlers (unchanged)
    // ─────────────────────────────────────────────────────────────

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TextureAssetEditorViewModel.PreviewBitmap))
        {
            if (m_previewImage is not null)
            {
                m_previewImage.Opacity = 0;
            }

            Dispatcher.UIThread.Post(ResetView, DispatcherPriority.Render);
        }
    }

    private void OnViewportSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        SyncViewportSurface();

        if (DataContext is TextureAssetEditorViewModel vm && e.NewSize.Width > 0 && e.NewSize.Height > 0)
        {
            vm.UpdateViewportSize(e.NewSize.Width, e.NewSize.Height);
            vm.EnablePreview();
        }

        ResetView();
    }

    private void OnPreviewWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (m_scaleTransform is null || m_translateTransform is null)
        {
            return;
        }

        double delta = e.Delta.Y > 0 ? 1.1 : 0.9;
        Point viewportPoint = e.GetPosition(m_viewportHost);
        ZoomAt(delta, viewportPoint);
        e.Handled = true;
    }

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed || m_previewImage is null)
        {
            return;
        }

        m_panStart = e.GetPosition(m_viewportHost);
        m_panOrigin = new Point(m_translateTransform?.X ?? 0, m_translateTransform?.Y ?? 0);
        e.Pointer.Capture(m_previewImage);
        e.Handled = true;
    }

    private void OnPreviewPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (m_previewImage is not null)
        {
            e.Pointer.Capture(null);
        }

        m_panStart = null;
    }

    private void OnPreviewPointerMoved(object? sender, PointerEventArgs e)
    {
        if (m_panStart is null || m_translateTransform is null || m_viewportHost is null)
        {
            return;
        }

        Point current = e.GetPosition(m_viewportHost);
        Vector delta = current - m_panStart.Value;
        m_translateTransform.X = m_panOrigin.X + delta.X;
        m_translateTransform.Y = m_panOrigin.Y + delta.Y;
    }

    // ─────────────────────────────────────────────────────────────
    // Inspector column resize grip (unchanged)
    // ─────────────────────────────────────────────────────────────

    private void OnInspectorGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not TextureAssetEditorViewModel vm || sender is not InputElement element)
        {
            return;
        }

        m_isInspectorGripDragging = true;
        m_inspectorGripStart = e.GetPosition(this);
        m_inspectorColumnStartWidth = vm.InspectorNameColumnWidth;
        e.Pointer.Capture(element);
        e.Handled = true;
    }

    private void OnInspectorGripMoved(object? sender, PointerEventArgs e)
    {
        if (!m_isInspectorGripDragging || DataContext is not TextureAssetEditorViewModel vm)
        {
            return;
        }

        Point current = e.GetPosition(this);
        double maxWidth = Math.Max(140, Bounds.Width - 80);
        vm.SetManualInspectorNameColumnWidth(Math.Clamp(m_inspectorColumnStartWidth + (current.X - m_inspectorGripStart.X), 80, maxWidth));
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

    // ─────────────────────────────────────────────────────────────
    // Inspector name column — expand / collapse only.
    // No editing is triggered from here.
    // ─────────────────────────────────────────────────────────────

    private void OnInspectorNamePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (sender is not Control row || row.DataContext is not InspectorNodeModel node)
        {
            return;
        }

        if (!node.HasChildren)
        {
            return;
        }

        bool expandAllLevels = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool expandOneLevel = e.KeyModifiers.HasFlag(KeyModifiers.Control);

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
            RefreshInspectorRows();
        }

        e.Handled = true;
    }

    // ─────────────────────────────────────────────────────────────
    // Inspector value column — expand / collapse parent nodes,
    // or begin inline editing for leaf nodes.
    // ─────────────────────────────────────────────────────────────

    private void OnInspectorValuePointerPressed(object? sender, PointerPressedEventArgs e)
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
            // Clicking the value side of a parent row also toggles expand/collapse
            bool shouldExpand = !node.IsExpanded;
            if (shouldExpand)
            {
                node.EnsureChildrenLoaded();
            }

            node.IsExpanded = shouldExpand;
            RefreshInspectorRows();
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

        // Focus the TextBox that appeared inside the value template row
        TextBox? editor = row.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
        if (editor is null)
        {
            return;
        }

        editor.Focus();
        editor.SelectAll();
        e.Handled = true;
    }

    // ─────────────────────────────────────────────────────────────
    // Inline editor events (unchanged)
    // ─────────────────────────────────────────────────────────────

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

    // ─────────────────────────────────────────────────────────────
    // Context menu (unchanged, only attached to value template)
    // ─────────────────────────────────────────────────────────────

    private void OnInspectorContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (IsEmbeddedTextEditorInteraction(e.Source))
        {
            e.Handled = true;
            return;
        }

        if (sender is not Control { ContextFlyout: MenuFlyout flyout } row ||
            flyout.Items.OfType<MenuItem>().FirstOrDefault(item => Equals(item.Tag, "copy")) is not { } copyItem)
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

        bool showReferenceActions = node.IsPointerRef;
        if (flyout.Items.OfType<Control>().FirstOrDefault(item => Equals(item.Tag, "ref-separator")) is { } separator)
        {
            separator.IsVisible = showReferenceActions;
        }

        if (flyout.Items.OfType<MenuItem>().FirstOrDefault(item => Equals(item.Tag, "copy-guid")) is { } copyGuidItem)
        {
            copyGuidItem.IsVisible = showReferenceActions && !string.IsNullOrWhiteSpace(node.PointerGuidText);
        }

        if (flyout.Items.OfType<MenuItem>().FirstOrDefault(item => Equals(item.Tag, "open-asset")) is { } openItem)
        {
            openItem.IsVisible = showReferenceActions;
        }

        if (flyout.Items.OfType<MenuItem>().FirstOrDefault(item => Equals(item.Tag, "find-asset")) is { } findItem)
        {
            findItem.IsVisible = showReferenceActions;
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

        if (DataContext is TextureAssetEditorViewModel viewModel)
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
        if (!TryGetContextNode(sender, out InspectorNodeModel node) || DataContext is not TextureAssetEditorViewModel viewModel)
        {
            return;
        }

        if (viewModel.TryPasteNode(node, out string? error))
        {
            RefreshInspectorRows();
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

    // ─────────────────────────────────────────────────────────────
    // Zoom helpers (unchanged)
    // ─────────────────────────────────────────────────────────────

    private void OnZoomInClicked(object? sender, RoutedEventArgs e)
    {
        if (m_viewportHost is null)
        {
            return;
        }

        ZoomAt(1.1, new Point(m_viewportHost.Bounds.Width / 2, m_viewportHost.Bounds.Height / 2));
    }

    private void OnZoomOutClicked(object? sender, RoutedEventArgs e)
    {
        if (m_viewportHost is null)
        {
            return;
        }

        ZoomAt(0.9, new Point(m_viewportHost.Bounds.Width / 2, m_viewportHost.Bounds.Height / 2));
    }

    private void OnResetViewClicked(object? sender, RoutedEventArgs e)
    {
        ResetView();
    }

    private void ZoomAt(double multiplier, Point viewportPoint)
    {
        if (m_previewImage?.Source is not Avalonia.Media.Imaging.Bitmap bitmap ||
            m_scaleTransform is null ||
            m_translateTransform is null)
        {
            return;
        }

        double previousZoom = m_zoom;
        m_zoom = Math.Clamp(m_zoom * multiplier, 0.05, 32.0);
        double ratio = m_zoom / previousZoom;

        m_translateTransform.X = viewportPoint.X - ((viewportPoint.X - m_translateTransform.X) * ratio);
        m_translateTransform.Y = viewportPoint.Y - ((viewportPoint.Y - m_translateTransform.Y) * ratio);
        m_scaleTransform.ScaleX = m_zoom;
        m_scaleTransform.ScaleY = m_zoom;

        if (DataContext is TextureAssetEditorViewModel vm)
        {
            vm.SetZoomText(m_zoom);
        }
    }

    private void ResetView()
    {
        if (m_previewImage?.Source is not Avalonia.Media.Imaging.Bitmap bitmap ||
            m_viewportHost is null ||
            m_scaleTransform is null ||
            m_translateTransform is null ||
            m_viewportHost.Bounds.Width <= 0 ||
            m_viewportHost.Bounds.Height <= 0)
        {
            return;
        }

        SyncViewportSurface();

        double scaleX = m_viewportHost.Bounds.Width / bitmap.Size.Width;
        double scaleY = m_viewportHost.Bounds.Height / bitmap.Size.Height;
        m_zoom = Math.Min(scaleX, scaleY);
        if (double.IsNaN(m_zoom) || double.IsInfinity(m_zoom) || m_zoom <= 0)
        {
            m_zoom = 1.0;
        }

        m_scaleTransform.ScaleX = m_zoom;
        m_scaleTransform.ScaleY = m_zoom;
        m_translateTransform.X = (m_viewportHost.Bounds.Width - (bitmap.Size.Width * m_zoom)) / 2;
        m_translateTransform.Y = (m_viewportHost.Bounds.Height - (bitmap.Size.Height * m_zoom)) / 2;
        m_previewImage.Opacity = 1;

        if (DataContext is TextureAssetEditorViewModel vm)
        {
            vm.SetZoomText(m_zoom);
        }
    }

    private void SyncViewportSurface()
    {
        if (m_viewportHost is null || m_previewCanvas is null)
        {
            return;
        }

        if (m_viewportHost.Bounds.Width <= 0 || m_viewportHost.Bounds.Height <= 0)
        {
            return;
        }

        m_previewCanvas.Width = m_viewportHost.Bounds.Width;
        m_previewCanvas.Height = m_viewportHost.Bounds.Height;
    }

    // ─────────────────────────────────────────────────────────────
    // Helpers (unchanged)
    // ─────────────────────────────────────────────────────────────

    private static bool IsEmbeddedControlInteraction(object? source)
    {
        if (source is not Visual visual)
        {
            return false;
        }

        return visual.GetSelfAndVisualAncestors().Any(ancestor => ancestor is ToggleButton or Button or TextBox or SelectableTextBlock);
    }

    private static bool IsEmbeddedTextEditorInteraction(object? source)
    {
        if (source is not Visual visual)
        {
            return false;
        }

        return visual.GetSelfAndVisualAncestors().Any(ancestor => ancestor is TextBox or SelectableTextBlock);
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

    private void RefreshInspectorRows()
    {
        if (DataContext is not TextureAssetEditorViewModel viewModel)
        {
            return;
        }

        viewModel.RefreshVisibleNodes();
    }
}
