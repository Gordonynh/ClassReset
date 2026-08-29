using System;
using System.Reflection;
using System.Runtime.Loader;
using ClassIsland.ClassReset.Models;
using ClassIsland.ClassReset.Services;
using ClassIsland.ClassReset.Views;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Extensions.Registry;
using ClassIsland.UltraCodeShared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClassIsland.ClassReset;

/// <summary>课后自动复原插件入口。</summary>
public class ClassResetPlugin : PluginBase
{
    private static readonly Assembly SelfAssembly = typeof(ClassResetPlugin).Assembly;

    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        EnsureAssemblyResolvable();

        ResetSettings.Initialize(PluginConfigFolder);

        // 复原遮罩用的是和 UltraCode 插件同一套像素场（按源码共享）。
        // 调参读 UltraCode 自己的 options.json；UltraCode 没装就退回出厂默认值。
        UltraCodeTuning.Current = JsonTuningSource.FromSiblingOf(PluginConfigFolder);

        var configFolder = PluginConfigFolder;
        services.AddSingleton(_ => new ClassResetService(configFolder));
        services.AddHostedService(sp => sp.GetRequiredService<ClassResetService>());
        services.AddSettingsPage<ResetSettingsPage>();
    }

    /// <summary>
    /// 让 <c>avares://ClassIsland.ClassReset/...</c> 能被解析到。
    /// </summary>
    /// <remarks>
    /// Avalonia 的资源加载器按名字用 <see cref="Assembly.Load(AssemblyName)"/> 找程序集，走的是
    /// 默认 <see cref="AssemblyLoadContext"/>；插件却在独立的 PluginLoadContext 里，默认上下文看不到它。
    /// </remarks>
    private static void EnsureAssemblyResolvable()
    {
        var selfName = SelfAssembly.GetName().Name;
        AssemblyLoadContext.Default.Resolving += (_, requested) =>
            requested.Name == selfName ? SelfAssembly : null;
    }
}
