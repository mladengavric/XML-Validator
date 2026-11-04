using System.Text;

namespace XmlValidator.Core.Cleaning;

/// <summary>Greška zbog koje fajl ne može da se analizira (poruka je za korisnika).</summary>
public sealed class InvoiceDocumentException : Exception
{
    public InvoiceDocumentException(string message)
        : base(message)
    {
    }

    public InvoiceDocumentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public InvoiceDocumentException()
    {
    }
}

/// <summary>Vrsta završetaka linija u fajlu.</summary>
public enum LineEndingKind
{
    /// <summary>Fajl nema nijedan prelazak u novi red.</summary>
    None,
    Lf,
    CrLf,
    Cr,

    /// <summary>Više vrsta; <see cref="DocumentTextFormat.NewLine"/> je najčešća.</summary>
    Mixed,
}

/// <summary>Kodiranje i formatiranje ulaznog fajla, koje se čuva u izlazu.</summary>
/// <param name="Encoding">Kodiranje (sa izuzetkom za neispravne bajtove i znakove koji ne postoje u kodiranju).</param>
/// <param name="EncodingName">Naziv kodiranja za izvještaj (npr. <c>UTF-8</c>, <c>windows-1250</c>).</param>
/// <param name="HasBom">Da li fajl počinje oznakom redoslijeda bajtova (BOM).</param>
/// <param name="LineEndings">Vrsta završetaka linija.</param>
/// <param name="NewLine">Najčešći završetak linije (za tekst koji se umeće, npr. uređen blok); <c>\n</c> ako ih nema.</param>
/// <param name="EndsWithNewLine">Da li se fajl završava prelaskom u novi red.</param>
public sealed record DocumentTextFormat(
    Encoding Encoding,
    string EncodingName,
    bool HasBom,
    LineEndingKind LineEndings,
    string NewLine,
    bool EndsWithNewLine)
{
    /// <summary>Opis za izvještaj, npr. „UTF-8 bez BOM-a, LF, bez završnog novog reda”.</summary>
    public string Description
    {
        get
        {
            var bom = HasBom ? "sa BOM-om" : "bez BOM-a";
            var endings = LineEndings switch
            {
                LineEndingKind.None => "bez prelazaka u novi red",
                LineEndingKind.Lf => "LF",
                LineEndingKind.CrLf => "CRLF",
                LineEndingKind.Cr => "CR",
                _ => $"mješoviti završeci linija (najčešći {NewLineName(NewLine)})",
            };
            var final = EndsWithNewLine ? "sa završnim novim redom" : "bez završnog novog reda";
            return $"{EncodingName} {bom}, {endings}, {final}";
        }
    }

    internal static string NewLineName(string newLine) => newLine switch
    {
        "\r\n" => "CRLF",
        "\r" => "CR",
        _ => "LF",
    };
}

/// <summary>Dekodiran tekst fajla.</summary>
/// <param name="Text">Tekst bez BOM-a, sa originalnim završecima linija.</param>
/// <param name="Format">Kodiranje i formatiranje.</param>
/// <param name="Warnings">Upozorenja pri određivanju kodiranja.</param>
public sealed record DecodedText(string Text, DocumentTextFormat Format, IReadOnlyList<string> Warnings);

/// <summary>
/// Određuje kodiranje po BOM-u i XML deklaraciji i dekodira fajl bez gubitka: neispravni bajtovi
/// su greška (nikad se ne zamjenjuju znakom �), pa ponovno kodiranje daje iste bajtove.
/// </summary>
public static class DocumentTextDecoder
{
    private const int DeclarationScanLimit = 1024;

    public static DecodedText Decode(ReadOnlySpan<byte> bytes)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var warnings = new List<string>();

        var (bomEncoding, bomLength) = DetectBom(bytes);
        var declared = ReadDeclaredEncoding(bytes[bomLength..], bomEncoding);

        Encoding encoding;
        if (bomEncoding is not null)
        {
            encoding = bomEncoding;
            if (declared is not null && !SameEncoding(declared, encoding))
            {
                warnings.Add($"XML deklaracija navodi kodiranje „{declared}”, a BOM označava {DisplayName(encoding)}; koristi se {DisplayName(encoding)}.");
            }
        }
        else if (declared is not null)
        {
            encoding = GetStrictEncoding(declared)
                ?? throw new InvoiceDocumentException($"Kodiranje „{declared}” iz XML deklaracije nije podržano.");
        }
        else
        {
            encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        }

