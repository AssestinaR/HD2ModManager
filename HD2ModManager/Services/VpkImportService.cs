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

		// **一个主体 = 一个条目**：把它的来源 VPK 平铺放进同一个目录，导入后由重构建统一转换 + 编译。
		//
		// 导入阶段**不再转换任何东西**：转换属于重构建（来源 → sectional → 编译 → Overwrite）。
		// 这样导入只有一件事——把权威来源放进 mod 根，链路自然打通。
		var usedDirectoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (plan.Groups.Count == 0)
		{
			// 没有主体（整批都是材质包）：整批当一个条目。
			var unassigned = plan.UnassignedMaterials.ToHashSet(StringComparer.OrdinalIgnoreCase);
			var convertible = plan.Convertible.Where(item => !unassigned.Contains(item.VpkPath)).ToArray();
			if (convertible.Length != 0)
			{
				var directoryName = MakeUniqueDirectoryName(
					SafeDirectoryName(Path.GetFileNameWithoutExtension(convertible[0].VpkPath) + "（整批）"),
					usedDirectoryNames);
				var target = Path.Combine(source.StagingRoot, "pkg", directoryName);
				Directory.CreateDirectory(target);
				foreach (var item in convertible)
					File.Copy(item.VpkPath, Path.Combine(target, Path.GetFileName(item.VpkPath)), overwrite: true);
				produced.Add(new VpkProducedPackage(target, "（整批）", IsModel: false));
				foreach (var item in convertible)
					items.Add(new VpkImportItem(item.VpkPath, VpkImportOutcome.Converted, directoryName, null, 0, 0, 0, []));
			}
		}
		else
		{
			var index = 0;
			foreach (var group in plan.Groups)
			{
				cancellationToken.ThrowIfCancellationRequested();
				index++;
				onStage?.Invoke($"正在打包 {index}/{plan.Groups.Count}：{group.DisplayName}");

				var sources = new[] { group.ModelVpkPath }.Concat(group.Materials).ToArray();
				var directoryName = MakeUniqueDirectoryName(SafeDirectoryName(group.DisplayName), usedDirectoryNames);
				var target = Path.Combine(source.StagingRoot, "pkg", directoryName);
				Directory.CreateDirectory(target);
				foreach (var vpk in sources)
					File.Copy(vpk, Path.Combine(target, Path.GetFileName(vpk)), overwrite: true);

				// **同一来源再来一次 = 更新那个条目**：换成新来源，随后重构建。
				var existing = VpkSourceIdentity.FindExistingMod(_paths.ModsDirectory, group.ModelVpkPath);
				produced.Add(new VpkProducedPackage(
					target, group.DisplayName, IsModel: true,
					existing?.Directory, existing?.Name, ReportJson: null,
					existing?.CandidateCount ?? 1));
				foreach (var vpk in sources)
					items.Add(new VpkImportItem(vpk, VpkImportOutcome.Converted, directoryName, null, 0, 0, 0, []));
			}
		}

		notes.Add("来源已入库；转换与编译会在导入后自动跑一次重构建，跑完才是最终产物。");
		return (new VpkImportReport(source.SourcePath, items, notes), produced);
	}

	/// <summary>规划里的一条：这个 VPK 能不能转、为什么不能。</summary>
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

	private async Task<PlanResult> PlanAsync(string executable, IReadOnlyList<string> vpkPaths, CancellationToken cancellationToken)
	{
		// 清单文件：一次导入上百个 VPK 时，把它们全拼进命令行会超过 Windows 的 32767 字符上限
		// （报的是「文件名或扩展名太长」，看着像路径问题，其实是命令行太长）。
		var listPath = WritePathList(Path.GetTempPath(), "hd2modmanager-vpk2patch-plan-" + Guid.NewGuid().ToString("N")[..8] + ".txt", vpkPaths);
		var arguments = new List<string> { "vpk2patch", "--vpk-list", listPath };
		arguments.AddRange(["--output", Path.Combine(Path.GetTempPath(), "hd2modmanager-vpk2patch-plan"), "--dry-run", "--json"]);
		// **三条路径都由管理器自己传，不许 CLI 去猜。**
		//
		// 发布包的 hd2mod.exe 在 <安装根>\bin\ 下，而设置与 data\ 在安装根 —— CLI 靠自己的目录
		// 反推根目录，一旦反推错就去找 bin\settings.json、bin\data\indexes\...，于是
		// "没配游戏 data、没有骨名表"，**每个 VPK 导入都在规划阶段失败**（1.5.5 实机回归：
		// 用户装的正是发布版，开发环境里不会复现）。管理器这三条路径本来就有，传过去这条路就
		// 不再依赖任何推导。
		if (!string.IsNullOrWhiteSpace(_managerRoot))
			arguments.AddRange(["--manager-root", _managerRoot]);
		if (!string.IsNullOrWhiteSpace(_libraryRoot))
			arguments.AddRange(["--library", _libraryRoot]);
		if (!string.IsNullOrWhiteSpace(_gameDataDirectory))
			arguments.AddRange(["--game-data", _gameDataDirectory]);

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
