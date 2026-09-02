using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassIsland.ClassReset.Models;

/// <summary>
/// 课后自动复原的设置。
/// </summary>
public partial class ResetSettings : ObservableObject
{
    public static ResetSettings Current { get; private set; } = new();

    private static string? _configPath;

    #region 触发

    /// <summary>总开关。</summary>
    [ObservableProperty] private bool _isEnabled = true;

    /// <summary>下课后等多少秒开始检查。</summary>
    [ObservableProperty] private int _delaySecondsAfterClass = 15;

    /// <summary>倒计时秒数。这段时间里点一下屏幕就取消。</summary>
    [ObservableProperty] private int _countdownSeconds = 5;

    /// <summary>
    /// 下一节课和上一节课是同一科目时跳过。
    /// </summary>
    /// <remarks>
    /// 连堂课中间不用收拾——老师还是同一个，东西还要接着用。
    /// </remarks>
    [ObservableProperty] private bool _skipWhenSameSubject = true;

    /// <summary>
    /// 即使同科目，只要两节课之间跨过了这些时刻中的<b>任意一个</b>，也照常执行。
    /// </summary>
    /// <remarks>
    /// 默认 09:40 / 13:00 / 18:00，正好卡在课表的三段长休里：
    /// 大课间 09:30–10:00、午休 12:30–14:30、晚饭 17:50–18:30。
    /// 跨过这么长的间隔说明教室很可能被别人用过，连堂的理由就不成立了。
    /// </remarks>
    [ObservableProperty] private List<string> _forceRunAcrossTimes = ["09:40", "13:00", "18:00"];

    /// <summary>
    /// 旧版本的单个时刻设置。
    /// </summary>
    /// <remarks>
    /// 只为读旧配置而留。<see cref="Initialize"/> 会把它迁移进
    /// <see cref="ForceRunAcrossTimes"/>，之后不再使用。
    /// </remarks>
    [ObservableProperty] private string? _forceRunAcrossTime;

    /// <summary>
    /// 这些科目下课后<b>直接开始重置</b>，不走倒计时。
    /// </summary>
    /// <remarks>
    /// 按<b>刚下课的那一节</b>的科目名判断。比如体育课下课后教室最乱，
    /// 又没人守着电脑，等倒计时没有意义。
    /// </remarks>
    [ObservableProperty] private List<string> _instantResetSubjects = [];

    /// <summary>
    /// 这些科目下课后<b>一律不重置</b>。同样按刚下课的那一节判断。
    /// </summary>
    [ObservableProperty] private List<string> _neverResetSubjects = [];

    #endregion

    #region 定时关机

    /// <summary>到点自动关机。</summary>
    [ObservableProperty] private bool _autoShutdownEnabled = true;

    /// <summary>
    /// 在这些时刻关机。
    /// </summary>
    /// <remarks>
    /// 默认 12:30 / 22:40，正是课表里上午最后一节和晚上最后一节的下课点。
    /// 到点会先跑一遍复原流程，收拾完再弹关机倒计时。
    /// </remarks>
    [ObservableProperty] private List<string> _autoShutdownTimes = ["12:30", "22:40"];

    /// <summary>关机前的倒计时秒数。这段时间里点屏幕任意位置或按任意键即取消。</summary>
    [ObservableProperty] private int _shutdownCountdownSeconds = 30;

    /// <summary>
    /// 被取消之后过一会儿再来一次。
    /// </summary>
    /// <remarks>
    /// 取消往往不是「不要执行」，而是「现在不方便」——正讲着课、正播着视频。
    /// 隔几分钟重新问一次，比一次被取消就整轮作废合理。
    /// 重试前会重新判定一次条件：桌面已经收拾干净了就不再打扰。
    /// </remarks>
    [ObservableProperty] private bool _retryAfterCancel = true;

    /// <summary>取消后隔多少秒重试。</summary>
    [ObservableProperty] private int _retryDelaySeconds = 180;

    /// <summary>
    /// 最多重试几次。
    /// </summary>
    /// <remarks>不设上限就成了没完没了的骚扰，到次数还没通过就这一轮作罢。</remarks>
    [ObservableProperty] private int _maxRetries = 2;

