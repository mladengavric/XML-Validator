namespace XmlValidator.App.ViewModels;

/// <summary>
/// Pomak linija između dvije verzije teksta, za jednu izmjenu (kucanje, brisanje ili lijepljenje bloka).
/// Izmijenjen dio se nalazi kao razlika između zajedničkog početka i zajedničkog kraja oba teksta.
/// Prelom linije je <c>\r\n</c>, <c>\n</c> ili <c>\r</c>, kao kod XML čitača koji broji linije nalaza.
/// </summary>
/// <param name="StartLine">Prva linija (od 1) koju izmjena dira.</param>
/// <param name="OldEndLine">Posljednja linija izmijenjenog dijela u starom tekstu.</param>
/// <param name="NewEndLine">Posljednja linija izmijenjenog dijela u novom tekstu.</param>
/// <param name="EndsAtLineStart">Izmijenjeni dio u starom tekstu se završava na početku linije
/// (npr. umetnuti ili obrisani cijeli redovi), pa se i linija <paramref name="OldEndLine"/> samo pomjera.</param>
internal sealed record TextLineShift(int StartLine, int OldEndLine, int NewEndLine, bool EndsAtLineStart)
{
    /// <summary>Razlika u broju linija (negativna kad su linije obrisane).</summary>
    public int Delta => NewEndLine - OldEndLine;

    /// <summary>
    /// Pomak linija; <c>null</c> kad se broj linija nije promijenio (izmjena unutar linije ne pomjera nalaze).
    /// </summary>
    public static TextLineShift? Compute(string previous, string current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        var prefix = previous.AsSpan().CommonPrefixLength(current);
        var max = Math.Min(previous.Length, current.Length) - prefix;
        var suffix = 0;
        while (suffix < max && previous[previous.Length - 1 - suffix] == current[current.Length - 1 - suffix])
        {
            suffix++;
        }

        // Prelom \r\n se ne cijepa: početak izmjene ide ispred \r, a kraj iza \n (u bilo kojem od dva teksta).
        if (prefix > 0 && previous[prefix - 1] == '\r'
            && ((prefix < previous.Length && previous[prefix] == '\n') || (prefix < current.Length && current[prefix] == '\n')))
        {
            prefix--;
        }

        if (suffix > 0 && previous[^suffix] == '\n'
            && ((previous.Length - suffix - 1 >= prefix && previous[previous.Length - suffix - 1] == '\r')
                || (current.Length - suffix - 1 >= prefix && current[current.Length - suffix - 1] == '\r')))
        {
            suffix--;
        }

        var removed = previous.AsSpan(prefix, previous.Length - prefix - suffix);
        var inserted = current.AsSpan(prefix, current.Length - prefix - suffix);
        var removedBreaks = CountBreaks(removed);
        var insertedBreaks = CountBreaks(inserted);
        if (removedBreaks == insertedBreaks)
        {
            return null;
        }

        var start = CountBreaks(previous.AsSpan(0, prefix)) + 1;
        var end = previous.Length - suffix;
        var endsAtLineStart = end == 0 || previous[end - 1] is '\n' or '\r';
        return new TextLineShift(start, start + removedBreaks, start + insertedBreaks, endsAtLineStart);
    }

    /// <summary>Nova linija za liniju iz starog teksta (0 = nepoznata, ostaje 0).</summary>
    public int Map(int line)
    {
        if (line <= 0 || line < StartLine)
        {
            return line;
        }

        if (line > OldEndLine || (line == OldEndLine && EndsAtLineStart))
        {
            return line + Delta;
        }

        // Linija u izmijenjenom dijelu: ostaje ako i dalje postoji, inače ide na kraj novog dijela.
        return Math.Min(line, NewEndLine);
    }

    private static int CountBreaks(ReadOnlySpan<char> text)
    {
        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                count++;
            }
            else if (text[i] == '\r')
            {
                count++;
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
            }
        }

        return count;
    }
}
