namespace XmlValidator.Core.Cleaning;

/// <summary>
/// Stanje jedne fakture u sesiji (snimak; poslije svake promjene sesija daje nove snimke). Obično je
/// jedna faktura = jedan blok iz ulaza; kad ručna izmjena podijeli blok na više faktura (npr. sumnjiv
/// blok kojem je dodat <c>&lt;/faktura&gt;</c>), svaki dio ima svoje stanje.
/// </summary>
/// <param name="Id">Jedinstven id u sesiji. Za prvi (ili jedini) dio bloka jednak je <see cref="InvoiceBlock.Id"/>.</param>
/// <param name="Block">Blok iz ulaza.</param>
/// <param name="PartIndex">Redni broj dijela bloka (od 0).</param>
/// <param name="PartCount">Broj dijelova bloka (1 ako blok nije podijeljen).</param>
/// <param name="Separator">Tekst ispred ovog dijela unutar uređenog bloka (za prvi dio prazan).</param>
/// <param name="Data">Trenutni podaci (iz uređenog teksta ako je faktura uređena).</param>
/// <param name="Classification">Trenutna klasifikacija.</param>
/// <param name="OriginalData">Podaci bloka prije ručne izmjene.</param>
/// <param name="OriginalClassification">Klasifikacija bloka prije ručne izmjene.</param>
/// <param name="EditedText">Uređen tekst, ili <c>null</c>.</param>
/// <param name="UserAction">Akcija koju je korisnik izričito izabrao, ili <c>null</c>.</param>
/// <param name="Action">Akcija koja važi (izabrana ili podrazumijevana); <c>null</c> za fakture koje ostaju i za sumnjiv blok bez odluke.</param>
/// <param name="Disposition">Ishod u izlazu.</param>
public sealed record BlockState(
    int Id,
    InvoiceBlock Block,
    int PartIndex,
    int PartCount,
    string Separator,
    InvoiceData Data,
    InvoiceClassification Classification,
    InvoiceData OriginalData,
    InvoiceClassification OriginalClassification,
    string? EditedText,
    CandidateAction? UserAction,
    CandidateAction? Action,
    BlockDisposition Disposition)
{
    public bool IsEdited => EditedText is not null;

    /// <summary>Tekst koji ide u izlaz ako faktura ostaje.</summary>
    public string OutputText => EditedText ?? Block.Text;

    /// <summary>Upozorenja: klasifikacija, nepopravljeni nedostaci i neispravan XML.</summary>
    public IEnumerable<string> Warnings
    {
        get
        {
            foreach (var warning in Classification.Warnings)
            {
                yield return warning;
            }

            if (!IsEdited)
            {
                foreach (var note in Block.Repairs.Where(r => !r.Applied))
                {
                    yield return $"Linija {note.Line}: {note.Description}";
                }
            }

            if (Data.ParseError is { } error && Classification.Category != InvoiceCategory.SuspiciousBlock)
            {
                yield return $"Faktura nije ispravan XML ({error}); atributi su pročitani iz teksta.";
            }
        }
    }
}

/// <summary>Rezultat ručne izmjene.</summary>
/// <param name="Succeeded">Da li je izmjena primijenjena.</param>
/// <param name="Error">Razlog odbijanja (za korisnika), ili <c>null</c>.</param>
/// <param name="States">Stanja faktura nastalih izmjenom (jedna ili više); ako izmjena nije primijenjena, nepromijenjeno stanje.</param>
public sealed record EditResult(bool Succeeded, string? Error, IReadOnlyList<BlockState> States)
{
    public BlockState State => States[0];
}

/// <summary>
/// Sesija čišćenja: dokument, klasifikacija i akcije po fakturi. Nije bezbjedna za istovremeni pristup
/// iz više niti (UI je koristi sa jedne niti; <see cref="BuildOutput"/> se može pozvati i iz pozadine
/// kad se sesija u međuvremenu ne mijenja).
/// </summary>
public sealed class CleaningSession
{
    private readonly InvoiceData[] _originalData;
    private readonly InvoiceClassification[] _originalClassification;
    private readonly List<Entry> _entries;
    private readonly Dictionary<int, Entry> _byId;
    private BlockState[]? _states;
    private int _nextId;

