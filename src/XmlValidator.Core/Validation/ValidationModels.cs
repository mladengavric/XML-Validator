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
public sealed record ValidationIssue(
    ValidationSeverity Severity,
    int Line,
    int Column,
    string Message,
    ValidationIssueKind Kind,
    string? TechnicalMessage = null);

/// <summary>Rezultat validacije.</summary>
/// <param name="Issues">Nalazi, najviše <see cref="MaxIssues"/>.</param>
/// <param name="ErrorCount">Ukupan broj grešaka (i onih koje nisu u listi).</param>
/// <param name="WarningCount">Ukupan broj upozorenja (i onih koja nisu u listi).</param>
/// <param name="MaxIssues">Ograničenje broja nalaza u listi.</param>
/// <param name="SchemaName">Naziv korišćene šeme (za izvještaje).</param>
/// <param name="SchemaIsFallback">Da li je korišćena ugrađena šema jer aktivna nije dostupna.</param>
/// <param name="SchemaFallbackReason">Razlog povratka na ugrađenu šemu.</param>
public sealed record ValidationResult(
    IReadOnlyList<ValidationIssue> Issues,
    int ErrorCount,
    int WarningCount,
    int MaxIssues,
    string SchemaName,
    bool SchemaIsFallback,
    string? SchemaFallbackReason)
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
        (var e, 0) => $"Pronađeno grešaka: {e}.",
        (var e, var w) => $"Pronađeno grešaka: {e}, upozorenja: {w}.",
    };
}
