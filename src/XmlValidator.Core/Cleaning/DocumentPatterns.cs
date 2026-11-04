using System.Text.RegularExpressions;

namespace XmlValidator.Core.Cleaning;

/// <summary>
/// Regularni izrazi za tekstualnu analizu. Imena elemenata i atributa se porede tačno: poslije imena
/// ne smije doći znak imena (<c>[\w.:-]</c>), pa se <c>&lt;faktura</c> ne poklapa sa
/// <c>&lt;fakture-zdravstva</c>, a <c>broj</c> ne sa <c>ugovor-broj</c>.
/// </summary>
internal static partial class DocumentPatterns
{
    public const string InvoiceOpen = "<faktura";
    public const string InvoiceClose = "</faktura";

    [GeneratedRegex("""^\s*<\?xml\s[^>]*?\bencoding\s*=\s*["'](?<enc>[A-Za-z][A-Za-z0-9._-]*)["']""")]
    public static partial Regex XmlDeclarationEncoding();

    [GeneratedRegex(@"<fakture-zdravstva(?![\w.:-])")]
    public static partial Regex RootOpen();

    [GeneratedRegex(@"</fakture-zdravstva(?![\w.:-])")]
    public static partial Regex RootClose();

    /// <summary>
    /// Početak fakture: <c>&lt;faktura</c>, ili (popravka 3) <c>faktura</c> na početku linije, eventualno
    /// uvučeno, kad iza slijedi atribut, <c>&gt;</c> ili <c>&lt;</c>. Komentar se hvata da bi se preskočio.
    /// </summary>
    [GeneratedRegex(@"<!--|<faktura(?![\w.:-])|(?<=^|[\r\n])[ \t]*(?<bare>faktura)(?=[ \t\r\n]+[A-Za-z_][\w.:-]*[ \t\r\n]*=|[ \t\r\n]*>|[ \t\r\n]*<)")]
    public static partial Regex InvoiceStartOrComment();

    [GeneratedRegex(@"</faktura(?![\w.:-])")]
    public static partial Regex InvoiceCloseTag();

    [GeneratedRegex(@"\G\s*>")]
    public static partial Regex CloseBracketAfterEndTag();

    [GeneratedRegex(@"<stavka(?:-recept|-obavjest)?(?![\w.:-])")]
    public static partial Regex ItemOpen();

    [GeneratedRegex("""(?<![\w.:-])usluga-grupa\s*=\s*(?:"(?<v>[^"]*)"|'(?<v>[^']*)')""")]
    public static partial Regex GroupAttribute();

    [GeneratedRegex(@"&(?:#x(?<hex>[0-9A-Fa-f]{1,6})|#(?<dec>[0-9]{1,7})|(?<name>lt|gt|amp|quot|apos));")]
    public static partial Regex EntityReference();

    [GeneratedRegex(@"(?<!\d)(?<keep>\d{7})\d{6}(?!\d)")]
    public static partial Regex Jmb();

    public static Regex Attribute(string name) => name switch
    {
        "broj" => BrojAttribute(),
        "datum" => DatumAttribute(),
        "osiguranik-jmb" => JmbAttribute(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    [GeneratedRegex("""(?<![\w.:-])broj\s*=\s*(?:"(?<v>[^"]*)"|'(?<v>[^']*)')""")]
    private static partial Regex BrojAttribute();

    [GeneratedRegex("""(?<![\w.:-])datum\s*=\s*(?:"(?<v>[^"]*)"|'(?<v>[^']*)')""")]
    private static partial Regex DatumAttribute();

    [GeneratedRegex("""(?<![\w.:-])osiguranik-jmb\s*=\s*(?:"(?<v>[^"]*)"|'(?<v>[^']*)')""")]
    private static partial Regex JmbAttribute();
}

/// <summary>Pretvaranje pozicije u tekstu u broj linije (od 1). CRLF, LF i CR se računaju kao jedan prelazak.</summary>
internal sealed class LineIndex
{
    private readonly int[] _lineStarts;

    public LineIndex(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                starts.Add(i + 1);
            }
            else if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        _lineStarts = [.. starts];
    }

    public int LineOf(int offset)
    {
        var index = Array.BinarySearch(_lineStarts, offset);
        return index >= 0 ? index + 1 : ~index;
    }
}
