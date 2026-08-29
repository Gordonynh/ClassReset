using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using ClassIsland.ClassReset.Interop;
using ClassIsland.ClassReset.Models;
using ClassIsland.ClassReset.Services;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace ClassIsland.ClassReset.Views;

/// <summary>课后自动复原的设置页。</summary>
[SettingsPageInfo("gordon.classreset", "课后自动复原", "", "")]
public partial class ResetSettingsPage : SettingsPageBase, INotifyPropertyChanged
{
    private readonly ClassResetService? _service;

    public ResetSettings Settings => ResetSettings.Current;

    public ResetSettingsPage()
    {
        _service = IAppHost.Host?.Services.GetService<ClassResetService>();
        DataContext = this;
        InitializeComponent();

        if (_service is not null)
        {
            _service.StateChanged += (_, _) => Refresh();
        }

        // 设置里任何一项变了都把派生的显示文字重算一遍。
        // 不挂这个的话，拖完滑块旁边那个数字不会变——绑定的是本页的只读属性，
        // 而本页从来没为它发过变更通知。
        // 用 RefreshLight 而不是 Refresh：见 RefreshLight 的注释，
        // 全量重算会在拖滑块时反复枚举整个桌面。
        Settings.PropertyChanged += (_, _) => RefreshLight();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        WireEditors();
    }

    /// <summary>把六个点选编辑器接上数据。候选来自课表和现在开着的程序。</summary>
    private void WireEditors()
    {
        Editor("AcrossTimesEditor")?.Configure(
            () => Settings.ForceRunAcrossTimes.ToList(),
            list => { Settings.ForceRunAcrossTimes = list; Settings.Save(); RefreshLight(); },
            () => _service?.AllClassEndTimes ?? [],
            "自定义，如 13:00",
            NormalizeTime, "格式为 HH:mm");

        Editor("ShutdownTimesEditor")?.Configure(
            () => Settings.AutoShutdownTimes.ToList(),
            list => { Settings.AutoShutdownTimes = list; Settings.Save(); RefreshLight(); },
            () => _service?.AllClassEndTimes ?? [],
            "自定义，如 22:40",
            NormalizeTime, "格式为 HH:mm");

        Editor("InstantSubjectsEditor")?.Configure(
            () => Settings.InstantResetSubjects.ToList(),
            list => { Settings.InstantResetSubjects = list; Settings.Save(); },
            () => _service?.AllSubjectNames ?? []);

        Editor("NeverSubjectsEditor")?.Configure(
            () => Settings.NeverResetSubjects.ToList(),
            list => { Settings.NeverResetSubjects = list; Settings.Save(); },
            () => _service?.AllSubjectNames ?? []);

        Editor("KeywordsEditor")?.Configure(
            () => Settings.AlwaysTriggerKeywords.ToList(),
            list => { Settings.AlwaysTriggerKeywords = list; Settings.Save(); },
            RunningProcessNames,
            "自定义关键词");

        Editor("NeverCloseEditor")?.Configure(
            () => Settings.NeverCloseProcesses.ToList(),
            list => { Settings.NeverCloseProcesses = list; Settings.Save(); },
            RunningProcessNames,
            "程序名，不含 .exe");
    }

    private ClassIsland.PluginShared.TokenListEditor? Editor(string name) =>
        this.FindControl<ClassIsland.PluginShared.TokenListEditor>(name);

    private static string? NormalizeTime(string text) =>
        ResetSettings.ParseTimePoint(text)?.ToString(@"hh\:mm");

