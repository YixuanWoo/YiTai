// ============================================================
// MainWindow.xaml.cs —— 主窗口行为（左原文 / 右整篇译稿）
// ============================================================
//
// 【保存的三条规矩】（跟"工作台"的写作台同一套）
//   1. 停手 1.2 秒自动存（DispatcherTimer，每敲一个字重置）；
//   2. Ctrl+S 立即存；
//   3. 存前比指纹，磁盘被外部改过就二选一，**绝不静默覆盖**。
//
// 【右边是一整篇，所以保存就是"原样写回"】
//   不做任何按段落重组的加工 —— 你手写的空行、缩进、批注全部保留。
//   回写时的换行/编码照原文件（第01章 CRLF、第02章 LF 都能原样还回去，
//   见 Core\TextFile.cs 的说明）。
//
// 【左栏高亮是估算】
//   源文没有分段信息，所以用"右栏光标在第几段"按比例映射到左栏句子。
//   界面上写明了"估算"，不假装精确。
// ============================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using YiTai.Core;

namespace YiTai.App;

public partial class MainWindow : Window
{
    private DeskConfig _cfg;
    private ChapterStore _store;

    private readonly ObservableCollection<ChapterVm> _chapters = new();
    private readonly ObservableCollection<SentenceVm> _sentenceView = new();
    private readonly DispatcherTimer _auto;

    private string _mdSha = "";
    private bool _loading;
    private bool _syncingList;
    private bool _dirty;
    private int _current;
    private int _currentSentence = -1;

    public MainWindow(DeskConfig cfg)
    {
        _cfg = cfg;
        _store = new ChapterStore(cfg.ToPaths(), cfg.ToProfile());

        InitializeComponent();

        Width = Math.Max(980, cfg.WindowWidth);
        Height = Math.Max(620, cfg.WindowHeight);

        ListSentences.ItemsSource = _sentenceView;
        ListChapters.ItemsSource = _chapters;

        _auto = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _auto.Tick += (_, __) =>
        {
            _auto.Stop();
            SaveQuietly();
        };

        Loaded += (_, __) => Boot();
        PreviewKeyDown += OnKeyDown;
    }

    // ================================================================ 启动

    private void Boot()
    {
        BuildChapterList();

        int start = _cfg.LastChapter;
        if (start < 1 || start > _cfg.ChapterCount) start = 1;

        if (!_chapters.Any(c => c.Number == start))
            start = _chapters.Count > 0 ? _chapters[0].Number : 1;

        LoadChapter(start);
    }

    private void BuildChapterList()
    {
        _syncingList = true;
        _chapters.Clear();

        foreach (var b in _store.ScanAll(_cfg.ChapterCount))
            _chapters.Add(new ChapterVm(b.Number, b.Title, b.Total) { Chars = b.Chars });

        _syncingList = false;
    }

    // ================================================================ 打开一章

    private void LoadChapter(int number)
    {
        if (_dirty) SaveQuietly();

        LoadResult res;
        try
        {
            res = _store.Load(number, DateTime.Now);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "这一章打不开：\n\n" + ex.Message, "打开章节",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _current = number;
        _mdSha = res.Md.Exists ? res.Md.Sha : "";
        _currentSentence = -1;
        _dirty = false;

        // ---- 左栏
        _sentenceView.Clear();
        for (int i = 0; i < res.State.Rows.Count; i++)
            _sentenceView.Add(new SentenceVm(res.State.Rows[i], i));

        TxtSrcHead.Text = $"英文原文　·　{_sentenceView.Count} 句";

        // ---- 右栏：有译稿读进来，没有就给一份只有章名的空稿
        _loading = true;
        TbxDoc.Text = res.Md.Exists && res.Md.Text.Trim().Length > 0
            ? res.Md.Text.Replace("\r\n", "\n").Replace("\n", "\r\n")
            : res.State.Title + "\r\n\r\n";
        _loading = false;

        TxtChapter.Text = res.State.Title;
        TxtSub.Text = $"{Path.GetFileName(_store.Paths.SourcePath(number))}"
                    + $"　·　{_sentenceView.Count} 句原文"
                    + "　·　右边写一整篇译文";
        TxtMdName.Text = Path.GetFileName(_store.Paths.TranslationPath(number));

        ScrollSentences.ScrollToTop();
        TbxDoc.CaretIndex = 0;

        _syncingList = true;
        ListChapters.SelectedIndex = IndexOfChapter(number);
        _syncingList = false;

        _cfg.LastChapter = number;

        UpdateStats();
        ComputeCurrent();
    }

    private int IndexOfChapter(int number)
    {
        for (int i = 0; i < _chapters.Count; i++)
            if (_chapters[i].Number == number) return i;
        return -1;
    }

    private void OnChapterPick(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingList) return;

        int idx = ListChapters.SelectedIndex;
        if (idx < 0 || idx >= _chapters.Count) return;
        if (_chapters[idx].Number == _current) return;

        LoadChapter(_chapters[idx].Number);
    }

