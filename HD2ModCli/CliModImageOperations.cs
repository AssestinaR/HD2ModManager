using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

internal sealed record CliModImageInspection(
	ModNode Node,
	string Directory,
	string? ResolvedImagePath,
	IReadOnlyList<string> CanonicalImagePaths);

internal sealed record CliModImageMutationResult(
	ModNode Node,
	string Operation,
	string? SourcePath,
	string? DestinationPath,
	IReadOnlyList<string> RemovedPaths,
	string? ResolvedImagePath);

/// <summary>
/// Performs the small, convention-based image operations shared by the WPF
/// Mod details page and headless callers. No image path is persisted in
/// library.json: the manager always resolves icon.* directly from the Mod
/// directory, so an image mutation deliberately leaves profile and Patch state
/// untouched.
/// </summary>
internal static class CliModImageOperations
{
	public static CliModImageInspection Inspect(CliLibraryState state, CliWorkspace workspace, string selector)
	{
		ArgumentNullException.ThrowIfNull(state);
		ArgumentNullException.ThrowIfNull(workspace);
		var node = CliProjections.ResolveMod(state.Snapshot.Nodes, selector);
		var directory = ResolveNodeDirectory(workspace.LibraryRoot, node);
		return new CliModImageInspection(
			node,
			directory,
			ModIconLocator.TryResolve(directory),
			ModIconLocator.CanonicalFileNames
				.Select(fileName => Path.Combine(directory, fileName))
				.Where(File.Exists)
				.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
				.ToArray());
	}

	public static async ValueTask<CliModImageMutationResult> SetAsync(
		CliLibraryWriteSession session,
		string selector,
		string sourcePath,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(sourcePath))
			throw new CliUserException("ModImageSourceRequired", "mod image set 需要提供一个本地 PNG、JPG、JPEG、WEBP 或 BMP 图像文件。 ");
		var source = CliOutput.NormalizeDirectoryPath(sourcePath, "ModImageSourcePathInvalid");
		if (!File.Exists(source))
			throw new CliUserException("ModImageSourceNotFound", $"图像文件不存在：{source}");
		var extension = Path.GetExtension(source).ToLowerInvariant();
		if (!ModIconLocator.IsSupportedExtension(extension))
		{
			throw new CliUserException(
				"ModImageFormatUnsupported",
				"Mod 图像仅支持 PNG、JPG、JPEG、WEBP 或 BMP；CLI 不会尝试解码或转换其他格式。 ");
		}

