using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
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
	public async Task<VpkImportSource?> TryPrepareAsync(string path, CancellationToken cancellationToken)
	{
		if (File.Exists(path) && Path.GetExtension(path).Equals(".vpk", StringComparison.OrdinalIgnoreCase))
		{
			return new VpkImportSource(path, CreateStaging(), [Path.GetFullPath(path)], ownsStaging: true);
		}

		if (Directory.Exists(path))
		{
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
			await extractor.ExtractAsync(path, extracted, cancellationToken).ConfigureAwait(false);
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
	public async Task<(VpkImportReport Report, IReadOnlyList<string> ProducedDirectories)> ConvertAsync(
		VpkImportSource source,
		Action<string>? onStage,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(source);
		var executable = ResolveCliExecutable();
		var items = new List<VpkImportItem>();
		var notes = new List<string>();
		var produced = new List<string>();

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

		// 解析集合：整批都交出去，这样材质包才能借到模型 VPK 里的几何与贴图。
		var materialSource = string.Join(';', source.VpkPaths);
		var index = 0;
		foreach (var candidate in plan.Convertible)
		{
			cancellationToken.ThrowIfCancellationRequested();
			index++;
			var name = Path.GetFileName(candidate.VpkPath);
			onStage?.Invoke($"正在转换 {index}/{plan.Convertible.Count}：{name}");

			var raw = Path.Combine(source.StagingRoot, "raw", index.ToString(CultureInfo.InvariantCulture));
			try
			{
				var result = await ConvertOneAsync(executable, candidate.VpkPath, raw, materialSource, cancellationToken).ConfigureAwait(false);
				if (result.PatchDirectory is null)
				{
					items.Add(new VpkImportItem(candidate.VpkPath, VpkImportOutcome.Skipped, null,
						result.FailureReason ?? "这个 VPK 里没有 HD2 能用的资源，因此没有产出 patch。", 0, 0, 0, result.Notes));
					continue;
				}

				var directoryName = SafeDirectoryName(Path.GetFileNameWithoutExtension(candidate.VpkPath));
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
				File.Copy(candidate.VpkPath, Path.Combine(target, Path.GetFileName(candidate.VpkPath)), overwrite: true);

				await File.WriteAllTextAsync(Path.Combine(target, "vpk2patch.json"), result.ReportJson, cancellationToken)
					.ConfigureAwait(false);

				produced.Add(target);
				items.Add(new VpkImportItem(candidate.VpkPath, VpkImportOutcome.Converted, directoryName, null,
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
				items.Add(new VpkImportItem(candidate.VpkPath, VpkImportOutcome.Failed, null, exception.Message, 0, 0, 0, []));
			}
		}

		if (items.Any(item => item.Outcome == VpkImportOutcome.Converted))
			notes.Add("转换产物写在各 Mod 文件夹的 vpk2patch/ 下；同目录的 vpk2patch.json 是这一批的转换报告。");

		return (new VpkImportReport(source.SourcePath, items, notes), produced);
	}

	private sealed record PlannedItem(string VpkPath, string? Reason);

	private sealed record PlanResult(IReadOnlyList<PlannedItem> Items)
	{
		public IReadOnlyList<PlannedItem> Convertible => Items.Where(item => item.Reason is null).ToArray();
	}

	private async Task<PlanResult> PlanAsync(string executable, IReadOnlyList<string> vpkPaths, CancellationToken cancellationToken)
	{
		var arguments = new List<string> { "vpk2patch" };
		arguments.AddRange(vpkPaths);
		arguments.AddRange(["--output", Path.Combine(Path.GetTempPath(), "hd2modmanager-vpk2patch-plan"), "--dry-run", "--json"]);

		var (exitCode, output, error) = await RunCliAsync(executable, arguments, cancellationToken).ConfigureAwait(false);
		if (exitCode != 0)
		{
			throw new InvalidDataException(
				$"规划 VPK 失败（hd2mod 退出码 {exitCode}）：{(string.IsNullOrWhiteSpace(error) ? output : error).Trim()}");
		}

		using var document = JsonDocument.Parse(output);
		var items = new List<PlannedItem>();
		foreach (var element in document.RootElement.GetProperty("convertible").EnumerateArray())
			items.Add(new PlannedItem(element.GetProperty("vpk").GetString() ?? string.Empty, null));
		foreach (var element in document.RootElement.GetProperty("skipped").EnumerateArray())
			items.Add(new PlannedItem(
				element.GetProperty("vpk").GetString() ?? string.Empty,
				element.TryGetProperty("reason", out var reason) ? reason.GetString() : null));
		return new PlanResult(items);
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
		string materialSource,
		CancellationToken cancellationToken)
	{
		Directory.CreateDirectory(outputRoot);
		var arguments = new List<string>
		{
			"vpk2patch", vpkPath,
			"--output", outputRoot,
			// 同批的包互为解析集合：材质包要能借到模型 VPK 的几何来算 matcap，模型包要能借到别处的材质。
			"--material-source", materialSource,
			// 默认产出"7 槽位标准版"：附加部件并进它主导的那个身躯槽位。
			"--components", "merge",
			// matcap 直接当底色：这样"只换 matcap"的包才是真正可换的 patch。
			"--matcap", "albedo",
			"--manager-root", _managerRoot,
			"--library", _libraryRoot,
			"--game-data", _gameDataDirectory,
			"--json",
		};

		var (exitCode, output, error) = await RunCliAsync(executable, arguments, cancellationToken).ConfigureAwait(false);
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
			return new ConversionResult(null, null, output, 0, 0, 0, notes);

		var units = 0;
		var materials = 0;
		var textures = 0;
		if (root.TryGetProperty("patches", out var patches) && patches.ValueKind == JsonValueKind.Array)
		{
			foreach (var patch in patches.EnumerateArray())
			{
				units += ReadInt(patch, "units");
				materials += ReadInt(patch, "materials");
				textures += ReadInt(patch, "textures");
				foreach (var warning in ReadStrings(patch, "warnings"))
					notes.Add(warning);
			}
		}

		return new ConversionResult(patchDirectory, null, output, units, materials, textures, notes);
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

	private static IEnumerable<string> ReadStrings(JsonElement element, string name)
	{
		if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
			yield break;
		foreach (var item in value.EnumerateArray())
		{
			var text = item.GetString();
			if (!string.IsNullOrWhiteSpace(text)) yield return text;
		}
	}

	private static async Task<(int ExitCode, string Output, string Error)> RunCliAsync(
		string executable,
		IReadOnlyList<string> arguments,
		CancellationToken cancellationToken)
	{
		using var process = new Process
		{
			StartInfo = new ProcessStartInfo
			{
				FileName = executable,
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				// 结论里全是中文。CLI 自己把 Console 设成 UTF-8（Program.cs），这里必须对齐。
				StandardOutputEncoding = Encoding.UTF8,
				StandardErrorEncoding = Encoding.UTF8,
				WorkingDirectory = Path.GetDirectoryName(executable)!,
			}
		};
		foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);

		if (!process.Start()) throw new InvalidOperationException("无法启动 hd2mod.exe。");
		var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
		var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
		using var registration = cancellationToken.Register(() =>
		{
			try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
		});
		await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
		return (process.ExitCode, await outputTask.ConfigureAwait(false), await errorTask.ConfigureAwait(false));
	}

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

	private string CreateStaging()
	{
		var staging = Path.Combine(Path.GetTempPath(), "hd2modmanager-vpk2patch", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(staging);
		return staging;
	}

	public static void DeleteQuietly(string directory)
	{
		try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}

	private static string SafeDirectoryName(string name)
	{
		var sanitized = string.IsNullOrWhiteSpace(name) ? "VPK" : name.Trim();
		foreach (var ch in Path.GetInvalidFileNameChars()) sanitized = sanitized.Replace(ch, '_');
		return string.IsNullOrWhiteSpace(sanitized) ? "VPK" : sanitized;
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
