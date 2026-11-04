using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using XmlValidator.App.Controls;
using XmlValidator.App.ViewModels;

namespace XmlValidator.App.Views;

/// <summary>
/// Prikaz taba „Čišćenje faktura”. Code-behind radi samo ono što je specifično za prikaz:
/// višestruki izbor u tabeli (<c>SelectedItems</c> nije bindable), skrolovanje do reda, tekst pregleda i
/// editora bloka u AvaloniaEdit-u (<c>TextEditor.Text</c> nije bindable), brojeve linija i označenu liniju
/// u pregledu, skok iz liste popravki i boje sintakse po temi.
/// </summary>
public partial class CleaningTabView : UserControl
{
    private readonly FileLineNumberMargin _previewLines = new();
    private readonly FileLineNumberMargin _editLines = new();
    private CleaningTabViewModel? _viewModel;
    private bool _syncingEditText;
    private (GridLength Invoices, GridLength Detail)? _heightsBeforeEdit;

    public CleaningTabView()
    {
        InitializeComponent();

        ConfigureEditor(PreviewEditor);
        ConfigureEditor(EditEditor);
        PreviewEditor.TextArea.LeftMargins.Insert(0, _previewLines);
        EditEditor.TextArea.LeftMargins.Insert(0, _editLines);

        EditEditor.TextChanged += OnEditTextChanged;
        ActualThemeVariantChanged += (_, _) => ApplySyntaxColors();

        InvoicesGrid.SelectionChanged += OnInvoiceSelectionChanged;
        NotesList.SelectionChanged += OnNoteSelectionChanged;
        NotesList.AddHandler(TappedEvent, OnNoteTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        NotesList.AddHandler(KeyDownEvent, OnNotesKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnViewKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>Visina tabele faktura u rasporedu (za testove).</summary>
    internal GridLength InvoicesPaneHeight => Panes.RowDefinitions[0].Height;

    /// <summary>Brojevi linija pregleda (za testove).</summary>
    internal FileLineNumberMargin PreviewLineNumbers => _previewLines;

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
            _viewModel.RowNavigationRequested -= OnRowNavigationRequested;
        }

        _viewModel = DataContext as CleaningTabViewModel;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.RowNavigationRequested += OnRowNavigationRequested;
        }

        UpdatePreview();
        LoadEditText();
        UpdateEditLayout();
    }

    private static void ConfigureEditor(TextEditor editor)
    {
        editor.Options.EnableHyperlinks = false;
        editor.Options.EnableEmailHyperlinks = false;
        editor.Options.ConvertTabsToSpaces = false;
        editor.Options.AllowScrollBelowDocument = false;
        editor.Options.EnableTextDragDrop = false;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CleaningTabViewModel.PreviewText)
                or nameof(CleaningTabViewModel.PreviewFirstLine)
                or nameof(CleaningTabViewModel.PreviewHighlightLine):
                UpdatePreview();
                break;
            case nameof(CleaningTabViewModel.EditText) when !_syncingEditText:
                LoadEditText();
                break;
            case nameof(CleaningTabViewModel.IsEditing):
                UpdateEditLayout();
                if (_viewModel is { IsEditing: true, EditingRow: { } editing })
                {
                    // Editor se upravo pojavio: red koji se uređuje ostaje vidljiv, a fokus ide u tekst.
                    Dispatcher.UIThread.Post(() =>
                    {
                        InvoicesGrid.ScrollIntoView(editing, null);
                        EditEditor.TextArea.Focus();
                    }, DispatcherPriority.Background);
                }

