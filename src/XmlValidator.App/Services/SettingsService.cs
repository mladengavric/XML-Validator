using System.Text.Json;

namespace XmlValidator.App.Services;

/// <summary>Čitanje i čuvanje podešavanja aplikacije.</summary>
public interface ISettingsService
{
    /// <summary>Trenutna podešavanja.</summary>
    AppSettings Current { get; }

    /// <summary>Poruka ako podešavanja nisu mogla da se učitaju (pa se koriste podrazumijevana); inače <c>null</c>.</summary>
    string? LoadProblem { get; }

    /// <summary>Poruka ako posljednje čuvanje nije uspjelo; inače <c>null</c>.</summary>
    string? SaveProblem { get; }

    /// <summary>Podešavanja su promijenjena.</summary>
    event EventHandler? Changed;

    /// <summary>Mijenja podešavanja i odmah ih čuva. Ako čuvanje ne uspije, izmjena važi do zatvaranja programa.</summary>
    void Update(Func<AppSettings, AppSettings> change);
}

/// <summary>
/// Podešavanja u JSON fajlu u korisničkom app-data folderu. Oštećen fajl se ne briše: preimenuje se u
/// <c>settings.json.ostecen-&lt;vrijeme&gt;</c>, a program nastavlja sa podrazumijevanim vrijednostima.
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly IAppLog _log;
    private readonly TimeProvider _time;
    private readonly Lock _sync = new();
    private AppSettings _current;

    public SettingsService(AppPaths paths, IAppLog log, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);
        _path = paths.SettingsFile;
        _log = log;
        _time = time ?? TimeProvider.System;
        _current = Load();
    }

    public AppSettings Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    public string? LoadProblem { get; private set; }

    public string? SaveProblem { get; private set; }

    public event EventHandler? Changed;

    public void Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_sync)
        {
            var updated = change(_current).Normalize();
            if (updated.Equals(_current))
            {
                return;
            }

            _current = updated;
            Save(updated);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private AppSettings Load()
    {
        if (!File.Exists(_path))
        {
            return new AppSettings();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllBytes(_path), JsonOptions)
                ?? throw new JsonException("Fajl je prazan.");
            return settings.Normalize();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            var backup = $"{_path}.ostecen-{_time.GetLocalNow():yyyyMMdd-HHmmss}";
            try
            {
                File.Move(_path, backup, overwrite: true);
                LoadProblem = $"Podešavanja su bila oštećena, pa se koriste podrazumijevana. Stari fajl je sačuvan kao „{Path.GetFileName(backup)}”.";
            }
            catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException)
            {
                LoadProblem = "Podešavanja su oštećena, pa se koriste podrazumijevana.";
            }

            _log.Warning("Podešavanja nisu mogla da se učitaju.", ex);
            return new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoadProblem = $"Podešavanja ne mogu da se pročitaju ({ex.Message}), pa se koriste podrazumijevana.";
            _log.Warning("Podešavanja ne mogu da se pročitaju.", ex);
            return new AppSettings();
        }
    }

    private void Save(AppSettings settings)
    {
        try
        {
            AtomicFile.WriteAllBytes(_path, JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions));
            SaveProblem = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveProblem = $"Podešavanja ne mogu da se sačuvaju: {ex.Message}";
            _log.Warning("Podešavanja ne mogu da se sačuvaju.", ex);
        }
    }
}
