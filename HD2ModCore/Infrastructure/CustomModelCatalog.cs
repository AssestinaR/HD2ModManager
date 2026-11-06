using System.IO;
using System.Text.Json;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <param name="Model">从 <c>custom-model.json</c> 还原出来的完整素体定义。</param>
/// <param name="ModDirectory">持有它的那个 Mod 目录（归属的唯一事实来源）。</param>
public sealed record CustomModelCatalogEntry(RuntimeArmorRegistryModel Model, string ModDirectory);

/// <summary>
/// 一次"从 Mod 文件夹收集自定义素体"的结果。
/// <para>
/// <b>为什么把 <see cref="Unreadable"/> 单独列出来。</b>这条链上最贵的一次事故就是
/// "读不到"被当成"没有"：某个 Mod 的 <c>custom-model.json</c> 在被占用/权限不足时读失败，
/// 对账就把它判成孤儿，于是<b>素体连同映射一起被删</b>。所以收集结果必须能区分
/// 「没有这个文件」和「这个文件读不出来」——后者出现一次，调用方就<b>不允许</b>做任何删除。
/// </para>
/// </summary>
public sealed class CustomModelCatalogResult
{
	public static readonly CustomModelCatalogResult Unusable = new(
		new Dictionary<string, CustomModelCatalogEntry>(StringComparer.OrdinalIgnoreCase),
		[],
		0,
		false);

	internal CustomModelCatalogResult(
		IReadOnlyDictionary<string, CustomModelCatalogEntry> entries,
		IReadOnlyList<string> unreadable,
		int scannedDirectories,
		bool libraryUsable)
	{
		Entries = entries;
		Unreadable = unreadable;
		ScannedDirectories = scannedDirectories;
		LibraryUsable = libraryUsable;
	}

	/// <summary>key → 条目（大小写不敏感）。</summary>
	public IReadOnlyDictionary<string, CustomModelCatalogEntry> Entries { get; }

	/// <summary>存在但读不出来的候选文件（fail-closed 的依据）。</summary>
	public IReadOnlyList<string> Unreadable { get; }

	public int ScannedDirectories { get; }

	/// <summary>库目录存在且至少有一个 Mod 目录。<b>否则不认为这是一次可信的收集。</b></summary>
	public bool LibraryUsable { get; }

	/// <summary>能不能拿这份结果去删东西。</summary>
	public bool IsTrustworthy => LibraryUsable && Unreadable.Count == 0;

	public IReadOnlyList<RuntimeArmorRegistryModel> Models
		=> Entries.Values.Select(entry => entry.Model).ToArray();

	/// <summary>不可信时给用户的一句话；可信时返回 <c>null</c>。</summary>
	public string? DescribeBlocked()
	{
		if (!LibraryUsable)
			return "Mod 库目录不可用（不存在或为空），本次不解除任何映射。";
		if (Unreadable.Count != 0)
			return $"有 {Unreadable.Count} 个 Mod 的 {VpkCustomModelFile.FileName} 读不出来"
				+ $"（{string.Join("、", Unreadable.Take(3).Select(Path.GetFileName))}），"
				+ "本次不解除任何映射——读不到不等于没有归属。";
		return null;
	}
}

/// <summary>
/// 从 Mod 文件夹收集自定义素体：<b>文件是权威，注册表只是缓存</b>。
/// <para>
/// <b>为什么改成这样。</b>素体的生命周期本来就是"随 Mod 出现、随 Mod 消失"，
/// 而 <c>custom-model.json</c> 已经写在 Mod 目录里（转换时产出、换载荷时被保留），
/// 内容就是 <see cref="RuntimeArmorRegistryModel"/> 的完整序列化（key / name / 件表）。
/// 以前把同一份数据再存一份进 <c>&lt;库&gt;/.hd2modmanager/armor-registry.workspace.json</c>，
/// 于是出现两个真相：工作区丢了 → 名字全丢（协议里刻意不存名字）；某个文件一时读不出来 →
/// 素体被判成孤儿被删。改成"从磁盘收集"之后，这两个故障模式在结构上消失。
/// </para>
/// <para>
/// <b>只看两个固定位置</b>（<c>&lt;mod&gt;/custom-model.json</c> 与
/// <c>&lt;mod&gt;/vpk2patch/custom-model.json</c>），<b>不递归</b>：以前的实现用
/// <c>SearchOption.AllDirectories</c> 扫整个库，一百多个 Mod 每次刷新都走一遍。
/// </para>
/// </summary>
public static class CustomModelCatalog
{
	public const string FileName = VpkCustomModelFile.FileName;

	/// <summary>管理器自己在库根上占用的目录前缀（不是 Mod，不能当成扫描目标）。</summary>
	public const string ManagerDirectoryPrefix = ".hd2modmanager";