    private CleaningSession(InvoiceDocument document, CleaningOptions options, ICleaningRule rule, InvoiceData[] data)
    {
        Document = document;
        Options = options;
        Rule = rule;
        _originalData = data;
        _entries = [.. document.Blocks.Select(b => new Entry(b.Id, b, string.Empty, null, data[b.Id]))];
        _byId = _entries.ToDictionary(e => e.Id);
        _nextId = _entries.Count;
        Classify();
        _originalClassification = [.. _entries.Select(e => e.Classification)];
    }

    public InvoiceDocument Document { get; }

    public CleaningOptions Options { get; private set; }

    public ICleaningRule Rule { get; }

    /// <summary>Raste sa svakom promjenom (za osvježavanje prikaza).</summary>
    public int Version { get; private set; }

    /// <summary>Stanje svih faktura, redom kao u izlazu.</summary>
    public IReadOnlyList<BlockState> Blocks => _states ??= BuildStates();

    /// <summary>Fakture koje čekaju odluku korisnika.</summary>
    public IReadOnlyList<BlockState> UnresolvedBlocks => [.. Blocks.Where(b => b.Disposition == BlockDisposition.Unresolved)];

    /// <summary>Izlaz se može napraviti kad nijedna faktura ne čeka odluku.</summary>
    public bool CanBuildOutput => Blocks.All(b => b.Disposition != BlockDisposition.Unresolved);

    /// <summary>Pravi sesiju nad dokumentom: čita atribute svih blokova i klasifikuje ih.</summary>
    public static CleaningSession Create(
        InvoiceDocument document,
        CleaningOptions? options = null,
        ICleaningRule? rule = null,
        IProgress<int>? blocksRead = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= new CleaningOptions();
        rule ??= new ShmpCleaningRule(options.TargetGroup, options.ExamService);
        var data = new InvoiceData[document.Blocks.Count];
        for (var i = 0; i < data.Length; i++)
        {
            if (i % 64 == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                blocksRead?.Report(i);
            }

            data[i] = InvoiceAttributeReader.Read(document.Blocks[i].Text);
        }

        blocksRead?.Report(data.Length);
        return new CleaningSession(document, options, rule, data);
    }

    /// <summary>Stanje fakture po id-u.</summary>
    public BlockState Get(int id)
    {
        var entry = Find(id);
        return Blocks[_entries.IndexOf(entry)];
    }

    /// <summary>Postavlja akciju za kandidata.</summary>
    /// <exception cref="InvalidOperationException">Faktura nije kandidat (ostaje po pravilu).</exception>
    public void SetAction(int id, CandidateAction action) => SetActions([Find(id)], action);

