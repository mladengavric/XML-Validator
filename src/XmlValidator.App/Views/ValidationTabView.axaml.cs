using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using XmlValidator.App.Controls;
using XmlValidator.App.ViewModels;

namespace XmlValidator.App.Views;

/// <summary>
/// Prikaz taba „Validacija”. Code-behind radi samo ono što je specifično za AvaloniaEdit:
/// sinhronizaciju teksta sa ViewModel-om (<c>TextEditor.Text</c> nije bindable), skok na mjesto nalaza,
/// oznake linija sa nalazima i boje sintakse po temi.
/// </summary>
public partial class ValidationTabView : UserControl
{
    private readonly IssueMarkerMargin _markers = new();
    private ValidationTabViewModel? _viewModel;
    private bool _syncingText;
    private bool _markersPending;
    private bool _keepFocusInList;

    public ValidationTabView()
    {
        InitializeComponent();

        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
        Editor.Options.ConvertTabsToSpaces = false;
        Editor.Options.AllowScrollBelowDocument = false;
        Editor.Options.EnableTextDragDrop = false;

        // Oznake nalaza su lijevo od brojeva linija.
        Editor.TextArea.LeftMargins.Insert(0, _markers);

        Editor.TextChanged += OnEditorTextChanged;
        Editor.TextArea.Caret.PositionChanged += (_, _) => UpdateCaretPosition();
        ActualThemeVariantChanged += (_, _) => ApplySyntaxColors();

        IssuesGrid.SelectionChanged += OnIssueSelectionChanged;
        IssuesGrid.CellPointerPressed += OnIssueCellPointerPressed;
        IssuesGrid.AddHandler(KeyDownEvent, OnIssuesGridKeyDown, RoutingStrategies.Tunnel);

        UpdateCaretPosition();
    }

