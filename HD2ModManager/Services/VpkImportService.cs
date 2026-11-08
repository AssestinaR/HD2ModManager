using System.Globalization;
using System.IO;
using System.Text.Json;
using HD2ModCore.Infrastructure;

namespace HD2ModManager.Services;

// 作用：把"来源 VPK"接进导入流程：解压 → 规划 → 逐个 vpk2patch → 交给导入管线。
// Purpose: Feeds source VPKs into the import flow: extract, plan, convert one by one, then hand the
// produced folders to the ordinary import pipeline.
public enum VpkImportOutcome
{
	Converted,
	Skipped,
	Failed,
}

/// <summary>一个 VPK 在这次导入里的去向。</summary>
/// <param name="Updated">
/// 这次是<b>更新库里已有的 Mod</b>，而不是新建。与"新建"分开报，是因为用户看到的
/// 后果完全不同（原有 Mod 的名字、位置、开关、素体都还在，只是产物换新）。
/// </param>
public sealed record VpkImportItem(
	string VpkPath,
	VpkImportOutcome Outcome,
	string? ModDirectoryName,
	string? Reason,
	int Units,
	int Materials,
	int Textures,
	IReadOnlyList<string> Notes,
	bool Updated = false);

/// <summary>
/// 一个转出来的 Mod 目录，以及它在**哪个模型主体组**里、它是主体还是材质包。
/// <para>
/// 分组要一路带到"挂选项"那一步：几套角色的材质裸名相同，把 plum 的材质挂到 chiffon 下面
/// 会得到一堆"看起来对、点开却什么都不变"的选项。
/// </para>
/// <para>
/// <see cref="UpdateTargetDirectory"/> 非 <c>null</c> 表示<b>这不是新 Mod</b>：库里已经有
/// 来自同一个 VPK 的 Mod，产物应当换进那个目录（保留节点、名字、顺序、启停与素体 key）。
/// </para>
/// </summary>
public sealed record VpkProducedPackage(
	string Directory,
	string? GroupName,
	bool IsModel,
	string? UpdateTargetDirectory = null,
	string? UpdateTargetName = null,
	string? ReportJson = null,
	int UpdateCandidateCount = 1)
{
	public bool IsUpdate => UpdateTargetDirectory is not null;
}

/// <summary>一次 VPK 导入的完整结果。**跳过也要给理由**——否则用户只会看到"少导入了几个"。</summary>
public sealed record VpkImportReport(
	string SourcePath,
	IReadOnlyList<VpkImportItem> Items,
	IReadOnlyList<string> Notes)
{
	public int ConvertedCount => Items.Count(item => item.Outcome == VpkImportOutcome.Converted);
	public int SkippedCount => Items.Count(item => item.Outcome == VpkImportOutcome.Skipped);
	public int FailedCount => Items.Count(item => item.Outcome == VpkImportOutcome.Failed);

	public bool HasConverted => ConvertedCount != 0;

	/// <summary>一句话结论，给通知/日志用。</summary>
	public string ToSummary()
	{
		var parts = new List<string> { $"VPK 导入：{Items.Count} 个来源包" };
		parts.Add($"转换 {ConvertedCount}");
		if (SkippedCount != 0) parts.Add($"跳过 {SkippedCount}");
		if (FailedCount != 0) parts.Add($"失败 {FailedCount}");
		return string.Join("，", parts) + "。";
	}

	/// <summary>逐条明细：每个 VPK 为什么转/为什么跳过。</summary>
	public string ToDetail()
	{
		var lines = new List<string> { ToSummary() };
		foreach (var item in Items.OrderBy(item => item.Outcome).ThenBy(item => item.VpkPath, StringComparer.OrdinalIgnoreCase))
		{
			var name = Path.GetFileName(item.VpkPath);
			switch (item.Outcome)
			{
				case VpkImportOutcome.Converted:
					lines.Add($"  ✔ {name} → {item.ModDirectoryName}"
						+ (item.Updated ? "（更新原有 Mod）" : string.Empty)
						+ $"（{item.Units} 个 Unit / {item.Materials} 个材质 / {item.Textures} 张贴图）");
					break;
				case VpkImportOutcome.Skipped:
					lines.Add($"  – {name}：{item.Reason}");
					break;
				default:
					lines.Add($"  ✘ {name}：{item.Reason}");
					break;
			}
		}
		lines.AddRange(Notes.Select(note => "  · " + note));
		return string.Join(Environment.NewLine, lines);
	}
}

