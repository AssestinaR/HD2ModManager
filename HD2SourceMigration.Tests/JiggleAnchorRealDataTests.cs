using HD2SourceModel;
using HD2SourceMigration.Retarget;
using HD2SourceMigration.VpkConversion;

namespace HD2SourceMigration.Tests;

/// <summary>
/// 真实资产验收：**每一条 jiggle 链根都必须能锚到一根真实存在的 HD2 骨**。
/// <para>
/// 这条钉的是「某块布完全不晃」的最常见死因。运行时挂链根的顺序是：
/// 先拿容器里的父骨名查源→HD2 映射表，查不到就退化成"把父骨名当 HD2 骨名查"。
/// 而源骨架里有一批骨在 HD2 侧根本不存在——无名骨 <c>source_extra_90</c>、
/// 非 Biped 命名的 <c>UN_Skirt_R_02</c>——两条路都查不到，于是整条链记「缺锚点」被跳过。
/// 实测 <c>lime un bill</c> 的裙子上 25 条链死 5 条，日志里就是反复的「缺锚点 6 / 7」。
/// </para>
/// <para>
/// 修法是在转换期把"这根父骨回退到了哪根 HD2 骨"的<b>名字</b>写进容器
/// （<see cref="Retargeter.PlanJiggleAnchors"/> + <see cref="PhysBoneRigSidecar"/>）。
/// 这条测试直接用运行时那两步判据去验结果。
/// </para>
/// </summary>
public sealed class JiggleAnchorRealDataTests
{
	private const string DefaultVpkPath = @"E:\mod\lime un bill\lime un bill.vpk";

	private const string DefaultAvatarTocPath =
		@"E:\Data\source\repos\WpfApp1\tmp\rig-lab\avatar\unit_0x4d1c334d294dfa97.toc";

	private const string DefaultBoneHashPath =
		@"E:\Data\source\repos\WpfApp1\tmp\pb-ref\HD2PhysBone-ReferenceSource-main\kit\tools\bonehash.txt";

	private static string? Resolve(string variable, string fallback)
	{
		var configured = Environment.GetEnvironmentVariable(variable);
		if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
			return configured;
		return File.Exists(fallback) ? fallback : null;
	}

	private static string ShortName(string name) =>
		name.StartsWith(SourceToHd2RigMap.SourceBonePrefix, StringComparison.Ordinal)
			? name[SourceToHd2RigMap.SourceBonePrefix.Length..]
			: name;

	/// <summary>
	/// 模拟运行时解析链根锚点的两步（<c>M.anchor_name</c> 的映射表 + 回退把名字当 HD2 骨名查）。
	/// 刻意用 C# 侧的对应表而不是抄一份 Lua 的映射表：两边漂移时这里会先炸。
	/// </summary>
	private static bool RuntimeCanResolve(Hd2Rig rig, string parentName)
	{
		var shortName = ShortName(parentName);
		var mapped = SourceToHd2RigMap.Correspondence
			.Where(pair => string.Equals(pair.SourceBone, shortName, StringComparison.Ordinal))
			.Select(pair => pair.Hd2Node)
			.FirstOrDefault();
		return rig.Find(mapped ?? parentName) is not null;
	}

	/// <summary>链根 = 父骨不是另一根 jiggle 骨的那些（与运行时 <c>build_chains</c> 同口径）。</summary>
	private static IEnumerable<string> ChainRootParentNames(IReadOnlyList<SourceJiggleBoneFact> facts)
	{
		var jiggleIndices = facts.Select(fact => fact.BoneIndex).ToHashSet();
		return facts
			.Where(fact => !jiggleIndices.Contains(fact.ParentBoneIndex))
			.Select(fact => fact.ParentName)
			.Distinct(StringComparer.Ordinal);
	}

	[Fact]
	public void RealData_EveryJiggleChainRootResolvesToAnExistingHd2Bone()
	{
		var vpkPath = Resolve("HD2_ANCHOR_TEST_VPK", DefaultVpkPath);
		var tocPath = Resolve("HD2_AVATAR_TOC", DefaultAvatarTocPath);
		var boneHashPath = Resolve("HD2_BONEHASH", DefaultBoneHashPath);
		if (vpkPath is null || tocPath is null || boneHashPath is null)
			return;

		var inputs = SourceRetargetPipeline.LoadSource(vpkPath);
		var rig = SourceRetargetPipeline.LoadRig(tocPath, boneHashPath);
		var result = SourceRetargetPipeline.Run(inputs, rig);

		var facts = SourceModelInspector.JiggleFacts(inputs.Model);
		// 资产在、但里面没有 jiggle 骨 = 拿错模型了（多模型 VPK 里 LargestMdl 不一定是要验的那份）。
		// 这种情况必须炸，否则整条测试会在空集上"通过"，看着是绿的其实什么都没验。
		Assert.True(facts.Count > 0, $"{Path.GetFileName(vpkPath)} 里选到的模型没有 jiggle 骨，验不到这条路径。");

		var roots = ChainRootParentNames(facts).ToList();
		Assert.NotEmpty(roots);

		// 先确认这份资产**确实**会踩到坑：否则下面的断言是在空集上通过，等于什么都没验。
		var brokenBefore = roots.Where(name => !RuntimeCanResolve(rig, name)).ToList();
		Assert.True(brokenBefore.Count > 0,
			$"这份资产没有需要锚点修复的链根（共 {roots.Count} 条链根，"
			+ $"未应用锚点时能解析 {roots.Count - brokenBefore.Count} 条），"
			+ "换一份带裙摆/缎带的资产才能验到这条路径。");

		var applied = PhysBoneRigSidecar.ApplyAnchors(facts, result.JiggleAnchors);
		var stillBroken = ChainRootParentNames(applied)
			.Where(name => !RuntimeCanResolve(rig, name))
			.ToList();

		Assert.True(stillBroken.Count == 0,
			$"这些链根修完仍然锚不上（{stillBroken.Count} 条）：{string.Join("、", stillBroken)}");
	}

	[Fact]
	public void RealData_AnchorsAreNamesThatActuallyExistInTheHd2Rig()
	{
		// 与上一条互补：上一条判"运行时能不能解析"，这条判"我们写进去的东西本身是合法的 HD2 骨名"。
		// 分开是有意义的——写进去一个不存在的名字，运行时会再退回原名，症状和没修一样。
		var vpkPath = Resolve("HD2_ANCHOR_TEST_VPK", DefaultVpkPath);
		var tocPath = Resolve("HD2_AVATAR_TOC", DefaultAvatarTocPath);
		var boneHashPath = Resolve("HD2_BONEHASH", DefaultBoneHashPath);
		if (vpkPath is null || tocPath is null || boneHashPath is null)
			return;

		var inputs = SourceRetargetPipeline.LoadSource(vpkPath);
		var rig = SourceRetargetPipeline.LoadRig(tocPath, boneHashPath);
		var result = SourceRetargetPipeline.Run(inputs, rig);
		// 锚点为空就是"没写任何东西"，没有可校验的条目。
		// 这条测试不负责判"该不该有锚点"——那是上一条的活。
		if (result.JiggleAnchors.Count == 0)
			return;

		var missing = result.JiggleAnchors
			.Where(pair => rig.Find(pair.Value) is null)
			.Select(pair => $"{pair.Key} → {pair.Value}")
			.ToList();

		Assert.True(missing.Count == 0,
			$"这些锚点名在 HD2 骨架里不存在：{string.Join("、", missing)}");
	}
}
