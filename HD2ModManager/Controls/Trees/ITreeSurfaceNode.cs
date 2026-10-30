using System.Collections.Generic;
using HD2ModManager.ViewModels;

namespace HD2ModManager.Controls.Trees;

/// <summary>
/// 树控件对一行节点的**全部**要求。控件不认识节点的业务含义，也不认识它的外观——
/// 外观由调用方的 <c>ItemTemplate</c> 决定，业务由节点自己带着。
///
/// 契约分两半：
/// <list type="bullet">
/// <item><b>输入</b>：<see cref="Children"/> 与 <see cref="IsExpanded"/>，由调用方提供并持久化。
/// 控件只在用户点击时翻转 <see cref="IsExpanded"/>，不替调用方记这件事。</item>
/// <item><b>输出</b>：<see cref="Depth"/> / <see cref="IsAlternateRow"/> / <see cref="ZIndex"/>，
/// 由 <see cref="TreeRowProjection"/> 在每次重投影时写入，行模板和行容器只读。</item>
/// </list>
///
/// 输出项由投影层拥有而不是节点自己算：它们是"这一行在当前可见序列里的位置"，
/// 只有看得到全部行的人才知道。缩进多少像素仍然是控件的事——契约只给"第几层"，不给像素。
/// </summary>
public interface ITreeSurfaceNode : IModListKeyed
{
    /// <summary>子节点。空集合表示叶子。</summary>
    IReadOnlyList<ITreeSurfaceNode> Children { get; }

    /// <summary>展开状态。用户点击时由控件翻转，调用方负责持久化。</summary>
    bool IsExpanded { get; set; }

    /// <summary>第几层，根为 0。投影层写入。缩进就是 Depth 条竖线。</summary>
    int Depth { get; set; }

    /// <summary>
    /// 隔行底色标记（可见序列里序号为奇数）。投影层写入。
    /// 由控件算而不是让模板去问 AlternationIndex：隔行色是每一棵树都要有的配套设施，
    /// 挂在"可见序号"上而不是数据序号上，调用方不该为它再写一遍逻辑。
    /// </summary>
    bool IsAlternateRow { get; set; }

    /// <summary>
    /// 重叠时的层级：越靠前的行越高。父行压住自己的子行，离父级近的子行压住远的，
    /// 展开动画里就靠它读成"扑克牌摊开"。投影层写入。
    /// </summary>
    int ZIndex { get; set; }

    /// <summary>
    /// 这一行是不是当前选中行。**由控件写入**（不是投影层）：选中是控件的交互状态，
    /// 与"这一行在可见序列里的第几位"无关。旧树靠 TreeViewItem 自带的选中，
    /// 换成扁平列表之后没有容器可依赖，只能由控件记、写到行上。
    /// </summary>
    bool IsSelected { get; set; }

    /// <summary>是否有子节点。</summary>
    bool HasChildren => Children.Count > 0;
}
