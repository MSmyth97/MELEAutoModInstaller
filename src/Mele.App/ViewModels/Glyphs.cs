using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Mele.Engine.Orchestration;

namespace Mele.App.ViewModels;

/// <summary>Small value converters used by the window.</summary>
public static class Glyphs
{
    public static readonly IValueConverter Check = new FuncConverter<bool, string>(ok => ok ? "✓" : "✗");
    public static readonly IValueConverter Weight = new FuncConverter<bool, FontWeight>(h => h ? FontWeight.SemiBold : FontWeight.Normal);

    private sealed class FuncConverter<TIn, TOut>(Func<TIn, TOut> f) : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is TIn v ? f(v) : default(TOut);
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}

public sealed record M3BatchRow(string Display);
