// ============================================================
// LineNumRail.cs —— 行号轨：按正文"每一个逻辑行的真实 Y 坐标"摆号码
// ============================================================
//
// 旧做法（T-1cd1d1b7 之前）是一根独立的 TextBox，自己拿隐藏 TextBlock 去估算
// "这一行会折成几行"，再往行号文本里补空行。这条路有两个天生的裂缝：
//   · 估算的折行宽度和正文实际可用宽度差着左右 Padding（32px），栏一窄就少数行；
//   · 正文与行号栏是两根滚动条，只靠 VerticalOffset 抄来抄去，尺寸一变就脱钩。
// 结果就是"栏一拉窄，行号逐行往上漂"。
//
// 现在的做法是：不问"应该折几行"，直接问正文 **每一行第一个字实际落在哪个 Y**
// （TextBox.GetRectFromCharacterIndex / TextPointer.GetCharacterRect 都是视口坐标），
// 再把这个 Y 摆到行号层上。折行、换字号、拉宽拉窄，全由 WPF 的排版给出答案，
// 行号不再自己算一遍，自然不会再和正文算岔。
//
// 性能：位置按"内容坐标"算一次（Y + 当时的滚动偏移），滚动时整个 Canvas 平移，
// 不重算；只在滚出已摆好的窗口时才补摆一批（视口上下各多留 Buffer 像素），
// 所以几千行的稿子也不会每滚一格就重排一遍。
// ============================================================

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace YiTai.App;

public sealed class LineNumRail
{
    private const double PadX = 8;       // 数字离栏左右各留这么多（与旧行号栏的 10 视觉接近）
    private const double Buffer = 320;   // 视口上下各多摆这么一截，滚动时少重建

    private sealed class Row
    {
        public int Line;
        public double Y;        // 内容坐标（已含滚动偏移）
        public double Height;   // 这一视觉行的高（TextBox 实测约 19.8）
    }

    private readonly Canvas _canvas;
    private readonly TranslateTransform _shift = new();
    private readonly List<Row> _rows = new();
    private readonly List<TextBlock> _pool = new();

    private int _poolStart;          // 池里第一个数字对应 _rows 的哪一项
    private bool _poolValid;
    private double _offset;
    private double _viewport;

    public LineNumRail(Canvas canvas)
    {
        _canvas = canvas;
        // 号码的 Canvas.Top 是"内容坐标"（整篇稿子的 Y，可能是几千像素），靠 RenderTransform 平移
        // 到视口里。Canvas 自己的 ClipToBounds 是按**局部**坐标裁的，会把视口外的号连同视口内的
        // 号一起裁掉（平移发生在裁剪之后）——所以这里绝不开，裁剪交给外面那层 Border。
        _canvas.RenderTransform = _shift;
    }

    /// <summary>当前摆着的号码条数（诊断/自检用）。</summary>
    public int Count => _rows.Count;

    public void Clear()
    {
        _rows.Clear();
        _pool.Clear();
        _canvas.Children.Clear();
        _poolValid = false;
    }

    /// <summary>
    /// 重建。rows 里的 Y 必须是**内容坐标**（调用方把拿到的视口 Y 加上当时的滚动偏移），
    /// offset / viewport 是当前滚动位置与可视高度。
    /// </summary>
    public void Build(IEnumerable<(int Line, double Y, double Height)> rows, double offset, double viewport)
    {
        _rows.Clear();
        foreach (var r in rows)
        {
            if (r.Y < -1e5 || r.Y > 1e6) continue;          // 明显量坏的不要
            _rows.Add(new Row { Line = r.Line, Y = r.Y, Height = Math.Max(6, r.Height) });
        }

        _offset = offset;
        _viewport = viewport;
        _poolValid = false;
        Sync();
    }