		var snapshot = await RequireSnapshotAsync(session, cancellationToken).ConfigureAwait(false);
		var node = CliProjections.ResolveMod(snapshot.Nodes, selector);
		var directory = ResolveNodeDirectory(session.Paths.ModsDirectory, node);
		if (!Directory.Exists(directory))
			throw new CliUserException("ModImageDirectoryMissing", $"Mod 目录不存在，无法写入图像：{directory}");
		EnsureDirectoryIsNotReparsePoint(directory);
		var destination = Path.Combine(directory, "icon" + extension);
		var temporary = Path.Combine(directory, ".hd2mod-cli-icon-" + Guid.NewGuid().ToString("N") + ".tmp");
		var staging = Path.Combine(directory, ".hd2mod-cli-icon-backup-" + Guid.NewGuid().ToString("N"));
		var moved = new List<(string Original, string Backup)>();
		try
		{
			await session.VerifyReadyForMutationAsync(cancellationToken).ConfigureAwait(false);
			// Stage a private copy first. This is important when source itself is an
			// existing icon in the target Mod directory.
			await CopyFileAsync(source, temporary, cancellationToken).ConfigureAwait(false);
			Directory.CreateDirectory(staging);
			foreach (var iconPath in EnumerateCanonicalIconPaths(directory))
			{
				var backup = Path.Combine(staging, Path.GetFileName(iconPath));
				File.Move(iconPath, backup, overwrite: false);
				moved.Add((iconPath, backup));
			}
			File.Move(temporary, destination, overwrite: false);
			Directory.Delete(staging, recursive: true);
			await session.VerifyReadyForMutationAsync(cancellationToken).ConfigureAwait(false);
			return new CliModImageMutationResult(
				node,
				"set",
				source,
				destination,
				moved.Select(item => item.Original).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
				ModIconLocator.TryResolve(directory));
		}
		catch
		{
			TryDeleteFile(temporary);
			TryDeleteFile(destination);
			foreach (var (original, backup) in moved.OrderByDescending(item => item.Original, StringComparer.OrdinalIgnoreCase))
			{
				try
				{
					if (File.Exists(backup) && !File.Exists(original)) File.Move(backup, original, overwrite: false);
				}
				catch { }
			}
			TryDeleteDirectory(staging);
			throw;
		}
	}

	public static async ValueTask<CliModImageMutationResult> ClearAsync(
		CliLibraryWriteSession session,
		string selector,
		CancellationToken cancellationToken = default)
	{
		var snapshot = await RequireSnapshotAsync(session, cancellationToken).ConfigureAwait(false);
		var node = CliProjections.ResolveMod(snapshot.Nodes, selector);
		var directory = ResolveNodeDirectory(session.Paths.ModsDirectory, node);
		if (!Directory.Exists(directory))
			throw new CliUserException("ModImageDirectoryMissing", $"Mod 目录不存在，无法清除图像：{directory}");
		EnsureDirectoryIsNotReparsePoint(directory);
		var staging = Path.Combine(directory, ".hd2mod-cli-icon-backup-" + Guid.NewGuid().ToString("N"));
		var moved = new List<(string Original, string Backup)>();
		try
		{
			await session.VerifyReadyForMutationAsync(cancellationToken).ConfigureAwait(false);
			Directory.CreateDirectory(staging);
			foreach (var iconPath in EnumerateCanonicalIconPaths(directory))
			{
				var backup = Path.Combine(staging, Path.GetFileName(iconPath));
				File.Move(iconPath, backup, overwrite: false);
				moved.Add((iconPath, backup));
			}
			Directory.Delete(staging, recursive: true);
			await session.VerifyReadyForMutationAsync(cancellationToken).ConfigureAwait(false);
			return new CliModImageMutationResult(
				node,
				"clear",
				null,
				null,
				moved.Select(item => item.Original).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
				ModIconLocator.TryResolve(directory));
		}
		catch
		{
			foreach (var (original, backup) in moved.OrderByDescending(item => item.Original, StringComparer.OrdinalIgnoreCase))
			{
				try
				{
					if (File.Exists(backup) && !File.Exists(original)) File.Move(backup, original, overwrite: false);
				}
				catch { }
			}
			TryDeleteDirectory(staging);
			throw;
		}
	}

	private static async ValueTask<LibrarySnapshot> RequireSnapshotAsync(CliLibraryWriteSession session, CancellationToken cancellationToken)
		=> await session.ReloadSnapshotAsync(cancellationToken).ConfigureAwait(false)
			?? throw new CliUserException("LibrarySnapshotMissing", "该操作只能修改已有的 Mod 库。 ");

	private static IEnumerable<string> EnumerateCanonicalIconPaths(string directory)
		=> ModIconLocator.CanonicalFileNames
			.Select(fileName => Path.Combine(directory, fileName))
			.Where(File.Exists)
			.OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

	private static string ResolveNodeDirectory(string? libraryRoot, ModNode node)
	{
		if (string.IsNullOrWhiteSpace(libraryRoot))
			throw new CliUserException("LibraryNotConfigured", "未能确定 Mod 库目录。 ");
		try
		{
			var root = Path.GetFullPath(libraryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			var directory = Path.GetFullPath(Path.Combine(root, node.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
			if (!directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
				throw new CliUserException("ModImagePathEscapesLibrary", "Mod 记录的目录超出了当前 Mod 库；CLI 已拒绝写入。 ");
			return directory;
		}
		catch (CliUserException) { throw; }
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			throw new CliUserException("ModImageDirectoryInvalid", $"Mod 图像目标目录无效：{exception.Message}");
		}
	}

	private static void EnsureDirectoryIsNotReparsePoint(string directory)
	{
		try
		{
			if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
				throw new CliUserException("ModImageDirectoryReparsePoint", "Mod 图像目录是链接或重解析点；CLI 已拒绝写入。 ");
		}
		catch (CliUserException) { throw; }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			throw new CliUserException("ModImageDirectoryUnreadable", $"无法安全检查 Mod 图像目录：{exception.Message}");
		}
	}

	private static async ValueTask CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
	{
		await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, FileOptions.SequentialScan);
		await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, FileOptions.SequentialScan);
		await input.CopyToAsync(output, 1024 * 128, cancellationToken).ConfigureAwait(false);
	}

	private static void TryDeleteFile(string path)
	{
		try { if (File.Exists(path)) File.Delete(path); }
		catch { }
	}

	private static void TryDeleteDirectory(string path)
	{
		try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
		catch { }
	}
}