    /// <summary>Postavlja akciju za više kandidata odjednom; fakture koje nisu kandidati se preskaču.</summary>
    /// <returns>Broj faktura kojima je akcija postavljena.</returns>
    public int SetActionForMany(IEnumerable<int> ids, CandidateAction action)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var entries = ids.Select(Find).Distinct().Where(IsCandidate).ToArray();
        SetActions(entries, action);
        return entries.Length;
    }

    /// <summary>Vraća fakturu na podrazumijevanu akciju za njenu kategoriju.</summary>
    public void ClearAction(int id)
    {
        Find(id).UserAction = null;
        Changed();
    }

    /// <summary>Mijenja podrazumijevanu akciju za kategoriju (važi za fakture bez izričito izabrane akcije).</summary>
    public void SetDefaultAction(InvoiceCategory category, CandidateAction? action)
    {
        Options = category switch
        {
            InvoiceCategory.EmptyInvoice => Options with { EmptyInvoiceAction = action },
            InvoiceCategory.NoGroupNoJmbMatch => Options with { NoGroupNoJmbMatchAction = action },
            _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Podrazumijevana akcija postoji samo za prazne fakture i fakture bez pregleda u SHMP."),
        };
        Changed();
    }

    /// <summary>
    /// Ručna izmjena. Tekst mora biti ispravan XML fragment od jedne ili više faktura (više faktura dijeli
    /// blok, npr. kad se sumnjivom bloku doda <c>&lt;/faktura&gt;</c>) i mora se moći zapisati u kodiranju
    /// fajla; završeci linija se usklađuju sa fajlom, a prazan prostor na početku i kraju se uklanja.
    /// Poslije izmjene se sve fakture ponovo klasifikuju (skup JMB-ova se mijenja), a uređene fakture
    /// dobijaju akciju Uredi.
    /// </summary>
    public EditResult ApplyEdit(int id, string newText)
    {
        ArgumentNullException.ThrowIfNull(newText);
        var entry = Find(id);
        var text = DocumentTextDecoder.NormalizeLineEndings(newText, Document.Format.NewLine).Trim();
        if (text.Length == 0)
        {
            return Rejected(entry, "Tekst je prazan. Za uklanjanje fakture izaberite akciju Obriši.");
        }

        if (InvoiceAttributeReader.ValidateFragment(text, out _) is { } error)
        {
            return Rejected(entry, $"Izmjena nije ispravan XML fragment faktura: {error}");
        }

        if (DocumentTextDecoder.FindUnencodableCharacter(text, Document.Format) is { } character)
        {
            return Rejected(entry, $"Znak „{character}” ne postoji u kodiranju fajla ({Document.Format.EncodingName}).");
        }

        var parts = InvoiceFragmentSplitter.Split(text);
        var created = new List<Entry>(parts.Count);
        for (var i = 0; i < parts.Count; i++)
        {
            var (separator, partText) = parts[i];
            created.Add(new Entry(
                i == 0 ? entry.Id : _nextId++,
                entry.Block,
                i == 0 ? entry.Separator : separator,
                partText,
                InvoiceAttributeReader.Read(partText))
            {
                UserAction = CandidateAction.Edit,
            });
        }

        var index = _entries.IndexOf(entry);
        _entries.RemoveAt(index);
        _entries.InsertRange(index, created);
        _byId.Remove(entry.Id);
        created.ForEach(e => _byId[e.Id] = e);
        Reclassify();
        return new EditResult(true, null, [.. created.Select(e => Get(e.Id))]);
    }

    /// <summary>
    /// Poništava ručne izmjene bloka kojem faktura pripada (svih njegovih dijelova): vraća tekst iz ulaza,
    /// sa popravkama, i ponovo klasifikuje.
    /// </summary>
    public void RevertEdit(int id)
    {
        var block = Find(id).Block;
        var parts = _entries.Where(e => e.Block.Id == block.Id).ToList();
        if (parts.All(e => e.EditedText is null))
        {
            return;
        }

        var first = parts[0];
        var index = _entries.IndexOf(first);
        _entries.RemoveRange(index, parts.Count);
        parts.ForEach(e => _byId.Remove(e.Id));
        var restored = new Entry(block.Id, block, string.Empty, null, _originalData[block.Id])
        {
            // Akcija izabrana za jedan dio podijeljenog bloka ne važi za cijeli blok.
            UserAction = parts.Count == 1 && first.UserAction != CandidateAction.Edit ? first.UserAction : null,
        };
        _entries.Insert(index, restored);
        _byId[restored.Id] = restored;
        Reclassify();
    }

    /// <summary>Pravi izlaz. Tekst zadržanih faktura i razmaci između njih ostaju isti kao u ulazu.</summary>
    /// <exception cref="InvalidOperationException">Neka faktura čeka odluku (<see cref="UnresolvedBlocks"/>).</exception>
    public CleaningOutput BuildOutput()
    {
        var blocks = Blocks;
        var unresolved = blocks.Where(b => b.Disposition == BlockDisposition.Unresolved).ToList();
        if (unresolved.Count > 0)
        {
            throw new InvalidOperationException(
                $"Izlaz ne može da se napravi: {unresolved.Count} faktura čeka odluku (prva na liniji {unresolved[0].Block.StartLine}).");
        }

        var text = CleaningOutputWriter.BuildText(Document, blocks);
        var bytes = DocumentTextDecoder.Encode(text, Document.Format);
        return new CleaningOutput(text, bytes, Document.Format, blocks);
    }

    private EditResult Rejected(Entry entry, string error) => new(false, error, [Get(entry.Id)]);

    private void SetActions(Entry[] entries, CandidateAction action)
    {
        foreach (var entry in entries)
        {
            if (!IsCandidate(entry))
            {
                throw new InvalidOperationException($"Faktura na liniji {entry.Block.StartLine} nije kandidat za brisanje; akcija se ne bira.");
            }
        }

        foreach (var entry in entries)
        {
            entry.UserAction = action;
        }

        Changed();
    }

    private static bool IsCandidate(Entry entry) => entry.Classification.IsCandidate;

    private Entry Find(int id) =>
        _byId.TryGetValue(id, out var entry) ? entry : throw new ArgumentOutOfRangeException(nameof(id), id, "Faktura sa tim id-om ne postoji u sesiji.");

    private void Reclassify()
    {
        Classify();
        Changed();
    }

    private void Changed()
    {
        _states = null;
        Version++;
    }

    private void Classify()
    {
        var inputs = new ClassificationInput[_entries.Count];
        for (var i = 0; i < inputs.Length; i++)
        {
            var entry = _entries[i];

            // Uređena faktura je provjerena kao ispravan XML, pa njena granica više nije sumnjiva.
            var suspicious = entry.EditedText is null ? entry.Block.SuspiciousReason : null;
            inputs[i] = new ClassificationInput(entry.Id, entry.Block.StartLine, entry.Data, suspicious);
        }

        var result = Rule.Classify(inputs);
        if (result.Count != inputs.Length)
        {
            throw new InvalidOperationException($"Pravilo „{Rule.Name}” je vratilo {result.Count} rezultata za {inputs.Length} faktura.");
        }

        for (var i = 0; i < result.Count; i++)
        {
            _entries[i].Classification = result[i];
        }
    }

    private BlockState[] BuildStates()
    {
        var partCounts = _entries.GroupBy(e => e.Block.Id).ToDictionary(g => g.Key, g => g.Count());
        var states = new BlockState[_entries.Count];
        var partIndex = 0;
        for (var i = 0; i < states.Length; i++)
        {
            var entry = _entries[i];
            partIndex = i > 0 && _entries[i - 1].Block.Id == entry.Block.Id ? partIndex + 1 : 0;
            var classification = entry.Classification;
            var action = classification.IsCandidate ? entry.UserAction ?? Options.DefaultActionFor(classification.Category) : null;
            var edited = entry.EditedText is not null;
            var disposition = !classification.IsCandidate
                ? edited ? BlockDisposition.Edited : BlockDisposition.Kept
                : action switch
                {
                    CandidateAction.Delete => BlockDisposition.Deleted,
                    CandidateAction.Skip => edited ? BlockDisposition.Edited : BlockDisposition.Skipped,
                    CandidateAction.Edit when edited => BlockDisposition.Edited,
                    _ => BlockDisposition.Unresolved,
                };

            states[i] = new BlockState(
                entry.Id,
                entry.Block,
                partIndex,
                partCounts[entry.Block.Id],
                entry.Separator,
                entry.Data,
                classification,
                _originalData[entry.Block.Id],
                _originalClassification[entry.Block.Id],
                entry.EditedText,
                entry.UserAction,
                action,
                disposition);
        }

        return states;
    }

    private sealed class Entry(int id, InvoiceBlock block, string separator, string? editedText, InvoiceData data)
    {
        public int Id { get; } = id;

        public InvoiceBlock Block { get; } = block;

        public string Separator { get; } = separator;

        public string? EditedText { get; } = editedText;

        public InvoiceData Data { get; } = data;

        public CandidateAction? UserAction { get; set; }

        public InvoiceClassification Classification { get; set; } = null!;
    }
}
