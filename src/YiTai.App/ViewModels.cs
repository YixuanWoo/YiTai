// ============================================================
// ViewModels.cs —— 界面上的两种数据：一行英文 / 一章目录
// ============================================================
//
// 设计改过一次，这里记下来免得以后又走回去：
//
//   ❌ 第一版：右边是"一句原文 + 一个译文框"的卡片行。
//      问题：译文被切成一格一格，读起来不像一篇文章；而且中文段落
//      一旦跨句，行与行的对应关系就撑不住了。
//
//   ✅ 现在：左边英文原文逐句列出（可复制、可看译注），右边是**一整篇**
//      译稿编辑区。所见即所存，段落怎么分完全由写的人决定。
//
// 于是"译到哪了"靠两件事表达：
//   1. 右边光标落在第几段 → 左边高亮对应的那几句（按段落位置估算，不是精确对齐）；
//   2. 左栏目录显示每章已写字数。
// 明确说"估算"：源文没有分段信息，任何声称精确的映射都是骗人的。
// ============================================================

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using YiTai.Core;

namespace YiTai.App;

public sealed class SentenceVm : INotifyPropertyChanged
{
    private static readonly SolidColorBrush BgCurrent = Freeze("#FFEFF6FD");
    private static readonly SolidColorBrush BgNone = Freeze("#00000000");
    private static readonly SolidColorBrush BarCurrent = Freeze("#FF2563EB");
    private static readonly SolidColorBrush BarNone = Freeze("#00000000");
    private static readonly SolidColorBrush FgCurrent = Freeze("#FF16202B");
    private static readonly SolidColorBrush FgNormal = Freeze("#FF44515F");

    private bool _noteOpen;
    private bool _isCurrent;

    public SentenceVm(Segment segment, int index)
    {
        Segment = segment;
        Index = index;
    }

    public Segment Segment { get; }

    public int Index { get; }

    public string En => Segment.En;

    public string Note => Segment.Note;

    public bool HasNote => Segment.Note.Length > 0;

    public string NumText => (Index + 1).ToString();

    /// <summary>当前正在译的那一句（由右侧光标位置估算出来）。</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            Raise(nameof(IsCurrent));
            Raise(nameof(RowBg));
            Raise(nameof(BarBrush));
            Raise(nameof(FgBrush));
        }
    }

    public Brush RowBg => _isCurrent ? BgCurrent : BgNone;
    public Brush BarBrush => _isCurrent ? BarCurrent : BarNone;
    public Brush FgBrush => _isCurrent ? FgCurrent : FgNormal;

    public Visibility NoteVisibility => HasNote && _noteOpen ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoteButtonVisibility => HasNote ? Visibility.Visible : Visibility.Collapsed;
    public string NoteButtonText => _noteOpen ? "注⌃" : "注⌄";

    public void ToggleNote()
    {
        _noteOpen = !_noteOpen;
        Raise(nameof(NoteVisibility));
        Raise(nameof(NoteButtonText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static SolidColorBrush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}

public sealed class ChapterVm : INotifyPropertyChanged
{
    private int _chars;

    public ChapterVm(int number, string title, int total)
    {
        Number = number;
        Title = title;
        Total = total;
    }

    public int Number { get; }

    public string Title { get; set; }

    /// <summary>原文句数。</summary>
    public int Total { get; }

    /// <summary>译稿已写字数（不含章名）。</summary>
    public int Chars
    {
        get => _chars;
        set
        {
            if (_chars == value) return;
            _chars = value;
            Raise(nameof(Chars));
            Raise(nameof(Display));
        }
    }

    public string Display => Chars > 0 ? $"{Chars:N0} 字" : "未开始";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