	public static CustomModelCatalogResult Collect(string? modsDirectory)
	{
		if (string.IsNullOrWhiteSpace(modsDirectory) || !Directory.Exists(modsDirectory))
			return CustomModelCatalogResult.Unusable;

		string[] directories;
		try
		{
			directories = Directory.GetDirectories(modsDirectory);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return CustomModelCatalogResult.Unusable;
		}

		var candidates = directories
			.Where(directory => !Path.GetFileName(directory)
				.StartsWith(ManagerDirectoryPrefix, StringComparison.OrdinalIgnoreCase))
			.OrderBy(directory => directory, StringComparer.OrdinalIgnoreCase)
			.ToArray();
		if (candidates.Length == 0)
			return CustomModelCatalogResult.Unusable;

		var entries = new Dictionary<string, CustomModelCatalogEntry>(StringComparer.OrdinalIgnoreCase);
		var unreadable = new List<string>();
		foreach (var modDirectory in candidates)
		{
			foreach (var file in CandidateFiles(modDirectory))
			{
				if (!File.Exists(file)) continue;
				if (!TryRead(file, out var model, out var error))
				{
					unreadable.Add(error ?? file);
					continue;
				}

				if (model is null) continue;   // 文件在但内容不完整：按"没有"处理（不阻断）
				// 同一个 key 出现在两个 Mod 里：保留第一个（按目录名排序，行为可预期）。
				entries.TryAdd(model.Key, new CustomModelCatalogEntry(model, modDirectory));
			}
		}

		return new CustomModelCatalogResult(entries, unreadable, candidates.Length, libraryUsable: true);
	}

	private static IEnumerable<string> CandidateFiles(string modDirectory)
	{
		yield return Path.Combine(modDirectory, FileName);
		yield return Path.Combine(modDirectory, ModNodePayloadLayout.VpkPatchDirectoryName, FileName);
		// 老库布局。这份"根 + 子目录"的路径在两处各写了一遍（这里与 VpkCustomModelFile），
		// 只改一处会出现"映射页列得出素体、详情页选不中它"——不报错，只是行为对不上。
		yield return Path.Combine(modDirectory, ModNodePayloadLayout.LegacyVpkPatchDirectoryName, FileName);
	}

	/// <summary>
	/// 读一份 <c>custom-model.json</c>。
	/// <para>
	/// 返回 <c>true</c> + <c>model == null</c> = 文件存在但内容不完整（没有 key），按"没有"处理；
	/// 返回 <c>false</c> = <b>读不出来</b>（IO/权限/坏 JSON），调用方必须 fail-closed。
	/// </para>
	/// </summary>
	private static bool TryRead(string path, out RuntimeArmorRegistryModel? model, out string? error)
	{
		model = null;
		error = null;
		try
		{
			using var stream = File.OpenRead(path);
			using var document = JsonDocument.Parse(stream);
			var root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object) return true;
			if (!root.TryGetProperty("key", out var keyElement)
				|| keyElement.ValueKind != JsonValueKind.String
				|| string.IsNullOrWhiteSpace(keyElement.GetString()))
			{
				return true;
			}

			var key = keyElement.GetString()!.Trim();
			var name = root.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
				? nameElement.GetString()
				: null;
			uint? sourceItemId = root.TryGetProperty("sourceItemId", out var sourceElement)
				&& sourceElement.ValueKind == JsonValueKind.Number
				&& sourceElement.TryGetUInt32(out var parsedSource)
					? parsedSource
					: null;

			var pieces = new List<RuntimeArmorRegistryModelPiece>();
			if (root.TryGetProperty("pieces", out var piecesElement) && piecesElement.ValueKind == JsonValueKind.Array)
			{
				foreach (var piece in piecesElement.EnumerateArray())
				{
					if (piece.ValueKind != JsonValueKind.Object) continue;
					var unit = piece.TryGetProperty("unit", out var unitElement) && unitElement.ValueKind == JsonValueKind.String
						? unitElement.GetString()
						: null;
					if (string.IsNullOrWhiteSpace(unit)) continue;
					pieces.Add(new RuntimeArmorRegistryModelPiece(
						ReadUInt32(piece, "body"),
						unit!,
						ReadUInt32(piece, "slot"),
						ReadUInt32(piece, "pieceType"),
						(byte)Math.Min(byte.MaxValue, ReadUInt32(piece, "tone")),
						ReadBool(piece, "enabled", fallback: true),
						ReadBool(piece, "isDefaultBase", fallback: false),
						ReadBool(piece, "isReserved", fallback: false),
						piece.TryGetProperty("originalBody", out var original) && original.ValueKind == JsonValueKind.Number
							&& original.TryGetUInt32(out var originalBody)
								? originalBody
								: null));
				}
			}

			model = new RuntimeArmorRegistryModel(key, sourceItemId, pieces, name);
			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
			or JsonException or NotSupportedException)
		{
			error = $"{path}（{exception.Message}）";
			return false;
		}
	}

	private static uint ReadUInt32(JsonElement element, string property)
		=> element.TryGetProperty(property, out var value)
			&& value.ValueKind == JsonValueKind.Number
			&& value.TryGetUInt32(out var parsed)
				? parsed
				: 0u;

	private static bool ReadBool(JsonElement element, string property, bool fallback)
		=> element.TryGetProperty(property, out var value)
			&& value.ValueKind is JsonValueKind.True or JsonValueKind.False
				? value.GetBoolean()
				: fallback;
}
