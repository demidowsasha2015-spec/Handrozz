using System.Globalization;
using System.Windows.Data;
using UniMouse.Core.Model;

namespace UniMouse.Wpf;

/// <summary>"key" <-> StepKind.Key using the schema's camelCase tokens.</summary>
public sealed class StepKindTokenConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is StepKind k ? ToToken(k) : "delay";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s ? FromToken(s) : StepKind.Delay;

    public static string ToToken(StepKind kind) => kind switch
    {
        StepKind.Key => "key",
        StepKind.Text => "text",
        StepKind.MouseMove => "mouseMove",
        StepKind.MouseClick => "mouseClick",
        StepKind.MouseScroll => "mouseScroll",
        _ => "delay",
    };

    public static StepKind FromToken(string token) => token.Trim().ToLowerInvariant() switch
    {
        "key" => StepKind.Key,
        "text" => StepKind.Text,
        "mousemove" => StepKind.MouseMove,
        "mouseclick" => StepKind.MouseClick,
        "mousescroll" => StepKind.MouseScroll,
        _ => StepKind.Delay,
    };
}

/// <summary>True -> Visible, false -> Collapsed (field relevance per step kind).</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var b = value is bool x && x;
        if (parameter is string p && p.Equals("invert", StringComparison.OrdinalIgnoreCase)) b = !b;
        return b ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Shows "Step N: message" when a row has a validation error.</summary>
public sealed class StepErrorFormatter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var idx = values[0] is int i ? i + 1 : 0;
        var err = values[1] as string;
        return string.IsNullOrEmpty(err) ? string.Empty : $"Step {idx}: {err}";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
