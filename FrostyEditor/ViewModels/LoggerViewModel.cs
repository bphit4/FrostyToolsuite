using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Frosty.Sdk.Interfaces;

namespace FrostyEditor.ViewModels;

public partial class LoggerViewModel : ViewModelBase, ILogger
{
    private static readonly string s_info = "INFO";
    private static readonly string s_warn = "WARN";
    private static readonly string s_error = "ERROR";

    [ObservableProperty]
    private string? m_text;

    [ObservableProperty]
    private string? m_lastEntry = "Waiting for editor activity.";

    [ObservableProperty]
    private int m_entryCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercentText))]
    private bool m_hasProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercentText))]
    private double m_progressValue;

    public ObservableCollection<string> Lines { get; } = [];
    public string ProgressPercentText => HasProgress ? $"{ProgressValue:P0}" : "Working...";

    public void LogInfo(string message)
    {
        Append(s_info, message);
    }

    public void LogWarning(string message)
    {
        Append(s_warn, message);
    }

    public void LogError(string message)
    {
        Append(s_error, message);
    }

    public void LogProgress(double progress)
    {
        ProgressValue = Math.Clamp(progress, 0.0, 1.0);
        HasProgress = true;
        LastEntry = $"PROGRESS - {ProgressValue:P0}";
    }

    private void Append(string level, string message)
    {
        HasProgress = false;
        string line = $"{DateTime.Now:HH:mm:ss}  {level}  {message}";
        Text = string.IsNullOrWhiteSpace(Text) ? line : $"{Text}{Environment.NewLine}{line}";
        Lines.Add(line);
        LastEntry = line;
        EntryCount++;
    }
}
