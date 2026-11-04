using System.Globalization;
using System.Reflection;
using System.Text;
using XmlValidator.Core.Cleaning;
using XmlValidator.Core.Schemas;
using XmlValidator.Core.Validation;

namespace XmlValidator.Cli;

/// <summary>Izlazni kodovi konzolnog alata.</summary>
public static class ExitCodes
{
    /// <summary>Uspjeh: fajl je ispravan, odnosno očišćen fajl je sačuvan.</summary>
    public const int Success = 0;

    /// <summary>Posao je urađen, ali rezultat nije dobar: fajl ima greške, ili izlaz nije sačuvan jer traži odluku.</summary>
    public const int Problems = 1;

    /// <summary>Pogrešni argumenti (nepoznata komanda ili opcija, nedostaje fajl, izlaz postoji bez <c>--overwrite</c>…).</summary>
    public const int Usage = 2;

    /// <summary>Fajl ne može da se pročita, analizira ili upiše.</summary>
    public const int Failure = 3;

    /// <summary>Prekinuto (Ctrl+C).</summary>
    public const int Cancelled = 130;
}

/// <summary>
/// Konzolni alat: <c>validate</c> i <c>clean</c> nad istim Core-om kao aplikacija. Važe ista pravila:
/// ulazni fajl se nikad ne mijenja, postojeći fajl se prepisuje samo uz izričito <c>--overwrite</c>,
/// sumnjiv blok traži izričitu odluku (<c>--suspicious</c>), a izlaz sa greškama šeme se čuva samo uz
/// <c>--allow-invalid</c>.
/// </summary>
public sealed class CliApplication
{
    internal const string ToolName = "xmlvalidator-cli";

    private const int MaxPrintedIssues = 50;

    private static readonly OptionSpec[] GlobalOptions = [new("help", false), new("h", false), new("version", false)];

    private static readonly OptionSpec[] ValidateOptions =
    [
        new("report", true), new("overwrite", false), new("builtin-schema", false), new("help", false), new("h", false),
    ];

    private static readonly OptionSpec[] CleanOptions =
    [
        new("group", true), new("exam", true), new("empty", true), new("nomatch", true), new("suspicious", true), new("no-repairs", false),
        new("report", true), new("mask-jmb", false), new("overwrite", false), new("allow-invalid", false),
        new("builtin-schema", false), new("help", false), new("h", false),
    ];

    private readonly TextWriter _out;
    private readonly TextWriter _err;
    private readonly string _schemaStoreRoot;
    private readonly TimeProvider _time;

