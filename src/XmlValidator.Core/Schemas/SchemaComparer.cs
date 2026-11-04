using System.Globalization;
using System.Xml.Schema;

namespace XmlValidator.Core.Schemas;

/// <summary>Promjena jednog ograničenja (fasete) elementa ili atributa.</summary>
/// <param name="Path">Putanja, npr. <c>fakture-zdravstva/faktura/@datum</c>.</param>
/// <param name="Restriction">
/// Naziv ograničenja: XSD faseta (<c>minInclusive</c>, <c>maxInclusive</c>, <c>pattern</c>,
/// <c>length</c>, <c>enumeration</c>, ...), ili <c>type</c> (osnovni XSD tip), <c>use</c> (obaveznost
/// atributa), <c>minOccurs</c>/<c>maxOccurs</c> (broj pojavljivanja elementa), <c>fixed</c>, <c>default</c>.
/// </param>
/// <param name="OldValue">Vrijednost u staroj šemi; <c>null</c> ako ograničenje nije postojalo.</param>
/// <param name="NewValue">Vrijednost u novoj šemi; <c>null</c> ako je ograničenje uklonjeno.</param>
public sealed record SchemaRestrictionChange(string Path, string Restriction, string? OldValue, string? NewValue);

/// <summary>Razlike između dvije šeme.</summary>
public sealed record SchemaDiff(
    IReadOnlyList<string> AddedElements,
    IReadOnlyList<string> RemovedElements,
    IReadOnlyList<string> AddedAttributes,
    IReadOnlyList<string> RemovedAttributes,
    IReadOnlyList<SchemaRestrictionChange> ChangedRestrictions)
{
    public bool IsEmpty =>
        AddedElements.Count == 0 && RemovedElements.Count == 0 && AddedAttributes.Count == 0
        && RemovedAttributes.Count == 0 && ChangedRestrictions.Count == 0;
}

/// <summary>
/// Poredi dvije kompajlirane šeme: dodati i uklonjeni elementi i atributi, i promijenjena ograničenja
/// (fasete prostih tipova, osnovni tip, obaveznost atributa, broj pojavljivanja elementa).
/// Elementi i atributi se identifikuju putanjom od korijenskog elementa.
/// </summary>
public static class SchemaComparer
{
    private const int MaxDepth = 64;

    public static SchemaDiff Compare(XmlSchemaSet oldSchema, XmlSchemaSet newSchema)
    {
        ArgumentNullException.ThrowIfNull(oldSchema);
        ArgumentNullException.ThrowIfNull(newSchema);

        var before = Collect(oldSchema);
        var after = Collect(newSchema);

        List<string> Keys(Dictionary<string, Node> a, Dictionary<string, Node> b, bool attribute) =>
            [.. a.Where(kv => kv.Value.IsAttribute == attribute && !b.ContainsKey(kv.Key)).Select(kv => kv.Key).Order(StringComparer.Ordinal)];

        var changes = new List<SchemaRestrictionChange>();
        foreach (var (path, oldNode) in before.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!after.TryGetValue(path, out var newNode))
            {
                continue;
            }

            foreach (var name in oldNode.Restrictions.Keys.Union(newNode.Restrictions.Keys).Order(StringComparer.Ordinal))
            {
                oldNode.Restrictions.TryGetValue(name, out var oldValue);
                newNode.Restrictions.TryGetValue(name, out var newValue);
                if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
                {
                    changes.Add(new SchemaRestrictionChange(path, name, oldValue, newValue));
                }
            }
        }

