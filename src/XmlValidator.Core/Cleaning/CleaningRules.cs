using System.Globalization;

namespace XmlValidator.Core.Cleaning;

/// <summary>Kategorija fakture (PLAN.md, „Poslovna pravila čišćenja”).</summary>
public enum InvoiceCategory
{
    /// <summary>Ima bar jednu stavku sa ciljnom grupom (usluga SHMP); ostaje.</summary>
    KeepGroup,

    /// <summary>Nema ciljnu grupu, ali isti JMB ima pregled u SHMP (usluga pregleda u ciljnoj grupi); ostaje.</summary>
    KeepSameJmb,

    /// <summary>Nema nijednu stavku; kandidat (podrazumijevano Obriši).</summary>
    EmptyInvoice,

    /// <summary>Samo usluge drugih službi, a isti JMB nema pregled u SHMP (ili nema JMB); kandidat (podrazumijevano Obriši).</summary>
    NoGroupNoJmbMatch,

    /// <summary>Granica bloka nije pouzdana; mora ručna odluka.</summary>
    SuspiciousBlock,
}

/// <summary>Ulaz za klasifikaciju jednog bloka.</summary>
/// <param name="BlockId">Id bloka.</param>
/// <param name="StartLine">Početna linija (za poruke).</param>
/// <param name="Data">Pročitani podaci.</param>
/// <param name="SuspiciousReason">Razlog sumnje u granicu bloka, ili <c>null</c>.</param>
public sealed record ClassificationInput(int BlockId, int StartLine, InvoiceData Data, string? SuspiciousReason);

/// <summary>Rezultat klasifikacije jednog bloka.</summary>
/// <param name="Category">Kategorija.</param>
/// <param name="Reason">Razlog za izvještaj.</param>
/// <param name="Warnings">Upozorenja (npr. neuobičajen zapis ciljne grupe).</param>
public sealed record InvoiceClassification(InvoiceCategory Category, string Reason, IReadOnlyList<string> Warnings)
{
    /// <summary>Kandidat za brisanje (o njemu odlučuje akcija).</summary>
    public bool IsCandidate => Category is not (InvoiceCategory.KeepGroup or InvoiceCategory.KeepSameJmb);
}

/// <summary>Pravilo čišćenja: klasifikuje sve fakture fajla odjednom (pravila mogu zavisiti od drugih faktura).</summary>
public interface ICleaningRule
{
    /// <summary>Naziv pravila za izvještaj.</summary>
    string Name { get; }

    /// <summary>Klasifikacija, istim redom kao ulaz.</summary>
    IReadOnlyList<InvoiceClassification> Classify(IReadOnlyList<ClassificationInput> invoices);
}

/// <summary>Vrijednost atributa <c>usluga-grupa</c> (XSD tip <c>xs:unsignedShort</c>).</summary>
public static class ServiceGroup
{
    /// <summary>
    /// Čita vrijednost po pravilima XSD-a: prazan prostor oko vrijednosti se zanemaruje (<c>collapse</c>),
    /// dozvoljen je znak <c>+</c> i vodeće nule; vrijednost mora biti 0–65535.
    /// </summary>
    public static bool TryParse(string? raw, out ushort value)
    {
        value = 0;
        if (raw is null)
        {
            return false;
        }

        var text = raw.AsSpan().Trim([' ', '\t', '\r', '\n']);
        if (text.StartsWith("+"))
        {
            text = text[1..];
        }

        if (text.IsEmpty)
        {
            return false;
        }

        foreach (var c in text)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        text = text.TrimStart('0');
        if (text.Length > 5)
        {
            return false;
        }

        var number = text.IsEmpty ? 0 : int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
        if (number > ushort.MaxValue)
        {
            return false;
        }

        value = (ushort)number;
        return true;
    }

    /// <summary>Uobičajen zapis grupe u IZIS fajlovima: najmanje tri cifre, sa vodećim nulama (<c>001</c>, <c>100</c>).</summary>
    public static string UsualNotation(ushort value) => value.ToString("D3", CultureInfo.InvariantCulture);
}

/// <summary>
/// Pravilo Službe hitne medicinske pomoći (SHMP), po <c>legacy/clean.py</c> sa ispravkama iz PLAN.md.
/// FZO prihvata usluge SHMP i usluge drugih službi za pacijenta koji je pregledan u SHMP: faktura sa uslugom
/// ciljne grupe uvijek ostaje, a faktura samo iz drugih službi ostaje ako isti JMB ima uslugu pregleda
/// (<see cref="ExamService"/>, podrazumijevano 183) u ciljnoj grupi. <c>clean.py</c> je za isti JMB računao bilo
/// koju uslugu ciljne grupe (<see cref="ExamService"/> = <c>null</c>).
/// Ciljna grupa se poredi po vrijednosti (odluka 9); neuobičajen zapis ciljne grupe (odluka 16) i
/// neispravna vrijednost (odluka 17) daju upozorenje.
/// </summary>
public sealed class ShmpCleaningRule : ICleaningRule
{
    public const ushort DefaultTargetGroup = 100;

    /// <summary>Usluga pregleda u SHMP (<c>usluga-oznaka</c>).</summary>
    public const ushort DefaultExamService = 183;

    public ShmpCleaningRule(ushort targetGroup = DefaultTargetGroup, ushort? examService = DefaultExamService)
    {
        TargetGroup = targetGroup;
        ExamService = examService;
    }

    public ushort TargetGroup { get; }

