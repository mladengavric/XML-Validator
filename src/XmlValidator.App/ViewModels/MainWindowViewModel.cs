using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XmlValidator.App.Services;
using XmlValidator.Core.Schemas;

namespace XmlValidator.App.ViewModels;

/// <summary>Stavka liste nedavnih fajlova.</summary>
public sealed record RecentFileItem(string Path)
{
    public string FileName => System.IO.Path.GetFileName(Path);

    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
}

/// <summary>Glavni prozor: komande za fajl, tabovi, nedavni fajlovi i statusna traka.</summary>
public sealed partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    public const string AppTitle = "XML Validator";

    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _ui;
    private readonly SchemaStore _store;
    private readonly ISchemaProvider _schemas;
    private readonly IAppLog _log;

    public MainWindowViewModel(
        ISettingsService settings,
        IDialogService dialogs,
        IUiDispatcher ui,
        SchemaStore store,
        ISchemaProvider schemas,
        IAppLog log,
        ValidationTabViewModel validation,
        CleaningTabViewModel cleaning)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(validation);
        ArgumentNullException.ThrowIfNull(cleaning);
        _settings = settings;
        _dialogs = dialogs;
        _ui = ui;
        _store = store;
        _schemas = schemas;
        _log = log;
        Validation = validation;
        Cleaning = cleaning;

        _settings.Changed += OnSettingsChanged;
        _store.Changed += OnSchemaStoreChanged;
        RecentFiles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRecentFiles));
        LoadRecentFiles();
    }

    public ValidationTabViewModel Validation { get; }

    public CleaningTabViewModel Cleaning { get; }

    /// <summary>Izabran tab: 0 = Validacija, 1 = Čišćenje faktura.</summary>
    [ObservableProperty]
    public partial int SelectedTabIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDocument), nameof(Title))]
    [NotifyCanExecuteChangedFor(nameof(SaveAsCommand))]
    public partial OpenedDocument? Document { get; private set; }

    public bool HasDocument => Document is not null;

    /// <summary>Naslov prozora.</summary>
    public string Title => Document is null ? AppTitle : $"{Document.FileName} — {AppTitle}";

    [ObservableProperty]
    public partial string StatusMessage { get; private set; } = "Spremno.";

    [ObservableProperty]
    public partial MessageSeverity StatusSeverity { get; private set; } = MessageSeverity.Info;

    /// <summary>Naziv šeme koja se stvarno koristi za validaciju.</summary>
    [ObservableProperty]
    public partial string ActiveSchemaName { get; private set; } = "učitavam…";

    /// <summary>Razlog povratka na ugrađenu šemu (statusna traka); <c>null</c> kad je sve u redu.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSchemaWarning))]
    public partial string? SchemaWarning { get; private set; }

    public bool HasSchemaWarning => SchemaWarning is not null;

    /// <summary>Učitavanje ili čuvanje je u toku.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand), nameof(OpenRecentCommand), nameof(SaveAsCommand))]
    public partial bool IsBusy { get; private set; }

    public ObservableCollection<RecentFileItem> RecentFiles { get; } = [];

    public bool HasRecentFiles => RecentFiles.Count > 0;

    /// <summary>Početno stanje: status šeme, problem sa podešavanjima, fajl iz komandne linije.</summary>
    public async Task InitializeAsync(string? startupFile = null)
    {
        if (_settings.LoadProblem is { } problem)
        {
            SetStatus(problem, MessageSeverity.Warning);
        }

        await RefreshSchemaStatusAsync();

        if (!string.IsNullOrWhiteSpace(startupFile))
        {
            await OpenFileAsync(startupFile);
        }
    }

    /// <summary>Otvara fajl. Vraća <c>true</c> ako je fajl učitan.</summary>
    public async Task<bool> OpenFileAsync(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (IsBusy)
        {
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            await _dialogs.ShowMessageAsync("Fajl ne može da se otvori", $"Putanja „{path}” nije ispravna.", MessageSeverity.Error);
            return false;
        }

        var fileName = Path.GetFileName(fullPath);
        if (!File.Exists(fullPath))
        {
            var wasRecent = RecentFiles.Any(r => AppSettings.PathComparer.Equals(r.Path, fullPath));
            if (wasRecent)
            {
                _settings.Update(s => s.WithoutRecentFile(fullPath));
            }

            var message = $"Fajl „{fileName}” ne postoji (premješten ili obrisan)."
                + (wasRecent ? " Uklonjen je iz liste nedavnih fajlova." : string.Empty);
            SetStatus(message, MessageSeverity.Error);
            await _dialogs.ShowMessageAsync("Fajl ne postoji", message, MessageSeverity.Error, [fullPath]);
            return false;
        }

        IsBusy = true;
        SetStatus($"Učitavam „{fileName}”…", MessageSeverity.Info);
        try
        {
            var size = new FileInfo(fullPath).Length;
            if (size > OpenedDocument.MaxFileSize)
            {
                var message = $"Fajl „{fileName}” je prevelik ({OpenedDocument.FormatSize(size)}). Najveća dozvoljena veličina je {OpenedDocument.FormatSize(OpenedDocument.MaxFileSize)}.";
                SetStatus(message, MessageSeverity.Error);
                IsBusy = false;
                await _dialogs.ShowMessageAsync("Fajl je prevelik", message, MessageSeverity.Error);
                return false;
            }

            var content = await File.ReadAllBytesAsync(fullPath);
            var document = new OpenedDocument(fullPath, content);
            Document = document;
            Validation.SetDocument(document);
            Cleaning.SetDocument(document);
            _settings.Update(s => s.WithRecentFile(fullPath));
            SetStatus($"Otvoren fajl „{fileName}” ({document.SizeText}).", MessageSeverity.Success);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning("Fajl ne može da se otvori.", ex);
            SetStatus($"Fajl „{fileName}” ne može da se otvori.", MessageSeverity.Error);
            IsBusy = false;
            await _dialogs.ShowMessageAsync("Fajl ne može da se otvori", $"Fajl „{fileName}” ne može da se otvori.", MessageSeverity.Error, [ex.Message]);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Fajlovi prevučeni na prozor: <c>.xsd</c> i <c>.zip</c> idu na uvoz šeme (Podešavanja → Šeme),
    /// ostali se otvaraju. Otvara se samo prvi fajl.
    /// </summary>
    public async Task HandleDroppedFilesAsync(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
        {
            return;
        }

        var first = paths[0];
        if (Directory.Exists(first))
        {
            SetStatus("Prevucite fajl, a ne folder.", MessageSeverity.Warning);
            return;
        }

        if (IsSchemaFile(first))
        {
            await _dialogs.ShowSettingsAsync(SettingsPage.Schemas, first);
            return;
        }

        if (await OpenFileAsync(first) && paths.Count > 1)
        {
            SetStatus($"{StatusMessage} Prevučeno je {paths.Count} fajlova; otvoren je samo prvi.", MessageSeverity.Warning);
        }
    }

    /// <summary>Osvježava prikaz aktivne šeme u statusnoj traci (kompajliranje ide van UI niti).</summary>
    public async Task RefreshSchemaStatusAsync()
    {
        try
        {
            var active = await Task.Run(_schemas.GetActiveSchema);
            ActiveSchemaName = active.Name;
            SchemaWarning = active.IsFallback ? active.FallbackReason : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error("Aktivna šema ne može da se učita.", ex);
            ActiveSchemaName = "nedostupna";
            SchemaWarning = "Nijedna šema ne može da se učita. Detalji su u logu.";
        }
    }

    internal static bool IsSchemaFile(string path) =>
        Path.GetExtension(path).ToUpperInvariant() is ".XSD" or ".ZIP";

    private bool CanOpen() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenAsync()
    {
        var path = await _dialogs.PickOpenFileAsync("Otvori XML fajl", [FileFilter.Xml, FileFilter.All], StartFolder());
        if (path is not null)
        {
            await OpenFileAsync(path);
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private Task OpenRecentAsync(RecentFileItem? item) => item is null ? Task.CompletedTask : OpenFileAsync(item.Path);

    [RelayCommand]
    private void ClearRecentFiles()
    {
        _settings.Update(s => s with { RecentFiles = [] });
        SetStatus("Lista nedavnih fajlova je obrisana (sami fajlovi nisu dirani).", MessageSeverity.Info);
    }

    private bool CanSaveAs() => Document is not null && !IsBusy;

    /// <summary>Čuva sadržaj u novi fajl. Ulazni fajl se nikad ne prepisuje.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveAs))]
    private async Task SaveAsAsync()
    {
        var document = Document;
        if (document is null)
        {
            return;
        }

        var activeTab = SelectedTabIndex == 1 ? (DocumentTabViewModel)Cleaning : Validation;
        var content = activeTab.GetContentToSave() ?? document.Content;
        var suggested = Path.GetFileNameWithoutExtension(document.FileName) + "_kopija" + Path.GetExtension(document.FileName);
        var target = await _dialogs.PickSaveFileAsync("Sačuvaj kao", suggested, [FileFilter.Xml, FileFilter.All], document.Folder);
        if (target is null)
        {
            return;
        }

        var fullTarget = Path.GetFullPath(target);
        if (AppSettings.PathComparer.Equals(fullTarget, document.Path))
        {
            const string message = "Ulazni fajl se nikad ne prepisuje. Izaberite drugo ime ili folder.";
            SetStatus(message, MessageSeverity.Error);
            await _dialogs.ShowMessageAsync("Fajl nije sačuvan", message, MessageSeverity.Error);
            return;
        }

        IsBusy = true;
        try
        {
            await AtomicFile.WriteAllBytesAsync(fullTarget, content);
            _settings.Update(s => s with { LastFolder = Path.GetDirectoryName(fullTarget) });
            SetStatus($"Sačuvano kao „{Path.GetFileName(fullTarget)}”.", MessageSeverity.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning("Fajl ne može da se sačuva.", ex);
            SetStatus("Fajl nije sačuvan.", MessageSeverity.Error);
            IsBusy = false;
            await _dialogs.ShowMessageAsync("Fajl nije sačuvan", $"Fajl „{Path.GetFileName(fullTarget)}” ne može da se sačuva.", MessageSeverity.Error, [ex.Message]);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task OpenSettingsAsync() => _dialogs.ShowSettingsAsync(SettingsPage.General);

    [RelayCommand]
    private Task ManageSchemasAsync() => _dialogs.ShowSettingsAsync(SettingsPage.Schemas);

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _store.Changed -= OnSchemaStoreChanged;
    }

    private string? StartFolder() =>
        Document?.Folder is { Length: > 0 } folder ? folder : _settings.Current.LastFolder;

    private void SetStatus(string message, MessageSeverity severity)
    {
        StatusMessage = message;
        StatusSeverity = severity;
    }

    private void LoadRecentFiles()
    {
        var current = _settings.Current.RecentFiles;
        if (RecentFiles.Select(r => r.Path).SequenceEqual(current, StringComparer.Ordinal))
        {
            return;
        }

        RecentFiles.Clear();
        foreach (var path in current)
        {
            RecentFiles.Add(new RecentFileItem(path));
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        _ui.Post(() =>
        {
            LoadRecentFiles();
            if (_settings.SaveProblem is { } problem)
            {
                SetStatus(problem, MessageSeverity.Warning);
            }
        });
    }

    private void OnSchemaStoreChanged(object? sender, EventArgs e) => _ui.Post(() => _ = RefreshSchemaStatusAsync());
}