                break;
        }
    }

    // ---------------------------------------------------------------- tabela

    private void OnInvoiceSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        _viewModel?.SetSelectedRows(InvoicesGrid.SelectedItems.OfType<InvoiceRowViewModel>());

    private void OnRowNavigationRequested(object? sender, InvoiceRowViewModel row) =>
        Dispatcher.UIThread.Post(() => InvoicesGrid.ScrollIntoView(row, null), DispatcherPriority.Background);

    // ---------------------------------------------------------------- pregled

    /// <summary>ViewModel → pregled: tekst, brojevi linija ulaznog fajla i označena linija.</summary>
    private void UpdatePreview()
    {
        var text = _viewModel?.PreviewText ?? string.Empty;
        if (!string.Equals(PreviewEditor.Document?.Text, text, StringComparison.Ordinal))
        {
            PreviewEditor.Document = new TextDocument(text);
            PreviewEditor.ScrollToHome();
        }

        _previewLines.FirstLine = _viewModel?.PreviewFirstLine ?? 0;
        var highlight = _viewModel?.PreviewHighlightLine ?? 0;
        _previewLines.HighlightLine = highlight;
        if (highlight > 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (PreviewEditor.Document is { } document && highlight <= document.LineCount)
                {
                    PreviewEditor.ScrollToLine(highlight);
                }
            }, DispatcherPriority.Background);
        }
    }

    // ---------------------------------------------------------------- uređivanje

    /// <summary>ViewModel → editor bloka (novi dokument: istorija poništavanja ne prelazi sa druge fakture).</summary>
    private void LoadEditText()
    {
        var text = _viewModel?.EditText ?? string.Empty;
        if (string.Equals(EditEditor.Document?.Text, text, StringComparison.Ordinal))
        {
            return;
        }

        _syncingEditText = true;
        try
        {
            EditEditor.Document = new TextDocument(text);
            EditEditor.ScrollToHome();
            EditEditor.TextArea.Caret.Offset = 0;
        }
        finally
        {
            _syncingEditText = false;
        }
    }

    /// <summary>
    /// Dok se blok uređuje, editor dobija veći dio visine (tabela ostaje vidljiva); poslije uređivanja
    /// vraća se raspored koji je korisnik imao (i ako je pomjerio granicu).
    /// </summary>
    private void UpdateEditLayout()
    {
        var rows = Panes.RowDefinitions;
        if (_viewModel is { IsEditing: true })
        {
            if (_heightsBeforeEdit is null
                && this.TryFindResource("EditingInvoicesPaneHeight", out var invoices) && invoices is GridLength invoicesHeight
                && this.TryFindResource("EditingDetailPaneHeight", out var detail) && detail is GridLength detailHeight)
            {
                _heightsBeforeEdit = (rows[0].Height, rows[2].Height);
                rows[0].Height = invoicesHeight;
                rows[2].Height = detailHeight;
            }
        }
        else if (_heightsBeforeEdit is { } previous)
        {
            rows[0].Height = previous.Invoices;
            rows[2].Height = previous.Detail;
            _heightsBeforeEdit = null;
        }
    }

    /// <summary>Editor bloka → ViewModel, pri svakoj izmjeni korisnika.</summary>
    private void OnEditTextChanged(object? sender, EventArgs e)
    {
        if (_syncingEditText || _viewModel is not { IsEditing: true })
        {
            return;
        }

        _syncingEditText = true;
        try
        {
            _viewModel.EditText = EditEditor.Text;
        }
        finally
        {
            _syncingEditText = false;
        }
    }

    // ---------------------------------------------------------------- popravke i upozorenja

    private void OnNoteSelectionChanged(object? sender, SelectionChangedEventArgs e) => GoToSelectedNote();

    /// <summary>Klik na već izabranu stavku takođe vodi na fakturu.</summary>
    private void OnNoteTapped(object? sender, TappedEventArgs e) => GoToSelectedNote();

    private void OnNotesKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && NotesList.SelectedItem is CleaningNoteRow)
        {
            e.Handled = true;
            GoToSelectedNote();
        }
    }

    private void GoToSelectedNote()
    {
        if (NotesList.SelectedItem is CleaningNoteRow { CanNavigate: true } note)
        {
            _viewModel?.GoToNoteCommand.Execute(note);
        }
    }

    // ---------------------------------------------------------------- tastatura

    /// <summary>Ctrl+F: fokus u pretragu faktura.</summary>
    private void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control && SearchBox.IsEffectivelyVisible)
        {
            e.Handled = true;
            SearchBox.Focus();
            SearchBox.SelectAll();
        }
    }

    // ---------------------------------------------------------------- tema

    private void ApplySyntaxColors()
    {
        var highlighting = XmlSyntaxHighlighting.Create(key =>
            this.TryFindResource(key, ActualThemeVariant, out var value) && value is ISolidColorBrush brush
                ? brush.Color
                : null);
        PreviewEditor.SyntaxHighlighting = highlighting;
        EditEditor.SyntaxHighlighting = highlighting;
    }
}
