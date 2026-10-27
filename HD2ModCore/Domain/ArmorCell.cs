namespace HD2ModCore.Domain;

/// <summary>
/// 一件护甲上的"格子"：<b>部位槽</b> × <b>层</b>。
/// </summary>
/// <remarks>
/// 这两个数字不是一回事，是本项目最容易踩的坑之一：
/// <list type="bullet">
///   <item><description><c>Slot</c> 是游戏的 <c>CustomizationKitSlot</c>（0..9），由 <see cref="ArmorStructure"/> 映射到部位。</description></item>
///   <item><description><c>Layer</c> 是"该部位的第几个 unit 槽"（0=内层、1=护甲、2=装饰），即注册表里的 <c>piece_type</c>。</description></item>
/// </list>
/// 混用两者会让 5/6/7 档错位。显示名一律走 <see cref="ArmorStructure.DisplayPieceSlot"/>，
/// 别处不许再自己写一套"内层/护甲/装饰"。
///
/// 为什么把这两个数当"身份"：实测 2006 个原版 unit 里，一个 unit 全局只属于一个格子（零冲突）。
/// 所以格子是 unit 的身份，而不是"某件护甲的属性"——共享 unit 在多件护甲里是同一个格子。
/// </remarks>
public readonly record struct ArmorCell(int Slot, int Layer)
{
	/// <summary>未知坐标。SQLite 里也用它落库（-1），于是"没有权威归属"和"槽位 0"不会混淆。</summary>
	public const int UnknownCoordinate = -1;

	public static ArmorCell Unknown => new(UnknownCoordinate, UnknownCoordinate);

	public bool IsKnown => Slot >= 0 && Layer >= 0;

	public ArmorPart Part => IsKnown ? ArmorStructure.FromSlot((uint)Slot) : ArmorPart.Unknown;

	/// <summary>
	/// 第一人称下会被游戏隐藏的部位：胸（躯干）与头（头盔）。
	/// 这两个部位的部件**不能外挂到别的槽位**——外挂后会在第一人称里出现，糊住相机。
	/// 反过来，别的部位的部件也不该挂进这里：挂进去等于在第一人称里凭空消失。
	/// </summary>
	public bool IsFirstPersonHidden => Part is ArmorPart.Torso or ArmorPart.Helmet;

	/// <summary>
	/// 披风。渲染路径与其它部位不同：披风挂到别的部位、或别的部件挂进披风槽，都会渲染异常。
	/// 替换护甲链路双向排除披风。
	/// </summary>
	public bool IsCape => Part == ArmorPart.Cape;

	/// <summary>表格里用的一格显示：<c>躯干 · 内层</c>；没有权威归属时是 <c>未定位</c>。</summary>
	public string Display => IsKnown
		? $"{ArmorStructure.Display(Part)} · {ArmorStructure.DisplayPieceSlot((uint)Layer)}"
		: "未定位";

	public override string ToString() => Display;
}
