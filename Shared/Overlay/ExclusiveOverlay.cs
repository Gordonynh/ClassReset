using System;
using System.Threading;

namespace ClassIsland.PluginShared;

/// <summary>
/// 「现在有一个独占的全屏窗口」这个事实的跨插件信号。
/// </summary>
/// <remarks>
/// <b>要解决的问题：</b>课后自动复原的全屏倒计时、值日的整屏提醒，
/// 和随机抽选的悬浮钮都在用定时器反复调 <c>SetWindowPos(HWND_TOPMOST)</c> 把自己顶回去。
/// 谁的定时器刚跳过谁就在上面，于是互相抢，画面闪；更糟的是悬浮钮一旦压在倒计时上面，
/// <b>点屏幕想取消，点到的是「抽一个人」</b>。
/// <para/>
/// 三个插件各自在独立的 <c>PluginLoadContext</c> 里，静态字段不通用，所以用一个
/// <b>内核命名事件</b>来传这个信号——名字一样就是同一个对象，跨程序集甚至跨进程都认。
/// <para/>
/// <b>为什么用「持有句柄」而不是「置位/复位」：</b>句柄全部关掉时内核对象自动消失，
/// 所以哪怕持有方崩了、没走到清理代码，信号也会自己失效，不会把悬浮钮永久压住。
/// </remarks>
public static class ExclusiveOverlay
{
    // Local\ 前缀 = 当前登录会话内可见。多用户登录时互不干扰。
    private const string EventName = @"Local\ClassIsland.Gordon.ExclusiveOverlay";

    private static readonly object Gate = new();
    private static EventWaitHandle? _held;
    private static int _depth;

    /// <summary>当前是不是有独占全屏窗口开着。</summary>
    /// <remarks>每次调用都去问内核，不缓存——持有方可能在别的程序集里。</remarks>
    public static bool IsActive
    {
        get
        {
            try
            {
                if (!EventWaitHandle.TryOpenExisting(EventName, out var handle))
                {
                    return false;
                }

                using (handle)
                {
                    return handle.WaitOne(0);
                }
            }
            catch (Exception)
            {
                // 拿不到就当没有，宁可让悬浮钮照常置顶，也不要因为信号出问题把它永久压住。
                return false;
            }
        }
    }

    /// <summary>
    /// 声明「我是独占全屏窗口」。用完必须 Dispose。
    /// </summary>
    /// <remarks>支持嵌套：内层释放不会提前撤掉信号，最外层释放才真的撤。</remarks>
    public static IDisposable Acquire()
    {
        lock (Gate)
        {
            if (_depth++ == 0)
            {
                try
                {
                    _held = new EventWaitHandle(true, EventResetMode.ManualReset, EventName);
                }
                catch (Exception)
                {
                    // 建不出来就退化成「没有独占」，各窗口各自置顶，回到老行为。
                    _held = null;
                }
            }

            return new Release();
        }
    }

    private static void ReleaseOne()
    {
        lock (Gate)
        {
            if (_depth == 0)
            {
                return;
            }

            if (--_depth == 0)
            {
                _held?.Dispose();
                _held = null;
            }
        }
    }

    private sealed class Release : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done)
            {
                return;
            }

            _done = true;
            ReleaseOne();
        }
    }
}
