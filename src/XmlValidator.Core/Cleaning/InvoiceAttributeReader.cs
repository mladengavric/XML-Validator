using System.Globalization;

namespace XmlValidator.Core.Cleaning;

/// <summary>Podaci fakture potrebni za klasifikaciju i izvještaj.</summary>
/// <param name="Number">Atribut <c>broj</c> iz otvarajućeg <c>&lt;faktura</c> taga.</param>
/// <param name="Date">Atribut <c>datum</c> iz otvarajućeg taga.</param>
/// <param name="Jmb">Atribut <c>osiguranik-jmb</c> iz otvarajućeg taga (ne iz stavki).</param>
/// <param name="Groups">Vrijednosti <c>usluga-grupa</c> svih stavki, kako su zapisane.</param>
/// <param name="ItemCount">Broj stavki (<c>stavka</c>, <c>stavka-recept</c>, <c>stavka-obavjest</c>).</param>
/// <param name="ParseError">Poruka parsera ako blok nije ispravan XML fragment (atributi su tada pročitani iz teksta); inače <c>null</c>.</param>
public sealed record InvoiceData(
    string? Number,
    string? Date,
    string? Jmb,
    IReadOnlyList<string> Groups,
    int ItemCount,
    string? ParseError)
{
    public bool IsWellFormed => ParseError is null;

    /// <summary>JMB koji se koristi za poređenje: <c>null</c> ako ga nema ili je prazan.</summary>
    public string? EffectiveJmb => string.IsNullOrEmpty(Jmb) ? null : Jmb;
}

/// <summary>
/// Čita atribute fakture. Prvo pokušava da parsira blok kao XML fragment (sa podrazumijevanim
/// namespace-om šeme); ako ne uspije, koristi regularne izraze usidrene na otvarajući tag, sa tačnim
/// imenom atributa i jednostrukim ili dvostrukim navodnicima.
/// </summary>
public static class InvoiceAttributeReader
{
    private static readonly HashSet<string> ItemNames = ["stavka", "stavka-recept", "stavka-obavjest"];

    public static InvoiceData Read(string blockText)
    {
        ArgumentNullException.ThrowIfNull(blockText);
        var (data, error) = TryReadXml(blockText);
        return data ?? ReadWithPatterns(blockText, error!);
    }

    /// <summary>
    /// Provjerava da je tekst ispravan XML fragment od jednog ili više <c>faktura</c> elemenata (između njih
    /// samo prazan prostor i komentari); vraća poruku greške ili <c>null</c>.
    /// </summary>
    public static string? ValidateFragment(string text, out int invoiceCount)
    {
        ArgumentNullException.ThrowIfNull(text);
        var (_, error, count) = TryReadXml(text, allowMany: true);
        invoiceCount = count;
        return error;
    }

    private static (InvoiceData? Data, string? Error) TryReadXml(string text)
    {
        var (data, error, _) = TryReadXml(text, allowMany: false);
        return (data, error);
    }

    private static (InvoiceData? Data, string? Error, int Count) TryReadXml(string text, bool allowMany)
    {
        var settings = new XmlReaderSettings
        {
            ConformanceLevel = ConformanceLevel.Fragment,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };
        var nameTable = new NameTable();
        var namespaces = new XmlNamespaceManager(nameTable);
        namespaces.AddNamespace(string.Empty, InvoiceSchemaConstants.TargetNamespace);
        var context = new XmlParserContext(nameTable, namespaces, null, XmlSpace.None);

        string? number = null, date = null, jmb = null;
        var groups = new List<string>();
        var items = 0;
        var topLevel = 0;
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), settings, context);
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element when reader.Depth == 0:
                        topLevel++;
                        if (topLevel > 1 && !allowMany)
                        {
                            return (null, "Tekst sadrži više od jednog elementa; očekuje se tačno jedna faktura.", topLevel);
                        }

                        if (reader.LocalName != "faktura")
                        {
                            return (null, $"Očekuje se element <faktura>, a pronađen je <{reader.Name}>.", topLevel);
                        }

                        if (topLevel > 1)
                        {
                            break;
                        }

                        number = reader.GetAttribute("broj");
                        date = reader.GetAttribute("datum");
                        jmb = reader.GetAttribute("osiguranik-jmb");
                        break;
                    case XmlNodeType.Element when reader.LocalName == "faktura":
                        return (null, "Element <faktura> ne može biti unutar druge fakture (fali </faktura>?).", topLevel);
                    case XmlNodeType.Element:
                        if (reader.Depth == 1 && ItemNames.Contains(reader.LocalName))
                        {
                            items++;
                        }

                        if (reader.GetAttribute("usluga-grupa") is { } group)
                        {
                            groups.Add(group);
                        }

                        break;
                    case XmlNodeType.Text or XmlNodeType.CDATA when reader.Depth == 0:
                        return (null, "Tekst sadrži sadržaj van elementa <faktura>.", topLevel);
                }
            }
        }
        catch (XmlException ex)
        {
            return (null, ex.Message, topLevel);
        }

        if (topLevel == 0)
        {
            return (null, "Tekst ne sadrži element <faktura>.", 0);
        }

        return (new InvoiceData(number, date, jmb, groups, items, ParseError: null), null, topLevel);
    }

    private static InvoiceData ReadWithPatterns(string text, string error)
    {
        // Otvarajući tag: od početka do prvog '>' ili do sljedećeg '<' (kad '>' fali).
        var tagEnd = text.Length;
        var gt = text.IndexOf('>', 1);
        var lt = text.IndexOf('<', 1);
        if (gt >= 0)
        {
            tagEnd = gt;
        }

        if (lt >= 0 && lt < tagEnd)
        {
            tagEnd = lt;
        }

        var openingTag = text[..tagEnd];
        var body = text[tagEnd..];
        var groups = DocumentPatterns.GroupAttribute().Matches(body).Select(m => DecodeEntities(m.Groups["v"].Value)).ToList();
        var items = DocumentPatterns.ItemOpen().Count(body);
        return new InvoiceData(
            Attribute(openingTag, "broj"),
            Attribute(openingTag, "datum"),
            Attribute(openingTag, "osiguranik-jmb"),
            groups,
            items,
            error);
    }

    private static string? Attribute(string openingTag, string name)
    {
        var match = DocumentPatterns.Attribute(name).Match(openingTag);
        return match.Success ? DecodeEntities(match.Groups["v"].Value) : null;
    }

    private static string DecodeEntities(string value) =>
        value.Contains('&', StringComparison.Ordinal)
            ? DocumentPatterns.EntityReference().Replace(value, m =>
            {
                if (m.Groups["name"].Success)
                {
                    return m.Groups["name"].Value switch
                    {
                        "lt" => "<",
                        "gt" => ">",
                        "amp" => "&",
                        "quot" => "\"",
                        _ => "'",
                    };
                }

                var code = m.Groups["hex"].Success
                    ? int.Parse(m.Groups["hex"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                    : int.Parse(m.Groups["dec"].Value, CultureInfo.InvariantCulture);
                return code is >= 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF) ? char.ConvertFromUtf32(code) : m.Value;
            })
            : value;
}
