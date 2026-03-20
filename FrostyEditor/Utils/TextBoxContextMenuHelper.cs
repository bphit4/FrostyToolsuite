using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace FrostyEditor.Utils;

public static class TextBoxContextMenuHelper
{
    private sealed class TextBoxContextState
    {
        public int SelectionStart { get; set; }
        public int SelectionEnd { get; set; }
        public bool IsContextMenuPending { get; set; }
        public bool IsFlyoutOpen { get; set; }
        public MenuFlyout? ManagedFlyout { get; set; }
    }

    private static readonly ConditionalWeakTable<TextBox, TextBoxContextState> States = [];
    private static bool s_isInitialized;

    public static void Initialize()
    {
        if (s_isInitialized)
        {
            return;
        }

        s_isInitialized = true;

        InputElement.PointerPressedEvent.AddClassHandler<TextBox>(OnTextBoxPointerPressed, RoutingStrategies.Tunnel);
        Control.ContextRequestedEvent.AddClassHandler<TextBox>(OnTextBoxContextRequested);
    }

    public static void AttachManagedContextFlyout(TextBox textBox)
    {
        TextBoxContextState state = States.GetOrCreateValue(textBox);
        if (state.ManagedFlyout is null)
        {
            state.ManagedFlyout = CreateFlyout(textBox, isManagedFlyout: true);
        }

        if (!ReferenceEquals(textBox.ContextFlyout, state.ManagedFlyout))
        {
            textBox.ContextFlyout = state.ManagedFlyout;
        }
    }

    public static bool HandlePointerPressed(TextBox textBox, PointerPressedEventArgs e)
    {
        if (e.Handled || !e.GetCurrentPoint(textBox).Properties.IsRightButtonPressed || !CanManageContextMenu(textBox))
        {
            return false;
        }

        PrepareContextMenu(textBox);
        e.Handled = true;
        Dispatcher.UIThread.Post(() => OpenContextMenu(textBox), DispatcherPriority.Input);
        return true;
    }

    public static bool HandleContextRequested(TextBox textBox, ContextRequestedEventArgs e)
    {
        if (e.Handled || !CanManageContextMenu(textBox))
        {
            return false;
        }

        PrepareContextMenu(textBox);
        e.Handled = true;
        Dispatcher.UIThread.Post(() => OpenContextMenu(textBox), DispatcherPriority.Input);
        return true;
    }

    public static bool ShouldRetainEditorOnLostFocus(TextBox textBox)
    {
        return States.TryGetValue(textBox, out TextBoxContextState? state) &&
               (state.IsContextMenuPending || state.IsFlyoutOpen);
    }

    private static void OnTextBoxPointerPressed(TextBox textBox, PointerPressedEventArgs e)
    {
        HandlePointerPressed(textBox, e);
    }

    private static void OnTextBoxContextRequested(TextBox textBox, ContextRequestedEventArgs e)
    {
        HandleContextRequested(textBox, e);
    }

    private static bool CanManageContextMenu(TextBox textBox)
    {
        TextBoxContextState state = States.GetOrCreateValue(textBox);
        return textBox.ContextFlyout is null || ReferenceEquals(textBox.ContextFlyout, state.ManagedFlyout);
    }

    private static void PrepareContextMenu(TextBox textBox)
    {
        TextBoxContextState state = States.GetOrCreateValue(textBox);
        state.SelectionStart = textBox.SelectionStart;
        state.SelectionEnd = textBox.SelectionEnd;
        state.IsContextMenuPending = true;
    }

    private static void OpenContextMenu(TextBox textBox)
    {
        if (TopLevel.GetTopLevel(textBox) is null || !textBox.IsEffectivelyEnabled)
        {
            ClearContextMenuState(textBox);
            return;
        }

        RestoreSelection(textBox);
        ShowContextFlyout(textBox);
    }

    private static void ShowContextFlyout(TextBox textBox)
    {
        TextBoxContextState state = States.GetOrCreateValue(textBox);
        if (state.IsFlyoutOpen)
        {
            return;
        }

        MenuFlyout flyout = GetOrCreateFlyout(textBox);
        UpdateFlyoutItems(textBox, flyout);

        state.IsContextMenuPending = false;
        state.IsFlyoutOpen = true;
        flyout.ShowAt(textBox, true);
    }

    private static MenuFlyout GetOrCreateFlyout(TextBox textBox)
    {
        TextBoxContextState state = States.GetOrCreateValue(textBox);
        if (ReferenceEquals(textBox.ContextFlyout, state.ManagedFlyout) && state.ManagedFlyout is not null)
        {
            return state.ManagedFlyout;
        }

        return CreateFlyout(textBox, isManagedFlyout: false);
    }

