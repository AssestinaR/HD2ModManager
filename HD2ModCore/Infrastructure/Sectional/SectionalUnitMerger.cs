using System.Globalization;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using AdaptationScanner = HD2ModAdaptation.PatchReconstruction.PatchTocScanner;
using AdaptationGameDataPackageResolver = HD2ModAdaptation.PatchReconstruction.GameDataPackageResolver;
using AdaptPayloadReader = HD2ModAdaptation.PatchReconstruction.PatchEntryPayloadReader;

namespace HD2ModCore.Infrastructure.Sectional;

/// <summary>要合并的一组来源 unit：<see cref="SourceFileIds"/> 第一个当宿主，其余几何追加进它。</summary>
/// <param name="SourceFileIds">来源 unit（16 位小写 hex），宿主在前；至少 2 个才需要合并。</param>
internal sealed record SectionalUnitMergeSpec(IReadOnlyList<string> SourceFileIds);

/// <summary>合并后的 unit 载荷（尚未落到任何键上）。</summary>
internal sealed record SectionalMergedUnit(
	byte[] TocData,
	byte[] GpuResourceData,
	byte[] StreamData,
	ulong Unknown1,
	ulong Unknown2,
	uint Unknown3,
	uint Unknown4,
	IReadOnlyList<string> Warnings);

/// <summary>
/// 把 <c>sectional/</c> 里的多个来源 unit 的几何合成**一个** unit（P4b）。
/// <para>
/// 与旧版拆件（<see cref="SectionalSplitter"/>）同一套做法：宿主 = 第一个来源 unit，其余当 donor 追加进来，
/// 骨骼按名字哈希对齐，<c>BoneInfo.Remaps</c>、材质槽、LOD 族、剔除包围盒全部由
/// <see cref="CanonicalDecorationUnitAppender"/> 重算（每段独占材质槽 + 包围盒取并集）。
/// 区别只有一点：宿主与 donor 来自<b>不同部位</b>（躯干 + 手臂 + 腿…），而不是同一部位的多份拷贝。
/// </para>
/// <para>
/// 合并只读来源 patch 与玩家头像骨架（要游戏 data 目录），<b>不读目标原版 unit</b>：承载件只是借它的键。
/// </para>
/// </summary>
internal static class SectionalUnitMerger
{
	/// <summary>读头像骨架（合并按骨骼名对齐所需）。要读游戏 data，调用方只在真要合并时才调。</summary>
	internal static ValueTask<UnitTransformInfo> ReadAvatarAsync(string gameDataDirectory, CancellationToken cancellationToken)
		=> new CanonicalAvatarRigReader(new AdaptationGameDataPackageResolver(gameDataDirectory))
			.ReadTransformInfoAsync(cancellationToken: cancellationToken);

	/// <summary>
	/// 合并一组来源 unit。失败（读不出来、没有共同 LOD、追加报诊断）一律抛 <see cref="InvalidDataException"/>：
	/// 合并是「全有或全无」，半成品比失败更糟（少一块几何而用户看不出来）。
	/// </summary>
	internal static async Task<SectionalMergedUnit> MergeAsync(
		string sourcePatchPath,
		SectionalUnitMergeSpec spec,
		UnitTransformInfo avatar,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sourcePatchPath);
		ArgumentNullException.ThrowIfNull(spec);
		if (spec.SourceFileIds.Count == 0)
			throw new InvalidDataException("合并需要至少一个来源 unit。");

		var scanned = await new AdaptationScanner().ScanEntriesAsync(sourcePatchPath, cancellationToken).ConfigureAwait(false);
		var reader = new PatchUnitMeshReader();
		var units = new List<PatchUnitMesh>(spec.SourceFileIds.Count);
		foreach (var fileId in spec.SourceFileIds)
		{
			var entry = scanned.SingleOrDefault(candidate =>
					candidate.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId
					&& string.Equals(
						candidate.AssetKey.FileId.ToString("x16", CultureInfo.InvariantCulture), fileId, StringComparison.Ordinal))
				?? throw new InvalidDataException($"来源 unit 0x{fileId} 不在 patch 里，合并不了。");
			// 必须用保留 payload 的读取器：重建要拿宿主的 TocData 当「未知区间原样保留」的基线。
			units.Add(await reader
				.ReadAsync(entry, scanned, PatchUnitDependencyPolicy.RequirePatchLocalComposite, cancellationToken)
				.ConfigureAwait(false));
		}

