using System;
using System.Collections;
using System.Collections.Generic;

namespace HD2ModManager.Controls.Trees;

/// <summary>
/// 把"节点树 + 展开状态"压平成"当前可见行序列"。
///
/// 这是整个方案的关键一步：控件只渲染这个序列，**不认层级**；层级只在这里被翻译成
/// 三个数字——<see cref="ITreeSurfaceNode.Depth"/>（缩进几级）、
/// <see cref="ITreeSurfaceNode.IsAlternateRow"/>（隔行底色）、
/// <see cref="ITreeSurfaceNode.ZIndex"/>（重叠时谁在上）。
///
/// 为什么不在控件里递归：一旦递归，每一层就活在自己的容器里，行与行再也无法互相重叠，
/// 也无法跨层决定上下关系——"子行从父行身上滑出来"这种动画在结构上就写不出来。
/// 压平之后全列表只有一个坐标系，动画就只是给每行设一个 translate。
/// </summary>
public static class TreeRowProjection
{
    /// <summary>投影结果。行序列给控件渲染，父子表给动画算起点。</summary>
    public sealed class Result
    {
        internal Result(
            IReadOnlyList<ITreeSurfaceNode> rows,
            IReadOnlyDictionary<object, ITreeSurfaceNode> parentOf)
        {
            Rows = rows;
            ParentOf = parentOf;
        }

        /// <summary>当前可见的行，自上而下。</summary>
        public IReadOnlyList<ITreeSurfaceNode> Rows { get; }

        /// <summary>子节点 → 它的父节点。展开时"子行从父行出发"要用它找锚点。</summary>
        public IReadOnlyDictionary<object, ITreeSurfaceNode> ParentOf { get; }

        public static Result Empty { get; } = new(
            Array.Empty<ITreeSurfaceNode>(),
            new Dictionary<object, ITreeSurfaceNode>(ReferenceEqualityComparer.Instance));
    }

    /// <summary>
    /// 压平。会**写**入每个节点的 Depth / IsAlternateRow / ZIndex —— 这三个字段是投影层的
    /// 输出，节点只是载体。
    /// </summary>
    public static Result Flatten(IEnumerable? roots)
    {
        var rows = new List<ITreeSurfaceNode>();
        var parentOf = new Dictionary<object, ITreeSurfaceNode>(ReferenceEqualityComparer.Instance);

        if (roots is not null)
        {
            foreach (var item in roots)
            {
                if (item is ITreeSurfaceNode node) Walk(node, 0, rows, parentOf);
            }
        }

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            // 隔行底色按**可见序号**算：展开/收起之后相邻两行仍然不同色。
            row.IsAlternateRow = (index & 1) == 1;
            // 层级：越靠前的行越高。不重叠时完全无影响；展开动画里子行紧跟在父行后面，
            // 于是"父压子、近压远"自然成立，不需要动画期间再单独管理 z。
            row.ZIndex = rows.Count - index;
        }

        return new Result(rows, parentOf);
    }

    private static void Walk(
        ITreeSurfaceNode node,
        int depth,
        List<ITreeSurfaceNode> rows,
        Dictionary<object, ITreeSurfaceNode> parentOf)
    {
        node.Depth = depth;
        rows.Add(node);

        if (!node.IsExpanded) return;
        var children = node.Children;
        if (children.Count == 0) return;

        for (var index = 0; index < children.Count; index++)
        {
            var child = children[index];
            parentOf[child] = node;
            Walk(child, depth + 1, rows, parentOf);
        }
    }
}
