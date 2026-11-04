using XmlValidator.Core.Validation;

namespace XmlValidator.Core.Cleaning;

/// <summary>Faza dugog posla, za prikaz napretka.</summary>
public enum CleaningStage
{
    Reading,
    Parsing,
    ReadingInvoices,
    Building,
    Validating,
}

/// <summary>Napredak analize ili pravljenja izlaza.</summary>
/// <param name="Stage">Faza.</param>
/// <param name="Done">Urađeno (npr. pročitanih faktura).</param>
/// <param name="Total">Ukupno; 0 kad nije poznato.</param>
public sealed record CleaningProgress(CleaningStage Stage, int Done, int Total)
{
    public string Description => Stage switch
    {
        CleaningStage.Reading => "Učitavanje fajla…",
        CleaningStage.Parsing => "Pronalaženje faktura…",
        CleaningStage.ReadingInvoices => Total > 0 ? $"Analiza faktura ({Done}/{Total})…" : "Analiza faktura…",
        CleaningStage.Building => "Pravljenje očišćenog fajla…",
        _ => "Provjera rezultata prema šemi…",
    };
}

/// <summary>Izlaz i rezultat njegove provjere (ispravnost XML-a i XSD šema).</summary>
public sealed record CleaningResult(CleaningOutput Output, ValidationResult Validation);

/// <summary>
/// Orkestracija čišćenja za UI: analiza fajla u pozadini i pravljenje izlaza sa provjerom prema
/// aktivnoj šemi. Ulazni fajl se samo čita.
/// </summary>
public sealed class CleaningService
{
    private readonly XmlValidationService _validator;

    public CleaningService(XmlValidationService validator)
    {
        ArgumentNullException.ThrowIfNull(validator);
        _validator = validator;
    }

    /// <summary>Učitava i analizira fajl.</summary>
    /// <exception cref="InvoiceDocumentException">Fajl ne može da se analizira (poruka za korisnika).</exception>
    public async Task<CleaningSession> AnalyzeFileAsync(
        string path,
        CleaningOptions? options = null,
        IProgress<CleaningProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        progress?.Report(new CleaningProgress(CleaningStage.Reading, 0, 0));
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return await AnalyzeAsync(bytes, options, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Analizira sadržaj fajla.</summary>
    /// <exception cref="InvoiceDocumentException">Sadržaj ne može da se analizira (poruka za korisnika).</exception>
    public Task<CleaningSession> AnalyzeAsync(
        byte[] content,
        CleaningOptions? options = null,
        IProgress<CleaningProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        options ??= new CleaningOptions();
        return Task.Run(
            () =>
            {
                progress?.Report(new CleaningProgress(CleaningStage.Parsing, 0, 0));
                var decoded = DocumentTextDecoder.Decode(content);
                var document = InvoiceDocumentParser.Parse(decoded, new InvoiceParseOptions(options.RepairBrackets), cancellationToken);
                var total = document.Blocks.Count;
                var blocksRead = progress is null
                    ? null
                    : new SynchronousProgress<int>(done => progress.Report(new CleaningProgress(CleaningStage.ReadingInvoices, done, total)));
                return CleaningSession.Create(document, options, rule: null, blocksRead, cancellationToken);
            },
            cancellationToken);
    }

    /// <summary>Pravi izlaz i provjerava ga na ispravnost XML-a i prema aktivnoj šemi.</summary>
    /// <exception cref="InvalidOperationException">Neki blok čeka odluku korisnika.</exception>
    public async Task<CleaningResult> BuildAndValidateAsync(
        CleaningSession session,
        IProgress<CleaningProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        progress?.Report(new CleaningProgress(CleaningStage.Building, 0, 0));
        var output = await Task.Run(session.BuildOutput, cancellationToken).ConfigureAwait(false);
        progress?.Report(new CleaningProgress(CleaningStage.Validating, 0, 0));
        using var stream = new MemoryStream(output.Bytes, writable: false);
        var validation = await _validator.ValidateAsync(stream, cancellationToken).ConfigureAwait(false);
        return new CleaningResult(output, validation);
    }

    /// <summary>Prosljeđuje napredak odmah, na niti koja radi posao (bez <see cref="SynchronizationContext"/>).</summary>
    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
