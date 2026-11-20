using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace ClassIsland.ClassReset.Services;

/// <summary>
/// 进程保护名单。判断某个进程<b>是否绝对不能碰</b>。
/// </summary>
/// <remarks>
/// 这是整个插件最需要保守的地方——宁可漏关一个软件，也绝不能杀错一个。
/// 这里的硬名单<b>用户改不掉</b>，设置里的白名单只能往上加、不能减。
/// <para/>
/// 判定分四层，任意一层命中就放过：
/// <list type="number">
/// <item>进程名在硬名单里（explorer、系统组件、输入法、远控、安全软件…）；</item>
/// <item>属于 Alesco 班级（进程名 AlescoClassroom，或可执行文件在 alesco-classroom-display 目录下）；</item>
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

    /// <summary>Alesco 班级主程序的进程名（不含 .exe）。</summary>
    private const string AlescoProcessName = "AlescoClassroom";

    /// <summary>
    /// Alesco 班级的安装目录名。主程序、守护程序（<c>guardian\guardian.exe</c>）都在它下面，
    /// 不论装在 Program Files 还是 <c>%LOCALAPPDATA%\Programs</c>。
    /// </summary>
    private const string AlescoInstallDirectoryName = "alesco-classroom-display";

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

        // 读路径放在用户白名单之前：Alesco 的判定也要用到它。
        var path = SafeGetPath(process);

        // 3. Alesco 班级（教室大屏 App 及其守护程序）
        if (IsAlesco(name, path))
        {
            reason = $"{name} 属于 Alesco 班级";
            return true;
        }

        // 4. 用户白名单
        if (extraAllowList is not null &&
            extraAllowList.Any(x => !string.IsNullOrWhiteSpace(x) &&
                                    string.Equals(x.Trim(), name, StringComparison.OrdinalIgnoreCase)))
        {
            reason = $"{name} 在用户白名单里";
            return true;
        }

        // 5. 路径在 Windows 目录下 = 系统自带
        if (path is not null)
        {
            if (!string.IsNullOrEmpty(WindowsDirectory) &&
                path.StartsWith(WindowsDirectory, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"{name} 是系统组件（位于 Windows 目录）";
                return true;
            }

            // 6. 和自己是同一个可执行文件（ClassIsland 的其它实例）
            if (SelfPath is not null && string.Equals(path, SelfPath, StringComparison.OrdinalIgnoreCase))
            {
                reason = "这是 ClassIsland 的另一个实例";
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 这个进程的子孙进程里有没有不能结束的。
    /// </summary>
    /// <remarks>
    /// <see cref="Process.Kill(bool)"/> 带上整棵进程树时，子孙进程<b>不会</b>再经过保护判定。
    /// 学生的程序可能拉起过受保护的进程（极端情况下 ClassIsland、Alesco 班级就是从它启动的），
    /// 所以结束整棵树之前先查一遍；有命中就只结束它自己。
    /// <para/>
    /// 父进程号可能已被复用，查出来的「子孙」偏多不偏少——只会让结果更保守。
    /// </remarks>
    public static bool HasProtectedDescendant(Process process, IEnumerable<string>? extraAllowList,
        out string reason)
    {
        reason = string.Empty;

        Dictionary<int, List<int>> children;
        try
        {
            children = SnapshotChildren();
        }
        catch (Exception)
        {
            // 查不到进程关系就当作有：宁可留下子进程，也不能误杀。
            reason = "无法读取子进程信息";
            return true;
        }

        var allow = extraAllowList as IList<string> ?? extraAllowList?.ToList();
        var visited = new HashSet<int> { process.Id };
        var queue = new Queue<int>();
        queue.Enqueue(process.Id);

        while (queue.Count > 0)
        {
            if (!children.TryGetValue(queue.Dequeue(), out var list))
            {
                continue;
            }

            foreach (var childId in list)
            {
                if (!visited.Add(childId))
                {
                    continue;
                }

                queue.Enqueue(childId);

                Process child;
                try
                {
                    child = Process.GetProcessById(childId);
                }
                catch (Exception)
                {
                    // 已经退出了。
                    continue;
                }

                using (child)
                {
                    if (IsProtected(child, allow, out var childReason))
                    {
                        reason = childReason;
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>父进程号 → 子进程号列表。</summary>
    private static Dictionary<int, List<int>> SnapshotChildren()
    {
        var map = new Dictionary<int, List<int>>();
        if (!OperatingSystem.IsWindows())
        {
            return map;
        }

        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == InvalidHandleValue)
        {
            throw new InvalidOperationException("CreateToolhelp32Snapshot 失败");
        }

        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry))
            {
                throw new InvalidOperationException("Process32First 失败");
            }

            do
            {
                var pid = (int)entry.th32ProcessID;
                var parent = (int)entry.th32ParentProcessID;
                if (pid != 0 && pid != parent)
                {
                    if (!map.TryGetValue(parent, out var list))
                    {
                        map[parent] = list = [];
                    }

                    list.Add(pid);
                }
            } while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return map;
    }

    /// <summary>
    /// 是否属于 Alesco 班级：进程名为 AlescoClassroom，或可执行文件路径中
    /// 含有 <c>alesco-classroom-display</c> 这一级目录。
    /// </summary>
    /// <remarks>
    /// 按目录名整段匹配（大小写不敏感），不按子串：<c>alesco-classroom-display-old</c>
    /// 这类目录不算。主程序、守护程序和它们拉起的子进程都在安装目录下，一并命中。
    /// </remarks>
    internal static bool IsAlesco(string? processName, string? executablePath)
    {
        var bareName = processName?.Trim();
        if (bareName is not null && bareName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            bareName = bareName[..^4];
        }

        if (string.Equals(bareName, AlescoProcessName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrEmpty(executablePath))
        {
            return false;
        }

        var segments = executablePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '\\', '/'],
            StringSplitOptions.RemoveEmptyEntries);

        // 最后一段是文件名本身，不参与目录匹配。
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (string.Equals(segments[i], AlescoInstallDirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 自检 <see cref="IsAlesco"/> 的判定。项目没有单独的测试工程，改动保护名单后跑一遍。
    /// </summary>
    /// <returns>不符合预期的用例；全部通过时为空。</returns>
    internal static List<string> SelfCheck()
    {
        var cases = new (string? Name, string? Path, bool Expected)[]
        {
            ("AlescoClassroom", null, true),
            ("alescoclassroom", @"D:\Other\AlescoClassroom.exe", true),
            ("AlescoClassroom.exe", null, true),
            ("guardian", @"C:\Program Files\alesco-classroom-display\guardian\guardian.exe", true),
            ("AlescoClassroom", @"C:\Program Files\alesco-classroom-display\AlescoClassroom.exe", true),
            ("guardian", @"C:\Users\t\AppData\Local\Programs\Alesco-Classroom-Display\guardian\guardian.exe", true),
            ("helper", "C:/Program Files/alesco-classroom-display/resources/helper.exe", true),
            ("guardian", @"C:\Program Files\alesco-classroom-display-old\guardian.exe", false),
            ("guardian", @"C:\Tools\guardian\guardian.exe", false),
            ("alesco-classroom-display", @"C:\Tools\alesco-classroom-display.exe", false),
            ("WINWORD", @"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE", false),
            (null, null, false),
        };

        var failures = new List<string>();
        foreach (var (name, path, expected) in cases)
        {
            if (IsAlesco(name, path) != expected)
            {
                failures.Add($"IsAlesco({name ?? "null"}, {path ?? "null"}) 应为 {expected}");
            }
        }

        return failures;
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
            var path = process.MainModule?.FileName;
            if (!string.IsNullOrEmpty(path))
            {
                return path;
            }
        }
        catch (Exception)
        {
            // 拿不到路径很常见（权限不足、64/32 位不匹配），不是错误。
        }

        // MainModule 要读进程内存，以管理员身份运行的程序、位数不同的程序都会失败。
        // 退一步只要「受限查询」权限，绝大多数进程都能拿到完整路径——
        // 路径读得到，保护名单才判得准（例如 Alesco 的守护程序 guardian.exe）。
        return QueryImagePath(process);
    }

    private static string? QueryImagePath(Process process)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var handle = IntPtr.Zero;
        try
        {
            handle = OpenProcess(ProcessQueryLimitedInformation, false, process.Id);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) && size > 0
                ? buffer.ToString(0, size)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                CloseHandle(handle);
            }
        }
    }

    #region Win32

    private const uint ProcessQueryLimitedInformation = 0x1000;

    private const uint Th32csSnapProcess = 0x00000002;

    private static readonly IntPtr InvalidHandleValue = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder exeName,
        ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    #endregion
}
