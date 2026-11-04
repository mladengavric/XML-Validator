namespace XmlValidator.Core.Schemas;

/// <summary>Jedan fajl skupa šema: relativna putanja (separator <c>/</c>) i sadržaj.</summary>
internal sealed record SchemaFile(string RelativePath, byte[] Content);
