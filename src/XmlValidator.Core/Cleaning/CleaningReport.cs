using System.Globalization;
using System.Text;
using XmlValidator.Core.Validation;

namespace XmlValidator.Core.Cleaning;

/// <summary>Promjena bloka ručnom izmjenom (staro i novo stanje).</summary>
public sealed record ReportEdit(InvoiceData Before, InvoiceCategory BeforeCategory, InvoiceData After, InvoiceCategory AfterCategory);

/// <summary>Jedna faktura (blok) u izvještaju.</summary>
public sealed record ReportEntry(
    int Id,
    int BlockId,
    int PartIndex,
    int PartCount,
    int StartLine,
    int EndLine,
    string? Number,
    string? Date,
    string? Jmb,
    IReadOnlyList<string> Groups,
    int ItemCount,
    InvoiceCategory Category,
    CandidateAction? Action,
    BlockDisposition Disposition,
    string Reason,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<RepairNote> Repairs,
    ReportEdit? Edit);

/// <summary>Strukturisan izvještaj o čišćenju (za prikaz i izvoz u .txt i .csv).</summary>
public sealed record CleaningReport(
    DateTimeOffset CreatedAt,
    string? InputPath,
    string? OutputPath,
    string RuleName,
    bool RepairsEnabled,
    string FormatDescription,
    IReadOnlyList<ReportEntry> Entries,
    IReadOnlyList<UnrecognizedContent> Unrecognized,
    IReadOnlyList<string> DocumentWarnings,
    ValidationResult? OutputValidation)
{
    public int Total => Entries.Count;

    public int KeptByRule => Count(BlockDisposition.Kept);

    public int Deleted => Count(BlockDisposition.Deleted);

    public int Skipped => Count(BlockDisposition.Skipped);

    public int Edited => Count(BlockDisposition.Edited);

    public int Unresolved => Count(BlockDisposition.Unresolved);

    /// <summary>Sve fakture koje ostaju u izlazu (po pravilu, preskočene i uređene).</summary>
    public int Remaining => KeptByRule + Skipped + Edited;

    public int RepairCount => Entries.Sum(e => e.Repairs.Count(r => r.Applied));

    public int WarningCount => Entries.Sum(e => e.Warnings.Count) + Unrecognized.Count + DocumentWarnings.Count;

    /// <summary>Pravi izvještaj iz trenutnog stanja sesije.</summary>
    /// <param name="session">Sesija.</param>
    /// <param name="result">Izlaz i njegova provjera, ako je napravljen.</param>
    /// <param name="inputPath">Ulazni fajl.</param>
    /// <param name="outputPath">Izlazni fajl, ako je sačuvan.</param>
    /// <param name="timeProvider">Izvor vremena (podrazumijevano sistemsko).</param>
    public static CleaningReport Create(
        CleaningSession session,
        CleaningResult? result = null,
        string? inputPath = null,
        string? outputPath = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var blocks = result?.Output.Blocks ?? session.Blocks;
        var entries = blocks.Select(b => new ReportEntry(
            b.Id,
            b.Block.Id,
            b.PartIndex,
            b.PartCount,
            b.Block.StartLine,
            b.Block.EndLine,
            b.Data.Number,
            b.Data.Date,
            b.Data.EffectiveJmb,
            b.Data.Groups,
            b.Data.ItemCount,
            b.Classification.Category,
            b.Action,
            b.Disposition,
            b.Classification.Reason,
            [.. b.Warnings],
            b.IsEdited ? [] : b.Block.Repairs,
            b.IsEdited ? new ReportEdit(b.OriginalData, b.OriginalClassification.Category, b.Data, b.Classification.Category) : null)).ToList();

        return new CleaningReport(
            (timeProvider ?? TimeProvider.System).GetLocalNow(),
            inputPath,
            outputPath,
            session.Rule.Name,
            session.Document.RepairsEnabled,
            session.Document.Format.Description,
            entries,
            [.. session.Document.Unrecognized],
            session.Document.Warnings,
            result?.Validation);
    }

    private int Count(BlockDisposition disposition) => Entries.Count(e => e.Disposition == disposition);
}

