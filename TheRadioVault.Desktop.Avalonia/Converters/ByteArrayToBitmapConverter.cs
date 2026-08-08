using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace TheRadioVault.Desktop.Avalonia.Converters;

public sealed class ByteArrayToBitmapConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not byte[] bytes || bytes.Length == 0) return AvaloniaProperty.UnsetValue;
        try { return new Bitmap(new MemoryStream(bytes, writable: false)); }
        catch { return AvaloniaProperty.UnsetValue; }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => AvaloniaProperty.UnsetValue;
}
