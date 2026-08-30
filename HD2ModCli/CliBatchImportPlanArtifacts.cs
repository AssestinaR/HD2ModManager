using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HD2ModCli;

/// <summary>
/// A reviewed collection of ordinary import plans. Batch import is deliberately
/// sequential, not pseudo-atomic: every completed source remains auditable if
/// a later source or post-commit Profile addition fails.
/// </summary>
public sealed record CliBatchImportPlanDocument(
	string Schema,
	int Version,
	DateTimeOffset CreatedUtc,
	string PlanFingerprint,
	IReadOnlyList<CliImportPlanDocument> Imports)
{
	public const string CurrentSchema = "hd2mod.batch-import-plan";
	public const int CurrentVersion = 1;

	public CliImportProfileTarget? AddToProfile
		=> Imports.Select(document => document.AddToProfile).FirstOrDefault(target => target is not null);
}

internal sealed record CliBatchImportPlanBuildResult(CliBatchImportPlanDocument Document);

internal sealed record CliBatchImportSourceValidation(
	int Index,
	CliImportPlanDocument Document,
	CliImportValidationResult Validation)
{
	public bool IsValid => Validation.IsValid;
}

internal sealed record CliBatchImportPlanValidationResult(
	CliBatchImportPlanDocument Document,
	IReadOnlyList<CliBatchImportSourceValidation> Sources)
{
	public bool IsValid => Sources.Count == Document.Imports.Count && Sources.All(source => source.IsValid);

	public CliImportTarget? Target
		=> Sources.Select(source => source.Validation.Target).FirstOrDefault();
}

internal static class CliBatchImportPlanArtifacts
{
	public static async ValueTask<CliBatchImportPlanBuildResult> BuildAsync(
		IReadOnlyList<string> requestedSources,
		CliImportProfileTarget? addToProfile,
		CancellationToken cancellationToken = default)
	{
		if (requestedSources is null || requestedSources.Count == 0)
			throw new CliUserException("BatchImportSourceRequired", "import batch 需要至少一个文件夹、压缩包或其中任意文件路径。 ");

		var imports = new List<CliImportPlanDocument>(requestedSources.Count);
		var knownSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var requestedSource in requestedSources)
		{
			var inspection = await CliImportPlanArtifacts.InspectAsync(requestedSource, cancellationToken).ConfigureAwait(false);
			var identity = CreateSourceIdentity(inspection.Source);
			if (!knownSources.Add(identity))
			{
				throw new CliUserException(
					"BatchImportSourceDuplicate",
					$"同一个导入来源在批量计划中重复出现：{inspection.Source.RequestedPath}");
			}
			imports.Add(CliImportPlanArtifacts.CreateDocument(inspection, addToProfile));
		}

