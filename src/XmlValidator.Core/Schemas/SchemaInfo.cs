namespace XmlValidator.Core.Schemas;

/// <summary>Metapodaci jedne šeme (ugrađene ili uvezene).</summary>
/// <param name="Id">Identifikator šeme; za ugrađenu <see cref="SchemaStore.BuiltInId"/>.</param>
/// <param name="Name">Naziv za prikaz i izvještaje.</param>
/// <param name="SourceFileName">Ime fajla iz kojeg je šema uvezena (<c>.xsd</c> ili <c>.zip</c>).</param>
/// <param name="ImportedAt">Vrijeme uvoza; za ugrađenu šemu <c>null</c>.</param>
/// <param name="Sha256">SHA-256 cijelog skupa fajlova (vidi <see cref="SchemaHash.ComputeSetHash(IEnumerable{KeyValuePair{string, byte[]}})"/>).</param>
/// <param name="ContentSha256">SHA-256 sadržaja kad skup ima samo jedan fajl; inače <c>null</c>.</param>
/// <param name="TargetNamespace"><c>targetNamespace</c> glavne šeme.</param>
/// <param name="Version">Verzija iz <c>xs:schema/@version</c> ili anotacije, ako postoji.</param>
/// <param name="MainSchemaFile">Relativna putanja glavne šeme unutar skupa.</param>
/// <param name="Files">Relativne putanje svih fajlova skupa (separator <c>/</c>).</param>
/// <param name="IsBuiltIn">Da li je ovo ugrađena šema.</param>
public sealed record SchemaInfo(
    string Id,
    string Name,
    string SourceFileName,
    DateTimeOffset? ImportedAt,
    string Sha256,
    string? ContentSha256,
    string TargetNamespace,
    string? Version,
    string MainSchemaFile,
    IReadOnlyList<string> Files,
    bool IsBuiltIn)
{
    /// <summary>Jednakost po vrijednosti, uključujući sadržaj liste <see cref="Files"/>.</summary>
    public bool Equals(SchemaInfo? other) =>
        other is not null
        && Id == other.Id
        && Name == other.Name
        && SourceFileName == other.SourceFileName
        && ImportedAt == other.ImportedAt
        && Sha256 == other.Sha256
        && ContentSha256 == other.ContentSha256
        && TargetNamespace == other.TargetNamespace
        && Version == other.Version
        && MainSchemaFile == other.MainSchemaFile
        && IsBuiltIn == other.IsBuiltIn
        && Files.SequenceEqual(other.Files, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Id, Sha256, Name, ImportedAt, IsBuiltIn);
}
