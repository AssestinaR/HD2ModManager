namespace HD2SourceMigration.VpkConversion;

/// <summary>
/// 半透明落地方式的**实验开关**（默认 <see cref="None"/> = 现有行为）。
/// <para>
/// <b>为什么要有它。</b>源侧的 <c>$translucent</c> 与"渐变 alpha 的 <c>$alphatest</c>"目前
/// 一律降级成 <c>alphaclip</c>（硬裁，阈值 0.5），渐变的 76% 中间值被压成硬边。
/// HD2 自带的 <c>translucent</c> 模板（父 id <c>9576304397847579354</c>）确实能做混合，
/// 但它**没有反照率槽**（唯一槽是 <c>NormalXyRoughnessOpacity</c>），只能靠
/// <c>BaseColor</c> 平涂——能不能接受、以及 <c>advanced</c> 会不会混合，
/// 都只能实机 A/B 才知道。这个开关就是为那一次对照准备的。
/// </para>
/// <para>
/// <b>关键约定：实验开关不改变材质 id。</b>三个变体必须同 id，模型 patch 才能不变、
/// 换材质包即可对照（否则每换一次都要重新换甲）。
/// </para>
/// </summary>
public enum AlphaExperiment
{
	/// <summary>现有行为：需要透明就 <c>alphaclip</c>，否则 <c>advanced</c>。</summary>
	None = 0,

	/// <summary>
	/// 路线 1：<c>translucent</c> 模板 + 把源 alpha 写进 <c>NormalXyRoughnessOpacity</c> 的 A，
	/// 颜色靠 <c>BaseColor</c> 平涂（取底色平均值）。
	/// </summary>
	Translucent = 1,

	/// <summary>
	/// 路线 2：<c>advanced</c> 模板 + 把源 alpha 写进 <c>SclarFieldOpacity</c> 槽，
	/// 看它到底会混合还是只做裁切。
	/// </summary>
	AdvancedOpacity = 2,
}
