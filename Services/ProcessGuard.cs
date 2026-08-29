using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace ClassIsland.ClassReset.Services;

/// <summary>
/// 进程保护名单。判断某个进程<b>是否绝对不能碰</b>。
/// </summary>
/// <remarks>
/// 这是整个插件最需要保守的地方——宁可漏关一个软件，也绝不能杀错一个。
/// 这里的硬名单<b>用户改不掉</b>，设置里的白名单只能往上加、不能减。
/// <para/>
/// 判定分三层，任意一层命中就放过：
/// <list type="number">
/// <item>进程名在硬名单里（explorer、系统组件、输入法、远控、安全软件…）；</item>
/// <item>进程可执行文件在 Windows 目录下（系统自带的东西一律不碰）；</item>
/// <item>就是 ClassIsland 自己，或者和自己同一个可执行文件。</item>
/// </list>
/// </remarks>
internal static class ProcessGuard
{
    /// <summary>
    /// 任何情况下都不结束的进程名（不含 .exe，小写比较）。
    /// </summary>
    private static readonly HashSet<string> HardProtected = new(StringComparer.OrdinalIgnoreCase)
    {
        // 外壳与系统核心——杀了会让系统进入很难恢复的状态
        "explorer", "dwm", "csrss", "wininit", "winlogon", "services", "lsass", "smss",
        "svchost", "fontdrvhost", "sihost", "taskhostw", "ctfmon", "runtimebroker",
        "shellexperiencehost", "startmenuexperiencehost", "searchhost", "searchapp",
        "textinputhost", "applicationframehost", "systemsettings", "userinit",
        "logonui", "consent", "dllhost", "conhost", "audiodg", "spoolsv",
        "registry", "memory compression", "idle", "system", "wudfhost",
        "securityhealthservice", "securityhealthsystray", "msmpeng", "nissrv",

        // 输入法：杀了就打不了字了
        "sogouinput", "sogoucloud", "sgtool", "qqpinyin", "baidupinyin", "wubi",
        "chsime", "imebroker", "inputpersonalization",

        // 远程控制/课堂管理：老师可能正靠它连着这台机器
        "todesk", "sunloginclient", "teamviewer", "anydesk", "rustdesk",
        "mstsc", "rdpclip",

        // 任务管理器本身——万一老师正开着在看
        "taskmgr",
    };

    /// <summary>Windows 目录，用来判断「是不是系统自带的程序」。</summary>
    private static readonly string WindowsDirectory =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private static readonly int SelfPid = Environment.ProcessId;

    private static readonly string? SelfPath = SafeGetPath(Process.GetCurrentProcess());

    /// <summary>
    /// 这个进程是否绝对不能结束。
    /// </summary>
    /// <param name="process">要判断的进程。</param>
    /// <param name="extraAllowList">用户在设置里额外加的白名单（进程名，不含 .exe）。</param>
    /// <param name="reason">命中原因，用于写日志和在界面上解释。</param>
    public static bool IsProtected(Process process, IEnumerable<string>? extraAllowList, out string reason)
    {
        reason = string.Empty;

        // 1. 自己
        if (process.Id == SelfPid)
        {
            reason = "这是 ClassIsland 自己";
            return true;
        }

        string name;
        try
        {
            name = process.ProcessName;
        }
        catch (Exception)
        {
            // 拿不到名字说明进程已经没了或者没权限，一律当作不能碰。
            reason = "无法读取进程信息";
            return true;
        }

        // 2. 硬名单
        if (HardProtected.Contains(name))
        {
            reason = $"{name} 在系统保护名单里";
            return true;
        }

        // 3. 用户白名单
        if (extraAllowList is not null &&
            extraAllowList.Any(x => !string.IsNullOrWhiteSpace(x) &&
                                    string.Equals(x.Trim(), name, StringComparison.OrdinalIgnoreCase)))
        {
            reason = $"{name} 在用户白名单里";
            return true;
        }

        // 4. 路径在 Windows 目录下 = 系统自带
        var path = SafeGetPath(process);
        if (path is not null)
        {
            if (!string.IsNullOrEmpty(WindowsDirectory) &&
                path.StartsWith(WindowsDirectory, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"{name} 是系统组件（位于 Windows 目录）";
                return true;
            }

            // 5. 和自己是同一个可执行文件（ClassIsland 的其它实例）
            if (SelfPath is not null && string.Equals(path, SelfPath, StringComparison.OrdinalIgnoreCase))
            {
                reason = "这是 ClassIsland 的另一个实例";
                return true;
            }
        }

        return false;
    }

    /// <summary>explorer 特判：它的窗口可以关，但进程绝对不能结束。</summary>
    public static bool IsExplorer(Process process)
    {
        try
        {
            return string.Equals(process.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? SafeGetPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception)
        {
            // 拿不到路径很常见（权限不足、64/32 位不匹配），不是错误。
            return null;
        }
    }
}
