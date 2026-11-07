using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModManager.Services
{
    // 用户自建的分类：Id 稳定不变，Name 可改。
    public sealed record CustomCategoryRecord(string Id, string Name);

    public static class SettingsService
    {
        private static readonly string SettingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
        private static readonly object SettingsGate = new();
        private static SettingsModel? _cachedSettings;
        private static Task _pendingSave = Task.CompletedTask;

        private class SettingsModel
        {
            public string? Language { get; set; }
            public string? SelectedProfileKey { get; set; }
            public string? ModLibraryFolder { get; set; }
            public string? GameDataFolder { get; set; }
            public bool AutoUpdateAssetMetadata { get; set; } = false;
            public string? AssetMetadataRepository { get; set; }
            public DateTime? LastAssetMetadataCheckUtc { get; set; }
            public int AssetMetadataCheckIntervalHours { get; set; } = 24;
            public bool AutoCheckGameDataIndex { get; set; } = true;
            public DateTime? LastGameDataIndexCheckUtc { get; set; }
            public int GameDataIndexCheckIntervalHours { get; set; } = 24;
            public bool AutoImportToActiveProfile { get; set; } = true;
            // 列表顶部的自动分类筛选条是否显示。默认开。
            public bool CategoryFilterEnabled { get; set; } = true;
            // 用户自建分类，以及"某个 Mod 手动归到哪个分类"的覆盖（Mod Guid → 分类键）。
            public List<CustomCategoryRecord>? CustomCategories { get; set; }
            public Dictionary<string, string>? CategoryOverrides { get; set; }
        // 旧的二态开关。保留字段只为读取历史设置并迁移，新代码一律读写 ArmorMappingMode。
        public bool DeployLuaMods { get; set; } = true;
        // 护甲映射模式（ArmorMappingMode 的整数值）。null 表示尚未迁移。
        public int? ArmorMappingMode { get; set; }
        // 总开关：是否启用「管理器内置 lua 类功能」。关闭时管理器永不占用 boot 入口。
        // 可空是为了区分"从没存过"（老配置，按默认开）与"显式关掉"。
        public bool? BuiltInLuaEnabled { get; set; }
        // 物理骨骼模式（PhysicsMode 的整数值）。null 表示尚未设置 ⇒ 默认关闭。
        public int? PhysicsMode { get; set; }
            public string? DefaultExportPath { get; set; }
        }

        public const string DefaultAssetMetadataRepository = GitHubAssetMetadataSyncService.DefaultRepositoryRawBaseUrl;
        public const string DefaultExportPath = "Output";

        public static string GetDefaultExportPath()
        {
            try
            {
                var configured = LoadAll()?.DefaultExportPath;
                return string.IsNullOrWhiteSpace(configured) ? DefaultExportPath : configured.Trim();
            }
            catch { return DefaultExportPath; }
        }

        public static bool SetDefaultExportPath(string? path)
        {
            try
            {
                Update(model => model.DefaultExportPath = string.IsNullOrWhiteSpace(path) ? DefaultExportPath : path.Trim());
                return true;
            }
            catch { return false; }
        }

        public static string GetResolvedDefaultExportPath()
        {
            var configured = GetDefaultExportPath();
            try
            {
                return Path.GetFullPath(Path.IsPathRooted(configured)
                    ? configured
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configured));
            }
            catch
            {
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, DefaultExportPath);
            }
        }

        /// <summary>
        /// 内置 Lua 功能外置包的固定根目录：<c>%LOCALAPPDATA%\HD2ModManager\LuaPackages</c>。
        /// 每个功能一个子目录（<c>physbone</c> / <c>wardrobe</c>），各自再分 <c>lua版</c> 与 <c>patch版</c>。
        ///
        /// 为什么不跟随"默认导出路径"：那个是给转换产物用的，混在一起用户会分不清
        /// "哪个文件是要拖进 mod 库的"。这里固定、可预期，且每次导出整目录重建。
        /// </summary>
        public static string GetLuaPackagesDirectory()
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(root, "HD2ModManager", "LuaPackages");
        }

        public static string? GetLanguage()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return null;
                var json = File.ReadAllText(SettingsPath);
                var model = JsonSerializer.Deserialize<SettingsModel>(json);
                return model?.Language;
            }
            catch { return null; }
        }

        public static bool GetAutoUpdateAssetMetadata()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return false;
                var json = File.ReadAllText(SettingsPath);
                var model = JsonSerializer.Deserialize<SettingsModel>(json);
                return model?.AutoUpdateAssetMetadata ?? false;
            }
            catch { return false; }
        }

        public static bool SetAutoUpdateAssetMetadata(bool enabled)
        {
            try
            {
                Update(model => model.AutoUpdateAssetMetadata = enabled);
                return true;
            }
            catch { return false; }
        }

        public static DateTime? GetLastAssetMetadataCheckUtc() => LoadAll()?.LastAssetMetadataCheckUtc;

        public static bool SetLastAssetMetadataCheckUtc(DateTime? value)
        {
            try
            {
                Update(model => model.LastAssetMetadataCheckUtc = value);
                return true;
            }
            catch { return false; }
        }

        public static int GetAssetMetadataCheckIntervalHours() => NormalizeInterval(LoadAll()?.AssetMetadataCheckIntervalHours);

        public static bool SetAssetMetadataCheckIntervalHours(int value)
        {
            try
            {
                Update(model => model.AssetMetadataCheckIntervalHours = NormalizeInterval(value));
                return true;
            }
            catch { return false; }
        }

        public static bool GetAutoCheckGameDataIndex() => LoadAll()?.AutoCheckGameDataIndex ?? true;

        public static bool SetAutoCheckGameDataIndex(bool enabled)
        {
            try
            {
                Update(model => model.AutoCheckGameDataIndex = enabled);
                return true;
            }
            catch { return false; }
        }

        public static DateTime? GetLastGameDataIndexCheckUtc() => LoadAll()?.LastGameDataIndexCheckUtc;

        public static bool SetLastGameDataIndexCheckUtc(DateTime? value)
        {
            try
            {
                Update(model => model.LastGameDataIndexCheckUtc = value);
                return true;
            }
            catch { return false; }
        }

        public static int GetGameDataIndexCheckIntervalHours() => NormalizeInterval(LoadAll()?.GameDataIndexCheckIntervalHours);

        public static bool SetGameDataIndexCheckIntervalHours(int value)
        {
            try
            {
                Update(model => model.GameDataIndexCheckIntervalHours = NormalizeInterval(value));
                return true;
            }
            catch { return false; }
        }

        public static bool GetCategoryFilterEnabled() => LoadAll()?.CategoryFilterEnabled ?? true;

        public static bool SetCategoryFilterEnabled(bool enabled)
        {
            try { Update(model => model.CategoryFilterEnabled = enabled); return true; }
            catch { return false; }
        }

        public static (IReadOnlyList<CustomCategoryRecord> Custom, IReadOnlyDictionary<string, string> Overrides) GetCategoryUserData()
        {
            var model = LoadAll();
            return (model?.CustomCategories ?? new List<CustomCategoryRecord>(),
                model?.CategoryOverrides ?? new Dictionary<string, string>());
        }

        public static bool SetCategoryUserData(IReadOnlyList<CustomCategoryRecord> custom, IReadOnlyDictionary<string, string> overrides)
        {
            try
            {
                Update(model =>
                {
                    model.CustomCategories = custom.ToList();
                    model.CategoryOverrides = new Dictionary<string, string>(overrides, StringComparer.OrdinalIgnoreCase);
                });
                return true;
            }
            catch { return false; }
        }

        public static bool GetAutoImportToActiveProfile() => LoadAll()?.AutoImportToActiveProfile ?? true;

        public static bool SetAutoImportToActiveProfile(bool enabled)
        {
            try { Update(model => model.AutoImportToActiveProfile = enabled); return true; }
            catch { return false; }
        }

        /// <summary>
        /// 读取护甲映射模式，并同步到 <see cref="LuaDeploymentService.MappingMode"/>——
        /// 计划阶段只读静态字段，不读磁盘。
        /// </summary>
        public static ArmorMappingMode GetArmorMappingMode()
        {
            ArmorMappingMode mode;
            try
            {
                var model = LoadAll();
                mode = model?.ArmorMappingMode is int stored
                    ? ArmorMappingModes.Normalize(stored)
                    // 迁移：旧开关关闭时是把脚本交给外置加载器，对应外置入口模式。
                    : model?.DeployLuaMods == false
                        ? ArmorMappingMode.ExternalLuaEntry
                        : ArmorMappingModes.Default;
                // 总开关关闭时「独占」不可选 ⇒ 收敛到外置，避免存着一个用不了的档。
                mode = ArmorMappingModes.Normalize((int)mode, GetBuiltInLuaEnabled());
            }
            catch
            {
                mode = ArmorMappingModes.Default;
            }
            LuaDeploymentService.MappingMode = mode;
            return mode;
        }

        public static bool SetArmorMappingMode(ArmorMappingMode mode)
        {
            LuaDeploymentService.MappingMode = mode;
            try
            {
                Update(model =>
                {
                    model.ArmorMappingMode = (int)mode;
                    // 同步旧字段，便于回退到旧版本时行为不突变。
                    model.DeployLuaMods = mode.UsesRuntimeLua();
                });
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 总开关「启用管理器内置 lua 类功能」。默认开（保持既有行为）。
        /// 关闭时管理器**永不占用 boot 入口**，所有内置 Lua 功能只能以外置补丁包形式存在。
        /// </summary>
        public static bool GetBuiltInLuaEnabled()
        {
            bool enabled;
            try { enabled = LoadAll()?.BuiltInLuaEnabled ?? true; }
            catch { enabled = true; }
            LuaDeploymentService.BuiltInLuaEnabled = enabled;
            return enabled;
        }

        public static bool SetBuiltInLuaEnabled(bool enabled)
        {
            LuaDeploymentService.BuiltInLuaEnabled = enabled;
            try
            {
                Update(model => model.BuiltInLuaEnabled = enabled);
                // 关掉总开关后，"独占"档不再合法 ⇒ 把已存的模式一并收敛，
                // 否则下次打开设置页会显示一个选项里不存在的值。
                if (!enabled)
                {
                    if (GetArmorMappingMode() == ArmorMappingMode.ExclusiveLuaEntry)
                        SetArmorMappingMode(ArmorMappingMode.ExternalLuaEntry);
                    GetPhysicsMode();
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 把三项 lua 相关设置一次性同步到 <see cref="LuaDeploymentService"/> 的静态字段。
        /// <para>
        /// ⚠️ 这三项在部署阶段都是**只读静态字段**（计划阶段不读磁盘），所以启动时必须
        /// 全部同步一次。少同步一项，那一项就停在默认值上，而设置页里明明显示"已开启"——
        /// 2026-11-01 实机就是这样：物理模式存着「独占 boot」，但启动时没人读它，
        /// 静态字段停在默认的「关闭」，于是部署出来的 patch 里根本没有物理运行时，
        /// 游戏里从头到尾没有任何反应，日志都不会被创建。
        /// </para>
        /// <para>
        /// 设置页改值时各自会同步，所以启动这一次就够；但必须三项都读。
        /// </para>
        /// </summary>
        public static void ApplyToLuaDeployment()
        {
            // 顺序有依赖：另外两项的合法性都要看总开关，所以先读它。
            GetBuiltInLuaEnabled();
            GetArmorMappingMode();
            GetPhysicsMode();
            // 求解器也必须在启动时同步一次。
            // 教训（2026-11-01）：物理那几项曾经只读静态字段、启动时漏同步一项，
            // 结果"配了也不生效"，而日志里什么线索都没有。
        }

        /// <summary>
        /// 读取物理骨骼模式并同步到 <see cref="LuaDeploymentService.PhysicsMode"/>。
        /// 总开关关闭时恒为外置入口（那一档是唯一合法的）。
        /// </summary>
        public static PhysicsMode GetPhysicsMode()
        {
            PhysicsMode mode;
            try
            {
                var model = LoadAll();
                mode = model?.PhysicsMode is int stored
                    ? (Enum.IsDefined(typeof(PhysicsMode), stored) ? (PhysicsMode)stored : PhysicsModes.Default)
                    : PhysicsModes.Default;
                mode = PhysicsModes.Normalize((int)mode, GetBuiltInLuaEnabled());
            }
            catch
            {
                mode = PhysicsModes.Default;
            }
            LuaDeploymentService.PhysicsMode = mode;
            return mode;
        }

        public static bool SetPhysicsMode(PhysicsMode mode)
        {
            LuaDeploymentService.PhysicsMode = mode;
            try
            {
                Update(model => model.PhysicsMode = (int)mode);
                return true;
            }
            catch { return false; }
        }

        public static string GetAssetMetadataRepository()
        {
            try
            {
                var model = LoadAll();
                return string.IsNullOrWhiteSpace(model?.AssetMetadataRepository) ? DefaultAssetMetadataRepository : model!.AssetMetadataRepository!;
            }
            catch { return DefaultAssetMetadataRepository; }
        }

        public static bool SetAssetMetadataRepository(string? repository)
        {
            try
            {
                Update(model => model.AssetMetadataRepository = string.IsNullOrWhiteSpace(repository) ? DefaultAssetMetadataRepository : repository);
                return true;
            }
            catch { return false; }
        }

        public static bool SetLanguage(string culture)
        {
            try
            {
                Update(model => model.Language = culture);
                return true;
            }
            catch { return false; }
        }

        public static string? GetSelectedProfileKey()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return null;
                var json = File.ReadAllText(SettingsPath);
                var model = JsonSerializer.Deserialize<SettingsModel>(json);
                return model?.SelectedProfileKey;
            }
            catch { return null; }
        }

        public static bool SetSelectedProfileKey(string? key)
        {
            try
            {
                Update(model => model.SelectedProfileKey = key);
                return true;
            }
            catch { return false; }
        }

        private static SettingsModel? LoadAll()
        {
            lock (SettingsGate)
            {
                if (_cachedSettings is not null) return _cachedSettings;
            }
            try
            {
                if (!File.Exists(SettingsPath)) return null;
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<SettingsModel>(json);
                lock (SettingsGate) return _cachedSettings ??= loaded;
            }
            catch { return null; }
        }

        private static int NormalizeInterval(int? value)
        {
            var interval = value ?? 24;
            return interval is 0 or 6 or 24 or 168 ? interval : 24;
        }

        private static void Save(SettingsModel model)
        {
            lock (SettingsGate)
            {
                // 在 gate 内捕获不可变快照，避免旧 setter 快照覆盖较新的内存设置。
                _cachedSettings = model;
                var snapshot = Clone(model);
                var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                _pendingSave = _pendingSave.ContinueWith(
                    _ => File.WriteAllTextAsync(SettingsPath, json),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default).Unwrap();
            }
        }

        private static void Update(Action<SettingsModel> update)
        {
            lock (SettingsGate)
            {
                var model = _cachedSettings ?? LoadAllUnsafe() ?? new SettingsModel();
                update(model);
                _cachedSettings = model;
                var snapshot = Clone(model);
                var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                _pendingSave = _pendingSave.ContinueWith(
                    _ => File.WriteAllTextAsync(SettingsPath, json),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default).Unwrap();
            }
        }

        private static SettingsModel? LoadAllUnsafe()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return null;
                return JsonSerializer.Deserialize<SettingsModel>(File.ReadAllText(SettingsPath));
            }
            catch { return null; }
        }

        private static SettingsModel Clone(SettingsModel model)
            => JsonSerializer.Deserialize<SettingsModel>(JsonSerializer.Serialize(model)) ?? new SettingsModel();

        // 退出前调用，等待已排队的设置写入完成；普通 setter 不再阻塞 UI 文件 IO。
        public static Task FlushAsync()
        {
            lock (SettingsGate) return _pendingSave;
        }

        public static string GetDefaultModLibraryFolder()
        {
            var recommended = GetRecommendedModLibraryFolder();
            return CanUseDirectory(recommended) ? recommended : GetPortableModLibraryFolder();
        }

        public static string GetPortableModLibraryFolder() => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mods");

        public static string GetRecommendedModLibraryFolder()
        {
            var data = GetGameDataFolder();
            if (string.IsNullOrWhiteSpace(data)) return GetPortableModLibraryFolder();
            var gameDirectory = Directory.GetParent(data)?.FullName;
            var commonDirectory = gameDirectory is null ? null : Directory.GetParent(gameDirectory)?.FullName;
            return string.IsNullOrWhiteSpace(commonDirectory)
                ? GetPortableModLibraryFolder()
                : Path.Combine(commonDirectory, "HD2ModManager", "mods");
        }

        public static void EnsureDefaultModLibraryFolder()
        {
            var portable = GetPortableModLibraryFolder();
            Update(model =>
            {
                if (!string.IsNullOrWhiteSpace(model.ModLibraryFolder)) return;
                model.ModLibraryFolder = Directory.Exists(portable) && Directory.EnumerateFileSystemEntries(portable).Any()
                    ? portable
                    : GetDefaultModLibraryFolder();
            });
        }

        // Must run before services capture StoragePaths. It never replaces a user-selected path.
        public static void InitializeDefaultPaths()
        {
            if (string.IsNullOrWhiteSpace(GetGameDataFolder())) TryDetectAndSetGameDataFolder();
            EnsureDefaultModLibraryFolder();
        }

        public static StoragePaths CreateStoragePaths()
            => new(AppDomain.CurrentDomain.BaseDirectory, GetModLibraryFolder());

        public static string GetModLibraryFolder()
        {
            try
            {
                var model = LoadAll();
                var p = model?.ModLibraryFolder;
                if (string.IsNullOrWhiteSpace(p)) return GetDefaultModLibraryFolder();
                return p!;
            }
            catch { return GetDefaultModLibraryFolder(); }
        }

        public static bool SetModLibraryFolder(string? folder)
        {
            try
            {
                Update(model => model.ModLibraryFolder = folder);
                return true;
            }
            catch { return false; }
        }

        private static bool CanUseDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            var probe = Path.Combine(path, $".hd2-write-probe-{Guid.NewGuid():N}.tmp");
            try
            {
                Directory.CreateDirectory(path);
                File.WriteAllText(probe, "probe");
                return true;
            }
            catch { return false; }
            finally { try { if (File.Exists(probe)) File.Delete(probe); } catch { } }
        }

        public static string GetGameDataFolder()
        {
            try
            {
                var model = LoadAll();
                var p = model?.GameDataFolder;
                return p ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        public static bool IsGameDataFolderValid(string? folder = null)
        {
            try
            {
                var dataFolder = string.IsNullOrWhiteSpace(folder) ? GetGameDataFolder() : folder;
                if (string.IsNullOrWhiteSpace(dataFolder) || !Directory.Exists(dataFolder)) return false;
                var gameDirectory = Directory.GetParent(Path.GetFullPath(dataFolder))?.FullName;
                return !string.IsNullOrWhiteSpace(gameDirectory)
                    && File.Exists(Path.Combine(gameDirectory, "bin", "helldivers2.exe"));
            }
            catch { return false; }
        }

        public static bool SetGameDataFolder(string? folder)
        {
            try
            {
                Update(model => model.GameDataFolder = folder);
                return true;
            }
            catch { return false; }
        }

        // 自动查找游戏 data 目录并写入设置；找不到时保留原有设置不动。
        // Auto-detects the game data directory and persists it; keeps the existing setting when nothing is found.
        public static string TryDetectAndSetGameDataFolder()
        {
            var detected = CoreServices.CreateGameDataLocator().Locate()?.GameDataDirectory ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(detected))
            {
                SetGameDataFolder(detected);
            }
            return detected;
        }
    }
}
