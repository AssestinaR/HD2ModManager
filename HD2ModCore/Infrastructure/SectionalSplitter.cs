using System.Globalization;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using HD2ModCore.Domain;
using AdaptationTocEntry = HD2ModAdaptation.PatchReconstruction.PatchTocEntry;
using AdaptationScanner = HD2ModAdaptation.PatchReconstruction.PatchTocScanner;
using AdaptationGameDataPackageResolver = HD2ModAdaptation.PatchReconstruction.GameDataPackageResolver;

namespace HD2ModCore.Infrastructure;

/// <summary>拆件的结果：新 patch 在哪、组表是什么、有没有需要用户知道的事。</summary>
public sealed record SectionalSplitResult(
	string PatchPath,
	SectionalSplitPlan Plan,
	SectionGroupDocument Groups,
	WardrobeSectionMapDocument Sections,
	IReadOnlyList<string> Warnings)
{
	public bool IsValid => PatchPath.Length != 0;
}

/// <summary>
/// 把一个"多 unit 的 legacy Mod"拆成"7 标准槽 × 身形"的分件产物。
/// <para>
/// <b>规划与执行分开</b>：规则全在 <see cref="SectionalSplitPlanner"/>（纯函数、可离线测），
/// 这里只负责读 patch、搬几何、写回去。这样"预演说会做什么"和"真做"共用同一份判断。
/// </para>
/// <para>
/// <b>两条关键取舍。</b>
/// </para>
/// <list type="number">
/// <item><b>打开 <c>uniqueSlotPerSection</c></b>：拆件的全部意义就是"每块几何都能单独开关"，
/// 而默认的材质槽复用会把共用材质的段并成一段（见 U0 实测）。代价是材质槽与
/// <c>BoneInfo.Remaps</c> 条数随段数增长。</item>
/// <item><b>打开 <c>UnionWithComputed</c></b>：不重算包围盒的话留下的是外壳的盒，
/// 顶点出盒会被游戏剔除（"整块几何消失但游戏不崩"）。保守并集只会变大不会变小。</item>
/// </list>
/// </summary>
public static class SectionalSplitter
{
	/// <summary>输出 patch 的文件名沿用源 patch 的名字——部署时靠同名顶掉，改名会变成两份。</summary>
	public static string ResolveOutputFileName(string sourcePatchPath)
		=> Path.GetFileName(sourcePatchPath);

