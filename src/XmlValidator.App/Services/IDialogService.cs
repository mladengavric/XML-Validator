namespace XmlValidator.App.Services;

/// <summary>Ozbiljnost poruke; određuje ikonicu i boju (nikad samo boju).</summary>
public enum MessageSeverity
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>Stranica prozora „Podešavanja”.</summary>
public enum SettingsPage
{
    General,
    Schemas,
}

/// <summary>Filter fajlova za dijaloge (npr. „XML fajlovi”, <c>*.xml</c>).</summary>
public sealed record FileFilter(string Name, IReadOnlyList<string> Patterns)
{
    public static FileFilter Xml { get; } = new("XML fajlovi", ["*.xml"]);

    public static FileFilter All { get; } = new("Svi fajlovi", ["*"]);

    public static FileFilter SchemaPackage { get; } = new("XSD šema ili ZIP paket", ["*.xsd", "*.zip"]);
}

/// <summary>Dijalozi i prozori, iza interfejsa da bi ViewModel-i mogli da se testiraju bez UI-ja.</summary>
public interface IDialogService
{
    /// <summary>Izbor fajla za otvaranje; <c>null</c> ako je korisnik odustao.</summary>
    Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FileFilter> filters, string? startFolder);

    /// <summary>
    /// Izbor fajla za čuvanje; <c>null</c> ako je korisnik odustao. Dijalog sistema traži potvrdu
    /// ako izabrani fajl već postoji.
    /// </summary>
    Task<string?> PickSaveFileAsync(string title, string suggestedFileName, IReadOnlyList<FileFilter> filters, string? startFolder);

    /// <summary>Poruka sa jednim dugmetom.</summary>
    Task ShowMessageAsync(string title, string message, MessageSeverity severity, IReadOnlyList<string>? details = null);

    /// <summary>Pitanje sa dva dugmeta; <c>true</c> ako je korisnik potvrdio.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText = "Otkaži", bool isDangerous = false);

    /// <summary>Izbor jedne od ponuđenih vrijednosti; <c>null</c> ako je korisnik odustao.</summary>
    Task<string?> ChooseAsync(string title, string message, IReadOnlyList<string> options, string confirmText);

    /// <summary>Unos teksta; <c>null</c> ako je korisnik odustao.</summary>
    Task<string?> PromptTextAsync(string title, string message, string initialText, string confirmText);

    /// <summary>Otvara prozor „Podešavanja” na zadatoj stranici; opciono odmah uvozi šemu iz fajla.</summary>
    Task ShowSettingsAsync(SettingsPage page, string? importSchemaPath = null);
}

/// <summary>Primjena teme (svijetla, tamna, po sistemu).</summary>
public interface IThemeService
{
    void Apply(ThemePreference theme);
}

/// <summary>Prebacivanje posla na UI nit (ViewModel-i ne zavise direktno od Avalonije).</summary>
public interface IUiDispatcher
{
    /// <summary>Izvršava akciju na UI niti; ako je pozivalac već na njoj, odmah.</summary>
    void Post(Action action);
}
