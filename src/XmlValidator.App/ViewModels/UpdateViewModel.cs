using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XmlValidator.App.Services;
using XmlValidator.Core.Updates;

namespace XmlValidator.App.ViewModels;

/// <summary>
/// Tab „Nadogradnja” u prozoru „O programu”: trenutna verzija, posljednja verzija na GitHub-u i
/// nadogradnja (preuzimanje, zamjena programa i ponovno pokretanje).
/// </summary>
public sealed partial class UpdateViewModel : ViewModelBase
{
    private static readonly CultureInfo Serbian = CultureInfo.GetCultureInfo("sr-Latn");

    private readonly AppUpdateService _updates;
    private readonly IUiDispatcher _ui;
    private readonly IAppLog _log;
    private CancellationTokenSource? _cts;

    public UpdateViewModel(AppUpdateService updates, IUiDispatcher ui, IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(log);
        _updates = updates;
        _ui = ui;
        _log = log;
    }

    public string CurrentVersionText => _updates.CurrentVersion.ToString(3);

    public string PlatformText => AppUpdateService.PlatformText;

    public string ReleasesPageText => GitHubReleaseClient.ReleasesPage.AbsoluteUri;

    /// <summary>Posljednja verzija na GitHub-u; <c>null</c> dok provjera nije urađena.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatestVersionText), nameof(HasRelease), nameof(ReleaseTitle), nameof(ReleaseNotes), nameof(ReleasePageText), nameof(PublishedText))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial ReleaseInfo? Latest { get; private set; }

    public bool HasRelease => Latest is not null;

    public string LatestVersionText => Latest?.Version.ToString(3) ?? (IsChecking ? "provjeravam…" : "—");

    public string ReleaseTitle => Latest?.Title ?? string.Empty;

    public string ReleaseNotes => Latest?.Notes.Trim() ?? string.Empty;

    public string ReleasePageText => Latest?.PageUrl.AbsoluteUri ?? ReleasesPageText;

    public string PublishedText => Latest?.PublishedAt is { } at ? "Objavljena " + at.ToLocalTime().ToString("dd.MM.yyyy.", Serbian) : string.Empty;

    /// <summary>Na GitHub-u je novija verzija od ove.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial bool IsUpdateAvailable { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatestVersionText), nameof(IsWorking))]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand), nameof(InstallCommand))]
    public partial bool IsChecking { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorking))]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand), nameof(InstallCommand), nameof(CancelCommand))]
    public partial bool IsDownloading { get; private set; }

    public bool IsWorking => IsChecking || IsDownloading;

    /// <summary>Napredak preuzimanja od 0 do 100; <c>null</c> kad veličina nije poznata.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDownloadProgressKnown))]
    public partial double? DownloadPercent { get; private set; }

    public bool IsDownloadProgressKnown => DownloadPercent is not null;

    /// <summary>Stanje provjere ili nadogradnje.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial UserMessage? Message { get; private set; }

    public bool HasMessage => Message is not null;

    /// <summary>Zašto program ne može sam da se nadogradi; <c>null</c> kad može.</summary>
    public string? InstallUnavailableReason => _updates.InstallUnavailableReason;

    private bool CanCheck() => !IsWorking;

    /// <summary>Provjerava posljednju verziju na GitHub-u.</summary>
    [RelayCommand(CanExecute = nameof(CanCheck))]
    public async Task CheckAsync()
    {
        if (IsWorking)
        {
            return;
        }

        IsChecking = true;
        Message = UserMessage.Info("Provjeravam posljednju verziju na GitHub-u…");
        try
        {
            var release = await _updates.GetLatestAsync();
            Latest = release;
            IsUpdateAvailable = _updates.IsNewer(release);
            Message = IsUpdateAvailable
                ? UserMessage.Warning(
                    $"Dostupna je nova verzija {release.Version.ToString(3)} (trenutna je {CurrentVersionText}).",
                    InstallUnavailableReason is { } reason ? [reason, "Novu verziju preuzmite ručno: " + release.PageUrl.AbsoluteUri] : null)
                : UserMessage.Success($"Imate najnoviju verziju programa ({CurrentVersionText}).");
        }
        catch (UpdateException ex)
        {
            _log.Warning("Provjera nove verzije programa nije uspjela.", ex);
            Message = UserMessage.Error("Provjera nove verzije nije uspjela.", [ex.Message]);
        }
        finally
        {
            IsChecking = false;
        }
    }

    private bool CanInstall() => !IsWorking && IsUpdateAvailable && Latest is not null && InstallUnavailableReason is null;

    /// <summary>
    /// Nadogradnja: pita za nesačuvane izmjene, preuzima novu verziju pored postojeće, zamjenjuje je,
    /// pokreće novu verziju i zatvara ovu.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanInstall))]
    public async Task InstallAsync()
    {
        if (!CanInstall() || Latest is not { } release)
        {
            return;
        }

        if (_updates.ConfirmCloseAsync is { } confirm && !await confirm())
        {
            Message = UserMessage.Info("Nadogradnja je otkazana; nesačuvane izmjene su ostale u programu.");
            return;
        }

        using var cts = new CancellationTokenSource();
        _cts = cts;
        IsDownloading = true;
        DownloadPercent = null;
        Message = UserMessage.Info($"Preuzimam verziju {release.Version.ToString(3)} za {PlatformText}…");
        try
        {
            var progress = new Progress<UpdateDownloadProgress>(p => _ui.Post(() =>
            {
                if (IsDownloading)
                {
                    DownloadPercent = p.Total is > 0 ? Math.Min(100, 100.0 * p.Received / p.Total.Value) : null;
                }
            }));
            var prepared = await _updates.DownloadAsync(release, progress, cts.Token);
            Message = UserMessage.Info($"Instaliram verziju {release.Version.ToString(3)}; program će se zatvoriti i ponovo pokrenuti…");
            await Task.Run(() => _updates.InstallAndRestart(prepared), CancellationToken.None);
            _updates.Shutdown?.Invoke();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Message = UserMessage.Info("Preuzimanje je prekinuto. Program nije promijenjen.");
        }
        catch (UpdateException ex)
        {
            _log.Warning("Nadogradnja programa nije uspjela.", ex);
            Message = UserMessage.Error("Nadogradnja nije uspjela. Program nije promijenjen.", [ex.Message]);
        }
        finally
        {
            _cts = null;
            IsDownloading = false;
            DownloadPercent = null;
        }
    }

    private bool CanCancel() => IsDownloading;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    /// <summary>Prekida preuzimanje (zatvaranje prozora).</summary>
    public void CancelRunning() => _cts?.Cancel();
}
