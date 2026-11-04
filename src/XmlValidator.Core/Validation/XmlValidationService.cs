using System.Text;
using System.Xml.Schema;
using XmlValidator.Core.Schemas;

namespace XmlValidator.Core.Validation;

/// <summary>
/// Validacija XML fajla prema aktivnoj šemi. Prikuplja sve nalaze (ne staje na prvoj grešci šeme),
/// bilježi fatalnu grešku ispravnosti XML-a sa linijom i kolonom, prepoznaje pogrešan namespace
/// korijenskog elementa i ništa ne preuzima sa mreže: <c>xsi:schemaLocation</c>, DTD i
/// <c>&lt;?xml-stylesheet?&gt;</c> se ne prate, uvijek se koristi aktivna šema.
/// </summary>
public sealed class XmlValidationService
{
    /// <summary>Podrazumijevano ograničenje broja nalaza u listi.</summary>
    public const int DefaultMaxIssues = 1000;

    private const int CancellationCheckInterval = 256;

    private readonly ISchemaProvider _schemaProvider;
    private readonly int _maxIssues;

    public XmlValidationService(ISchemaProvider schemaProvider, int maxIssues = DefaultMaxIssues)
    {
        ArgumentNullException.ThrowIfNull(schemaProvider);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxIssues, 1);
        _schemaProvider = schemaProvider;
        _maxIssues = maxIssues;

        // Omogućava fajlove u windows-1250 (prema XML deklaraciji). Registracija je idempotentna.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>Validira sadržaj iz toka (fajla). Kodiranje se određuje po BOM-u i XML deklaraciji. Tok se ne zatvara.</summary>
    public Task<ValidationResult> ValidateAsync(Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Task.Run(() => Validate(settings => XmlReader.Create(content, settings), cancellationToken), cancellationToken);
    }

    /// <summary>Validira tekst (npr. sadržaj editora). Kodiranje iz XML deklaracije se ne primjenjuje.</summary>
    public Task<ValidationResult> ValidateAsync(string content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        // Znak BOM na početku teksta (ako je ostao iz fajla) nije dio XML-a.
        var text = content.Length > 0 && content[0] == '﻿' ? content[1..] : content;
        return Task.Run(() => Validate(settings => XmlReader.Create(new StringReader(text), settings), cancellationToken), cancellationToken);
    }

    /// <summary>Validira fajl sa diska (fajl se otvara samo za čitanje i odmah zatvara).</summary>
    public async Task<ValidationResult> ValidateFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ValidateAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private ValidationResult Validate(Func<XmlReaderSettings, XmlReader> createReader, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var schema = _schemaProvider.GetActiveSchema();
        var collector = new IssueCollector(_maxIssues);

        var settings = new XmlReaderSettings
        {
            ValidationType = ValidationType.Schema,
            Schemas = schema.SchemaSet,

            // Bez ProcessSchemaLocation i ProcessInlineSchema: šema iz dokumenta se nikad ne učitava.
            ValidationFlags = XmlSchemaValidationFlags.ReportValidationWarnings
                | XmlSchemaValidationFlags.ProcessIdentityConstraints
                | XmlSchemaValidationFlags.AllowXmlAttributes,
            XmlResolver = null,
            DtdProcessing = DtdProcessing.Prohibit,
            CloseInput = false,
        };

        var rootSeen = false;
        var namespaceMismatch = false;
        var pending = new List<ValidationIssue>();

        // Validator javlja grešku atributa dok je čitač na samom atributu (mjesto i tip tada nisu dostupni), pa se
        // nalazi čuvaju dok Read() ne stane na element kojem pripadaju i tek tada dobijaju objašnjenje (Flush).
        string? currentInvoice = null;
        var raw = new List<ValidationEventArgs>();
        settings.ValidationEventHandler += (_, e) =>
        {
            if (!namespaceMismatch)
            {
                raw.Add(e); // šema se ne primjenjuje na dokument u drugom namespace-u
            }
        };

        void Flush(XmlReader? at)
        {
            if (raw.Count == 0)
            {
                return;
            }

            var context = Context(at, currentInvoice);
            foreach (var e in raw)
            {
                var issue = new ValidationIssue(
                    e.Severity == XmlSeverityType.Error ? ValidationSeverity.Error : ValidationSeverity.Warning,
                    e.Exception?.LineNumber ?? 0,
                    e.Exception?.LinePosition ?? 0,
                    e.Exception is { } exception ? ValidationMessages.FromSchema(exception, context) : e.Message,
                    ValidationIssueKind.Schema,
                    e.Message);
                if (rootSeen)
                {
                    collector.Add(issue);
                }
                else
                {
                    pending.Add(issue);
                }
            }

            raw.Clear();
        }

        XmlReader? reader = null;
        try
        {
            reader = createReader(settings);
            var lineInfo = reader as IXmlLineInfo;
            var nodes = 0;
            while (reader.Read())
            {
                if (++nodes % CancellationCheckInterval == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "faktura")
                {
                    currentInvoice = reader.GetAttribute("broj");
                }

                Flush(reader);
                if (!rootSeen && reader.NodeType == XmlNodeType.Element)
                {
                    rootSeen = true;
                    if (reader.NamespaceURI != InvoiceSchemaConstants.TargetNamespace)
                    {
                        namespaceMismatch = true;
                        pending.Clear();
                        collector.Add(new ValidationIssue(
                            ValidationSeverity.Error,
                            lineInfo?.LineNumber ?? 0,
                            lineInfo?.LinePosition ?? 0,
                            NamespaceMessage(reader.LocalName, reader.NamespaceURI),
                            ValidationIssueKind.Namespace));
                    }
                    else
                    {
                        pending.ForEach(collector.Add);
                        pending.Clear();
                    }
                }
            }
        }
        catch (XmlException ex)
        {
            Flush(null);
            pending.ForEach(collector.Add);
            pending.Clear();
            collector.Add(new ValidationIssue(
                ValidationSeverity.Error, ex.LineNumber, ex.LinePosition, ValidationMessages.FromXml(ex), ValidationIssueKind.WellFormedness, ex.Message));
        }
        finally
        {
            reader?.Dispose();
        }

        Flush(null);
        pending.ForEach(collector.Add);
        return new ValidationResult(
            collector.Issues,
            collector.ErrorCount,
            collector.WarningCount,
            _maxIssues,
            schema.Name,
            schema.IsFallback,
            schema.FallbackReason);
    }

