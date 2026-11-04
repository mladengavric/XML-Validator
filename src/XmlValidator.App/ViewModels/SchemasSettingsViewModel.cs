using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XmlValidator.App.Services;
using XmlValidator.Core.Schemas;

namespace XmlValidator.App.ViewModels;

/// <summary>Jedna šema u listi na stranici „Šeme”.</summary>
public sealed class SchemaItemViewModel
{
    private static readonly CultureInfo Serbian = CultureInfo.GetCultureInfo("sr-Latn");

    public SchemaItemViewModel(SchemaInfo info, bool isActive, string? problem)
    {
        ArgumentNullException.ThrowIfNull(info);
        Info = info;
        IsActive = isActive;
        Problem = problem;
    }

    public SchemaInfo Info { get; }

    public string Id => Info.Id;

    public string Name => Info.Name;

    public bool IsBuiltIn => Info.IsBuiltIn;

    /// <summary>Izabrana kao aktivna (u skladištu).</summary>
    public bool IsActive { get; }

    /// <summary>Zašto aktivna šema ne može da se koristi (pa se koristi ugrađena); inače <c>null</c>.</summary>
    public string? Problem { get; }

    public bool HasProblem => Problem is not null;

    public string VersionText => string.IsNullOrWhiteSpace(Info.Version) ? "—" : Info.Version;

    public string ImportedAtText => Info.ImportedAt is { } at
        ? at.ToLocalTime().ToString("dd.MM.yyyy. HH:mm", Serbian)
        : "—";

    public string SourceText => IsBuiltIn ? "ugrađena u program" : Info.SourceFileName;

    /// <summary>Kratak opis stanja za čitače ekrana i opis stavke.</summary>
    public string StatusText => (IsActive, IsBuiltIn, HasProblem) switch
    {
        (true, _, true) => "Aktivna, ali neupotrebljiva (koristi se ugrađena)",
        (true, true, _) => "Aktivna · Ugrađena",
        (true, false, _) => "Aktivna",
        (false, true, _) => "Ugrađena",
        _ => string.Empty,
    };
}

/// <summary>Jedan odjeljak prikaza razlika (npr. „Dodati elementi”).</summary>
public sealed record SchemaDiffSection(string Title, IReadOnlyList<string> Items)
{
    public int Count => Items.Count;
}

/// <summary>
/// Stranica „Šeme”: lista šema, uvoz (dugme ili prevlačenje), izbor aktivne, brisanje, preimenovanje
/// i prikaz razlika prema aktivnoj šemi.
/// </summary>
public sealed partial class SchemasSettingsViewModel : ViewModelBase, IDisposable
{
    private readonly SchemaStore _store;
    private readonly ISchemaProvider _schemas;
    private readonly IDialogService _dialogs;
    private readonly ISettingsService _settings;
    private readonly IUiDispatcher _ui;
    private readonly IAppLog _log;
    private readonly SchemaDownloader _downloader;
    private readonly AppPaths _paths;
    private CancellationTokenSource? _downloadCancellation;
    private int _loadGeneration;

    public SchemasSettingsViewModel(
        SchemaStore store,
        ISchemaProvider schemas,
        IDialogService dialogs,
        ISettingsService settings,
        IUiDispatcher ui,
        IAppLog log,
        SchemaDownloader downloader,
        AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(paths);
        _downloader = downloader;
        _paths = paths;
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(log);
        _store = store;
        _schemas = schemas;
        _dialogs = dialogs;
        _settings = settings;
        _ui = ui;
        _log = log;
        _store.Changed += OnStoreChanged;
    }

