namespace HD2ModManager.Controls.Trees;

/// <summary>
/// 树的行几何。放在 Trees 命名空间而不是控件里：竖线绘制和行容器都要用同一个数，
/// 任何一方自己写死一份，行距和竖线就会差半个像素。
/// </summary>
public static class TreeMetrics
{
    /// <summary>
    /// 固定行高。调用方的行模板改不了它（行容器样式钉死），这样扑克牌位移严格等距、
    /// 滚动条长度稳定，将来打开虚拟化也不需要重新量行。
    /// </summary>
    public const double RowHeight = 26d;

    /// <summary>
    /// 一层的缩进宽度，也是相邻两条竖线的间距。
    /// 取 16 是为了和旧的简单树对齐：它每层是 8 外边距 + 8 内边距。
    /// 每一层画一条贯穿整行的竖线，不做 ├ / └ 那种拐角——旧的简单树就是这么画的，
    /// 层级深了也只是一排平行的线，比拐角安静得多。
    /// </summary>
    public const double IndentWidth = 16d;
}
