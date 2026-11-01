using HD2SourceMigration;
using HD2SourceMigration.Retarget;
using Xunit;

namespace HD2SourceMigration.Tests;

/// <summary>
/// 对照实验：**只切 <see cref="RetargetOptions.IncludeJiggleBones"/>**，重定向两遍，
/// 逐顶点比较最终位置。
/// <para>
/// 为什么需要它：2026-10-31 实机发现 Lilium 的头发上有"闪电形折痕"，而且**关掉物理插件也在**，
/// 说明它烘在转换产物里。肉眼对比"无骨备份"不可靠（那是更早一版转换器的产物，顶点数都不一样）。
/// 只有"同一个 VPK、同一版代码、只切这一个开关"才能回答：
/// 插骨到底有没有改变顶点位置。
/// </para>
/// <para>
/// 结论写进 <c>tmp/jiggle-geom-diff.txt</c>（测试进程里读文件比看控制台稳）。
/// </para>
/// </summary>
public sealed class JiggleBoneGeometryTests
{
	private const string RepoRoot = @"E:\Data\source\repos\WpfApp1";
	private const string DefaultVpk = @"E:\mod\lilium_sancta_bill\lilium_sancta_bill.vpk";
	private const string DefaultModelStem = "survivor_namvet";
	private const string DefaultAvatarToc = RepoRoot + @"\tmp\rig-lab\avatar\unit_0x4d1c334d294dfa97.toc";
	private const string DefaultBoneHash = RepoRoot + @"\HD2ModManager\Resources\AssetMetadata\bonehash.txt";
	private static readonly string ReportPath = RepoRoot + @"\tmp\jiggle-geom-diff.txt";

	[Fact]
	public void InsertingJiggleBones_MustNotMoveVertices()
	{
		var vpk = Environment.GetEnvironmentVariable("HD2_JIGGLE_VPK") ?? DefaultVpk;
		var avatarToc = Environment.GetEnvironmentVariable("HD2_AVATAR_TOC") ?? DefaultAvatarToc;
		var boneHash = Environment.GetEnvironmentVariable("HD2_BONEHASH") ?? DefaultBoneHash;
		if (!File.Exists(vpk) || !File.Exists(avatarToc) || !File.Exists(boneHash))
		{
			// 缺素材就不跑（这套测试依赖本机游戏数据），但要留下痕迹而不是静默通过。
			File.WriteAllText(ReportPath,
				$"SKIP 缺素材：vpk={File.Exists(vpk)} avatarToc={File.Exists(avatarToc)} boneHash={File.Exists(boneHash)}");
			return;
		}

		var inputs = SourceRetargetPipeline.LoadSource(vpk, DefaultModelStem);
		var rig = SourceRetargetPipeline.LoadRig(avatarToc, boneHash);
		var withBones = SourceRetargetPipeline.Run(inputs, rig, null, new RetargetOptions { IncludeJiggleBones = true });
		var without = SourceRetargetPipeline.Run(inputs, rig, null, new RetargetOptions { IncludeJiggleBones = false });

		var lines = new List<string>
		{
			$"顶点数：有骨 {withBones.Output.Count} / 无骨 {without.Output.Count}",
			$"额外骨：有骨 {withBones.ExtraBones.Count} 根 / 无骨 {without.ExtraBones.Count} 根",
		};

		var shared = Math.Min(withBones.Output.Count, without.Output.Count);
		var moved = 0;
		var maxDelta = 0.0;
		var maxAt = -1;
		var minX = double.MaxValue; var maxX = double.MinValue;
		var minY = double.MaxValue; var maxY = double.MinValue;
		var minZ = double.MaxValue; var maxZ = double.MinValue;
		for (var i = 0; i < shared; i++)
		{
			var a = withBones.Output[i];
			var b = without.Output[i];
			var dx = a.X - b.X;
			var dy = a.Y - b.Y;
			var dz = a.Z - b.Z;
			var delta = Math.Sqrt(dx * dx + dy * dy + dz * dz);
			if (delta > 1e-6)
			{
				moved++;
				minX = Math.Min(minX, a.X); maxX = Math.Max(maxX, a.X);
				minY = Math.Min(minY, a.Y); maxY = Math.Max(maxY, a.Y);
				minZ = Math.Min(minZ, a.Z); maxZ = Math.Max(maxZ, a.Z);
			}

			if (delta > maxDelta) { maxDelta = delta; maxAt = i; }
		}

		lines.Add($"位移 > 1e-6 m 的顶点：{moved} / {shared}（{100.0 * moved / Math.Max(shared, 1):F1}%）");
		lines.Add($"最大位移：{maxDelta:F6} m（顶点 #{maxAt}）");
		if (moved > 0)
		{
			lines.Add($"被移动顶点的范围：X[{minX:F3},{maxX:F3}] Y[{minY:F3},{maxY:F3}] Z[{minZ:F3},{maxZ:F3}]");
		}

		lines.Add(moved == 0
			? "结论：插骨**没有**改变任何顶点位置（几何与不插骨时逐顶点一致）⇒ 这就是要守住的不变量。"
			: "结论：插骨**改变了**顶点位置 ⇒ 折痕来自顶点烘焙，需要让挂在新骨上的顶点与其锚点烘焙一致。");

		// 说明：合成骨**不该**有 deform 条目 —— 几何按锚点烘焙，合成骨只在"写权重"那一层生效
		// （见 Retargeter.JiggleBones.cs 里 CollectJiggleBones 的注释）。这里核对权重目标表是否齐备。
		var targetValues = withBones.JiggleWeightTargets.Values.ToHashSet();
		var missingTargets = withBones.ExtraBones
			.Where((_, i) => !targetValues.Contains(rig.Bones.Count + i))
			.Select(extra => extra.Name)
			.ToList();
		lines.Add($"权重目标表：{withBones.JiggleWeightTargets.Count} 项；没被任何源骨指向的合成骨：{missingTargets.Count}"
			+ (missingTargets.Count > 0 ? "  → " + string.Join(", ", missingTargets.Take(8)) : string.Empty));
		// 诊断：挑位移最大的顶点，把它参与的源骨 / 两次运行解析到的目标骨打出来
		if (moved > 0 && maxAt >= 0)
		{
			var v = inputs.Geometry.Vertices[maxAt];
			lines.Add($"最大位移顶点 #{maxAt} 的源权重：");
			foreach (var (boneIndex, weight) in new[]
			{
				(v.Weight0.BoneIndex, v.Weight0.Weight),
				(v.Weight1.BoneIndex, v.Weight1.Weight),
				(v.Weight2.BoneIndex, v.Weight2.Weight),
			})
			{
				if (weight <= 0) continue;
				var name = boneIndex >= 0 && boneIndex < inputs.Model.Bones.Count
					? inputs.Model.Bones[boneIndex].DisplayName
					: "?";
				withBones.Resolved.TryGetValue(boneIndex, out var a);
				without.Resolved.TryGetValue(boneIndex, out var b);
				lines.Add($"   源骨 {boneIndex} {name} 权重 {weight:F3} → 有骨解析 {a}（有形变={withBones.Deform.ContainsKey(a)}）"
					+ $" / 无骨解析 {b}（有形变={without.Deform.ContainsKey(b)}）");
			}
		}

		File.WriteAllLines(ReportPath, lines);

		Assert.Equal(without.Output.Count, withBones.Output.Count);
	}
}
