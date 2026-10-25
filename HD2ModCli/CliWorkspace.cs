using System.Text.Json;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

public sealed record CliManagerSettings(
	string? SelectedProfileKey,
	string? ModLibraryFolder,
	string? GameDataFolder,
	string? AssetMetadataRepository = null,
	DateTime? LastAssetMetadataCheckUtc = null)
{
	/// <summary>
	/// 旧的二态开关，只为读取历史 settings.json 并迁移保留。新代码一律使用
	/// <see cref="ArmorMappingMode"/>。
	/// </summary>
	public bool DeployLuaMods { get; init; } = true;

	/// <summary>
	/// 护甲映射模式（<see cref="Domain.ArmorMappingMode"/> 的整数值）。null 表示尚未迁移，
	/// 此时按旧开关推导，保证 CLI 与 GUI 对同一份设置得出相同结论。
	/// </summary>
	public int? ArmorMappingMode { get; init; }
}

public sealed record CliLibraryState(
	LibrarySnapshot Snapshot,
	OptionActivationSnapshot OptionActivations,
	IReadOnlyDictionary<string, IReadOnlySet<string>> DecorationActivations,
	IReadOnlyList<string> ReadIssues);

public sealed class CliWorkspace
{
	private readonly List<string> _issues;

	private CliWorkspace(
		string? managerRoot,
		string? settingsPath,
		CliManagerSettings? settings,
		string? libraryRoot,
		string? gameDataRoot,
		List<string> issues)
	{
		ManagerRoot = managerRoot;
		SettingsPath = settingsPath;
		Settings = settings;
		LibraryRoot = libraryRoot;
		GameDataRoot = gameDataRoot;
		_issues = issues;
	}

	public string? ManagerRoot { get; }
	public string? SettingsPath { get; }
	public CliManagerSettings? Settings { get; }
	public string? LibraryRoot { get; }
	public string? GameDataRoot { get; }
	public IReadOnlyList<string> Issues => _issues;

	public static CliWorkspace Open(CliGlobalOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);
		var issues = new List<string>();
		string? managerRoot = null;
		string? settingsPath = null;
		CliManagerSettings? settings = null;

		if (!string.IsNullOrWhiteSpace(options.ManagerRoot))
		{
			managerRoot = CliOutput.NormalizeDirectoryPath(options.ManagerRoot, "ManagerRootInvalid");
			if (!Directory.Exists(managerRoot))
				throw new CliUserException("ManagerRootNotFound", $"管理器目录不存在：{managerRoot}");
			settingsPath = Path.Combine(managerRoot, "settings.json");
			if (File.Exists(settingsPath))
			{
				try
				{
					var json = File.ReadAllText(settingsPath);
					settings = JsonSerializer.Deserialize<CliManagerSettings>(json, CliOutput.JsonOptions);
					if (settings is null)
						issues.Add($"无法读取管理器设置：{settingsPath}");
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
				{
					issues.Add($"无法读取管理器设置：{settingsPath}（{exception.Message}）");
				}
			}
			else
			{
				issues.Add($"未找到管理器设置：{settingsPath}");
			}
		}

		var libraryRoot = ResolveLibraryRoot(options.Library, managerRoot, settings, issues);
		var gameDataRoot = ResolveStoredPath(options.GameData ?? settings?.GameDataFolder, managerRoot);
		// The GUI arms this switch at startup, so the CLI has to read the same file
		// before planning; otherwise `deploy plan` would silently drop the Lua patch
		// that the GUI would have produced for the identical profile.
		LuaDeploymentService.MappingMode = settings?.ArmorMappingMode is int storedMode
			? HD2ModCore.Domain.ArmorMappingModes.Normalize(storedMode)
			: settings?.DeployLuaMods == false
				? HD2ModCore.Domain.ArmorMappingMode.ExternalLuaEntry
				: HD2ModCore.Domain.ArmorMappingModes.Default;
		return new CliWorkspace(managerRoot, settingsPath, settings, libraryRoot, gameDataRoot, issues);
	}

	public async ValueTask<CliLibraryState> LoadLibraryAsync(CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(LibraryRoot))
		{
			throw new CliUserException(
				"LibraryNotConfigured",
				"未能确定 Mod 库。请显式传入 --library <Mod库目录>，或传入包含 settings.json 的 --manager-root <管理器目录>。");
		}
		if (!Directory.Exists(LibraryRoot))
			throw new CliUserException("LibraryDirectoryNotFound", $"Mod 库目录不存在：{LibraryRoot}");
		var libraryPath = Path.Combine(LibraryRoot, "library.json");
		if (!File.Exists(libraryPath))
			throw new CliUserException("LibrarySnapshotNotFound", $"未找到 Mod 库状态文件：{libraryPath}。CLI 不会创建空库或迁移旧数据。");

		var paths = new StoragePaths(ManagerRoot ?? Directory.GetCurrentDirectory(), LibraryRoot);
		var store = new JsonModLibraryStore(paths);
		var snapshot = await store.TryLoadReadOnlyAsync(cancellationToken).ConfigureAwait(false);
		if (snapshot is null)
			throw new CliUserException("LibrarySnapshotUnreadable", $"无法读取 Mod 库状态文件：{libraryPath}");

		OptionActivationSnapshot optionStates;
		try
		{
			optionStates = await OptionActivationStore.TryLoadReadOnlySnapshotAsync(
				Path.Combine(LibraryRoot, "option-activations.json"),
				cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
		{
			throw new CliUserException("OptionActivationsUnreadable", $"无法安全读取 option-activations.json：{exception.Message}");
		}
		DecorationActivationSnapshot decorationActivations;
		try
		{
			decorationActivations = await DecorationActivationStore.TryLoadReadOnlySnapshotAsync(
				paths.DecorationActivationsPath,
				cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
		{
			throw new CliUserException("DecorationActivationsUnreadable", $"无法安全读取 decoration-activations.json：{exception.Message}");
		}

		return new CliLibraryState(snapshot, optionStates, decorationActivations.EnabledHostsByDecoration, _issues.ToArray());
	}

	private static string? ResolveLibraryRoot(
		string? explicitLibrary,
		string? managerRoot,
		CliManagerSettings? settings,
		List<string> issues)
	{
		if (!string.IsNullOrWhiteSpace(explicitLibrary))
		{
			var fullPath = CliOutput.NormalizeDirectoryPath(explicitLibrary, "LibraryPathInvalid");
			if (string.Equals(Path.GetFileName(fullPath), "library.json", StringComparison.OrdinalIgnoreCase))
				return Path.GetDirectoryName(fullPath);
			return fullPath;
		}

		if (string.IsNullOrWhiteSpace(managerRoot)) return null;
		if (string.IsNullOrWhiteSpace(settings?.ModLibraryFolder))
		{
			issues.Add("管理器设置中未配置 ModLibraryFolder；请传入 --library。");
			return null;
		}
		return ResolveStoredPath(settings.ModLibraryFolder, managerRoot);
	}

	private static string? ResolveStoredPath(string? path, string? managerRoot)
	{
		if (string.IsNullOrWhiteSpace(path)) return null;
		try
		{
			return Path.GetFullPath(Path.IsPathRooted(path)
				? path
				: Path.Combine(managerRoot ?? Directory.GetCurrentDirectory(), path));
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return null;
		}
	}

}
