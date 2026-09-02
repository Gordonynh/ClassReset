using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using ClassIsland.ClassReset.Interop;
using ClassIsland.ClassReset.Models;

namespace ClassIsland.ClassReset.Services;

/// <summary>桌面和基线的差异。</summary>
public sealed class DesktopDiff
{
    /// <summary>基线里没有、现在多出来的项目（完整路径）。</summary>
    public List<string> AddedPaths { get; } = [];

    /// <summary>基线里有、现在不见了的项目名。</summary>
    public List<string> Missing { get; } = [];

    /// <summary>位置和基线对不上的图标名。</summary>
    public List<string> Moved { get; } = [];

    public bool HasChanges => AddedPaths.Count > 0 || Missing.Count > 0 || Moved.Count > 0;

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (AddedPaths.Count > 0)
            {
                parts.Add($"新增 {AddedPaths.Count} 项");
            }

            if (Missing.Count > 0)
            {
                parts.Add($"缺少 {Missing.Count} 项");
            }

            if (Moved.Count > 0)
            {
                parts.Add($"{Moved.Count} 个图标移位");
            }

            return parts.Count == 0 ? "与基线一致" : string.Join("、", parts);
        }
    }
}

/// <summary>
/// 桌面布局的录入、比对与复原。
/// </summary>
/// <remarks>
/// <b>这个类里的每一道保险都不是过度设计。</b>本机的桌面是
/// <c>C:\Mac\Home\Desktop</c>——一个指向 Mac 宿主机的 Parallels 共享目录，
/// 实测里面有约 217 GB / 22.8 万个文件，而且这个路径<b>不支持回收站</b>。
/// 只要在共享盘短暂掉线时录了基线（此时枚举得到 0 项），等盘回来，
/// 桌面上所有东西都会被判成「新增」——没有下面这些熔断，
/// 后果就是把老师 Mac 上的资料静默清空。
/// <para/>
/// 所以删除这条路径上，任何一项不确定都直接放弃并如实上报，绝不「尽力而为」。
/// </remarks>
internal sealed class DesktopLayoutService
{
    /// <summary>图标位置允许的误差（像素）。小幅抖动不算「被挪动」。</summary>
    private const int PositionTolerance = 4;

    /// <summary>数量熔断的硬上限。用户设得再高也不会超过这个数。</summary>
    private const int HardMaxItemsToRecycle = 100;

    /// <summary>单个文件超过这个大小就不动它。</summary>
    private const long MaxFileSizeBytes = 200L * 1024 * 1024;

    /// <summary>文件夹里的文件超过这么多个就不动它。</summary>
    private const int MaxFolderFiles = 200;

    /// <summary>文件夹总大小超过这么多就不动它。</summary>
    private const long MaxFolderBytes = 500L * 1024 * 1024;

    private readonly string _snapshotPath;

    public DesktopLayoutService(string pluginConfigFolder)
    {
        _snapshotPath = Path.Combine(pluginConfigFolder, "桌面基线.json");
        Snapshot = DesktopSnapshot.Load(_snapshotPath);
    }

    public DesktopSnapshot Snapshot { get; private set; }

    public bool HasBaseline => !Snapshot.IsEmpty;

    public static string UserDesktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    public static string PublicDesktop => Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

    /// <summary>上一次比对/复原时被安全闸拦下的原因。为空表示没被拦。</summary>
    public string? LastSafetyBlock { get; private set; }

    /// <summary>
    /// 桌面所在位置到底能不能用回收站。
    /// </summary>
    /// <remarks>
    /// 设置页要如实告诉用户「会不会真的删」，就得走和执行时同一套判断。
    /// <b>不能拿 <c>DriveInfo.DriveType</c> 顶替</b>——本机桌面是
    /// <c>C:\Mac\Home\Desktop</c>，DriveType 报的是 <c>Fixed</c>，
    /// 照它显示会给出「能删」这个错误结论。
    /// </remarks>
    public static bool CanRecycleOnDesktop => CanUseRecycleBin(UserDesktop);