    /// <param name="output">Standardni izlaz (rezultati).</param>
    /// <param name="error">Izlaz za greške i upozorenja.</param>
    /// <param name="schemaStoreRoot">Korijen skladišta šema (isti kao u aplikaciji: <see cref="SchemaStore.GetDefaultRootPath"/>).</param>
    /// <param name="timeProvider">Izvor vremena za izvještaje.</param>
    public CliApplication(TextWriter output, TextWriter error, string schemaStoreRoot, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaStoreRoot);
        _out = output;
        _err = error;
        _schemaStoreRoot = schemaStoreRoot;
        _time = timeProvider ?? TimeProvider.System;
    }

    internal static string Version
    {
        get
        {
            var informational = typeof(CliApplication).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus > 0 ? informational[..plus] : informational;
        }
    }

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        try
        {
            if (args.Count == 0)
            {
                await _err.WriteAsync(GeneralHelp).ConfigureAwait(false);
                return ExitCodes.Usage;
            }

            var command = args[0];
            var rest = args.Skip(1);
            switch (command)
            {
                case "validate":
                    return await ValidateAsync(rest, cancellationToken).ConfigureAwait(false);
                case "clean":
                    return await CleanAsync(rest, cancellationToken).ConfigureAwait(false);
                case "help":
                    return await HelpAsync(rest).ConfigureAwait(false);
            }

            var global = CliArguments.Parse(args, GlobalOptions, out var error);
            if (global is null || global.Positionals.Count > 0)
            {
                return await UsageErrorAsync(error ?? $"Nepoznata komanda „{command}”.", GeneralHelp).ConfigureAwait(false);
            }

            if (global.Has("version"))
            {
                await _out.WriteLineAsync($"{ToolName} {Version}").ConfigureAwait(false);
                return ExitCodes.Success;
            }

            await _out.WriteAsync(GeneralHelp).ConfigureAwait(false);
            return ExitCodes.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await _err.WriteLineAsync("Prekinuto.").ConfigureAwait(false);
            return ExitCodes.Cancelled;
        }
    }

    // ------------------------------------------------------------------ validate

    private async Task<int> ValidateAsync(IEnumerable<string> rawArgs, CancellationToken cancellationToken)
    {
        var args = CliArguments.Parse(rawArgs, ValidateOptions, out var error);
        if (args is null)
        {
            return await UsageErrorAsync(error!, ValidateHelp).ConfigureAwait(false);
        }

        if (args.Has("help") || args.Has("h"))
        {
            await _out.WriteAsync(ValidateHelp).ConfigureAwait(false);
            return ExitCodes.Success;
        }

        if (args.Positionals.Count != 1)
        {
            return await UsageErrorAsync(
                args.Positionals.Count == 0 ? "Nije naveden fajl za validaciju." : "Komanda validate prima tačno jedan fajl.",
                ValidateHelp).ConfigureAwait(false);
        }

        var input = Path.GetFullPath(args.Positionals[0]);
        var overwrite = args.Has("overwrite");
        var reportPath = args.Value("report") is { } r ? Path.GetFullPath(r) : null;
        if (await CheckInputAsync(input).ConfigureAwait(false) is { } inputProblem)
        {
            return inputProblem;
        }

        if (reportPath is not null && await CheckTargetAsync(reportPath, input, overwrite, "Izvještaj").ConfigureAwait(false) is { } reportProblem)
        {
            return reportProblem;
        }

        var validator = new XmlValidationService(CreateSchemaProvider(args.Has("builtin-schema")));
        ValidationResult result;
        try
        {
            result = await validator.ValidateFileAsync(input, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return await FailureAsync($"Fajl „{input}” ne može da se pročita: {ex.Message}").ConfigureAwait(false);
        }

        await WriteSchemaLineAsync(result).ConfigureAwait(false);
        await _out.WriteLineAsync($"Fajl:  {input}").ConfigureAwait(false);
        await WriteIssuesAsync(input, result, int.MaxValue).ConfigureAwait(false);
        await _out.WriteLineAsync(result.Summary).ConfigureAwait(false);

        if (reportPath is not null)
        {
            var report = new ValidationReport(result, input, _time.GetLocalNow(), ContentModified: false);
            var (text, encoding) = IsCsv(reportPath)
                ? (ValidationReportFormatter.ToCsv(report), ValidationReportFormatter.CsvEncoding)
                : (ValidationReportFormatter.ToText(report), ValidationReportFormatter.TextEncoding);
            if (await WriteFileAsync(Encode(text, encoding), reportPath, input, overwrite, cancellationToken).ConfigureAwait(false) is { } writeProblem)
            {
                return writeProblem;
            }

            await _out.WriteLineAsync($"Izvještaj: {reportPath}").ConfigureAwait(false);
        }

        return result.IsValid ? ExitCodes.Success : ExitCodes.Problems;
    }

    // ------------------------------------------------------------------ clean

    private async Task<int> CleanAsync(IEnumerable<string> rawArgs, CancellationToken cancellationToken)
    {
        var args = CliArguments.Parse(rawArgs, CleanOptions, out var error);
        if (args is null)
        {
            return await UsageErrorAsync(error!, CleanHelp).ConfigureAwait(false);
        }

        if (args.Has("help") || args.Has("h"))
        {
            await _out.WriteAsync(CleanHelp).ConfigureAwait(false);
            return ExitCodes.Success;
        }

        if (args.Positionals.Count is 0 or > 2)
        {
            return await UsageErrorAsync(
                args.Positionals.Count == 0 ? "Nije naveden ulazni fajl." : "Komanda clean prima ulazni i (opciono) izlazni fajl.",
                CleanHelp).ConfigureAwait(false);
        }

        if (!TryParseOptions(args, out var options, out var suspiciousAction, out error))
        {
            return await UsageErrorAsync(error!, CleanHelp).ConfigureAwait(false);
        }

        var input = Path.GetFullPath(args.Positionals[0]);
        var output = Path.GetFullPath(args.Positionals.Count > 1 ? args.Positionals[1] : CleaningOutputWriter.GetDefaultOutputPath(input));
        var reportPath = args.Value("report") is { } r ? Path.GetFullPath(r) : null;
        var overwrite = args.Has("overwrite");
        var exportOptions = new ReportExportOptions(MaskJmb: args.Has("mask-jmb"));

        if (await CheckInputAsync(input).ConfigureAwait(false) is { } inputProblem)
        {
            return inputProblem;
        }

        if (await CheckTargetAsync(output, input, overwrite, "Izlaz").ConfigureAwait(false) is { } outputProblem)
        {
            return outputProblem;
        }

        if (reportPath is not null)
        {
            if (SamePath(reportPath, output))
            {
                return await UsageErrorAsync("Izvještaj i očišćeni fajl ne mogu biti isti fajl.", null).ConfigureAwait(false);
            }

            if (await CheckTargetAsync(reportPath, input, overwrite, "Izvještaj").ConfigureAwait(false) is { } reportProblem)
            {
                return reportProblem;
            }
        }

        var service = new CleaningService(new XmlValidationService(CreateSchemaProvider(args.Has("builtin-schema"))));
        CleaningSession session;
        try
        {
            session = await service.AnalyzeFileAsync(input, options, progress: null, cancellationToken).ConfigureAwait(false);
        }
        catch (InvoiceDocumentException ex)
        {
            return await FailureAsync($"Fajl „{input}” ne može da se analizira: {ex.Message}").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return await FailureAsync($"Fajl „{input}” ne može da se pročita: {ex.Message}").ConfigureAwait(false);
        }

        await _out.WriteLineAsync($"Ulaz:  {input}").ConfigureAwait(false);
        await _out.WriteLineAsync($"Pravilo: {session.Rule.Name}; automatske popravke {(options.RepairBrackets ? "uključene" : "isključene")}").ConfigureAwait(false);

        if (suspiciousAction is { } action)
        {
            var suspicious = session.Blocks.Where(b => b.Classification.Category == InvoiceCategory.SuspiciousBlock).Select(b => b.Id).ToList();
            session.SetActionForMany(suspicious, action);
        }

        if (!session.CanBuildOutput)
        {
            await _err.WriteLineAsync("Izlaz nije napravljen: sljedeći blokovi traže odluku.").ConfigureAwait(false);
            foreach (var block in session.UnresolvedBlocks)
            {
                await _err.WriteLineAsync($"  {Describe(block)}: {CleaningText.Category(block.Classification.Category)}: {block.Classification.Reason}").ConfigureAwait(false);
            }

            await _err.WriteLineAsync(
                "Odluku dajte opcijama --suspicious, --empty ili --nomatch (delete|skip), ili fakture riješite u aplikaciji (akcija Uredi).").ConfigureAwait(false);
            var pending = CleaningReport.Create(session, result: null, input, outputPath: null, _time);
            await WriteSummaryAsync(pending).ConfigureAwait(false);
            return await WriteCleaningReportAsync(pending, reportPath, input, overwrite, exportOptions, cancellationToken).ConfigureAwait(false)
                ?? ExitCodes.Problems;
        }

        var result = await service.BuildAndValidateAsync(session, progress: null, cancellationToken).ConfigureAwait(false);
        await WriteSchemaLineAsync(result.Validation).ConfigureAwait(false);
        var save = result.Validation.IsValid || args.Has("allow-invalid");
        if (!result.Validation.IsValid)
        {
            await _err.WriteLineAsync($"Provjera očišćenog fajla: {result.Validation.Summary}").ConfigureAwait(false);
            await WriteIssuesAsync("(izlaz)", result.Validation, MaxPrintedIssues, _err).ConfigureAwait(false);
            await _err.WriteLineAsync(save
                ? "Očišćeni fajl se čuva uprkos greškama (--allow-invalid). Greške mogu poticati iz ulaza."
                : "Očišćeni fajl nije sačuvan. Greške mogu poticati iz ulaza; provjerite ulaz komandom validate, a za čuvanje uprkos greškama dodajte --allow-invalid.").ConfigureAwait(false);
        }

        string? savedOutput = null;
        if (save)
        {
            if (await WriteFileAsync(result.Output.Bytes, output, input, overwrite, cancellationToken).ConfigureAwait(false) is { } writeProblem)
            {
                return writeProblem;
            }

            savedOutput = output;
        }

        var report = CleaningReport.Create(session, result, input, savedOutput, _time);
        await WriteSummaryAsync(report).ConfigureAwait(false);
        if (savedOutput is not null)
        {
            await _out.WriteLineAsync($"Očišćeni fajl: {savedOutput}").ConfigureAwait(false);
        }

        return await WriteCleaningReportAsync(report, reportPath, input, overwrite, exportOptions, cancellationToken).ConfigureAwait(false)
            ?? (savedOutput is null ? ExitCodes.Problems : ExitCodes.Success);
    }

    private static bool TryParseOptions(CliArguments args, out CleaningOptions options, out CandidateAction? suspicious, out string? error)
    {
        options = new CleaningOptions();
        suspicious = null;
        error = null;
        var group = options.TargetGroup;
        if (args.Value("group") is { } groupText
            && !ushort.TryParse(groupText, NumberStyles.None, CultureInfo.InvariantCulture, out group))
        {
            error = $"Neispravna grupa SHMP „{groupText}”: očekuje se cijeli broj od 0 do 65535.";
            return false;
        }

        var exam = options.ExamService;
        switch (args.Value("exam"))
        {
            case null:
                break;
            case "any":
                exam = null;
                break;
            case var examText when ushort.TryParse(examText, NumberStyles.None, CultureInfo.InvariantCulture, out var code):
                exam = code;
                break;
            case var examText:
                error = $"Neispravna usluga pregleda „{examText}”: očekuje se cijeli broj od 0 do 65535 ili any.";
                return false;
        }

        if (!TryParseAction(args, "empty", options.EmptyInvoiceAction, allowAsk: true, out var empty, out error)
            || !TryParseAction(args, "nomatch", options.NoGroupNoJmbMatchAction, allowAsk: true, out var noMatch, out error))
        {
            return false;
        }

        if (args.Has("suspicious"))
        {
            if (!TryParseAction(args, "suspicious", CandidateAction.Skip, allowAsk: false, out var s, out error))
            {
                return false;
            }

            suspicious = s;
        }

        options = new CleaningOptions(group, RepairBrackets: !args.Has("no-repairs"), empty, noMatch, exam);
        return true;
    }

    /// <summary><c>delete</c>, <c>skip</c> ili (gdje je dozvoljeno) <c>ask</c>: bez podrazumijevane akcije, faktura čeka odluku.</summary>
    private static bool TryParseAction(CliArguments args, string name, CandidateAction? fallback, bool allowAsk, out CandidateAction? action, out string? error)
    {
        error = null;
        action = fallback;
        switch (args.Value(name))
        {
            case null:
                return true;
            case "delete":
                action = CandidateAction.Delete;
                return true;
            case "skip":
                action = CandidateAction.Skip;
                return true;
            case "ask" when allowAsk:
                action = null;
                return true;
            case var other:
                error = $"Neispravna vrijednost „{other}” za --{name}: dozvoljeno je delete{(allowAsk ? ", skip ili ask" : " ili skip")}.";
                return false;
        }
    }

    private async Task WriteSummaryAsync(CleaningReport report)
    {
        foreach (var warning in report.DocumentWarnings)
        {
            await _err.WriteLineAsync($"Upozorenje: {warning}").ConfigureAwait(false);
        }

        // Ništa se ne briše tiho: svaka obrisana, preskočena ili popravljena faktura i svako upozorenje se navodi.
        foreach (var entry in report.Entries)
        {
            var head = $"{EntryName(entry)} (linija {entry.StartLine})";
            switch (entry.Disposition)
            {
                case BlockDisposition.Deleted:
                    await _out.WriteLineAsync($"Obrisana:   {head}: {CleaningText.Category(entry.Category)}").ConfigureAwait(false);
                    break;
                case BlockDisposition.Skipped:
                    await _out.WriteLineAsync($"Preskočena: {head}: {CleaningText.Category(entry.Category)}").ConfigureAwait(false);
                    break;
            }

            foreach (var repair in entry.Repairs.Where(r => r.Applied))
            {
                await _out.WriteLineAsync($"Popravka:   {EntryName(entry)} (linija {repair.Line}): {repair.Description}").ConfigureAwait(false);
            }

            foreach (var warning in entry.Warnings)
            {
                await _err.WriteLineAsync($"Upozorenje: {head}: {warning}").ConfigureAwait(false);
            }
        }

        foreach (var unrecognized in report.Unrecognized)
        {
            await _err.WriteLineAsync($"Upozorenje: neprepoznat sadržaj na liniji {unrecognized.StartLine} (sačuvan u izlazu)").ConfigureAwait(false);
        }

        await _out.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"Ukupno faktura: {report.Total}; ostaje: {report.Remaining} (po pravilu {report.KeptByRule}, preskočeno {report.Skipped}); obrisano: {report.Deleted}; čeka odluku: {report.Unresolved}; popravki: {report.RepairCount}; upozorenja: {report.WarningCount}")).ConfigureAwait(false);
    }

    private async Task<int?> WriteCleaningReportAsync(
        CleaningReport report, string? reportPath, string input, bool overwrite, ReportExportOptions options, CancellationToken cancellationToken)
    {
        if (reportPath is null)
        {
            return null;
        }

        var (text, encoding) = IsCsv(reportPath)
            ? (CleaningReportFormatter.ToCsv(report, options), CleaningReportFormatter.CsvEncoding)
            : (CleaningReportFormatter.ToText(report, options), CleaningReportFormatter.TextEncoding);
        if (await WriteFileAsync(Encode(text, encoding), reportPath, input, overwrite, cancellationToken).ConfigureAwait(false) is { } problem)
        {
            return problem;
        }

        await _out.WriteLineAsync($"Izvještaj: {reportPath}").ConfigureAwait(false);
        return null;
    }

    private static string EntryName(ReportEntry entry) =>
        (string.IsNullOrEmpty(entry.Number) ? "faktura bez broja" : $"faktura {entry.Number}")
        + (entry.PartCount > 1 ? string.Create(CultureInfo.InvariantCulture, $" (dio {entry.PartIndex + 1}/{entry.PartCount})") : string.Empty);

    private static string Describe(BlockState block) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{(string.IsNullOrEmpty(block.Data.Number) ? "faktura bez broja" : $"faktura {block.Data.Number}")} (linije {block.Block.StartLine}–{block.Block.EndLine})");

    // ------------------------------------------------------------------ zajedničko

    private ISchemaProvider CreateSchemaProvider(bool builtIn) =>
        builtIn ? new BuiltInSchemaProvider() : new StoreSchemaProvider(new SchemaStore(_schemaStoreRoot));

    private async Task WriteSchemaLineAsync(ValidationResult result)
    {
        await _out.WriteLineAsync($"Šema:  {result.SchemaName}").ConfigureAwait(false);
        if (result.SchemaIsFallback)
        {
            await _err.WriteLineAsync($"Upozorenje: aktivna šema nije dostupna, koristi se ugrađena. {result.SchemaFallbackReason}").ConfigureAwait(false);
        }
    }

    private Task WriteIssuesAsync(string file, ValidationResult result, int max) => WriteIssuesAsync(file, result, max, _out);

    private static async Task WriteIssuesAsync(string file, ValidationResult result, int max, TextWriter writer)
    {
        // Oblik „fajl(linija,kolona): ozbiljnost: poruka” prepoznaju editori i CI alati.
        foreach (var issue in result.Issues.Take(max))
        {
            var position = issue.Line > 0 ? string.Create(CultureInfo.InvariantCulture, $"({issue.Line},{Math.Max(issue.Column, 1)})") : string.Empty;
            await writer.WriteLineAsync(
                $"{file}{position}: {ValidationReportFormatter.Severity(issue.Severity).ToLowerInvariant()} ({ValidationReportFormatter.Kind(issue.Kind)}): {issue.Message}").ConfigureAwait(false);
        }

        var shown = Math.Min(max, result.Issues.Count);
        if (shown < result.TotalIssueCount)
        {
            await writer.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"… prikazano {shown} od {result.TotalIssueCount} nalaza.")).ConfigureAwait(false);
        }
    }

    private async Task<int?> CheckInputAsync(string input)
    {
        if (Directory.Exists(input))
        {
            return await UsageErrorAsync($"„{input}” je folder, a ne fajl.", null).ConfigureAwait(false);
        }

        return File.Exists(input) ? null : await FailureAsync($"Fajl „{input}” ne postoji.").ConfigureAwait(false);
    }

    /// <summary>Provjera ciljnog fajla prije posla: nikad ulaz, postojeći fajl samo uz <c>--overwrite</c>.</summary>
    private async Task<int?> CheckTargetAsync(string target, string input, bool overwrite, string what)
    {
        if (SamePath(target, input))
        {
            return await UsageErrorAsync($"{what} ne može biti isti fajl kao ulaz: ulazni fajl se nikad ne mijenja.", null).ConfigureAwait(false);
        }

        if (Directory.Exists(target))
        {
            return await UsageErrorAsync($"{what} „{target}” je folder.", null).ConfigureAwait(false);
        }

        if (!overwrite && File.Exists(target))
        {
            return await UsageErrorAsync($"Fajl „{target}” već postoji. Za prepisivanje dodajte --overwrite.", null).ConfigureAwait(false);
        }

        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            return await FailureAsync($"Folder „{directory}” ne postoji.").ConfigureAwait(false);
        }

        return null;
    }

    private async Task<int?> WriteFileAsync(byte[] bytes, string path, string input, bool overwrite, CancellationToken cancellationToken)
    {
        try
        {
            await CleaningOutputWriter.WriteAsync(bytes, path, input, overwrite, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return await FailureAsync($"Fajl „{path}” ne može da se upiše: {ex.Message}").ConfigureAwait(false);
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a),
            Path.GetFullPath(b),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsCsv(string path) => string.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase);

    private static byte[] Encode(string text, Encoding encoding) => [.. encoding.GetPreamble(), .. encoding.GetBytes(text)];

    private async Task<int> UsageErrorAsync(string message, string? help)
    {
        await _err.WriteLineAsync($"Greška: {message}").ConfigureAwait(false);
        await _err.WriteLineAsync(help is null ? $"Pomoć: {ToolName} --help" : $"Pomoć: {ToolName} {HelpCommandFor(help)}--help").ConfigureAwait(false);
        return ExitCodes.Usage;
    }

    private async Task<int> FailureAsync(string message)
    {
        await _err.WriteLineAsync($"Greška: {message}").ConfigureAwait(false);
        return ExitCodes.Failure;
    }

    private async Task<int> HelpAsync(IEnumerable<string> rest)
    {
        var topic = rest.FirstOrDefault();
        var text = topic switch
        {
            null => GeneralHelp,
            "validate" => ValidateHelp,
            "clean" => CleanHelp,
            _ => null,
        };
        if (text is null)
        {
            return await UsageErrorAsync($"Nepoznata komanda „{topic}”.", GeneralHelp).ConfigureAwait(false);
        }

        await _out.WriteAsync(text).ConfigureAwait(false);
        return ExitCodes.Success;
    }

    private static string HelpCommandFor(string help) =>
        ReferenceEquals(help, ValidateHelp) ? "validate " : ReferenceEquals(help, CleanHelp) ? "clean " : string.Empty;

    // ------------------------------------------------------------------ pomoć

    internal static readonly string GeneralHelp = $"""
        {ToolName} {Version}: validacija i čišćenje IZIS XML faktura zdravstva.

        Upotreba:
          {ToolName} validate <fajl> [opcije]
          {ToolName} clean <ulaz> [izlaz] [opcije]
          {ToolName} help <komanda>
          {ToolName} --version

        Ulazni fajl se nikad ne mijenja. Postojeći fajl se prepisuje samo uz --overwrite.
        Šema: aktivna šema iz aplikacije (Podešavanja → Šeme); --builtin-schema koristi ugrađenu.

        Izlazni kodovi:
          0  uspjeh (fajl je ispravan / očišćeni fajl je sačuvan)
          1  fajl ima greške, ili očišćeni fajl nije sačuvan (čeka odluku ili ne prolazi provjeru)
          2  pogrešni argumenti
          3  fajl ne može da se pročita, analizira ili upiše
        """ + Environment.NewLine;

    internal static readonly string ValidateHelp = $"""
        {ToolName} validate <fajl> [opcije]

        Provjerava fajl prema aktivnoj XSD šemi i ispisuje sve nalaze u obliku
        „fajl(linija,kolona): greška (vrsta): poruka”.

        Opcije:
          --report <fajl>     izvještaj u .txt ili .csv (po ekstenziji), sa nazivom šeme
          --builtin-schema    koristi ugrađenu šemu umjesto aktivne
          --overwrite         prepiši postojeći izvještaj

        Izlazni kod: 0 = ispravan (upozorenja ne utiču), 1 = ima grešaka, 2 = argumenti, 3 = fajl.
        """ + Environment.NewLine;

    internal static readonly string CleanHelp = $"""
        {ToolName} clean <ulaz> [izlaz] [opcije]

        Čisti fakture po pravilu SHMP (isto kao tab „Čišćenje faktura”). Izlaz je podrazumijevano
        <ime>_ociscen.xml pored ulaza. Svaka obrisana, preskočena i popravljena faktura se ispisuje.

        Opcije:
          --group <broj>              grupa usluga SHMP (usluga-grupa, podrazumijevano 100)
          --exam <broj>|any           usluga pregleda u SHMP (usluga-oznaka, podrazumijevano 183);
                                      any = bilo koja usluga SHMP (kao clean.py)
          --empty delete|skip|ask     akcija za praznu fakturu (podrazumijevano delete)
          --nomatch delete|skip|ask   akcija za fakturu samo iz drugih službi čiji pacijent (JMB)
                                      nema pregled u SHMP (podrazumijevano delete)
                                      ask = bez akcije: fakture čekaju odluku, izlaz se ne pravi
          --suspicious delete|skip    odluka za sumnjive blokove; bez nje se izlaz ne pravi
          --no-repairs                bez automatskih popravki zagrada
          --report <fajl>             izvještaj u .txt ili .csv (po ekstenziji)
          --mask-jmb                  maskiraj JMB u izvještaju (npr. 0101990******)
          --allow-invalid             sačuvaj izlaz i kad ne prolazi provjeru prema šemi
          --builtin-schema            provjera izlaza ugrađenom šemom umjesto aktivnom
          --overwrite                 prepiši postojeći izlaz i izvještaj (ulaz nikad)

        Faktura sa uslugom SHMP uvijek ostaje; faktura samo iz drugih službi ostaje ako isti pacijent
        (JMB) ima pregled u SHMP. Rezultat clean.py daju opcije --exam any (uz podrazumijevane akcije).
        Izlazni kod: 0 = sačuvan, 1 = nije sačuvan (čeka odluku ili ne prolazi provjeru),
        2 = argumenti, 3 = fajl.
        """ + Environment.NewLine;
}
