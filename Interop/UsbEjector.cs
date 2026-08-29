using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace ClassIsland.ClassReset.Interop;

/// <summary>一次弹出尝试的结果。</summary>
/// <param name="Drive">盘符，例如 <c>E:</c>。</param>
/// <param name="Label">卷标，没有就是空串。</param>
/// <param name="Success">是否成功弹出。</param>
/// <param name="Message">失败原因，成功时为空串。</param>
public readonly record struct EjectResult(string Drive, string Label, bool Success, string Message);

/// <summary>
/// 弹出可移动磁盘（U 盘 / SD 卡）。
/// </summary>
/// <remarks>
/// <b>只处理可移动卷，不碰任何别的 USB 设备。</b>
/// 键盘、鼠标、摄像头、加密狗这些根本不是卷，压根进不了枚举范围；
/// USB 移动硬盘通常被系统报成 Fixed，也会被排除在外。
/// <para/>
/// 弹出走的是标准的卷层四步：
/// <c>FSCTL_LOCK_VOLUME</c> → <c>FSCTL_DISMOUNT_VOLUME</c> →
/// <c>IOCTL_STORAGE_MEDIA_REMOVAL</c>（允许取出）→ <c>IOCTL_STORAGE_EJECT_MEDIA</c>。
/// 这条路径对可移动卷<b>不需要管理员权限</b>；如果卷里还有文件被打开，
/// 第一步加锁就会失败，此时直接放弃并报出是哪个盘——绝不强行 dismount，
/// 那样有丢数据的风险。
/// </remarks>
internal static class UsbEjector
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;

    private const uint FsctlLockVolume = 0x00090018;
    private const uint FsctlDismountVolume = 0x00090020;
    private const uint IoctlStorageMediaRemoval = 0x002D4804;
    private const uint IoctlStorageEjectMedia = 0x002D4808;

    private static readonly IntPtr InvalidHandle = new(-1);

    /// <summary>列出当前插着的可移动磁盘。</summary>
    public static List<DriveInfo> FindRemovableDrives()
    {
        var drives = new List<DriveInfo>();
        if (!OperatingSystem.IsWindows())
        {
            return drives;
        }

        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType == DriveType.Removable && drive.IsReady)
                    {
                        drives.Add(drive);
                    }
                }
                catch (Exception)
                {
                    // 单个盘读不出来就跳过，别影响其它盘。
                }
            }
        }
        catch (Exception)
        {
            // 枚举整体失败，返回空表。
        }

        return drives;
    }

    /// <summary>弹出所有可移动磁盘。</summary>
    public static List<EjectResult> EjectAll()
    {
        var results = new List<EjectResult>();
        foreach (var drive in FindRemovableDrives())
        {
            results.Add(Eject(drive));
        }

        return results;
    }

    /// <summary>弹出一个可移动磁盘。</summary>
    public static EjectResult Eject(DriveInfo drive)
    {
        var letter = drive.Name.TrimEnd('\\');
        var label = SafeLabel(drive);

        if (!OperatingSystem.IsWindows())
        {
            return new EjectResult(letter, label, false, "仅支持 Windows");
        }

        var handle = CreateFile($@"\\.\{letter}",
            GenericRead | GenericWrite,
            FileShareRead | FileShareWrite,
            IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

        if (handle == InvalidHandle)
        {
            return new EjectResult(letter, label, false,
                $"打不开卷（错误码 {Marshal.GetLastWin32Error()}）");
        }

        try
        {
            // 1. 加锁。有文件正开着就会失败——这时候就该放弃，强行 dismount 有丢数据的风险。
            if (!Control(handle, FsctlLockVolume))
            {
                return EjectFailure(letter, label, "盘里还有文件正在使用，没有弹出");
            }

            // 2. 卸载卷
            if (!Control(handle, FsctlDismountVolume))
            {
                return EjectFailure(letter, label, "卸载卷失败");
            }

            // 3. 允许取出介质
            var removal = new PreventMediaRemoval { Prevent = false };
            var size = Marshal.SizeOf<PreventMediaRemoval>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(removal, buffer, false);
                DeviceIoControl(handle, IoctlStorageMediaRemoval, buffer, (uint)size,
                    IntPtr.Zero, 0, out _, IntPtr.Zero);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            // 4. 真正弹出
            if (!Control(handle, IoctlStorageEjectMedia))
            {
                return EjectFailure(letter, label, "弹出指令失败");
            }

            return new EjectResult(letter, label, true, string.Empty);
        }
        catch (Exception ex)
        {
            return new EjectResult(letter, label, false, ex.Message);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>构造一条失败结果，纯粹为了让上面的返回语句短一点。</summary>
    private static EjectResult EjectFailure(string letter, string label, string message) =>
        new(letter, label, false, message);

    private static bool Control(IntPtr handle, uint code) =>
        DeviceIoControl(handle, code, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);

    private static string SafeLabel(DriveInfo drive)
    {
        try
        {
            return drive.VolumeLabel ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PreventMediaRemoval
    {
        [MarshalAs(UnmanagedType.U1)] public bool Prevent;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFile(string fileName, uint access, uint shareMode,
        IntPtr security, uint creationDisposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr device, uint controlCode,
        IntPtr inBuffer, uint inBufferSize, IntPtr outBuffer, uint outBufferSize,
        out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