    #region 录入基线

    /// <summary>
    /// 把当前桌面状态录成新的基线。
    /// </summary>
    /// <returns>录入失败时返回 null，并在 <see cref="LastSafetyBlock"/> 里说明原因。</returns>
    public DesktopSnapshot? Capture()
    {
        LastSafetyBlock = null;

        var entries = EnumerateEntries();

        // 共享盘掉线时枚举会得到空表。这时候录基线是灾难的起点——
        // 盘回来之后所有东西都会被当成「新增」。宁可不录。
        if (entries.Count == 0)
        {
            LastSafetyBlock = "读不到桌面内容（共享盘可能未挂载），未录入基线";
            return null;
        }

        var snapshot = new DesktopSnapshot
        {
            CapturedAt = DateTime.Now,
            MachineName = Environment.MachineName,
            UserSid = SafeUserSid(),
            UserDesktopPath = UserDesktop,
            PublicDesktopPath = PublicDesktop,
            Entries = entries
        };

        var positions = DesktopIcons.Read();
        snapshot.HasIconPositions = positions.Count > 0;
        foreach (var p in positions)
        {
            snapshot.IconPositions[p.Name] = new IconPoint { X = p.X, Y = p.Y };
        }

        snapshot.Save(_snapshotPath);
        Snapshot = snapshot;
        return snapshot;
    }

    #endregion

    #region 比对

    /// <summary>把当前桌面和基线比一比。</summary>
    public DesktopDiff Compare()
    {
        var diff = new DesktopDiff();
        if (!HasBaseline || !PassesIdentityCheck(out _))
        {
            // 没录过基线、或者基线不是这台机器/这个用户的，无从比较。
            return diff;
        }

        var current = EnumerateEntries();
        if (current.Count == 0)
        {
            // 读不到任何东西，多半是盘掉了，不是「桌面被清空了」。
            return diff;
        }

        var baseline = Snapshot.Entries.Select(x => (x.Name, x.IsPublic)).ToHashSet();
        foreach (var entry in current)
        {
            if (!baseline.Contains((entry.Name, entry.IsPublic)))
            {
                var root = entry.IsPublic ? PublicDesktop : UserDesktop;
                diff.AddedPaths.Add(Path.Combine(root, entry.Name));
            }
        }

        var currentNames = current.Select(x => (x.Name, x.IsPublic)).ToHashSet();
        foreach (var entry in Snapshot.Entries)
        {
            if (!currentNames.Contains((entry.Name, entry.IsPublic)))
            {
                diff.Missing.Add(entry.Name);
            }
        }

        if (Snapshot.HasIconPositions)
        {
            foreach (var p in DesktopIcons.Read())
            {
                if (Snapshot.IconPositions.TryGetValue(p.Name, out var want) &&
                    (Math.Abs(want.X - p.X) > PositionTolerance || Math.Abs(want.Y - p.Y) > PositionTolerance))
                {
                    diff.Moved.Add(p.Name);
                }
            }
        }

        return diff;
    }

    #endregion

    #region 复原

    /// <summary>
    /// 复原桌面：新增的项目丢进回收站，图标位置摆回基线。
    /// </summary>
    public List<string> Restore(ResetSettings settings)
    {
        var actions = new List<string>();
        LastSafetyBlock = null;

        if (!HasBaseline)
        {
            return actions;
        }

        if (!PassesIdentityCheck(out var why))
        {
            LastSafetyBlock = why;
            actions.Add($"桌面操作已跳过：{why}");
            return actions;
        }

        var diff = Compare();

        if (settings.RecycleNewDesktopItems && diff.AddedPaths.Count > 0)
        {
            actions.AddRange(RecycleAdded(diff.AddedPaths, settings));
        }

        if (settings.RestoreIconPositions && Snapshot.HasIconPositions)
        {
            var wanted = Snapshot.IconPositions.ToDictionary(kv => kv.Key, kv => (kv.Value.X, kv.Value.Y));
            var restored = DesktopIcons.Restore(wanted);
            if (restored > 0)
            {
                actions.Add($"已恢复 {restored} 个图标位置");
            }
        }

        return actions;
    }

