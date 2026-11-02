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
public sealed record VpkImportItem(
	string VpkPath,
	VpkImportOutcome Outcome,
	string? ModDirectoryName,
	string? Reason,
	int Units,
	int Materials,
	int Textures,
	IReadOnlyList<string> Notes);

/// <summary>
/// 一个转出来的 Mod 目录，以及它在**哪个模型主体组**里、它是主体还是材质包。
/// <para>
/// 分组要一路带到"挂选项"那一步：几套角色的材质裸名相同，把 plum 的材质挂到 chiffon 下面
/// 会得到一堆"看起来对、点开却什么都不变"的选项。
/// </para>
/// </summary>
public sealed record VpkProducedPackage(string Directory, string? GroupName, bool IsModel);

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
					lines.Add($"  ✔ {name} → {item.ModDirectoryName}（{item.Units} 个 Unit / {item.Materials} 个材质 / {item.Textures} 张贴图）");
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
		var index = 0;
		foreach (var job in workload)
		{
			cancellationToken.ThrowIfCancellationRequested();
			index++;
			var name = Path.GetFileName(job.VpkPath);
			onStage?.Invoke($"正在转换 {index}/{workload.Count}：{job.GroupName} → {name}");

			var raw = Path.Combine(source.StagingRoot, "raw", index.ToString(CultureInfo.InvariantCulture));
			try
			{
				var result = await ConvertOneAsync(
					executable, job.VpkPath, raw,
					job.MaterialSourceListPath, switchListPath, job.IdNamespaceSource, job.IsModel,
					cancellationToken, onProgress).ConfigureAwait(false);
				if (result.PatchDirectory is null)
				{
					items.Add(new VpkImportItem(job.VpkPath, VpkImportOutcome.Skipped, null,
						result.FailureReason ?? "这个 VPK 里没有 HD2 能用的资源，因此没有产出 patch。", 0, 0, 0, result.Notes));
					continue;
				}

				var directoryName = MakeUniqueDirectoryName(
					SafeDirectoryName(Path.GetFileNameWithoutExtension(job.VpkPath) + job.DirectorySuffix),
					usedDirectoryNames);
				var target = Path.Combine(source.StagingRoot, "pkg", directoryName);
				var payload = Path.Combine(target, ModNodePayloadLayout.VpkPatchDirectoryName);
				Directory.CreateDirectory(target);
				Directory.Move(result.PatchDirectory, payload);

				// 把来源 VPK 原件留在 Mod 文件夹根上。
				//
				// 为什么必须留：patch 是**转换产物**。换过目标护甲、烘过 section 之后，
				// vpk2patch/ 里那份已经不是最初转换出来的东西了；没有原件，用户想回到
				// "原始转换"或者换个参数重转，就只能重新去下载一遍来源包。
				// 这也正是这套目录约定：根 = 来源，vpk2patch/ = 转换产物，
				// Overwrite/ = 在产物上再改出来的变体。
				File.Copy(job.VpkPath, Path.Combine(target, Path.GetFileName(job.VpkPath)), overwrite: true);

				await File.WriteAllTextAsync(Path.Combine(target, PackageReportFileName), result.ReportJson, cancellationToken)
					.ConfigureAwait(false);

				produced.Add(new VpkProducedPackage(target, job.GroupName, job.IsModel));
				items.Add(new VpkImportItem(job.VpkPath, VpkImportOutcome.Converted, directoryName, null,
					result.Units, result.Materials, result.Textures, result.Notes));
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception exception) when (exception is IOException or InvalidDataException
				or InvalidOperationException or UnauthorizedAccessException or JsonException)
			{
				// 单个 VPK 失败**不拖垮整批**：其它包照样能导入。
				items.Add(new VpkImportItem(job.VpkPath, VpkImportOutcome.Failed, null, exception.Message, 0, 0, 0, []));
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

	/// <summary>一次转换任务：转哪个 VPK、用哪组的命名空间与解析集合、产出目录要不要加后缀。</summary>
	private sealed record ConversionJob(
		string VpkPath,
		string GroupName,
		string IdNamespaceSource,
		string MaterialSourceListPath,
		string DirectorySuffix,
		// true = 这一项是模型主体（不是它的材质包）：挂"选项"关系时靠它区分主次。
		bool IsModel);

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
			// 没有主体（比如只导材质包）：退回"整批一份"的老做法，至少不要什么都不做。
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
			foreach (var item in convertible)
				jobs.Add(new ConversionJob(item.VpkPath, "（整批）", namespaceSource, allListPath, string.Empty, IsModel: false));
			return jobs;
		}

		// 同一份材质被几个主体共用 → 决定哪些产出目录要加主体后缀（只出现一次的不加，名字保持干净）。
		var usageCount = plan.Groups
			.SelectMany(group => group.Materials)
			.GroupBy(path => path, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

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

			jobs.Add(new ConversionJob(
				group.ModelVpkPath, group.DisplayName, group.IdNamespaceSource, listPath, string.Empty, IsModel: true));

			foreach (var material in group.Materials)
			{
				var shared = usageCount.TryGetValue(material, out var count) && count > 1;
				jobs.Add(new ConversionJob(
					material,
					group.DisplayName,
					group.IdNamespaceSource,
					listPath,
					shared ? $"（{group.DisplayName}）" : string.Empty, IsModel: false));
			}
		}

		return jobs;
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
		string vpkPath,
		string outputRoot,
		string materialSourceListPath,
		string? componentSourceListPath,
		string idNamespaceSource,
		bool isModel,
		CancellationToken cancellationToken,
		Action<string, int, int, string>? onProgress = null)
	{
		Directory.CreateDirectory(outputRoot);
		var arguments = new List<string>
		{
			"vpk2patch", vpkPath,
			"--output", outputRoot,
			// **显式告诉转换器这是模型还是材质包**。不能让它只看"有没有 .mdl"：整合包里
			// "材质包塞了展示模型"的情况会让它当角色去转，死在模型解析上（实测 `MDL 骨骼数量异常：0`）。
			"--treat-as", isModel ? "model" : "material",
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
		};

		// 组件开关包单独指过去：它们只用于"识别开关"，不进解析集合（会顶掉同名真材质），
		// 而分主体转换时它们散在各组之外——不指过来就翻译不出 components.json。
		if (!string.IsNullOrWhiteSpace(componentSourceListPath))
			arguments.AddRange(["--component-source-list", componentSourceListPath]);

		var (exitCode, output, error) = await RunCliAsync(executable, arguments, cancellationToken, onProgress).ConfigureAwait(false);
		if (string.IsNullOrWhiteSpace(output))
		{
			throw new InvalidDataException(
				$"转换 {Path.GetFileName(vpkPath)} 失败（hd2mod 退出码 {exitCode}）：{error.Trim()}");
		}

		using var document = JsonDocument.Parse(output);
		var root = document.RootElement;
		var notes = new List<string>();
		if (root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.Object)
		{
			return new ConversionResult(null, errorElement.TryGetProperty("message", out var message) ? message.GetString() : null,
				output, 0, 0, 0, notes);
		}

		var patchDirectory = FindPatchDirectory(outputRoot);
		if (patchDirectory is null)
		{
			// ⚠️ **没有产物时必须把 CLI 说的原因带出来。**
			//
			// 以前这里一律返回 null，管理器于是显示"这个 VPK 里没有 HD2 能用的资源"——
			// 而真实原因可能完全不是"没资源"：实测 `影鸢女武神shinano` 是**某个顶点的法线是零向量**，
			// 导致两个 Unit 写不出来，整份 patch 被丢弃。那句话把用户和排查者一起带偏了方向。
			return new ConversionResult(null, ExplainNothingProduced(root), output, 0, 0, 0, notes);
		}

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
			}
		}

		return new ConversionResult(patchDirectory, null, output, units, materials, textures, notes);
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

	/// <summary>
	/// 找到 CLI 产出的那个目录。<c>vpk2patch</c> 会在 <c>--output</c> 下建一个以包名命名的目录，
	/// 所以这里取"唯一含 patch 文件的子目录"，而不是猜名字（名字会被 CLI 清洗过）。
	/// </summary>
	private static string? FindPatchDirectory(string outputRoot)
	{
		if (!Directory.Exists(outputRoot)) return null;
		foreach (var directory in Directory.EnumerateDirectories(outputRoot))
		{
			if (Directory.EnumerateFiles(directory, "*.patch_*", SearchOption.TopDirectoryOnly).Any())
				return directory;
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
	/// 这个目录是不是<b>已经转换过</b>的 VPK 版 Mod（根上有 <c>vpk2patch/</c> 或 <c>vpk2patch.json</c>）。
	/// <para>
	/// 判据只看转换器自己写的这两样东西：它们只有 <c>vpk2patch</c> 会产出，用户的源包不会有。
	/// </para>
	/// </summary>
	private static bool IsConvertedPackage(string directory)
		=> File.Exists(Path.Combine(directory, PackageReportFileName))
			|| Directory.Exists(ModNodePayloadLayout.GetVpkPatchDirectory(directory));

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
