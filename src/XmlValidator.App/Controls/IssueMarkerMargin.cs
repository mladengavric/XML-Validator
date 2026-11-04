using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using XmlValidator.App.Services;

namespace XmlValidator.App.Controls;

/// <summary>Nalaz vezan za liniju editora (linija počinje od 1).</summary>
public sealed record LineMarker(int Line, MessageSeverity Severity, string Message);

/// <summary>
/// Margina editora sa oznakama linija koje imaju nalaze: krug za grešku, trougao za upozorenje
/// (oblik nosi značenje i bez boje), uz blagu pozadinu i podvlačenje same linije u tekstu.
/// Boje dolaze iz stilova (<c>Controls.axaml</c>), pa prate temu.
/// </summary>
public sealed class IssueMarkerMargin : AbstractMargin
{
    public static readonly StyledProperty<IBrush?> ErrorBrushProperty =
        AvaloniaProperty.Register<IssueMarkerMargin, IBrush?>(nameof(ErrorBrush));

    public static readonly StyledProperty<IBrush?> WarningBrushProperty =
        AvaloniaProperty.Register<IssueMarkerMargin, IBrush?>(nameof(WarningBrush));

    public static readonly StyledProperty<IBrush?> ErrorLineBrushProperty =
        AvaloniaProperty.Register<IssueMarkerMargin, IBrush?>(nameof(ErrorLineBrush));

    public static readonly StyledProperty<IBrush?> WarningLineBrushProperty =
        AvaloniaProperty.Register<IssueMarkerMargin, IBrush?>(nameof(WarningLineBrush));

    public static readonly StyledProperty<double> MarkerWidthProperty =
        AvaloniaProperty.Register<IssueMarkerMargin, double>(nameof(MarkerWidth), 16);

    private readonly LineBackgroundRenderer _renderer;
    private IReadOnlyDictionary<int, LineInfo> _lines = new Dictionary<int, LineInfo>();
    private TextView? _attachedView;

    static IssueMarkerMargin()
    {
        AffectsRender<IssueMarkerMargin>(ErrorBrushProperty, WarningBrushProperty);
        AffectsMeasure<IssueMarkerMargin>(MarkerWidthProperty);
    }

    public IssueMarkerMargin()
    {
        _renderer = new LineBackgroundRenderer(this);
        Cursor = new Cursor(StandardCursorType.Arrow);
    }

    public IBrush? ErrorBrush
    {
        get => GetValue(ErrorBrushProperty);
        set => SetValue(ErrorBrushProperty, value);
    }

    public IBrush? WarningBrush
    {
        get => GetValue(WarningBrushProperty);
        set => SetValue(WarningBrushProperty, value);
    }

    public IBrush? ErrorLineBrush
    {
        get => GetValue(ErrorLineBrushProperty);
        set => SetValue(ErrorLineBrushProperty, value);
    }

    public IBrush? WarningLineBrush
    {
        get => GetValue(WarningLineBrushProperty);
        set => SetValue(WarningLineBrushProperty, value);
    }

    public double MarkerWidth
    {
        get => GetValue(MarkerWidthProperty);
        set => SetValue(MarkerWidthProperty, value);
    }

    /// <summary>Linije sa nalazima (za testove i opis).</summary>
    public IReadOnlyCollection<int> MarkedLines => (IReadOnlyCollection<int>)_lines.Keys;

    /// <summary>Ozbiljnost oznake na liniji (<c>null</c> ako linija nema nalaz).</summary>
    public MessageSeverity? GetSeverity(int line) => _lines.TryGetValue(line, out var info) ? info.Severity : null;

