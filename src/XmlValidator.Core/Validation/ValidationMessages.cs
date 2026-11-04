using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Schema;

namespace XmlValidator.Core.Validation;

/// <summary>Gdje je nađen problem: element i podaci za opis (broj fakture, redni broj stavke).</summary>
/// <param name="ElementName">Lokalno ime elementa na kojem je nalaz (npr. <c>stavka</c>), ili <c>null</c>.</param>
/// <param name="InvoiceNumber">Broj fakture (atribut <c>broj</c>) u kojoj je nalaz, ili <c>null</c>.</param>
/// <param name="ItemNumber">Redni broj stavke (atribut <c>redni-broj</c>), ili <c>null</c>.</param>
/// <param name="ElementType">Tip elementa iz šeme (za opis atributa koji nedostaju), ili <c>null</c>.</param>
public sealed record ValidationContext(string? ElementName, string? InvoiceNumber, string? ItemNumber, XmlSchemaType? ElementType = null);

/// <summary>
/// Pretvara tehničke poruke .NET validatora (na engleskom) u kratka objašnjenja na srpskom: gdje je problem,
/// šta nije u redu i šta treba upisati ili ispraviti. Dozvoljene vrijednosti, granice i tip se čitaju iz
/// same šeme (<see cref="XmlSchemaException.SourceSchemaObject"/>), pa poruke važe i za uvezene verzije šeme.
/// Kad poruka nije prepoznata, vraća se original sa kratkim uvodom (ništa se ne gubi).
/// </summary>
public static partial class ValidationMessages
{
    private const string Namespace = @"(?: in namespace '[^']*')?";

    /// <summary>Nazivi polja za korisnika (atributi iz <c>fakture-zdravstva.xsd</c>).</summary>
    private static readonly Dictionary<string, string> FieldNames = new(StringComparer.Ordinal)
    {
        ["ustanova-sifra"] = "šifra ustanove",
        ["ustanova-naziv"] = "naziv ustanove",
        ["vrsta-fakture"] = "vrsta fakture",
        ["jif"] = "JIF",
        ["xml-verzija"] = "verzija XML-a",
        ["tip"] = "tip fakture",
        ["broj"] = "broj fakture",
        ["datum"] = "datum fakture",
        ["rok-placanja"] = "rok plaćanja",
        ["valuta"] = "valuta",
        ["iznos"] = "iznos",
        ["participacija-placa"] = "da li se plaća participacija",
        ["participacija-iznos"] = "iznos participacije",
        ["ugovor-broj"] = "broj ugovora",
        ["projekat-sifra"] = "šifra projekta",
        ["osiguranik-jmb"] = "JMB osiguranika",
        ["osiguranik-ime-prezime"] = "ime i prezime osiguranika",
        ["lijecenje-period-od"] = "početak liječenja",
        ["lijecenje-period-do"] = "kraj liječenja",
        ["lijecenje-prvi-dan"] = "prvi dan liječenja",
        ["dijagnoza-oznaka"] = "šifra dijagnoze",
        ["ustanova-uputilac-sifra"] = "šifra ustanove uputioca",
        ["ustanova-uputilac-naziv"] = "naziv ustanove uputioca",
        ["uputnica-tip"] = "tip uputnice",
        ["izvrsioc-klinika-sifra"] = "šifra klinike izvršioca",
        ["izvrsioc-klinika-naziv"] = "naziv klinike izvršioca",
        ["ljekar-oznaka"] = "oznaka ljekara",
        ["ljekar-ime-prezime"] = "ime i prezime ljekara",
        ["rjesenje-broj"] = "broj rješenja",
        ["ljekar-prijedlog"] = "ljekar koji predlaže",
        ["ustanova-prijedlog"] = "ustanova koja predlaže",
        ["datum-prijedloga"] = "datum prijedloga",
        ["uputnica-id"] = "ID uputnice",
        ["regresna-faktura"] = "regresna faktura",
        ["redni-broj"] = "redni broj stavke",
        ["usluga-nivo"] = "nivo usluge",
        ["usluga-grupa"] = "grupa usluge",
        ["usluga-oznaka"] = "oznaka (šifra) usluge",
        ["usluga-naziv"] = "naziv usluge",
        ["opis"] = "opis",
        ["kolicina"] = "količina",
        ["broj-slucajeva"] = "broj slučajeva",
        ["rjesenje"] = "rješenje",
        ["datum-preuzimanja"] = "datum preuzimanja",
        ["ljekar-sifra"] = "šifra ljekara",
        ["lijek-oznaka"] = "oznaka lijeka",
        ["lijek-lista-oznaka"] = "lista lijeka",
        ["lijek-indikacija-oznaka"] = "oznaka indikacije",
        ["recept-serijski-broj"] = "serijski broj recepta",
        ["recept-datum-izdavanja"] = "datum izdavanja recepta",
        ["recept-ustanova-sifra"] = "šifra ustanove na receptu",
        ["recept-ljekar-oznaka"] = "oznaka ljekara na receptu",
        ["recept-ljekar-ime-prezime"] = "ljekar na receptu",
        ["recept-dijagnoza-oznaka"] = "šifra dijagnoze na receptu",
        ["datum-izdavanja-lijeka"] = "datum izdavanja lijeka",
        ["lijek-zdrustanova"] = "zdravstvena ustanova lijeka",
        ["racun-broj"] = "broj računa",
        ["racun-datum"] = "datum računa",
        ["racun-stavka-redni-broj"] = "redni broj stavke računa",
        ["napomena"] = "napomena",
    };

