// ============================================================
// MainWindow.CopyFeedback.cs —— 复制当前句的"当场确认"（声音 + 视觉）
// ============================================================
//
// 解决的问题（T-37）：按下 Ctrl+Alt+C 之后，"到底复制成了没有"看不出来。
// 原来是状态栏一行 11.5px 的浅灰字，还跟自动保存的「已保存 hh:mm:ss」抢同一个位置；
// 更糟的是几条提前 return 的路径（剪贴板被别的程序占着、这一栏没有句子、
// 光标取不到区间）连那行灰字都没有 —— 按下去真的"毫无反应"。
//
// 现在的口径：
//   · 一次按下 = 一次确认，成功和失败都必须有反馈，不留"静默返回"；
//   · 声音：一声短音（成功上滑 100ms / 失败下滑 140ms，见 Core 的 CopyCue），
//     只在成功写完剪贴板之后才响 —— 反馈不先于事实；
//   · 视觉：状态栏上方那条浮条（MainWindow.xaml 的 CopyToast），
//     成功绿 ✓ + 「已复制当前句（N 字）」，失败橙 ！ + 一句为什么；
//   · 连续按：每次从头重放（BeginAnimation 替换上一轮时钟），不排队不叠加，
//     所以"连按十下漏掉几下的反馈"这种问题不会出现；
//   · 不打断朗读：无弹窗、无焦点变化、不吃鼠标、不动布局，只有 6px 上浮 + 淡入淡出；
//   · 声音放不出来（没声卡/被独占）只记日志、不抛异常：视觉反馈照样在，
//     不会因为"响不了"就把一次成功的复制报成失败。
// ============================================================

using System;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using YiTai.Core;

namespace YiTai.App;

public partial class MainWindow
{
    // ---- 浮条的时间口径（毫秒）。成功短、失败长：失败要留出读懂"为什么"的时间 ----
    private const int CopyToastFadeInMs = 120;
    private const int CopyToastHoldSuccessMs = 1150;
    private const int CopyToastHoldFailureMs = 2300;
    private const int CopyToastFadeOutMs = 240;

    /// <summary>
    /// 复制结果唯一的出口：声音 + 浮条一起给。
    /// 成功和失败都走这里，所以"某条路径忘了反馈"在代码上只剩一种可能 —— 没调用它。
    /// </summary>
    /// <param name="ok">是否真的写进了剪贴板。</param>
    /// <param name="message">给眼睛看的那句话（成功时与状态栏同一句）。</param>
    private void ShowCopyFeedback(bool ok, string message)
    {
        Diag.AddCopy(ok);

        CopyFeedbackSound.Play(ok ? CopyCueKind.Success : CopyCueKind.Failure);

        string accentKey = ok ? "Done" : "Warn";
        Brush accent = (Brush)FindResource(accentKey);

        CopyToast.BorderBrush = accent;
        TxtCopyToastIcon.Foreground = accent;
        TxtCopyToastIcon.Text = ok ? "✓" : "！";
        TxtCopyToastText.Text = message;
        TxtCopyToastText.Foreground = (Brush)FindResource(ok ? "TextMain" : "Warn");

        int hold = ok ? CopyToastHoldSuccessMs : CopyToastHoldFailureMs;

        // 关键：每次都用一个新的动画去 BeginAnimation，等于把上一轮的时间轴作废。
        // 这样"连按"只会让浮条一直亮着并重新计时，不会叠出第二条、也不会漏闪。
        var opacity = new DoubleAnimationUsingKeyFrames
        {
            FillBehavior = FillBehavior.HoldEnd,
        };
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame(1.0,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(CopyToastFadeInMs))));
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame(1.0,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(CopyToastFadeInMs + hold))));
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame(0.0,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(CopyToastFadeInMs + hold + CopyToastFadeOutMs))));

        CopyToast.BeginAnimation(UIElement.OpacityProperty, opacity);

        // 6px 上浮：给"刚刚发生了一件事"一点动感，但幅度小到不会牵着视线走
        var shift = new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(CopyToastFadeInMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        CopyToastShift.BeginAnimation(TranslateTransform.YProperty, shift);
    }
}

/// <summary>
/// 提示音的播放器。音本身由 Core 的 <see cref="CopyCue"/> 生成，
/// 这里只负责"放一次"。懒加载：第一次按才建 SoundPlayer，启动不受影响。
/// </summary>
internal static class CopyFeedbackSound
{
    private static readonly object Gate = new();

    private static SoundPlayer? _success;
    private static SoundPlayer? _failure;

    // SoundPlayer 在读流；把流也钉住，别让 GC 把手里的数据收走
    private static MemoryStream? _successStream;
    private static MemoryStream? _failureStream;

    /// <summary>放一声。任何异常都吞掉并只记日志：响不了不该影响复制这件事本身。</summary>
    public static void Play(CopyCueKind kind)
    {
        try
        {
            Player(kind).Play();   // 异步，立刻返回，UI 线程不等它
        }
        catch (Exception ex)
        {
            Diag.Error("复制提示音", ex);
        }
    }

    private static SoundPlayer Player(CopyCueKind kind)
    {
        lock (Gate)
        {
            if (kind == CopyCueKind.Success)
                return _success ??= Build(CopyCueKind.Success, out _successStream);

            return _failure ??= Build(CopyCueKind.Failure, out _failureStream);
        }
    }

    private static SoundPlayer Build(CopyCueKind kind, out MemoryStream stream)
    {
        stream = new MemoryStream(CopyCue.Wav(kind), writable: false);

        var player = new SoundPlayer(stream);
        player.Load();   // 先装进内存：第一次按下不用等文件/流解析

        return player;
    }
}
