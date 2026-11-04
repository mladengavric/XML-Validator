using System.IO.Compression;

namespace XmlValidator.Core.Schemas;

/// <summary>Ograničenja pri čitanju paketa šema (zaštita od „zip bombi” i ogromnih paketa).</summary>
/// <param name="MaxEntries">Najveći broj stavki u ZIP-u (svih, ne samo <c>.xsd</c>).</param>
/// <param name="MaxFileBytes">Najveća veličina jednog <c>.xsd</c> fajla, raspakovanog.</param>
/// <param name="MaxTotalBytes">Najveća ukupna veličina svih <c>.xsd</c> fajlova, raspakovanih.</param>
public sealed record SchemaPackageLimits(int MaxEntries, long MaxFileBytes, long MaxTotalBytes)
{
    /// <summary>100 stavki, 5 MB po fajlu, 20 MB ukupno.</summary>
    public static SchemaPackageLimits Default { get; } = new(100, 5L * 1024 * 1024, 20L * 1024 * 1024);
}

/// <summary>Sadržaj paketa: <c>.xsd</c> fajlovi u memoriji i spisak ignorisanih fajlova.</summary>
internal sealed record SchemaPackage(
    IReadOnlyList<SchemaFile> Files,
    IReadOnlyList<string> IgnoredFiles,
    IReadOnlyDictionary<string, DateTimeOffset> LastWriteTimes);

/// <summary>Izuzetak sa porukom za korisnika kad paket ne može da se pročita.</summary>
internal sealed class SchemaPackageException(string message) : Exception(message);

/// <summary>
/// Čita <c>.xsd</c> fajl ili <c>.zip</c> paket u memoriju. ZIP se nikad ne raspakuje direktno na disk:
/// svaka putanja se provjerava (zaštita od „zip slip”), poštuju se ograničenja veličine i broja
/// fajlova, a fajlovi koji nisu <c>.xsd</c> se ignorišu i navode.
/// </summary>
internal static class SchemaPackageReader
{
    public static SchemaPackage Read(string path, SchemaPackageLimits limits, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".xsd", StringComparison.OrdinalIgnoreCase))
        {
            return ReadSingleFile(path, limits);
        }

        if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return ReadZip(path, limits, cancellationToken);
        }

        throw new SchemaPackageException($"Nepodržan tip fajla '{Path.GetFileName(path)}'. Očekuje se .xsd ili .zip.");
    }

    private static SchemaPackage ReadSingleFile(string path, SchemaPackageLimits limits)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new SchemaPackageException($"Fajl '{path}' ne postoji.");
        }

        if (info.Length > limits.MaxFileBytes)
        {
            throw new SchemaPackageException(
                $"Fajl '{info.Name}' je prevelik ({info.Length} B); najviše je dozvoljeno {limits.MaxFileBytes} B.");
        }

        var content = File.ReadAllBytes(path);
        return new SchemaPackage(
            [new SchemaFile(info.Name, content)],
            [],
            new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase) { [info.Name] = info.LastWriteTime });
    }

    private static SchemaPackage ReadZip(string path, SchemaPackageLimits limits, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new SchemaPackageException($"Fajl '{path}' ne postoji.");
        }

        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(path);
        }
        catch (InvalidDataException ex)
        {
            throw new SchemaPackageException($"Fajl '{Path.GetFileName(path)}' nije ispravan ZIP paket: {ex.Message}");
        }

        using (archive)
        {
            if (archive.Entries.Count > limits.MaxEntries)
            {
                throw new SchemaPackageException(
                    $"Paket ima previše fajlova ({archive.Entries.Count}); najviše je dozvoljeno {limits.MaxEntries}.");
            }

            var files = new List<SchemaFile>();
            var ignored = new List<string>();
            var times = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = ValidateEntryPath(entry.FullName);
                if (relative is null)
                {
                    continue; // folder
                }

                if (!relative.EndsWith(".xsd", StringComparison.OrdinalIgnoreCase))
                {
                    ignored.Add(relative);
                    continue;
                }

                if (!seen.Add(relative))
                {
                    throw new SchemaPackageException($"Paket sadrži fajl '{relative}' više puta.");
                }

                if (entry.Length > limits.MaxFileBytes)
                {
                    throw new SchemaPackageException(
                        $"Fajl '{relative}' u paketu je prevelik ({entry.Length} B); najviše je dozvoljeno {limits.MaxFileBytes} B.");
                }

                var content = ReadBounded(entry, relative, limits.MaxFileBytes);
                total += content.Length;
                if (total > limits.MaxTotalBytes)
                {
                    throw new SchemaPackageException(
                        $"Raspakovan paket je prevelik; ukupno je dozvoljeno najviše {limits.MaxTotalBytes} B.");
                }

                files.Add(new SchemaFile(relative, content));
                times[relative] = entry.LastWriteTime;
            }

            if (files.Count == 0)
            {
                throw new SchemaPackageException($"Paket '{Path.GetFileName(path)}' ne sadrži nijedan .xsd fajl.");
            }

            return new SchemaPackage(files, ignored, times);
        }
    }

    /// <summary>
    /// Provjerava putanju stavke iz ZIP-a. Vraća normalizovanu relativnu putanju, <c>null</c> za folder,
    /// a za opasnu putanju (apsolutna, sa diskom, sa <c>..</c>) baca izuzetak i odbija cijeli paket.
    /// </summary>
    internal static string? ValidateEntryPath(string fullName)
    {
        var normalized = fullName.Replace('\\', '/');
        if (normalized.EndsWith('/'))
        {
            return null;
        }

        var segments = normalized.Split('/');
        if (normalized.StartsWith('/')
            || normalized.Contains(':', StringComparison.Ordinal)
            || segments.Any(s => s is "" or "." or ".." || s.Any(c => c < ' ' || Path.GetInvalidFileNameChars().Contains(c))))
        {
            throw new SchemaPackageException(
                $"Paket sadrži nedozvoljenu putanju '{fullName}' (izlazak van paketa). Uvoz je odbijen.");
        }

        return normalized;
    }

    private static byte[] ReadBounded(ZipArchiveEntry entry, string relative, long maxBytes)
    {
        using var input = entry.Open();
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, read);
            if (output.Length > maxBytes)
            {
                throw new SchemaPackageException(
                    $"Fajl '{relative}' u paketu je prevelik; najviše je dozvoljeno {maxBytes} B.");
            }
        }

        return output.ToArray();
    }
}
