// ============================================================
// GrowTextBox.cs —— 会自己长高的 TextBox
// ============================================================
//
// WPF 的 TextBox 在 TextWrapping=Wrap 时**不会**按换行后的行数报告高度，
// 只会按一行算 —— 结果就是译文写到第二行时被截掉，用户得手动拉高。
// （这是 WPF 的老毛病，WinForms 反而没这个问题。）
//
// 对策：自己用 FormattedText 量一遍，把 Height 顶上去。
// 宽度跟着容器走（ActualWidth），所以拉大窗口也会重算。
//
// 只做"长高"，不做"长矮"以外的任何事：不改边距、不改字体。
// ============================================================

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace YiTai.App.UI;

public sealed class GrowTextBox : TextBox
{
    private bool _refitting;

    public GrowTextBox()
    {
        TextWrapping = TextWrapping.Wrap;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        SizeChanged += (_, __) => Refit();
        TextChanged += (_, __) => Refit();
    }

    /// <summary>最小高度（空框时也占这么高，行与行间距才稳定）。</summary>
    public double MinBoxHeight { get; set; } = 34;

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        Refit();
    }

    private void Refit()
    {
        if (_refitting) return;
        _refitting = true;

        try
        {
            string text = Text.Length == 0 ? " " : Text.Replace("\r\n", "\n");

            var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
            double dpi;
            try { dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip; }
            catch { dpi = 1.0; }

            var ft = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                FontSize,
                Brushes.Black,
                dpi);

            double avail = ActualWidth - Padding.Left - Padding.Right - 4;
            if (avail <= 20) return;   // 还没布局完，等 SizeChanged 再来一次

            ft.MaxTextWidth = avail;
            double h = ft.Height + Padding.Top + Padding.Bottom + 6;

            double target = Math.Max(MinBoxHeight, Math.Ceiling(h));
            if (Math.Abs(Height - target) > 0.5) Height = target;
        }
        finally
        {
            _refitting = false;
        }
    }
}
