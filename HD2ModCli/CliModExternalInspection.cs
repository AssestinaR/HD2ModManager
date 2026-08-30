using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

/// <summary>
/// Direct, cache-free inspection of the external components that one
/// registered Mod is eligible to deploy. It deliberately reads the current
/// Mod directory instead of ModInformationCenter so an Agent can review the
/// exact ReShade/add-on contract before changing a Profile or GameData.
/// </summary>
public sealed record CliModExternalInspectionDocument(
	string Schema,
	int Version,
	DateTimeOffset InspectedUtc,
	CliModExternalInspectionSource Source,
	IReadOnlyList<CliModExternalInspectionFile> Files,
	IReadOnlyList<string> UnrecognizedExternalPaths,
	IReadOnlyList<CliModExternalInspectionConflict> Conflicts,
	IReadOnlyList<CliModExternalInspectionDiagnostic> Diagnostics)
{
	public const string CurrentSchema = "hd2mod.mod-external-inspection";
	public const int CurrentVersion = 1;

	public bool IsReady => !Diagnostics.Any(diagnostic => diagnostic.Blocking);
}

public sealed record CliModExternalInspectionSource(
	string NodeId,
	string Name,
	string Kind,
	string RelativePath,
	string Directory);

public sealed record CliModExternalInspectionFile(
	string SourceRelativePath,
	string Kind,
	string TargetRoot,
	string TargetRelativePath,
	long Length,
	string Sha256,
	DateTimeOffset LastWriteUtc);

public sealed record CliModExternalInspectionConflict(
	string ConflictKey,
	string TargetRoot,
	string TargetRelativePath,
	IReadOnlyList<string> SourceRelativePaths);

public sealed record CliModExternalInspectionDiagnostic(
	string Code,
	string Severity,
	bool Blocking,
	string Message,
	string? Path = null,
	string? Details = null);

internal static class CliModExternalInspection
{
	public static async ValueTask<CliModExternalInspectionDocument> BuildAsync(
		CliWorkspace workspace,
		string nodeSelector,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		if (string.IsNullOrWhiteSpace(nodeSelector))
			throw new CliUserException("ModExternalInspectionSourceRequired", "mod external inspect 需要一个 Mod GUID 或唯一 GUID 前缀。 ");

		var state = await workspace.LoadLibraryAsync(cancellationToken).ConfigureAwait(false);
		var node = CliProjections.ResolveMod(state.Snapshot.Nodes, nodeSelector);
		var sourceDirectory = ResolveNodeDirectory(workspace.LibraryRoot!, node);
		var source = new CliModExternalInspectionSource(
			CliProjections.FormatId(node.Id),
			node.Metadata.Name,
			node.Metadata.Kind.ToString().ToLowerInvariant(),
			node.RelativePath,
			sourceDirectory);
		var diagnostics = new List<CliModExternalInspectionDiagnostic>();

		if (!Directory.Exists(sourceDirectory))
		{
			diagnostics.Add(new CliModExternalInspectionDiagnostic(
				"ModExternalInspectionSourceDirectoryMissing", "error", true,
				"该 Mod 的已登记存储目录不存在；无法检查外部组件。", sourceDirectory));
			return CreateDocument(source, [], [], [], diagnostics);
		}

		ExternalInstallDetectionResult detection;
		try
		{
			if ((File.GetAttributes(sourceDirectory) & FileAttributes.ReparsePoint) != 0)
			{
				diagnostics.Add(new CliModExternalInspectionDiagnostic(
					"ModExternalInspectionSourceDirectoryReparsePoint", "error", true,
					"该 Mod 的存储目录是重解析点/链接；CLI 不会在未经确认的库边界外读取外部组件。", sourceDirectory));
				return CreateDocument(source, [], [], [], diagnostics);
			}

			detection = await new ExternalInstallFileFactsService()
				.DetectAsync(sourceDirectory, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or ArgumentException
			or NotSupportedException
			or PathTooLongException)
		{
			diagnostics.Add(new CliModExternalInspectionDiagnostic(
				"ModExternalInspectionFailed", "error", true,
				$"无法检查该 Mod 的外部组件：{exception.Message}", sourceDirectory, exception.ToString()));
			return CreateDocument(source, [], [], [], diagnostics);
		}

		var files = detection.Files
			.OrderBy(file => file.SourceRelativePath, StringComparer.OrdinalIgnoreCase)
			.Select(file => new CliModExternalInspectionFile(
				file.SourceRelativePath,
				file.Kind.ToString().ToLowerInvariant(),
				file.TargetRoot.ToString().ToLowerInvariant(),
				file.TargetRelativePath,
				file.Length,
				file.Sha256,
				file.LastWriteTimeUtc))
			.ToArray();
		var conflicts = detection.Conflicts
			.OrderBy(conflict => conflict.ConflictKey, StringComparer.OrdinalIgnoreCase)
			.Select(conflict => new CliModExternalInspectionConflict(
				conflict.ConflictKey,
				conflict.Files[0].TargetRoot.ToString().ToLowerInvariant(),
				conflict.Files[0].TargetRelativePath,
				conflict.Files.Select(file => file.SourceRelativePath).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray()))
			.ToArray();
		foreach (var conflict in conflicts)
		{
			diagnostics.Add(new CliModExternalInspectionDiagnostic(
				"ExternalInstallTargetConflict", "error", true,
				$"同一 Mod 中的多个外部文件映射到了同一目标：{conflict.TargetRoot}/{conflict.TargetRelativePath}。", conflict.ConflictKey));
		}

		return CreateDocument(source, files, detection.UnrecognizedExternalPaths, conflicts, diagnostics);
	}

	private static CliModExternalInspectionDocument CreateDocument(
		CliModExternalInspectionSource source,
		IReadOnlyList<CliModExternalInspectionFile> files,
		IReadOnlyList<string> unrecognizedExternalPaths,
		IReadOnlyList<CliModExternalInspectionConflict> conflicts,
		IEnumerable<CliModExternalInspectionDiagnostic> diagnostics)
		=> new(
			CliModExternalInspectionDocument.CurrentSchema,
			CliModExternalInspectionDocument.CurrentVersion,
			DateTimeOffset.UtcNow,
			source,
			files,
			unrecognizedExternalPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
			conflicts,
			diagnostics
				.GroupBy(item => $"{item.Code}\u001f{item.Message}\u001f{item.Path}", StringComparer.Ordinal)
				.Select(group => group.OrderByDescending(item => item.Blocking).First())
				.OrderByDescending(item => item.Blocking)
				.ThenBy(item => item.Code, StringComparer.Ordinal)
				.ToArray());

	private static string ResolveNodeDirectory(string libraryRoot, ModNode node)
	{
		var root = Path.GetFullPath(libraryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		var directory = Path.GetFullPath(Path.Combine(root, node.RelativePath));
		if (string.Equals(directory, root, StringComparison.OrdinalIgnoreCase)
			|| (!directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
				&& !directory.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
		{
			throw new CliUserException("ModExternalInspectionSourcePathInvalid", "当前 Mod 的存储路径不在目标 Mod 库内。 ");
		}
		return directory;
	}
}