/// <summary>Opcije izvoza izvještaja.</summary>
/// <param name="MaskJmb">Maskiranje JMB-a (npr. <c>0101990******</c>); podrazumijevano isključeno.</param>
/// <param name="MaxValidationIssues">Najviše nalaza validacije u .txt izvještaju.</param>
public sealed record ReportExportOptions(bool MaskJmb = false, int MaxValidationIssues = 50);

/// <summary>Srpski nazivi za kategorije, akcije i ishode (izvještaji i UI).</summary>
public static class CleaningText
{
    public static string Category(InvoiceCategory category) => category switch
    {
        InvoiceCategory.KeepGroup => "Ima ciljnu grupu",
        InvoiceCategory.KeepSameJmb => "Isti JMB ima ciljnu grupu",
        InvoiceCategory.EmptyInvoice => "Prazna faktura",
        InvoiceCategory.NoGroupNoJmbMatch => "Bez grupe i bez podudaranja JMB-a",
        _ => "Sumnjiv blok",
    };

    public static string Action(CandidateAction? action) => action switch
    {
        CandidateAction.Delete => "Obriši",
        CandidateAction.Skip => "Preskoči",
        CandidateAction.Edit => "Uredi",
        _ => "—",
    };

    public static string Disposition(BlockDisposition disposition) => disposition switch
    {
        BlockDisposition.Kept => "Ostaje",
        BlockDisposition.Deleted => "Obrisana",
        BlockDisposition.Skipped => "Preskočena",
        BlockDisposition.Edited => "Uređena",
        _ => "Čeka odluku",
    };

    /// <summary>Grupe za prikaz, kao u <c>clean.py</c>: različite vrijednosti, sortirane.</summary>
    public static string Groups(IReadOnlyList<string> groups, int itemCount) =>
        itemCount == 0 && groups.Count == 0 ? "nema stavki"
        : groups.Count == 0 ? "nema usluga-grupa"
        : string.Join(", ", groups.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(g => g.Trim().Length == g.Length ? g : $"„{g}”"));

    /// <summary>Maskira JMB: ostaje prvih 7 znakova (datum rođenja), ostatak su zvjezdice.</summary>
    public static string MaskJmb(string jmb)
    {
        ArgumentNullException.ThrowIfNull(jmb);
        return jmb.Length > 7 ? jmb[..7] + new string('*', jmb.Length - 7) : new string('*', jmb.Length);
    }

    /// <summary>Maskira svaki niz od tačno 13 cifara u slobodnom tekstu (poruke, neprepoznat sadržaj).</summary>
    public static string MaskJmbInText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return DocumentPatterns.Jmb().Replace(text, m => m.Groups["keep"].Value + "******");
    }
}

/// <summary>Izvoz izvještaja u tekst (po uzoru na <c>clean.py</c>) i CSV (jedna linija po fakturi).</summary>
public static class CleaningReportFormatter
{
    private const string Rule = "======================================================================";
    private const string Separator = "----------------------------------------------------------------------";