    /// <summary>Šta se upisuje u polje (akuzativ): „Upišite broj nivoa usluge (…)”.</summary>
    private static readonly Dictionary<string, string> ValueNouns = new(StringComparer.Ordinal)
    {
        ["ustanova-sifra"] = "šifru ustanove",
        ["vrsta-fakture"] = "broj vrste fakture",
        ["jif"] = "JIF",
        ["xml-verzija"] = "verziju XML-a",
        ["tip"] = "tip fakture",
        ["broj"] = "broj fakture",
        ["datum"] = "datum fakture",
        ["rok-placanja"] = "rok plaćanja",
        ["valuta"] = "oznaku valute",
        ["iznos"] = "iznos",
        ["participacija-placa"] = "oznaku da li se plaća participacija",
        ["participacija-iznos"] = "iznos participacije",
        ["projekat-sifra"] = "šifru projekta",
        ["osiguranik-jmb"] = "JMB osiguranika",
        ["lijecenje-period-od"] = "datum početka liječenja",
        ["lijecenje-period-do"] = "datum kraja liječenja",
        ["lijecenje-prvi-dan"] = "datum prvog dana liječenja",
        ["dijagnoza-oznaka"] = "šifru dijagnoze",
        ["ustanova-uputilac-sifra"] = "šifru ustanove uputioca",
        ["uputnica-tip"] = "tip uputnice",
        ["izvrsioc-klinika-sifra"] = "šifru klinike izvršioca",
        ["ljekar-oznaka"] = "oznaku ljekara",
        ["datum-prijedloga"] = "datum prijedloga",
        ["uputnica-id"] = "ID uputnice",
        ["redni-broj"] = "redni broj stavke",
        ["usluga-nivo"] = "broj nivoa usluge",
        ["usluga-grupa"] = "broj grupe usluge",
        ["usluga-oznaka"] = "broj (šifru) usluge",
        ["kolicina"] = "količinu",
        ["broj-slucajeva"] = "broj slučajeva",
        ["datum-preuzimanja"] = "datum preuzimanja",
        ["ljekar-sifra"] = "šifru ljekara",
        ["lijek-oznaka"] = "oznaku lijeka",
        ["lijek-lista-oznaka"] = "listu lijeka",
        ["recept-serijski-broj"] = "serijski broj recepta",
        ["recept-datum-izdavanja"] = "datum izdavanja recepta",
        ["recept-ustanova-sifra"] = "šifru ustanove na receptu",
        ["recept-ljekar-oznaka"] = "oznaku ljekara na receptu",
        ["recept-dijagnoza-oznaka"] = "šifru dijagnoze na receptu",
        ["datum-izdavanja-lijeka"] = "datum izdavanja lijeka",
        ["racun-datum"] = "datum računa",
        ["racun-stavka-redni-broj"] = "redni broj stavke računa",
    };

