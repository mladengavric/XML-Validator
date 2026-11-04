using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace XmlValidator.App.Controls;

/// <summary>
/// Prikazna pomoć za grupu dugmadi (npr. filter): <c>true</c> kad je vrijednost enum-a jednaka parametru
/// (naziv člana), a izbor dugmeta vraća tu vrijednost. Isključivanje dugmeta ne mijenja vrijednost.
/// Upotreba: <c>IsChecked="{Binding Filter, Converter={x:Static ui:EnumEqualsConverter.Instance}, ConverterParameter=Deleted}"</c>.
/// </summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public static EnumEqualsConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Enum && parameter is string name && string.Equals(value.ToString(), name, StringComparison.Ordinal);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        return value is true && parameter is string name && enumType.IsEnum && Enum.TryParse(enumType, name, out var result)
            ? result
            : BindingOperations.DoNothing;
    }
}
