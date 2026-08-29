using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ClassIsland.ClassReset.Interop;

/// <summary>一个出现在任务栏上的窗口。</summary>
/// <param name="Handle">窗口句柄。</param>
/// <param name="Title">窗口标题。</param>
/// <param name="ProcessId">所属进程 ID。</param>
/// <param name="ProcessName">进程名（不含 .exe）。拿不到时为空串。</param>
public readonly record struct TaskbarWindow(IntPtr Handle, string Title, int ProcessId, string ProcessName)
{
    public override string ToString() =>
        string.IsNullOrEmpty(ProcessName) ? Title : $"{Title}（{ProcessName}）";
}

/// <summary>
/// 枚举「出现在任务栏上的窗口」。
/// </summary>
/// <remarks>
/// 判定规则用的是 Windows 上公认的那套「Alt+Tab 窗口」判据
/// （Raymond Chen 在 <i>Which windows appear in the Alt+Tab list?</i> 里写的）：
/// <list type="number">
/// <item>必须可见；</item>
/// <item>取 <c>GetAncestor(GA_ROOTOWNER)</c> 之后必须还是自己——也就是它是「最后一个活跃的根拥有者」，
///       这一条把对话框、子窗口、属主窗口的附属窗口全都归并掉；</item>
/// <item>有 <c>WS_EX_TOOLWINDOW</c> 的排除，除非同时有 <c>WS_EX_APPWINDOW</c>；</item>
/// <item>被 DWM cloak 掉的排除——UWP 应用挂起后窗口还在但不显示，
///       不排掉会把一堆根本没开的商店应用算进来；</item>
/// <item>标题为空的排除。</item>
/// </list>
/// 这套规则的意义在于：它只会命中「学生真的开着、任务栏上看得见」的软件，
/// 后台服务、托盘程序、其它插件的浮窗都不会被算进来。
/// </remarks>
internal static class WindowScanner
{
    private const int GwlExStyle = -20;
    private const int GwlStyle = -16;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExAppWindow = 0x00040000;
    private const int WsVisible = 0x10000000;

    private const uint GaRootOwner = 3;
    private const int DwmwaCloaked = 14;

    /// <summary>
    /// 列出当前所有出现在任务栏上的窗口。
    /// </summary>
    public static List<TaskbarWindow> Enumerate()
    {
        var result = new List<TaskbarWindow>();
        if (!OperatingSystem.IsWindows())
        {
            return result;
        }

        var seenProcesses = new Dictionary<int, string>();

        EnumWindows((hwnd, _) =>
        {
            if (!IsTaskbarWindow(hwnd))
            {
                return true;
            }

            var title = GetTitle(hwnd);
            if (title.Length == 0)
            {
                return true;
            }

            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0)
            {
                return true;
            }

            if (!seenProcesses.TryGetValue((int)pid, out var name))
            {
                name = SafeProcessName((int)pid);
                seenProcesses[(int)pid] = name;
            }

            result.Add(new TaskbarWindow(hwnd, title, (int)pid, name));
            return true;
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>
    /// 这个窗口会不会出现在任务栏上。
    /// </summary>
    private static bool IsTaskbarWindow(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd))
        {
            return false;
        }

        var style = GetWindowLong(hwnd, GwlStyle);
        if ((style & WsVisible) == 0)
        {
            return false;
        }

        // 只要「最后一个活跃的根拥有者」不是自己，就说明它是别人的附属窗口。
        if (GetAncestor(hwnd, GaRootOwner) != hwnd)
        {
            return false;
        }

        var exStyle = GetWindowLong(hwnd, GwlExStyle);
        var isToolWindow = (exStyle & WsExToolWindow) != 0;
        var isAppWindow = (exStyle & WsExAppWindow) != 0;
        if (isToolWindow && !isAppWindow)
        {
            return false;
        }

        // UWP 挂起后窗口仍在，但被 DWM cloak 了。不排掉会误算一堆没开的应用。
        if (IsCloaked(hwnd))
        {
            return false;
        }

        return true;
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        try
        {
            var hr = DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int));
            return hr == 0 && cloaked != 0;
        }
        catch (Exception)
        {
            // 老系统没有这个属性，当作没被 cloak。
            return false;
        }
    }

    private static string GetTitle(IntPtr hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(length + 1);
        GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString().Trim();
    }

    private static string SafeProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 当前是不是「就停在桌面上」——没有任何任务栏窗口。
    /// </summary>
    public static bool IsShowingDesktop() => Enumerate().Count == 0;

    /// <summary>给窗口发一条关闭请求（等价于点右上角的叉）。</summary>
    public static void RequestClose(IntPtr hwnd)
    {
        const uint WmClose = 0x0010;
        PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
    }

    #region P/Invoke

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    #endregion
}