    /// <summary>
    /// 把新增项目移到回收站。层层设闸，任何一条不满足就只上报不动手。
    /// </summary>
    /// <remarks>
    /// <b>数量熔断曾经算错过，导致这个功能实际上从没删过东西。</b>
    /// 老版本的上限是 <c>Math.Min(10, Math.Max(1, 基线项目数 / 4))</c>；
    /// 教室那台机器的基线有 11 项，<c>11 / 4 = 2</c>，
    /// 学生只要多放 3 个东西就整批「只上报不删除」。
    /// 熔断的本意是防「基线坏了导致误删一整个桌面」，那是个绝对量的问题，
    /// 和基线本身有多少项没有关系，所以现在直接用
    /// <see cref="ResetSettings.MaxRecycleItems"/> 这个绝对值。
    /// </remarks>
    private List<string> RecycleAdded(List<string> added, ResetSettings settings)
    {
        var actions = new List<string>();

        // 闸 1：数量熔断。一次冒出一大堆「新增」，多半是基线出了问题，不是学生真放了这么多。
        var limit = Math.Clamp(settings.MaxRecycleItems, 1, HardMaxItemsToRecycle);
        if (added.Count > limit)
        {
            LastSafetyBlock = $"新增 {added.Count} 项，超过上限 {limit}";
            actions.Add($"新增 {added.Count} 项，超过上限 {limit}，仅记录不删除");
            return actions;
        }

        // 闸 2：回收站不可用，而又没开「直接删除」时才拦。
        //
        // 老版本这里是「探测到不支持回收站就整批不删」，判据还是启发式的
        // （解析真实路径 + 看盘符类型）。学校那台明明有回收站却被判成不支持，
        // 结果清理从来不生效。现在把探测降级成一条提示：
        // 真正的做法是下面先试回收站，失败了再按开关决定退不退到直接删除。
        if (!settings.DeleteWithoutRecycleBin && !CanUseRecycleBin(UserDesktop))
        {
            LastSafetyBlock = "桌面所在位置可能不支持回收站，且未开启直接删除";
            actions.Add($"新增 {added.Count} 项，未删除（该位置可能不支持回收站，可在设置里开启直接删除）");
            return actions;
        }

        var safe = new List<string>();
        var skipped = new List<string>();
        foreach (var path in added)
        {
            var name = Path.GetFileName(path);

            if (Directory.Exists(path))
            {
                // 闸 3：文件夹单独一个开关，而且体量必须先量过再动。
                if (!settings.RecycleNewDesktopFolders)
                {
                    skipped.Add($"{name}（文件夹，未开启清理）");
                    continue;
                }

                if (!IsFolderSmallEnough(path, out var why))
                {
                    skipped.Add($"{name}（{why}）");
                    continue;
                }

                safe.Add(path);
                continue;
            }

            if (!File.Exists(path))
            {
                continue;
            }

            // 闸 4：大文件不动。快捷方式（.lnk）本身就是几 KB 的文件，走的也是这条路径。
            try
            {
                var size = new FileInfo(path).Length;
                if (size > MaxFileSizeBytes)
                {
                    skipped.Add($"{name}（{size / 1024 / 1024} MB，超出上限）");
                    continue;
                }
            }
            catch (Exception)
            {
                skipped.Add($"{name}（无法读取大小）");
                continue;
            }

            safe.Add(path);
        }

        if (skipped.Count > 0)
        {
            actions.Add($"跳过 {skipped.Count} 项：{string.Join("、", skipped.Take(5))}");
        }

        if (safe.Count == 0)
        {
            return actions;
        }

        // 开了「直接删除」就不绕回收站；否则先试回收站，失败的那部分再看要不要退到直接删除。
        List<string> moved;
        string how;
        if (settings.DeleteWithoutRecycleBin)
        {
            moved = RecycleBin.Delete(safe);
            how = "已删除";
        }
        else
        {
            moved = RecycleBin.Send(safe);
            how = "已移入回收站";
        }

        if (moved.Count == safe.Count)
        {
            actions.Add($"{how} {moved.Count} 项");
            return actions;
        }

        // 没全部成功。公共桌面要管理员权限，这是最常见的原因，得说出来而不是静默吞掉。
        var failed = safe.Where(x => !moved.Contains(x)).ToList();
        var needsAdmin = !string.IsNullOrEmpty(PublicDesktop) &&
                         failed.Any(x => x.StartsWith(PublicDesktop, StringComparison.OrdinalIgnoreCase));
        var reason = needsAdmin ? "（公共桌面需要管理员权限）" : string.Empty;

        // 回收站失败的那部分，若允许直接删除就再补一刀。
        // 这才是「回收站用不了」的正确处理：退到直接删除，而不是整批放弃。
        if (!settings.DeleteWithoutRecycleBin || failed.Count == 0)
        {
            actions.Add(moved.Count > 0
                ? $"{how} {moved.Count} 项，失败 {failed.Count} 项{reason}"
                : $"处理失败 {failed.Count} 项{reason}");
            if (needsAdmin)
            {
                LastSafetyBlock = "公共桌面上的项目需要管理员权限才能删除";
            }

            return actions;
        }

        var forced = RecycleBin.Delete(failed);
        actions.Add($"{how} {moved.Count} 项；回收站不可用，另直接删除 {forced.Count} 项");
        if (forced.Count < failed.Count)
        {
            LastSafetyBlock = needsAdmin
                ? "公共桌面上的项目需要管理员权限才能删除"
                : $"有 {failed.Count - forced.Count} 项删不掉";
            actions.Add($"仍有 {failed.Count - forced.Count} 项未能删除{reason}");
        }

        return actions;
    }

