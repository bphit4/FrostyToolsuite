using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FrostyEditor.Models;
using FrostyEditor.ViewModels;

namespace FrostyEditor.Views;

public partial class LoggerView : UserControl
{
    private TextBox? m_loggerTextBox;
    private LoggerViewModel? m_viewModel;

    public LoggerView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => HookViewModel();
        DetachedFromVisualTree += (_, _) => UnhookViewModel();
    }

    private void HookViewModel()
    {
        m_loggerTextBox = this.FindControl<TextBox>("LoggerTextBox");

        if (DataContext is not LoggerViewModel viewModel)
        {
            return;
        }

        if (ReferenceEquals(m_viewModel, viewModel))
        {
            return;
        }

        UnhookViewModel();
        m_viewModel = viewModel;
        m_viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ScrollToLatest();
    }

    private void UnhookViewModel()
    {
        if (m_viewModel is null)
        {
            return;
        }

        m_viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        m_viewModel = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LoggerViewModel.EntryCount) || e.PropertyName == nameof(LoggerViewModel.Text))
        {
            ScrollToLatest();
        }
    }

    private void ScrollToLatest()
    {
        if (m_loggerTextBox is null || m_viewModel is null || string.IsNullOrEmpty(m_viewModel.Text))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (m_loggerTextBox is null || m_viewModel is null || string.IsNullOrEmpty(m_viewModel.Text))
            {
                return;
            }

            m_loggerTextBox.CaretIndex = m_loggerTextBox.Text?.Length ?? 0;
        }, DispatcherPriority.Background);
    }

    private void OnReferenceListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not LoggerViewModel viewModel || sender is not ListBox listBox)
        {
            return;
        }

        if (listBox.SelectedItem is LoggerReferenceItem item && viewModel.OpenReferenceCommand.CanExecute(item))
        {
            viewModel.OpenReferenceCommand.Execute(item);
        }
    }

    private void OnReferenceRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not StyledElement { DataContext: LoggerReferenceItem item })
        {
            return;
        }

        if (sender is Visual visual)
        {
            ListBox? listBox = visual.FindAncestorOfType<ListBox>();
            if (listBox is not null)
            {
                listBox.SelectedItem = item;
            }
        }
    }

    private void OnOpenReferenceMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LoggerViewModel viewModel ||
            sender is not MenuItem menuItem ||
            menuItem.Tag is not LoggerReferenceItem item ||
            !viewModel.OpenReferenceCommand.CanExecute(item))
        {
            return;
        }

        viewModel.OpenReferenceCommand.Execute(item);
    }

    private void OnFindReferenceMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LoggerViewModel viewModel ||
            sender is not MenuItem menuItem ||
            menuItem.Tag is not LoggerReferenceItem item ||
            !viewModel.FindReferenceCommand.CanExecute(item))
        {
            return;
        }

        viewModel.FindReferenceCommand.Execute(item);
    }

    private void OnCopyReferenceAssetNameMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LoggerViewModel viewModel ||
            sender is not MenuItem menuItem ||
            menuItem.Tag is not LoggerReferenceItem item ||
            !viewModel.CopyReferenceAssetNameCommand.CanExecute(item))
        {
            return;
        }

        viewModel.CopyReferenceAssetNameCommand.Execute(item);
    }

    private void OnCopyReferenceAssetPathMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LoggerViewModel viewModel ||
            sender is not MenuItem menuItem ||
            menuItem.Tag is not LoggerReferenceItem item ||
            !viewModel.CopyReferenceAssetPathCommand.CanExecute(item))
        {
            return;
        }

        viewModel.CopyReferenceAssetPathCommand.Execute(item);
    }

    private void OnBookmarkNodePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not LoggerViewModel viewModel)
        {
            return;
        }

        Guid? bookmarkId = FindBookmarkId(e.Source);
        if (bookmarkId.HasValue)
        {
            viewModel.SelectBookmarkById(bookmarkId.Value);
        }
    }

    private void OnBookmarkTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not LoggerViewModel viewModel)
        {
            return;
        }

        Guid? bookmarkId = FindBookmarkId(e.Source);
        if (bookmarkId.HasValue)
        {
            viewModel.HandleBookmarkDoubleTapped(bookmarkId.Value);
        }
    }

    private static Guid? FindBookmarkId(object? source)
    {
        if (source is StyledElement element)
        {
            if (element.DataContext is BookmarkNodeModel directBookmark)
            {
                return directBookmark.Id;
            }

            if (element is Visual visualElement)
            {
                foreach (Visual visual in visualElement.GetVisualAncestors())
                {
                    if (visual is StyledElement styledElement && styledElement.DataContext is BookmarkNodeModel bookmark)
                    {
                        return bookmark.Id;
                    }
                }
            }
        }

        return null;
    }

    private void OnOpenBookmarkMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LoggerViewModel viewModel ||
            sender is not MenuItem menuItem ||
            menuItem.Tag is not BookmarkNodeModel bookmark)
        {
            return;
        }

        viewModel.SelectBookmarkById(bookmark.Id);
        if (viewModel.OpenBookmarkCommand.CanExecute(null))
        {
            viewModel.OpenBookmarkCommand.Execute(null);
        }
    }

    private void OnFindBookmarkMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LoggerViewModel viewModel ||
            sender is not MenuItem menuItem ||
            menuItem.Tag is not BookmarkNodeModel bookmark)
        {
            return;
        }

        viewModel.SelectBookmarkById(bookmark.Id);
        if (viewModel.FindBookmarkCommand.CanExecute(null))
        {
            viewModel.FindBookmarkCommand.Execute(null);
        }
    }

    private void OnAddBookmarkMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LoggerViewModel viewModel)
        {
            return;
        }

        if (viewModel.AddBookmarkCommand.CanExecute(null))
        {
            viewModel.AddBookmarkCommand.Execute(null);
        }
    }

    private void OnCreateBookmarkFolderMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LoggerViewModel viewModel)
        {
            return;
        }

        if (viewModel.CreateBookmarkFolderCommand.CanExecute(null))
        {
            viewModel.CreateBookmarkFolderCommand.Execute(null);
        }
    }

    private void OnRenameBookmarkMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LoggerViewModel viewModel ||
            sender is not MenuItem menuItem ||
            menuItem.Tag is not BookmarkNodeModel bookmark)
        {
            return;
        }

        viewModel.SelectBookmarkById(bookmark.Id);
        if (viewModel.RenameBookmarkCommand.CanExecute(null))
        {
            viewModel.RenameBookmarkCommand.Execute(null);
        }
    }

    private void OnDeleteBookmarkMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LoggerViewModel viewModel ||
            sender is not MenuItem menuItem ||
            menuItem.Tag is not BookmarkNodeModel bookmark)
        {
            return;
        }

        viewModel.SelectBookmarkById(bookmark.Id);
        if (viewModel.DeleteBookmarkCommand.CanExecute(null))
        {
            viewModel.DeleteBookmarkCommand.Execute(null);
        }
    }

    private void OnCopyBookmarkAssetNameMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LoggerViewModel viewModel ||
            sender is not MenuItem menuItem ||
            menuItem.Tag is not BookmarkNodeModel bookmark)
        {
            return;
        }

        viewModel.SelectBookmarkById(bookmark.Id);
        if (viewModel.CopyBookmarkAssetNameCommand.CanExecute(null))
        {
            viewModel.CopyBookmarkAssetNameCommand.Execute(null);
        }
    }

    private void OnCopyBookmarkAssetPathMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LoggerViewModel viewModel ||
            sender is not MenuItem menuItem ||
            menuItem.Tag is not BookmarkNodeModel bookmark)
        {
            return;
        }

        viewModel.SelectBookmarkById(bookmark.Id);
        if (viewModel.CopyBookmarkAssetPathCommand.CanExecute(null))
        {
            viewModel.CopyBookmarkAssetPathCommand.Execute(null);
        }
    }
}
