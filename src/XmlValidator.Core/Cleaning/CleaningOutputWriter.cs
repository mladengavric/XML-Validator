using System.Text;

namespace XmlValidator.Core.Cleaning;

/// <summary>Izlaz čišćenja.</summary>
/// <param name="Text">Tekst izlaza (bez BOM-a).</param>
/// <param name="Bytes">Bajtovi izlaza u kodiranju ulaza, sa BOM-om ako ga je ulaz imao.</param>
/// <param name="Format">Kodiranje i formatiranje (isto kao ulaz).</param>
/// <param name="Blocks">Stanje blokova od kojih je izlaz napravljen.</param>
public sealed record CleaningOutput(string Text, byte[] Bytes, DocumentTextFormat Format, IReadOnlyList<BlockState> Blocks);

/// <summary>
/// Sastavlja i upisuje izlaz čišćenja. Zaglavlje, podnožje, tekst zadržanih blokova i sadržaj između
/// njih se prepisuju bez izmjene. Obrisan blok se uklanja zajedno sa svojom linijom: uvlačenje ispred
/// njega i prelazak u novi red iza njega, kad je blok sam u liniji.
/// </summary>
public static class CleaningOutputWriter
{
    /// <summary>Podrazumijevani sufiks imena izlaznog fajla.</summary>
    public const string DefaultSuffix = "_ociscen";

    /// <summary>Podrazumijevana putanja izlaza: <c>&lt;ime&gt;_ociscen&lt;ekstenzija&gt;</c> u istom folderu.</summary>
    public static string GetDefaultOutputPath(string inputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        var directory = Path.GetDirectoryName(inputPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(inputPath);
        var extension = Path.GetExtension(inputPath);
        return Path.Combine(directory, name + DefaultSuffix + (string.IsNullOrEmpty(extension) ? ".xml" : extension));
    }

    /// <summary>Sastavlja tekst izlaza.</summary>
    public static string BuildText(InvoiceDocument document, IReadOnlyList<BlockState> blocks)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(blocks);
        var builder = new StringBuilder(document.Header.Length + document.Footer.Length + document.Segments.Sum(s => s.Text.Length));
        builder.Append(document.Header);
        var parts = blocks.ToLookup(b => b.Block.Id);
        var skipLineBreak = false;
        foreach (var segment in document.Segments)
        {
            if (segment.Kind == SegmentKind.Invoice)
            {
                // Blok podijeljen ručnom izmjenom daje više faktura; tekst između njih je iz izmjene.
                var kept = parts[segment.BlockId].Where(p => p.Disposition != BlockDisposition.Deleted).ToList();
                if (kept.Count == 0)
                {
                    skipLineBreak = RemoveIndentationIfLineStart(builder);
                    continue;
                }

                skipLineBreak = false;
                for (var i = 0; i < kept.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(kept[i].Separator.Length > 0 ? kept[i].Separator : document.Format.NewLine);
                    }

                    builder.Append(kept[i].OutputText);
                }

                continue;
            }

            var text = segment.Text;
            if (skipLineBreak)
            {
                text = SkipFirstLineBreak(text);
                skipLineBreak = false;
            }

            builder.Append(text);
        }

        var footer = document.Footer;
        if (skipLineBreak)
        {
            footer = SkipFirstLineBreak(footer);
        }

        builder.Append(footer);
        return builder.ToString();
    }

    /// <summary>
    /// Upisuje bajtove izlaza. Ulazni fajl se nikad ne prepisuje; postojeći fajl se prepisuje samo uz
    /// <paramref name="overwrite"/> (izričita potvrda korisnika). Upis ide preko privremenog fajla u
    /// istom folderu, pa prekinut upis ne ostavlja polovičan fajl.
    /// </summary>
    /// <exception cref="InvalidOperationException">Izlaz je isti fajl kao ulaz.</exception>
    /// <exception cref="IOException">Fajl postoji, a prepisivanje nije potvrđeno.</exception>
    public static async Task WriteAsync(byte[] bytes, string outputPath, string? inputPath, bool overwrite, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var fullOutput = Path.GetFullPath(outputPath);
        if (inputPath is not null && IsSameFile(fullOutput, Path.GetFullPath(inputPath)))
        {
            throw new InvalidOperationException("Izlaz ne može biti isti fajl kao ulaz: ulazni fajl se nikad ne mijenja. Izaberite drugo ime.");
        }

        if (!overwrite && File.Exists(fullOutput))
        {
            throw new IOException($"Fajl „{fullOutput}” već postoji. Prepisivanje mora da potvrdi korisnik.");
        }

        var directory = Path.GetDirectoryName(fullOutput) ?? ".";
        var temp = Path.Combine(directory, $".{Path.GetFileName(fullOutput)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, fullOutput, overwrite);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static bool IsSameFile(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>Uklanja razmake/tabove na kraju ako ispred njih počinje linija; vraća da li je blok bio na početku linije.</summary>
    private static bool RemoveIndentationIfLineStart(StringBuilder builder)
    {
        var end = builder.Length;
        var start = end;
        while (start > 0 && builder[start - 1] is ' ' or '\t')
        {
            start--;
        }

        if (start > 0 && builder[start - 1] is not ('\n' or '\r'))
        {
            return false;
        }

        builder.Length = start;
        return true;
    }

    /// <summary>Preskače razmake i prvi prelazak u novi red, ako je tekst do njega samo prazan prostor.</summary>
    private static string SkipFirstLineBreak(string text)
    {
        var i = 0;
        while (i < text.Length && text[i] is ' ' or '\t')
        {
            i++;
        }

        if (i < text.Length && text[i] == '\r')
        {
            return text[(i + 1 < text.Length && text[i + 1] == '\n' ? i + 2 : i + 1)..];
        }

        if (i < text.Length && text[i] == '\n')
        {
            return text[(i + 1)..];
        }

        return i == text.Length ? string.Empty : text;
    }
}
