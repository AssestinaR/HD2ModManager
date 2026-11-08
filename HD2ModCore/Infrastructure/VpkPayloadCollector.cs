using System.Text.RegularExpressions;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 把一次 <c>vpk2patch</c> 批转换的产出**收成一份平铺载荷**。
/// <para>
/// <b>为什么必须有这一步。</b><c>vpk2patch</c> 的输出根下是**每个输入 VPK 一个子目录**
/// （见 CLI：`outputRoot/&lt;清洗过的 vpk 名&gt;/`）。这与存储约定冲突：
/// <c>sectional/</c> 里必须**全部 patch 组平铺按序号**（unit 恒为 <c>patch_0</c>，
/// 材质 <c>patch_1..patch_N</c>），不建子目录。少了这一步，会出现两种都很糟的结果：
/// </para>
/// <list type="number">
/// <item><b>只取第一个子目录</b>（旧的重构建就是这么干的）：模型 patch 进来了，材质全丢，
/// 表现是"重构完成、游戏里材质没了"。</item>
/// <item><b>把整个输出根搬进 <c>sectional/</c></b>：得到一堆子目录，而下游所有枚举器
/// 都只扫顶层 —— 这些 patch 既不被识别也不被部署，表现是"部署成功、什么都没变"。</item>
/// </list>
/// <para>
/// <b>序号怎么定。</b>只看转换报告：模型那一份 <c>unitPatch</c> 恒为 <c>patch_0</c>，
/// 其余材质 patch（模型自带的在前，其它来源按报告顺序）依次排 <c>patch_1..patch_N</c>。
/// 每个 base patch 的 <c>.gpu_resources</c> / <c>.stream</c> 跟着它一起改名，绝不拆开——
/// TOC 里写的是 sidecar 里的偏移与长度，拆开就是"读出来是错的、但没有任何报错"。
/// </para>
/// </summary>
public static class VpkPayloadCollector
{
	/// <summary>一次收集的结果。</summary>
	/// <param name="Succeeded">是否收成功。</param>
	/// <param name="UnitPatches">收进来的 unit patch 个数（正常是 1）。</param>
	/// <param name="MaterialPatches">收进来的材质 patch 个数。</param>
	/// <param name="Error">失败原因。</param>
	/// <param name="Notes">值得告诉用户的细节（例如"某个材质 patch 没找到"）。</param>
	/// <param name="ReportJson">
	/// **改写过 patch 文件名的**转换报告（把每个来源 VPK 各自目录里的 `patch_0` 换成平铺后的编号）。
	/// 调用方要用它去写 `vpk2patch.json`：`patches.json` 里"哪个 patch 是 unit、材质叫什么名字"
	/// 全部由报告决定，报告不跟着改，材质选项就会退化成「材质 1 / 2 / 3」。
	/// </param>
	public sealed record Result(
		bool Succeeded,
		int UnitPatches,
		int MaterialPatches,
		string? Error,
		IReadOnlyList<string> Notes,
		string? ReportJson = null)
	{
		public static Result Failure(string error, IReadOnlyList<string>? notes = null)
			=> new(false, 0, 0, error, notes ?? Array.Empty<string>());
	}