	/// <summary>
	/// 只读预演：读源 patch、跑规划器、把结论说出来。<b>不写任何文件。</b>
	/// </summary>
	public static async ValueTask<(SectionalSplitPlan Plan, IReadOnlyList<string> Warnings)> PlanAsync(
		string sourcePatchPath,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sourcePatchPath);
		var sources = await ReadSourcesAsync(sourcePatchPath, cancellationToken).ConfigureAwait(false);
		var plan = SectionalSplitPlanner.Plan([.. sources.Units.Values.Select(unit => Describe(unit))]);
		var warnings = new List<string>(plan.Warnings);
		foreach (var skipped in sources.Skipped)
			warnings.Add($"{skipped}");
		return (plan, warnings);
	}

	/// <summary>
	/// 执行拆件：读源 patch → 按计划合并 → 写出新 patch + <c>sections.json</c> + <c>components.json</c>。
	/// </summary>
	/// <param name="gameDataDirectory">读头像骨架用（合并要按骨骼名对齐）。</param>
	/// <param name="outputDirectory">产物目录。为空则抛。</param>
	public static async ValueTask<SectionalSplitResult> ExecuteAsync(
		string sourcePatchPath,
		string gameDataDirectory,
		string outputDirectory,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sourcePatchPath);
		ArgumentException.ThrowIfNullOrWhiteSpace(gameDataDirectory);
		ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

		var warnings = new List<string>();
		var sources = await ReadSourcesAsync(sourcePatchPath, cancellationToken).ConfigureAwait(false);
		foreach (var skipped in sources.Skipped)
			warnings.Add(skipped);

		var plan = SectionalSplitPlanner.Plan([.. sources.Units.Values.Select(unit => Describe(unit))]);
		warnings.AddRange(plan.Warnings);
		if (plan.Targets.Count == 0)
			return new SectionalSplitResult(string.Empty, plan, SectionGroupDocument.Empty,
				EmptySections(sourcePatchPath), warnings);

		// 只有真的要合并时才去读头像骨架：一个"7 个 unit 各自占一个槽"的产物不需要它，
		// 而读骨架要开游戏 data，是个不该被无条件付出的成本。
		var needsMerge = plan.Targets.Any(target => target.DonorUnitIds.Count != 0);
		var avatar = needsMerge
			? await new CanonicalAvatarRigReader(new AdaptationGameDataPackageResolver(gameDataDirectory))
				.ReadTransformInfoAsync(cancellationToken: cancellationToken)
				.ConfigureAwait(false)
			: UnitTransformInfo.Empty;

		var appender = new CanonicalDecorationUnitAppender();
		var session = new CanonicalPatchSession();
		var originsByTarget = new Dictionary<string, IReadOnlyList<CanonicalAppendedSection>>(StringComparer.Ordinal);
		var donorIds = new HashSet<string>(StringComparer.Ordinal);
		foreach (var target in plan.Targets)
		{
			foreach (var donor in target.DonorUnitIds)
				donorIds.Add(donor);
		}

		foreach (var target in plan.Targets)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var host = sources.Units[target.HostUnitId];
			var anchor = UnitLodFamilySource.ResolveAnchor(host.Model);
			if (anchor is null)
			{
				warnings.Add($"{target.Slot}/{target.BodyType}：宿主 unit 没有可见网格，跳过。");
				continue;
			}

			var donors = target.DonorUnitIds.Select(id => sources.Units[id]).ToArray();
			var inputsByMesh = new Dictionary<int, IReadOnlyList<CanonicalDecorationAppendInput>>();
			foreach (var lodTarget in UnitLodFamilySource.ResolveVisibleLodFamily(host.Model, anchor.Value.Mesh))
			{
				var inputs = new List<CanonicalDecorationAppendInput>();
				foreach (var donor in donors)
				{
					// 按 LOD 序号取，不校验部位族：不同部位的 SemanticInfo 本来就不同（与跨甲合并同一条规则）。
					var donorMesh = UnitLodFamilySource.ResolveVisibleMeshAtLod(donor.Model, lodTarget.Raw.LodIndex);
					if (donorMesh is null)
						continue;
					inputs.Add(new CanonicalDecorationAppendInput(
						UnitLodFamilySource.CreateFragment(donor, donorMesh.Value.Mesh, donorMesh.Value.Raw),
						MaterialNamespace(donor.Entry.AssetKey.FileId)));
				}

				if (inputs.Count != 0)
					inputsByMesh[lodTarget.Mesh.Index] = inputs;
			}

			if (inputsByMesh.Count == 0 && donors.Length != 0)
			{
				warnings.Add($"{target.Slot}/{target.BodyType}：来源之间没有共同可见 LOD，只保留宿主几何。");
			}

			CanonicalDecorationAppendResult appended;
			if (inputsByMesh.Count == 0)
			{
				// 没有 donor：这个槽本来就只有一个来源，产物就是它自己，不需要重建。
				AddTargetOutputFromSource(session, host);
				originsByTarget[target.HostUnitId] = [];
				continue;
			}

			appended = appender.TryAppendLodFamily(
				host,
				inputsByMesh,
				avatar,
				cullingBounds: CanonicalCullingBoundsMode.UnionWithComputed,
				uniqueSlotPerSection: true);
			if (!appended.IsValid || appended.Edit is null)
			{
				foreach (var diagnostic in appended.Diagnostics)
					warnings.Add($"{target.Slot}/{target.BodyType}：{diagnostic.Code} {diagnostic.Message}");
				AddTargetOutputFromSource(session, host);
				originsByTarget[target.HostUnitId] = [];
				continue;
			}

			if (appended.Origins.Count == 0)
				warnings.Add($"{target.Slot}/{target.BodyType}：段来源账缺失，这个槽的变体组只能按整块 unit 表达。");
			originsByTarget[target.HostUnitId] = appended.Origins;
			AddTargetOutput(session, host, appended.Edit);
		}

		// 其余条目（材质、贴图、以及没被当成宿主的 unit）原样带过去；
		// **donor 必须排除**——它们的几何已经并进宿主，再带一份就是重复。
		foreach (var entry in sources.Entries)
		{
			if (entry.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId)
			{
				var key = entry.AssetKey.FileId.ToString("x16", CultureInfo.InvariantCulture);
				if (donorIds.Contains(key) || sources.Units.ContainsKey(key) && !originsByTarget.ContainsKey(key))
					continue;
				if (originsByTarget.ContainsKey(key))
					continue;
			}

			AddPassThrough(session, entry);
		}

		var finalized = session.Finalize(CanonicalDependencyClosureValidation.Valid);
		if (!finalized.IsValid)
		{
			foreach (var diagnostic in finalized.Diagnostics)
				warnings.Add($"依赖闭包不完整：{diagnostic.Code} {diagnostic.Message}");
			return new SectionalSplitResult(string.Empty, plan, SectionGroupDocument.Empty,
				EmptySections(sourcePatchPath), warnings);
		}

		Directory.CreateDirectory(outputDirectory);
		var fileName = ResolveOutputFileName(sourcePatchPath);
		await new CanonicalPatchWriter()
			.WriteAsync(session, outputDirectory, fileName, ReadHeaderTemplate(sourcePatchPath), overwriteExisting: true, cancellationToken)
			.ConfigureAwait(false);

		var patchPath = Path.Combine(outputDirectory, fileName);
		var sections = await WardrobeSectionMapFile.BuildAsync(patchPath, cancellationToken).ConfigureAwait(false);
		var groups = BuildGroups(plan, originsByTarget, sections.Document);
		return new SectionalSplitResult(patchPath, plan, groups, sections.Document, warnings);
	}

	/// <summary>
	/// 把"规划出来的组"翻译成组表。成员是<b>产物里的 section 下标</b>：
	/// 靠 <see cref="CanonicalAppendedSection"/> 的段来源账把 donor 的几何找出来。
	/// </summary>
	public static SectionGroupDocument BuildGroups(
		SectionalSplitPlan plan,
		IReadOnlyDictionary<string, IReadOnlyList<CanonicalAppendedSection>> originsByTarget,
		WardrobeSectionMapDocument sections)
	{
		ArgumentNullException.ThrowIfNull(plan);
		ArgumentNullException.ThrowIfNull(originsByTarget);
		ArgumentNullException.ThrowIfNull(sections);

		var groups = new List<SectionGroup>(plan.Groups.Count);
		foreach (var group in plan.Groups)
		{
			var targets = new List<SectionGroupTarget>();
			foreach (var unitId in group.MemberUnitIds)
			{
				var mapUnit = sections.Units.FirstOrDefault(unit =>
					string.Equals(unit.FileId, unitId, StringComparison.OrdinalIgnoreCase));
				if (mapUnit is null)
					continue;

				int[] indices;
				if (originsByTarget.TryGetValue(unitId, out var origins) && origins.Count != 0)
				{
					// 只取 LOD0 基准网格上的段：sections.json 的 section 下标就是它的段序。
					var canonicalMeshIndex = ResolveCanonicalMeshIndex(mapUnit, origins);
					indices = origins
						.Where(origin => origin.MeshInfoIndex == canonicalMeshIndex)
						.Select(origin => origin.FinalSectionIndex)
						.Distinct()
						.Order()
						.ToArray();
				}
				else
				{
					// 没有段来源账（这个槽本来就只有它一个来源，没走过追加）：
					// 它自己就是这个槽的全部几何 —— 组员就是它所有的节。
					indices = [.. mapUnit.Sections.Select(section => section.Index).Order()];
				}

				if (indices.Length == 0)
					continue;
				targets.Add(new SectionGroupTarget(mapUnit.FileId, indices));
			}

			groups.Add(new SectionGroup(group.Name, string.Empty, [], targets)
			{
				Source = group.MemberUnitIds.Count == 1 && plan.Targets.Any(target => target.HostUnitId == group.MemberUnitIds[0])
					? SectionGroupSource.DonorUnit
					: SectionGroupSource.DonorUnit,
				ExclusiveGroup = group.ExclusiveGroup,
			});
		}

		return new SectionGroupDocument(SectionGroupDocument.CurrentSchema, groups, []);
	}

	/// <summary>
	/// 段来源账里"哪个 mesh 是 LOD0 基准"。<c>sections.json</c> 的 section 下标取的是
	/// LOD0 里 section 最多的那个 mesh（与运行时同一条规则），所以这里必须用同一判据，
	/// 否则组员会指向别的几何。
	/// </summary>
	private static int ResolveCanonicalMeshIndex(WardrobeSectionMapUnit unit, IReadOnlyList<CanonicalAppendedSection> origins)
	{
		var firstSection = unit.Sections.FirstOrDefault();
		if (firstSection is null)
			return -1;
		// 每个 mesh 上都有全套段：取"段数等于 unit.SectionCount"的那个 mesh 里出现最多的 MeshInfoIndex。
		return origins
			.GroupBy(origin => origin.MeshInfoIndex)
			.OrderByDescending(group => group.Count())
			.Select(group => group.Key)
			.FirstOrDefault(-1);
	}

	private static void AddTargetOutput(CanonicalPatchSession session, PatchUnitMesh host, PatchUnitMeshEditResult edit)
	{
		var entry = new CanonicalPatchSessionEntry(
			host.Entry.AssetKey,
			CanonicalPatchEntryOwnership.TargetOutput,
			edit.TocData,
			edit.GpuResourceData,
			host.Entry.StreamSize == 0 ? Array.Empty<byte>() : null,
			host.Entry.Unknown1,
			host.Entry.Unknown2,
			host.Entry.Unknown3,
			host.Entry.Unknown4)
		{
			StreamDataSource = host.Entry.StreamSize == 0
				? null
				: new CanonicalPayloadSourceRange(
					host.Entry.SourceFilePath + ".stream", host.Entry.StreamOffset, host.Entry.StreamSize),
		};
		session.AddEntry(entry);
	}

	/// <summary>
	/// 没有 donor 的目标：这个槽本来就只有一个来源，产物就是它自己。
	/// <para>
	/// <b>仍然要声明成 <c>TargetOutput</c></b>：一是会话至少需要一个输出条目才定稿得了，
	/// 二是它确实就是"这一槽的输出"（只是字节没变）。字节从源 patch 直接引用，
	/// 所以这种槽的产物与源逐字节相同 —— 这也是个可断言的性质。
	/// </para>
	/// </summary>
	private static void AddTargetOutputFromSource(CanonicalPatchSession session, PatchUnitMesh host)
	{
		var entry = host.Entry;
		session.AddEntry(new CanonicalPatchSessionEntry(
			entry.AssetKey,
			CanonicalPatchEntryOwnership.TargetOutput,
			entry.TocDataSize == 0 ? Array.Empty<byte>() : null,
			entry.GpuResourceSize == 0 ? Array.Empty<byte>() : null,
			entry.StreamSize == 0 ? Array.Empty<byte>() : null,
			entry.Unknown1,
			entry.Unknown2,
			entry.Unknown3,
			entry.Unknown4)
		{
			TocDataSource = entry.TocDataSize == 0 ? null : new CanonicalPayloadSourceRange(entry.SourceFilePath, entry.TocDataOffset, entry.TocDataSize),
			GpuDataSource = entry.GpuResourceSize == 0 ? null : new CanonicalPayloadSourceRange(entry.SourceFilePath + ".gpu_resources", entry.GpuResourceOffset, entry.GpuResourceSize),
			StreamDataSource = entry.StreamSize == 0 ? null : new CanonicalPayloadSourceRange(entry.SourceFilePath + ".stream", entry.StreamOffset, entry.StreamSize),
		});
	}

	private static void AddPassThrough(CanonicalPatchSession session, PatchUnitMesh unit)
		=> AddPassThrough(session, unit.Entry);

	private static void AddPassThrough(CanonicalPatchSession session, AdaptationTocEntry entry)
	{
		var tocPath = entry.SourceFilePath;
		var gpuPath = tocPath + ".gpu_resources";
		var streamPath = tocPath + ".stream";
		session.AddEntry(new CanonicalPatchSessionEntry(
			entry.AssetKey,
			CanonicalPatchEntryOwnership.RequiredDependency,
			entry.TocDataSize == 0 ? Array.Empty<byte>() : null,
			entry.GpuResourceSize == 0 ? Array.Empty<byte>() : null,
			entry.StreamSize == 0 ? Array.Empty<byte>() : null,
			entry.Unknown1,
			entry.Unknown2,
			entry.Unknown3,
			entry.Unknown4)
		{
			TocDataSource = entry.TocDataSize == 0 ? null : new CanonicalPayloadSourceRange(tocPath, entry.TocDataOffset, entry.TocDataSize),
			GpuDataSource = entry.GpuResourceSize == 0 ? null : new CanonicalPayloadSourceRange(gpuPath, entry.GpuResourceOffset, entry.GpuResourceSize),
			StreamDataSource = entry.StreamSize == 0 ? null : new CanonicalPayloadSourceRange(streamPath, entry.StreamOffset, entry.StreamSize),
		});
	}

	/// <summary>
	/// 表头模板：新 patch 要沿用源 patch 的头部（版本、magic 之外的保留字段）。
	/// 只取前 72 字节——<c>CanonicalPatchWriter</c> 只从这里复制前缀。
	/// </summary>
	private static byte[]? ReadHeaderTemplate(string sourcePatchPath)
	{
		try
		{
			var buffer = new byte[72];
			using var stream = File.OpenRead(sourcePatchPath);
			var read = stream.Read(buffer, 0, buffer.Length);
			return read == 0 ? null : buffer[..read];
		}
		catch (IOException)
		{
			return null;
		}
	}

	private static WardrobeSectionMapDocument EmptySections(string sourcePatchPath)
		=> new(
			WardrobeSectionMapDocument.CurrentSchema,
			new WardrobeSectionMapPatch(Path.GetFullPath(sourcePatchPath), string.Empty, 0, null),
			WardrobeSectionMapDocument.ExpectedSectionRecordSize,
			WardrobeSectionMapDocument.ExpectedNumIndicesOffset,
			WardrobeSectionMapDocument.TocRelativeOffsets,
			[]);

	/// <summary>与跨甲合并同一个命名空间算法：不同来源各自的 slot 3 不该被认成同一材质。</summary>
	private static ulong MaterialNamespace(ulong unitFileId)
	{
		Span<byte> bytes = stackalloc byte[8];
		System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes, unitFileId);
		var hash = System.Security.Cryptography.SHA256.HashData(bytes);
		return System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(hash);
	}

	private static SectionalSplitSource Describe(PatchUnitMesh unit)
	{
		var semantic = unit.Model.Meshes.FirstOrDefault()?.SemanticInfo;
		return new SectionalSplitSource(
			unit.Entry.AssetKey.FileId.ToString("x16", CultureInfo.InvariantCulture),
			semantic?.Slot ?? string.Empty,
			semantic?.BodyType ?? SectionalSplitPlanner.DefaultBody,
			unit.Model.Meshes.Sum(mesh => mesh.Sections.Count),
			semantic?.Name ?? string.Empty);
	}

	private sealed record SourceSet(
		IReadOnlyList<AdaptationTocEntry> Entries,
		IReadOnlyDictionary<string, PatchUnitMesh> Units,
		IReadOnlyList<string> Skipped);

	private static async ValueTask<SourceSet> ReadSourcesAsync(string patchPath, CancellationToken cancellationToken)
	{
		var fullPath = Path.GetFullPath(patchPath);
		if (!File.Exists(fullPath))
			throw new FileNotFoundException($"找不到 patch 文件：{fullPath}", fullPath);

		var entries = await new AdaptationScanner().ScanEntriesAsync(fullPath, cancellationToken).ConfigureAwait(false);
		var reader = new PatchUnitMeshReader();
		var units = new Dictionary<string, PatchUnitMesh>(StringComparer.Ordinal);
		var skipped = new List<string>();
		foreach (var entry in entries)
		{
			if (entry.AssetKey.TypeId != PatchUnitMeshReader.UnitTypeId)
				continue;
			var fileId = entry.AssetKey.FileId.ToString("x16", CultureInfo.InvariantCulture);
			try
			{
				units[fileId] = await reader
					.ReadCanonicalSourceAsync(entry, entries, PatchUnitDependencyPolicy.RequirePatchLocalComposite, cancellationToken)
					.ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or ArgumentException or OverflowException)
			{
				skipped.Add($"unit {fileId} 读不出来，已跳过：{exception.Message}");
			}
		}

		return new SourceSet(entries, units, skipped);
	}
}
