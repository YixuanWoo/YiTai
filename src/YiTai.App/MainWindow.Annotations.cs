// ============================================================
// MainWindow.Annotations.cs —— 译文批注：接线、落盘、交互
// ============================================================
//
// 功能边界（本单只做这些）：
//   · 在「译文」的渲染视图（预览）里选中文字 → 右键「为选中文字加批注…」；
//   · 批注处正文可见（橙色虚线下划线 + 极浅底），鼠标悬停直接显示批注正文；
//   · 「批注」按钮打开本页批注列表，可定位 / 改 / 删；
//   · 存 .yitai\annotations.json（新增文件，不改任何既有结构），关掉重开还在。
//
// 为什么挂在「预览」上：
//   编辑区是 TextBox，插不进带 ToolTip 的行内 Span；预览/对照栏是 FlowDocument，
//   批注必须"包在文字里"才能跟着字号、切栏、滚动一起走，也才悬停得到。
//
// 对照栏里如果放的正好是本项目的译文，同样能看到并加批注（同一份 annotations.json）：
//   「切栏 / 改字号」这类操作在对照栏上才有，批注不跟着丢是这单的硬要求。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using YiTai.Core;

namespace YiTai.App;

public partial class MainWindow
{
    /// <summary>当前项目的批注表（一次读进内存，改动立刻原子落盘）。</summary>
    private AnnotationState _annState = new();

    /// <summary>"本页批注"窗口（模型窗口，跟着主窗口）。</summary>
    private AnnotationPanelWindow? _annPanel;

    private void InitAnnotationUi()
    {
        // 预览区默认没有右键菜单；给它一个空的，右键时再按选区/命中的批注现填。
        PreviewDoc.ContextMenu = new ContextMenu();
        PreviewDoc.ContextMenuOpening += OnPreviewContextMenuOpening;
    }

    /// <summary>换项目时重新读批注（旧项目的不算数）。</summary>
    private void LoadAnnotations()
    {
        _annState = _store == null ? new AnnotationState() : AnnotationStore.Load(_store.Root);
    }

    // ---------------------------------------------------------------- 键与回贴

    /// <summary>当前这篇译文在批注表里的键（与 progress.json 同一套 rel:/abs:）。</summary>
    private string CurrentAnnotationKey()
    {
        if (_store == null || _current == null) return "";
        return ProgressStore.KeyFor(_store.Root, _store.TranslationAbsPath(_current));
    }

    /// <summary>
    /// 取出这份文件上的批注，并逐条贴回当前正文：
    /// 偏移处就是 quote → 原位；附近找到 → 回贴（顺手把新偏移写回去）；
    /// 找不到 → 标记未定位（Start = -1），正文里不画，面板里如实说明。
    /// </summary>
    private List<AnnotationView> ViewsFor(string fileKey, string text, out bool rebased)
    {
        rebased = false;
        var views = new List<AnnotationView>();
        if (fileKey.Length == 0) return views;

        foreach (var item in AnnotationStore.ItemsFor(_annState, fileKey))
        {
            var (s, e) = AnnotationStore.Anchor(item, text);

            if (s >= 0 && (s != item.Start || e != item.End))
            {
                item.Start = s;
                item.End = e;
                rebased = true;
            }

            views.Add(new AnnotationView { Item = item, Start = s, End = e });
        }

        return views;
    }

    private List<AnnotationView> CurrentAnnotationViews()
    {
        if (_current == null || _store == null) return new List<AnnotationView>();
        return ViewsFor(CurrentAnnotationKey(), AnnotationStore.Normalize(TbxTranslation.Text), out _);
    }

    private void SaveAnnotations()
    {
        if (_store == null) return;

        try
        {
            AnnotationStore.Save(_store.Root, _annState);
        }
        catch (Exception ex)
        {
            Diag.Error("annotation-save", ex);
            Status("批注没存上：" + ex.Message);
        }
    }

    // ---------------------------------------------------------------- 预览：画批注

    /// <summary>预览文档画好之后调它：把批注包进文字里（没有批注就原样不动）。</summary>
    private void ApplyPreviewAnnotations(FlowDocument doc)
    {
        if (_current == null || _store == null)
        {
            UpdateAnnotationButton(0);
            return;
        }

        var views = ViewsFor(CurrentAnnotationKey(), AnnotationStore.Normalize(TbxTranslation.Text), out bool rebased);
        if (rebased) SaveAnnotations();

        AnnotationRender.Apply(doc, views);
        UpdateAnnotationButton(views.Count);
    }

    private void UpdateAnnotationButton(int count)
    {
        if (BtnAnnotate == null) return;
        BtnAnnotate.Content = count > 0 ? "批注 " + count : "批注";
    }

