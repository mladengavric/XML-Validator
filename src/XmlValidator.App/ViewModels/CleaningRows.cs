using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using XmlValidator.App.Services;
using XmlValidator.Core.Cleaning;

namespace XmlValidator.App.ViewModels;

/// <summary>Filter tabele faktura.</summary>
public enum InvoiceFilter
{
    /// <summary>Sve fakture.</summary>
    All,

    /// <summary>Samo kandidati za brisanje (o njima odlučuje akcija).</summary>
    Candidates,

    /// <summary>Fakture sa upozorenjem, popravkom ili koje čekaju odluku.</summary>
    Warnings,
}

/// <summary>Jedan red tabele faktura (snimak stanja iz sesije; osvježava se poslije svake promjene).</summary>
public sealed partial class InvoiceRowViewModel : ObservableObject
{
    private static readonly string[] DerivedProperties =
    [
        nameof(Id), nameof(NumberText), nameof(DateText), nameof(JmbText), nameof(GroupsText), nameof(ItemCount),
        nameof(StartLine), nameof(LineText), nameof(Category), nameof(CategoryText), nameof(Reason), nameof(IsCandidate),
        nameof(IsSuspicious), nameof(IsUnresolved), nameof(IsEdited), nameof(Disposition), nameof(DispositionText),
        nameof(ActionText), nameof(Warnings), nameof(WarningCount), nameof(HasWarnings), nameof(AppliedRepairs),
        nameof(RepairCount), nameof(HasRepairs), nameof(PartText), nameof(HasPart), nameof(StatusSeverity),
        nameof(StatusText), nameof(NeedsAttention), nameof(CanEdit), nameof(Tooltip),
    ];

    private readonly Action<InvoiceRowViewModel, CandidateAction> _actionChosen;
    private bool _updating;

    internal InvoiceRowViewModel(BlockState state, Action<InvoiceRowViewModel, CandidateAction> actionChosen)
    {
        _actionChosen = actionChosen;
        State = state;
        Warnings = [.. state.Warnings];
        _updating = true;
        SelectedAction = FindOption(state.Action);
        _updating = false;
    }

    /// <summary>Akcije za kandidata (ComboBox u koloni „Akcija”).</summary>
    public static IReadOnlyList<Option<CandidateAction>> ActionOptions { get; } =
    [
        new(CandidateAction.Delete, "Obriši"),
        new(CandidateAction.Skip, "Preskoči"),
        new(CandidateAction.Edit, "Uredi"),
    ];

    /// <summary>Trenutno stanje fakture u sesiji.</summary>
    public BlockState State { get; private set; }

    /// <summary>Akcija izabrana u tabeli; izbor korisnika se prosljeđuje sesiji. <c>null</c> za sumnjiv blok bez odluke.</summary>
    [ObservableProperty]
    public partial Option<CandidateAction>? SelectedAction { get; set; }

    public int Id => State.Id;

    public string NumberText => State.Data.Number is { Length: > 0 } number ? number : "(bez broja)";

    public string DateText => State.Data.Date is { Length: > 0 } date ? date : "—";

    public string JmbText => State.Data.EffectiveJmb ?? "bez JMB-a";

    public string GroupsText => CleaningText.Groups(State.Data.Groups, State.Data.ItemCount);

    public int ItemCount => State.Data.ItemCount;

    public int StartLine => State.Block.StartLine;

    public string LineText => State.Block.StartLine == State.Block.EndLine
        ? State.Block.StartLine.ToString(CultureInfo.InvariantCulture)
        : string.Create(CultureInfo.InvariantCulture, $"{State.Block.StartLine}–{State.Block.EndLine}");

    public InvoiceCategory Category => State.Classification.Category;

    public string CategoryText => CleaningText.Category(Category);

    public string Reason => State.Classification.Reason;

    /// <summary>Kandidat za brisanje: samo za njega se bira akcija.</summary>
    public bool IsCandidate => State.Classification.IsCandidate;

    public bool IsSuspicious => Category == InvoiceCategory.SuspiciousBlock;

    /// <summary>Čeka odluku (sumnjiv blok bez akcije ili Uredi bez primijenjene izmjene).</summary>
    public bool IsUnresolved => State.Disposition == BlockDisposition.Unresolved;

    public bool IsEdited => State.IsEdited;

    public BlockDisposition Disposition => State.Disposition;

    /// <summary>Ishod u izlazu: „Ostaje”, „Obrisana”, „Preskočena”, „Uređena”, „Čeka odluku”.</summary>
    public string DispositionText => CleaningText.Disposition(Disposition);

    /// <summary>Tekst kolone „Akcija” za fakture koje nisu kandidati (za njih se akcija ne bira).</summary>
    public string ActionText => IsCandidate ? CleaningText.Action(State.Action) : DispositionText;

    public IReadOnlyList<string> Warnings { get; private set; } = [];

    public int WarningCount => Warnings.Count;

    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>Primijenjene automatske popravke (za uređenu fakturu ih nema: važi ručna izmjena).</summary>
    public IReadOnlyList<RepairNote> AppliedRepairs => State.IsEdited ? [] : [.. State.Block.Repairs.Where(r => r.Applied)];

