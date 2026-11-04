namespace XmlValidator.Core.Cleaning;

/// <summary>Akcija za fakturu koja je kandidat za brisanje.</summary>
public enum CandidateAction
{
    /// <summary>Blok se izbacuje iz izlaza.</summary>
    Delete,

    /// <summary>Blok ostaje u izlazu kakav jeste; u izvještaju „preskočeno”.</summary>
    Skip,

    /// <summary>Blok se ručno mijenja; u izlaz ide uređen tekst (dok izmjena nije primijenjena, blok čeka odluku).</summary>
    Edit,
}

/// <summary>Konačan ishod bloka u izlazu.</summary>
public enum BlockDisposition
{
    /// <summary>Ostaje (nije kandidat).</summary>
    Kept,

    /// <summary>Obrisan.</summary>
    Deleted,

    /// <summary>Kandidat koji ostaje netaknut (akcija Preskoči).</summary>
    Skipped,

    /// <summary>Ostaje sa ručnom izmjenom.</summary>
    Edited,

    /// <summary>Čeka odluku korisnika (sumnjiv blok bez akcije, ili Uredi bez primijenjene izmjene); izlaz se ne može napraviti.</summary>
    Unresolved,
}

/// <summary>Podešavanja čišćenja.</summary>
/// <param name="TargetGroup">Grupa usluga Službe hitne medicinske pomoći (SHMP), podrazumijevano 100.</param>
/// <param name="RepairBrackets">Automatske popravke zagrada.</param>
/// <param name="EmptyInvoiceAction">Podrazumijevana akcija za <see cref="InvoiceCategory.EmptyInvoice"/>; <c>null</c> = „Prikaži” (svaka faktura čeka pojedinačnu odluku).</param>
/// <param name="NoGroupNoJmbMatchAction">Podrazumijevana akcija za <see cref="InvoiceCategory.NoGroupNoJmbMatch"/>; <c>null</c> = „Prikaži”.</param>
/// <param name="ExamService">
/// Usluga pregleda u SHMP (<c>usluga-oznaka</c>, podrazumijevano 183): faktura samo iz drugih službi ostaje ako isti
/// JMB ima ovu uslugu u ciljnoj grupi. <c>null</c> = bilo koja usluga ciljne grupe (kao <c>clean.py</c>).
/// </param>
public sealed record CleaningOptions(
    ushort TargetGroup = ShmpCleaningRule.DefaultTargetGroup,
    bool RepairBrackets = true,
    CandidateAction? EmptyInvoiceAction = CandidateAction.Delete,
    CandidateAction? NoGroupNoJmbMatchAction = CandidateAction.Delete,
    ushort? ExamService = ShmpCleaningRule.DefaultExamService)
{
    /// <summary>
    /// Isti rezultat kao <c>clean.py</c>: Obriši za obje kategorije kandidata, uz popravke, a za isti JMB se
    /// računa bilo koja usluga ciljne grupe (bez uslova pregleda).
    /// </summary>
    public static CleaningOptions ScriptCompatible { get; } = new(ExamService: null);

    /// <summary>Podrazumijevana akcija za kategoriju; <c>null</c> za sumnjiv blok i „Prikaži” (mora ručna odluka) i za fakture koje ostaju.</summary>
    public CandidateAction? DefaultActionFor(InvoiceCategory category) => category switch
    {
        InvoiceCategory.EmptyInvoice => EmptyInvoiceAction,
        InvoiceCategory.NoGroupNoJmbMatch => NoGroupNoJmbMatchAction,
        _ => null,
    };
}
