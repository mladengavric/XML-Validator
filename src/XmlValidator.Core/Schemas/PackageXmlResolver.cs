using System.Net;

namespace XmlValidator.Core.Schemas;

/// <summary>
/// Zatvoren resolver za <c>xs:include</c>/<c>xs:import</c>/<c>xs:redefine</c>: daje isključivo fajlove
/// iz zadatog skupa (paketa), koji se drže u memoriji pod virtuelnim korijenom <see cref="PackageRoot"/>.
/// Mrežne adrese, UNC putanje, apsolutne putanje na disku i sve što izlazi van paketa se odbija.
/// Ništa se ne čita ni sa diska ni sa mreže, pa nijedan fajl ne ostaje zaključan.
/// </summary>
internal sealed class PackageXmlResolver : XmlResolver
{
    /// <summary>Virtuelni korijen paketa; fajlovi skupa dobijaju URI-je ispod njega.</summary>
    public static readonly Uri PackageRoot = new("file:///xml-validator-package/");

    private readonly Dictionary<string, byte[]> _files;
    private readonly List<string> _rejected = [];
    private readonly List<string> _missing = [];

    public PackageXmlResolver(IEnumerable<SchemaFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        _files = files.ToDictionary(
            f => SchemaHash.NormalizePath(f.RelativePath),
            f => f.Content,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Reference koje su odbijene jer pokazuju van paketa (mreža, drugi folder, disk).</summary>
    public IReadOnlyList<string> RejectedReferences => _rejected;

    /// <summary>Reference unutar paketa na fajlove koji ne postoje.</summary>
    public IReadOnlyList<string> MissingReferences => _missing;

    public override ICredentials Credentials
    {
        set { }
    }

    /// <summary>Virtuelni URI fajla iz paketa.</summary>
    public static Uri GetUri(string relativePath)
    {
        var segments = SchemaHash.NormalizePath(relativePath).Split('/').Select(Uri.EscapeDataString);
        return new Uri(PackageRoot, string.Join('/', segments));
    }

    public override Uri ResolveUri(Uri? baseUri, string? relativeUri)
    {
        ArgumentNullException.ThrowIfNull(relativeUri);
        if (Uri.TryCreate(relativeUri, UriKind.Absolute, out var absolute))
        {
            return absolute;
        }

        return new Uri(baseUri is { IsAbsoluteUri: true } ? baseUri : PackageRoot, relativeUri);
    }

    public override object? GetEntity(Uri absoluteUri, string? role, Type? ofObjectToReturn)
    {
        ArgumentNullException.ThrowIfNull(absoluteUri);
        if (!TryGetRelativePath(absoluteUri, out var relative))
        {
            _rejected.Add(absoluteUri.OriginalString);
            throw new XmlException(
                $"Referenca '{absoluteUri.OriginalString}' nije dozvoljena: šema može da uključuje samo fajlove iz istog paketa, bez preuzimanja sa mreže.");
        }

        if (!_files.TryGetValue(relative, out var content))
        {
            _missing.Add(relative);
            throw new XmlException($"Fajl '{relative}' na koji šema upućuje ne postoji u paketu.");
        }

        return new MemoryStream(content, writable: false);
    }

    /// <summary>Vraća relativnu putanju unutar paketa ako URI pokazuje u paket.</summary>
    public static bool TryGetRelativePath(Uri uri, out string relativePath)
    {
        relativePath = string.Empty;
        if (!uri.IsAbsoluteUri
            || uri.Scheme != Uri.UriSchemeFile
            || !string.IsNullOrEmpty(uri.Host)
            || !uri.AbsolutePath.StartsWith(PackageRoot.AbsolutePath, StringComparison.Ordinal))
        {
            return false;
        }

        var path = Uri.UnescapeDataString(uri.AbsolutePath[PackageRoot.AbsolutePath.Length..]);
        if (path.Length == 0 || path.Split('/').Any(s => s is "" or "." or ".."))
        {
            return false;
        }

        relativePath = path;
        return true;
    }
}
