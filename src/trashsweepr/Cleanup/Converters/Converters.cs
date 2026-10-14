using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TrashSweepr.Cleanup.Converters;

/// <summary>Visível quando o valor é nulo/vazio (ou o contrário, com <see cref="Invert"/>).</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    /// <summary>Se verdadeiro: visível quando NÃO é nulo/vazio.</summary>
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isEmpty = value is null || (value is string text && text.Length == 0);
        return isEmpty != Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Opacidade menor para itens que não podem ser marcados (já aplicados, incompatíveis).</summary>
public sealed class DimWhenFalseConverter : IValueConverter
{
    public double DimOpacity { get; set; } = 0.55;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? 1.0 : DimOpacity;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