    public int RepairCount => AppliedRepairs.Count;

    public bool HasRepairs => RepairCount > 0;

    /// <summary>„dio 1/2” za dijelove podijeljenog bloka.</summary>
    public string PartText => State.PartCount > 1
        ? string.Create(CultureInfo.InvariantCulture, $"dio {State.PartIndex + 1}/{State.PartCount}")
        : string.Empty;

    public bool HasPart => State.PartCount > 1;

    /// <summary>Ozbiljnost za ikonicu reda: čeka odluku → greška, upozorenje → upozorenje, popravka → informacija (kao u listi napomena).</summary>
    public MessageSeverity StatusSeverity =>
        IsUnresolved ? MessageSeverity.Error
        : HasWarnings ? MessageSeverity.Warning
        : HasRepairs || Disposition == BlockDisposition.Deleted ? MessageSeverity.Info
        : MessageSeverity.Success;

    /// <summary>Tekst uz ikonicu reda (status se nikad ne prikazuje samo bojom).</summary>
    public string StatusText =>
        IsUnresolved ? "Čeka odluku"
        : HasWarnings ? $"Upozorenja: {WarningCount}"
        : HasRepairs ? $"Popravke: {RepairCount}"
        : DispositionText;

    /// <summary>Red za filter „Samo upozorenja”.</summary>
    public bool NeedsAttention => IsUnresolved || IsSuspicious || HasWarnings || HasRepairs;

    /// <summary>Ručna izmjena je dozvoljena za kandidate i već uređene fakture.</summary>
    public bool CanEdit => IsCandidate || IsEdited;

    /// <summary>Opis za tooltip reda.</summary>
    public string Tooltip => HasWarnings ? string.Join(Environment.NewLine, Warnings) : Reason;

    /// <summary>Pretraga po broju fakture i JMB-u.</summary>
    public bool Matches(string search) =>
        search.Length == 0
        || (State.Data.Number?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
        || (State.Data.EffectiveJmb?.Contains(search, StringComparison.Ordinal) ?? false);

    public bool MatchesFilter(InvoiceFilter filter) => filter switch
    {
        InvoiceFilter.Candidates => IsCandidate,
        InvoiceFilter.Warnings => NeedsAttention,
        _ => true,
    };

    internal void Update(BlockState state)
    {
        State = state;
        Warnings = [.. state.Warnings];
        foreach (var name in DerivedProperties)
        {
            OnPropertyChanged(name);
        }

        _updating = true;
        try
        {
            SelectedAction = FindOption(state.Action);
        }
        finally
        {
            _updating = false;
        }
    }

    partial void OnSelectedActionChanged(Option<CandidateAction>? value)
    {
        if (!_updating && value is not null)
        {
            _actionChosen(this, value.Value);
        }
    }

    private static Option<CandidateAction>? FindOption(CandidateAction? action) =>
        action is null ? null : ActionOptions.First(o => o.Value == action);
}

/// <summary>Vrsta stavke u listi popravki i upozorenja.</summary>
public enum CleaningNoteKind
{
    Repair,
    Warning,
    Unresolved,
    Unrecognized,
    Document,
}

/// <summary>Stavka liste popravki i upozorenja; klik vodi na fakturu (ili na neprepoznat sadržaj).</summary>
/// <param name="Kind">Vrsta.</param>
/// <param name="Line">Linija u ulaznom fajlu (0 = cijeli fajl).</param>
/// <param name="Text">Opis.</param>
/// <param name="RowId">Id fakture u sesiji, ili <c>null</c>.</param>
/// <param name="Unrecognized">Neprepoznat sadržaj, ili <c>null</c>.</param>
public sealed record CleaningNoteRow(CleaningNoteKind Kind, int Line, string Text, int? RowId, UnrecognizedContent? Unrecognized = null)
{
    public string KindText => Kind switch
    {
        CleaningNoteKind.Repair => "Popravka",
        CleaningNoteKind.Warning => "Upozorenje",
        CleaningNoteKind.Unresolved => "Čeka odluku",
        CleaningNoteKind.Unrecognized => "Neprepoznat sadržaj",
        _ => "Fajl",
    };

    public MessageSeverity Severity => Kind switch
    {
        CleaningNoteKind.Repair => MessageSeverity.Info,
        CleaningNoteKind.Unresolved => MessageSeverity.Error,
        _ => MessageSeverity.Warning,
    };

    public string LineText => Line > 0 ? Line.ToString(CultureInfo.InvariantCulture) : "—";

    public bool CanNavigate => RowId is not null || Unrecognized is not null;
}

/// <summary>Zbirni brojevi analize za sažetak.</summary>
public sealed record CleaningSummary(
    int Total,
    int Remaining,
    int Candidates,
    int Deleted,
    int Skipped,
    int Edited,
    int Unresolved,
    int Repairs,
    int Warnings)
{
    public static CleaningSummary Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0);

    public bool HasUnresolved => Unresolved > 0;

    public bool HasWarnings => Warnings > 0;
}
