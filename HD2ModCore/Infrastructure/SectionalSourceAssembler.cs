using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>一个来源条目：库里的节点 id、它当时的名字、它在磁盘上的目录。</summary>
public sealed record SectionalSourceCandidate(string NodeId, string Name, string Directory);

/// <summary>装配结果。</summary>
public sealed record SectionalSourceAssemblyOutcome(
	bool Ok,
	string? Error,
	IReadOnlyList<string> Notes)
{
	public static SectionalSourceAssemblyOutcome Failed(string error, IReadOnlyList<string>? notes = null)
		=> new(false, error, notes ?? Array.Empty<string>());
}

/// <summary>
/// 「生成sectional版」的**导入侧**，只做两件事：
/// <list type="number">
/// <item><b>收集 patch</b>：把每个来源根上的 patch 全部收上来（**不按内容挑**，见下面那条）。</item>
/// <item><b>连续重新编号</b>：按来源顺序把它们一路排成 <c>patch_0</c>、<c>patch_1</c>……
/// 然后再交给转换器。</item>
/// </list>
///
/// <para>
/// <b>为什么这里不按白名单挑文件（2026-11-10 的教训）。</b>我一度在这一层用"unit/材质/贴图"
/// 白名单去筛 patch，结果把系列里真正的部件丢掉了：装饰件（上衣、内裤、翅膀、鞋子…）各带
/// 1 个 unit，而那些 unit 不在"选中的那件护甲"的意图集合里，于是被判成"没有命中"整份跳过，
/// 用户看到的是"9 个材质组 + 1 个 unit 组"，缺了一大半几何。
/// <b>内容筛选是转换器的活</b>（<see cref="SectionalConversionPlanner"/> 按白名单取并集），
/// 装配层只知道"这些 patch 属于这个来源"，没有能力判断哪一份对该系列没用。
/// 判据只留一份，才不会两边打架。
/// </para>
///
/// <para>
/// <b>为什么要重新编号（2026-11-10）。</b>一个系列里多个子级常常各自带**同一个**游戏档案的 patch，
/// 原名是同一个 <c>&lt;archive&gt;.patch_0</c>、内容却不同。原名平铺到同一个目录里必然互相覆盖：
/// 实测「本体」的 patch 有 21 个 unit，「翅膀」「鞋子」各只有 1 个（而且是本体已包含的那一个），
/// 覆盖后只剩 1 个 unit —— 产物看起来成功、实际丢了 20 个。
/// 重新编号之后它们各自占一个序号，转换器看到的是"多个 patch 按顺序叠加"，
/// 由它按 unit key 取并集、同 key 后来源覆盖，这才是这个系列真正想要的语义。
/// </para>
///
/// <para>
/// <b>文件名必须保持 <c>hex16.patch_N</c>。</b>序号是"叠加顺序"，hex 是"打到哪个游戏档案"，
/// 缺一不可（见 <see cref="PatchFileNameParser"/>）。所以重新编号只改 <c>N</c>，<c>hex</c> 原样保留，
/// 且计数器跨来源连续累加（modA → patch_0；modB 的两个 → patch_1、patch_2；modA2 接着往后）。
/// </para>
///
/// <para>
/// <b>它不转换。</b>按四段链路，导入只负责"把来源放进条目"；转换与编译是紧接着的
/// <see cref="Legacy2SectionalConverter"/>。
/// </para>
/// </summary>
public static class SectionalSourceAssembler
{
	/// <summary>
	/// 建条目：按 <paramref name="sources"/> 的顺序收集并重新编号来源 patch，再写 <c>sectional-source.json</c>。
	/// </summary>
	/// <param name="modDirectory">新建的条目目录（调用方负责创建；本方法只往里写）。</param>
	/// <param name="armor">来源里选中的护甲（可空）。</param>
	/// <param name="helmet">来源里选中的头盔（可空）。</param>
	/// <param name="assetKeys">干净资产白名单：**只写进依据文件**，供转换器按它筛内容；装配层不用它挑文件。</param>
	/// <param name="paths">索引库位置；给了就去游戏件表反查每个来源 unit 的部位/身形/层并写进依据文件。</param>
	public static async ValueTask<SectionalSourceAssemblyOutcome> AssembleAsync(
		string modDirectory,
		IReadOnlyList<SectionalSourceCandidate> sources,
		SectionalSourceSelection? armor,
		SectionalSourceSelection? helmet,
		SectionalSourceAssetKeys assetKeys,
		CancellationToken cancellationToken = default,
		StoragePaths? paths = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(modDirectory);
		ArgumentNullException.ThrowIfNull(sources);
		ArgumentNullException.ThrowIfNull(assetKeys);

		if (sources.Count == 0) return SectionalSourceAssemblyOutcome.Failed("没有来源条目。");
		if (armor?.HasValue != true && helmet?.HasValue != true)
			return SectionalSourceAssemblyOutcome.Failed("护甲与头盔都没有选：没有来源可转。");
		if (assetKeys.Units.Count == 0)
			return SectionalSourceAssemblyOutcome.Failed("干净资产白名单里没有任何 unit。");

		var parser = new PatchFileNameParser();
		var scanner = new HD2ModAdaptation.PatchReconstruction.PatchTocScanner();
		var notes = new List<string>();
		var entries = new List<SectionalSourceEntry>(sources.Count);
		// 每个来源的 unit id（先收集，全部拷完再一次性查件表，省掉 N 次开库）。
		var unitIdsByOrder = new Dictionary<int, List<string>>();

		// 跨来源连续累加：序号即叠加顺序，后来源的数字更大、在转换器里覆盖前来源的同 key unit。
		var nextIndex = 0;

		for (var order = 0; order < sources.Count; order++)
		{
			var source = sources[order];
			if (!Directory.Exists(source.Directory))
			{
				notes.Add($"来源「{source.Name}」的目录不存在，已跳过：{source.Directory}");
				continue;
			}

			var patchFiles = new List<string>();
			foreach (var patch in SectionalSplitter.EnumeratePatchFiles(source.Directory))
			{
				if (!parser.TryParse(Path.GetFileName(patch), out var info) || info is null)
				{
					notes.Add($"来源「{source.Name}」里有个文件不是合法的 patch 名，已跳过：{Path.GetFileName(patch)}");
					continue;
				}

				// 只改序号，hex 原样保留 —— 见类型注释里"文件名必须保持 hex16.patch_N"。
				var name = $"{info.ArchiveHex16}.patch_{nextIndex++}";
				CopyInto(patch, Path.Combine(modDirectory, name));
				foreach (var suffix in SidecarSuffixes)
				{
					var from = patch + suffix;
					if (!File.Exists(from)) continue;
					CopyInto(from, Path.Combine(modDirectory, name + suffix));
				}

				patchFiles.Add(name);
				// 顺手记下这份 patch 里有哪些 unit：后面一次性按 id 去游戏件表反查坐标。
				if (!unitIdsByOrder.TryGetValue(order, out var ids)) unitIdsByOrder[order] = ids = [];
				try
				{
					var scanned = await scanner.ScanEntriesAsync(patch, cancellationToken).ConfigureAwait(false);
					foreach (var entry in scanned)
					{
						if (entry.AssetKey.TypeId != HD2ModAdaptation.PatchReconstruction.UnitMesh.PatchUnitMeshReader.UnitTypeId) continue;
						var id = entry.AssetKey.FileId.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
						if (!ids.Contains(id, StringComparer.OrdinalIgnoreCase)) ids.Add(id);
					}
				}
				catch (Exception exception) when (exception is IOException
					or InvalidDataException
					or UnauthorizedAccessException
					or NotSupportedException)
				{
					notes.Add($"来源「{source.Name}」的「{info.FullFileName}」读不出 unit 列表，坐标会记为未知：{exception.Message}");
				}
			}

			if (patchFiles.Count == 0)
			{
				notes.Add($"来源「{source.Name}」根上没有 patch（不是 legacy 类），已跳过。");
				continue;
			}

			entries.Add(new SectionalSourceEntry(source.NodeId, source.Name, order, source.Directory, patchFiles));
		}

		if (entries.Count == 0)
			return SectionalSourceAssemblyOutcome.Failed(
				"所有来源根上都没有 patch，条目里不会有任何内容。", notes);

		// 每个来源 unit 的 (部位, 身形, 层)：**按 unit id 查游戏件表**。
		//
		// 这是自定义素体命中目标部位的唯一依据（模型 piece 靠 (body,slot,pieceType) 与目标 kit 对齐）。
		// legacy 的 unit 自己不带这三个字段，所以只能按 id 反查；查不到的如实记 -1，不猜。
		var allUnitIds = unitIdsByOrder.Values.SelectMany(ids => ids).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		var cells = paths is null || allUnitIds.Length == 0
			? new Dictionary<string, SectionalSourceUnitCell>(StringComparer.OrdinalIgnoreCase)
			: new Dictionary<string, SectionalSourceUnitCell>(
				await ArmorUnitCellLookup.ReadAsync(paths, allUnitIds, cancellationToken).ConfigureAwait(false),
				StringComparer.OrdinalIgnoreCase);

		if (paths is null) notes.Add("没有给索引库位置，来源 unit 的部位/身形/层一律记为未知。");

		var withCells = new List<SectionalSourceEntry>(entries.Count);
		foreach (var entry in entries)
		{
			var ids = unitIdsByOrder.TryGetValue(entry.Order, out var list) ? list : [];
			var units = ids
				.Select(id => cells.TryGetValue(id, out var cell) ? cell : SectionalSourceUnitCell.Unknown(id))
				.ToArray();
			withCells.Add(entry with { Units = units });
			var unknown = units.Count(unit => unit.Slot < 0);
			if (unknown != 0)
				notes.Add($"来源「{entry.Name}」有 {unknown} 个 unit 在游戏件表里查不到坐标（索引未建或不是原版护甲件）。");
		}

		var document = new SectionalSourceDocument(
			SectionalSourceManifest.CurrentSchema,
			DateTimeOffset.UtcNow,
			armor, helmet, withCells, assetKeys);

		var error = SectionalSourceFile.TryWrite(modDirectory, document);
		return error is null
			? new SectionalSourceAssemblyOutcome(true, null, notes)
			: SectionalSourceAssemblyOutcome.Failed($"写转换依据失败：{error}", notes);
	}

	/// <summary>patch 的两份 sidecar。它们必须跟着主文件走，否则产物读不出来。</summary>
	private static readonly string[] SidecarSuffixes = [".gpu_resources", ".stream"];

	/// <summary>
	/// 拷一个文件到新条目里。
	/// <para>
	/// <b>为什么要动属性。</b>来源 patch 常常是只读的（从 zip 导入时带着 ReadOnly），
	/// <c>File.Copy</c> 会把只读属性一起带给目标；此后任何覆盖同名文件都会
	/// <c>UnauthorizedAccessException: Access to the path … is denied</c>（2026-11-10 线上两次失败）。
	/// 新条目是我们自己的产物，后面还要往里写（拆分、编译），所以复制完统一抹成 Normal。
	/// </para>
	/// </summary>
	private static void CopyInto(string sourcePath, string destinationPath)
	{
		if (File.Exists(destinationPath)) File.SetAttributes(destinationPath, FileAttributes.Normal);
		File.Copy(sourcePath, destinationPath, overwrite: true);
		File.SetAttributes(destinationPath, FileAttributes.Normal);
	}
}
