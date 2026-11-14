// ============================================================
// Diag.cs —— "用一会儿就变慢"要看的那几个数
// ============================================================
//
// 只记数字，不记正文。每 5 秒往 %LOCALAPPDATA%\YiTai\diag.log 追加一行。
// 打开方式照旧：双击 exe。要关掉就设环境变量 YITAI_DIAG=0。
//
// 为什么记这几项：
//   · layoutUpdate 每秒次数 —— WPF 静止时应当接近 0。若持续 60/s，
//     说明"刷新竖线"这件事反过来又触发了新布局，自己拖着自己跑。
//   · marker 每次耗时 —— 若随时间变大，是文档或元素在累积。
//   · click 平均与最大 —— 直接对应"点一下多久才动"。
//   · refresh 耗时与各栏字符数 —— 切章、换源的成本。
//   · 内存 / GC / 句柄 —— 判断是不是越用越大。
//
// 代价：几个 long 自增 + Stopwatch.GetTimestamp()，可以忽略。

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace YiTai.App;

public static class Diag
{
    /// <summary>默认开；环境变量 YITAI_DIAG=0 关掉。</summary>
    public static readonly bool On =
        Environment.GetEnvironmentVariable("YITAI_DIAG") != "0";

    /// <summary>日志文件在哪。异常提示里要报给用户，所以是公开的。</summary>
    public static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "YiTai", "diag.log");

    private static readonly object Gate = new();

    private static Timer? _timer;
    private static DateTime _started = DateTime.Now;
    private static DateTime _windowAt = DateTime.Now;

    // ---- 本窗口（5 秒）里的计数，Pump 时取走并清零 ----
    private static long _clicks, _clickTicks, _clickTicksMax;
    private static long _layouts, _layoutTicks, _layoutTicksMax;
    private static long _refreshes, _refreshTicks, _refreshTicksMax;
    private static long _saves, _saveTicks;
    private static long _places, _placeTicks, _placeTicksMax;
    private static long _undos;

    // ---- 最近一次刷新后的栏况（只有 UI 线程写）----
    private static string _panes = "";

    /// <summary>计时起点。Diag 关掉时返回 0，Add* 收到 0 就不计。</summary>
    public static long Tick() => On ? Stopwatch.GetTimestamp() : 0;

    private static long Delta(long since)
        => (since == 0) ? 0 : Stopwatch.GetTimestamp() - since;

    private static double Ms(long ticks)
        => ticks <= 0 ? 0 : ticks * 1000.0 / Stopwatch.Frequency;

    // ---------------------------------------------------------------- 打点

    public static void AddClick(long since)
    {
        if (!On) return;
        long d = Delta(since);
        _clicks++;
        _clickTicks += d;
        if (d > _clickTicksMax) _clickTicksMax = d;
    }

    /// <summary>一次 LayoutUpdated 里的竖线计算。这是最可能累积开销的地方。</summary>
    public static void AddLayout(long since)
    {
        if (!On) return;
        long d = Delta(since);
        _layouts++;
        _layoutTicks += d;
        if (d > _layoutTicksMax) _layoutTicksMax = d;
    }

    public static void AddRefresh(long since, int paneCount, int chars)
    {
        if (!On) return;
        long d = Delta(since);
        _refreshes++;
        _refreshTicks += d;
        if (d > _refreshTicksMax) _refreshTicksMax = d;
        _panes = $"{paneCount} 栏 chars={chars}";
    }

    public static void AddSave(long since)
    {
        if (!On) return;
        _saves++;
        _saveTicks += Delta(since);
    }

    public static void AddPlace(long since)
    {
        if (!On) return;
        long d = Delta(since);
        _places++;
        _placeTicks += d;
        if (d > _placeTicksMax) _placeTicksMax = d;
    }

    public static void AddUndo() { if (On) _undos++; }

    /// <summary>
    /// 记一次"不该发生但发生了"的错误（全局异常兜底用）。
    /// 弹窗只给人看一句人话，完整堆栈进日志 —— 用户截图给得出原因，我们也不用猜。
    /// 写不进去就算了，绝不让记日志本身再抛一次。
    /// </summary>
    public static void Error(string where, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath,
                $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} 异常 @ {where} ====\r\n" +
                (ex == null ? "(无异常对象)\r\n" : ex + "\r\n"),
                new UTF8Encoding(false));
        }
        catch (Exception) { /* 记不住就算了 */ }
    }

    // ---------------------------------------------------------------- 落盘

    /// <summary>启动时调一次。日志写不进去也绝不影响程序。</summary>
    public static void Start()
    {
        if (!On) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);

            var fi = new FileInfo(LogPath);
            if (fi.Exists && fi.Length > 4 * 1024 * 1024) fi.Delete();   // 别无限长

            _started = DateTime.Now;
            _windowAt = _started;

            // 每次写都开-写-关，不常驻文件句柄：
            // 同时开两个窗口时，常驻句柄会让第二份日志静默丢失（正是排查时最怕的）。
            File.AppendAllText(LogPath,
                $"==== {_started:yyyy-MM-dd HH:mm:ss} 启动 ====\r\n" +
                "字段：click=点击次数/平均与最大耗时；layout=竖线重算次数与耗时；"
                + "place=落线耗时（含写盘）；save=写进度文件耗时；refresh=换文档次数；mem=私有内存\r\n",
                new UTF8Encoding(false));

            _timer = new Timer(_ => Pump(), null, 5000, 5000);
        }
        catch (Exception) { /* 记不住就算了 */ }
    }

    public static void Stop()
    {
        try
        {
            _timer?.Dispose();
            _timer = null;
        }
        catch (Exception) { }
    }

    private static long Take(ref long slot) => Interlocked.Exchange(ref slot, 0);

    private static void Pump()
    {
        if (!On) return;

        try
        {
            DateTime now = DateTime.Now;
            double sec = Math.Max(0.001, (now - _windowAt).TotalSeconds);
            _windowAt = now;

            long clicks = Take(ref _clicks);
            long clickTicks = Take(ref _clickTicks);
            long clickMax = Take(ref _clickTicksMax);

            long layouts = Take(ref _layouts);
            long layoutTicks = Take(ref _layoutTicks);
            long layoutMax = Take(ref _layoutTicksMax);

            long refreshes = Take(ref _refreshes);
            long refreshTicks = Take(ref _refreshTicks);
            long refreshMax = Take(ref _refreshTicksMax);

            long places = Take(ref _places);
            long placeTicks = Take(ref _placeTicks);
            long placeMax = Take(ref _placeTicksMax);

            long saves = Take(ref _saves);
            long saveTicks = Take(ref _saveTicks);

            long undos = Take(ref _undos);

            // 这一窗什么都没发生，就不写，省得日志全是零
            if (clicks == 0 && layouts == 0 && refreshes == 0 && places == 0 && saves == 0 && undos == 0)
                return;

            string mem = "?";
            string gc = "?";
            string thr = "?";
            string hnd = "?";

            try
            {
                using var p = Process.GetCurrentProcess();
                mem = $"{p.PrivateMemorySize64 / 1024 / 1024}MB";
                hnd = p.HandleCount.ToString();
                thr = p.Threads.Count.ToString();
                gc = $"{GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}";
            }
            catch (Exception) { }

            int elapsed = (int)(now - _started).TotalSeconds;

            string line =
                $"{elapsed / 60:00}:{elapsed % 60:00} | " +
                $"click {clicks} avg {Ms(clickTicks / Math.Max(1, clicks)):0.0}ms max {Ms(clickMax):0.0}ms | " +
                $"layout {layouts} ({layouts / sec:0.0}/s) avg {Ms(layoutTicks / Math.Max(1, layouts)):0.00}ms max {Ms(layoutMax):0.0}ms | " +
                $"place {places} avg {Ms(placeTicks / Math.Max(1, places)):0.0}ms max {Ms(placeMax):0.0}ms | " +
                $"save {saves} avg {Ms(saveTicks / Math.Max(1, saves)):0.0}ms | " +
                $"refresh {refreshes} avg {Ms(refreshTicks / Math.Max(1, refreshes)):0.0}ms max {Ms(refreshMax):0.0}ms | " +
                $"undo {undos} | {_panes} | mem {mem} gc {gc} thr {thr} hnd {hnd}";

            var sb = new StringBuilder();
            sb.AppendLine(line);

            // 碰到明显异常就顺手标一行，省得回头看时漏掉
            if (layouts / sec > 30 || Ms(clickMax) > 50 || Ms(layoutMax) > 30)
                sb.AppendLine($"   ↑ 异常：layout {layouts / sec:0.0}/s，click max {Ms(clickMax):0.0}ms，layout max {Ms(layoutMax):0.0}ms");

            lock (Gate)
            {
                File.AppendAllText(LogPath, sb.ToString(), new UTF8Encoding(false));
            }
        }
        catch (Exception) { }
    }
}
