namespace XmlValidator.Core.Cleaning;

/// <summary>Vrsta dijela sekcije faktura.</summary>
public enum SegmentKind
{
    /// <summary>Prazan prostor i XML komentari između faktura (dozvoljen sadržaj, čuva se).</summary>
    Gap,

    /// <summary>Sadržaj koji nije faktura ni dozvoljen sadržaj; čuva se u izlazu i prijavljuje u izvještaju.</summary>
    Unrecognized,

    /// <summary>Blok fakture.</summary>
    Invoice,
}

/// <summary>Dio sekcije faktura (između zaglavlja i podnožja), sa originalnim tekstom.</summary>
/// <param name="Kind">Vrsta dijela.</param>
/// <param name="Start">Pozicija u dekodiranom tekstu.</param>
/// <param name="StartLine">Početna linija (od 1).</param>
/// <param name="EndLine">Krajnja linija (od 1).</param>
/// <param name="Text">Originalni tekst.</param>
/// <param name="BlockId">Id bloka za <see cref="SegmentKind.Invoice"/>, inače -1.</param>
public sealed record DocumentSegment(SegmentKind Kind, int Start, int StartLine, int EndLine, string Text, int BlockId = -1);

/// <summary>Vrsta automatske popravke zagrada.</summary>
public enum RepairKind
{
    /// <summary>Popravka 1: zatvarajući <c>&lt;/faktura</c> bez <c>&gt;</c>.</summary>
    EndTagMissingClosingBracket,

    /// <summary>Popravka 2: otvarajući <c>&lt;faktura ...</c> bez <c>&gt;</c> ispred <c>&lt;stavka</c> ili <c>&lt;/faktura</c>.</summary>
    StartTagMissingClosingBracket,

    /// <summary>Popravka 3: linija počinje sa <c>faktura</c> bez vodećeg <c>&lt;</c>.</summary>
    StartTagMissingOpeningBracket,
}

/// <summary>
/// Popravka zagrada (ili, kad su popravke isključene, pronađen nedostatak koji nije popravljen).
/// </summary>
/// <param name="Kind">Vrsta popravke.</param>
/// <param name="BlockId">Blok na koji se odnosi.</param>
/// <param name="Line">Linija u ulaznom fajlu (od 1).</param>
/// <param name="Description">Opis za izvještaj.</param>
/// <param name="Applied">Da li je popravka primijenjena (<c>false</c> kad su automatske popravke isključene).</param>
public sealed record RepairNote(RepairKind Kind, int BlockId, int Line, string Description, bool Applied);

/// <summary>Blok jedne fakture, onako kako je pronađen u tekstu.</summary>
/// <param name="Id">Redni broj bloka (od 0), stabilan tokom sesije.</param>
/// <param name="Start">Pozicija u dekodiranom tekstu.</param>
/// <param name="StartLine">Početna linija (od 1).</param>
/// <param name="EndLine">Krajnja linija (od 1).</param>
/// <param name="OriginalText">Originalni tekst bloka, bajt po bajt iz ulaza (poslije dekodiranja).</param>
/// <param name="Text">Tekst poslije automatskih popravki (isti kao original ako popravki nema).</param>
/// <param name="TextBefore">Originalni tekst između prethodnog bloka (ili zaglavlja) i ovog bloka.</param>
/// <param name="Repairs">Popravke (ili nepopravljeni nedostaci) u ovom bloku.</param>
/// <param name="SuspiciousReason">Razlog zašto granica bloka nije pouzdana; <c>null</c> ako jeste.</param>
public sealed record InvoiceBlock(
    int Id,
    int Start,
    int StartLine,
    int EndLine,
    string OriginalText,
    string Text,
    string TextBefore,
    IReadOnlyList<RepairNote> Repairs,
    string? SuspiciousReason)
{
    public bool IsSuspicious => SuspiciousReason is not null;

    public bool IsRepaired => Repairs.Any(r => r.Applied);
}

/// <summary>Neprepoznat sadržaj između faktura (čuva se u izlazu, prijavljuje se u izvještaju).</summary>
/// <param name="StartLine">Početna linija (od 1).</param>
/// <param name="EndLine">Krajnja linija (od 1).</param>
/// <param name="Text">Originalni tekst.</param>
public sealed record UnrecognizedContent(int StartLine, int EndLine, string Text)
{
    /// <summary>Prvih 120 znakova u jednom redu, za izvještaj (kao u <c>clean.py</c>).</summary>
    public string Preview => TextPreview.Create(Text);
}

/// <summary>
/// IZIS fajl podijeljen na zaglavlje (sve prije prve fakture), sekciju faktura i podnožje
/// (od <c>&lt;/fakture-zdravstva&gt;</c> do kraja). Spajanjem zaglavlja, teksta svih dijelova i
/// podnožja dobija se tačno ulazni tekst.
/// </summary>
/// <param name="Format">Kodiranje i formatiranje ulaza.</param>
/// <param name="Header">Zaglavlje.</param>
/// <param name="Segments">Dijelovi sekcije faktura, redom.</param>
/// <param name="Blocks">Blokovi faktura, redom (id = indeks).</param>
/// <param name="Footer">Podnožje.</param>
/// <param name="FooterLine">Linija na kojoj počinje podnožje.</param>
/// <param name="RepairsEnabled">Da li su automatske popravke bile uključene.</param>
/// <param name="Warnings">Upozorenja za cijeli fajl (npr. kodiranje).</param>
public sealed record InvoiceDocument(
    DocumentTextFormat Format,
    string Header,
    IReadOnlyList<DocumentSegment> Segments,
    IReadOnlyList<InvoiceBlock> Blocks,
    string Footer,
    int FooterLine,
    bool RepairsEnabled,
    IReadOnlyList<string> Warnings)
{
    public IEnumerable<UnrecognizedContent> Unrecognized => Segments
        .Where(s => s.Kind == SegmentKind.Unrecognized)
        .Select(s => new UnrecognizedContent(s.StartLine, s.EndLine, s.Text));

    public IEnumerable<RepairNote> Repairs => Blocks.SelectMany(b => b.Repairs);

    /// <summary>Originalni tekst (bez BOM-a), sastavljen iz dijelova.</summary>
    public string ReconstructOriginal() =>
        string.Concat([Header, .. Segments.Select(s => s.Text), Footer]);
}

internal static class TextPreview
{
    public const int MaxLength = 120;

    public static string Create(string text)
    {
        var trimmed = text.Trim();
        var oneLine = string.Join(" ", trimmed.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()));
        return oneLine.Length <= MaxLength ? oneLine : oneLine[..MaxLength] + "…";
    }
}
