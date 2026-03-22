using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Threading;
using FrostyEditor.Controls;
using FrostyEditor.Managers;
using FrostyEditor.MeshViewportHost;
using FrostyEditor.Models;
using FrostyEditor.Utils;
using FrostyEditor.ViewModels;

namespace FrostyEditor.Views;

public partial class MeshAssetEditorView : UserControl
{
    private readonly WindowsMeshViewportHost? m_viewportHost;
    private MeshAssetEditorViewModel? m_boundViewModel;
    private int m_sceneLoadVersion;
    private CancellationTokenSource? m_sceneLoadCancellation;
    private int m_lastAppliedSceneRevision = -1;
    private int m_lastAppliedSettingsRevision = -1;
    private MeshPreviewView? m_lastAppliedPreviewView;
    private bool m_isInspectorGripDragging;
    private Point m_inspectorGripStart;
    private double m_inspectorColumnStartWidth;
    private InspectorNodeModel? m_contextNode;
    private readonly List<(ListBox NameList, ListBox ValueList)> m_inspectorListPairs = [];
    private readonly List<(ScrollViewer NameScroll, ScrollViewer ValueScroll)> m_inspectorScrollPairs = [];
    private bool m_isSyncingInspectorScroll;

    public MeshAssetEditorView()
    {
        InitializeComponent();
        m_viewportHost = this.FindControl<WindowsMeshViewportHost>("Viewport3D");
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        bool viewModelChanged = AttachViewModel(DataContext as MeshAssetEditorViewModel);
        if (viewModelChanged)
        {
            m_lastAppliedSceneRevision = -1;
            m_lastAppliedSettingsRevision = -1;
            m_lastAppliedPreviewView = null;
        }

        EnsureViewportState();
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        CancelSceneLoad();
        DetachInspectorScrollSync();
        DetachViewModel();
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(InitializeScrollSync, DispatcherPriority.Loaded);
    }

    private bool AttachViewModel(MeshAssetEditorViewModel? viewModel)
    {
        if (ReferenceEquals(m_boundViewModel, viewModel))
        {
            return false;
        }

        DetachViewModel();
        m_boundViewModel = viewModel;
        if (m_boundViewModel is not null)
        {
            m_boundViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        return true;
    }

    private void DetachViewModel()
    {
        if (m_boundViewModel is null)
        {
            return;
        }

        m_boundViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        m_boundViewModel = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MeshAssetEditorViewModel.ViewportSceneRevision))
        {
            QueueRefreshViewportScene();
            return;
        }

        if (e.PropertyName == nameof(MeshAssetEditorViewModel.ViewportSettingsRevision))
        {
            ApplyViewportSettings();
            return;
        }

