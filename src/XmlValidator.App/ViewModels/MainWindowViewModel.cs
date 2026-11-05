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
    private Task<bool> _lastOpenInValidation = Task.FromResult(false);
    private Task _lastCleaningCheck = Task.CompletedTask;
    private CancellationTokenSource? _cleaningCheckCts;

    /// <summary>Revizija teksta taba „Validacija” predata tabu „Čišćenje faktura” (izmjene idu u očišćen fajl).</summary>
    private long? _handedOverRevision;

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
        Validation.PropertyChanged += OnTabPropertyChanged;
        Cleaning.PropertyChanged += OnTabPropertyChanged;
        Cleaning.OpenInValidationRequested += OnOpenInValidationRequested;
        Validation.ValidationCompleted += OnValidationCompleted;
        LoadRecentFiles();
    }

    public ValidationTabViewModel Validation { get; }

    public CleaningTabViewModel Cleaning { get; }

    /// <summary>Izabran tab: 0 = Validacija, 1 = Čišćenje faktura.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyCanExecuteChangedFor(nameof(SaveAsCommand))]
    public partial int SelectedTabIndex { get; set; }

    /// <summary>
    /// Validirani XML nema grešaka, a ima faktura za čišćenje: tab „Čišćenje faktura” to pokazuje trepćućom
    /// linijom dok ga korisnik ne otvori.
    /// </summary>
    [ObservableProperty]
    public partial bool CleaningNeedsAttention { get; private set; }

    /// <summary>Broj faktura za čišćenje iz posljednje provjere (za opis alata uz oznaku na tabu).</summary>
    [ObservableProperty]
    public partial int InvoicesToClean { get; private set; }

    /// <summary>Posljednji fajl otvoren za oba taba.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDocument), nameof(Title))]
    [NotifyCanExecuteChangedFor(nameof(SaveAsCommand), nameof(CloseFileCommand))]
    public partial OpenedDocument? Document { get; private set; }

    public bool HasDocument => Document is not null;

    /// <summary>Aktivan tab (0 = Validacija, 1 = Čišćenje faktura).</summary>
    public DocumentTabViewModel ActiveTab => SelectedTabIndex == 1 ? Cleaning : Validation;

    /// <summary>
    /// Naslov prozora: fajl aktivnog taba. Tab „Validacija” može imati drugi fajl od taba „Čišćenje faktura”
    /// (sačuvan očišćen fajl otvoren za validaciju).
    /// </summary>
    public string Title => (ActiveTab.Document ?? Document) is { } document ? $"{document.FileName} — {AppTitle}" : AppTitle;

    /// <summary>Neki tab ima izmjene ili odluke koje nisu sačuvane.</summary>
    public bool HasUnsavedChanges => Validation.HasUnsavedChanges || Cleaning.HasUnsavedChanges;

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
    [NotifyCanExecuteChangedFor(nameof(OpenCommand), nameof(OpenRecentCommand), nameof(SaveAsCommand), nameof(CloseFileCommand), nameof(SaveValidatedXmlCommand))]
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
        await Validation.InitializeAsync();

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

        if (!await ConfirmDiscardChangesAsync())
        {
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
            _handedOverRevision = null;
            ResetCleaningAttention();
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

    /// <summary>
    /// Pita korisnika da li da odbaci nesačuvane izmjene u editoru (prije otvaranja drugog fajla ili zatvaranja
    /// prozora). Vraća <c>true</c> ako izmjena nema ili ih korisnik odbacuje.
    /// </summary>
    public Task<bool> ConfirmDiscardChangesAsync() => ConfirmDiscardAsync(Validation.HasUnsavedChanges, Cleaning.HasUnsavedChanges);

    /// <summary>
    /// Otvara fajl samo u tabu „Validacija” i prebacuje se na njega (npr. sačuvan očišćen fajl); tab
    /// „Čišćenje faktura” zadržava svoju analizu.
    /// </summary>
    public async Task<bool> OpenInValidationTabAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        // Izmjene predate čišćenju su u očišćenom fajlu; ne pita se da li da se odbace.
        var handedOver = _handedOverRevision == Validation.TextRevision;
        if (IsBusy || !await ConfirmDiscardAsync(Validation.HasUnsavedChanges && !handedOver, cleaning: false))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        var fileName = Path.GetFileName(fullPath);
        IsBusy = true;
        try
        {
            var content = await File.ReadAllBytesAsync(fullPath);
            Validation.SetDocument(new OpenedDocument(fullPath, content));
            _handedOverRevision = null;
            SelectedTabIndex = 0;
            _settings.Update(s => s.WithRecentFile(fullPath));
            SetStatus($"Očišćen fajl „{fileName}” je otvoren u tabu „Validacija”.", MessageSeverity.Success);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning("Očišćen fajl ne može da se otvori za validaciju.", ex);
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

    /// <summary>Posljednje otvaranje rezultata u tabu „Validacija”; za testove.</summary>
    internal Task WhenOpenInValidationCompletedAsync() => _lastOpenInValidation;

    private async Task<bool> ConfirmDiscardAsync(bool validation, bool cleaning)
    {
        var message = (validation, cleaning) switch
        {
            (false, false) => null,
            (true, false) => "Sadržaj u editoru je izmijenjen i nije sačuvan (ulazni fajl je netaknut). Da li da odbacim izmjene?",
            (false, true) => "Odluke o čišćenju (izabrane akcije i ručne izmjene faktura) nisu sačuvane u očišćen fajl (ulazni fajl je netaknut). Da li da ih odbacim?",
            _ => "Izmjene u editoru i odluke o čišćenju nisu sačuvane (ulazni fajl je netaknut). Da li da ih odbacim?",
        };

        return message is null
            || await _dialogs.ConfirmAsync("Izmjene nisu sačuvane", message, "Odbaci izmjene", "Otkaži", isDangerous: true);
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

    private bool CanCloseFile() => !IsBusy && (Document is not null || Validation.Document is not null || Cleaning.Document is not null);

    /// <summary>
    /// „Zatvori fajl” (Ctrl+W): zatvara otvoren fajl u oba taba, bez zatvaranja programa. Nesačuvane izmjene i
    /// odluke traže potvrdu, kao prije otvaranja drugog fajla; fajl na disku se ne dira.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCloseFile))]
    private async Task CloseFileAsync()
    {
        if (!await ConfirmDiscardChangesAsync())
        {
            return;
        }

        var name = (Document ?? Validation.Document ?? Cleaning.Document)?.FileName;
        _handedOverRevision = null;
        ResetCleaningAttention();
        Validation.SetDocument(null);
        Cleaning.SetDocument(null);
        Document = null;
        SetStatus(name is null ? "Fajl je zatvoren." : $"Fajl „{name}” je zatvoren.", MessageSeverity.Info);
    }

    private bool CanSaveAs() => ActiveTab.Document is not null && !IsBusy;

    /// <summary>Čuva sadržaj u novi fajl. Ulazni fajl se nikad ne prepisuje.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveAs))]
    private Task SaveAsAsync() => SaveTabAsync(ActiveTab);

    private bool CanSaveValidatedXml() => !IsBusy && Validation.Document is not null && Validation.HasUnsavedChanges;

    /// <summary>
    /// „Sačuvaj XML” na tabu „Validacija” (aktivno samo kad je XML izmijenjen u editoru): ako u izmijenjenom
    /// sadržaju ima faktura za čišćenje, pita da li da se pređe na čišćenje (Da: tab „Čišćenje faktura” analizira
    /// sadržaj iz editora; Ne: čuvanje se nastavlja). Ulazni fajl se nikad ne prepisuje.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSaveValidatedXml))]
    private async Task SaveValidatedXmlAsync()
    {
        var document = Validation.Document;
        if (document is null)
        {
            return;
        }

        if (Validation.GetSaveProblem() is { } problem)
        {
            SetStatus("Fajl nije sačuvan.", MessageSeverity.Error);
            await _dialogs.ShowMessageAsync("Fajl nije sačuvan", problem, MessageSeverity.Error);
            return;
        }

        var content = Validation.GetContentToSave() ?? document.Content;
        int? count;
        IsBusy = true;
        try
        {
            SetStatus("Provjeravam da li ima faktura za čišćenje…", MessageSeverity.Info);
            count = await Cleaning.CountInvoicesToCleanAsync(content);
        }
        finally
        {
            IsBusy = false;
        }

        if (count > 0)
        {
            var choice = await _dialogs.ChooseAsync(
                "Fakture za čišćenje",
                $"U XML-u ima {Invoices(count.Value)} za čišćenje (fakture koje FZO ne prihvata po pravilu SHMP ili čekaju odluku). "
                + "Da li želite da uradite čišćenje prije čuvanja?",
                [YesOption, NoOption],
                "Izaberi");
            if (choice is null)
            {
                SetStatus("Čuvanje je otkazano.", MessageSeverity.Info);
                return;
            }

            if (choice == YesOption)
            {
                await HandOverToCleaningAsync(document, content);
                return;
            }
        }

        await SaveTabAsync(Validation);
    }

    /// <summary>Odgovor „Da” u pitanju o čišćenju.</summary>
    public const string YesOption = "Da";

    /// <summary>Odgovor „Ne” u pitanju o čišćenju.</summary>
    public const string NoOption = "Ne";

    /// <summary>Tab „Čišćenje faktura” dobija sadržaj iz editora i postaje aktivan.</summary>
    private async Task HandOverToCleaningAsync(OpenedDocument document, byte[] content)
    {
        if (!await ConfirmDiscardAsync(validation: false, Cleaning.HasUnsavedChanges))
        {
            return;
        }

        Cleaning.SetEditedDocument(new OpenedDocument(document.Path, content));
        _handedOverRevision = Validation.TextRevision;
        CleaningNeedsAttention = false;
        SelectedTabIndex = 1;
        SetStatus("Sadržaj iz editora je predat na čišćenje; očišćen fajl sačuvajte dugmetom „Sačuvaj XML” na tabu „Čišćenje faktura”.", MessageSeverity.Info);
    }

    /// <summary>„1 faktura”, „3 fakture”, „5 faktura” (srpska množina).</summary>
    internal static string Invoices(int count) =>
        count % 10 is >= 2 and <= 4 && count % 100 is < 12 or > 14 ? $"{count} fakture" : $"{count} faktura";

    private async Task SaveTabAsync(DocumentTabViewModel activeTab)
    {
        var document = activeTab.Document;
        if (document is null)
        {
            return;
        }

        if (activeTab.GetSaveProblem() is { } problem)
        {
            SetStatus("Fajl nije sačuvan.", MessageSeverity.Error);
            await _dialogs.ShowMessageAsync("Fajl nije sačuvan", problem, MessageSeverity.Error);
            return;
        }

        var content = activeTab.GetContentToSave() ?? document.Content;
        // Ispravke iz taba „Validacija” dobijaju sufiks „_ispravljen”, nepromijenjen sadržaj „_kopija”.
        var suffix = ReferenceEquals(activeTab, Validation) && Validation.IsModified ? "_ispravljen" : "_kopija";
        var suggested = Path.GetFileNameWithoutExtension(document.FileName) + suffix + Path.GetExtension(document.FileName);
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
            activeTab.MarkSaved();
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

    /// <summary>„O programu” (F1): verzija, licence korišćenih paketa i prečice.</summary>
    [RelayCommand]
    private Task ShowAboutAsync() => _dialogs.ShowAboutAsync();

    /// <summary>Izbor taba prečicom (Ctrl+1 / Ctrl+2); parametar je indeks taba kao tekst ili broj.</summary>
    [RelayCommand]
    private void SelectTab(object? index)
    {
        var value = index switch
        {
            int i => i,
            string s when int.TryParse(s, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var i) => i,
            _ => -1,
        };
        if (value is 0 or 1)
        {
            SelectedTabIndex = value;
        }
    }

    [RelayCommand]
    private Task ManageSchemasAsync() => _dialogs.ShowSettingsAsync(SettingsPage.Schemas);

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _store.Changed -= OnSchemaStoreChanged;
        Validation.PropertyChanged -= OnTabPropertyChanged;
        Cleaning.PropertyChanged -= OnTabPropertyChanged;
        Cleaning.OpenInValidationRequested -= OnOpenInValidationRequested;
        Validation.ValidationCompleted -= OnValidationCompleted;
        _cleaningCheckCts?.Cancel();
        _cleaningCheckCts?.Dispose();
    }

    /// <summary>Posljednja provjera faktura za čišćenje poslije validacije; za testove.</summary>
    internal Task WhenCleaningCheckCompletedAsync() => _lastCleaningCheck;

    partial void OnSelectedTabIndexChanged(int value)
    {
        if (value == 1)
        {
            CleaningNeedsAttention = false;
        }
    }

    /// <summary>Poslije validacije bez grešaka provjerava da li u istom sadržaju ima faktura za čišćenje.</summary>
    private void OnValidationCompleted(object? sender, ValidationCompletedEventArgs e)
    {
        ResetCleaningAttention();
        if (e.Result.ErrorCount > 0 || e.Content is not { } content)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _cleaningCheckCts = cts;
        _lastCleaningCheck = CheckInvoicesToCleanAsync(content, cts);
    }

    private async Task CheckInvoicesToCleanAsync(byte[] content, CancellationTokenSource cts)
    {
        try
        {
            var count = await Cleaning.CountInvoicesToCleanAsync(content, cts.Token);
            _ui.Post(() =>
            {
                if (ReferenceEquals(_cleaningCheckCts, cts))
                {
                    InvoicesToClean = count ?? 0;
                    CleaningNeedsAttention = count > 0 && SelectedTabIndex != 1;
                }
            });
        }
        catch (OperationCanceledException)
        {
            // novija validacija ili zatvoren fajl
        }
        catch (Exception ex)
        {
            _log.Warning("Provjera faktura za čišćenje nije uspjela.", ex);
        }
    }

    private void ResetCleaningAttention()
    {
        var cts = _cleaningCheckCts;
        _cleaningCheckCts = null;
        if (cts is not null)
        {
            cts.Cancel();
            cts.Dispose();
        }

        CleaningNeedsAttention = false;
        InvoicesToClean = 0;
    }

    private void OnTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentTabViewModel.Document))
        {
            OnPropertyChanged(nameof(Title));
            SaveAsCommand.NotifyCanExecuteChanged();
            CloseFileCommand.NotifyCanExecuteChanged();
            SaveValidatedXmlCommand.NotifyCanExecuteChanged();
        }
        else if (e.PropertyName is nameof(DocumentTabViewModel.HasUnsavedChanges))
        {
            OnPropertyChanged(nameof(HasUnsavedChanges));
            SaveValidatedXmlCommand.NotifyCanExecuteChanged();
        }
    }

    private void OnOpenInValidationRequested(object? sender, OpenInValidationEventArgs e) =>
        _ui.Post(() => _lastOpenInValidation = OpenInValidationTabAsync(e.Path));

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