    /// <summary>滚动：只平移，不重算；滚出已摆好的窗口才补摆一批。</summary>
    public void ScrollTo(double offset, double viewport)
    {
        _offset = offset;
        _viewport = viewport;

        if (_rows.Count == 0 || _viewport <= 0) return;

        if (_poolValid && _pool.Count > 0)
        {
            double top = _rows[_poolStart].Y;
            int last = _poolStart + _pool.Count - 1;
            double bottom = _rows[last].Y + _rows[last].Height;

            // 已经摆好的号码仍盖得住当前视口：只把整层平移 -offset
            if (offset >= top - 1 && offset + viewport <= bottom + 1)
            {
                _shift.Y = -offset;
                return;
            }
        }

        Sync();
    }

    /// <summary>
    /// 已摆好的号码是否真的盖得住这段视口。盖不住（例如稿子变长、栏位变窄、号码是上一版
    /// 排版量出来的）就说明位置已经过期，调用方应当重建而不是平移 —— 这是"行号整体错位
    /// 或滚到后面干脆没有号码"的自愈入口。
    /// </summary>
    public bool Covers(double offset, double viewport)
    {
        if (_rows.Count == 0 || viewport <= 0) return false;

        var first = _rows[0];
        var last = _rows[_rows.Count - 1];

        if (first.Y > offset + 2) return false;                     // 上面还缺一段
        if (last.Y + last.Height < offset + viewport - 2) return false; // 下面还缺一段
        return true;
    }

    /// <summary>按当前偏移摆出视口附近的那一段号码。</summary>
    private void Sync()
    {
        if (_rows.Count == 0 || _viewport <= 0)
        {
            _canvas.Children.Clear();
            _pool.Clear();
            _poolValid = false;
            return;
        }

        double lo = _offset - Buffer;
        double hi = _offset + _viewport + Buffer;

        int a = FirstRowAtOrAfter(lo);
        int b = LastRowAtOrBefore(hi);

        if (b < a)
        {
            _canvas.Children.Clear();
            _pool.Clear();
            _poolValid = false;
            return;
        }

        int need = b - a + 1;

        // 池子大小对齐（只增删差额，号码变了就改文本，不重建控件）
        while (_pool.Count > need)
        {
            _canvas.Children.Remove(_pool[_pool.Count - 1]);
            _pool.RemoveAt(_pool.Count - 1);
        }
        while (_pool.Count < need)
        {
            var t = new TextBlock
            {
                FontFamily = new FontFamily("Microsoft YaHei"),
                FontSize = 15,
                TextAlignment = TextAlignment.Right,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                IsHitTestVisible = false,
                Foreground = _canvas.TryFindResource("TextFaint") as Brush ?? Brushes.Gray,
            };
            Canvas.SetLeft(t, PadX);
            _canvas.Children.Add(t);
            _pool.Add(t);
        }

        // 数字要贴着行首那个字的行盒顶：行盒高按正文实测值给，
        // 同一字体、同一行高、同一个堆叠策略，基线才对得上。
        double width = Math.Max(0, _canvas.ActualWidth - PadX * 2);

        for (int i = 0; i < need; i++)
        {
            var row = _rows[a + i];
            var t = _pool[i];

            if (t.Text != row.Line.ToString())
                t.Text = row.Line.ToString();

            t.Width = width;
            t.LineHeight = row.Height;
            Canvas.SetTop(t, row.Y);
        }

        _poolStart = a;
        _poolValid = true;
        _shift.Y = -_offset;
    }

    private int FirstRowAtOrAfter(double y)
    {
        int lo = 0, hi = _rows.Count - 1, ans = _rows.Count;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_rows[mid].Y + _rows[mid].Height >= y) { ans = mid; hi = mid - 1; }
            else lo = mid + 1;
        }
        return ans;
    }

    private int LastRowAtOrBefore(double y)
    {
        int lo = 0, hi = _rows.Count - 1, ans = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_rows[mid].Y <= y) { ans = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return ans;
    }
}
