using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using XmlValidator.App.ViewModels;
using XmlValidator.App.Views;

namespace XmlValidator.App.Services;

/// <summary>UI nit preko Avalonia dispečera.</summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }
}

/// <summary>Tema preko <see cref="Application.RequestedThemeVariant"/>.</summary>
public sealed class AvaloniaThemeService : IThemeService
{
    public void Apply(ThemePreference theme)
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = theme switch
            {
                ThemePreference.Light => ThemeVariant.Light,
                ThemePreference.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
        }
    }
}

/// <summary>Dijalozi preko Avalonia prozora i <see cref="IStorageProvider"/>.</summary>
public sealed class AvaloniaDialogService : IDialogService
{
    private readonly IServiceProvider _services;
    private readonly List<Window> _openDialogs = [];

    public AvaloniaDialogService(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <summary>Vlasnik novog dijaloga: posljednji otvoreni dijalog, inače glavni prozor.</summary>
    private Window? Owner =>
        _openDialogs.LastOrDefault()
        ?? (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public async Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FileFilter> filters, string? startFolder)
    {
        if (Owner is not { } owner)
        {
            return null;
        }

        var storage = owner.StorageProvider;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = ToFileTypes(filters),
            SuggestedStartLocation = await TryGetFolderAsync(storage, startFolder),
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveFileAsync(string title, string suggestedFileName, IReadOnlyList<FileFilter> filters, string? startFolder)
    {
        if (Owner is not { } owner)
        {
            return null;
        }

        var storage = owner.StorageProvider;
        var extension = Path.GetExtension(suggestedFileName);
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            DefaultExtension = string.IsNullOrEmpty(extension) ? null : extension.TrimStart('.'),
            FileTypeChoices = ToFileTypes(filters),
            ShowOverwritePrompt = true,
            SuggestedStartLocation = await TryGetFolderAsync(storage, startFolder),
        });
        return file?.TryGetLocalPath();
    }

    public Task ShowMessageAsync(string title, string message, MessageSeverity severity, IReadOnlyList<string>? details = null) =>
        ShowDialogAsync(new DialogViewModel(title, message, severity, "U redu", cancelText: null) { Details = details ?? [] });

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText = "Otkaži", bool isDangerous = false) =>
        await ShowDialogAsync(new DialogViewModel(title, message, isDangerous ? MessageSeverity.Warning : MessageSeverity.Info, confirmText, cancelText)
        {
            IsDangerous = isDangerous,
        });

    public async Task<string?> ChooseAsync(string title, string message, IReadOnlyList<string> options, string confirmText)
    {
        var dialog = new DialogViewModel(title, message, MessageSeverity.Info, confirmText, "Otkaži") { Options = options };
        return await ShowDialogAsync(dialog) ? dialog.SelectedOption : null;
    }

    public async Task<string?> PromptTextAsync(string title, string message, string initialText, string confirmText)
    {
        var dialog = new DialogViewModel(title, message, MessageSeverity.Info, confirmText, "Otkaži") { IsPrompt = true, InputText = initialText };
        return await ShowDialogAsync(dialog) ? dialog.InputText.Trim() : null;
    }

    public async Task ShowSettingsAsync(SettingsPage page, string? importSchemaPath = null)
    {
        // Opseg po prozoru: ViewModel-i stranica se oslobađaju kad se prozor zatvori.
        using var scope = _services.CreateScope();
        var viewModel = scope.ServiceProvider.GetRequiredService<SettingsViewModel>();
        var window = new SettingsWindow { DataContext = viewModel };
        await ShowWindowAsync(window, viewModel.ActivateAsync(page, importSchemaPath));
    }

    private async Task<bool> ShowDialogAsync(DialogViewModel viewModel)
    {
        var window = new DialogWindow { DataContext = viewModel };
        viewModel.CloseRequested += (_, _) => window.Close();
        await ShowWindowAsync(window, null);
        return viewModel.Result == true;
    }

    private async Task ShowWindowAsync(Window window, Task? whileOpen)
    {
        var owner = Owner;
        _openDialogs.Add(window);
        try
        {
            if (owner is null)
            {
                var closed = new TaskCompletionSource();
                window.Closed += (_, _) => closed.TrySetResult();
                window.Show();
                await Task.WhenAll(closed.Task, whileOpen ?? Task.CompletedTask);
            }
            else
            {
                await Task.WhenAll(window.ShowDialog(owner), whileOpen ?? Task.CompletedTask);
            }
        }
        finally
        {
            _openDialogs.Remove(window);
        }
    }

    private static List<FilePickerFileType> ToFileTypes(IReadOnlyList<FileFilter> filters) =>
        [.. filters.Select(f => new FilePickerFileType(f.Name) { Patterns = f.Patterns })];

    private static async Task<IStorageFolder?> TryGetFolderAsync(IStorageProvider storage, string? folder) =>
        !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder)
            ? await storage.TryGetFolderFromPathAsync(folder)
            : null;
}
