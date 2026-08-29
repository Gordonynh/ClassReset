using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace ClassIsland.ClassReset.Interop;

/// <summary>
/// 把文件/文件夹移到回收站。
/// </summary>
/// <remarks>
/// 用 <c>SHFileOperation</c> 加 <c>FOF_ALLOWUNDO</c>，也就是资源管理器里按 Delete 的效果。
/// <b>刻意不用 <c>File.Delete</c></b>——学生放在桌面上的东西可能是他自己要交的作业，
/// 直接永久删除是不可接受的。进了回收站还能还原。
/// <para/>
/// 同时带 <c>FOF_NOCONFIRMATION</c> 和 <c>FOF_SILENT</c>：这是无人值守的清理，
/// 不能弹确认框卡住流程。
/// </remarks>
internal static class RecycleBin
{
    private const uint FoDelete = 0x0003;

    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;
    private const ushort FofNoConfirmMkDir = 0x0200;

    /// <summary>
    /// 把这些路径移到回收站。
    /// </summary>
    /// <returns>成功移走的路径。</returns>
    public static List<string> Send(IEnumerable<string> paths)
    {
        var list = paths.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0 || !OperatingSystem.IsWindows())
        {
            return [];
        }

        // SHFileOperation 要的是「双 \0 结尾」的多字符串：每个路径 \0 分隔，整体再补一个 \0。
        var packed = string.Join('\0', list) + '\0' + '\0';

        var op = new ShFileOpStruct
        {
            wFunc = FoDelete,
            pFrom = packed,
            fFlags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi | FofNoConfirmMkDir
        };

        try
        {
            var code = SHFileOperation(ref op);
            if (code != 0 || op.fAnyOperationsAborted)
            {
                return [];
            }

            return list;
        }
        catch (Exception)
        {
            return [];
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHFileOperation(ref ShFileOpStruct fileOp);
}