	/// <summary>patch 与 sidecar 的命名约定（<c>&lt;16 位 hex&gt;.patch_&lt;N&gt;[.gpu_resources|.stream]</c>）。</summary>
	private static readonly Regex PatchFileName = new(
		"^(?<hex>[0-9a-fA-F]{16})\\.patch_(?<index>\\d+)$",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly string[] SidecarSuffixes = [".gpu_resources", ".stream"];

	/// <summary>子目录里"不是 patch 也不是 sidecar"的文件：结构文件（sections.json 等）。</summary>
	private static readonly string[] StructuralFileNames =
	[
		WardrobeSectionMapFile.FileName,   // sections.json
		SectionGroupFile.FileName,         // components.json
		VpkCustomModelFile.FileName,       // custom-model.json
		"physbone-rig.bin",
	];

	/// <summary>
	/// 把 <paramref name="convertRoot"/> 下所有产出子目录里的 patch 收进
	/// <paramref name="targetPayloadDirectory"/>，按报告决定序号与角色。
	/// </summary>
	/// <param name="convertRoot"><c>vpk2patch --output</c> 指向的目录（含每个 VPK 一个子目录）。</param>
	/// <param name="targetPayloadDirectory">目标载荷目录（<c>sectional/</c>）；会被创建，但不会被清空。</param>
	/// <param name="report">这次转换的报告（决定哪一份是 unit、哪些是材质）。</param>
	/// <param name="modelVpkStem">模型 VPK 的文件名（不含扩展名）：用来认出哪个子目录是模型。</param>
	public static Result Collect(
		string convertRoot,
		string targetPayloadDirectory,
		VpkConversionReport? report,
		string? modelVpkStem = null,
		string? rawReportJson = null,
		IReadOnlyList<string>? sourceVpkPaths = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(convertRoot);
		ArgumentException.ThrowIfNullOrWhiteSpace(targetPayloadDirectory);

		if (!Directory.Exists(convertRoot))
			return Result.Failure($"转换输出目录不存在：{convertRoot}");

		var produced = EnumerateProducedDirectories(convertRoot);
		if (produced.Count == 0)
			return Result.Failure($"这次转换没有产出任何 patch（输出目录：{convertRoot}）。");

		Directory.CreateDirectory(targetPayloadDirectory);
		var notes = new List<string>();

		// ① 按报告排定顺序：unit 先，材质按报告里的出现次序（模型自带的天然靠前）。
		//
		// ⚠️ **同一个输出根下，每个子目录里的 patch 文件名是一样的**（都叫 `<archive>.patch_0`），
		// 所以只能"先按报告里的来源 VPK 名定位到子目录，再在那个目录里找 patch"。
		// 直接按文件名全局找会永远命中第一个目录，三份 patch 全指向同一份——静默塌成一份。
		var ordered = new List<(string Path, bool IsUnit)>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (report is not null)
		{
			foreach (var patch in report.Patches)
			{
				if (string.IsNullOrWhiteSpace(patch.UnitPatch)) continue;
				if (!TryFindInSourceDirectory(produced, patch.Name, patch.UnitPatch!, out var unitPath)) continue;
				if (!seen.Add(unitPath)) continue;
				ordered.Add((unitPath, true));
			}

			foreach (var patch in report.Patches)
			{
				if (string.IsNullOrWhiteSpace(patch.MaterialPatch)) continue;
				if (!TryFindInSourceDirectory(produced, patch.Name, patch.MaterialPatch!, out var materialPath)) continue;
				if (!seen.Add(materialPath)) continue;
				ordered.Add((materialPath, false));
			}
		}

		// ② 报告缺失/对不上时的兜底：模型目录的 patch_0 当 unit，其余按目录名与序号排。
		var modelDirectory = FindModelDirectory(produced, modelVpkStem);
		foreach (var (directory, patches) in produced)
		{
			foreach (var patch in patches)
			{
				if (seen.Contains(patch.Path)) continue;
				var isUnit = string.Equals(directory, modelDirectory, StringComparison.OrdinalIgnoreCase)
					&& patch.Index == 0
					&& ordered.All(item => !item.IsUnit);
				ordered.Add((patch.Path, isUnit));
				seen.Add(patch.Path);
			}
		}

		if (ordered.Count == 0)
			return Result.Failure($"转换产出的子目录里没有可用的 patch（输出目录：{convertRoot}）。");

		// ③ 逐个装进目标目录：base 与 sidecar 一起改名，序号连续（unit = 0，材质 1..N）。
		var units = 0;
		var materials = 0;
		var materialIndex = 1;
		// 「来源子目录 | 原来的 patch 文件名」→ 平铺后的文件名。改写报告要用它。
		var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var (path, isUnitLayer) in ordered)
		{
			var name = Path.GetFileName(path);
			var match = PatchFileName.Match(name);
			if (!match.Success)
			{
				notes.Add($"跳过了认不出命名的文件：{name}");
				continue;
			}

			var isUnit = isUnitLayer && units == 0;
			if (!isUnit) materials++;
			else units++;

			var targetIndex = isUnit ? 0 : materialIndex++;
			if (isUnitLayer && !isUnit)
				notes.Add($"{name}：报告里也标成 unit，但已经有 unit patch 了，按材质排到 patch_{targetIndex}。");

			var archive = match.Groups["hex"].Value.ToLowerInvariant();
			var targetFileName = $"{archive}.patch_{targetIndex}";
			CopyBaseAndSidecars(path, Path.Combine(targetPayloadDirectory, targetFileName));
			mappings[$"{Path.GetFileName(Path.GetDirectoryName(path)!)}|{name}"] = targetFileName;
		}

		// ④ 结构文件只从模型目录取一份：它们是"这具素体是谁""哪些 section 算开关"，不属于某个 patch。
		if (modelDirectory is not null)
		{
			foreach (var fileName in StructuralFileNames)
			{
				var source = Path.Combine(modelDirectory, fileName);
				if (!File.Exists(source)) continue;
				VpkPayloadSwap.ClearReadOnly(Path.Combine(targetPayloadDirectory, fileName));
				File.Copy(source, Path.Combine(targetPayloadDirectory, fileName), overwrite: true);
			}
		}

		if (units == 0)
			return new Result(false, 0, materials,
				"这次转换没有产出 unit patch：模型主体没有被转出来。", notes);

		return new Result(true, units, materials, null, notes,
			RewriteReport(rawReportJson, produced, mappings, sourceVpkPaths));
	}

