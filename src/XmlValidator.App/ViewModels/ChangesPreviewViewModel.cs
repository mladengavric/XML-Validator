using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XmlValidator.App.Services;
using XmlValidator.Core.Cleaning;
using XmlValidator.Core.Validation;

namespace XmlValidator.App.ViewModels;

/// <summary>Filter liste u pregledu izmjena.</summary>
public enum ChangeFilter
{
    All,
    Deleted,
    Edited,
    Repaired,
}

/// <summary>Jedna izmjena (blok iz ulaza) u pregledu prije čuvanja.</summary>
public sealed class ChangePreviewRow
{
    public ChangePreviewRow(CleaningChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Change = change;
    }

    public CleaningChange Change { get; }

    public CleaningChangeKind Kind => Change.Kind;

    public string KindText => Change.Kind switch
    {
        CleaningChangeKind.Deleted => "Briše se",
        CleaningChangeKind.Edited => "Ručno izmijenjena",
        _ => "Automatski popravljena",
    };

    /// <summary>Ozbiljnost za ikonicu i boju oznake (uz tekst, nikad samo boja).</summary>
    public MessageSeverity Severity => Change.Kind switch
    {
        CleaningChangeKind.Deleted => MessageSeverity.Error,
        CleaningChangeKind.Edited => MessageSeverity.Warning,
        _ => MessageSeverity.Info,
    };

    public bool IsDeleted => Change.Kind == CleaningChangeKind.Deleted;

    public string LinesText => Change.StartLine == Change.EndLine
        ? Change.StartLine.ToString(CultureInfo.InvariantCulture)
        : $"{Change.StartLine}–{Change.EndLine}";

    public string Number => Change.Number ?? "—";

    public string Date => Change.Date ?? "—";

    public string Jmb => Change.Jmb ?? "—";

    public string Description => Change.Description;

    public string OldText => Change.OldText;

    /// <summary>Tekst u izlazu; za obrisan blok objašnjenje umjesto teksta.</summary>
    public string NewText => Change.NewText ?? "(blok se ne nalazi u očišćenom fajlu)";

    /// <summary>Opis reda za čitače ekrana.</summary>
    public string AccessibleText => $"{KindText}: faktura {Number}, linija {LinesText}. {Description}";
}

/// <summary>
/// Pregled razlika između ulaza i izlaza (blokovi koji se brišu, mijenjaju ručno ili automatski popravljaju).
/// Prikazuje se prije čuvanja očišćenog fajla, kao potvrda („Sačuvaj” / „Otkaži”); režim samo za prikaz
/// („Zatvori”) se više ne koristi u tabu, ali ga prozor i dalje podržava.
/// </summary>
public sealed partial class ChangesPreviewViewModel : ViewModelBase
{
    private readonly IReadOnlyList<ChangePreviewRow> _allRows;

    /// <param name="changes">Razlike.</param>
    /// <param name="fileName">Ime ulaznog fajla.</param>
    /// <param name="validation">Provjera izlaza prema šemi, ako je urađena.</param>
    /// <param name="isConfirmation">Pregled prije čuvanja (traži potvrdu).</param>
    public ChangesPreviewViewModel(CleaningChangeSet changes, string fileName, ValidationResult? validation, bool isConfirmation)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(fileName);
        Changes = changes;
        FileName = fileName;
        IsConfirmation = isConfirmation;
        _allRows = [.. changes.Changes.Select(c => new ChangePreviewRow(c))];

        if (validation is not null)
        {
            (ValidationText, ValidationSeverity) = CleaningTabViewModel.DescribeCheck(validation);
            IsOutputValid = validation.IsValid;
        }

        ApplyFilter();
    }

    public CleaningChangeSet Changes { get; }

    public string FileName { get; }

    public bool IsConfirmation { get; }

    public string Title => IsConfirmation ? "Pregled izmjena prije čuvanja" : "Pregled izmjena";

    public string Summary =>
        $"Ulaz „{FileName}”: {CleaningTabViewModel.Invoices(Changes.TotalBlocks)}. "
        + $"Briše se {Changes.DeletedCount}, ručno izmijenjeno {Changes.EditedCount}, automatski popravljeno {Changes.RepairedCount}. "
        + $"U očišćenom fajlu ostaje {CleaningTabViewModel.Invoices(Changes.RemainingInvoices)}; ostale fakture su bajt po bajt iste kao u ulazu.";

    public string DeletedCountText => $"Briše se ({Changes.DeletedCount})";

    public string EditedCountText => $"Izmijenjene ({Changes.EditedCount})";

    public string RepairedCountText => $"Popravljene ({Changes.RepairedCount})";

    public string AllCountText => $"Sve ({Changes.Changes.Count})";

    public bool IsEmpty => Changes.IsEmpty;

    public string EmptyText => "Nema izmjena: očišćen fajl je isti kao ulaz.";

    /// <summary>Opis provjere izlaza prema šemi; <c>null</c> ako provjera nije urađena.</summary>
    public string? ValidationText { get; }

    public MessageSeverity ValidationSeverity { get; } = MessageSeverity.Info;

    public bool HasValidation => ValidationText is not null;

    public bool IsOutputValid { get; } = true;

    public string ConfirmText => !IsConfirmation ? "Zatvori" : IsOutputValid ? "Sačuvaj" : "Sačuvaj svejedno";

    /// <summary>Tekst dugmeta za odustajanje; <c>null</c> kad je pregled samo prikaz.</summary>
    public string? CancelText => IsConfirmation ? "Otkaži" : null;

    public bool HasCancel => CancelText is not null;

    /// <summary>Potvrda kad izlaz nije ispravan prema šemi: dugme se ističe kao opasno.</summary>
    public bool IsDangerous => IsConfirmation && !IsOutputValid;

    public ObservableCollection<ChangePreviewRow> Rows { get; } = [];

    [ObservableProperty]
    public partial ChangeFilter Filter { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial ChangePreviewRow? SelectedRow { get; set; }

    public bool HasSelection => SelectedRow is not null;

    /// <summary>Ishod: <c>true</c> potvrđeno, <c>false</c> odustao (ili zatvoren prozor).</summary>
    public bool? Result { get; private set; }

    public event EventHandler<bool>? CloseRequested;

    [RelayCommand]
    private void Confirm() => Close(true);

    [RelayCommand]
    private void Cancel() => Close(false);

    partial void OnFilterChanged(ChangeFilter value) => ApplyFilter();

    private void ApplyFilter()
    {
        var selected = SelectedRow;
        Rows.Clear();
        foreach (var row in _allRows.Where(r => Filter switch
        {
            ChangeFilter.Deleted => r.Kind == CleaningChangeKind.Deleted,
            ChangeFilter.Edited => r.Kind == CleaningChangeKind.Edited,
            ChangeFilter.Repaired => r.Kind == CleaningChangeKind.Repaired,
            _ => true,
        }))
        {
            Rows.Add(row);
        }

        SelectedRow = selected is not null && Rows.Contains(selected) ? selected : Rows.FirstOrDefault();
    }

    private void Close(bool result)
    {
        // Pregled bez potvrde nikad ne pokreće čuvanje.
        Result = result && IsConfirmation;
        CloseRequested?.Invoke(this, Result.Value);
    }
}
