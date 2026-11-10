// ============================================================
// App.xaml.cs —— 启动流程
// ============================================================
//
// 第一次运行的体验决定"陌生人会不会继续用"，所以这里只有三条：
//   1. 有配置 → 直接开；
//   2. 没配置 → 让他选**一份原文文件**，程序自己反推目录结构；
//   3. 反推不出来 → 就按"这一份文件 + 同目录的译文"单文件模式跑。
//
// 不做多步向导（多一步就多一个放弃的理由）。
// ============================================================

using System;
using System.IO;
using System.Windows;
using PairDesk.Core;

namespace PairDesk.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ★ 必须先关掉 WPF 的默认退出策略。
        //   默认是 OnLastWindowClose：导入窗口一关（还没建主窗口），
        //   整个进程就退出了 —— 实测点"取消"后主窗口根本没出现。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        string cfgPath = DeskConfig.DefaultPath;

        // 便利项：自用时可以用环境变量指一个项目根，免去第一次要点选
        string? seed = Environment.GetEnvironmentVariable("PAIRDESK_PROJECT");
        var cfg = DeskConfig.LoadOrCreate(cfgPath, seed);

        // 把原文文件拖到 exe 上（或命令行带一个路径）：直接进导入预览
        string? preset = e.Args.Length > 0 && File.Exists(e.Args[0]) ? e.Args[0] : null;

        if (!cfg.IsUsable() || preset != null)
        {
            // 首次运行：不是"随便选个文件"，而是导入窗口 —— 它会先猜格式、
            // 再真跑一遍把结果摆给用户看，点头才落库（见 ImportWindow）。
            var dlg = new ImportWindow(preset);
            bool ok = dlg.ShowDialog() == true && dlg.Result != null;

            if (ok) ApplyImport(cfg, dlg.Result!);
            else if (!cfg.IsUsable()) { Shutdown(); return; }   // 没配置又没导入：没法开工

            if (ok)
            {
                try { cfg.Save(cfgPath); } catch { /* 存不下也能用这一轮 */ }
            }
        }

        var win = new MainWindow(cfg);
        MainWindow = win;
        win.Show();

        // 主窗口起来了，恢复"关掉主窗口就退出"的正常行为
        ShutdownMode = ShutdownMode.OnMainWindowClose;
    }

    /// <summary>把导入窗口的结论搬进配置（目录结构 + 解析口径）。</summary>
    public static void ApplyImport(DeskConfig cfg, DeskConfig derived)
    {
        cfg.ProjectRoot = derived.ProjectRoot;
        cfg.SourcePattern = derived.SourcePattern;
        cfg.TranslationPattern = derived.TranslationPattern;
        cfg.BackupPattern = derived.BackupPattern;
        cfg.StatePattern = derived.StatePattern;
        cfg.ChapterCount = Math.Max(1, derived.ChapterCount);
        cfg.LastChapter = 1;

        cfg.Notes = derived.Notes;
        cfg.Split = derived.Split;
        cfg.CustomNotePattern = derived.CustomNotePattern;
    }
}
