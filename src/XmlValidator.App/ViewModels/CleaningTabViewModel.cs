using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XmlValidator.App.Services;
using XmlValidator.Core.Cleaning;
using XmlValidator.Core.Validation;

namespace XmlValidator.App.ViewModels;

/// <summary>Zahtjev da se fajl otvori u tabu „Validacija” (npr. sačuvan očišćen fajl).</summary>
public sealed class OpenInValidationEventArgs(string path) : EventArgs
{
    public string Path { get; } = path;
}

/// <summary>
/// Tab „Čišćenje faktura”: analiza po pravilu SHMP u pozadini (sa napretkom i prekidom), tabela faktura
/// sa akcijom po fakturi i grupnom promjenom, lista popravki i upozorenja, ručna izmjena bloka,
/// čuvanje očišćenog fajla i izvještaja. Ulazni fajl se samo čita.
/// </summary>
public sealed partial class CleaningTabViewModel : DocumentTabViewModel, IDisposable
{
    private readonly CleaningService _cleaning;
    private readonly XmlValidationService _validator;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _ui;
    private readonly IAppLog _log;
    private readonly TimeProvider _time;

    private CleaningSession? _session;
    private List<InvoiceRowViewModel> _allRows = [];
    private CancellationTokenSource? _analysisCts;
    private long _analysisGeneration;
    private CleaningResult? _lastResult;
    private int _lastResultVersion = -1;
    private string? _savedOutputPath;
    private int _savedVersion = -1;
    private bool _loadingSettings;
    private string _editInitialText = string.Empty;
    private CandidateAction? _editPreviousAction;
    private bool _editChangedAction;
    private Task _lastBackgroundWork = Task.CompletedTask;
    private string? _contentNote;

    public CleaningTabViewModel(
        CleaningService cleaning,
        XmlValidationService validator,
        ISettingsService settings,
        IDialogService dialogs,
        IUiDispatcher ui,
        IAppLog log,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(cleaning);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(log);
        _cleaning = cleaning;
        _validator = validator;
        _settings = settings;
        _dialogs = dialogs;
        _ui = ui;
        _log = log;
        _time = time ?? TimeProvider.System;

        // Početne vrijednosti samo za nullability; ne smiju se upisati u podešavanja (stvarne dolaze iz LoadSettings).
        _loadingSettings = true;
        EmptyInvoiceAction = DefaultActionOptions[0];
        NoGroupNoJmbMatchAction = DefaultActionOptions[0];
        _loadingSettings = false;
        SelectedFilter = FilterOptions[0];
        LoadSettings(settings.Current);
        _settings.Changed += OnSettingsChanged;
    }

    public override string Header => "Čišćenje faktura";

    public override string EmptyStateHint =>
        "Otvorite XML fajl sa fakturama (Ctrl+O) ili ga prevucite na prozor da biste ga analizirali po pravilu SHMP.";

    /// <summary>Zahtjev prikazu da tabela skroluje do reda (poslije klika na popravku ili upozorenje).</summary>
    public event EventHandler<InvoiceRowViewModel>? RowNavigationRequested;

    /// <summary>Zahtjev glavnom prozoru da otvori fajl u tabu „Validacija”.</summary>
    public event EventHandler<OpenInValidationEventArgs>? OpenInValidationRequested;

    // ---------------------------------------------------------------- podešavanja

    /// <summary>Podrazumijevane akcije po kategoriji (Uredi nije ponuđena: traži ručnu izmjenu; Prikaži = odluka po fakturi).</summary>
    public IReadOnlyList<Option<CandidateAction?>> DefaultActionOptions { get; } =
    [
        new(CandidateAction.Delete, "Obriši"),
        new(CandidateAction.Skip, "Preskoči"),
        new(null, "Prikaži (odluka po fakturi)"),
    ];