    /// <summary>Margina sa oznakama nalaza (za testove).</summary>
    internal IssueMarkerMargin Markers => _markers;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplySyntaxColors();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.NavigationRequested -= OnNavigationRequested;
            _viewModel.Issues.CollectionChanged -= OnIssuesChanged;
            _viewModel.IssueLinesShifted -= OnIssueLinesShifted;
        }

        _viewModel = DataContext as ValidationTabViewModel;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.NavigationRequested += OnNavigationRequested;
            _viewModel.Issues.CollectionChanged += OnIssuesChanged;
            _viewModel.IssueLinesShifted += OnIssueLinesShifted;
        }

        LoadTextFromViewModel();
        RebuildMarkers();
    }

    // ---------------------------------------------------------------- tekst

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ValidationTabViewModel.EditorText) && !_syncingText)
        {
            LoadTextFromViewModel();
        }
    }

    /// <summary>ViewModel → editor: samo kad se tekst razlikuje (npr. otvoren je drugi fajl).</summary>
    private void LoadTextFromViewModel()
    {
        var text = _viewModel?.EditorText ?? string.Empty;
        if (string.Equals(Editor.Document?.Text, text, StringComparison.Ordinal))
        {
            return;
        }

        _syncingText = true;
        try
        {
            // Novi dokument: istorija poništavanja ne prelazi sa prethodnog fajla.
            Editor.Document = new TextDocument(text);
            Editor.ScrollToHome();
            Editor.TextArea.Caret.Offset = 0;
        }
        finally
        {
            _syncingText = false;
        }

        UpdateCaretPosition();
    }

    /// <summary>Editor → ViewModel, pri svakoj izmjeni korisnika.</summary>
    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_syncingText || _viewModel is null)
        {
            return;
        }

        _syncingText = true;
        try
        {
            _viewModel.EditorText = Editor.Text;
        }
        finally
        {
            _syncingText = false;
        }
    }

    private void UpdateCaretPosition()
    {
        var caret = Editor.TextArea.Caret;
        CaretPositionText.Text = string.Format(CultureInfo.CurrentCulture, "Linija {0}, kolona {1}", caret.Line, caret.Column);
    }

    // ---------------------------------------------------------------- nalazi

    /// <summary>Linije nalaza su pomjerene izmjenom teksta: oznake prate nove linije.</summary>
    private void OnIssueLinesShifted(object? sender, EventArgs e) => OnIssuesChanged(sender, null);

    private void OnIssuesChanged(object? sender, NotifyCollectionChangedEventArgs? e)
    {
        // Kolekcija se puni red po red; oznake se računaju jednom, poslije cijele izmjene.
        if (_markersPending)
        {
            return;
        }

        _markersPending = true;
        Dispatcher.UIThread.Post(RebuildMarkers, DispatcherPriority.Background);
    }

    private void RebuildMarkers()
    {
        _markersPending = false;
        var issues = _viewModel?.Issues;
        _markers.SetMarkers(issues is null
            ? []
            : issues.Where(r => r.HasPosition).Select(r => new LineMarker(r.Line, r.Severity, r.Message)));
    }

    private void OnIssueSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (IssuesGrid.SelectedItem is ValidationIssueRow row)
        {
            IssuesGrid.ScrollIntoView(row, null);

            // Redovi sa prelomljenom porukom imaju različite visine; prvo pomjeranje ide po procjeni visine,
            // pa se poslije rasporeda ponavlja da bi izabran red sigurno bio vidljiv.
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(IssuesGrid.SelectedItem, row))
                {
                    IssuesGrid.ScrollIntoView(row, null);
                }
            }, DispatcherPriority.Loaded);
            _viewModel?.GoToIssueCommand.Execute(row);
        }

        _keepFocusInList = false;
    }

    /// <summary>Klik na već izabran red takođe vodi na mjesto u editoru.</summary>
    private void OnIssueCellPointerPressed(object? sender, DataGridCellPointerPressedEventArgs e)
    {
        if (e.Row.DataContext is ValidationIssueRow row && ReferenceEquals(row, IssuesGrid.SelectedItem))
        {
            _viewModel?.GoToIssueCommand.Execute(row);
        }
    }

    /// <summary>
    /// Strelice u listi: editor prati izbor, a fokus ostaje u listi. Enter: skok i fokus u editor.
    /// </summary>
    private void OnIssuesGridKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End:
                _keepFocusInList = true;
                break;
            case Key.Enter when IssuesGrid.SelectedItem is ValidationIssueRow row:
                e.Handled = true;
                _keepFocusInList = false;
                _viewModel?.GoToIssueCommand.Execute(row);
                break;
        }
    }

    private void OnNavigationRequested(object? sender, EditorNavigationEventArgs e)
    {
        var document = Editor.Document;
        if (document is null || document.LineCount == 0)
        {
            return;
        }

        var lineNumber = Math.Clamp(e.Line, 1, document.LineCount);
        var line = document.GetLineByNumber(lineNumber);
        var (start, length) = FindHighlight(document, line, e.Column);

        Editor.TextArea.Caret.Offset = start;
        Editor.Select(start, length);
        Editor.ScrollTo(lineNumber, Math.Max(1, start - line.Offset + 1));

        if (!_keepFocusInList)
        {
            Editor.TextArea.Focus();
        }
    }

    /// <summary>
    /// Dio linije koji se označava: od kolone do kraja naziva (elementa ili atributa), a bez kolone
    /// cijela linija bez uvlačenja.
    /// </summary>
    internal static (int Start, int Length) FindHighlight(TextDocument document, DocumentLine line, int column)
    {
        var lineStart = line.Offset;
        var lineEnd = line.EndOffset;

        if (column <= 0 || lineStart + column - 1 >= lineEnd)
        {
            var first = lineStart;
            while (first < lineEnd && char.IsWhiteSpace(document.GetCharAt(first)))
            {
                first++;
            }

            return first < lineEnd ? (first, lineEnd - first) : (lineStart, lineEnd - lineStart);
        }

        var start = lineStart + column - 1;
        var end = start;
        if (document.GetCharAt(end) is '<' or '/' or '?')
        {
            end++;
        }

        while (end < lineEnd && IsNameChar(document.GetCharAt(end)))
        {
            end++;
        }

        if (end == start)
        {
            end = start + 1;
        }

        return (start, end - start);
    }

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or ':';

    // ---------------------------------------------------------------- tema

    private void ApplySyntaxColors()
    {
        Editor.SyntaxHighlighting = XmlSyntaxHighlighting.Create(key =>
            this.TryFindResource(key, ActualThemeVariant, out var value) && value is ISolidColorBrush brush
                ? brush.Color
                : null);
    }
}
