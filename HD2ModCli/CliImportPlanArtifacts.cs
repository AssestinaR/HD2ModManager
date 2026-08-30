using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

/// <summary>
/// The persisted declaration used by the CLI's import plan → validate → apply
/// workflow. It is an audit/change-detection artifact, not a signature and not
/// an authority to bypass the explicit --confirm gate.
/// </summary>
public sealed record CliImportPlanDocument(
	string Schema,
	int Version,
	DateTimeOffset CreatedUtc,
	CliImportSource Source,
	CliImportFingerprint SourceFingerprint,
	CliImportFingerprint ImportFingerprint,
	ImportPlan Import,
	CliImportProfileTarget? AddToProfile)
{
	public const string CurrentSchema = "hd2mod.import-plan";
	public const int CurrentVersion = 4;
}

public sealed record CliImportSource(
	string RequestedPath,
	string EffectivePath,
	string Kind);

public sealed record CliImportFingerprint(
	string Algorithm,
	string Value,
	int FileCount,
	long TotalBytes);

/// <summary>
/// A profile target frozen into an import plan. The CLI deliberately records
/// identity and revision rather than a display selector so a later apply can
/// reject profile drift instead of adding content to a different profile.
/// </summary>
public sealed record CliImportProfileTarget(
	string ProfileId,
	string ProfileName,
	long ProfileRevision);

public sealed record CliImportInspection(
	CliImportSource Source,
	ImportPlan Import,
	CliImportFingerprint SourceFingerprint,
	CliImportFingerprint ImportFingerprint);

