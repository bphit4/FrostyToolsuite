using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FrostyEditor.Models;

public partial class DocumentModel : ObservableObject
{
    public string? Key { get; set; }
    public string? Header { get; set; }
    public string? Description { get; set; }
    public object? Content { get; set; }
    public ICommand? CloseCommand { get; set; }
    public ICommand? ActivateCommand { get; set; }

    public string TabBackground => IsActive ? "#A42D21" : "#35353A";
    public string TabBorderBrush => IsActive ? "#C64A3C" : "#4E4E55";
    public string TabForeground => "#F2F2F2";

    [ObservableProperty]
    private bool m_isActive;

    partial void OnIsActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(TabBackground));
        OnPropertyChanged(nameof(TabBorderBrush));
        OnPropertyChanged(nameof(TabForeground));
    }
}
