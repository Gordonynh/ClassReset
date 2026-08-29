using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;
using ClassIsland.PluginShared;

namespace ClassIsland.ClassReset.Interop;

/// <summary>
/// 把窗口按在最顶层。
/// </summary>
/// <remarks>
/// 只设 <see cref="Window.Topmost"/> 是不够的：全屏应用、别的置顶窗口、
/// 甚至资源管理器重启，都会把窗口挤到下面去。所以这里额外做两件事：
/// <list type="number">
/// <item>加上 <c>WS_EX_TOOLWINDOW</c> 和 <c>WS_EX_NOACTIVATE</c>，
///       让它不进 Alt+Tab、不抢焦点——不抢焦点本身就减少了被系统降层的机会；</item>
/// <item>起一个秒级定时器反复调用 <c>SetWindowPos(HWND_TOPMOST)</c> 把它顶回去。</item>
/// </list>
/// 这套组合在 Windows 上足以稳定盖住任务栏和普通全屏窗口，而且全是文档化的公开 API，
/// 不需要改系统设置，也不用动别的进程。
/// </remarks>
public sealed class TopmostEnforcer : IDisposable
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNoTopmost = new(-2);

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    private readonly Window _window;
    private readonly DispatcherTimer _timer;
    private readonly bool _preventActivation;
    private readonly bool _exclusive;
    private IntPtr _handle;

    /// <summary>上一次让位之后有没有恢复置顶。避免每个 tick 都重复调同一个 SetWindowPos。</summary>
    private bool _yielded;

    /// <param name="window">要维持置顶的窗口。</param>
    /// <param name="interval">重申置顶的间隔。</param>
    /// <param name="preventActivation">
    /// 是否加 <c>WS_EX_NOACTIVATE</c>。悬浮钮要（不抢别人的焦点），
    /// 但菜单窗口**不能**加——它靠失焦来自动关闭，拿不到焦点就永远关不掉。
    /// </param>
    /// <param name="exclusive">
    /// 这个窗口是不是「独占的全屏窗口」（复原倒计时、整屏值日提醒）。
    /// 独占窗口开着的时候，其它窗口会主动让位，不再跟它抢置顶。
    /// </param>
    public TopmostEnforcer(Window window, TimeSpan? interval = null, bool preventActivation = true,
        bool exclusive = false)
    {
        _window = window;
        _preventActivation = preventActivation;
        _exclusive = exclusive;
        _timer = new DispatcherTimer { Interval = interval ?? TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Reassert();
    }

    /// <summary>
    /// 开始维持置顶。窗口必须已经显示出来，否则拿不到句柄。
    /// </summary>
    public void Attach()
    {
        if (!OperatingSystem.IsWindows())
        {
            // 非 Windows 平台就只靠 Avalonia 自己的 Topmost。
            return;
        }

        _handle = _window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var style = GetWindowLong(_handle, GwlExStyle) | WsExToolWindow;
            if (_preventActivation)
            {
                style |= WsExNoActivate;
            }

            SetWindowLong(_handle, GwlExStyle, style);
        }
        catch (Exception)
        {
            // 拿不到扩展样式不影响后面的置顶重申。
        }

        Reassert();
        _timer.Start();
    }

    /// <summary>
    /// 立刻把窗口顶回最上层。位置和大小都不动，也不抢焦点。
    /// </summary>
    /// <remarks>
    /// <b>有独占全屏窗口开着时会主动让位</b>，把自己降到普通层。
    /// 不这么做的话两边的定时器会互相把对方顶下去，画面一直闪；
    /// 更要命的是悬浮钮一旦压在复原倒计时上面，用户点屏幕想取消，
    /// 点到的会是「抽一个人」。
    /// </remarks>
    public void Reassert()
    {
        if (!OperatingSystem.IsWindows() || _handle == IntPtr.Zero)
        {
            return;
        }

        var yield = !_exclusive && ExclusiveOverlay.IsActive;
        if (yield && _yielded)
        {
            // 已经让过位了，不用每个 tick 重复调。
            return;
        }

        try
        {
            SetWindowPos(_handle, yield ? HwndNoTopmost : HwndTopmost, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoActivate | (yield ? 0 : SwpShowWindow));
            _yielded = yield;
        }
        catch (Exception)
        {
            // 单次失败不要紧，下一个 tick 会再来一次。
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _handle = IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
