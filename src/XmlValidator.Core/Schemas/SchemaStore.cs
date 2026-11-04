using System.Text.Json;
using System.Xml.Linq;

namespace XmlValidator.Core.Schemas;

/// <summary>
/// Skladište uvezenih šema. Svaka šema je u svom folderu <c>&lt;root&gt;/&lt;id&gt;/</c>, sa svojim
/// <c>.xsd</c> fajlovima i metapodacima u <c>schema.json</c>. Aktivna šema se pamti u
/// <c>&lt;root&gt;/active.json</c>. Ugrađena šema nije na disku, uvijek je u listi i ne može se obrisati.
/// </summary>
public sealed class SchemaStore
{
    /// <summary>Id ugrađene šeme.</summary>
    public const string BuiltInId = "ugradjena";

    internal const string MetadataFileName = "schema.json";
    internal const string ActiveFileName = "active.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly SchemaPackageLimits _limits;
    private long _version;

    /// <param name="rootPath">Korijenski folder skladišta (pravi se ako ne postoji).</param>
    /// <param name="timeProvider">Izvor vremena (za datum uvoza); podrazumijevano sistemsko vrijeme.</param>
    /// <param name="limits">Ograničenja paketa; podrazumijevano <see cref="SchemaPackageLimits.Default"/>.</param>
    public SchemaStore(string rootPath, TimeProvider? timeProvider = null, SchemaPackageLimits? limits = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        RootPath = Path.GetFullPath(rootPath);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _limits = limits ?? SchemaPackageLimits.Default;
        BuiltIn = BuiltInSchema.CreateInfo(BuiltInSchema.ReadContent());
    }

    /// <summary>Promijenjen sadržaj skladišta ili aktivna šema.</summary>
    public event EventHandler? Changed;

    public string RootPath { get; }

    /// <summary>Metapodaci ugrađene šeme.</summary>
    public SchemaInfo BuiltIn { get; }

    /// <summary>Brojač izmjena; raste sa svakom promjenom (za keširanje).</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>Id aktivne šeme; ako nije izabrana ili je zapis oštećen, <see cref="BuiltInId"/>.</summary>
    public string ActiveSchemaId => ReadActiveSelection().Id;

    /// <summary>Podrazumijevani korijen: <c>%AppData%/XmlValidator/Schemas</c>.</summary>
    public static string GetDefaultRootPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XmlValidator", "Schemas");

    /// <summary>Sve šeme: prvo ugrađena, pa uvezene od najnovije.</summary>
    public IReadOnlyList<SchemaInfo> List()
    {
        var imported = new List<SchemaInfo>();
        if (Directory.Exists(RootPath))
        {
            foreach (var directory in Directory.EnumerateDirectories(RootPath))
            {
                var name = Path.GetFileName(directory);
                if (name.StartsWith('.'))
                {
                    continue;
                }

                if (TryReadMetadata(name) is { } info)
                {
                    imported.Add(info);
                }
            }
        }

        return [BuiltIn, .. imported.OrderByDescending(s => s.ImportedAt).ThenBy(s => s.Name, StringComparer.CurrentCulture)];
    }

    /// <summary>Šema sa zadatim id-em, ili <c>null</c>.</summary>
    public SchemaInfo? Find(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return id == BuiltInId ? BuiltIn : TryReadMetadata(id);
    }

    /// <summary>Postavlja aktivnu šemu.</summary>
    public void SetActive(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_sync)
        {
            if (Find(id) is null)
            {
                throw new ArgumentException($"Šema '{id}' ne postoji.", nameof(id));
            }

            WriteActive(id);
        }

