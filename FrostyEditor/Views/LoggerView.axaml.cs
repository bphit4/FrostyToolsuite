using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
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
}
