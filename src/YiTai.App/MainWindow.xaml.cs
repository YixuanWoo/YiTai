// ============================================================
// MainWindow.xaml.cs —— 译台主窗口的全部交互
// ============================================================
//
// 这个文件只做"把界面动作翻译成 Core 调用"，自己不解析、不拼路径、
// 不管目录布局（那些都在 YiTai.Core 里）。加新功能时优先加 Core 能力，
// 界面只接线 —— 这样功能可以无限加，界面不会越写越乱。
//
// 落盘策略（四道保险）：
//   1. 停手 AutoSaveDelayMs（默认 1.2s）后自动保存；
//   2. Ctrl+S 立刻保存；
//   3. 切源文件 / 关窗口前强制保存一次；
//   4. 磁盘被别的程序改过 → 弹窗让用户选，不静默覆盖。
//
// 空稿保护：译文全空时拒绝落盘（防一次误全选删除把整章清空）。
// ============================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using YiTai.Core;

namespace YiTai.App;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DispatcherTimer _autoSave = new();

    private ProjectStore? _store;
    private SourceEntry? _current;
    private TranslationDoc? _doc;

    private bool _dirty;        // 有未保存的改动
    private bool _loading;      // 正在程序性填内容，别把 TextChanged 当用户输入
    private bool _previewing;   // 译文栏是否切到了"渲染预览"（编辑区仍在，只是藏起来）

    public MainWindow()
    {
        InitializeComponent();

        InitPanes();   // 先摆一排空栏；打开上次项目时再按项目记忆替换

        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;

        _autoSave.Tick += (_, _) => { _autoSave.Stop(); SaveNow(quiet: true); };

        // 上次的项目：直接接着干；没有就留空，等人从菜单里新建/打开
        if (_settings.LastProjectRoot.Length > 0 && File.Exists(ProjectLayout.ProjectFile(_settings.LastProjectRoot)))
        {
            try
            {
                OpenProject(_settings.LastProjectRoot, _settings.LastSourceId);
            }
            catch
            {
                SetEmptyState();   // 项目被搬走/删了，不该让软件起不来
            }
        }
        else
        {
            SetEmptyState();
        }
    }

    // ================================================================ 项目管理

    private void OnNewProject(object sender, RoutedEventArgs e) => NewProject();

    private void OnOpenProject(object sender, RoutedEventArgs e) => OpenProjectDialog();

    private void NewProject()
    {
        if (!ConfirmLeaveCurrent()) return;

        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选一个文件夹作为项目根（会在里面建 sources / translations / backups）",
            Multiselect = false,
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var store = ProjectStore.CreateNew(dlg.FolderName, Path.GetFileName(dlg.FolderName));
            OpenProject(store.Root, null);
        }
        catch (Exception ex)
        {
            Warn("新建项目失败", ex.Message);
        }
    }

    private void OpenProjectDialog()
    {
        if (!ConfirmLeaveCurrent()) return;

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "打开译台项目（选 project.json，或直接选项目文件夹里的它）",
            Filter = "译台项目 (project.json)|project.json|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            OpenProject(Path.GetDirectoryName(dlg.FileName)!, null);
        }
        catch (Exception ex)
        {
            Warn("打开项目失败", ex.Message);
        }
    }

    /// <summary>给外部（App 解析命令行/拖拽）用：从项目根打开。</summary>
    public void OpenProjectFromOutside(string root)
    {
        if (!ConfirmLeaveCurrent()) return;
        OpenProject(root, null);
    }

    /// <summary>装载一个项目：刷新标题、源文件下拉、默认选中上次那一份。</summary>
    private void OpenProject(string rootOrProjectFile, string? wantSourceId)
    {
        SavePaneLayout();   // 先把上一个项目的栏位记下来，再换
        _current = null;    // 旧项目的章节不再算数，免得新项目还没装载时栏里显示上一本的字

        _store = ProjectStore.Open(rootOrProjectFile);
        _settings.LastProjectRoot = _store.Root;

        Title = $"译台 — {_store.Meta.Name}";
        TxtProject.Text = _store.Meta.Name;
        TxtProjectPath.Text = _store.Root;

        _loading = true;
        CmbSources.ItemsSource = null;
        CmbSources.ItemsSource = _store.Meta.Sources;
        CmbSources.DisplayMemberPath = "FileName";
        _loading = false;

        LoadPanesForProject();   // 这个项目上次摆了几栏，就摆几栏

        bool hasSources = _store.Meta.Sources.Count > 0;
        CmbSources.IsEnabled = hasSources;
        TbxTranslation.IsEnabled = hasSources;
        MiSave.IsEnabled = hasSources;
        MiSaveAs.IsEnabled = hasSources;

        if (!hasSources)
        {
            _current = null;
            _doc = null;
            SetPanesEmpty("这个项目还没有源文件。文件 → 导入源文件，选几个 md/txt 进来。");
            return;
        }

        string? id = wantSourceId ?? _store.Meta.LastSourceId;
        SourceEntry entry = _store.Meta.Sources.Find(s => s.Id == id) ?? _store.Meta.Sources[0];

        _loading = true;
        CmbSources.SelectedItem = entry;
        _loading = false;
        LoadSource(entry);
    }

    // ================================================================ 导入

    private void OnImport(object sender, RoutedEventArgs e)
    {
        if (_store == null)
        {
            Warn("还没有项目", "先“新建项目”或“打开项目”，再导入源文件。");
            return;
        }

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入源文件（会复制进项目的 sources 文件夹；可多选）",
            Filter = "文本 (*.md;*.txt;*.markdown)|*.md;*.txt;*.markdown|所有文件 (*.*)|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var added = _store.ImportFiles(dlg.FileNames);
            if (added.Count == 0) return;

            // 刷新下拉（ItemsSource 是同一个 List 实例，重新赋值才会重绘）
            _loading = true;
            CmbSources.ItemsSource = null;
            CmbSources.ItemsSource = _store.Meta.Sources;
            CmbSources.IsEnabled = true;
            TbxTranslation.IsEnabled = true;
            MiSave.IsEnabled = true;
            MiSaveAs.IsEnabled = true;
            CmbSources.SelectedItem = added[0];
            _loading = false;

            LoadSource(added[0]);
            RebuildPanes();   // 下拉里要出现刚导入的这几份
            Status($"已导入 {added.Count} 个文件");
        }
        catch (Exception ex)
        {
            Warn("导入失败", ex.Message);
        }
    }

    // ================================================================ 关联已有译文

    /// <summary>
    /// 迁移旧工作流用：源文件 ch01..ch27 + 旧译文 第01章..第27章，
    /// 名字对不上但顺序一致，按顺序配对即可，不必一章章「另存为」。
    /// </summary>
    private void OnLinkTranslations(object sender, RoutedEventArgs e)
    {
        if (_store == null) { Warn("还没有项目", "先新建或打开一个项目。"); return; }
        if (_store.Meta.Sources.Count == 0) { Warn("还没有源文件", "先导入源文件，再来关联译文。"); return; }

        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选放旧译文的文件夹（按章号与源文件配对，配不准的会跳过）",
        };
        if (dlg.ShowDialog(this) != true) return;

        (int n, List<int> skipped) = _store.LinkTranslationsByNumber(dlg.FolderName);

        if (n == 0)
        {
            // 按章号配不上，退一步按顺序（仅当两边数量相等）
            n = _store.LinkTranslationsByOrder(dlg.FolderName);
            if (n == 0)
            {
                Warn("没能配对",
                     $"源文件 {_store.Meta.Sources.Count} 份，但这个文件夹里没找到能对上的译文。\n" +
                     "按章号对不上、数量也不相等，所以一份都没改。\n" +
                     "可以先用「另存为」逐份指定。");
                return;
            }
        }

        string msg = $"已关联 {n} 份译文";
        if (skipped.Count > 0)
            msg += "；这些章号有多个候选，没替你猜：" + string.Join("、", skipped);
        Status(msg);
        if (_current != null) LoadSource(_current);
        RebuildPanes();   // 译文指向变了，下拉里的「译文 · …」要跟着更新
    }

    // ================================================================ 切换源文件

    private void OnSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (CmbSources.SelectedItem is not SourceEntry entry) return;
        if (_current != null && entry.Id == _current.Id) return;

        // 切走之前先把上一份存掉（不然切一下就把刚写的丢了）
        if (!SaveNow(quiet: false)) return;

        LoadSource(entry);
    }

    /// <summary>把一份源文件连同它的译文装进界面。</summary>
    private void LoadSource(SourceEntry entry)
    {
        _current = entry;
        _settings.LastSourceId = entry.Id;
        _store!.Meta.LastSourceId = entry.Id;
        _store.Save();

        string trPath = _store.TranslationPath(entry);
        _doc = TranslationStore.Load(trPath);

        _loading = true;
        TbxTranslation.Text = _doc.TextLf;
        _loading = false;

        _dirty = false;
        _autoSave.Stop();

        TxtTrPath.Text = RelativeForDisplay(trPath);
        RefreshPanes();   // 「跟随当前章」的那些对照栏跟着换
        if (_previewing) RefreshPreview();   // 预览态下切章，画面也得跟着翻
        UpdateStat();
        Status(_doc.Exists ? "已载入" : "译文文件还不存在，一开写就会建");
    }

    // ================================================================ 编辑 / 保存

    private void OnTranslationChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;

        _dirty = true;
        UpdateStat();

        int delay = Math.Max(0, _settings.AutoSaveDelayMs);
        if (delay == 0) { TxtSaveState.Text = "未保存"; return; }

        _autoSave.Stop();
        _autoSave.Interval = TimeSpan.FromMilliseconds(delay);
        _autoSave.Start();
        TxtSaveState.Text = "未保存（停手后自动存）";
    }

    private void OnSave(object sender, RoutedEventArgs e) => SaveNow(quiet: false);

    /// <summary>
    /// 真正落盘。quiet=true 时（自动保存）失败不弹窗，只把状态写进状态栏，
    /// 免得每次重试都糊用户一脸。
    /// </summary>
    private bool SaveNow(bool quiet)
    {
        _autoSave.Stop();

        if (_store == null || _current == null) return true;
        if (!_dirty) return true;

        string path = _store.TranslationPath(_current);

        try
        {
            _doc = TranslationStore.Save(
                path, TbxTranslation.Text, _doc?.Sha,
                ProjectLayout.Backups(_store.Root), DateTime.Now);

            _dirty = false;
            Status($"已保存 {DateTime.Now:HH:mm:ss}");
            TxtSaveState.Text = "已保存";
            return true;
        }
        catch (TranslationConflictException ex)
        {
            if (quiet) { TxtSaveState.Text = "磁盘上的文件被改过，等你决定"; return false; }
            return ResolveConflict(path, ex);
        }
        catch (InvalidOperationException ex)
        {
            if (!quiet) Warn("没保存", ex.Message);
            else TxtSaveState.Text = "没保存：" + ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            if (!quiet) Warn("保存失败", ex.Message);
            else TxtSaveState.Text = "保存失败";
            return false;
        }
    }

    /// <summary>磁盘被别的程序改过：让用户二选一，绝不替他决定。</summary>
    private bool ResolveConflict(string path, TranslationConflictException ex)
    {
        var r = MessageBox.Show(this,
            "磁盘上的这份译文被别的程序改过了。\n\n" +
            "是：用我这边的内容覆盖磁盘\n" +
            "否：放弃我这边，改成读磁盘上的版本\n" +
            "取消：先什么都不做\n\n" + path,
            "文件冲突", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

        if (r == MessageBoxResult.Cancel) return false;

        if (r == MessageBoxResult.Yes)
        {
            try
            {
                _doc = TranslationStore.Save(path, TbxTranslation.Text, null,
                                             ProjectLayout.Backups(_store!.Root), DateTime.Now);
                _dirty = false;
                Status("已覆盖保存 " + DateTime.Now.ToString("HH:mm:ss"));
                TxtSaveState.Text = "已保存（覆盖了磁盘版本）";
                return true;
            }
            catch (Exception e2) { Warn("保存失败", e2.Message); return false; }
        }

        // 否：读回磁盘版本。用户的编辑是有意放弃的，别再问一次。
        _doc = TranslationStore.Load(path);
        _loading = true;
        TbxTranslation.Text = _doc.TextLf;
        _loading = false;
        _dirty = false;
        UpdateStat();
        Status("已载入磁盘上的版本");
        TxtSaveState.Text = "已载入磁盘版本";
        return true;
    }

    private void OnSaveAs(object sender, RoutedEventArgs e)
    {
        if (_store == null || _current == null) return;

        string suggest = _store.TranslationPath(_current);
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "另存译文",
            Filter = "Markdown (*.md)|*.md|所有文件 (*.*)|*.*",
            FileName = Path.GetFileName(suggest),
            InitialDirectory = Path.GetDirectoryName(suggest),
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            // 先当作"新文件"写出去，再把项目里的这条记录指过去
            _doc = TranslationStore.Save(dlg.FileName, TbxTranslation.Text, null,
                                         ProjectLayout.Backups(_store.Root), DateTime.Now);
            _store.SetTranslationPath(_current, dlg.FileName);
            _dirty = false;

            TxtTrPath.Text = RelativeForDisplay(dlg.FileName);
            RefreshPanes();   // 有栏正看着这份译文的话，路径变了要重读
            Status("已另存为 " + dlg.FileName);
            TxtSaveState.Text = "已保存（另存）";
        }
        catch (Exception ex)
        {
            Warn("另存失败", ex.Message);
        }
    }

    // ================================================================ 状态显示

    private void UpdateStat()
    {
        string t = TbxTranslation.Text;
        int chars = TextStats.CountChars(t);
        int paras = TextStats.CountParagraphs(t);

        string s = $"{chars:N0} 字 · {paras} 段";

        int sel = TbxTranslation.SelectionLength;
        if (sel > 0)
            s += $" · 选中 {TextStats.CountChars(TbxTranslation.SelectedText):N0} 字";

        TxtStat.Text = s;     // 状态栏
        TxtTrStat.Text = s;   // 译文栏自己脚下这个更顺手
    }

    /// <summary>选中一段就刷一次：译的时候最常问的是"这一句多少字"。</summary>
    private void OnTranslationSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        UpdateStat();
    }

    // ================================================================ 译文预览

    private void OnTogglePreview(object sender, RoutedEventArgs e)
    {
        _previewing = !_previewing;

        if (_previewing)
        {
            RefreshPreview();
            BtnPreview.Content = "回到编辑";
            TbxTranslation.Visibility = Visibility.Collapsed;
            PreviewDoc.Visibility = Visibility.Visible;
            return;
        }

        PreviewDoc.Visibility = Visibility.Collapsed;
        TbxTranslation.Visibility = Visibility.Visible;
        BtnPreview.Content = "预览";
    }

    /// <summary>把当前译文按 Markdown 画一遍。渲染失败就当没这回事，绝不影响编辑区。</summary>
    private void RefreshPreview()
    {
        try
        {
            PreviewDoc.Document = MdRender.Build(MdParser.Parse(TbxTranslation.Text), MakeStyleFor(15, 1.8));
        }
        catch (Exception)
        {
            // 预览只是看一眼，出问题也不该打断翻译
        }
    }

    private void Status(string msg) => TxtSaveState.Text = msg;

    private string RelativeForDisplay(string absolute)
    {
        if (_store == null) return absolute;
        string rel = Path.GetRelativePath(_store.Root, absolute);
        return rel.StartsWith("..") ? absolute : rel;
    }

    private void Warn(string title, string body) =>
        MessageBox.Show(this, body, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    // ================================================================ 快捷键

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.S: _autoSave.Stop(); SaveNow(quiet: false); e.Handled = true; return;
                case Key.N: NewProject(); e.Handled = true; return;
                case Key.O: OpenProjectDialog(); e.Handled = true; return;
            }
        }
        base.OnKeyDown(e);
    }

    // ================================================================ 关闭

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!ConfirmLeaveCurrent())
        {
            e.Cancel = true;
            return;
        }

        _settings.WindowWidth = ActualWidth;
        _settings.WindowHeight = ActualHeight;
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        if (_store != null) _settings.LastProjectRoot = _store.Root;
        SavePaneLayout();   // 栏位就和窗口尺寸一起记下来
        _settings.Save();

        base.OnClosing(e);
    }

    /// <summary>
    /// 离开当前稿子（切文件 / 关窗 / 换项目）前的统一关口：
    /// 有改动就先存；存不下（冲突被取消）就返回 false，别让操作继续。
    /// </summary>
    private bool ConfirmLeaveCurrent()
    {
        if (!_dirty) return true;
        return SaveNow(quiet: false);
    }

    // ================================================================ 空状态 / 说明

    private void SetEmptyState()
    {
        _store = null;
        _current = null;
        _doc = null;

        Title = "译台";
        TxtProject.Text = "还没有打开项目";
        TxtProjectPath.Text = "文件 → 新建项目 / 打开项目";
        CmbSources.IsEnabled = false;
        TbxTranslation.IsEnabled = false;
        MiSave.IsEnabled = false;
        MiSaveAs.IsEnabled = false;
        SetPanesEmpty("先从“文件 → 新建项目”开始：\n\n" +
                      "选一个文件夹当项目根，软件会在里面建 sources（原文）、translations（译文）、backups（备份）。\n" +
                      "然后“导入源文件”把要翻的文件复制进去，左边对照栏就显示原文，最右边写译文。");
    }

    private void SetPanesEmpty(string hint)
    {
        _ = hint;   // 提示语现在由对照栏自己按状态给出，见 RefreshPanes

        _loading = true;
        TbxTranslation.Text = "";
        _loading = false;

        TxtTrPath.Text = "";
        TxtStat.Text = "";
        TxtTrStat.Text = "";
        RefreshPanes();
        if (_previewing) RefreshPreview();
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void OnExplainLayout(object sender, RoutedEventArgs e)
    {
        string root = _store?.Root ?? "（还没打开项目）";
        MessageBox.Show(this,
            "项目就是一个普通文件夹，双击里面的 project.json 就能打开。\n\n" +
            root + "\n" +
            "  project.json    项目清单：有哪些源文件、译文在哪、上次打开的是哪一份\n" +
            "  sources\\        导入时复制进来的原文（项目自足，整包可以搬走）\n" +
            "  translations\\   译文，默认与原文同名；另存为改的是项目清单里的指向\n" +
            "  backups\\        每天第一次保存前的上一版\n" +
            "  .yitai\\         程序自己用，别管它\n\n" +
            "为什么用文件夹而不是一个文件：译文得是能单独打开的 md，\n" +
            "能被别的编辑器、git、排版流程直接用；塞进一个二进制里就谁也读不了。",
            "项目文件夹", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            "译台 / YiTai\n\n左原文、右译文的最小翻译工作台。\n" +
            "MIT 许可，源码可读可改。\n\n" +
            "快捷键：Ctrl+S 保存 · Ctrl+N 新建项目 · Ctrl+O 打开项目",
            "关于译台", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
