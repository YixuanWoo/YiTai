// ============================================================
// App.xaml.cs —— 启动流程（尽量短）
// ============================================================
//
// 只做一件事：开主窗口。
//   · 上次的项目、窗口尺寸 → MainWindow 自己从 AppSettings 读；
//   · 项目没打开时的提示 → MainWindow 的空状态；
//   · 命令行带一个 project.json 路径 → 直接开它（拖拽也走这条）。
//
// 启动期不再有"向导""导入预览"这种多窗口流程：一个窗口，菜单办事。
// ============================================================

using System.IO;
using System.Windows;
using YiTai.Core;

namespace YiTai.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Diag.Start();   // 诊断日志：%LOCALAPPDATA%\YiTai\diag.log，YITAI_DIAG=0 可关

        var win = new MainWindow();
        MainWindow = win;
        win.Show();

        // 把项目文件拖到 exe 上：直接打开那个项目
        if (e.Args.Length > 0 && File.Exists(e.Args[0]))
        {
            try
            {
                var store = ProjectStore.Open(e.Args[0]);
                win.OpenProjectFromOutside(store.Root);
            }
            catch
            {
                // 打不开就算了，主窗口已经在，用户可以自己选
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Diag.Stop();     // 退出前把最后半个窗口的数冲下去
        base.OnExit(e);
    }
}
