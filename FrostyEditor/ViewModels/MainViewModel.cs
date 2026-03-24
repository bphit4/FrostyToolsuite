using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Frosty.Sdk;
using FrostyEditor.Managers;
using FrostyEditor.Models;
using FrostyEditor.Utils;
using FrostyEditor.Windows;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;

namespace FrostyEditor.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private string? m_currentProjectDirectory;
    private string m_currentProjectName = "FrostyToolsuite Project";
    private bool m_isClosingDocuments;

    [ObservableProperty]
    private MenuViewModel m_menu = new();

    [ObservableProperty]
    private DataExplorerViewModel m_dataExplorer = null!;

    [ObservableProperty]
    private LoggerViewModel m_logger = new();

    [ObservableProperty]
    private DocumentModel? m_activeDocument;

    public ObservableCollection<DocumentModel> Documents { get; } = new();

    public string WindowTitle => "Frosty Editor 2.0";
    public string WindowSubtitle => "Modern Avalonia rewrite of the M24 editor shell";
    public string DocumentStatus => $"{Documents.Count} open document(s)";
    public string ActiveDocumentTitle => ActiveDocument?.Header ?? "Home Page";
    public string BuildStatus => Logger.LastEntry ?? "Ready";

    public MainViewModel()
    {
        if (App.MainViewModel is not null)
        {
            throw new Exception();
        }

        App.MainViewModel = this;
        AddStartPage();

        if (FrostyLogger.Logger is LoggerViewModel logger)
        {
            Logger = logger;
        }

        Logger.LogInfo("Editor shell initialized.");
        MeshVariationDatabaseManager.StartBackgroundWarmup();
        DataExplorer = new DataExplorerViewModel();
        DataExplorer.PropertyChanged += OnDataExplorerPropertyChanged;
        Logger.SetExplorerSelectedAsset(DataExplorer.SelectedAssetEntry);
        _ = Task.Run(async () =>
        {
            await Task.Delay(250).ConfigureAwait(false);
            MeshVariationDatabaseManager.StartFullCacheBuildIfNeeded();
        });
    }

    public void AddEditor(AssetEditorViewModel inEditor)
    {
        AddTabItem(inEditor.DocumentKey, inEditor.Header, inEditor.Path, inEditor);
    }

    public void AddDocument(string key, string header, string? description, object content)
    {
        AddTabItem(key, header, description, content);
    }

    public bool ActivateDocumentByKey(string key)
    {
        DocumentModel? existing = Documents.FirstOrDefault(doc =>
            string.Equals(doc.Key, key, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return false;
        }

        ActiveDocument = existing;
        OnPropertyChanged(nameof(DocumentStatus));
        OnPropertyChanged(nameof(ActiveDocumentTitle));
        return true;
    }

    public DocumentModel? GetDocumentByKey(string key)
    {
        return Documents.FirstOrDefault(doc =>
            string.Equals(doc.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    public bool RefreshDocumentByKey(string key)
    {
        DocumentModel? existing = Documents.FirstOrDefault(doc =>
            string.Equals(doc.Key, key, StringComparison.OrdinalIgnoreCase));
        if (existing?.Content is not IReloadableDocument reloadable)
        {
            return false;
        }

        reloadable.ReloadFromSource();
        return true;
    }

    [RelayCommand]
    private void NewProject()
    {
        ProjectPersistenceManager.ResetSession();
        m_currentProjectDirectory = null;
        m_currentProjectName = "FrostyToolsuite Project";
        ReloadOpenDocumentsFromSource();
        RefreshOpenDocumentSessionState();
        DataExplorer.RefreshExplorerState();
        Logger.LogInfo("Started a new project session.");
    }

    [RelayCommand]
    private async Task OpenProject()
    {
        IReadOnlyList<IStorageFolder>? folders = await FileService.OpenFoldersAsync(new FolderPickerOpenOptions
        {
            Title = "Open Project Folder",
            AllowMultiple = false
        });

        IStorageFolder? folder = folders?.FirstOrDefault();
        if (folder is null)
        {
            return;
        }

        ProjectOperationResult result = ProjectPersistenceManager.LoadProject(folder.Path.LocalPath);
        if (!result.Success)
        {
            Logger.LogWarning(result.Message);
            return;
        }

        m_currentProjectDirectory = result.Path;
        m_currentProjectName = Path.GetFileName(result.Path) ?? "FrostyToolsuite Project";
        ReloadOpenDocumentsFromSource();
        RefreshOpenDocumentSessionState();
        DataExplorer.RefreshExplorerState();
        Logger.LogInfo(result.Message);
    }

    [RelayCommand]
    private async Task SaveProject()
    {
        if (string.IsNullOrWhiteSpace(m_currentProjectDirectory))
        {
            await SaveProjectAs();
            return;
        }

        ProjectOperationResult result = ProjectPersistenceManager.SaveProject(m_currentProjectDirectory, m_currentProjectName);
        if (!result.Success)
        {
            Logger.LogWarning(result.Message);
            return;
        }

        RefreshOpenDocumentSessionState();
        DataExplorer.RefreshExplorerState();
        Logger.LogInfo(result.Message);
    }

    [RelayCommand]
    private async Task SaveProjectAs()
    {
        IReadOnlyList<IStorageFolder>? folders = await FileService.OpenFoldersAsync(new FolderPickerOpenOptions
        {
            Title = "Save Project Folder",
            AllowMultiple = false
        });

        IStorageFolder? folder = folders?.FirstOrDefault();
        if (folder is null)
        {
            return;
        }

        ProjectOperationResult result = ProjectPersistenceManager.SaveProject(folder.Path.LocalPath, Path.GetFileName(folder.Path.LocalPath));
        if (!result.Success)
        {
            Logger.LogWarning(result.Message);
            return;
        }

        m_currentProjectDirectory = result.Path;
        m_currentProjectName = Path.GetFileName(result.Path) ?? "FrostyToolsuite Project";
        RefreshOpenDocumentSessionState();
        DataExplorer.RefreshExplorerState();
        Logger.LogInfo(result.Message);
    }

    [RelayCommand]
    private async Task ExportMod()
    {
        IStorageFile? file = await FileService.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Mod",
            SuggestedFileName = $"{m_currentProjectName}.fbmod",
            DefaultExtension = "fbmod",
            FileTypeChoices =
            [
                new FilePickerFileType("Frosty Mod (*.fbmod)") { Patterns = ["*.fbmod"] }
            ]
        });

        if (file is null)
        {
            return;
        }

        ProjectOperationResult result = ProjectPersistenceManager.ExportMod(file.Path.LocalPath, m_currentProjectName);
        if (!result.Success)
        {
            Logger.LogWarning(result.Message);
            return;
        }

        Logger.LogInfo(result.Message);
    }

    [RelayCommand]
    private void Launch()
    {
        Logger.LogInfo("Launch UI is wired in, but the mod-launch pipeline is intentionally not ported in this pass.");
    }

    [RelayCommand]
    private void OpenAbout()
    {
        Logger.LogInfo("Frosty Editor 2.0 is running on Avalonia with the M24 shell rebuilt.");
    }

    [RelayCommand]
    private async Task CloseActiveDocument()
    {
        if (ActiveDocument is null || Documents.Count == 0)
        {
            return;
        }

        await CloseDocumentAsync(ActiveDocument).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task CloseAllDocuments()
    {
        await CloseAllDocumentsCoreAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ExitApplication()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        if (!await ConfirmCloseAllDocumentsAsync().ConfigureAwait(true))
        {
            return;
        }

        desktop.Shutdown();
    }

    [RelayCommand]
    private void ResetWindowSettings()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow is MainWindow window)
        {
            window.ResetWindowSettings();
            Logger.LogInfo("Window settings reset to defaults.");
        }
    }

    partial void OnActiveDocumentChanged(DocumentModel? value)
    {
        foreach (DocumentModel document in Documents)
        {
            document.IsActive = ReferenceEquals(document, value);
        }

        Logger.SetActiveDocumentAsset(value?.Content as AssetEditorViewModel is AssetEditorViewModel editor ? editor.Entry : null);
        OnPropertyChanged(nameof(ActiveDocumentTitle));
    }

    private void OnDataExplorerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DataExplorerViewModel.SelectedAssetEntry))
        {
            Logger.SetExplorerSelectedAsset(DataExplorer.SelectedAssetEntry);
        }
    }

    public async Task<bool> CloseDocumentAsync(DocumentModel? document)
    {
        if (document is null || Documents.Count == 0)
        {
            return true;
        }

        if (!await EnsureDocumentCanCloseAsync(document).ConfigureAwait(true))
        {
            return false;
        }

        CloseDocumentCore(document);
        return true;
    }

    public async Task<bool> ConfirmCloseAllDocumentsAsync()
    {
        if (m_isClosingDocuments)
        {
            return false;
        }

        return await CloseAllDocumentsCoreAsync(shutdownMode: true).ConfigureAwait(true);
    }

    private void AddStartPage()
    {
        AddTabItem(
            "home-page",
            "Home Page",
            "Convenient links, current work, and shell updates.",
            new HomePageContent());
    }

    private void AddTabItem(string inKey, string inHeader, string? inDescription, object? inContent)
    {
        DocumentModel? existing = Documents.FirstOrDefault(doc =>
            string.Equals(doc.Key, inKey, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            ActiveDocument = existing;
            OnPropertyChanged(nameof(DocumentStatus));
            OnPropertyChanged(nameof(ActiveDocumentTitle));
            return;
        }

        DocumentModel model = new()
        {
            Key = inKey,
            Header = inHeader,
            Description = inDescription,
            Content = inContent
        };

        model.CloseCommand = new AsyncRelayCommand(() => CloseDocumentAsync(model));
        model.ActivateCommand = new RelayCommand(() => ActiveDocument = model);

        Documents.Add(model);
        ActiveDocument = model;

        OnPropertyChanged(nameof(DocumentStatus));
        OnPropertyChanged(nameof(ActiveDocumentTitle));
    }

    private void AddTabItem(string inKey, string inHeader, string inLead, string inBody)
    {
        AddTabItem(inKey, inHeader, inLead, new TextDocumentContent
        {
            Text = $"{inLead}{Environment.NewLine}{Environment.NewLine}{inBody}"
        });
    }

    private async Task<bool> CloseAllDocumentsCoreAsync(bool shutdownMode = false)
    {
        if (m_isClosingDocuments)
        {
            return false;
        }

        m_isClosingDocuments = true;
        try
        {
            foreach (DocumentModel document in Documents.ToList())
            {
                if (!await EnsureDocumentCanCloseAsync(document).ConfigureAwait(true))
                {
                    return false;
                }
            }

            Documents.Clear();
            if (!shutdownMode)
            {
                Logger.LogInfo("Closed all open tabs.");
                AddStartPage();
            }

            ActiveDocument = Documents.FirstOrDefault();
            OnPropertyChanged(nameof(DocumentStatus));
            OnPropertyChanged(nameof(ActiveDocumentTitle));
            return true;
        }
        finally
        {
            m_isClosingDocuments = false;
        }
    }

    private async Task<bool> EnsureDocumentCanCloseAsync(DocumentModel document)
    {
        if (document.Content is not ISaveableDocument saveable || !saveable.HasUnsavedChanges)
        {
            return true;
        }

        ButtonResult result = await MessageBoxManager
            .GetMessageBoxStandard(
                "FrostyEditor",
                $"Save changes to '{document.Header}' before closing?",
                ButtonEnum.YesNoCancel,
                Icon.Question)
            .ShowAsync()
            .ConfigureAwait(true);

        if (result == ButtonResult.Cancel)
        {
            return false;
        }

        if (result == ButtonResult.Yes)
        {
            if (!saveable.CanSaveDocument)
            {
                Logger.LogWarning($"'{document.Header}' has unsaved changes but cannot be saved in its current state.");
                return false;
            }

            bool saved = await saveable.SaveDocumentAsync().ConfigureAwait(true);
            if (!saved)
            {
                Logger.LogWarning($"Save failed for '{document.Header}'.");
                return false;
            }

            if (document.Content is ISessionStateAwareDocument sessionAware)
            {
                sessionAware.RefreshSessionState();
            }
        }

        return true;
    }

    private void CloseDocumentCore(DocumentModel document)
    {
        int index = Documents.IndexOf(document);
        if (index < 0)
        {
            return;
        }

        Documents.RemoveAt(index);
        Logger.LogInfo($"Closed tab '{document.Header}'.");

        if (Documents.Count == 0)
        {
            AddStartPage();
            return;
        }

        ActiveDocument = Documents[Math.Clamp(index - 1, 0, Documents.Count - 1)];
        OnPropertyChanged(nameof(DocumentStatus));
        OnPropertyChanged(nameof(ActiveDocumentTitle));
    }

    private void RefreshOpenDocumentSessionState()
    {
        foreach (DocumentModel document in Documents)
        {
            if (document.Content is ISessionStateAwareDocument sessionAware)
            {
                sessionAware.RefreshSessionState();
            }
        }
    }

    private void ReloadOpenDocumentsFromSource()
    {
        foreach (DocumentModel document in Documents)
        {
            if (document.Content is IReloadableDocument reloadable)
            {
                reloadable.ReloadFromSource();
            }
        }
    }
}