    /// <summary>Usluga pregleda koja povezuje fakture istog JMB-a; <c>null</c> = bilo koja usluga ciljne grupe.</summary>
    public ushort? ExamService { get; }

    public string Name => ExamService is { } exam
        ? $"SHMP (grupa {TargetGroup}, pregled {exam})"
        : $"SHMP (grupa {TargetGroup}, bilo koja usluga)";

    public IReadOnlyList<InvoiceClassification> Classify(IReadOnlyList<ClassificationInput> invoices)
    {
        ArgumentNullException.ThrowIfNull(invoices);
        var usual = ServiceGroup.UsualNotation(TargetGroup);
        var examText = ExamService is { } code
            ? $"pregled u SHMP (usluga {code}, grupa {TargetGroup})"
            : $"uslugu SHMP (grupa {TargetGroup})";

        // Prvi prolaz: ciljna grupa po fakturi i JMB-ovi sa pregledom u SHMP (prva takva faktura po JMB-u;
        // pouzdani blokovi imaju prednost pred sumnjivim).
        var hasTarget = new bool[invoices.Count];
        var warnings = new List<string>[invoices.Count];
        var firstWithExam = new Dictionary<string, ClassificationInput>(StringComparer.Ordinal);
        for (var i = 0; i < invoices.Count; i++)
        {
            var invoice = invoices[i];
            var data = invoice.Data;
            warnings[i] = [];
            var hasExam = false;
            for (var g = 0; g < data.Groups.Count; g++)
            {
                var raw = data.Groups[g];
                if (!ServiceGroup.TryParse(raw, out var value))
                {
                    warnings[i].Add($"Neispravna vrijednost usluga-grupa „{raw}” (nije cijeli broj 0–65535); stavka se ne računa kao grupa {TargetGroup}.");
                    continue;
                }

                if (value != TargetGroup)
                {
                    continue;
                }

                hasTarget[i] = true;
                if (!string.Equals(raw, usual, StringComparison.Ordinal))
                {
                    warnings[i].Add($"usluga-grupa „{raw}” ima vrijednost {TargetGroup}, ali nije zapisana kao „{usual}”; računa se kao grupa {TargetGroup} (clean.py je ne bi računao).");
                }

                if (ExamService is not { } examCode)
                {
                    hasExam = true;
                }
                else if (g < data.Services.Count && data.Services[g] is { } service)
                {
                    if (!ServiceGroup.TryParse(service, out var serviceValue))
                    {
                        warnings[i].Add($"Neispravna vrijednost usluga-oznaka „{service}” u grupi {TargetGroup}; ne računa se kao pregled {examCode}.");
                    }
                    else if (serviceValue == examCode)
                    {
                        hasExam = true;
                    }
                }
            }

            if (hasExam && data.ItemCount > 0 && data.EffectiveJmb is { } jmb
                && (!firstWithExam.TryGetValue(jmb, out var existing) || (existing.SuspiciousReason is not null && invoice.SuspiciousReason is null)))
            {
                firstWithExam[jmb] = invoice;
            }
        }

        // Drugi prolaz: odluka po fakturi.
        var result = new InvoiceClassification[invoices.Count];
        for (var i = 0; i < invoices.Count; i++)
        {
            var invoice = invoices[i];
            var data = invoice.Data;
            if (invoice.SuspiciousReason is not null)
            {
                result[i] = new InvoiceClassification(InvoiceCategory.SuspiciousBlock, invoice.SuspiciousReason, warnings[i]);
            }
            else if (data.ItemCount == 0)
            {
                result[i] = new InvoiceClassification(InvoiceCategory.EmptyInvoice, "Prazna/polomljena faktura (nema stavki).", warnings[i]);
            }
            else if (hasTarget[i])
            {
                result[i] = new InvoiceClassification(InvoiceCategory.KeepGroup, $"Ima uslugu Službe hitne medicinske pomoći (grupa {TargetGroup}).", warnings[i]);
            }
            else if (data.EffectiveJmb is { } jmb && firstWithExam.TryGetValue(jmb, out var source))
            {
                var reason = $"Samo usluge drugih službi ({OtherGroups(data)}), ali isti pacijent (JMB) ima {examText}: {Describe(source)}.";
                if (source.SuspiciousReason is not null)
                {
                    warnings[i].Add($"Faktura ostaje samo zbog sumnjivog bloka na liniji {source.StartLine}; provjerite je poslije odluke o tom bloku.");
                }

                result[i] = new InvoiceClassification(InvoiceCategory.KeepSameJmb, reason, warnings[i]);
            }
            else
            {
                var reason = data.EffectiveJmb is null
                    ? $"Samo usluge drugih službi ({OtherGroups(data)}), a faktura nema osiguranik-jmb, pa se ne može povezati sa pregledom u SHMP."
                    : $"Samo usluge drugih službi ({OtherGroups(data)}); pacijent (JMB) nema {examText} ni na jednoj fakturi.";
                result[i] = new InvoiceClassification(InvoiceCategory.NoGroupNoJmbMatch, reason, warnings[i]);
            }
        }

        return result;
    }

    private static string OtherGroups(InvoiceData data) => "grupe " + CleaningText.Groups(data.Groups, data.ItemCount);

    private static string Describe(ClassificationInput invoice) =>
        invoice.Data.Number is { Length: > 0 } number
            ? $"faktura {number}, linija {invoice.StartLine}"
            : $"faktura na liniji {invoice.StartLine}";
}
