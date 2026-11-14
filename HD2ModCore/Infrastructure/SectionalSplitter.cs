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
	/// 从自定义素体的 <c>modelKey</c> 推导出 12 位小写 hex 前缀，用来分配稳定的私有 unit id
	/// （格式 <c>&lt;12位hex&gt;&lt;4位序号&gt;</c>，共 16 位 hex），避开原版 unit id 并消除跨套污染。
	/// </summary>
	public static string DeriveUnitIdPrefix(string modelKey)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(modelKey);
		var hash = System.Security.Cryptography.SHA256.HashData(
			System.Text.Encoding.UTF8.GetBytes("hd2mod.sectional.unit:" + modelKey.Trim()));
		return Convert.ToHexString(hash[..6]).ToLowerInvariant();
	}

	private static string? NormalizeUnitIdPrefix(string? outputUnitIdPrefix)
	{
		if (string.IsNullOrWhiteSpace(outputUnitIdPrefix))
			return null;
		var trimmed = outputUnitIdPrefix.Trim().ToLowerInvariant();
		if (trimmed.Length != 12
			|| !ulong.TryParse(trimmed + "0001", NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
		{
			throw new ArgumentException($"outputUnitIdPrefix 必须是 12 位十六进制字符串，实际收到：{outputUnitIdPrefix}", nameof(outputUnitIdPrefix));
		}

		return trimmed;
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
		var plan = SectionalSplitPlanner.Plan([.. sources.Units.Values.Select(unit =>
		{
			var fileId = unit.Entry.AssetKey.FileId.ToString("x16", CultureInfo.InvariantCulture);
			return Describe(unit, unitIntents: null, sources.Copies.GetValueOrDefault(fileId));
		})]);
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
	/// <param name="unitIntents">
	/// unit → 部位/身形/层。legacy 来源的 unit 自己不带 customization，只读字节会把 7 个槽压成 1 个。
	/// </param>
	/// <param name="unitSourceNames">
	/// <b>unit id → 来源条目名</b>（`sectional-source.json` 的 `sources[].name`，由**胜者那份拷贝**所在来源决定）。
	/// <para>
	/// 给了它就按「一个来源一个组」建 <c>components.json</c>：那才是用户认得的部件名
	/// （`大衣 健壮` / `尾巴` / `丝袜`…）。不给就退回按槽位分组（CLI 直接拆单个模型时没有"来源"可言）。
	/// </para>
	/// <para>
	/// <b>为什么是 per-unit 而不是 per-patch。</b>调用方先把白名单里的 unit 收成**一份**子集 patch
	/// （<c>PatchSubsetWriter</c> 按 key 去重），落盘之后"这个 unit 来自哪个来源 patch"就没了。
	/// 唯一还知道答案的是**建这份子集的人**（它的选择表里每个 key 带着来源 patch），所以由它把名字传进来。
	/// </para>
	/// </param>
	/// <param name="outputUnitIdPrefix">
	/// 可选的 12 位十六进制前缀（常由 <see cref="DeriveUnitIdPrefix"/> 算出）。
	/// 传入时每个输出目标写到 <c>&lt;prefix&gt;&lt;4位序号&gt;</c> 私有 unit id 下，原版 unit id 不再进入产物。
	/// </param>
	public static async ValueTask<SectionalSplitResult> ExecuteAsync(
		string sourcePatchOrDirectory,
		string gameDataDirectory,
		string outputDirectory,
		CancellationToken cancellationToken = default,
		IReadOnlyDictionary<string, SectionalSourceUnitIntent>? unitIntents = null,
		IReadOnlyDictionary<string, string>? unitSourceNames = null,
		string? outputUnitIdPrefix = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sourcePatchOrDirectory);
		ArgumentException.ThrowIfNullOrWhiteSpace(gameDataDirectory);
		ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
		var normalizedPrefix = NormalizeUnitIdPrefix(outputUnitIdPrefix);

		var warnings = new List<string>();
		var sources = await ReadSourcesAsync(sourcePatchOrDirectory, cancellationToken).ConfigureAwait(false);
		foreach (var skipped in sources.Skipped)
			warnings.Add(skipped);

		var plan = SectionalSplitPlanner.Plan([.. sources.Units.Values.Select(unit =>
		{
			var fileId = unit.Entry.AssetKey.FileId.ToString("x16", CultureInfo.InvariantCulture);
			return Describe(unit, unitIntents, sources.Copies.GetValueOrDefault(fileId));
		})]);
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
		var hostToOutput = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var ordinal = 0;
		foreach (var target in plan.Targets)
		{
			foreach (var donor in target.DonorUnitIds)
				donorIds.Add(donor);
			hostToOutput[target.HostUnitId] = normalizedPrefix is not null
				? $"{normalizedPrefix}{++ordinal:x4}"
				: target.HostUnitId;
		}

		var effectivePlan = normalizedPrefix is null
			? plan
			: plan with
			{
				Targets = plan.Targets
					.Select(target => target with { HostUnitId = hostToOutput[target.HostUnitId] })
					.ToArray(),
				Groups = plan.Groups
					.Select(group => group with
					{
						MemberUnitIds = group.MemberUnitIds
							.Select(id => !donorIds.Contains(id) && hostToOutput.TryGetValue(id, out var mapped) ? mapped : id)
							.ToArray(),
					})
					.ToArray(),
			};

		// 段来源：谁贡献了哪一段。名字由调用方给（unit id → 来源条目名），它才知道每个 unit 来自哪份来源。
		var ownSourceByTarget = new Dictionary<string, string>(StringComparer.Ordinal);
		var inputSourcesByTarget = new Dictionary<string, IReadOnlyDictionary<int, IReadOnlyList<string>>>(StringComparer.Ordinal);
		var sourceNameOf = BuildSourceNameResolver(unitSourceNames);
		var presentSources = new List<string>();

		foreach (var target in plan.Targets)
		{
			AddUnique(presentSources, sourceNameOf(target.HostUnitId));
			foreach (var donorId in target.DonorUnitIds)
				AddUnique(presentSources, sourceNameOf(donorId));
		}

		foreach (var target in plan.Targets)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var host = sources.Units[target.HostUnitId];
			var outputUnitId = hostToOutput[target.HostUnitId];
			var outputAssetKey = normalizedPrefix is not null
				? new AdaptationAssetKey(PatchUnitMeshReader.UnitTypeId, ulong.Parse(outputUnitId, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
				: host.Entry.AssetKey;

			var anchor = UnitLodFamilySource.ResolveAnchor(host.Model);
			if (anchor is null)
			{
				warnings.Add($"{target.Slot}/{target.BodyType}：宿主 unit 没有可见网格，跳过。");
				continue;
			}

			ownSourceByTarget[outputUnitId] = sourceNameOf(target.HostUnitId);

			// **所有来源、所有层、所有拷贝全部并进这个 unit**（不是"后者赢"）。
			// 系列里装饰子级各自带同一 unit 的另一份几何，缺一份就是丢内容。
			var donors = new List<PatchUnitMesh>();
			foreach (var donorId in target.DonorUnitIds)
			{
				if (sources.Copies.TryGetValue(donorId, out var donorCopies) && donorCopies.Count != 0)
					donors.AddRange(donorCopies);
				else if (sources.Units.TryGetValue(donorId, out var donorUnit))
					donors.Add(donorUnit);
			}

			// 宿主自己也可能有多份（同一 unit 被多个来源各带一份）：除当前这份外都要并进来。
			// **两道判据都要**：① 引用相等（`ReadSourcesAsync` 已保证胜者与 Copies 里是同一实例）；
			// ② 同一份 patch 就是同一份拷贝（一个 patch 里一个 key 只有一条），
			// 引用一旦因为将来某次重构又分叉，第 ② 条还能挡住"把宿主追加到自身"。
			if (sources.Copies.TryGetValue(target.HostUnitId, out var hostCopies))
			{
				foreach (var copy in hostCopies)
				{
					if (ReferenceEquals(copy, host)) continue;
					if (string.Equals(copy.Entry.SourceFilePath, host.Entry.SourceFilePath, StringComparison.OrdinalIgnoreCase))
						continue;
					donors.Add(copy);
				}
			}

			var inputsByMesh = new Dictionary<int, IReadOnlyList<CanonicalDecorationAppendInput>>();
			// **按 mesh 记名字**：每个 LOD 的可见输入列表不同（某份来源在某级 LOD 没有网格就整条跳过），
			// 段账里的 InputIndex 指的是"这个 mesh 的输入表里的第几个"，所以名字也得按 mesh 对齐。
			var inputSourcesByMesh = new Dictionary<int, IReadOnlyList<string>>();
			foreach (var lodTarget in UnitLodFamilySource.ResolveVisibleLodFamily(host.Model, anchor.Value.Mesh))
			{
				var inputs = new List<CanonicalDecorationAppendInput>();
				var inputSources = new List<string>();
				foreach (var donor in donors)
				{
					// 按 LOD 序号取，不校验部位族：不同部位的 SemanticInfo 本来就不同（与跨甲合并同一条规则）。
					var donorMesh = UnitLodFamilySource.ResolveVisibleMeshAtLod(donor.Model, lodTarget.Raw.LodIndex);
					if (donorMesh is null)
						continue;
					inputs.Add(new CanonicalDecorationAppendInput(
						UnitLodFamilySource.CreateFragment(donor, donorMesh.Value.Mesh, donorMesh.Value.Raw),
						MaterialNamespace(donor.Entry.AssetKey.FileId)));
					inputSources.Add(sourceNameOf(donor.Entry.AssetKey.FileId.ToString("x16", CultureInfo.InvariantCulture)));
				}

				if (inputs.Count != 0)
				{
					inputsByMesh[lodTarget.Mesh.Index] = inputs;
					inputSourcesByMesh[lodTarget.Mesh.Index] = inputSources;
				}
			}

			inputSourcesByTarget[outputUnitId] = inputSourcesByMesh;

			if (inputsByMesh.Count == 0 && donors.Count != 0)
			{
				warnings.Add($"{target.Slot}/{target.BodyType}：来源之间没有共同可见 LOD，只保留宿主几何。");
			}

			CanonicalDecorationAppendResult appended;
			if (inputsByMesh.Count == 0)
			{
				// 没有 donor：这个槽本来就只有一个来源，产物就是它自己，不需要重建。
				AddTargetOutputFromSource(session, host, outputAssetKey);
				originsByTarget[outputUnitId] = [];
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
				AddTargetOutputFromSource(session, host, outputAssetKey);
				originsByTarget[outputUnitId] = [];
				continue;
			}

			if (appended.Origins.Count == 0)
				warnings.Add($"{target.Slot}/{target.BodyType}：段来源账缺失，这个槽的变体组只能按整块 unit 表达。");
			originsByTarget[outputUnitId] = appended.Origins;
			AddTargetOutput(session, host, appended.Edit, outputAssetKey);
		}

		// 其余条目（材质、贴图、以及没被当成宿主的 unit）原样带过去；
		// **donor 与已映射或丢弃的源 unit 必须排除**——几何已经进目标或被丢弃，再带原样拷贝就是污染/重复。
		foreach (var entry in sources.Entries)
		{
			if (entry.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId)
			{
				var key = entry.AssetKey.FileId.ToString("x16", CultureInfo.InvariantCulture);
				if (donorIds.Contains(key) || sources.Units.ContainsKey(key) || originsByTarget.ContainsKey(key))
					continue;
			}

			AddPassThrough(session, entry);
		}

		var finalized = session.Finalize(CanonicalDependencyClosureValidation.Valid);
		if (!finalized.IsValid)
		{
			foreach (var diagnostic in finalized.Diagnostics)
				warnings.Add($"依赖闭包不完整：{diagnostic.Code} {diagnostic.Message}");
			return new SectionalSplitResult(string.Empty, effectivePlan, SectionGroupDocument.Empty,
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
		// 身份必须**由计划来**：来源 unit 自己不带 customization（实测整批都是空串），
		// 只读字节的话组件树、换甲编译、rekey 三处都会把它们当成"认不出部位"。
		var documented = WardrobeSectionMapFile.ApplyUnitIdentities(sections.Document, BuildUnitIdentities(effectivePlan));
		// 有来源名 ⇒ 按「一个来源一个组」建组表：那才是用户认得的部件名（大衣/尾巴/丝袜…）。
		// 没有来源名（CLI 直接拆单个模型）⇒ 退回按槽位分组，那条路上没有"部件来源"可言。
		var groups = unitSourceNames is { Count: > 0 }
			? BuildSourceGroups(
				effectivePlan,
				originsByTarget,
				documented,
				new SectionalSourceProvenance(ownSourceByTarget, inputSourcesByTarget),
				presentSources,
				warnings)
			: BuildGroups(effectivePlan, originsByTarget, documented);

		// 两份 sidecar 必须跟着产物一起落盘，否则产物"能用但不可编辑"：
		// sections.json 是组件树与烘焙的唯一偏移来源，components.json 是组表。
		await WardrobeSectionMapFile
			.WriteAsync(Path.Combine(outputDirectory, WardrobeSectionMapFile.FileName), documented, cancellationToken)
			.ConfigureAwait(false);
		await SectionGroupFile
			.WriteAsync(outputDirectory, groups, cancellationToken)
			.ConfigureAwait(false);

		// 叠加成一个 patch 之后，被折叠掉的源 patch **必须删掉**：它们还在目录里就会在部署时
		// 重新覆盖产物，用户看到的是"拆了但没生效"。只在"产物写回源目录"时才删——
		// 输出到别的目录时源目录不该被碰。
		var consumed = RemoveConsumedPatches(sources.ConsumedPatches, outputDirectory, Path.GetFullPath(patchPath), warnings);

		return new SectionalSplitResult(patchPath, effectivePlan, groups, documented, warnings) { RemovedSourcePatchCount = consumed };
	}

	/// <summary>
	/// 产物的 unit 身份：<b>一个 target 就是一个输出 unit</b>，所以身份只能取自拆件计划。
	/// <para>
	/// 这与 <see cref="SectionalModelDefinition"/> 用的是同一个来源、同一套取值
	/// （部位名 / 身形名 / <see cref="SectionalModelDefinition.RegistryPieceLayer"/>）：
	/// <c>custom-model.json</c> 说这具素体有哪些格子，<c>sections.json</c> 说这些格子里的 unit 是谁，
	/// 两边说不一样的部位，组件树就会画出一棵与素体对不上的树。
	/// </para>
	/// </summary>
	internal static IReadOnlyDictionary<string, WardrobeSectionMapUnitIdentity> BuildUnitIdentities(SectionalSplitPlan plan)
	{
		ArgumentNullException.ThrowIfNull(plan);
		var identities = new Dictionary<string, WardrobeSectionMapUnitIdentity>(StringComparer.OrdinalIgnoreCase);
		foreach (var target in plan.Targets)
		{
			if (string.IsNullOrWhiteSpace(target.HostUnitId))
				continue;
			identities[target.HostUnitId] = new WardrobeSectionMapUnitIdentity(
				target.Slot,
				SectionalModelDefinition.PieceTypeName(SectionalModelDefinition.RegistryPieceLayer),
				SectionalSplitPlanner.NormalizeBody(target.BodyType));
		}

		return identities;
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
				Key = "slot:" + group.ExclusiveGroup,
			});
		}

		return new SectionGroupDocument(SectionGroupDocument.CurrentSchema, groups, []);
	}

	/// <summary>
	/// 产物的段来源：<b>哪一段几何来自哪份来源</b>。
	/// <para>
	/// 依据文件里一个"来源条目"平铺成一个 patch，所以段账要能回答"这一段是哪个 patch 带来的"。
	/// 输入表按 mesh 记（每个 LOD 的可见输入列表不同），与 <see cref="CanonicalAppendedSection.InputIndex"/> 同口径。
	/// </para>
	/// </summary>
	/// <param name="OwnSourceByTarget">宿主 unit → 它自己那份拷贝的来源名。</param>
	/// <param name="InputSourcesByTarget">宿主 unit → mesh 下标 → "第 k 个追加输入"的来源名。</param>
	public sealed record SectionalSourceProvenance(
		IReadOnlyDictionary<string, string> OwnSourceByTarget,
		IReadOnlyDictionary<string, IReadOnlyDictionary<int, IReadOnlyList<string>>> InputSourcesByTarget);

	/// <summary>
	/// 按「一个来源一个组」建组表：名字是用户认得的部件名（`大衣 健壮` / `尾巴` / `丝袜`…），
	/// 成员是这个来源贡献的全部段，<b>跨部位合成一个组</b>（关掉"尾巴"就该整条尾巴消失）。
	/// <para>
	/// <b>为什么不设互斥。</b>这些是**部件**，不是同一部位的二选一；按 <c>{槽位}/{身形}</c> 互斥
	/// 等于逼用户"躯干只能选一个"，与"全都要"正相反。
	/// </para>
	/// <para>
	/// <b>为什么不会出现空组。</b>组只在真有段归属时才生成；反过来，"带了 unit 却一段都没归到它"
	/// 的来源必须报出来（<c>unmatched</c> + warning）——否则用户会以为那个部件根本不存在，
	/// 而实际是装饰追加失败把它丢了。
	/// </para>
	/// </summary>
	public static SectionGroupDocument BuildSourceGroups(
		SectionalSplitPlan plan,
		IReadOnlyDictionary<string, IReadOnlyList<CanonicalAppendedSection>> originsByTarget,
		WardrobeSectionMapDocument sections,
		SectionalSourceProvenance provenance,
		IReadOnlyList<string> presentSources,
		List<string> warnings)
	{
		ArgumentNullException.ThrowIfNull(plan);
		ArgumentNullException.ThrowIfNull(originsByTarget);
		ArgumentNullException.ThrowIfNull(sections);
		ArgumentNullException.ThrowIfNull(provenance);
		ArgumentNullException.ThrowIfNull(presentSources);
		ArgumentNullException.ThrowIfNull(warnings);

		var order = new List<string>(4);
		var membersBySource = new Dictionary<string, Dictionary<string, List<int>>>(StringComparer.Ordinal);
		var unattributed = new List<string>();

		foreach (var target in plan.Targets)
		{
			var hostUnitId = target.HostUnitId;
			var mapUnit = sections.Units.FirstOrDefault(unit =>
				string.Equals(unit.FileId, hostUnitId, StringComparison.OrdinalIgnoreCase));
			if (mapUnit is null)
				continue;

			var ownSource = provenance.OwnSourceByTarget.GetValueOrDefault(hostUnitId) ?? UnknownSourceName;
			var sectionSource = new Dictionary<int, string>();
			if (originsByTarget.TryGetValue(hostUnitId, out var origins) && origins.Count != 0)
			{
				var canonicalMeshIndex = ResolveCanonicalMeshIndex(mapUnit, origins);
				provenance.InputSourcesByTarget.TryGetValue(hostUnitId, out var inputSources);
				foreach (var origin in origins.Where(origin => origin.MeshInfoIndex == canonicalMeshIndex))
				{
					var source = ownSource;
					if (origin.InputIndex >= 0)
					{
						if (inputSources is not null
							&& inputSources.TryGetValue(canonicalMeshIndex, out var names)
							&& origin.InputIndex < names.Count)
						{
							source = names[origin.InputIndex];
						}
						else
						{
							AddUnique(unattributed,
								$"{target.Slot}/{target.BodyType}：第 {origin.InputIndex} 个追加输入的来源说不清，已归到「{ownSource}」。");
						}
					}

					sectionSource[origin.FinalSectionIndex] = source;
				}
			}

			// 没有段账、或账里没提到的节：它们确实是宿主自己那份拷贝的几何。
			foreach (var section in mapUnit.Sections)
				sectionSource.TryAdd(section.Index, ownSource);

			foreach (var pair in sectionSource.OrderBy(pair => pair.Key))
				AddSourceMember(membersBySource, order, pair.Value, hostUnitId, pair.Key);
		}

		var groups = new List<SectionGroup>(order.Count);
		foreach (var source in order)
		{
			var members = membersBySource[source]
				.Select(pair => new SectionGroupTarget(pair.Key, [.. pair.Value.Distinct().Order()]))
				.ToArray();
			// 名字与 SourceVpk 都写来源名：组行副标题据此显示「来自「尾巴」」，比"来自附加部件"可操作。
			groups.Add(new SectionGroup(source, source, [], members)
			{
				Source = SectionGroupSource.DonorUnit,
				ExclusiveGroup = null,
				// 稳定键：重建之后"还是这个来源的组"。用户的改名/互斥/删除都按它对齐。
				Key = "source:" + source,
			});
		}

		var unmatched = new List<SectionGroupUnmatched>();
		foreach (var missing in presentSources.Where(name => !membersBySource.ContainsKey(name)))
		{
			unmatched.Add(new SectionGroupUnmatched(missing, missing,
				"它在这次转换里带了 unit，但没有任何一段几何进产物（装饰追加失败或段账缺失）。"));
			warnings.Add($"来源「{missing}」的几何没有进产物：这次没有它的开关。");
		}

		foreach (var note in unattributed)
			AddUnique(warnings, note);

		return new SectionGroupDocument(SectionGroupDocument.CurrentSchema, groups, unmatched);
	}

	/// <summary>来源名缺失时的写法。空字符串会让组名变成一行空白，比"不知道"更糟。</summary>
	public const string UnknownSourceName = "未标注来源";

	/// <summary>
	/// unit id → 来源条目名。没给名字就退回 <see cref="UnknownSourceName"/>（不编、不猜）；
	/// 键按 16 位小写 hex 归一（大小写、带不带 <c>0x</c> 都接受）。
	/// </summary>
	private static Func<string, string> BuildSourceNameResolver(IReadOnlyDictionary<string, string>? unitSourceNames)
	{
		var byUnit = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		if (unitSourceNames is not null)
		{
			foreach (var pair in unitSourceNames)
			{
				if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
					continue;
				byUnit[NormalizeUnitKey(pair.Key)] = pair.Value.Trim();
			}
		}

		return unitId => byUnit.TryGetValue(NormalizeUnitKey(unitId), out var display) ? display : UnknownSourceName;
	}

	private static string NormalizeUnitKey(string unitId)
	{
		var value = unitId.Trim();
		if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
			value = value[2..];
		return value.ToLowerInvariant();
	}

	private static void AddSourceMember(
		Dictionary<string, Dictionary<string, List<int>>> membersBySource,
		List<string> order,
		string source,
		string unitId,
		int sectionIndex)
	{
		if (!membersBySource.TryGetValue(source, out var perUnit))
		{
			membersBySource[source] = perUnit = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
			order.Add(source);
		}

		if (!perUnit.TryGetValue(unitId, out var sections))
			perUnit[unitId] = sections = [];
		sections.Add(sectionIndex);
	}

	/// <summary>去重追加（保持首次出现顺序）：来源清单与提示都要稳定，否则 diff 全是噪音。</summary>
	private static void AddUnique(List<string> list, string value)
	{
		if (!list.Contains(value, StringComparer.Ordinal))
			list.Add(value);
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

	private static void AddTargetOutput(CanonicalPatchSession session, PatchUnitMesh host, PatchUnitMeshEditResult edit, AdaptationAssetKey? outputAssetKey = null)
	{
		var entry = new CanonicalPatchSessionEntry(
			outputAssetKey ?? host.Entry.AssetKey,
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
	private static void AddTargetOutputFromSource(CanonicalPatchSession session, PatchUnitMesh host, AdaptationAssetKey? outputAssetKey = null)
	{
		var entry = host.Entry;
		session.AddEntry(new CanonicalPatchSessionEntry(
			outputAssetKey ?? entry.AssetKey,
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

	/// <summary>
	/// 描述一个来源 unit。**部位/身形/层优先取「意图」**（用户选中的护甲/头盔里它属于哪个槽），
	/// 只有意图缺失时才退回 unit 自身的 semantic。
	/// <para>
	/// legacy 来源的 unit 自己不带 customization：实测某系列 21 个 unit 的
	/// <c>slot/bodyType/pieceType</c> 全是空串。只读 unit 的话，它们会全部兜底成
	/// <c>Torso/Any</c> 一个桶，"标准 7 槽"就压成了 3 个 unit（2026-11-10 的事故）。
	/// </para>
	/// </summary>
	private static SectionalSplitSource Describe(
		PatchUnitMesh unit,
		IReadOnlyDictionary<string, SectionalSourceUnitIntent>? unitIntents,
		IReadOnlyList<PatchUnitMesh>? unitCopies = null)
	{
		var semantic = unit.Model.Meshes.FirstOrDefault()?.SemanticInfo;
		var fileId = unit.Entry.AssetKey.FileId.ToString("x16", CultureInfo.InvariantCulture);
		var name = semantic?.Name ?? string.Empty;
		var facts = UnitGeometryFactsBuilder.Analyze(unit.Model);
		int sections;
		string? geometrySignature;
		if (!facts.HasRenderableVisibleGeometry)
		{
			sections = 0;
			geometrySignature = null;
		}
		else
		{
			var bestLod0 = UnitGeometryFactsBuilder.SelectBestRenderableLod0(unit.Model);
			var lod0NonEmpty = bestLod0?.Sections.Count(section => section.Triangles.Count > 0) ?? 0;
			sections = lod0NonEmpty > 0
				? lod0NonEmpty
				: Math.Max(1, unit.Model.Meshes.Sum(mesh => mesh.Sections.Count));
			geometrySignature = ComputeGeometrySignature(unit, unitCopies);
		}

		if (unitIntents is not null && unitIntents.TryGetValue(fileId, out var intent))
		{
			var slot = SlotName(intent.Slot);
			if (slot.Length != 0)
				return new SectionalSplitSource(fileId, slot, BodyName(intent.Body), sections, name, LayerName(intent.Layer), geometrySignature);
		}

		return new SectionalSplitSource(
			fileId,
			semantic?.Slot ?? string.Empty,
			semantic?.BodyType ?? SectionalSplitPlanner.DefaultBody,
			sections,
			name,
			semantic?.PieceType ?? string.Empty,
			geometrySignature);
	}

	private static string? ComputeGeometrySignature(
		PatchUnitMesh unit,
		IReadOnlyList<PatchUnitMesh>? unitCopies)
	{
		var copies = unitCopies is { Count: > 0 } ? unitCopies : [unit];
		var parts = new List<string>(copies.Count);
		foreach (var copy in copies)
		{
			var best = UnitGeometryFactsBuilder.SelectBestRenderableLod0(copy.Model)
				?? copy.Model.RawMeshData
					.Where(UnitGeometryFactsBuilder.HasRenderableGeometry)
					.OrderByDescending(UnitGeometryFactsBuilder.CountTriangles)
					.ThenByDescending(raw => raw.Vertices.Count)
					.FirstOrDefault();
			if (best is null)
				return null;

			var materialBySlot = copy.Model.Materials
				.GroupBy(binding => binding.SectionId)
				.ToDictionary(group => group.Key, group => group.First().MaterialId);

			var sectionTokens = best.Sections
				.Where(section => section.Triangles.Count > 0)
				.Select(section =>
				{
					var materialId = materialBySlot.TryGetValue(section.MaterialSlotId, out var raw) ? raw : 0UL;
					return $"{materialId:x16}:{section.Triangles.Count}";
				})
				.ToList();
			if (sectionTokens.Count == 0 && best.Triangles.Count > 0)
				sectionTokens.Add($"0000000000000000:{best.Triangles.Count}");

			parts.Add($"v={best.Vertices.Count};t={UnitGeometryFactsBuilder.CountTriangles(best)};s=[{string.Join(',', sectionTokens)}]");
		}

		var hash = System.Security.Cryptography.SHA256.HashData(
			System.Text.Encoding.UTF8.GetBytes(string.Join('|', parts)));
		return Convert.ToHexString(hash).ToLowerInvariant();
	}

	/// <summary>槽位号 → 标准槽名（<see cref="SectionalSplitPlanner.StandardSlots"/> 的写法定）。</summary>
	private static string SlotName(int slot) => ArmorStructure.FromSlot((uint)slot) switch
	{
		ArmorPart.Helmet => "Helmet",
		ArmorPart.Torso => "Torso",
		ArmorPart.Hips => "Hip",
		ArmorPart.LeftArm => "LeftArm",
		ArmorPart.RightArm => "RightArm",
		ArmorPart.LeftLeg => "LeftLeg",
		ArmorPart.RightLeg => "RightLeg",
		_ => string.Empty,
	};

	private static string BodyName(int body) => body switch
	{
		0 => "Stocky",
		1 => "Slim",
		_ => SectionalSplitPlanner.DefaultBody,
	};

	/// <summary>
	/// 层号 → 件级 <c>PieceType</c> 字符串。取值与 <see cref="SectionalModelDefinition.PieceTypeName"/>
	/// 同一处，别在这里另写一套写法（历史上写成过 <c>UnderGarment</c>，与游戏串只差一个字母）。
	/// </summary>
	private static string LayerName(int layer)
		=> SectionalModelDefinition.PieceTypeName(unchecked((uint)layer));

	private sealed record SourceSet(
		IReadOnlyList<AdaptationTocEntry> Entries,
		IReadOnlyDictionary<string, PatchUnitMesh> Units,
		IReadOnlyList<string> Skipped,
		/// <summary>参与叠加的源 patch，按序号升序。第一个同时充当输出文件名与头部模板的来源。</summary>
		IReadOnlyList<string> ConsumedPatches)
	{
		/// <summary>输出 patch 的文件名与头部模板取它。</summary>
		public string HeaderTemplatePatchPath => ConsumedPatches[0];

		/// <summary>
		/// **每个来源里的每一份 unit**（同一个 unit id 可能有多份：系列里的装饰子级各自带一份）。
		/// 合并时必须把它们全部并进目标 unit —— 不是"后者赢"。按 patch 序号升序。
		/// </summary>
		public IReadOnlyDictionary<string, IReadOnlyList<PatchUnitMesh>> Copies { get; init; } =
			new Dictionary<string, IReadOnlyList<PatchUnitMesh>>(StringComparer.Ordinal);
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
		var copies = new Dictionary<string, List<PatchUnitMesh>>(StringComparer.Ordinal);

		// 先把**每一份拷贝**读出来：同一个 unit id 在系列里可能有多个来源各带一份，
		// 而 entries 已经按"后者赢"去重了。合并要的是全部几何，不能只剩一份。
		foreach (var patch in consumedPatches)
		{
			IReadOnlyList<AdaptationTocEntry> scanned;
			try
			{
				scanned = await new AdaptationScanner().ScanEntriesAsync(patch, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or ArgumentException)
			{
				continue;
			}

			foreach (var entry in scanned)
			{
				if (entry.AssetKey.TypeId != PatchUnitMeshReader.UnitTypeId) continue;
				var fileId = entry.AssetKey.FileId.ToString("x16", CultureInfo.InvariantCulture);
				try
				{
					// **必须用保留 payload 的那个读取器。** CanonicalUnitRebuilder 要拿
					// targetUnit.Payload.TocData 当"未知区间原样保留"的基线；
					// ReadCanonicalSourceAsync 把 payload 丢掉，换来的是
					// MissingTargetTocData / InvalidUnitHeaderRange / InvalidMeshInfoRange 一整串诊断。
					var mesh = await reader
						.ReadAsync(entry, scanned, PatchUnitDependencyPolicy.RequirePatchLocalComposite, cancellationToken)
						.ConfigureAwait(false);
					if (!copies.TryGetValue(fileId, out var list)) copies[fileId] = list = [];
					list.Add(mesh);
				}
				catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or ArgumentException or OverflowException)
				{
					skipped.Add($"unit {fileId}（{Path.GetFileName(patch)}）读不出来，已跳过：{exception.Message}");
				}
			}
		}

		// 胜者 = 序号最大的那个 patch 提供的那份拷贝（与 ReadMergedEntriesAsync 的"后者赢"同口径），
		// 而且**必须与 Copies 里是同一个实例**：合并时靠 `ReferenceEquals(copy, host)` 判"这份是不是宿主自己"，
		// 另读一遍就是另一个对象，判断恒假 ⇒ 宿主被当成 donor 又追加一次 ⇒ 每份基础几何都重复一份
		// （实测 2026-11-10：`ecc8` 的 6 段里 `#1≡#4`、`#2≡#5`）。顺带省掉一次全量解析。
		// 同时过滤掉不含可见可渲染几何的隐藏空壳副本；若某 unit 所有副本均为空壳，
		// 仍保留在 units 里阻止 PassThrough 原样放行，交由 Describe + 规划器按 0 段几何丢弃。
		var filteredCopies = new Dictionary<string, IReadOnlyList<PatchUnitMesh>>(StringComparer.Ordinal);
		foreach (var (fileId, list) in copies)
		{
			if (list.Count == 0) continue;
			var renderable = list
				.Where(mesh => UnitGeometryFactsBuilder.Analyze(mesh.Model).HasRenderableVisibleGeometry)
				.ToArray();
			if (renderable.Length != 0)
			{
				units[fileId] = renderable[^1];
				filteredCopies[fileId] = renderable;
			}
			else
			{
				units[fileId] = list[^1];
				filteredCopies[fileId] = list;
			}
		}

		return new SourceSet(entries, units, skipped, consumedPatches)
		{
			Copies = filteredCopies,
		};
	}
}