	/// <summary>
	/// 把报告里每个来源 VPK 的 patch 文件名改成**平铺之后**的编号。
	/// <para>
	/// 报告里记的是"它自己那个子目录里"的文件名（都叫 <c>&lt;archive&gt;.patch_0</c>）。
	/// 收成平铺载荷之后这些名字全变了，而 <c>patches.json</c>（材质选项的名字与角色）
	/// 完全靠报告来判定：报告不改，合并进来的材质在清单里就没有名字，界面退化成「材质 1 / 2 / 3」。
	/// </para>
	/// </summary>
	private static string? RewriteReport(
		string? rawReportJson,
		IReadOnlyList<(string Directory, List<(string Path, int Index)> Patches)> produced,
		IReadOnlyDictionary<string, string> mappings,
		IReadOnlyList<string>? sourceVpkPaths)
	{
		if (string.IsNullOrWhiteSpace(rawReportJson)) return rawReportJson;
		try
		{
			if (System.Text.Json.Nodes.JsonNode.Parse(rawReportJson) is not System.Text.Json.Nodes.JsonObject root)
				return rawReportJson;
			if (root["patches"] is not System.Text.Json.Nodes.JsonArray patches) return rawReportJson;

			foreach (var item in patches)
			{
				if (item is not System.Text.Json.Nodes.JsonObject entry) continue;
				var vpkName = entry["name"]?.GetValue<string>();
				var directory = ResolveProducedDirectory(produced, vpkName);
				if (directory is null) continue;
				var directoryKey = Path.GetFileName(directory);

				// 报告里的 `name` 常被 CLI 洗成小写；材质选项名直接用它会变成
				// "elise基础材质a1主体"。原始文件名有大小写，按目录对回去。
				var originalName = FindOriginalName(directoryKey, sourceVpkPaths);
				if (originalName is not null)
					entry["name"] = originalName;

				foreach (var field in new[] { "unitPatch", "materialPatch" })
				{
					if (entry[field] is not System.Text.Json.Nodes.JsonValue value) continue;
					var fileName = value.GetValue<string>();
					if (string.IsNullOrWhiteSpace(fileName)) continue;
					if (!mappings.TryGetValue(directoryKey + "|" + fileName, out var rewritten)) continue;
					entry[field] = rewritten;
				}
			}

			return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
		}
		catch (System.Text.Json.JsonException)
		{
			// 不是合法 JSON：原样交回去，让后面的步骤按它们自己的方式报错。
			return rawReportJson;
		}

	}

	/// <summary>在来源 VPK 列表里按"清洗后的名字"找回原始文件名（大小写以磁盘为准）。</summary>
	private static string? FindOriginalName(string? directoryKey, IReadOnlyList<string>? sourceVpkPaths)
	{
		if (sourceVpkPaths is null || string.IsNullOrWhiteSpace(directoryKey)) return null;
		foreach (var path in sourceVpkPaths)
		{
			var stem = Path.GetFileNameWithoutExtension(path);
			if (!string.Equals(SafeName(stem), directoryKey, StringComparison.OrdinalIgnoreCase)) continue;
			return Path.GetFileName(path);
		}

		return null;
	}

	/// <summary>按来源 VPK 名认出它的产出子目录（名字会被 CLI 清洗，大小写也不保证）。</summary>
	private static string? ResolveProducedDirectory(
		IReadOnlyList<(string Directory, List<(string Path, int Index)> Patches)> produced,
		string? vpkFileName)
	{
		if (string.IsNullOrWhiteSpace(vpkFileName)) return null;
		var wanted = SafeName(Path.GetFileNameWithoutExtension(vpkFileName));
		foreach (var (directory, _) in produced)
		{
			if (string.Equals(Path.GetFileName(directory), wanted, StringComparison.OrdinalIgnoreCase))
				return directory;
		}

		return null;
	}