public static class CliImportPlanArtifacts
{
	private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase) { ".zip", ".rar", ".7z" };
	private const string DirectoryKind = "directory";
	private const string ArchiveKind = "archive";
	private const string FileParentDirectoryKind = "file-parent-directory";

	public static async ValueTask<CliImportInspection> InspectAsync(string sourcePath, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(sourcePath))
			throw new CliUserException("ImportSourceRequired", "请提供待导入的文件夹、压缩包或其中任意文件路径。");

		var requestedPath = CliOutput.NormalizeDirectoryPath(sourcePath, "ImportSourcePathInvalid");
		CliImportSource source;
		if (Directory.Exists(requestedPath))
		{
			source = new CliImportSource(requestedPath, requestedPath, DirectoryKind);
		}
		else if (File.Exists(requestedPath) && ArchiveExtensions.Contains(Path.GetExtension(requestedPath)))
		{
			source = new CliImportSource(requestedPath, requestedPath, ArchiveKind);
		}
		else if (File.Exists(requestedPath))
		{
			var parent = Path.GetDirectoryName(requestedPath);
			if (string.IsNullOrWhiteSpace(parent))
				throw new CliUserException("ImportSourcePathInvalid", $"无法定位来源文件的目录：{requestedPath}");
			source = new CliImportSource(requestedPath, parent, FileParentDirectoryKind);
		}
		else
		{
			throw new CliUserException("ImportSourceNotFound", $"待导入来源不存在：{requestedPath}");
		}

		try
		{
			var before = await ComputeSourceFingerprintAsync(source, cancellationToken).ConfigureAwait(false);
			var planner = CoreServices.CreateModImportPlanner();
			var plan = string.Equals(source.Kind, ArchiveKind, StringComparison.Ordinal)
				? await planner.PlanArchiveAsync(source.EffectivePath, cancellationToken).ConfigureAwait(false)
				: await planner.PlanFolderAsync(source.EffectivePath, cancellationToken).ConfigureAwait(false);
			var after = await ComputeSourceFingerprintAsync(source, cancellationToken).ConfigureAwait(false);
			if (!FingerprintsEqual(before, after))
				throw new CliUserException("ImportSourceChangedDuringInspection", "来源在扫描过程中发生变化；请等待下载或解压完成后重新生成计划。");
			return new CliImportInspection(source, plan, after, ComputeImportFingerprint(plan));
		}
		catch (CliUserException) { throw; }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or DirectoryNotFoundException or FileNotFoundException)
		{
			throw new CliUserException("ImportInspectionFailed", $"无法完成导入前扫描：{exception.Message}");
		}
	}

	public static CliImportPlanDocument CreateDocument(
		CliImportInspection inspection,
		CliImportProfileTarget? addToProfile = null)
	{
		ArgumentNullException.ThrowIfNull(inspection);
		return new CliImportPlanDocument(
			CliImportPlanDocument.CurrentSchema,
			CliImportPlanDocument.CurrentVersion,
			DateTimeOffset.UtcNow,
			inspection.Source,
			inspection.SourceFingerprint,
			inspection.ImportFingerprint,
			inspection.Import,
			addToProfile);
	}

	public static async ValueTask<CliImportPlanDocument> ReadDocumentAsync(string path, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(path))
			throw new CliUserException("ImportPlanRequired", "请通过 --plan <计划文件.json> 提供已导出的导入计划。");
		var fullPath = CliOutput.NormalizeDirectoryPath(path, "ImportPlanPathInvalid");
		if (!File.Exists(fullPath))
			throw new CliUserException("ImportPlanNotFound", $"导入计划文件不存在：{fullPath}");
		try
		{
			var json = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
			var document = JsonSerializer.Deserialize<CliImportPlanDocument>(json, CliOutput.JsonOptions);
			if (document is null)
				throw new CliUserException("ImportPlanUnreadable", "导入计划文件为空或无法解析。");
			if (!string.Equals(document.Schema, CliImportPlanDocument.CurrentSchema, StringComparison.Ordinal)
				|| document.Version != CliImportPlanDocument.CurrentVersion)
			{
				throw new CliUserException(
					"ImportPlanVersionUnsupported",
					$"导入计划版本不受支持。需要 {CliImportPlanDocument.CurrentSchema} v{CliImportPlanDocument.CurrentVersion}；请重新运行 import plan。");
			}
			if (document.Source is null
				|| string.IsNullOrWhiteSpace(document.Source.RequestedPath)
				|| string.IsNullOrWhiteSpace(document.Source.EffectivePath)
				|| document.SourceFingerprint is null
				|| document.ImportFingerprint is null
				|| document.Import is null)
			{
				throw new CliUserException("ImportPlanUnreadable", "导入计划缺少来源、指纹或导入树信息。");
			}
			if (document.AddToProfile is not null
				&& (!Guid.TryParse(document.AddToProfile.ProfileId, out _)
					|| string.IsNullOrWhiteSpace(document.AddToProfile.ProfileName)
					|| document.AddToProfile.ProfileRevision < 0))
			{
				throw new CliUserException("ImportPlanUnreadable", "导入计划中的目标配置文件无效。");
			}
			return document;
		}
		catch (CliUserException) { throw; }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			throw new CliUserException("ImportPlanUnreadable", $"无法读取导入计划：{exception.Message}");
		}
	}

	public static bool FingerprintsEqual(CliImportFingerprint left, CliImportFingerprint right)
		=> string.Equals(left.Algorithm, right.Algorithm, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(left.Value, right.Value, StringComparison.OrdinalIgnoreCase)
			&& left.FileCount == right.FileCount
			&& left.TotalBytes == right.TotalBytes;

	private static CliImportFingerprint ComputeImportFingerprint(ImportPlan plan)
	{
		var bytes = Encoding.UTF8.GetBytes(CliOutput.ToJson(CliProjections.CreateImportPlanProjection(plan)));
		return new CliImportFingerprint("sha256-plan-projection-v1", Convert.ToHexString(SHA256.HashData(bytes)), 0, bytes.LongLength);
	}

	private static async ValueTask<CliImportFingerprint> ComputeSourceFingerprintAsync(CliImportSource source, CancellationToken cancellationToken)
	{
		if (string.Equals(source.Kind, ArchiveKind, StringComparison.Ordinal))
		{
			await using var stream = new FileStream(source.EffectivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, FileOptions.SequentialScan);
			var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
			return new CliImportFingerprint("sha256-file-v1", Convert.ToHexString(hash), 1, stream.Length);
		}

		using var hashBuilder = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		var fileCount = 0;
		long totalBytes = 0;
		foreach (var path in EnumerateFiles(source.EffectivePath, cancellationToken))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var info = new FileInfo(path);
			var relativePath = Path.GetRelativePath(source.EffectivePath, path).Replace('\\', '/');
			AppendField(hashBuilder, relativePath);
			// A directory plan is a safety gate, so inventory metadata alone is not
			// enough: a file can be replaced while retaining its length and timestamp.
			// Stream one content hash per file into the tree hash instead of retaining
			// any import bytes in memory.
			await using var stream = new FileStream(
				path,
				FileMode.Open,
				FileAccess.Read,
				FileShare.Read,
				bufferSize: 1024 * 128,
				FileOptions.SequentialScan);
			var contentHash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
			hashBuilder.AppendData(contentHash);
			fileCount++;
			totalBytes = checked(totalBytes + info.Length);
		}
		return new CliImportFingerprint("sha256-directory-content-v1", Convert.ToHexString(hashBuilder.GetHashAndReset()), fileCount, totalBytes);
	}

	private static IEnumerable<string> EnumerateFiles(string root, CancellationToken cancellationToken)
	{
		var pending = new Stack<string>();
		pending.Push(root);
		while (pending.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var directory = pending.Pop();
			foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
				yield return file;
			var directories = Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly)
				.OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
				.ToArray();
			foreach (var child in directories)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
					pending.Push(child);
			}
		}
	}

	private static void AppendField(IncrementalHash hash, string value)
	{
		hash.AppendData(Encoding.UTF8.GetBytes(value));
		hash.AppendData([0]);
	}
}
