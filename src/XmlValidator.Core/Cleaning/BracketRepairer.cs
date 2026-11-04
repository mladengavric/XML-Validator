using System.Text;

namespace XmlValidator.Core.Cleaning;

/// <summary>Rezultat popravke jednog bloka.</summary>
/// <param name="Text">Tekst poslije popravki (isti kao ulaz ako ništa nije primijenjeno).</param>
/// <param name="Notes">Popravke i nedostaci, redom po poziciji u bloku.</param>
public sealed record BracketRepairResult(string Text, IReadOnlyList<RepairNote> Notes);

/// <summary>
/// Tri sigurne popravke zagrada iz <c>clean.py</c>, sa ispravkama (PLAN.md):
/// <list type="number">
/// <item>zatvarajući <c>&lt;/faktura</c> bez <c>&gt;</c> dobija <c>&gt;</c>; <c>&lt;/faktura &gt;</c> i
/// <c>&lt;/faktura</c> + novi red + <c>&gt;</c> su ispravni i ne diraju se;</item>
/// <item>otvarajući <c>&lt;faktura ...</c> bez <c>&gt;</c> ispred <c>&lt;stavka</c> ili <c>&lt;/faktura</c>
/// dobija <c>&gt;</c> odmah iza posljednjeg atributa;</item>
/// <item>faktura koja počinje bez <c>&lt;</c> (i uvučena) dobija <c>&lt;</c>; radi se prije popravke 2,
/// pa faktura kojoj fale obje zagrade dobija obje.</item>
/// </list>
/// Popravka 3 se primjenjuje samo na početak bloka; parser blok bez <c>&lt;</c> prepoznaje samo
/// između faktura, nikad unutar sadržaja stavki.
/// </summary>
public static class BracketRepairer
{
    private const string BareName = "faktura";

    /// <summary>Popravlja blok.</summary>
    /// <param name="text">Originalni tekst bloka (počinje sa <c>&lt;faktura</c> ili <c>faktura</c>).</param>
    /// <param name="blockId">Id bloka (za bilješke).</param>
    /// <param name="lineOfOffset">Broj linije u ulaznom fajlu za poziciju u tekstu bloka.</param>
    /// <param name="apply">Da li se popravke primjenjuju; ako ne, nedostaci se samo bilježe.</param>
    public static BracketRepairResult Repair(string text, int blockId, Func<int, int> lineOfOffset, bool apply)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(lineOfOffset);

        var notes = new List<RepairNote>();
        var insertions = new List<(int Offset, char Value)>();
        var disabled = apply ? string.Empty : " (automatske popravke su isključene, nije popravljeno)";

        // Popravka 3: fali vodeći '<'.
        var missingLeading = text.StartsWith(BareName, StringComparison.Ordinal);
        if (missingLeading)
        {
            insertions.Add((0, '<'));
            notes.Add(new RepairNote(RepairKind.StartTagMissingOpeningBracket, blockId, lineOfOffset(0),
                (apply ? "Dodat vodeći '<' na <faktura tag." : "Fali vodeći '<' na <faktura tagu.") + disabled, apply));
        }

        // Popravka 2: otvarajući tag bez '>'.
        var nameEnd = missingLeading ? BareName.Length : DocumentPatterns.InvoiceOpen.Length;
        var gt = text.IndexOf('>', nameEnd);
        var lt = text.IndexOf('<', nameEnd);
        if (lt >= 0 && (gt < 0 || lt < gt))
        {
            var insertAt = lt;
            while (insertAt > nameEnd && char.IsWhiteSpace(text[insertAt - 1]))
            {
                insertAt--;
            }

            var followedByItemOrEnd = string.CompareOrdinal(text, lt, "<stavka", 0, 7) == 0
                || string.CompareOrdinal(text, lt, DocumentPatterns.InvoiceClose, 0, DocumentPatterns.InvoiceClose.Length) == 0;
            if (followedByItemOrEnd)
            {
                insertions.Add((insertAt, '>'));
                notes.Add(new RepairNote(RepairKind.StartTagMissingClosingBracket, blockId, lineOfOffset(Math.Max(insertAt - 1, 0)),
                    (apply ? "Dodat '>' na otvarajući <faktura ...> tag." : "Fali '>' na otvarajućem <faktura ...> tagu.") + disabled, apply));
            }
            else
            {
                notes.Add(new RepairNote(RepairKind.StartTagMissingClosingBracket, blockId, lineOfOffset(Math.Max(insertAt - 1, 0)),
                    "Fali '>' na otvarajućem <faktura ...> tagu, a iza njega nije <stavka ni </faktura; nije popravljeno, provjerite ručno.", Applied: false));
            }
        }

        // Popravka 1: zatvarajući tag bez '>' (parser završava blok odmah iza "</faktura" kad '>' fali).
        if (text.EndsWith(DocumentPatterns.InvoiceClose, StringComparison.Ordinal))
        {
            insertions.Add((text.Length, '>'));
            notes.Add(new RepairNote(RepairKind.EndTagMissingClosingBracket, blockId,
                lineOfOffset(text.Length - DocumentPatterns.InvoiceClose.Length),
                (apply ? "Dodat '>' na zatvarajući </faktura tag." : "Fali '>' na zatvarajućem </faktura tagu.") + disabled, apply));
        }

        if (!apply || insertions.Count == 0)
        {
            return new BracketRepairResult(text, notes);
        }

        var builder = new StringBuilder(text, text.Length + insertions.Count);
        foreach (var (offset, value) in insertions.OrderByDescending(i => i.Offset))
        {
            builder.Insert(offset, value);
        }

        return new BracketRepairResult(builder.ToString(), notes);
    }
}
