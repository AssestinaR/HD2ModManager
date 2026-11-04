using HD2ModManager.Controls.Trees;
using Xunit;

namespace HD2ModCore.Tests;

/// <summary>
/// 树的多选集合运算（<see cref="TreeSelectionMath"/>）。
/// <para>
/// 三条规则各有一组用例：同一时刻只有一层、Ctrl 切换、Shift 只取同层的可见区间。
/// 这些都是"错了就会让用户选中一堆不该一起出现的块"的地方，所以钉得细一点。
/// </para>
/// </summary>
public sealed class TreeSelectionMathTests
{
	private sealed class Row(string name, int depth)
	{
		public string Name { get; } = name;
		public int Depth { get; } = depth;
		public override string ToString() => Name;
	}

	private static Row R(string name, int depth) => new(name, depth);

	private static TreeSelectionResult<Row> Compute(
		IReadOnlyList<Row> current, Row? anchor, IReadOnlyList<Row> visible, Row target, TreeSelectionMode mode)
		=> TreeSelectionMath.Compute(current, anchor, visible, target, mode, row => row.Depth);

	private static string[] Names(TreeSelectionResult<Row> result)
		=> [.. result.Nodes.Select(node => node.Name)];

	[Fact]
	public void Replace_PutsOnlyTheTargetInTheSet()
	{
		var a = R("a", 1);
		var b = R("b", 1);
		var c = R("c", 1);
		Row[] visible = [a, b, c];

		var result = Compute([a, b], b, visible, c, TreeSelectionMode.Replace);

		Assert.Equal(["c"], Names(result));
		Assert.Same(c, result.Anchor);
	}

	[Fact]
	public void Toggle_AddsARowOnAnotherLayerOfTheSameDepth()
	{
		var a = R("a", 2);
		var b = R("b", 2);
		Row[] visible = [a, b];

		var result = Compute([a], a, visible, b, TreeSelectionMode.Toggle);

		Assert.Equal(["a", "b"], Names(result));
		Assert.Same(b, result.Anchor);
	}

	[Fact]
	public void Toggle_OnAnAlreadySelectedRow_RemovesIt()
	{
		var a = R("a", 2);
		var b = R("b", 2);
		Row[] visible = [a, b];

		var result = Compute([a, b], b, visible, a, TreeSelectionMode.Toggle);

		Assert.Equal(["b"], Names(result));
	}

	[Fact]
	public void Toggle_RemovingTheLastRow_ClearsTheAnchorToo()
	{
		var a = R("a", 2);
		Row[] visible = [a];

		var result = Compute([a], a, visible, a, TreeSelectionMode.Toggle);

		Assert.Empty(result.Nodes);
		// 锚点必须一起清掉：留着它之后 Shift 会从一个"已取消"的点长区间。
		Assert.Null(result.Anchor);
	}

	[Fact]
	public void CrossDepth_IsTreatedAsARestart_ForEveryMode()
	{
		var shallow = R("shallow", 1);
		var deep = R("deep", 2);
		Row[] visible = [shallow, deep];

		foreach (var mode in new[] { TreeSelectionMode.Replace, TreeSelectionMode.Toggle, TreeSelectionMode.Range })
		{
			var result = Compute([shallow], shallow, visible, deep, mode);

			// 跨层永远只留下这一个 —— 不管按的是哪个修饰键。
			Assert.Equal(["deep"], Names(result));
			Assert.Same(deep, result.Anchor);
		}
	}

	[Fact]
	public void Range_TakesTheWholeStretchWhenAllRowsShareTheDepth()
	{
		var a = R("a", 1);
		var b = R("b", 1);
		var c = R("c", 1);
		var d = R("d", 1);
		Row[] visible = [a, b, c, d];

		var result = Compute([a], a, visible, c, TreeSelectionMode.Range);

		Assert.Equal(["a", "b", "c"], Names(result));
		// 锚点不动：连续 Shift 点几次，区间始终从同一个起点长出来。
		Assert.Same(a, result.Anchor);
	}

	[Fact]
	public void Range_WorksBackwardsToo()
	{
		var a = R("a", 1);
		var b = R("b", 1);
		var c = R("c", 1);
		Row[] visible = [a, b, c];

		var result = Compute([c], c, visible, a, TreeSelectionMode.Range);

		Assert.Equal(["a", "b", "c"], Names(result));
		Assert.Same(c, result.Anchor);
	}

	[Fact]
	public void Range_SkipsRowsOfOtherDepths_ThatFallInsideTheStretch()
	{
		// 可见序列里 a/b 是第 1 层，x 是第 2 层（它展开在 b 下面），c 又是第 1 层。
		var a = R("a", 1);
		var b = R("b", 1);
		var x = R("x", 2);
		var c = R("c", 1);
		Row[] visible = [a, b, x, c];

		var result = Compute([a], a, visible, c, TreeSelectionMode.Range);

		// 夹在中间的 x 不能被带进来。
		Assert.Equal(["a", "b", "c"], Names(result));
	}

	[Fact]
	public void Range_WithoutAUsableAnchor_FallsBackToASingleRow()
	{
		var a = R("a", 1);
		var b = R("b", 1);
		Row[] visible = [a, b];

		var result = Compute([], null, visible, b, TreeSelectionMode.Range);

		Assert.Equal(["b"], Names(result));
		Assert.Same(b, result.Anchor);
	}

	[Fact]
	public void Range_WithAnAnchorThatIsNoLongerVisible_FallsBackToASingleRow()
	{
		var a = R("a", 1);
		var b = R("b", 1);
		var collapsed = R("collapsed", 1);
		Row[] visible = [a, b];

		// 锚点被收起了（不在可见序列里）：退化成单选，而不是从错误的位置长区间。
		var result = Compute([collapsed], collapsed, visible, b, TreeSelectionMode.Range);

		Assert.Equal(["b"], Names(result));
	}

	[Fact]
	public void RepeatedRangeClicks_GrowFromTheSameAnchor()
	{
		var a = R("a", 1);
		var b = R("b", 1);
		var c = R("c", 1);
		var d = R("d", 1);
		Row[] visible = [a, b, c, d];

		var first = Compute([a], a, visible, b, TreeSelectionMode.Range);
		// 再 Shift 点更远的一行：锚点还是 a，所以区间是 a..c，而不是 b..c。
		var second = Compute(first.Nodes, first.Anchor, visible, c, TreeSelectionMode.Range);

		Assert.Equal(["a", "b", "c"], Names(second));
		Assert.Same(a, second.Anchor);
	}
}
