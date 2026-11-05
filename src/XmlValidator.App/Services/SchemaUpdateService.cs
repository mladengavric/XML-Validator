using System.Globalization;
using XmlValidator.Core.Schemas;

namespace XmlValidator.App.Services;

/// <summary>Ishod provjere šeme na sajtu FZO.</summary>
public enum SchemaUpdateState
{
    /// <summary>Aktivna šema je ista kao na sajtu FZO (ili je ova verzija već ponuđena pri pokretanju).</summary>
    UpToDate,

    /// <summary>Na sajtu je šema koje nema u programu.</summary>
    NewVersion,

    /// <summary>Šema sa sajta je već uvezena, ali nije aktivna.</summary>
    ImportedNotActive,
}

/// <summary>Rezultat provjere šeme na sajtu FZO.</summary>
/// <param name="State">Ishod.</param>
/// <param name="RemoteModified">„Last modified” fajla na sajtu FZO (ako ga server javlja).</param>
/// <param name="ActiveName">Naziv šeme koja se sada koristi.</param>
/// <param name="ExistingSchema">Već uvezena šema sa istim sadržajem (za <see cref="SchemaUpdateState.ImportedNotActive"/>).</param>
public sealed record SchemaUpdateCheck(SchemaUpdateState State, DateTimeOffset? RemoteModified, string ActiveName, SchemaInfo? ExistingSchema)
{
    private static readonly CultureInfo Serbian = CultureInfo.GetCultureInfo("sr-Latn");

    /// <summary>Datum izmjene za prikaz, npr. „13.02.2026. u 08:47”.</summary>
    public string RemoteModifiedText => RemoteModified is { } at
        ? at.ToLocalTime().ToString("dd.MM.yyyy. 'u' HH:mm", Serbian)
        : "nepoznat datum";

    public bool HasUpdate => State != SchemaUpdateState.UpToDate;

    /// <summary>Datum izmjene bez vremena, npr. „13.02.2026.”.</summary>
    public string RemoteDateText => RemoteModified is { } at ? at.ToLocalTime().ToString("dd.MM.yyyy.", Serbian) : "nepoznat datum";

    /// <summary>Pitanje za korisnika kad ima nove šeme (kratko: staje u karticu pitanja „Da/Ne”).</summary>
    public string OfferText => State == SchemaUpdateState.ImportedNotActive
        ? $"Šema sa sajta FZO (izmijenjena {RemoteDateText}) je već uvezena kao „{ExistingSchema?.Name}”, ali nije aktivna. Da li da postane aktivna?"
        : $"Na sajtu FZO je nova verzija šeme (izmijenjena {RemoteDateText}). Da li da je program preuzme i postavi kao aktivnu?";
}

/// <summary>Ishod nadogradnje šeme.</summary>
/// <param name="Schema">Šema koja je sada aktivna.</param>
/// <param name="Message">Poruka za korisnika.</param>
public sealed record SchemaUpdateResult(SchemaInfo Schema, string Message);

/// <summary>
/// Praćenje šeme faktura na sajtu FZO (<see cref="FzoSchemaSource"/>): vrijeme izmjene („Last modified”)
/// fajla <c>fakture-zdravstva.xsd</c> i poređenje njegovog sadržaja sa šemama u programu. Nadogradnja
/// preuzima fajl, uvozi ga u skladište (isti uvoz kao za fajl sa diska) i postavlja ga kao aktivnu šemu.
/// </summary>
public sealed class SchemaUpdateService
{
    private readonly SchemaDownloader _downloader;
    private readonly SchemaStore _store;
    private readonly ISchemaProvider _schemas;
    private readonly ISettingsService _settings;
    private readonly AppPaths _paths;
    private readonly IAppLog _log;

