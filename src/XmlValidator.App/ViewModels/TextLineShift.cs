namespace XmlValidator.App.ViewModels;

/// <summary>
/// Promjena redova između dvije verzije teksta, za jednu izmjenu (kucanje, brisanje ili lijepljenje bloka).
/// Izmijenjen dio se prvo nalazi po znakovima (zajednički početak i kraj oba teksta), proširi na cijele redove,
/// pa se unutar njega porede cijeli redovi. Tako se zna koji su redovi obrisani, a koji samo pomjereni.
/// Prelom reda je <c>\r\n</c>, <c>\n</c> ili <c>\r</c>, kao kod XML čitača koji broji linije nalaza.
/// </summary>
/// <param name="FirstLine">Prvi zamijenjeni red starog teksta (od 1).</param>
/// <param name="RemovedLines">Broj redova starog teksta koji su zamijenjeni (obrisani ili izmijenjeni).</param>
/// <param name="InsertedLines">Broj redova novog teksta koji su došli na njihovo mjesto.</param>
internal sealed record TextLineShift(int FirstLine, int RemovedLines, int InsertedLines)
{
    /// <summary>Razlika u broju redova (negativna kad su redovi obrisani).</summary>
    public int Delta => InsertedLines - RemovedLines;

    /// <summary>
    /// Promjena redova; <c>null</c> kad se broj redova nije promijenio (izmjena unutar reda ne pomjera nalaze).
    /// </summary>
    public static TextLineShift? Compute(string previous, string current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        var prefix = previous.AsSpan().CommonPrefixLength(current);
        if (prefix == previous.Length && prefix == current.Length)
        {
            return null;
        }

        var max = Math.Min(previous.Length, current.Length) - prefix;
        var suffix = 0;
        while (suffix < max && previous[previous.Length - 1 - suffix] == current[current.Length - 1 - suffix])
        {
            suffix++;
        }

        // Prozor izmjene se širi na cijele redove u oba teksta (početak i kraj na granici reda).
        var start = prefix;
        while (start > 0 && !(AtLineBoundary(previous, start) && AtLineBoundary(current, start)))
        {
            start--;
        }

        var oldEnd = previous.Length - suffix;
        var newEnd = current.Length - suffix;
        while (!(AtLineBoundary(previous, oldEnd) && AtLineBoundary(current, newEnd)))
        {
            oldEnd++;
            newEnd++;
        }

        var oldLines = SplitLines(previous.AsSpan(start, oldEnd - start));
        var newLines = SplitLines(current.AsSpan(start, newEnd - start));

        var same = 0;
        while (same < oldLines.Count && same < newLines.Count && oldLines[same] == newLines[same])
        {
            same++;
        }

        var sameEnd = 0;
        while (sameEnd < oldLines.Count - same && sameEnd < newLines.Count - same
            && oldLines[oldLines.Count - 1 - sameEnd] == newLines[newLines.Count - 1 - sameEnd])
        {
            sameEnd++;
        }

        var removed = oldLines.Count - same - sameEnd;
        var inserted = newLines.Count - same - sameEnd;
        if (removed == inserted)
        {
            return null;
        }

        var first = CountBreaks(previous.AsSpan(0, start)) + 1 + same;
        return new TextLineShift(first, removed, inserted);
    }

    /// <summary>Red je obrisan (zamijenjen je, a na njegovo mjesto nije došao nijedan red).</summary>
    public bool IsDeleted(int line) =>
        InsertedLines == 0 && line >= FirstLine && line < FirstLine + RemovedLines;

    /// <summary>
    /// Novi red za red starog teksta (0 = nepoznat, ostaje 0). Obrisan red ide na prvi red iza obrisanog dijela;
    /// da li je obrisan, kaže <see cref="IsDeleted"/>.
    /// </summary>
    public int Map(int line)
    {
        if (line <= 0 || line < FirstLine)
        {
            return line;
        }

        if (line >= FirstLine + RemovedLines)
        {
            return line + Delta;
        }

        return InsertedLines == 0 ? FirstLine : FirstLine + Math.Min(line - FirstLine, InsertedLines - 1);
    }

    /// <summary>Pozicija je na početku reda (ili na kraju teksta); \r\n se ne cijepa.</summary>
    private static bool AtLineBoundary(string text, int offset) =>
        offset <= 0 || offset >= text.Length
        || text[offset - 1] == '\n'
        || (text[offset - 1] == '\r' && text[offset] != '\n');

    /// <summary>Redovi zajedno sa prelomom (posljednji može biti bez preloma).</summary>
    private static List<string> SplitLines(ReadOnlySpan<char> text)
    {
        var lines = new List<string>();
        var begin = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (IsBreakEnd(text, i))
            {
                lines.Add(text[begin..(i + 1)].ToString());
                begin = i + 1;
            }
        }

        if (begin < text.Length)
        {
            lines.Add(text[begin..].ToString());
        }

        return lines;
    }

    private static int CountBreaks(ReadOnlySpan<char> text)
    {
        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (IsBreakEnd(text, i))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Znak na poziciji završava prelom reda (\n, ili \r koji nije dio \r\n).</summary>
    private static bool IsBreakEnd(ReadOnlySpan<char> text, int i) =>
        text[i] == '\n' || (text[i] == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n'));
}
