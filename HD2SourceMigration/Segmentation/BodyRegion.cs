namespace HD2SourceMigration.Segmentation;

/// <summary>
/// 身体本体的七大部位。槽位编号沿用 HD2 运行时的语义（<c>armor_registry_composition.lua</c>：
/// <c>slot 0</c> = 头盔、<c>1</c> = 披风、<c>2..9</c> = 身体槽），所以这里的映射是：
/// <list type="table">
/// <item><term>Head</term><description>slot 0（头盔槽，body = Any）</description></item>
/// <item><term>Chest</term><description>slot 2</description></item>
/// <item><term>Hips</term><description>slot 3</description></item>
/// <item><term>LeftLeg</term><description>slot 4</description></item>
/// <item><term>RightLeg</term><description>slot 5</description></item>
/// <item><term>LeftArm</term><description>slot 6</description></item>
/// <item><term>RightArm</term><description>slot 7</description></item>
/// </list>
/// <para>
/// <b>切分不影响形变。</b>七份各自保留原顶点的骨骼权重，用的是同一套 HD2 原生骨架，所以一个顶点
/// 归到"胸"还是"胯"<b>不会改变它怎么动</b>。切分只决定它归哪个护甲槽管——也就是"能不能被单独隐藏/替换"。
/// 这条性质很重要：它意味着边界只要<b>语义上说得通</b>就够了，不需要为了形变去凑。
/// </para>
/// </summary>
public enum BodyRegion
{
	Chest,
	Hips,
	LeftLeg,
	RightLeg,
	LeftArm,
	RightArm,
	Head,
}

public static class BodyRegions
{
	/// <summary>部位 → HD2 运行时槽位。</summary>
	public static uint Slot(this BodyRegion region) => region switch
	{
		BodyRegion.Head => 0,
		BodyRegion.Chest => 2,
		BodyRegion.Hips => 3,
		BodyRegion.LeftLeg => 4,
		BodyRegion.RightLeg => 5,
		BodyRegion.LeftArm => 6,
		BodyRegion.RightArm => 7,
		_ => throw new ArgumentOutOfRangeException(nameof(region)),
	};

	/// <summary>稳定、可写进文件的名字（用作 Unit 的可读标签）。</summary>
	public static string Name(this BodyRegion region) => region.ToString();

	public static BodyRegion? Parse(string name) =>
		Enum.TryParse<BodyRegion>(name, ignoreCase: true, out var value) ? value : null;

	/// <summary>七份的固定顺序：槽位从小到大。顺序稳定，所以产物可复现。</summary>
	public static IReadOnlyList<BodyRegion> All { get; } =
	[
		BodyRegion.Head,
		BodyRegion.Chest,
		BodyRegion.Hips,
		BodyRegion.LeftLeg,
		BodyRegion.RightLeg,
		BodyRegion.LeftArm,
		BodyRegion.RightArm,
	];
}