    /// <summary>预览里的选区 → 文档偏移（[Start,End)）。选到表格/空白处返回 null。</summary>
    private (int Start, int End)? SelectedRangeInPreview()
    {
        if (!_previewing || PreviewDoc.Document == null) return null;

        var sel = PreviewDoc.Selection;
        if (sel == null || sel.IsEmpty) return null;

        int? a = MdRender.OffsetAtPointer(PreviewDoc.Document, sel.Start);
        int? b = MdRender.OffsetAtPointer(PreviewDoc.Document, sel.End);
        if (a == null || b == null) return null;

        int s = Math.Min(a.Value, b.Value);
        int e = Math.Max(a.Value, b.Value);
        return e > s ? (s, e) : null;
    }

    private void OnPreviewContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = PreviewDoc.ContextMenu;
        if (menu == null) return;

        menu.Items.Clear();
        menu.PlacementTarget = PreviewDoc;

        if (!_previewing || _current == null) return;

        // 保留默认的"复制"：批注是加法，不抢原来就有的能力
        menu.Items.Add(new MenuItem
        {
            Header = "复制",
            Command = ApplicationCommands.Copy,
            CommandTarget = PreviewDoc,
        });

        menu.Items.Add(new Separator());

        var range = SelectedRangeInPreview();

        var add = new MenuItem
        {
            Header = range == null ? "为选中文字加批注…（先在正文里选中文字）" : "为选中文字加批注…",
            IsEnabled = range != null,
        };
        if (range != null) add.Click += (_, _) => AddAnnotationForPreview(range.Value);
        menu.Items.Add(add);

        string id = AnnotationRender.AnnotationIdAt((sender as FlowDocumentScrollViewer)?.Selection?.Start);
        string idAtSource = AnnotationIdOfElement(e.OriginalSource);

        var edit = new MenuItem { Header = "改这条批注…", IsEnabled = id.Length > 0 };
        if (id.Length > 0) edit.Click += (_, _) => EditAnnotation(id);
        menu.Items.Add(edit);

        var del = new MenuItem { Header = "删这条批注", IsEnabled = id.Length > 0 };
        if (id.Length > 0) del.Click += (_, _) => DeleteAnnotation(id);
        menu.Items.Add(del);

        menu.Items.Add(new Separator());

        var list = new MenuItem { Header = "本页批注列表…" };
        list.Click += (_, _) => ShowAnnotationPanel();
        menu.Items.Add(list);

