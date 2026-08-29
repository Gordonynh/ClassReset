using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ClassIsland.ClassReset.Interop;
using ClassIsland.ClassReset.Models;
using ClassIsland.PluginShared;
using ClassIsland.UltraCodeShared;

namespace ClassIsland.ClassReset.Views;

/// <summary>
/// 复原用的全屏遮罩，分两个阶段。
/// </summary>
/// <remarks>
/// <b>阶段一 · 倒计时。</b>灰色遮罩淡入，标题和秒数随后浮现，
/// 点屏幕任意位置或按任意键即取消。这个窗口<b>必须铺满屏幕并且吃点击</b>——
/// 和其它插件的浮窗刚好相反。它的作用就是拦住即将发生的破坏性操作：
/// 铺满整屏保证不管鼠标在哪都点得到，也保证不可能没看见。
/// <para/>
/// <b>阶段二 · 执行。</b>倒计时走完后<b>原地</b>切换，不新开窗口：
/// UltraCode 像素场从右往左覆盖全屏，标题换成「正在重置，请稍候」，
/// 下面一行一步显示进度。收拾完像素场再从右往左流走。
/// <para/>
/// 阶段二不再响应点击——那时候进程已经在关了，给一个假的「取消」比不给更糟。
/// </remarks>
internal class ResetOverlayWindow : Window
{
    /// <summary>遮罩底色。刻意用中灰而不是接近白的浅灰，和后面的像素场也衔接得上。</summary>
    private static readonly Color ShroudColor = Color.FromRgb(0x3A, 0x3A, 0x40);

    /// <summary>阶段一的文字色。底是深灰，所以用浅色。</summary>
    private static readonly Color ShroudInk = Color.FromRgb(0xF2, 0xF2, 0xF5);

    private const int ShroudFadeMs = 220;
    private const int TextFadeMs = 260;
    private const int FieldRevealMs = 950;
    private const int FieldCollapseMs = 700;

    private static ResetOverlayWindow? _instance;

    private readonly Panel _shroud;
    private readonly StackPanel _countdownBody;
    private readonly StackPanel _executionBody;
    private readonly StackPanel _stepList;
    private readonly TextBlock _countdownText;
    private readonly TextBlock _executionTitle;
    private readonly UltraCodeField _field;
    private readonly DispatcherTimer _tick;
    private readonly Action<ResetOverlayWindow> _onConfirmed;
    private readonly Action _onCancelled;

    private readonly List<StepRow> _rows = [];

    private int _remaining;
    private bool _settled;
    private bool _executing;

    /// <summary>独占标记。开着的时候别的插件的置顶窗口会让位，不跟这个遮罩抢。</summary>
    private IDisposable? _exclusive;

    /// <summary>当前是否有遮罩开着。</summary>
    public static bool IsOpen => _instance is { _settled: false } or { _executing: true };

    /// <summary>
    /// 显示倒计时遮罩。
    /// </summary>
    /// <param name="title">大标题，比如「即将还原系统」。</param>
    /// <param name="seconds">倒计时秒数。传 0 表示不倒计时，直接进执行阶段。</param>
    /// <param name="hint">标题下的一行小字，比如「点击屏幕以取消」。</param>
    /// <param name="onConfirmed">倒计时走完（没人拦）时调用，参数是这个窗口本身。</param>
    /// <param name="onCancelled">用户点了屏幕时调用。</param>
    public static void Show(string title, int seconds, string hint,
        Action<ResetOverlayWindow> onConfirmed, Action onCancelled)
    {
        CloseCurrent();
        var window = new ResetOverlayWindow(title, seconds, hint, onConfirmed, onCancelled);
        _instance = window;
        window.Show();
        window.Activate();
    }

    /// <summary>强行收掉遮罩（按取消处理）。执行阶段不受影响。</summary>
    public static void CloseCurrent()
    {
        if (_instance is { _executing: false })
        {
            _instance.Settle(confirmed: false);
        }
    }

