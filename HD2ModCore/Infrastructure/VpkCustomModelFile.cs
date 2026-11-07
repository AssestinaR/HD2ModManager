using System.Text.Json;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// Mod 目录里的 <c>custom-model.json</c>：VPK 转换登记"这套 Unit 是一具素体"的地方。
/// <para>
/// <b>为什么放在 Core 而不是管理器。</b>它回答的问题与界面无关：
/// "这个素体是谁的"。两个地方都要用它——详情页据此把默认目标设成"自己的素体"，
/// 护甲映射页据此拒绝单独删除它。放进 Core 才能离线测，也避免两边各写一份读法。
/// </para>
/// </summary>
public static class VpkCustomModelFile
{
	/// <summary>文件名（写在 Mod 目录里，实测位于 <c>vpk2patch/</c> 下；扫描是递归的）。</summary>
	public const string FileName = "custom-model.json";

	/// <summary>
	/// 读一个 Mod 目录登记的素体 key；没有或读不懂返回 <c>null</c>。
	/// <para>
	/// <b>两处都要看。</b>转换产物把这份文件写在 <c>vpk2patch/</c> 下（内容层），
	/// 手写的或早期的写在根上。只认根目录的话，VPK 类 Mod 会读不到自己的素体 ——
	/// 表现是详情页的"默认目标"选不中自己的素体、重构建拿不到 key。
	/// </para>
	/// </summary>
	public static string? TryReadKey(string modDirectory)
	{
		if (string.IsNullOrWhiteSpace(modDirectory))
			return null;
		return ReadKeyAt(Path.Combine(modDirectory, FileName))
			?? ReadKeyAt(Path.Combine(modDirectory, ModNodePayloadLayout.VpkPatchDirectoryName, FileName))
			// 老库里它在旧名目录下。漏了这一条，全库素体都会被判成孤儿，护甲映射页会把记录删掉。
			?? ReadKeyAt(Path.Combine(modDirectory, ModNodePayloadLayout.LegacyVpkPatchDirectoryName, FileName));
	}

	private static string? ReadKeyAt(string path)
	{
		if (!File.Exists(path))
			return null;
		try
		{
			using var stream = File.OpenRead(path);
			using var document = JsonDocument.Parse(stream);
			return document.RootElement.TryGetProperty("key", out var key) ? key.GetString() : null;
		}
		catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	/// <summary>
	/// 把一个素体改名写进<b>持有它的那份</b> <c>custom-model.json</c>。找不到持有者返回 false。
	/// <para>
	/// <b>为什么不能只改注册表。</b>注册表里那份是投影，而 <c>custom-model.json</c> 才是
	/// "这具素体属于哪个 Mod"的凭证——重构建就是拿它当输入的（<c>--reuse-model</c>），
	/// 产物重新登记时又会用那里的名字。只改注册表的话，下一次重构建就把名字盖回 VPK 文件名，
	/// 用户看到的是"改名过一会又被还原"。
	/// </para>
	/// <para>读取失败 / 文件损坏时不动它（返回 false）：宁可这次没改名，也不要把文件写坏。</para>
	/// </summary>
	public static bool TryRenameOwned(string modsDirectory, string modelKey, string name)
	{
		if (string.IsNullOrWhiteSpace(modsDirectory) || string.IsNullOrWhiteSpace(modelKey))
			return false;
		if (string.IsNullOrWhiteSpace(name))
			return false;
		var path = TryFindOwnerFile(modsDirectory, modelKey);
		if (path is null)
			return false;
		try
		{
			var json = File.ReadAllText(path);
			var model = JsonSerializer.Deserialize<RuntimeArmorRegistryModel>(json, JsonOptions);
			if (model is null)
				return false;
			if (string.Equals(model.Name, name, StringComparison.Ordinal))
				return true;
			var renamed = model with { Name = name.Trim() };
			// 库里的产物是只读的：不清属性就写不进去，异常被下面的 catch 吞掉，
			// 调用方只能看到 false——于是"改名失败"看起来像"找不到这个素体"。
			VpkPayloadSwap.ClearReadOnlyForWrite(path);
			File.WriteAllText(path, JsonSerializer.Serialize(renamed, JsonOptions));
			return true;
		}
		catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	/// <summary>按 key 找到持有它的那份 <c>custom-model.json</c> 的完整路径。</summary>
	public static string? TryFindOwnerFile(string modsDirectory, string modelKey)
	{
		if (string.IsNullOrWhiteSpace(modsDirectory) || !Directory.Exists(modsDirectory))
			return null;
		IEnumerable<string> files;
		try
		{
			files = Directory.EnumerateFiles(modsDirectory, FileName, SearchOption.AllDirectories);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return null;
		}

		foreach (var file in files)
		{
			if (string.Equals(TryReadKey(Path.GetDirectoryName(file)!), modelKey, StringComparison.Ordinal))
				return file;
		}

		return null;
	}

	/// <summary>与转换器写这份文件时用的同一套选项（Web 默认 = camelCase）。</summary>
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = true,
	};

	/// <summary>
	/// 整个 Mod 库里被 VPK 模组"持有"的素体：key → <b>持有它的那个 Mod 的名字</b>。
	/// <para>
	/// <b>为什么要扫库而不是查注册表。</b>注册表里没有"这个素体属于哪个 Mod"这一维，
	/// 而 <c>custom-model.json</c> 就在 Mod 目录里——那是唯一不会漂移的事实来源。
	/// </para>
	/// <para>
	/// <b>为什么要把名字带出来。</b>拒绝删除时只说"这是 VPK 转换出来的"没有可操作性：
	/// 用户手上有几十个 Mod，他不知道该移除哪一个。带上名字，提示才能变成一句能照做的话。
	/// 名字取 <c>custom-model.json</c> 的 <c>name</c>（转换时写的就是 Mod 名），
	/// 缺了才退回目录名——两者在正常流程里是同一个值。
	/// </para>
	/// </summary>
	public static IReadOnlyDictionary<string, string> ReadOwners(string modsDirectory)
	{
		var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var (key, directory) in ReadOwnerDirectories(modsDirectory))
			owners[key] = TryReadName(directory) ?? Path.GetFileName(directory);
		return owners;
	}

