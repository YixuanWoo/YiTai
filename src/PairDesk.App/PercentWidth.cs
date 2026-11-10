// ============================================================
// PercentWidth.cs —— 把 ProgressBar 的 Value/Maximum 换算成像素宽度
// ============================================================
//
// WPF 的 ProgressBar 默认模板带一圈斜纹动画，在"每章一条细线"这种
// 小尺寸上很难看。所以自绘一条 3px 的线：宽度靠这个转换器算。
// ============================================================

using System;
using System.Globalization;
using System.Windows.Data;

namespace PairDesk.App;

public sealed class PercentWidth : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 3) return 0d;

        double value = values[0] is double d1 ? d1 : 0;
        double max = values[1] is double d2 ? d2 : 0;
        double width = values[2] is double d3 ? d3 : 0;

        if (max <= 0 || width <= 0) return 0d;
        return Math.Max(0, Math.Min(width, width * (value / max)));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
