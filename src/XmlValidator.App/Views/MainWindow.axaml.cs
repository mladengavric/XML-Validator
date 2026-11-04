using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using XmlValidator.App.ViewModels;

namespace XmlValidator.App.Views;

public partial class MainWindow : Window
{
    private const int ValidationTabIndex = 0;
    private const int CleaningTabIndex = 1;

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

        // F5 radi glavnu akciju aktivnog taba (prečica važi i dok je fokus u editoru).
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        // Prečice aktivnog taba rade i kad je fokus van taba (traka, zaglavlje taba, statusna traka).
        AddHandler(KeyDownEvent, OnActiveTabKeyDown, RoutingStrategies.Bubble);
    }

    /// <summary>Prikaz aktivnog taba (ako je fajl otvoren), za prečice tog taba.</summary>
    internal Control? ActiveTabView => DataContext is MainWindowViewModel viewModel
        ? viewModel.SelectedTabIndex switch
        {
            ValidationTabIndex => ValidationView,
            CleaningTabIndex => CleaningView,
            _ => null,
        }
        : null;

    /// <summary>
    /// Prečice taba (<c>KeyBindings</c> prikaza taba) važe dok je tab izabran, i kad je fokus van njega.
    /// Ako je fokus u tabu, prikaz ih je već obradio pri prolasku događaja.
    /// </summary>
    private void OnActiveTabKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || ActiveTabView is not { IsEffectivelyVisible: true } view
            || (e.Source is Visual source && (ReferenceEquals(source, view) || view.IsVisualAncestorOf(source))))
        {
            return;
        }

        foreach (var binding in view.KeyBindings)
        {
            if (binding.Gesture?.Matches(e) == true && binding.Command is { } command
                && command.CanExecute(binding.CommandParameter))
            {
                e.Handled = true;
                command.Execute(binding.CommandParameter);
                return;
            }
        }
    }

    /// <summary>F5: u tabu „Validacija” validira, u tabu „Čišćenje faktura” ponovo analizira.</summary>
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.F5 || e.KeyModifiers != KeyModifiers.None || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var command = viewModel.SelectedTabIndex switch
        {
            ValidationTabIndex => viewModel.Validation.ValidateCommand,
            CleaningTabIndex => viewModel.Cleaning.AnalyzeCommand,
            _ => null,
        };

        if (command is not null && command.CanExecute(null))
        {
            e.Handled = true;
            command.Execute(null);
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
            || !viewModel.HasUnsavedChanges)
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
