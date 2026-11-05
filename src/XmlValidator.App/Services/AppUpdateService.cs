using System.Diagnostics;
using System.Runtime.InteropServices;
using XmlValidator.App.ViewModels;
using XmlValidator.Core.Updates;

namespace XmlValidator.App.Services;

/// <summary>Preuzeta nova verzija, spremna za zamjenu.</summary>
/// <param name="Release">Release iz kojeg je preuzeta.</param>
/// <param name="NewExecutablePath">Preuzet izvršni fajl pored postojećeg (<c>XmlValidator.exe.novo</c>).</param>
public sealed record PreparedUpdate(ReleaseInfo Release, string NewExecutablePath);

/// <summary>
/// Nadogradnja programa sa GitHub release-a: provjera verzije, preuzimanje izvršnog fajla za ovaj sistem
/// pored postojećeg, zamjena (postojeći fajl se preimenuje u <c>.staro</c>, jer pokrenut program na Windowsu
/// ne može da se prepiše, ali može da se preimenuje) i pokretanje nove verzije. Nova verzija pri pokretanju
/// briše <c>.staro</c> i javlja korisniku da je nadogradnja završena.
/// </summary>
public sealed class AppUpdateService
{
    /// <summary>Argument kojim stara verzija pokreće novu poslije zamjene: <c>--nadogradnja-zavrsena &lt;stara verzija&gt; &lt;pid&gt;</c>.</summary>
    public const string FinishedArgument = "--nadogradnja-zavrsena";

    private const string NewSuffix = ".novo";
    private const string OldSuffix = ".staro";

    private readonly GitHubReleaseClient _client;
    private readonly AppPaths _paths;
    private readonly IAppLog _log;

