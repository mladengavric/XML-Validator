using Avalonia;
using XmlValidator.App.Services;

namespace XmlValidator.App.Controls;

/// <summary>
/// Prikazna pomoć: pretvara <see cref="MessageSeverity"/> u klasu stila (<c>info</c>, <c>success</c>,
/// <c>warning</c>, <c>error</c>), da bi stilovi iz <c>Controls.axaml</c> birali ikonicu i boje.
/// Upotreba: <c>ui:SeverityStyle.Severity="{Binding StatusSeverity}"</c>.
/// </summary>
public sealed class SeverityStyle : AvaloniaObject
{
    public static readonly AttachedProperty<MessageSeverity?> SeverityProperty =
        AvaloniaProperty.RegisterAttached<SeverityStyle, StyledElement, MessageSeverity?>("Severity");

    private static readonly (MessageSeverity Severity, string ClassName)[] ClassNames =
    [
        (MessageSeverity.Info, "info"),
        (MessageSeverity.Success, "success"),
        (MessageSeverity.Warning, "warning"),
        (MessageSeverity.Error, "error"),
    ];

    static SeverityStyle()
    {
        SeverityProperty.Changed.AddClassHandler<StyledElement>((element, e) =>
        {
            var value = e.NewValue as MessageSeverity?;
            foreach (var (severity, className) in ClassNames)
            {
                element.Classes.Set(className, value == severity);
            }
        });
    }

    public static MessageSeverity? GetSeverity(StyledElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(SeverityProperty);
    }

    public static void SetSeverity(StyledElement element, MessageSeverity? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(SeverityProperty, value);
    }
}
