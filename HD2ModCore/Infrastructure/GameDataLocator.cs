using HD2ModCore.Application;
using HD2ModCore.Domain;
using Microsoft.Win32;

namespace HD2ModCore.Infrastructure;

// 作用：自动查找 HD2 的 data 目录。查找顺序：Steam 注册表安装位置 → libraryfolders.vdf 列出的各 Steam 库
//       → Program Files 下的常见 Steam 位置 → 本地固定磁盘上的常见 Steam 库根目录。
//       只探测已知的库根目录，不递归遍历磁盘，因此查找过程有界且可预期。
// Purpose: Auto-detects the HD2 data directory. Order: Steam registry install path → libraries listed in
//          libraryfolders.vdf → common Steam locations under Program Files → common Steam library roots on
//          local fixed drives. Only known library roots are probed (never a recursive drive walk), so the
//          scan stays bounded and predictable.
public sealed class GameDataLocator : IGameDataLocator
{
	private static readonly string[] GameFolderNames = ["Helldivers 2", "HELLDIVERS 2"];

	private readonly Func<IReadOnlyList<string>> _candidateLibraries;

	public GameDataLocator()
		: this(DiscoverCandidateLibraries)
	{
	}

	// 测试用：注入候选 Steam 库列表，从而在不依赖本机是否安装 Steam 的前提下验证查找规则。
	// Test seam: injects the candidate Steam library list so the lookup rules can be verified without
	// depending on whether Steam happens to be installed on the machine.
	internal GameDataLocator(Func<IReadOnlyList<string>> candidateLibraries)
	{
		_candidateLibraries = candidateLibraries ?? throw new ArgumentNullException(nameof(candidateLibraries));
	}

	public GameDataLocation? Locate()
	{
		var examined = new List<string>();

		IReadOnlyList<string> candidates;
		try
		{
			candidates = _candidateLibraries();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
		{
			return null;
		}

		foreach (var candidate in candidates)
		{
			if (string.IsNullOrWhiteSpace(candidate)) continue;

			string library;
			try
			{
				library = Path.GetFullPath(Environment.ExpandEnvironmentVariables(candidate))
					.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			}
			catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
			{
				continue;
			}

			if (!Directory.Exists(library)) continue;
			if (examined.Contains(library, StringComparer.OrdinalIgnoreCase)) continue;
			examined.Add(library);

			foreach (var gameFolderName in GameFolderNames)
			{
				var gameDirectory = Path.Combine(library, "steamapps", "common", gameFolderName);
				var dataDirectory = Path.Combine(gameDirectory, "data");
				// data 目录必须真实存在：返回一个不存在的路径会让调用方把无效路径当成检测结果保存。
				// The data directory must really exist: returning a non-existent path would make callers
				// persist an invalid location as if it were a successful detection.
				if (Directory.Exists(dataDirectory))
				{
					return new GameDataLocation(dataDirectory, gameDirectory, library, examined);
				}
			}
		}

		return null;
	}

	private static IReadOnlyList<string> DiscoverCandidateLibraries()
	{
		var libraries = new List<string>();

		void Add(string? path)
		{
			if (!string.IsNullOrWhiteSpace(path)) libraries.Add(path);
		}

		var steamPath = TryGetSteamInstallPath();
		if (!string.IsNullOrWhiteSpace(steamPath))
		{
			// 该辅助类会连同 Steam 安装目录本身一起返回。
			// The helper returns the Steam install directory itself along with every library it lists.
			foreach (var library in SteamLibraryFoldersReader.TryGetLibraryFolders(steamPath))
			{
				Add(library);
			}
		}

		// 注册表缺失时的常见安装位置。
		Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
		Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"));

		// 最后兜底：只探测本地固定磁盘上已知的 Steam 库根目录，不递归遍历磁盘。
		// Final fallback: probe only well-known Steam library roots on local fixed drives, never a recursive walk.
		try
		{
			foreach (var drive in DriveInfo.GetDrives())
			{
				if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;

				string root;
				try
				{
					root = drive.RootDirectory.FullName;
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
				{
					continue;
				}

				Add(Path.Combine(root, "Steam"));
				Add(Path.Combine(root, "SteamLibrary"));
				Add(Path.Combine(root, "SteamGames"));
				Add(Path.Combine(root, "Games", "Steam"));
				Add(Path.Combine(root, "Program Files (x86)", "Steam"));
				Add(Path.Combine(root, "Program Files", "Steam"));
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// 磁盘枚举失败不应让整个查找失败：注册表与 vdf 的结果仍然有效。
			// A failed drive enumeration must not fail the whole lookup: registry and vdf results remain valid.
		}

		return libraries;
	}

	private static string? TryGetSteamInstallPath()
	{
		// 注册表是 Windows 专有 API：在其它平台上直接跳过，交给后续的常见路径探测兜底。
		// The registry is a Windows-only API: skip it elsewhere and let the common-path probing take over.
		if (!OperatingSystem.IsWindows())
		{
			return null;
		}

		static string? ReadValue(RegistryKey baseKey, string subKey, string valueName)
		{
			try
			{
				using var key = baseKey.OpenSubKey(subKey);
				return key?.GetValue(valueName) as string;
			}
			catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
			{
				return null;
			}
		}

		var path = ReadValue(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath")
			?? ReadValue(Registry.LocalMachine, @"Software\Valve\Steam", "InstallPath")
			?? ReadValue(Registry.LocalMachine, @"Software\WOW6432Node\Valve\Steam", "InstallPath");

		if (string.IsNullOrWhiteSpace(path))
		{
			return null;
		}

		try
		{
			path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
			return Directory.Exists(path) ? path : null;
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return null;
		}
	}
}
