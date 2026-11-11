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

	/// <summary>
	/// <see cref="ArmorMappingMode"/> 那个数字用的是哪一套口径。null/0 = 旧口径
	/// （0=独占 / 1=外置 / 2=真映射）；2 = 新口径（0=关闭 / 1=独占 / 2=外置）。
	/// CLI 必须与 GUI 用同一套翻译，否则同一份设置会得出两个不同结论。
	/// </summary>
	public int? MappingModeSchema { get; init; }

	/// <summary>
	/// 总开关「启用管理器内置 lua 类功能」。null = 从没存过 ⇒ 按默认开，
	/// 与 <c>SettingsService.GetBuiltInLuaEnabled</c> 同一口径。
	/// </summary>
	public bool? BuiltInLuaEnabled { get; init; }

	/// <summary>
	/// 物理骨骼模式（<see cref="Domain.PhysicsMode"/> 的整数值）。null = 尚未设置 ⇒ 按默认值。
	/// </summary>
	public int? PhysicsMode { get; init; }
}

public sealed record CliLibraryState(
	LibrarySnapshot Snapshot,
	OptionActivationSnapshot OptionActivations,
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

		// 没给 --manager-root 时**按布局反推**管理器根目录，而不是直接取 hd2mod.exe 所在目录。
		//
		// 发布包的布局是 <安装根>\HD2ModManager.exe + <安装根>\bin\hd2mod.exe：命令行程序的目录
		// 是 bin\，而 settings.json 与 data\ 在上一级。取错这一级，CLI 会去找
		// bin\settings.json 与 bin\data\indexes\asset-index.sqlite，两个都不存在 ——
		// 于是"没配游戏 data、没有骨名表"，**整批 VPK 导入在规划阶段全部失败**
		// （1.5.5 的实机回归）。推导规则见 Hd2ModCliLocator.ResolveManagerRoot。
		var managerRootOption = string.IsNullOrWhiteSpace(options.ManagerRoot)
			? Hd2ModCliLocator.ResolveManagerRoot()
			: options.ManagerRoot;

		if (!string.IsNullOrWhiteSpace(managerRootOption))
		{
			managerRoot = CliOutput.NormalizeDirectoryPath(managerRootOption, "ManagerRootInvalid");
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
				// 手工把 --manager-root 指到 bin\ 是每次发版都会重演一次的老误会：
				// 用户/agent 看到的是"hd2mod.exe 就在这儿"，而管理器根在上一级。
				// 既然上一级确实像管理器根，就直说，不要让人对着"未找到设置"自己猜。
				if (Hd2ModCliLocator.TryGetManagerRootParent(managerRoot) is { } parent)
				{
					issues.Add($"--manager-root 指向了 {Hd2ModCliLocator.ReleaseSubdirectory} 子目录，"
						+ $"管理器根目录是它的上一级：{parent}");
				}
			}
		}

		var libraryRoot = ResolveLibraryRoot(options.Library, managerRoot, settings, issues);
		var gameDataRoot = ResolveStoredPath(options.GameData ?? settings?.GameDataFolder, managerRoot);
		// The GUI arms these switches at startup (SettingsService.ApplyToLuaDeployment), so the
		// CLI has to read the same file before planning; otherwise `deploy plan` would silently
		// drop (or invent) the Lua patches the GUI would have produced for the identical profile.
		//
		// 三项都要读，少读一项那一项就停在静态默认值上——2026-11-01 实机就是这么丢掉整轮物理的。
		LuaDeploymentService.BuiltInLuaEnabled = settings?.BuiltInLuaEnabled ?? true;
		var builtInLuaEnabled = LuaDeploymentService.BuiltInLuaEnabled;
		LuaDeploymentService.MappingMode = ArmorMappingModes.Normalize(
			(int)(settings?.ArmorMappingMode is int storedMode
				// 必须带 schema 读：旧口径 0=独占/1=外置/2=真映射，新口径 0=关闭/1=独占/2=外置。
				// 不带版本直接当新值读，会把"开着独占"读成"关闭"。
				? ArmorMappingModes.MigrateStoredValue(storedMode, settings?.MappingModeSchema ?? 0)
				: settings?.DeployLuaMods == false
					? ArmorMappingMode.ExternalLuaEntry
					: ArmorMappingModes.Default),
			builtInLuaEnabled);
		LuaDeploymentService.PhysicsMode = PhysicsModes.Normalize(
			settings?.PhysicsMode is int storedPhysics && Enum.IsDefined(typeof(PhysicsMode), storedPhysics)
				? storedPhysics
				: (int)PhysicsModes.Default,
			builtInLuaEnabled);
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

		return new CliLibraryState(snapshot, optionStates, _issues.ToArray());
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
