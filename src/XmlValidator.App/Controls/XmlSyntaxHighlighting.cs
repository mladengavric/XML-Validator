using System.Xml;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;

namespace XmlValidator.App.Controls;

/// <summary>
/// Bojenje XML sintakse: ugrađena definicija „XML” iz AvaloniaEdit-a, sa bojama iz resursa teme
/// (originalne boje su za svijetlu pozadinu i nisu čitljive u tamnoj temi). Za svaku temu se pravi
/// nova definicija, a globalni <see cref="HighlightingManager"/> se ne mijenja.
/// </summary>
public static class XmlSyntaxHighlighting
{
    private const string XmlDefinitionResource = "AvaloniaEdit.Highlighting.Resources.XML-Mode.xshd";

    /// <summary>Imenovane boje iz definicije „XML” → ključ resursa u <c>Tokens.axaml</c>.</summary>
    public static IReadOnlyDictionary<string, string> ColorResources { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["XmlTag"] = "SyntaxTagBrush",
        ["AttributeName"] = "SyntaxAttributeNameBrush",
        ["AttributeValue"] = "SyntaxAttributeValueBrush",
        ["Comment"] = "SyntaxCommentBrush",
        ["CData"] = "SyntaxDeclarationBrush",
        ["DocType"] = "SyntaxDeclarationBrush",
        ["XmlDeclaration"] = "SyntaxDeclarationBrush",
        ["Entity"] = "SyntaxEntityBrush",
        ["BrokenEntity"] = "SyntaxEntityBrush",
    };

    /// <summary>
    /// Pravi definiciju „XML” sa bojama koje vraća <paramref name="resolveColor"/> (po ključu resursa);
    /// boja za koju nema resursa ostaje originalna.
    /// </summary>
    public static IHighlightingDefinition Create(Func<string, Color?> resolveColor)
    {
        ArgumentNullException.ThrowIfNull(resolveColor);

        using var stream = typeof(TextEditor).Assembly.GetManifestResourceStream(XmlDefinitionResource);
        if (stream is null)
        {
            // Rezerva: ugrađena definicija sa originalnim bojama.
            return HighlightingManager.Instance.GetDefinition("XML");
        }

        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var xshd = HighlightingLoader.LoadXshd(reader);
        foreach (var color in xshd.Elements.OfType<XshdColor>())
        {
            if (color.Name is { } name
                && ColorResources.TryGetValue(name, out var key)
                && resolveColor(key) is { } value)
            {
                color.Foreground = new SimpleHighlightingBrush(value);
            }
        }

        return HighlightingLoader.Load(xshd, HighlightingManager.Instance);
    }
}
