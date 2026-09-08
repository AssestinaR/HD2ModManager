using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModManager.Services
{
    // 作用：为 Manager 暴露基于 HD2ModCore Profile 语义的配置服务。
    public sealed class ProfileService
    {
        private readonly HD2ModCore.Application.IModLibraryManager _manager;
        private LibrarySnapshot _snapshot;
        private Dictionary<string, ProfileId> _profileIds = new(StringComparer.OrdinalIgnoreCase);
        private string? _selectedProfileName;
        private long _stateVersion;
        private readonly SemaphoreSlim _writeGate = new(1, 1);

        public ProfileService(string profilesPath)
        {
            var paths = SettingsService.CreateStoragePaths();
            _manager = CoreServices.CreateModLibraryManager(paths);
            _snapshot = EmptySnapshot();
        }

        public LibrarySnapshot Snapshot => _snapshot;
        public IReadOnlyList<Profile> Profiles => _snapshot.Profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        public string? SelectedKey => _selectedProfileName;
        public Profile? SelectedProfile => SelectedProfileId is ProfileId id ? _snapshot.Profiles.FirstOrDefault(p => p.Id == id) : null;
        public ProfileId? SelectedProfileId => !string.IsNullOrWhiteSpace(_selectedProfileName) && _profileIds.TryGetValue(_selectedProfileName, out var id) ? id : null;
        public string? ActiveKey => ActiveProfile?.Name;
        public Profile? ActiveProfile => _snapshot.ActiveProfileId is ProfileId id ? _snapshot.Profiles.FirstOrDefault(p => p.Id == id) : null;
        public Profile? ActiveCoreProfile => ActiveProfile;
        public ProfileId? ActiveProfileId => _snapshot.ActiveProfileId;
        public event EventHandler? ActiveProfileDeploymentRequired;
        public event EventHandler? ActiveProfileDeactivationRequired;
        public event EventHandler? Changed;

        public void RequestActiveProfileDeployment() => ActiveProfileDeploymentRequired?.Invoke(this, EventArgs.Empty);

        public void Load()
        {
            _writeGate.Wait();
            try
            {
                _snapshot = _manager.LoadOrCreateAsync().AsTask().GetAwaiter().GetResult();
                RebuildIndex();
            }
            finally { _writeGate.Release(); }
        }

        public async Task LoadAsync(CancellationToken cancellationToken = default)
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
            var loadVersion = Volatile.Read(ref _stateVersion);
            var snapshot = await _manager.LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
            if (loadVersion != Volatile.Read(ref _stateVersion))
            {
                LogService.Info("跳过过期的后台配置加载结果：用户操作已先行提交。");
                return;
            }

            _snapshot = snapshot;
            RebuildIndex();
            }
            finally { _writeGate.Release(); }
        }

        public void ReloadFromLibrary()
        {
            Load();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public async Task ReloadFromLibraryAsync(CancellationToken cancellationToken = default)
        {
            await LoadAsync(cancellationToken).ConfigureAwait(false);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public IReadOnlyList<Profile> All() => Profiles;

        public string CreateNew(string? requestedName = null)
        {
            _writeGate.Wait();
            try
            {
            var name = CreateUniqueName(requestedName);
            var profile = new Profile(ProfileId.New(), name, DateTimeOffset.UtcNow, null, Array.Empty<ProfileEntry>());
            _snapshot = _manager.UpsertProfileAsync(profile).AsTask().GetAwaiter().GetResult();
            Interlocked.Increment(ref _stateVersion);
            _selectedProfileName = name;
            SettingsService.SetSelectedProfileKey(_selectedProfileName);
            RebuildIndex();
            Changed?.Invoke(this, EventArgs.Empty);
            return name;
            }
            finally { _writeGate.Release(); }
        }

        public async Task<string> CreateNewAsync(string? requestedName = null, CancellationToken cancellationToken = default)
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var name = CreateUniqueName(requestedName);
                var profile = new Profile(ProfileId.New(), name, DateTimeOffset.UtcNow, null, Array.Empty<ProfileEntry>());
                _snapshot = await _manager.UpsertProfileAsync(profile, cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _stateVersion);
                _selectedProfileName = name;
                SettingsService.SetSelectedProfileKey(_selectedProfileName);
                RebuildIndex();
                Changed?.Invoke(this, EventArgs.Empty);
                return name;
            }
            finally { _writeGate.Release(); }
        }

        public bool Remove(string name)
        {
            _writeGate.Wait();
            try
            {
            if (!_profileIds.TryGetValue(name, out var id)) return false;
            _snapshot = _manager.DeleteProfileAsync(id).AsTask().GetAwaiter().GetResult();
            Interlocked.Increment(ref _stateVersion);
            if (string.Equals(_selectedProfileName, name, StringComparison.OrdinalIgnoreCase))
            {
                _selectedProfileName = _snapshot.Profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault()?.Name;
                SettingsService.SetSelectedProfileKey(_selectedProfileName);
            }
            RebuildIndex();
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
            }
            finally { _writeGate.Release(); }
        }

        public async Task<bool> RemoveAsync(string name, CancellationToken cancellationToken = default)
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!_profileIds.TryGetValue(name, out var id)) return false;
                _snapshot = await _manager.DeleteProfileAsync(id, cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _stateVersion);
                if (string.Equals(_selectedProfileName, name, StringComparison.OrdinalIgnoreCase))
                {
                    _selectedProfileName = _snapshot.Profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault()?.Name;
                    SettingsService.SetSelectedProfileKey(_selectedProfileName);
                }
                RebuildIndex();
                Changed?.Invoke(this, EventArgs.Empty);
                return true;
            }
            finally { _writeGate.Release(); }
        }

        public void Select(string name)
        {
            if (_profileIds.ContainsKey(name))
            {
                _selectedProfileName = name;
                SettingsService.SetSelectedProfileKey(name);
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        public bool ActivateSelected()
        {
            return Task.Run(() => ActivateSelectedAsync()).GetAwaiter().GetResult();
        }

        public async Task<bool> ActivateSelectedAsync(CancellationToken cancellationToken = default)
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (SelectedProfileId is not ProfileId profileId) return false;
                _snapshot = await _manager.SetActiveProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _stateVersion);
            ActiveProfileDeploymentRequired?.Invoke(this, EventArgs.Empty);
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
            }
            finally { _writeGate.Release(); }
        }

        public bool DisableActive()
        {
            return Task.Run(() => DisableActiveAsync()).GetAwaiter().GetResult();
        }

        public async Task<bool> DisableActiveAsync(CancellationToken cancellationToken = default)
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_snapshot.ActiveProfileId is null) return false;
                _snapshot = await _manager.SetActiveProfileAsync(null, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _stateVersion);
            ActiveProfileDeactivationRequired?.Invoke(this, EventArgs.Empty);
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
            }
            finally { _writeGate.Release(); }
        }

        public bool Rename(string oldName, string newName)
        {
            return Task.Run(() => RenameAsync(oldName, newName)).GetAwaiter().GetResult();
        }

        public async Task<bool> RenameAsync(string oldName, string newName, CancellationToken cancellationToken = default)
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName)) return false;
                if (!_profileIds.TryGetValue(oldName, out var id)) return false;
                try
                {
                    _snapshot = await _manager.RenameProfileAsync(id, newName, cancellationToken).ConfigureAwait(false);
                    Interlocked.Increment(ref _stateVersion);
                }
                catch
                {
                    return false;
                }

                var normalized = newName.Trim();
                if (string.Equals(_selectedProfileName, oldName, StringComparison.OrdinalIgnoreCase))
                {
                    _selectedProfileName = normalized;
                    SettingsService.SetSelectedProfileKey(normalized);
                }
                RebuildIndex();
                Changed?.Invoke(this, EventArgs.Empty);
                return true;
            }
            finally { _writeGate.Release(); }
        }

        public bool AddModToSelected(string nodeGuid)
        {
            _writeGate.Wait();
            try
            {
            if (SelectedProfileId is not ProfileId profileId || !TryParseNodeId(nodeGuid, out var nodeId)) return false;
            _snapshot = _manager.AddProfileEntryAsync(profileId, nodeId).AsTask().GetAwaiter().GetResult();
            Interlocked.Increment(ref _stateVersion);
            RebuildIndex();
            NotifyIfActive(profileId);
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
            }
            finally { _writeGate.Release(); }
        }

        public async Task<bool> AddModToSelectedAsync(string nodeGuid, CancellationToken cancellationToken = default)
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
            if (SelectedProfileId is not ProfileId profileId || !TryParseNodeId(nodeGuid, out var nodeId)) return false;
            var operationVersion = Interlocked.Increment(ref _stateVersion);
            var snapshot = await Task.Run(
                () => _manager.AddProfileEntryAsync(profileId, nodeId, cancellationToken).AsTask(),
                cancellationToken).ConfigureAwait(false);
            // 提交内存快照必须在写门内直接完成；绝不能在持门时等待 UI Dispatcher。
            ApplyAddedProfileSnapshot(snapshot, profileId, operationVersion);
            return true;
            }
            finally { _writeGate.Release(); }
        }

        public async Task<int> AddModsToSelectedAsync(IReadOnlyList<string> nodeGuids, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(nodeGuids);
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (SelectedProfileId is not ProfileId profileId) return 0;
                var nodeIds = nodeGuids
                    .Select(guid => TryParseNodeId(guid, out var nodeId) ? (ModNodeId?)nodeId : null)
                    .Where(nodeId => nodeId.HasValue)
                    .Select(nodeId => nodeId!.Value)
                    .Distinct()
                    .ToArray();
                if (nodeIds.Length == 0) return 0;

                var existing = _snapshot.Profiles.FirstOrDefault(profile => profile.Id == profileId)?.Entries
                    .Select(entry => entry.NodeId)
                    .ToHashSet() ?? [];
                var added = nodeIds.Count(nodeId => !existing.Contains(nodeId));
                if (added == 0) return 0;

                var operationVersion = Interlocked.Increment(ref _stateVersion);
                var snapshot = await _manager.AddProfileEntriesAsync(profileId, nodeIds, cancellationToken).ConfigureAwait(false);
                ApplyAddedProfileSnapshot(snapshot, profileId, operationVersion);
                return added;
            }
            finally { _writeGate.Release(); }
        }

        public async Task<int> AddModsToActiveAsync(IReadOnlyList<string> nodeGuids, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(nodeGuids);
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_snapshot.ActiveProfileId is not ProfileId profileId) return 0;
                var nodeIds = nodeGuids
                    .Select(guid => TryParseNodeId(guid, out var nodeId) ? (ModNodeId?)nodeId : null)
                    .Where(nodeId => nodeId.HasValue)
                    .Select(nodeId => nodeId!.Value)
                    .Distinct()
                    .ToArray();
                if (nodeIds.Length == 0) return 0;
                var existing = _snapshot.Profiles.FirstOrDefault(profile => profile.Id == profileId)?.Entries.Select(entry => entry.NodeId).ToHashSet() ?? [];
                var added = nodeIds.Count(nodeId => !existing.Contains(nodeId));
                if (added == 0) return 0;
                var operationVersion = Interlocked.Increment(ref _stateVersion);
                var snapshot = await _manager.AddProfileEntriesAsync(profileId, nodeIds, cancellationToken).ConfigureAwait(false);
                ApplyAddedProfileSnapshot(snapshot, profileId, operationVersion);
                return added;
            }
            finally { _writeGate.Release(); }
        }

        public bool RemoveModFromSelected(string nodeGuid)
        {
            _writeGate.Wait();
            try
            {
            if (SelectedProfileId is not ProfileId profileId || !TryParseNodeId(nodeGuid, out var nodeId)) return false;
            _snapshot = _manager.RemoveProfileEntryAsync(profileId, nodeId).AsTask().GetAwaiter().GetResult();
            Interlocked.Increment(ref _stateVersion);
            RebuildIndex();
            NotifyIfActive(profileId);
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
            }
            finally { _writeGate.Release(); }
        }

        public async Task<bool> RemoveModFromSelectedAsync(string nodeGuid, CancellationToken cancellationToken = default)
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
            if (SelectedProfileId is not ProfileId profileId || !TryParseNodeId(nodeGuid, out var nodeId)) return false;
            var operationVersion = Interlocked.Increment(ref _stateVersion);
            var snapshot = await Task.Run(
                () => _manager.RemoveProfileEntryAsync(profileId, nodeId, cancellationToken).AsTask(),
                cancellationToken).ConfigureAwait(false);
            ApplyAddedProfileSnapshot(snapshot, profileId, operationVersion);
            return true;
            }
            finally { _writeGate.Release(); }
        }

        public async Task<int> RemoveModsFromSelectedAsync(IReadOnlyList<string> nodeGuids, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(nodeGuids);
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
            if (nodeGuids.Count == 0 || SelectedProfileId is not ProfileId profileId) return 0;
            var ids = nodeGuids.Where(guid => TryParseNodeId(guid, out _)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (ids.Count == 0) return 0;
            var nodeIds = ids.Select(guid => TryParseNodeId(guid, out var nodeId) ? nodeId : default).ToArray();
            var existing = _snapshot.Profiles.FirstOrDefault(profile => profile.Id == profileId)?.Entries
                .Select(entry => entry.NodeId)
                .ToHashSet() ?? [];
            var removed = nodeIds.Count(existing.Contains);
            if (removed == 0) return 0;
            var operationVersion = Interlocked.Increment(ref _stateVersion);
            var snapshot = await _manager.RemoveProfileEntriesAsync(profileId, nodeIds, cancellationToken).ConfigureAwait(false);

            Action apply = () =>
            {
                if (operationVersion != Volatile.Read(ref _stateVersion)) return;
                _snapshot = snapshot;
                RebuildIndex();
                NotifyIfActive(profileId);
                Changed?.Invoke(this, EventArgs.Empty);
            };
            // 写门内完成快照提交，不把 Dispatcher 当作提交锁的一部分。
            apply();
            return removed;
            }
            finally { _writeGate.Release(); }
        }

        public bool MoveModInSelected(string nodeGuid, int direction)
        {
            return Task.Run(() => MoveModInSelectedAsync(nodeGuid, direction)).GetAwaiter().GetResult();
        }

        public async Task<bool> MoveModInSelectedAsync(string nodeGuid, int direction, CancellationToken cancellationToken = default)
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (SelectedProfileId is not ProfileId profileId || !TryParseNodeId(nodeGuid, out var nodeId)) return false;
                _snapshot = await _manager.MoveProfileEntryAsync(profileId, nodeId, direction, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _stateVersion);
            RebuildIndex();
            NotifyIfActive(profileId);
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
            }
            finally { _writeGate.Release(); }
        }

        // Purpose: Apply a complete ordered Profile membership atomically.
        public bool ReplaceSelectedEntries(IReadOnlyList<string> nodeGuids)
        {
            _writeGate.Wait();
            try
            {
                if (SelectedProfileId is not ProfileId profileId) return false;
                var ids = nodeGuids.Select(guid => TryParseNodeId(guid, out var nodeId) ? (ModNodeId?)nodeId : null).Where(id => id.HasValue).Select(id => id!.Value).ToList();
                var profile = _snapshot.Profiles.FirstOrDefault(item => item.Id == profileId);
                if (profile is null) return false;
                var entries = ids.Select((id, index) =>
                {
                    var existing = profile.Entries.FirstOrDefault(entry => entry.NodeId == id);
                    return existing is null ? new ProfileEntry(id, index) : existing with { LoadOrder = index };
                }).ToList();
                _snapshot = _manager.UpsertProfileAsync(profile with { Entries = entries, ModifiedUtc = DateTimeOffset.UtcNow, Revision = checked(profile.Revision + 1) }).AsTask().GetAwaiter().GetResult();
                Interlocked.Increment(ref _stateVersion);
                RebuildIndex();
                NotifyIfActive(profileId);
                Changed?.Invoke(this, EventArgs.Empty);
                return true;
            }
            finally { _writeGate.Release(); }
        }

        public async Task<bool> ReplaceSelectedEntriesAsync(IReadOnlyList<string> nodeGuids, CancellationToken cancellationToken = default)
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (SelectedProfileId is not ProfileId profileId) return false;
                var ids = nodeGuids.Select(guid => TryParseNodeId(guid, out var nodeId) ? (ModNodeId?)nodeId : null).Where(id => id.HasValue).Select(id => id!.Value).ToList();
                var profile = _snapshot.Profiles.FirstOrDefault(item => item.Id == profileId);
                if (profile is null) return false;
                var entries = ids.Select((id, index) =>
                {
                    var existing = profile.Entries.FirstOrDefault(entry => entry.NodeId == id);
                    return existing is null ? new ProfileEntry(id, index) : existing with { LoadOrder = index };
                }).ToList();
                _snapshot = await _manager.UpsertProfileAsync(profile with { Entries = entries, ModifiedUtc = DateTimeOffset.UtcNow, Revision = checked(profile.Revision + 1) }, cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _stateVersion);
                RebuildIndex();
                NotifyIfActive(profileId);
                Changed?.Invoke(this, EventArgs.Empty);
                return true;
            }
            finally { _writeGate.Release(); }
        }

        public IReadOnlyList<ProfileEntry> GetSortedEntries(Profile profile)
        {
            return profile.Entries.OrderBy(e => e.LoadOrder).ThenBy(e => e.AddedUtc).ToList();
        }

        private void RebuildIndex()
        {
            var rebuilt = new Dictionary<string, ProfileId>(StringComparer.OrdinalIgnoreCase);
            foreach (var profile in _snapshot.Profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                rebuilt[profile.Name] = profile.Id;
            }

            var persisted = SettingsService.GetSelectedProfileKey();
            if (!string.IsNullOrWhiteSpace(persisted) && rebuilt.ContainsKey(persisted))
            {
                _selectedProfileName = persisted;
            }
            else if (_selectedProfileName == null || !rebuilt.ContainsKey(_selectedProfileName))
            {
                _selectedProfileName = rebuilt.Keys.FirstOrDefault();
                SettingsService.SetSelectedProfileKey(_selectedProfileName);
            }
            Volatile.Write(ref _profileIds, rebuilt);
        }

        public void NotifyActiveModContentChanged()
        {
            if (_snapshot.ActiveProfileId is not null)
            {
                ActiveProfileDeploymentRequired?.Invoke(this, EventArgs.Empty);
            }
        }

        private void NotifyIfActive(ProfileId profileId)
        {
            if (_snapshot.ActiveProfileId == profileId)
            {
                ActiveProfileDeploymentRequired?.Invoke(this, EventArgs.Empty);
            }
        }

        private string CreateUniqueName(string? requestedName)
        {
            var baseName = string.IsNullOrWhiteSpace(requestedName) ? "Profile" : requestedName.Trim();
            if (!_profileIds.ContainsKey(baseName)) return baseName;

            var index = 2;
            string candidate;
            do
            {
                candidate = $"{baseName} {index}";
                index++;
            }
            while (_profileIds.ContainsKey(candidate));
            return candidate;
        }

        private static bool TryParseNodeId(string nodeGuid, out ModNodeId nodeId)
        {
            if (Guid.TryParse(nodeGuid, out var guid))
            {
                nodeId = new ModNodeId(guid);
                return true;
            }

            nodeId = default;
            return false;
        }

        private static LibrarySnapshot EmptySnapshot() => new(
            Version: 1,
            SavedUtc: DateTimeOffset.UtcNow,
            Nodes: new Dictionary<ModNodeId, ModNode>(),
            Profiles: Array.Empty<Profile>());

        private void ApplyAddedProfileSnapshot(LibrarySnapshot snapshot, ProfileId profileId, long operationVersion)
        {
            if (operationVersion != Volatile.Read(ref _stateVersion)) return;
            _snapshot = snapshot;
            RebuildIndex();
            NotifyIfActive(profileId);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
