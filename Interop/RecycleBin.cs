using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace ClassIsland.ClassReset.Interop;

/// <summary>
/// 把文件/文件夹移到回收站。
/// </summary>
/// <remarks>
/// <see cref="Send"/> 用 <c>SHFileOperation</c> 加 <c>FOF_ALLOWUNDO</c>，
/// 也就是资源管理器里按 Delete 的效果，进了回收站还能还原——
/// 学生放在桌面上的可能是他自己要交的作业，默认路径必须是可还原的。
/// <para/>
/// <see cref="Delete"/> 是永久删除，只在调用方明确开了「直接删除」时才用。
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

            // SHFileOperation 报成功也可能什么都没删（有些位置会静默忽略），
            // 所以按「路径是不是真的不在了」来算，而不是信返回码。
            return list.Where(x => !File.Exists(x) && !Directory.Exists(x)).ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// 直接删除，不进回收站。
    /// </summary>
    /// <remarks>
    /// <b>永久删除，删了找不回来。</b>只在调用方明确开了「直接删除」时才走这里。
    /// 逐个删而不是批量：一项失败不该连累其余，而且要能如实报出删掉了几个。
    /// </remarks>
    /// <returns>确实已经不在了的路径。</returns>
    public static List<string> Delete(IEnumerable<string> paths)
    {
        var done = new List<string>();
        foreach (var path in paths.Where(x => !string.IsNullOrWhiteSpace(x))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                else if (File.Exists(path))
                {
                    // 只读属性会让 Delete 抛异常，先摘掉。
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // 单项失败不影响其余，最终按「还在不在」统一判定。
            }

            if (!File.Exists(path) && !Directory.Exists(path))
            {
                done.Add(path);
            }
        }

        return done;
    }

    /// <summary>
    /// <c>SHFILEOPSTRUCTW</c>。
    /// </summary>
    /// <remarks>
    /// <b>绝对不能写 <c>Pack = 1</c>。</b>这个结构体在头文件里用的是默认对齐，
    /// 强行按 1 字节紧排会让 <c>pFrom</c> 之后的每个字段偏移都错位：
    /// x64 上 <c>fFlags</c> 应该在 32、紧排会算到 28，
    /// 于是 shell 把结构体后半段当指针解引用，直接
    /// <c>AccessViolationException</c> 把整个进程带走——
    /// 而且这种异常是<b>接不住的</b>，外面包多少层 try/catch 都没用。
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
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