    // ================================================================ 编辑

    private void OnDocChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;

        _dirty = true;
        _auto.Stop();
        _auto.Start();

        TxtStateL.Text = "● 未保存（停手 1.2 秒自动存，或 Ctrl+S）";
        UpdateStats();
        ComputeCurrent();
    }

    private void OnDocSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ComputeCurrent();
    }

    /// <summary>
    /// 右栏光标落在第几段 → 左栏高亮对应的句子。
    /// 段落位置按比例映射（源文没有分段，只能估算）。
    /// </summary>
    private void ComputeCurrent()
    {
        if (_sentenceView.Count == 0) return;

        string text = TbxDoc.Text ?? "";
        int caret = Math.Min(TbxDoc.CaretIndex, text.Length);
        string head = text.Substring(0, caret);

        int paraAtCaret = CountParagraphs(head) - 1;          // 第一段是章名
        int bodyParas = CountParagraphs(text) - 1;            // 正文段数

        int idx;
        if (bodyParas <= 0)
        {
            // 正文还一个字都没有：就指着第一句
            idx = 0;
        }
        else
        {
            if (paraAtCaret < 0) paraAtCaret = 0;
            if (paraAtCaret >= bodyParas) paraAtCaret = bodyParas - 1;

            // 第 k 段对应句区间的起点：写第一段时左边指第一句，
            // 写最后一段时指最后一组句子。按段落位置线性映射（估算）。
            idx = (int)Math.Floor((double)paraAtCaret * _sentenceView.Count / bodyParas);
        }

        if (idx < 0) idx = 0;
        if (idx > _sentenceView.Count - 1) idx = _sentenceView.Count - 1;

        if (idx == _currentSentence) return;
        _currentSentence = idx;

        foreach (var s in _sentenceView) s.IsCurrent = false;
        var target = _sentenceView[idx];
        target.IsCurrent = true;

        ScrollSentenceIntoView(target);
    }

    private void ScrollSentenceIntoView(SentenceVm vm)
    {
        ListSentences.UpdateLayout();
        if (ListSentences.ItemContainerGenerator.ContainerFromItem(vm) is FrameworkElement fe)
            fe.BringIntoView();
    }

    /// <summary>数"空行分隔"的段落块数（块内可能还有单换行）。</summary>
    private static int CountParagraphs(string text)
    {
        if (text.Length == 0) return 0;
        int count = 0;
        foreach (string block in Regex.Split(text, @"\r?\n[ \t]*\r?\n"))
        {
            if (block.Trim().Length > 0) count++;
        }
        return count;
    }

    // ================================================================ 保存

    private void SaveQuietly(bool force = false)
    {
        if (_current <= 0) return;

        SaveOutcome outcome;
        try
        {
            outcome = _store.SaveRaw(_current, TbxDoc.Text, _mdSha, DateTime.Now, force);
        }
        catch (Exception ex)
        {
            TxtStateL.Text = "⚠ 保存失败：" + ex.Message;
            return;
        }

        if (outcome.Conflict)
        {
            AskConflict();
            return;
        }

        if (!outcome.Ok)
        {
            TxtStateL.Text = "⚠ " + outcome.Reason;
            return;
        }

        _dirty = false;
        _mdSha = outcome.NewSha;

        TxtStateL.Text = "已保存 " + outcome.SavedAt.ToString("HH:mm:ss")
                       + (outcome.BackupPath != null ? "（今日首存已备份）" : "");

        RefreshChapterChars(_current);
        UpdateStats();
    }

    private void AskConflict()
    {
        var r = MessageBox.Show(this,
            "这一章的译稿在磁盘上被改过了（可能是 MarkText、git 切分支或别的程序）。\n\n"
            + "是 = 用我现在的覆盖磁盘\n"
            + "否 = 丢掉我的改动，重新读磁盘那一版\n"
            + "取消 = 先不存，我自己决定",
            "磁盘版 vs 我的版",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

        if (r == MessageBoxResult.Yes)
        {
            SaveQuietly(force: true);
            TxtStateL.Text = "已用我的版本覆盖磁盘";
        }
        else if (r == MessageBoxResult.No)
        {
            _dirty = false;
            LoadChapter(_current);
            TxtStateL.Text = "已重载磁盘版本";
        }
    }

    // ================================================================ 按钮与快捷键

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        _auto.Stop();
        SaveQuietly();
    }

    private void OnCopyEn(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not SentenceVm vm) return;

        try
        {
            Clipboard.SetText(vm.En);
            TxtStateL.Text = $"已复制第 {vm.NumText} 句（纯英文，不含译注）";
        }
        catch (Exception ex)
        {
            TxtStateL.Text = "复制失败：" + ex.Message;
        }
    }

    private void OnToggleNote(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not SentenceVm vm) return;
        vm.ToggleNote();
    }

    private void OnOpenTranslation(object sender, RoutedEventArgs e)
    {
        string path = _store.Paths.TranslationPath(_current);
        if (!File.Exists(path))
        {
            MessageBox.Show(this, "这一章还没有译稿文件 —— 写一句它就会自动生成。",
                            "打开译稿", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "打不开这个文件：" + ex.Message, "打开译稿",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnSwitchProject(object sender, RoutedEventArgs e)
    {
        if (_dirty) SaveQuietly();

        // 换项目也走导入窗口：先看清格式，再决定怎么读（不再"盲选文件"）
        var dlg = new ImportWindow();
        if (dlg.ShowDialog() != true || dlg.Result == null) return;

        App.ApplyImport(_cfg, dlg.Result);

        try { _cfg.Save(DeskConfig.DefaultPath); } catch { /* 存不下也能继续用 */ }

        _store = new ChapterStore(_cfg.ToPaths(), _cfg.ToProfile());
        BuildChapterList();
        LoadChapter(_chapters.Count > 0 ? _chapters[0].Number : 1);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _auto.Stop();
            SaveQuietly();
            e.Handled = true;
        }
    }

    // ================================================================ 统计

    private void UpdateStats()
    {
        string text = TbxDoc.Text ?? "";
        int chars = ChapterStore.CountChars(text.Replace("\r\n", "\n"));
        int paras = Math.Max(0, CountParagraphs(text) - 1);

        TxtStat.Text = $"{chars:N0} 字　·　{paras} 段　·　原文 {_sentenceView.Count} 句";
    }

    private void RefreshChapterChars(int number)
    {
        var row = _chapters.FirstOrDefault(c => c.Number == number);
        if (row == null) return;
        row.Chars = ChapterStore.CountChars(TbxDoc.Text.Replace("\r\n", "\n"));
    }

    // ================================================================ 关闭

    protected override void OnClosing(CancelEventArgs e)
    {
        _auto.Stop();

        if (_dirty)
        {
            var r = MessageBox.Show(this,
                "这一章还有没保存的改动。\n\n"
                + "是 = 保存后关闭\n"
                + "否 = 丢掉改动关闭\n"
                + "取消 = 继续编辑",
                "未保存的改动",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (r == MessageBoxResult.Cancel) { e.Cancel = true; return; }
            if (r == MessageBoxResult.Yes)
            {
                SaveQuietly();
                if (_dirty) { e.Cancel = true; return; }
            }
        }

        SaveConfig();
        base.OnClosing(e);
    }

    private void SaveConfig()
    {
        _cfg.LastChapter = _current;
        if (WindowState == WindowState.Normal)
        {
            _cfg.WindowWidth = ActualWidth;
            _cfg.WindowHeight = ActualHeight;
        }

        try { _cfg.Save(DeskConfig.DefaultPath); }
        catch { /* 配置存不下不影响退出 */ }
    }
}