    /// <summary>
    /// 关机前先强制退出任务栏上的软件。
    /// </summary>
    /// <remarks>
    /// 不这么做的话，只要有一个程序弹着「是否保存」，Windows 的关机就会停在那儿等，
    /// 教室里没人管，第二天来还开着。先自己把它们清干净，关机才是确定会发生的事。
    /// <para/>
    /// 受保护的进程（系统外壳、输入法、远程控制、ClassIsland 自己）照样不碰。
    /// </remarks>
    [ObservableProperty] private bool _killAppsBeforeShutdown = true;

    #endregion

    #region 判定条件（满足任意一条就提示复原）

    /// <summary>有任务栏窗口开着就算需要收拾。</summary>
    [ObservableProperty] private bool _triggerOnOpenWindows = true;

    /// <summary>桌面布局变了就算需要收拾。</summary>
    [ObservableProperty] private bool _triggerOnDesktopChanged = true;

    /// <summary>有可移动磁盘没弹出就算需要收拾。</summary>
    [ObservableProperty] private bool _triggerOnRemovableDrive = true;

    /// <summary>
    /// 只要窗口标题或进程名里含有这些关键词就一定算「需要收拾」，
    /// 即使别的条件都不满足。
    /// </summary>
    /// <remarks>
    /// 默认带上 <c>Hite</c>（鸿合）、<c>WPS</c> 和 <c>Seewo</c>（希沃）——
    /// 这几类最常被学生留在屏幕上。大小写不敏感。
    /// </remarks>
    [ObservableProperty] private List<string> _alwaysTriggerKeywords = ["Hite", "WPS", "Seewo"];

    #endregion

    #region 复原动作（各自可关）

    /// <summary>关闭任务栏上的软件。</summary>
    [ObservableProperty] private bool _closeTaskbarApps = true;

    /// <summary>温和关闭失败后强制结束进程。</summary>
    [ObservableProperty] private bool _forceKillIfNotClosed = true;

    /// <summary>温和关闭之后等多少毫秒再强杀。</summary>
    [ObservableProperty] private int _graceMilliseconds = 1500;

    /// <summary>弹出可移动磁盘。</summary>
    [ObservableProperty] private bool _ejectRemovableDrives = true;

    /// <summary>把桌面上新增的文件移到回收站。</summary>
    [ObservableProperty] private bool _recycleNewDesktopItems = true;

    /// <summary>
    /// 新增的<b>文件夹</b>也移到回收站。
    /// </summary>
    /// <remarks>
    /// 单独一个开关，因为文件夹的体量不可预知。真删之前还会递归统计一遍，
    /// 超过 <c>MaxFolderFiles</c> 个文件或 <c>MaxFolderBytes</c> 就跳过并如实上报。
    /// </remarks>
    [ObservableProperty] private bool _recycleNewDesktopFolders = true;

    /// <summary>
    /// 一次最多清理多少个新增项，超过就只上报不删除。
    /// </summary>
    /// <remarks>
    /// 这是防「基线出问题导致误删一整个桌面」的熔断。
    /// <para/>
    /// <b>老版本这里算错过。</b>原来的上限是
    /// <c>Math.Min(10, Math.Max(1, 基线项目数 / 4))</c>——基线有 11 项时上限只有 <b>2</b>，
    /// 学生多放 3 个东西就整批不删了，表现就是「桌面清理从来没生效过」。
    /// 现在改成直接给一个绝对值，和基线大小无关。
    /// </remarks>
    [ObservableProperty] private int _maxRecycleItems = 25;

    /// <summary>
    /// 直接删除，不放入回收站。
    /// </summary>
    /// <remarks>
    /// <b>开了就是永久删除，找不回来。</b>默认关。
    /// <para/>
    /// 之所以要这个开关：回收站不是随处可用的，而老版本一旦判定「不支持回收站」
    /// 就整批只记录不删除，表现成清理从来不生效。现在的做法是
    /// 先试回收站，失败再看这个开关决定要不要退到直接删除。
    /// <para/>
    /// 真正拦住误删的不是这个开关，而是基线的身份绑定（机器名 + 用户 SID + 桌面路径）
    /// 和数量、体积熔断——它们在这条路径上一条都没放松。
    /// </remarks>
    [ObservableProperty] private bool _deleteWithoutRecycleBin;

    /// <summary>恢复桌面图标位置。</summary>
    [ObservableProperty] private bool _restoreIconPositions = true;

