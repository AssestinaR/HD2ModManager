using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using HD2ModCore.Infrastructure.KitInstance;
using AdaptAssetKey = HD2ModAdaptation.PatchReconstruction.AssetKey;
using AdaptPackageResolver = HD2ModAdaptation.PatchReconstruction.GameDataPackageResolver;

namespace HD2ModCore.Infrastructure.Sectional;

internal static class SeafSoldierUnitBuilder
{
	// SEAF 的 LOD 路由在近档同时加载装甲外覆件（0–8）和人物（9–17）。
	// 两组都保留 MeshInfo 和路由，人物组换成整身素体，原装甲缩成占位几何。
	private const int BodyLodStart = 9;

	internal static async ValueTask<SectionalMergedUnit> BuildAsync(
		KitNpcRecords.NpcRecord npc, SectionalMergedUnit source, string gameDataDirectory, CancellationToken cancellationToken)
	{
		var reader = new GameDataUnitMeshReader(new AdaptPackageResolver(gameDataDirectory));
		var shell = await reader.ReadAsync(npc.RootArchiveId, new AdaptAssetKey(PatchUnitMeshReader.UnitTypeId, npc.RootUnitId),
			cancellationToken: cancellationToken).ConfigureAwait(false);
		var layout = shell.Model;
		var bodyMeshes = layout.RawMeshData.Where(mesh => mesh.LodIndex >= BodyLodStart).OrderBy(mesh => mesh.LodIndex).ToArray();
		var armorMeshes = layout.RawMeshData.Where(mesh => mesh.LodIndex is >= 0 and < BodyLodStart).ToArray();
		if (layout.BonesRef != npc.BonesResourceId || layout.CompositeRef != 0
			|| !bodyMeshes.Select(mesh => mesh.LodIndex).SequenceEqual(Enumerable.Range(BodyLodStart, BodyLodStart))
			|| !armorMeshes.Select(mesh => mesh.LodIndex).Order().SequenceEqual(Enumerable.Range(0, BodyLodStart)))
			throw new InvalidDataException("当前游戏版本的 SEAF 骨架引用或网格分组已变更，无法生成替换 unit。");

		var donor = new UnitMeshReader().Read(source.TocData, source.GpuResourceData);
		var anchor = UnitLodFamilySource.ResolveAnchor(donor)
			?? throw new InvalidDataException("SEAF 映射的整身素体没有可见网格。");
		var donorFamily = UnitLodFamilySource.ResolveVisibleLodFamily(donor, anchor.Mesh).OrderBy(lod => lod.Raw.LodIndex).ToArray();
		if (donorFamily.Length == 0)
			throw new InvalidDataException("SEAF 映射的整身素体没有可用 LOD。");
		var (aliases, folded) = ResolveBones(donor, layout);
		var minifier = new CanonicalPlaceholderMinifier();
		var rawMeshes = layout.RawMeshData.Select(mesh =>
		{
			if (mesh.LodIndex is < 0 or >= BodyLodStart) return mesh;
			var placeholder = minifier.TryMinify(mesh, layout.Streams.Single(stream => stream.Index == mesh.StreamIndex));
			return placeholder.Mesh ?? throw new InvalidDataException($"SEAF LOD{mesh.LodIndex} 装甲占位生成失败。");
		}).ToArray();

		var inputs = new Dictionary<int, IReadOnlyList<CanonicalDecorationAppendInput>>();
		foreach (var target in bodyMeshes)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var sourceLod = donorFamily[Math.Min(target.LodIndex - BodyLodStart, donorFamily.Length - 1)];
			var fragment = new CanonicalDecorationFragment(sourceLod.Mesh, sourceLod.Raw with { LodIndex = target.LodIndex },
				donor.Streams.Single(stream => stream.Index == sourceLod.Raw.StreamIndex), donor.Materials,
				AtLod(donor.BoneInfos, sourceLod.Raw.LodIndex, target.LodIndex), donor.TransformInfo, aliases);
			inputs.Add(target.MeshInfoIndex, [new(fragment, npc.RootUnitId)]);
		}

		var host = new PatchUnitMesh(shell.Payload.Entry, shell.Payload, layout with { RawMeshData = rawMeshes });
		var rebuilt = new CanonicalDecorationUnitAppender().TryAppendLodFamily(host, inputs, layout.TransformInfo,
			replaceTargetGeometry: true, cullingBounds: CanonicalCullingBoundsMode.UnionWithComputed, uniqueSlotPerSection: true);
		if (!rebuilt.IsValid || rebuilt.Edit is not { } edit)
			throw new InvalidDataException("SEAF 本体重建失败：" + string.Join("；", rebuilt.Diagnostics.Select(item => item.Message).Distinct()));

		var entry = shell.Payload.Entry;
		var warnings = folded == 0 ? Array.Empty<string>() : new[] { $"SEAF：素体的 {folded} 个额外骨权重归并到了士兵骨架的上级骨。" };
		return new SectionalMergedUnit(edit.TocData, edit.GpuResourceData, shell.Payload.StreamData,
			entry.Unknown1, entry.Unknown2, entry.Unknown3, entry.Unknown4, warnings);
	}

	private static IReadOnlyList<UnitBoneInfo> AtLod(IReadOnlyList<UnitBoneInfo> source, int sourceLod, int targetLod)
	{
		if (sourceLod < 0 || sourceLod >= source.Count)
			throw new InvalidDataException($"SEAF 映射的素体 LOD{sourceLod} 没有蒙皮信息。");
		var palette = source[sourceLod];
		return Enumerable.Repeat(palette, targetLod + 1).ToArray();
	}

	private static (IReadOnlyList<uint> Aliases, int Folded) ResolveBones(UnitMeshModel donor, UnitMeshModel target)
	{
		var names = donor.TransformNameHashes;
		var supported = target.TransformNameHashes.ToHashSet();
		var used = donor.BoneInfos.SelectMany(info => info.RealIndices).ToHashSet();
		var aliases = names.ToArray();
		var folded = 0;
		foreach (var index in used)
		{
			if (index >= names.Count || names.Count != donor.TransformInfo.Entries.Count)
				throw new InvalidDataException("SEAF 映射的素体蒙皮引用了无效的骨骼索引。");
			var current = (int)index;
			var visited = new HashSet<int>();
			while (current < names.Count && visited.Add(current))
			{
				if (supported.Contains(names[current])) break;
				current = donor.TransformInfo.Entries[current].ParentIndex;
			}
			if (current >= names.Count || !supported.Contains(names[current]))
				throw new InvalidDataException($"素体骨 0x{names[(int)index]:x8} 在 SEAF 骨架上没有可用的上级骨。");
			aliases[(int)index] = names[current];
			if (current != index) folded++;
		}
		return (aliases, folded);
	}
}
