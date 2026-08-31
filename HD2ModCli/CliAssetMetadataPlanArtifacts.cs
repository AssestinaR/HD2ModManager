using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

/// <summary>
/// The desktop Settings page owns an online cache of community hash tables.
/// This CLI adapter deliberately stages that mutable network input beside an
/// explicit plan first, so validate/apply never fetch a different revision.
/// </summary>
public sealed record CliAssetMetadataDiagnostic(
	string Code,
	string Severity,
	bool Blocking,
	string Message,
	string? Path = null);

public sealed record CliAssetMetadataFile(
	string FileName,
	bool Exists,
	long Length,
	string? Sha256);

public sealed record CliAssetMetadataSnapshot(
	string ResourcesDirectory,
	bool ManifestReadable,
	bool ManifestMatchesFiles,
	string? ManifestSource,
	DateTimeOffset? ManifestUpdatedAtUtc,
	IReadOnlyList<CliAssetMetadataFile> Files,
	string Fingerprint,
	IReadOnlyList<CliAssetMetadataDiagnostic> Diagnostics)
{
	public bool IsCompleteAndVerified
		=> ManifestReadable
			&& ManifestMatchesFiles
			&& Files.All(file => file.Exists && file.Length > 0 && !string.IsNullOrWhiteSpace(file.Sha256));
}

public sealed record CliAssetMetadataInspection(
	string ManagerRoot,
	string SettingsPath,
	string EffectiveRepository,
	DateTime? LastCheckUtc,
	CliAssetMetadataSnapshot Local,
	IReadOnlyList<CliAssetMetadataDiagnostic> Diagnostics)
{
	public bool IsReady => Local.IsCompleteAndVerified && !Diagnostics.Any(diagnostic => diagnostic.Blocking);
}

public sealed record CliAssetMetadataRequest(
	string RepositoryInput,
	bool RepositoryFromSettings,
	string StagingRootDirectory);

public sealed record CliAssetMetadataInputs(
	string ManagerRoot,
	string SettingsPath,
	string SettingsSha256,
	CliAssetMetadataSnapshot Current);

public sealed record CliAssetMetadataStaging(
	string RepositoryRawBaseUrl,
	CliAssetMetadataSnapshot Snapshot);

public sealed record CliAssetMetadataPlanDocument(
	string Schema,
	int Version,
	DateTimeOffset CreatedUtc,
	string InputFingerprint,
	CliAssetMetadataRequest Request,
	CliAssetMetadataInputs Inputs,
	CliAssetMetadataStaging Staging,
	IReadOnlyList<CliAssetMetadataDiagnostic> Diagnostics)
{
	public const string CurrentSchema = "hd2mod.asset-metadata-plan";
	public const int CurrentVersion = 1;

	public bool IsReady => !Diagnostics.Any(diagnostic => diagnostic.Blocking) && Staging.Snapshot.IsCompleteAndVerified;
}

internal sealed record CliAssetMetadataPlanBuildResult(
	CliAssetMetadataPlanDocument Document,
	StoragePaths Paths,
	string PlanPath);

internal sealed record CliAssetMetadataPlanValidationResult(
	CliAssetMetadataPlanDocument Document,
	CliAssetMetadataInspection? Current,
	IReadOnlyList<CliAssetMetadataDiagnostic> Diagnostics)
{
	public bool IsValid => Current is not null && Document.IsReady && !Diagnostics.Any(diagnostic => diagnostic.Blocking);
}

internal static class CliAssetMetadataPlanArtifacts
{
	private static readonly string[] RequiredFileNames =
	[
		"archivehashes.json",
		"bonehash.txt",
		"typehash.txt",
		"friendlynames.txt",
		"asset-metadata-manifest.json",
	];

	public static async ValueTask<CliAssetMetadataInspection> InspectAsync(
		CliWorkspace workspace,
		CancellationToken cancellationToken = default)
	{
		var (paths, settingsPath) = EnsureManagerWorkspace(workspace);
		var diagnostics = new List<CliAssetMetadataDiagnostic>();
		var local = await CaptureSnapshotAsync(paths, diagnostics, cancellationToken).ConfigureAwait(false);
		return new CliAssetMetadataInspection(
			paths.AppRootDirectory,
			settingsPath,
			ResolveRepositoryInput(workspace, repositoryArgument: null, out _),
			workspace.Settings?.LastAssetMetadataCheckUtc,
			local,
			DistinctDiagnostics(diagnostics));
	}

