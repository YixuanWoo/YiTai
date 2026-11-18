// ============================================================
// CopyCue.cs —— 复制当前句的两种提示音（成功 / 失败）
// ============================================================
//
// 为什么要放在 Core：
//   · 界面层只管"放"，音本身是一段确定的 PCM，属于纯计算，不该混进界面；
//   · 放在 Core 就能被试跑（第 24 节断言：波形走向、时长、振幅、逐字节稳定）；
//   · 零第三方依赖：一个 16 位单声道 WAV 自己拼头就行（44 字节 RIFF 头 + PCM）。
//
// 声音怎么设计（不是随便加个 beep）：
//   · 成功：100ms、一声上滑（780Hz → 1180Hz），音量压到 0.28 峰值。
//     上滑 = "办成了"的直觉；短到不盖住朗读的一个词；音量只够听清、不刺耳。
//   · 失败：140ms、一声下滑（560Hz → 340Hz），峰值 0.34（比成功略响一点）。
//     下滑 = "没成"的直觉，音色更低更闷，闭着眼也不会跟成功混淆。
//   · 两端包络用 sin² 渐入渐出：起止都归零，喇叭不会"啪"一声（那才是真烦人）。
//   · 只有一个音，连续按就是同一个音重来一遍，不叠、不排队，不会越按越乱。
//
// 波形走向能被断言（第 24 节用零交叉数比较前后半段）：上滑后段更密、下滑更疏，
// 所以"成功/失败听得出区别"这件事有可复算的证据，不是只写在注释里。

using System;

namespace YiTai.Core;

/// <summary>提示音的种类。与"成功/失败"一一对应，不设第三种。</summary>
public enum CopyCueKind
{
    /// <summary>复制成功：上滑、短、轻。</summary>
    Success,

    /// <summary>复制没成（含"没东西可复制"）：下滑、略长、略沉。</summary>
    Failure,
}

/// <summary>把"成功/失败"合成为一段可直接播放的 WAV 字节。</summary>
public static class CopyCue
{
    /// <summary>采样率。手机/桌面都能直接放，不需要重采样。</summary>
    public const int SampleRate = 44100;

    /// <summary>单声道。一个提示音不需要立体声音场。</summary>
    public const int Channels = 1;

    /// <summary>16 位小端 PCM，最通用。</summary>
    public const int BitsPerSample = 16;

    /// <summary>成功音的时长（毫秒）。一眨眼就过去，不打断读句。</summary>
    public const int SuccessMs = 100;

    /// <summary>失败音的时长（毫秒）。比成功略长一点，给人反应时间。</summary>
    public const int FailureMs = 140;

    /// <summary>某个提示音的时长（毫秒）。</summary>
    public static int DurationMs(CopyCueKind kind)
        => kind == CopyCueKind.Success ? SuccessMs : FailureMs;

    /// <summary>某个提示音的扫频范围（起 Hz → 止 Hz）。上滑=成功，下滑=失败。</summary>
    public static (double FromHz, double ToHz) Sweep(CopyCueKind kind)
        => kind == CopyCueKind.Success ? (780.0, 1180.0) : (560.0, 340.0);

    /// <summary>峰值振幅（0～1，相对满量程）。只够听清，不刺耳。</summary>
    public static double PeakAmplitude(CopyCueKind kind)
        => kind == CopyCueKind.Success ? 0.28 : 0.34;

    /// <summary>
    /// 生成完整的 WAV 文件字节（RIFF 头 + 16 位单声道 PCM）。
    /// 同样的 kind 每次得到逐字节相同的结果 —— 真机放出来的声音
    /// 与交付给审查的那份 .wav 是同一段，可以拿去复听比对。
    /// </summary>
    public static byte[] Wav(CopyCueKind kind)
    {
        int ms = DurationMs(kind);
        int count = SampleRate * ms / 1000;
        if (count < 2) count = 2;

        (double from, double to) = Sweep(kind);
        double peak = PeakAmplitude(kind);

        var pcm = new short[count];
        double phase = 0;

        for (int i = 0; i < count; i++)
        {
            double t = (double)i / (count - 1);            // 0 → 1
            double hz = from + (to - from) * t;            // 线性扫频

            // 相位要自己积分：直接用 sin(2π f t) 会在扫频时产生假的频率跳变
            phase += 2 * Math.PI * hz / SampleRate;

            // sin² 包络：两端为 0，中间最响；既无爆音，也听不出"开关"
            double env = Math.Sin(Math.PI * t);
            env *= env;

            double sample = Math.Sin(phase) * env * peak;
            pcm[i] = (short)Math.Round(sample * short.MaxValue);
        }

        return PackWav(pcm);
    }

    /// <summary>把 PCM 样本装进标准 WAV 容器。头是死的 44 字节，没有可选项。</summary>
    private static byte[] PackWav(short[] pcm)
    {
        int dataBytes = pcm.Length * 2;
        var wav = new byte[44 + dataBytes];
        int p = 0;

        // ---- RIFF 头 ----
        WriteAscii(wav, ref p, "RIFF");
        WriteInt32(wav, ref p, 36 + dataBytes);      // 后面还有多少字节
        WriteAscii(wav, ref p, "WAVE");

        // ---- fmt 块（PCM 格式，长度固定 16）----
        WriteAscii(wav, ref p, "fmt ");
        WriteInt32(wav, ref p, 16);
        WriteInt16(wav, ref p, 1);                   // 1 = PCM，不压缩
        WriteInt16(wav, ref p, Channels);
        WriteInt32(wav, ref p, SampleRate);
        WriteInt32(wav, ref p, SampleRate * Channels * (BitsPerSample / 8));  // 每秒字节
        WriteInt16(wav, ref p, (short)(Channels * (BitsPerSample / 8)));      // 每帧字节
        WriteInt16(wav, ref p, (short)BitsPerSample);

        // ---- data 块 ----
        WriteAscii(wav, ref p, "data");
        WriteInt32(wav, ref p, dataBytes);

        for (int i = 0; i < pcm.Length; i++)
        {
            wav[p++] = (byte)(pcm[i] & 0xFF);
            wav[p++] = (byte)((pcm[i] >> 8) & 0xFF);
        }

        return wav;
    }

    private static void WriteAscii(byte[] buf, ref int p, string s)
    {
        for (int i = 0; i < s.Length; i++) buf[p++] = (byte)s[i];
    }

    private static void WriteInt32(byte[] buf, ref int p, int v)
    {
        buf[p++] = (byte)(v & 0xFF);
        buf[p++] = (byte)((v >> 8) & 0xFF);
        buf[p++] = (byte)((v >> 16) & 0xFF);
        buf[p++] = (byte)((v >> 24) & 0xFF);
    }

    private static void WriteInt16(byte[] buf, ref int p, short v)
    {
        buf[p++] = (byte)(v & 0xFF);
        buf[p++] = (byte)((v >> 8) & 0xFF);
    }
}
