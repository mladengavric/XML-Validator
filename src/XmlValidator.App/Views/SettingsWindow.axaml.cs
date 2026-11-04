using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using XmlValidator.App.ViewModels;

namespace XmlValidator.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();

        // Prevlačenje .xsd/.zip fajla na stranicu „Šeme” pokreće uvoz (samo prvi fajl).
        SchemasPage.AddHandler(DragDrop.DragEnterEvent, OnSchemaDragOver);
        SchemasPage.AddHandler(DragDrop.DragOverEvent, OnSchemaDragOver);
        SchemasPage.AddHandler(DragDrop.DragLeaveEvent, (_, _) => SchemaDropOverlay.IsVisible = false);
        SchemasPage.AddHandler(DragDrop.DropEvent, OnSchemaDrop);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnSchemaDragOver(object? sender, DragEventArgs e)
    {
        var hasFiles = e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        SchemaDropOverlay.IsVisible = hasFiles;
        e.Handled = true;
    }

    private async void OnSchemaDrop(object? sender, DragEventArgs e)
    {
        SchemaDropOverlay.IsVisible = false;
        var paths = MainWindow.GetDroppedPaths(e);
        e.Handled = true;
        if (paths.Count > 0 && DataContext is SettingsViewModel viewModel)
        {
            await viewModel.Schemas.ImportFromPathAsync(paths[0]);
        }
    }
}
