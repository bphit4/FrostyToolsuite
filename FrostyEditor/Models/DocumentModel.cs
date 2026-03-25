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

    public string TabBackground => IsActive ? "#5A5A5E" : "#202024";
    public string TabBorderBrush => IsActive ? "#7A7A7F" : "#3C3C40";
    public string TabForeground => IsActive ? "#FFFFFF" : "#B8B8BC";

    [ObservableProperty]
    private bool m_isActive;

    partial void OnIsActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(TabBackground));
        OnPropertyChanged(nameof(TabBorderBrush));
        OnPropertyChanged(nameof(TabForeground));
    }
}
