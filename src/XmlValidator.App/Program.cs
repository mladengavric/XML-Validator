using Avalonia;
using XmlValidator.App.Services;

namespace XmlValidator.App;

internal static class Program
{
    // Prije AppMain ne koristiti Avaloniju ni kod koji zavisi od SynchronizationContext-a.
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // Greška pri pokretanju (prije nego što UI može da prikaže poruku): bar u log.
            new FileAppLog(AppPaths.Default).Error("Program ne može da se pokrene.", ex);
            throw;
        }
    }

    // Koristi ga i vizuelni dizajner; ne uklanjati.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
