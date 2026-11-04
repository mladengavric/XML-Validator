using System.Net;
using System.Net.Http.Headers;

namespace XmlValidator.Core.Schemas;

/// <summary>Greška pri preuzimanju šeme; poruka je za korisnika.</summary>
public sealed class SchemaDownloadException : Exception
{
    public SchemaDownloadException()
    {
    }

    public SchemaDownloadException(string message)
        : base(message)
    {
    }

    public SchemaDownloadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Preuzet fajl šeme.</summary>
/// <param name="FilePath">Putanja preuzetog fajla (u folderu koji je zadao pozivalac).</param>
/// <param name="FileName">Ime fajla (iz adrese; postaje „izvorni fajl” u metapodacima šeme).</param>
/// <param name="Size">Veličina u bajtovima.</param>
/// <param name="FinalUri">Adresa sa koje je fajl stvarno preuzet (poslije preusmjeravanja).</param>
public sealed record SchemaDownload(string FilePath, string FileName, long Size, Uri FinalUri);

/// <summary>Napredak preuzimanja: preuzeto bajtova i ukupno (ako server javi veličinu).</summary>
public sealed record SchemaDownloadProgress(long Received, long? Total);

/// <summary>
/// Preuzima paket šeme (<c>.zip</c> ili <c>.xsd</c>) sa adrese koju je unio korisnik, da bi se uvezao kao
/// fajl sa diska (<see cref="SchemaStore.ImportAsync"/>). Dozvoljen je samo <c>https</c>, veličina je
/// ograničena, a preusmjeravanje na <c>http</c> se odbija. Pri uvozu se dalje ništa ne preuzima
/// (<c>xs:include</c>/<c>xs:import</c> se razrješavaju samo unutar paketa).
/// </summary>
public sealed class SchemaDownloader : IDisposable
{
    /// <summary>Podrazumijevano ograničenje veličine: kao ukupna veličina raspakovanog paketa.</summary>
    public static readonly long DefaultMaxBytes = SchemaPackageLimits.Default.MaxTotalBytes;

    /// <summary>Podrazumijevano vrijeme čekanja na cijelo preuzimanje.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _client;
    private readonly long _maxBytes;

