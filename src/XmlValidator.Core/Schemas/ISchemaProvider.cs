using System.Xml.Schema;

namespace XmlValidator.Core.Schemas;

/// <summary>Kompajlirana šema koja se koristi za validaciju.</summary>
/// <param name="SchemaSet">Kompajliran skup šema (samo za čitanje; ne mijenjati).</param>
/// <param name="Info">Metapodaci šeme.</param>
/// <param name="IsFallback">Da li je ugrađena šema upotrijebljena kao rezerva jer aktivna nije dostupna.</param>
/// <param name="FallbackReason">Razlog povratka na ugrađenu šemu (za statusnu traku i izvještaje).</param>
public sealed record ActiveSchema(XmlSchemaSet SchemaSet, SchemaInfo Info, bool IsFallback, string? FallbackReason)
{
    /// <summary>Naziv šeme za izvještaje.</summary>
    public string Name => Info.Name;
}

/// <summary>Daje kompajliranu aktivnu šemu.</summary>
public interface ISchemaProvider
{
    ActiveSchema GetActiveSchema();
}

/// <summary>Uvijek daje ugrađenu šemu (kompajlira se jednom po instanci).</summary>
public sealed class BuiltInSchemaProvider : ISchemaProvider
{
    private readonly Lazy<ActiveSchema> _schema = new(() => CompileBuiltIn(isFallback: false, reason: null));

    public ActiveSchema GetActiveSchema() => _schema.Value;

    internal static ActiveSchema CompileBuiltIn(bool isFallback, string? reason)
    {
        var content = BuiltInSchema.ReadContent();
        var compilation = SchemaCompiler.Compile([new SchemaFile(BuiltInSchema.FileName, content)], BuiltInSchema.FileName);
        if (!compilation.Succeeded)
        {
            throw new InvalidOperationException(
                "Ugrađena šema ne može da se kompajlira: " + string.Join(" ", compilation.Errors));
        }

        return new ActiveSchema(compilation.SchemaSet!, BuiltInSchema.CreateInfo(content), isFallback, reason);
    }
}

/// <summary>
/// Daje aktivnu šemu iz <see cref="SchemaStore"/>. Rezultat se kešira i ponovo računa kad se skladište
/// promijeni. Ako aktivna šema nedostaje, izmijenjena je (hash se ne poklapa) ili ne može da se
/// kompajlira, vraća ugrađenu šemu sa <see cref="ActiveSchema.IsFallback"/> i razlogom.
/// </summary>
public sealed class StoreSchemaProvider : ISchemaProvider
{
    private readonly SchemaStore _store;
    private readonly object _sync = new();
    private readonly Lazy<ActiveSchema> _builtIn = new(() => BuiltInSchemaProvider.CompileBuiltIn(isFallback: false, reason: null));
    private ActiveSchema? _cached;
    private long _cachedVersion = -1;

    public StoreSchemaProvider(SchemaStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public ActiveSchema GetActiveSchema()
    {
        lock (_sync)
        {
            var version = _store.Version;
            if (_cached is null || _cachedVersion != version)
            {
                _cached = Load();
                _cachedVersion = version;
            }

            return _cached;
        }
    }

    /// <summary>Briše keš (npr. kad su fajlovi skladišta promijenjeni van programa).</summary>
    public void Invalidate()
    {
        lock (_sync)
        {
            _cached = null;
        }
    }

    private ActiveSchema Load()
    {
        var (id, problem) = _store.ReadActiveSelection();
        if (problem is not null)
        {
            return Fallback(problem);
        }

        if (id == SchemaStore.BuiltInId)
        {
            return _builtIn.Value;
        }

        var info = _store.Find(id);
        if (info is null)
        {
            return Fallback($"Aktivna šema '{id}' nije pronađena.");
        }

        var files = _store.TryLoadFiles(info, out var loadProblem);
        if (files is null)
        {
            return Fallback($"Aktivna šema „{info.Name}” je oštećena: {loadProblem}.");
        }

        if (!string.Equals(SchemaHash.ComputeSetHash(files), info.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return Fallback($"Aktivna šema „{info.Name}” je oštećena: fajlovi su izmijenjeni poslije uvoza (hash se ne poklapa).");
        }

        var compilation = SchemaCompiler.Compile(files, info.MainSchemaFile);
        if (!compilation.Succeeded)
        {
            return Fallback($"Aktivna šema „{info.Name}” ne može da se kompajlira: {compilation.Errors[0]}");
        }

        return new ActiveSchema(compilation.SchemaSet!, info, IsFallback: false, FallbackReason: null);
    }

    private ActiveSchema Fallback(string reason) =>
        _builtIn.Value with { IsFallback = true, FallbackReason = $"{reason} Koristi se ugrađena šema." };
}