    public static string ToText(CleaningReport report, ReportExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        options ??= new ReportExportOptions();
        var mask = options.MaskJmb;
        var sb = new StringBuilder();
        void Line(string text = "") => sb.Append(mask ? CleaningText.MaskJmbInText(text) : text).Append("\r\n");
        string Jmb(string? jmb) => jmb is null ? "bez JMB-a" : mask ? CleaningText.MaskJmb(jmb) : jmb;
        string Number(ReportEntry e) => string.IsNullOrEmpty(e.Number) ? "(bez broja)" : e.Number;
        string Head(ReportEntry e) =>
            $"{Number(e)} | datum={e.Date ?? "?"} | JMB={Jmb(e.Jmb)} | grupe={CleaningText.Groups(e.Groups, e.ItemCount)} | linija {e.StartLine}"
            + (e.PartCount > 1 ? $" (dio {e.PartIndex + 1}/{e.PartCount} podijeljenog bloka)" : string.Empty);

        Line(Rule);
        Line("IZVJEŠTAJ O ČIŠĆENJU FAKTURA");
        Line(Rule);
        Line($"Datum izvještaja: {report.CreatedAt.ToString("dd.MM.yyyy. HH:mm", CultureInfo.InvariantCulture)}");
        Line($"Ulazni fajl:  {report.InputPath ?? "(nije naveden)"}");
        Line($"Izlazni fajl: {report.OutputPath ?? "(nije sačuvan)"}");
        Line($"Pravilo:      {report.RuleName}");
        Line($"Format:       {report.FormatDescription}");
        Line($"Automatske popravke: {(report.RepairsEnabled ? "uključene" : "isključene")}");
        if (mask)
        {
            Line("JMB:          maskiran");
        }

        Line(Separator);
        Line($"Ukupno faktura u ulazu: {report.Total}");
        Line($"Ostaje u izlazu: {report.Remaining} (po pravilu {report.KeptByRule}, preskočeno {report.Skipped}, uređeno {report.Edited})");
        Line($"Obrisano: {report.Deleted}");
        if (report.Unresolved > 0)
        {
            Line($"Čeka odluku: {report.Unresolved}");
        }

        Line($"Automatske popravke: {report.RepairCount}");
        Line($"Upozorenja: {report.WarningCount}");
        Line(Separator);

        var repairs = report.Entries.SelectMany(e => e.Repairs.Where(r => r.Applied).Select(r => (Entry: e, Note: r))).ToList();
        if (repairs.Count > 0)
        {
            Line("POPRAVKE:");
            foreach (var (entry, note) in repairs)
            {
                Line($"  - linija {note.Line}: {note.Description} (faktura {Number(entry)})");
            }

            Line(Separator);
        }

        var warnings = report.DocumentWarnings.Select(w => $"  - {w}")
            .Concat(report.Entries.SelectMany(e => e.Warnings.Select(w => $"  - linija {e.StartLine} (faktura {Number(e)}): {w}")))
            .Concat(report.Unrecognized.Select(u =>
                $"  - neprepoznat sadržaj između faktura, {Lines(u.StartLine, u.EndLine)} (sačuvan u izlazu, provjerite ručno): '{u.Preview}'"))
            .ToList();
        if (warnings.Count > 0)
        {
            Line("UPOZORENJA:");
            warnings.ForEach(w => Line(w));
            Line(Separator);
        }

        Section("ČEKAJU ODLUKU", BlockDisposition.Unresolved);
        Section("OBRISANE FAKTURE", BlockDisposition.Deleted, emptyText: "Nijedna faktura nije obrisana.");
        Section("PRESKOČENE FAKTURE (kandidati koji ostaju netaknuti)", BlockDisposition.Skipped);

        var edited = report.Entries.Where(e => e.Disposition == BlockDisposition.Edited).ToList();
        if (edited.Count > 0)
        {
            Line("UREĐENE FAKTURE:");
            foreach (var entry in edited)
            {
                Line($"  • {Head(entry)}");
                if (entry.Edit is { } edit)
                {
                    Line($"      prije:   {State(edit.Before, edit.BeforeCategory)}");
                    Line($"      poslije: {State(edit.After, edit.AfterCategory)}");
                }

                Line($"      razlog: {entry.Reason}");
            }

            Line(Separator);
        }

        Section("FAKTURE KOJE OSTAJU PO PRAVILU", BlockDisposition.Kept);

        if (report.OutputValidation is { } validation)
        {
            Line($"VALIDACIJA IZLAZA (šema: {validation.SchemaName}): {validation.Summary}");
            if (validation.SchemaIsFallback && validation.SchemaFallbackReason is { } reason)
            {
                Line($"  Napomena: {reason}");
            }

            foreach (var issue in validation.Issues.Take(options.MaxValidationIssues))
            {
                var severity = issue.Severity == ValidationSeverity.Error ? "greška" : "upozorenje";
                Line($"  - linija {issue.Line}, kolona {issue.Column} ({severity}): {issue.Message}");
            }

            var hidden = validation.TotalIssueCount - Math.Min(validation.Issues.Count, options.MaxValidationIssues);
            if (hidden > 0)
            {
                Line($"  … i još {hidden} nalaza.");
            }
        }
        else
        {
            Line("VALIDACIJA IZLAZA: izlaz nije napravljen.");
        }

        Line(Rule);
        return sb.ToString();

        void Section(string title, BlockDisposition disposition, string? emptyText = null)
        {
            var entries = report.Entries.Where(e => e.Disposition == disposition).ToList();
            if (entries.Count == 0)
            {
                if (emptyText is not null)
                {
                    Line(emptyText);
                    Line(Separator);
                }

                return;
            }

            Line($"{title}:");
            foreach (var entry in entries)
            {
                Line($"  • {Head(entry)}");
                Line($"      razlog: {entry.Reason}");
            }

            Line(Separator);
        }

        string State(InvoiceData data, InvoiceCategory category) =>
            $"{CleaningText.Category(category)}; broj={data.Number ?? "?"}, datum={data.Date ?? "?"}, JMB={Jmb(data.EffectiveJmb)}, "
            + $"grupe={CleaningText.Groups(data.Groups, data.ItemCount)}, stavki={data.ItemCount}";
    }

