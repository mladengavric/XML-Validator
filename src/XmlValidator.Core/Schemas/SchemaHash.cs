using System.Security.Cryptography;
using System.Text;

namespace XmlValidator.Core.Schemas;

/// <summary>Računanje hash-eva skupa šema.</summary>
public static class SchemaHash
{
    /// <summary>
    /// SHA-256 skupa fajlova. Fajlovi se sortiraju po relativnoj putanji (ordinalno), a hash se računa
    /// nad tekstom <c>putanja\nsha256(sadržaj)\n</c> za svaki fajl. Imena su dio hash-a jer od njih
    /// zavise <c>xs:include</c>/<c>xs:import</c> veze.
    /// </summary>
    public static string ComputeSetHash(IEnumerable<KeyValuePair<string, byte[]>> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var builder = new StringBuilder();
        foreach (var (path, content) in files.OrderBy(f => NormalizePath(f.Key), StringComparer.Ordinal))
        {
            builder.Append(NormalizePath(path)).Append('\n').Append(ComputeContentHash(content)).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    /// <summary>SHA-256 sadržaja jednog fajla, kao heks string malim slovima.</summary>
    public static string ComputeContentHash(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Convert.ToHexStringLower(SHA256.HashData(content));
    }

    internal static string ComputeSetHash(IEnumerable<SchemaFile> files) =>
        ComputeSetHash(files.Select(f => new KeyValuePair<string, byte[]>(f.RelativePath, f.Content)));

    internal static string NormalizePath(string path) => path.Replace('\\', '/');
}
