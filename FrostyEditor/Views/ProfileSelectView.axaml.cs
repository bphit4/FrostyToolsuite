using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using FrostyEditor.ViewModels;

namespace FrostyEditor.Views;

public partial class ProfileSelectView : UserControl
{
    public ProfileSelectView()
    {
        InitializeComponent();
    }

    private void ProfilesList_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is ProfileSelectViewModel viewModel)
        {
            viewModel.HandleProfileDoubleTapped();
        }
    }
}
