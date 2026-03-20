using CommunityToolkit.Mvvm.ComponentModel;

namespace FrostyEditor.ViewModels;

public enum OperationStepState
{
    Inactive,
    Active,
    CompletedSuccessful,
    CompletedFail
}

public partial class OperationStepViewModel : ViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatePrefix))]
    private OperationStepState m_state;

    [ObservableProperty]
    private string m_displayName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string? m_statusMessage;

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public string StatePrefix => State switch
    {
        OperationStepState.Active => "[>]",
        OperationStepState.CompletedSuccessful => "[x]",
        OperationStepState.CompletedFail => "[!]",
        _ => "[ ]"
    };
}
