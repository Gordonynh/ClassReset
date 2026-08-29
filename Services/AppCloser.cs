using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using ClassIsland.ClassReset.Interop;
using ClassIsland.ClassReset.Models;

namespace ClassIsland.ClassReset.Services;

/// <summary>
/// 关闭学生开着的软件。
/// </summary>
/// <remarks>
/// 整个流程刻意设计得很保守，宁可漏关也不错杀：
/// <list type="number">
/// <item>只处理<b>任务栏上看得见</b>的窗口（<see cref="WindowScanner"/>），
///       后台服务、托盘程序、别的插件的浮窗一律不在范围内；</item>
/// <item>每个进程都先过 <see cref="ProcessGuard"/>，命中保护名单直接跳过；</item>
/// <item>explorer <b>只关窗口、绝不结束进程</b>——结束了桌面和任务栏就没了；</item>
/// <item>先发 WM_CLOSE 让程序自己退（这样 WPS 之类还能弹保存提示），
///       等一段宽限期，仍然活着才强制结束。</item>
/// </list>
/// </remarks>
internal static class AppCloser
{
    /// <summary>一次关闭操作的结果，用来写日志和在界面上交代做了什么。</summary>
    public sealed class Result
    {
        public List<string> Closed { get; } = [];
        public List<string> Killed { get; } = [];
        public List<string> Skipped { get; } = [];
        public List<string> ExplorerWindowsClosed { get; } = [];
        public List<string> Failed { get; } = [];
    }

    /// <summary>
    /// 关掉任务栏上的软件。
    /// </summary>
    /// <param name="settings">设置，决定是否强杀、宽限期多长、额外白名单。</param>
    /// <param name="dryRun">只统计不动手，用于设置页里的「看看会关掉什么」。</param>
    public static Result CloseAll(ResetSettings settings, bool dryRun = false)
    {
        var result = new Result();
        var windows = WindowScanner.Enumerate();
        if (windows.Count == 0)
        {
            return result;
        }

        // 按进程归拢：一个程序可能开了好几个窗口，只需要处理一次进程。
        var byProcess = windows.GroupBy(x => x.ProcessId);
        var pendingKill = new List<Process>();

        foreach (var group in byProcess)
        {
            Process process;
            try
            {
                process = Process.GetProcessById(group.Key);
            }
            catch (Exception)
            {
                // 进程已经没了，跳过。
                continue;
            }

            // explorer 必须**先**判：它同时也在保护名单里，
            // 要是先走保护判定就直接 continue 了，窗口永远关不掉。
            // 对它的正确处理是「关窗口、不碰进程」，不是「完全跳过」。
            if (ProcessGuard.IsExplorer(process))
            {
                foreach (var window in group)
                {
                    if (!dryRun)
                    {
                        WindowScanner.RequestClose(window.Handle);
                    }

                    result.ExplorerWindowsClosed.Add(window.Title);
                }

                process.Dispose();
                continue;
            }

            if (ProcessGuard.IsProtected(process, settings.NeverCloseProcesses, out var reason))
            {
                result.Skipped.Add($"{Describe(group)} — {reason}");
                process.Dispose();
                continue;
            }

            if (dryRun)
            {
                result.Closed.Add(Describe(group));
                process.Dispose();
                continue;
            }

            // 先温和关：给程序自己退出的机会（未保存的文档会弹提示）。
            foreach (var window in group)
            {
                WindowScanner.RequestClose(window.Handle);
            }

            result.Closed.Add(Describe(group));
            pendingKill.Add(process);
        }

        if (dryRun || !settings.ForceKillIfNotClosed || pendingKill.Count == 0)
        {
            foreach (var p in pendingKill)
            {
                p.Dispose();
            }

            return result;
        }

        // 宽限期：等程序自己退。
        Thread.Sleep(Math.Clamp(settings.GraceMilliseconds, 500, 30000));

        foreach (var process in pendingKill)
        {
            try
            {
                process.Refresh();
                if (process.HasExited)
                {
                    continue;
                }

                // 到这一步还活着，说明它自己不肯退（可能卡在保存提示上）。
                // 再确认一次保护名单——宽限期里进程 ID 有可能已经被复用了。
                if (ProcessGuard.IsProtected(process, settings.NeverCloseProcesses, out var reason))
                {
                    result.Skipped.Add($"{process.ProcessName} — {reason}");
                    continue;
                }

                var name = process.ProcessName;
                process.Kill(entireProcessTree: true);
                result.Killed.Add(name);
            }
            catch (Exception ex)
            {
                result.Failed.Add($"{SafeName(process)}：{ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }

        return result;
    }

    private static string Describe(IGrouping<int, TaskbarWindow> group)
    {
        var first = group.First();
        var name = string.IsNullOrEmpty(first.ProcessName) ? "未知程序" : first.ProcessName;
        return group.Count() > 1 ? $"{name}（{group.Count()} 个窗口）" : $"{name}｜{first.Title}";
    }

    private static string SafeName(Process process)
    {
        try
        {
            return process.ProcessName;
        }
        catch (Exception)
        {
            return "未知进程";
        }
    }
}
