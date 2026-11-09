using System.Text.Json;
using System.Text.Json.Serialization;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// <c>sectional-source.json</c> 的读写。**唯一入口**——转换链路、类型判定、详情页都从这里读。
/// <para>
/// <b>写入永远是全量覆盖。</b>它是导入那一刻定下来的输入，之后只在"重新生成"时整体换掉；
/// 不做局部更新，避免出现"白名单改了但来源没改"这种半截状态。
/// </para>
/// </summary>
public static class SectionalSourceFile
{
	/// <summary>上限：这是一份小清单，超过这个大小只可能是写坏了。</summary>
	private const long MaximumBytes = 1024 * 1024;

	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = true,
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
	};

	public static string GetPath(string modDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(modDirectory);
		return Path.Combine(modDirectory, SectionalSourceManifest.FileName);
	}

	public static bool Exists(string modDirectory)
	{
		if (string.IsNullOrWhiteSpace(modDirectory)) return false;
		try
		{
			return File.Exists(GetPath(modDirectory));
		}
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or System.Security.SecurityException)
		{
			return false;
		}
	}

	/// <summary>读依据文件；不存在、读不懂、schema 不认识一律返回 <c>null</c>。</summary>
	public static async ValueTask<SectionalSourceDocument?> TryReadAsync(
		string modDirectory,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(modDirectory)) return null;
		try
		{
			var path = GetPath(modDirectory);
			var info = new FileInfo(path);
			if (!info.Exists || info.Length > MaximumBytes) return null;
			var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
			var document = JsonSerializer.Deserialize<SectionalSourceDocument>(json, JsonOptions);
			if (document is null) return null;
			if (!SectionalSourceManifest.ReadableSchemas.Contains(document.Schema, StringComparer.Ordinal))
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

	/// <summary>校验一份依据文件能不能拿去转换。返回错误说明；<c>null</c> 表示通过。</summary>
	public static string? Validate(SectionalSourceDocument? document)
	{
		if (document is null) return "没有转换依据（sectional-source.json 缺失或读不懂）。";
		if (!document.HasSelection) return "护甲与头盔都没有选：没有来源可转。";
		if (document.Sources.Count == 0) return "依据文件里没有来源条目。";
		if (document.Sources.Any(entry => string.IsNullOrWhiteSpace(entry.Name)))
			return "依据文件里有来源条目没有名字：材质组没法命名。";
		if (document.AssetKeys.Units.Count == 0) return "干净资产白名单里没有任何 unit。";
		return null;
	}

	/// <summary>写依据文件（先校验，再落盘）。返回错误说明；<c>null</c> 表示成功。</summary>
	public static string? TryWrite(string modDirectory, SectionalSourceDocument? document)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(modDirectory);
		var error = Validate(document);
		if (error is not null) return error;
		try
		{
			var path = GetPath(modDirectory);
			var temporary = path + ".tmp";
			File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions) + "\n");
			File.Move(temporary, path, overwrite: true);
			return null;
		}
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or JsonException
			or System.Security.SecurityException)
		{
			return exception.Message;
		}
	}
}
