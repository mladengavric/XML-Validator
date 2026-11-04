using Microsoft.Extensions.DependencyInjection;
using XmlValidator.App.ViewModels;
using XmlValidator.Core.Cleaning;
using XmlValidator.Core.Schemas;
using XmlValidator.Core.Validation;

namespace XmlValidator.App.Services;

/// <summary>Povezivanje servisa i ViewModel-a (Microsoft.Extensions.DependencyInjection).</summary>
public static class AppServices
{
    /// <summary>Servisi aplikacije sa Avalonia implementacijama dijaloga, teme i UI niti.</summary>
    public static ServiceProvider Build(AppPaths paths)
    {
        var services = new ServiceCollection();
        AddCore(services, paths);
        services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
        services.AddSingleton<IThemeService, AvaloniaThemeService>();
        services.AddSingleton<IDialogService, AvaloniaDialogService>();
        services.AddSingleton<IClipboardService, AvaloniaClipboardService>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    /// <summary>Servisi bez zavisnosti od Avalonije (dijeli se sa testovima).</summary>
    public static IServiceCollection AddCore(IServiceCollection services, AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(paths);
        services.AddSingleton(paths);
        services.AddSingleton<IAppLog, FileAppLog>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton(_ => new SchemaStore(paths.SchemasFolder));
        services.AddSingleton(_ => new SchemaDownloader());
        services.AddSingleton<StoreSchemaProvider>();
        services.AddSingleton<ISchemaProvider>(sp => sp.GetRequiredService<StoreSchemaProvider>());
        services.AddSingleton<XmlValidationService>();
        services.AddSingleton<CleaningService>();
        services.AddSingleton<GlobalExceptionHandler>();

        services.AddSingleton<ValidationTabViewModel>();
        services.AddSingleton<CleaningTabViewModel>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddTransient<GeneralSettingsViewModel>();
        services.AddTransient<SchemasSettingsViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<AboutViewModel>();
        return services;
    }
}
