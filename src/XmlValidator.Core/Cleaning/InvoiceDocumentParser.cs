namespace XmlValidator.Core.Cleaning;

/// <summary>Opcije parsiranja.</summary>
/// <param name="RepairBrackets">Da li se primjenjuju automatske popravke zagrada.</param>
public sealed record InvoiceParseOptions(bool RepairBrackets = true);

/// <summary>
/// Tekstualni parser IZIS fajla koji radi i nad polomljenim XML-om. Ne mijenja tekst: svaki dio
/// pamti originalni tekst, a popravke se bilježe uz blok.
/// <para>
/// Razlike u odnosu na <c>clean.py</c>: granica zaglavlja prepoznaje i prvu fakturu bez <c>&lt;</c>;
/// komentari i prazan prostor između faktura su dozvoljen sadržaj (komentar koji sadrži
/// <c>&lt;faktura</c> nije faktura); <c>&lt;/faktura &gt;</c> je ispravan kraj; blok sa više otvaranja
/// i blok bez zatvarajućeg taga su sumnjivi; neprepoznat sadržaj se čuva.
/// </para>
/// </summary>
public static class InvoiceDocumentParser
{
    private const int CancellationCheckInterval = 64;

    public static InvoiceDocument Parse(DecodedText decoded, InvoiceParseOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decoded);
        options ??= new InvoiceParseOptions();
        var text = decoded.Text;
        var lines = new LineIndex(text);

        // Fakture se traže tek iza otvarajućeg korijenskog taga (on može biti u više linija).
        var searchFrom = 0;
        var root = DocumentPatterns.RootOpen().Match(text);
        if (root.Success)
        {
            var rootEnd = text.IndexOf('>', root.Index);
            searchFrom = rootEnd >= 0 ? rootEnd + 1 : root.Index + root.Length;
        }

        var firstStart = FindNextInvoiceStart(text, searchFrom, text.Length);
        if (firstStart < 0)
        {
            throw new InvoiceDocumentException("U fajlu nije pronađena nijedna faktura (<faktura ...>).");
        }

        var footerMatch = DocumentPatterns.RootClose().Match(text, firstStart);
        if (!footerMatch.Success)
        {
            throw new InvoiceDocumentException("U fajlu nije pronađen zatvarajući tag </fakture-zdravstva> iza faktura.");
        }

        var footerStart = footerMatch.Index;
        var segments = new List<DocumentSegment>();
        var blocks = new List<InvoiceBlock>();
        var pendingBefore = new System.Text.StringBuilder();
        var pos = firstStart;

        while (pos < footerStart)
        {
            if (segments.Count % CancellationCheckInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            // Prazan prostor i komentari.
            var gapEnd = SkipAllowedContent(text, pos, footerStart);
            if (gapEnd > pos)
            {
                AddSegment(SegmentKind.Gap, pos, gapEnd);
                pos = gapEnd;
                continue;
            }

            if (IsInvoiceStartAt(text, pos))
            {
                pos = ParseBlock(pos);
                continue;
            }

            // Neprepoznat sadržaj do sljedeće fakture; prazan prostor na kraju pripada sljedećem dijelu.
            var next = FindNextInvoiceStart(text, pos + 1, footerStart);
            var end = next < 0 ? footerStart : next;
            end = TrimEndWhitespace(text, pos, end);
            AddSegment(SegmentKind.Unrecognized, pos, end);
            pos = end;
        }

        var document = new InvoiceDocument(
            decoded.Format,
            text[..firstStart],
            segments,
            blocks,
            text[footerStart..],
            lines.LineOf(footerStart),
            options.RepairBrackets,
            decoded.Warnings);
        return document;

        void AddSegment(SegmentKind kind, int start, int end, int blockId = -1)
        {
            var segmentText = text[start..end];
            segments.Add(new DocumentSegment(kind, start, lines.LineOf(start), lines.LineOf(Math.Max(start, end - 1)), segmentText, blockId));
            if (kind != SegmentKind.Invoice)
            {
                pendingBefore.Append(segmentText);
            }
        }

        int ParseBlock(int start)
        {
            var nameLength = text[start] == '<' ? DocumentPatterns.InvoiceOpen.Length : "faktura".Length;
            var close = DocumentPatterns.InvoiceCloseTag().Match(text, start + nameLength, footerStart - start - nameLength);
            int end;
            string? suspicious = null;
            int extraOpenings;
            if (close.Success)
            {
                var afterName = close.Index + close.Length;
                var bracket = DocumentPatterns.CloseBracketAfterEndTag().Match(text, afterName, footerStart - afterName);
                end = bracket.Success ? afterName + bracket.Length : afterName;
                extraOpenings = CountInvoiceStarts(text, start + nameLength, close.Index);
                if (extraOpenings > 0)
                {
                    suspicious = $"Blok sadrži {extraOpenings + 1} otvaranja <faktura, a samo jedan zatvarajući tag: vjerovatno fali </faktura> "
                        + "prethodne fakture, pa granica fakture nije pouzdana. Potrebna je ručna odluka (najbolje Uredi).";
                }
            }
            else
            {
                end = TrimEndWhitespace(text, start, footerStart);
                extraOpenings = CountInvoiceStarts(text, start + nameLength, end);
                suspicious = extraOpenings > 0
                    ? $"Faktura nema zatvarajući tag </faktura>, a iza nje slijedi još {extraOpenings} otvaranja <faktura; granica fakture nije pouzdana. Potrebna je ručna odluka."
                    : "Faktura nema zatvarajući tag </faktura> (posljednja faktura u fajlu); granica fakture nije pouzdana. Potrebna je ručna odluka.";
            }

            var id = blocks.Count;
            var original = text[start..end];
            var repair = BracketRepairer.Repair(original, id, offset => lines.LineOf(start + offset), options.RepairBrackets);
            blocks.Add(new InvoiceBlock(
                id,
                start,
                lines.LineOf(start),
                lines.LineOf(Math.Max(start, end - 1)),
                original,
                repair.Text,
                pendingBefore.ToString(),
                repair.Notes,
                suspicious));
            pendingBefore.Clear();
            AddSegment(SegmentKind.Invoice, start, end, id);
            return end;
        }
    }

