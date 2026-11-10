// ============================================================
// ImportWindow.xaml.cs —— 导入原文：探测、预览、确认
// ============================================================
//
// 这个窗口存在的唯一理由是"别让程序替人赌"：
//   选文件 → 猜格式 → **真跑一遍** → 把结果摆在眼前 → 人点头才落库。
//
// 三处要小心的实现细节：
//   1. 换下拉框会触发 SelectionChanged；重填下拉框时又会触发一次。
//      所以用 _keep 挡住（跟写作台的 _suppress 一个道理）。
//   2. 预览要"真解析"，不是拿格式假装：统计里的句数和注数都来自
//      SourceParser.Parse 的真实结果，不是估的。
//   3. 选完文件说明白了要落到哪个项目根（ProjectLocator 反推），
//      反推不出来就退化成"单文件 + 同目录译文"。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using PairDesk.Core;

namespace PairDesk.App;

public partial class ImportWindow : Window
{
    private bool _keep;             // 正在程序性改控件，别当成用户操作
    private string _text = "";      // 原文全文
    private ProfileGuess? _guess;

    /// <param name="presetFile">
    /// 可选的预选文件：拖到 exe 上 / 命令行带参数时走这里，省一次点选。
    /// </param>
    public ImportWindow(string? presetFile = null)
    {
        InitializeComponent();
        CmbNotes.SelectedIndex = 0;
        CmbSplit.SelectedIndex = 0;

        if (presetFile != null && File.Exists(presetFile)) LoadFile(presetFile);

        RefreshPreview();
    }

    /// <summary>确认之后要带出去的东西：项目结构 + 解析口径。</summary>
    public DeskConfig? Result { get; private set; }

    // ---------------------------------------------------------------- 选文件

    private void OnPickFile(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选一份原文（随便哪一章都行，我据此推断整本书的结构）",
            Filter = "Markdown / 文本 (*.md;*.txt)|*.md;*.txt|所有文件 (*.*)|*.*",
        };

        if (dlg.ShowDialog(this) != true) return;
        LoadFile(dlg.FileName);
    }

    /// <summary>读一个文件进来，探测一次，摆好下拉框。</summary>
    private void LoadFile(string path)
    {
        var info = TextFile.Read(path);
        if (!info.Exists || info.Text.Trim().Length == 0)
        {
            MessageBox.Show(this, "这个文件读不出内容（可能不是文本，或者编码不对）。",
                            "导入", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _text = info.Text;
        TxtPath.Text = path;
        DetectAndFill();
    }

    /// <summary>探测一次，把下拉框摆到最可能的位置，然后预览。</summary>
    private void DetectAndFill()
    {
        if (_text.Length == 0) return;

        _guess = SourceProbe.Guess(_text);

        _keep = true;
        SelectByTag(CmbNotes, _guess.Profile.Notes.ToString());
        SelectByTag(CmbSplit, _guess.Profile.Split.ToString());
        _keep = false;

        RefreshPreview();
    }

    private void OnReProbe(object sender, RoutedEventArgs e) => DetectAndFill();

    // ---------------------------------------------------------------- 预览

    private void OnSettingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_keep) return;
        RefreshPreview();
    }

    private void OnPatternChanged(object sender, TextChangedEventArgs e)
    {
        if (_keep) return;
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        if (_text.Length == 0)
        {
            TxtStat.Text = "还没选文件。";
            TbxPreview.Text = "";
            BtnOk.IsEnabled = false;
            return;
        }

        var profile = CurrentProfile();
        var parsed = SourceParser.Parse(_text, profile);

        int noted = 0;
        foreach (var s in parsed.Segments) if (s.Note.Length > 0) noted++;

        var sb = new StringBuilder();
        sb.Append($"切成 {parsed.Segments.Count} 句　·　{noted} 句带注");
        if (parsed.Title.Length > 0) sb.Append($"　·　标题「{parsed.Title}」");
        if (parsed.MergedFragments > 0) sb.Append($"　·　并入碎片 {parsed.MergedFragments} 处");
        if (_guess != null && _guess.Explain.Length > 0) sb.Append($"\n探测依据：{_guess.Explain}");
        TxtStat.Text = sb.ToString();

        // 注悬挂警告：选了认注却没剥出来，说明这一档选错了
        var lines = new StringBuilder();
        int show = Math.Min(10, parsed.Segments.Count);

        for (int i = 0; i < show; i++)
        {
            var s = parsed.Segments[i];
            string en = s.En.Length > 96 ? s.En.Substring(0, 96) + "…" : s.En;

            lines.Append($"{i + 1,2}. {en}\n");
            if (s.Note.Length > 0)
            {
                string note = s.Note.Replace("\n", " ");
                if (note.Length > 90) note = note.Substring(0, 90) + "…";
                lines.Append($"    ↳ 注：{note}\n");
            }
        }

        if (parsed.Segments.Count > show)
            lines.Append($"\n…… 其余 {parsed.Segments.Count - show} 句同理，打开后左栏可逐句看。");

        if (profile.Notes != NoteStyle.None && noted == 0 && parsed.Segments.Count > 0)
            lines.Append("\n\n⚠ 这一档声称有注，但一句都没剥出来 —— 大概选错了，换一档再看。");

        if (parsed.Segments.Count == 0)
            lines.Append("⚠ 一句都没切出来。这个文件可能不是正文（比如整篇都是表格或代码）。");

        TbxPreview.Text = lines.ToString();
        BtnOk.IsEnabled = parsed.Segments.Count > 0;
    }

    private SourceProfile CurrentProfile()
    {
        string notes = SelectedTag(CmbNotes) ?? "InlineBoldParen";
        string split = SelectedTag(CmbSplit) ?? "LatinSentence";

        return new SourceProfile
        {
            Notes = (NoteStyle)Enum.Parse(typeof(NoteStyle), notes),
            Split = (SplitMode)Enum.Parse(typeof(SplitMode), split),
            CustomNotePattern = TbxPattern.Text.Trim(),
            ChineseHeuristics = true,
            TitlePrefix = "# ",
        };
    }

    // ---------------------------------------------------------------- 确认

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        if (TxtPath.Text.Length == 0 || _text.Length == 0) return;

        var derived = ProjectLocator.DeriveFromSourceFile(TxtPath.Text);
        var profile = CurrentProfile();

        derived.Notes = profile.Notes;
        derived.Split = profile.Split;
        derived.CustomNotePattern = profile.CustomNotePattern;

        Result = derived;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    // ---------------------------------------------------------------- 小工具

    private void SelectByTag(ComboBox box, string tag)
    {
        foreach (var item in box.Items)
        {
            if (item is ComboBoxItem ci && (ci.Tag as string) == tag)
            {
                box.SelectedItem = ci;
                return;
            }
        }
    }

    private static string? SelectedTag(ComboBox box)
        => box.SelectedItem is ComboBoxItem ci ? ci.Tag as string : null;
}
