using HD2SourceModel;

namespace HD2SourceMigration.VpkConversion;

/// <summary>
/// 转换期产出的物理参数 rig，落在 patch 旁边，等 <c>physbone embed-mod</c> 把它嵌进 <c>.gpu_resources</c>。
/// <para>
/// <b>为什么要有这个中间文件，而不是转换时直接嵌进去。</b>往 <c>.gpu_resources</c> 追加字节会让
/// 后续受管步骤的记账错位（历史症状：材质整体投递失败、面部品红），所以嵌入必须放在整批产物
/// 都落盘之后。但"链根锚点名"只有重定向那一刻才知道——<c>embed-mod</c> 只看得到来源 VPK，
/// 看不到 HD2 骨架，重算不出来。于是把答案<b>落在产物旁边</b>：转换期写，嵌入期读。
/// </para>
/// <para>
/// <b>为什么是 rig 二进制而不是 JSON。</b>它要带的就是 <c>HD2SRG01</c> 的全部内容
/// （骨名、父骨名、flags、bind 位置、29 个参数），而那个格式已经有了读写与校验。
/// 多造一种中间格式等于多一处会漂移的真相。
/// </para>
/// <para>
/// <b>旧 mod 不受影响。</b>没有这个文件时 <c>embed-mod</c> 仍按老路重读来源 VPK，
/// 行为与之前完全一致。
/// </para>
/// </summary>
public static class PhysBoneRigSidecar
{
	/// <summary>约定文件名。放在 <c>vpk2patch/</c>（和 <c>sections.json</c> 同级）。</summary>
	public const string FileName = "physbone-rig.bin";

	/// <summary>
	/// 把"源父骨显示名 → HD2 锚点名"应用到 jiggle 事实表上。
	/// <para>
	/// 只替换**键命中**的那些：源父骨是 ValveBiped 名（<c>ValveBiped.Bip01_Head1</c> 之类）时
	/// 运行时自己能映射，保留原名反而更稳（万一将来映射表变了，运行时那一份是活的）。
	/// </para>
	/// </summary>
	public static IReadOnlyList<SourceJiggleBoneFact> ApplyAnchors(
		IReadOnlyList<SourceJiggleBoneFact> facts,
		IReadOnlyDictionary<string, string>? anchors)
	{
		ArgumentNullException.ThrowIfNull(facts);
		if (anchors is null || anchors.Count == 0)
			return facts;

		var applied = new List<SourceJiggleBoneFact>(facts.Count);
		foreach (var fact in facts)
		{
			applied.Add(anchors.TryGetValue(fact.ParentName, out var anchor)
				? fact with { ParentName = anchor }
				: fact);
		}

		return applied;
	}

	/// <summary>
	/// 写出 rig 文件。没有 jiggle 骨时**不写**并返回 <c>null</c>——
	/// 空文件会让 <c>embed-mod</c> 走"有 rig"这条分支，从而不再报告"来源模型没有 jigglebone"。
	/// </summary>
	public static string? Write(
		string directory,
		IReadOnlyList<SourceJiggleBoneFact> facts,
		IReadOnlyDictionary<string, string>? anchors)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directory);
		ArgumentNullException.ThrowIfNull(facts);
		if (facts.Count == 0)
			return null;

		var path = Path.Combine(directory, FileName);
		File.WriteAllBytes(path, PhysBoneSpringRig.Serialize(ApplyAnchors(facts, anchors)));
		return path;
	}

	/// <summary>
	/// 在 mod 目录的几个约定位置找 rig 文件，找不到返回 <c>null</c>。
	/// <para>
	/// 顺序与 <see cref="PhysBoneContainerEmbedder"/> 遍历 patch 的顺序一致：
	/// 根 → <c>vpk2patch/</c> → <c>Overwrite/</c>。同一份 mod 里出现多个时以先找到的为准
	/// （正常只会有一个；有多个说明用户手工摆过，此时按"最靠近来源"的那个来）。
	/// </para>
	/// </summary>
	public static string? Locate(string modDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(modDirectory);
		foreach (var directory in new[]
		{
			modDirectory,
			Path.Combine(modDirectory, HD2ModAdaptation.PatchReconstruction.ModContentLayerNames.Sectional),
			Path.Combine(modDirectory, HD2ModAdaptation.PatchReconstruction.ModContentLayerNames.LegacySectional),
			Path.Combine(modDirectory, HD2ModAdaptation.PatchReconstruction.ModContentLayerNames.Overwrite),
		})
		{
			var path = Path.Combine(directory, FileName);
			if (File.Exists(path))
				return path;
		}

		return null;
	}

	/// <summary>读出来并校验 magic；不是 rig 就返回 <c>null</c>（宁可回退到重读 VPK，也不嵌一份坏数据）。</summary>
	public static byte[]? TryRead(string path)
	{
		if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
			return null;

		var bytes = File.ReadAllBytes(path);
		if (bytes.Length < 8 || !bytes.AsSpan(0, 8).SequenceEqual(PhysBoneSpringRig.Magic))
			return null;
		return bytes;
	}
}
