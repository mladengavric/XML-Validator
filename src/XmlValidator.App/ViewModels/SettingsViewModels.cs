using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XmlValidator.App.Services;
using XmlValidator.Core.Cleaning;

namespace XmlValidator.App.ViewModels;

/// <summary>Prozor „Podešavanja” sa stranicama „Opšte” i „Šeme”.</summary>
public sealed partial class SettingsViewModel : ViewModelBase, IDisposable
{
    public SettingsViewModel(GeneralSettingsViewModel general, SchemasSettingsViewModel schemas)
    {
        ArgumentNullException.ThrowIfNull(general);
        ArgumentNullException.ThrowIfNull(schemas);
        General = general;
        Schemas = schemas;
    }

    public GeneralSettingsViewModel General { get; }

    public SchemasSettingsViewModel Schemas { get; }

    public string Title => "Podešavanja";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedPageIndex), nameof(IsGeneralPage), nameof(IsSchemasPage))]
    public partial SettingsPage SelectedPage { get; set; }

    /// <summary>Indeks stranice za listu/tabove u prikazu (0 = Opšte, 1 = Šeme).</summary>
    public int SelectedPageIndex
    {
        get => (int)SelectedPage;
        set => SelectedPage = Enum.IsDefined((SettingsPage)value) ? (SettingsPage)value : SettingsPage.General;
    }

    public bool IsGeneralPage => SelectedPage == SettingsPage.General;

    public bool IsSchemasPage => SelectedPage == SettingsPage.Schemas;

    /// <summary>Poziva se pri otvaranju prozora: bira stranicu, učitava šeme i po potrebi pokreće uvoz.</summary>
    public async Task ActivateAsync(SettingsPage page, string? importSchemaPath = null)
    {
        SelectedPage = page;
        await Schemas.LoadAsync();
        if (!string.IsNullOrWhiteSpace(importSchemaPath))
        {
            SelectedPage = SettingsPage.Schemas;
            await Schemas.ImportFromPathAsync(importSchemaPath);
        }
    }

    public void Dispose() => Schemas.Dispose();
}

/// <summary>Stranica „Opšte”: tema i podešavanja čišćenja. Izmjene se čuvaju odmah.</summary>
public sealed partial class GeneralSettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private readonly IThemeService _theme;
    private bool _loading;

    public GeneralSettingsViewModel(ISettingsService settings, IThemeService theme, AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(paths);
        _settings = settings;
        _theme = theme;
        DataFolder = paths.Root;
        Load(settings.Current);
    }

    public IReadOnlyList<Option<ThemePreference>> ThemeOptions { get; } =
    [
        new(ThemePreference.System, "Prema sistemu"),
        new(ThemePreference.Light, "Svijetla"),
        new(ThemePreference.Dark, "Tamna"),
    ];

    /// <summary>Akcije koje mogu biti podrazumijevane (Uredi traži ručnu izmjenu, pa nije ponuđena).</summary>
    public IReadOnlyList<Option<CandidateAction>> ActionOptions { get; } =
    [
        new(CandidateAction.Delete, "Obriši"),
        new(CandidateAction.Skip, "Preskoči"),
    ];

    [ObservableProperty]
    public partial Option<ThemePreference> SelectedTheme { get; set; }

    /// <summary>Ciljna grupa (0–65535, <c>xs:unsignedShort</c>); <c>decimal?</c> zbog NumericUpDown kontrole.</summary>
    [ObservableProperty]
    public partial decimal? TargetGroup { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTargetGroupError))]
    public partial string? TargetGroupError { get; private set; }

    public bool HasTargetGroupError => TargetGroupError is not null;

    [ObservableProperty]
    public partial Option<CandidateAction> EmptyInvoiceAction { get; set; }

    [ObservableProperty]
    public partial Option<CandidateAction> NoGroupNoJmbMatchAction { get; set; }

    [ObservableProperty]
    public partial bool RepairBrackets { get; set; }

    /// <summary>Folder sa podešavanjima, šemama i logovima (za informaciju).</summary>
    public string DataFolder { get; }

    /// <summary>Problem pri čuvanju podešavanja, ako postoji.</summary>
    public string? SaveProblem => _settings.SaveProblem;

    /// <summary>Vraća podešavanja čišćenja na podrazumijevana (kao <c>clean.py</c>: grupa 100, Obriši, popravke uključene).</summary>
    [RelayCommand]
    private void ResetCleaningDefaults()
    {
        var defaults = new AppSettings();
        Save(s => s with
        {
            TargetGroup = defaults.TargetGroup,
            RepairBrackets = defaults.RepairBrackets,
            EmptyInvoiceAction = defaults.EmptyInvoiceAction,
            NoGroupNoJmbMatchAction = defaults.NoGroupNoJmbMatchAction,
        });
        Load(_settings.Current);
    }

    partial void OnSelectedThemeChanged(Option<ThemePreference> value)
    {
        if (_loading || value is null)
        {
            return;
        }

        _theme.Apply(value.Value);
        Save(s => s with { Theme = value.Value });
    }

    partial void OnTargetGroupChanged(decimal? value)
    {
        if (_loading)
        {
            return;
        }

        if (value is null || value < ushort.MinValue || value > ushort.MaxValue || decimal.Truncate(value.Value) != value)
        {
            TargetGroupError = "Ciljna grupa mora biti cijeli broj od 0 do 65535.";
            return;
        }

        TargetGroupError = null;
        Save(s => s with { TargetGroup = (ushort)value.Value });
    }

    partial void OnEmptyInvoiceActionChanged(Option<CandidateAction> value)
    {
        if (!_loading && value is not null)
        {
            Save(s => s with { EmptyInvoiceAction = value.Value });
        }
    }

    partial void OnNoGroupNoJmbMatchActionChanged(Option<CandidateAction> value)
    {
        if (!_loading && value is not null)
        {
            Save(s => s with { NoGroupNoJmbMatchAction = value.Value });
        }
    }

    partial void OnRepairBracketsChanged(bool value)
    {
        if (!_loading)
        {
            Save(s => s with { RepairBrackets = value });
        }
    }

    private void Save(Func<AppSettings, AppSettings> change)
    {
        _settings.Update(change);
        OnPropertyChanged(nameof(SaveProblem));
    }

    private void Load(AppSettings settings)
    {
        _loading = true;
        try
        {
            SelectedTheme = ThemeOptions.First(o => o.Value == settings.Theme);
            TargetGroup = settings.TargetGroup;
            TargetGroupError = null;
            EmptyInvoiceAction = ActionOptions.First(o => o.Value == settings.EmptyInvoiceAction);
            NoGroupNoJmbMatchAction = ActionOptions.First(o => o.Value == settings.NoGroupNoJmbMatchAction);
            RepairBrackets = settings.RepairBrackets;
        }
        finally
        {
            _loading = false;
        }
    }
}