    public SchemaUpdateService(SchemaDownloader downloader, SchemaStore store, ISchemaProvider schemas, ISettingsService settings, AppPaths paths, IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);
        _downloader = downloader;
        _store = store;
        _schemas = schemas;
        _settings = settings;
        _paths = paths;
        _log = log;
    }

    /// <summary>
    /// Provjerava šemu na sajtu FZO. Pri pokretanju (<paramref name="atStartup"/>) se šema sa istim
    /// „Last modified” kao posljednja provjerena ne preuzima ponovo; ručna provjera uvijek poredi sadržaj.
    /// </summary>
    /// <exception cref="SchemaDownloadException">Sajt nije dostupan ili fajl ne može da se preuzme.</exception>
    public async Task<SchemaUpdateCheck> CheckAsync(bool atStartup, CancellationToken cancellationToken = default)
    {
        var modified = await _downloader.GetLastModifiedAsync(FzoSchemaSource.MainSchemaUrl, cancellationToken).ConfigureAwait(false);
        var active = await Task.Run(_schemas.GetActiveSchema, cancellationToken).ConfigureAwait(false);
        if (atStartup && modified is not null && modified == _settings.Current.SeenFzoSchemaModified)
        {
            return new SchemaUpdateCheck(SchemaUpdateState.UpToDate, modified, active.Name, null);
        }

        var hash = await WithDownloadAsync(async download =>
            SchemaHash.ComputeContentHash(await File.ReadAllBytesAsync(download.FilePath, cancellationToken).ConfigureAwait(false)),
            cancellationToken).ConfigureAwait(false);

        var existing = (await Task.Run(_store.List, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(s => string.Equals(s.ContentSha256, hash, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && existing.Id == active.Info.Id && !active.IsFallback)
        {
            MarkSeen(modified);
            return new SchemaUpdateCheck(SchemaUpdateState.UpToDate, modified, active.Name, existing);
        }

        return new SchemaUpdateCheck(
            existing is null ? SchemaUpdateState.NewVersion : SchemaUpdateState.ImportedNotActive, modified, active.Name, existing);
    }

    /// <summary>Pamti da je ova verzija šeme ponuđena (pri pokretanju se ne nudi ponovo).</summary>
    public void MarkSeen(DateTimeOffset? modified)
    {
        if (modified is not null && _settings.Current.SeenFzoSchemaModified != modified)
        {
            _settings.Update(s => s with { SeenFzoSchemaModified = modified });
        }
    }

    /// <summary>
    /// Nadogradnja: već uvezena šema samo postaje aktivna; inače se šema preuzima sa sajta FZO, uvozi i
    /// postavlja kao aktivna. Pri uvozu se ništa dalje ne preuzima sa mreže.
    /// </summary>
    /// <exception cref="SchemaDownloadException">Preuzimanje ili uvoz nisu uspjeli (poruka je za korisnika).</exception>
    public async Task<SchemaUpdateResult> InstallAsync(SchemaUpdateCheck check, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(check);
        if (check.State == SchemaUpdateState.ImportedNotActive && check.ExistingSchema is { } existing && _store.Find(existing.Id) is not null)
        {
            _store.SetActive(existing.Id);
            MarkSeen(check.RemoteModified);
            return new SchemaUpdateResult(existing, $"Šema „{existing.Name}” je sada aktivna.");
        }

        var name = check.RemoteModified is { } at
            ? $"FZO šema od {at.ToLocalTime().ToString("dd.MM.yyyy.", CultureInfo.InvariantCulture)}"
            : null;
        var result = await WithDownloadAsync(
            download => _store.ImportAsync(download.FilePath, new SchemaImportOptions(Name: name), cancellationToken),
            cancellationToken).ConfigureAwait(false);

        if (result.Status is not (SchemaImportStatus.Imported or SchemaImportStatus.AlreadyExists or SchemaImportStatus.AlreadyExistsBuiltIn)
            || result.Schema is not { } schema)
        {
            _log.Warning($"Šema sa sajta FZO ne može da se uveze: {result.Message} {string.Join(" ", result.Errors)}");
            throw new SchemaDownloadException(
                $"Šema sa sajta FZO ne može da se uveze: {result.Message}" + (result.Errors.Count > 0 ? " " + result.Errors[0] : string.Empty));
        }

        _store.SetActive(schema.Id);
        MarkSeen(check.RemoteModified);
        return new SchemaUpdateResult(schema, result.Status == SchemaImportStatus.Imported
            ? $"Šema „{schema.Name}” je preuzeta sa sajta FZO, uvezena i postavljena kao aktivna."
            : $"Šema sa sajta FZO već postoji kao „{schema.Name}” i sada je aktivna.");
    }

    /// <summary>Preuzima šemu u privremeni podfolder, poziva <paramref name="use"/> i briše podfolder.</summary>
    private async Task<T> WithDownloadAsync<T>(Func<SchemaDownload, Task<T>> use, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(_paths.DownloadsFolder, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            var download = await _downloader.DownloadAsync(FzoSchemaSource.MainSchemaUrl, folder, cancellationToken: cancellationToken).ConfigureAwait(false);
            return await use(download).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SchemaDownloadException("Šema sa sajta FZO ne može da se sačuva u privremeni folder: " + ex.Message, ex);
        }
        finally
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warning("Privremeni folder preuzete šeme ne može da se obriše.", ex);
            }
        }
    }
}
