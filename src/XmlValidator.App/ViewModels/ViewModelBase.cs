using CommunityToolkit.Mvvm.ComponentModel;

namespace XmlValidator.App.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
}

/// <summary>Stavka padajuće liste: vrijednost i tekst za prikaz.</summary>
public sealed record Option<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Poruka za korisnika u traci na vrhu stranice (ili u statusnoj traci).</summary>
/// <param name="Text">Glavni tekst.</param>
/// <param name="Severity">Ozbiljnost (ikonica i boja).</param>
/// <param name="Details">Detalji (npr. greške uvoza), jedna stavka po liniji.</param>
public sealed record UserMessage(string Text, Services.MessageSeverity Severity, IReadOnlyList<string> Details)
{
    public bool HasDetails => Details.Count > 0;

    public static UserMessage Info(string text, IReadOnlyList<string>? details = null) => new(text, Services.MessageSeverity.Info, details ?? []);

    public static UserMessage Success(string text, IReadOnlyList<string>? details = null) => new(text, Services.MessageSeverity.Success, details ?? []);

    public static UserMessage Warning(string text, IReadOnlyList<string>? details = null) => new(text, Services.MessageSeverity.Warning, details ?? []);

    public static UserMessage Error(string text, IReadOnlyList<string>? details = null) => new(text, Services.MessageSeverity.Error, details ?? []);
}
