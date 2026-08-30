using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// 作用：纯只读地验证部署目标是否确实是 Helldivers 2 的 data 目录。
// Purpose: Pure read-only validation that a deployment target is the Helldivers 2 data directory.
public sealed class GameDataTargetGuard
{
	private const string DataDirectoryName = "data";
	private const string BinDirectoryName = "bin";
	private const string GameExecutableName = "helldivers2.exe";

	/// <summary>
	/// Normalizes and validates a GameData target without creating, deleting, opening for write,
	/// or resolving links. Consumers must use the returned normalized path only when IsValid is true.
	/// </summary>
	public GameDataTargetValidationResult Validate(string? gameDataDirectory, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var issues = new List<CoreIssue>();
		if (!TryNormalize(gameDataDirectory, out var normalizedPath, issues))
			return Failure(normalizedPath, issues);

		if (!string.Equals(Path.GetFileName(normalizedPath), DataDirectoryName, StringComparison.OrdinalIgnoreCase))
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				"GameDataDirectoryNameInvalid",
				"部署目标目录必须命名为 data。",
				normalizedPath));
			return Failure(normalizedPath, issues);
		}

		if (File.Exists(normalizedPath))
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				"GameDataTargetIsFile",
				"部署目标是文件，不是 data 目录。",
				normalizedPath));
			return Failure(normalizedPath, issues);
		}

		if (!Directory.Exists(normalizedPath))
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				"GameDataDirectoryMissing",
				"部署目标 data 目录不存在。",
				normalizedPath));
			return Failure(normalizedPath, issues);
		}

		if (!TryRejectReparsePointInAncestors(normalizedPath, issues, cancellationToken))
			return Failure(normalizedPath, issues);

		var gameRootDirectory = Path.GetDirectoryName(normalizedPath);
		if (string.IsNullOrWhiteSpace(gameRootDirectory) || !Directory.Exists(gameRootDirectory))
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				"GameRootDirectoryMissing",
				"data 目录的游戏根目录不存在。",
				normalizedPath));
			return Failure(normalizedPath, issues);
		}

		var binDirectory = FindSingleDirectory(gameRootDirectory, BinDirectoryName, issues, cancellationToken);
		if (binDirectory is null)
			return Failure(normalizedPath, issues);

		if (!TryRejectReparsePoint(binDirectory, issues, cancellationToken))
			return Failure(normalizedPath, issues);

		var gameExecutablePath = FindSingleFile(binDirectory, GameExecutableName, issues, cancellationToken);
		if (gameExecutablePath is null)
			return Failure(normalizedPath, issues);

		if (!TryRejectReparsePoint(gameExecutablePath, issues, cancellationToken))
			return Failure(normalizedPath, issues);

		return new GameDataTargetValidationResult(
			true,
			normalizedPath,
			gameRootDirectory,
			binDirectory,
			gameExecutablePath,
			issues);
	}

	private static bool TryNormalize(string? inputPath, out string? normalizedPath, List<CoreIssue> issues)
	{
		normalizedPath = null;
		if (string.IsNullOrWhiteSpace(inputPath))
		{
			issues.Add(new CoreIssue(CoreIssueSeverity.Error, "GameDataPathRequired", "必须指定游戏 data 目录。"));
			return false;
		}

		var candidate = Environment.ExpandEnvironmentVariables(inputPath.Trim());
		if (candidate.IndexOf('\0') >= 0)
		{
			issues.Add(new CoreIssue(CoreIssueSeverity.Error, "GameDataPathInvalid", "游戏 data 路径包含无效字符。"));
			return false;
		}

		if (IsDevicePath(candidate))
		{
			issues.Add(new CoreIssue(CoreIssueSeverity.Error, "GameDataSpecialPathRejected", "不支持设备路径形式的游戏 data 目录。", candidate));
			return false;
		}

		if (IsUncPath(candidate))
		{
			issues.Add(new CoreIssue(CoreIssueSeverity.Error, "GameDataNetworkPathRejected", "不允许将网络共享目录作为游戏 data 目录。", candidate));
			return false;
		}

		try
		{
			if (Path.IsPathRooted(candidate) && !Path.IsPathFullyQualified(candidate))
			{
				issues.Add(new CoreIssue(CoreIssueSeverity.Error, "GameDataPathAmbiguous", "游戏 data 路径必须是完整路径或普通相对路径，不能使用盘符相对路径。", candidate));
				return false;
			}

			normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
			if (IsDevicePath(normalizedPath) || IsUncPath(normalizedPath))
			{
				issues.Add(new CoreIssue(CoreIssueSeverity.Error, "GameDataNetworkPathRejected", "不允许将网络共享或设备路径作为游戏 data 目录。", normalizedPath));
				return false;
			}

			if (!TryRejectNetworkDrive(normalizedPath, issues))
				return false;

			return true;
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			issues.Add(new CoreIssue(CoreIssueSeverity.Error, "GameDataPathInvalid", exception.Message, candidate, ExceptionMessage: exception.ToString()));
			return false;
		}
	}

	private static bool IsDevicePath(string path)
		=> path.StartsWith("\\\\?\\", StringComparison.Ordinal)
			|| path.StartsWith("\\\\.\\", StringComparison.Ordinal);

	private static bool IsUncPath(string path)
		=> path.StartsWith("\\\\", StringComparison.Ordinal)
			|| path.StartsWith("//", StringComparison.Ordinal);

	private static bool TryRejectNetworkDrive(string path, List<CoreIssue> issues)
	{
		if (!OperatingSystem.IsWindows()) return true;
		try
		{
			var root = Path.GetPathRoot(path);
			if (string.IsNullOrWhiteSpace(root))
			{
				issues.Add(new CoreIssue(CoreIssueSeverity.Error, "GameDataPathInvalid", "无法确定游戏 data 路径所在的卷。", path));
				return false;
			}

			if (new DriveInfo(root).DriveType != DriveType.Network) return true;
			issues.Add(new CoreIssue(CoreIssueSeverity.Error, "GameDataNetworkPathRejected", "不允许将映射网络驱动器作为游戏 data 目录。", path));
			return false;
		}
		catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				"GameDataDriveInspectionFailed",
				"无法安全确认游戏 data 路径不位于网络驱动器。",
				path,
				ExceptionMessage: exception.ToString()));
			return false;
		}
	}

	private static bool TryRejectReparsePointInAncestors(string path, List<CoreIssue> issues, CancellationToken cancellationToken)
	{
		var current = path;
		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!TryRejectReparsePoint(current, issues, cancellationToken)) return false;
			var parent = Path.GetDirectoryName(current);
			if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) return true;
			current = parent;
		}
	}

	private static bool TryRejectReparsePoint(string path, List<CoreIssue> issues, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		try
		{
			if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) return true;
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				"GameDataPathReparsePoint",
				"游戏 data 路径、其游戏根目录或游戏签名不能是符号链接、联接或其他 reparse point。",
				path));
			return false;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				"GameDataPathAttributesUnreadable",
				"无法安全读取游戏 data 路径的文件系统属性。",
				path,
				ExceptionMessage: exception.ToString()));
			return false;
		}
	}

	private static string? FindSingleDirectory(string parentDirectory, string expectedName, List<CoreIssue> issues, CancellationToken cancellationToken)
	{
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			var matches = Directory.EnumerateDirectories(parentDirectory)
				.Where(path => string.Equals(Path.GetFileName(path), expectedName, StringComparison.OrdinalIgnoreCase))
				.Take(2)
				.ToArray();
			if (matches.Length == 1) return matches[0];

			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				matches.Length == 0 ? "GameDataBinDirectoryMissing" : "GameDataBinDirectoryAmbiguous",
				matches.Length == 0 ? "游戏根目录中缺少 bin 目录。" : "游戏根目录中存在多个大小写不同的 bin 目录，无法安全确定目标。",
				parentDirectory));
			return null;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			issues.Add(new CoreIssue(CoreIssueSeverity.Error, "GameDataBinDirectoryUnreadable", exception.Message, parentDirectory, ExceptionMessage: exception.ToString()));
			return null;
		}
	}

	private static string? FindSingleFile(string parentDirectory, string expectedName, List<CoreIssue> issues, CancellationToken cancellationToken)
	{
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			var matches = Directory.EnumerateFiles(parentDirectory)
				.Where(path => string.Equals(Path.GetFileName(path), expectedName, StringComparison.OrdinalIgnoreCase))
				.Take(2)
				.ToArray();
			if (matches.Length == 1) return matches[0];

			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				matches.Length == 0 ? "GameExecutableMissing" : "GameExecutableAmbiguous",
				matches.Length == 0 ? "bin 目录中缺少 helldivers2.exe。" : "bin 目录中存在多个大小写不同的 helldivers2.exe，无法安全确定目标。",
				parentDirectory));
			return null;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			issues.Add(new CoreIssue(CoreIssueSeverity.Error, "GameExecutableUnreadable", exception.Message, parentDirectory, ExceptionMessage: exception.ToString()));
			return null;
		}
	}

	private static GameDataTargetValidationResult Failure(string? normalizedPath, IReadOnlyList<CoreIssue> issues)
		=> new(false, normalizedPath, null, null, null, issues);
}

// 作用：GameDataTargetGuard 的只读验证结果；有效时包含可直接用于后续操作的规范化路径。
// Purpose: Read-only GameDataTargetGuard validation result; valid results carry normalized paths for later operations.
public sealed record GameDataTargetValidationResult(
	bool IsValid,
	string? GameDataDirectory,
	string? GameRootDirectory,
	string? GameBinDirectory,
	string? GameExecutablePath,
	IReadOnlyList<CoreIssue> Issues);
