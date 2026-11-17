// ============================================================
// WorkTimer.cs —— "本次工作了多久"的计时器（界面层）
// ============================================================
//
// 需求原文：工作状态下计时——键鼠操作、点选、设置进度、滚动视窗、输入等，都算
// "正在工作"的证据；窗口一段时间没有任何动作，或者窗口被最小化、未被激活，
// 就说明人走开了，计时停止；界面上要显示本次工作了多长时间。
//
// 口径（验收依据，别留模糊地带）：
//
//   什么算"活动"：
//     · 键盘按下（任意键）；
//     · 鼠标左/中/右键按下（点选、落进度线、拖滚动条都落在这里）；
//     · 滚轮滚动（滚动视窗、Ctrl+滚轮缩放都算）。
//     · **纯鼠标移动不算活动**：人走开碰了一下桌子不算干活。
//
//   什么算"走开"（暂停计时）：
//     · 最近一次活动距今超过 180 秒（3 分钟）；
//     · 或窗口不是激活状态（切到别的程序、最小化了）——立即暂停，不等 3 分钟。
//
//   恢复怎么算：
//     · 窗口回到激活状态且来了新活动 → 继续累计，**不另开一段**。
//       "本次" = 软件本次启动以来所有"正在工作"的秒数总和。
//
//   显示：
//     · 窗口标题后面追加「· 工作 h:mm:ss」。放标题不放栏底：三四个栏各有各的
//       栏底信息条，塞哪栏都别扭；标题全局可见、不挤任何排版。
//     · 窗口底部状态栏常显同一份「工作 h:mm:ss」（OnDisplay 回调，T-34；
//       没开项目也显示「工作 0:00:00」，口径与标题一致）。标题后缀同时保留。
//
//   落盘（存不存、存哪、崩溃怎么办）：
//     · 每 30 秒、换项目时、关窗时，把累计的秒数交给当前项目的
//       .yitai\worktime.json（按自然日入账，见 WorkTimeStore）；
//     · 跨午夜：每秒记进"当时所在的那一天"，自然拆开，不用特殊处理；
//     · 崩溃/断电：最多丢最后 30 秒，其余都在账上；
//     · "本次"时长只在内存里，重启从零开始（按天的账不丢）。
//
//   没开项目时不计时、不落盘：计的是"在这本书上干了多久"，没有书就不计，
//   免得挂机时间错记到下一个打开的项目头上。
//
//   不提供手动暂停/清零：口径全自动，误触反而毁账；真要清零删 worktime.json 即可。
// ============================================================

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using YiTai.Core;

namespace YiTai.App;

public sealed class WorkTimer
{
    /// <summary>多久没动算"走开"。3 分钟是写进验收口径的数，别悄悄改。</summary>
    public static readonly TimeSpan IdleThreshold = TimeSpan.FromSeconds(180);

    /// <summary>每隔多少秒往项目里落一次账。</summary>
    private const int FlushEverySeconds = 30;

    private readonly Window _owner;
    private readonly DispatcherTimer _tick;

    /// <summary>最近一次键鼠活动。活动时刷新，暂停时保持不动。</summary>
    private DateTime _lastActivity = DateTime.UtcNow;

    /// <summary>
    /// 回到前台后还没等到新活动：这段时间一律不算。
    /// 光看"窗口激活 + 180 秒内有活动"不够——刚切回来时 _lastActivity 往往还很新，
    /// 会在你真正动手之前就先开始数秒（口径要求"激活后有新活动才续算"）。
    /// </summary>
    private bool _needsActivity = true;

    /// <summary>还没落盘的账：自然日 → 秒。落盘后清空。</summary>
    private readonly Dictionary<string, long> _pending = new();

    /// <summary>"本次"工作秒数（软件本次启动以来的累计，跟落盘无关）。</summary>
    private long _sessionSeconds;

    /// <summary>距离上次落盘攒了几秒。</summary>
    private int _secondsSinceFlush;

    /// <summary>当前计时要记到的项目根。null = 没开项目，不计时。</summary>
    private string? _projectRoot;

    /// <summary>标题的固定部分（"译台 — 项目名"）。计时后缀由这里拼上去。</summary>
    private string _baseTitle = "译台";

    /// <summary>
    /// 状态栏常显回调（T-34）：每次刷标题时同步把「工作 h:mm:ss」送出去，
    /// MainWindow 接到窗口底部状态栏的 TxtWorkTime 上。null = 没接，什么都不发生。
    /// 计时口径（活动才计时、闲置 180 秒停、失焦即停）与这套回调无关，一字不动。
    /// </summary>
    public Action<string>? OnDisplay;