        if (e.PropertyName == nameof(MeshAssetEditorViewModel.PreviewView))
        {
            ApplyViewPreset();
        }
    }

    private void EnsureViewportState()
    {
        if (m_boundViewModel is null)
        {
            return;
        }

        if (m_lastAppliedSceneRevision != m_boundViewModel.ViewportSceneRevision)
        {
            QueueRefreshViewportScene();
        }
        else
        {
            ApplyViewportSettings();
            ApplyViewPreset();
        }
    }

    private async void QueueRefreshViewportScene()
    {
        if (m_viewportHost is null || m_boundViewModel is null)
        {
            return;
        }

        int version = Interlocked.Increment(ref m_sceneLoadVersion);
        CancelSceneLoad();
        CancellationTokenSource cancellationSource = new();
        m_sceneLoadCancellation = cancellationSource;
        CancellationToken cancellationToken = cancellationSource.Token;
        MeshAssetEditorViewModel viewModel = m_boundViewModel;

        try
        {
            MeshViewportSceneBuilder.Prewarm(viewModel.LoadResult, viewModel.MeshName, ShouldLoadTexturedScene(viewModel));
            Task<MeshViewportSceneData> geometrySceneTask = Task.Run(
                () => MeshViewportSceneBuilder.Build(viewModel, MeshViewportSceneBuilder.BuildStage.GeometryOnly),
                cancellationToken);

            MeshViewportSceneData geometryScene = await geometrySceneTask;
            await ApplySceneAsync(viewModel, geometryScene, version, cancellationToken, applyViewPreset: true);

            if (ShouldLoadTexturedScene(viewModel))
            {
                Task<MeshViewportSceneData> texturedPreviewSceneTask = Task.Run(
                    () => MeshViewportSceneBuilder.Build(viewModel, MeshViewportSceneBuilder.BuildStage.TexturedLowRes),
                    cancellationToken);
                MeshViewportSceneData texturedPreviewScene = await texturedPreviewSceneTask;
                await ApplySceneAsync(viewModel, texturedPreviewScene, version, cancellationToken, applyViewPreset: false);
                MeshViewportSceneBuilder.QueueDeferredTextureWarmup(viewModel, MeshViewportSceneBuilder.BuildStage.TexturedLowRes);

                Task<MeshViewportSceneData> texturedSceneTask = Task.Run(
                    () => MeshViewportSceneBuilder.Build(viewModel, MeshViewportSceneBuilder.BuildStage.Textured),
                    cancellationToken);
                MeshViewportSceneData texturedScene = await texturedSceneTask;
                await ApplySceneAsync(viewModel, texturedScene, version, cancellationToken, applyViewPreset: false);
                MeshViewportSceneBuilder.QueueDeferredTextureWarmup(viewModel, MeshViewportSceneBuilder.BuildStage.Textured);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (cancellationToken.IsCancellationRequested ||
                version != m_sceneLoadVersion ||
                !ReferenceEquals(m_boundViewModel, viewModel))
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!cancellationToken.IsCancellationRequested &&
                    version == m_sceneLoadVersion &&
                    ReferenceEquals(m_boundViewModel, viewModel))
                {
                    viewModel.SetViewportFailureMessage(ex.GetBaseException().Message);
                }
            });
        }
    }

    private async Task ApplySceneAsync(
        MeshAssetEditorViewModel viewModel,
        MeshViewportSceneData scene,
        int version,
        CancellationToken cancellationToken,
        bool applyViewPreset)
    {
        if (cancellationToken.IsCancellationRequested ||
            version != m_sceneLoadVersion ||
            !ReferenceEquals(m_boundViewModel, viewModel))
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (cancellationToken.IsCancellationRequested ||
                version != m_sceneLoadVersion ||
                !ReferenceEquals(m_boundViewModel, viewModel))
            {
                return;
            }

            MeshViewportSceneData effectiveScene = new()
            {
                Name = scene.Name,
                ObjPath = scene.ObjPath,
                CurrentLod = viewModel.SelectedLod?.Index ?? scene.CurrentLod,
                TexturesEnabled = ShouldLoadTexturedScene(viewModel),
                Wireframe = viewModel.SelectedRenderMode == MeshViewportRenderMode.Wireframe,
                ViewPreset = ToHostPreset(viewModel.PreviewView),
                Lods = scene.Lods
            };

            m_viewportHost?.LoadScene(effectiveScene);
            ApplyViewportSettings();
            if (applyViewPreset)
            {
                ApplyViewPreset();
            }

            m_lastAppliedSceneRevision = viewModel.ViewportSceneRevision;
            viewModel.SetViewportFailureMessage(null);
        });
    }

    private static bool ShouldLoadTexturedScene(MeshAssetEditorViewModel viewModel)
    {
        return viewModel.SelectedRenderMode is MeshViewportRenderMode.Lit or MeshViewportRenderMode.Base;
    }

    private void ApplyViewportSettings()
    {
        if (m_viewportHost is null || m_boundViewModel is null)
        {
            return;
        }

        m_viewportHost.ApplySceneSettings(
            m_boundViewModel.SelectedLod?.Index ?? 0,
            m_boundViewModel.SelectedRenderMode is MeshViewportRenderMode.Lit or MeshViewportRenderMode.Base,
            m_boundViewModel.SelectedRenderMode == MeshViewportRenderMode.Wireframe);
        m_lastAppliedSettingsRevision = m_boundViewModel.ViewportSettingsRevision;
    }

    private void ApplyViewPreset()
    {
        if (m_viewportHost is null || m_boundViewModel is null)
        {
            return;
        }

        m_viewportHost.ApplyView(ToHostPreset(m_boundViewModel.PreviewView));
        m_lastAppliedPreviewView = m_boundViewModel.PreviewView;
    }

    private static MeshViewportViewPreset ToHostPreset(MeshPreviewView previewView)
    {
        return previewView switch
        {
            MeshPreviewView.Front => MeshViewportViewPreset.Front,
            MeshPreviewView.Back => MeshViewportViewPreset.Back,
            MeshPreviewView.Left => MeshViewportViewPreset.Left,
            MeshPreviewView.Right => MeshViewportViewPreset.Right,
            MeshPreviewView.Top => MeshViewportViewPreset.Top,
            MeshPreviewView.Bottom => MeshViewportViewPreset.Bottom,
            _ => MeshViewportViewPreset.Perspective
        };
    }

    private void OnResetViewClick(object? sender, RoutedEventArgs e)
    {
        m_viewportHost?.ResetView();
    }

    private void OnRefreshViewportClick(object? sender, RoutedEventArgs e)
    {
        QueueRefreshViewportScene();
    }

    private void OnInspectorTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not TabControl tabControl ||
            tabControl.SelectedItem is not TabItem tabItem)
        {
            return;
        }

        string? header = tabItem.Header?.ToString();
        m_boundViewModel?.SetActiveInspectorTab(header);
        if (string.Equals(header, "Variations", StringComparison.OrdinalIgnoreCase))
        {
            m_boundViewModel?.RequestVariationListLoad();
        }
    }

    private void OnPerspectiveViewClick(object? sender, RoutedEventArgs e)
    {
        ReapplyOrExecuteView(MeshPreviewView.Perspective, () => m_boundViewModel?.SetPerspectiveViewCommand.Execute(null));
    }

    private void OnFrontViewClick(object? sender, RoutedEventArgs e)
    {
        ReapplyOrExecuteView(MeshPreviewView.Front, () => m_boundViewModel?.SetFrontViewCommand.Execute(null));
    }

    private void OnBackViewClick(object? sender, RoutedEventArgs e)
    {
        ReapplyOrExecuteView(MeshPreviewView.Back, () => m_boundViewModel?.SetBackViewCommand.Execute(null));
    }

    private void OnLeftViewClick(object? sender, RoutedEventArgs e)
    {
        ReapplyOrExecuteView(MeshPreviewView.Left, () => m_boundViewModel?.SetLeftViewCommand.Execute(null));
    }

    private void OnRightViewClick(object? sender, RoutedEventArgs e)
    {
        ReapplyOrExecuteView(MeshPreviewView.Right, () => m_boundViewModel?.SetRightViewCommand.Execute(null));
    }

    private void OnTopViewClick(object? sender, RoutedEventArgs e)
    {
        ReapplyOrExecuteView(MeshPreviewView.Top, () => m_boundViewModel?.SetTopViewCommand.Execute(null));
    }

    private void OnBottomViewClick(object? sender, RoutedEventArgs e)
    {
        ReapplyOrExecuteView(MeshPreviewView.Bottom, () => m_boundViewModel?.SetBottomViewCommand.Execute(null));
    }

    private void ReapplyOrExecuteView(MeshPreviewView previewView, Action executeViewChange)
    {
        if (m_boundViewModel?.PreviewView == previewView)
        {
            m_viewportHost?.ApplyView(ToHostPreset(previewView));
            return;
        }

        executeViewChange();
    }

    private void InitializeScrollSync()
    {
        DetachInspectorScrollSync();
        m_inspectorListPairs.Clear();
        AddInspectorListPair("PropertyInspectorNameList", "PropertyInspectorValueList");
        AddInspectorListPair("VariationInspectorNameList", "VariationInspectorValueList");
        AddInspectorListPair("MeshInspectorNameList", "MeshInspectorValueList");
        AddInspectorListPair("SceneInspectorNameList", "SceneInspectorValueList");

        foreach ((ListBox nameList, ListBox valueList) in m_inspectorListPairs)
        {
            ScrollViewer? nameScroll = nameList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            ScrollViewer? valueScroll = valueList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (nameScroll is null || valueScroll is null)
            {
                continue;
            }

            nameScroll.ScrollChanged += OnInspectorScrollChanged;
            valueScroll.ScrollChanged += OnInspectorScrollChanged;
            m_inspectorScrollPairs.Add((nameScroll, valueScroll));
        }
    }

    private void DetachInspectorScrollSync()
    {
        foreach ((ScrollViewer nameScroll, ScrollViewer valueScroll) in m_inspectorScrollPairs)
        {
            nameScroll.ScrollChanged -= OnInspectorScrollChanged;
            valueScroll.ScrollChanged -= OnInspectorScrollChanged;
        }

        m_inspectorScrollPairs.Clear();
    }

    private void AddInspectorListPair(string nameListName, string valueListName)
    {
        ListBox? nameList = this.FindControl<ListBox>(nameListName);
        ListBox? valueList = this.FindControl<ListBox>(valueListName);
        if (nameList is null || valueList is null)
        {
            return;
        }

        m_inspectorListPairs.Add((nameList, valueList));
    }

    private void OnInspectorScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (m_isSyncingInspectorScroll || e.OffsetDelta.Y == 0)
        {
            return;
        }

        foreach ((ScrollViewer nameScroll, ScrollViewer valueScroll) in m_inspectorScrollPairs)
        {
            ScrollViewer? source = null;
            ScrollViewer? target = null;
            if (ReferenceEquals(sender, nameScroll))
            {
                source = nameScroll;
                target = valueScroll;
            }
            else if (ReferenceEquals(sender, valueScroll))
            {
                source = valueScroll;
                target = nameScroll;
            }

            if (source is null || target is null)
            {
                continue;
            }

            double targetY = source.Offset.Y;
            if (Math.Abs(target.Offset.Y - targetY) < 0.5)
            {
                return;
            }

            m_isSyncingInspectorScroll = true;
            target.Offset = new Vector(target.Offset.X, targetY);
            m_isSyncingInspectorScroll = false;
            return;
        }
    }

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

        if (row.DataContext is not InspectorNodeModel node)
        {
            return;
        }

        m_contextNode = node;

        bool hasSelection = TryGetSelectedEditorText(node, out _);
        copyItem.Header = hasSelection ? "Copy" : node.HasChildren ? "Copy Values" : "Copy Value";

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
        bool showReferenceSeparator = showReferenceActions &&
                                      (!string.IsNullOrWhiteSpace(node.PointerGuidText) ||
                                       node.CanOpenReferenceAsset ||
                                       node.CanFindReferenceAsset);
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

        if (DataContext is MeshAssetEditorViewModel viewModel)
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
            }
            else if (expandOneLevel)
            {
                node.ExpandOneLevelProgressive();
            }
            else
            {
                bool shouldExpand = !node.IsExpanded;
                if (shouldExpand)
                {
                    node.EnsureChildrenLoaded();
                }

                node.IsExpanded = shouldExpand;
            }

            RefreshInspectorRows(node);
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

    private void OnInspectorTreeExpanded(object? sender, RoutedEventArgs e)
    {
        if (e.Source is TreeViewItem { DataContext: InspectorNodeModel node })
        {
            node.IsExpanded = true;
        }
    }

    private void OnInspectorTreeCollapsed(object? sender, RoutedEventArgs e)
    {
        if (e.Source is TreeViewItem { DataContext: InspectorNodeModel node })
        {
            node.IsExpanded = false;
        }
    }

    private void OnInspectorGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MeshAssetEditorViewModel viewModel || sender is not InputElement element)
        {
            return;
        }

        m_isInspectorGripDragging = true;
        m_inspectorGripStart = e.GetPosition(this);
        m_inspectorColumnStartWidth = viewModel.InspectorNameColumnWidth;
        e.Pointer.Capture(element);
        e.Handled = true;
    }

    private void OnInspectorGripMoved(object? sender, PointerEventArgs e)
    {
        if (!m_isInspectorGripDragging || DataContext is not MeshAssetEditorViewModel viewModel)
        {
            return;
        }

        Point current = e.GetPosition(this);
        double maxWidth = Math.Max(140, Bounds.Width - 80);
        viewModel.SetManualInspectorNameColumnWidth(Math.Clamp(m_inspectorColumnStartWidth + (current.X - m_inspectorGripStart.X), 120, maxWidth));
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

    private void OnAddCollectionItemClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: InspectorNodeModel node } || DataContext is not MeshAssetEditorViewModel viewModel)
        {
            return;
        }

        if (node.AddCollectionItem(out string? error))
        {
            RefreshInspectorRows(node);
            return;
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            Frosty.Sdk.FrostyLogger.Logger?.LogWarning($"Unable to add an item to {node.Name}: {error}");
        }
    }

    private void OnClearCollectionItemsClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: InspectorNodeModel node } || DataContext is not MeshAssetEditorViewModel viewModel)
        {
            return;
        }

        if (node.ClearCollectionItems(out string? error))
        {
            RefreshInspectorRows(node);
            return;
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            Frosty.Sdk.FrostyLogger.Logger?.LogWarning($"Unable to clear {node.Name}: {error}");
        }
    }

    private void OnRemoveCollectionEntryClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: InspectorNodeModel node } || DataContext is not MeshAssetEditorViewModel viewModel)
        {
            return;
        }

        if (node.RemoveCollectionEntry(out string? error))
        {
            viewModel.RebuildInspectorPanels();
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

        if (!node.TryGetSelectedAssetPointerOptions(out var options, out string? error))
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

        foreach (var option in options)
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
        RefreshInspectorRows(node);
    }

    private void OnExpandAllLevelsMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (!TryGetContextNode(sender, out InspectorNodeModel node))
        {
            return;
        }

        node.ExpandAllDescendants();
        RefreshInspectorRows(node);
    }

    private void OnCollapseOneLevelMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (!TryGetContextNode(sender, out InspectorNodeModel node))
        {
            return;
        }

        node.CollapseOneLevelProgressive();
        RefreshInspectorRows(node);
    }

    private void OnCollapseAllLevelsMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (!TryGetContextNode(sender, out InspectorNodeModel node))
        {
            return;
        }

        node.CollapseAllDescendants();
        RefreshInspectorRows(node);
    }

    private static bool IsEmbeddedControlInteraction(object? source)
    {
        if (source is not Visual visual)
        {
            return false;
        }

        return visual.GetSelfAndVisualAncestors().Any(ancestor => ancestor is Avalonia.Controls.Primitives.ToggleButton or Button or TextBox);
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
        if (DataContext is not MeshAssetEditorViewModel viewModel)
        {
            return;
        }

        PreserveInspectorViewports(() =>
        {
            if (node is not null)
            {
                if (!viewModel.TryRefreshInspectorRows(node))
                {
                    viewModel.RebuildInspectorPanels();
                }

                return;
            }

            viewModel.RefreshAllInspectorRows();
        });
    }

    private void PreserveInspectorViewports(Action refreshAction)
    {
        List<(ScrollViewer ScrollViewer, Vector Offset)> offsets = [];
        foreach ((ScrollViewer nameScroll, ScrollViewer valueScroll) in m_inspectorScrollPairs)
        {
            offsets.Add((nameScroll, nameScroll.Offset));
            offsets.Add((valueScroll, valueScroll.Offset));
        }

        refreshAction();

        Dispatcher.UIThread.Post(() =>
        {
            m_isSyncingInspectorScroll = true;
            try
            {
                foreach ((ScrollViewer scrollViewer, Vector offset) in offsets)
                {
                    scrollViewer.Offset = offset;
                }
            }
            finally
            {
                m_isSyncingInspectorScroll = false;
            }
        }, DispatcherPriority.Background);
    }

    private void CancelSceneLoad()
    {
        if (m_sceneLoadCancellation is null)
        {
            return;
        }

        m_sceneLoadCancellation.Cancel();
        m_sceneLoadCancellation.Dispose();
        m_sceneLoadCancellation = null;
    }
}
