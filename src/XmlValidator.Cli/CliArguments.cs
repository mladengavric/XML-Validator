namespace XmlValidator.Cli;

/// <summary>Opis opcije komande: ime (bez <c>--</c>) i da li traži vrijednost.</summary>
internal sealed record OptionSpec(string Name, bool HasValue);

/// <summary>Rastavljeni argumenti jedne komande.</summary>
internal sealed class CliArguments
{
    private readonly Dictionary<string, string?> _options;

    private CliArguments(IReadOnlyList<string> positionals, Dictionary<string, string?> options)
    {
        Positionals = positionals;
        _options = options;
    }

    public IReadOnlyList<string> Positionals { get; }

    public bool Has(string name) => _options.ContainsKey(name);

    public string? Value(string name) => _options.GetValueOrDefault(name);

    /// <summary>
    /// Rastavlja argumente: <c>--ime vrijednost</c>, <c>--ime=vrijednost</c>, zastavice <c>--ime</c> i pozicione
    /// argumente. Poslije <c>--</c> sve je poziciono (npr. fajl čije ime počinje crticom).
    /// </summary>
    /// <returns>Argumenti, ili <c>null</c> uz poruku greške.</returns>
    public static CliArguments? Parse(IEnumerable<string> args, IReadOnlyList<OptionSpec> specs, out string? error)
    {
        var positionals = new List<string>();
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        var onlyPositionals = false;
        using var enumerator = args.GetEnumerator();
        while (enumerator.MoveNext())
        {
            var arg = enumerator.Current;
            if (onlyPositionals || !arg.StartsWith('-') || arg == "-")
            {
                positionals.Add(arg);
                continue;
            }

            if (arg == "--")
            {
                onlyPositionals = true;
                continue;
            }

            var body = arg.StartsWith("--", StringComparison.Ordinal) ? arg[2..] : arg[1..];
            string? inlineValue = null;
            var equals = body.IndexOf('=', StringComparison.Ordinal);
            if (equals >= 0)
            {
                inlineValue = body[(equals + 1)..];
                body = body[..equals];
            }

            var spec = specs.FirstOrDefault(s => s.Name == body);
            if (spec is null)
            {
                error = $"Nepoznata opcija „{arg}”.";
                return null;
            }

            if (options.ContainsKey(spec.Name))
            {
                error = $"Opcija „--{spec.Name}” je navedena više puta.";
                return null;
            }

            if (!spec.HasValue)
            {
                if (inlineValue is not null)
                {
                    error = $"Opcija „--{spec.Name}” nema vrijednost.";
                    return null;
                }

                options[spec.Name] = null;
                continue;
            }

            if (inlineValue is null)
            {
                if (!enumerator.MoveNext() || enumerator.Current.StartsWith("--", StringComparison.Ordinal))
                {
                    error = $"Opcija „--{spec.Name}” traži vrijednost.";
                    return null;
                }

                inlineValue = enumerator.Current;
            }

            if (inlineValue.Length == 0)
            {
                error = $"Opcija „--{spec.Name}” traži vrijednost.";
                return null;
            }

            options[spec.Name] = inlineValue;
        }

        error = null;
        return new CliArguments(positionals, options);
    }
}