        // 命中的是批注正文时，Selection 可能落在别处；以鼠标下那截文字为准。
        // （FlowDocumentScrollViewer 拿不到鼠标下的 TextPointer，只能靠 OriginalSource 的祖先。）
        if (id.Length == 0 && idAtSource.Length > 0)
        {
            edit.IsEnabled = true;
            del.IsEnabled = true;
            edit.Click += (_, _) => EditAnnotation(idAtSource);
            del.Click += (_, _) => DeleteAnnotation(idAtSource);
        }
    }

    /// <summary>从右键点到的元素往上找"这是哪条批注"。</summary>
    private static string AnnotationIdOfElement(object? source)
    {
        DependencyObject? cur = source as DependencyObject;

        while (cur != null)
        {
            if (cur is Span span && span.Tag is string tag && tag.StartsWith("ann:", StringComparison.Ordinal))
                return tag.Substring(4);

            cur = (cur as FrameworkContentElement)?.Parent;
        }

        return "";
    }

    private void AddAnnotationForPreview((int Start, int End) range)
    {
        if (_current == null || _store == null) return;

        string text = AnnotationStore.Normalize(TbxTranslation.Text);
        int s = Math.Max(0, Math.Min(text.Length, range.Start));
        int e = Math.Max(0, Math.Min(text.Length, range.End));
        if (e <= s) { Status("先选中要批注的文字"); return; }

        string quote = text.Substring(s, e - s);
        string? body = AnnotationEditorWindow.Ask(this, "加批注", "", quote);
        if (body == null) return;

        var item = AnnotationStore.Create(CurrentAnnotationKey(), s, e, quote, body, DateTime.Now);
        AnnotationStore.Add(_annState, item);
        SaveAnnotations();

        RefreshPreview();
        ScrollPreviewTo(item.Start);

        Status($"已加批注（本页 {AnnotationStore.ItemsFor(_annState, CurrentAnnotationKey()).Count} 条）");
        RefreshPanelIfOpen();
    }

    private void ScrollPreviewTo(int offset)
    {
        var doc = PreviewDoc.Document;
        if (doc == null || offset < 0) return;

        var pos = MdRender.PointerAtOffset(doc, offset);
        if (pos?.Paragraph != null) pos.Paragraph.BringIntoView();
    }

    // ---------------------------------------------------------------- 改 / 删 / 面板

    private void EditAnnotation(string id)
    {
        var item = AnnotationStore.Find(_annState, id);
        if (item == null) return;

        string? body = AnnotationEditorWindow.Ask(this, "改批注", item.Body, item.Quote);
        if (body == null) return;

        item.Body = body;
        item.Modified = DateTime.Now.ToString("o");

        SaveAnnotations();
        RefreshPreview();
        RefreshPaneAnnotations();
        RefreshPanelIfOpen();
        Status("批注已改（译文一个字没动）");
    }

    private void DeleteAnnotation(string id)
    {
        if (!AnnotationStore.Remove(_annState, id)) return;

        SaveAnnotations();
        RefreshPreview();
        RefreshPaneAnnotations();
        RefreshPanelIfOpen();
        Status("批注已删（只动 annotations.json，译文一个字没动）");
    }

    private void OnShowAnnotationPanel(object sender, RoutedEventArgs e) => ShowAnnotationPanel();

    private void ShowAnnotationPanel()
    {
        if (_current == null || _store == null) { Status("先打开一篇译文"); return; }

        if (_annPanel == null)
        {
            _annPanel = new AnnotationPanelWindow(this, CurrentAnnotationViews,
                v => EditAnnotation(v.Item.Id),
                v => DeleteAnnotation(v.Item.Id),
                LocateAnnotation);
            _annPanel.Closed += (_, _) => _annPanel = null;
            _annPanel.Show();
            return;
        }

        _annPanel.Reload();
        if (!_annPanel.IsVisible) _annPanel.Show();
        _annPanel.Activate();
    }

    private void RefreshPanelIfOpen() => _annPanel?.Reload();

    private void LocateAnnotation(AnnotationView view)
    {
        if (view == null || !view.Anchored) return;

        if (!_previewing) OnTogglePreview(this, new RoutedEventArgs());
        ScrollPreviewTo(view.Start);
        Status("已定位到批注");
    }

    // ---------------------------------------------------------------- 对照栏（译文栏同样能批注）

    /// <summary>这份文件在不在本项目的 translations\ 里 —— 只有译文才给批注。</summary>
    private bool IsTranslationPath(string absolute)
    {
        if (_store == null || absolute.Length == 0) return false;

        string dir = Path.GetFullPath(ProjectLayout.Translations(_store.Root));
        string full = Path.GetFullPath(absolute);

        return full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>一栏文档画好之后调它：如果这栏放的是译文，就把它的批注也画上。</summary>
    private void ApplyPaneAnnotations(PaneView pane, string text)
    {
        pane.AnnKey = "";
        pane.AnnViews = new List<AnnotationView>();

        if (_store == null || pane.Body?.Document == null || pane.Spec.Path.Length == 0) return;

        string abs;
        try { abs = Path.GetFullPath(ProjectLayout.ToAbsolute(_store.Root, pane.Spec.Path)); }
        catch (Exception) { return; }

        if (!IsTranslationPath(abs)) return;   // 资料栏不掺和批注

        pane.AnnKey = ProgressStore.KeyFor(_store.Root, abs);

        var views = ViewsFor(pane.AnnKey, AnnotationStore.Normalize(text), out bool rebased);
        if (rebased) SaveAnnotations();

        pane.AnnViews = views;
        AnnotationRender.Apply(pane.Body.Document, views);
    }

    /// <summary>批注改/删之后，重画所有"放译文"的对照栏（预览由 RefreshPreview 单独刷）。</summary>
    private void RefreshPaneAnnotations()
    {
        foreach (var p in _panes)
            if (p.AnnKey.Length > 0 && p.Body != null)
                RefreshOne(p);
    }

    /// <summary>鼠标这一下落在哪条批注上（对照栏用 GetPositionFromPoint，比预览准）。</summary>
    private string AnnotationIdAtPane(PaneView pane, Point at)
    {
        if (pane.AnnKey.Length == 0 || pane.Body == null) return "";

        var pos = pane.Body.GetPositionFromPoint(at, snapToText: true);
        return AnnotationRender.AnnotationIdAt(pos);
    }

    /// <summary>为对照栏里选中的文字加一条批注（写的还是同一份 annotations.json）。</summary>
    private void AddAnnotationForPane(PaneView pane)
    {
        if (pane.AnnKey.Length == 0) return;

        var sel = pane.Body.Selection;
        if (sel == null || sel.IsEmpty) { Status("先选中要批注的文字"); return; }

        int? a = MdRender.OffsetAtPointer(pane.Body.Document, sel.Start);
        int? b = MdRender.OffsetAtPointer(pane.Body.Document, sel.End);
        if (a == null || b == null) return;

        string text = AnnotationStore.Normalize(pane.PaneText);
        int s = Math.Max(0, Math.Min(text.Length, Math.Min(a.Value, b.Value)));
        int e = Math.Max(0, Math.Min(text.Length, Math.Max(a.Value, b.Value)));
        if (e <= s) { Status("先选中要批注的文字"); return; }

        string quote = text.Substring(s, e - s);
        string? body = AnnotationEditorWindow.Ask(this, "加批注", "", quote);
        if (body == null) return;

        var item = AnnotationStore.Create(pane.AnnKey, s, e, quote, body, DateTime.Now);
        AnnotationStore.Add(_annState, item);
        SaveAnnotations();

        RefreshOne(pane);
        Status($"已加批注（这份译文现在 {AnnotationStore.ItemsFor(_annState, pane.AnnKey).Count} 条）");
        RefreshPreview();       // 预览要是也开着同一篇，一并刷新
        RefreshPanelIfOpen();
    }
}