    /// <summary>Opisi poznatih obrazaca (<c>xs:pattern</c>) iz šeme.</summary>
    private static readonly Dictionary<string, string> PatternDescriptions = new(StringComparer.Ordinal)
    {
        ["[0-9]{13}"] = "tačno 13 cifara",
        ["[0-9][0-9][0-9][0-9][0-9]"] = "tačno 5 cifara",
        ["[0-9]+"] = "samo cifre",
        ["[a-zA-Z0-9]+"] = "samo slova i cifre",
        [@"(\s*[^\s]\s*)+"] = "tekst koji nije prazan",
        ["[A-Z]([0-9])+([.])*([0-9]+)"] = "šifru po MKB-10 (veliko slovo pa cifre, npr. J06.9 ili I10)",
    };

    /// <summary>Prevodi grešku ili upozorenje validacije prema šemi.</summary>
    public static string FromSchema(XmlSchemaException exception, ValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(context);
        var message = StripPosition(exception.Message);
        var where = Where(context);

        if (InvalidValue().Match(message) is { Success: true } invalid)
        {
            var name = invalid.Groups["name"].Value;
            var isAttribute = invalid.Groups["kind"].Value == "attribute";
            var value = invalid.Groups["value"].Value;
            var simpleType = SimpleTypeOf(exception.SourceSchemaObject, name, context);
            var expected = What(name, Describe(simpleType));
            var required = IsRequired(exception.SourceSchemaObject, name, context);
            var field = isAttribute ? Field(name) : $"element <{name}>";

            if (string.IsNullOrWhiteSpace(value))
            {
                var fix = $"Upišite {expected}";
                if (isAttribute && !required)
                {
                    fix += $", ili obrišite polje {Quote(name)} jer nije obavezno";
                }
                else if (isAttribute && IsItem(context.ElementName))
                {
                    fix += ", ili obrišite cijeli red stavke ako ta usluga ne treba da bude u fakturi";
                }

                return $"{where}: {field} je prazno. {fix}.";
            }

            var reason = Reason(invalid.Groups["detail"].Value, value, simpleType);
            return $"{where}: {field} ima vrijednost „{value}” koja {reason}. Upišite {expected}.";
        }

        if (MissingAttribute().Match(message) is { Success: true } missing)
        {
            var name = missing.Groups["name"].Value;
            var expected = What(name, Describe(SimpleTypeOf(exception.SourceSchemaObject, name, context)));
            return $"{where}: nedostaje obavezno {Field(name)}. Dodajte ga u tag ({name}=\"…\") i upišite {expected}.";
        }

        if (UndeclaredAttribute().Match(message) is { Success: true } undeclared)
        {
            var name = undeclared.Groups["name"].Value;
            return $"{where}: polje {Quote(name)} ne postoji u šemi. Provjerite da li je naziv tačno napisan, "
                + "ili ga obrišite.";
        }

        if (InvalidChild().Match(message) is { Success: true } child)
        {
            var parent = child.Groups["parent"].Value;
            where = Where(context with { ElementName = parent });
            var name = child.Groups["child"].Value;
            var expectedList = child.Groups["expected"].Success ? Elements(child.Groups["expected"].Value) : null;
            if (parent == "fakture-zdravstva" && expectedList == "<opsti-podaci>")
            {
                return "Na početku fajla, prije prve fakture, nedostaje element <opsti-podaci> (podaci o ustanovi, vrsta fakture, JIF). "
                    + "Dodajte ga ili provjerite da nije pogrešno napisan.";
            }

            if (parent == "faktura" && IsItem(name) && expectedList is not null)
            {
                return $"{where}: faktura sadrži različite vrste stavki (<{name}> uz drugu vrstu). Jedna faktura smije imati "
                    + $"samo jednu vrstu stavki: {expectedList}. Premjestite stavku u posebnu fakturu ili je obrišite.";
            }

            return expectedList is null
                ? $"{where}: element <{name}> nije dozvoljen unutar <{parent}>. Provjerite naziv elementa ili ga obrišite."
                : $"{where}: element <{name}> nije dozvoljen na ovom mjestu unutar <{parent}>. Ovdje se očekuje: {expectedList}. "
                  + "Provjerite naziv i redoslijed elemenata.";
        }

        if (IncompleteContent().Match(message) is { Success: true } incomplete)
        {
            var element = incomplete.Groups["element"].Value;
            var expectedList = incomplete.Groups["expected"].Success ? Elements(incomplete.Groups["expected"].Value) : "još elemenata";
            return element == "faktura"
                ? $"{where}: faktura nema nijednu stavku. Dodajte stavke ili obrišite cijelu fakturu."
                : $"{where}: element <{element}> nije potpun, nedostaje: {expectedList}.";
        }

        if (UndeclaredElement().Match(message) is { Success: true } element2)
        {
            return $"{where}: element <{element2.Groups["element"].Value}> ne postoji u šemi. Provjerite da li je naziv tačno "
                + "napisan, ili ga obrišite.";
        }

        if (TextNotAllowed().Match(message) is { Success: true } text)
        {
            return $"{where}: element <{text.Groups["element"].Value}> ne smije da sadrži tekst, samo druge elemente i atribute. "
                + "Obrišite tekst (često je to zalutao znak između tagova).";
        }

        return $"{where}: sadržaj ne odgovara šemi. Tehnički opis: {message}";
    }

