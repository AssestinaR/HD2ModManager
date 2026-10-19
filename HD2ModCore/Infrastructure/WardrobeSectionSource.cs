using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>解析结果：找到的 unit 表、没找到的 unit、以及实际扫过的 patch。</summary>
public sealed record WardrobeSectionResolution(
	IReadOnlyList<WardrobeSectionMapUnit> Units,
	IReadOnlyList<string> MissingUnitIds,
	IReadOnlyList<string> ScannedPatches);

/// <summary>
/// 编辑器用的 section 数据来源：**从当前部署的 patch 现算**。
///
/// 为什么不用 `<mod目录>/sections.json`：编辑器要显示的是"游戏现在实际加载的那份几何"，
/// 而 `sections.json` 是某个 mod 构建时的快照，可能早就和部署内容对不上。
/// 现算出来的东西和运行时、和玩家眼睛看到的必然一致。
/// `sections.json` 仍然是 CLI / 投影的输入（那边要的是"某次构建"，可以离线、跨 mod）。
///
/// 部署目录里的 patch 名形如 `<archive>.patch_N`，旁边的 `.gpu_resources` / `.stream`
/// 是附属载荷，不是独立 patch——漏掉这层过滤会把附属文件当成 patch 解析，直接报错。
///
/// The editor resolves sections from the patches that are actually deployed, not from a
/// mod's build-time sections.json: what the user is looking at must be what the game loads.
/// sections.json stays the CLI/projection input, where "a specific build" is the right unit.
/// </summary>
public static class WardrobeSectionSource
{
	/// <summary>列出部署目录里的 patch 文件（不含 `.gpu_resources` / `.stream` 附属载荷）。</summary>
	public static IReadOnlyList<string> EnumerateDeployedPatches(string gameDataDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(gameDataDirectory);
		var root = Path.GetFullPath(gameDataDirectory);
		if (!Directory.Exists(root))
			return Array.Empty<string>();
		return Directory
			.EnumerateFiles(root, "*.patch_*", SearchOption.TopDirectoryOnly)
			.Where(path => !path.EndsWith(".gpu_resources", StringComparison.OrdinalIgnoreCase)
				&& !path.EndsWith(".stream", StringComparison.OrdinalIgnoreCase))
			// patch_10 要排在 patch_2 之后：按数字后缀排，不按字符串。
			.OrderBy(PatchOrdinal)
			.ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	private static int PatchOrdinal(string path)
	{
		var index = path.LastIndexOf(".patch_", StringComparison.OrdinalIgnoreCase);
		return index >= 0
			&& int.TryParse(path[(index + ".patch_".Length)..], out var ordinal)
			? ordinal
			: int.MaxValue;
	}

	/// <summary>
	/// 在部署的 patch 里找这些 unit 的 section 表。全部找齐就提前停——
	/// 一个护甲通常只涉及一两个 patch，没必要把整个部署目录走完。
	/// </summary>
	public static async ValueTask<WardrobeSectionResolution> ResolveAsync(
		string gameDataDirectory,
		IReadOnlyCollection<string> unitIds,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(unitIds);
		var wanted = new HashSet<string>(StringComparer.Ordinal);
		foreach (var unitId in unitIds)
		{
			if (WardrobeSectionFingerprint.TryNormalizeUnit(unitId, out var normalized))
				wanted.Add(normalized);
		}

		var found = new Dictionary<string, WardrobeSectionMapUnit>(StringComparer.Ordinal);
		var scanned = new List<string>();
		if (wanted.Count > 0)
		{
			foreach (var patch in EnumerateDeployedPatches(gameDataDirectory))
			{
				cancellationToken.ThrowIfCancellationRequested();
				scanned.Add(patch);
				WardrobeSectionMapBuildResult build;
				try
				{
					build = await WardrobeSectionMapFile.BuildAsync(patch, cancellationToken).ConfigureAwait(false);
				}
				catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
				{
					// 一个 patch 读不了不该让整次解析失败：继续下一个，最后由"缺了哪些 unit"说话。
					continue;
				}

				foreach (var unit in build.Document.Units)
				{
					if (!wanted.Contains(unit.FileId) || found.ContainsKey(unit.FileId))
						continue;
					found.Add(unit.FileId, unit);
				}

				if (found.Count == wanted.Count)
					break;
			}
		}

		var missing = wanted.Where(id => !found.ContainsKey(id)).Order(StringComparer.Ordinal).ToArray();
		return new WardrobeSectionResolution(
			found.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value).ToArray(),
			missing,
			scanned);
	}
}
