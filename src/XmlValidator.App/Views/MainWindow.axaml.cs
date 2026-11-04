using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using XmlValidator.App.ViewModels;

namespace XmlValidator.App.Views;

public partial class MainWindow : Window
{
    private const int ValidationTabIndex = 0;

    private bool _closeConfirmed;
    private bool _closeQuestionOpen;

    public MainWindow()
    {
        InitializeComponent();

        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // Izbor stavke u meniju nedavnih fajlova zatvara meni.
        RecentPanel.AddHandler(Button.ClickEvent, (_, _) => RecentButton.Flyout?.Hide());

        // F5 validira samo kad je aktivan tab „Validacija” (prečica važi i dok je fokus u editoru).
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5 && e.KeyModifiers == KeyModifiers.None
            && DataContext is MainWindowViewModel { SelectedTabIndex: ValidationTabIndex } viewModel
            && viewModel.Validation.ValidateCommand.CanExecute(null))
        {
            e.Handled = true;
            viewModel.Validation.ValidateCommand.Execute(null);
        }
    }

    /// <summary>
    /// Zatvaranje sa nesačuvanim izmjenama u editoru: zatvaranje se otkazuje, korisnik se pita
    /// (asinhrono), pa se prozor zatvara samo ako potvrdi.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _closeConfirmed || DataContext is not MainWindowViewModel viewModel
            || !viewModel.Validation.HasUnsavedChanges)
        {
            return;
        }

        e.Cancel = true;
        if (!_closeQuestionOpen)
        {
            _ = ConfirmCloseAsync(viewModel);
        }
    }

    private async Task ConfirmCloseAsync(MainWindowViewModel viewModel)
    {
        _closeQuestionOpen = true;
        try
        {
            if (!await viewModel.ConfirmDiscardChangesAsync())
            {
                return;
            }
        }
        finally
        {
            _closeQuestionOpen = false;
        }

        // Zatvaranje van obrade događaja Closing (dijalog može da se završi i sinhrono).
        _closeConfirmed = true;
        Dispatcher.UIThread.Post(Close);
    }

    /// <summary>Lokalne putanje prevučenih fajlova (prazno ako se ne prevlače fajlovi).</summary>
    internal static IReadOnlyList<string> GetDroppedPaths(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles() is { } files
            ? [.. files.Select(f => f.TryGetLocalPath()).OfType<string>()]
            : [];

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var hasFiles = e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.IsVisible = hasFiles;
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => DropOverlay.IsVisible = false;

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        DropOverlay.IsVisible = false;
        var paths = GetDroppedPaths(e);
        if (paths.Count > 0 && DataContext is MainWindowViewModel viewModel)
        {
            e.Handled = true;
            await viewModel.HandleDroppedFilesAsync(paths);
        }
    }
}
