namespace XmlValidator.App.Services;

/// <summary>Putanje u korisničkom app-data folderu. Korijen je parametar (u testovima privremeni folder).</summary>
/// <param name="Root">Korijenski folder, podrazumijevano <c>%AppData%/XmlValidator</c>.</param>
public sealed record AppPaths(string Root)
{
    /// <summary>Podrazumijevane putanje: <c>%AppData%/XmlValidator</c>.</summary>
    public static AppPaths Default { get; } = new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XmlValidator"));

    /// <summary>Fajl sa podešavanjima aplikacije.</summary>
    public string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>Skladište uvezenih šema (za podrazumijevani korijen isto kao <c>SchemaStore.GetDefaultRootPath()</c>).</summary>
    public string SchemasFolder => Path.Combine(Root, "Schemas");

    /// <summary>Folder sa log fajlovima.</summary>
    public string LogsFolder => Path.Combine(Root, "logs");
}
