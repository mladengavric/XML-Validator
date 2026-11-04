namespace XmlValidator.App.Services;

/// <summary>
/// Neočekivani izuzeci: upis u log (bez JMB-ova i sadržaja faktura, vidi <see cref="LogSanitizer"/>)
/// i poruka korisniku. Istovremeno se prikazuje najviše jedna poruka, da niz grešaka ne otvori niz prozora.
/// </summary>
public sealed class GlobalExceptionHandler
{
    private readonly IAppLog _log;
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _ui;
    private int _showing;

    public GlobalExceptionHandler(IAppLog log, IDialogService dialogs, IUiDispatcher ui)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(ui);
        _log = log;
        _dialogs = dialogs;
        _ui = ui;
    }

    /// <summary>Hvatanje izuzetaka van UI niti (UI nit povezuje <c>App</c> preko Avalonia dispečera).</summary>
    public void InstallProcessHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                _log.Error("Neobrađen izuzetak; program se zatvara.", ex);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _log.Error("Neobrađen izuzetak u pozadinskom zadatku.", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>Bilježi izuzetak i obavještava korisnika; program nastavlja rad.</summary>
    public void Report(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        _log.Error("Neočekivana greška.", exception);
        if (Interlocked.Exchange(ref _showing, 1) == 1)
        {
            return;
        }

        _ui.Post(async () =>
        {
            try
            {
                await _dialogs.ShowMessageAsync(
                    "Neočekivana greška",
                    "Došlo je do neočekivane greške. Program nastavlja rad, ali posljednja radnja možda nije završena. "
                    + "Ulazni fajl nije mijenjan.",
                    MessageSeverity.Error,
                    [$"{exception.GetType().Name}: {exception.Message}", $"Detalji su upisani u log: {_log.CurrentLogFile}"]);
            }
            catch (Exception ex)
            {
                _log.Error("Poruka o grešci ne može da se prikaže.", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _showing, 0);
            }
        });
    }
}
