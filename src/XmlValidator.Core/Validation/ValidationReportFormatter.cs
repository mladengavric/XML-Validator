using System.Globalization;
using System.Text;
using XmlValidator.Core.Cleaning;

namespace XmlValidator.Core.Validation;

/// <summary>Podaci o validaciji za izvoz (rezultat i okolnosti: fajl, vrijeme).</summary>
/// <param name="Result">Rezultat validacije.</param>
/// <param name="FilePath">Putanja validiranog fajla; <c>null</c> ako nije poznata.</param>
/// <param name="ValidatedAt">Vrijeme validacije.</param>
/// <param name="ContentModified">Da li je validiran sadržaj izmijenjen u editoru (nije isti kao fajl na disku).</param>
public sealed record ValidationReport(ValidationResult Result, string? FilePath, DateTimeOffset ValidatedAt, bool ContentModified);

/// <summary>Izvoz rezultata validacije u tekst (<c>.txt</c>, clipboard) i CSV.</summary>
public static class ValidationReportFormatter
{
    private const string Rule = "======================================================================";

    /// <summary>Tekst za korisnika, CRLF; isti format za <c>.txt</c> i clipboard.</summary>
    public static string ToText(ValidationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var result = report.Result;
        var sb = new StringBuilder();
        void Line(string text = "") => sb.Append(text).Append("\r\n");

        Line(Rule);
        Line("IZVJEŠTAJ O VALIDACIJI");
        Line(Rule);
        Line($"Datum:  {report.ValidatedAt.ToString("dd.MM.yyyy. HH:mm", CultureInfo.InvariantCulture)}");
        Line($"Fajl:   {report.FilePath ?? "(nije naveden)"}{(report.ContentModified ? " (izmijenjen sadržaj iz editora, nije sačuvan)" : string.Empty)}");
        Line($"Šema:   {SchemaLine(result)}");
        Line($"Ishod:  {result.Summary}");
        if (result.TruncationNote is { } note)
        {
            Line($"Napomena: {note}");
        }

        if (result.Issues.Count > 0)
        {
            Line();
            foreach (var issue in result.Issues)
            {
                Line($"{Severity(issue.Severity)} | {Position(issue)} | {Kind(issue.Kind)} | {issue.Message}");
                if (issue.TechnicalMessage is { } technical && technical != issue.Message)
                {
                    Line($"    tehnički opis: {technical}");
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>CSV sa tačkom-zarezom (Excel sa srpskim podešavanjima), CRLF; prva linija navodi fajl i šemu.</summary>
    public static string ToCsv(ValidationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var result = report.Result;
        var sb = new StringBuilder();
        void Row(params string[] values) => sb.Append(string.Join(";", values.Select(CleaningReportFormatter.Escape))).Append("\r\n");

        Row("Fajl", report.FilePath ?? string.Empty, "Šema", SchemaLine(result), "Ishod", result.Summary);
        Row("Ozbiljnost", "Linija", "Kolona", "Vrsta", "Poruka", "Tehnički opis");
        foreach (var issue in result.Issues)
        {
            Row(
                Severity(issue.Severity),
                issue.Line > 0 ? issue.Line.ToString(CultureInfo.InvariantCulture) : string.Empty,
                issue.Column > 0 ? issue.Column.ToString(CultureInfo.InvariantCulture) : string.Empty,
                Kind(issue.Kind),
                issue.Message,
                issue.TechnicalMessage ?? string.Empty);
        }

        return sb.ToString();
    }

    /// <summary>Kodiranje za <c>.txt</c>: UTF-8 bez BOM-a (kao izvještaj čišćenja).</summary>
    public static Encoding TextEncoding => CleaningReportFormatter.TextEncoding;

    /// <summary>Kodiranje za <c>.csv</c>: UTF-8 sa BOM-om, da ga Excel ispravno otvori.</summary>
    public static Encoding CsvEncoding => CleaningReportFormatter.CsvEncoding;

    /// <summary>Ozbiljnost za prikaz.</summary>
    public static string Severity(ValidationSeverity severity) => severity == ValidationSeverity.Error ? "Greška" : "Upozorenje";

    /// <summary>Vrsta nalaza za prikaz.</summary>
    public static string Kind(ValidationIssueKind kind) => kind switch
    {
        ValidationIssueKind.WellFormedness => "Ispravnost XML-a",
        ValidationIssueKind.Namespace => "Namespace",
        _ => "Šema",
    };

    private static string Position(ValidationIssue issue) => issue switch
    {
        { Line: <= 0 } => "pozicija nepoznata",
        { Column: <= 0 } => $"linija {issue.Line}",
        _ => $"linija {issue.Line}, kolona {issue.Column}",
    };

    private static string SchemaLine(ValidationResult result) =>
        result.SchemaIsFallback
            ? $"{result.SchemaName} (rezervna; aktivna šema nije dostupna: {result.SchemaFallbackReason})"
            : result.SchemaName;
}
