using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ClassIsland.ClassReset.Interop;
using ClassIsland.ClassReset.Models;
using ClassIsland.ClassReset.Views;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared;
using ClassIsland.Shared.Enums;
using ClassIsland.Shared.Models.Profile;
using Microsoft.Extensions.Hosting;

namespace ClassIsland.ClassReset.Services;

/// <summary>一次复原执行的结果，用于在设置页里回看做了什么。</summary>
public sealed record ResetReport(DateTime At, List<string> Actions)
{
    public override string ToString() =>
        $"{At:MM-dd HH:mm}　" + (Actions.Count == 0 ? "无需处理" : string.Join("；", Actions));
}

/// <summary>
/// 课后自动复原的主体：盯着课程状态，下课一段时间后判断要不要收拾，
/// 要收拾就先弹倒计时，没人拦就执行。另外还负责定时关机。
/// </summary>
internal sealed class ClassResetService : IHostedService
{
    /// <summary>关机时刻的判定容差。定时器 5 秒一跳，容差取 90 秒，防止某一跳被卡住就整个错过。</summary>
    private static readonly TimeSpan ShutdownTolerance = TimeSpan.FromSeconds(90);

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DesktopLayoutService _desktop;

    private ILessonsService? _lessons;
    private IProfileService? _profiles;

    /// <summary>上一次从「上课」切出来的时刻。为空表示当前不在「刚下课」的窗口里。</summary>
    private DateTime? _classEndedAt;

    /// <summary>这一轮下课已经处理过了（不管是执行了还是被取消/跳过）。</summary>
    private bool _handledThisBreak;

    private TimeState _lastState = TimeState.None;

    /// <summary>今天已经触发过的关机时刻，避免同一个点反复弹。</summary>
    private readonly HashSet<TimeSpan> _shutdownFired = [];

    private DateTime _shutdownFiredDate = DateTime.MinValue;

    /// <summary>正在跑复原流程。这期间不接新的触发。</summary>
    private bool _busy;

    /// <summary>当前进行到哪一档流程。</summary>
    private enum Flow
    {
        /// <summary>没有流程在跑。</summary>
        None,

        /// <summary>下课复原。</summary>
        Reset,

        /// <summary>定时关机（含关机前的那遍复原）。</summary>
        Shutdown
    }

    /// <summary>
    /// 正在进行的流程。倒计时期间也算，不是只有执行期间才算。
    /// </summary>
    /// <remarks>
    /// <b>这是「关机和下课重置打架」的修复点。</b>
    /// <see cref="ResetOverlayWindow.Show"/> 会先把已有的遮罩按「取消」收掉，
    /// 而 <c>_busy</c> 只在真正执行时才为真、倒计时期间是假的。
    /// 于是 12:30 这种既是关机点、又正好是下课点的时刻，
    /// 关机倒计时刚弹出来，十几秒后下课复原的判定就到了，
    /// 后者一 Show 就把关机遮罩当成「用户取消」收掉——
    /// 日志里留下一条「定时关机已取消」，关机则再也不会发生。
    /// <para/>
    /// 现在倒计时一开始就占住这个字段，同级或更低优先级的流程不再插队；
    /// 关机比复原高一档，可以接管（见 <see cref="CheckShutdown"/>）。
    /// </remarks>
    private Flow _flow = Flow.None;

    /// <summary>取消之后的重试计数，按流程分开记。</summary>
    private int _resetRetries;

    private int _shutdownRetries;

    /// <summary>重试用的一次性定时器，保留引用以便取消。</summary>
    private DispatcherTimer? _retryTimer;

    public ClassResetService(string pluginConfigFolder)
    {
        _desktop = new DesktopLayoutService(pluginConfigFolder);
    }

    /// <summary>桌面布局服务，设置页要用。</summary>
    public DesktopLayoutService Desktop => _desktop;

    /// <summary>最近几次执行的记录。</summary>
    public List<ResetReport> History { get; } = [];

    /// <summary>状态有变化时触发，设置页据此刷新。</summary>
    public event EventHandler? StateChanged;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lessons = IAppHost.GetService<ILessonsService>();
        _profiles = IAppHost.GetService<IProfileService>();