	/// <summary>
	/// 素体 key → 登记它的那份 <c>custom-model.json</c> 所在目录（通常是 Mod 目录下的 <c>vpk2patch</c>）。
	/// <para>
	/// 为什么要单独给目录：<c>custom-model.json</c> 里的 <c>name</c> 是转换时写的名字，
	/// 用户在库里把 Mod 改名之后两者就不一样了（库里叫"布莱默顿"，素体仍叫目录名"2505895990"）。
	/// 想告诉用户"该去库里移除哪个 Mod"，要靠目录去库里反查<b>现在的</b>名字。
	/// </para>
	/// </summary>
	public static IReadOnlyDictionary<string, string> ReadOwnerDirectories(string modsDirectory)
	{
		var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		if (string.IsNullOrWhiteSpace(modsDirectory) || !Directory.Exists(modsDirectory))
			return owners;
		IEnumerable<string> files;
		try
		{
			files = Directory.EnumerateFiles(modsDirectory, FileName, SearchOption.AllDirectories);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return owners;
		}

		foreach (var file in files)
		{
			var directory = Path.GetDirectoryName(file)!;
			var key = TryReadKey(directory);
			if (string.IsNullOrWhiteSpace(key))
				continue;
			// 同一个 key 出现在两个 Mod 里说明库里有重复项：保留第一个，行为可预期。
			owners.TryAdd(key, directory);
		}

		return owners;
	}

	/// <summary>
	/// 读 <c>custom-model.json</c> 的 <c>name</c>（转换时写进去的 Mod 名）。
	/// <para>
	/// <b>和 <see cref="TryReadKey"/> 找同样三处。</b>转换产物把这份文件写在 <c>vpk2patch/</c>
	/// （现在是 <c>sectional/</c>）下；只读根目录的话 VPK 类 Mod 永远读不到名字，
	/// 界面就退回显示 <c>custom-xxxx</c>——用户报的"素体名变乱码"就是这么来的。
	/// </para>
	/// </summary>
	public static string? TryReadName(string modDirectory)
	{
		if (string.IsNullOrWhiteSpace(modDirectory))
			return null;
		return ReadNameAt(Path.Combine(modDirectory, FileName))
			?? ReadNameAt(Path.Combine(modDirectory, ModNodePayloadLayout.VpkPatchDirectoryName, FileName))
			?? ReadNameAt(Path.Combine(modDirectory, ModNodePayloadLayout.LegacyVpkPatchDirectoryName, FileName));
	}

	private static string? ReadNameAt(string path)
	{
		if (!File.Exists(path))
			return null;
		try
		{
			using var stream = File.OpenRead(path);
			using var document = JsonDocument.Parse(stream);
			var name = document.RootElement.TryGetProperty("name", out var value) ? value.GetString() : null;
			return string.IsNullOrWhiteSpace(name) ? null : name;
		}
		catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	/// <summary>
	/// 从一批素体定义里挑出<b>没有 Mod 归属</b>的那些（纯函数，不碰磁盘）。
	/// <para>
	/// <b>为什么要有这一步。</b>素体不是独立数据，它是某个 Mod 的转换产物：
	/// 那套 Unit 只有跟着那个 Mod 部署才有意义。所以它的生命周期必须与 Mod 绑定 ——
	/// Mod 被删掉之后，注册表里留下的记录只是一堆指向不存在 Unit 的指针，
	/// 用户看到的却是"护甲映射页里还挂着一个素体，选它却什么都不发生"。
	/// </para>
	/// <para>
	/// <b>判据只能是"库里有没有那份 <c>custom-model.json</c>"。</b>注册表里没有"归属"这一维，
	/// 而那份文件就躺在 Mod 目录里、随 Mod 一起删——它是唯一不会漂移的事实来源。
	/// 反过来，也正因为没有"归属"字段，我们<b>无法</b>区分"Mod 被删了"和"这个素体压根没被导入"，
	/// 于是两者一律按孤儿处理；这正好符合产品口径（见 notes/backlog-20261104.md 的 P2）：
	/// 素体只允许由"一键转为自定义素体"或"导入 VPK 自动绑定"生成，两种都会落成 Mod。
	/// </para>
	/// </summary>
	public static IReadOnlyList<RuntimeArmorRegistryModel> FindOrphans(
		IReadOnlyList<RuntimeArmorRegistryModel> models,
		IReadOnlyDictionary<string, string> owners)
	{
		ArgumentNullException.ThrowIfNull(models);
		ArgumentNullException.ThrowIfNull(owners);
		return models
			.Where(model => !owners.ContainsKey(model.Key))
			.ToArray();
	}
}
