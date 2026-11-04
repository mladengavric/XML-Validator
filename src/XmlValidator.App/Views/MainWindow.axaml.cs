using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using XmlValidator.App.ViewModels;

namespace XmlValidator.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // Izbor stavke u meniju nedavnih fajlova zatvara meni.
        RecentPanel.AddHandler(Button.ClickEvent, (_, _) => RecentButton.Flyout?.Hide());
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