    public AppUpdateService(GitHubReleaseClient client, AppPaths paths, IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);
        _client = client;
        _paths = paths;
        _log = log;
        CurrentVersion = GitHubReleaseClient.ParseVersion(AboutViewModel.ReadVersion(typeof(AppUpdateService).Assembly)) ?? new Version(0, 0, 0);
    }

    /// <summary>Verzija programa koji radi.</summary>
    public Version CurrentVersion { get; }

    /// <summary>Sistem za koji se traži nova verzija; <c>null</c> za sisteme za koje nema izvršnog fajla.</summary>
    public static UpdatePlatform? Platform =>
        RuntimeInformation.OSArchitecture != Architecture.X64 ? null
        : OperatingSystem.IsWindows() ? UpdatePlatform.Windows
        : OperatingSystem.IsLinux() ? UpdatePlatform.Linux
        : null;

    /// <summary>Naziv sistema za prikaz.</summary>
    public static string PlatformText => Platform switch
    {
        UpdatePlatform.Windows => "Windows (x64)",
        UpdatePlatform.Linux => "Linux (x64)",
        _ => RuntimeInformation.OSDescription,
    };

    /// <summary>
    /// Zašto program ne može sam da se nadogradi (npr. pokrenut iz razvojnog okruženja); <c>null</c> kad može.
    /// Tada se nova verzija i dalje prikazuje, sa adresom za ručno preuzimanje.
    /// </summary>
    public string? InstallUnavailableReason
    {
        get
        {
            if (Platform is null)
            {
                return "Za ovaj operativni sistem nema gotove verzije programa na GitHub-u.";
            }

            // Objavljen program je jedan fajl; iz razvojnog okruženja (dotnet run) i testova pored njega je XmlValidator.dll.
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, typeof(AppUpdateService).Assembly.GetName().Name + ".dll")) || ExecutablePath is null)
            {
                return "Program nije pokrenut iz objavljenog izvršnog fajla (npr. iz razvojnog okruženja), pa ne može sam da se nadogradi.";
            }

            return null;
        }
    }

    /// <summary>Potvrda zatvaranja kad ima nesačuvanih izmjena (postavlja aplikacija; <c>null</c> = nema pitanja).</summary>
    public Func<Task<bool>>? ConfirmCloseAsync { get; set; }

    /// <summary>Zatvaranje programa bez pitanja, poslije pokretanja nove verzije (postavlja aplikacija).</summary>
    public Action? Shutdown { get; set; }

    private static string? ExecutablePath =>
        Environment.ProcessPath is { } path && File.Exists(path) ? Path.GetFullPath(path) : null;

    /// <summary>Posljednja objavljena verzija na GitHub-u.</summary>
    /// <exception cref="UpdateException">Provjera nije uspjela (poruka je za korisnika).</exception>
    public Task<ReleaseInfo> GetLatestAsync(CancellationToken cancellationToken = default) => _client.GetLatestAsync(cancellationToken);

    /// <summary>Da li je release novija verzija od ove.</summary>
    public bool IsNewer(ReleaseInfo release)
    {
        ArgumentNullException.ThrowIfNull(release);
        return release.Version > CurrentVersion;
    }

    /// <summary>Preuzima izvršni fajl nove verzije pored postojećeg (ništa se još ne zamjenjuje).</summary>
    /// <exception cref="UpdateException">Preuzimanje nije uspjelo ili nema fajla za ovaj sistem.</exception>
    public async Task<PreparedUpdate> DownloadAsync(ReleaseInfo release, IProgress<UpdateDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (InstallUnavailableReason is { } reason)
        {
            throw new UpdateException(reason);
        }

        var platform = Platform!.Value;
        var asset = GitHubReleaseClient.SelectAsset(release, platform)
            ?? throw new UpdateException($"Verzija {release.Version.ToString(3)} na GitHub-u nema fajl za {PlatformText}.");
        var executable = ExecutablePath!;
        var newPath = executable + NewSuffix;
        try
        {
            if (platform == UpdatePlatform.Windows)
            {
                await _client.DownloadAssetAsync(asset, newPath, progress, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var folder = Path.Combine(_paths.DownloadsFolder, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                try
                {
                    var archive = Path.Combine(folder, "nova-verzija.tar.gz");
                    await _client.DownloadAssetAsync(asset, archive, progress, cancellationToken).ConfigureAwait(false);
                    await GitHubReleaseClient.ExtractLinuxExecutableAsync(archive, newPath, cancellationToken).ConfigureAwait(false);
                    if (!OperatingSystem.IsWindows())
                    {
                        File.SetUnixFileMode(newPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                    }
                }
                finally
                {
                    TryDeleteDirectory(folder);
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            TryDelete(newPath);
            _log.Warning("Nova verzija ne može da se sačuva pored programa.", ex);
            throw new UpdateException(
                $"Nova verzija ne može da se sačuva u folder programa „{Path.GetDirectoryName(executable)}” ({ex.Message}). "
                + "Preuzmite je ručno sa GitHub-a i zamijenite postojeći fajl.", ex);
        }
        catch
        {
            TryDelete(newPath);
            throw;
        }

        return new PreparedUpdate(release, newPath);
    }

    /// <summary>
    /// Zamjenjuje izvršni fajl preuzetom verzijom i pokreće je. Ako pokretanje ne uspije, vraća stari fajl.
    /// Poslije uspjeha pozivalac zatvara program (<see cref="Shutdown"/>).
    /// </summary>
    /// <exception cref="UpdateException">Zamjena ili pokretanje nisu uspjeli (program ostaje kakav je bio).</exception>
    public void InstallAndRestart(PreparedUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var executable = ExecutablePath ?? throw new UpdateException("Putanja programa nije poznata.");
        var oldPath = executable + OldSuffix;
        if (File.Exists(oldPath) && !TryDelete(oldPath))
        {
            oldPath = $"{executable}{OldSuffix}-{DateTime.Now:yyyyMMddHHmmss}";
        }

        try
        {
            File.Move(executable, oldPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(update.NewExecutablePath);
            throw new UpdateException($"Postojeći program ne može da se zamijeni ({ex.Message}).", ex);
        }

        try
        {
            File.Move(update.NewExecutablePath, executable);
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
            };
            start.ArgumentList.Add(FinishedArgument);
            start.ArgumentList.Add(CurrentVersion.ToString(3));
            start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var process = Process.Start(start) ?? throw new UpdateException("Nova verzija nije pokrenuta.");
            _log.Info($"Program je nadograđen sa {CurrentVersion.ToString(3)} na {update.Release.Version.ToString(3)}; pokrenuta je nova verzija.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or UpdateException)
        {
            // Vraća se stari program da korisnik ne ostane bez njega.
            try
            {
                if (File.Exists(executable))
                {
                    File.Move(executable, update.NewExecutablePath, overwrite: true);
                }

                File.Move(oldPath, executable);
            }
            catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
            {
                _log.Error("Stari program ne može da se vrati poslije neuspjele nadogradnje.", rollback);
            }

            TryDelete(update.NewExecutablePath);
            _log.Warning("Nova verzija ne može da se pokrene.", ex);
            throw ex as UpdateException ?? new UpdateException($"Nova verzija ne može da se pokrene ({ex.Message}). Program je ostao na verziji {CurrentVersion.ToString(3)}.", ex);
        }
    }

    /// <summary>
    /// Poslije nadogradnje: čeka da se stara verzija zatvori i briše njen fajl (<c>.staro</c>) i
    /// eventualne ostatke prekinutog preuzimanja (<c>.novo</c>).
    /// </summary>
    public async Task CleanupAfterUpdateAsync(int? oldProcessId, CancellationToken cancellationToken = default)
    {
        if (ExecutablePath is not { } executable)
        {
            return;
        }

        if (oldProcessId is { } pid && pid != Environment.ProcessId)
        {
            try
            {
                using var old = Process.GetProcessById(pid);
                await old.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or TimeoutException)
            {
                // Proces je već završen (ili ne odgovara); brisanje se svakako pokušava.
            }
        }

        var folder = Path.GetDirectoryName(executable)!;
        var name = Path.GetFileName(executable);
        foreach (var path in Directory.EnumerateFiles(folder, name + OldSuffix + "*").Append(executable + NewSuffix))
        {
            for (var attempt = 0; attempt < 10 && File.Exists(path) && !TryDelete(path); attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Čita argumente nadogradnje iz komandne linije i vraća preostale argumente (npr. fajl za otvaranje).</summary>
    public static (string? UpdatedFrom, int? OldProcessId, IReadOnlyList<string> Remaining) ParseArguments(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var index = args.ToList().IndexOf(FinishedArgument);
        if (index < 0)
        {
            return (null, null, args);
        }

        var from = index + 1 < args.Count ? args[index + 1] : "?";
        int? pid = index + 2 < args.Count && int.TryParse(args[index + 2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : null;
        var rest = args.Take(index).Concat(args.Skip(index + 3)).ToList();
        return (from, pid, rest);
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void TryDeleteDirectory(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning("Privremeni folder nove verzije ne može da se obriše.", ex);
        }
    }
}
