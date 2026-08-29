using System;
using System.IO;
using System.Text.Json;

namespace ClassIsland.UltraCodeShared;

/// <summary>
/// 从 UltraCode 插件的 <c>options.json</c> 读调参。
/// </summary>
/// <remarks>
/// 给值日提醒、课后自动复原这类「用了像素场但不拥有它的设置」的插件用。
/// 路径是兄弟目录：<c>&lt;Config&gt;\Plugins\gordon.ultracode\options.json</c>。
/// <para/>
/// 读取结果缓存 <see cref="CacheSeconds"/> 秒。动画每次打开时会取值，
/// 缓存足以避免连续帧里反复读盘，同时又能让用户在设置页拖完滑块后很快看到效果。
/// 文件不存在、读不动、格式坏了，一律退回出厂默认值，绝不抛异常——
/// 这些属性是在渲染路径上取的，抛出去会把宿主的 UI 线程带崩。
/// </remarks>
public sealed class JsonTuningSource : IUltraCodeTuning
{
    /// <summary>UltraCode 插件的 id，也是它的配置目录名。</summary>
    public const string UltraCodePluginId = "gordon.ultracode";

    private const double CacheSeconds = 2.0;

    private static readonly IUltraCodeTuning Fallback = new DefaultUltraCodeTuning();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _path;
    private readonly object _gate = new();

    private Snapshot _cached = Snapshot.From(Fallback);
    private DateTime _readAt = DateTime.MinValue;
    private DateTime _fileStamp = DateTime.MinValue;

    /// <summary>
    /// 由本插件的配置目录推出 UltraCode 的 <c>options.json</c>。
    /// </summary>
    /// <param name="ownPluginConfigFolder">本插件的 <c>PluginConfigFolder</c>。</param>
    public static JsonTuningSource FromSiblingOf(string ownPluginConfigFolder)
    {
        var parent = Path.GetDirectoryName(ownPluginConfigFolder?.TrimEnd(Path.DirectorySeparatorChar) ?? string.Empty);
        var path = string.IsNullOrEmpty(parent)
            ? string.Empty
            : Path.Combine(parent, UltraCodePluginId, "options.json");
        return new JsonTuningSource(path);
    }

    public JsonTuningSource(string optionsJsonPath) => _path = optionsJsonPath ?? string.Empty;

    /// <summary>配置文件路径，设置页里显示用。</summary>
    public string Path_ => _path;

    /// <summary>UltraCode 的配置文件在不在。不在就是用的出厂默认值。</summary>
    public bool IsAvailable => !string.IsNullOrEmpty(_path) && File.Exists(_path);

    public double AnimationOpacity => Read().AnimationOpacity;
    public double PixelIntensity => Read().PixelIntensity;
    public double Saturation => Read().Saturation;
    public double Contrast => Read().Contrast;
    public double Brightness => Read().Brightness;
    public double CellSize => Read().CellSize;
    public double FlowSpeed => Read().FlowSpeed;
    public bool CountdownDrain => Read().CountdownDrain;

    private Snapshot Read()
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if ((now - _readAt).TotalSeconds < CacheSeconds)
            {
                return _cached;
            }

            _readAt = now;

            try
            {
                if (string.IsNullOrEmpty(_path) || !File.Exists(_path))
                {
                    _cached = Snapshot.From(Fallback);
                    return _cached;
                }

                var stamp = File.GetLastWriteTimeUtc(_path);
                if (stamp == _fileStamp)
                {
                    return _cached;
                }

                var parsed = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(_path), JsonOptions);
                if (parsed is not null)
                {
                    _cached = parsed;
                    _fileStamp = stamp;
                    UltraCodePalette.Invalidate();
                }
            }
            catch (Exception)
            {
                // 渲染路径上不允许抛异常，读不到就继续用上一次的值。
            }

            return _cached;
        }
    }

    /// <summary>options.json 的形状。字段名和 UltraCodeOptions 的属性名一一对应。</summary>
    private sealed class Snapshot
    {
        public double AnimationOpacity { get; set; } = 0.92;
        public double PixelIntensity { get; set; } = 1.95;
        public double Saturation { get; set; } = 3.0;
        public double Contrast { get; set; } = 1.45;
        public double Brightness { get; set; } = 0.07;
        public double CellSize { get; set; } = 6;
        public double FlowSpeed { get; set; } = 1.0;
        public bool CountdownDrain { get; set; } = true;

        public static Snapshot From(IUltraCodeTuning t) => new()
        {
            AnimationOpacity = t.AnimationOpacity,
            PixelIntensity = t.PixelIntensity,
            Saturation = t.Saturation,
            Contrast = t.Contrast,
            Brightness = t.Brightness,
            CellSize = t.CellSize,
            FlowSpeed = t.FlowSpeed,
            CountdownDrain = t.CountdownDrain
        };
    }
}
