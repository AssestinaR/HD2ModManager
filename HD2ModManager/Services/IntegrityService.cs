using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace HD2ModManager.Services
{
    public class IntegrityService
    {
        private readonly ModLibraryService _library;
        private readonly NotificationService _notify;
        private readonly string _configDir;

        public IntegrityService(ModLibraryService library, NotificationService notify, string configDir)
        {
            _library = library;
            _notify = notify;
            _configDir = configDir;
        }

        public async Task CheckAndFixAsync()
        {
            var missingRecords = new List<string>();
            var updated = false;
            var removeModGuids = new List<string>();
            var debug = new List<string>();
            foreach (var kv in _library.ByGuid)
            {
                var mod = kv.Value;
                var basePath = string.IsNullOrWhiteSpace(mod.SourcePath) ? string.Empty : _library.ResolveAbsolutePath(mod.SourcePath!);
                debug.Add($"Check mod: {mod.Name} [{mod.Guid}] base={basePath} groups={mod.FileGroups.Count}");
                var toRemoveGroups = new List<Models.FileGroup>();
                foreach (var group in mod.FileGroups)
                {
                    var toRemoveFiles = new List<string>();
                    foreach (var rel in group.Files)
                    {
                        try
                        {
                            var full = ResolveFullPath(basePath, rel);
                            debug.Add($"  file: rel={rel} full={full} exists={File.Exists(full)}");
                            if (!File.Exists(full))
                            {
                                missingRecords.Add($"{mod.Name} [{mod.Guid}] => {rel}");
                                toRemoveFiles.Add(rel);
                            }
                            // zero-length files are permitted; do not treat as missing
                        }
                        catch
                        {
                            missingRecords.Add($"{mod.Name} [{mod.Guid}] => {rel}");
                            toRemoveFiles.Add(rel);
                        }
                    }
                    if (toRemoveFiles.Count > 0)
                    {
                        updated = true;
                        foreach (var f in toRemoveFiles) group.Files.Remove(f);
                    }
                    if (group.Files.Count == 0)
                    {
                        toRemoveGroups.Add(group);
                    }
                }
                if (toRemoveGroups.Count > 0)
                {
                    updated = true;
                    foreach (var g in toRemoveGroups) mod.FileGroups.Remove(g);
                }

                if (mod.FileGroups.Count == 0)
                {
                    removeModGuids.Add(mod.Guid);
                }
            }

            if (missingRecords.Count > 0)
            {
                try
                {
                    var logsDir = Path.Combine(_configDir, "logs");
                    Directory.CreateDirectory(logsDir);
                    var logPath = Path.Combine(logsDir, $"integrity-{DateTime.UtcNow:yyyyMMddHHmmss}.txt");
                    File.WriteAllLines(logPath, missingRecords);
                    var dbgPath = Path.Combine(logsDir, $"integrity-debug-{DateTime.UtcNow:yyyyMMddHHmmss}.txt");
                    File.WriteAllLines(dbgPath, debug);
                    _notify.Show($"库完整性：移除 {missingRecords.Count} 个缺失文件，日志已生成。", NotificationLevel.Warning, TimeSpan.FromSeconds(8));
                }
                catch { }
            }

            if (removeModGuids.Count > 0)
            {
                try { updated |= (await _library.RemoveManyAsync(removeModGuids).ConfigureAwait(false)).RemovedNodeIds.Count > 0; } catch { }
            }

            if (updated)
            {
                await _library.SaveAsync().ConfigureAwait(false);
            }
        }

        // Zero-length files are permitted; only non-existent files are treated as missing.

        private static string ResolveFullPath(string basePath, string rel)
        {
            if (string.IsNullOrWhiteSpace(basePath)) return Path.GetFullPath(rel);
            // Normalize separators in rel and combine segment-wise to avoid double separators
            var parts = rel.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            var combined = basePath;
            foreach (var p in parts) combined = Path.Combine(combined, p);
            return Path.GetFullPath(combined);
        }
    }
}