    private ResetOverlayWindow(string title, int seconds, string hint,
        Action<ResetOverlayWindow> onConfirmed, Action onCancelled)
    {
        _remaining = Math.Max(0, seconds);
        _onConfirmed = onConfirmed;
        _onCancelled = onCancelled;

        SystemDecorations = SystemDecorations.None;
        Background = null;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        ShowActivated = true;

        _countdownText = new TextBlock
        {
            FontSize = 96,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(ShroudInk),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };

        _countdownBody = new StackPanel
        {
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0,
            Transitions =
            [
                new DoubleTransition
                {
                    Property = OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(TextFadeMs),
                    Easing = new CubicEaseOut()
                }
            ],
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    FontSize = 46,
                    FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(ShroudInk),
                    HorizontalAlignment = HorizontalAlignment.Center
                },
                new TextBlock
                {
                    Text = hint,
                    FontSize = 22,
                    Margin = new Thickness(0, 2, 0, 18),
                    Foreground = new SolidColorBrush(ShroudInk, 0.62),
                    HorizontalAlignment = HorizontalAlignment.Center
                },
                _countdownText
            }
        };

        // 阶段二的内容。先建好但整体透明，切过去的时候再淡入。
        _executionTitle = new TextBlock
        {
            Text = "正在重置，请稍候",
            FontSize = 52,
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 34)
        };

        _stepList = new StackPanel
        {
            Spacing = 14,
            HorizontalAlignment = HorizontalAlignment.Left
        };

        _executionBody = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
            Opacity = 0,
            Transitions =
            [
                new DoubleTransition
                {
                    Property = OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(TextFadeMs),
                    Easing = new CubicEaseOut()
                }
            ],
            Children = { _executionTitle, _stepList }
        };

        _field = new UltraCodeField
        {
            CellSize = 6,
            // 整屏的格子数是提醒条的几十倍，边长必须能单独调粗来压计算量，
            // 不能跟着 UltraCode 设置页那个（那是给提醒条调的）。
            CellSizeOverride = Math.Clamp(ResetSettings.Current.OverlayCellSize, 3, 40),
            // 整屏面积很大，像素层压低一点，否则进度文字会被闪烁盖过去。
            PixelOpacity = 0.45,
            RevealDurationMs = FieldRevealMs,
            CollapseDurationMs = FieldCollapseMs,
            ClipToBounds = true,
            IsVisible = false
        };
        _field.Collapsed += (_, _) => Dispatcher.UIThread.Post(CloseForGood);

        _shroud = new Panel
        {
            Background = new SolidColorBrush(ShroudColor, 0.92),
            Opacity = 0,
            Transitions =
            [
                new DoubleTransition
                {
                    Property = OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(ShroudFadeMs),
                    Easing = new CubicEaseOut()
                }
            ]
        };

        Content = new Panel { Children = { _shroud, _field, _countdownBody, _executionBody } };

        UpdateCountdown();

        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) =>
        {
            _remaining--;
            if (_remaining <= 0)
            {
                Settle(confirmed: true);
                return;
            }

            UpdateCountdown();
        };

        // 倒计时期间点哪儿都算取消，按键也算。执行阶段这两个处理器会自己让路。
        PointerPressed += (_, _) => Settle(confirmed: false);
        KeyDown += (_, _) => Settle(confirmed: false);
    }

    private void UpdateCountdown() => _countdownText.Text = $"{_remaining}";

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is not null)
        {
            Position = screen.Bounds.Position;
            var scaling = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
            Width = screen.Bounds.Width / scaling;
            Height = screen.Bounds.Height / scaling;
        }

        // 遮罩要盖住一切，包括别的置顶窗口。但它是可激活的——
        // 需要接键盘，也需要能被点到。
        // exclusive: 抽人悬浮钮那些会主动让位，不再跟这个遮罩抢置顶。
        _exclusive = ExclusiveOverlay.Acquire();
        new TopmostEnforcer(this, TimeSpan.FromMilliseconds(300), preventActivation: false,
            exclusive: true).Attach();

        // 先灰色淡入，文字随后浮现——不要一上来整块砸在脸上。
        _shroud.Opacity = 1;
        var textDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ShroudFadeMs * 0.8) };
        textDelay.Tick += (_, _) =>
        {
            textDelay.Stop();
            if (!_executing)
            {
                _countdownBody.Opacity = 1;
            }
        };
        textDelay.Start();

        if (_remaining <= 0)
        {
            // 不倒计时的科目：灰色淡入之后直接进执行阶段。
            var straight = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ShroudFadeMs) };
            straight.Tick += (_, _) =>
            {
                straight.Stop();
                Settle(confirmed: true);
            };
            straight.Start();
            return;
        }

        _tick.Start();
    }

    /// <summary>结束倒计时阶段。<paramref name="confirmed"/> 为 true 表示没人拦、该执行了。</summary>
    private void Settle(bool confirmed)
    {
        if (_settled || _executing)
        {
            return;
        }

        _settled = true;
        _tick.Stop();

        if (!confirmed)
        {
            CloseForGood();
            Dispatcher.UIThread.Post(_onCancelled);
            return;
        }

        // 确认执行：不关窗，原地切到阶段二。
        _countdownBody.Opacity = 0;
        _countdownBody.IsVisible = false;
        Dispatcher.UIThread.Post(() => _onConfirmed(this));
    }

    #region 阶段二

    /// <summary>切到执行阶段：像素场覆盖全屏，列出要做的步骤。</summary>
    public void BeginExecution(IReadOnlyList<string> stepNames)
    {
        _executing = true;

        // 像素场的配色和文字色都来自同一套调色板，文字对比度是算出来的不是拍的。
        UltraCodePalette.Refresh();
        var ink = UltraCodePalette.ForegroundColor;

        _executionTitle.Foreground = new SolidColorBrush(ink);
        _stepList.Children.Clear();
        _rows.Clear();

        foreach (var name in stepNames)
        {
            var row = new StepRow(name, ink);
            _rows.Add(row);
            _stepList.Children.Add(row.Root);
        }

        _field.IsVisible = true;
        _field.Open();

        // 文字等像素场扫过一多半再浮现，和值日浮窗一个节奏。
        _executionBody.IsVisible = true;
        var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FieldRevealMs * 0.6) };
        delay.Tick += (_, _) =>
        {
            delay.Stop();
            _executionBody.Opacity = 1;
        };
        delay.Start();
    }

    /// <summary>更新某一步的状态。</summary>
    public void SetStep(int index, ResetStepState state, string? detail = null)
    {
        if (index >= 0 && index < _rows.Count)
        {
            _rows[index].Update(state, detail);
        }
    }

    /// <summary>全部做完：停一下让人看清结果，然后让像素场从右往左流走并关窗。</summary>
    public void FinishExecution(string summary)
    {
        _executionTitle.Text = summary;

        var hold = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
        hold.Tick += (_, _) =>
        {
            hold.Stop();
            _executionBody.Opacity = 0;
            _field.Collapse();

            // 兜底：像素场没在跑时 Collapsed 不会来，别把整屏窗口留在屏幕上。
            var safety = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FieldCollapseMs + 300) };
            safety.Tick += (_, _) =>
            {
                safety.Stop();
                CloseForGood();
            };
            safety.Start();
        };
        hold.Start();
    }

    #endregion

    private void CloseForGood()
    {
        _exclusive?.Dispose();
        _exclusive = null;
        _tick.Stop();
        _settled = true;
        _executing = false;
        if (ReferenceEquals(_instance, this))
        {
            _instance = null;
        }

        Close();
    }

    /// <summary>
    /// 进度表里的一行：转圈 / 对勾 + 步骤名 + 结果。
    /// </summary>
    /// <remarks>
    /// 转圈用 <see cref="Arc"/> 加一个每 60 ms 转 24 度的定时器，不用字体里的符号——
    /// 教室那台机器的中文字体不一定有盲文点阵之类的旋转字符。
    /// </remarks>
    private sealed class StepRow
    {
        private static readonly string[] Marks = ["", "", "✓", "—", "×"];

        private readonly Arc _spinner;
        private readonly TextBlock _mark;
        private readonly TextBlock _name;
        private readonly TextBlock _detail;
        private readonly Color _ink;
        private readonly RotateTransform _rotate = new();
        private readonly DispatcherTimer _spin = new() { Interval = TimeSpan.FromMilliseconds(60) };

        public Panel Root { get; }

        public StepRow(string name, Color ink)
        {
            _ink = ink;

            _spinner = new Arc
            {
                Width = 22,
                Height = 22,
                StartAngle = 0,
                SweepAngle = 280,
                Stroke = new SolidColorBrush(ink),
                StrokeThickness = 2.5,
                StrokeLineCap = PenLineCap.Round,
                RenderTransform = _rotate,
                RenderTransformOrigin = RelativePoint.Center,
                IsVisible = false,
                VerticalAlignment = VerticalAlignment.Center
            };

            _spin.Tick += (_, _) => _rotate.Angle = (_rotate.Angle + 24) % 360;

            _mark = new TextBlock
            {
                Text = "·",
                FontSize = 26,
                Width = 22,
                TextAlignment = TextAlignment.Center,
                Foreground = new SolidColorBrush(ink, 0.45),
                VerticalAlignment = VerticalAlignment.Center
            };

            _name = new TextBlock
            {
                Text = name,
                FontSize = 26,
                Foreground = new SolidColorBrush(ink, 0.55),
                VerticalAlignment = VerticalAlignment.Center
            };

            _detail = new TextBlock
            {
                FontSize = 20,
                Foreground = new SolidColorBrush(ink, 0.62),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 640
            };

            Root = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 14,
                Children =
                {
                    new Panel { Width = 22, Height = 22, Children = { _mark, _spinner } },
                    _name,
                    _detail
                }
            };
        }

        public void Update(ResetStepState state, string? detail)
        {
            var running = state == ResetStepState.Running;
            _spinner.IsVisible = running;
            _mark.IsVisible = !running;

            if (running)
            {
                _spin.Start();
            }
            else
            {
                _spin.Stop();
            }

            _mark.Text = Marks[(int)state];
            _name.Foreground = new SolidColorBrush(_ink, state == ResetStepState.Pending ? 0.55 : 1.0);
            _mark.Foreground = new SolidColorBrush(_ink,
                state is ResetStepState.Pending or ResetStepState.Skipped ? 0.45 : 1.0);

            if (!string.IsNullOrEmpty(detail))
            {
                _detail.Text = detail;
            }
        }
    }
}