		return new CliBatchImportPlanBuildResult(CreateDocument(imports));
	}

	public static async ValueTask<CliBatchImportPlanDocument> ReadDocumentAsync(
		string path,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(path))
			throw new CliUserException("BatchImportPlanRequired", "请通过 --plan <批量导入计划.json> 提供已导出的批量导入计划。 ");
		var fullPath = CliOutput.NormalizeDirectoryPath(path, "BatchImportPlanPathInvalid");
		if (!File.Exists(fullPath))
			throw new CliUserException("BatchImportPlanNotFound", $"批量导入计划不存在：{fullPath}");
		try
		{
			var document = JsonSerializer.Deserialize<CliBatchImportPlanDocument>(
				await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false),
				CliOutput.JsonOptions);
			if (document is null)
				throw new CliUserException("BatchImportPlanUnreadable", "批量导入计划为空或无法解析。 ");
			ValidateDocumentShape(document);
			var actualFingerprint = ComputePlanFingerprint(document.Imports);
			if (!string.Equals(document.PlanFingerprint, actualFingerprint, StringComparison.OrdinalIgnoreCase))
			{
				throw new CliUserException(
					"BatchImportPlanIntegrityMismatch",
					"批量导入计划内容在导出后被修改，指纹不再匹配；请重新生成计划并审阅。 ");
			}
			return document;
		}
		catch (CliUserException) { throw; }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			throw new CliUserException("BatchImportPlanUnreadable", $"无法读取批量导入计划：{exception.Message}");
		}
	}

	public static async ValueTask<CliBatchImportPlanValidationResult> ValidateAsync(
		CliBatchImportPlanDocument document,
		CliWorkspace workspace,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(workspace);
		var sources = new List<CliBatchImportSourceValidation>(document.Imports.Count);
		for (var index = 0; index < document.Imports.Count; index++)
		{
			var import = document.Imports[index];
			var validation = await CliImportValidator.ValidateAsync(import, workspace, cancellationToken).ConfigureAwait(false);
			sources.Add(new CliBatchImportSourceValidation(index, import, validation));
		}
		return new CliBatchImportPlanValidationResult(document, sources);
	}

	private static CliBatchImportPlanDocument CreateDocument(IReadOnlyList<CliImportPlanDocument> imports)
		=> new(
			CliBatchImportPlanDocument.CurrentSchema,
			CliBatchImportPlanDocument.CurrentVersion,
			DateTimeOffset.UtcNow,
			ComputePlanFingerprint(imports),
			imports.ToArray());

	private static void ValidateDocumentShape(CliBatchImportPlanDocument document)
	{
		if (!string.Equals(document.Schema, CliBatchImportPlanDocument.CurrentSchema, StringComparison.Ordinal)
			|| document.Version != CliBatchImportPlanDocument.CurrentVersion)
		{
			throw new CliUserException(
				"BatchImportPlanVersionUnsupported",
				$"批量导入计划版本不受支持。需要 {CliBatchImportPlanDocument.CurrentSchema} v{CliBatchImportPlanDocument.CurrentVersion}；请重新生成计划。 ");
		}
		if (string.IsNullOrWhiteSpace(document.PlanFingerprint) || document.Imports is null || document.Imports.Count == 0)
			throw new CliUserException("BatchImportPlanUnreadable", "批量导入计划缺少来源或完整性指纹。 ");

		var knownSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		CliImportProfileTarget? sharedProfile = null;
		var profileTargetSeen = false;
		foreach (var import in document.Imports)
		{
			ValidateImportDocumentShape(import);
			if (!knownSources.Add(CreateSourceIdentity(import.Source)))
				throw new CliUserException("BatchImportPlanUnreadable", "批量导入计划包含重复来源。 ");
			if (!profileTargetSeen)
			{
				sharedProfile = import.AddToProfile;
				profileTargetSeen = true;
			}
			else if (!ProfileTargetsEqual(sharedProfile, import.AddToProfile))
			{
				throw new CliUserException("BatchImportPlanUnreadable", "批量导入计划中的来源没有固定到同一个 Profile 目标。 ");
			}
		}
	}

	private static void ValidateImportDocumentShape(CliImportPlanDocument import)
	{
		if (!string.Equals(import.Schema, CliImportPlanDocument.CurrentSchema, StringComparison.Ordinal)
			|| import.Version != CliImportPlanDocument.CurrentVersion
			|| import.Source is null
			|| string.IsNullOrWhiteSpace(import.Source.RequestedPath)
			|| string.IsNullOrWhiteSpace(import.Source.EffectivePath)
			|| import.SourceFingerprint is null
			|| import.ImportFingerprint is null
			|| import.Import is null)
		{
			throw new CliUserException("BatchImportPlanUnreadable", "批量导入计划包含无效的单项导入计划。 ");
		}
		if (import.AddToProfile is not null
			&& (!Guid.TryParse(import.AddToProfile.ProfileId, out _)
				|| string.IsNullOrWhiteSpace(import.AddToProfile.ProfileName)
				|| import.AddToProfile.ProfileRevision < 0
				|| string.IsNullOrWhiteSpace(import.AddToProfile.LibraryRoot)))
		{
			throw new CliUserException("BatchImportPlanUnreadable", "批量导入计划包含无效的 Profile 目标。 ");
		}
	}

	private static string ComputePlanFingerprint(IReadOnlyList<CliImportPlanDocument> imports)
	{
		var payload = CliOutput.ToJson(new
		{
			imports = imports.Select(import => new
			{
				import.Schema,
				import.Version,
				import.Source,
				import.SourceFingerprint,
				import.ImportFingerprint,
				importTree = CliProjections.CreateImportPlanProjection(import.Import),
				import.AddToProfile,
			}).ToArray(),
		});
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
	}

	private static string CreateSourceIdentity(CliImportSource source)
	{
		try
		{
			return source.Kind.Trim().ToLowerInvariant() + ":" + Path.GetFullPath(source.EffectivePath)
				.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			throw new CliUserException("BatchImportPlanUnreadable", $"批量导入计划包含无效来源路径：{source.EffectivePath}");
		}
	}

	private static bool ProfileTargetsEqual(CliImportProfileTarget? left, CliImportProfileTarget? right)
	{
		if (left is null || right is null) return left is null && right is null;
		return string.Equals(left.ProfileId, right.ProfileId, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(left.ProfileName, right.ProfileName, StringComparison.Ordinal)
			&& left.ProfileRevision == right.ProfileRevision
			&& PathsEqual(left.LibraryRoot, right.LibraryRoot);
	}

	private static bool PathsEqual(string left, string right)
	{
		try
		{
			return string.Equals(
				Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
				Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
				StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return false;
		}
	}
}