/// <summary>
/// VPK 导入的执行体。
/// <para>
/// <b>为什么转换交给 <c>hd2mod.exe</c> 而不是在进程内做。</b>转换链路的每一条语义
/// （哪些材质写进哪个 patch、matcap 怎么处理、外壳怎么挑）都是 <c>vpk2patch</c> 的契约，
/// 已经在实机上对过。管理器再实现一遍就是两份会漂移的实现，而漂移的表现是
/// "命令行转出来的能用、管理器转出来的不能用"——那种问题最难查。
/// 管理器只负责"哪一批、放哪里、怎么导入、怎么报告"。
/// </para>
/// <para>
/// <b>为什么每个 VPK 单独起一次进程。</b>一次导入几十个包要几分钟，必须能看进度、能取消；
/// 整批一次调用只会在最后给一个 JSON。跨包引用靠 <c>--material-source</c> 把整批都交给
/// 解析集合，所以单独调用不改变"能引用别的 VPK 的材质"这件事。
/// </para>
/// </summary>
public sealed class VpkImportService
{
	private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase) { ".zip", ".rar", ".7z" };

	/// <summary>转换产出的报告文件名。它和 <c>vpk2patch/</c> 一起构成"这是产出目录"的标记。</summary>
	private const string PackageReportFileName = "vpk2patch.json";

	private readonly StoragePaths _paths;
	private readonly string _gameDataDirectory;
	private readonly string _managerRoot;
	private readonly string _libraryRoot;

	public VpkImportService(StoragePaths paths, string gameDataDirectory)
	{
		_paths = paths ?? throw new ArgumentNullException(nameof(paths));
		_gameDataDirectory = gameDataDirectory ?? string.Empty;
		_managerRoot = paths.AppRootDirectory;
		_libraryRoot = paths.ModsRootDirectory ?? paths.AppRootDirectory;
	}

	/// <summary>
	/// 判断一个导入来源是不是"VPK 来源"，是就把它摊开到临时目录并列出所有 VPK。
	/// <para>返回 <c>null</c> 表示这不是 VPK 来源，调用方应当走原来的导入路径。</para>
	/// </summary>
	public async Task<VpkImportSource?> TryPrepareAsync(
		string path,
		CancellationToken cancellationToken,
		Action<string>? onStage = null)
	{
		if (File.Exists(path) && Path.GetExtension(path).Equals(".vpk", StringComparison.OrdinalIgnoreCase))
		{
			return new VpkImportSource(path, CreateStaging(), [Path.GetFullPath(path)], ownsStaging: true);
		}

		if (Directory.Exists(path))
		{
			// 转换产出的 Mod 文件夹根上留着来源 VPK 原件（为了能重转），所以它**看起来**像 VPK 来源。
			// 但它已经是产物：再按来源处理就是"转换 → 产出 → 再按来源转换 → 又产出"的无限递归，
			// 结果是转换报告一路显示成功、一个 Mod 都建不出来。这里直接交回普通导入。
			if (IsConvertedPackage(path)) return null;

			onStage?.Invoke($"正在查找文件夹里的 VPK：{Path.GetFileName(path)}");
			var vpks = EnumerateVpkFiles(path);
			return vpks.Count == 0 ? null : new VpkImportSource(path, CreateStaging(), vpks, ownsStaging: true);
		}

		if (!File.Exists(path) || !ArchiveExtensions.Contains(Path.GetExtension(path)))
			return null;

		// 先只看目录区：普通 patch 包不该被我们解压一遍再让导入器解压一遍。
		var extractor = new SevenZipArchiveExtractor();
		IReadOnlyList<string> entries;
		try
		{
			entries = await extractor.ListAsync(path, cancellationToken).ConfigureAwait(false);
		}
		catch (InvalidDataException)
		{
			return null;
		}

		if (!entries.Any(entry => entry.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase)))
			return null;

		var staging = CreateStaging();
		var extracted = Path.Combine(staging, "source");
		try
		{
			// 解压这一步在实测里是 8 秒（2.45 GB / 19 个 VPK），而它之后还有几分钟的转换。
			// 不在这里说一句话，用户看到的就是"一直停在解压中"——那也是他报的那个现象的起点。
			var vpkCount = entries.Count(entry => entry.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase));
			onStage?.Invoke($"正在解压 {Path.GetFileName(path)}（{vpkCount} 个 VPK）");

			// **摊平解压**：中文整合包的目录又深又长（实测最深路径 268 字符，撞 260 上限），
			// 而 VPK 转换只按文件名找包，包内层次对它没意义。
			// 只有"包内没有同名 VPK"时才敢摊平——同名会被互相覆盖，那等于悄悄少一个包。
			var flatten = HasUniqueVpkNames(entries, out var duplicateName);
			if (!flatten)
			{
				LogService.Warn(
					$"「{Path.GetFileName(path)}」里有两个同名 VPK（{duplicateName}），本次保留目录结构解压"
					+ "（如果路径太长导致导入失败，请先把这个包的子目录改成短名）。");
			}

			await extractor.ExtractAsync(path, extracted, cancellationToken, flatten).ConfigureAwait(false);
			var vpks = EnumerateVpkFiles(extracted);
			if (vpks.Count == 0)
			{
				DeleteQuietly(staging);
				return null;
			}

			return new VpkImportSource(path, staging, vpks, ownsStaging: true);
		}
		catch
		{
			DeleteQuietly(staging);
			throw;
		}
	}

	/// <summary>
	/// 规划 + 逐个转换，产出"每个 VPK 一个文件夹，里面的 <c>vpk2patch/</c> 放实际生效的 patch"。
	/// 返回值里的目录由调用方交给导入管线。
	/// </summary>
	public async Task<(VpkImportReport Report, IReadOnlyList<VpkProducedPackage> ProducedPackages)> ConvertAsync(
		VpkImportSource source,
		Action<string>? onStage,
		CancellationToken cancellationToken,
		Action<string, int, int, string>? onProgress = null)
	{
		ArgumentNullException.ThrowIfNull(source);
		var executable = ResolveCliExecutable();
		var items = new List<VpkImportItem>();
		var notes = new List<string>();
		var produced = new List<VpkProducedPackage>();

		if (string.IsNullOrWhiteSpace(_gameDataDirectory) || !Directory.Exists(_gameDataDirectory))
		{
			return (new VpkImportReport(source.SourcePath,
				source.VpkPaths.Select(vpk => new VpkImportItem(vpk, VpkImportOutcome.Failed, null,
					"没有配置游戏 data 目录：转换外壳 Unit 与 avatar 骨架都要从游戏本体读。", 0, 0, 0, [])).ToArray(),
				notes), produced);
		}

		// **导入 VPK 之前先确认「游戏资产索引」在不在。**
		//
		// 为什么必须挡在这里：模型转换要读索引里的装备关系表来选外壳 Unit（donor），
		// 索引不在时 CLI 报 `ArmorIndexUnavailable` → **模型整批失败**；
		// 而材质包不依赖它、照样能转成。于是用户看到的是
		// **"这一批只导进了材质，模型没了"**，既没有醒目的失败，也没有任何线索指向索引。
		//
		// 口径（用户定）：**只在"不存在"时自动建一次**；已存在的一律不动 ——
		// 哪怕过期也不重建（重建要几十秒，而过期与否该由用户显式决定）。
		var indexProblem = await DescribeMissingGameDataIndexAsync(cancellationToken).ConfigureAwait(false);
		if (indexProblem is not null)
		{
			onStage?.Invoke("游戏资产索引不存在，正在自动建立（只这一次）…");
			var buildError = await TryBuildGameDataIndexAsync(onStage, cancellationToken).ConfigureAwait(false);
			if (buildError is not null)
			{
				var reason = "游戏资产索引不存在，自动建立也失败了 —— 模型转换需要它来选外壳 Unit。"
					+ $"请到「设置与资产」里手动建立一次资产索引，再重新导入。（{buildError}）";
				return (new VpkImportReport(source.SourcePath,
					source.VpkPaths.Select(vpk => new VpkImportItem(vpk, VpkImportOutcome.Failed, null, reason, 0, 0, 0, [])).ToArray(),
					notes), produced);
			}

			notes.Add("导入前发现游戏资产索引不存在，已自动建立一次（模型转换靠它选外壳 Unit；"
				+ "没有它时模型会整批失败，只剩材质包能转出来）。");
		}

		onStage?.Invoke("正在识别哪些 VPK 能转换");
		var plan = await PlanAsync(executable, source.VpkPaths, cancellationToken).ConfigureAwait(false);
		if (plan.Convertible.Count == 0)
		{
			items.AddRange(plan.Items.Select(item => new VpkImportItem(item.VpkPath, VpkImportOutcome.Skipped, null, item.Reason, 0, 0, 0, [])));
			notes.Add("这一批里没有任何可转换的 VPK（只支持角色模型以及它引用到的材质）。");
			return (new VpkImportReport(source.SourcePath, items, notes), produced);
		}

		var convertiblePaths = plan.Convertible.Select(item => item.VpkPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
		items.AddRange(plan.Items
			.Where(item => !convertiblePaths.Contains(item.VpkPath))
			.Select(item => new VpkImportItem(item.VpkPath, VpkImportOutcome.Skipped, null, item.Reason, 0, 0, 0, [])));

		// 认不出属于哪个主体的材质包：跳过并点名（判据见 VpkImportPlanner.BuildModelGroups）。
		foreach (var unassigned in plan.UnassignedMaterials)
		{
			items.Add(new VpkImportItem(unassigned, VpkImportOutcome.Skipped, null,
				"认不出它属于哪个模型主体（它提供的材质目录对不上任何模型声明的目录），已跳过。", 0, 0, 0, []));
		}

		// 组件开关包：只用于识别开关，不进解析集合（它们会顶掉同名真材质）。分主体转换时
		// 开关包散在各组之外，所以统一写一份清单交给 CLI。
		var switchListPath = WritePathList(source.StagingRoot, "component-switches.txt",
			plan.Items.Where(item => item.Kind == "componentSwitch").Select(item => item.VpkPath));

		// **命名空间与解析集合都按主体分**：整合包里几套角色的材质裸名相同（tights_socks.vmt
		// 谁都有），整批一个命名空间会让它们互相覆盖，整批一份解析集合会让模型吃到别人的材质。
		var workload = BuildConversionWorkload(plan);
		if (workload.Count == 0)
		{
			items.AddRange(plan.Convertible.Select(item => new VpkImportItem(item.VpkPath, VpkImportOutcome.Skipped, null,
				"没法分组：这批里没有可用的模型主体。", 0, 0, 0, [])));
			return (new VpkImportReport(source.SourcePath, items, notes), produced);
		}

		// 同一个压缩包里可能出现同名（或只差大小写）的 VPK——实测一个八人包里就有一份重复的
		// "手套Gloves关"。它们会算到同一个产出目录，第二个在 Move 时抛
		// "a file or directory with the same name already exists"，那一个包就丢了。
		// 加后缀区分：宁可多出一个 Mod，也不要静默丢一个。
		var usedDirectoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		// 这一批里已经"更新"过的目标目录：同一个目标只更新一次（见下面的说明）。
		var updatedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var index = 0;
		foreach (var job in workload)
		{
			cancellationToken.ThrowIfCancellationRequested();
			index++;
			var name = Path.GetFileName(job.PrimaryVpkPath);
			onStage?.Invoke($"正在转换 {index}/{workload.Count}：{job.GroupName} → {name}");
			if (job.Inputs.Count > 1)
				onStage?.Invoke($"这一组共 {job.Inputs.Count} 个 VPK（模型 + 它的 {job.Inputs.Count - 1} 个材质包），一次转成同一个 Mod");

			var raw = Path.Combine(source.StagingRoot, "raw", index.ToString(CultureInfo.InvariantCulture));
			try
			{
				// **同一来源再来一次 = 更新那个 Mod，不是再建一个。**
				//
				// 判据：库里某个 Mod 目录的根上就躺着这个 VPK（导入时留的原件），
				// 且文件名与**字节数**都相同。字节数必须比——同名不同内容的两份包
				// （作者发了第二版、或者两个人各自命名成 `材质.vpk`）覆盖掉对方是**破坏性的**；
				// 多出一个 Mod 只是让人烦。所以宁可保守。
				//
				// ⚠️ **同一批里一个目标只能更新一次。** 一个压缩包里放了两份完全相同的 VPK
				// 是真会发生的（实测八人包里就有一份重复的「手套Gloves关」）。两次都换进
				// 同一个目录，后一个会静默盖掉前一个 —— 那正是我们要消灭的那种"看不出来"的错。
				var existing = VpkSourceIdentity.FindExistingMod(_paths.ModsDirectory, job.PrimaryVpkPath);
				if (existing is not null && !updatedTargets.Add(Path.GetFullPath(existing.Directory)))
					existing = null;
				string? reuseModelPath = null;
				if (existing is not null)
				{
					// 复用旧素体定义：unit id 与 key 都必须逐位相同，否则护甲映射、
					// idea 里的开关掩码、真映射写进 dl_bin 的件表全部失效。
					// 这条与"重构建"用的是同一个机制（见 VpkRebuildService）。
					reuseModelPath = Path.Combine(
						existing.Directory, ModNodePayloadLayout.VpkPatchDirectoryName, VpkCustomModelFile.FileName);
					if (!File.Exists(reuseModelPath)) reuseModelPath = null;
				}

				var result = await ConvertOneAsync(
					executable, job, raw,
					job.MaterialSourceListPath, switchListPath, job.IdNamespaceSource, job.IsModel,
					cancellationToken, onProgress, reuseModelPath).ConfigureAwait(false);
				if (result.PatchDirectory is null)
				{
					foreach (var input in job.Inputs)
						items.Add(new VpkImportItem(input.VpkPath, VpkImportOutcome.Skipped, null,
							result.FailureReason ?? "这个 VPK 里没有 HD2 能用的资源，因此没有产出 patch。", 0, 0, 0, result.Notes));
					continue;
				}

				if (existing is not null)
				{
					// 更新：转换物只是"待换进旧 Mod 的载荷"，不建新目录。
					var extraNote = existing.CandidateCount > 1
						? $"库里另有 {existing.CandidateCount - 1} 个同源 Mod（同名同大小），本次只更新了「{existing.Name}」；多余的可以删掉。"
						: null;
					produced.Add(new VpkProducedPackage(
						result.PatchDirectory, job.GroupName, job.IsModel,
						existing.Directory, existing.Name, result.ReportJson, existing.CandidateCount));
					items.Add(new VpkImportItem(job.PrimaryVpkPath, VpkImportOutcome.Converted, existing.Name, null,
						result.Units, result.Materials, result.Textures,
						extraNote is null
							? [.. result.Notes, $"库里已有来自同一批来源的 Mod「{existing.Name}」，本次**更新它**而不是新建。"]
							: [.. result.Notes, $"库里已有来自同一批来源的 Mod「{existing.Name}」，本次**更新它**而不是新建。", extraNote],
						Updated: true));
					continue;
				}

				// 目录名取**主体**那个 VPK 的名字：一个组只建一个 Mod，材质包不再是独立条目。
				var directoryName = MakeUniqueDirectoryName(
					SafeDirectoryName(Path.GetFileNameWithoutExtension(job.PrimaryVpkPath)),
					usedDirectoryNames);
				var target = Path.Combine(source.StagingRoot, "pkg", directoryName);
				var payload = Path.Combine(target, ModNodePayloadLayout.VpkPatchDirectoryName);
				Directory.CreateDirectory(target);
				Directory.Move(result.PatchDirectory, payload);

				// 把**这一组所有**来源 VPK 原件留在 Mod 文件夹根上。
				//
				// 为什么必须留：patch 是**转换产物**。换过目标护甲、烘过 section 之后，
				// sectional/ 里那份已经不是最初转换出来的东西了；没有原件，用户想回到
				// "原始转换"或者换个参数重转，就只能重新去下载一遍来源包。
				// 这同时是重构建的输入：根上有几个 VPK，重构建就转几个、产出几组 patch。
				// 它也是"同一来源"的判据（见 VpkSourceIdentity）。
				foreach (var input in job.Inputs)
					File.Copy(input.VpkPath, Path.Combine(target, Path.GetFileName(input.VpkPath)), overwrite: true);

				await File.WriteAllTextAsync(Path.Combine(target, PackageReportFileName), result.ReportJson, cancellationToken)
					.ConfigureAwait(false);

				produced.Add(new VpkProducedPackage(target, job.GroupName, job.IsModel));
				foreach (var input in job.Inputs)
					items.Add(new VpkImportItem(input.VpkPath, VpkImportOutcome.Converted, directoryName, null,
						result.Units, result.Materials, result.Textures, result.Notes));
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception exception) when (exception is IOException or InvalidDataException
				or InvalidOperationException or UnauthorizedAccessException or JsonException)
			{
				// 一组失败**不拖垮整批**：其它组照样能导入。
				foreach (var input in job.Inputs)
					items.Add(new VpkImportItem(input.VpkPath, VpkImportOutcome.Failed, null, exception.Message, 0, 0, 0, []));
			}
		}

		if (plan.Groups.Count != 0)
		{
			notes.Add($"识别到 {plan.Groups.Count} 个模型主体："
				+ string.Join("、", plan.Groups.Select(group =>
					$"{group.DisplayName}（{group.Materials.Count} 个材质包）"))
				+ "。每个主体各用一份命名空间，互不覆盖。");
		}

		if (items.Any(item => item.Outcome == VpkImportOutcome.Converted))
			notes.Add("转换产物写在各 Mod 文件夹的 vpk2patch/ 下；同目录的 vpk2patch.json 是这一批的转换报告。");

		// 批次落地后：把物理参数容器嵌进**模型 patch**（来源有 jiggle 骨才做；材质包由 CLI 自己跳过）。
		// 放在最后一步是刻意的——转换阶段往 `.gpu_resources` 追加字节会让后续受管步骤记账错位
		// （历史症状：材质整体投递失败、面部品红）。这里已经是产物，没人会再重排它。
		//
		// 来源由 CLI 自己找（转换期产出的 physbone-rig.bin 优先，其次目录里唯一的 .vpk）：
		// 这里以前取"第一个 *.vpk"，多 VPK 的包（模型 + 贴图分包）会选错模型，
		// 拿别的模型的参数去驱动这套护甲——静默的。
		foreach (var package in produced)
		{
			// **更新型的产物跳过这一步。**
			//
			// 它们的 `Directory` 只是暂存里的转换物（没有来源 VPK），而 `physbone embed-mod`
			// 要靠目录里那份 .vpk 才找得到 jiggle 骨 —— 在这里跑只会得到"没有物理参数"，
			// 然后被原样换进用户的 Mod，把原来那套参数覆盖成空的。
			// 真正的嵌入放在换进 Mod 之后（见 ImportService.SwapIntoExistingModAsync）。
			if (package.IsUpdate)
				continue;

			var modDirectory = package.Directory;
			var modName = Path.GetFileName(modDirectory);
			try
			{
				var (code, embedOutput, embedError) = await RunCliAsync(
					executable,
					["physbone", "embed-mod", modDirectory, "--json"],
					cancellationToken).ConfigureAwait(false);
				if (code != 0)
				{
					notes.Add($"{modName}：物理参数嵌入失败（hd2mod 退出码 {code}）：{(string.IsNullOrWhiteSpace(embedError) ? embedOutput : embedError).Trim()}");
					continue;
				}

				// 退出码 0 只说明"命令跑完了"。**跳过也返回 0**，所以必须读 json 里的 ok/embedded，
				// 否则"这个 mod 没有物理参数"和"已经嵌好了"在报告里长得一模一样，用户一个字都看不到。
				var outcome = ParseEmbedOutcome(embedOutput);
				if (outcome is null)
					continue;   // 输出不是预期格式：不编造结论，也不打扰用户

				notes.Add(outcome.Embedded
					? $"{modName}：已写入物理参数（{outcome.Patches} 个 patch、{outcome.Bones} 根骨"
						+ (string.IsNullOrWhiteSpace(outcome.RigSource) ? "）" : $"，参数来自 {outcome.RigSource}）")
					: $"{modName}：**没有物理参数**（{outcome.Reason ?? "原因未知"}）——"
						+ "这套护甲不会有头发/裙摆摆动。");
			}
			catch (Exception exception) when (exception is IOException or InvalidOperationException or InvalidDataException or JsonException)
			{
				// 嵌不进去不该让导入失败：参数可以以后补，产物本身是好的。
				notes.Add($"{modName}：物理参数嵌入跳过（{exception.Message}）");
			}
		}

		return (new VpkImportReport(source.SourcePath, items, notes), produced);
	}

	/// <summary><c>physbone embed-mod --json</c> 的输出。</summary>
	/// <param name="Bones">-1 表示这次没解析骨数（来自转换期 rig 或裸 rig，长度由回读校验把关）。</param>
	private sealed record EmbedOutcome(bool Embedded, int Bones, int Patches, string? RigSource, string? Reason);

	private static EmbedOutcome? ParseEmbedOutcome(string json)
	{
		if (string.IsNullOrWhiteSpace(json))
			return null;
		try
		{
			using var document = JsonDocument.Parse(json);
			var root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object)
				return null;
			return new EmbedOutcome(
				root.TryGetProperty("embedded", out var embedded) && embedded.ValueKind == JsonValueKind.True,
				ReadInt(root, "bones"),
				ReadInt(root, "patches"),
				ReadString(root, "rigSource"),
				ReadString(root, "reason"));
		}
		catch (JsonException)
		{
			return null;
		}

		static int ReadInt(JsonElement root, string name) =>
			root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
				? number
				: 0;

		static string? ReadString(JsonElement root, string name) =>
			root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
				? value.GetString()
				: null;
	}

	private sealed record PlannedItem(string VpkPath, string? Reason, string Kind);

	/// <summary>规划结果里的一个「模型主体」。<c>Materials</c> 是它自己的材质包。</summary>
	private sealed record PlannedGroup(
		string ModelVpkPath,
		string DisplayName,
		string IdNamespaceSource,
		IReadOnlyList<string> Materials);

	private sealed record PlanResult(
		IReadOnlyList<PlannedItem> Items,
		IReadOnlyList<PlannedGroup> Groups,
		IReadOnlyList<string> UnassignedMaterials)
	{
		public IReadOnlyList<PlannedItem> Convertible => Items.Where(item => item.Reason is null).ToArray();
	}

	/// <summary>转换任务里的一个输入 VPK，以及它在规划阶段被认成的类型。</summary>
	private sealed record ConversionInput(string VpkPath, string Kind);

	/// <summary>
	/// 一次转换任务 = **一个自洽条目**（一个模型主体 + 它自己的全部材质包）。
	/// <para>
	/// <b>为什么是"一组 VPK"而不是"一个 VPK"。</b>存储约定是「一个 sectional Mod = 一个自洽条目，
	/// 材质不再作为独立 Option mod」：整组一次 <c>vpk2patch</c>，产物平铺成
	/// <c>patch_0</c>(unit) + <c>patch_1..N</c>(材质)，全部落进**同一个** Mod。
	/// 按单个 VPK 建任务的话，每个材质包都会各自变成一个 Mod —— 那正是"材质与主体分离"的成因
	/// （2026-11-08 用户实测：只有自带材质的 `恋星milfy_bill` 是自洽的）。
	/// </para>
	/// </summary>
	private sealed record ConversionJob(
		IReadOnlyList<ConversionInput> Inputs,
		string GroupName,
		string IdNamespaceSource,
		string MaterialSourceListPath,
		// true = 这一组里有模型主体（不是纯材质批）。
		bool IsModel)
	{
		/// <summary>这个任务的主 VPK：模型组就是模型，材质批就是第一个。目录名与"同一来源"判定用它。</summary>
		public string PrimaryVpkPath => Inputs.Count == 0 ? string.Empty : Inputs[0].VpkPath;
	}

	private async Task<PlanResult> PlanAsync(string executable, IReadOnlyList<string> vpkPaths, CancellationToken cancellationToken)
	{
		// 清单文件：一次导入上百个 VPK 时，把它们全拼进命令行会超过 Windows 的 32767 字符上限
		// （报的是「文件名或扩展名太长」，看着像路径问题，其实是命令行太长）。
		var listPath = WritePathList(Path.GetTempPath(), "hd2modmanager-vpk2patch-plan-" + Guid.NewGuid().ToString("N")[..8] + ".txt", vpkPaths);
		var arguments = new List<string> { "vpk2patch", "--vpk-list", listPath };
		arguments.AddRange(["--output", Path.Combine(Path.GetTempPath(), "hd2modmanager-vpk2patch-plan"), "--dry-run", "--json"]);

		try
		{
			var (exitCode, output, error) = await RunCliAsync(executable, arguments, cancellationToken).ConfigureAwait(false);
			if (exitCode != 0)
			{
				throw new InvalidDataException(
					$"规划 VPK 失败（hd2mod 退出码 {exitCode}）：{(string.IsNullOrWhiteSpace(error) ? output : error).Trim()}");
			}

			using var document = JsonDocument.Parse(output);
			var items = new List<PlannedItem>();
			foreach (var element in document.RootElement.GetProperty("convertible").EnumerateArray())
				items.Add(new PlannedItem(ReadString(element, "vpk") ?? string.Empty, null, ReadString(element, "kind") ?? string.Empty));
			foreach (var element in document.RootElement.GetProperty("skipped").EnumerateArray())
			{
				items.Add(new PlannedItem(
					ReadString(element, "vpk") ?? string.Empty,
					ReadString(element, "reason"),
					ReadString(element, "kind") ?? string.Empty));
			}

			var groups = new List<PlannedGroup>();
			if (document.RootElement.TryGetProperty("modelGroups", out var groupElements) && groupElements.ValueKind == JsonValueKind.Array)
			{
				foreach (var element in groupElements.EnumerateArray())
				{
					var modelPath = ReadString(element, "model") ?? string.Empty;
					if (modelPath.Length == 0) continue;
					groups.Add(new PlannedGroup(
						modelPath,
						ReadString(element, "name") ?? Path.GetFileNameWithoutExtension(modelPath),
						ReadString(element, "idNamespace") ?? Path.GetFileNameWithoutExtension(modelPath),
						ReadStrings(element, "materials")));
				}
			}

			return new PlanResult(items, groups, ReadStrings(document.RootElement, "unassignedMaterials"));
		}
		finally
		{
			try { File.Delete(listPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
		}
	}

	/// <summary>
	/// 把"主体 → 材质包"摊成逐条转换任务。**同一份材质包出现在多个主体下时就转多次**——
	/// 每个主体有自己的命名空间，同一份材质在另一套角色下必须是另一批 id，否则两套角色装一起会互相覆盖。
	/// 产出目录名加主体后缀，避免两次转换撞到同一个文件夹。
	/// </summary>
	private List<ConversionJob> BuildConversionWorkload(PlanResult plan)
	{
		var jobs = new List<ConversionJob>();
		if (plan.Groups.Count == 0)
		{
			// 没有主体（比如只导材质包）：整批当**一个**条目转。
			//
			// **排除"没归属"的那些**：报告里已经把它们写成"认不出属于谁，已跳过"，
			// 这里再转一遍就成了"报告说跳过、实际却导入了"，对不上账。
			var unassigned = plan.UnassignedMaterials.ToHashSet(StringComparer.OrdinalIgnoreCase);
			var convertible = plan.Convertible.Where(item => !unassigned.Contains(item.VpkPath)).ToArray();
			if (convertible.Length == 0) return jobs;
			var allListPath = WritePathList(
				Path.GetTempPath(),
				"hd2modmanager-materials-" + Guid.NewGuid().ToString("N")[..8] + ".txt",
				convertible.Select(item => item.VpkPath));
			var namespaceSource = string.Join("+", convertible
				.Select(item => Path.GetFileNameWithoutExtension(item.VpkPath))
				.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
			var inputs = convertible
				.Select(item => new ConversionInput(item.VpkPath, "material"))
				.ToArray();
			jobs.Add(new ConversionJob(inputs, "（整批）", namespaceSource, allListPath, IsModel: false));
			return jobs;
		}

		var staging = Path.GetTempPath();
		for (var index = 0; index < plan.Groups.Count; index++)
		{
			var group = plan.Groups[index];
			// 解析集合 = 这个主体的模型 + 它自己的材质包。**不含别的角色**：
			// 解析按裸名先到先得，混进别人的包就会串味。
			var materialSource = new[] { group.ModelVpkPath }.Concat(group.Materials).ToArray();
			var listPath = WritePathList(
				staging,
				$"hd2modmanager-materials-{index}-{Guid.NewGuid().ToString("N")[..8]}.txt",
				materialSource);

			// **一个主体 = 一个任务 = 一个 Mod。** 模型在前（决定 unit patch 的归属），
			// 它自己的材质包跟在后面；类型逐 VPK 标出来，转换器不用再猜。
			var inputs = new List<ConversionInput> { new(group.ModelVpkPath, "model") };
			inputs.AddRange(group.Materials.Select(material => new ConversionInput(material, "material")));
			jobs.Add(new ConversionJob(inputs, group.DisplayName, group.IdNamespaceSource, listPath, IsModel: true));
		}

		return jobs;
	}

	/// <summary>
	/// 给 <c>--treat-as-list</c> 写一份「路径 → 类型」清单（每行 <c>&lt;绝对路径&gt;\t model|material</c>）。
	/// <para>
	/// 为什么不在命令行上逐 VPK 传：一次导入上百个包时命令行会撞 Windows 的 32767 字符上限
	/// （报的是「文件名或扩展名太长」，看着像路径问题）。
	/// </para>
	/// </summary>
	private static string WriteTreatAsList(ConversionJob job)
	{
		var path = Path.Combine(
			Path.GetTempPath(),
			"hd2modmanager-treatas-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
		var lines = job.Inputs.Select(input => Path.GetFullPath(input.VpkPath) + "\t" + input.Kind);
		File.WriteAllLines(path, lines);
		return path;
	}

	/// <summary>把一批路径写成清单文件（每行一个绝对路径），供 CLI 的 <c>--*-list</c> 参数读取。</summary>
	private static string WritePathList(string directory, string fileName, IEnumerable<string> paths)
	{
		var path = Path.Combine(directory, fileName);
		File.WriteAllLines(path, paths.Where(line => !string.IsNullOrWhiteSpace(line)));
		return path;
	}

	private static string? ReadString(JsonElement element, string name)
		=> element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
			? value.GetString() is { Length: > 0 } text ? text : null
			: null;

	private static IReadOnlyList<string> ReadStrings(JsonElement element, string name)
	{
		if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
			return [];
		var items = new List<string>();
		foreach (var item in value.EnumerateArray())
		{
			if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
				items.Add(text);
		}

		return items;
	}

	private sealed record ConversionResult(
		string? PatchDirectory,
		string? FailureReason,
		string ReportJson,
		int Units,
		int Materials,
		int Textures,
		IReadOnlyList<string> Notes);

	private async Task<ConversionResult> ConvertOneAsync(
		string executable,
		ConversionJob job,
		string outputRoot,
		string materialSourceListPath,
		string? componentSourceListPath,
		string idNamespaceSource,
		bool isModel,
		CancellationToken cancellationToken,
		Action<string, int, int, string>? onProgress = null,
		string? reuseModelPath = null)
	{
		Directory.CreateDirectory(outputRoot);
		var arguments = new List<string> { "vpk2patch" };
		arguments.AddRange(job.Inputs.Select(input => input.VpkPath));
		arguments.AddRange(["--output", outputRoot]);

		// **逐 VPK 的类型**：模型出 unit patch、材质包出材质 patch，而一次调用里两者是混着的。
		//   · 单输入：`--treat-as` 就够（不能让它只看"有没有 .mdl"：整合包里"材质包塞了展示模型"
		//     会被当角色去转，死在模型解析上，实测 `MDL 骨骼数量异常：0`）。
		//   · 多输入：`--treat-as` 会作用于整批，混着传必然错。改用 `--treat-as-list` 逐行指定，
		//     结论来自规划阶段（`--dry-run` 的 `modelGroups`）。
		if (job.Inputs.Count == 1)
		{
			arguments.AddRange(["--treat-as", job.Inputs[0].Kind]);
		}
		else
		{
			var typesPath = WriteTreatAsList(job);
			arguments.AddRange(["--treat-as-list", typesPath]);
		}

		arguments.AddRange([
			// 解析集合（**本主体自己那一组**）走清单文件：上百个 VPK 的路径拼进命令行会超出
			// Windows 的 32767 字符上限。注意它不再是"整批"——混进别的角色就会串材质。
			"--material-source-list", materialSourceListPath,
			// 每个主体一个命名空间：同一份材质在不同主体下得到不同 id，几套角色可以共存。
			"--id-namespace", idNamespaceSource,
			// 默认产出"7 槽位标准版"：附加部件并进它主导的那个身躯槽位。
			"--components", "merge",
			// matcap 涂成**中性金属 F0**，而不是原样当底色。
			//
			// **这个改动有实机依据**：albedo 模式下金属件的底色就是 matcap 图，
			// 实测它的平均亮度只等效反射率 0.14（银应该是 0.97），于是"又黑又没反光"；
			// 而且 matcap 的 UV 是视角法线、不是模型 UV，当底色会采到球面上的一块随机区域
			// （实测鞋底落在暗部 = "粗糙的生铁"）。
			// metal 模式丢掉 matcap 的图案与色相，整片涂成中性 F0（默认 0.96），
			// 配合金属度 1.0 / 粗糙度 0.20 交给 PBR 算反光。
			// **色相必须丢**：matcap 由可选的颜色变体包提供，取色相会让同一份源的
			// 转换结果取决于装了哪个变体包。详见 MaterialAppearance.FlattenToMetalF0。
			"--matcap", "metal",
			"--manager-root", _managerRoot,
			"--library", _libraryRoot,
			"--game-data", _gameDataDirectory,
			"--json",
		]);

		// 复用旧素体定义（key + unit id 基）：
		// 重新导入同一个 VPK 时要**更新**原来那个 Mod，而 unit id 是 `seed + index` 生成的。
		// 不指过去就会重新随机 —— 护甲映射、衣柜里记的开关掩码、真映射写进 dl_bin 的件表
		// 全部失效，用户看到的是"重导一次，之前挂好的护甲不生效了"。
		// 与重构建用的是同一个 CLI 参数（见 VpkRebuildService）。
		if (!string.IsNullOrWhiteSpace(reuseModelPath))
			arguments.AddRange(["--reuse-model", reuseModelPath]);

		// 组件开关包单独指过去：它们只用于"识别开关"，不进解析集合（会顶掉同名真材质），
		// 而分主体转换时它们散在各组之外——不指过来就翻译不出 components.json。
		if (!string.IsNullOrWhiteSpace(componentSourceListPath))
			arguments.AddRange(["--component-source-list", componentSourceListPath]);

		var (exitCode, output, error) = await RunCliAsync(executable, arguments, cancellationToken, onProgress).ConfigureAwait(false);
		if (string.IsNullOrWhiteSpace(output))
		{
			throw new InvalidDataException(
				$"转换 {job.GroupName} 失败（hd2mod 退出码 {exitCode}）：{error.Trim()}");
		}

		using var document = JsonDocument.Parse(output);
		var root = document.RootElement;
		var notes = new List<string>();
		if (root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.Object)
		{
			return new ConversionResult(null, errorElement.TryGetProperty("message", out var message) ? message.GetString() : null,
				output, 0, 0, 0, notes);
		}

		// ⚠️ **必须以 CLI 自己的结论为准，不能只判"输出非空"。**
		//
		// `vpk2patch` 的退出码与 JSON 里的 `ok` 是同一个判据（`ok = exitCode == 0`）。
		// 以前这里只看输出是否为空，于是 CLI 因 `registryError`（素体没能登记）返回 2 时，
		// 只要 patch 文件还在盘上，管理器就当成成功继续走 —— 库里留下一个
		// **"patch 有、素体没登记"的半成品**：护甲换外观不生效，而界面上一切正常。
		//
		// 半成品比失败更难查（它看起来是"转换成功但游戏里没反应"），所以这里一旦
		// CLI 说不行就整体判失败，把原因带到用户面前。
		var registryError = ReadFirstRegistryError(root);
		if (root.TryGetProperty("ok", out var okElement) && okElement.ValueKind == JsonValueKind.False)
		{
			var detail = !string.IsNullOrWhiteSpace(registryError)
				? $"素体未登记：{registryError}"
				: error.Trim();
			throw new InvalidDataException(
				$"转换 {job.GroupName} 未成功（hd2mod 退出码 {exitCode}）"
				+ (string.IsNullOrWhiteSpace(detail) ? "。" : $"：{detail}"));
		}

		// ⚠️ **一次转换的产出是「每个输入 VPK 一个子目录」**（模型一个、每个材质包各一个）。
		// 而存储约定要求 `sectional/` 里全部 patch 组**平铺按序号**：unit 恒为 `patch_0`，
		// 材质 `patch_1..patch_N`，不建子目录。所以这里必须先把产出收成一份平铺载荷。
		//
		// 少了这一步只有两种结果，都很糟：
		//   · 只取第一个子目录 → 模型 patch 进来、材质全丢（"导入成功、游戏里材质没了"）；
		//   · 把整个输出根搬进去 → 得到一堆子目录，而下游所有枚举器只扫顶层，
		//     这些 patch 既不被识别也不被部署（"部署成功、什么都没变"）。
		var payloadDirectory = outputRoot.TrimEnd(Path.DirectorySeparatorChar) + "-payload";
		var collected = VpkPayloadCollector.Collect(
			outputRoot,
			payloadDirectory,
			VpkConversionReport.TryParse(output),
			Path.GetFileNameWithoutExtension(job.PrimaryVpkPath),
			// 报告要跟着改成平铺后的编号：`patches.json` 的材质选项名字全靠它。
			rawReportJson: output,
			// 原始 VPK 名（大小写以磁盘为准）：报告里的名字常被 CLI 洗成小写，
			// 直接拿来当材质选项名会显示成 "elise基础材质a1主体"。
			sourceVpkPaths: job.Inputs.Select(input => input.VpkPath).ToArray());
		if (!collected.Succeeded)
		{
			return new ConversionResult(
				null,
				(collected.Error ?? "收集转换产物失败。") + ExplainNothingProduced(root),
				output, 0, 0, 0, notes);
		}

		foreach (var note in collected.Notes)
			notes.Add(note);
		var patchDirectory = payloadDirectory;

		var units = 0;
		var materials = 0;
		var textures = 0;
		// 报告级的提示（不是某个 patch 的警告）：例如"这次转换没给命名空间，同一模型的
		// 相关 VPK 必须一次性转换"。它描述的是**整批**的性质，只存在于根对象上；
		// 漏读等于让用户看不到这条唯一的线索。
		foreach (var note in ReadStrings(root, "notes"))
			notes.Add(note);
		if (root.TryGetProperty("patches", out var patches) && patches.ValueKind == JsonValueKind.Array)
		{
			foreach (var patch in patches.EnumerateArray())
			{
				units += ReadInt(patch, "units");
				materials += ReadInt(patch, "materials");
				textures += ReadInt(patch, "textures");
				foreach (var warning in ReadStrings(patch, "warnings"))
					notes.Add(warning);
				// 诊断不是警告：它表示"该写出来的东西没写出来"。以前只在 JSON 里，界面完全看不到。
				foreach (var diagnostic in ReadStrings(patch, "diagnostics"))
					notes.Add("诊断：" + diagnostic);

				// **材质总账**：模型要的材质里，哪些没有真实落地。
				//
				// 以前管理器完全不读这一段，于是"某个部件变成一块灰面"或者"某个部件没了"
				// 在界面上**没有任何解释**——用户只能看到"导入成功"。而这份总账正是
				// 唯一能回答"我那件东西为什么不对"的数据，它已经在 JSON 里躺了很久。
				//
				// 两类分开说，因为处理方式不同：
				//   · fallback 有兜底灰材质 → 部件在，颜色/图案不对（能透过外观判断是哪里）；
				//   · missing  连兜底都没有   → 部件会被移除（要去看诊断里的技术原因）。
				if (patch.TryGetProperty("materialGap", out var gap) && gap.ValueKind == JsonValueKind.Object)
				{
					var fallback = ReadStrings(gap, "fallback");
					if (fallback.Count != 0)
					{
						notes.Add($"材质总账：{fallback.Count} 个材质用了兜底灰占位（源包里没有它们，颜色/图案不会对）："
							+ DescribeFirst(fallback));
					}

					var missing = ReadStrings(gap, "missing");
					if (missing.Count != 0)
					{
						notes.Add($"材质总账：{missing.Count} 个材质连兜底都造不出来，对应部件会被移除："
							+ DescribeFirst(missing));
					}
				}
			}
		}

		// 报告要用**改写过 patch 文件名**的那一份：`patches.json` 靠它判定哪个 patch 是 unit、
		// 每个材质选项叫什么。写原始报告的话，合并进来的材质在清单里没有名字。
		return new ConversionResult(
			patchDirectory, null, collected.ReportJson ?? output, units, materials, textures, notes);
	}

	/// <summary>把一串名字写成"前几个 + 共几个"，避免几十个名字把界面撑爆。</summary>
	private static string DescribeFirst(IReadOnlyList<string> names, int limit = 6)
		=> names.Count <= limit
			? string.Join("、", names)
			: string.Join("、", names.Take(limit)) + $"…（共 {names.Count} 个）";

	/// <summary>
	/// 游戏资产索引**在不在**。<c>null</c> = 就绪（可以直接用）。
	/// <para>
	/// 判据是"索引数据库里能不能读出装备关系表"——那正是模型转换选外壳 Unit 用的那张表。
	/// **刻意不判"过期"**：过期与否要看游戏文件指纹，那属于用户在「设置与资产」里的决定；
	/// 导入流程只在"根本没有"时自动补一次。
	/// </para>
	/// </summary>
	private async Task<string?> DescribeMissingGameDataIndexAsync(CancellationToken cancellationToken)
	{
		// 一批导入是"一个来源一个 `VpkImportService`"，所以同一个批次里会问很多次。
		// 索引库有 100 MB 量级，逐个来源都去读一遍表既慢又没必要 —— 按
		// （路径 + 大小 + 修改时间）记一次结果就够了；重建/替换库文件会让键自然失效。
		var stamp = DescribeIndexFile(_paths.DbPath);
		if (GameDataIndexProbeCache.TryGetValue(stamp, out var cached))
			return cached;

		string? problem;
		try
		{
			var snapshot = await ArmorAuthorityIndexReader.TryReadAsync(_paths, cancellationToken).ConfigureAwait(false);
			problem = snapshot is null ? $"索引数据库里没有装备关系表：{_paths.DbPath}" : null;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
			or InvalidDataException or NotSupportedException
			or Microsoft.Data.Sqlite.SqliteException)
		{
			// 库文件存在但读不懂 / 被占用，与"没有"同样处理：都等于拿不到。
			problem = $"索引数据库读不出来：{exception.Message}";
		}

		GameDataIndexProbeCache[stamp] = problem;
		return problem;
	}

	/// <summary>索引库文件的"身份"：路径 + 大小 + 修改时间。三者任一变化就重新探一次。</summary>
	private static string DescribeIndexFile(string path)
	{
		try
		{
			var info = new FileInfo(path);
			return info.Exists
				? $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}"
				: $"{path}|missing";
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return $"{path}|unreadable";
		}
	}

	/// <summary>
	/// 「索引库身份 → 探测结果」的记忆。只缓存"有没有"，不缓存快照本身
	/// （快照是调用方要用的数据，缓存它会让人以为读到的是一次性的新鲜结论）。
	/// </summary>
	private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> GameDataIndexProbeCache = new(StringComparer.Ordinal);

	/// <summary>
	/// 自动建立一次游戏资产索引（基础索引 + Armor/Helmet 的 Unit 部件事实）。
	/// <para>
	/// 两步必须连着做，理由与「设置与资产」那个按钮一致：VPK 换甲依赖部位事实，
	/// 只做基础索引的话，用户"刚建完索引"仍然会在换甲时被告知需要重建。
	/// </para>
	/// </summary>
	/// <returns>成功返回 <c>null</c>；失败返回可读原因。</returns>
	private async Task<string?> TryBuildGameDataIndexAsync(Action<string>? onStage, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(_gameDataDirectory) || !Directory.Exists(_gameDataDirectory))
			return "没有配置游戏 data 目录";

		try
		{
			var archiveHashes = await CoreServices.CreateFileSystemArchiveHashesProvider(_paths)
				.GetArchiveHashesJsonAsync(cancellationToken).ConfigureAwait(false);

			var index = CoreServices.CreateAssetArchiveIndexService(_paths);
			var progress = new Progress<HD2ModCore.Application.IndexBuildProgress>(item =>
				onStage?.Invoke(item.Total <= 0
					? "正在索引 Archive…"
					: $"正在索引 Archive {item.Current}/{item.Total}"));
			await Task.Run(
				() => index.BuildOrRebuildAsync(_gameDataDirectory, archiveHashes, progress, cancellationToken).AsTask(),
				cancellationToken).ConfigureAwait(false);

			onStage?.Invoke("正在计算 Unit 部件部位…");
			var advanced = CoreServices.CreateAdvancedEquipmentIndexService(_paths);
			var unitProgress = new Progress<HD2ModCore.Application.IndexBuildProgress>(item =>
				onStage?.Invoke(item.Current <= 0
					? (item.CurrentArchiveId ?? "正在分析 Unit 部位")
					: $"正在分析 Unit 部位 {item.Current}/{item.Total}"));
			await Task.Run(
				() => advanced.BuildMissingUnitPartFactsAsync(_gameDataDirectory, unitProgress, cancellationToken).AsTask(),
				cancellationToken).ConfigureAwait(false);

			return null;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception)
		{
			return exception.Message;
		}
	}

	/// <summary>
	/// 从转换报告里取第一条 <c>registryError</c>（素体没能登记的真正原因）。
	/// <para>
	/// 这一个字段决定"导进去了但护甲换不了"——它只出现在 patch 对象上，而报告根对象的
	/// <c>ok</c> 已经因此是 false。没有它，用户只能看到"转换未成功"而不知道是登记失败。
	/// </para>
	/// </summary>
	private static string? ReadFirstRegistryError(JsonElement root)
	{
		if (!root.TryGetProperty("patches", out var patches) || patches.ValueKind != JsonValueKind.Array)
			return null;
		foreach (var patch in patches.EnumerateArray())
		{
			if (patch.TryGetProperty("registryError", out var value) && value.ValueKind == JsonValueKind.String
				&& value.GetString() is { Length: > 0 } text)
			{
				return text;
			}
		}

		return null;
	}

	/// <summary>
	/// 一个 VPK 什么产物都没有时，给一句**能照做**的原因。
	/// <para>
	/// 优先用 CLI 自己写好的 <c>note</c>（它知道"占位几何""只有描边遮罩"这类源侧意图），
	/// 其次用第一条诊断（真正的技术原因，例如"法线是零向量"），最后才退回那句通用说明。
	/// </para>
	/// </summary>
	private static string? ExplainNothingProduced(JsonElement root)
	{
		if (!root.TryGetProperty("patches", out var patches) || patches.ValueKind != JsonValueKind.Array)
			return null;
		foreach (var patch in patches.EnumerateArray())
		{
			if (patch.TryGetProperty("note", out var note) && note.ValueKind == JsonValueKind.String
				&& !string.IsNullOrWhiteSpace(note.GetString()))
			{
				return note.GetString();
			}

			foreach (var diagnostic in ReadStrings(patch, "diagnostics"))
				return "转换没能写出 patch：" + diagnostic;
		}

		return null;
	}

	private static int ReadInt(JsonElement element, string name)
		=> element.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : 0;

	/// <summary>
	/// 跑一次 <c>hd2mod.exe</c>。
	/// <para>
	/// 进程样板（编码、逐行读 stderr、取消时杀进程树）在 <see cref="Hd2ModProcess"/> 里只有一份：
	/// 以前导入 / 换甲 / 重构建各自抄了一遍，差异全在"怎么读 stderr"上，而那种差异的表现
	/// 是"某个入口永远看不到进度"，或者"失败原因里塞满进度行"。
	/// </para>
	/// </summary>
	private static Task<Hd2ModProcess.Result> RunCliAsync(
		string executable,
		IReadOnlyList<string> arguments,
		CancellationToken cancellationToken,
		Action<string, int, int, string>? onProgress = null)
		=> Hd2ModProcess.RunAsync(executable, arguments, cancellationToken, onProgress);

	/// <summary>
	/// 转换器的位置随布局而变：开发构建把它复制到管理器旁边（<c>CopyHd2ModCliArtifacts</c>），
	/// 发布包把它放在 <c>bin\</c> 下。见 <see cref="Hd2ModCliLocator"/>。
	/// </summary>
	private static string ResolveCliExecutable()
	{
		var resolved = Hd2ModCliLocator.TryResolve();
		if (resolved is not null) return resolved;
		throw new FileNotFoundException(
			"找不到 hd2mod.exe：VPK 转换依赖它。请确认管理器目录（或其中的 bin 子目录）里有 hd2mod.exe（发布包自带）。"
			+ $"已查找：{Hd2ModCliLocator.DescribeSearchedLocations()}",
			Hd2ModCliLocator.GetCandidatePaths()[0]);
	}

	/// <summary>
	/// 递归列出目录下的 VPK。分卷 <c>pak01_000.vpk</c> 由 <c>pak01_dir.vpk</c> 引用，
	/// 单独列出来会重复转换，所以跳过纯数字分卷。
	/// </summary>
	public static IReadOnlyList<string> EnumerateVpkFiles(string directory)
	{
		if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
			return Array.Empty<string>();
		return Directory.EnumerateFiles(directory, "*.vpk", SearchOption.AllDirectories)
			.Where(path => !IsVolume(path))
			.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	private static bool IsVolume(string path)
	{
		var stem = Path.GetFileNameWithoutExtension(path);
		return stem.Length > 4
			&& stem[^4] == '_'
			&& stem[^3..].All(char.IsAsciiDigit);
	}

	/// <summary>
	/// 这个目录是不是<b>已经转换过</b>的 VPK 版 Mod（根上有转换报告，或已有基底目录）。
	/// <para>
	/// 判据只看转换器自己写的这两样东西：它们只有转换器会产出，用户的源包不会有。
	/// 基底目录新旧两个名字都要认——老库里它叫 <c>vpk2patch/</c>。
	/// </para>
	/// </summary>
	private static bool IsConvertedPackage(string directory)
		=> File.Exists(Path.Combine(directory, PackageReportFileName))
			|| ModNodePayloadLayout.TryGetExistingVpkPatchDirectory(directory) is not null;

	private string CreateStaging()
	{
		// **短临时根**：整个路径预算里，临时目录是最容易被忽视的一段。
		// `%TEMP%\hd2modmanager-vpk2patch\<32 位 GUID>` 就吃掉 85 个字符，再叠上包里那 212 字符的
		// 嵌套目录就直接越过 260。这里换成 `%TEMP%\h2v\<8 位>`，只占 ~30。
		// 缩短不影响"目录名可预期"这条要求：它在 %TEMP% 下、前缀固定，出问题照样找得到。
		var staging = Path.Combine(Path.GetTempPath(), "h2v", Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(staging);
		return staging;
	}

	/// <summary>
	/// 解压前判断"能不能摊平"：包内 VPK 的文件名必须互不相同。
	/// <para>同名会被 <c>7z e</c> 覆盖掉一个——那是静默丢包，比路径太长更糟。</para>
	/// </summary>
	private static bool HasUniqueVpkNames(IReadOnlyList<string> entries, out string? duplicateName)
	{
		duplicateName = null;
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var entry in entries)
		{
			if (!entry.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase))
				continue;
			var name = Path.GetFileName(entry);
			if (!seen.Add(name))
			{
				duplicateName = name;
				return false;
			}
		}

		return true;
	}

	public static void DeleteQuietly(string directory)
	{
		try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}

	/// <summary>
	/// 命名空间的"来源标识"：压缩包/目录/单个 VPK 的名字（去扩展名）。
	/// <para>
	/// 只取名字、不取路径：同一个包被解到别的地方、或者用户挪了目录，重转出来的 id 仍应相同。
	/// 真正的 token 由 CLI 侧算（<c>Hd2AssetId.NamespaceFor</c>），这里只负责给一个稳定的名字。
	/// </para>
	/// </summary>
	private static string ResolveNamespaceSource(string sourcePath)
	{
		if (string.IsNullOrWhiteSpace(sourcePath))
			return "vpk";
		var trimmed = sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		var name = Path.GetFileNameWithoutExtension(trimmed);
		if (string.IsNullOrWhiteSpace(name))
			name = Path.GetFileName(trimmed);
		return string.IsNullOrWhiteSpace(name) ? "vpk" : name;
	}

	private static string SafeDirectoryName(string name)
	{
		var sanitized = string.IsNullOrWhiteSpace(name) ? "VPK" : name.Trim();
		foreach (var ch in Path.GetInvalidFileNameChars()) sanitized = sanitized.Replace(ch, '_');
		return string.IsNullOrWhiteSpace(sanitized) ? "VPK" : sanitized;
	}

	/// <summary>在本批里取一个没被用过的产出目录名：撞名时依次追加 <c> (2)</c>、<c> (3)</c>……</summary>
	private static string MakeUniqueDirectoryName(string name, HashSet<string> used)
	{
		if (used.Add(name)) return name;
		for (var suffix = 2; ; suffix++)
		{
			var candidate = $"{name} ({suffix.ToString(CultureInfo.InvariantCulture)})";
			if (used.Add(candidate)) return candidate;
		}
	}

}

/// <summary>一个已摊开的 VPK 来源：临时根目录 + 这批 VPK。</summary>
public sealed class VpkImportSource(string sourcePath, string stagingRoot, IReadOnlyList<string> vpkPaths, bool ownsStaging)
{
	public string SourcePath { get; } = sourcePath;
	public string StagingRoot { get; } = stagingRoot;
	public IReadOnlyList<string> VpkPaths { get; } = vpkPaths;
	public bool OwnsStaging { get; } = ownsStaging;

	public void Dispose()
	{
		if (OwnsStaging) VpkImportService.DeleteQuietly(StagingRoot);
	}
}
