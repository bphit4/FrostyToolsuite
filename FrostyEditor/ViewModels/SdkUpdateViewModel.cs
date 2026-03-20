using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Frosty.Sdk;
using Frosty.Sdk.Sdk;

namespace FrostyEditor.ViewModels;

public partial class SdkUpdateViewModel : WindowViewModel
{
    public sealed class SdkProcessItem
    {
        public required int Id { get; init; }
        public required string ProcessName { get; init; }
        public required string DisplayPath { get; init; }
        public required string WindowTitle { get; init; }
    }

    [ObservableProperty]
    private ObservableCollection<SdkProcessItem> m_detectedProcesses = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreateSdk))]
    private SdkProcessItem? m_selectedProcess;

    [ObservableProperty]
    private string m_statusMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreateSdk))]
    [NotifyPropertyChangedFor(nameof(ShowActivityLog))]
    [NotifyPropertyChangedFor(nameof(IsProgressIndeterminate))]
    private bool m_isGenerating;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowActivityLog))]
    [NotifyPropertyChangedFor(nameof(IsProgressIndeterminate))]
    private LoggerViewModel m_logger = new();

    [ObservableProperty]
    private ObservableCollection<OperationStepViewModel> m_sdkSteps = [];

    public bool GeneratedSdk;

    public string ProfileDisplayName => ProfilesLibrary.DisplayName;
    public string ProfileKey => ProfilesLibrary.ProfileName;
    public string MissingSdkMessage => $"The SDK for '{ProfileDisplayName}' is missing or out of date.";
    public string LaunchInstructions => $"Launch the game for profile '{ProfileKey}', then select the detected process below.";
    public bool CanCreateSdk => !IsGenerating && SelectedProcess is not null;
    public bool ShowActivityLog => IsGenerating || Logger.EntryCount > 0;
    public bool IsProgressIndeterminate => IsGenerating && !Logger.HasProgress;

    private OperationStepViewModel? m_attachProcessStep;
    private OperationStepViewModel? m_scanTypeInfoStep;
    private OperationStepViewModel? m_dumpTypesStep;
    private OperationStepViewModel? m_createSdkStep;

    public SdkUpdateViewModel()
    {
        Title = "Update Profile SDK";
        Width = 640;
        Height = 620;
        Logger.PropertyChanged += OnLoggerPropertyChanged;
        ResetSdkSteps();

        if (FrostyLogger.Logger is LoggerViewModel logger)
        {
            Logger = logger;
        }

        RefreshProcesses();
    }

    [RelayCommand]
    private void RefreshProcesses()
    {
        DetectedProcesses.Clear();

        foreach (SdkProcessItem item in Process.GetProcesses()
                     .Select(CreateProcessItem)
                     .Where(static item => item is not null)
                     .Cast<SdkProcessItem>()
                     .OrderBy(static item => item.ProcessName)
                     .ThenBy(static item => item.Id))
        {
            DetectedProcesses.Add(item);
        }

        SelectedProcess = DetectedProcesses.FirstOrDefault();
        StatusMessage = DetectedProcesses.Count == 0
            ? $"No running process matching \"{ProfileKey}\" was found. Launch the game, then click Refresh."
            : $"Detected {DetectedProcesses.Count} matching process{(DetectedProcesses.Count == 1 ? string.Empty : "es")}.";
    }

    [RelayCommand]
    private async Task CreateSdk()
    {
        if (SelectedProcess is null)
        {
            StatusMessage = $"No running process matching \"{ProfileKey}\" is selected.";
            return;
        }

        IsGenerating = true;
        ResetSdkSteps();
        StartSdkStep(m_attachProcessStep, $"Waiting on PID {SelectedProcess.Id}...");
        StatusMessage = $"Generating SDK from {SelectedProcess.ProcessName} (PID {SelectedProcess.Id})...";
        FrostyLogger.Logger = Logger;

        try
        {
            bool generated = await Task.Run(() =>
            {
                using Process process = Process.GetProcessById(SelectedProcess.Id);
                TypeSdkGenerator typeSdkGenerator = new();

                Dispatcher.UIThread.Post(() =>
                {
                    CompleteSdkStep(m_attachProcessStep, $"Attached to PID {SelectedProcess.Id}");
                    StartSdkStep(m_scanTypeInfoStep, "Searching for a readable TypeInfo signature...");
                });
                Logger.LogInfo($"Attached to {SelectedProcess.ProcessName} (PID {SelectedProcess.Id}).");
                Logger.LogInfo("Scanning live game memory for type information...");
                if (!typeSdkGenerator.DumpTypes(process))
                {
                    return false;
                }

                Dispatcher.UIThread.Post(() => StartSdkStep(m_createSdkStep, "Compiling generated SDK assembly..."));
                Logger.LogInfo("Compiling generated SDK assembly...");
                return typeSdkGenerator.CreateSdk(ProfilesLibrary.SdkPath);
            });

            GeneratedSdk = generated;
            if (!generated)
            {
                FailActiveSdkStep(Logger.LastEntry ?? "SDK generation failed.");
            }
            StatusMessage = generated
                ? "SDK was generated successfully."
                : Logger.LastEntry ?? "SDK generation failed. Check the log output and make sure the game is running.";

            if (generated)
            {
                CloseWindow?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex.Message);
            FailActiveSdkStep(ex.Message);
            StatusMessage = $"SDK generation failed: {ex.Message}";
        }
        finally
        {
            IsGenerating = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        CloseWindow?.Invoke();
    }

    partial void OnLoggerChanged(LoggerViewModel value)
    {
        value.PropertyChanged += OnLoggerPropertyChanged;
        OnPropertyChanged(nameof(ShowActivityLog));
        OnPropertyChanged(nameof(IsProgressIndeterminate));
    }

    partial void OnLoggerChanging(LoggerViewModel? oldValue, LoggerViewModel newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnLoggerPropertyChanged;
        }
    }

    private void OnLoggerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LoggerViewModel.EntryCount) or nameof(LoggerViewModel.Text) or nameof(LoggerViewModel.LastEntry))
        {
            OnPropertyChanged(nameof(ShowActivityLog));
        }

        if (e.PropertyName is nameof(LoggerViewModel.HasProgress) or nameof(LoggerViewModel.ProgressValue))
        {
            OnPropertyChanged(nameof(IsProgressIndeterminate));
        }

        if (e.PropertyName == nameof(LoggerViewModel.LastEntry) && Logger.LastEntry is not null)
        {
            HandleSdkLogEntry(GetLogMessage(Logger.LastEntry));
        }
    }

    private SdkProcessItem? CreateProcessItem(Process process)
    {
        try
        {
            string processName = process.ProcessName;
            string moduleName = process.MainModule?.ModuleName ?? string.Empty;
            string fileName = Path.GetFileNameWithoutExtension(moduleName);

            if (!MatchesProfile(processName) && !MatchesProfile(fileName))
            {
                return null;
            }

            return new SdkProcessItem
            {
                Id = process.Id,
                ProcessName = processName,
                DisplayPath = process.MainModule?.FileName ?? moduleName,
                WindowTitle = process.MainWindowTitle
            };
        }
        catch
        {
            return MatchesProfile(process.ProcessName)
                ? new SdkProcessItem
                {
                    Id = process.Id,
                    ProcessName = process.ProcessName,
                    DisplayPath = "Path unavailable",
                    WindowTitle = process.MainWindowTitle
                }
                : null;
        }
    }

    private bool MatchesProfile(string value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.Contains(ProfileKey, StringComparison.OrdinalIgnoreCase);
    }

    private void ResetSdkSteps()
    {
        SdkSteps.Clear();
        m_attachProcessStep = AddSdkStep("Attach to running game");
        m_scanTypeInfoStep = AddSdkStep("Scan for type info offset");
        m_dumpTypesStep = AddSdkStep("Dump types from memory");
        m_createSdkStep = AddSdkStep("Create SDK assembly");
    }

    private OperationStepViewModel AddSdkStep(string displayName)
    {
        OperationStepViewModel step = new() { DisplayName = displayName };
        SdkSteps.Add(step);
        return step;
    }

    private static void StartSdkStep(OperationStepViewModel? step, string? statusMessage = null)
    {
        if (step is null)
        {
            return;
        }

        step.State = OperationStepState.Active;
        if (!string.IsNullOrWhiteSpace(statusMessage))
        {
            step.StatusMessage = statusMessage;
        }
    }

    private static void CompleteSdkStep(OperationStepViewModel? step, string? statusMessage = null)
    {
        if (step is null)
        {
            return;
        }

        step.State = OperationStepState.CompletedSuccessful;
        if (!string.IsNullOrWhiteSpace(statusMessage))
        {
            step.StatusMessage = statusMessage;
        }
    }

    private static void FailSdkStep(OperationStepViewModel? step, string? statusMessage = null)
    {
        if (step is null)
        {
            return;
        }

        step.State = OperationStepState.CompletedFail;
        if (!string.IsNullOrWhiteSpace(statusMessage))
        {
            step.StatusMessage = statusMessage;
        }
    }

    private void FailActiveSdkStep(string message)
    {
        OperationStepViewModel? activeStep = SdkSteps.FirstOrDefault(step => step.State == OperationStepState.Active);
        FailSdkStep(activeStep ?? m_createSdkStep ?? m_scanTypeInfoStep, message);
    }

    private void HandleSdkLogEntry(string message)
    {
        if (message.StartsWith("Scanning live game memory", StringComparison.OrdinalIgnoreCase))
        {
            StartSdkStep(m_scanTypeInfoStep, "Scanning process memory...");
        }
        else if (message.StartsWith("Using fallback TypeInfo signature", StringComparison.OrdinalIgnoreCase))
        {
            StartSdkStep(m_scanTypeInfoStep, message);
        }
        else if (message.StartsWith("Matched TypeInfo signature at", StringComparison.OrdinalIgnoreCase))
        {
            StartSdkStep(m_scanTypeInfoStep, message);
        }
        else if (message.StartsWith("Dumping types at offset", StringComparison.OrdinalIgnoreCase))
        {
            CompleteSdkStep(m_scanTypeInfoStep, message.Replace("Dumping types at ", "Offset "));
            StartSdkStep(m_dumpTypesStep, "Reading type infos from live memory...");
        }
        else if (message.Contains("types in the games memory", StringComparison.OrdinalIgnoreCase))
        {
            CompleteSdkStep(m_dumpTypesStep, message);
        }
        else if (message.Equals("Creating sdk", StringComparison.OrdinalIgnoreCase))
        {
            StartSdkStep(m_createSdkStep, "Compiling generated SDK assembly...");
        }
        else if (message.Equals("Successfully compiled sdk", StringComparison.OrdinalIgnoreCase))
        {
            CompleteSdkStep(m_createSdkStep, "SDK compiled successfully.");
        }
        else if (message.StartsWith("No readable TypeInfo signature was found", StringComparison.OrdinalIgnoreCase))
        {
            FailSdkStep(m_scanTypeInfoStep, message);
        }
        else if (message.Contains("may need an updated TypeInfoSignature", StringComparison.OrdinalIgnoreCase))
        {
            FailSdkStep(m_scanTypeInfoStep, message);
        }
        else if (message.StartsWith("Could not compile sdk", StringComparison.OrdinalIgnoreCase))
        {
            FailSdkStep(m_createSdkStep, message);
        }
    }

    private static string GetLogMessage(string line)
    {
        int separatorIndex = line.IndexOf("  ", StringComparison.Ordinal);
        if (separatorIndex < 0)
        {
            return line;
        }

        separatorIndex = line.IndexOf("  ", separatorIndex + 2, StringComparison.Ordinal);
        if (separatorIndex < 0 || separatorIndex + 2 >= line.Length)
        {
            return line;
        }

        return line[(separatorIndex + 2)..];
    }
}