		// 宿主选自定义骨（Avatar 里没有的节点）最全的那个来源：宿主有的骨 donor 不用再搬。合并结果最后写到承载件的键上，
		// 宿主是谁和写到哪里无关；并列时保持传入顺序（主承载部位在前）。
		var avatarHashes = avatar.NameHashes.ToHashSet();
		var ids = spec.SourceFileIds.ToList();
		var hostPick = Enumerable.Range(0, units.Count)
			.OrderByDescending(index => units[index].Model.TransformNameHashes.Count(hash => !avatarHashes.Contains(hash)))
			.ThenBy(index => index)
			.First();
		if (hostPick != 0)
		{
			(units[0], units[hostPick]) = (units[hostPick], units[0]);
			(ids[0], ids[hostPick]) = (ids[hostPick], ids[0]);
		}

		var host = units[0];
		var warnings = new List<string>();
		var payload = await new AdaptPayloadReader().ReadPayloadAsync(host.Entry, cancellationToken).ConfigureAwait(false);
		if (units.Count == 1)
		{
			// 没有 donor：产物就是宿主自己，字节不动。
			return new SectionalMergedUnit(
				payload.TocData, payload.GpuResourceData, payload.StreamData,
				host.Entry.Unknown1, host.Entry.Unknown2, host.Entry.Unknown3, host.Entry.Unknown4, warnings);
		}

		var anchor = UnitLodFamilySource.ResolveAnchor(host.Model)
			?? throw new InvalidDataException($"宿主 unit 0x{ids[0]} 没有可见网格。");
		var inputsByMesh = new Dictionary<int, IReadOnlyList<CanonicalDecorationAppendInput>>();
		foreach (var lodTarget in UnitLodFamilySource.ResolveVisibleLodFamily(host.Model, anchor.Mesh))
		{
			var inputs = new List<CanonicalDecorationAppendInput>();
			for (var index = 1; index < units.Count; index++)
			{
				var donor = units[index];
				var donorMesh = UnitLodFamilySource.ResolveVisibleMeshAtLod(donor.Model, lodTarget.Raw.LodIndex);
				if (donorMesh is null)
				{
					warnings.Add($"来源 0x{ids[index]} 没有 LOD{lodTarget.Raw.LodIndex}：这一级 LOD 上它会消失（远处）。");
					continue;
				}

				inputs.Add(new CanonicalDecorationAppendInput(
					UnitLodFamilySource.CreateFragment(donor, donorMesh.Value.Mesh, donorMesh.Value.Raw),
					MaterialNamespace(donor.Entry.AssetKey.FileId)));
			}

			if (inputs.Count != 0)
				inputsByMesh[lodTarget.Mesh.Index] = inputs;
		}

		if (inputsByMesh.Count == 0)
			throw new InvalidDataException("来源之间没有共同可见 LOD，合并不了。");

		var appended = new CanonicalDecorationUnitAppender().TryAppendLodFamily(
			host,
			inputsByMesh,
			avatar,
			cullingBounds: CanonicalCullingBoundsMode.UnionWithComputed,
			uniqueSlotPerSection: true,
			allowTargetOwnedBones: true);
		if (!appended.IsValid || appended.Edit is null)
		{
			throw new InvalidDataException(
				"合并几何失败：" + string.Join("；", appended.Diagnostics.Select(item => $"{item.Code} {item.Message}")));
		}

		return new SectionalMergedUnit(
			appended.Edit.TocData,
			appended.Edit.GpuResourceData,
			payload.StreamData,
			host.Entry.Unknown1, host.Entry.Unknown2, host.Entry.Unknown3, host.Entry.Unknown4,
			warnings);
	}

	/// <summary>与旧版拆件、跨甲合并同一个命名空间算法：不同来源各自的材质槽不会被当成同一个。</summary>
	private static ulong MaterialNamespace(ulong unitFileId)
	{
		Span<byte> bytes = stackalloc byte[8];
		System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes, unitFileId);
		var hash = System.Security.Cryptography.SHA256.HashData(bytes);
		return System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(hash);
	}
}
