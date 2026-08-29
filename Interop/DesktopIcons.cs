using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;

namespace ClassIsland.ClassReset.Interop;

/// <summary>桌面上一个图标的位置。</summary>
/// <param name="Name">显示名（就是图标下面那行字）。</param>
/// <param name="X">在桌面视图里的 X 坐标。</param>
/// <param name="Y">Y 坐标。</param>
public readonly record struct IconPosition(string Name, int X, int Y);

/// <summary>
/// 读写 Windows 桌面图标的位置。
/// </summary>
/// <remarks>
/// 走的是 Shell 的 COM 接口链：
/// <c>IShellWindows</c> → <c>FindWindowSW(SWC_DESKTOP)</c> → <c>IServiceProvider</c> →
/// <c>IShellBrowser</c> → <c>QueryActiveShellView</c> → <c>IFolderView</c>，
/// 然后用 <c>GetItemPosition</c> / <c>SelectAndPositionItems</c>。
/// <para/>
/// 之所以不用「往 explorer 的 SysListView32 发 LVM_GETITEMPOSITION」那套：
/// 那需要 <c>VirtualAllocEx</c> 往 explorer 进程里写内存，既要更高权限，
/// 在受保护的系统上还容易被安全软件拦。COM 这条路是文档化的、同权限就能用。
/// <para/>
/// 所有调用都必须在 <b>STA 线程</b>上执行，所以这里统一用 <see cref="RunSta"/> 包一层。
/// 任何一步失败都返回空/false，让上层降级成「只比对名字、不管位置」。
/// </remarks>
internal static class DesktopIcons
{
    private const int SwcDesktop = 8;
    private const int SwfoNeedDispatch = 1;
    // SVGIO_ALLVIEW = 0x2。写成 0 的话是 SVGIO_BACKGROUND，一个项目都枚举不到。
    private const uint SvgioAllView = 0x00000002;
    private const uint SvsiPositionItem = 0x00000080;
    private const uint SvsiDeselect = 0x00000000;
    private const uint ShgdnNormal = 0x0000;
    private const int DpiAwarenessPerMonitorV2 = -4;

