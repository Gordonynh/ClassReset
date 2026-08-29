namespace ClassIsland.ClassReset.Models;

/// <summary>复原流程里单个步骤的状态。</summary>
/// <remarks>枚举值的顺序和 <c>ResetOverlayWindow.StepRow.Marks</c> 里的符号一一对应，别乱调。</remarks>
public enum ResetStepState
{
    /// <summary>还没轮到。</summary>
    Pending,

    /// <summary>正在做（界面上转圈）。</summary>
    Running,

    /// <summary>做完了。</summary>
    Done,

    /// <summary>这项被关掉了，没做。</summary>
    Skipped,

    /// <summary>出错了。</summary>
    Failed
}
