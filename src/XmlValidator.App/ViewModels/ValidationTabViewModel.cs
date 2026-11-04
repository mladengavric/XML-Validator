using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XmlValidator.App.Services;
using XmlValidator.Core.Cleaning;
using XmlValidator.Core.Schemas;
using XmlValidator.Core.Validation;

namespace XmlValidator.App.ViewModels;

/// <summary>Jedan red u listi nalaza validacije.</summary>
public sealed record ValidationIssueRow(ValidationIssue Issue)
{
    public bool IsError => Issue.Severity == ValidationSeverity.Error;

    public MessageSeverity Severity => IsError ? MessageSeverity.Error : MessageSeverity.Warning;

    public string SeverityText => ValidationReportFormatter.Severity(Issue.Severity);

    /// <summary>Linija (0 = nepoznata).</summary>
    public int Line => Issue.Line;

    public string LineText => Issue.Line > 0 ? Issue.Line.ToString(CultureInfo.InvariantCulture) : "—";

    public string ColumnText => Issue.Column > 0 ? Issue.Column.ToString(CultureInfo.InvariantCulture) : "—";

    public string KindText => ValidationReportFormatter.Kind(Issue.Kind);

    public string Message => Issue.Message;

    /// <summary>Originalna poruka validatora (engleski), za opis alata; <c>null</c> kad je nema.</summary>
    public string? TechnicalMessage => Issue.TechnicalMessage;

    public bool HasTechnicalMessage => Issue.TechnicalMessage is not null && Issue.TechnicalMessage != Issue.Message;

    public bool HasPosition => Issue.Line > 0;
}

/// <summary>Šema u padajućoj listi taba.</summary>
public sealed record SchemaOption(string Id, string Name, bool IsBuiltIn)
{
    public string DisplayName => IsBuiltIn ? $"{Name} (ugrađena)" : Name;

    public override string ToString() => DisplayName;
}

/// <summary>Zahtjev da editor skoči na mjesto (linija i kolona počinju od 1; kolona 0 = početak linije).</summary>
public sealed class EditorNavigationEventArgs(int line, int column) : EventArgs
{
    public int Line { get; } = line;

    public int Column { get; } = column;
}

/// <summary>
/// Tab „Validacija”: sadržaj fajla u editoru, validacija prema aktivnoj šemi (F5), lista nalaza sa
/// skokom na liniju, brza promjena aktivne šeme, kopiranje i izvoz nalaza.
/// </summary>
public sealed partial class ValidationTabViewModel : DocumentTabViewModel, IDisposable
{
    private readonly XmlValidationService _validator;
    private readonly SchemaStore _store;
    private readonly ISchemaProvider _schemas;
    private readonly IDialogService _dialogs;
    private readonly IClipboardService _clipboard;
    private readonly ISettingsService _settings;
    private readonly IUiDispatcher _ui;
    private readonly IAppLog _log;
    private readonly TimeProvider _time;

    private string _loadedText = string.Empty;
    private DocumentTextFormat? _format;
    private CancellationTokenSource? _validationCts;
    private ValidationReport? _report;
    private long _validatedRevision = -1;
    private long _savedRevision;
    private long _schemaLoadGeneration;
    private bool _suppressSchemaSelection;
    private Task _lastBackgroundWork = Task.CompletedTask;

    public ValidationTabViewModel(
        XmlValidationService validator,
        SchemaStore store,
        ISchemaProvider schemas,
        IDialogService dialogs,
        IClipboardService clipboard,
        ISettingsService settings,
        IUiDispatcher ui,
        IAppLog log,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(log);
        _validator = validator;
        _store = store;
        _schemas = schemas;
        _dialogs = dialogs;
        _clipboard = clipboard;
        _settings = settings;
        _ui = ui;
        _log = log;
        _time = time ?? TimeProvider.System;
        _store.Changed += OnSchemaStoreChanged;
    }

    public override string Header => "Validacija";

    public override string EmptyStateHint =>
        "Otvorite XML fajl (Ctrl+O) ili ga prevucite na prozor da biste ga provjerili prema aktivnoj šemi.";