    /// <summary>Prevodi grešku u ispravnosti XML-a (fajl se ne može ni pročitati kao XML).</summary>
    public static string FromXml(XmlException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var message = StripPosition(exception.Message);

        if (UnexpectedToken().Match(message) is { Success: true } token)
        {
            var found = token.Groups["found"].Value;
            var expected = token.Groups["expected"].Value;
            return $"XML nije ispravan: na ovom mjestu se očekuje {Token(expected)}, a nađen je {Token(found)}. "
                + "Najčešće nedostaje navodnik oko vrijednosti, znak = između naziva i vrijednosti, ili > na kraju taga.";
        }

        if (TagMismatch().Match(message) is { Success: true } mismatch)
        {
            var start = mismatch.Groups["start"].Value;
            var end = mismatch.Groups["end"].Value;
            return $"XML nije ispravan: zatvarajući tag </{end}> ne odgovara otvorenom tagu <{start}> sa linije "
                + $"{mismatch.Groups["line"].Value}. Vjerovatno nedostaje </{start}>, ili je naziv taga pogrešno napisan.";
        }

        if (UnexpectedEnd().Match(message) is { Success: true } end2)
        {
            var open = end2.Groups["open"].Success ? $" Nisu zatvoreni: {end2.Groups["open"].Value.TrimEnd('.')}." : string.Empty;
            return "XML nije ispravan: fajl se završava prije nego što su zatvoreni svi tagovi." + open
                + " Dodajte zatvarajuće tagove koji nedostaju (npr. </faktura> ili </fakture-zdravstva>) ili provjerite da fajl nije skraćen.";
        }

        if (NameStart().Match(message) is { Success: true } name)
        {
            return $"XML nije ispravan: naziv taga ili polja ne može početi sa {Token(name.Groups["char"].Value)}. "
                + "Vjerovatno nedostaje > na kraju prethodnog taga, navodnik na kraju vrijednosti, ili postoji višak znaka <.";
        }

        if (InvalidAttributeChar().Match(message) is { Success: true } attributeChar)
        {
            return $"XML nije ispravan: {Token(attributeChar.Groups["char"].Value)} nije dozvoljen u vrijednosti polja. "
                + "Najčešće vrijednost nije zatvorena navodnikom.";
        }

        if (InvalidCharacter().Match(message) is { Success: true } invalidChar)
        {
            return $"XML nije ispravan: fajl sadrži nedozvoljen znak ({invalidChar.Groups["hex"].Value}). Obrišite ga ili zamijenite.";
        }

        if (DuplicateAttribute().Match(message) is { Success: true } duplicate)
        {
            return $"XML nije ispravan: polje {Quote(duplicate.Groups["name"].Value)} je navedeno dva puta u istom tagu. "
                + "Obrišite jedno.";
        }

        return message switch
        {
            _ when message.StartsWith("There are multiple root elements", StringComparison.Ordinal) =>
                "XML nije ispravan: fajl ima više glavnih elemenata. Sve fakture moraju biti unutar jednog <fakture-zdravstva>; "
                + "provjerite da nema sadržaja poslije </fakture-zdravstva>.",
            _ when message.StartsWith("Data at the root level is invalid", StringComparison.Ordinal) =>
                "XML nije ispravan: prije ili poslije glavnog elementa postoji sadržaj koji nije XML (npr. tekst van tagova). "
                + "Obrišite ga.",
            _ when message.StartsWith("Root element is missing", StringComparison.Ordinal) =>
                "Fajl je prazan ili ne sadrži XML.",
            _ when message.StartsWith("An error occurred while parsing EntityName", StringComparison.Ordinal) =>
                "XML nije ispravan: znak & se u vrijednosti piše kao &amp;amp; (npr. „A &amp;amp; B”).",
            _ when message.StartsWith("Unexpected end tag", StringComparison.Ordinal) =>
                "XML nije ispravan: zatvarajući tag bez odgovarajućeg otvorenog taga. Obrišite ga ili dodajte otvoreni tag.",
            _ when message.StartsWith("Invalid character in the given encoding", StringComparison.Ordinal) =>
                "XML nije ispravan: fajl sadrži znak koji ne odgovara navedenom kodiranju (npr. fajl je sačuvan kao "
                + "windows-1250, a u zaglavlju piše UTF-8). Sačuvajte fajl u kodiranju koje piše u zaglavlju.",
            _ => $"XML nije ispravan. Tehnički opis: {message}",
        };
    }

