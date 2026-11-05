using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace XmlValidator.Core.Updates;

/// <summary>Greška pri provjeri ili preuzimanju nove verzije; poruka je za korisnika.</summary>
public sealed class UpdateException : Exception
{
    public UpdateException()
    {
    }

    public UpdateException(string message)
        : base(message)
    {
    }

    public UpdateException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Operativni sistem za koji se traži izvršni fajl u release-u.</summary>
public enum UpdatePlatform
{
    Windows,

    Linux,
}

/// <summary>Fajl priložen uz release.</summary>
/// <param name="Name">Ime fajla (npr. <c>XmlValidator.exe</c>).</param>
/// <param name="Size">Veličina u bajtovima.</param>
/// <param name="DownloadUrl">Adresa za preuzimanje.</param>
/// <param name="Sha256">SHA-256 iz GitHub-a (heks, mala slova), ako ga GitHub javlja.</param>
public sealed record ReleaseAsset(string Name, long Size, Uri DownloadUrl, string? Sha256);

/// <summary>Objavljena verzija programa (GitHub release).</summary>
/// <param name="Version">Verzija iz oznake (taga), npr. 1.4.0.</param>
/// <param name="TagName">Oznaka, npr. <c>v1.4.0</c>.</param>
/// <param name="Title">Naslov release-a.</param>
/// <param name="Notes">Opis release-a (Markdown, kako je napisan na GitHub-u).</param>
/// <param name="PageUrl">Stranica release-a na GitHub-u.</param>
/// <param name="PublishedAt">Vrijeme objave.</param>
/// <param name="Assets">Priloženi fajlovi.</param>
public sealed record ReleaseInfo(
    Version Version,
    string TagName,
    string Title,
    string Notes,
    Uri PageUrl,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<ReleaseAsset> Assets)
{
    /// <summary>Jednakost po vrijednosti, uključujući listu fajlova.</summary>
    public bool Equals(ReleaseInfo? other) =>
        other is not null
        && Version == other.Version
        && TagName == other.TagName
        && Title == other.Title
        && Notes == other.Notes
        && PageUrl == other.PageUrl
        && PublishedAt == other.PublishedAt
        && Assets.SequenceEqual(other.Assets);

    public override int GetHashCode() => HashCode.Combine(Version, TagName, PageUrl);
}

/// <summary>Napredak preuzimanja: preuzeto bajtova i ukupno (ako je poznato).</summary>
public sealed record UpdateDownloadProgress(long Received, long? Total);

/// <summary>
/// Provjera nove verzije programa preko GitHub release-a (javni REST API, bez prijave) i preuzimanje
/// izvršnog fajla za Windows ili Linux. Preuzet fajl se provjerava po veličini i, kad ga GitHub javlja,
/// po SHA-256.
/// </summary>
public sealed class GitHubReleaseClient : IDisposable
{
    /// <summary>Vlasnik repozitorijuma na GitHub-u.</summary>
    public const string Owner = "mladengavric";

    /// <summary>Repozitorijum sa release-ima.</summary>
    public const string Repository = "XML-Validator";

    /// <summary>Ime izvršnog fajla za Windows u release-u.</summary>
    public const string WindowsAssetName = "XmlValidator.exe";

    /// <summary>Ime izvršnog fajla za Linux (unutar <c>.tar.gz</c> arhive iz release-a).</summary>
    public const string LinuxExecutableName = "XmlValidator";

    /// <summary>Najveći dozvoljeni fajl iz release-a.</summary>
    public const long MaxAssetBytes = 600L * 1024 * 1024;

    /// <summary>Stranica sa svim verzijama.</summary>
    public static readonly Uri ReleasesPage = new($"https://github.com/{Owner}/{Repository}/releases");

    private static readonly Uri LatestReleaseApi = new($"https://api.github.com/repos/{Owner}/{Repository}/releases/latest");
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);

    private readonly HttpClient _client;