        // 这个 Tick 跑在 Avalonia 的 DispatcherTimer 上。抛出去会被 ClassIsland 的
        // 全局异常处理接住，然后 DiagnosticService.DisableCorruptPlugins() 会按堆栈里的
        // 程序集把本插件直接禁用掉——一次异常 = 插件静默失效。所以这里必须自己兜住。
        _timer.Tick += (_, _) =>
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                Record($"检查时出错：{ex.Message}");
            }
        };
        Dispatcher.UIThread.Post(() => _timer.Start(), DispatcherPriority.Background);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer.Stop();
        // 重试定时器也要停。留着的话插件停用之后还会再弹一次遮罩。
        _retryTimer?.Stop();
        _retryTimer = null;
        _flow = Flow.None;
        Dispatcher.UIThread.Post(ResetOverlayWindow.CloseCurrent);
        return Task.CompletedTask;
    }

    #region 触发判断

    private void Tick()
    {
        var settings = ResetSettings.Current;
        if (_lessons is null || _busy)
        {
            return;
        }

        // 关机的判定先走：它比复原高一档，正在倒计时的复原也让位给它。
        if (CheckShutdown(settings))
        {
            return;
        }

        if (!settings.IsEnabled)
        {
            return;
        }

        // 已经有流程在跑（含倒计时）就不要再开一个。
        // 不加这一条的话，新流程一 Show 就把旧遮罩按「取消」收掉了。
        if (_flow != Flow.None)
        {
            return;
        }

        var state = _lessons.CurrentState;

        // 从「上课」切到别的状态 = 刚下课。
        if (_lastState == TimeState.OnClass && state != TimeState.OnClass)
        {
            _classEndedAt = DateTime.Now;
            _handledThisBreak = false;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        // 又上课了，这一轮就结束了。
        if (state == TimeState.OnClass)
        {
            _classEndedAt = null;
            _handledThisBreak = false;
        }

        _lastState = state;

        if (_handledThisBreak || _classEndedAt is not { } endedAt)
        {
            return;
        }

        if ((DateTime.Now - endedAt).TotalSeconds < Math.Max(5, settings.DelaySecondsAfterClass))
        {
            return;
        }

        // 到点了，这一轮只判断一次。
        _handledThisBreak = true;

        var (prev, next) = GetNeighbourClasses();
        var prevSubject = prev is null ? null : ResolveSubjectName(prev.Value.ClassIndex);

        // 「这些科目一律不重置」优先于所有别的判断。
        if (MatchesSubject(settings.NeverResetSubjects, prevSubject))
        {
            Record($"跳过：{prevSubject} 下课不重置");
            return;
        }

        if (ShouldSkipForSameSubject(settings, prev, next))
        {
            return;
        }

        var reasons = CollectReasons(settings);
        if (reasons.Count == 0)
        {
            return;
        }

        // 「这些科目直接开始」：倒计时秒数传 0，遮罩会跳过阶段一。
        var instant = MatchesSubject(settings.InstantResetSubjects, prevSubject);
        _resetRetries = 0;
        AskThenReset(settings, instant ? $"{prevSubject} 下课，直接重置" : "即将还原系统",
            instant ? 0 : settings.CountdownSeconds);
    }

    /// <summary>弹复原倒计时。被取消就按设置排一次重试。</summary>
    private void AskThenReset(ResetSettings settings, string title, int seconds)
    {
        _flow = Flow.Reset;
        Dispatcher.UIThread.Post(() =>
            ResetOverlayWindow.Show(title, seconds, "点击屏幕以取消",
                onConfirmed: overlay => RunReset(settings, overlay, dryRun: false, thenShutdown: false),
                onCancelled: () => OnCancelled(Flow.Reset, settings)));
    }

    /// <summary>
    /// 倒计时被取消了。
    /// </summary>
    /// <remarks>
    /// 取消常常不是「不要执行」而是「现在不方便」——正讲着课、正放着视频。
    /// 所以隔一段时间再问一次，到次数为止。
    /// <para/>
    /// 被更高优先级的流程接管时（复原让位给关机）不算取消，那条路径会先把
    /// <see cref="_flow"/> 改掉，这里据此认出来并跳过重试。
    /// </remarks>
    private void OnCancelled(Flow flow, ResetSettings settings)
    {
        if (_flow != flow)
        {
            // 已经被别的流程接管，这次「取消」是接管的副作用，不是人点的。
            return;
        }

        _flow = Flow.None;

        var isShutdown = flow == Flow.Shutdown;
        var used = isShutdown ? _shutdownRetries : _resetRetries;
        var max = Math.Max(0, settings.MaxRetries);
        var what = isShutdown ? "定时关机" : "重置";

        if (!settings.RetryAfterCancel || used >= max)
        {
            Record($"{what}已取消" + (settings.RetryAfterCancel && max > 0 ? "，重试次数已用完" : string.Empty));
            return;
        }

        var delay = Math.Max(10, settings.RetryDelaySeconds);
        if (isShutdown)
        {
            _shutdownRetries++;
        }
        else
        {
            _resetRetries++;
        }

        Record($"{what}已取消，{delay} 秒后重试（第 {used + 1}/{max} 次）");

        _retryTimer?.Stop();
        _retryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(delay) };
        _retryTimer.Tick += (_, _) =>
        {
            _retryTimer?.Stop();
            _retryTimer = null;
            Retry(flow, ResetSettings.Current);
        };
        _retryTimer.Start();
    }

    /// <summary>重试一次被取消的流程。重试前重新判定条件。</summary>
    private void Retry(Flow flow, ResetSettings settings)
    {
        if (_busy || _flow != Flow.None)
        {
            return;
        }

        if (flow == Flow.Shutdown)
        {
            BeginScheduledShutdown(settings);
            return;
        }

        if (!settings.IsEnabled)
        {
            return;
        }

        // 期间可能已经收拾干净了，或者又上课了——这两种情况都不该再打扰。
        if (_lessons?.CurrentState == TimeState.OnClass)
        {
            Record("重试取消：已经上课");
            return;
        }

        if (CollectReasons(settings).Count == 0)
        {
            Record("重试取消：已无需重置");
            return;
        }

        AskThenReset(settings, "即将还原系统", settings.CountdownSeconds);
    }

    /// <summary>科目名在不在这个列表里。空列表永远不命中。</summary>
    private static bool MatchesSubject(List<string> list, string? subject) =>
        !string.IsNullOrEmpty(subject) && list.Any(x =>
            !string.IsNullOrWhiteSpace(x) &&
            string.Equals(x.Trim(), subject, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 连堂课（下节课和上节课同一科目）时跳过。
    /// </summary>
    /// <remarks>
    /// 但如果两节课之间跨过了设定的任意一个时刻（默认 09:40 / 13:00 / 18:00），
    /// 说明中间隔了大课间、午休或晚饭那么久，教室很可能被别人用过，
    /// 连堂的理由就不成立了——这种情况照常执行。
    /// </remarks>
    private bool ShouldSkipForSameSubject(ResetSettings settings,
        (TimeLayoutItem Item, int ClassIndex)? prev, (TimeLayoutItem Item, int ClassIndex)? next)
    {
        if (!settings.SkipWhenSameSubject || prev is null || next is null)
        {
            return false;
        }

        var prevSubject = ResolveSubjectName(prev.Value.ClassIndex);
        var nextSubject = ResolveSubjectName(next.Value.ClassIndex);
        if (string.IsNullOrEmpty(prevSubject) || prevSubject != nextSubject)
        {
            return false;
        }

        // 同科目。再看中间有没有跨过任何一个中断时刻。
        var gapStart = prev.Value.Item.EndTime;
        var gapEnd = next.Value.Item.StartTime;
        foreach (var across in settings.ForceRunAcrossPoints)
        {
            if (gapStart <= across && across <= gapEnd)
            {
                return false;
            }
        }

        Record($"跳过：连堂 {nextSubject}");
        return true;
    }

    /// <summary>找出这次课间前后的两节课。</summary>
    private ((TimeLayoutItem Item, int ClassIndex)? Prev, (TimeLayoutItem Item, int ClassIndex)? Next)
        GetNeighbourClasses()
    {
        var plan = _lessons?.CurrentClassPlan;
        var layout = plan?.TimeLayout;
        if (plan is null || layout is null)
        {
            return (null, null);
        }

        var now = DateTime.Now.TimeOfDay;
        (TimeLayoutItem, int)? prev = null;
        (TimeLayoutItem, int)? next = null;

        var classIndex = 0;
        foreach (var item in layout.Layouts)
        {
            if (item.TimeType != 0)
            {
                continue;
            }

            if (item.EndTime <= now)
            {
                prev = (item, classIndex);
            }
            else if (next is null)
            {
                // 判据是 EndTime > now 而不是 StartTime > now。
                // 连堂时下一节课可能已经开始了（StartTime <= now），
                // 用 StartTime 判会直接跳过它，把「再下一节」当成下一节。
                next = (item, classIndex);
            }

            classIndex++;
        }

        return (prev, next);
    }

    /// <summary>把「第几节课」翻译成科目名。</summary>
    private string? ResolveSubjectName(int classIndex)
    {
        var plan = _lessons?.CurrentClassPlan;
        var subjects = _profiles?.Profile.Subjects;
        if (plan is null || subjects is null || classIndex < 0 || classIndex >= plan.Classes.Count)
        {
            return null;
        }

        return subjects.TryGetValue(plan.Classes[classIndex].SubjectId, out var subject)
            ? subject.Name
            : null;
    }

    /// <summary>刚下课的那一节是什么科目。设置页里显示用。</summary>
    public string? CurrentPreviousSubject
    {
        get
        {
            var (prev, _) = GetNeighbourClasses();
            return prev is null ? null : ResolveSubjectName(prev.Value.ClassIndex);
        }
    }

    /// <summary>
    /// 课表里所有下课时刻，去重排好序。设置页拿来当「连堂中断」和「关机时刻」的候选。
    /// </summary>
    public List<string> AllClassEndTimes
    {
        get
        {
            var result = new SortedSet<TimeSpan>();
            var layouts = _profiles?.Profile.TimeLayouts;
            if (layouts is not null)
            {
                foreach (var layout in layouts.Values)
                {
                    foreach (var item in layout.Layouts)
                    {
                        if (item.TimeType == 0)
                        {
                            result.Add(item.EndTime);
                        }
                    }
                }
            }

            return result.Select(x => x.ToString(@"hh\:mm")).ToList();
        }
    }

    /// <summary>课表里出现过的全部科目名。设置页里列出来方便照抄。</summary>
    public List<string> AllSubjectNames =>
        _profiles?.Profile.Subjects.Values
            .Select(x => x.Name)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList() ?? [];

    /// <summary>看看当前有哪些「需要收拾」的情况。</summary>
    public List<string> CollectReasons(ResetSettings settings)
    {
        var reasons = new List<string>();

        var windows = WindowScanner.Enumerate();
        if (windows.Count > 0)
        {
            // 关键词命中的单独列出来，让人一眼知道是什么留在屏幕上。
            var hits = windows
                .Where(w => settings.AlwaysTriggerKeywords.Any(k =>
                    !string.IsNullOrWhiteSpace(k) &&
                    (w.Title.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                     w.ProcessName.Contains(k, StringComparison.OrdinalIgnoreCase))))
                .Select(w => w.ProcessName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (hits.Count > 0)
            {
                reasons.Add($"{string.Join("、", hits)} 还开着");
            }
            else if (settings.TriggerOnOpenWindows)
            {
                reasons.Add($"{windows.Count} 个窗口没关");
            }
        }

        if (settings.TriggerOnDesktopChanged && _desktop.HasBaseline)
        {
            var diff = _desktop.Compare();
            if (diff.HasChanges)
            {
                reasons.Add($"桌面{diff.Summary}");
            }
        }

        if (settings.TriggerOnRemovableDrive)
        {
            var drives = UsbEjector.FindRemovableDrives();
            if (drives.Count > 0)
            {
                reasons.Add($"{drives.Count} 个 U 盘没弹出");
            }
        }

        return reasons;
    }

    #endregion

    #region 定时关机

    /// <summary>
    /// 到关机时刻了吗。到了就先收拾再关。
    /// </summary>
    /// <returns>本次 tick 是否已经被关机流程接管。</returns>
    private bool CheckShutdown(ResetSettings settings)
    {
        if (!settings.AutoShutdownEnabled)
        {
            return false;
        }

        var now = DateTime.Now;
        if (now.Date != _shutdownFiredDate)
        {
            _shutdownFiredDate = now.Date;
            _shutdownFired.Clear();
        }

        foreach (var point in settings.AutoShutdownPoints)
        {
            if (_shutdownFired.Contains(point))
            {
                continue;
            }

            var delta = now.TimeOfDay - point;
            // 只在「刚过点」的一小段窗口里触发。晚太多就当错过了，不补关机——
            // 中午开机不该把上午错过的关机点一次性全补上。
            if (delta < TimeSpan.Zero || delta > ShutdownTolerance)
            {
                continue;
            }

            // 关机比复原高一档。复原正在倒计时的话直接接管——
            // 关机流程本身就先跑一遍复原，接管不会少做任何事。
            // 先改 _flow 再 Show：这样被顶掉的那个遮罩触发的 onCancelled
            // 能认出「不是人点的取消」，不会去排重试。
            if (_flow == Flow.Reset)
            {
                Record("定时关机接管正在进行的重置");
            }
            else if (_flow == Flow.Shutdown)
            {
                // 关机流程已经在跑了，同一个点不重复弹。
                return true;
            }

            _shutdownFired.Add(point);

            // 这一轮下课就交给关机流程了，别再单独触发一次复原。
            _handledThisBreak = true;
            _shutdownRetries = 0;
            _flow = Flow.Shutdown;
            Dispatcher.UIThread.Post(() => BeginScheduledShutdown(settings));
            return true;
        }

        return false;
    }

    /// <summary>
    /// 定时关机：先跑一遍复原，收拾完再弹关机倒计时。
    /// </summary>
    /// <remarks>
    /// 没什么好收拾的就跳过复原直接弹关机倒计时，不用为了走流程而走流程。
    /// </remarks>
    private void BeginScheduledShutdown(ResetSettings settings)
    {
        _flow = Flow.Shutdown;

        var reasons = settings.IsEnabled ? CollectReasons(settings) : [];
        if (reasons.Count == 0)
        {
            AskThenShutdown(settings);
            return;
        }

        ResetOverlayWindow.Show("即将重置并关机", Math.Max(3, settings.CountdownSeconds),
            "点击屏幕以取消",
            onConfirmed: overlay => RunReset(settings, overlay, dryRun: false, thenShutdown: true),
            onCancelled: () => OnCancelled(Flow.Shutdown, settings));
    }

    /// <summary>
    /// 弹关机倒计时。没人拦就先强制退出软件，再关机。
    /// </summary>
    /// <remarks>
    /// <b>顺序很重要：必须先自己把程序清干净，再发关机命令。</b>
    /// 否则只要有一个程序弹着「是否保存」，Windows 的关机就会停在那儿等人点，
    /// 教室里没人管，第二天来电脑还开着——「定时关机」就等于没做。
    /// </remarks>
    private void AskThenShutdown(ResetSettings settings)
    {
        _flow = Flow.Shutdown;
        Dispatcher.UIThread.Post(() =>
            ResetOverlayWindow.Show("即将关机", Math.Max(5, settings.ShutdownCountdownSeconds),
                "点击屏幕以取消",
                onConfirmed: overlay => RunShutdown(settings, overlay),
                onCancelled: () => OnCancelled(Flow.Shutdown, settings)));
    }

    /// <summary>关机流程：强制退出软件 → 发关机命令。</summary>
    private void RunShutdown(ResetSettings settings, ResetOverlayWindow overlay)
    {
        var killFirst = settings.KillAppsBeforeShutdown;
        var steps = killFirst ? new[] { "强制退出程序", "关机" } : ["关机"];
        overlay.BeginExecution(steps);

        var actions = new List<string>();

        Task.Run(() =>
        {
            // 等整屏像素场铺开再动手，否则第一步在动画还没出来时就闪完了。
            Thread.Sleep(700);

            var index = 0;
            if (killFirst)
            {
                RunStep(overlay, index++, true, actions, () => CloseApps(ForShutdown(settings), false));
            }

            Dispatcher.UIThread.Post(() => overlay.SetStep(index, ResetStepState.Running));
            var ok = Shutdown(out var message);
            actions.Add(ok ? "已发出关机命令" : $"关机失败：{message}");

            var stepIndex = index;
            Dispatcher.UIThread.Post(() =>
            {
                overlay.SetStep(stepIndex, ok ? ResetStepState.Done : ResetStepState.Failed, message);
                overlay.FinishExecution(ok ? "再见" : "关机失败");
                Record(string.Join("；", actions));

                // 关机没成功的话机器还在，流程必须放掉。
                // 不放的话 _flow 永远停在 Shutdown，此后连下课重置都不会再触发。
                if (!ok)
                {
                    _flow = Flow.None;
                }
            });
        });
    }

    /// <summary>
    /// 关机专用的关闭设置：一律强制结束，宽限期压到 1 秒。
    /// </summary>
    /// <remarks>
    /// 平时收拾教室会给程序 1.5 秒自己退，让 WPS 之类有机会弹保存提示；
    /// 关机时反过来——留着提示只会把关机卡住。这里仍然先发一次 WM_CLOSE，
    /// 但只等 1 秒就下手，够程序把手上正在写的文件收尾，又不至于让它赖着不走。
    /// <para/>
    /// <see cref="ProcessGuard"/> 的硬保护名单不受影响：系统外壳、输入法、
    /// 远程控制、ClassIsland 自己，任何情况下都不碰。
    /// </remarks>
    private static ResetSettings ForShutdown(ResetSettings source) => new()
    {
        CloseTaskbarApps = true,
        ForceKillIfNotClosed = true,
        GraceMilliseconds = 1000,
        NeverCloseProcesses = source.NeverCloseProcesses
    };

    /// <summary>
    /// 发关机命令。
    /// </summary>
    /// <remarks>
    /// <c>/f</c> 让系统别再为「有程序没退」停下来问——前一步已经把该关的都关了，
    /// 剩下还赖着的一律不等。<c>/t 5</c> 留 5 秒缓冲，这期间
    /// <c>shutdown /a</c> 仍然来得及撤销。
    /// </remarks>
    private static bool Shutdown(out string message)
    {
        try
        {
            var info = new ProcessStartInfo("shutdown", "/s /f /t 5 /c \"课后自动复原：定时关机\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(info);
            message = "5 秒后关机";
            return process is not null;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    #endregion

    #region 执行

    /// <summary>
    /// 真正做复原，一步一步推给遮罩显示。
    /// </summary>
    /// <remarks>
    /// <b>必须跑在后台线程。</b><see cref="AppCloser.CloseAll"/> 里有一段
    /// <c>Thread.Sleep(GraceMilliseconds)</c> 的宽限期，还要等进程真的退出；
    /// 放在 UI 线程上会把整个动画冻住，进度表和像素场全都不动。
    /// 所以这里只在后台做事，每一步的结果再 Post 回 UI 线程更新。
    /// </remarks>
    private void RunReset(ResetSettings settings, ResetOverlayWindow overlay,
        bool dryRun, bool thenShutdown)
    {
        if (_busy)
        {
            // 没接手就得把位置让出来，否则流程卡在这一档下不来。
            _flow = Flow.None;
            return;
        }

        _busy = true;

        var steps = new List<(string Name, bool Enabled)>
        {
            ("关闭程序", settings.CloseTaskbarApps),
            ("弹出 U 盘", settings.EjectRemovableDrives),
            ("清理桌面", settings.RecycleNewDesktopItems),
            ("恢复图标位置", settings.RestoreIconPositions)
        };

        overlay.BeginExecution(steps.Select(x => x.Name).ToList());
        for (var i = 0; i < steps.Count; i++)
        {
            if (!steps[i].Enabled)
            {
                overlay.SetStep(i, ResetStepState.Skipped, "未启用");
            }
        }

        var actions = new List<string>();

        Task.Run(() =>
        {
            // 阶段二的入场动画大约 950 ms，这里等一下再动手，
            // 免得第一步在像素场还没铺满时就闪完了。
            Thread.Sleep(700);

            RunStep(overlay, 0, steps[0].Enabled, actions, () => CloseApps(settings, dryRun));
            RunStep(overlay, 1, steps[1].Enabled, actions, () => EjectDrives(dryRun));
            RunStep(overlay, 2, steps[2].Enabled, actions, () => CleanDesktop(settings, dryRun));
            RunStep(overlay, 3, steps[3].Enabled, actions, () => RestoreIcons(settings, dryRun));
        }).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            _busy = false;
            overlay.FinishExecution(dryRun ? "演练结束" : "重置完成");
            Record((dryRun ? "演练：" : string.Empty) +
                   (actions.Count == 0 ? "无需处理" : string.Join("；", actions)));

            // 执行完了就腾出位置；接着要关机的话流程还没结束，仍占着。
            if (!thenShutdown || dryRun)
            {
                _flow = Flow.None;
            }

            if (thenShutdown && !dryRun)
            {
                // 复原的遮罩要先退场，不然两个全屏窗口会叠在一起。
                var wait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2600) };
                wait.Tick += (_, _) =>
                {
                    wait.Stop();
                    AskThenShutdown(settings);
                };
                wait.Start();
            }
        }));
    }

    /// <summary>跑一步，并把状态推给遮罩。单步出错不影响后面的步骤。</summary>
    private static void RunStep(ResetOverlayWindow overlay, int index, bool enabled,
        List<string> actions, Func<string> work)
    {
        if (!enabled)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => overlay.SetStep(index, ResetStepState.Running));

        string detail;
        var state = ResetStepState.Done;
        try
        {
            detail = work();
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            state = ResetStepState.Failed;
        }

        if (!string.IsNullOrEmpty(detail))
        {
            actions.Add(detail);
        }

        var shown = string.IsNullOrEmpty(detail) ? "无需处理" : detail;
        Dispatcher.UIThread.Post(() => overlay.SetStep(index, state, shown));

        // 每步之间留一点间隔，否则四步在半秒内全部划过去，等于没显示。
        Thread.Sleep(260);
    }

    private static string CloseApps(ResetSettings settings, bool dryRun)
    {
        var result = AppCloser.CloseAll(settings, dryRun);
        var parts = new List<string>();
        if (result.Closed.Count > 0)
        {
            parts.Add(dryRun ? $"将关闭 {result.Closed.Count} 个程序" : $"关闭 {result.Closed.Count} 个程序");
        }

        if (result.Killed.Count > 0)
        {
            parts.Add($"强制结束 {string.Join("、", result.Killed)}");
        }

        if (result.ExplorerWindowsClosed.Count > 0)
        {
            parts.Add($"关闭 {result.ExplorerWindowsClosed.Count} 个资源管理器窗口");
        }

        if (result.Skipped.Count > 0)
        {
            parts.Add($"跳过 {result.Skipped.Count} 个受保护程序");
        }

        if (result.Failed.Count > 0)
        {
            parts.Add($"{result.Failed.Count} 个失败");
        }

        return string.Join("，", parts);
    }

    private static string EjectDrives(bool dryRun)
    {
        if (dryRun)
        {
            var found = UsbEjector.FindRemovableDrives();
            return found.Count == 0 ? string.Empty : $"将弹出 {string.Join("、", found)}";
        }

        var results = UsbEjector.EjectAll();
        var parts = new List<string>();
        var ok = results.Where(x => x.Success).Select(x => x.Drive).ToList();
        if (ok.Count > 0)
        {
            parts.Add($"已弹出 {string.Join("、", ok)}");
        }

        foreach (var bad in results.Where(x => !x.Success))
        {
            parts.Add($"{bad.Drive} 失败：{bad.Message}");
        }

        return string.Join("，", parts);
    }

    private string CleanDesktop(ResetSettings settings, bool dryRun)
    {
        if (!_desktop.HasBaseline)
        {
            return "未录入基线，跳过";
        }

        if (dryRun)
        {
            return $"桌面{_desktop.Compare().Summary}";
        }

        // Restore 里同时做「清新增」和「摆图标」，这里只要清新增那部分，
        // 所以临时把图标那一项关掉；下一步再单独做图标。
        var onlyRecycle = CloneWithout(settings, recycle: true, icons: false);
        var actions = _desktop.Restore(onlyRecycle);
        var text = string.Join("，", actions);
        if (!string.IsNullOrEmpty(_desktop.LastSafetyBlock))
        {
            // 安全闸拦下来的时候一定要说出来，不然表现就是「点了没反应」。
            text = string.IsNullOrEmpty(text)
                ? $"未删除：{_desktop.LastSafetyBlock}"
                : $"{text}（{_desktop.LastSafetyBlock}）";
        }

        return text;
    }

    private string RestoreIcons(ResetSettings settings, bool dryRun)
    {
        if (!_desktop.HasBaseline || !_desktop.Snapshot.HasIconPositions)
        {
            return "无图标基线，跳过";
        }

        if (dryRun)
        {
            var moved = _desktop.Compare().Moved.Count;
            return moved == 0 ? "图标位置无变化" : $"将恢复 {moved} 个图标";
        }

        var onlyIcons = CloneWithout(settings, recycle: false, icons: true);
        return string.Join("，", _desktop.Restore(onlyIcons));
    }

    /// <summary>
    /// 复制一份设置，只留下想要的那一项动作。
    /// </summary>
    /// <remarks>
    /// <see cref="DesktopLayoutService.Restore"/> 是「清新增 + 摆图标」一起做的，
    /// 但进度表要分成两步显示，所以这里各调一次、每次只开一项。
    /// 不直接改 <see cref="ResetSettings.Current"/>——那是用户的设置，改了会落盘。
    /// </remarks>
    private static ResetSettings CloneWithout(ResetSettings source, bool recycle, bool icons) => new()
    {
        RecycleNewDesktopItems = recycle && source.RecycleNewDesktopItems,
        RecycleNewDesktopFolders = source.RecycleNewDesktopFolders,
        MaxRecycleItems = source.MaxRecycleItems,
        // 漏掉这一项的话，删除方式在这条路径上会悄悄退回默认值。
        DeleteWithoutRecycleBin = source.DeleteWithoutRecycleBin,
        RestoreIconPositions = icons && source.RestoreIconPositions
    };

    private void Record(string what)
    {
        History.Insert(0, new ResetReport(DateTime.Now, [what]));
        while (History.Count > 20)
        {
            History.RemoveAt(History.Count - 1);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    #endregion

    /// <summary>录入桌面基线。返回失败原因，成功时返回 null。</summary>
    public string? CaptureBaseline()
    {
        var snapshot = _desktop.Capture();
        if (snapshot is null)
        {
            return _desktop.LastSafetyBlock ?? "录入失败";
        }

        Record($"已录入桌面基线：{snapshot.Entries.Count} 个项目" +
               (snapshot.HasIconPositions ? $"、{snapshot.IconPositions.Count} 个图标位置" : "（未读到图标位置）"));
        return null;
    }

    /// <summary>设置页里的「演练一次」：整套流程照走，但只报告不动手。</summary>
    public void Preview()
    {
        var settings = ResetSettings.Current;
        _flow = Flow.Reset;
        Dispatcher.UIThread.Post(() =>
            ResetOverlayWindow.Show("演练：即将还原系统", Math.Max(3, settings.CountdownSeconds),
                "点击屏幕以取消",
                onConfirmed: overlay => RunReset(settings, overlay, dryRun: true, thenShutdown: false),
                onCancelled: () =>
                {
                    _flow = Flow.None;
                    Record("演练已取消");
                }));
    }

    /// <summary>
    /// 设置页里的「立即执行（真实）」：不等下课，当场把该做的都做了。
    /// </summary>
    /// <remarks>
    /// 演练只报告不动手，验不出「到底删没删掉」「程序关没关得掉」这类问题——
    /// 学校那台就是演练一切正常、真跑起来才发现回收站判定拦住了。
    /// 所以要有一条能真动手的测试入口。
    /// <para/>
    /// 它<b>不受总开关约束</b>：手动按下就是明确的意图，
    /// 正好用来在没开自动重置的机器上验证一遍再决定要不要开。
    /// 倒计时照走，随时可以点掉。
    /// </remarks>
    public void RunNow()
    {
        if (_busy || _flow != Flow.None)
        {
            Record("已有流程正在进行，忽略本次手动执行");
            return;
        }

        var settings = ResetSettings.Current;
        _resetRetries = 0;
        AskThenReset(settings, "手动执行：即将还原系统", Math.Max(3, settings.CountdownSeconds));
    }

    /// <summary>设置页里的「试一下关机提示」：走完整的关机倒计时，但不真关。</summary>
    public void PreviewShutdown()
    {
        var settings = ResetSettings.Current;
        _flow = Flow.Shutdown;
        Dispatcher.UIThread.Post(() =>
            ResetOverlayWindow.Show("即将关机（演练）", Math.Max(5, settings.ShutdownCountdownSeconds),
                "点击屏幕以取消",
                onConfirmed: overlay =>
                {
                    var dry = AppCloser.CloseAll(ForShutdown(settings), dryRun: true);
                    overlay.BeginExecution(["强制退出程序", "关机"]);
                    overlay.SetStep(0, ResetStepState.Skipped,
                        $"演练：将强制退出 {dry.Closed.Count} 个程序，跳过 {dry.Skipped.Count} 个受保护程序");
                    overlay.SetStep(1, ResetStepState.Skipped, "演练，未真正关机");
                    overlay.FinishExecution("演练结束");
                    Record($"演练：将强制退出 {dry.Closed.Count} 个程序，未真正关机");
                    _flow = Flow.None;
                },
                onCancelled: () =>
                {
                    _flow = Flow.None;
                    Record("关机演练已取消");
                }));
    }
}
