using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 一个资产键的极简投影。<c>TypeId</c> 决定它是 unit / 材质 / 贴图，<c>FileId</c> 是 id。
/// <para>
/// 刻意不用 <c>HD2ModAdaptation.AssetKey</c>：规划规则是纯逻辑，不该被"哪个程序集定义了 key"
/// 绑住，否则单测要拖进整个 patch 读写栈。
/// </para>
/// </summary>
public readonly record struct SectionalAssetRef(ulong TypeId, ulong FileId);

/// <summary>规划里的一次"从哪个来源 patch 取哪个 key"。</summary>
/// <param name="SourcePatch">Mod 根上的 patch 文件名（不含目录）。</param>
public sealed record SectionalPlannedSelection(string SourcePatch, SectionalAssetRef Key);

/// <summary>规划要产出的一个 patch 组。</summary>
/// <param name="Role">unit 还是材质。</param>
/// <param name="OptionName">材质选项名；unit 组为 <c>null</c>。</param>
/// <param name="SourceName">这一组来自哪个来源条目；内置材质组为 <c>null</c>。</param>
public sealed record SectionalPlannedPatch(
	SectionalPatchRole Role,
	string? OptionName,
	string? SourceName,
	IReadOnlyList<SectionalPlannedSelection> Selections);

/// <summary>一次 sectional 转换的产出计划：哪些 patch、各装哪些 key、按什么顺序。</summary>
public sealed record SectionalConversionPlan(
	IReadOnlyList<SectionalPlannedPatch> Patches,
	IReadOnlyList<string> Warnings)
{
	/// <summary>第 0 组必须是 unit（编译段按 <c>patch_0</c> 找它）。</summary>
	public SectionalPlannedPatch? Unit => Patches.FirstOrDefault(patch => patch.Role == SectionalPatchRole.Unit);
}

/// <summary>
/// 「来源 → sectional」的**规划**部分：只看"来源里有那些 patch、每个 patch 里有哪些 key"，
/// 决定产出哪几组、每组装什么。纯函数，可离线测。
///
/// <para><b>规则（按用户口径）：</b></para>
/// <list type="number">
/// <item><b>unit 组</b>：白名单里的 unit key，来自任何来源；同名 key 以后面的来源为准（覆盖顺序）。</item>
/// <item><b>每个来源一条材质组</b>：该来源的 patch 里出现过的、在白名单内的材质与贴图。
/// 组名 = 来源条目名（patch 本身没有名字，这是唯一能区分它们的地方）。</item>
/// <item><b>内置材质组</b>：**贡献了 unit 的那些 patch 里**出现的、在白名单内的材质与贴图，
/// 组名固定 <see cref="SectionalPatchManifest.BuiltInMaterialOptionName"/>。</item>
/// </list>
/// <para>
/// <b>第 2 与第 3 组不互斥。</b>同一条材质允许同时出现在「内置材质」与某个来源组里，
/// 在 <c>sectional/</c> 中是两份不同的 patch —— 不做"去重到一处"的优化，
/// 否则「内置材质」的含义会退化成"来源里没抽到的剩余"。
/// </para>
/// </summary>
public static class SectionalConversionPlanner
{
	public static SectionalConversionPlan Plan(
		SectionalSourceDocument document,
		IReadOnlyDictionary<string, SectionalPatchContents> patchesByFileName,
		IReadOnlyCollection<ulong> unitTypeIds,
		IReadOnlyCollection<ulong> materialTypeIds,
		IReadOnlyCollection<ulong> textureTypeIds)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(patchesByFileName);
		ArgumentNullException.ThrowIfNull(unitTypeIds);
		ArgumentNullException.ThrowIfNull(materialTypeIds);
		ArgumentNullException.ThrowIfNull(textureTypeIds);

		var warnings = new List<string>();
		var whitelist = BuildWhitelist(document.AssetKeys);
		var orderedSources = document.Sources.OrderBy(source => source.Order).ToArray();

		// ① unit 组：跨来源收集，同名 key 后来者覆盖（与部署的"同名后者赢"一致）。
		var unitSelections = new Dictionary<SectionalAssetRef, string>();
		foreach (var source in orderedSources)
		{
			foreach (var file in SourceFiles(source, patchesByFileName))
			{
				foreach (var key in patchesByFileName[file].Keys)
				{
					if (!unitTypeIds.Contains(key.TypeId)) continue;
					if (!whitelist.Contains(key)) continue;
					unitSelections[key] = file;
				}
			}
		}

		if (unitSelections.Count == 0)
			warnings.Add("来源里没有白名单内的 unit：产物只有材质层。");

