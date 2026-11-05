using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using XmlValidator.App.Services;
using XmlValidator.App.ViewModels;
using XmlValidator.App.Views;

namespace XmlValidator.App;

public partial class App : Application
{
    private ServiceProvider? _services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = AppServices.Build(AppPaths.Default);

            var exceptions = _services.GetRequiredService<GlobalExceptionHandler>();
            exceptions.InstallProcessHandlers();
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                e.Handled = true;
                exceptions.Report(e.Exception);
            };

            _services.GetRequiredService<IThemeService>().Apply(_services.GetRequiredService<ISettingsService>().Current.Theme);

            var viewModel = _services.GetRequiredService<MainWindowViewModel>();
            // Glavni prozor se otvara preko cijelog ekrana; Width/Height iz XAML-a ostaju veličina pri vraćanju.
            // Samo ovdje (pravo pokretanje), da bi UI testovi imali stalnu veličinu prozora za snimke.
            var mainWindow = new MainWindow { DataContext = viewModel, WindowState = WindowState.Maximized };
            desktop.MainWindow = mainWindow;
            var shutdown = new CancellationTokenSource();
            desktop.Exit += (_, _) =>
            {
                shutdown.Cancel();
                _services.Dispose();
                shutdown.Dispose();
            };

            // Nadogradnja programa: pitanje za nesačuvane izmjene prije preuzimanja i zatvaranje bez pitanja poslije.
            var appUpdates = _services.GetRequiredService<AppUpdateService>();
            appUpdates.ConfirmCloseAsync = viewModel.ConfirmDiscardChangesAsync;
            appUpdates.Shutdown = () =>
            {
                mainWindow.AllowCloseWithoutConfirmation();
                desktop.Shutdown();
            };

            var (updatedFrom, oldProcessId, args) = AppUpdateService.ParseArguments(desktop.Args ?? []);
            var startupFile = args.Count > 0 ? args[0] : null;
            var schemaUpdates = _services.GetRequiredService<SchemaUpdateService>();
            var token = shutdown.Token;
            Dispatcher.UIThread.Post(async () =>
            {
                await viewModel.InitializeAsync(startupFile);
                await viewModel.RunStartupChecksAsync(appUpdates, schemaUpdates, updatedFrom, oldProcessId, MainWindowViewModel.StartupCheckDelay, token);
            });
        }

        base.OnFrameworkInitializationCompleted();
    }
}
