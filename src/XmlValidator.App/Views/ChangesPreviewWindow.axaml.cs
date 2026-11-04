using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using XmlValidator.App.Controls;
using XmlValidator.App.ViewModels;

namespace XmlValidator.App.Views;

/// <summary>
/// Pregled izmjena. Code-behind radi samo ono što je specifično za prikaz: tekst izabranog bloka u
/// AvaloniaEdit-u (<c>TextEditor.Text</c> nije bindable), brojeve linija ulaznog fajla, boje sintakse
/// po temi i početni fokus (kod opasne potvrde na „Otkaži”, kao u <see cref="DialogWindow"/>).
/// </summary>
public partial class ChangesPreviewWindow : Window
{
    private readonly FileLineNumberMargin _oldLines = new();
    private ChangesPreviewViewModel? _viewModel;

    public ChangesPreviewWindow()
    {
        InitializeComponent();

        ConfigureEditor(OldEditor);
        ConfigureEditor(NewEditor);
        OldEditor.TextArea.LeftMargins.Insert(0, _oldLines);

        ActualThemeVariantChanged += (_, _) => ApplySyntaxColors();
        Opened += (_, _) =>
        {
            ApplySyntaxColors();
            FocusInitialControl();
        };
    }

    /// <summary>Brojevi linija ulaza (za testove).</summary>
    internal FileLineNumberMargin OldLineNumbers => _oldLines;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as ChangesPreviewViewModel;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        ShowSelectedRow();
    }

    private static void ConfigureEditor(TextEditor editor)
    {
        editor.Options.EnableHyperlinks = false;
        editor.Options.EnableEmailHyperlinks = false;
        editor.Options.AllowScrollBelowDocument = false;
        editor.Options.EnableTextDragDrop = false;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChangesPreviewViewModel.SelectedRow))
        {
            ShowSelectedRow();
        }
    }

    /// <summary>ViewModel → editori: tekst bloka u ulazu (sa brojevima linija fajla) i u izlazu.</summary>
    private void ShowSelectedRow()
    {
        var row = _viewModel?.SelectedRow;
        OldEditor.Document = new TextDocument(row?.OldText ?? string.Empty);
        NewEditor.Document = new TextDocument(row is null || row.IsDeleted ? string.Empty : row.NewText);
        _oldLines.FirstLine = row?.Change.StartLine ?? 0;
        OldEditor.ScrollToHome();
        NewEditor.ScrollToHome();

        if (row is not null)
        {
            ChangesGrid.ScrollIntoView(row, null);
        }
    }

    private void FocusInitialControl()
    {
        if (_viewModel is { IsDangerous: true, HasCancel: true })
        {
            CancelButton.Focus();
        }
        else
        {
            ConfirmButton.Focus();
        }
    }

    private void ApplySyntaxColors()
    {
        var highlighting = XmlSyntaxHighlighting.Create(key =>
            this.TryFindResource(key, ActualThemeVariant, out var value) && value is ISolidColorBrush brush
                ? brush.Color
                : null);
        OldEditor.SyntaxHighlighting = highlighting;
        NewEditor.SyntaxHighlighting = highlighting;
    }
}
