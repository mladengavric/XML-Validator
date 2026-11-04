using System.Globalization;

namespace XmlValidator.App.Services;

/// <summary>Otvoren fajl: putanja i originalni bajtovi (ulazni fajl se nikad ne mijenja).</summary>
/// <param name="Path">Puna putanja.</param>
/// <param name="Content">Sadržaj fajla, bajt po bajt.</param>
public sealed record OpenedDocument(string Path, byte[] Content)
{
    /// <summary>Najveći fajl koji se otvara (zaštita od slučajnog otvaranja ogromnog fajla).</summary>
    public const long MaxFileSize = 100L * 1024 * 1024;

    public string FileName => System.IO.Path.GetFileName(Path);

    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;

    public long Size => Content.LongLength;

    public string SizeText => FormatSize(Size);

    /// <summary>Veličina za prikaz: „512 B”, „48,3 KB”, „1,2 MB”.</summary>
    public static string FormatSize(long bytes)
    {
        var culture = CultureInfo.GetCultureInfo("sr-Latn");
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => (bytes / 1024d).ToString("0.#", culture) + " KB",
            _ => (bytes / (1024d * 1024d)).ToString("0.#", culture) + " MB",
        };
    }
}