		var unitSourceFiles = unitSelections.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);

		var patches = new List<SectionalPlannedPatch>(orderedSources.Length + 2);
		patches.Add(new SectionalPlannedPatch(
			SectionalPatchRole.Unit,
			OptionName: null,
			SourceName: null,
			unitSelections.Select(pair => new SectionalPlannedSelection(pair.Value, pair.Key)).ToArray()));

		// ② 内置材质组：贡献了 unit 的那些 patch 里带的材质与贴图。
		var builtIn = CollectMaterials(unitSourceFiles, patchesByFileName, whitelist, materialTypeIds, textureTypeIds);
		if (builtIn.Count != 0)
		{
			patches.Add(new SectionalPlannedPatch(
				SectionalPatchRole.Material,
				SectionalPatchManifest.BuiltInMaterialOptionName,
				SourceName: null,
				builtIn));
		}

		// ③ 每个来源一条材质组（按 order；空的跳过）。
		//
		// **贡献过 unit 的 patch 不参与这里**：它带的材质已经整份归到"模型内置材质"了。
		// 否则同一个文件里的同一批材质会同时出现在内置组和来源组里——那不是在表达"两处都有"，
		// 只是同一份内容被数了两遍。跨来源的重复（另一个来源也提供了同一条材质）仍然保留，
		// 那才是用户说的"可能同时在两处出现"。
		foreach (var source in orderedSources)
		{
			var materialFiles = SourceFiles(source, patchesByFileName)
				.Where(file => !unitSourceFiles.Contains(file));
			var selections = CollectMaterials(
				materialFiles,
				patchesByFileName,
				whitelist,
				materialTypeIds,
				textureTypeIds);
			if (selections.Count == 0) continue;   // 纯 unit 来源不产出空组
			patches.Add(new SectionalPlannedPatch(
				SectionalPatchRole.Material,
				OptionName: source.Name,
				SourceName: source.Name,
				selections));
		}

		return new SectionalConversionPlan(patches, warnings);
	}

	private static List<SectionalPlannedSelection> CollectMaterials(
		IEnumerable<string> files,
		IReadOnlyDictionary<string, SectionalPatchContents> patchesByFileName,
		IReadOnlySet<SectionalAssetRef> whitelist,
		IReadOnlyCollection<ulong> materialTypeIds,
		IReadOnlyCollection<ulong> textureTypeIds)
	{
		var selected = new Dictionary<SectionalAssetRef, string>();
		foreach (var file in files)
		{
			if (!patchesByFileName.TryGetValue(file, out var contents)) continue;
			foreach (var key in contents.Keys)
			{
				if (!materialTypeIds.Contains(key.TypeId) && !textureTypeIds.Contains(key.TypeId)) continue;
				if (!whitelist.Contains(key)) continue;
				selected[key] = file;
			}
		}

		return selected
			.OrderBy(pair => pair.Key.TypeId)
			.ThenBy(pair => pair.Key.FileId)
			.Select(pair => new SectionalPlannedSelection(pair.Value, pair.Key))
			.ToList();
	}

	/// <summary>来源在根上占哪几个 patch。没记 <c>PatchFiles</c> 的旧文件退化成"全部来源共用全部 patch"。</summary>
	private static IEnumerable<string> SourceFiles(
		SectionalSourceEntry source,
		IReadOnlyDictionary<string, SectionalPatchContents> patchesByFileName)
	{
		if (source.PatchFiles is { Count: > 0 })
			return source.PatchFiles.Where(patchesByFileName.ContainsKey);

		return patchesByFileName.Keys;
	}

	private static HashSet<SectionalAssetRef> BuildWhitelist(SectionalSourceAssetKeys keys)
	{
		var whitelist = new HashSet<SectionalAssetRef>();
		foreach (var raw in keys.Units.Concat(keys.Materials).Concat(keys.Textures))
		{
			if (!TryParseKey(raw, out var key)) continue;
			whitelist.Add(key);
		}

		return whitelist;
	}

	/// <summary>
	/// 白名单里的 key 用 <c>typeId:fileId</c> 的十六进制文本存（见依据文件），这里解析回来。
	/// 也接受只有 fileId 的写法（type 由它出现在哪个列表决定不了，所以只保留 fileId=0 的那种退化形式会失败）。
	/// </summary>
	public static bool TryParseKey(string? raw, out SectionalAssetRef key)
	{
		key = default;
		if (string.IsNullOrWhiteSpace(raw)) return false;
		var text = raw.Trim();
		var separator = text.IndexOf(':');
		if (separator <= 0 || separator == text.Length - 1) return false;
		return ulong.TryParse(text[..separator], System.Globalization.NumberStyles.HexNumber, null, out var typeId)
			&& ulong.TryParse(text[(separator + 1)..], System.Globalization.NumberStyles.HexNumber, null, out var fileId)
			&& (key = new SectionalAssetRef(typeId, fileId)) != default;
	}

	/// <summary>把 key 写成依据文件里的形式。</summary>
	public static string FormatKey(SectionalAssetRef key)
		=> $"{key.TypeId:x16}:{key.FileId:x16}";
}

/// <summary>一个来源 patch 里有哪些资产键（规划只关心"有没有"，不关心内容）。</summary>
public sealed record SectionalPatchContents(IReadOnlySet<SectionalAssetRef> Keys);
