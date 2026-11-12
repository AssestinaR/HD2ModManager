using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2SourceMigration.Retarget;
using HD2SourceMigration.Segmentation;
using HD2SourceMigration.VpkConversion;
using Xunit;

namespace HD2SourceMigration.Tests;

/// <summary>
/// 真实资产上的"按件切骨"：**每个 unit 只该带自己蒙到的那几根物理骨**。
/// <para>
/// <b>为什么必须有一条打在真数据上的用例。</b>纯逻辑层（<see cref="ExtraBoneSubsetTests"/>）
/// 只能证明"给定用到的骨哈希，切得对"；而这条要证的是<b>真实几何确实按件分开了</b> ——
/// 如果某个模型的布料被复制进每个 unit（分段装甲为了按 section 开关，确实会把身体网格复制过去），
/// 那么切分就一分钱也省不下来，而运行时日志里仍然只是"已挂载 N 个 unit"，看不出区别。
/// </para>
/// <para>
/// 判据来自<b>顶点影响</b>（骨名哈希），不是材质名猜的：分段模型的 unit = 一批
/// <see cref="MeshGroup"/> 的三角形，而顶点权重是转换期手里的精确答案。
/// </para>
/// </summary>
public sealed class ExtraBoneSubsetRealDataTests
{
	private const string RepoRoot = @"E:\Data\source\repos\WpfApp1";
	private const string DefaultVpk = @"E:\mod\Lunalice_Bill\Lunalice_Bill.vpk";
	private const string DefaultAvatarToc = RepoRoot + @"\tmp\rig-lab\avatar\unit_0x4d1c334d294dfa97.toc";
	private const string DefaultBoneHash = RepoRoot + @"\HD2ModManager\Resources\AssetMetadata\bonehash.txt";

	private static readonly string ReportPath = Directory.Exists(RepoRoot + @"\tmp")
		? RepoRoot + @"\tmp\physbone-subset-per-unit.txt"
		: Path.Combine(Path.GetTempPath(), "physbone-subset-per-unit.txt");

