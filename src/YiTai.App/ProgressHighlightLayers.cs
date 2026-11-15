// ============================================================
// ProgressHighlightLayers.cs —— 进度高亮的三层画笔
// ============================================================
//
// 三层是：底带（半透明主色，每视觉行一段）→ 下划线（每段下沿 2px 实色）→ 起笔标（3px）。
// 都画在正文**外面**的浮层 Canvas 上，不插字、不切段、不改行距。
//
// 这里单独抽出来，是为了能被"真 WPF 探针"直接调用去截图验证：
// 之前出过一次事故 —— 底带和下划线套在一个 Canvas 里，而嵌套 Canvas 的自适应尺寸是 0×0，
// 再 `ClipToBounds` 就把它们整片裁掉了：屏幕上只剩 3px 的起笔标。
// 所以现在每个图层都必须显式给出与正文视口一致的宽高（见 EnsureSize）。
//
// 颜色：琥珀 #E8A33D。撞上 ==高亮== 的粉底时改用加深的 #C9861F，且不画底带
// （两片半透明色叠在一起会发脏）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using YiTai.Core;

namespace YiTai.App;

public static class ProgressHighlightLayers
{
    /// <summary>进度主色：琥珀。</summary>
    public static readonly Color Main = Color.FromRgb(0xE8, 0xA3, 0x3D);

    /// <summary>和 ==高亮== 撞上时用的加深色。</summary>
    public static readonly Color Deep = Color.FromRgb(0xC9, 0x86, 0x1F);

    public static readonly Brush MainBrush = MakeBrush(Main);
    public static readonly Brush DeepBrush = MakeBrush(Deep);

    private static Brush MakeBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>底带用的半透明主色（alpha = 0~1）。</summary>
    public static Brush BandBrush(double alpha)
    {
        byte a = (byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255);
        var b = new SolidColorBrush(Color.FromArgb(a, Main.R, Main.G, Main.B));
        b.Freeze();
        return b;
    }

    /// <summary>
    /// 把图层撑到正文视口那么大。
    /// **这一步不能省**：Canvas 的自适应尺寸是 0×0，套在父 Canvas 里就永远是 0×0，
    /// 配上 ClipToBounds 等于把整层内容裁没 —— 只剩浮层直系子节点的起笔标可见。
    /// </summary>
    public static void EnsureSize(Canvas? layer, double width, double height)
    {
        if (layer == null || width <= 0 || height <= 0) return;

        // 注意：新建 Canvas 的 Width/Height 是 double.NaN，而 NaN 参与任何比较都是 false。
        // 写成 `Math.Abs(layer.Width - width) > 0.5` 会漏掉 NaN 这一种（真机上就是这么漏的），
        // 所以先说清楚 NaN 也算"尺寸不对"。
        if (double.IsNaN(layer.Width) || Math.Abs(layer.Width - width) > 0.5) layer.Width = width;
        if (double.IsNaN(layer.Height) || Math.Abs(layer.Height - height) > 0.5) layer.Height = height;
    }

    /// <summary>底带：句子里每个视觉行一段，圆角。alpha 传 0 表示这一句不画底带（撞高亮时）。</summary>
    public static void SyncBand(Canvas? layer, IReadOnlyList<Rect> rects, double alpha)
    {
        if (layer == null) return;

        while (layer.Children.Count > rects.Count) layer.Children.RemoveAt(layer.Children.Count - 1);
        while (layer.Children.Count < rects.Count)
            layer.Children.Add(new Rectangle
            {
                RadiusX = 3,
                RadiusY = 3,
                IsHitTestVisible = false,
            });

        Brush? fill = alpha <= 0 ? null : BandBrush(alpha);

        for (int i = 0; i < rects.Count; i++)
        {
            var shape = (Rectangle)layer.Children[i];
            Rect r = rects[i];

            shape.Fill = fill;
            shape.Width = Math.Max(1, r.Width);
            shape.Height = Math.Max(4, r.Height);
            Canvas.SetLeft(shape, r.X);
            Canvas.SetTop(shape, r.Y);
        }
    }

    /// <summary>下划线：每段下沿 2px 实色。撞高亮时换成加深色，让它压得住粉底。</summary>
    public static void SyncUnderline(Canvas? layer, IReadOnlyList<Rect> rects, bool clash)
    {
        if (layer == null) return;

        while (layer.Children.Count > rects.Count) layer.Children.RemoveAt(layer.Children.Count - 1);
        while (layer.Children.Count < rects.Count)
            layer.Children.Add(new Rectangle { IsHitTestVisible = false });

        Brush fill = clash ? DeepBrush : MainBrush;

        for (int i = 0; i < rects.Count; i++)
        {
            var shape = (Rectangle)layer.Children[i];
            Rect r = rects[i];

            shape.Fill = fill;
            shape.Width = Math.Max(1, r.Width);
            shape.Height = 2;
            Canvas.SetLeft(shape, r.X);
            Canvas.SetTop(shape, r.Y + Math.Max(4, r.Height) - 2);
        }
    }

    public static void Clear(params Canvas?[] layers)
    {
        foreach (var layer in layers) layer?.Children.Clear();
    }

    // ---------------------------------------------------------------- 分块：底带该让哪儿给 ==高亮==

    /// <summary>
    /// 一次重画要用的全部矩形。
    ///   <see cref="Sentence"/> 整句（起笔标按它的第一段定位）；
    ///   <see cref="Band"/>     只画底带的部分（已经减掉 ==高亮==）；
    ///   <see cref="Underline"/>只画下划线的部分；
    ///   <see cref="Mixed"/>    这一句里有没有 ==高亮==（有的话下划线换加深色）。
    /// </summary>
    public readonly record struct Plan(
        IReadOnlyList<Rect> Sentence,
        IReadOnlyList<Rect> Band,
        IReadOnlyList<Rect> Underline,
        bool Mixed);

    /// <summary>
    /// 把"一句"拆成"没标过的"和"标过的"两组矩形。
    ///
    /// 为什么不能整句一刀切：一句话里常常只有几个词被 ==...== 标过。
    /// 早先的写法是"碰上一点高亮就整句不画底带"，结果那句里没标过的字也跟着不亮了。
    /// 现在没标过的部分照常画底带，只有标过的部分改画加深下划线，底带在那里让开
    /// （两片半透明色叠一起会发脏）。
    /// </summary>
    public static Plan PlanRects(
        FlowDocument doc, int start, int end, IReadOnlyList<(int Start, int End)>? highlights)
    {
        var sentence = MdRender.RectSegments(doc, start, end);

        var overlap = RangeMath.Intersect(start, end, highlights);
        var plain = RangeMath.Subtract(start, end, highlights);

        if (overlap.Count == 0) return new Plan(sentence, sentence, sentence, false);

        var band = new List<Rect>();
        foreach (var (s, e) in plain)
            if (e > s) band.AddRange(MdRender.RectSegments(doc, s, e));

        var underline = new List<Rect>();
        foreach (var (s, e) in overlap)
            if (e > s) underline.AddRange(MdRender.RectSegments(doc, s, e));

        return new Plan(sentence, band, underline, true);
    }
}
