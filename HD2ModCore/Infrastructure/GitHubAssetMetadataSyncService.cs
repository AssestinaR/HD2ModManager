using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// 作用：从可配置的 GitHub raw 仓库地址下载并校验资产元数据表。
// Purpose: Downloads and validates asset metadata hash lists from a configurable GitHub raw repository URL.
public sealed class GitHubAssetMetadataSyncService : IAssetMetadataSyncService
{
	public const string DefaultRepositoryRawBaseUrl = "https://raw.githubusercontent.com/Boxofbiscuits97/HD2SDK-CommunityEdition/main";

	private static readonly JsonSerializerOptions ManifestJsonOptions = new() { WriteIndented = true };
	private static readonly IReadOnlyList<AssetMetadataFileSpec> Files = new[]
	{
		new AssetMetadataFileSpec("archivehashes.json", ValidateArchiveHashesJson),
		new AssetMetadataFileSpec("bonehash.txt", ValidateBoneHashesFile),
		new AssetMetadataFileSpec("typehash.txt", ValidateHashTextFile),
		new AssetMetadataFileSpec("friendlynames.txt", ValidateFriendlyNamesFile),
	};

	private readonly HttpClient _httpClient;
	private readonly StoragePaths _paths;

	public GitHubAssetMetadataSyncService(HttpClient httpClient, StoragePaths paths)
	{
		_httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
		_paths = paths ?? throw new ArgumentNullException(nameof(paths));
	}

