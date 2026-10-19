using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Domain;
using AdaptationScanner = HD2ModAdaptation.PatchReconstruction.PatchTocScanner;

namespace HD2ModCore.Infrastructure;

/// <summary>解析结果：找到的 unit 表、没找到的 unit、以及实际扫过的 patch。</summary>
public sealed record WardrobeSectionResolution(
	IReadOnlyList<WardrobeSectionMapUnit> Units,
	IReadOnlyList<string> MissingUnitIds,
	IReadOnlyList<string> ScannedPatches,
	int GameDataUnitCount = 0);

/// <summary>
/// 编辑器用的 section 数据来源：**只读这件护甲真正需要的 unit**。
///
/// 之前是"整份 patch 建一遍 section 映射表再挑"，23 个 unit 全解一遍要两秒，
/// 而编辑器往往只要其中一两个。现在只解命中的条目——同一次打开从秒级降到几十毫秒。
///
/// 两级来源，按"谁更可能是当前生效的那份"排序：
/// 1. **部署的 patch**：玩家实际加载的覆盖内容，必须优先；
/// 2. **原版 archive**：没有来源 mod（或 mod 只覆盖了部分 unit）时，
///    剩下的 unit 从游戏数据包里按 unit 类型直接读。没有这一级，
///    一件原版护甲打开编辑器就是空树——而原版护甲恰恰是最该能编辑的。
///
/// 两级都只读 toc + gpu 两段载荷，交给同一个 <c>UnitMeshReader</c> 解析，
/// 再交给 <see cref="WardrobeSectionMapFile.BuildUnit"/> 换算——和 CLI 产出
/// <c>sections.json</c> 走的是同一套规则，不存在"编辑器算的和 CLI 算的不一样"。
///
/// Reads only the units this armour actually needs. Building a whole patch's section map
/// took seconds while the editor usually needs one or two units. Two sources, ordered by
/// "which one is actually in effect": deployed patches first, then vanilla archives — without
/// the second tier a vanilla armour would open an empty tree, and vanilla armours are exactly
/// the ones most worth editing. Both tiers hand off to the same reader and the same
/// BuildUnit, so the editor and the CLI can never disagree.
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
	/// 找这些 unit 的 section 表：先部署的 patch，再原版 archive。全部找齐就提前停。
	/// </summary>
	public static async ValueTask<WardrobeSectionResolution> ResolveAsync(
		string gameDataDirectory,
		IReadOnlyCollection<string> unitIds,
		string? archiveHint = null,
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
		var gameDataUnits = 0;
		if (wanted.Count > 0)
		{
			await ReadFromDeployedPatchesAsync(gameDataDirectory, wanted, found, scanned, cancellationToken).ConfigureAwait(false);
			var remaining = wanted.Where(id => !found.ContainsKey(id)).ToHashSet(StringComparer.Ordinal);
			if (remaining.Count > 0)
				gameDataUnits = await ReadFromGameDataAsync(gameDataDirectory, remaining, found, archiveHint, cancellationToken).ConfigureAwait(false);
		}

		var missing = wanted.Where(id => !found.ContainsKey(id)).Order(StringComparer.Ordinal).ToArray();
		return new WardrobeSectionResolution(
			found.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value).ToArray(),
			missing,
			scanned,
			gameDataUnits);
	}

	private static async ValueTask ReadFromDeployedPatchesAsync(
		string gameDataDirectory,
		HashSet<string> wanted,
		Dictionary<string, WardrobeSectionMapUnit> found,
		List<string> scanned,
		CancellationToken cancellationToken)
	{
		var scanner = new AdaptationScanner();
		var reader = new PatchUnitMeshReader();
		foreach (var patch in EnumerateDeployedPatches(gameDataDirectory))
		{
			cancellationToken.ThrowIfCancellationRequested();
			scanned.Add(patch);
			IReadOnlyList<HD2ModAdaptation.PatchReconstruction.PatchTocEntry> entries;
			try
			{
				entries = await scanner.ScanEntriesAsync(patch, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
			{
				// 一个 patch 读不了不该让整次解析失败：继续下一个，最后由"缺了哪些 unit"说话。
				continue;
			}

			foreach (var entry in entries)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (entry.AssetKey.TypeId != PatchUnitMeshReader.UnitTypeId)
					continue;
				var fileId = entry.AssetKey.FileId.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
				if (!wanted.Contains(fileId) || found.ContainsKey(fileId))
					continue;
				try
				{
					// 只解命中的这一条：整份 patch 全解一遍是之前慢的原因。
					var unit = await reader.ReadAsync(entry, entries, PatchUnitDependencyPolicy.RequirePatchLocalComposite, cancellationToken)
						.ConfigureAwait(false);
					var built = WardrobeSectionMapFile.BuildUnit(fileId, unit.Model);
					if (built is not null)
						found.Add(fileId, built);
				}
				catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or ArgumentException or OverflowException)
				{
					// 这一条坏了就交给原版那一级再试一次。
				}
			}

			if (wanted.All(id => found.ContainsKey(id)))
				return;
		}
	}

	/// <summary>
	/// 从游戏数据包里按 unit 类型直接读。这是"没有来源 mod 也要能编辑"的那一半：
	/// 原版护甲的 unit 只存在于游戏自己的 archive 里，没有它就只能打开一棵空树。
	/// </summary>
	private static async ValueTask<int> ReadFromGameDataAsync(
		string gameDataDirectory,
		HashSet<string> remaining,
		Dictionary<string, WardrobeSectionMapUnit> found,
		string? archiveHint,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(gameDataDirectory) || !Directory.Exists(gameDataDirectory))
			return 0;
		// 先只扫这件护甲自己所属的 archive：整库枚举要几秒，而护甲的 unit 基本都在
		// 它自己的包里。提示没命中再退回全量枚举——宁可慢一次，也不能少东西。
		var total = 0;
		if (!string.IsNullOrWhiteSpace(archiveHint))
		{
			total += await ReadFromArchiveAsync(gameDataDirectory, remaining, found, archiveHint, cancellationToken).ConfigureAwait(false);
			if (remaining.All(id => found.ContainsKey(id)))
				return total;
		}

		total += await ReadFromArchiveAsync(gameDataDirectory, remaining, found, archiveId: null, cancellationToken).ConfigureAwait(false);
		return total;
	}

	private static async ValueTask<int> ReadFromArchiveAsync(
		string gameDataDirectory,
		HashSet<string> remaining,
		Dictionary<string, WardrobeSectionMapUnit> found,
		string? archiveId,
		CancellationToken cancellationToken)
	{
		try
		{
			var catalog = CoreServices.CreateGameDataAssetCatalogService(gameDataDirectory);
			var scan = await catalog
				.EnumerateAsync([PatchUnitMeshReader.UnitTypeId], archiveId, cancellationToken)
				.ConfigureAwait(false);
			var reader = new UnitMeshReader();
			var count = 0;
			foreach (var asset in scan.Assets)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var fileId = asset.FileId.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
				if (!remaining.Contains(fileId) || found.ContainsKey(fileId))
					continue;
				var toc = await catalog.ReadPartAsync(asset, GameDataAssetPart.TocData, cancellationToken).ConfigureAwait(false);
				if (toc is null || toc.Length == 0)
					continue;
				var gpu = await catalog.ReadPartAsync(asset, GameDataAssetPart.GpuResources, cancellationToken).ConfigureAwait(false);
				try
				{
					var model = reader.Read(toc, gpu ?? Array.Empty<byte>());
					var built = WardrobeSectionMapFile.BuildUnit(fileId, model);
					if (built is null)
						continue;
					found.Add(fileId, built);
					count++;
				}
				catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or ArgumentException or OverflowException)
				{
					// 原版里也有 composite-backed unit；这一级先不支持，交给"缺了哪些 unit"报出来。
				}
			}

			return count;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException)
		{
			return 0;
		}
	}
}