	[Fact]
	public void RealData_EachUnitCarriesOnlyThePhysicsBonesItIsSkinnedTo()
	{
		var vpk = Environment.GetEnvironmentVariable("HD2_SUBSET_VPK") ?? DefaultVpk;
		var toc = Environment.GetEnvironmentVariable("HD2_AVATAR_TOC") ?? DefaultAvatarToc;
		var boneHash = Environment.GetEnvironmentVariable("HD2_BONEHASH") ?? DefaultBoneHash;
		if (!File.Exists(vpk) || !File.Exists(toc) || !File.Exists(boneHash))
		{
			// 缺本机素材就跳过，但要留痕迹（静默通过会让这条用例永远不知道自己没跑）。
			File.WriteAllText(ReportPath,
				$"SKIP 缺素材：vpk={File.Exists(vpk)} avatarToc={File.Exists(toc)} boneHash={File.Exists(boneHash)}");
			return;
		}

		var inputs = SourceRetargetPipeline.LoadSource(vpk);
		var rig = SourceRetargetPipeline.LoadRig(toc, boneHash);
		var result = SourceRetargetPipeline.Run(inputs, rig, null, new RetargetOptions { IncludeJiggleBones = true });
		var extras = result.ExtraBones;
		var lines = new List<string>
		{
			$"模型 {inputs.ModelStem}：顶点 {inputs.Geometry.Vertices.Count} / 三角形 {inputs.Geometry.Triangles.Count}"
				+ $" / 原生骨 {rig.Bones.Count} / 合成骨 {extras.Count}",
		};
		if (extras.Count == 0)
		{
			File.WriteAllLines(ReportPath, [.. lines, "这份资产没有物理骨，用例不适用。"]);
			return;
		}

		// 与生产路径同序：先追加合成骨，再建网格（`VpkPatchAssembler` 的 ①b / ②）。
		var baseCount = rig.Bones.Count;
		var avatarInfo = UnitMeshReader.ReadTransformInfoFromUnitToc(File.ReadAllBytes(toc));
		Assert.Equal(baseCount, avatarInfo.NameHashes.Count);
		var fullInfo = UnitTransformInfoAppender.Append(avatarInfo, extras)!;
		var split = SourceSegmentation.Split(inputs.Model, inputs.Geometry, new BodyRegionResolver(rig), result.Resolved);

		// 链根：沿 ParentIndex 一路往上，直到落在原生骨上。
		var rootOf = new int[extras.Count];
		for (var index = 0; index < extras.Count; index++)
		{
			var current = index;
			for (var guard = 0; guard < 64; guard++)
			{
				var parent = extras[current].ParentIndex;
				if (parent < baseCount)
					break;
				current = parent - baseCount;
			}

			rootOf[index] = current;
		}

		var totalSelected = 0;
		var anyPruned = false;
		lines.Add("件 | 顶点 | 三角形 | 物理骨 | 其中链数 | 锚点分布");
		foreach (var group in split.Groups)
		{
			var mesh = RetargetedMeshBuilder.Build(group, inputs.Geometry, result, fullInfo);
			var selection = ExtraBoneSubset.Select(extras, baseCount, mesh);
			Assert.Null(selection.Warning);
			Assert.InRange(selection.Extras.Count, 0, extras.Count);
			totalSelected += selection.Extras.Count;
			if (selection.Extras.Count < extras.Count)
				anyPruned = true;

			// 裁完之后父链必须仍然完整：每个父要么是原生骨，要么排在它前面。
			for (var position = 0; position < selection.Extras.Count; position++)
			{
				var parent = selection.Extras[position].ParentIndex;
				Assert.True(parent < baseCount || parent < baseCount + position,
					$"{group.Name} 的 {selection.Extras[position].Name} 父下标 {parent} 没有排在它前面");
			}

			// 这条 unit 的几何必须真的用到了它带进来的每一根骨（否则就是白带）。
			var usedByMesh = new HashSet<uint>();
			foreach (var vertex in mesh.Vertices)
			{
				foreach (var influence in vertex.Influences)
					usedByMesh.Add(influence.BoneHash);
			}

			Assert.All(selection.Extras, extra => Assert.Contains(extra.NameHash, usedByMesh));

			var chains = selection.Extras
				.Select(extra => IndexOf(extras, extra.NameHash))
				.Where(index => index >= 0)
				.Select(index => rootOf[index])
				.Distinct()
				.Count();
			var anchors = selection.Extras
				.Where(extra => extra.ParentIndex < baseCount)
				.GroupBy(extra => rig.Bones[extra.ParentIndex].Name ?? "?")
				.OrderBy(pair => pair.Key)
				.Select(pair => $"{pair.Key}×{pair.Count()}");
			lines.Add($"{group.Name} | {mesh.VertexCount} | {mesh.TriangleCount} | {selection.Extras.Count} "
				+ $"| {chains} | {string.Join(" ", anchors)}");
		}

		lines.Add("");
		lines.Add($"合计 {totalSelected} 根；若每件都带全量会是 {split.Groups.Count * extras.Count} 根。");
		File.WriteAllLines(ReportPath, lines);

		// 只有"多件"的模型才谈得上切分：单件模型本来就该带全量。
		if (split.Groups.Count > 1)
		{
			Assert.True(anyPruned, "没有任何一件被裁过 —— 按件切骨没生效（每件都带着整套物理骨）。");
			Assert.True(totalSelected < split.Groups.Count * extras.Count,
				$"按件切完之后总量 {totalSelected} 仍不小于 {split.Groups.Count}×{extras.Count}。");
		}
	}

	private static int IndexOf(IReadOnlyList<RetargetExtraBone> extras, uint hash)
	{
		for (var index = 0; index < extras.Count; index++)
		{
			if (extras[index].NameHash == hash)
				return index;
		}

		return -1;
	}
}
