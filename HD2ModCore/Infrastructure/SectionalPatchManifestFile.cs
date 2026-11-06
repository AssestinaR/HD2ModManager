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
	/// 按"目录里实际有哪些 patch + 转换报告怎么说"生成一份清单。
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
			var carried = previous?.Patches.FirstOrDefault(entry => entry.PatchIndex == patchIndex);
			entries.Add(new SectionalPatchGroupEntry(
				patchIndex,
				archiveHex16,
				role,
				matched?.Patch.Name,
				// 模型自带的那份材质也是选项之一，名字固定为「模型内置材质」：
				// 它和折并进来的材质包是同一类东西（一个 patch、可勾选），
				// 单独给它一个名字是为了让用户在选项表里一眼认出"这是自带的，不是后来加的包"。
				OptionName: optionNameByPatchFileName?.GetValueOrDefault(name)
					?? carried?.OptionName
					?? (matched is { MatchedMaterial: true } ? SectionalPatchManifest.BuiltInMaterialOptionName : null),
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
			return document;
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
