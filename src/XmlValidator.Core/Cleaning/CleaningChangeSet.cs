namespace XmlValidator.Core.Cleaning;

/// <summary>Vrsta razlike između ulaza i izlaza za jedan blok.</summary>
public enum CleaningChangeKind
{
    /// <summary>Blok se izbacuje iz izlaza.</summary>
    Deleted,

    /// <summary>Blok je ručno izmijenjen (akcija Uredi).</summary>
    Edited,

    /// <summary>Blok ostaje, ali sa automatskom popravkom zagrada.</summary>
    Repaired,
}

/// <summary>Jedna razlika između ulaza i izlaza (jedan blok iz ulaza).</summary>
/// <param name="Kind">Vrsta razlike.</param>
/// <param name="BlockId">Id bloka iz ulaza.</param>
/// <param name="StartLine">Početna linija bloka u ulazu.</param>
/// <param name="EndLine">Završna linija bloka u ulazu.</param>
/// <param name="Number">Broj fakture iz ulaza (prije izmjene).</param>
/// <param name="Date">Datum fakture iz ulaza.</param>
/// <param name="Jmb">JMB iz ulaza.</param>
/// <param name="Description">Kratak opis za korisnika (razlog brisanja, opis izmjene ili popravke).</param>
/// <param name="OldText">Tekst bloka u ulazu (originalni, prije popravki).</param>
/// <param name="NewText">Tekst koji ide u izlaz; <c>null</c> za obrisan blok.</param>
public sealed record CleaningChange(
    CleaningChangeKind Kind,
    int BlockId,
    int StartLine,
    int EndLine,
    string? Number,
    string? Date,
    string? Jmb,
    string Description,
    string OldText,
    string? NewText);

/// <summary>
/// Pregled razlika između ulaza i izlaza prije čuvanja: blokovi koji se brišu, mijenjaju ručno ili
/// automatski popravljaju. Blokovi koji ostaju bajt po bajt isti nisu u listi.
/// </summary>
public sealed record CleaningChangeSet(IReadOnlyList<CleaningChange> Changes, int TotalBlocks, int RemainingInvoices)
{
    public int DeletedCount => Changes.Count(c => c.Kind == CleaningChangeKind.Deleted);

    public int EditedCount => Changes.Count(c => c.Kind == CleaningChangeKind.Edited);

    public int RepairedCount => Changes.Count(c => c.Kind == CleaningChangeKind.Repaired);

    public bool IsEmpty => Changes.Count == 0;

    /// <summary>Razlike iz stanja faktura (sesija ili napravljen izlaz), redom kao u ulazu.</summary>
    /// <param name="blocks">Stanja faktura (<see cref="CleaningSession.Blocks"/> ili <see cref="CleaningOutput.Blocks"/>).</param>
    /// <exception cref="InvalidOperationException">Neka faktura čeka odluku.</exception>
    public static CleaningChangeSet Create(IReadOnlyList<BlockState> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        if (blocks.FirstOrDefault(b => b.Disposition == BlockDisposition.Unresolved) is { } unresolved)
        {
            throw new InvalidOperationException($"Pregled izmjena nije moguć: faktura na liniji {unresolved.Block.StartLine} čeka odluku.");
        }

        var changes = new List<CleaningChange>();
        foreach (var parts in blocks.GroupBy(b => b.Block.Id))
        {
            var list = parts.OrderBy(p => p.PartIndex).ToList();
            if (Describe(list) is { } change)
            {
                changes.Add(change);
            }
        }

        var remaining = blocks.Count(b => b.Disposition != BlockDisposition.Deleted);
        var total = blocks.Select(b => b.Block.Id).Distinct().Count();
        return new CleaningChangeSet([.. changes.OrderBy(c => c.StartLine).ThenBy(c => c.BlockId)], total, remaining);
    }

    private static CleaningChange? Describe(List<BlockState> parts)
    {
        var first = parts[0];
        var block = first.Block;
        var data = first.OriginalData;
        CleaningChange Change(CleaningChangeKind kind, string description, string? newText) =>
            new(kind, block.Id, block.StartLine, block.EndLine, data.Number, data.Date, data.EffectiveJmb, description, block.OriginalText, newText);

        var kept = parts.Where(p => p.Disposition != BlockDisposition.Deleted).ToList();
        if (kept.Count == 0)
        {
            var reason = parts.Count == 1
                ? first.Classification.Reason
                : $"Svi dijelovi uređenog bloka ({parts.Count}) se brišu.";
            return Change(CleaningChangeKind.Deleted, reason, null);
        }

        if (parts.Any(p => p.IsEdited))
        {
            var newText = string.Concat(kept.Select((p, i) => (i == 0 ? string.Empty : p.Separator) + p.OutputText));
            var description = parts.Count == 1
                ? "Ručno izmijenjena."
                : $"Ručno izmijenjena i podijeljena na {parts.Count} faktura"
                    + (kept.Count < parts.Count ? $"; briše se {parts.Count - kept.Count}." : ".");
            return Change(CleaningChangeKind.Edited, description, newText);
        }

        var applied = block.Repairs.Where(r => r.Applied).ToList();
        if (applied.Count > 0 && !string.Equals(block.OriginalText, block.Text, StringComparison.Ordinal))
        {
            var description = string.Join(" ", applied.Select(r => $"Linija {r.Line}: {r.Description}"));
            return Change(CleaningChangeKind.Repaired, description, block.Text);
        }

        return null;
    }
}