    /// <summary>
    /// 复原遮罩上像素场的格子边长。
    /// </summary>
    /// <remarks>
    /// 只影响铺满整屏的复原动画，不影响提醒条上的那个（那个听 UltraCode 设置页的）。
    /// <para/>
    /// 每帧开销和格子数成正比，而格子数是 <c>宽 × 高 ÷ 边长²</c>——<b>边长翻倍，开销降到四分之一</b>。
    /// 提醒条只有几百像素宽，边长 6 无所谓；整屏完全是另一回事：
    /// 4K 屏在边长 6 时每帧要算 23 万个格子。默认 10，教室那种老机器可以再往上调。
    /// </remarks>
    [ObservableProperty] private double _overlayCellSize = 10;

    /// <summary>
    /// 永不结束的进程名（不含 .exe，大小写不敏感）。
    /// </summary>
    /// <remarks>
    /// 这里只是<b>用户可加的额外白名单</b>。真正的硬保护写在
    /// <see cref="Services.ProcessGuard"/> 里，用户改不动——
    /// explorer、系统进程、ClassIsland 自己那些，任何情况下都不许碰。
    /// </remarks>
    [ObservableProperty] private List<string> _neverCloseProcesses =
        ["ToDesk", "SunloginClient", "TeamViewer", "BingWallpaper"];

    #endregion

    #region 读写

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Initialize(string pluginConfigFolder)
    {
        _configPath = Path.Combine(pluginConfigFolder, "settings.json");
        try
        {
            if (File.Exists(_configPath) &&
                JsonSerializer.Deserialize<ResetSettings>(File.ReadAllText(_configPath), JsonOptions) is { } loaded)
            {
                Current = loaded;
            }
        }
        catch (Exception)
        {
            // 配置坏了就用默认值，不要因为一个 json 拦住整个插件。
        }

        Current.MigrateLegacyAcrossTime();
        Current.PropertyChanged += (_, _) => Current.Save();
    }

    public void Save()
    {
        if (_configPath is null)
        {
            return;
        }

        try
        {
            var dir = Path.GetDirectoryName(_configPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(_configPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception)
        {
            // 存不上就算了，下次再存。
        }
    }

    #endregion

    #region 时刻解析

    /// <summary>把 <c>HH:mm</c> 解析成 <see cref="TimeSpan"/>。解析不了返回 null。</summary>
    public static TimeSpan? ParseTimePoint(string? text)
    {
        var parts = (text ?? string.Empty).Trim().Replace('：', ':').Split(':');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m) &&
            h is >= 0 and <= 23 && m is >= 0 and <= 59)
        {
            return new TimeSpan(h, m, 0);
        }

        return null;
    }

    private static List<TimeSpan> ParseAll(IEnumerable<string>? texts)
    {
        var result = new List<TimeSpan>();
        if (texts is null)
        {
            return result;
        }

        foreach (var text in texts)
        {
            if (ParseTimePoint(text) is { } point && !result.Contains(point))
            {
                result.Add(point);
            }
        }

        result.Sort();
        return result;
    }

    /// <summary>连堂中断时刻。两节课之间跨过其中任意一个，就不算连堂。</summary>
    [JsonIgnore]
    public List<TimeSpan> ForceRunAcrossPoints => ParseAll(ForceRunAcrossTimes);

    /// <summary>关机时刻。</summary>
    [JsonIgnore]
    public List<TimeSpan> AutoShutdownPoints => ParseAll(AutoShutdownTimes);

    #endregion

    /// <summary>
    /// 把旧版本的单个 <see cref="ForceRunAcrossTime"/> 迁移进
    /// <see cref="ForceRunAcrossTimes"/>。
    /// </summary>
    /// <remarks>
    /// 只在列表为空时才迁移，免得把用户后来配的多个时刻覆盖掉。
    /// 迁移完把旧字段清空，下次存盘就不再写它了。
    /// </remarks>
    private void MigrateLegacyAcrossTime()
    {
        if (string.IsNullOrWhiteSpace(ForceRunAcrossTime))
        {
            return;
        }

        if (ForceRunAcrossTimes.Count == 0 && ParseTimePoint(ForceRunAcrossTime) is not null)
        {
            ForceRunAcrossTimes = [ForceRunAcrossTime.Trim()];
        }

        ForceRunAcrossTime = null;
    }
}