    // ------------------------------------------------------------------ opis mjesta i polja

    private static string Where(ValidationContext context) => context.ElementName switch
    {
        "faktura" => context.InvoiceNumber is { Length: > 0 } number ? $"Faktura {number}" : "Faktura (bez broja)",
        "stavka" or "stavka-recept" or "stavka-obavjest" =>
            (context.ItemNumber is { Length: > 0 } item ? $"Stavka {item}" : "Stavka")
            + (context.InvoiceNumber is { Length: > 0 } invoice ? $" fakture {invoice}" : string.Empty),
        "opsti-podaci" => "Opšti podaci (zaglavlje fajla)",
        "fakture-zdravstva" => "Glavni element fajla",
        null or "" => "Fajl",
        var other => $"Element <{other}>",
    };

    /// <summary>
    /// „šifru dijagnoze (…)” ili samo opis kad polje nema poseban naziv. Opis „cijeli broj od 0 do 65535” (samo granice
    /// tipa <c>xs:unsignedShort</c>) se uz naziv ne navodi, jer je suvišan: „Upišite broj nivoa usluge”.
    /// </summary>
    private static string What(string field, string description) =>
        ValueNouns.TryGetValue(field, out var noun)
            ? description == UnsignedShortRange
                ? noun
                : $"{noun} ({(description.StartsWith("datum ", StringComparison.Ordinal) ? description["datum ".Length..] : description)})"
            : description;

    private const string UnsignedShortRange = "cijeli broj od 0 do 65535";

    private static bool IsItem(string? element) => element is "stavka" or "stavka-recept" or "stavka-obavjest";

    private static string Field(string name) =>
        FieldNames.TryGetValue(name, out var label) && label != name ? $"polje {Quote(name)} ({label})" : $"polje {Quote(name)}";

    private static string Quote(string text) => $"„{text}”";

    private static string Elements(string list) =>
        string.Join(", ", list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(e => $"<{StripNamespace(e)}>"));

    private static string StripNamespace(string name) => name.Trim().Trim('\'');

    private static string Token(string token) => token switch
    {
        "\"" => "navodnik (\")",
        "'" => "apostrof (')",
        "=" => "znak =",
        ">" => "znak >",
        "<" => "znak <",
        "/" => "znak /",
        " " => "razmak",
        _ => $"„{token}”",
    };

    // ------------------------------------------------------------------ šema: tip i ograničenja

    private static XmlSchemaSimpleType? SimpleTypeOf(XmlSchemaObject? source, string name, ValidationContext context) =>
        source switch
        {
            XmlSchemaAttribute attribute => attribute.AttributeSchemaType,
            XmlSchemaElement element => element.ElementSchemaType as XmlSchemaSimpleType,
            _ => AttributeOf(context, name)?.AttributeSchemaType,
        };

    private static bool IsRequired(XmlSchemaObject? source, string name, ValidationContext context) =>
        (source as XmlSchemaAttribute ?? AttributeOf(context, name))?.Use == XmlSchemaUse.Required;

