namespace HD2SourceMigration.Retarget;

/// <summary>一根源骨在手臂里的分段。</summary>
public enum SourceArmPart
{
	/// <summary>这条祖先链上认不出任何手臂骨——披风、躯干、腿上的辅助骨都是这一类。</summary>
	None,

	Upper,
	Forearm,
	Hand,

	/// <summary>锁骨/肩。属于手臂，但不在长度补偿的斜坡上。</summary>
	Shoulder,
}

/// <summary>
/// 源骨 → 手臂分段的分类。两条规则，缺一不可。
/// <para>
/// <b>一、按名字。</b> 源骨名带 <c>ValveBiped.Bip01_</c> 前缀，剥掉之后 <c>L_</c>/<c>R_</c> 开头再认
/// <c>UpperArm</c>/<c>Forearm</c>/<c>Hand</c>/<c>Finger</c>/<c>Clavicle</c>/<c>Shoulder</c>。
/// </para>
/// <para>
/// <b>二、认不出名字时沿源骨架<b>上溯</b>到最近的、名字能认出来的祖先。</b> 这一条不是保险，
/// 是必需的：这些模型带 60 根读不出名字的 helper 骨（<see cref="HD2SourceModel.SourceBone.DisplayName"/>
/// 把它们叫 <c>source_extra_{index}</c>），其中 <c>source_extra_78</c> 挂在手上、它的顶点就落在腕上。
/// 只按名字判会把它归成"不是手臂"，于是小臂长度补偿
/// （<c>Retargeter.Pose.cs</c>）里的 <c>(arm &amp;&amp; tube ? 1 : 0)</c> 把它<b>钉死</b>，
/// 而它的 <c>L_Hand</c> 邻居整体平移一个补偿量——同一条边上权重跳 <b>1.0</b>，
/// 腕上那几十个顶点就被折成"一层翻过来的皮"。
/// </para>
/// <para>
/// 上溯同时保住了另一件事：披风、躯干上的辅助骨，祖先链上只有脊柱/腿骨，走到底得到
/// <see cref="SourceArmPart.None"/>，仍然不动。所以这条规则不会把披风拖走。
/// </para>
/// <para>
/// 实测（Lunalice_Zoey，左手腕）：本模型腕区 t/L 0.95–1.10 里 <c>arm=false</c> 的顶点
/// 100% 是 <c>source_extra_78</c>；把整条网格按源坐标重算二面角，源网格在该窗口只有 2 条边
/// 超过 150 度、我们的输出有 22 条。加上上溯之后腕区回到 <b>0</b> 条。
/// </para>
/// </summary>
public static class SourceArmBones
{
	/// <summary>
	/// 判断一根源骨属于手臂的哪一段。
	/// </summary>
	/// <param name="side">HD2 侧的小写 <c>l</c> / <c>r</c>；源骨名对应 <c>L_</c> / <c>R_</c>。</param>
	/// <param name="names">逐骨显示名（含 <c>source_extra_{index}</c> 兜底），下标 = 源骨下标。</param>
	/// <param name="parents">逐骨父下标；<c>-1</c> 表示根。</param>
	/// <param name="boneIndex">要判的源骨下标。</param>
	public static SourceArmPart Classify(
		string side,
		IReadOnlyList<string> names,
		IReadOnlyList<int> parents,
		int boneIndex)
	{
		var prefix = side.ToUpperInvariant() + "_";
		var current = boneIndex;
		for (var hops = 0; current >= 0 && current < names.Count && hops <= RetargetConstants.MaxSourceAncestorHops; hops++)
		{
			var part = ClassifySelf(side, names[current]);
			if (part != SourceArmPart.None)
				return part;

			if (current >= parents.Count)
				break;
			current = parents[current];
		}

		return SourceArmPart.None;
	}

	/// <summary>
	/// 只看这根骨<b>自己</b>的名字，不上溯。<see cref="Classify"/> 的每一跳都用它，
	/// 对拍测试也用它来区分"名字认得出"和"靠祖先才认得出"。
	/// </summary>
	public static SourceArmPart ClassifySelf(string side, string? name)
	{
		var text = name ?? string.Empty;
		if (text.StartsWith(RetargetConstants.SourceBonePrefix, StringComparison.Ordinal))
			text = text[RetargetConstants.SourceBonePrefix.Length..];
		var prefix = side.ToUpperInvariant() + "_";
		if (!text.StartsWith(prefix, StringComparison.Ordinal))
			return SourceArmPart.None;

		var tail = text[prefix.Length..];
		if (tail.StartsWith("UpperArm", StringComparison.Ordinal))
			return SourceArmPart.Upper;
		if (tail.StartsWith("Forearm", StringComparison.Ordinal))
			return SourceArmPart.Forearm;
		if (tail.StartsWith("Hand", StringComparison.Ordinal)
			|| tail.StartsWith("Finger", StringComparison.Ordinal))
			return SourceArmPart.Hand;
		if (tail.StartsWith("Clavicle", StringComparison.Ordinal)
			|| tail.StartsWith("Shoulder", StringComparison.Ordinal))
			return SourceArmPart.Shoulder;
		return SourceArmPart.None;
	}

	/// <summary>这根骨（或其祖先）在手臂上。</summary>
	public static bool IsArm(SourceArmPart part) => part != SourceArmPart.None;

	/// <summary>小臂 / 手 / 手指——长度补偿里"整体刚性平移"的那一类。</summary>
	public static bool IsDistal(SourceArmPart part) => part is SourceArmPart.Forearm or SourceArmPart.Hand;

	/// <summary>手或手指——圆管闸必须豁免的那一类。</summary>
	public static bool IsHand(SourceArmPart part) => part is SourceArmPart.Hand;
}