    /// <summary>
    /// 文件夹小到可以安全丢进回收站吗。
    /// </summary>
    /// <remarks>
    /// 递归数一遍文件数和总字节。任何一项超限、或者中途读不动，都判为「不能动」——
    /// 文件夹的体量不可预知，宁可漏做。
    /// </remarks>
    private static bool IsFolderSmallEnough(string path, out string why)
    {
        why = string.Empty;
        try
        {
            var files = 0;
            var bytes = 0L;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                files++;
                if (files > MaxFolderFiles)
                {
                    why = $"超过 {MaxFolderFiles} 个文件";
                    return false;
                }

                bytes += new FileInfo(file).Length;
                if (bytes > MaxFolderBytes)
                {
                    why = $"超过 {MaxFolderBytes / 1024 / 1024} MB";
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            why = $"无法读取:{ex.GetType().Name}";
            return false;
        }
    }

    #endregion

    #region 安全检查

    /// <summary>
    /// 基线是不是这台机器、这个用户、这些路径录的。
    /// </summary>
    /// <remarks>
    /// 换了机器或换了用户还拿旧基线去删东西，等于拿别人的清单删自己的文件。
    /// 老基线（没记这些字段）一律放行，但只影响比对，删除那边还有数量熔断兜着。
    /// </remarks>
    private bool PassesIdentityCheck(out string reason)
    {
        reason = string.Empty;

        if (!string.IsNullOrEmpty(Snapshot.MachineName) &&
            !string.Equals(Snapshot.MachineName, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            reason = "基线来自另一台电脑";
            return false;
        }

        var sid = SafeUserSid();
        if (!string.IsNullOrEmpty(Snapshot.UserSid) && !string.IsNullOrEmpty(sid) &&
            !string.Equals(Snapshot.UserSid, sid, StringComparison.OrdinalIgnoreCase))
        {
            reason = "基线来自另一个 Windows 用户";
            return false;
        }

        if (!string.IsNullOrEmpty(Snapshot.UserDesktopPath) &&
            !string.Equals(Snapshot.UserDesktopPath, UserDesktop, StringComparison.OrdinalIgnoreCase))
        {
            reason = "桌面目录与录入时不一致";
            return false;
        }

        if (string.IsNullOrEmpty(UserDesktop) || !Directory.Exists(UserDesktop))
        {
            reason = "当前无法访问桌面目录";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 这个路径能不能进回收站。
    /// </summary>
    /// <remarks>
    /// <b>不能只看盘符类型。</b>本机桌面是 <c>C:\Mac\Home\Desktop</c>，
    /// <c>Path.GetPathRoot</c> 给出 <c>C:\</c>、<c>DriveInfo.DriveType</c> 报 <c>Fixed</c>——
    /// 看起来是本地固定盘，其实它是个指向 Mac 宿主机的重解析点，
    /// <c>GetFinalPathNameByHandle</c> 解出来是 <c>\?\UNC\Mac\Home\Desktop</c>。
    /// 网络路径没有回收站，往那儿「删除」就是<b>永久删除</b>。
    /// <para/>
    /// （也试过 <c>SHQueryRecycleBin</c>，这台机器上对任何路径都返回 E_INVALIDARG，
    /// 拿它当判据只会得到误判，所以改用解析真实路径。）
    /// </remarks>
    private static bool CanUseRecycleBin(string path)
    {
        try
        {
            var real = ResolveFinalPath(path);
            if (real is null)
            {
                // 路径都解析不了，一律当作不能删。
                return false;
            }

            if (real.StartsWith(UncPrefix, StringComparison.OrdinalIgnoreCase) ||
                real.StartsWith(NetworkPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            var stripped = real.StartsWith(LongPathPrefix, StringComparison.Ordinal) ? real[4..] : real;
            var root = Path.GetPathRoot(stripped);
            if (string.IsNullOrEmpty(root))
            {
                return false;
            }

            var drive = new DriveInfo(root);
            return drive.DriveType is DriveType.Fixed or DriveType.Removable;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>长路径前缀 <c>\\?\</c>。</summary>
    private const string LongPathPrefix = @"\\?\";

    /// <summary>UNC 长路径前缀 <c>\\?\UNC\</c>，网络路径的标志。</summary>
    private const string UncPrefix = @"\\?\UNC\";

    /// <summary>普通 UNC 前缀 <c>\\</c>。</summary>
    private const string NetworkPrefix = @"\\";

    /// <summary>把路径解析成真实路径（会穿透重解析点 / 共享目录映射）。</summary>
    private static string? ResolveFinalPath(string path)
    {
        const uint fileFlagBackupSemantics = 0x02000000;
        var handle = CreateFileW(path, 0, 0x00000007, IntPtr.Zero, 3, fileFlagBackupSemantics, IntPtr.Zero);
        if (handle == new IntPtr(-1))
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            return length == 0 ? null : buffer.ToString();
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string path, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(IntPtr handle, StringBuilder buffer,
        uint size, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private static string SafeUserSid()
    {
        try
        {
            return WindowsIdentity.GetCurrent().User?.Value ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    #endregion

    #region 枚举

    private static List<DesktopEntry> EnumerateEntries()
    {
        var entries = new List<DesktopEntry>();
        AddFrom(UserDesktop, isPublic: false, entries);
        AddFrom(PublicDesktop, isPublic: true, entries);
        return entries;
    }

    private static void AddFrom(string root, bool isPublic, List<DesktopEntry> into)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            return;
        }

        try
        {
            // 只取第一级，不递归——文件夹里面怎么变都不关心。
            foreach (var path in Directory.EnumerateFileSystemEntries(root))
            {
                var name = Path.GetFileName(path);
                if (string.IsNullOrEmpty(name) ||
                    string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                into.Add(new DesktopEntry(name, Directory.Exists(path), isPublic));
            }
        }
        catch (Exception)
        {
            // 读不了就跳过这个目录。注意调用方要能区分「空桌面」和「读不到」。
        }
    }

    #endregion
}