    private static XmlSchemaAttribute? AttributeOf(ValidationContext context, string name) =>
        context.ElementType is XmlSchemaComplexType complex
            ? complex.AttributeUses[new XmlQualifiedName(name)] as XmlSchemaAttribute
            : null;

    /// <summary>Opis vrijednosti koju šema traži, npr. „cijeli broj od 0 do 65535”.</summary>
    internal static string Describe(XmlSchemaSimpleType? type)
    {
        if (type is null)
        {
            return "ispravnu vrijednost";
        }

        var facets = Facets(type);
        string? Facet<T>() where T : XmlSchemaFacet => facets.OfType<T>().Select(f => f.Value).LastOrDefault();

        var enumeration = facets.OfType<XmlSchemaEnumerationFacet>().Select(f => f.Value ?? string.Empty).ToList();
        if (enumeration.Count > 0)
        {
            return "jednu od vrijednosti: " + Or(enumeration);
        }

        var parts = new List<string>();
        var pattern = Facet<XmlSchemaPatternFacet>();
        var code = type.Datatype?.TypeCode ?? XmlTypeCode.String;
        var isText = code is XmlTypeCode.String or XmlTypeCode.NormalizedString or XmlTypeCode.Token;

        if (pattern is not null && PatternChoice().Match(pattern) is { Success: true } choice)
        {
            parts.Add("jedno slovo: " + Or(choice.Groups["chars"].Value.Select(c => c.ToString())));
        }
        else if (pattern is not null && PatternDescriptions.TryGetValue(pattern, out var described))
        {
            parts.Add(described);
        }
        else if (pattern is not null)
        {
            parts.Add($"tekst u obliku {pattern}");
        }
        else
        {
            parts.Add(code switch
            {
                XmlTypeCode.UnsignedByte or XmlTypeCode.UnsignedShort or XmlTypeCode.UnsignedInt or XmlTypeCode.UnsignedLong
                    or XmlTypeCode.Integer or XmlTypeCode.Int or XmlTypeCode.Long or XmlTypeCode.Short
                    or XmlTypeCode.NonNegativeInteger or XmlTypeCode.PositiveInteger => "cijeli broj",
                XmlTypeCode.Decimal when Facet<XmlSchemaFractionDigitsFacet>() == "0" => "cijeli broj",
                XmlTypeCode.Decimal or XmlTypeCode.Double or XmlTypeCode.Float => "broj",
                XmlTypeCode.Date => "datum u obliku GGGG-MM-DD, npr. 2026-01-31",
                XmlTypeCode.Boolean => "true ili false",
                _ => "tekst",
            });
        }

        var min = Facet<XmlSchemaMinInclusiveFacet>();
        var max = Facet<XmlSchemaMaxInclusiveFacet>();
        var minExclusive = Facet<XmlSchemaMinExclusiveFacet>();
        var maxExclusive = Facet<XmlSchemaMaxExclusiveFacet>();
        (min, max) = (min, max) switch
        {
            (null, null) when minExclusive is null && maxExclusive is null => code switch
            {
                XmlTypeCode.UnsignedByte => ("0", "255"),
                XmlTypeCode.UnsignedShort => ("0", "65535"),
                XmlTypeCode.UnsignedInt => ("0", "4294967295"),
                _ => (null, null),
            },
            _ => (min, max),
        };

        if (min is not null && max is not null)
        {
            if (code == XmlTypeCode.Date)
            {
                parts.Add($"od {min} do {max}");
            }
            else
            {
                parts[^1] += $" od {min} do {max}";
            }
        }
        else if (min is not null)
        {
            parts.Add(code == XmlTypeCode.Date ? $"ne prije {min}" : $"{min} ili veći");
        }
        else if (max is not null)
        {
            parts.Add(code == XmlTypeCode.Date ? $"ne poslije {max}" : $"{max} ili manji");
        }

        if (minExclusive is not null)
        {
            parts.Add($"veći od {minExclusive}");
        }

        if (maxExclusive is not null)
        {
            parts.Add(maxExclusive == "0" ? "negativan (manji od 0)" : $"manji od {maxExclusive}");
        }

        if (Facet<XmlSchemaFractionDigitsFacet>() is { } fraction && fraction != "0")
        {
            parts.Add($"najviše {fraction} {(fraction == "1" ? "decimala" : "decimale")} sa decimalnom tačkom, npr. 12.50");
        }

        if (Facet<XmlSchemaTotalDigitsFacet>() is { } total)
        {
            parts.Add($"ukupno najviše {total} cifara");
        }

        var length = Facet<XmlSchemaLengthFacet>();
        var minLength = Facet<XmlSchemaMinLengthFacet>();
        var maxLength = Facet<XmlSchemaMaxLengthFacet>();
        if (length is not null)
        {
            parts.Add($"tačno {length} {Characters(length)}");
        }
        else if (minLength is not null && maxLength is not null)
        {
            parts.Add(minLength == maxLength ? $"tačno {maxLength} {Characters(maxLength)}" : $"od {minLength} do {maxLength} znakova");
        }
        else if (maxLength is not null)
        {
            parts.Add($"najviše {maxLength} {Characters(maxLength)}");
        }
        else if (minLength is not null)
        {
            parts.Add($"najmanje {minLength} {Characters(minLength)}");
        }

        if (isText && parts.Count > 1 && parts[0] == "tekst")
        {
            parts[0] = "tekst,";
            return string.Join(" ", parts[0], string.Join(", ", parts.Skip(1)));
        }

        return string.Join(", ", parts);
    }

