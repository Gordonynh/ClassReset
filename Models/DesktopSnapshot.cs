using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ClassIsland.ClassReset.Models;

/// <summary>桌面上的一个项目。</summary>
/// <param name="Name">文件/文件夹名（含扩展名）。</param>
/// <param name="IsDirectory">是不是文件夹。</param>
/// <param name="IsPublic">是否来自公共桌面（<c>%PUBLIC%\Desktop</c>）。</param>
public sealed record DesktopEntry(string Name, bool IsDirectory, bool IsPublic);

/// <summary>
/// 桌面布局的基线快照：有哪些项目、图标各自摆在哪。
/// </summary>
/// <remarks>
/// 刻意<b>只记第一级</b>——文件夹里面有没有变化不关心。
/// 学生把作业存进某个文件夹里是正常使用，不该被当成「布局被改了」。
/// </remarks>
public sealed class DesktopSnapshot
{
    /// <summary>录入这份基线的时间。</summary>
    public DateTime CapturedAt { get; set; } = DateTime.Now;

    /// <summary>录入时的机器名。换了机器就不能拿这份基线去删东西。</summary>
    public string MachineName { get; set; } = string.Empty;

    /// <summary>录入时的 Windows 用户 SID。</summary>
    public string UserSid { get; set; } = string.Empty;

    /// <summary>录入时的用户桌面路径。路径变了说明环境变了，基线作废。</summary>
    public string UserDesktopPath { get; set; } = string.Empty;

    /// <summary>录入时的公共桌面路径。</summary>
    public string PublicDesktopPath { get; set; } = string.Empty;

    /// <summary>桌面第一级的项目。</summary>
    public List<DesktopEntry> Entries { get; set; } = [];

    /// <summary>图标位置：显示名 → 坐标。</summary>
    public Dictionary<string, IconPoint> IconPositions { get; set; } = [];

    /// <summary>录入时有没有成功拿到图标位置。拿不到就只比对项目增减。</summary>
    public bool HasIconPositions { get; set; }

    public bool IsEmpty => Entries.Count == 0 && IconPositions.Count == 0;

    #region 读写

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static DesktopSnapshot Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<DesktopSnapshot>(File.ReadAllText(path), JsonOptions)
                       ?? new DesktopSnapshot();
            }
        }
        catch (Exception)
        {
            // 基线坏了就当没录过，用户可以在设置里重新录一次。
        }

        return new DesktopSnapshot();
    }

    public void Save(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception)
        {
            // 存不上就算了。
        }
    }

    #endregion
}

/// <summary>图标坐标。用类而不是元组，纯粹是为了 JSON 好看好改。</summary>
public sealed class IconPoint
{
    public int X { get; set; }
    public int Y { get; set; }
}
