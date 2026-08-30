using System.Text.Json;
using System.Text.Json.Serialization;

namespace HD2ModCli;

public static class CliOutput
{
	public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = true,
		Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
	};

	public static void WriteJson(object value)
		=> Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

	public static async Task WriteExplicitFileAsync(string outputPath, string content, bool overwrite, CancellationToken cancellationToken = default)
	{
		var fullPath = NormalizeFilePath(outputPath, "OutputPathInvalid");
		if (File.Exists(fullPath) && !overwrite)
			throw new CliUserException("OutputAlreadyExists", $"输出文件已存在：{fullPath}。如需覆盖，请显式传入 --overwrite。");

		var directory = Path.GetDirectoryName(fullPath);
		if (string.IsNullOrWhiteSpace(directory))
			throw new CliUserException("OutputPathInvalid", $"无法解析输出文件路径：{outputPath}");
		Directory.CreateDirectory(directory);

		var temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			await File.WriteAllTextAsync(temporaryPath, content, cancellationToken).ConfigureAwait(false);
			File.Move(temporaryPath, fullPath, overwrite);
		}
		finally
		{
			if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
		}
	}

	public static string ToJson(object value)
		=> JsonSerializer.Serialize(value, JsonOptions);

	public static string NormalizeDirectoryPath(string path, string code)
	{
		try { return Path.GetFullPath(path); }
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			throw new CliUserException(code, $"路径无效：{path}");
		}
	}

	private static string NormalizeFilePath(string path, string code)
	{
		try { return Path.GetFullPath(path); }
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			throw new CliUserException(code, $"路径无效：{path}");
		}
	}
}