    private static List<XmlSchemaFacet> Facets(XmlSchemaSimpleType type)
    {
        var facets = new List<XmlSchemaFacet>();
        for (var current = type; current is not null; current = current.BaseXmlSchemaType as XmlSchemaSimpleType)
        {
            if (current.Content is XmlSchemaSimpleTypeRestriction restriction)
            {
                facets.InsertRange(0, restriction.Facets.OfType<XmlSchemaFacet>());
            }

            if (current.QualifiedName.Namespace == XmlSchema.Namespace)
            {
                break;
            }
        }

        return facets;
    }

    private static string Characters(string count) =>
        int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14
            ? "znaka"
            : "znakova";

    private static string Or(IEnumerable<string> values)
    {
        var list = values.ToList();
        return list.Count == 1 ? list[0] : string.Join(", ", list.Take(list.Count - 1)) + " ili " + list[^1];
    }

    /// <summary>Zašto vrijednost nije prihvaćena (iz završnog dijela poruke validatora).</summary>
    private static string Reason(string detail, string value, XmlSchemaSimpleType? type)
    {
        var code = type?.Datatype?.TypeCode ?? XmlTypeCode.String;
        if (detail.Contains("Pattern constraint", StringComparison.Ordinal))
        {
            return "nije u propisanom obliku";
        }

        if (detail.Contains("MaxInclusive", StringComparison.Ordinal) || detail.Contains("MaxExclusive", StringComparison.Ordinal))
        {
            return code == XmlTypeCode.Date ? "je kasniji datum od dozvoljenog" : "je veća od dozvoljene";
        }

        if (detail.Contains("MinInclusive", StringComparison.Ordinal) || detail.Contains("MinExclusive", StringComparison.Ordinal))
        {
            return code == XmlTypeCode.Date ? "je raniji datum od dozvoljenog" : "je manja od dozvoljene";
        }

        if (detail.Contains("greater than the MaxLength", StringComparison.Ordinal))
        {
            return $"je preduga ({value.Length} znakova)";
        }

        if (detail.Contains("less than the MinLength", StringComparison.Ordinal))
        {
            return $"je prekratka ({value.Length} znakova)";
        }

        if (detail.Contains("not equal to the specified length", StringComparison.Ordinal))
        {
            return $"nema propisan broj znakova (ima {value.Length})";
        }

        if (detail.Contains("Enumeration constraint", StringComparison.Ordinal))
        {
            return "nije među dozvoljenim vrijednostima";
        }

        if (detail.Contains("TotalDigits", StringComparison.Ordinal))
        {
            return "ima previše cifara";
        }

        if (detail.Contains("FractionDigits", StringComparison.Ordinal))
        {
            return "ima previše decimala";
        }

        if (detail.Contains("too large or too small", StringComparison.Ordinal))
        {
            return "je izvan dozvoljenog opsega";
        }

        if (detail.Contains("is not a valid", StringComparison.Ordinal))
        {
            return code switch
            {
                XmlTypeCode.Date when DottedDate().IsMatch(value.Trim()) => "nije zapisana kao datum GGGG-MM-DD (tačke umjesto crtica)",
                XmlTypeCode.Date => "nije ispravan datum",
                XmlTypeCode.Decimal when value.Contains(',', StringComparison.Ordinal) => "ima decimalni zarez; koristite tačku",
                XmlTypeCode.Decimal or XmlTypeCode.Double or XmlTypeCode.Float => "nije broj",
                XmlTypeCode.Boolean => "nije true ni false",
                _ when value.Trim().StartsWith('-') => "je negativna, a dozvoljen je samo broj od 0 naviše",
                _ when value.Trim().All(char.IsAsciiDigit) => "je prevelik broj",
                _ when value.Contains('.', StringComparison.Ordinal) || value.Contains(',', StringComparison.Ordinal) => "nije cijeli broj",
                _ => "nije cijeli broj",
            };
        }

        return "nije ispravna";
    }

