using System.Globalization;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using HD2ModCore.Domain;
using AdaptationTocEntry = HD2ModAdaptation.PatchReconstruction.PatchTocEntry;
using AdaptationAssetKey = HD2ModAdaptation.PatchReconstruction.AssetKey;
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

	/// <summary>
	/// 为了折叠成单个产物 patch 而被删掉的源 patch 数量（输入是单 patch 或输出到别的目录时为 0）。
	/// </summary>
	public int RemovedSourcePatchCount { get; init; }
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
	/// <summary>输出 patch 的文件名沿用**最低序号源 patch** 的名字——部署时靠同名顶掉，改名会变成两份。</summary>
	public static string ResolveOutputFileName(string sourcePatchPath)
		=> Path.GetFileName(sourcePatchPath);

	/// <summary>
	/// 枚举一个 Mod 目录里的 patch，**按 patch 序号升序**。
	/// <para>
	/// 序号是文件名末尾的 <c>.patch_N</c>。升序即"覆盖顺序"：数字大的后写、覆盖数字小的，
	/// 与游戏侧叠加顺序一致。
	/// </para>
	/// </summary>
	public static IReadOnlyList<string> EnumeratePatchFiles(string modDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(modDirectory);
		if (!Directory.Exists(modDirectory))
			throw new DirectoryNotFoundException($"找不到 Mod 目录：{modDirectory}");
		return [.. Directory
			.EnumerateFiles(modDirectory, "*.patch_*", SearchOption.TopDirectoryOnly)
			// sidecar 也叫 *.patch_N.stream / .gpu_resources，这里只要主文件。
			.Where(path => !path.EndsWith(".stream", StringComparison.OrdinalIgnoreCase)
				&& !path.EndsWith(".gpu_resources", StringComparison.OrdinalIgnoreCase))
			.OrderBy(ParsePatchIndex)
			.ThenBy(path => path, StringComparer.OrdinalIgnoreCase)];
	}

	/// <summary>从 <c>xxx.patch_N</c> 里取 N；解析不出来就排到最后（当"最高优先"看会掩盖问题，所以排最后）。</summary>
	internal static int ParsePatchIndex(string path)
	{
		var name = Path.GetFileName(path);
		var marker = name.LastIndexOf(".patch_", StringComparison.OrdinalIgnoreCase);
		if (marker < 0) return int.MaxValue;
		return int.TryParse(name[(marker + ".patch_".Length)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
			? index
			: int.MaxValue;
	}

	/// <summary>
	/// 只读预演：读源 → 跑规划器 → 把结论说出来。<b>不写任何文件。</b>
	/// </summary>
	/// <param name="sourcePatchOrDirectory">
	/// Mod 目录，或单个 patch 文件。<b>给目录时会把目录里所有 patch 当成一个集合叠加</b>
	/// （同一 asset key 以 patch 序号较大者为胜者），见 <see cref="ReadSourcesAsync"/>。
	/// </param>
	public static async ValueTask<(SectionalSplitPlan Plan, IReadOnlyList<string> Warnings)> PlanAsync(
		string sourcePatchOrDirectory,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sourcePatchOrDirectory);
		var sources = await ReadSourcesAsync(sourcePatchOrDirectory, cancellationToken).ConfigureAwait(false);
		var plan = SectionalSplitPlanner.Plan([.. sources.Units.Values.Select(unit => Describe(unit))]);
		var warnings = new List<string>(plan.Warnings);
		foreach (var skipped in sources.Skipped)
			warnings.Add($"{skipped}");
		return (plan, warnings);
	}

	/// <summary>
	/// 执行拆件：读源 → 按计划合并 → 写出新 patch + <c>sections.json</c> + <c>components.json</c>。
	/// </summary>
	/// <param name="sourcePatchOrDirectory">
	/// Mod 目录，或单个 patch 文件。给目录时把所有 patch 叠加成一个集合
	/// （同一 asset key 以 patch 序号较大者为胜者），**并且只写一个输出 patch**
	/// ——<c>sections.json</c> 的格式只能描述一个 patch，所以叠加后的胜者集合必须折叠成一份。
	/// 被折叠掉的源 patch 会在写成功后从目录里删掉，否则它们会重新覆盖产物。
	/// </param>
	/// <param name="gameDataDirectory">读头像骨架用（合并要按骨骼名对齐）。</param>
	/// <param name="outputDirectory">产物目录。为空则抛。</param>
	public static async ValueTask<SectionalSplitResult> ExecuteAsync(
		string sourcePatchOrDirectory,
		string gameDataDirectory,
		string outputDirectory,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sourcePatchOrDirectory);
		ArgumentException.ThrowIfNullOrWhiteSpace(gameDataDirectory);
		ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

		var warnings = new List<string>();
		var sources = await ReadSourcesAsync(sourcePatchOrDirectory, cancellationToken).ConfigureAwait(false);
		foreach (var skipped in sources.Skipped)
			warnings.Add(skipped);

		var plan = SectionalSplitPlanner.Plan([.. sources.Units.Values.Select(unit => Describe(unit))]);
		warnings.AddRange(plan.Warnings);
		if (plan.Targets.Count == 0)
			return new SectionalSplitResult(string.Empty, plan, SectionGroupDocument.Empty,
				EmptySections(sources.HeaderTemplatePatchPath), warnings);

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
				EmptySections(sources.HeaderTemplatePatchPath), warnings);
		}

		Directory.CreateDirectory(outputDirectory);
		var fileName = ResolveOutputFileName(sources.HeaderTemplatePatchPath);
		// 头部模板必须在覆盖之前读：搬回去之后源文件就没了。
		var headerTemplate = ReadHeaderTemplate(sources.HeaderTemplatePatchPath);

		// **就地写会把自己锁死。** writer 用 `FileShare.None` 打开目标文件，而它拷贝 payload 时
		// 读的又是**源 patch**——产物名与源 patch 同名同目录时，同一份文件既要写又要读，
		// Windows 上直接报"文件正被另一进程使用"。所以就地覆盖时先进暂存目录，写完再搬回去。
		var outputFull = Path.GetFullPath(outputDirectory);
		var sourceFull = Path.GetFullPath(Path.GetDirectoryName(sources.HeaderTemplatePatchPath) ?? string.Empty);
		var inPlace = string.Equals(outputFull, sourceFull, StringComparison.OrdinalIgnoreCase);
		var writeDirectory = inPlace
			? Path.Combine(outputDirectory, ".sectional-staging-" + Guid.NewGuid().ToString("N"))
			: outputDirectory;
		try
		{
			await new CanonicalPatchWriter()
				.WriteAsync(session, writeDirectory, fileName, headerTemplate, overwriteExisting: true, cancellationToken)
				.ConfigureAwait(false);
			if (inPlace)
			{
				foreach (var produced in ProducedFileNames(fileName))
					MoveIfExists(Path.Combine(writeDirectory, produced), Path.Combine(outputDirectory, produced));
			}
		}
		finally
		{
			if (inPlace) TryDeleteDirectory(writeDirectory);
		}

		var patchPath = Path.Combine(outputDirectory, fileName);
		var sections = await WardrobeSectionMapFile.BuildAsync(patchPath, cancellationToken).ConfigureAwait(false);
		var groups = BuildGroups(plan, originsByTarget, sections.Document);

		// 两份 sidecar 必须跟着产物一起落盘，否则产物"能用但不可编辑"：
		// sections.json 是组件树与烘焙的唯一偏移来源，components.json 是组表。
		await WardrobeSectionMapFile
			.WriteAsync(Path.Combine(outputDirectory, WardrobeSectionMapFile.FileName), sections.Document, cancellationToken)
			.ConfigureAwait(false);
		await SectionGroupFile
			.WriteAsync(outputDirectory, groups, cancellationToken)
			.ConfigureAwait(false);

		// 叠加成一个 patch 之后，被折叠掉的源 patch **必须删掉**：它们还在目录里就会在部署时
		// 重新覆盖产物，用户看到的是"拆了但没生效"。只在"产物写回源目录"时才删——
		// 输出到别的目录时源目录不该被碰。
		var consumed = RemoveConsumedPatches(sources.ConsumedPatches, outputDirectory, Path.GetFullPath(patchPath), warnings);

		return new SectionalSplitResult(patchPath, plan, groups, sections.Document, warnings) { RemovedSourcePatchCount = consumed };
	}

	/// <summary>writer 一次产出的三个文件（主文件 + 两份 sidecar）。</summary>
	private static IReadOnlyList<string> ProducedFileNames(string patchFileName)
		=> [patchFileName, patchFileName + ".stream", patchFileName + ".gpu_resources"];

	private static void MoveIfExists(string source, string destination)
	{
		if (!File.Exists(source)) return;
		File.Move(source, destination, overwrite: true);
	}

	private static void TryDeleteDirectory(string directory)
	{
		try
		{
			if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
		}
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}

	/// <summary>
	/// 删掉参与叠加、但**没有成为产物**的那些源 patch（含它们的 <c>.stream</c> / <c>.gpu_resources</c>）。
	/// 返回删掉的数量。产物目录与源目录不一致时什么都不做。
	/// </summary>
	internal static int RemoveConsumedPatches(
		IReadOnlyList<string> consumedPatches,
		string outputDirectory,
		string producedPatchPath,
		List<string> warnings)
	{
		if (consumedPatches.Count <= 1) return 0;
		var outputFull = Path.GetFullPath(outputDirectory);
		var sourceDirectory = Path.GetFullPath(Path.GetDirectoryName(consumedPatches[0]) ?? string.Empty);
		if (!string.Equals(outputFull, sourceDirectory, StringComparison.OrdinalIgnoreCase)) return 0;

		var removed = 0;
		foreach (var patch in consumedPatches)
		{
			var full = Path.GetFullPath(patch);
			if (string.Equals(full, producedPatchPath, StringComparison.OrdinalIgnoreCase)) continue;
			foreach (var path in new[] { full, full + ".stream", full + ".gpu_resources" })
			{
				try
				{
					if (File.Exists(path)) File.Delete(path);
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
				{
					// 删不掉就明说：残留的 patch 会重新覆盖产物，这是会让用户困惑的那种失败。
					warnings.Add($"无法删除已被折叠的源 patch「{Path.GetFileName(path)}」：{exception.Message}"
						+ "。它会在部署时重新覆盖产物，请手动删除。");
				}
			}
			removed++;
		}
		if (removed != 0)
			warnings.Add($"已把 {removed + 1} 个源 patch 折叠为 1 个，并删除被折叠的 {removed} 个。");
		return removed;
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

		// 段来源账是**按目标 unit 记**的：一条 (MeshInfoIndex, FinalSectionIndex, InputIndex, SourceSectionIndex)。
		// 其中 InputIndex = -1 表示"目标自己原有的段"，>= 0 表示"第几个 donor 贡献的段"。
		// 所以要把 donor 的几何找出来，必须先知道它是**哪个目标的第几个 donor**。
		var donorSlot = new Dictionary<string, (string HostUnitId, int InputIndex)>(StringComparer.Ordinal);
		var hostIds = new HashSet<string>(StringComparer.Ordinal);
		foreach (var target in plan.Targets)
		{
			hostIds.Add(target.HostUnitId);
			for (var index = 0; index < target.DonorUnitIds.Count; index++)
				donorSlot[target.DonorUnitIds[index]] = (target.HostUnitId, index);
		}

		var groups = new List<SectionGroup>(plan.Groups.Count);
		foreach (var group in plan.Groups)
		{
			var members = new List<SectionGroupTarget>();
			var source = SectionGroupSource.Manual;
			foreach (var unitId in group.MemberUnitIds)
			{
				// 谁承载这个 unit 的几何：自己就是宿主，或者是某个目标的第 k 个 donor。
				string hostUnitId;
				int inputIndex;
				if (hostIds.Contains(unitId))
				{
					hostUnitId = unitId;
					inputIndex = -1;
					source = SectionGroupSource.DonorUnit;
				}
				else if (donorSlot.TryGetValue(unitId, out var slot))
				{
					hostUnitId = slot.HostUnitId;
					inputIndex = slot.InputIndex;
					source = SectionGroupSource.DonorUnit;
				}
				else
				{
					continue;
				}

				var mapUnit = sections.Units.FirstOrDefault(unit =>
					string.Equals(unit.FileId, hostUnitId, StringComparison.OrdinalIgnoreCase));
				if (mapUnit is null)
					continue;

				int[] indices;
				if (originsByTarget.TryGetValue(hostUnitId, out var origins) && origins.Count != 0)
				{
					// 只取 LOD0 基准网格上的段：sections.json 的 section 下标就是它的段序。
					var canonicalMeshIndex = ResolveCanonicalMeshIndex(mapUnit, origins);
					indices = origins
						.Where(origin => origin.MeshInfoIndex == canonicalMeshIndex && origin.InputIndex == inputIndex)
						.Select(origin => origin.FinalSectionIndex)
						.Distinct()
						.Order()
						.ToArray();
				}
				else if (inputIndex < 0)
				{
					// 没有段来源账（这个槽本来就只有它一个来源，没走过追加）：
					// 它自己就是这个槽的全部几何 —— 组员就是它所有的节。
					indices = [.. mapUnit.Sections.Select(section => section.Index).Order()];
				}
				else
				{
					// 有 donor 却没有段来源账：说不出 donor 落在哪几段。宁可空着也不猜。
					indices = [];
				}

				if (indices.Length == 0)
					continue;
				members.Add(new SectionGroupTarget(mapUnit.FileId, indices));
			}

			groups.Add(new SectionGroup(group.Name, string.Empty, [], members)
			{
				Source = source,
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
			semantic?.Name ?? string.Empty,
			semantic?.PieceType ?? string.Empty);
	}

	private sealed record SourceSet(
		IReadOnlyList<AdaptationTocEntry> Entries,
		IReadOnlyDictionary<string, PatchUnitMesh> Units,
		IReadOnlyList<string> Skipped,
		/// <summary>参与叠加的源 patch，按序号升序。第一个同时充当输出文件名与头部模板的来源。</summary>
		IReadOnlyList<string> ConsumedPatches)
	{
		/// <summary>输出 patch 的文件名与头部模板取它。</summary>
		public string HeaderTemplatePatchPath => ConsumedPatches[0];
	}

	/// <summary>
	/// 读出"叠加后的条目表"。参数是目录时把目录里所有 patch 当成一个集合，是文件时等价于单个。
	/// <para>
	/// 规则：按 patch 序号升序扫描，**同一 <c>asset key</c> 以序号较大者为胜者**（与游戏侧加载顺序一致）。
	/// 败者完全不进结果表——产物只写一个 patch，留一份败者就等于把死内容打进产物。
	/// 条目顺序保持"首次出现的位置"，只把内容换成胜者，所以同一批输入的产物是确定的。
	/// </para>
	/// <para>internal 是为了让测试能直接验证叠加规则，不必先造出可拆件的 unit。</para>
	/// </summary>
	internal static async ValueTask<(IReadOnlyList<AdaptationTocEntry> Entries, IReadOnlyList<string> ConsumedPatches, IReadOnlyList<string> Skipped)> ReadMergedEntriesAsync(
		string patchOrDirectory,
		CancellationToken cancellationToken = default)
	{
		var fullPath = Path.GetFullPath(patchOrDirectory);
		IReadOnlyList<string> patches;
		if (Directory.Exists(fullPath))
		{
			patches = EnumeratePatchFiles(fullPath);
			if (patches.Count == 0)
				throw new InvalidOperationException($"这个目录里没有 patch 文件：{fullPath}");
		}
		else if (File.Exists(fullPath))
		{
			patches = [fullPath];
		}
		else
		{
			throw new FileNotFoundException($"找不到 patch 文件或 Mod 目录：{fullPath}", fullPath);
		}

		// 按序号升序扫描；后面的覆盖前面的。
		var ordered = patches.OrderBy(ParsePatchIndex).ThenBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
		var entries = new List<AdaptationTocEntry>();
		var positionByKey = new Dictionary<AdaptationAssetKey, int>();
		var skipped = new List<string>();
		foreach (var patch in ordered)
		{
			cancellationToken.ThrowIfCancellationRequested();
			IReadOnlyList<AdaptationTocEntry> scanned;
			try
			{
				scanned = await new AdaptationScanner().ScanEntriesAsync(patch, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or ArgumentException)
			{
				skipped.Add($"patch「{Path.GetFileName(patch)}」读不出来，已整份跳过：{exception.Message}");
				continue;
			}

			foreach (var entry in scanned)
			{
				if (positionByKey.TryGetValue(entry.AssetKey, out var position))
				{
					// 序号更大的 patch 覆盖：位置不动，内容换成胜者。
					entries[position] = entry;
				}
				else
				{
					positionByKey[entry.AssetKey] = entries.Count;
					entries.Add(entry);
				}
			}
		}

		if (entries.Count == 0)
			throw new InvalidOperationException($"这些 patch 里一条条目都读不出来：{fullPath}");
		return (entries, ordered, skipped);
	}

	private static async ValueTask<SourceSet> ReadSourcesAsync(string patchOrDirectory, CancellationToken cancellationToken)
	{
		var (entries, consumedPatches, readSkipped) = await ReadMergedEntriesAsync(patchOrDirectory, cancellationToken).ConfigureAwait(false);
		var skipped = new List<string>(readSkipped);

		var reader = new PatchUnitMeshReader();
		var units = new Dictionary<string, PatchUnitMesh>(StringComparer.Ordinal);
		foreach (var entry in entries)
		{
			if (entry.AssetKey.TypeId != PatchUnitMeshReader.UnitTypeId)
				continue;
			var fileId = entry.AssetKey.FileId.ToString("x16", CultureInfo.InvariantCulture);
			try
			{
				// **必须用保留 payload 的那个读取器。** CanonicalUnitRebuilder 要拿
				// targetUnit.Payload.TocData 当"未知区间原样保留"的基线；
				// ReadCanonicalSourceAsync 把 payload 丢掉，换来的是
				// MissingTargetTocData / InvalidUnitHeaderRange / InvalidMeshInfoRange 一整串诊断。
				units[fileId] = await reader
					.ReadAsync(entry, entries, PatchUnitDependencyPolicy.RequirePatchLocalComposite, cancellationToken)
					.ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or ArgumentException or OverflowException)
			{
				skipped.Add($"unit {fileId} 读不出来，已跳过：{exception.Message}");
			}
		}

		return new SourceSet(entries, units, skipped, consumedPatches);
	}
}
