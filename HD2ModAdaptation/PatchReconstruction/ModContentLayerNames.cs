namespace HD2ModAdaptation.PatchReconstruction;

/// <summary>
/// 一个 Mod 目录里三个内容层的目录名。
/// <para>
/// <b>为什么放在这里，而不是 <c>HD2ModCore</c>。</b>依赖方向是
/// <c>HD2ModCore → HD2ModAdaptation</c> 与 <c>HD2SourceMigration → HD2ModAdaptation</c>，
/// 反向不成立。所以需要同时被 Core 与 SourceMigration 用到的名字只能落在 Adaptation。
/// 以前这三处各自写了字面量 <c>"vpk2patch"</c>：改常量时全解决方案编译通过、单测全绿，
/// 但物理骨骼定位会静默返回 null、产物枚举会只剩根目录——参数悄悄消失，不报任何错。
/// </para>
/// </summary>
public static class ModContentLayerNames
{
	/// <summary>来源（用户丢进来的 patch 或 vpk）。不生效，只作权威来源。</summary>
	public const string Source = "";

	/// <summary>转换产物基底（unit patch 与全部材质 patch 平铺）；不直接生效，由 Overwrite 物化。</summary>
	public const string Sectional = "sectional";

	/// <summary>2026-11 之前基底目录的名字。只读兼容用。</summary>
	public const string LegacySectional = "vpk2patch";

	/// <summary>替换层：真正生效的产物。</summary>
	public const string Overwrite = "Overwrite";
}
