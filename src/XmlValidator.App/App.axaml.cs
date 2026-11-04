using Avalonia;
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
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            desktop.Exit += (_, _) => _services.Dispose();

            var startupFile = desktop.Args is { Length: > 0 } args ? args[0] : null;
            Dispatcher.UIThread.Post(async () => await viewModel.InitializeAsync(startupFile));
        }

        base.OnFrameworkInitializationCompleted();
    }
}