    /// <summary>CSV sa tačkom-zarezom (Excel sa srpskim podešavanjima), UTF-8; jedna linija po fakturi.</summary>
    public static string ToCsv(CleaningReport report, ReportExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        options ??= new ReportExportOptions();
        var sb = new StringBuilder();
        Row("Linija", "Broj", "Datum", "JMB", "Grupe", "Br. stavki", "Kategorija", "Akcija", "Ishod", "Razlog", "Upozorenja");
        foreach (var e in report.Entries)
        {
            var jmb = e.Jmb is null ? string.Empty : options.MaskJmb ? CleaningText.MaskJmb(e.Jmb) : e.Jmb;
            var warnings = string.Join(" | ", e.Warnings.Concat(e.Repairs.Where(r => r.Applied).Select(r => $"Popravka (linija {r.Line}): {r.Description}")));
            Row(
                e.StartLine.ToString(CultureInfo.InvariantCulture),
                e.Number ?? string.Empty,
                e.Date ?? string.Empty,
                jmb,
                string.Join(" ", e.Groups.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
                e.ItemCount.ToString(CultureInfo.InvariantCulture),
                CleaningText.Category(e.Category),
                CleaningText.Action(e.Action),
                CleaningText.Disposition(e.Disposition),
                e.Reason,
                options.MaskJmb ? CleaningText.MaskJmbInText(warnings) : warnings);
        }

        return sb.ToString();

        void Row(params string[] values) => sb.Append(string.Join(";", values.Select(Escape))).Append("\r\n");
    }

    /// <summary>Kodiranje za upis CSV-a: UTF-8 sa BOM-om, da ga Excel ispravno otvori.</summary>
    public static Encoding CsvEncoding { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    /// <summary>Kodiranje za upis .txt izvještaja: UTF-8 bez BOM-a.</summary>
    public static Encoding TextEncoding { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static string Lines(int start, int end) => start == end ? $"linija {start}" : $"linije {start}–{end}";

    private static string Escape(string value)
    {
        // Zaštita od formula u tabelama (CSV injection).
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.IndexOfAny([';', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }
}