    public WorkTimer(Window owner)
    {
        _owner = owner;

        // 活动证据走 Preview（隧道）事件：不管焦点在哪个栏、哪个按钮，都先经过窗口这里。
        owner.PreviewKeyDown += (_, _) => NoteActivity();
        owner.PreviewMouseDown += (_, _) => NoteActivity();
        owner.PreviewMouseWheel += (_, _) => NoteActivity();

        // 不是激活状态（切程序 / 最小化）立即暂停；回来以后先动一下才续算。
        owner.Deactivated += (_, _) => NoteStateChange();
        owner.Activated += (_, _) =>
        {
            // 刚切回来：把"等新活动"的门闩落下，别让计时抢在动手之前先跑
            _needsActivity = true;
            NoteStateChange();
        };
        owner.StateChanged += (_, _) => NoteStateChange();

        // 离开窗口时把没落盘的秒数记上 —— 别让正常关窗也丢账。
        owner.Closed += (_, _) => Flush();

        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => OnTick();
        _tick.Start();
    }

    /// <summary>窗口标题的固定部分。MainWindow 换项目时告诉它一声，后缀照旧拼。</summary>
    public string BaseTitle
    {
        get => _baseTitle;
        set { _baseTitle = value; RefreshTitle(); }
    }

    /// <summary>换项目时调用：先把上个项目的账落盘，再开始记新项目的。</summary>
    public void SetProject(string? projectRoot)
    {
        Flush();                 // 老账记到老项目头上
        _projectRoot = projectRoot;
        if (projectRoot == null)
        {
            _pending.Clear();    // 没开项目期间的秒数不欠账（反正也没在计）
            RefreshTitle();
        }
    }

    /// <summary>"本次"工作了多久。</summary>
    public TimeSpan SessionElapsed => TimeSpan.FromSeconds(_sessionSeconds);

    /// <summary>现在是不是"正在工作"的状态。</summary>
    public bool IsWorking =>
        _projectRoot != null &&
        !_needsActivity &&
        _owner.IsActive &&
        _owner.WindowState != WindowState.Minimized &&
        DateTime.UtcNow - _lastActivity <= IdleThreshold;

    private void NoteActivity()
    {
        bool wasWorking = IsWorking;
        _lastActivity = DateTime.UtcNow;
        _needsActivity = false;   // 动过了，从现在起才承认"在工作"
        // 从暂停里醒过来时立刻把标题刷一下（秒数没变，但状态变了）。
        if (!wasWorking && IsWorking) RefreshTitle();
    }

    private void NoteStateChange() => RefreshTitle();

    private void OnTick()
    {
        if (!IsWorking)
        {
            // 暂停期间也定期看看标题要不要变（比如刚好到 3 分钟整）。
            RefreshTitle();
            return;
        }

        _sessionSeconds++;
        _secondsSinceFlush++;

        // 记进"现在所在的那一天"——跨午夜的工作自然拆成两天的账。
        string day = DateTime.Now.ToString("yyyy-MM-dd");
        _pending.TryGetValue(day, out long cur);
        _pending[day] = cur + 1;

        if (_secondsSinceFlush >= FlushEverySeconds)
        {
            Flush();
            _secondsSinceFlush = 0;
        }

        RefreshTitle();
    }

    /// <summary>把没落盘的秒数交给当前项目的 worktime.json。没项目/没新增秒数就不动盘。</summary>
    public void Flush()
    {
        if (_projectRoot == null || _pending.Count == 0) return;

        var ledger = WorkTimeStore.Load(_projectRoot);
        foreach (var (day, seconds) in _pending)
            WorkTimeStore.Add(ledger, day, seconds);
        WorkTimeStore.Save(_projectRoot, ledger);

        _pending.Clear();
    }

    private void RefreshTitle()
    {
        // 没开项目就不带后缀；开着的暂停了就显示冻结的时长（看得见"停了"）。
        string title = _projectRoot == null
            ? _baseTitle
            : $"{_baseTitle} · 工作 {Format(SessionElapsed)}";
        if (_owner.Title != title) _owner.Title = title;

        // 状态栏常显同一份时长（T-34）：没开项目也显示，口径与标题一致（0:00:00）。
        OnDisplay?.Invoke("工作 " + Format(SessionElapsed));
    }

    /// <summary>时长格式：h:mm:ss（小时不封顶，翻一天账也读得懂）。</summary>
    public static string Format(TimeSpan t) =>
        $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}";
}