    private static MenuFlyout CreateFlyout(TextBox textBox, bool isManagedFlyout)
    {
        MenuFlyout flyout = new()
        {
            Placement = PlacementMode.Pointer,
            ShowMode = FlyoutShowMode.Transient
        };

        flyout.Items.Add(CreateMenuItem("Cut", "cut", textBox, OnCutClicked));
        flyout.Items.Add(CreateMenuItem("Copy", "copy", textBox, OnCopyClicked));
        flyout.Items.Add(CreateMenuItem("Paste", "paste", textBox, OnPasteClicked));
        flyout.Items.Add(new Separator());
        flyout.Items.Add(CreateMenuItem("Select All", "select-all", textBox, OnSelectAllClicked));
        flyout.Closed += isManagedFlyout ? OnManagedFlyoutClosed : OnTransientFlyoutClosed;

        return flyout;
    }

    private static MenuItem CreateMenuItem(string header, string tag, TextBox textBox, EventHandler<RoutedEventArgs> clickHandler)
    {
        MenuItem item = new()
        {
            Header = header,
            Tag = tag,
            DataContext = textBox
        };
        item.Click += clickHandler;
        return item;
    }

    private static void UpdateFlyoutItems(TextBox textBox, MenuFlyout flyout)
    {
        foreach (MenuItem item in flyout.Items.OfType<MenuItem>())
        {
            item.DataContext = textBox;
            item.IsEnabled = item.Tag switch
            {
                "cut" => !textBox.IsReadOnly && textBox.CanCut,
                "copy" => textBox.CanCopy,
                "paste" => !textBox.IsReadOnly && textBox.CanPaste,
                "select-all" => !string.IsNullOrEmpty(textBox.Text),
                _ => item.IsEnabled
            };
        }
    }

    private static void OnCutClicked(object? sender, RoutedEventArgs e)
    {
        if (!TryGetTextBox(sender, out TextBox textBox))
        {
            return;
        }

        RestoreSelection(textBox);
        textBox.Focus();
        textBox.Cut();
    }

    private static void OnCopyClicked(object? sender, RoutedEventArgs e)
    {
        if (!TryGetTextBox(sender, out TextBox textBox))
        {
            return;
        }

        RestoreSelection(textBox);
        textBox.Focus();
        textBox.Copy();
    }

    private static void OnPasteClicked(object? sender, RoutedEventArgs e)
    {
        if (!TryGetTextBox(sender, out TextBox textBox))
        {
            return;
        }

        RestoreSelection(textBox);
        textBox.Focus();
        textBox.Paste();
    }

    private static void OnSelectAllClicked(object? sender, RoutedEventArgs e)
    {
        if (!TryGetTextBox(sender, out TextBox textBox))
        {
            return;
        }

        textBox.Focus();
        textBox.SelectAll();
    }

    private static void OnManagedFlyoutClosed(object? sender, EventArgs e)
    {
        HandleFlyoutClosed(sender, detachHandlers: false);
    }

    private static void OnTransientFlyoutClosed(object? sender, EventArgs e)
    {
        HandleFlyoutClosed(sender, detachHandlers: true);
    }

    private static void HandleFlyoutClosed(object? sender, bool detachHandlers)
    {
        if (sender is not MenuFlyout flyout || flyout.Items is null)
        {
            return;
        }

        foreach (MenuItem item in flyout.Items.OfType<MenuItem>())
        {
            if (item.DataContext is TextBox textBox && States.TryGetValue(textBox, out TextBoxContextState? state))
            {
                state.IsContextMenuPending = false;
                state.IsFlyoutOpen = false;
                Dispatcher.UIThread.Post(() =>
                {
                    if (TopLevel.GetTopLevel(textBox) is null || !textBox.IsEffectivelyEnabled)
                    {
                        return;
                    }

                    textBox.Focus();
                    RestoreSelection(textBox);
                }, DispatcherPriority.Input);
            }

            if (!detachHandlers)
            {
                continue;
            }

            item.Click -= OnCutClicked;
            item.Click -= OnCopyClicked;
            item.Click -= OnPasteClicked;
            item.Click -= OnSelectAllClicked;
        }

        if (detachHandlers)
        {
            flyout.Closed -= OnTransientFlyoutClosed;
        }
    }

    private static void RestoreSelection(TextBox textBox)
    {
        if (!States.TryGetValue(textBox, out TextBoxContextState? state))
        {
            return;
        }

        int textLength = textBox.Text?.Length ?? 0;
        int selectionStart = Math.Clamp(state.SelectionStart, 0, textLength);
        int selectionEnd = Math.Clamp(state.SelectionEnd, selectionStart, textLength);
        textBox.SelectionStart = selectionStart;
        textBox.SelectionEnd = selectionEnd;
    }

    private static void ClearContextMenuState(TextBox textBox)
    {
        if (!States.TryGetValue(textBox, out TextBoxContextState? state))
        {
            return;
        }

        state.IsContextMenuPending = false;
        state.IsFlyoutOpen = false;
    }

    private static bool TryGetTextBox(object? sender, out TextBox textBox)
    {
        textBox = sender switch
        {
            MenuItem { DataContext: TextBox menuTextBox } => menuTextBox,
            TextBox directTextBox => directTextBox,
            _ => null!
        };

        return textBox is not null;
    }
}
