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
using System.Windows.Media;
using System.Windows.Threading;
using YiTai.Core;

namespace YiTai.App;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DispatcherTimer _autoSave = new();

    private ProjectStore? _store;
    private TranslationEntry? _current;   // 当前打开的那篇译文（和源文件没有任何关系）
    private TranslationDoc? _doc;

    private bool _dirty;        // 有未保存的改动
    private bool _loading;      // 正在程序性填内容，别把 TextChanged 当用户输入
    private bool _previewing;   // 译文栏是否切到了"渲染预览"（编辑区仍在，只是藏起来）

    /// <summary>当前这篇译文的文件读不了（缺失/没权限/被独占）。此时编辑区锁着，等用户选一条路。</summary>
    private bool _missingTrans;

    /// <summary>状态栏那句"N 个文件不在了"点开时列的东西：资料栏 + 当前这篇译文里读不到的路径。</summary>
    private readonly List<string> _missingPaths = new();

    public MainWindow()
    {
        InitializeComponent();

        InitPanes();   // 先摆一排空栏；打开上次项目时再按项目记忆替换

        InitMarker();  // Ctrl+Shift+↑/↓ 挪"译到哪了"的绿线

        InitLineNumbers();  // 译文栏左侧的行号

        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;

        _autoSave.Tick += (_, _) => { _autoSave.Stop(); SaveNow(quiet: true); };

        // 上次的项目：直接接着干；没有就留空，等人从菜单里新建/打开
        if (_settings.LastProjectRoot.Length > 0 && File.Exists(ProjectLayout.ProjectFile(_settings.LastProjectRoot)))
        {
            try
            {
                OpenProject(_settings.LastProjectRoot, _settings.LastTranslationId);
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

    /// <summary>装载一个项目：刷新标题、译文下拉、默认选中上次那一篇。</summary>
    private void OpenProject(string rootOrProjectFile, string? wantTranslationId)
    {
        SavePaneLayout();   // 先把上一个项目的栏位记下来，再换
        _current = null;    // 旧项目的译文不再算数，免得新项目还没装载时栏里显示上一本的字

        _store = ProjectStore.Open(rootOrProjectFile);
        _settings.LastProjectRoot = _store.Root;

        LoadProgress();   // 这个项目"译到哪了"

        // 译文清单以磁盘为准：translations\ 里的 md/txt 一律出现在下拉里，
        // 不要求"先有源文件"，也不跟源文件一一对应。
        _store.SyncTranslations();
        _store.Save();

        Title = $"译台 — {_store.Meta.Name}";
        TxtProject.Text = _store.Meta.Name;
        TxtProjectPath.Text = _store.Root;

        _loading = true;
        CmbSources.ItemsSource = null;
        CmbSources.ItemsSource = _store.Meta.Translations;
        CmbSources.DisplayMemberPath = "FileName";
        _loading = false;

        LoadPanesForProject();   // 这个项目上次摆了几栏，就摆几栏

        bool hasTrans = _store.Meta.Translations.Count > 0;
        CmbSources.IsEnabled = hasTrans;
        MiSave.IsEnabled = hasTrans;
        MiSaveAs.IsEnabled = hasTrans;

        if (!hasTrans)
        {
            ShowEmptyTranslation("这个项目还没有译文",
                                 "不用先有资料 —— 起个名字就能开始写。译文统一落在项目的 translations 文件夹里。");
            return;
        }

        string? id = wantTranslationId ?? _store.Meta.LastTranslationId;
        TranslationEntry entry = _store.Meta.Translations.Find(s => s.Id == id) ?? _store.Meta.Translations[0];

        _loading = true;
        CmbSources.SelectedItem = entry;
        _loading = false;
        LoadTranslation(entry);
    }

    // ================================================================ 译文：新建 / 收进来 / 切换
    //
    // 这里只有"译文"，没有"源文件"这一说。要参考什么东西，就开一栏资料（只读）。
    // 一条文件是不是"译文"，不靠它放在哪、也不靠它跟谁同名 —— 靠人在这里点了"新建"或"收进来"。

    /// <summary>新建一篇译文。空项目里也能按 —— 不需要先有资料文件。</summary>
    private void OnNewTranslation(object sender, RoutedEventArgs e)
    {
        if (_store == null) { Warn("还没有项目", "先从「文件 → 新建项目 / 打开项目」开始。"); return; }
        if (!SaveNow(quiet: false)) return;

        string? name = AskName("新建译文", "给这篇译文起个名字（不用写 .md）：", "未命名");
        if (name == null) return;

        try
        {
            var t = _store.NewTranslation(name);
            BindTranslations(t);
            LoadTranslation(t);
            Status("已新建 " + t.FileName);
        }
        catch (Exception ex) { Warn("新建失败", ex.Message); }
    }

    /// <summary>把一个已有的 md/txt 收进项目当译文（复制进 translations\，外面那份不动）。</summary>
    private void OnImportTranslation(object sender, RoutedEventArgs e)
    {
        if (_store == null) { Warn("还没有项目", "先新建或打开一个项目。"); return; }
        if (!SaveNow(quiet: false)) return;

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选一份译文收进项目（会复制进 translations\\）",
            Filter = "文本 (*.md;*.txt;*.markdown)|*.md;*.txt;*.markdown|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var t = _store.ImportTranslation(dlg.FileName);
            BindTranslations(t);
            LoadTranslation(t);
            Status("已收进 " + t.FileName);
        }
        catch (Exception ex) { Warn("收不进来", ex.Message); }
    }

    /// <summary>
    /// 打开任何文件当一栏资料：只读、不复制、不进清单、不动译文栏。
    /// 这里不判断"这是资料还是译文" —— 软件没资格判，你也可能放进来一份跟这本书无关的东西。
    /// </summary>
    private void OnOpenRefFile(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "打开一份资料（只读，不复制、不改项目）",
            Filter = "所有文件 (*.*)|*.*|文本 (*.md;*.txt;*.markdown)|*.md;*.txt;*.markdown|PDF (*.pdf)|*.pdf",
        };
        if (dlg.ShowDialog(this) != true) return;

        AddRefPanes(new[] { dlg.FileName });
    }

    /// <summary>下拉换成最新清单，并选中指定那一篇。</summary>
    private void BindTranslations(TranslationEntry? select)
    {
        bool has = _store!.Meta.Translations.Count > 0;

        _loading = true;
        CmbSources.ItemsSource = null;
        CmbSources.ItemsSource = _store.Meta.Translations;
        CmbSources.DisplayMemberPath = "FileName";
        CmbSources.SelectedItem = select;
        _loading = false;

        CmbSources.IsEnabled = has;
        MiSave.IsEnabled = has;
        MiSaveAs.IsEnabled = has;
        EmptyTrans.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 要一个名字。自建一个小窗（一个输入框 + 确定/取消），
    /// 不用系统那种 Win32 老输入框 —— 那个跟这个软件的皮完全不搭。
    /// </summary>
    private string? AskName(string title, string prompt, string initial)
    {
        var win = new Window
        {
            Title = title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            Background = (Brush)FindResource("BgCard"),
            Foreground = (Brush)FindResource("TextMain"),
            ShowInTaskbar = false,
        };

        var root = new StackPanel { Margin = new Thickness(18, 16, 18, 14) };
        root.Children.Add(new TextBlock
        {
            Text = prompt,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
        });

        var box = new TextBox
        {
            Text = initial,
            Margin = new Thickness(0, 10, 0, 0),
            FontSize = 13,
            Padding = new Thickness(6, 5, 6, 5),
        };
        root.Children.Add(box);

        string? result = null;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        var ok = new Button
        {
            Content = "确定",
            IsDefault = true,
            MinWidth = 76,
            Margin = new Thickness(0, 0, 8, 0),
            Style = (Style)FindResource("BtnAccent"),
        };
        var cancel = new Button
        {
            Content = "取消",
            IsCancel = true,
            MinWidth = 76,
            Style = (Style)FindResource("Btn"),
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        ok.Click += (_, _) => { result = box.Text; win.Close(); };
        win.Content = root;
        win.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };

        win.ShowDialog();
        return result;
    }

    // ================================================================ 切换译文

    private void OnSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (CmbSources.SelectedItem is not TranslationEntry entry) return;
        if (_current != null && entry.Id == _current.Id) return;

        // 切走之前先把上一篇存掉（不然切一下就把刚写的丢了）
        if (!SaveNow(quiet: false)) return;

        LoadTranslation(entry);
    }

    /// <summary>把一份源文件连同它的译文装进界面。</summary>
    /// <summary>把一篇译文装进界面。它和源文件没有关系，只认 translations\ 里的那个文件。</summary>
    private void LoadTranslation(TranslationEntry entry)
    {
        _current = entry;
        _settings.LastTranslationId = entry.Id;
        _store!.Meta.LastTranslationId = entry.Id;
        _store.Save();

        string trPath = _store.TranslationAbsPath(entry);
        _doc = TranslationStore.Load(trPath);

        _loading = true;
        TbxTranslation.Text = _doc.TextLf;
        _loading = false;

        _dirty = false;
        _autoSave.Stop();

        // ★ 读不了就先别让人写：编辑区只读 + 上方黄条说清楚，
        //   否则空白编辑区会让人以为"这章还没开始"，一入字保存就在原路径长出一份新文件，
        //   把"其实是一份旧稿被移走了"这件事彻底盖掉。
        _missingTrans = _doc.State != TextReadState.Ok;
        TbxTranslation.IsEnabled = !_missingTrans;
        LineNumPanel.Visibility = Visibility.Visible;
        EmptyTrans.Visibility = Visibility.Collapsed;
        TxtTrPath.Text = RelativeForDisplay(trPath);
        ShowMissingTrans(trPath);

        RefreshPanes();   // 缩放之类的栏内状态刷一下（栏里放什么是不跟着译文变的）
        if (_previewing) RefreshPreview();
        UpdateStat();
        Status(_missingTrans ? "这篇译文的文件读不了，先别写" : "已载入");
    }

    // ================================================================ 译文文件不在了

    /// <summary>
    /// 编辑区上方那条提示。只在文件读不了时亮，三颗按钮给人三条路：
    /// 指一份现有的稿子过来 / 就按原文件名建空稿从头写 / 什么都不动先只读。
    /// 用提示条而不是弹窗：这不是"出事了"，是"这里缺东西，你决定怎么办"，
    /// 不该每次开项目都被一个对话框拦住。
    /// </summary>
    private void ShowMissingTrans(string trPath)
    {
        if (!_missingTrans)
        {
            MissingTransBar.Visibility = Visibility.Collapsed;
            RefreshMissingIndicator();
            return;
        }

        string reason = _doc!.Problem.Length > 0 ? _doc.Problem : "文件不在了。";

        TxtMissingTrans.Text =
            "这篇译文的文件读不了：" + reason + "\n" +
            RelativeForDisplay(trPath) + "\n" +
            "选一条：重新指定（把现有的稿子复制到这里）· 在这里新建空稿 · 先只读。在选之前编辑区是锁着的。";

        MissingTransBar.Visibility = Visibility.Visible;
        RefreshMissingIndicator();
    }

    /// <summary>"重新指定"：把用户挑的那份稿子**复制**到它原来的位置，再重新载入。</summary>
    private void OnRelocateTranslation(object sender, RoutedEventArgs e)
    {
        if (_store == null || _current == null) return;

        string trPath = _store.TranslationAbsPath(_current);

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "这份译文现在在哪？",
            Filter = "Markdown / 文本 (*.md;*.markdown;*.txt)|*.md;*.markdown;*.txt|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            // 目标位置此刻确实没有文件（我们正是因为它不在才提示的），所以覆盖风险为零；
            // 万一中途有人替它建了一份，也宁可报错不覆盖。
            File.Copy(dlg.FileName, trPath, overwrite: false);
        }
        catch (Exception ex)
        {
            Warn("没能放回去", ex.Message);
            return;
        }

        LoadTranslation(_current);
        Status("已把选中的稿子放回原位置：" + RelativeForDisplay(trPath));
    }

    /// <summary>"在这里新建空稿"：按原文件名建一份空文件，从头写。旧内容就算有也找不回来了，所以要说清。</summary>
    private void OnCreateMissingTranslation(object sender, RoutedEventArgs e)
    {
        if (_store == null || _current == null) return;

        string trPath = _store.TranslationAbsPath(_current);

        var answer = MessageBox.Show(this,
            "就在原位置建一份空稿，从零开始写？\n\n" + RelativeForDisplay(trPath) +
            "\n\n（如果那份旧稿其实还在别处，先按「取消」，改用「重新指定…」把它找回来。）",
            "新建空稿", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(trPath)!);
            TextFile.WriteAtomic(trPath, "", "\n", withBom: false);
        }
        catch (Exception ex)
        {
            Warn("没建成", ex.Message);
            return;
        }

        LoadTranslation(_current);
        Status("已建空稿，可以开始写了");
    }

    /// <summary>"先只读"：收起提示条，编辑区继续锁着 —— 只是别拿黄条一直烦人。</summary>
    private void OnDismissMissingTranslation(object sender, RoutedEventArgs e)
    {
        MissingTransBar.Visibility = Visibility.Collapsed;
        Status("只读：这篇译文的文件读不了，改动不会保存");
    }

    /// <summary>
    /// 状态栏那句"N 个文件不在了"。资料栏按路径查、译文按当前这一篇查；
    /// 点一下能看到具体是哪几个。
    /// </summary>
    private void RefreshMissingIndicator()
    {
        _missingPaths.Clear();

        foreach (var pane in _panes)
            if (pane.Spec.Path.Length > 0 && !File.Exists(pane.Spec.Path))
                _missingPaths.Add(pane.Spec.Path);

        if (_missingTrans && _store != null && _current != null)
            _missingPaths.Add(_store.TranslationAbsPath(_current));

        if (_missingPaths.Count == 0)
        {
            TxtMissingCount.Visibility = Visibility.Collapsed;
            return;
        }

        TxtMissingCount.Text = $"⚠ {_missingPaths.Count} 个文件不在了";
        TxtMissingCount.ToolTip = string.Join("\n", _missingPaths);
        TxtMissingCount.Visibility = Visibility.Visible;
    }

    private void OnShowMissingList(object sender, RoutedEventArgs e)
    {
        if (_missingPaths.Count == 0) return;

        MessageBox.Show(this,
            "这些文件在记录里的位置已经找不到了：\n\n" + string.Join("\n", _missingPaths) +
            "\n\n资料栏点栏头的「重新指定…」；译文点编辑区上方的「重新指定…」。",
            "文件不在了", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>
    /// 一篇译文都没有时，编辑区不是一块死灰：直接告诉人能做什么，并给一个能按的按钮。
    /// </summary>
    private void ShowEmptyTranslation(string title, string hint)
    {
        _current = null;
        _doc = null;

        _loading = true;
        TbxTranslation.Text = "";
        _loading = false;

        _dirty = false;
        _autoSave.Stop();

        _missingTrans = false;                 // 没有当前译文，就没有"译文缺失"这回事
        MissingTransBar.Visibility = Visibility.Collapsed;

        TbxTranslation.IsEnabled = false;
        LineNumPanel.Visibility = Visibility.Collapsed;
        TxtTrPath.Text = "";
        TxtEmptyTitle.Text = title;
        TxtEmptyHint.Text = hint;
        EmptyTrans.Visibility = Visibility.Visible;

        UpdateStat();
        Status("");
        RefreshMissingIndicator();
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

        string path = _store.TranslationAbsPath(_current);

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

    /// <summary>
    /// 给当前译文改名。改的就是磁盘文件名 —— 清单和文件永远只有一个名字，
    /// 不会出现"清单里叫 A、文件夹里叫 B"这种对不上的状态。译文只能待在 translations\ 里。
    /// </summary>
    private void OnSaveAs(object sender, RoutedEventArgs e)
    {
        if (_store == null || _current == null) return;

        string stem = Path.GetFileNameWithoutExtension(_current.FileName);
        string? name = AskName("给这篇译文改名", "新名字（不用写 .md）：", stem);
        if (name == null) return;
        if (string.Equals(name.Trim(), stem, StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            if (!SaveNow(quiet: false)) return;

            var renamed = _store.RenameTranslation(_current, name);
            BindTranslations(renamed);
            LoadTranslation(renamed);
            Status("已改名为 " + renamed.FileName);
            TxtSaveState.Text = "已保存";
        }
        catch (Exception ex)
        {
            Warn("改名失败", ex.Message);
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
            LineNumPanel.Visibility = Visibility.Collapsed;
            PreviewDoc.Visibility = Visibility.Visible;
            return;
        }

        PreviewDoc.Visibility = Visibility.Collapsed;
        TbxTranslation.Visibility = Visibility.Visible;
        LineNumPanel.Visibility = Visibility.Visible;
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
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.N)
        {
            OnNewTranslation(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

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
        _progress = new ProgressState();   // 没项目就没有进度可言

        Title = "译台";
        TxtProject.Text = "还没有打开项目";
        TxtProjectPath.Text = "文件 → 新建项目 / 打开项目";

        _loading = true;
        CmbSources.ItemsSource = null;
        _loading = false;

        CmbSources.IsEnabled = false;
        MiSave.IsEnabled = false;
        MiSaveAs.IsEnabled = false;

        ShowEmptyTranslation("还没有项目",
                             "先「文件 → 新建项目」，选一个文件夹当项目根。\n" +
                             "建好之后这一栏就能直接写第一篇译文 —— 不用先找资料，也不用先建 .md。");

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
            "  project.json    项目清单：有哪些译文、各有几栏资料、上次打开的是哪一篇\n" +
            "  translations\\   ★ 所有译文都只落在这里，一篇一个文件\n" +
            "  sources\\        想归档的原稿/资料（可选，不归档也能翻译）\n" +
            "  backups\\        每天第一次保存前的上一版\n" +
            "  .yitai\\         程序自己用，别管它\n\n" +
            "为什么译文必须统一在一个文件夹：这样「第几章」只是文件名，\n" +
            "软件不用猜哪份是资料、哪份是译文 —— 你放进来当资料的一律只读。\n\n" +
            "为什么用文件夹而不是一个文件：译文得是能单独打开的 md，\n" +
            "能被别的编辑器、git、排版流程直接用；塞进一个二进制里就谁也读不了。",
            "项目文件夹", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            "译台 / YiTai\n\n左资料、右译文的最小翻译工作台。\n" +
            "MIT 许可，源码可读可改。\n\n" +
            "快捷键：Ctrl+S 保存 · Ctrl+N 新建项目 · Ctrl+O 打开项目",
            "关于译台", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
