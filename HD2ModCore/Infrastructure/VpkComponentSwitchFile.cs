using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// <c>components.json</c> 的读写。与 <c>sections.json</c> 放在同一个 patch 目录里。
/// <para>
/// 读取对<b>坏文件宽容</b>（返回 <c>null</c>，由调用方决定怎么提示），写入是原子的。
/// 这条不对称是刻意的：组件树是可选增强，一份坏掉的开关表不该让整个 Mod 详情页打不开；
/// 而写坏一份表会让用户下次打开时"开关凭空消失"，所以写必须要么成功要么不留痕。
/// </para>
/// </summary>
public static class VpkComponentSwitchFile
{
	public const string FileName = "components.json";

	/// <summary>上限：实测 15 个开关约 4 KiB，留三个数量级。</summary>
	public const int MaximumBytes = 4 * 1024 * 1024;

	public static JsonSerializerOptions JsonOptions { get; } = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
		// 开关名是中文（`猫耳`、`披风`）。这份文件是给人看、给人 grep 的，不转义。
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	public static string Serialize(VpkComponentSwitchDocument document)
	{
		ArgumentNullException.ThrowIfNull(document);
		return JsonSerializer.Serialize(document, JsonOptions) + "\n";
	}

	/// <summary>解析并校验版本。不合法就抛——写入方自己产的文件不该悄悄失效。</summary>
	public static VpkComponentSwitchDocument Parse(string text)
	{
		ArgumentNullException.ThrowIfNull(text);
		if (Encoding.UTF8.GetByteCount(text) > MaximumBytes)
			throw new InvalidDataException($"components.json 超过 {MaximumBytes} 字节上限。");

		VpkComponentSwitchDocument? document;
		try
		{
			document = JsonSerializer.Deserialize<VpkComponentSwitchDocument>(text, JsonOptions);
		}
		catch (JsonException exception)
		{
			throw new InvalidDataException($"components.json 不是合法的 JSON：{exception.Message}", exception);
		}

		if (document is null)
			throw new InvalidDataException("components.json 内容为空。");
		if (!string.Equals(document.Schema, VpkComponentSwitchDocument.CurrentSchema, StringComparison.Ordinal))
			throw new InvalidDataException(
				$"components.json 的 schema 是 '{document.Schema}'，本程序只认 "
				+ $"'{VpkComponentSwitchDocument.CurrentSchema}'；请重新导入一次这个 Mod。");
		return document with
		{
			Switches = document.Switches ?? [],
			Unmatched = document.Unmatched ?? [],
		};
	}

	/// <summary>读一份开关表；文件不存在或读不懂都返回 <c>null</c>（调用方据此隐藏开关区）。</summary>
	public static async ValueTask<VpkComponentSwitchDocument?> TryReadAsync(
		string directory,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(directory))
			return null;
		var path = Path.Combine(directory, FileName);
		if (!File.Exists(path))
			return null;
		try
		{
			var text = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
			return Parse(text);
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	/// <summary>原子写：先写同目录临时文件再替换，避免详情页读到半份表。</summary>
	public static async ValueTask WriteAsync(
		string directory,
		VpkComponentSwitchDocument document,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directory);
		ArgumentNullException.ThrowIfNull(document);
		var text = Serialize(document);
		if (Encoding.UTF8.GetByteCount(text) > MaximumBytes)
			throw new InvalidDataException($"components.json 超过 {MaximumBytes} 字节上限：{directory}");

		Directory.CreateDirectory(directory);
		var path = Path.Combine(directory, FileName);
		var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			await File.WriteAllTextAsync(temporary, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken)
				.ConfigureAwait(false);
			File.Move(temporary, path, overwrite: true);
		}
		finally
		{
			if (File.Exists(temporary))
				File.Delete(temporary);
		}
	}
}
