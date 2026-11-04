using CommunityToolkit.Mvvm.ComponentModel;
using XmlValidator.App.Services;
using XmlValidator.Core.Cleaning;

namespace XmlValidator.App.ViewModels;

/// <summary>Zajednički dio tabova koji rade nad otvorenim fajlom.</summary>
public abstract partial class DocumentTabViewModel : ViewModelBase
{
    /// <summary>Otvoren fajl; <c>null</c> dok fajl nije otvoren (prikazuje se prazno stanje).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDocument))]
    public partial OpenedDocument? Document { get; private set; }

    public bool HasDocument => Document is not null;

    /// <summary>Naslov taba.</summary>
    public abstract string Header { get; }

    /// <summary>Naslov praznog stanja.</summary>
    public string EmptyStateTitle => "Nijedan fajl nije otvoren";

    /// <summary>Sljedeći korak u praznom stanju.</summary>
    public abstract string EmptyStateHint { get; }

    /// <summary>Postavlja otvoren fajl (poziva glavni prozor).</summary>
    public virtual void SetDocument(OpenedDocument? document) => Document = document;

    /// <summary>Sadržaj za „Sačuvaj kao”; u fazi 3 su to originalni bajtovi, a od faze 4 sadržaj editora.</summary>
    public virtual byte[]? GetContentToSave() => Document?.Content;
}

/// <summary>Tab „Validacija” (sadržaj se dodaje u fazi 4).</summary>
public sealed class ValidationTabViewModel : DocumentTabViewModel
{
    public override string Header => "Validacija";

    public override string EmptyStateHint =>
        "Otvorite XML fajl (Ctrl+O) ili ga prevucite na prozor da biste ga provjerili prema aktivnoj šemi.";

    /// <summary>Privremena napomena dok editor i lista grešaka ne postoje.</summary>
    public string PendingFeatureNote => "Prikaz fajla i lista grešaka validacije dolaze u sljedećoj verziji.";
}

/// <summary>Tab „Čišćenje faktura” (sadržaj se dodaje u fazi 5).</summary>
public sealed partial class CleaningTabViewModel : DocumentTabViewModel, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly IUiDispatcher _ui;

    public CleaningTabViewModel(ISettingsService settings, IUiDispatcher ui)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(ui);
        _settings = settings;
        _ui = ui;
        _settings.Changed += OnSettingsChanged;
        SettingsSummary = BuildSummary(settings.Current);
    }

    public override string Header => "Čišćenje faktura";

    public override string EmptyStateHint =>
        "Otvorite XML fajl sa fakturama (Ctrl+O) ili ga prevucite na prozor da biste ga analizirali po pravilu SHMP.";

    /// <summary>Privremena napomena dok analiza ne postoji.</summary>
    public string PendingFeatureNote => "Analiza i čišćenje faktura dolaze u sljedećoj verziji.";

    /// <summary>Podešavanja čišćenja koja će se primijeniti (iz Podešavanja → Opšte).</summary>
    [ObservableProperty]
    public partial string SettingsSummary { get; private set; }

    public void Dispose() => _settings.Changed -= OnSettingsChanged;

    internal static string BuildSummary(AppSettings settings)
    {
        static string Action(CandidateAction action) => action == CandidateAction.Skip ? "Preskoči" : "Obriši";
        return $"Ciljna grupa: {settings.TargetGroup} · Prazna faktura: {Action(settings.EmptyInvoiceAction)} · "
            + $"Bez ciljne grupe i bez podudaranja JMB-a: {Action(settings.NoGroupNoJmbMatchAction)} · "
            + $"Automatske popravke: {(settings.RepairBrackets ? "uključene" : "isključene")}";
    }

    private void OnSettingsChanged(object? sender, EventArgs e) =>
        _ui.Post(() => SettingsSummary = BuildSummary(_settings.Current));
}