    /// <summary>现在任务栏上开着的程序名，当关键词和白名单的候选。</summary>
    private static List<string> RunningProcessNames()
    {
        try
        {
            return WindowScanner.Enumerate()
                .Select(w => w.ProcessName)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    #region 显示文字

    /// <summary>现在有哪些「需要收拾」的情况。</summary>
    public string StatusText
    {
        get
        {
            if (_service is null)
            {
                return "插件未就绪。";
            }

            var reasons = _service.CollectReasons(Settings);
            var windows = WindowScanner.Enumerate();
            var head = reasons.Count == 0
                ? "当前无需重置。"
                : "将触发：" + string.Join("；", reasons);
            var subject = _service.CurrentPreviousSubject;
            var subjectText = string.IsNullOrEmpty(subject) ? string.Empty : $"　上一节：{subject}";
            return $"{head}\n任务栏窗口 {windows.Count} 个。{subjectText}";
        }
    }

    public string BaselineText
    {
        get
        {
            if (_service is null)
            {
                return string.Empty;
            }

            var desktop = _service.Desktop;
            if (!desktop.HasBaseline)
            {
                return $"未录入。桌面位置:{DesktopLayoutService.UserDesktop}\n" +
                       "未录入基线时不执行任何桌面操作。";
            }

            var s = desktop.Snapshot;
            var line = $"已录入 {s.CapturedAt:MM-dd HH:mm}：{s.Entries.Count} 个项目" +
                       (s.HasIconPositions ? $"、{s.IconPositions.Count} 个图标位置" : "（未读到图标位置）");
            var diff = desktop.Compare();
            return $"{line}\n当前：{diff.Summary}";
        }
    }

    public string HistoryText
    {
        get
        {
            if (_service is null || _service.History.Count == 0)
            {
                return "暂无记录。";
            }

            return string.Join("\n", _service.History.Take(6).Select(x => x.ToString()));
        }
    }

    public string GraceText => $"{Settings.GraceMilliseconds / 1000.0:F1} 秒";

    public string CellSizeText => $"{Settings.OverlayCellSize:F0} px";

    /// <summary>
    /// 按当前屏幕算出每帧要画多少个格子，让「调大一点」的收益是看得见的。
    /// </summary>
    /// <remarks>
    /// 开销和格子数成正比，格子数是 <c>宽 × 高 ÷ 边长²</c>——边长翻倍，开销降到四分之一。
    /// 只影响铺满整屏的复原动画；提醒条上那个还是听 UltraCode 设置页的。
    /// </remarks>
    public string CellSizeSummary
    {
        get
        {
            var cell = Math.Clamp(Settings.OverlayCellSize, 3, 40);
            var screen = TopLevel.GetTopLevel(this)?.Screens?.Primary?.Bounds;
            var width = screen?.Width ?? 1920;
            var height = screen?.Height ?? 1080;
            var cells = (long)Math.Ceiling(width / cell) * (long)Math.Ceiling(height / cell);

            _ = cells;
            _ = width;
            _ = height;
            return "仅影响全屏的重置动画。调大可降低性能占用，颗粒更粗。";
        }
    }

    /// <summary>
    /// 回收站可不可用。
    /// </summary>
    /// <remarks>
    /// 走的是和执行时同一套判断（解析真实路径），不是看盘符类型——
    /// 本机桌面在 Parallels 共享盘上，盘符类型报的是 <c>Fixed</c>，看它会得到相反的结论。
    /// </remarks>
    public string RecycleWarning
    {
        get
        {
            var desktop = DesktopLayoutService.UserDesktop;
            if (!DesktopLayoutService.CanRecycleOnDesktop)
            {
                return $"⚠ 桌面（{desktop}）不支持回收站，此项即使开启也不会删除文件。";
            }

            return "新增文件移入回收站，可随时找回。过大的文件自动跳过。";
        }
    }

    #endregion

    #region 逗号分隔的列表

    /// <summary>把用户填的时刻回读一遍，认出来几个就说几个——写错了要能当场看见。</summary>
    public string AcrossTimesSummary => DescribePoints(Settings.ForceRunAcrossTimes,
        Settings.ForceRunAcrossPoints, "若跨越此时间点（如午休等），忽略连堂判定。");

    public string ShutdownTimesSummary => DescribePoints(Settings.AutoShutdownTimes,
        Settings.AutoShutdownPoints, "先进行重置后关机。");

    private static string DescribePoints(List<string> raw, List<TimeSpan> parsed, string hint)
    {
        if (raw.Count == 0)
        {
            return hint + "　当前未设置。";
        }

        var text = string.Join("、", parsed.Select(x => x.ToString(@"hh\:mm")));
        var bad = raw.Count - parsed.Count;
        return bad == 0
            ? $"{hint}　当前:{text}"
            : $"{hint}　当前:{text}；另有 {bad} 个无法识别，格式为 HH:mm";
    }

    #endregion

    /// <summary>
    /// 只重算便宜的显示文字。改设置（拖滑块）走这条。
    /// </summary>
    /// <remarks>
    /// <b>这里绝对不能带上 <see cref="StatusText"/> 和 <see cref="BaselineText"/>。</b>
    /// 那两个会枚举任务栏窗口、枚举整个桌面目录、还会走 Shell COM 读图标位置；
    /// 而本机桌面是 Parallels 共享盘（22.8 万个文件）。拖一下滑块就是几十次重算，
    /// 界面会直接卡死。
    /// <para/>
    /// 也不能带上那些 TextBox 绑的属性（<c>AcrossTimesText</c> 之类）——
    /// 重新通知会把输入框里的文字换成规范化之后的版本，正在打字的人会被打断。
    /// </remarks>
    private void RefreshLight()
    {
        Raise(nameof(GraceText), nameof(CellSizeText), nameof(CellSizeSummary),
            nameof(AcrossTimesSummary), nameof(ShutdownTimesSummary), nameof(HistoryText));
    }

    /// <summary>全部重算，包括要枚举窗口和桌面的那几项。只在按「刷新」和状态真的变了时调。</summary>
    private void Refresh()
    {
        RefreshLight();
        Raise(nameof(StatusText), nameof(BaselineText), nameof(RecycleWarning));
    }

    private void Raise(params string[] names)
    {
        foreach (var name in names)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    private void OnRefresh(object? sender, RoutedEventArgs e) => Refresh();

    private void OnPreview(object? sender, RoutedEventArgs e) => _service?.Preview();

    private void OnPreviewShutdown(object? sender, RoutedEventArgs e) => _service?.PreviewShutdown();

    private void OnCapture(object? sender, RoutedEventArgs e)
    {
        _service?.CaptureBaseline();
        Refresh();
    }

    public new event PropertyChangedEventHandler? PropertyChanged;
}
