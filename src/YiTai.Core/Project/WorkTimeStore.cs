// ============================================================
// WorkTimeStore.cs —— 每个项目"每天干了多久"的落盘
// ============================================================
//
// 存在 <项目根>\.yitai\worktime.json：
//
//   { "Version": 1,
//     "Days":    { "2026-11-17": 3600, "2026-11-18": 1275 } }
//
// 值是那一天累计的工作秒数（键是 yyyy-MM-dd 自然日）。
//
// 口径（跟界面层的 WorkTimer 约定，两边别各说各话）：
//   · 界面层每秒把"正在工作"的 1 秒记到**当时所在的自然日**上，
//     所以跨午夜的一段工作会自然拆进两天，不需要任何特殊处理；
//   · 界面层每 30 秒 Flush 一次 + 关窗时 Flush 一次，
//     所以软件崩溃/断电最多丢最后 30 秒，其他都在；
//   · "本次工作时长"只在内存里，重启从零开始；这里只留按天的账。
//
// 为什么单独一个文件而不是塞进 project.json / panes.json：
//   · 它是流水账，一天一条会无限增长，跟"清单/摆法"不是一类东西；
//   · **只新增文件、不动任何旧文件**：旧项目没有它照样打开（Load 返回空账），
//     不用升 schema、不用迁移 —— 红线里"改结构先备份"的压力根本不会出现。
//
// 文件坏了就当没记过（跟 panes.json 一个脾气），绝不连累项目打不开。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YiTai.Core;

public sealed class WorkTimeLedger
{
    public int Version { get; set; } = 1;

    /// <summary>自然日（yyyy-MM-dd）→ 该日累计工作秒数。</summary>
    public Dictionary<string, long> Days { get; set; } = new();
}

public static class WorkTimeStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// 读账。项目没记过、文件不存在、内容坏掉，都返回空账 —— 旧项目打开绝不报错。
    /// </summary>
    public static WorkTimeLedger Load(string projectRoot)
    {
        var empty = new WorkTimeLedger();

        if (string.IsNullOrEmpty(projectRoot)) return empty;

        string file = ProjectLayout.WorkTimeFile(projectRoot);
        if (!File.Exists(file)) return empty;

        try
        {
            var ledger = JsonSerializer.Deserialize<WorkTimeLedger>(File.ReadAllText(file), Options);
            if (ledger == null) return empty;

            ledger.Days ??= new Dictionary<string, long>();
            return ledger;
        }
        catch (Exception)
        {
            return empty;   // 读不懂就当没记过，不连累打开项目
        }
    }

    /// <summary>某天累计了多少秒。没记过就是 0。</summary>
    public static long SecondsOf(WorkTimeLedger ledger, string day) =>
        ledger.Days.TryGetValue(day, out long v) ? v : 0;

    /// <summary>把 seconds 记到 day 上（累加，不是覆盖）。秒数不足 1 就不动账。</summary>
    public static void Add(WorkTimeLedger ledger, string day, long seconds)
    {
        if (ledger == null || string.IsNullOrEmpty(day) || seconds <= 0) return;
        ledger.Days.TryGetValue(day, out long cur);
        ledger.Days[day] = cur + seconds;
    }

    /// <summary>
    /// 原子写：先写 .tmp 再 move（先 .tmp 再 move，写一半断电不会留半个坏文件）。
    /// 只碰 worktime.json 这一个文件，.yitai 里别的一个字不动。
    /// </summary>
    public static void Save(string projectRoot, WorkTimeLedger ledger)
    {
        if (string.IsNullOrEmpty(projectRoot) || ledger == null) return;

        Directory.CreateDirectory(ProjectLayout.Private(projectRoot));

        string file = ProjectLayout.WorkTimeFile(projectRoot);
        string tmp = file + ".tmp";

        File.WriteAllText(tmp, JsonSerializer.Serialize(ledger, Options));
        File.Move(tmp, file, overwrite: true);
    }
}