    /// <summary>
    /// Mjesto nalaza iz validirajućeg čitača: element na kojem je čitač (atributi se provjeravaju dok je na elementu),
    /// broj fakture i redni broj stavke. Čitač se ne pomjera; greška pri čitanju daje prazan kontekst.
    /// </summary>
    private static ValidationContext Context(XmlReader? reader, string? currentInvoice)
    {
        try
        {
            if (reader is null || reader.NodeType is not (XmlNodeType.Element or XmlNodeType.EndElement))
            {
                return new ValidationContext(null, currentInvoice, null);
            }

            var element = reader.LocalName;
            var isStart = reader.NodeType == XmlNodeType.Element;
            var type = reader.SchemaInfo?.SchemaElement?.ElementSchemaType ?? reader.SchemaInfo?.SchemaType;
            return element switch
            {
                "faktura" => new ValidationContext(element, isStart ? reader.GetAttribute("broj") : currentInvoice, null, type),
                "stavka" or "stavka-recept" or "stavka-obavjest" =>
                    new ValidationContext(element, currentInvoice, isStart ? reader.GetAttribute("redni-broj") : null, type),
                "fakture-zdravstva" or "opsti-podaci" => new ValidationContext(element, null, null, type),
                _ => new ValidationContext(element, currentInvoice, null, type),
            };
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException)
        {
            return new ValidationContext(null, currentInvoice, null);
        }
    }

    private static string NamespaceMessage(string localName, string namespaceUri) =>
        string.IsNullOrEmpty(namespaceUri)
            ? $"Korijenski element '{localName}' nema namespace, a očekuje se '{InvoiceSchemaConstants.TargetNamespace}'. "
              + "Fajl nije provjeren prema šemi (provjerena je samo ispravnost XML-a)."
            : $"Korijenski element '{localName}' je u namespace-u '{namespaceUri}', a očekuje se '{InvoiceSchemaConstants.TargetNamespace}'. "
              + "Fajl nije provjeren prema šemi (provjerena je samo ispravnost XML-a).";

    private sealed class IssueCollector(int maxIssues)
    {
        private readonly List<ValidationIssue> _issues = [];

        public IReadOnlyList<ValidationIssue> Issues => _issues;

        public int ErrorCount { get; private set; }

        public int WarningCount { get; private set; }

        public void Add(ValidationIssue issue)
        {
            if (issue.Severity == ValidationSeverity.Error)
            {
                ErrorCount++;
            }
            else
            {
                WarningCount++;
            }

            if (_issues.Count < maxIssues)
            {
                _issues.Add(issue);
            }
        }
    }
}
