using System.Text.Json;
using System.Text.Json.Serialization;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// Mod 自己的外观设置，存在 mod 文件夹里（跟着文件夹走，迁移/复制都不丢）。
/// <para>
/// <b>文件名为什么带前缀。</b><c>mod.json</c> 这个名字被第三方工具占着（作者的工程配置，
/// schema 完全不同）。用管理器自己的前缀既不会撞，也不会被残留清理当成"可以删的碎片"。
/// </para>
/// <para>
/// <b>它记的是用户意图，不是派生数据。</b>所以读不出来时**不猜**——返回 <c>null</c> 让调用方
/// 走各自的安全默认（材质层默认只启用模型内置材质），而不是把它当成"用户什么都没启用"。
/// </para>
/// </summary>
public static class ModSettingsFile
{
	public const string FileName = ".hd2modmanager-mod.json";

	public const string CurrentSchema = "hd2mod.mod/1";

	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	public sealed record Document(
		string Schema,
		IReadOnlyList<string>? EnabledMaterialPatches);

	/// <summary>读设置；文件不存在或读不懂时返回 <c>null</c>（调用方走默认，不猜）。</summary>
	public static Document? TryRead(string modDirectory)
	{
		if (string.IsNullOrWhiteSpace(modDirectory)) return null;
		var path = Path.Combine(modDirectory, FileName);
		try
		{
			if (!File.Exists(path)) return null;
			var text = File.ReadAllText(path);
			var document = JsonSerializer.Deserialize<Document>(text, JsonOptions);
			return document is null || !string.Equals(document.Schema, CurrentSchema, StringComparison.Ordinal)
				? null
				: document;
		}
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or JsonException
			or System.Security.SecurityException)
		{
			return null;
		}
	}

	/// <summary>写设置。写不进去返回原因，调用方决定要不要说一声。</summary>
	public static string? TryWrite(string modDirectory, Document document)
	{
		ArgumentNullException.ThrowIfNull(document);
		if (string.IsNullOrWhiteSpace(modDirectory)) return "没有 Mod 目录。";
		try
		{
			var path = Path.Combine(modDirectory, FileName);
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
}
