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
using System.Windows;
using PairDesk.Core;

namespace PairDesk.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string cfgPath = DeskConfig.DefaultPath;

        // 便利项：自用时可以用环境变量指一个项目根，免去第一次要点选
        string? seed = Environment.GetEnvironmentVariable("PAIRDESK_PROJECT");
        var cfg = DeskConfig.LoadOrCreate(cfgPath, seed);

        if (!cfg.IsUsable())
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选一份原文文件（例如 ch03.md）",
                Filter = "Markdown / 文本 (*.md;*.txt)|*.md;*.txt|所有文件 (*.*)|*.*",
            };

            if (dlg.ShowDialog() != true)
            {
                Shutdown();
                return;
            }

            var derived = ProjectLocator.DeriveFromSourceFile(dlg.FileName);
            cfg.ProjectRoot = derived.ProjectRoot;
            cfg.SourcePattern = derived.SourcePattern;
            cfg.TranslationPattern = derived.TranslationPattern;
            cfg.BackupPattern = derived.BackupPattern;
            cfg.StatePattern = derived.StatePattern;
            cfg.ChapterCount = Math.Max(1, derived.ChapterCount);
            cfg.LastChapter = 1;

            try { cfg.Save(cfgPath); } catch { /* 存不下也能用这一轮 */ }
        }

        var win = new MainWindow(cfg);
        MainWindow = win;
        win.Show();
    }
}
