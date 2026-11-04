using System.Text.RegularExpressions;
using System.Xml.Schema;

namespace XmlValidator.Core.Schemas;

/// <summary>Rezultat kompajliranja skupa šema.</summary>
internal sealed record SchemaCompilation(
    XmlSchemaSet? SchemaSet,
    IReadOnlyList<string> Errors,
    string? TargetNamespace,
    string? Version)
{
    public bool Succeeded => SchemaSet is not null && Errors.Count == 0;
}

/// <summary>
/// Kompajlira skup šema iz memorije uz zatvoren resolver (<see cref="PackageXmlResolver"/>).
/// Upozorenja pri kompajliranju (npr. nerazriješen <c>xs:include</c>, koji .NET prijavljuje samo kao
/// upozorenje) tretiraju se kao greške, jer bi inače šema bila tiho nepotpuna.
/// </summary>
internal static partial class SchemaCompiler
{
    public static SchemaCompilation Compile(IReadOnlyList<SchemaFile> files, string mainSchemaFile)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(mainSchemaFile);

        var main = files.FirstOrDefault(f => string.Equals(
            SchemaHash.NormalizePath(f.RelativePath),
            SchemaHash.NormalizePath(mainSchemaFile),
            StringComparison.OrdinalIgnoreCase));
        if (main is null)
        {
            return Failed($"Glavna šema '{mainSchemaFile}' ne postoji u paketu.");
        }

        var resolver = new PackageXmlResolver(files);
        var errors = new List<string>();
        var set = new XmlSchemaSet { XmlResolver = resolver };
        set.ValidationEventHandler += (_, e) => errors.Add(Describe(e.Exception, e.Message));

        XmlSchema? schema = null;
        try
        {
            var readerSettings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            };
            using var stream = new MemoryStream(main.Content, writable: false);
            using var reader = XmlReader.Create(stream, readerSettings, PackageXmlResolver.GetUri(main.RelativePath).AbsoluteUri);
            schema = set.Add(null, reader);
            if (schema is not null && errors.Count == 0)
            {
                set.Compile();
            }
        }
        catch (XmlSchemaException ex)
        {
            errors.Add(Describe(ex, ex.Message));
        }
        catch (XmlException ex)
        {
            errors.Add(Describe(main.RelativePath, ex.LineNumber, ex.LinePosition, ex.Message));
        }

        // .NET za nerazriješenu referencu daje samo opštu poruku („Cannot resolve the 'schemaLocation'
        // attribute.”), pa se ispred nje dodaje jasan razlog iz resolvera.
        var referenceErrors = resolver.RejectedReferences
            .Select(r => $"Referenca '{r}' nije dozvoljena: šema može da uključuje samo fajlove iz istog paketa, bez preuzimanja sa mreže.")
            .Concat(resolver.MissingReferences.Select(m => $"Fajl '{m}' na koji šema upućuje ne postoji u paketu."))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        errors.InsertRange(0, referenceErrors);

        var targetNamespace = schema?.TargetNamespace;
        if (errors.Count > 0 || schema is null)
        {
            if (errors.Count == 0)
            {
                errors.Add($"Fajl '{main.RelativePath}' nije ispravna XSD šema.");
            }

            return new SchemaCompilation(null, errors, targetNamespace, null);
        }

        if (!string.Equals(targetNamespace, InvoiceSchemaConstants.TargetNamespace, StringComparison.Ordinal))
        {
            errors.Add(WrongNamespaceMessage(main.RelativePath, targetNamespace));
        }
        else if (!set.GlobalElements.Contains(new XmlQualifiedName(InvoiceSchemaConstants.RootElementName, InvoiceSchemaConstants.TargetNamespace)))
        {
            errors.Add($"Šema '{main.RelativePath}' ne definiše korijenski element '{InvoiceSchemaConstants.RootElementName}'.");
        }

        return errors.Count > 0
            ? new SchemaCompilation(null, errors, targetNamespace, null)
            : new SchemaCompilation(set, errors, targetNamespace, ReadVersion(schema));
    }

    internal static string WrongNamespaceMessage(string file, string? targetNamespace) =>
        string.IsNullOrEmpty(targetNamespace)
            ? $"Šema '{file}' nema targetNamespace; očekuje se '{InvoiceSchemaConstants.TargetNamespace}'."
            : $"Šema '{file}' ima targetNamespace '{targetNamespace}', a očekuje se '{InvoiceSchemaConstants.TargetNamespace}'.";

    private static SchemaCompilation Failed(string error) => new(null, [error], null, null);

    private static string? ReadVersion(XmlSchema schema)
    {
        if (!string.IsNullOrWhiteSpace(schema.Version))
        {
            return schema.Version.Trim();
        }

        foreach (var annotation in schema.Items.OfType<XmlSchemaAnnotation>())
        {
            foreach (var documentation in annotation.Items.OfType<XmlSchemaDocumentation>())
            {
                var text = string.Concat(documentation.Markup?.Select(n => n?.InnerText) ?? []);
                var match = VersionInAnnotation().Match(text);
                if (match.Success)
                {
                    return match.Groups["version"].Value;
                }
            }
        }

        return null;
    }

    private static string Describe(XmlSchemaException? exception, string message)
    {
        var file = exception?.SourceUri is { Length: > 0 } source
            && Uri.TryCreate(source, UriKind.Absolute, out var uri)
            && PackageXmlResolver.TryGetRelativePath(uri, out var relative)
                ? relative
                : null;
        return Describe(file, exception?.LineNumber ?? 0, exception?.LinePosition ?? 0, message);
    }

    private static string Describe(string? file, int line, int column, string message)
    {
        if (file is null)
        {
            return message;
        }

        return line > 0
            ? $"{file} (linija {line}, kolona {column}): {message}"
            : $"{file}: {message}";
    }

    [GeneratedRegex(@"(?i)\b(verzija|version)\b\s*[:=]?\s*(?<version>[0-9][\w.\-]*)")]
    private static partial Regex VersionInAnnotation();
}
