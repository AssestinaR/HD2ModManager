using System;
using System.Collections.Generic;
using System.Linq;

namespace HD2ModManager.Controls.Trees;

/// <summary>选择模式。对应鼠标修饰键，语义与资源管理器一致。</summary>
public enum TreeSelectionMode
{
    /// <summary>普通点击：把选中集合换成这一个。</summary>
    Replace = 0,

    /// <summary>Ctrl + 点击：把这一行加进集合，或从集合里去掉。</summary>
    Toggle = 1,

    /// <summary>Shift + 点击：从锚点到这一行的区间（只取同一层级的可见行）。</summary>
    Range = 2,
}

/// <summary>算完之后的新选中集合，以及新的区间锚点。</summary>
public readonly record struct TreeSelectionResult<T>(IReadOnlyList<T> Nodes, T? Anchor)
    where T : class;

/// <summary>
/// 树的多选集合运算。<b>刻意不碰 WPF</b>：这样它可以被单独测。
/// <para>
/// 三条规则，都在这里定死：
/// </para>
/// <list type="number">
/// <item><b>同一时刻只有一层。</b>已有选择且层级不同时，加选被当成"重新开始"——
/// 直接换成这一个。这样"永远不可能同时选到不同层级"，用户也不会遇到"点了没反应"。</item>
/// <item><b>Ctrl 是切换。</b>已选中的再点一次取消；取消到空时锚点一起清掉。</item>
/// <item><b>Shift 只取同层。</b>区间按可见顺序取，但夹在中间的其他层级的行会被跳过；
/// 锚点不动，所以连续 Shift 点几次，区间始终从同一个起点长出来。</item>
/// </list>
/// </summary>
public static class TreeSelectionMath
{
    /// <summary>
    /// 算出新的选中集合。
    /// </summary>
    /// <param name="current">当前选中的行（应当同层；空集合表示没选）。</param>
    /// <param name="anchor">区间锚点，通常是上次点的那一行。</param>
    /// <param name="visible">压平后的可见行，按显示顺序。</param>
    /// <param name="target">这次点到的行。</param>
    /// <param name="mode">选择模式。</param>
    /// <param name="depthOf">取一行的层级。根为 0。</param>
    public static TreeSelectionResult<T> Compute<T>(
        IReadOnlyList<T> current,
        T? anchor,
        IReadOnlyList<T> visible,
        T target,
        TreeSelectionMode mode,
        Func<T, int> depthOf)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(visible);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(depthOf);

        // 规则 1：跨层 = 重新开始。
        if (current.Count != 0 && depthOf(current[0]) != depthOf(target))
            return new([target], target);

        switch (mode)
        {
            case TreeSelectionMode.Toggle:
                if (current.Any(selected => ReferenceEquals(selected, target)))
                {
                    var remaining = current.Where(selected => !ReferenceEquals(selected, target)).ToArray();
                    // 规则 2：取消到空时锚点也清掉，否则之后 Shift 会从一个"已取消"的点长区间。
                    return new(remaining, remaining.Length == 0 ? null : target);
                }

                return new([.. current, target], target);

            case TreeSelectionMode.Range:
                var anchorIndex = IndexOf(visible, anchor);
                var targetIndex = IndexOf(visible, target);
                if (anchorIndex < 0 || targetIndex < 0)
                    return new([target], target);

                // 规则 3：只取同层；锚点不动。
                var from = Math.Min(anchorIndex, targetIndex);
                var to = Math.Max(anchorIndex, targetIndex);
                var depth = depthOf(target);
                var range = new List<T>(to - from + 1);
                for (var index = from; index <= to; index++)
                {
                    if (depthOf(visible[index]) == depth) range.Add(visible[index]);
                }

                return new(range, anchor);

            default:
                return new([target], target);
        }
    }

    private static int IndexOf<T>(IReadOnlyList<T> rows, T? node) where T : class
    {
        if (node is null) return -1;
        for (var index = 0; index < rows.Count; index++)
        {
            if (ReferenceEquals(rows[index], node)) return index;
        }

        return -1;
    }
}
