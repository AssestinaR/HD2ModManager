using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Infrastructure.Sectional;
using Xunit.Abstractions;

namespace HD2ModCore.Tests;

/// <summary>
/// P4b 几何合并在真实 VPK 产物（雫）上的离线检查：骨骼、材质槽、LOD、Remaps、包围盒。
/// 样本由环境变量给出（<c>HD2_TEST_SECTIONAL_MOD</c> = 带 <c>sectional/</c> 的 VPK Mod 目录，
/// <c>HD2_TEST_GAME_DATA</c> = 游戏 data 目录，读头像骨架用）；缺样本时静默跳过。
/// </summary>
public sealed class SectionalUnitMergerRealDataTests(ITestOutputHelper output)
{
	private static (string Patch, string GameData)? Locate()
	{
		var mod = Environment.GetEnvironmentVariable("HD2_TEST_SECTIONAL_MOD");
		var game = Environment.GetEnvironmentVariable("HD2_TEST_GAME_DATA");
		if (string.IsNullOrWhiteSpace(mod) || string.IsNullOrWhiteSpace(game) || !Directory.Exists(game)) return null;
		var sectional = Path.Combine(mod, "sectional");
		if (!Directory.Exists(sectional)) return null;
		var patch = Directory.EnumerateFiles(sectional, "*.patch_0").OrderBy(path => path, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
		return patch is null ? null : (patch, game);
	}

	private static readonly string[] Body = ["079d12e38aec0002", "079d12e38aec0003", "079d12e38aec0004", "079d12e38aec0005", "079d12e38aec0006", "079d12e38aec0007"];

	private void Dump(string label, UnitMeshModel model)
	{
		output.WriteLine($"== {label}: meshes={model.Meshes.Count} bones={model.BoneInfos.Count} materials={model.Materials.Count} streams={model.Streams.Count}");
		foreach (var mesh in model.Meshes.OrderBy(item => item.LodIndex))
		{
			var bone = model.BoneInfos.FirstOrDefault(item => item.Index == mesh.LodIndex || item.Index == mesh.Index);
			output.WriteLine(
				$"  mesh#{mesh.Index} lod={mesh.LodIndex} sections={mesh.Sections.Count} slots={mesh.MaterialSlotIds.Count} tris={mesh.Sections.Sum(section => (int)(section.NumIndices / 3))} "
				+ $"bounds=[{string.Join(",", mesh.CullingBounds.Values.Select(value => value.ToString("0.00")))}] visual={mesh.SemanticInfo.IsVisualMesh}");
		}

		foreach (var bone in model.BoneInfos)
			output.WriteLine($"  boneInfo#{bone.Index} numBones={bone.NumBones} realIdx={bone.RealIndices.Count} remaps={bone.Remaps.Count}");
	}

	[Fact]
	public async Task ProbeWhichBonesAreOutsideTheAvatar()
	{
		if (Locate() is not { } located) return;
		var avatar = await SectionalUnitMerger.ReadAvatarAsync(located.GameData, default);
		var avatarHashes = avatar.NameHashes.ToHashSet();
		var scanned = await new HD2ModAdaptation.PatchReconstruction.PatchTocScanner().ScanEntriesAsync(located.Patch, default);
		var reader = new HD2ModAdaptation.PatchReconstruction.UnitMesh.PatchUnitMeshReader();
		foreach (var entry in scanned.Where(entry => entry.AssetKey.TypeId == HD2ModAdaptation.PatchReconstruction.UnitMesh.PatchUnitMeshReader.UnitTypeId))
		{
			var unit = await reader.ReadAsync(entry, scanned, HD2ModAdaptation.PatchReconstruction.UnitMesh.PatchUnitDependencyPolicy.RequirePatchLocalComposite, default);
			var all = unit.Model.TransformNameHashes;
			output.WriteLine($"  has046d44f9: inAvatar={avatarHashes.Contains(0x046d44f9u)} inUnitTransforms={all.Contains(0x046d44f9u)}");
			output.WriteLine($"unit {entry.AssetKey.FileId:x16}: transforms={all.Count} notInAvatar={all.Count(hash => !avatarHashes.Contains(hash))} lods={unit.Model.BoneInfos.Count}");
			for (var lod = 0; lod < unit.Model.BoneInfos.Count; lod++)
			{
				var palette = unit.Model.BoneInfos[lod].RealIndices.Select(index => all[(int)index]).ToArray();
				var outside = palette.Where(hash => !avatarHashes.Contains(hash)).Distinct().ToArray();
				output.WriteLine($"  lod{lod}: palette={palette.Length} outsideAvatar={outside.Length} [{string.Join(",", outside.Take(60).Select(hash => "0x" + hash.ToString("x8")))}]");
			}
		}
	}

	/// <summary>
	/// 回归（2026-11-14 实机失败）：别的 VPK Mod（蚀骨魂龙）的胯 unit 带 19 根自定义骨，躯干宿主一根都没有。
	/// 合并必须把 donor 独有的自定义骨连同父链搬进宿主，而不是抛 “does not contain required bone hash”。
	/// 对任何带 <c>sectional/</c> 的 VPK Mod 都成立：把它的全部 unit（含头）合成一个，逐条断言。
	/// </summary>
	[Fact]
	public async Task MergingEveryUnitOfAModWorksEvenWhenTheHostLacksTheDonorsCustomBones()
	{
		if (Locate() is not { } located) return;
		var avatar = await SectionalUnitMerger.ReadAvatarAsync(located.GameData, default);
		var scanned = await new HD2ModAdaptation.PatchReconstruction.PatchTocScanner().ScanEntriesAsync(located.Patch, default);
		var ids = scanned
			.Where(entry => entry.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId)
			.Select(entry => entry.AssetKey.FileId.ToString("x16", System.Globalization.CultureInfo.InvariantCulture))
			.Order().ToArray();
		if (ids.Length < 2) return;

		// 每个 unit 单独读出来：自己的调色板骨（哈希）与 LOD0 三角形。
		var reader = new PatchUnitMeshReader();
		var singles = new Dictionary<string, (HashSet<uint> Bones, int Triangles)>();
		foreach (var id in ids)
		{
			var entry = scanned.Single(item => item.AssetKey.FileId.ToString("x16", System.Globalization.CultureInfo.InvariantCulture) == id
				&& item.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId);
			var unit = await reader.ReadAsync(entry, scanned, PatchUnitDependencyPolicy.RequirePatchLocalComposite, default);
			var palette = unit.Model.BoneInfos[0].RealIndices.Select(index => unit.Model.TransformNameHashes[(int)index]).ToHashSet();
			var lod0 = unit.Model.Meshes.Single(mesh => mesh.LodIndex == 0 && mesh.SemanticInfo.IsVisualMesh);
			singles[id] = (palette, lod0.Sections.Sum(section => (int)(section.NumIndices / 3)));
		}

		// 每个 unit 轮流排第一（传入顺序不该影响成败），全部合成一个。
		foreach (var first in ids)
		{
			var order = ids.OrderBy(id => id == first ? 0 : 1).ThenBy(id => id, StringComparer.Ordinal).ToArray();
			var merged = await SectionalUnitMerger.MergeAsync(located.Patch, new SectionalUnitMergeSpec(order), avatar);
			var model = new UnitMeshReader().Read(merged.TocData, merged.GpuResourceData);
			var lod0 = model.Meshes.Single(mesh => mesh.LodIndex == 0 && mesh.SemanticInfo.IsVisualMesh);
			Assert.Equal(singles.Values.Sum(item => item.Triangles), lod0.Sections.Sum(section => (int)(section.NumIndices / 3)));
			Assert.Equal(lod0.Sections.Count, lod0.MaterialSlotIds.Distinct().Count());
			// 每个来源用到的骨，合并后的节点表里都有（donor 独有的自定义骨被搬进来了）。
			var mergedHashes = model.TransformNameHashes.ToHashSet();
			foreach (var (id, single) in singles)
				Assert.True(single.Bones.IsSubsetOf(mergedHashes), $"first={first}: {id} 的调色板骨没有全部进合并结果");
			// 父链完整：节点表里每个节点的父下标都在表内。
			foreach (var entry in model.TransformInfo.Entries)
				Assert.True(entry.ParentIndex < model.TransformInfo.NameHashes.Count);
			var bone = model.BoneInfos[0];
			Assert.Equal(lod0.MaterialSlotIds.Count, bone.Remaps.Count);
			Assert.Equal((int)bone.NumBones, bone.RealIndices.Count);
		}
	}

	[Fact]
	public async Task MergingAllBodyUnitsIntoOneProducesAWellFormedUnit()
	{
		if (Locate() is not { } located) return;
		var present = await new HD2ModAdaptation.PatchReconstruction.PatchTocScanner().ScanEntriesAsync(located.Patch, default);
		if (!Body.All(id => present.Any(entry => entry.AssetKey.FileId.ToString("x16", System.Globalization.CultureInfo.InvariantCulture) == id))) return;   // 这条只认「雫」的 unit id
		var avatar = await SectionalUnitMerger.ReadAvatarAsync(located.GameData, default);

		var merged = await SectionalUnitMerger.MergeAsync(located.Patch, new SectionalUnitMergeSpec(Body), avatar);
		var model = new UnitMeshReader().Read(merged.TocData, merged.GpuResourceData);
		Dump("merged(6)", model);
		foreach (var warning in merged.Warnings) output.WriteLine("warn: " + warning);
		Assert.Empty(merged.Warnings);   // 每个来源在每一级 LOD 上都有网格

		var sourceLods = new Dictionary<string, UnitMeshModel>();
		foreach (var id in Body)
		{
			var single = await SectionalUnitMerger.MergeAsync(located.Patch, new SectionalUnitMergeSpec([id]), avatar);
			sourceLods[id] = new UnitMeshReader().Read(single.TocData, single.GpuResourceData);
		}

		var visible = model.Meshes.Where(mesh => mesh.LodIndex >= 0 && mesh.SemanticInfo.IsVisualMesh).OrderBy(mesh => mesh.LodIndex).ToArray();
		var hostVisible = sourceLods[Body[0]].Meshes.Where(mesh => mesh.LodIndex >= 0 && mesh.SemanticInfo.IsVisualMesh).ToArray();
		// LOD：与宿主同样多级，每一级都带齐全部来源的 section（每段独占材质槽）。
		Assert.Equal(hostVisible.Length, visible.Length);
		foreach (var mesh in visible)
		{
			var expected = Body.Sum(id => sourceLods[id].Meshes.Single(item => item.LodIndex == mesh.LodIndex && item.SemanticInfo.IsVisualMesh).Sections.Count);
			Assert.Equal(expected, mesh.Sections.Count);
			Assert.Equal(mesh.Sections.Count, mesh.MaterialSlotIds.Distinct().Count());
			// 三角形一个不少。
			var triangles = Body.Sum(id => sourceLods[id].Meshes.Single(item => item.LodIndex == mesh.LodIndex && item.SemanticInfo.IsVisualMesh).Sections.Sum(section => (int)(section.NumIndices / 3)));
			Assert.Equal(triangles, mesh.Sections.Sum(section => (int)(section.NumIndices / 3)));
			// 包围盒：合并后的盒必须包住宿主自己的盒（并集只会变大不会变小；盒外的顶点会被游戏剔除）。
			// 只比宿主：别的来源的盒在它们各自的局部坐标系里，合并时顶点已换算到宿主坐标系，直接比没有意义。
			var box = mesh.CullingBounds.Values;
			foreach (var id in Body.Take(1))
			{
				var sourceBox = sourceLods[id].Meshes.Single(item => item.LodIndex == mesh.LodIndex && item.SemanticInfo.IsVisualMesh).CullingBounds.Values;
				for (var axis = 0; axis < 3; axis++)
				{
					Assert.True(box[axis] <= sourceBox[axis] + 1e-4f, $"LOD{mesh.LodIndex} min[{axis}] {box[axis]} > {id} {sourceBox[axis]}");
					Assert.True(box[axis + 3] >= sourceBox[axis + 3] - 1e-4f, $"LOD{mesh.LodIndex} max[{axis}] {box[axis + 3]} < {id} {sourceBox[axis + 3]}");
				}
			}
		}

		// 骨骼：每级 LOD 的调色板一致，Remaps 条数 = 材质槽数，骨数不少于任一来源。
		Assert.Equal(model.BoneInfos.Count, hostVisible.Length);
		foreach (var bone in model.BoneInfos)
		{
			Assert.Equal(visible[0].MaterialSlotIds.Count, bone.Remaps.Count);
			Assert.Equal((int)bone.NumBones, bone.RealIndices.Count);
			Assert.Equal(bone.RealIndices.Count, bone.RealIndices.Distinct().Count());
			foreach (var id in Body)
				Assert.True(bone.RealIndices.Count >= (int)sourceLods[id].BoneInfos[0].NumBones);
		}
	}
}
