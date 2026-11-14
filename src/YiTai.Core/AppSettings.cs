// ============================================================
// AppSettings.cs —— 软件自己的设置（跟项目无关）
// ============================================================
//
// 放在 %APPDATA%\YiTai\settings.json，与项目文件严格分开：
//   项目文件说"这本书怎么翻"，这里说"这个软件上次开到哪儿"。
// 门窗尺寸、上次打开的项目、自动保存间隔都在这里。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace YiTai.Core;

public sealed class AppSettings
{
    /// <summary>上次打开的项目根目录（下次启动直接进它）。空 = 没有。</summary>
    public string LastProjectRoot { get; set; } = "";

    /// <summary>上次选中的译文 Id（译文是独立文档，不再挂在源文件上）。</summary>
    public string LastTranslationId { get; set; } = "";

    /// <summary>
    /// 最近打开过的资料文件（绝对路径，最多留几十条）。
    /// 放在下拉里方便一键换回来 —— 只是"最近用过"，不代表它属于这个项目。
    /// </summary>
    public List<string> RecentRefFiles { get; set; } = new();

    /// <summary>
    /// 每个项目上次开着的对照栏（键是项目根的小写全路径）。
    /// 按项目分开记：换个项目不该把上一本排列搬过去。
    /// </summary>
    public Dictionary<string, List<RefDocSpec>> PanesByProject { get; set; } = new();

    /// <summary>
    /// 每个项目上次的栏宽比例（键同上，值依次是 各对照栏…、译文栏）。
    /// 只用来恢复"上次拖成什么样"，缺了就用默认比例。
    /// </summary>
    public Dictionary<string, List<double>> PaneWeightsByProject { get; set; } = new();

    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 860;
    public bool WindowMaximized { get; set; }

    /// <summary>停手多久后自动保存（毫秒）。0 = 关闭自动保存（Ctrl+S 仍然有效）。</summary>
    public int AutoSaveDelayMs { get; set; } = 1200;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 设置文件的位置。默认 %APPDATA%\YiTai\settings.json。
    /// 环境变量 YITAI_CONFIG_DIR 可以把它指到别处 —— 测试必须这么做：
    /// 冒烟测试里有一节是"设置读写"，直接落盘会把用户真实的
    /// 上次项目、栏位记忆、最近文件整份覆盖掉（2026-11-13 已出过一次事故）。
    /// </summary>
    public static string DefaultPath
    {
        get
        {
            string over = Environment.GetEnvironmentVariable("YITAI_CONFIG_DIR") ?? "";

            string dir = over.Trim().Length > 0
                ? over
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YiTai");

            return Path.Combine(dir, "settings.json");
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(DefaultPath)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(DefaultPath), Options) ?? new AppSettings();
        }
        catch
        {
            // 设置坏了不该让软件打不开：用默认值继续
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            string dir = Path.GetDirectoryName(DefaultPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(DefaultPath, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // 存不下就下次再说，不打断用户干活
        }
    }
}
