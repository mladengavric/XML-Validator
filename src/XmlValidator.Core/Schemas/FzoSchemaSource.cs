namespace XmlValidator.Core.Schemas;

/// <summary>
/// Mjesto na sajtu FZO na kojem se objavljuje šema faktura. Program prati vrijeme izmjene
/// („Last modified”) fajla <see cref="MainSchemaFileName"/> u folderu <see cref="FolderUrl"/>.
/// </summary>
public static class FzoSchemaSource
{
    /// <summary>Folder sa šemama na sajtu FZO.</summary>
    public const string FolderUrl = "https://zdravstvo-srpske.org/rp/xml-sheme/";

    /// <summary>Glavna šema faktura (samostalna, bez <c>xs:include</c>/<c>xs:import</c>).</summary>
    public const string MainSchemaFileName = "fakture-zdravstva.xsd";

    /// <summary>Puna adresa glavne šeme.</summary>
    public const string MainSchemaUrl = FolderUrl + MainSchemaFileName;
}