        OnChanged();
    }

    /// <summary>Briše uvezenu šemu. Ako je bila aktivna, aktivna postaje ugrađena.</summary>
    public void Delete(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (id == BuiltInId)
        {
            throw new InvalidOperationException("Ugrađena šema se ne može obrisati.");
        }

        lock (_sync)
        {
            if (TryReadMetadata(id) is null)
            {
                throw new ArgumentException($"Šema '{id}' ne postoji.", nameof(id));
            }

            if (ActiveSchemaId == id)
            {
                WriteActive(BuiltInId);
            }

            Directory.Delete(GetSchemaDirectory(id), recursive: true);
        }

        OnChanged();
    }

    /// <summary>Mijenja naziv uvezene šeme.</summary>
    public SchemaInfo Rename(string id, string name)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (id == BuiltInId)
        {
            throw new InvalidOperationException("Naziv ugrađene šeme se ne može mijenjati.");
        }

        SchemaInfo renamed;
        lock (_sync)
        {
            var info = TryReadMetadata(id) ?? throw new ArgumentException($"Šema '{id}' ne postoji.", nameof(id));
            renamed = info with { Name = name.Trim() };
            WriteAllBytesAtomic(Path.Combine(GetSchemaDirectory(id), MetadataFileName), SerializeMetadata(renamed));
        }

        OnChanged();
        return renamed;
    }

    /// <summary>
    /// Uvozi šemu iz <c>.xsd</c> fajla ili <c>.zip</c> paketa. Ništa se ne preuzima sa mreže. Uvezena
    /// šema ne postaje automatski aktivna.
    /// </summary>
    public Task<SchemaImportResult> ImportAsync(string path, SchemaImportOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Task.Run(() => Import(path, options ?? new SchemaImportOptions(), cancellationToken), cancellationToken);
    }

    internal (string Id, string? Problem) ReadActiveSelection()
    {
        var path = Path.Combine(RootPath, ActiveFileName);
        if (!File.Exists(path))
        {
            return (BuiltInId, null);
        }

        try
        {
            var active = JsonSerializer.Deserialize<ActiveSchemaFile>(File.ReadAllBytes(path), JsonOptions);
            return string.IsNullOrWhiteSpace(active?.ActiveSchemaId)
                ? (BuiltInId, $"Zapis o aktivnoj šemi ({ActiveFileName}) je prazan.")
                : (active.ActiveSchemaId, null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return (BuiltInId, $"Zapis o aktivnoj šemi ({ActiveFileName}) je oštećen: {ex.Message}");
        }
    }

    /// <summary>Učitava fajlove uvezene šeme; vraća <c>null</c> i razlog ako neki nedostaje.</summary>
    internal IReadOnlyList<SchemaFile>? TryLoadFiles(SchemaInfo info, out string? problem)
    {
        var directory = GetSchemaDirectory(info.Id);
        var files = new List<SchemaFile>();
        foreach (var relative in info.Files)
        {
            var path = Path.Combine(directory, relative);
            if (!IsInside(directory, path) || !File.Exists(path))
            {
                problem = $"nedostaje fajl '{relative}'";
                return null;
            }

            try
            {
                files.Add(new SchemaFile(relative, File.ReadAllBytes(path)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problem = $"fajl '{relative}' ne može da se pročita: {ex.Message}";
                return null;
            }
        }

        problem = null;
        return files;
    }

    private SchemaImportResult Import(string path, SchemaImportOptions options, CancellationToken cancellationToken)
    {
        var sourceName = Path.GetFileName(path);
        SchemaPackage package;
        try
        {
            package = SchemaPackageReader.Read(path, _limits, cancellationToken);
        }
        catch (SchemaPackageException ex)
        {
            return Failed(ex.Message, [], []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed($"Fajl '{sourceName}' ne može da se pročita: {ex.Message}", [], []);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var selection = SelectMainSchema(package, options.MainSchemaFile);
        if (selection.Result is { } early)
        {
            return early;
        }

        var main = selection.Main!;
        var compilation = selection.Compilation!;
        var setHash = SchemaHash.ComputeSetHash(package.Files);
        var contentHash = package.Files.Count == 1 ? SchemaHash.ComputeContentHash(package.Files[0].Content) : null;

        var result = StoreImported(package, main, compilation, sourceName, options, setHash, contentHash);
        if (result.Status == SchemaImportStatus.Imported)
        {
            OnChanged();
        }

        return result;
    }

    private SchemaImportResult StoreImported(
        SchemaPackage package,
        string main,
        SchemaCompilation compilation,
        string sourceName,
        SchemaImportOptions options,
        string setHash,
        string? contentHash)
    {
        lock (_sync)
        {
            if (IsSameContent(BuiltIn, setHash, contentHash))
            {
                return new SchemaImportResult(
                    SchemaImportStatus.AlreadyExistsBuiltIn, "Ova šema već postoji (ugrađena).", BuiltIn, [], [], package.IgnoredFiles);
            }

            var existing = List().FirstOrDefault(s => !s.IsBuiltIn && IsSameContent(s, setHash, contentHash));
            if (existing is not null)
            {
                return new SchemaImportResult(
                    SchemaImportStatus.AlreadyExists, $"Ova šema već postoji: „{existing.Name}”.", existing, [], [], package.IgnoredFiles);
            }

            var id = setHash[..16];
            var date = package.LastWriteTimes.TryGetValue(main, out var written) ? written : _timeProvider.GetLocalNow();
            var info = new SchemaInfo(
                Id: id,
                Name: string.IsNullOrWhiteSpace(options.Name)
                    ? $"{Path.GetFileNameWithoutExtension(main)} ({date:yyyy-MM-dd})"
                    : options.Name.Trim(),
                SourceFileName: sourceName,
                ImportedAt: _timeProvider.GetUtcNow(),
                Sha256: setHash,
                ContentSha256: contentHash,
                TargetNamespace: compilation.TargetNamespace ?? string.Empty,
                Version: compilation.Version,
                MainSchemaFile: main,
                Files: [.. package.Files.Select(f => f.RelativePath).Order(StringComparer.Ordinal)],
                IsBuiltIn: false);

            WriteSchemaDirectory(info, package.Files);
            var message = package.IgnoredFiles.Count > 0
                ? $"Šema „{info.Name}” je uvezena. Preskočeni fajlovi koji nisu .xsd: {string.Join(", ", package.IgnoredFiles)}."
                : $"Šema „{info.Name}” je uvezena.";
            return new SchemaImportResult(SchemaImportStatus.Imported, message, info, [], [], package.IgnoredFiles);
        }
    }

    private sealed record MainSchemaSelection(string? Main, SchemaCompilation? Compilation, SchemaImportResult? Result);

    private static MainSchemaSelection SelectMainSchema(SchemaPackage package, string? requestedMain)
    {
        if (requestedMain is not null)
        {
            var match = package.Files.FirstOrDefault(f => string.Equals(
                f.RelativePath, SchemaHash.NormalizePath(requestedMain), StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                return new(null, null, Failed($"Izabrana glavna šema '{requestedMain}' ne postoji u paketu.", [], package.IgnoredFiles));
            }

            var compiled = SchemaCompiler.Compile(package.Files, match.RelativePath);
            return compiled.Succeeded
                ? new(match.RelativePath, compiled, null)
                : new(null, null, Failed(FailedMessage(compiled.Errors), compiled.Errors, package.IgnoredFiles));
        }

        var analysis = AnalyzeFiles(package.Files);
        var unreferenced = analysis.Where(a => a.ParseError is null && !a.IsReferenced).ToList();
        var namespaceCandidates = unreferenced
            .Where(a => a.TargetNamespace == InvoiceSchemaConstants.TargetNamespace)
            .ToList();

        if (namespaceCandidates.Count == 0)
        {
            var parseErrors = analysis.Where(a => a.ParseError is not null).Select(a => a.ParseError!).ToList();
            List<string> errors = parseErrors.Count > 0
                ? parseErrors
                : unreferenced.Count > 0
                    ? [.. unreferenced.Select(a => SchemaCompiler.WrongNamespaceMessage(a.Path, a.TargetNamespace))]
                    : ["U paketu nije pronađena glavna šema (svaki fajl je uključen iz nekog drugog)."];
            return new(null, null, Failed(FailedMessage(errors), errors, package.IgnoredFiles));
        }

        var compilations = namespaceCandidates
            .Select(a => (a.Path, Compilation: SchemaCompiler.Compile(package.Files, a.Path)))
            .ToList();
        var succeeded = compilations.Where(c => c.Compilation.Succeeded).ToList();

        if (succeeded.Count == 1)
        {
            return new(succeeded[0].Path, succeeded[0].Compilation, null);
        }

        if (succeeded.Count > 1)
        {
            var candidates = succeeded.Select(c => c.Path).Order(StringComparer.Ordinal).ToList();
            return new(null, null, new SchemaImportResult(
                SchemaImportStatus.NeedsMainSchemaSelection,
                $"Paket sadrži više glavnih šema ({string.Join(", ", candidates)}). Izaberite koju treba uvesti.",
                null,
                [],
                candidates,
                package.IgnoredFiles));
        }

        var allErrors = compilations.Count == 1
            ? [.. compilations[0].Compilation.Errors]
            : compilations.SelectMany(c => c.Compilation.Errors.Select(e => $"{c.Path}: {e}")).ToList();
        return new(null, null, Failed(FailedMessage(allErrors), allErrors, package.IgnoredFiles));
    }

    private sealed record FileAnalysis(string Path, string? TargetNamespace, bool IsReferenced, string? ParseError);

    /// <summary>Čita svaki <c>.xsd</c> (bez kompajliranja): namespace i reference na druge fajlove.</summary>
    private static List<FileAnalysis> AnalyzeFiles(IReadOnlyList<SchemaFile> files)
    {
        XNamespace xs = "http://www.w3.org/2001/XMLSchema";
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parsed = new List<(SchemaFile File, string? TargetNamespace, string? Error)>();
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };

        foreach (var file in files)
        {
            try
            {
                using var stream = new MemoryStream(file.Content, writable: false);
                using var reader = XmlReader.Create(stream, settings);
                var document = XDocument.Load(reader, LoadOptions.SetLineInfo);
                var root = document.Root;
                if (root is null || root.Name != xs + "schema")
                {
                    parsed.Add((file, null, $"Fajl '{file.RelativePath}' nije XSD šema (korijenski element nije xs:schema)."));
                    continue;
                }

                var baseUri = PackageXmlResolver.GetUri(file.RelativePath);
                foreach (var reference in root.Elements()
                    .Where(e => e.Name == xs + "include" || e.Name == xs + "import" || e.Name == xs + "redefine" || e.Name == xs + "override"))
                {
                    var location = (string?)reference.Attribute("schemaLocation");
                    if (!string.IsNullOrWhiteSpace(location)
                        && Uri.TryCreate(baseUri, location.Trim(), out var target)
                        && PackageXmlResolver.TryGetRelativePath(target, out var relative))
                    {
                        referenced.Add(relative);
                    }
                }

                parsed.Add((file, (string?)root.Attribute("targetNamespace"), null));
            }
            catch (XmlException ex)
            {
                parsed.Add((file, null, $"Fajl '{file.RelativePath}' nije ispravan XML (linija {ex.LineNumber}, kolona {ex.LinePosition}): {ex.Message}"));
            }
        }

        return [.. parsed.Select(p => new FileAnalysis(p.File.RelativePath, p.TargetNamespace, referenced.Contains(p.File.RelativePath), p.Error))];
    }

    private static string FailedMessage(IReadOnlyList<string> errors) =>
        errors.Count == 1 ? $"Šema nije uvezena: {errors[0]}" : $"Šema nije uvezena ({errors.Count} grešaka).";

    private static SchemaImportResult Failed(string message, IReadOnlyList<string> errors, IReadOnlyList<string> ignored) =>
        new(SchemaImportStatus.Failed, message.StartsWith("Šema nije uvezena", StringComparison.Ordinal) ? message : $"Šema nije uvezena: {message}",
            null, errors.Count > 0 ? errors : [message], [], ignored);

    private static bool IsSameContent(SchemaInfo info, string setHash, string? contentHash) =>
        string.Equals(info.Sha256, setHash, StringComparison.OrdinalIgnoreCase)
        || (contentHash is not null && string.Equals(info.ContentSha256, contentHash, StringComparison.OrdinalIgnoreCase));

    private void WriteSchemaDirectory(SchemaInfo info, IReadOnlyList<SchemaFile> files)
    {
        Directory.CreateDirectory(RootPath);
        var temporary = Path.Combine(RootPath, $".tmp-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(temporary);
            foreach (var file in files)
            {
                var target = Path.GetFullPath(Path.Combine(temporary, file.RelativePath));
                if (!IsInside(temporary, target))
                {
                    throw new InvalidOperationException($"Nedozvoljena putanja '{file.RelativePath}'.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, file.Content);
            }

            File.WriteAllBytes(Path.Combine(temporary, MetadataFileName), SerializeMetadata(info));
            Directory.Move(temporary, GetSchemaDirectory(info.Id));
        }
        finally
        {
            if (Directory.Exists(temporary))
            {
                Directory.Delete(temporary, recursive: true);
            }
        }
    }

    private SchemaInfo? TryReadMetadata(string id)
    {
        if (id.Length == 0 || id.StartsWith('.') || id.IndexOfAny(['/', '\\', ':']) >= 0 || id.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        var path = Path.Combine(GetSchemaDirectory(id), MetadataFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<StoredSchemaMetadata>(File.ReadAllBytes(path), JsonOptions);
            if (stored is null || stored.Id != id || stored.Files is not { Count: > 0 })
            {
                return null;
            }

            return new SchemaInfo(
                stored.Id, stored.Name, stored.SourceFileName, stored.ImportedAt, stored.Sha256, stored.ContentSha256,
                stored.TargetNamespace, stored.Version, stored.MainSchemaFile, stored.Files, IsBuiltIn: false);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static byte[] SerializeMetadata(SchemaInfo info) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new StoredSchemaMetadata(
                1, info.Id, info.Name, info.SourceFileName, info.ImportedAt, info.Sha256, info.ContentSha256,
                info.TargetNamespace, info.Version, info.MainSchemaFile, [.. info.Files]),
            JsonOptions);

    private void WriteActive(string id)
    {
        Directory.CreateDirectory(RootPath);
        WriteAllBytesAtomic(
            Path.Combine(RootPath, ActiveFileName),
            JsonSerializer.SerializeToUtf8Bytes(new ActiveSchemaFile(id), JsonOptions));
    }

    private static void WriteAllBytesAtomic(string path, byte[] content)
    {
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, content);
        File.Move(temporary, path, overwrite: true);
    }

    private string GetSchemaDirectory(string id) => Path.Combine(RootPath, id);

    private static bool IsInside(string directory, string path)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private void OnChanged()
    {
        Interlocked.Increment(ref _version);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed record ActiveSchemaFile(string ActiveSchemaId);

    private sealed record StoredSchemaMetadata(
        int FormatVersion,
        string Id,
        string Name,
        string SourceFileName,
        DateTimeOffset? ImportedAt,
        string Sha256,
        string? ContentSha256,
        string TargetNamespace,
        string? Version,
        string MainSchemaFile,
        List<string> Files);
}