    /// <param name="handler">HTTP handler (za testove); podrazumijevano <see cref="SocketsHttpHandler"/>.</param>
    /// <param name="productVersion">Verzija programa za zaglavlje User-Agent (GitHub ga traži).</param>
    public GitHubReleaseClient(HttpMessageHandler? handler = null, string? productVersion = null)
    {
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
        };

        // Vrijeme se ograničava po zahtjevu (provjera kratko, preuzimanje duže).
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("XmlValidator", string.IsNullOrWhiteSpace(productVersion) ? "1.0" : productVersion));
    }

    /// <summary>Verzija iz oznake release-a (<c>v1.4.0</c>, <c>1.4</c>…), svedena na tri dijela; <c>null</c> ako nije verzija.</summary>
    public static Version? ParseVersion(string? text)
    {
        var value = text?.Trim() ?? string.Empty;
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        var end = value.IndexOfAny(['-', '+', ' ']);
        if (end >= 0)
        {
            value = value[..end];
        }

        if (!Version.TryParse(value.Contains('.', StringComparison.Ordinal) ? value : value + ".0", out var version))
        {
            return null;
        }

        return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
    }

    /// <summary>Fajl iz release-a za zadati sistem: <c>XmlValidator.exe</c> ili <c>XmlValidator-*-linux-x64.tar.gz</c>.</summary>
    public static ReleaseAsset? SelectAsset(ReleaseInfo release, UpdatePlatform platform)
    {
        ArgumentNullException.ThrowIfNull(release);
        return platform == UpdatePlatform.Windows
            ? release.Assets.FirstOrDefault(a => a.Name.Equals(WindowsAssetName, StringComparison.OrdinalIgnoreCase))
            : release.Assets.FirstOrDefault(a =>
                a.Name.StartsWith("XmlValidator-", StringComparison.OrdinalIgnoreCase)
                && a.Name.EndsWith("-linux-x64.tar.gz", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Posljednja objavljena verzija (bez nacrta i probnih verzija).</summary>
    /// <exception cref="UpdateException">Nema objavljene verzije, repozitorijum nije javan, greška mreže ili servera.</exception>
    public async Task<ReleaseInfo> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ApiTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new UpdateException(
                    "Na GitHub-u nije pronađena nijedna objavljena verzija. Program može da provjeri nove verzije samo "
                    + "ako je repozitorijum sa release-ima javan (public).");
            }

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                throw new UpdateException("GitHub je privremeno ograničio broj provjera sa ove adrese. Pokušajte ponovo za sat vremena.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateException($"GitHub je vratio grešku {(int)response.StatusCode} ({response.ReasonPhrase}).");
            }

            var json = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            return ParseRelease(json);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new UpdateException("GitHub ne odgovara (isteklo je vrijeme čekanja). Pokušajte ponovo.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException($"Provjera nove verzije nije uspjela: {ex.Message} Provjerite internet vezu.", ex);
        }
    }

    /// <summary>
    /// Preuzima fajl iz release-a u <paramref name="targetPath"/> (postojeći fajl se prepisuje). Provjerava
    /// veličinu i SHA-256; ako provjera ne prođe, fajl se briše.
    /// </summary>
    /// <exception cref="UpdateException">Greška mreže ili servera, prevelik ili oštećen fajl.</exception>
    /// <exception cref="OperationCanceledException">Korisnik je prekinuo preuzimanje.</exception>
    public async Task DownloadAssetAsync(
        ReleaseAsset asset,
        string targetPath,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        if (asset.DownloadUrl.Scheme != Uri.UriSchemeHttps)
        {
            throw new UpdateException("Adresa nove verzije nije bezbjedna (nije https); preuzimanje je odbijeno.");
        }

        if (asset.Size > MaxAssetBytes)
        {
            throw new UpdateException($"Fajl „{asset.Name}” je prevelik ({asset.Size / (1024 * 1024)} MB).");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DownloadTimeout);
        var completed = false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var finalUri = response.RequestMessage?.RequestUri ?? asset.DownloadUrl;
            if (finalUri.Scheme != Uri.UriSchemeHttps)
            {
                throw new UpdateException("GitHub je preusmjerio preuzimanje na nebezbjednu adresu (http); preuzimanje je odbijeno.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateException($"Preuzimanje nije uspjelo: server je vratio grešku {(int)response.StatusCode} ({response.ReasonPhrase}).");
            }

            var total = response.Content.Headers.ContentLength ?? (asset.Size > 0 ? asset.Size : null);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            await using (var output = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            await using (var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    received += read;
                    if (received > MaxAssetBytes)
                    {
                        throw new UpdateException($"Fajl „{asset.Name}” je prevelik.");
                    }

                    sha.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                    progress?.Report(new UpdateDownloadProgress(received, total));
                }
            }

            if (asset.Size > 0 && received != asset.Size)
            {
                throw new UpdateException($"Preuzet fajl nije potpun ({received} od {asset.Size} bajtova). Pokušajte ponovo.");
            }

            var hash = Convert.ToHexStringLower(sha.GetHashAndReset());
            if (asset.Sha256 is { } expected && !string.Equals(expected, hash, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateException("Preuzet fajl je oštećen (SHA-256 se ne poklapa sa onim na GitHub-u). Pokušajte ponovo.");
            }

            completed = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new UpdateException("Preuzimanje je trajalo predugo. Pokušajte ponovo.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException($"Preuzimanje nije uspjelo: {ex.Message} Provjerite internet vezu.", ex);
        }
        finally
        {
            if (!completed)
            {
                TryDelete(targetPath);
            }
        }
    }

    /// <summary>Izdvaja izvršni fajl <see cref="LinuxExecutableName"/> iz <c>.tar.gz</c> arhive release-a.</summary>
    /// <exception cref="UpdateException">Arhiva je oštećena ili u njoj nema programa.</exception>
    public static async Task ExtractLinuxExecutableAsync(string archivePath, string targetPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        try
        {
            await using var file = File.OpenRead(archivePath);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            await using var reader = new TarReader(gzip);
            while (await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false) is { } entry)
            {
                if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile
                    && entry.DataStream is not null
                    && string.Equals(Path.GetFileName(entry.Name.TrimEnd('/')), LinuxExecutableName, StringComparison.Ordinal))
                {
                    await using var output = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    await entry.DataStream.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or EndOfStreamException)
        {
            throw new UpdateException("Preuzeta arhiva je oštećena.", ex);
        }

        throw new UpdateException($"U preuzetoj arhivi nema programa „{LinuxExecutableName}”.");
    }

    public void Dispose() => _client.Dispose();

    /// <summary>Čita odgovor GitHub API-ja (<c>releases/latest</c>).</summary>
    internal static ReleaseInfo ParseRelease(byte[] json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
            var version = ParseVersion(tag) ?? throw new UpdateException($"Oznaka posljednje verzije na GitHub-u („{tag}”) nije broj verzije.");
            var title = String(root, "name") is { Length: > 0 } name ? name : tag;
            var page = Uri.TryCreate(String(root, "html_url"), UriKind.Absolute, out var html) ? html : ReleasesPage;
            DateTimeOffset? published = root.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String && p.TryGetDateTimeOffset(out var at) ? at : null;

            var assets = new List<ReleaseAsset>();
            if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    var assetName = String(item, "name");
                    if (string.IsNullOrEmpty(assetName) || !Uri.TryCreate(String(item, "browser_download_url"), UriKind.Absolute, out var url))
                    {
                        continue;
                    }

                    var size = item.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;
                    var digest = String(item, "digest");
                    var sha = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null;
                    assets.Add(new ReleaseAsset(assetName, size, url, sha));
                }
            }

            return new ReleaseInfo(version, tag, title, String(root, "body") ?? string.Empty, page, published, assets);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new UpdateException("Odgovor GitHub-a nije u očekivanom obliku.", ex);
        }
    }

    private static string? String(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nepotpun fajl ostaje; sljedeće preuzimanje ga prepisuje.
        }
    }
}
