using System.Reflection;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.Input;
using XmlValidator.App.Services;

namespace XmlValidator.App.ViewModels;

/// <summary>Korišćena biblioteka (ili drugi sadržaj) sa licencom, za prozor „O programu”.</summary>
/// <param name="Name">Naziv.</param>
/// <param name="Version">Verzija (iz učitanog sklopa), ili <c>—</c>.</param>
/// <param name="License">Licenca.</param>
/// <param name="Url">Adresa projekta.</param>
/// <param name="Purpose">Čemu služi u programu.</param>
public sealed record ThirdPartyComponent(string Name, string Version, string License, string Url, string Purpose);

/// <summary>Prečica na tastaturi (tekst za prikaz).</summary>
/// <param name="Area">Gdje važi (npr. „Svuda”, „Validacija”).</param>
/// <param name="Keys">Tasteri, npr. <c>Ctrl+O</c>.</param>
/// <param name="Description">Šta radi.</param>
public sealed record ShortcutInfo(string Area, string Keys, string Description);

/// <summary>Prečice na tastaturi; ista lista se prikazuje u prozoru „O programu” i provjerava u UI testovima.</summary>
public static class KeyboardShortcuts
{
    public const string Global = "Svuda";
    public const string Validation = "Validacija";
    public const string Cleaning = "Čišćenje faktura";
    public const string Dialogs = "Dijalozi";

    public static IReadOnlyList<ShortcutInfo> All { get; } =
    [
        new(Global, "Ctrl+O", "Otvori XML fajl"),
        new(Global, "Ctrl+Shift+S", "Sačuvaj kao (novi fajl; ulaz se nikad ne prepisuje)"),
        new(Global, "Ctrl+1", "Tab „Validacija”"),
        new(Global, "Ctrl+2", "Tab „Čišćenje faktura”"),
        new(Global, "Ctrl+,", "Podešavanja"),
        new(Global, "F1", "O programu i prečice"),
        new(Validation, "F5", "Validiraj"),
        new(Validation, "F8", "Sljedeća greška ili upozorenje"),
        new(Validation, "Shift+F8", "Prethodna greška ili upozorenje"),
        new(Validation, "Ctrl+Shift+C", "Kopiraj nalaze"),
        new(Validation, "Ctrl+E", "Izvezi nalaze (.txt / .csv)"),
        new(Cleaning, "F5", "Analiziraj"),
        new(Cleaning, "Ctrl+F", "Pretraga po broju i JMB-u"),
        new(Cleaning, "Delete", "Obriši označene fakture (kandidate)"),
        new(Cleaning, "Ctrl+D", "Pregled izmjena"),
        new(Cleaning, "Ctrl+S", "Sačuvaj očišćeni fajl"),
        new(Cleaning, "Ctrl+R", "Sačuvaj izvještaj"),
        new(Cleaning, "Ctrl+Enter", "Primijeni izmjenu bloka (u editoru)"),
        new(Cleaning, "Esc", "Odustani od izmjene bloka (u editoru)"),
        new(Dialogs, "Enter", "Potvrdi (osim kod opasnih akcija)"),
        new(Dialogs, "Esc", "Otkaži ili zatvori"),
    ];
}

/// <summary>Prozor „O programu”: verzija, okruženje, licence korišćenih paketa i prečice.</summary>
public sealed partial class AboutViewModel : ViewModelBase
{
    /// <summary>Paketi: sklop po kojem se čita verzija, naziv, licenca, adresa, namjena.</summary>
    private static readonly (string Assembly, string Name, string License, string Url, string Purpose)[] Packages =
    [
        ("Avalonia.Base", "Avalonia", "MIT", "https://github.com/AvaloniaUI/Avalonia", "korisnički interfejs"),
        ("Avalonia.Themes.Fluent", "Avalonia.Themes.Fluent", "MIT", "https://github.com/AvaloniaUI/Avalonia", "Fluent tema"),
        ("Avalonia.Controls.DataGrid", "Avalonia.Controls.DataGrid", "MIT", "https://github.com/AvaloniaUI/Avalonia.Controls.DataGrid", "tabele"),
        ("AvaloniaEdit", "AvaloniaEdit", "MIT", "https://github.com/AvaloniaUI/AvaloniaEdit", "XML editor sa bojenjem sintakse"),
        ("Avalonia.Fonts.Inter", "Avalonia.Fonts.Inter", "MIT (paket); font Inter: SIL Open Font License 1.1", "https://github.com/rsms/inter", "font Inter"),
        ("CommunityToolkit.Mvvm", "CommunityToolkit.Mvvm", "MIT", "https://github.com/CommunityToolkit/dotnet", "MVVM osnova"),
        ("Microsoft.Extensions.DependencyInjection", "Microsoft.Extensions.DependencyInjection", "MIT", "https://github.com/dotnet/runtime", "povezivanje servisa"),
    ];

    private readonly IClipboardService _clipboard;

    public AboutViewModel(AppPaths paths, IClipboardService clipboard)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(clipboard);
        _clipboard = clipboard;
        DataFolder = paths.Root;
        Version = ReadVersion(typeof(AboutViewModel).Assembly);
        Components =
        [
            .. Packages.Select(p => new ThirdPartyComponent(p.Name, ReadVersion(p.Assembly), p.License, p.Url, p.Purpose)),
            new(".NET", Environment.Version.ToString(), "MIT", "https://github.com/dotnet/runtime", "izvršno okruženje"),
            new("Material Design Icons (oblici ikonica)", "—", "Apache 2.0", "https://github.com/Templarian/MaterialDesign", "po uzoru na njih su nacrtane ikonice"),
        ];
    }

    public string AppName => MainWindowViewModel.AppTitle;

    public string Description =>
        "Validacija IZIS XML faktura zdravstva prema XSD šemi i čišćenje faktura po pravilu Službe hitne medicinske pomoći (SHMP). "
        + "Ulazni fajl se nikad ne mijenja; rezultat čišćenja uvijek ide u novi fajl.";

    /// <summary>Verzija programa (bez dodatka za izvorni kod iza <c>+</c>).</summary>
    public string Version { get; }

    public string VersionText => $"Verzija {Version}";

    public string RuntimeText => $"{RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";

    /// <summary>Folder sa podešavanjima, šemama i logom.</summary>
    public string DataFolder { get; }

    public IReadOnlyList<ThirdPartyComponent> Components { get; }

    public IReadOnlyList<ShortcutInfo> Shortcuts => KeyboardShortcuts.All;

    /// <summary>Tekst za prijavu problema: verzija, okruženje i paketi (bez ličnih podataka).</summary>
    public string DiagnosticText =>
        $"{AppName} {Version}{Environment.NewLine}{RuntimeText}{Environment.NewLine}"
        + string.Join(Environment.NewLine, Components.Select(c => $"{c.Name} {c.Version} ({c.License})"));

    public event EventHandler? CloseRequested;

    [RelayCommand]
    private Task CopyDiagnosticsAsync() => _clipboard.SetTextAsync(DiagnosticText);

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    internal static string ReadVersion(string assemblyName)
    {
        try
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == assemblyName)
                ?? Assembly.Load(new AssemblyName(assemblyName));
            return ReadVersion(assembly);
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return "—";
        }
    }

    internal static string ReadVersion(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus > 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "—";
    }
}
