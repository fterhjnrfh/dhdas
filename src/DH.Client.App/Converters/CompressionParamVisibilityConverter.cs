using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace DH.Client.App.Converters;

/// <summary>
/// 压缩参数可见性转换器：根据 CompressionTypeIndex 和 ConverterParameter 判断
/// 参数面板是否应该可见。
/// ConverterParameter 是逗号分隔的可见索引列表，例如 "1,6" 表示 LZ4 和 LZ4_HC。
/// </summary>
public class CompressionParamVisibilityConverter : IValueConverter
{
    public static readonly CompressionParamVisibilityConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int index && parameter is string paramStr)
        {
            var parts = paramStr.Split(',');
            foreach (var part in parts)
            {
                if (int.TryParse(part.Trim(), out int match) && match == index)
                    return true;
            }
        }
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