    /// <summary>Preskače prazan prostor i završene komentare; vraća poziciju prvog drugog znaka.</summary>
    private static int SkipAllowedContent(string text, int pos, int limit)
    {
        while (pos < limit)
        {
            if (char.IsWhiteSpace(text[pos]))
            {
                pos++;
            }
            else if (string.CompareOrdinal(text, pos, "<!--", 0, 4) == 0)
            {
                var end = text.IndexOf("-->", pos + 4, limit - pos - 4, StringComparison.Ordinal);
                if (end < 0)
                {
                    return pos; // nezavršen komentar je neprepoznat sadržaj
                }

                pos = end + 3;
            }
            else
            {
                break;
            }
        }

        return pos;
    }

    private static bool IsInvoiceStartAt(string text, int pos)
    {
        var lineStart = pos;
        while (lineStart > 0 && text[lineStart - 1] is ' ' or '\t')
        {
            lineStart--;
        }

        var match = DocumentPatterns.InvoiceStartOrComment().Match(text, lineStart);
        return match.Success && StartOf(match) == pos && match.Value != "<!--";
    }

    /// <summary>Sljedeći početak fakture u [from, limit), uz preskakanje komentara; -1 ako ga nema.</summary>
    private static int FindNextInvoiceStart(string text, int from, int limit)
    {
        var match = DocumentPatterns.InvoiceStartOrComment().Match(text, LineStartForSearch(text, from));
        while (match.Success && match.Index < limit)
        {
            if (match.Value == "<!--")
            {
                var end = text.IndexOf("-->", match.Index + 4, StringComparison.Ordinal);
                if (end < 0)
                {
                    return -1;
                }

                match = DocumentPatterns.InvoiceStartOrComment().Match(text, end + 3);
                continue;
            }

            var start = StartOf(match);
            if (start >= from && start < limit)
            {
                return start;
            }

            match = match.NextMatch();
        }

        return -1;
    }

    /// <summary>Broj početaka faktura u [from, limit) (bez komentara).</summary>
    private static int CountInvoiceStarts(string text, int from, int limit)
    {
        var count = 0;
        var pos = from;
        while (true)
        {
            var next = FindNextInvoiceStart(text, pos, limit);
            if (next < 0)
            {
                return count;
            }

            count++;
            pos = next + 1;
        }
    }

    // Pretraga počinje od početka linije da bi lookbehind za početak linije radio i usred teksta.
    private static int LineStartForSearch(string text, int from)
    {
        var start = from;
        while (start > 0 && text[start - 1] is ' ' or '\t')
        {
            start--;
        }

        return start;
    }

    private static int StartOf(System.Text.RegularExpressions.Match match)
    {
        var bare = match.Groups["bare"];
        return bare.Success ? bare.Index : match.Index;
    }

    private static int TrimEndWhitespace(string text, int start, int end)
    {
        while (end > start && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        return end;
    }
}

/// <summary>Dijeli provjeren XML fragment (jedna ili više faktura) na pojedinačne fakture.</summary>
internal static class InvoiceFragmentSplitter
{
    /// <summary>Fakture iz teksta, sa tekstom ispred svake (prazan prostor i komentari; za prvu prazan).</summary>
    /// <remarks>Tekst mora biti provjeren sa <see cref="InvoiceAttributeReader.ValidateFragment"/>.</remarks>
    public static IReadOnlyList<(string Separator, string Text)> Split(string text)
    {
        var parts = new List<(string, string)>();
        var pos = 0;
        var previousEnd = -1;
        while (pos < text.Length)
        {
            if (string.CompareOrdinal(text, pos, "<!--", 0, 4) == 0)
            {
                var commentEnd = text.IndexOf("-->", pos + 4, StringComparison.Ordinal);
                pos = commentEnd < 0 ? text.Length : commentEnd + 3;
                continue;
            }

            if (string.CompareOrdinal(text, pos, DocumentPatterns.InvoiceOpen, 0, DocumentPatterns.InvoiceOpen.Length) != 0)
            {
                pos++;
                continue;
            }

            var tagEnd = FindTagEnd(text, pos + DocumentPatterns.InvoiceOpen.Length);
            int end;
            if (text[tagEnd - 1] == '/')
            {
                end = tagEnd + 1;
            }
            else
            {
                var close = DocumentPatterns.InvoiceCloseTag().Match(text, tagEnd);
                var bracket = text.IndexOf('>', close.Index + close.Length);
                end = bracket + 1;
            }

            var separator = previousEnd < 0 ? string.Empty : text[previousEnd..pos];
            parts.Add((separator, text[pos..end]));
            previousEnd = end;
            pos = end;
        }

        return parts;
    }

    /// <summary>Pozicija '>' koji zatvara tag, uz preskakanje vrijednosti atributa pod navodnicima.</summary>
    private static int FindTagEnd(string text, int pos)
    {
        char? quote = null;
        for (; pos < text.Length; pos++)
        {
            var c = text[pos];
            if (quote is { } q)
            {
                if (c == q)
                {
                    quote = null;
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '>')
            {
                return pos;
            }
        }

        return text.Length - 1;
    }
}