    /// <summary>Ciljna grupa (0–65535); promjena traži novu analizu.</summary>
    [ObservableProperty]
    public partial decimal? TargetGroup { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTargetGroupError))]
    public partial string? TargetGroupError { get; private set; }

    public bool HasTargetGroupError => TargetGroupError is not null;

    /// <summary>Podrazumijevana akcija za praznu fakturu; važi odmah, bez nove analize.</summary>
    [ObservableProperty]
    public partial Option<CandidateAction?> EmptyInvoiceAction { get; set; }

    /// <summary>Podrazumijevana akcija za fakturu samo iz drugih službi čiji pacijent nema pregled u SHMP; važi odmah.</summary>
    [ObservableProperty]
    public partial Option<CandidateAction?> NoGroupNoJmbMatchAction { get; set; }

    /// <summary>Automatske popravke zagrada; promjena traži novu analizu.</summary>
    [ObservableProperty]
    public partial bool RepairBrackets { get; set; }

    // ---------------------------------------------------------------- analiza

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(IsInteractive))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand), nameof(CancelAnalysisCommand), nameof(UseDefaultsCommand))]
    public partial bool IsAnalyzing { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(IsInteractive))]
    public partial bool IsSaving { get; private set; }

    /// <summary>Analiza ili čuvanje je u toku (izmjene su tada onemogućene).</summary>
    public bool IsBusy => IsAnalyzing || IsSaving;

    /// <summary>Tabela i komande za odluke su dostupne.</summary>
    public bool IsInteractive => HasAnalysis && !IsBusy;

    [ObservableProperty]
    public partial string ProgressText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial double ProgressValue { get; private set; }

    [ObservableProperty]
    public partial double ProgressMaximum { get; private set; } = 1;

    [ObservableProperty]
    public partial bool IsProgressIndeterminate { get; private set; } = true;

    /// <summary>Fajl ne može da se analizira (poruka za korisnika); <c>null</c> kad je analiza uspjela.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnalysisError))]
    public partial string? AnalysisError { get; private set; }

    public bool HasAnalysisError => AnalysisError is not null;

    /// <summary>Postoji rezultat analize.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInteractive))]
    public partial bool HasAnalysis { get; private set; }

    /// <summary>Ciljna grupa ili popravke su promijenjene poslije analize (prikaz je za stara podešavanja).</summary>
    [ObservableProperty]
    public partial bool IsAnalysisStale { get; private set; }

    /// <summary>Podešavanja kojima je urađena prikazana analiza.</summary>
    [ObservableProperty]
    public partial string AnalyzedWith { get; private set; } = string.Empty;

    /// <summary>Kodiranje i završeci linija ulaza.</summary>
    [ObservableProperty]
    public partial string FormatDescription { get; private set; } = string.Empty;

    /// <summary>Upozorenja za cijeli fajl (npr. kodiranje).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> DocumentWarnings { get; private set; } = [];

    [ObservableProperty]
    public partial CleaningSummary Summary { get; private set; } = CleaningSummary.Empty;

    /// <summary>Poruka u traci taba (rezultat posljednje radnje).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial UserMessage? Message { get; private set; }

    public bool HasMessage => Message is not null;

    // ---------------------------------------------------------------- tabela

    /// <summary>Redovi koji prolaze filter i pretragu.</summary>
    public ObservableCollection<InvoiceRowViewModel> Rows { get; } = [];

    public IReadOnlyList<Option<InvoiceFilter>> FilterOptions { get; } =
    [
        new(InvoiceFilter.All, "Sve"),
        new(InvoiceFilter.Candidates, "Samo kandidati"),
        new(InvoiceFilter.Warnings, "Samo upozorenja"),
        new(InvoiceFilter.Unresolved, "Čeka odluku"),
    ];

    [ObservableProperty]
    public partial Option<InvoiceFilter> SelectedFilter { get; set; }

    /// <summary>Pretraga po broju fakture i JMB-u.</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>„Prikazano 12 od 40 faktura”.</summary>
    [ObservableProperty]
    public partial string RowsCountText { get; private set; } = string.Empty;

    /// <summary>Red u fokusu (pregled bloka, uređivanje).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartEditCommand), nameof(RevertEditCommand))]
    public partial InvoiceRowViewModel? SelectedRow { get; set; }

    /// <summary>Svi označeni redovi (višestruki izbor u tabeli; postavlja prikaz).</summary>
    public IReadOnlyList<InvoiceRowViewModel> SelectedRows { get; private set; } = [];

    /// <summary>Broj označenih kandidata (za grupnu promjenu akcije).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand), nameof(SkipSelectedCommand), nameof(ResetSelectedCommand))]
    public partial int SelectedCandidateCount { get; private set; }

    // ---------------------------------------------------------------- popravke i upozorenja

    public ObservableCollection<CleaningNoteRow> Notes { get; } = [];

    public bool HasNotes => Notes.Count > 0;

    // ---------------------------------------------------------------- pregled bloka

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    public partial string? PreviewTitle { get; private set; }

    /// <summary>Tekst bloka za pregled (samo za čitanje).</summary>
    [ObservableProperty]
    public partial string PreviewText { get; private set; } = string.Empty;

    /// <summary>Linija ulaznog fajla na kojoj počinje pregled (0 = ne odgovara fajlu, npr. uređen tekst).</summary>
    [ObservableProperty]
    public partial int PreviewFirstLine { get; private set; }

    /// <summary>Linija u tekstu pregleda koju treba označiti (od 1; 0 = nijedna).</summary>
    [ObservableProperty]
    public partial int PreviewHighlightLine { get; private set; }

    public bool HasPreview => PreviewTitle is not null;

    // ---------------------------------------------------------------- uređivanje

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyEditCommand), nameof(CancelEditCommand), nameof(StartEditCommand))]
    public partial InvoiceRowViewModel? EditingRow { get; private set; }

    public bool IsEditing => EditingRow is not null;

    [ObservableProperty]
    public partial string EditTitle { get; private set; } = string.Empty;

    /// <summary>Tekst bloka u editoru (prikaz ga sinhronizuje sa AvaloniaEdit-om).</summary>
    [ObservableProperty]
    public partial string EditText { get; set; } = string.Empty;

    /// <summary>Razlog odbijanja izmjene (neispravan XML fragment, znak van kodiranja…).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEditError))]
    public partial string? EditError { get; private set; }

    public bool HasEditError => EditError is not null;

    // ---------------------------------------------------------------- čuvanje

    /// <summary>Razlog zašto očišćen fajl ne može da se sačuva; <c>null</c> kad može.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaveBlockedReason))]
    public partial string? SaveBlockedReason { get; private set; }

    public bool HasSaveBlockedReason => SaveBlockedReason is not null;

    /// <summary>Rezultat provjere izlaza (ispravnost XML-a i šema), ili prazno dok izlaz nije provjeren.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutputCheck))]
    public partial string? OutputCheckText { get; private set; }

    [ObservableProperty]
    public partial MessageSeverity OutputCheckSeverity { get; private set; } = MessageSeverity.Info;

    public bool HasOutputCheck => OutputCheckText is not null;

    /// <summary>Korisnik je donio odluke (akcije, izmjene) koje nisu sačuvane u očišćen fajl.</summary>
    public override bool HasUnsavedChanges =>
        _session is { } session && _savedVersion != session.Version && HasDecisions(session);

    // ---------------------------------------------------------------- javni API

    /// <summary>Postavlja otvoren fajl i odmah pokreće analizu.</summary>
    public override void SetDocument(OpenedDocument? document) => SetDocument(document, contentNote: null);

    /// <summary>
    /// Analizira sadržaj iz editora taba „Validacija” (sa izmjenama koje još nisu sačuvane). Putanja ostaje putanja
    /// ulaznog fajla, koji se ne mijenja; očišćen fajl se i dalje čuva u novi fajl.
    /// </summary>
    public void SetEditedDocument(OpenedDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        SetDocument(document, "Analiziran je sadržaj iz taba „Validacija”, sa izmjenama koje još nisu sačuvane (ulazni fajl je netaknut).");
    }

    /// <summary>
    /// Broj faktura za čišćenje u sadržaju (kandidati i fakture koje čekaju odluku), sa trenutnim podešavanjima.
    /// Ne mijenja analizu u tabu. <c>null</c> kad sadržaj ne može da se analizira.
    /// </summary>
    public async Task<int?> CountInvoicesToCleanAsync(byte[] content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        try
        {
            var session = await _cleaning.AnalyzeAsync(content, _settings.Current.ToCleaningOptions(), progress: null, cancellationToken);
            return session.Blocks.Count(b => b.Classification.IsCandidate || b.Disposition == BlockDisposition.Unresolved);
        }
        catch (InvoiceDocumentException)
        {
            return null;
        }
    }

    private void SetDocument(OpenedDocument? document, string? contentNote)
    {
        _contentNote = contentNote;
        CancelRunningAnalysis();
        base.SetDocument(document);
        ClearAnalysis();
        AnalysisError = null;
        Message = null;
        if (document is not null)
        {
            _lastBackgroundWork = RunAnalysisAsync();
        }

        NotifyCommands();
    }

    /// <summary>Označeni redovi u tabeli (poziva prikaz pri promjeni izbora).</summary>
    public void SetSelectedRows(IEnumerable<InvoiceRowViewModel> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        SelectedRows = [.. rows];
        SelectedCandidateCount = SelectedRows.Count(r => r.IsCandidate);
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        CancelRunningAnalysis();
    }

    /// <summary>Posao pokrenut automatski (analiza pri otvaranju); za testove.</summary>
    internal Task WhenBackgroundWorkCompletedAsync() => _lastBackgroundWork;

    internal CleaningSession? Session => _session;

    // ---------------------------------------------------------------- komande: analiza

    private bool CanAnalyze() => Document is not null && !IsAnalyzing;

    /// <summary>Ponovna analiza sa trenutnim podešavanjima (pita ako bi se izgubile odluke).</summary>
    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeAsync()
    {
        if (!await ConfirmDiscardDecisionsAsync())
        {
            return;
        }

        await RunAnalysisAsync();
    }

    private bool CanCancelAnalysis() => IsAnalyzing;

    [RelayCommand(CanExecute = nameof(CanCancelAnalysis))]
    private void CancelAnalysis() => _analysisCts?.Cancel();

    private bool CanUseScriptSettings() => !IsAnalyzing;

    /// <summary>
    /// „Podrazumijevano”: grupa SHMP 100, pregled 183, popravke uključene, Obriši za obje kategorije kandidata,
    /// pa nova analiza (pojedinačno izabrane akcije se poništavaju). Bivše dugme „Kao skripta”: rezultat je isti
    /// kao <c>clean.py</c> osim uslova pregleda u SHMP (odluka 121).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUseScriptSettings))]
    private async Task UseDefaultsAsync()
    {
        if (!await ConfirmDiscardDecisionsAsync())
        {
            return;
        }

        var defaults = new AppSettings();
        _settings.Update(s => s with
        {
            TargetGroup = defaults.TargetGroup,
            ExamService = defaults.ExamService,
            RepairBrackets = defaults.RepairBrackets,
            EmptyInvoiceAction = defaults.EmptyInvoiceAction,
            NoGroupNoJmbMatchAction = defaults.NoGroupNoJmbMatchAction,
        });

        if (Document is not null)
        {
            await RunAnalysisAsync();
        }
    }

    // ---------------------------------------------------------------- komande: tabela

    private bool CanChangeSelected() => IsInteractive && SelectedCandidateCount > 0;

    [RelayCommand(CanExecute = nameof(CanChangeSelected))]
    private void DeleteSelected() => SetActionForSelected(CandidateAction.Delete);

    [RelayCommand(CanExecute = nameof(CanChangeSelected))]
    private void SkipSelected() => SetActionForSelected(CandidateAction.Skip);

    /// <summary>Vraća označene kandidate na podrazumijevanu akciju za njihovu kategoriju.</summary>
    [RelayCommand(CanExecute = nameof(CanChangeSelected))]
    private void ResetSelected()
    {
        if (_session is not { } session)
        {
            return;
        }

        var ids = SelectedRows.Where(r => r.IsCandidate && r.State.UserAction is not null && !r.IsEdited).Select(r => r.Id).ToList();
        foreach (var id in ids)
        {
            session.ClearAction(id);
        }

        Refresh();
        Message = UserMessage.Info($"Podrazumijevana akcija je vraćena za {Invoices(ids.Count)}.");
    }

    /// <summary>Skok na fakturu ili neprepoznat sadržaj iz liste popravki i upozorenja.</summary>
    [RelayCommand]
    private void GoToNote(CleaningNoteRow? note)
    {
        if (note is null)
        {
            return;
        }

        if (note.RowId is { } id && _allRows.FirstOrDefault(r => r.Id == id) is { } row)
        {
            ShowRow(row);
            ShowRowPreview(row, note.Line);
        }
        else if (note.Unrecognized is { } content)
        {
            SelectedRow = null;
            PreviewTitle = string.Create(CultureInfo.InvariantCulture, $"Neprepoznat sadržaj · linije {content.StartLine}–{content.EndLine}");
            PreviewText = content.Text;
            PreviewFirstLine = content.StartLine;
            PreviewHighlightLine = 1;
        }
    }

    /// <summary>Prikazuje sve fakture (filter „Sve”, bez pretrage).</summary>
    [RelayCommand]
    private void ClearFilter()
    {
        SearchText = string.Empty;
        SelectedFilter = FilterOptions[0];
    }

    /// <summary>Skok na prvu fakturu koja čeka odluku.</summary>
    [RelayCommand]
    private void GoToFirstUnresolved()
    {
        if (_allRows.FirstOrDefault(r => r.IsUnresolved) is { } row)
        {
            ShowRow(row);
            ShowRowPreview(row, 0);
        }
    }

    // ---------------------------------------------------------------- komande: uređivanje

    private bool CanStartEdit() => IsInteractive && SelectedRow is { CanEdit: true } && EditingRow is null;

    /// <summary>Otvara izabranu fakturu u editoru.</summary>
    [RelayCommand(CanExecute = nameof(CanStartEdit))]
    private void StartEdit()
    {
        if (SelectedRow is { } row)
        {
            BeginEdit(row, changedAction: false, previousAction: row.State.UserAction);
        }
    }

    private bool CanApplyEdit() => EditingRow is not null && !IsBusy;

    /// <summary>Primjenjuje izmjenu: blok se provjerava i sve fakture se ponovo klasifikuju.</summary>
    [RelayCommand(CanExecute = nameof(CanApplyEdit))]
    private void ApplyEdit()
    {
        if (_session is not { } session || EditingRow is not { } row)
        {
            return;
        }

        var result = session.ApplyEdit(row.Id, EditText);
        if (!result.Succeeded)
        {
            EditError = result.Error;
            return;
        }

        CloseEditor();
        Refresh(refilter: true);
        var state = result.State;
        var split = result.States.Count > 1 ? $" Blok je podijeljen na {Invoices(result.States.Count)}." : string.Empty;
        Message = UserMessage.Success(
            $"Izmjena je primijenjena. Faktura je ponovo klasifikovana: {CleaningText.Category(state.Classification.Category)}; ishod: {CleaningText.Disposition(state.Disposition)}.{split}");
        if (_allRows.FirstOrDefault(r => r.Id == state.Id) is { } edited)
        {
            ShowRow(edited);
            ShowRowPreview(edited, 0);
        }
    }

    private bool CanCancelEdit() => EditingRow is not null;

    /// <summary>Zatvara editor bez izmjene; akcija Uredi izabrana u tabeli se vraća na prethodnu.</summary>
    [RelayCommand(CanExecute = nameof(CanCancelEdit))]
    private void CancelEdit() => DiscardEdit();

    private bool CanRevertEdit() => IsInteractive && SelectedRow is { IsEdited: true };

    /// <summary>Vraća originalni tekst bloka (sa automatskim popravkama) i ponovo klasifikuje.</summary>
    [RelayCommand(CanExecute = nameof(CanRevertEdit))]
    private void RevertEdit()
    {
        if (_session is not { } session || SelectedRow is not { } row)
        {
            return;
        }

        var blockId = row.State.Block.Id;
        if (EditingRow is { } editing && editing.State.Block.Id == blockId)
        {
            DiscardEdit();
        }

        session.RevertEdit(row.Id);
        Refresh(refilter: true);
        Message = UserMessage.Info("Izmjena je poništena; faktura je vraćena na tekst iz ulaznog fajla.");
        if (_allRows.FirstOrDefault(r => r.State.Block.Id == blockId) is { } restored)
        {
            ShowRow(restored);
            ShowRowPreview(restored, 0);
        }
    }

    // ---------------------------------------------------------------- komande: čuvanje

    private bool CanSaveOutput() => IsInteractive && SaveBlockedReason is null && EditingRow is null;

    /// <summary>Čuva očišćen fajl u novi fajl (podrazumijevano <c>&lt;ime&gt;_ociscen.xml</c>); ulaz se nikad ne prepisuje.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveOutput))]
    private async Task SaveOutputAsync()
    {
        var document = Document;
        if (_session is not { } session || document is null)
        {
            return;
        }

        IsSaving = true;
        NotifyCommands();
        var fullTarget = string.Empty;
        try
        {
            var result = await GetCheckedResultAsync(session);
            if (result is null)
            {
                return;
            }

            // Pregled izmjena (sa provjerom prema šemi) prije izbora fajla; izlaz sa greškama šeme traži „Sačuvaj svejedno”.
            var preview = new ChangesPreviewViewModel(CleaningChangeSet.Create(result.Output.Blocks), document.FileName, result.Validation, isConfirmation: true);
            if (!await _dialogs.ShowChangesPreviewAsync(preview))
            {
                return;
            }

            var suggested = Path.GetFileName(CleaningOutputWriter.GetDefaultOutputPath(document.Path));
            var target = await _dialogs.PickSaveFileAsync("Sačuvaj očišćeni fajl", suggested, [FileFilter.Xml, FileFilter.All], document.Folder);
            if (target is null)
            {
                return;
            }

            fullTarget = Path.GetFullPath(target);
            if (AppSettings.PathComparer.Equals(fullTarget, document.Path))
            {
                await _dialogs.ShowMessageAsync("Fajl nije sačuvan", "Ulazni fajl se nikad ne prepisuje. Izaberite drugo ime ili folder.", MessageSeverity.Error);
                return;
            }

            // Prepisivanje postojećeg fajla je potvrdio korisnik u sistemskom dijalogu za čuvanje.
            await CleaningOutputWriter.WriteAsync(result.Output.Bytes, fullTarget, document.Path, overwrite: File.Exists(fullTarget));
            _savedOutputPath = fullTarget;
            _savedVersion = session.Version;
            OnPropertyChanged(nameof(HasUnsavedChanges));
            _settings.Update(s => s with { LastFolder = Path.GetDirectoryName(fullTarget) });
            Message = UserMessage.Success(
                $"Očišćen fajl je sačuvan: „{Path.GetFileName(fullTarget)}” (ostaje {Invoices(Summary.Remaining)} od {Summary.Total}).");
            // Sačuvan očišćen fajl se uvijek validira u tabu „Validacija”.
            OpenInValidationRequested?.Invoke(this, new OpenInValidationEventArgs(fullTarget));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _log.Warning("Očišćen fajl ne može da se sačuva.", ex);
            Message = UserMessage.Error("Očišćen fajl nije sačuvan.");
            await _dialogs.ShowMessageAsync("Fajl nije sačuvan", $"Fajl „{Path.GetFileName(fullTarget)}” ne može da se sačuva.", MessageSeverity.Error, [ex.Message]);
        }
        finally
        {
            IsSaving = false;
            NotifyCommands();
        }
    }

    private bool CanSaveReport() => IsInteractive && EditingRow is null;

    /// <summary>Čuva izvještaj u <c>.txt</c> ili <c>.csv</c> (po ekstenziji); JMB se ne maskira.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveReport))]
    private async Task SaveReportAsync()
    {
        var document = Document;
        if (_session is not { } session || document is null)
        {
            return;
        }

        var suggested = Path.GetFileNameWithoutExtension(document.FileName) + "_izvjestaj.txt";
        var target = await _dialogs.PickSaveFileAsync("Sačuvaj izvještaj o čišćenju", suggested, [FileFilter.Text, FileFilter.Csv], document.Folder);
        if (target is null)
        {
            return;
        }

        var fullTarget = Path.GetFullPath(target);
        if (AppSettings.PathComparer.Equals(fullTarget, document.Path))
        {
            await _dialogs.ShowMessageAsync("Izvještaj nije sačuvan", "Ulazni fajl se nikad ne prepisuje. Izaberite drugo ime.", MessageSeverity.Error);
            return;
        }

        IsSaving = true;
        NotifyCommands();
        try
        {
            // Izvještaj uključuje provjeru izlaza kad izlaz može da se napravi.
            var result = SaveBlockedReason is null ? await GetCheckedResultAsync(session) : null;
            var outputPath = _savedVersion == session.Version ? _savedOutputPath : null;
            var report = CleaningReport.Create(session, result, document.Path, outputPath, _time);
            var options = new ReportExportOptions();
            var isCsv = string.Equals(Path.GetExtension(fullTarget), ".csv", StringComparison.OrdinalIgnoreCase);
            var (text, encoding) = isCsv
                ? (CleaningReportFormatter.ToCsv(report, options), CleaningReportFormatter.CsvEncoding)
                : (CleaningReportFormatter.ToText(report, options), CleaningReportFormatter.TextEncoding);
            await AtomicFile.WriteAllBytesAsync(fullTarget, [.. encoding.GetPreamble(), .. encoding.GetBytes(text)]);
            _settings.Update(s => s with { LastFolder = Path.GetDirectoryName(fullTarget) });
            Message = UserMessage.Success($"Izvještaj je sačuvan: „{Path.GetFileName(fullTarget)}”.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning("Izvještaj o čišćenju ne može da se sačuva.", ex);
            Message = UserMessage.Error("Izvještaj nije sačuvan.");
            await _dialogs.ShowMessageAsync("Izvještaj nije sačuvan", $"Fajl „{Path.GetFileName(fullTarget)}” ne može da se sačuva.", MessageSeverity.Error, [ex.Message]);
        }
        finally
        {
            IsSaving = false;
            NotifyCommands();
        }
    }

    [RelayCommand]
    private void DismissMessage() => Message = null;

    // ---------------------------------------------------------------- podešavanja → sesija

    partial void OnTargetGroupChanged(decimal? value)
    {
        if (_loadingSettings)
        {
            return;
        }

        if (value is null || value < ushort.MinValue || value > ushort.MaxValue || decimal.Truncate(value.Value) != value.Value)
        {
            TargetGroupError = "Grupa SHMP mora biti cijeli broj od 0 do 65535.";
            return;
        }

        TargetGroupError = null;
        _settings.Update(s => s with { TargetGroup = (ushort)value.Value });
    }

    partial void OnEmptyInvoiceActionChanged(Option<CandidateAction?> value)
    {
        if (!_loadingSettings && value is not null)
        {
            _settings.Update(s => s with { EmptyInvoiceAction = value.Value });
        }
    }

    partial void OnNoGroupNoJmbMatchActionChanged(Option<CandidateAction?> value)
    {
        if (!_loadingSettings && value is not null)
        {
            _settings.Update(s => s with { NoGroupNoJmbMatchAction = value.Value });
        }
    }

    partial void OnRepairBracketsChanged(bool value)
    {
        if (!_loadingSettings)
        {
            _settings.Update(s => s with { RepairBrackets = value });
        }
    }

    partial void OnSelectedFilterChanged(Option<InvoiceFilter> value) => ApplyFilter();

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedRowChanged(InvoiceRowViewModel? value)
    {
        if (value is not null)
        {
            ShowRowPreview(value, 0);
        }
        else
        {
            ClearPreview();
        }
    }

    partial void OnEditingRowChanged(InvoiceRowViewModel? value)
    {
        OnPropertyChanged(nameof(IsEditing));
        NotifyCommands();
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => _ui.Post(() =>
    {
        LoadSettings(_settings.Current);
        ApplySettingsToSession();
    });

    private void LoadSettings(AppSettings settings)
    {
        _loadingSettings = true;
        try
        {
            if (TargetGroupError is null || TargetGroup == settings.TargetGroup)
            {
                TargetGroup = settings.TargetGroup;
                TargetGroupError = null;
            }

            EmptyInvoiceAction = DefaultActionOptions.First(o => o.Value == settings.EmptyInvoiceAction);
            NoGroupNoJmbMatchAction = DefaultActionOptions.First(o => o.Value == settings.NoGroupNoJmbMatchAction);
            RepairBrackets = settings.RepairBrackets;
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    /// <summary>Podrazumijevane akcije važe odmah; ciljna grupa i popravke traže novu analizu.</summary>
    private void ApplySettingsToSession()
    {
        if (_session is not { } session)
        {
            return;
        }

        var settings = _settings.Current;
        var changed = false;
        if (session.Options.EmptyInvoiceAction != settings.EmptyInvoiceAction)
        {
            session.SetDefaultAction(InvoiceCategory.EmptyInvoice, settings.EmptyInvoiceAction);
            changed = true;
        }

        if (session.Options.NoGroupNoJmbMatchAction != settings.NoGroupNoJmbMatchAction)
        {
            session.SetDefaultAction(InvoiceCategory.NoGroupNoJmbMatch, settings.NoGroupNoJmbMatchAction);
            changed = true;
        }

        if (changed)
        {
            Refresh();
        }
        else
        {
            UpdateSaveState();
        }
    }

    // ---------------------------------------------------------------- analiza (unutrašnje)

    private async Task RunAnalysisAsync()
    {
        var document = Document;
        if (document is null)
        {
            return;
        }

        CancelRunningAnalysis();
        if (EditingRow is not null)
        {
            CloseEditor();
        }

        var cts = new CancellationTokenSource();
        _analysisCts = cts;
        var generation = Interlocked.Increment(ref _analysisGeneration);
        var options = _settings.Current.ToCleaningOptions();

        IsAnalyzing = true;
        AnalysisError = null;
        Message = null;
        ShowProgress(new CleaningProgress(CleaningStage.Parsing, 0, 0));
        NotifyCommands();
        var progress = new CallbackProgress<CleaningProgress>(p => _ui.Post(() =>
        {
            if (generation == Interlocked.Read(ref _analysisGeneration))
            {
                ShowProgress(p);
            }
        }));

        try
        {
            var session = await _cleaning.AnalyzeAsync(document.Content, options, progress, cts.Token);
            if (generation != Interlocked.Read(ref _analysisGeneration) || !ReferenceEquals(document, Document))
            {
                return;
            }

            SetSession(session);
        }
        catch (OperationCanceledException)
        {
            if (generation == Interlocked.Read(ref _analysisGeneration))
            {
                Message = UserMessage.Warning(_session is null
                    ? "Analiza je prekinuta. Kliknite „Analiziraj” da je pokrenete ponovo."
                    : "Analiza je prekinuta; prikazan je prethodni rezultat.");
            }
        }
        catch (InvoiceDocumentException ex)
        {
            if (generation == Interlocked.Read(ref _analysisGeneration))
            {
                ClearAnalysis();
                AnalysisError = ex.Message;
            }
        }
        catch (Exception ex)
        {
            _log.Error("Analiza faktura nije uspjela.", ex);
            if (generation == Interlocked.Read(ref _analysisGeneration))
            {
                ClearAnalysis();
                AnalysisError = "Analiza nije uspjela zbog neočekivane greške. Detalji su u logu.";
            }
        }
        finally
        {
            if (ReferenceEquals(_analysisCts, cts))
            {
                _analysisCts = null;
                IsAnalyzing = false;
                NotifyCommands();
            }

            cts.Dispose();
        }
    }

    private void ShowProgress(CleaningProgress progress)
    {
        ProgressText = progress.Description;
        IsProgressIndeterminate = progress.Total <= 0;
        ProgressMaximum = Math.Max(1, progress.Total);
        ProgressValue = Math.Min(progress.Done, ProgressMaximum);
    }

    private void CancelRunningAnalysis()
    {
        Interlocked.Increment(ref _analysisGeneration);
        var cts = _analysisCts;
        _analysisCts = null;
        if (cts is not null)
        {
            cts.Cancel();
            IsAnalyzing = false;
        }
    }

    private void SetSession(CleaningSession session)
    {
        _session = session;
        _lastResult = null;
        _lastResultVersion = -1;
        _savedOutputPath = null;
        _savedVersion = -1;
        _allRows = [];
        HasAnalysis = true;
        FormatDescription = session.Document.Format.Description;
        DocumentWarnings = session.Document.Warnings;
        AnalyzedWith = Describe(session.Options);
        SelectedRow = null;
        ClearPreview();
        Refresh(refilter: true);
        var summary = Summary;
        var message = summary.Total == 0
            ? UserMessage.Warning("U fajlu nema nijedne fakture.")
            : summary.HasUnresolved
                ? UserMessage.Warning($"Analiza je završena. {Invoices(summary.Unresolved)} čeka odluku prije čuvanja.")
                : UserMessage.Success($"Analiza je završena: {Invoices(summary.Total)}, ostaje {summary.Remaining}, kandidata {summary.Candidates}.");
        Message = _contentNote is { } note ? message with { Text = $"{message.Text} {note}" } : message;
    }

    private void ClearAnalysis()
    {
        if (EditingRow is not null)
        {
            CloseEditor();
        }

        _session = null;
        _allRows = [];
        _lastResult = null;
        _lastResultVersion = -1;
        _savedOutputPath = null;
        _savedVersion = -1;
        Rows.Clear();
        Notes.Clear();
        OnPropertyChanged(nameof(HasNotes));
        SelectedRow = null;
        SetSelectedRows([]);
        ClearPreview();
        HasAnalysis = false;
        IsAnalysisStale = false;
        AnalyzedWith = string.Empty;
        FormatDescription = string.Empty;
        DocumentWarnings = [];
        Summary = CleaningSummary.Empty;
        RowsCountText = string.Empty;
        SaveBlockedReason = null;
        OutputCheckText = null;
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    /// <summary>Osvježava redove, sažetak, listu napomena i stanje čuvanja iz sesije.</summary>
    private void Refresh(bool refilter = false)
    {
        if (_session is not { } session)
        {
            return;
        }

        var existing = _allRows.ToDictionary(r => r.Id);
        var rows = new List<InvoiceRowViewModel>(session.Blocks.Count);
        var structureChanged = existing.Count != session.Blocks.Count;
        foreach (var state in session.Blocks)
        {
            if (existing.TryGetValue(state.Id, out var row))
            {
                row.Update(state);
            }
            else
            {
                row = new InvoiceRowViewModel(state, OnRowActionChosen);
                structureChanged = true;
            }

            rows.Add(row);
        }

        _allRows = rows;
        if (refilter || structureChanged)
        {
            ApplyFilter();
        }

        if (SelectedRow is { } selected && !_allRows.Contains(selected))
        {
            SelectedRow = null;
        }

        SetSelectedRows(SelectedRows.Where(_allRows.Contains));
        BuildSummary(session);
        BuildNotes(session);
        if (SelectedRow is { } current)
        {
            ShowRowPreview(current, PreviewHighlightLine > 0 && PreviewFirstLine > 0 ? PreviewFirstLine + PreviewHighlightLine - 1 : 0);
        }

        UpdateSaveState();
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    private void ApplyFilter()
    {
        var filter = SelectedFilter?.Value ?? InvoiceFilter.All;
        var search = (SearchText ?? string.Empty).Trim();
        var visible = _allRows.Where(r => r.MatchesFilter(filter) && r.Matches(search)).ToList();
        if (!Rows.SequenceEqual(visible))
        {
            var selected = SelectedRow;
            Rows.Clear();
            foreach (var row in visible)
            {
                Rows.Add(row);
            }

            if (selected is not null && visible.Contains(selected))
            {
                SelectedRow = selected;
            }
        }

        RowsCountText = _allRows.Count == 0
            ? string.Empty
            : visible.Count == _allRows.Count
                ? $"Prikazano: {Invoices(visible.Count)}"
                : $"Prikazano {visible.Count} od {Invoices(_allRows.Count)}";
    }

    private void BuildSummary(CleaningSession session)
    {
        var report = CleaningReport.Create(session, timeProvider: _time);
        Summary = new CleaningSummary(
            report.Total,
            report.Remaining,
            session.Blocks.Count(b => b.Classification.IsCandidate),
            report.Deleted,
            report.Skipped,
            report.Edited,
            report.Unresolved,
            report.RepairCount,
            report.WarningCount);
    }

    private void BuildNotes(CleaningSession session)
    {
        var notes = new List<CleaningNoteRow>();
        foreach (var warning in session.Document.Warnings)
        {
            notes.Add(new CleaningNoteRow(CleaningNoteKind.Document, 0, warning, null));
        }

        var unrecognized = session.Document.Unrecognized.ToList();
        var u = 0;
        foreach (var state in session.Blocks)
        {
            while (u < unrecognized.Count && unrecognized[u].StartLine < state.Block.StartLine)
            {
                notes.Add(UnrecognizedNote(unrecognized[u++]));
            }

            var number = state.Data.Number is { Length: > 0 } n ? n : "(bez broja)";
            if (state.Disposition == BlockDisposition.Unresolved)
            {
                var why = state.Classification.Category == InvoiceCategory.SuspiciousBlock
                    ? $"Faktura {number}: sumnjiv blok, izaberite akciju. {state.Classification.Reason}"
                    : $"Faktura {number}: izabrana je akcija Uredi, a izmjena nije primijenjena.";
                notes.Add(new CleaningNoteRow(CleaningNoteKind.Unresolved, state.Block.StartLine, why, state.Id));
            }

            if (!state.IsEdited && state.PartIndex == 0)
            {
                foreach (var repair in state.Block.Repairs.Where(r => r.Applied))
                {
                    notes.Add(new CleaningNoteRow(CleaningNoteKind.Repair, repair.Line, $"Faktura {number}: {repair.Description}", state.Id));
                }
            }

            foreach (var warning in state.Warnings)
            {
                var line = !state.IsEdited && state.Block.Repairs.FirstOrDefault(r => !r.Applied && warning.Contains(r.Description, StringComparison.Ordinal)) is { } note
                    ? note.Line
                    : state.Block.StartLine;
                notes.Add(new CleaningNoteRow(CleaningNoteKind.Warning, line, $"Faktura {number}: {warning}", state.Id));
            }
        }

        while (u < unrecognized.Count)
        {
            notes.Add(UnrecognizedNote(unrecognized[u++]));
        }

        if (!Notes.SequenceEqual(notes))
        {
            Notes.Clear();
            foreach (var note in notes)
            {
                Notes.Add(note);
            }

            OnPropertyChanged(nameof(HasNotes));
        }
    }

    private static CleaningNoteRow UnrecognizedNote(UnrecognizedContent content) =>
        new(CleaningNoteKind.Unrecognized, content.StartLine, $"Neprepoznat sadržaj (ostaje u izlazu): {content.Preview}", null, content);

    private void UpdateSaveState()
    {
        if (_session is not { } session)
        {
            SaveBlockedReason = null;
            IsAnalysisStale = false;
            return;
        }

        var settings = _settings.Current;
        IsAnalysisStale = session.Options.TargetGroup != settings.TargetGroup
            || session.Options.ExamService != settings.ExamService
            || session.Options.RepairBrackets != settings.RepairBrackets;
        var unresolved = session.UnresolvedBlocks;
        SaveBlockedReason =
            IsAnalysisStale
                ? $"Pravilo čišćenja ili automatske popravke su promijenjeni poslije analize (prikazano: {Describe(session.Options)}). Kliknite „Analiziraj” prije čuvanja."
            : unresolved.Count > 0
                ? $"Čuvanje nije moguće: {Invoices(unresolved.Count)} čeka odluku (prva na liniji {unresolved[0].Block.StartLine}). "
                    + "Izaberite akciju (Obriši, Preskoči ili Uredi) za svaku fakturu koja čeka odluku, a za akciju Uredi primijenite izmjenu."
            : null;

        if (_lastResult is { } result && _lastResultVersion == session.Version)
        {
            (OutputCheckText, OutputCheckSeverity) = DescribeCheck(result.Validation);
        }
        else
        {
            OutputCheckText = null;
            OutputCheckSeverity = MessageSeverity.Info;
        }

        NotifyCommands();
    }

    internal static (string Text, MessageSeverity Severity) DescribeCheck(ValidationResult validation)
    {
        var (summary, severity) = ValidationTabViewModel.Summarize(validation);
        var text = validation.IsValid
            ? $"Očišćen fajl je provjeren prema šemi „{validation.SchemaName}”: {summary}"
            : $"Očišćen fajl ima greške prema šemi „{validation.SchemaName}”: {summary}";
        return (text, severity);
    }

    /// <summary>Pravi izlaz (na UI niti, jer sesija nije bezbjedna za više niti) i provjerava ga u pozadini.</summary>
    private async Task<CleaningResult?> GetCheckedResultAsync(CleaningSession session)
    {
        if (_lastResult is { } cached && _lastResultVersion == session.Version)
        {
            return cached;
        }

        var version = session.Version;
        var output = session.BuildOutput();
        try
        {
            using var stream = new MemoryStream(output.Bytes, writable: false);
            var validation = await _validator.ValidateAsync(stream);
            var result = new CleaningResult(output, validation);
            if (ReferenceEquals(session, _session) && session.Version == version)
            {
                _lastResult = result;
                _lastResultVersion = version;
                UpdateSaveState();
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error("Provjera očišćenog fajla nije uspjela.", ex);
            await _dialogs.ShowMessageAsync("Provjera nije uspjela", "Očišćen fajl ne može da se provjeri prema šemi. Detalji su u logu.", MessageSeverity.Error);
            return null;
        }
    }

    // ---------------------------------------------------------------- akcije i uređivanje (unutrašnje)

    private void OnRowActionChosen(InvoiceRowViewModel row, CandidateAction action)
    {
        if (_session is not { } session || IsBusy || !row.IsCandidate)
        {
            row.Update(row.State);
            return;
        }

        if (action == CandidateAction.Edit)
        {
            if (row.IsEdited && row.State.UserAction == CandidateAction.Edit)
            {
                SelectedRow = row;
                BeginEdit(row, changedAction: false, previousAction: row.State.UserAction);
                return;
            }

            var previous = row.State.UserAction;
            session.SetAction(row.Id, CandidateAction.Edit);
            Refresh();
            SelectedRow = row;
            BeginEdit(row, changedAction: true, previousAction: previous);
            return;
        }

        session.SetAction(row.Id, action);
        Message = null;
        Refresh();
    }

    private void SetActionForSelected(CandidateAction action)
    {
        if (_session is not { } session)
        {
            return;
        }

        var selected = SelectedRows.Count > 0 ? SelectedRows : SelectedRow is { } single ? [single] : [];
        var changed = session.SetActionForMany(selected.Select(r => r.Id), action);
        var skipped = selected.Count - changed;
        Refresh();
        Message = UserMessage.Info(
            $"Akcija „{CleaningText.Action(action)}” je postavljena za {Invoices(changed)}."
            + (skipped > 0 ? $" {Invoices(skipped)} nije kandidat i ostaje u fajlu." : string.Empty));
    }

    private void BeginEdit(InvoiceRowViewModel row, bool changedAction, CandidateAction? previousAction)
    {
        if (EditingRow is not null)
        {
            DiscardEdit();
        }

        _editChangedAction = changedAction;
        _editPreviousAction = previousAction;
        _editInitialText = row.State.OutputText;
        EditError = null;
        Message = null;
        EditTitle = $"Uređivanje fakture {row.NumberText} (linije {row.LineText})";
        EditText = _editInitialText;
        EditingRow = row;
    }

    private void DiscardEdit()
    {
        var row = EditingRow;
        var changedAction = _editChangedAction;
        var previous = _editPreviousAction;
        CloseEditor();
        if (row is null || _session is not { } session || !changedAction)
        {
            return;
        }

        // Akcija Uredi je izabrana samo da bi se otvorio editor: vraća se prethodna.
        var state = session.Blocks.FirstOrDefault(b => b.Id == row.Id);
        if (state is { IsEdited: false, UserAction: CandidateAction.Edit })
        {
            if (previous is { } action)
            {
                session.SetAction(row.Id, action);
            }
            else
            {
                session.ClearAction(row.Id);
            }

            Refresh();
        }
    }

    private void CloseEditor()
    {
        EditingRow = null;
        EditError = null;
        EditTitle = string.Empty;
        EditText = string.Empty;
        _editInitialText = string.Empty;
        _editChangedAction = false;
        _editPreviousAction = null;
    }

    // ---------------------------------------------------------------- prikaz (unutrašnje)

    private void ShowRow(InvoiceRowViewModel row)
    {
        if (!Rows.Contains(row))
        {
            SearchText = string.Empty;
            SelectedFilter = FilterOptions[0];
        }

        SelectedRow = row;
        RowNavigationRequested?.Invoke(this, row);
    }

    private void ShowRowPreview(InvoiceRowViewModel row, int fileLine)
    {
        var state = row.State;
        var edited = state.IsEdited;
        PreviewTitle = $"Faktura {row.NumberText} · linije {row.LineText}"
            + (row.HasPart ? $" · {row.PartText}" : string.Empty)
            + (edited ? " · uređen tekst" : state.Block.IsRepaired ? " · sa automatskim popravkama" : string.Empty);
        PreviewText = state.OutputText;
        PreviewFirstLine = edited ? 0 : state.Block.StartLine;
        PreviewHighlightLine = !edited && fileLine >= state.Block.StartLine && fileLine <= state.Block.EndLine
            ? fileLine - state.Block.StartLine + 1
            : 0;
    }

    private void ClearPreview()
    {
        PreviewTitle = null;
        PreviewText = string.Empty;
        PreviewFirstLine = 0;
        PreviewHighlightLine = 0;
    }

    private void NotifyCommands()
    {
        AnalyzeCommand.NotifyCanExecuteChanged();
        CancelAnalysisCommand.NotifyCanExecuteChanged();
        UseDefaultsCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        SkipSelectedCommand.NotifyCanExecuteChanged();
        ResetSelectedCommand.NotifyCanExecuteChanged();
        StartEditCommand.NotifyCanExecuteChanged();
        ApplyEditCommand.NotifyCanExecuteChanged();
        CancelEditCommand.NotifyCanExecuteChanged();
        RevertEditCommand.NotifyCanExecuteChanged();
        SaveOutputCommand.NotifyCanExecuteChanged();
        SaveReportCommand.NotifyCanExecuteChanged();
    }

    private async Task<bool> ConfirmDiscardDecisionsAsync()
    {
        if (_session is not { } session || !HasDecisions(session))
        {
            return true;
        }

        return await _dialogs.ConfirmAsync(
            "Ponovna analiza",
            "Ponovna analiza poništava pojedinačno izabrane akcije i ručne izmjene faktura. Ulazni fajl je netaknut. Da li da nastavim?",
            "Analiziraj ponovo",
            "Otkaži",
            isDangerous: true);
    }

    private static bool HasDecisions(CleaningSession session) => session.Blocks.Any(b => b.UserAction is not null || b.IsEdited);

    internal static string Describe(CleaningOptions options) =>
        $"grupa SHMP {options.TargetGroup}, pregled {(options.ExamService is { } exam ? exam.ToString(CultureInfo.InvariantCulture) : "bilo koja usluga")}, "
        + $"automatske popravke {(options.RepairBrackets ? "uključene" : "isključene")}";

    /// <summary>„1 faktura”, „2 fakture”, „5 faktura” (srpska množina).</summary>
    internal static string Invoices(int count)
    {
        var mod10 = count % 10;
        var mod100 = count % 100;
        var word = mod10 == 1 && mod100 != 11 ? "faktura"
            : mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14 ? "fakture"
            : "faktura";
        return $"{count} {word}";
    }

    /// <summary>Prosljeđuje napredak odmah (bez <see cref="SynchronizationContext"/>); UI nit se bira preko <see cref="IUiDispatcher"/>.</summary>
    private sealed class CallbackProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
