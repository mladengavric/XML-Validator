namespace XmlValidator.Core.Schemas;

/// <summary>Šema ugrađena u program (rezerva koja je uvijek dostupna).</summary>
public static class BuiltInSchema
{
    public const string FileName = "fakture-zdravstva.xsd";

    public const string DisplayName = "Ugrađena šema (fakture-zdravstva.xsd)";

    private const string ResourceName = "XmlValidator.Core.Schemas.fakture-zdravstva.xsd";

    /// <summary>Bajtovi ugrađene šeme, tačno onakvi kakvi su u resursu.</summary>
    public static byte[] ReadContent()
    {
        using var stream = typeof(BuiltInSchema).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Ugrađena šema '{ResourceName}' nije pronađena u programu.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>Metapodaci ugrađene šeme (hash se računa iz sadržaja).</summary>
    public static SchemaInfo CreateInfo(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new SchemaInfo(
            Id: SchemaStore.BuiltInId,
            Name: DisplayName,
            SourceFileName: FileName,
            ImportedAt: null,
            Sha256: SchemaHash.ComputeSetHash([new SchemaFile(FileName, content)]),
            ContentSha256: SchemaHash.ComputeContentHash(content),
            TargetNamespace: InvoiceSchemaConstants.TargetNamespace,
            Version: null,
            MainSchemaFile: FileName,
            Files: [FileName],
            IsBuiltIn: true);
    }
}