    /// <summary>Zahtjev editoru da skoči na mjesto nalaza (prikaz se pretplaćuje).</summary>
    public event EventHandler<EditorNavigationEventArgs>? NavigationRequested;

    // ---------------------------------------------------------------- editor

    /// <summary>Tekst u editoru. Pri otvaranju fajla to je dekodiran sadržaj fajla, bez izmjena.</summary>
    [ObservableProperty]
    public partial string EditorText { get; set; } = string.Empty;

    /// <summary>Brojač izmjena teksta; rezultat validacije je zastario kad se razlikuje od validirane verzije.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsResultStale))]
    public partial long TextRevision { get; private set; }

    /// <summary>Tekst u editoru se razlikuje od fajla na disku.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsavedChanges))]
    public partial bool IsModified { get; private set; }

    /// <summary>Editor je samo za čitanje (fajl nije ispravno kodiran, pa izmjena ne bi mogla da se sačuva bez gubitka).</summary>
    [ObservableProperty]
    public partial bool IsReadOnly { get; private set; }

    /// <summary>Napomena o kodiranju fajla (upozorenje ili razlog zašto je editor samo za čitanje).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEncodingNote))]
    public partial string? EncodingNote { get; private set; }

    public bool HasEncodingNote => EncodingNote is not null;

    /// <summary>Kodiranje i završeci linija fajla, npr. „UTF-8 bez BOM-a, CRLF, …”.</summary>
    [ObservableProperty]
    public partial string FormatDescription { get; private set; } = string.Empty;

    public override bool HasUnsavedChanges => IsModified && TextRevision != _savedRevision;