	/// <summary>
	/// Creates the external, immutable-looking staging payload required by a
	/// plan.  The payload is intentionally a sibling of the explicit plan file
	/// rather than a hidden cache under the manager installation.
	/// </summary>
	public static async ValueTask<CliAssetMetadataPlanBuildResult> BuildAsync(
		CliWorkspace workspace,
		string? repositoryArgument,
		string planOutputPath,
		bool overwrite,
		Func<StoragePaths, IAssetMetadataSyncService>? stagingServiceFactory = null,
		CancellationToken cancellationToken = default)
	{
		var (paths, settingsPath) = EnsureManagerWorkspace(workspace);
		var planPath = NormalizeFilePath(planOutputPath, "AssetMetadataPlanOutputInvalid");
		var stagingRoot = GetStagingRootDirectory(planPath);
		EnsurePlanOutputAvailable(planPath, stagingRoot, overwrite);

		var diagnostics = new List<CliAssetMetadataDiagnostic>();
		var current = await CaptureSnapshotAsync(paths, diagnostics, cancellationToken).ConfigureAwait(false);
		var settingsSha256 = await ComputeFileSha256Async(settingsPath, cancellationToken).ConfigureAwait(false);
		var repositoryInput = ResolveRepositoryInput(workspace, repositoryArgument, out var repositoryFromSettings);
		CliAssetMetadataSnapshot stagingSnapshot;
		string stagedRepository = repositoryInput;

		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(planPath)!);
			var stagingPaths = new StoragePaths(stagingRoot);
			var result = await SyncToStagingAsync(stagingPaths, repositoryInput, stagingServiceFactory, cancellationToken).ConfigureAwait(false);
			stagedRepository = result.RepositoryRawBaseUrl;
			if (!result.Success)
			{
				diagnostics.Add(new CliAssetMetadataDiagnostic(
					"AssetMetadataStageFailed", "error", true,
					$"无法从在线资产索引源取得可验证的暂存内容：{result.ErrorMessage ?? "未知错误"}", stagingRoot));
				TryDeleteDirectory(stagingRoot);
			}
		}
		catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
		{
			diagnostics.Add(new CliAssetMetadataDiagnostic(
				"AssetMetadataStageFailed", "error", true,
				$"无法从在线资产索引源取得可验证的暂存内容：{exception.Message}", stagingRoot));
			TryDeleteDirectory(stagingRoot);
		}

		var stagingPathsForRead = new StoragePaths(stagingRoot);
		stagingSnapshot = await CaptureSnapshotAsync(stagingPathsForRead, diagnostics, cancellationToken).ConfigureAwait(false);
		if (!stagingSnapshot.IsCompleteAndVerified)
		{
			diagnostics.Add(new CliAssetMetadataDiagnostic(
				"AssetMetadataStageIncomplete", "error", true,
				"在线资产元数据暂存区不完整或 manifest 与内容不一致；不会生成可应用计划。", stagingRoot));
		}
		if (!string.IsNullOrWhiteSpace(stagingSnapshot.ManifestSource)) stagedRepository = stagingSnapshot.ManifestSource;

		var request = new CliAssetMetadataRequest(repositoryInput, repositoryFromSettings, stagingRoot);
		var inputs = new CliAssetMetadataInputs(paths.AppRootDirectory, settingsPath, settingsSha256, current);
		var staging = new CliAssetMetadataStaging(stagedRepository, stagingSnapshot);
		var document = new CliAssetMetadataPlanDocument(
			CliAssetMetadataPlanDocument.CurrentSchema,
			CliAssetMetadataPlanDocument.CurrentVersion,
			DateTimeOffset.UtcNow,
			ComputeInputFingerprint(request, inputs, staging),
			request,
			inputs,
			staging,
			DistinctDiagnostics(diagnostics));
		return new CliAssetMetadataPlanBuildResult(document, paths, planPath);
	}

	public static async ValueTask<CliAssetMetadataPlanDocument> ReadDocumentAsync(
		string path,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(path))
			throw new CliUserException("AssetMetadataPlanRequired", "请通过 --plan <资产元数据计划.json> 提供计划文件。 ");
		var fullPath = NormalizeFilePath(path, "AssetMetadataPlanPathInvalid");
		if (!File.Exists(fullPath))
			throw new CliUserException("AssetMetadataPlanNotFound", $"资产元数据计划不存在：{fullPath}");
		try
		{
			var document = JsonSerializer.Deserialize<CliAssetMetadataPlanDocument>(
				await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false),
				CliOutput.JsonOptions);
			if (document is null)
				throw new CliUserException("AssetMetadataPlanUnreadable", "资产元数据计划为空或无法解析。 ");
			if (!string.Equals(document.Schema, CliAssetMetadataPlanDocument.CurrentSchema, StringComparison.Ordinal)
				|| document.Version != CliAssetMetadataPlanDocument.CurrentVersion)
			{
				throw new CliUserException("AssetMetadataPlanVersionUnsupported", $"资产元数据计划版本不受支持。需要 {CliAssetMetadataPlanDocument.CurrentSchema} v{CliAssetMetadataPlanDocument.CurrentVersion}；请重新生成计划。 ");
			}
			if (string.IsNullOrWhiteSpace(document.InputFingerprint)
				|| document.Request is null
				|| string.IsNullOrWhiteSpace(document.Request.RepositoryInput)
				|| string.IsNullOrWhiteSpace(document.Request.StagingRootDirectory)
				|| document.Inputs is null
				|| string.IsNullOrWhiteSpace(document.Inputs.ManagerRoot)
				|| string.IsNullOrWhiteSpace(document.Inputs.SettingsPath)
				|| string.IsNullOrWhiteSpace(document.Inputs.SettingsSha256)
				|| document.Inputs.Current is null
				|| document.Staging is null
				|| document.Staging.Snapshot is null
				|| document.Diagnostics is null)
			{
				throw new CliUserException("AssetMetadataPlanUnreadable", "资产元数据计划缺少请求、暂存输入或诊断信息。 ");
			}
			return document;
		}
		catch (CliUserException) { throw; }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			throw new CliUserException("AssetMetadataPlanUnreadable", $"无法读取资产元数据计划：{exception.Message}");
		}
	}

	public static async ValueTask<CliAssetMetadataPlanValidationResult> ValidateAsync(
		CliAssetMetadataPlanDocument document,
		CliWorkspace workspace,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(workspace);
		var diagnostics = new List<CliAssetMetadataDiagnostic>();
		StoragePaths paths;
		string settingsPath;
		try
		{
			(paths, settingsPath) = EnsureManagerWorkspace(workspace);
		}
		catch (CliUserException exception)
		{
			diagnostics.Add(new CliAssetMetadataDiagnostic(exception.Code, "error", true, exception.Message));
			return new CliAssetMetadataPlanValidationResult(document, null, diagnostics);
		}

		if (!PathsEqual(paths.AppRootDirectory, document.Inputs.ManagerRoot)
			|| !PathsEqual(settingsPath, document.Inputs.SettingsPath))
		{
			diagnostics.Add(new CliAssetMetadataDiagnostic(
				"AssetMetadataTargetChanged", "error", true,
				"当前 --manager-root 与计划固定的管理器设置目标不一致；请重新生成计划。"));
			return new CliAssetMetadataPlanValidationResult(document, null, diagnostics);
		}

		var currentSettingsSha256 = await ComputeFileSha256Async(settingsPath, cancellationToken).ConfigureAwait(false);
		if (!string.Equals(document.Inputs.SettingsSha256, currentSettingsSha256, StringComparison.OrdinalIgnoreCase))
		{
			diagnostics.Add(new CliAssetMetadataDiagnostic(
				"AssetMetadataSettingsChanged", "error", true,
				"settings.json 自计划生成后已变化；为避免覆盖新的设置，请重新生成计划。", settingsPath));
		}

		if (document.Request.RepositoryFromSettings)
		{
			var currentRepositoryInput = ResolveRepositoryInput(workspace, repositoryArgument: null, out _);
			if (!string.Equals(document.Request.RepositoryInput, currentRepositoryInput, StringComparison.Ordinal))
			{
				diagnostics.Add(new CliAssetMetadataDiagnostic(
					"AssetMetadataRepositoryChanged", "error", true,
					"设置中的在线资产索引源已变化；请重新生成计划并审阅新的远端来源。", settingsPath));
			}
		}

		var currentDiagnostics = new List<CliAssetMetadataDiagnostic>();
		var currentSnapshot = await CaptureSnapshotAsync(paths, currentDiagnostics, cancellationToken).ConfigureAwait(false);
		var current = new CliAssetMetadataInspection(
			paths.AppRootDirectory,
			settingsPath,
			ResolveRepositoryInput(workspace, repositoryArgument: null, out _),
			workspace.Settings?.LastAssetMetadataCheckUtc,
			currentSnapshot,
			DistinctDiagnostics(currentDiagnostics));
		if (!string.Equals(document.Inputs.Current.Fingerprint, currentSnapshot.Fingerprint, StringComparison.OrdinalIgnoreCase))
		{
			diagnostics.Add(new CliAssetMetadataDiagnostic(
				"AssetMetadataLocalCacheChanged", "error", true,
				"本地资产元数据缓存自计划生成后已变化；请重新生成计划以避免覆盖较新的内容。", paths.ResourcesDirectory));
		}

		var stageDiagnostics = new List<CliAssetMetadataDiagnostic>();
		var stageSnapshot = await CaptureSnapshotAsync(new StoragePaths(document.Request.StagingRootDirectory), stageDiagnostics, cancellationToken).ConfigureAwait(false);
		if (!stageSnapshot.IsCompleteAndVerified
			|| !string.Equals(document.Staging.Snapshot.Fingerprint, stageSnapshot.Fingerprint, StringComparison.OrdinalIgnoreCase))
		{
			diagnostics.Add(new CliAssetMetadataDiagnostic(
				"AssetMetadataStageChanged", "error", true,
				"计划旁的在线资产元数据暂存内容缺失、损坏或已变化；请重新生成计划。", document.Request.StagingRootDirectory));
		}
		if (!string.Equals(document.Staging.RepositoryRawBaseUrl, stageSnapshot.ManifestSource, StringComparison.Ordinal))
		{
			diagnostics.Add(new CliAssetMetadataDiagnostic(
				"AssetMetadataStageSourceChanged", "error", true,
				"暂存 manifest 声明的在线资产索引源与计划不一致；请重新生成计划。", document.Request.StagingRootDirectory));
		}

		var recomputedFingerprint = ComputeInputFingerprint(document.Request, document.Inputs, document.Staging);
		if (!string.Equals(document.InputFingerprint, recomputedFingerprint, StringComparison.OrdinalIgnoreCase))
		{
			diagnostics.Add(new CliAssetMetadataDiagnostic(
				"AssetMetadataPlanTampered", "error", true,
				"计划的输入指纹与其请求、当前缓存快照或暂存内容不一致；请重新生成计划。"));
		}

		diagnostics.AddRange(current.Diagnostics.Where(diagnostic => diagnostic.Blocking));
		diagnostics.AddRange(stageDiagnostics.Where(diagnostic => diagnostic.Blocking));
		return new CliAssetMetadataPlanValidationResult(document, current, DistinctDiagnostics(diagnostics));
	}

	internal static IReadOnlyList<string> GetRequiredFileNames() => RequiredFileNames;

	internal static string GetStagingResourcesDirectory(CliAssetMetadataPlanDocument document)
		=> new StoragePaths(document.Request.StagingRootDirectory).ResourcesDirectory;

	internal static async ValueTask<CliAssetMetadataSnapshot> CaptureSnapshotAsync(
		StoragePaths paths,
		ICollection<CliAssetMetadataDiagnostic> diagnostics,
		CancellationToken cancellationToken = default)
	{
		var files = new List<CliAssetMetadataFile>(RequiredFileNames.Length);
		foreach (var fileName in RequiredFileNames)
		{
			var path = Path.Combine(paths.ResourcesDirectory, fileName);
			if (!File.Exists(path))
			{
				files.Add(new CliAssetMetadataFile(fileName, false, 0, null));
				continue;
			}
			try
			{
				var info = new FileInfo(path);
				files.Add(new CliAssetMetadataFile(fileName, true, info.Length, await ComputeFileSha256Async(path, cancellationToken).ConfigureAwait(false)));
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				diagnostics.Add(new CliAssetMetadataDiagnostic("AssetMetadataFileUnreadable", "error", true, $"无法读取资产元数据文件：{exception.Message}", path));
				files.Add(new CliAssetMetadataFile(fileName, true, 0, null));
			}
		}

		var manifestPath = Path.Combine(paths.ResourcesDirectory, "asset-metadata-manifest.json");
		AssetMetadataManifest? manifest = null;
		if (File.Exists(manifestPath))
		{
			try
			{
				manifest = JsonSerializer.Deserialize<AssetMetadataManifest>(
					await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false),
					CliOutput.JsonOptions);
				if (manifest is null || string.IsNullOrWhiteSpace(manifest.Source) || manifest.Files.Count == 0)
				{
					diagnostics.Add(new CliAssetMetadataDiagnostic("AssetMetadataManifestInvalid", "warning", false, "asset-metadata-manifest.json 为空或缺少来源/文件摘要。", manifestPath));
					manifest = null;
				}
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
			{
				diagnostics.Add(new CliAssetMetadataDiagnostic("AssetMetadataManifestUnreadable", "warning", false, $"无法读取 asset-metadata-manifest.json：{exception.Message}", manifestPath));
			}
		}

		var contentFiles = files.Where(file => !string.Equals(file.FileName, "asset-metadata-manifest.json", StringComparison.OrdinalIgnoreCase)).ToArray();
		var manifestMatchesFiles = manifest is not null && contentFiles.All(file =>
			file.Exists
			&& manifest.Files.TryGetValue(file.FileName, out var expected)
			&& expected.Bytes == file.Length
			&& string.Equals(expected.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase));
		if (manifest is not null && !manifestMatchesFiles)
		{
			diagnostics.Add(new CliAssetMetadataDiagnostic(
				"AssetMetadataManifestMismatch", "warning", false,
				"asset-metadata-manifest.json 与当前 hashlist 文件摘要不一致。", manifestPath));
		}

		var fingerprint = ComputeSnapshotFingerprint(files, manifest);
		return new CliAssetMetadataSnapshot(
			paths.ResourcesDirectory,
			manifest is not null,
			manifestMatchesFiles,
			manifest?.Source,
			manifest?.UpdatedAtUtc,
			files,
			fingerprint,
			DistinctDiagnostics(diagnostics));
	}

	private static async ValueTask<AssetMetadataSyncResult> SyncToStagingAsync(
		StoragePaths stagingPaths,
		string repositoryInput,
		Func<StoragePaths, IAssetMetadataSyncService>? stagingServiceFactory,
		CancellationToken cancellationToken)
	{
		if (stagingServiceFactory is not null)
			return await stagingServiceFactory(stagingPaths).SyncAsync(repositoryInput, cancellationToken).ConfigureAwait(false);

		using var client = new HttpClient();
		return await new GitHubAssetMetadataSyncService(client, stagingPaths)
			.SyncAsync(repositoryInput, cancellationToken)
			.ConfigureAwait(false);
	}

	private static (StoragePaths Paths, string SettingsPath) EnsureManagerWorkspace(CliWorkspace workspace)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		if (string.IsNullOrWhiteSpace(workspace.ManagerRoot))
			throw new CliUserException("AssetMetadataManagerRootRequired", "assets metadata 命令需要 --manager-root <管理器目录>，以定位设置与资产元数据缓存。 ");
		if (!Directory.Exists(workspace.ManagerRoot))
			throw new CliUserException("ManagerRootNotFound", $"管理器目录不存在：{workspace.ManagerRoot}");
		if (string.IsNullOrWhiteSpace(workspace.SettingsPath) || !File.Exists(workspace.SettingsPath) || workspace.Settings is null)
			throw new CliUserException("ManagerSettingsRequired", "assets metadata 命令要求 --manager-root 中存在可读取的 settings.json。 ");
		return (new StoragePaths(workspace.ManagerRoot), workspace.SettingsPath);
	}

	private static string ResolveRepositoryInput(CliWorkspace workspace, string? repositoryArgument, out bool repositoryFromSettings)
	{
		repositoryFromSettings = string.IsNullOrWhiteSpace(repositoryArgument);
		var value = repositoryFromSettings ? workspace.Settings?.AssetMetadataRepository : repositoryArgument;
		return string.IsNullOrWhiteSpace(value)
			? GitHubAssetMetadataSyncService.DefaultRepositoryRawBaseUrl
			: value.Trim();
	}

	private static void EnsurePlanOutputAvailable(string planPath, string stagingRoot, bool overwrite)
	{
		if (File.Exists(planPath) && !overwrite)
			throw new CliUserException("OutputAlreadyExists", $"输出文件已存在：{planPath}。如需覆盖，请显式传入 --overwrite。");
		if (File.Exists(stagingRoot))
			throw new CliUserException("AssetMetadataStagePathInvalid", $"计划暂存路径被普通文件占用：{stagingRoot}");
		if (!Directory.Exists(stagingRoot)) return;
		if (!overwrite)
			throw new CliUserException("AssetMetadataStageAlreadyExists", $"计划暂存目录已存在：{stagingRoot}。如需覆盖，请显式传入 --overwrite。");
		TryDeleteDirectory(stagingRoot);
	}

	private static string GetStagingRootDirectory(string planPath) => planPath + ".assets";

	private static string NormalizeFilePath(string path, string code)
	{
		try { return Path.GetFullPath(path); }
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			throw new CliUserException(code, $"路径无效：{path}");
		}
	}

	private static async ValueTask<string> ComputeFileSha256Async(string path, CancellationToken cancellationToken)
	{
		await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, FileOptions.Asynchronous | FileOptions.SequentialScan);
		return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
	}

	private static string ComputeSnapshotFingerprint(IReadOnlyList<CliAssetMetadataFile> files, AssetMetadataManifest? manifest)
	{
		var payload = CliOutput.ToJson(new
		{
			files = files.OrderBy(file => file.FileName, StringComparer.OrdinalIgnoreCase)
				.Select(file => new { file.FileName, file.Exists, file.Length, file.Sha256 }).ToArray(),
			manifest = manifest is null ? null : new
			{
				manifest.Source,
				manifest.UpdatedAtUtc,
				files = manifest.Files.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
					.Select(pair => new { fileName = pair.Key, pair.Value.Bytes, pair.Value.Sha256 }).ToArray(),
			},
		});
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
	}

	private static string ComputeInputFingerprint(
		CliAssetMetadataRequest request,
		CliAssetMetadataInputs inputs,
		CliAssetMetadataStaging staging)
	{
		var payload = CliOutput.ToJson(new
		{
			request.RepositoryInput,
			request.RepositoryFromSettings,
			request.StagingRootDirectory,
			inputs.ManagerRoot,
			inputs.SettingsPath,
			inputs.SettingsSha256,
			local = inputs.Current.Fingerprint,
			staging.RepositoryRawBaseUrl,
			staged = staging.Snapshot.Fingerprint,
		});
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
	}

	private static bool PathsEqual(string? left, string? right)
	{
		if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
			return string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right);
		try
		{
			return string.Equals(
				Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
				Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
				StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
	}

	private static void TryDeleteDirectory(string path)
	{
		try
		{
			if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			throw new CliUserException("AssetMetadataStageCleanupFailed", $"无法清理计划暂存目录：{path}（{exception.Message}）");
		}
	}

	private static IReadOnlyList<CliAssetMetadataDiagnostic> DistinctDiagnostics(IEnumerable<CliAssetMetadataDiagnostic> diagnostics)
		=> diagnostics.GroupBy(diagnostic => new { diagnostic.Code, diagnostic.Severity, diagnostic.Blocking, diagnostic.Message, diagnostic.Path })
			.Select(group => group.First())
			.ToArray();
}