        return new SchemaDiff(
            AddedElements: Keys(after, before, attribute: false),
            RemovedElements: Keys(before, after, attribute: false),
            AddedAttributes: Keys(after, before, attribute: true),
            RemovedAttributes: Keys(before, after, attribute: true),
            ChangedRestrictions: changes);
    }

    private sealed record Node(bool IsAttribute, SortedDictionary<string, string> Restrictions);

    private static Dictionary<string, Node> Collect(XmlSchemaSet set)
    {
        var nodes = new Dictionary<string, Node>(StringComparer.Ordinal);
        foreach (var element in set.GlobalElements.Values.OfType<XmlSchemaElement>()
            .OrderBy(e => e.QualifiedName.Name, StringComparer.Ordinal))
        {
            VisitElement(element, parentPath: null, isGlobal: true, nodes, [], depth: 0);
        }

        return nodes;
    }

    private static void VisitElement(
        XmlSchemaElement element,
        string? parentPath,
        bool isGlobal,
        Dictionary<string, Node> nodes,
        HashSet<XmlSchemaType> typesOnPath,
        int depth)
    {
        var name = element.QualifiedName.Name;
        var path = parentPath is null ? name : $"{parentPath}/{name}";
        var restrictions = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (!isGlobal)
        {
            restrictions["minOccurs"] = element.MinOccurs.ToString(CultureInfo.InvariantCulture);
            restrictions["maxOccurs"] = element.MaxOccurs == decimal.MaxValue
                ? "unbounded"
                : element.MaxOccurs.ToString(CultureInfo.InvariantCulture);
        }

        AddValueConstraints(restrictions, element.FixedValue, element.DefaultValue);

        var type = element.ElementSchemaType;
        if (type is XmlSchemaSimpleType simpleType)
        {
            AddSimpleTypeRestrictions(restrictions, simpleType);
        }

        nodes.TryAdd(path, new Node(IsAttribute: false, restrictions));

        if (type is not XmlSchemaComplexType complexType || depth >= MaxDepth || !typesOnPath.Add(complexType))
        {
            return;
        }

        foreach (var attribute in complexType.AttributeUses.Values.OfType<XmlSchemaAttribute>()
            .OrderBy(a => a.QualifiedName.Name, StringComparer.Ordinal))
        {
            var attributeRestrictions = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["use"] = attribute.Use == XmlSchemaUse.Required ? "required"
                    : attribute.Use == XmlSchemaUse.Prohibited ? "prohibited"
                    : "optional",
            };
            AddValueConstraints(attributeRestrictions, attribute.FixedValue, attribute.DefaultValue);
            if (attribute.AttributeSchemaType is { } attributeType)
            {
                AddSimpleTypeRestrictions(attributeRestrictions, attributeType);
            }

            nodes.TryAdd($"{path}/@{attribute.QualifiedName.Name}", new Node(IsAttribute: true, attributeRestrictions));
        }

        VisitParticle(complexType.ContentTypeParticle, path, nodes, typesOnPath, depth + 1);
        typesOnPath.Remove(complexType);
    }

    private static void VisitParticle(
        XmlSchemaParticle? particle,
        string path,
        Dictionary<string, Node> nodes,
        HashSet<XmlSchemaType> typesOnPath,
        int depth)
    {
        switch (particle)
        {
            case XmlSchemaElement element:
                VisitElement(element, path, isGlobal: false, nodes, typesOnPath, depth);
                break;
            case XmlSchemaGroupBase group:
                foreach (var item in group.Items.OfType<XmlSchemaParticle>())
                {
                    VisitParticle(item, path, nodes, typesOnPath, depth);
                }

                break;
            case XmlSchemaGroupRef groupRef:
                VisitParticle(groupRef.Particle, path, nodes, typesOnPath, depth);
                break;
        }
    }

    private static void AddValueConstraints(SortedDictionary<string, string> restrictions, string? fixedValue, string? defaultValue)
    {
        if (fixedValue is not null)
        {
            restrictions["fixed"] = fixedValue;
        }

        if (defaultValue is not null)
        {
            restrictions["default"] = defaultValue;
        }
    }

    private static void AddSimpleTypeRestrictions(SortedDictionary<string, string> restrictions, XmlSchemaSimpleType type)
    {
        restrictions["type"] = DescribeBaseType(type);

        // Lanac izvođenja od osnovnog tipa ka izvedenom: izvedeni tip nadjačava fasete osnovnog,
        // a obrasci (pattern) se akumuliraju jer važe svi nivoi istovremeno.
        var chain = new List<XmlSchemaSimpleType>();
        for (XmlSchemaType? current = type; current is XmlSchemaSimpleType simple && !IsBuiltIn(simple); current = current.BaseXmlSchemaType)
        {
            chain.Add(simple);
            if (chain.Count > MaxDepth)
            {
                break;
            }
        }

        chain.Reverse();
        var patterns = new List<string>();
        foreach (var simple in chain)
        {
            if (simple.Content is not XmlSchemaSimpleTypeRestriction restriction)
            {
                continue;
            }

            var levelPatterns = new List<string>();
            var levelEnumeration = new List<string>();
            foreach (var facet in restriction.Facets.OfType<XmlSchemaFacet>())
            {
                var value = facet.Value ?? string.Empty;
                switch (facet)
                {
                    case XmlSchemaPatternFacet:
                        levelPatterns.Add(value);
                        break;
                    case XmlSchemaEnumerationFacet:
                        levelEnumeration.Add(value);
                        break;
                    default:
                        restrictions[FacetName(facet)] = value;
                        break;
                }
            }

            if (levelPatterns.Count > 0)
            {
                patterns.Add(string.Join(" | ", levelPatterns));
            }

            if (levelEnumeration.Count > 0)
            {
                restrictions["enumeration"] = string.Join(", ", levelEnumeration.Order(StringComparer.Ordinal));
            }
        }

        if (patterns.Count > 0)
        {
            restrictions["pattern"] = string.Join(" ; ", patterns);
        }
    }

    private static bool IsBuiltIn(XmlSchemaSimpleType type) =>
        type.QualifiedName.Namespace == XmlSchema.Namespace;

    private static string DescribeBaseType(XmlSchemaSimpleType type)
    {
        for (XmlSchemaType? current = type; current is not null; current = current.BaseXmlSchemaType)
        {
            if (current is XmlSchemaSimpleType { Content: XmlSchemaSimpleTypeList } && !IsBuiltIn((XmlSchemaSimpleType)current))
            {
                return "list";
            }

            if (current is XmlSchemaSimpleType { Content: XmlSchemaSimpleTypeUnion } && !IsBuiltIn((XmlSchemaSimpleType)current))
            {
                return "union";
            }

            if (current.QualifiedName.Namespace == XmlSchema.Namespace)
            {
                return current.QualifiedName.Name;
            }
        }

        return "anySimpleType";
    }

    private static string FacetName(XmlSchemaFacet facet) => facet switch
    {
        XmlSchemaMinInclusiveFacet => "minInclusive",
        XmlSchemaMaxInclusiveFacet => "maxInclusive",
        XmlSchemaMinExclusiveFacet => "minExclusive",
        XmlSchemaMaxExclusiveFacet => "maxExclusive",
        XmlSchemaLengthFacet => "length",
        XmlSchemaMinLengthFacet => "minLength",
        XmlSchemaMaxLengthFacet => "maxLength",
        XmlSchemaTotalDigitsFacet => "totalDigits",
        XmlSchemaFractionDigitsFacet => "fractionDigits",
        XmlSchemaWhiteSpaceFacet => "whiteSpace",
        _ => facet.GetType().Name,
    };
}
