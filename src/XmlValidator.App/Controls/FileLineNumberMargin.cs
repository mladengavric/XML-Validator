using System.Globalization;
using Avalonia;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace XmlValidator.App.Controls;

/// <summary>
/// Brojevi linija za pregled dijela fajla: prva linija pregleda nosi broj <see cref="FirstLine"/>
/// (linija u ulaznom fajlu), pa se brojevi poklapaju sa linijama iz liste popravki i upozorenja.
/// Kad je <see cref="FirstLine"/> 0 (tekst ne odgovara fajlu, npr. uređen blok), brojevi kreću od 1.
/// Linija <see cref="HighlightLine"/> (od 1, u tekstu pregleda) ima blagu pozadinu i istaknut broj.
/// Boje dolaze iz stilova (<c>Controls.axaml</c>), pa prate temu.
/// </summary>
public sealed class FileLineNumberMargin : AbstractMargin
{
    public static readonly StyledProperty<int> FirstLineProperty =
        AvaloniaProperty.Register<FileLineNumberMargin, int>(nameof(FirstLine));

    public static readonly StyledProperty<int> HighlightLineProperty =
        AvaloniaProperty.Register<FileLineNumberMargin, int>(nameof(HighlightLine));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<FileLineNumberMargin, IBrush?>(nameof(Foreground));

    public static readonly StyledProperty<IBrush?> HighlightForegroundProperty =
        AvaloniaProperty.Register<FileLineNumberMargin, IBrush?>(nameof(HighlightForeground));

    public static readonly StyledProperty<IBrush?> HighlightBrushProperty =
        AvaloniaProperty.Register<FileLineNumberMargin, IBrush?>(nameof(HighlightBrush));

    private readonly HighlightRenderer _renderer;
    private TextView? _attachedView;

    static FileLineNumberMargin()
    {
        AffectsMeasure<FileLineNumberMargin>(FirstLineProperty);
        AffectsRender<FileLineNumberMargin>(HighlightLineProperty, ForegroundProperty, HighlightForegroundProperty);
    }

    public FileLineNumberMargin() => _renderer = new HighlightRenderer(this);

    /// <summary>Broj linije u fajlu za prvu liniju teksta (0 = brojevi od 1).</summary>
    public int FirstLine
    {
        get => GetValue(FirstLineProperty);
        set => SetValue(FirstLineProperty, value);
    }

    /// <summary>Linija u tekstu (od 1) koja se ističe; 0 = nijedna.</summary>
    public int HighlightLine
    {
        get => GetValue(HighlightLineProperty);
        set => SetValue(HighlightLineProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public IBrush? HighlightForeground
    {
        get => GetValue(HighlightForegroundProperty);
        set => SetValue(HighlightForegroundProperty, value);
    }

    public IBrush? HighlightBrush
    {
        get => GetValue(HighlightBrushProperty);
        set => SetValue(HighlightBrushProperty, value);
    }

    /// <summary>Broj koji se prikazuje uz liniju teksta <paramref name="line"/> (od 1).</summary>
    public int DisplayNumber(int line) => FirstLine > 0 ? FirstLine + line - 1 : line;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HighlightLineProperty || change.Property == HighlightBrushProperty)
        {
            TextView?.InvalidateLayer(KnownLayer.Background);
        }
    }

    protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
    {
        if (_attachedView is not null)
        {
            _attachedView.BackgroundRenderers.Remove(_renderer);
            _attachedView.VisualLinesChanged -= OnVisualLinesChanged;
        }

        base.OnTextViewChanged(oldTextView, newTextView);

        _attachedView = newTextView;
        if (newTextView is not null)
        {
            newTextView.BackgroundRenderers.Add(_renderer);
            newTextView.VisualLinesChanged += OnVisualLinesChanged;
        }

        InvalidateMeasure();
    }

    protected override void OnDocumentChanged(AvaloniaEdit.Document.TextDocument? oldDocument, AvaloniaEdit.Document.TextDocument? newDocument)
    {
        base.OnDocumentChanged(oldDocument, newDocument);
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var lineCount = Document?.LineCount ?? 1;
        var digits = Math.Max(2, DisplayNumber(lineCount).ToString(CultureInfo.InvariantCulture).Length);
        var text = CreateText(new string('9', digits), Foreground, bold: true);
        return new Size(text.Width, 0);
    }

    public override void Render(DrawingContext context)
    {
        var view = TextView;
        if (view is null || !view.VisualLinesValid)
        {
            return;
        }

        foreach (var visualLine in view.VisualLines)
        {
            var line = visualLine.FirstDocumentLine.LineNumber;
            var isHighlighted = line == HighlightLine;
            var text = CreateText(
                DisplayNumber(line).ToString(CultureInfo.CurrentCulture),
                isHighlighted ? HighlightForeground ?? Foreground : Foreground,
                isHighlighted);
            var y = visualLine.GetTextLineVisualYPosition(visualLine.TextLines[0], VisualYPosition.TextTop) - view.VerticalOffset;
            context.DrawText(text, new Point(Bounds.Width - text.Width, y));
        }
    }

    private FormattedText CreateText(string text, IBrush? brush, bool bold) =>
        new(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(GetValue(TextElement.FontFamilyProperty), FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal),
            GetValue(TextElement.FontSizeProperty),
            brush ?? Brushes.Transparent);

    private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

    /// <summary>Blaga pozadina istaknute linije u tekstu.</summary>
    private sealed class HighlightRenderer(FileLineNumberMargin owner) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (owner.HighlightLine <= 0 || owner.HighlightBrush is not { } brush || !textView.VisualLinesValid)
            {
                return;
            }

            foreach (var visualLine in textView.VisualLines)
            {
                if (visualLine.FirstDocumentLine.LineNumber == owner.HighlightLine)
                {
                    var top = visualLine.VisualTop - textView.VerticalOffset;
                    drawingContext.FillRectangle(brush, new Rect(0, top, textView.Bounds.Width, visualLine.Height));
                }
            }
        }
    }
}