	public async ValueTask<AssetMetadataSyncResult> SyncAsync(string repositoryRawBaseUrl, CancellationToken cancellationToken = default)
	{
		if (!TryNormalizeRawBaseUrl(repositoryRawBaseUrl, out var rawBaseUrl, out var normalizeError))
		{
			return AssetMetadataSyncResult.Failed(repositoryRawBaseUrl, normalizeError);
		}

		var tempDirectory = Path.Combine(_paths.ResourcesDirectory, ".asset-metadata-sync-" + Guid.NewGuid().ToString("N"));
		try
		{
			Directory.CreateDirectory(_paths.ResourcesDirectory);
			Directory.CreateDirectory(tempDirectory);

			var manifest = new AssetMetadataManifest
			{
				Source = rawBaseUrl,
				UpdatedAtUtc = DateTimeOffset.UtcNow,
			};

			foreach (var file in Files)
			{
				var content = await DownloadStringAsync(BuildFileUri(rawBaseUrl, file.FileName), cancellationToken).ConfigureAwait(false);
				if (!file.Validate(content, out var validationError))
				{
					return AssetMetadataSyncResult.Failed(rawBaseUrl, $"{file.FileName}: {validationError}");
				}

				var tempPath = Path.Combine(tempDirectory, file.FileName);
				await File.WriteAllTextAsync(tempPath, content, cancellationToken).ConfigureAwait(false);

				var bytes = new FileInfo(tempPath).Length;
				manifest.Files[file.FileName] = new AssetMetadataFileManifest
				{
					Bytes = bytes,
					Sha256 = ComputeSha256(tempPath),
				};
			}

			await File.WriteAllTextAsync(
				Path.Combine(tempDirectory, "asset-metadata-manifest.json"),
				JsonSerializer.Serialize(manifest, ManifestJsonOptions),
				cancellationToken).ConfigureAwait(false);

			File.Copy(Path.Combine(tempDirectory, "archivehashes.json"), _paths.ArchiveHashesPath, overwrite: true);
			File.Copy(Path.Combine(tempDirectory, "bonehash.txt"), _paths.BoneHashesPath, overwrite: true);
			File.Copy(Path.Combine(tempDirectory, "typehash.txt"), _paths.TypeHashesPath, overwrite: true);
			File.Copy(Path.Combine(tempDirectory, "friendlynames.txt"), _paths.FriendlyNamesPath, overwrite: true);
			File.Copy(Path.Combine(tempDirectory, "asset-metadata-manifest.json"), _paths.AssetMetadataManifestPath, overwrite: true);

			return new AssetMetadataSyncResult(
				true,
				manifest.UpdatedAtUtc,
				rawBaseUrl,
				Files.Select(f => f.FileName).ToArray(),
				null);
		}
		catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or TaskCanceledException)
		{
			return AssetMetadataSyncResult.Failed(rawBaseUrl ?? repositoryRawBaseUrl, ex.Message);
		}
		finally
		{
			try
			{
				if (Directory.Exists(tempDirectory))
				{
					Directory.Delete(tempDirectory, recursive: true);
				}
			}
			catch
			{
				// ignored
			}
		}
	}

	private async Task<string> DownloadStringAsync(Uri uri, CancellationToken cancellationToken)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, uri);
		request.Headers.UserAgent.ParseAdd("HD2ModManager/1.0");

		using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
		response.EnsureSuccessStatusCode();
		var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		if (string.IsNullOrWhiteSpace(content))
		{
			throw new InvalidOperationException($"Downloaded file is empty: {uri}");
		}

		return content;
	}

	private static Uri BuildFileUri(string rawBaseUrl, string fileName)
		=> new($"{rawBaseUrl.TrimEnd('/')}/hashlists/{fileName}", UriKind.Absolute);

	private static bool TryNormalizeRawBaseUrl(string value, out string rawBaseUrl, out string error)
	{
		rawBaseUrl = string.Empty;
		error = string.Empty;

		if (string.IsNullOrWhiteSpace(value))
		{
			error = "仓库地址不能为空。";
			return false;
		}

		var trimmed = value.Trim().TrimEnd('/');
		if (trimmed.Contains("github.com/", StringComparison.OrdinalIgnoreCase))
		{
			trimmed = ConvertGitHubRepositoryUrl(trimmed);
		}

		if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
		{
			error = "仓库地址必须是有效的 HTTPS 地址。";
			return false;
		}

		rawBaseUrl = trimmed;
		return true;
	}

	private static string ConvertGitHubRepositoryUrl(string value)
	{
		var uri = new Uri(value);
		var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length < 2)
		{
			return value;
		}

		var owner = parts[0];
		var repo = parts[1];
		var branch = "main";
		if (parts.Length >= 4 && string.Equals(parts[2], "tree", StringComparison.OrdinalIgnoreCase))
		{
			branch = parts[3];
		}

		return $"https://raw.githubusercontent.com/{owner}/{repo}/{branch}";
	}

	private static bool ValidateArchiveHashesJson(string content, out string error)
	{
		error = string.Empty;
		try
		{
			using var document = JsonDocument.Parse(content);
			if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.EnumerateObject().Any())
			{
				error = "JSON 根节点必须是非空对象。";
				return false;
			}

			foreach (var category in document.RootElement.EnumerateObject())
			{
				if (category.Value.ValueKind != JsonValueKind.Object)
				{
					error = $"分类 {category.Name} 必须是对象。";
					return false;
				}
			}

			return true;
		}
		catch (JsonException ex)
		{
			error = ex.Message;
			return false;
		}
	}

	private static bool ValidateHashTextFile(string content, out string error)
	{
		error = string.Empty;
		var lines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
		if (lines.Length == 0)
		{
			error = "文件没有有效行。";
			return false;
		}

		foreach (var line in lines.Take(20))
		{
			var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length != 2)
			{
				error = $"行格式无效：{line}";
				return false;
			}

			if (!ulong.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out _))
			{
				error = $"类型 hash 不是十六进制：{parts[0]}";
				return false;
			}
		}

		return true;
	}

	private static bool ValidateBoneHashesFile(string content, out string error)
	{
		error = string.Empty;
		var lines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
		if (lines.Length == 0)
		{
			error = "文件没有有效行。";
			return false;
		}

		foreach (var line in lines.Take(20))
		{
			var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length != 2 || !uint.TryParse(parts[0], out _))
			{
				error = $"骨骼 hash 行格式无效：{line}";
				return false;
			}
		}

		return true;
	}

	private static bool ValidateFriendlyNamesFile(string content, out string error)
	{
		error = string.Empty;
		var lines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
		if (lines.Length == 0)
		{
			error = "文件没有有效行。";
			return false;
		}

		foreach (var line in lines.Take(20))
		{
			var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length != 2)
			{
				error = $"行格式无效：{line}";
				return false;
			}

			if (!ulong.TryParse(parts[0], out _))
			{
				error = $"File ID 不是十进制数字：{parts[0]}";
				return false;
			}
		}

		return true;
	}

	private static string ComputeSha256(string path)
	{
		using var stream = File.OpenRead(path);
		return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
	}

	private sealed record AssetMetadataFileSpec(string FileName, ValidateAssetMetadataFile Validate);
	private delegate bool ValidateAssetMetadataFile(string content, out string error);
}
