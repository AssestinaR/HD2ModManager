using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HD2ModCore.Infrastructure;
using Microsoft.Win32;

namespace HD2ModManager.Services
{
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
            public string? DefaultExportPath { get; set; }
        }

        public const string DefaultAssetMetadataRepository = "https://raw.githubusercontent.com/Boxofbiscuits97/HD2SDK-CommunityEdition/main";
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

        public static bool GetAutoImportToActiveProfile() => LoadAll()?.AutoImportToActiveProfile ?? true;

        public static bool SetAutoImportToActiveProfile(bool enabled)
        {
            try { Update(model => model.AutoImportToActiveProfile = enabled); return true; }
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

        // Try to detect Helldivers 2 installation via Steam library folders and set GameDataFolder
        public static string TryDetectAndSetGameDataFolder()
        {
            var detected = DetectGameDataFolderInternal();
            if (!string.IsNullOrWhiteSpace(detected))
            {
                SetGameDataFolder(detected);
            }
            return detected;
        }

        private static string DetectGameDataFolderInternal()
        {
            try
            {
                var libs = new System.Collections.Generic.List<string>();
                var steamPath = GetSteamInstallPath();
                if (!string.IsNullOrWhiteSpace(steamPath)) libs.Add(steamPath);
                // libraryfolders.vdf
                var vdf = Path.Combine(steamPath ?? string.Empty, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                {
                    try
                    {
                        var text = File.ReadAllText(vdf);
                        foreach (Match m in Regex.Matches(text, "\"path\"\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase))
                        {
                            var p = m.Groups[1].Value.Replace("\\\\", "\\");
                            if (Directory.Exists(p)) libs.Add(p);
                        }
                    }
                    catch { }
                }
                // common candidates if registry missing
                var defaults = new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"),
                };
                foreach (var d in defaults) if (Directory.Exists(d)) libs.Add(d);

                // Registry-free fallback: probe only well-known Steam library roots on local fixed drives.
                // Do not recurse through drives; startup discovery must remain bounded and predictable.
                foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.IsReady && drive.DriveType == DriveType.Fixed))
                {
                    var root = drive.RootDirectory.FullName;
                    var candidates = new[]
                    {
                        Path.Combine(root, "Steam"),
                        Path.Combine(root, "SteamLibrary"),
                        Path.Combine(root, "SteamGames"),
                        Path.Combine(root, "Games", "Steam"),
                        Path.Combine(root, "Program Files (x86)", "Steam"),
                        Path.Combine(root, "Program Files", "Steam"),
                    };
                    foreach (var candidate in candidates)
                    {
                        if (Directory.Exists(candidate)) libs.Add(candidate);
                    }
                }

                // probe each lib for Helldivers 2
                foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var common = Path.Combine(lib, "steamapps", "common");
                        if (!Directory.Exists(common)) continue;
                        var candidates = new[] { "Helldivers 2", "HELLDIVERS 2" };
                        foreach (var name in candidates)
                        {
                            var game = Path.Combine(common, name);
                            if (Directory.Exists(game))
                            {
                                var data = Path.Combine(game, "data");
                                if (Directory.Exists(data)) return data;
                                // fallback to return expected path even if not yet present
                                return data;
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return string.Empty;
        }

        private static string GetSteamInstallPath()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                var v = key?.GetValue("SteamPath") as string;
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
            catch { }
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"Software\Valve\Steam");
                var v = key?.GetValue("InstallPath") as string;
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
            catch { }
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"Software\WOW6432Node\Valve\Steam");
                var v = key?.GetValue("InstallPath") as string;
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
            catch { }
            return string.Empty;
        }
    }
}