    private static string StripPosition(string message) => PositionSuffix().Replace(message, string.Empty).Trim();

    // ------------------------------------------------------------------ obrasci poruka validatora (.NET, engleski)

    [GeneratedRegex(@"^The '(?<name>[^']+)' (?<kind>attribute|element) is invalid - The value '(?<value>.*)' is invalid according to its datatype '[^']*' - (?<detail>.*)$", RegexOptions.Singleline)]
    private static partial Regex InvalidValue();

    [GeneratedRegex(@"^The required attribute '(?<name>[^']+)' is missing\.")]
    private static partial Regex MissingAttribute();

    [GeneratedRegex(@"^The '(?<name>[^']+)' attribute is not declared\.")]
    private static partial Regex UndeclaredAttribute();

    [GeneratedRegex(@"^The element '(?<parent>[^']+)'" + Namespace + @" has invalid child element '(?<child>[^']+)'" + Namespace + @"\.(?: List of possible elements expected: '(?<expected>[^']+)'" + Namespace + @"\.)?")]
    private static partial Regex InvalidChild();

    [GeneratedRegex(@"^The element '(?<element>[^']+)'" + Namespace + @" has incomplete content\.(?: List of possible elements expected: '(?<expected>[^']+)'" + Namespace + @"\.)?")]
    private static partial Regex IncompleteContent();

    [GeneratedRegex(@"^The '(?<element>[^']+)' element is not declared\.")]
    private static partial Regex UndeclaredElement();

    [GeneratedRegex(@"^The element '(?<element>[^']+)'" + Namespace + @" cannot contain text\.")]
    private static partial Regex TextNotAllowed();

    [GeneratedRegex(@"^'(?<found>.+?)' is an unexpected token\. The expected token is '(?<expected>.+?)'\.")]
    private static partial Regex UnexpectedToken();

    [GeneratedRegex(@"^The '(?<start>[^']+)' start tag on line (?<line>\d+) position \d+ does not match the end tag of '(?<end>[^']+)'\.")]
    private static partial Regex TagMismatch();

    [GeneratedRegex(@"^Unexpected end of file (?:has occurred|while parsing [^.]*)\.(?: The following elements are not closed: (?<open>.*))?")]
    private static partial Regex UnexpectedEnd();

    [GeneratedRegex(@"^Name cannot begin with the '(?<char>.)' character")]
    private static partial Regex NameStart();

    [GeneratedRegex(@"^'(?<char>.)', hexadecimal value 0x[0-9A-F]+, is an invalid attribute character\.")]
    private static partial Regex InvalidAttributeChar();

    [GeneratedRegex(@"hexadecimal value (?<hex>0x[0-9A-F]+), is an invalid character\.")]
    private static partial Regex InvalidCharacter();

    [GeneratedRegex(@"^'(?<name>[^']+)' is a duplicate attribute name\.")]
    private static partial Regex DuplicateAttribute();

    [GeneratedRegex(@"^\[(?<chars>[A-Za-z0-9]+)\]$")]
    private static partial Regex PatternChoice();

    [GeneratedRegex(@"^\d{1,2}\.\d{1,2}\.\d{4}\.?$")]
    private static partial Regex DottedDate();

    [GeneratedRegex(@"\s*Line \d+, position \d+\.\s*$")]
    private static partial Regex PositionSuffix();
}