    /// <param name="handler">HTTP handler (za testove); podrazumijevano <see cref="SocketsHttpHandler"/> sa najviše 5 preusmjeravanja.</param>
    /// <param name="maxBytes">Najveća dozvoljena veličina fajla.</param>
    /// <param name="timeout">Najduže trajanje preuzimanja.</param>
    public SchemaDownloader(HttpMessageHandler? handler = null, long? maxBytes = null, TimeSpan? timeout = null)
    {
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
        };
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = timeout ?? DefaultTimeout };
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("XmlValidator", "1.0"));
        _maxBytes = maxBytes ?? DefaultMaxBytes;
        if (_maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "Ograničenje mora biti pozitivno.");
        }
    }

    /// <summary>Najveća dozvoljena veličina fajla.</summary>
    public long MaxBytes => _maxBytes;

    /// <summary>
    /// Provjerava adresu koju je unio korisnik: apsolutna, <c>https</c>, sa imenom hosta, bez korisničkog
    /// imena i lozinke. Vodeći i prateći razmaci se uklanjaju.
    /// </summary>
    /// <returns><c>null</c> ako je adresa ispravna, inače razlog za korisnika.</returns>
    public static string? ValidateUrl(string? text, out Uri? uri)
    {
        uri = null;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return "Unesite adresu (link) ZIP paketa.";
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed) || parsed.IsFile || parsed.IsUnc)
        {
            return $"„{trimmed}” nije ispravna internet adresa.";
        }

        if (parsed.Scheme != Uri.UriSchemeHttps)
        {
            return "Dozvoljene su samo adrese koje počinju sa https:// (bezbjedna veza).";
        }

        if (string.IsNullOrEmpty(parsed.Host))
        {
            return "Adresa nema ime servera.";
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            return "Adresa ne smije sadržati korisničko ime ni lozinku.";
        }

        uri = parsed;
        return null;
    }

    /// <summary>
    /// Ime fajla iz adrese (posljednji dio putanje), očišćeno od znakova koji nisu dozvoljeni u imenu fajla.
    /// Ako ime nema ekstenziju <c>.zip</c> ni <c>.xsd</c>, ekstenzija se određuje po sadržaju.
    /// </summary>
    internal static string FileNameFor(Uri uri, ReadOnlySpan<byte> head)
    {
        var name = Uri.UnescapeDataString(uri.AbsolutePath.TrimEnd('/'));
        name = name[(name.LastIndexOf('/') + 1)..];
        var invalid = Path.GetInvalidFileNameChars();
        name = new string([.. name.Select(c => Array.IndexOf(invalid, c) >= 0 || char.IsControl(c) ? '_' : c)]).Trim().Trim('.');
        if (name.Length > 100)
        {
            name = name[^100..];
        }

        var extension = Path.GetExtension(name);
        if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) || extension.Equals(".xsd", StringComparison.OrdinalIgnoreCase))
        {
            return name;
        }

        var baseName = name.Length == 0 ? "preuzeta-sema" : name;
        var isZip = head.Length >= 4 && head[0] == (byte)'P' && head[1] == (byte)'K' && head[2] == 3 && head[3] == 4;
        return baseName + (isZip ? ".zip" : ".xsd");
    }

    /// <summary>Preuzima fajl u zadati (postojeći) folder.</summary>
    /// <exception cref="SchemaDownloadException">Neispravna adresa, greška servera ili mreže, prevelik fajl, istek vremena.</exception>
    /// <exception cref="OperationCanceledException">Korisnik je prekinuo preuzimanje.</exception>
    public async Task<SchemaDownload> DownloadAsync(
        string url,
        string targetFolder,
        IProgress<SchemaDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFolder);
        if (ValidateUrl(url, out var uri) is { } problem)
        {
            throw new SchemaDownloadException(problem);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            var finalUri = response.RequestMessage?.RequestUri ?? uri!;
            if (finalUri.Scheme != Uri.UriSchemeHttps)
            {
                throw new SchemaDownloadException("Server je preusmjerio preuzimanje na nebezbjednu adresu (http); preuzimanje je odbijeno.");
            }

            if ((int)response.StatusCode is >= 300 and < 400)
            {
                throw new SchemaDownloadException($"Server je preusmjerio preuzimanje ({(int)response.StatusCode}) na adresu koja nije dozvoljena.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new SchemaDownloadException(response.StatusCode == HttpStatusCode.NotFound
                    ? "Fajl nije pronađen na toj adresi (404). Provjerite link na sajtu FZO."
                    : $"Server je vratio grešku {(int)response.StatusCode} ({response.ReasonPhrase}).");
            }

            var total = response.Content.Headers.ContentLength;
            if (total > _maxBytes)
            {
                throw new SchemaDownloadException($"Fajl je prevelik ({FormatSize(total.Value)}); najviše je dozvoljeno {FormatSize(_maxBytes)}.");
            }

            if (response.Content.Headers.ContentType?.MediaType is { } mediaType
                && mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase))
            {
                throw new SchemaDownloadException("Na toj adresi je web stranica, a ne ZIP paket. Kopirajte link koji vodi direktno na .zip fajl.");
            }

            var buffer = new MemoryStream();
            await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            {
                var chunk = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > _maxBytes)
                    {
                        throw new SchemaDownloadException($"Fajl je prevelik; najviše je dozvoljeno {FormatSize(_maxBytes)}.");
                    }

                    buffer.Write(chunk, 0, read);
                    progress?.Report(new SchemaDownloadProgress(buffer.Length, total));
                }
            }

            if (buffer.Length == 0)
            {
                throw new SchemaDownloadException("Server je vratio prazan fajl.");
            }

            var bytes = buffer.ToArray();
            var head = bytes.AsSpan(0, Math.Min(4, bytes.Length));
            if (LooksLikeHtml(bytes))
            {
                throw new SchemaDownloadException("Na toj adresi je web stranica, a ne ZIP paket. Kopirajte link koji vodi direktno na .zip fajl.");
            }

            var fileName = FileNameFor(finalUri, head);
            var path = Path.Combine(targetFolder, fileName);
            await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            return new SchemaDownload(path, fileName, bytes.LongLength, finalUri);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            throw new SchemaDownloadException("Preuzimanje je trajalo predugo (server ne odgovara). Pokušajte ponovo.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new SchemaDownloadException($"Preuzimanje nije uspjelo: {ex.Message} Provjerite internet vezu i adresu.", ex);
        }
    }

    public void Dispose() => _client.Dispose();

    internal static string FormatSize(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / (1024.0 * 1024.0):0.#} MB"
        : $"{Math.Max(1, bytes / 1024)} KB";

    private static bool LooksLikeHtml(byte[] bytes)
    {
        var length = Math.Min(bytes.Length, 512);
        var start = System.Text.Encoding.UTF8.GetString(bytes, 0, length).TrimStart('﻿', ' ', '\t', '\r', '\n');
        return start.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase)
            || start.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
    }
}
