using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Schema;
using XmlValidator.Core.Cleaning;

namespace XmlValidator.App.Services;

/// <summary>Log aplikacije. Nikad ne sadrži JMB-ove ni sadržaj faktura.</summary>
public interface IAppLog
{
    /// <summary>Putanja trenutnog log fajla.</summary>
    string CurrentLogFile { get; }

    void Info(string message);

    void Warning(string message, Exception? exception = null);

    void Error(string message, Exception exception);
}

/// <summary>
/// Log u fajlovima <c>logs/app-yyyy-MM-dd.log</c> (UTF-8); čuva se posljednjih
/// <see cref="RetainedFiles"/> fajlova. Greška pri upisu loga se nikad ne propagira.
/// </summary>
public sealed class FileAppLog : IAppLog
{
    /// <summary>Broj dnevnih log fajlova koji se čuvaju.</summary>
    public const int RetainedFiles = 14;

    private readonly string _folder;
    private readonly TimeProvider _time;
    private readonly Lock _sync = new();

    public FileAppLog(AppPaths paths, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _folder = paths.LogsFolder;
        _time = time ?? TimeProvider.System;
        DeleteOldFiles();
    }

    public string CurrentLogFile =>
        Path.Combine(_folder, $"app-{_time.GetLocalNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.log");

    public void Info(string message) => Write("INFO", message, null);

    public void Warning(string message, Exception? exception = null) => Write("UPOZORENJE", message, exception);

    public void Error(string message, Exception exception) => Write("GREŠKA", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        var sb = new StringBuilder()
            .Append(_time.GetLocalNow().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(" [").Append(level).Append("] ")
            .Append(LogSanitizer.Sanitize(message))
            .Append(Environment.NewLine);
        if (exception is not null)
        {
            sb.Append(LogSanitizer.Describe(exception)).Append(Environment.NewLine);
        }

        try
        {
            lock (_sync)
            {
                Directory.CreateDirectory(_folder);
                File.AppendAllText(CurrentLogFile, sb.ToString(), new UTF8Encoding(false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Log nije dostupan; program nastavlja rad.
        }
    }

    private void DeleteOldFiles()
    {
        try
        {
            if (!Directory.Exists(_folder))
            {
                return;
            }

            foreach (var old in Directory.GetFiles(_folder, "app-*.log").Order(StringComparer.Ordinal).SkipLast(RetainedFiles))
            {
                File.Delete(old);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Stari logovi ostaju; nije bitno za rad.
        }
    }
}

/// <summary>
/// Priprema tekst za log tako da ne sadrži lične podatke: poruke XML grešaka (mogu citirati sadržaj
/// fakture) se izostavljaju, a svaki niz od 13 cifara (JMB) se maskira u ostalim porukama.
/// </summary>
public static class LogSanitizer
{
    private const int MaxMessageLength = 500;

    public static string Sanitize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var masked = CleaningText.MaskJmbInText(text);
        return masked.Length > MaxMessageLength ? masked[..MaxMessageLength] + "…" : masked;
    }

    /// <summary>Tip, poruka (ako je bezbjedna) i stack trace izuzetka, uključujući unutrašnje izuzetke.</summary>
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var sb = new StringBuilder();
        var depth = 0;
        for (var current = exception; current is not null && depth < 8; current = current.InnerException, depth++)
        {
            if (depth > 0)
            {
                sb.Append(Environment.NewLine).Append("--- unutrašnji izuzetak ---").Append(Environment.NewLine);
            }

            sb.Append(current.GetType().FullName).Append(": ").Append(SafeMessage(current));
            if (current.StackTrace is { } trace)
            {
                sb.Append(Environment.NewLine).Append(trace);
            }
        }

        return sb.ToString();
    }

    private static string SafeMessage(Exception exception) => exception switch
    {
        XmlException xml => $"(poruka izostavljena: može sadržati podatke iz fajla; linija {xml.LineNumber}, kolona {xml.LinePosition})",
        XmlSchemaException schema => $"(poruka izostavljena: može sadržati podatke iz fajla; linija {schema.LineNumber}, kolona {schema.LinePosition})",
        _ => Sanitize(exception.Message),
    };
}