    public ObservableCollection<SchemaItemViewModel> Schemas { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetActiveCommand), nameof(DeleteCommand), nameof(RenameCommand), nameof(ShowDiffCommand))]
    public partial SchemaItemViewModel? SelectedSchema { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand), nameof(ImportFromUrlCommand), nameof(SetActiveCommand), nameof(DeleteCommand), nameof(RenameCommand), nameof(ShowDiffCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string BusyText { get; private set; } = string.Empty;

    /// <summary>Preuzimanje sa linka je u toku (može se prekinuti).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelDownloadCommand))]
    public partial bool IsDownloading { get; private set; }

    /// <summary>Napredak preuzimanja od 0 do 100; <c>null</c> kad server ne javi veličinu.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDownloadProgressKnown))]
    public partial double? DownloadPercent { get; private set; }

    public bool IsDownloadProgressKnown => DownloadPercent is not null;

    /// <summary>Poruka na vrhu stranice (ishod uvoza, greška, upozorenje o aktivnoj šemi).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial UserMessage? Message { get; private set; }

    public bool HasMessage => Message is not null;

    /// <summary>Upozorenje kad se umjesto aktivne koristi ugrađena šema.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFallbackWarning))]
    public partial string? FallbackWarning { get; private set; }

    public bool HasFallbackWarning => FallbackWarning is not null;

    /// <summary>Naslov prikaza razlika; <c>null</c> kad prikaz nije otvoren.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiff))]
    public partial string? DiffTitle { get; private set; }

    public bool HasDiff => DiffTitle is not null;

    /// <summary>Sažetak razlika (npr. „Nema razlika…” ili broj promjena).</summary>
    [ObservableProperty]
    public partial string? DiffSummary { get; private set; }

    public ObservableCollection<SchemaDiffSection> DiffSections { get; } = [];

    /// <summary>Tekst za prazno stanje liste (samo ugrađena šema).</summary>
    public string ImportHint => "Prevucite .xsd ili .zip fajl ovdje, kliknite „Uvezi šemu…”, ili nalijepite link ZIP paketa sa sajta FZO preko „Uvezi sa linka…”.";

    /// <summary>
    /// Uvoz šeme sa linka: preuzima <c>.zip</c> ili <c>.xsd</c> (samo <c>https</c>, ograničena veličina) u privremeni
    /// folder i pokreće isti uvoz kao za fajl. Pri uvozu se dalje ništa ne preuzima sa mreže.
    /// </summary>
    public async Task ImportFromUrlAsync(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (IsBusy)
        {
            return;
        }

        var problem = SchemaDownloader.ValidateUrl(url, out var uri);
        if (problem is not null || uri is null)
        {
            Message = UserMessage.Error("Šema nije preuzeta.", [problem ?? "Adresa nije ispravna."]);
            return;
        }

        Message = null;
        _settings.Update(s => s with { LastSchemaUrl = uri.AbsoluteUri });
        var folder = Path.Combine(_paths.DownloadsFolder, Guid.NewGuid().ToString("N"));
        using var cancellation = new CancellationTokenSource();
        _downloadCancellation = cancellation;
        SetBusy($"Preuzimam šemu sa „{uri.Host}”…");
        IsDownloading = true;
        DownloadPercent = null;
        try
        {
            Directory.CreateDirectory(folder);
            var progress = new Progress<SchemaDownloadProgress>(p => _ui.Post(() =>
            {
                if (IsDownloading)
                {
                    DownloadPercent = p.Total is > 0 ? Math.Min(100, 100.0 * p.Received / p.Total.Value) : null;
                }
            }));
            var download = await _downloader.DownloadAsync(uri.AbsoluteUri, folder, progress, cancellation.Token);
            _downloadCancellation = null;
            IsDownloading = false;
            DownloadPercent = null;
            BusyText = $"Uvozim „{download.FileName}”…";

            var result = await _store.ImportAsync(download.FilePath);
            if (result.Status == SchemaImportStatus.NeedsMainSchemaSelection)
            {
                ClearBusy();
                var choice = await _dialogs.ChooseAsync("Izbor glavne šeme", result.Message, result.MainSchemaCandidates, "Uvezi");
                if (choice is null)
                {
                    Message = UserMessage.Info("Uvoz je otkazan.");
                    return;
                }

                SetBusy($"Uvozim „{download.FileName}”…");
                result = await _store.ImportAsync(download.FilePath, new SchemaImportOptions(MainSchemaFile: choice));
            }

            await HandleImportResultAsync(result);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Message = UserMessage.Info("Preuzimanje je prekinuto. Ništa nije uvezeno.");
        }
        catch (SchemaDownloadException ex)
        {
            _log.Warning("Šema ne može da se preuzme sa linka.", ex);
            Message = UserMessage.Error($"Šema nije preuzeta sa „{uri.Host}”.", [ex.Message]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning("Preuzeta šema ne može da se sačuva.", ex);
            Message = UserMessage.Error("Preuzeta šema ne može da se sačuva u privremeni folder.", [ex.Message]);
        }
        finally
        {
            _downloadCancellation = null;
            IsDownloading = false;
            DownloadPercent = null;
            ClearBusy();
            DeleteDownloadFolder(folder);
        }
    }

    /// <summary>Učitava listu šema i stanje aktivne (bez blokiranja UI niti).</summary>
    public async Task LoadAsync(string? selectId = null)
    {
        selectId ??= SelectedSchema?.Id;
        var generation = Interlocked.Increment(ref _loadGeneration);
        try
        {
            var (schemas, activeId, active) = await Task.Run(() => (_store.List(), _store.ActiveSchemaId, _schemas.GetActiveSchema()));

            // Učitavanja se mogu preklopiti (komanda i događaj Changed); primjenjuje se samo najnovije.
            if (generation != Volatile.Read(ref _loadGeneration))
            {
                return;
            }

            var problem = active.IsFallback ? active.FallbackReason : null;
            Schemas.Clear();
            foreach (var info in schemas)
            {
                var isActive = info.Id == activeId;
                Schemas.Add(new SchemaItemViewModel(info, isActive, isActive ? problem : null));
            }

            FallbackWarning = problem;
            SelectedSchema = Schemas.FirstOrDefault(s => s.Id == selectId) ?? Schemas.FirstOrDefault(s => s.IsActive) ?? Schemas.FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning("Lista šema ne može da se učita.", ex);
            Message = UserMessage.Error("Lista šema ne može da se učita.", [ex.Message]);
        }
    }

    /// <summary>Uvoz šeme iz fajla (dugme „Uvezi šemu…” ili prevlačenje).</summary>
    public async Task ImportFromPathAsync(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (IsBusy)
        {
            return;
        }

        var fileName = Path.GetFileName(path);
        if (!MainWindowViewModel.IsSchemaFile(path))
        {
            Message = UserMessage.Error($"Fajl „{fileName}” nije XSD šema ni ZIP paket. Uvoze se samo .xsd i .zip fajlovi.");
            return;
        }

        Message = null;
        SetBusy($"Uvozim „{fileName}”…");
        try
        {
            var result = await _store.ImportAsync(path);
            if (result.Status == SchemaImportStatus.NeedsMainSchemaSelection)
            {
                ClearBusy();
                var choice = await _dialogs.ChooseAsync("Izbor glavne šeme", result.Message, result.MainSchemaCandidates, "Uvezi");
                if (choice is null)
                {
                    Message = UserMessage.Info("Uvoz je otkazan.");
                    return;
                }

                SetBusy($"Uvozim „{fileName}”…");
                result = await _store.ImportAsync(path, new SchemaImportOptions(MainSchemaFile: choice));
            }

            _settings.Update(s => s with { LastFolder = Path.GetDirectoryName(Path.GetFullPath(path)) });
            await HandleImportResultAsync(result);
        }
        finally
        {
            ClearBusy();
        }
    }

    public void Dispose() => _store.Changed -= OnStoreChanged;

    private bool CanImport() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportAsync()
    {
        var path = await _dialogs.PickOpenFileAsync("Uvezi šemu", [FileFilter.SchemaPackage, FileFilter.All], _settings.Current.LastFolder);
        if (path is not null)
        {
            await ImportFromPathAsync(path);
        }
    }

    /// <summary>„Uvezi sa linka…”: korisnik nalijepi adresu ZIP paketa sa sajta FZO.</summary>
    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportFromUrlAsync()
    {
        var url = await _dialogs.PromptTextAsync(
            "Uvezi šemu sa linka",
            "Nalijepite link (adresu) ZIP paketa šema sa sajta FZO. Dozvoljene su samo adrese koje počinju sa https://.",
            _settings.Current.LastSchemaUrl ?? "https://",
            "Preuzmi i uvezi");
        if (!string.IsNullOrWhiteSpace(url))
        {
            await ImportFromUrlAsync(url);
        }
    }

    private bool CanCancelDownload() => IsDownloading;

    [RelayCommand(CanExecute = nameof(CanCancelDownload))]
    private void CancelDownload() => _downloadCancellation?.Cancel();

    private bool CanSetActive() => !IsBusy && SelectedSchema is { IsActive: false };

    [RelayCommand(CanExecute = nameof(CanSetActive))]
    private async Task SetActiveAsync()
    {
        if (SelectedSchema is not { } schema)
        {
            return;
        }

        try
        {
            _store.SetActive(schema.Id);
            await LoadAsync(schema.Id);
            Message = FallbackWarning is null
                ? UserMessage.Success($"Aktivna šema je sada „{schema.Name}”.")
                : UserMessage.Warning($"Šema „{schema.Name}” je izabrana, ali ne može da se koristi.", [FallbackWarning]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log.Warning("Aktivna šema ne može da se promijeni.", ex);
            Message = UserMessage.Error("Aktivna šema ne može da se promijeni.", [ex.Message]);
        }
    }

    private bool CanModify() => !IsBusy && SelectedSchema is { IsBuiltIn: false };

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task DeleteAsync()
    {
        if (SelectedSchema is not { IsBuiltIn: false } schema)
        {
            return;
        }

        var message = $"Obrisati šemu „{schema.Name}” iz programa? Originalni fajl „{schema.Info.SourceFileName}” iz kojeg je uvezena ostaje netaknut."
            + (schema.IsActive ? " Ova šema je aktivna; aktivna postaje ugrađena šema." : string.Empty);
        if (!await _dialogs.ConfirmAsync("Brisanje šeme", message, "Obriši", isDangerous: true))
        {
            return;
        }

        try
        {
            _store.Delete(schema.Id);
            CloseDiff();
            await LoadAsync();
            Message = UserMessage.Success($"Šema „{schema.Name}” je obrisana."
                + (schema.IsActive ? " Aktivna je sada ugrađena šema." : string.Empty));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log.Warning("Šema ne može da se obriše.", ex);
            Message = UserMessage.Error($"Šema „{schema.Name}” ne može da se obriše.", [ex.Message]);
        }
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task RenameAsync()
    {
        if (SelectedSchema is not { IsBuiltIn: false } schema)
        {
            return;
        }

        var name = await _dialogs.PromptTextAsync("Preimenovanje šeme", "Novi naziv šeme (koristi se u izvještajima):", schema.Name, "Preimenuj");
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == schema.Name)
        {
            return;
        }

        try
        {
            var renamed = _store.Rename(schema.Id, name);
            await LoadAsync(schema.Id);
            Message = UserMessage.Success($"Šema je preimenovana u „{renamed.Name}”.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log.Warning("Šema ne može da se preimenuje.", ex);
            Message = UserMessage.Error("Šema ne može da se preimenuje.", [ex.Message]);
        }
    }

    private bool CanShowDiff() => !IsBusy && SelectedSchema is { IsActive: false };

    /// <summary>Razlike izabrane šeme prema aktivnoj.</summary>
    [RelayCommand(CanExecute = nameof(CanShowDiff))]
    private async Task ShowDiffAsync()
    {
        if (SelectedSchema is { } schema)
        {
            SetBusy("Poredim šeme…");
            try
            {
                await ShowDiffForAsync(schema.Info);
            }
            finally
            {
                ClearBusy();
            }
        }
    }

    [RelayCommand]
    private void CloseDiff()
    {
        DiffTitle = null;
        DiffSummary = null;
        DiffSections.Clear();
    }

    [RelayCommand]
    private void DismissMessage() => Message = null;

    private async Task HandleImportResultAsync(SchemaImportResult result)
    {
        var details = new List<string>(result.Errors);
        if (result.IgnoredFiles.Count > 0)
        {
            details.Add("Preskočeni fajlovi (nisu .xsd): " + string.Join(", ", result.IgnoredFiles));
        }

        switch (result.Status)
        {
            case SchemaImportStatus.Imported:
                var schema = result.Schema!;
                await LoadAsync(schema.Id);
                await ShowDiffForAsync(schema);
                ClearBusy();
                var activate = await _dialogs.ConfirmAsync(
                    "Šema je uvezena",
                    $"Šema „{schema.Name}” je uvezena. Da li da postane aktivna? Validacija i provjera rezultata čišćenja tada koriste nju.",
                    "Postavi kao aktivnu",
                    "Ne sada");
                if (activate)
                {
                    _store.SetActive(schema.Id);
                    await LoadAsync(schema.Id);
                    Message = UserMessage.Success($"Šema „{schema.Name}” je uvezena i postavljena kao aktivna.", details);
                }
                else
                {
                    Message = UserMessage.Success($"Šema „{schema.Name}” je uvezena. Aktivna šema nije promijenjena.", details);
                }

                break;

            case SchemaImportStatus.AlreadyExists:
            case SchemaImportStatus.AlreadyExistsBuiltIn:
                await LoadAsync(result.Schema?.Id);
                Message = UserMessage.Info(result.Message, details);
                break;

            default:
                Message = UserMessage.Error(
                    $"Šema nije uvezena iz „{result.Schema?.SourceFileName ?? "izabranog fajla"}”.",
                    [.. new[] { result.Message }.Concat(details).Distinct(StringComparer.Ordinal)]);
                break;
        }
    }

    private async Task ShowDiffForAsync(SchemaInfo schema)
    {
        var (activeName, loaded, diff) = await Task.Run(() =>
        {
            var active = _schemas.GetActiveSchema();
            var loaded = _store.LoadCompiled(schema.Id);
            var diff = loaded.Succeeded ? SchemaComparer.Compare(active.SchemaSet, loaded.SchemaSet!) : null;
            return (active.Name, loaded, diff);
        });

        DiffSections.Clear();
        DiffTitle = $"Razlike: „{schema.Name}” prema aktivnoj „{activeName}”";
        if (diff is null)
        {
            DiffSummary = loaded.Problem ?? "Šema ne može da se učita.";
            return;
        }

        void Section(string title, IReadOnlyList<string> items)
        {
            if (items.Count > 0)
            {
                DiffSections.Add(new SchemaDiffSection(title, items));
            }
        }

        Section("Dodati elementi", diff.AddedElements);
        Section("Uklonjeni elementi", diff.RemovedElements);
        Section("Dodati atributi", diff.AddedAttributes);
        Section("Uklonjeni atributi", diff.RemovedAttributes);
        Section("Promijenjena ograničenja", [.. diff.ChangedRestrictions.Select(FormatRestriction)]);

        var total = DiffSections.Sum(s => s.Count);
        DiffSummary = diff.IsEmpty
            ? "Nema razlika u elementima, atributima ni ograničenjima."
            : $"Ukupno promjena: {total}.";
    }

    internal static string FormatRestriction(SchemaRestrictionChange change) =>
        $"{change.Path}: {change.Restriction} {change.OldValue ?? "(nije postojalo)"} → {change.NewValue ?? "(uklonjeno)"}";

    private void SetBusy(string text)
    {
        BusyText = text;
        IsBusy = true;
    }

    private void ClearBusy()
    {
        IsBusy = false;
        BusyText = string.Empty;
    }

    private void DeleteDownloadFolder(string folder)
    {
        // Briše se samo podfolder koji je napravilo ovo preuzimanje.
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning("Privremeni folder preuzete šeme ne može da se obriše.", ex);
        }
    }

    private void OnStoreChanged(object? sender, EventArgs e) => _ui.Post(() =>
    {
        if (!IsBusy)
        {
            _ = LoadAsync();
        }
    });
}
