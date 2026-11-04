namespace XmlValidator.Core.Schemas;

/// <summary>Ishod uvoza šeme.</summary>
public enum SchemaImportStatus
{
    /// <summary>Šema je uvezena (nije automatski postala aktivna).</summary>
    Imported,

    /// <summary>Šema sa istim sadržajem je već uvezena.</summary>
    AlreadyExists,

    /// <summary>Šema ima isti sadržaj kao ugrađena.</summary>
    AlreadyExistsBuiltIn,

    /// <summary>Paket sadrži više glavnih šema; korisnik bira jednu i ponavlja uvoz.</summary>
    NeedsMainSchemaSelection,

    /// <summary>Uvoz nije uspio; razlozi su u <see cref="SchemaImportResult.Errors"/>.</summary>
    Failed,
}

/// <summary>Opcije uvoza.</summary>
/// <param name="Name">Naziv šeme; ako nije zadat, predlaže se iz imena fajla i datuma.</param>
/// <param name="MainSchemaFile">Glavna šema (relativna putanja u paketu), kad ih paket ima više.</param>
public sealed record SchemaImportOptions(string? Name = null, string? MainSchemaFile = null);

/// <summary>Rezultat uvoza šeme.</summary>
/// <param name="Status">Ishod.</param>
/// <param name="Message">Poruka za korisnika (srpski).</param>
/// <param name="Schema">Uvezena ili već postojeća šema.</param>
/// <param name="Errors">Detaljne greške (za <see cref="SchemaImportStatus.Failed"/>).</param>
/// <param name="MainSchemaCandidates">Moguće glavne šeme (za <see cref="SchemaImportStatus.NeedsMainSchemaSelection"/>).</param>
/// <param name="IgnoredFiles">Fajlovi iz paketa koji nisu <c>.xsd</c> i zato su preskočeni.</param>
public sealed record SchemaImportResult(
    SchemaImportStatus Status,
    string Message,
    SchemaInfo? Schema,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> MainSchemaCandidates,
    IReadOnlyList<string> IgnoredFiles);