        string text;
        try
        {
            text = encoding.GetString(bytes[bomLength..]);
        }
        catch (DecoderFallbackException ex)
        {
            var position = ex.Index >= 0 ? $" (oko bajta {bomLength + ex.Index})" : string.Empty;
            throw new InvoiceDocumentException(
                $"Fajl nije ispravno kodiran kao {DisplayName(encoding)}{position}. Provjerite kodiranje u XML deklaraciji.", ex);
        }

        var (kind, newLine) = DetectLineEndings(text);
        var endsWithNewLine = text.EndsWith('\n') || text.EndsWith('\r');
        var format = new DocumentTextFormat(encoding, DisplayName(encoding), bomLength > 0, kind, newLine, endsWithNewLine);
        return new DecodedText(text, format, warnings);
    }

    /// <summary>Kodira tekst u format fajla (sa BOM-om ako ga je ulaz imao).</summary>
    /// <exception cref="EncoderFallbackException">Tekst sadrži znak koji ne postoji u kodiranju.</exception>
    public static byte[] Encode(string text, DocumentTextFormat format)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(format);
        // BOM postoji samo kad ga je ulaz imao, a tada je kodiranje napravljeno sa BOM-om (DetectBom).
        var preamble = format.HasBom ? format.Encoding.GetPreamble() : [];
        var body = format.Encoding.GetBytes(text);
        return [.. preamble, .. body];
    }

    /// <summary>Prvi znak koji ne postoji u kodiranju fajla, ili <c>null</c> ako se tekst može kodirati.</summary>
    public static string? FindUnencodableCharacter(string text, DocumentTextFormat format)
    {
        try
        {
            format.Encoding.GetByteCount(text);
            return null;
        }
        catch (EncoderFallbackException ex)
        {
            return ex.CharUnknownHigh != '\0'
                ? new string([ex.CharUnknownHigh, ex.CharUnknownLow])
                : ex.CharUnknown.ToString();
        }
    }

    /// <summary>Pretvara sve završetke linija u zadati.</summary>
    public static string NormalizeLineEndings(string text, string newLine)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lf = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return newLine == "\n" ? lf : lf.Replace("\n", newLine, StringComparison.Ordinal);
    }

    private static (Encoding? Encoding, int Length) DetectBom(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true), 3);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return (new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true), 2);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return (new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: true), 2);
        }

        return (null, 0);
    }

    private static string? ReadDeclaredEncoding(ReadOnlySpan<byte> bytes, Encoding? bomEncoding)
    {
        string head;
        if (bomEncoding is UnicodeEncoding)
        {
            var length = Math.Min(bytes.Length, DeclarationScanLimit * 2) & ~1;
            head = bomEncoding.GetString(bytes[..length]);
        }
        else
        {
            // Deklaracija je ASCII u svim podržanim jednobajtnim kodiranjima i u UTF-8.
            head = Encoding.Latin1.GetString(bytes[..Math.Min(bytes.Length, DeclarationScanLimit)]);
        }

        var match = DocumentPatterns.XmlDeclarationEncoding().Match(head);
        return match.Success ? match.Groups["enc"].Value : null;
    }

    private static Encoding? GetStrictEncoding(string name)
    {
        if (string.Equals(name, "utf-8", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "utf8", StringComparison.OrdinalIgnoreCase))
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        }

        try
        {
            return Encoding.GetEncoding(name, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool SameEncoding(string declared, Encoding encoding)
    {
        var other = GetStrictEncoding(declared);
        return other is not null && other.CodePage == encoding.CodePage;
    }

    private static string DisplayName(Encoding encoding) => encoding.CodePage switch
    {
        65001 => "UTF-8",
        1200 => "UTF-16LE",
        1201 => "UTF-16BE",
        _ => encoding.WebName,
    };

    private static (LineEndingKind Kind, string NewLine) DetectLineEndings(string text)
    {
        int lf = 0, crlf = 0, cr = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    crlf++;
                    i++;
                }
                else
                {
                    cr++;
                }
            }
            else if (text[i] == '\n')
            {
                lf++;
            }
        }

        var kinds = (lf > 0 ? 1 : 0) + (crlf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0);
        var newLine = crlf >= lf && crlf >= cr && crlf > 0 ? "\r\n" : cr > lf ? "\r" : "\n";
        var kind = kinds switch
        {
            0 => LineEndingKind.None,
            > 1 => LineEndingKind.Mixed,
            _ => lf > 0 ? LineEndingKind.Lf : crlf > 0 ? LineEndingKind.CrLf : LineEndingKind.Cr,
        };
        return (kind, newLine);
    }
}
