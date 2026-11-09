using System.Text.Json;
using System.Text.Json.Serialization;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 读写基底目录里的 <c>patches.json</c>：让"每个 patch 组是谁"这件事写在目录里，
/// 而不是靠读的人去回溯转换报告或猜文件名。
/// <para>
/// <b>它的权威性低于目录本身。</b>读不到、schema 不认识、内容对不上目录里的文件——一律
/// 回退到"按目录列举现推"，<b>不报错、不阻断</b>。这份清单是可重建的派生数据，
/// 对它设防只会把"清单坏了"变成"这个 Mod 用不了"。
/// </para>
/// </summary>
public static class SectionalPatchManifestFile
{
	private const int MaximumBytes = 256 * 1024;

	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		// 角色写成字符串、中文不转义：这份文件是给人看的（“每个 patch 组是谁”）。
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
		Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	public static string GetPath(string payloadDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(payloadDirectory);
		return Path.Combine(payloadDirectory, SectionalPatchManifest.FileName);
	}

	/// <summary>
	/// 从目录里任意一个 patch 文件名取出 16 位归档段（<c>&lt;archive&gt;.patch_N</c>）。
	/// 目录为空或没有符合命名的文件时返回空串——调用方据此决定"没什么可写"。
	/// </summary>
	public static string ReadArchiveHex16(string payloadDirectory)
	{
		if (string.IsNullOrWhiteSpace(payloadDirectory) || !Directory.Exists(payloadDirectory))
			return string.Empty;
		try
		{
			foreach (var path in Directory.EnumerateFiles(payloadDirectory, "*.patch_*", SearchOption.TopDirectoryOnly))
			{
				var name = Path.GetFileName(path);
				var marker = name.IndexOf(".patch_", StringComparison.OrdinalIgnoreCase);
				if (marker == 16) return name[..16];
			}
		}
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or DirectoryNotFoundException)
		{
			return string.Empty;
		}
		return string.Empty;
	}

	/// <summary>
	/// 按**当前的**转换报告重写一份 <c>patches.json</c>；目录不存在/没有转换报告时返回 <c>null</c>。
	/// <para>
	/// <b>必须在"新报告已经写进 Mod 根"之后调用。</b>报告是"哪个 patch 是谁"的唯一来源；
	/// 拿上一份报告写出来的清单会把材质选项的名字全丢掉（界面上退化成「材质 1 / 2 / 3」），
	/// 而且顺序、角色也会跟着错。2026-11-08 的实测就是这条：载荷换了、报告还没换，
	/// 于是清单里那几个新材质没有名字。
	/// </para>
	/// </summary>
	public static SectionalPatchManifestDocument? Rebuild(
		string modDirectory,
		DateTimeOffset generatedUtc,
		SectionalPatchManifestDocument? previous = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(modDirectory);
		var payload = ModNodePayloadLayout.TryGetExistingVpkPatchDirectory(modDirectory);
		if (payload is null) return null;
		var archive = ReadArchiveHex16(payload);
		if (string.IsNullOrEmpty(archive)) return null;

		// 上一版里的**用户输入**（选项名/备注/配图/顺序）必须跨重建保留。
		// 调用方应该在"换载荷之前"读好传进来：换载荷会把旧文件删掉。
		previous ??= TryReadAsync(payload).AsTask().GetAwaiter().GetResult();
		var document = Build(payload, archive, VpkConversionReport.TryRead(modDirectory), generatedUtc, null, previous);
		if (document.Patches.Count == 0) return null;
		_ = TryWrite(payload, document);
		return document;
	}

	/// <summary>按"目录里实际有哪些 patch + 转换报告怎么说"生成一份清单。
	/// <para>
	/// 报告里 <c>patches[]</c> 的 <c>unitPatch</c> / <c>materialPatch</c> 给出"来源 VPK → patch 文件名"
	/// 的对应关系；据此可以把每个 patch 文件名翻译成"来自哪个来源、是 unit 还是材质、带多少资源"。
	/// 报告缺失或对不上时，退化为按命名约定判：<c>patch_0</c> = unit，其余 = 材质。
	/// </para>
	/// </summary>
	public static SectionalPatchManifestDocument Build(
		string payloadDirectory,
		string archiveHex16,
		VpkConversionReport? report,
		DateTimeOffset generatedUtc,
		IReadOnlyDictionary<string, string>? optionNameByPatchFileName = null,
		SectionalPatchManifestDocument? previous = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(payloadDirectory);
		var entries = new List<SectionalPatchGroupEntry>();
		if (!Directory.Exists(payloadDirectory)) return new SectionalPatchManifestDocument(
			SectionalPatchManifest.CurrentSchema, generatedUtc, entries);

		foreach (var path in Directory.EnumerateFiles(payloadDirectory, "*.patch_*", SearchOption.TopDirectoryOnly))
		{
			var name = Path.GetFileName(path);
			if (name.EndsWith(".gpu_resources", StringComparison.OrdinalIgnoreCase)
				|| name.EndsWith(".stream", StringComparison.OrdinalIgnoreCase))
				continue;
			if (!TryReadPatchIndex(name, out var patchIndex)) continue;

			var matched = Match(report, name);
			// 角色看**命中的是哪个字段**，而不是那条报告的 kind。
			//
			// 一次转换可能同时产出 unit patch 与材质 patch（模型自带材质时会拆成两份），
			// 那条报告的 kind 是 "model" —— 照 kind 判会把材质 patch 也标成 unit，
			// 于是 `mod show` 的材质选项表是空的、`mod material set` 无从下手。
			var role = matched switch
			{
				{ MatchedMaterial: true } => SectionalPatchRole.Material,
				{ MatchedUnit: true } => SectionalPatchRole.Unit,
				_ => patchIndex == 0 ? SectionalPatchRole.Unit : SectionalPatchRole.Material,
			};
			// **按来源 VPK 认领上一版的用户字段**，序号只作兜底。
			//
			// 用户改的是"某个材质包叫什么、排在哪儿"。重转之后序号可能变（报告顺序变了、
			// 少了一个包……），按序号认领会把名字/顺序接到**另一个**包上 —— 那既污染了名字，
			// 也污染了部署顺序，而用户看不出是重建干的。
			//
			// ⚠️ **一个 VPK 可以产出多个 patch**（模型自带材质时 unit + 材质同源）。这时只按名字
			// 找会永远命中第一条（unit，没有名字），于是材质那一行又退回默认名 ——
			// 2026-11-08 实测的"名字不显示"就是这个：同源两条，按名字认领必须再带上序号/角色。
			var sourceName = matched?.Patch.Name;
			var sameSource = previous is null || string.IsNullOrWhiteSpace(sourceName)
				? Array.Empty<SectionalPatchGroupEntry>()
				: previous.Patches
					.Where(entry => string.Equals(entry.SourceName, sourceName, StringComparison.OrdinalIgnoreCase))
					.ToArray();
			var carried = sameSource.FirstOrDefault(entry => entry.PatchIndex == patchIndex)
				?? (sameSource.Length == 1 ? sameSource[0] : null)
				?? sameSource.FirstOrDefault(entry => entry.Role == role)
				?? previous?.Patches.FirstOrDefault(entry => entry.PatchIndex == patchIndex);
			entries.Add(new SectionalPatchGroupEntry(
				patchIndex,
				archiveHex16,
				role,
				sourceName,
				// 模型自带的那份材质也是选项之一，名字固定为「模型内置材质」：
				// 它和折并进来的材质包是同一类东西（一个 patch、可勾选），
				// 单独给它一个名字是为了让用户在选项表里一眼认出"这是自带的，不是后来加的包"。
				OptionName: optionNameByPatchFileName?.GetValueOrDefault(name)
					?? carried?.OptionName
					?? DefaultOptionName(matched),
				UnitCount: matched?.Patch.UnitCount,
				MaterialCount: matched?.Patch.MaterialCount,
				TextureCount: matched?.Patch.TextureCount,
				// 用户写的那几项跨重建保留：它们是用户输入，不该因为重转一次就没了。
				Order: carried?.Order,
				Notes: carried?.Notes,
				ImageFileName: carried?.ImageFileName));
		}

		entries.Sort((left, right) => left.PatchIndex.CompareTo(right.PatchIndex));
		return new SectionalPatchManifestDocument(
			SectionalPatchManifest.CurrentSchema, generatedUtc, entries);
	}

	/// <summary>写一份清单；写不出来只返回错误文本，调用方决定要不要记日志。</summary>
	public static async ValueTask<string?> TryWriteAsync(
		string payloadDirectory,
		SectionalPatchManifestDocument document,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(payloadDirectory);
		ArgumentNullException.ThrowIfNull(document);
		try
		{
			Directory.CreateDirectory(payloadDirectory);
			var path = GetPath(payloadDirectory);
			var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
			await File.WriteAllTextAsync(
				temporary,
				JsonSerializer.Serialize(document, JsonOptions) + "\n",
				new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
				cancellationToken).ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();
			File.Move(temporary, path, overwrite: true);
			return null;
		}
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or System.Security.SecurityException)
		{
			return exception.Message;
		}
	}

	/// <summary>同步写入口：导入路径本身是同步的，不值得为一个小文件引入 async 波纹。</summary>
	public static string? TryWrite(string payloadDirectory, SectionalPatchManifestDocument document)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(payloadDirectory);
		ArgumentNullException.ThrowIfNull(document);
		try
		{
			Directory.CreateDirectory(payloadDirectory);
			var path = GetPath(payloadDirectory);
			var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
			File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions) + "\n",
				new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			File.Move(temporary, path, overwrite: true);
			return null;
		}
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or System.Security.SecurityException)
		{
			return exception.Message;
		}
	}

	/// <summary>读清单；**任何问题都返回 <c>null</c>**——它只是便利信息，不是权威。</summary>
	public static async ValueTask<SectionalPatchManifestDocument?> TryReadAsync(
		string payloadDirectory,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(payloadDirectory)) return null;
		var path = GetPath(payloadDirectory);
		try
		{
			var info = new FileInfo(path);
			if (!info.Exists || info.Length > MaximumBytes) return null;
			var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
			var document = JsonSerializer.Deserialize<SectionalPatchManifestDocument>(json, JsonOptions);
			if (document is null) return null;
			if (!SectionalPatchManifest.ReadableSchemas.Contains(document.Schema, StringComparer.Ordinal))
				return null;
			return UpgradeLegacySourceNames(document, json);
		}
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or JsonException
			or System.Security.SecurityException)
		{
			return null;
		}
	}

	/// <param name="MatchedUnit">命中报告的 <c>unitPatch</c> 字段。</param>
	/// <param name="MatchedMaterial">命中报告的 <c>materialPatch</c> 字段。</param>
	private sealed record MatchResult(VpkConversionPatchSummary Patch, bool MatchedUnit, bool MatchedMaterial);

	/// <summary>1.5.x 写的来源名键。字段已泛化成 <c>sourceName</c>，老库仍要照读。</summary>
	private const string LegacySourceNameProperty = "sourceVpkName";

	/// <summary>
	/// 旧键兼容：把 <c>sourceVpkName</c> 补进 <see cref="SectionalPatchGroupEntry.SourceName"/>。
	/// <para>
	/// 为什么在读取层做而不是给属性挂两个名字：<c>sourceName</c> 是新写的唯一键，
	/// 若同时保留一个"旧属性"，两处读值就会分叉（谁为空、谁为准）。这里只在**确有缺失**时
	/// 二次解析原始 JSON，正常文件一次都不会走到。
	/// </para>
	/// </summary>
	private static SectionalPatchManifestDocument UpgradeLegacySourceNames(
		SectionalPatchManifestDocument document,
		string json)
	{
		if (document.Patches.All(entry => entry.SourceName is not null)) return document;
		try
		{
			using var parsed = JsonDocument.Parse(json);
			if (!TryGetProperty(parsed.RootElement, "patches", out var patches)
				|| patches.ValueKind != JsonValueKind.Array)
				return document;

			var legacy = new Dictionary<int, string>();
			foreach (var element in patches.EnumerateArray())
			{
				if (element.ValueKind != JsonValueKind.Object) continue;
				if (!TryGetProperty(element, "patchIndex", out var indexElement)
					|| !indexElement.TryGetInt32(out var index)) continue;
				if (!TryGetProperty(element, LegacySourceNameProperty, out var nameElement)
					|| nameElement.ValueKind != JsonValueKind.String) continue;
				var name = nameElement.GetString();
				if (!string.IsNullOrWhiteSpace(name)) legacy[index] = name;
			}

			if (legacy.Count == 0) return document;
			var upgraded = document.Patches
				.Select(entry => entry.SourceName is null && legacy.TryGetValue(entry.PatchIndex, out var name)
					? entry with { SourceName = name }
					: entry)
				.ToArray();
			return document with { Patches = upgraded };
		}
		catch (JsonException)
		{
			return document;
		}
	}

	/// <summary>按大小写不敏感取属性：手改过的清单不该因为键名大小写就整份读不出来。</summary>
	private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
	{
		if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
			return true;
		if (element.ValueKind == JsonValueKind.Object)
		{
			foreach (var property in element.EnumerateObject())
			{
				if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
				value = property.Value;
				return true;
			}
		}

		value = default;
		return false;
	}

	/// <summary>
	/// 材质选项的默认名字：**模型自己那次转换**产出的材质叫「模型内置材质」，
	/// 别人的材质包产出的就叫那个 VPK 的名字。
	/// <para>
	/// <b>为什么必须区分。</b>一整组（模型 + N 个材质包）转进同一个 Mod 之后，材质选项表里有 N+1 行。
	/// 全都叫「模型内置材质」的话，用户在界面上根本分不出哪一行是哪个包——而以前每个材质包是
	/// 独立 Mod，靠 Mod 名区分；合并成一个条目之后，只剩这个字段能区分了。
	/// </para>
	/// </summary>
	private static string? DefaultOptionName(MatchResult? matched)
	{
		if (matched is not { MatchedMaterial: true }) return null;
		var kind = matched.Patch.Kind;
		if (string.IsNullOrWhiteSpace(kind)
			|| string.Equals(kind, "model", StringComparison.OrdinalIgnoreCase))
			return SectionalPatchManifest.BuiltInMaterialOptionName;

		var name = matched.Patch.Name;
		return string.IsNullOrWhiteSpace(name)
			? SectionalPatchManifest.BuiltInMaterialOptionName
			: Path.GetFileNameWithoutExtension(name);
	}

	private static MatchResult? Match(VpkConversionReport? report, string patchFileName)
	{
		if (report is null) return null;
		foreach (var patch in report.Patches)
		{
			var unit = string.Equals(patch.UnitPatch, patchFileName, StringComparison.OrdinalIgnoreCase);
			var material = string.Equals(patch.MaterialPatch, patchFileName, StringComparison.OrdinalIgnoreCase);
			if (unit || material) return new MatchResult(patch, unit, material);
		}
		return null;
	}

	private static bool TryReadPatchIndex(string fileName, out int patchIndex)
	{
		patchIndex = 0;
		// <16 位 archive>.patch_<N>
		var marker = fileName.LastIndexOf(".patch_", StringComparison.OrdinalIgnoreCase);
		if (marker <= 0) return false;
		return int.TryParse(
			fileName.AsSpan(marker + ".patch_".Length),
			System.Globalization.NumberStyles.None,
			System.Globalization.CultureInfo.InvariantCulture,
			out patchIndex);
	}
}