    /// <summary>读取当前所有桌面图标的位置。失败返回空表。</summary>
    public static List<IconPosition> Read()
    {
        return RunSta(() =>
        {
            var list = new List<IconPosition>();
            if (!TryGetFolderView(out var view, out var folder) || view is null || folder is null)
            {
                return list;
            }

            try
            {
                view.ItemCount(SvgioAllView, out var count);
                for (var i = 0; i < count; i++)
                {
                    IntPtr pidl = IntPtr.Zero;
                    try
                    {
                        if (view.Item(i, out pidl) != 0 || pidl == IntPtr.Zero)
                        {
                            continue;
                        }

                        if (view.GetItemPosition(pidl, out var point) != 0)
                        {
                            continue;
                        }

                        var name = GetDisplayName(folder, pidl);
                        if (!string.IsNullOrEmpty(name))
                        {
                            list.Add(new IconPosition(name, point.X, point.Y));
                        }
                    }
                    finally
                    {
                        if (pidl != IntPtr.Zero)
                        {
                            Marshal.FreeCoTaskMem(pidl);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 读到一半失败就返回已经读到的部分。
            }
            finally
            {
                Release(view);
                // 注意：folder 来自 shell 的进程级单例，这里**不能** ReleaseComObject——
                // 把它从 RCW 缓存里摘掉之后，宿主 ClassIsland 里任何还握着它的代码
                // 下次一用就是 InvalidComObjectException。
            }

            return list;
        }) ?? [];
    }

    /// <summary>
    /// 把图标摆回记录下来的位置。
    /// </summary>
    /// <param name="wanted">名字 → 位置。</param>
    /// <returns>实际摆回去的图标数。</returns>
    public static int Restore(IReadOnlyDictionary<string, (int X, int Y)> wanted)
    {
        if (wanted.Count == 0)
        {
            return 0;
        }

        return RunSta(() =>
        {
            if (!TryGetFolderView(out var view, out var folder) || view is null || folder is null)
            {
                return 0;
            }

            var pidls = new List<IntPtr>();
            var points = new List<Point>();
            try
            {
                view.ItemCount(SvgioAllView, out var count);
                for (var i = 0; i < count; i++)
                {
                    if (view.Item(i, out var pidl) != 0 || pidl == IntPtr.Zero)
                    {
                        continue;
                    }

                    var name = GetDisplayName(folder, pidl);
                    if (name is not null && wanted.TryGetValue(name, out var target))
                    {
                        pidls.Add(pidl);
                        points.Add(new Point { X = target.X, Y = target.Y });
                    }
                    else
                    {
                        Marshal.FreeCoTaskMem(pidl);
                    }
                }

                if (pidls.Count == 0)
                {
                    return 0;
                }

                var pidlArray = pidls.ToArray();
                var pointArray = points.ToArray();
                view.SelectAndPositionItems((uint)pidlArray.Length, pidlArray, pointArray,
                    SvsiPositionItem | SvsiDeselect);
                return pidlArray.Length;
            }
            catch (Exception)
            {
                return 0;
            }
            finally
            {
                foreach (var pidl in pidls)
                {
                    Marshal.FreeCoTaskMem(pidl);
                }

                Release(view);
                // folder 是进程级单例，不释放。
            }
        });
    }

    /// <summary>桌面是不是开着「自动排列」。开着的话位置根本设不了。</summary>
    public static bool IsAutoArrangeOn()
    {
        return RunSta(() =>
        {
            if (!TryGetFolderView(out var view, out var folder) || view is null)
            {
                return false;
            }

            try
            {
                // FWF_AUTOARRANGE = 0x0001
                view.GetAutoArrange();
                return view.GetAutoArrange() == 0;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                Release(view);
                // folder 是进程级单例，不释放。
            }
        });
    }

    #region COM 链路

    private static bool TryGetFolderView(out IFolderView? view, out IShellFolder? folder)
    {
        view = null;
        folder = null;

        try
        {
            var shellWindowsType = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"));
            if (shellWindowsType is null)
            {
                return false;
            }

            var shellWindows = (IShellWindows?)Activator.CreateInstance(shellWindowsType);
            if (shellWindows is null)
            {
                return false;
            }

            try
            {
                object loc = 0; // CSIDL_DESKTOP
                object empty = 0;
                var disp = shellWindows.FindWindowSW(ref loc, ref empty, SwcDesktop, out _, SwfoNeedDispatch);
                if (disp is null)
                {
                    return false;
                }

                var provider = (IServiceProvider)disp;
                var serviceId = SidStopLevelBrowser;
                var browserIid = typeof(IShellBrowser).GUID;
                provider.QueryService(ref serviceId, ref browserIid, out var browserPtr);
                if (browserPtr == IntPtr.Zero)
                {
                    return false;
                }

                var browser = (IShellBrowser)Marshal.GetObjectForIUnknown(browserPtr);
                Marshal.Release(browserPtr);

                browser.QueryActiveShellView(out var shellView);
                view = shellView as IFolderView;
                if (view is null)
                {
                    return false;
                }

                var folderIid = typeof(IShellFolder).GUID;
                view.GetFolder(ref folderIid, out var folderObj);
                folder = folderObj as IShellFolder;
                return folder is not null;
            }
            finally
            {
                Release(shellWindows);
            }
        }
        catch (Exception)
        {
            // 任何一步挂掉都当作「拿不到桌面视图」，上层会降级。
            return false;
        }
    }

    private static string? GetDisplayName(IShellFolder folder, IntPtr pidl)
    {
        try
        {
            var strret = new StrRet();
            if (folder.GetDisplayNameOf(pidl, ShgdnNormal, ref strret) != 0)
            {
                return null;
            }

            // 交给 shlwapi 解联合体，别自己判 uType——三种分支的内存布局都不一样。
            var buffer = new StringBuilder(260);
            return StrRetToBuf(ref strret, pidl, buffer, (uint)buffer.Capacity) == 0
                ? buffer.ToString()
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            try
            {
                Marshal.ReleaseComObject(comObject);
            }
            catch (Exception)
            {
                // 释放失败无所谓，进程退出时会清掉。
            }
        }
    }

    /// <summary>Shell 的 COM 必须在 STA 上调，这里起一个专用线程跑。</summary>
    private static T? RunSta<T>(Func<T> action)
    {
        if (!OperatingSystem.IsWindows())
        {
            return default;
        }

        T? result = default;
        var thread = new Thread(() =>
        {
            var previousDpi = IntPtr.Zero;
            try
            {
                // 必须用**线程级** DPI 感知。插件跑在宿主进程里，
                // SetProcessDpiAwarenessContext 早就被宿主定死了，再调只会静默返回 FALSE，
                // 然后拿到的图标坐标是被缩放过的——实测 35 个图标里有 18 个会被压到边上。
                previousDpi = SetThreadDpiAwarenessContext(new IntPtr(DpiAwarenessPerMonitorV2));
                result = action();
            }
            catch (Exception)
            {
                result = default;
            }
            finally
            {
                if (previousDpi != IntPtr.Zero)
                {
                    SetThreadDpiAwarenessContext(previousDpi);
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        // 桌面项目多的时候会慢一点，给足时间但不能无限等——卡住 UI 线程更糟。
        thread.Join(TimeSpan.FromSeconds(10));
        return result;
    }

    private static Guid SidStopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [DllImport("shlwapi.dll", EntryPoint = "StrRetToBufW", CharSet = CharSet.Unicode)]
    private static extern int StrRetToBuf(ref StrRet str, IntPtr pidl, StringBuilder buffer, uint bufferSize);

    #endregion

    #region COM 接口声明

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    /// <summary>
    /// Shell 的 <c>STRRET</c>。
    /// </summary>
    /// <remarks>
    /// 这个结构里是个联合体，其中一个成员是 <c>char cStr[MAX_PATH]</c>——足足 260 字节。
    /// 如果按「uint + 指针」声明成 16 字节，shell 往里写 STRRET_CSTR 的时候就会越界，
    /// 直接踩坏栈，表现为**无法捕获的访问冲突、整个进程当场消失**（排查过一轮，就是这里）。
    /// 所以必须按完整大小声明，并且统一交给 <c>StrRetToBufW</c> 去解，
    /// 让它处理三种联合体分支，不要自己判 uType。
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, Size = 272)]
    private struct StrRet
    {
        public uint uType;
    }

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"),
     InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IShellWindows
    {
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object? FindWindowSW([In] ref object loc, [In] ref object root,
            int swClass, out int hwnd, int options);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        void QueryService([In] ref Guid service, [In] ref Guid riid, out IntPtr ppv);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void GetWindow(out IntPtr hwnd);
        void ContextSensitiveHelp(bool enterMode);
        void InsertMenusSB(IntPtr hmenuShared, IntPtr lpMenuWidths);
        void SetMenuSB(IntPtr hmenuShared, IntPtr holemenuRes, IntPtr hwndActiveObject);
        void RemoveMenusSB(IntPtr hmenuShared);
        void SetStatusTextSB(IntPtr pszStatusText);
        void EnableModelessSB(bool fEnable);
        void TranslateAcceleratorSB(IntPtr pmsg, ushort wID);
        void BrowseObject(IntPtr pidl, uint wFlags);
        void GetViewStateStream(uint grfMode, out IStream ppStrm);
        void GetControlWindow(uint id, out IntPtr phwnd);
        void SendControlMsg(uint id, uint uMsg, uint wParam, uint lParam, IntPtr pret);
        void QueryActiveShellView([MarshalAs(UnmanagedType.IUnknown)] out object ppshv);
        void OnViewWindowActive([MarshalAs(UnmanagedType.IUnknown)] object pshv);
        void SetToolbarItems(IntPtr lpButtons, uint nButtons, uint uFlags);
    }

    [ComImport, Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView
    {
        [PreserveSig] int GetCurrentViewMode(out uint viewMode);
        [PreserveSig] int SetCurrentViewMode(uint viewMode);
        [PreserveSig] int GetFolder([In] ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
        [PreserveSig] int Item(int itemIndex, out IntPtr ppidl);
        [PreserveSig] int ItemCount(uint flags, out int items);
        [PreserveSig] int Items(uint flags, [In] ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
        [PreserveSig] int GetSelectionMarkedItem(out int item);
        [PreserveSig] int GetFocusedItem(out int item);
        [PreserveSig] int GetItemPosition(IntPtr pidl, out Point point);
        [PreserveSig] int GetSpacing(out Point point);
        [PreserveSig] int GetDefaultSpacing(out Point point);
        [PreserveSig] int GetAutoArrange();
        [PreserveSig] int SelectItem(int item, uint flags);
        [PreserveSig] int SelectAndPositionItems(uint cidl,
            [In, MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl,
            [In, MarshalAs(UnmanagedType.LPArray)] Point[] apt, uint flags);
    }

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc,
            [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
            ref uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
        [PreserveSig] int EnumObjects(IntPtr hwnd, int grfFlags, out IntPtr enumIDList);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, [In] ref Guid riid, out IntPtr ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, [In] ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwndOwner, [In] ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributesOf(uint cidl, [In] IntPtr apidl, ref uint rgfInOut);
        [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [In] IntPtr apidl,
            [In] ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);
        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint uFlags, ref StrRet pName);
        [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl,
            [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
    }

    #endregion
}
