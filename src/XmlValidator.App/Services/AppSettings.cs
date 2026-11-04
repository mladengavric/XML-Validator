using System.Text.Json.Serialization;
using XmlValidator.Core.Cleaning;

namespace XmlValidator.App.Services;

/// <summary>Izbor teme.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ThemePreference>))]
public enum ThemePreference
{
    /// <summary>Prati sistem (svijetla ili tamna).</summary>
    System,

    Light,

    Dark,
}

/// <summary>
/// Podešavanja aplikacije. Aktivna šema se ovdje ne čuva: pamti je skladište šema (odluka 20).
/// </summary>
public sealed record AppSettings
{
    /// <summary>Najviše nedavnih fajlova u listi.</summary>
    public const int MaxRecentFiles = 10;

    /// <summary>Posljednji folder iz kojeg je otvoren ili u koji je sačuvan fajl.</summary>
    public string? LastFolder { get; init; }

    /// <summary>Nedavni fajlovi, od najnovijeg.</summary>
    public IReadOnlyList<string> RecentFiles { get; init; } = [];

    public ThemePreference Theme { get; init; } = ThemePreference.System;

    /// <summary>Ciljna grupa za čišćenje (SHMP), podrazumijevano 100.</summary>
    public ushort TargetGroup { get; init; } = ShmpCleaningRule.DefaultTargetGroup;

    /// <summary>Automatske popravke zagrada.</summary>
    public bool RepairBrackets { get; init; } = true;

    /// <summary>Podrazumijevana akcija za praznu fakturu (Obriši ili Preskoči).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<CandidateAction>))]
    public CandidateAction EmptyInvoiceAction { get; init; } = CandidateAction.Delete;

    /// <summary>Podrazumijevana akcija za fakturu bez ciljne grupe i bez podudaranja JMB-a (Obriši ili Preskoči).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<CandidateAction>))]
    public CandidateAction NoGroupNoJmbMatchAction { get; init; } = CandidateAction.Delete;

    /// <summary>Poslije čuvanja očišćenog fajla otvori ga u tabu „Validacija” (podrazumijevano isključeno).</summary>
    public bool ValidateAfterSave { get; init; }

    /// <summary>Posljednja adresa sa koje je preuzeta šema („Uvezi sa linka…”); nudi se pri sljedećem uvozu.</summary>
    public string? LastSchemaUrl { get; init; }

    /// <summary>Poređenje putanja: na Windowsu bez obzira na velika i mala slova.</summary>
    internal static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Opcije čišćenja iz podešavanja.</summary>
    public CleaningOptions ToCleaningOptions() =>
        new(TargetGroup, RepairBrackets, EmptyInvoiceAction, NoGroupNoJmbMatchAction);

    /// <summary>Dodaje fajl na vrh liste nedavnih (bez duplikata) i pamti njegov folder kao posljednji.</summary>
    public AppSettings WithRecentFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        return this with
        {
            RecentFiles = [full, .. RecentFiles.Where(p => !PathComparer.Equals(p, full)).Take(MaxRecentFiles - 1)],
            LastFolder = Path.GetDirectoryName(full) ?? LastFolder,
        };
    }

    /// <summary>Uklanja fajl iz liste nedavnih.</summary>
    public AppSettings WithoutRecentFile(string path) =>
        this with { RecentFiles = [.. RecentFiles.Where(p => !PathComparer.Equals(p, path))] };

    /// <summary>
    /// Ispravlja vrijednosti koje su mogle doći iz ručno izmijenjenog fajla: prazne i duple putanje,
    /// predugačka lista i nepoznata tema se ispravljaju; akcija Uredi kao podrazumijevana (traži ručnu
    /// izmjenu, pa ne može biti podrazumijevana) i nepoznate akcije se vraćaju na Obriši (vrijednost iz plana).
    /// </summary>
    public AppSettings Normalize()
    {
        static CandidateAction Action(CandidateAction value) =>
            value is CandidateAction.Delete or CandidateAction.Skip ? value : CandidateAction.Delete;

        return this with
        {
            LastFolder = string.IsNullOrWhiteSpace(LastFolder) ? null : LastFolder,
            RecentFiles = [.. (RecentFiles ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(PathComparer).Take(MaxRecentFiles)],
            Theme = Enum.IsDefined(Theme) ? Theme : ThemePreference.System,
            EmptyInvoiceAction = Action(EmptyInvoiceAction),
            NoGroupNoJmbMatchAction = Action(NoGroupNoJmbMatchAction),
        };
    }

    /// <summary>Jednakost po vrijednosti, uključujući sadržaj liste nedavnih fajlova.</summary>
    public bool Equals(AppSettings? other) =>
        other is not null
        && LastFolder == other.LastFolder
        && RecentFiles.SequenceEqual(other.RecentFiles, StringComparer.Ordinal)
        && Theme == other.Theme
        && TargetGroup == other.TargetGroup
        && RepairBrackets == other.RepairBrackets
        && EmptyInvoiceAction == other.EmptyInvoiceAction
        && NoGroupNoJmbMatchAction == other.NoGroupNoJmbMatchAction
        && ValidateAfterSave == other.ValidateAfterSave
        && LastSchemaUrl == other.LastSchemaUrl;

    public override int GetHashCode() => HashCode.Combine(LastFolder, RecentFiles.Count, Theme, TargetGroup, RepairBrackets);
}
