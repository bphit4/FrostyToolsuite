using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Frosty.Sdk;
using Frosty.Sdk.IO;
using Frosty.Sdk.Managers;
using FrostyEditor.Managers;
using FrostyEditor.Utils;
using FrostyEditor.Windows;
using MsBox.Avalonia;

namespace FrostyEditor.ViewModels;

public partial class ProfileSelectViewModel : WindowViewModel
{
    public class ProfileConfig
    {
        public string Name { get; set; }
        public string Key { get; set; }
        public string Path { get; set; }
        public Avalonia.Media.Imaging.Bitmap? Banner => ProfileBannerRegistry.GetBanner(Key);
        public Avalonia.Media.Imaging.Bitmap FallbackIcon => ProfileBannerRegistry.GetFallbackIcon();
        public bool HasBanner => Banner is not null;
        public bool ShowFallbackIcon => !HasBanner;

        public ProfileConfig(string inKey)
        {
            Key = inKey;
            Path = Config.Get("GamePath", string.Empty, ConfigScope.Game, Key);
            Name = ProfilesLibrary.GetDisplayName(Key) ?? Key;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInteract))]
    [NotifyPropertyChangedFor(nameof(CanEditProfiles))]
    private ProfileConfig? m_selectedProfile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInteract))]
    [NotifyPropertyChangedFor(nameof(CanEditProfiles))]
    [NotifyPropertyChangedFor(nameof(HasStartupLog))]
    [NotifyPropertyChangedFor(nameof(IsStartupProgressIndeterminate))]
    private bool m_isBusy;

    [ObservableProperty]
    private string m_startupStatus = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStartupLog))]
    [NotifyPropertyChangedFor(nameof(IsStartupProgressIndeterminate))]
    private LoggerViewModel m_startupLogger = new();

    [ObservableProperty]
    private ObservableCollection<OperationStepViewModel> m_startupSteps = [];

    public string Heading => "Select Game";
    public string ProfileCountText => $"{Profiles.Count} configured profile(s)";
    public bool CanInteract => !IsBusy && SelectedProfile is not null;
    public bool CanEditProfiles => !IsBusy;
    public bool HasStartupLog => IsBusy || StartupLogger.EntryCount > 0;
    public bool IsStartupProgressIndeterminate => IsBusy && !StartupLogger.HasProgress;

    private OperationStepViewModel? m_profileMetadataStep;
    private OperationStepViewModel? m_initFsStep;
    private OperationStepViewModel? m_fileSystemStep;
    private OperationStepViewModel? m_typeLibraryStep;
    private OperationStepViewModel? m_resourceManagerStep;
    private OperationStepViewModel? m_assetDatabaseStep;
    private OperationStepViewModel? m_finishStartupStep;
    private string? m_assetDatabaseSubstatus;

    public ObservableCollection<ProfileConfig> Profiles { get; set; } = new();

    public ProfileSelectViewModel()
    {
        Title = "FrostyEditor";
        Width = 620;
        Height = 760;
        StartupLogger.PropertyChanged += OnStartupLoggerPropertyChanged;

        // init ProfilesLibrary to load all profile json files
        ProfilesLibrary.Initialize();

        AutoRegisterKnownGames();

        foreach (string profile in Config.GameProfiles)
        {
            ProfileConfig config = new(profile);
            if (File.Exists(config.Path))
            {
                Profiles.Add(config);
            }
            else
            {
                Config.RemoveGame(profile);
            }
        }

        SelectedProfile = Profiles.FirstOrDefault();
        Config.Save(App.ConfigPath);
    }

    private async Task<byte[]?> ResolveInitFsKeyAsync()
    {
        string configuredPath = Config.Get("InitFsKeyPath", string.Empty);
        string[] candidatePaths =
        {
            configuredPath,
            Path.Combine(Frosty.Sdk.Utils.Utils.BaseDirectory, "Keys", "initFs.key"),
            Path.Combine(AppContext.BaseDirectory, "Keys", "initFs.key"),
            Path.Combine(AppContext.BaseDirectory, "initFs.key")
        };

        foreach (string candidatePath in candidatePaths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            if (!File.Exists(candidatePath))
            {
                continue;
            }

            byte[] keyBytes = await File.ReadAllBytesAsync(candidatePath);
            if (keyBytes.Length == 0x10)
            {
                Config.Add("InitFsKeyPath", candidatePath);
                Config.Save(App.ConfigPath);
                return keyBytes;
            }
        }

        IReadOnlyList<IStorageFile>? files = await FileService.OpenFilesAsync(new FilePickerOpenOptions
        {
            Title = "Select initFs.key",
            AllowMultiple = false
        });

        IStorageFile? file = files?.FirstOrDefault();
        if (file is null || !File.Exists(file.Path.LocalPath))
        {
            return null;
        }

        byte[] selectedBytes = await File.ReadAllBytesAsync(file.Path.LocalPath);
        if (selectedBytes.Length != 0x10)
        {
            return Array.Empty<byte>();
        }

        Config.Add("InitFsKeyPath", file.Path.LocalPath);
        Config.Save(App.ConfigPath);
        return selectedBytes;
    }

    private void AutoRegisterKnownGames()
    {
        RegisterKnownGame("Madden22", @"C:\Program Files\EA Games\Madden NFL 22\Madden22.exe");
        RegisterKnownGame("Madden25", @"C:\Program Files\EA Games\Madden NFL 25\Madden25.exe");
        RegisterKnownGame("Madden26", @"C:\Program Files\EA Games\Madden NFL 26\Madden26.exe");
    }

    private static void RegisterKnownGame(string key, string gamePath)
    {
        if (!File.Exists(gamePath))
        {
            return;
        }

        string existingPath = Config.Get("GamePath", string.Empty, ConfigScope.Game, key);
        if (string.Equals(existingPath, gamePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Config.AddGame(key, gamePath);
    }

    [RelayCommand]
    private async Task AddProfile()
    {
        IReadOnlyList<IStorageFile>? files = await FileService.OpenFilesAsync(new FilePickerOpenOptions
        {
            Title = "Select Game Executable",
            AllowMultiple = false
        });

        if (files is null)
        {
            return;
        }

        foreach (IStorageFile file in files)
        {
            string key = Path.GetFileNameWithoutExtension(file.Name);
            if (Profiles.Any(profile => string.Equals(profile.Key, key, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            Config.AddGame(key, file.Path.LocalPath);
            Profiles.Add(new ProfileConfig(key));
        }
        Config.Save(App.ConfigPath);
        OnPropertyChanged(nameof(ProfileCountText));
    }

    [RelayCommand]
    private void RemoveProfile()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        Config.RemoveGame(SelectedProfile.Key);
        Profiles.Remove(SelectedProfile);
        SelectedProfile = Profiles.FirstOrDefault();
        Config.Save(App.ConfigPath);
        OnPropertyChanged(nameof(ProfileCountText));
    }

    [RelayCommand]
    private async Task SelectProfile()
    {
        if (SelectedProfile is null || IsBusy || Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktopLifetime)
        {
            return;
        }

        string failureReason = "Failed to initialize Frosty, for more information check the log.";
        IsBusy = true;
        ResetStartupSteps();
        StartupStatus = $"Initializing profile '{SelectedProfile.Name}'...";
        StartStartupStep(m_profileMetadataStep, $"Loading '{SelectedProfile.Name}'...");

        // TODO: add some kind of task window which shows a loading screen or sth

        StartupLogger = new LoggerViewModel();
        FrostyLogger.Logger = StartupLogger;

        // set base directory to the directory containing the executable
        Frosty.Sdk.Utils.Utils.BaseDirectory = Path.GetDirectoryName(AppContext.BaseDirectory) ?? string.Empty;

        StartupStatus = $"Loading profile metadata for '{SelectedProfile.Name}'...";
        if (!ProfilesLibrary.Initialize(SelectedProfile.Key))
        {
            FailActiveStartupStep($"Failed to load profile '{SelectedProfile.Key}'.");
            failureReason = $"Failed to initialize profile '{SelectedProfile.Key}'.";
            goto failed;
        }
        CompleteStartupStep(m_profileMetadataStep, SelectedProfile.Name);

        bool usedInitFsKey = false;
        if (ProfilesLibrary.RequiresInitFsKey)
        {
            StartupStatus = "Resolving initFs key...";
            StartStartupStep(m_initFsStep, "Looking for initFs.key...");
            byte[]? initFsKey = await ResolveInitFsKeyAsync();
            if (initFsKey is null)
            {
                FailStartupStep(m_initFsStep, "No initFs key selected.");
                failureReason = "No initFs key file was selected.";
                goto failed;
            }

            if (initFsKey.Length != 0x10)
            {
                FailStartupStep(m_initFsStep, "initFs key must be exactly 16 bytes.");
                failureReason = "The selected initFs key file must be exactly 16 bytes.";
                goto failed;
            }

            KeyManager.AddKey("InitFsKey", initFsKey);
            usedInitFsKey = true;
            CompleteStartupStep(m_initFsStep, "Loaded initFs key.");
        }
        else
        {
            CompleteStartupStep(m_initFsStep, "Not required for this profile.");
        }

        // init filesystem manager, this parses the layout.toc file
        try
        {
            StartupStatus = "Initializing file system...";
            StartStartupStep(m_fileSystemStep, "Reading game layout...");
            if (!FileSystemManager.Initialize(Path.GetDirectoryName(SelectedProfile.Path) ?? string.Empty))
            {
                FailStartupStep(m_fileSystemStep, "File system initialization failed.");
                failureReason = $"Failed to initialize the file system for '{SelectedProfile.Path}'.";
                goto failed;
            }
            CompleteStartupStep(m_fileSystemStep, "File system ready.");
        }
        catch (CryptographicException)
        {
            if (usedInitFsKey)
            {
                Config.Remove("InitFsKeyPath");
                Config.Save(App.ConfigPath);
                FailStartupStep(m_initFsStep, "The selected initFs key is not valid.");
                failureReason = "The selected initFs key is not valid for this game/profile.";
                goto failed;
            }

            throw;
        }

        // generate sdk if needed
        string sdkPath = ProfilesLibrary.SdkPath;
        if (!File.Exists(sdkPath))
        {
            StartupStatus = "Waiting for SDK generation...";
            ViewWindow sdkUpdateWindow = ViewWindow.Create(out SdkUpdateViewModel vm);
            await sdkUpdateWindow.ShowDialog(desktopLifetime.MainWindow!);
            if (!vm.GeneratedSdk)
            {
                failureReason = "SDK generation was cancelled or did not complete.";
                await MessageBoxManager.GetMessageBoxStandard("FrostyEditor", failureReason).ShowAsync();
                IsBusy = false;
                StartupStatus = string.Empty;
                return;
            }
        }

        StartupStatus = "Loading Frosty SDK resources...";
        Progress<(string Step, string Status, bool Completed)> startupProgress = new(update =>
            Dispatcher.UIThread.Post(() => ApplyStartupStageUpdate(update.Step, update.Status, update.Completed)));
        if (await SetupFrostySdk(startupProgress))
        {
            CompleteStartupStep(m_finishStartupStep, "Editor shell ready.");
            desktopLifetime.MainWindow = new MainWindow();
            desktopLifetime.MainWindow.Show();
            CloseWindow?.Invoke();
            return;
        }

        FailActiveStartupStep(StartupLogger.LastEntry ?? "Frosty SDK initialization failed.");
        failureReason = "Frosty SDK initialization failed.";

failed:
        Debug.WriteLine($"Profile selection failed: {failureReason}");
        await MessageBoxManager.GetMessageBoxStandard("FrostyEditor", failureReason).ShowAsync();
        IsBusy = false;
        StartupStatus = string.Empty;
        return;
    }

    public void HandleProfileDoubleTapped()
    {
        if (SelectProfileCommand.CanExecute(null))
        {
            SelectProfileCommand.Execute(null);
        }
    }

    partial void OnSelectedProfileChanged(ProfileConfig? value)
    {
        if (!IsBusy)
        {
            StartupStatus = value is null ? string.Empty : $"Ready to initialize '{value.Name}'.";
        }
    }

    partial void OnStartupLoggerChanged(LoggerViewModel value)
    {
        value.PropertyChanged += OnStartupLoggerPropertyChanged;
        OnPropertyChanged(nameof(HasStartupLog));
        OnPropertyChanged(nameof(IsStartupProgressIndeterminate));
    }

    partial void OnStartupLoggerChanging(LoggerViewModel? oldValue, LoggerViewModel newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnStartupLoggerPropertyChanged;
        }
    }

    private void OnStartupLoggerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LoggerViewModel.EntryCount) or nameof(LoggerViewModel.Text))
        {
            OnPropertyChanged(nameof(HasStartupLog));
        }

        if (e.PropertyName is nameof(LoggerViewModel.HasProgress) or nameof(LoggerViewModel.ProgressValue))
        {
            OnPropertyChanged(nameof(IsStartupProgressIndeterminate));
            UpdateAssetDatabaseProgress();
        }

        if (e.PropertyName == nameof(LoggerViewModel.LastEntry) && StartupLogger.LastEntry is not null)
        {
            HandleStartupLogEntry(GetLogMessage(StartupLogger.LastEntry));
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        CloseWindow?.Invoke();
    }

    public static async Task<bool> SetupFrostySdk(IProgress<(string Step, string Status, bool Completed)>? progress = null)
    {
        return await Task.Run(() =>
        {
            progress?.Report(("TypeLibrary", "Loading type library...", false));
            if (!TypeLibrary.Initialize())
            {
                return false;
            }
            progress?.Report(("TypeLibrary", "Type library ready.", true));

            progress?.Report(("ResourceManager", "Loading resource manager...", false));
            if (!ResourceManager.Initialize())
            {
                return false;
            }
            progress?.Report(("ResourceManager", "Resource manager ready.", true));

            progress?.Report(("AssetDatabase", "Loading asset database...", false));
            if (!AssetManager.Initialize())
            {
                return false;
            }
            progress?.Report(("AssetDatabase", "Asset database ready.", true));

            return true;
        });
    }

    private void ResetStartupSteps()
    {
        StartupSteps.Clear();
        m_profileMetadataStep = AddStartupStep("Load profile metadata");
        m_initFsStep = AddStartupStep("Resolve initFs key");
        m_fileSystemStep = AddStartupStep("Initialize file system");
        m_typeLibraryStep = AddStartupStep("Load type library");
        m_resourceManagerStep = AddStartupStep("Load resource manager");
        m_assetDatabaseStep = AddStartupStep("Load asset database");
        m_finishStartupStep = AddStartupStep("Finalize startup");
        m_assetDatabaseSubstatus = null;
    }

    private OperationStepViewModel AddStartupStep(string displayName)
    {
        OperationStepViewModel step = new() { DisplayName = displayName };
        StartupSteps.Add(step);
        return step;
    }

    private static void StartStartupStep(OperationStepViewModel? step, string? statusMessage = null)
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

    private static void CompleteStartupStep(OperationStepViewModel? step, string? statusMessage = null)
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

    private static void FailStartupStep(OperationStepViewModel? step, string? statusMessage = null)
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

    private void FailActiveStartupStep(string message)
    {
        OperationStepViewModel? activeStep = StartupSteps.FirstOrDefault(step => step.State == OperationStepState.Active);
        FailStartupStep(activeStep ?? m_finishStartupStep ?? m_assetDatabaseStep, message);
    }

    private void ApplyStartupStageUpdate(string step, string status, bool completed)
    {
        StartupStatus = status;
        switch (step)
        {
            case "TypeLibrary":
                if (completed)
                {
                    CompleteStartupStep(m_typeLibraryStep, status);
                    StartStartupStep(m_resourceManagerStep, "Waiting for resource manager...");
                }
                else
                {
                    StartStartupStep(m_typeLibraryStep, status);
                }
                break;

            case "ResourceManager":
                if (completed)
                {
                    CompleteStartupStep(m_resourceManagerStep, status);
                }
                else
                {
                    StartStartupStep(m_resourceManagerStep, status);
                }
                break;

            case "AssetDatabase":
                if (completed)
                {
                    CompleteStartupStep(m_assetDatabaseStep, status);
                    StartStartupStep(m_finishStartupStep, "Preparing editor shell...");
                }
                else
                {
                    StartStartupStep(m_assetDatabaseStep, status);
                }
                break;
        }
    }

    private void HandleStartupLogEntry(string message)
    {
        if (m_assetDatabaseStep is null)
        {
            return;
        }

        if (message.StartsWith("Loading ebx from cache", StringComparison.OrdinalIgnoreCase))
        {
            StartStartupStep(m_assetDatabaseStep, "Loading EBX from cache...");
            m_assetDatabaseSubstatus = "EBX cache";
        }
        else if (message.StartsWith("Loading res from cache", StringComparison.OrdinalIgnoreCase))
        {
            StartStartupStep(m_assetDatabaseStep, "Loading resources from cache...");
            m_assetDatabaseSubstatus = "Resource cache";
        }
        else if (message.StartsWith("Loading chunks from cache", StringComparison.OrdinalIgnoreCase))
        {
            StartStartupStep(m_assetDatabaseStep, "Loading chunks from cache...");
            m_assetDatabaseSubstatus = "Chunk cache";
        }
        else if (message.StartsWith("Loading FileInfos from catalogs", StringComparison.OrdinalIgnoreCase))
        {
            StartStartupStep(m_assetDatabaseStep, "Reading catalogs...");
            m_assetDatabaseSubstatus = null;
        }
        else if (message.StartsWith("Loading Assets from SuperBundles", StringComparison.OrdinalIgnoreCase))
        {
            StartStartupStep(m_assetDatabaseStep, "Loading assets from SuperBundles...");
            m_assetDatabaseSubstatus = null;
        }
        else if (message.StartsWith("Indexing Ebx", StringComparison.OrdinalIgnoreCase))
        {
            StartStartupStep(m_assetDatabaseStep, "Indexing EBX...");
            m_assetDatabaseSubstatus = null;
        }
        else if (message.StartsWith("Finished initializing", StringComparison.OrdinalIgnoreCase))
        {
            CompleteStartupStep(m_assetDatabaseStep, "Asset database ready.");
            StartStartupStep(m_finishStartupStep, "Preparing editor shell...");
            m_assetDatabaseSubstatus = null;
        }
        else if (message.StartsWith("No readable TypeInfo signature was found", StringComparison.OrdinalIgnoreCase))
        {
            FailStartupStep(m_assetDatabaseStep, message);
        }
    }

    private void UpdateAssetDatabaseProgress()
    {
        if (m_assetDatabaseStep is null || string.IsNullOrWhiteSpace(m_assetDatabaseSubstatus) || !StartupLogger.HasProgress)
        {
            return;
        }

        m_assetDatabaseStep.StatusMessage = $"{m_assetDatabaseSubstatus} ({StartupLogger.ProgressPercentText})";
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
