namespace XmlValidator.Core.Validation;

/// <summary>Ozbiljnost nalaza.</summary>
public enum ValidationSeverity
{
    Error,
    Warning,
}

/// <summary>Vrsta nalaza.</summary>
public enum ValidationIssueKind
{
    /// <summary>XML nije ispravno formiran (npr. tagovi se ne poklapaju, prazan fajl).</summary>
    WellFormedness,

    /// <summary>Sadržaj ne odgovara XSD šemi.</summary>
    Schema,

    /// <summary>Korijenski element nije u očekivanom namespace-u.</summary>
    Namespace,
}

/// <summary>Jedan nalaz validacije. Linija i kolona počinju od 1; 0 znači da nisu poznate.</summary>
/// <param name="Message">Objašnjenje na srpskom: gdje je problem i šta treba ispraviti.</param>
/// <param name="TechnicalMessage">Originalna poruka validatora (engleski), za napredne korisnike; <c>null</c> kad je nema.</param>
/// <param name="InvoiceOrdinal">Redni broj fakture u fajlu (od 1) na koju se nalaz odnosi; <c>null</c> van faktura (zaglavlje, struktura fajla).</param>
public sealed record ValidationIssue(
    ValidationSeverity Severity,
    int Line,
    int Column,
    string Message,
    ValidationIssueKind Kind,
    string? TechnicalMessage = null,
    int? InvoiceOrdinal = null);

/// <summary>Rezultat validacije.</summary>
/// <param name="Issues">Nalazi, najviše <see cref="MaxIssues"/>.</param>
/// <param name="ErrorCount">Ukupan broj grešaka (i onih koje nisu u listi).</param>
/// <param name="WarningCount">Ukupan broj upozorenja (i onih koja nisu u listi).</param>
/// <param name="MaxIssues">Ograničenje broja nalaza u listi.</param>
/// <param name="SchemaName">Naziv korišćene šeme (za izvještaje).</param>
/// <param name="SchemaIsFallback">Da li je korišćena ugrađena šema jer aktivna nije dostupna.</param>
/// <param name="SchemaFallbackReason">Razlog povratka na ugrađenu šemu.</param>
/// <param name="InvoicesWithErrors">Broj različitih faktura sa bar jednom greškom (i onih van liste).</param>
/// <param name="ErrorsOutsideInvoices">Broj grešaka van faktura (zaglavlje, struktura fajla).</param>
public sealed record ValidationResult(
    IReadOnlyList<ValidationIssue> Issues,
    int ErrorCount,
    int WarningCount,
    int MaxIssues,
    string SchemaName,
    bool SchemaIsFallback,
    string? SchemaFallbackReason,
    int InvoicesWithErrors = 0,
    int ErrorsOutsideInvoices = 0)
{
    /// <summary>Fajl je ispravan kad nema grešaka; upozorenja ne utiču.</summary>
    public bool IsValid => ErrorCount == 0;

    public int TotalIssueCount => ErrorCount + WarningCount;

    /// <summary>Da li ima više nalaza nego što ih je u listi.</summary>
    public bool IsTruncated => TotalIssueCount > Issues.Count;

    /// <summary>Napomena za korisnika kad lista nije potpuna; inače <c>null</c>.</summary>
    public string? TruncationNote => IsTruncated
        ? $"Prikazano je prvih {Issues.Count} od ukupno {TotalIssueCount} nalaza; ostali nisu prikazani."
        : null;

    /// <summary>Kratak sažetak za korisnika.</summary>
    public string Summary => (ErrorCount, WarningCount) switch
    {
        (0, 0) => "Fajl je ispravan.",
        (0, var w) => $"Fajl je ispravan, uz {w} upozorenja.",
        (var e, 0) => $"Pronađeno grešaka: {e}{Where}.",
        (var e, var w) => $"Pronađeno grešaka: {e}{Where}, upozorenja: {w}.",
    };

    /// <summary>Gdje su greške: „ u 3 fakture”, „ u 3 fakture i 1 van faktura”, „ van faktura”.</summary>
    private string Where => (InvoicesWithErrors, ErrorsOutsideInvoices) switch
    {
        (0, 0) => string.Empty,
        (0, _) => " (van faktura)",
        (var i, 0) => $" ({InInvoices(i)})",
        (var i, var o) => $" ({InInvoices(i)} i {o} van faktura)",
    };

    /// <summary>„u 1 fakturi”, „u 2 fakture”, „u 5 faktura”, „u 21 fakturi” (srpska množina).</summary>
    public static string InInvoices(int count)
    {
        var mod10 = count % 10;
        var mod100 = count % 100;
        var word = mod10 == 1 && mod100 != 11 ? "fakturi"
            : mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14 ? "fakture"
            : "faktura";
        return $"u {count} {word}";
    }
}