	private static void CopyBaseAndSidecars(string sourceBase, string targetBase)
	{
		VpkPayloadSwap.ClearReadOnly(targetBase);
		File.Copy(sourceBase, targetBase, overwrite: true);
		foreach (var suffix in SidecarSuffixes)
		{
			var sourceSidecar = sourceBase + suffix;
			if (!File.Exists(sourceSidecar)) continue;
			var targetSidecar = targetBase + suffix;
			VpkPayloadSwap.ClearReadOnly(targetSidecar);
			File.Copy(sourceSidecar, targetSidecar, overwrite: true);
		}
	}

	/// <summary>输出根下所有"含 patch 文件"的子目录，以及它们各自的 patch 列表。</summary>
	private static List<(string Directory, List<(string Path, int Index)> Patches)> EnumerateProducedDirectories(string convertRoot)
	{
		var list = new List<(string, List<(string, int)>)>();
		foreach (var directory in Directory.EnumerateDirectories(convertRoot))
		{
			var patches = new List<(string Path, int Index)>();
			foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
			{
				var match = PatchFileName.Match(Path.GetFileName(path));
				if (!match.Success) continue;
				if (!int.TryParse(match.Groups["index"].Value, out var index)) continue;
				patches.Add((path, index));
			}

			if (patches.Count != 0)
				list.Add((directory, patches.OrderBy(item => item.Index).ToList()));
		}

		// 目录名排序，让"报告缺失 + 没有模型提示"时的结果至少是稳定的。
		return list.OrderBy(item => item.Item1, StringComparer.OrdinalIgnoreCase).ToList();
	}

	/// <summary>模型目录：优先按 VPK 名认，其次认"有 custom-model.json / sections.json"的那个。</summary>
	private static string? FindModelDirectory(
		IReadOnlyList<(string Directory, List<(string Path, int Index)> Patches)> produced,
		string? modelVpkStem)
	{
		if (!string.IsNullOrWhiteSpace(modelVpkStem))
		{
			var wanted = modelVpkStem.Trim();
			foreach (var (directory, _) in produced)
			{
				var name = Path.GetFileName(directory);
				if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase)
					|| string.Equals(name, SafeName(wanted), StringComparison.OrdinalIgnoreCase))
					return directory;
			}
		}

		foreach (var (directory, _) in produced)
		{
			if (File.Exists(Path.Combine(directory, VpkCustomModelFile.FileName))
				|| File.Exists(Path.Combine(directory, WardrobeSectionMapFile.FileName)))
				return directory;
		}

		return produced.Count == 1 ? produced[0].Directory : null;
	}

	/// <summary>
	/// 按报告里的**来源 VPK 名**定位子目录，再在那个目录里找 patch。
	/// <para>
	/// CLI 给每个输入 VPK 建一个以包名命名的子目录（名字会被清洗、常见是转小写），
	/// 所以这里用"清洗后的名字 + 忽略大小写"比对；对不上就返回 false，让调用方走兜底顺序。
	/// </para>
	/// </summary>
	private static bool TryFindInSourceDirectory(
		IReadOnlyList<(string Directory, List<(string Path, int Index)> Patches)> produced,
		string? vpkFileName,
		string patchFileName,
		out string found)
	{
		found = string.Empty;
		var directory = ResolveProducedDirectory(produced, vpkFileName);
		if (directory is null) return false;
		foreach (var (candidate, patches) in produced)
		{
			if (!string.Equals(candidate, directory, StringComparison.OrdinalIgnoreCase)) continue;
			foreach (var (path, _) in patches)
			{
				if (!string.Equals(Path.GetFileName(path), patchFileName, StringComparison.OrdinalIgnoreCase)) continue;
				found = path;
				return true;
			}
		}

		return false;
	}

	/// <summary>CLI 建输出子目录时用的清洗规则的最小镜像（只用于"名字对上"，不用于写盘）。</summary>
	private static string SafeName(string name)
	{
		var invalid = Path.GetInvalidFileNameChars();
		var builder = new System.Text.StringBuilder(name.Length);
		foreach (var ch in name)
			builder.Append(invalid.Contains(ch) ? '_' : ch);
		return builder.ToString();
	}
}