    // ---------------------------------------------------------------- validacija

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ValidateCommand), nameof(CancelValidationCommand), nameof(CopyIssuesCommand), nameof(ExportIssuesCommand))]
    public partial bool IsValidating { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult), nameof(IsResultStale), nameof(HasIssues), nameof(TruncationNote), nameof(HasTruncationNote))]
    [NotifyCanExecuteChangedFor(nameof(CopyIssuesCommand), nameof(ExportIssuesCommand))]
    public partial ValidationResult? Result { get; private set; }

    public bool HasResult => Result is not null;

    public bool HasIssues => Issues.Count > 0;

    /// <summary>Tekst je izmijenjen poslije posljednje validacije.</summary>
    public bool IsResultStale => Result is not null && _validatedRevision != TextRevision;

    public string? TruncationNote => Result?.TruncationNote;

    public bool HasTruncationNote => TruncationNote is not null;

    /// <summary>Sažetak: „Fajl je ispravan ✓”, „Pronađeno N grešaka”, „Validacija u toku…”.</summary>
    [ObservableProperty]
    public partial string SummaryText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial MessageSeverity SummarySeverity { get; private set; } = MessageSeverity.Info;

    /// <summary>Šema kojom je urađena posljednja validacija (za prikaz i izvještaj).</summary>
    [ObservableProperty]
    public partial string? ValidatedSchemaName { get; private set; }

    public ObservableCollection<ValidationIssueRow> Issues { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedIssue))]
    [NotifyCanExecuteChangedFor(nameof(CopySelectedIssueCommand))]
    public partial ValidationIssueRow? SelectedIssue { get; set; }

    /// <summary>Izabran je nalaz (prikazuju se detalji ispod tabele).</summary>
    public bool HasSelectedIssue => SelectedIssue is not null;

    // ---------------------------------------------------------------- šeme

    public ObservableCollection<SchemaOption> SchemaOptions { get; } = [];

    /// <summary>Izabrana šema; izbor korisnika postavlja aktivnu šemu (i ponovo pokreće validaciju).</summary>
    [ObservableProperty]
    public partial SchemaOption? SelectedSchema { get; set; }

    /// <summary>Razlog povratka na ugrađenu šemu; <c>null</c> kad je aktivna šema u redu.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSchemaWarning))]
    public partial string? SchemaWarning { get; private set; }

    public bool HasSchemaWarning => SchemaWarning is not null;

    // ---------------------------------------------------------------- javni API

    /// <summary>Učitava listu šema (poziva glavni prozor pri pokretanju).</summary>
    public Task InitializeAsync() => RefreshSchemasAsync();

    /// <summary>Postavlja otvoren fajl: dekodira ga u editor i odmah pokreće validaciju.</summary>
    public override void SetDocument(OpenedDocument? document)
    {
        CancelRunningValidation();
        base.SetDocument(document);
        ValidateCommand.NotifyCanExecuteChanged();
        ClearResult();

        if (document is null)
        {
            _format = null;
            _loadedText = string.Empty;
            EncodingNote = null;
            FormatDescription = string.Empty;
            IsReadOnly = false;
            SetEditorTextSilently(string.Empty);
            return;
        }

        try
        {
            var decoded = DocumentTextDecoder.Decode(document.Content);
            _format = decoded.Format;
            _loadedText = decoded.Text;
            IsReadOnly = false;
            FormatDescription = decoded.Format.Description;
            EncodingNote = decoded.Warnings.Count > 0 ? string.Join(" ", decoded.Warnings) : null;
        }
        catch (InvoiceDocumentException ex)
        {
            // Prikaz bez gubitka nije moguć: tekst se prikazuje približno, samo za čitanje, a validira se original.
            _format = null;
            _loadedText = LossyDecode(document.Content);
            IsReadOnly = true;
            FormatDescription = string.Empty;
            EncodingNote = ex.Message + " Prikaz je približan i samo za čitanje; validira se originalni fajl.";
        }

        SetEditorTextSilently(_loadedText);
        _lastBackgroundWork = RunValidationAsync();
    }

    public override byte[]? GetContentToSave()
    {
        var document = Document;
        if (document is null)
        {
            return null;
        }

        if (!IsModified || _format is null)
        {
            return document.Content;
        }

        return DocumentTextDecoder.Encode(PrepareTextForSave(EditorText, _format), _format);
    }

    public override string? GetSaveProblem()
    {
        if (Document is null || !IsModified || _format is null)
        {
            return null;
        }

        var bad = DocumentTextDecoder.FindUnencodableCharacter(EditorText, _format);
        return bad is null
            ? null
            : $"Znak „{bad}” ne postoji u kodiranju fajla ({_format.EncodingName}). Uklonite ga ili zamijenite pa sačuvajte ponovo.";
    }

    public override void MarkSaved()
    {
        _savedRevision = TextRevision;
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    public void Dispose()
    {
        _store.Changed -= OnSchemaStoreChanged;
        CancelRunningValidation();
    }

    /// <summary>Pretvara završetke linija u one iz fajla kad su u fajlu ujednačeni (mješoviti ostaju kakvi jesu).</summary>
    internal static string PrepareTextForSave(string text, DocumentTextFormat format) =>
        format.LineEndings is LineEndingKind.Lf or LineEndingKind.CrLf or LineEndingKind.Cr
            ? DocumentTextDecoder.NormalizeLineEndings(text, format.NewLine)
            : text;

    // ---------------------------------------------------------------- komande

    private bool CanValidate() => Document is not null && !IsValidating;

    /// <summary>Validira trenutni sadržaj editora (F5). Neizmijenjen sadržaj se validira iz originalnih bajtova.</summary>
    [RelayCommand(CanExecute = nameof(CanValidate))]
    private Task ValidateAsync() => RunValidationAsync();

    private bool CanCancelValidation() => IsValidating;

    [RelayCommand(CanExecute = nameof(CanCancelValidation))]
    private void CancelValidation() => _validationCts?.Cancel();

    /// <summary>Skok na mjesto nalaza u editoru.</summary>
    [RelayCommand]
    private void GoToIssue(ValidationIssueRow? row)
    {
        if (row is { HasPosition: true })
        {
            NavigationRequested?.Invoke(this, new EditorNavigationEventArgs(row.Issue.Line, row.Issue.Column));
        }
    }

    /// <summary>Sljedeći nalaz (F8): bira ga u listi i skače na njega u editoru; poslije posljednjeg ide na prvi.</summary>
    [RelayCommand]
    private void NextIssue() => MoveIssue(+1);

    /// <summary>Prethodni nalaz (Shift+F8); prije prvog ide na posljednji.</summary>
    [RelayCommand]
    private void PreviousIssue() => MoveIssue(-1);

    private void MoveIssue(int step)
    {
        if (Issues.Count == 0)
        {
            return;
        }

        var index = SelectedIssue is { } current ? Issues.IndexOf(current) : -1;
        index = index < 0
            ? (step > 0 ? 0 : Issues.Count - 1)
            : (index + step + Issues.Count) % Issues.Count;
        SelectedIssue = Issues[index];
        GoToIssue(SelectedIssue);
    }

    private bool CanExport() => Result is not null && !IsValidating;

    /// <summary>Kopira poruku izabranog nalaza (sa mjestom i tehničkim opisom) u clipboard.</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedIssue))]
    private async Task CopySelectedIssueAsync()
    {
        if (SelectedIssue is not { } issue)
        {
            return;
        }

        var text = issue.Message;
        if (issue.HasPosition)
        {
            text = $"Linija {issue.LineText}, kolona {issue.ColumnText}: {text}";
        }

        if (issue.HasTechnicalMessage)
        {
            text += Environment.NewLine + "Tehnički opis: " + issue.TechnicalMessage;
        }

        try
        {
            await _clipboard.SetTextAsync(text);
            SetSummaryNote("Poruka je kopirana u clipboard.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning("Kopiranje u clipboard nije uspjelo.", ex);
            await _dialogs.ShowMessageAsync("Kopiranje nije uspjelo", "Poruka ne može da se kopira u clipboard.", MessageSeverity.Error);
        }
    }

    /// <summary>Kopira nalaze (sa nazivom šeme) u clipboard.</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task CopyIssuesAsync()
    {
        if (_report is null)
        {
            return;
        }

        try
        {
            await _clipboard.SetTextAsync(ValidationReportFormatter.ToText(_report));
            SetSummaryNote("Nalazi su kopirani u clipboard.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning("Kopiranje u clipboard nije uspjelo.", ex);
            await _dialogs.ShowMessageAsync("Kopiranje nije uspjelo", "Nalazi ne mogu da se kopiraju u clipboard.", MessageSeverity.Error);
        }
    }

    /// <summary>Izvoz nalaza u <c>.txt</c> ili <c>.csv</c> (po ekstenziji izabranog fajla).</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportIssuesAsync()
    {
        var report = _report;
        var document = Document;
        if (report is null || document is null)
        {
            return;
        }

        var suggested = Path.GetFileNameWithoutExtension(document.FileName) + "_validacija.txt";
        var target = await _dialogs.PickSaveFileAsync("Izvezi nalaze validacije", suggested, [FileFilter.Text, FileFilter.Csv], document.Folder);
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

        var isCsv = string.Equals(Path.GetExtension(fullTarget), ".csv", StringComparison.OrdinalIgnoreCase);
        var (text, encoding) = isCsv
            ? (ValidationReportFormatter.ToCsv(report), ValidationReportFormatter.CsvEncoding)
            : (ValidationReportFormatter.ToText(report), ValidationReportFormatter.TextEncoding);
        try
        {
            await AtomicFile.WriteAllBytesAsync(fullTarget, [.. encoding.GetPreamble(), .. encoding.GetBytes(text)]);
            _settings.Update(s => s with { LastFolder = Path.GetDirectoryName(fullTarget) });
            SetSummaryNote($"Nalazi su sačuvani u „{Path.GetFileName(fullTarget)}”.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning("Izvještaj validacije ne može da se sačuva.", ex);
            await _dialogs.ShowMessageAsync("Izvještaj nije sačuvan", $"Fajl „{Path.GetFileName(fullTarget)}” ne može da se sačuva.", MessageSeverity.Error, [ex.Message]);
        }
    }

    [RelayCommand]
    private Task ManageSchemasAsync() => _dialogs.ShowSettingsAsync(SettingsPage.Schemas);

    // ---------------------------------------------------------------- unutrašnje

    partial void OnEditorTextChanged(string value)
    {
        TextRevision++;
        IsModified = !string.Equals(value, _loadedText, StringComparison.Ordinal);
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    partial void OnSelectedSchemaChanged(SchemaOption? value)
    {
        if (_suppressSchemaSelection || value is null)
        {
            return;
        }

        // SetActive može odmah pokrenuti osvježavanje (događaj skladišta); čeka se i ono.
        var activation = ActivateSchemaAsync(value);
        _lastBackgroundWork = Task.WhenAll(activation, _lastBackgroundWork);
    }

    private async Task ActivateSchemaAsync(SchemaOption option)
    {
        try
        {
            if (string.Equals(_store.ActiveSchemaId, option.Id, StringComparison.Ordinal) && !HasSchemaWarning)
            {
                return;
            }

            // Changed iz skladišta osvježava listu i ponovo pokreće validaciju.
            _store.SetActive(option.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            _log.Warning("Aktivna šema ne može da se promijeni.", ex);
            await _dialogs.ShowMessageAsync("Šema nije promijenjena", $"Šema „{option.Name}” ne može da se postavi kao aktivna.", MessageSeverity.Error, [ex.Message]);
            await RefreshSchemasAsync();
        }
    }

    private async Task RunValidationAsync()
    {
        var document = Document;
        if (document is null)
        {
            return;
        }

        CancelRunningValidation();
        var cts = new CancellationTokenSource();
        _validationCts = cts;
        var revision = TextRevision;
        var modified = IsModified && !IsReadOnly;
        var text = EditorText;

        IsValidating = true;
        SummaryText = "Validacija u toku…";
        SummarySeverity = MessageSeverity.Info;
        try
        {
            ValidationResult result;
            if (modified)
            {
                result = await _validator.ValidateAsync(text, cts.Token);
            }
            else
            {
                using var stream = new MemoryStream(document.Content, writable: false);
                result = await _validator.ValidateAsync(stream, cts.Token);
            }

            if (cts.IsCancellationRequested || !ReferenceEquals(document, Document))
            {
                return;
            }

            ApplyResult(result, new ValidationReport(result, document.Path, _time.GetLocalNow(), modified), revision);
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_validationCts, cts))
            {
                SummaryText = Result is null ? "Validacija je prekinuta." : "Validacija je prekinuta; prikazan je prethodni rezultat.";
                SummarySeverity = MessageSeverity.Warning;
            }
        }
        catch (Exception ex)
        {
            _log.Error("Validacija nije uspjela.", ex);
            if (ReferenceEquals(_validationCts, cts))
            {
                SummaryText = "Validacija nije uspjela. Detalji su u logu.";
                SummarySeverity = MessageSeverity.Error;
            }
        }
        finally
        {
            if (ReferenceEquals(_validationCts, cts))
            {
                _validationCts = null;
                IsValidating = false;
            }

            cts.Dispose();
        }
    }

    private void ApplyResult(ValidationResult result, ValidationReport report, long revision)
    {
        _report = report;
        _validatedRevision = revision;
        Issues.Clear();
        foreach (var issue in result.Issues)
        {
            Issues.Add(new ValidationIssueRow(issue));
        }

        SelectedIssue = null;
        Result = result;
        ValidatedSchemaName = result.SchemaName;
        OnPropertyChanged(nameof(IsResultStale));
        (SummaryText, SummarySeverity) = Summarize(result);
    }

    internal static (string Text, MessageSeverity Severity) Summarize(ValidationResult result) => (result.ErrorCount, result.WarningCount) switch
    {
        (0, 0) => ("Fajl je ispravan ✓", MessageSeverity.Success),
        (0, var w) => ($"Fajl je ispravan ✓ (upozorenja: {w})", MessageSeverity.Success),
        (var e, 0) => ($"Pronađeno {Errors(e)}{Where(result)}", MessageSeverity.Error),
        (var e, var w) => ($"Pronađeno {Errors(e)}{Where(result)} · upozorenja: {w}", MessageSeverity.Error),
    };

    /// <summary>„ u 3 fakture”, „ u 3 fakture i 1 van faktura”, „ van faktura” (više grešaka može biti na jednoj fakturi).</summary>
    internal static string Where(ValidationResult result) => (result.InvoicesWithErrors, result.ErrorsOutsideInvoices) switch
    {
        (0, 0) => string.Empty,
        (0, _) => " van faktura",
        (var i, 0) => $" {ValidationResult.InInvoices(i)}",
        (var i, var o) => $" {ValidationResult.InInvoices(i)} i {o} van faktura",
    };

    /// <summary>„1 greška”, „2 greške”, „5 grešaka”, „21 greška” (srpska množina).</summary>
    internal static string Errors(int count)
    {
        var mod10 = count % 10;
        var mod100 = count % 100;
        var word = mod10 == 1 && mod100 != 11 ? "greška"
            : mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14 ? "greške"
            : "grešaka";
        return $"{count} {word}";
    }

    private void ClearResult()
    {
        _report = null;
        _validatedRevision = -1;
        Issues.Clear();
        SelectedIssue = null;
        Result = null;
        ValidatedSchemaName = null;
        SummaryText = string.Empty;
        SummarySeverity = MessageSeverity.Info;
    }

    private void SetSummaryNote(string note)
    {
        if (Result is { } result)
        {
            var (text, severity) = Summarize(result);
            SummaryText = $"{text} · {note}";
            SummarySeverity = severity;
        }
    }

    private void CancelRunningValidation()
    {
        var cts = _validationCts;
        _validationCts = null;
        if (cts is not null)
        {
            cts.Cancel();
            IsValidating = false;
        }
    }

    private void SetEditorTextSilently(string text)
    {
        EditorText = text;
        IsModified = false;
        _savedRevision = TextRevision;
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    private static string LossyDecode(byte[] content)
    {
        var text = System.Text.Encoding.UTF8.GetString(content);
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }

    private async Task RefreshSchemasAsync()
    {
        var generation = Interlocked.Increment(ref _schemaLoadGeneration);
        IReadOnlyList<SchemaInfo> list;
        ActiveSchema active;
        try
        {
            (list, active) = await Task.Run(() => (_store.List(), _schemas.GetActiveSchema()));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error("Lista šema ne može da se učita.", ex);
            SchemaWarning = "Lista šema ne može da se učita. Detalji su u logu.";
            return;
        }

        if (generation != Interlocked.Read(ref _schemaLoadGeneration))
        {
            return; // stigao je noviji zahtjev
        }

        var options = list.Select(s => new SchemaOption(s.Id, s.Name, s.IsBuiltIn)).ToList();
        _suppressSchemaSelection = true;
        try
        {
            if (!SchemaOptions.SequenceEqual(options))
            {
                SchemaOptions.Clear();
                foreach (var option in options)
                {
                    SchemaOptions.Add(option);
                }
            }

            SelectedSchema = SchemaOptions.FirstOrDefault(o => o.Id == active.Info.Id);
        }
        finally
        {
            _suppressSchemaSelection = false;
        }

        SchemaWarning = active.IsFallback ? active.FallbackReason : null;
    }

    private void OnSchemaStoreChanged(object? sender, EventArgs e) => _ui.Post(() => _lastBackgroundWork = OnSchemaStoreChangedAsync());

    /// <summary>Posao pokrenut automatski (validacija pri otvaranju, osvježavanje poslije promjene šeme); za testove.</summary>
    internal Task WhenBackgroundWorkCompletedAsync() => _lastBackgroundWork;

    private async Task OnSchemaStoreChangedAsync()
    {
        await RefreshSchemasAsync();
        if (Document is not null)
        {
            await RunValidationAsync();
        }
    }
}
