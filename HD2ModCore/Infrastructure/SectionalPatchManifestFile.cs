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
		// 角色写成字符串：这份文件是给人看的（"每个 patch 组是谁"），数字读起来是噪音。
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
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
		IReadOnlyDictionary<string, string>? optionNameByPatchFileName = null)
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
			var role = matched?.Kind switch
			{
				"model" => SectionalPatchRole.Unit,
				"material" => SectionalPatchRole.Material,
				// 报告缺失/对不上时按约定：patch_0 是 unit，其余是材质。
				_ => patchIndex == 0 ? SectionalPatchRole.Unit : SectionalPatchRole.Material,
			};
			entries.Add(new SectionalPatchGroupEntry(
				patchIndex,
				archiveHex16,
				role,
				matched?.Name,
				OptionName: optionNameByPatchFileName?.GetValueOrDefault(name),
				UnitCount: matched?.UnitCount,
				MaterialCount: matched?.MaterialCount,
				TextureCount: matched?.TextureCount));
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

	private static VpkConversionPatchSummary? Match(VpkConversionReport? report, string patchFileName)
	{
		if (report is null) return null;
		foreach (var patch in report.Patches)
		{
			if (string.Equals(patch.UnitPatch, patchFileName, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(patch.MaterialPatch, patchFileName, StringComparison.OrdinalIgnoreCase))
				return patch;
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
