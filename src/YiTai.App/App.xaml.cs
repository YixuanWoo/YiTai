// ============================================================
// App.xaml.cs —— 启动流程 + 最后一道兜底（尽量短）
// ============================================================
//
// 干两件事：
//   1. 开主窗口；
//   2. 接住那些"没人管的异常"—— 界面线程漏掉的、后台任务漏掉的。
//
// 为什么要有第 2 件：
//   一次"资料文件被移走/占着/没权限"不该把整个窗口带走。Core 层已经把常见的
//   读写失败收成数据返回了（TextFile.Read 不抛），但没人敢保证世上所有异常
//   都被点名处理过 —— 所以留这一层：记日志、说一句人话、继续开着。
//
// 底线：这里绝不"吞掉错误假装没发生"。每次都会写进 %LOCALAPPDATA%\YiTai\diag.log
//       （见 Diag.Error），并在界面上让人看见"刚才出了个错"。
// ============================================================

using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using YiTai.Core;

namespace YiTai.App;

public partial class App : Application
{
    /// <summary>
    /// 同一条异常被反复触发时（比如每次重绘都撞上同一个坏文件），
    /// 别每次弹一个窗 —— 那样用户点不完。同一分钟内只提示一次。
    /// </summary>
    private DateTime _lastCry = DateTime.MinValue;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Diag.Start();   // 诊断日志：%LOCALAPPDATA%\YiTai\diag.log，YITAI_DIAG=0 可关

        InstallCrashGuards();

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

    // ================================================================ 兜底

    private void InstallCrashGuards()
    {
        // 界面线程（按钮事件、排版、绑定……）里漏出来的异常
        DispatcherUnhandledException += (_, args) =>
        {
            Diag.Error("界面线程", args.Exception);

            args.Handled = true;          // 不标记就会照默认行为退出
            TellUser(args.Exception);
        };

        // Task / 异步里没人 await 的异常（不会带崩程序，但也不能一声不响）
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Diag.Error("后台任务", args.Exception);
            args.SetObserved();
        };

        // 非界面线程里彻底漏掉的异常：留个记录，能否续命交给运行时
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Diag.Error("非界面线程", args.ExceptionObject as Exception);
        };
    }

    /// <summary>给人看的那一句话：说清"刚才出错了、东西没丢、日志在哪"，不去解释堆栈。</summary>
    private void TellUser(Exception ex)
    {
        DateTime now = DateTime.Now;
        if ((now - _lastCry).TotalSeconds < 60) return;   // 一分钟内只提示一次
        _lastCry = now;

        string tail = ex.Message.Length > 160 ? ex.Message.Substring(0, 160) + "…" : ex.Message;

        MessageBox.Show(
            "刚才有一步没做好，已经跳过去了，窗口还能接着用。\n\n" +
            "原因：" + tail + "\n\n" +
            "如果你的稿子还在，它就没丢 —— 保存是单独的流程，失败会有自己的提示。\n" +
            "详细记录：" + Diag.LogPath,
            "出了个错", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