    /// <summary>Postavlja nalaze; greška ima prednost pred upozorenjem na istoj liniji.</summary>
    public void SetMarkers(IEnumerable<LineMarker> markers)
    {
        ArgumentNullException.ThrowIfNull(markers);
        var lines = new Dictionary<int, LineInfo>();
        foreach (var marker in markers)
        {
            if (marker.Line <= 0)
            {
                continue;
            }

            if (lines.TryGetValue(marker.Line, out var existing))
            {
                var severity = existing.Severity == MessageSeverity.Error || marker.Severity == MessageSeverity.Error
                    ? MessageSeverity.Error
                    : MessageSeverity.Warning;
                existing.Messages.Add(marker.Message);
                lines[marker.Line] = existing with { Severity = severity };
            }
            else
            {
                lines[marker.Line] = new LineInfo(marker.Severity, [marker.Message]);
            }
        }

        _lines = lines;
        Refresh();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ErrorLineBrushProperty || change.Property == WarningLineBrushProperty
            || change.Property == ErrorBrushProperty || change.Property == WarningBrushProperty)
        {
            Refresh();
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

        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(MarkerWidth, 0);

    public override void Render(DrawingContext context)
    {
        // Prozirna pozadina da bi margina primala pokazivač (tooltip sa porukom).
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        var view = TextView;
        if (view is null || !view.VisualLinesValid || _lines.Count == 0)
        {
            return;
        }

        var size = Math.Min(MarkerWidth, view.DefaultLineHeight) * 0.62;
        foreach (var visualLine in view.VisualLines)
        {
            var number = visualLine.FirstDocumentLine.LineNumber;
            if (!_lines.TryGetValue(number, out var info))
            {
                continue;
            }

            var top = visualLine.GetTextLineVisualYPosition(visualLine.TextLines[0], VisualYPosition.LineTop) - view.VerticalOffset;
            var height = visualLine.TextLines[0].Height;
            var center = new Point(Bounds.Width / 2, top + (height / 2));
            if (info.Severity == MessageSeverity.Error)
            {
                if (ErrorBrush is { } brush)
                {
                    context.DrawEllipse(brush, null, center, size / 2, size / 2);
                }
            }
            else if (WarningBrush is { } brush)
            {
                var half = size / 2;
                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    ctx.BeginFigure(new Point(center.X, center.Y - half), true);
                    ctx.LineTo(new Point(center.X + half, center.Y + half * 0.85));
                    ctx.LineTo(new Point(center.X - half, center.Y + half * 0.85));
                    ctx.EndFigure(true);
                }

                context.DrawGeometry(brush, null, geometry);
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var tip = GetMessagesAt(e.GetPosition(this).Y);
        if (!Equals(ToolTip.GetTip(this), tip))
        {
            ToolTip.SetTip(this, tip);
            ToolTip.SetIsOpen(this, tip is not null);
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        ToolTip.SetIsOpen(this, false);
        ToolTip.SetTip(this, null);
    }

    private string? GetMessagesAt(double y)
    {
        var view = TextView;
        if (view is null || !view.VisualLinesValid)
        {
            return null;
        }

        var line = view.GetDocumentLineByVisualTop(y + view.VerticalOffset);
        return line is not null && _lines.TryGetValue(line.LineNumber, out var info)
            ? string.Join(Environment.NewLine, info.Messages.Distinct(StringComparer.Ordinal))
            : null;
    }

    private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

    private void Refresh()
    {
        InvalidateVisual();
        TextView?.InvalidateLayer(KnownLayer.Background);
    }

    private sealed record LineInfo(MessageSeverity Severity, List<string> Messages);

    /// <summary>Blaga pozadina linije i podvlačenje teksta linije sa nalazom.</summary>
    private sealed class LineBackgroundRenderer(IssueMarkerMargin owner) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (owner._lines.Count == 0 || !textView.VisualLinesValid || textView.Document is not { } document)
            {
                return;
            }

            foreach (var visualLine in textView.VisualLines)
            {
                var documentLine = visualLine.FirstDocumentLine;
                if (!owner._lines.TryGetValue(documentLine.LineNumber, out var info))
                {
                    continue;
                }

                var isError = info.Severity == MessageSeverity.Error;
                var fill = isError ? owner.ErrorLineBrush : owner.WarningLineBrush;
                var stroke = isError ? owner.ErrorBrush : owner.WarningBrush;

                var top = visualLine.VisualTop - textView.VerticalOffset;
                if (fill is not null)
                {
                    drawingContext.FillRectangle(fill, new Rect(0, top, textView.Bounds.Width, visualLine.Height));
                }

                if (stroke is null)
                {
                    continue;
                }

                // Podvlačenje od prvog znaka koji nije razmak do kraja linije.
                var start = documentLine.Offset;
                while (start < documentLine.EndOffset && char.IsWhiteSpace(document.GetCharAt(start)))
                {
                    start++;
                }

                if (start >= documentLine.EndOffset)
                {
                    continue;
                }

                var segment = new TextSegment { StartOffset = start, EndOffset = documentLine.EndOffset };
                var pen = new Pen(stroke, 1.5);
                foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                {
                    var y = rect.Bottom - 1;
                    drawingContext.DrawLine(pen, new Point(rect.Left, y), new Point(rect.Right, y));
                }
            }
        }
    }
}
