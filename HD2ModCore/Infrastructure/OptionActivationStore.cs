using System.Text.Json;

namespace HD2ModCore.Infrastructure;

// Persists host-scoped option state. EffectiveOrder is local to one host, so a
// future drag/drop reorder never mutates the portable source-package order.
public sealed class OptionActivationStore
{
	private const int FirstVersionedFormatVersion = 2;
	private const int CurrentVersion = 3;
	private readonly string _path;
	private readonly object _gate = new();
	private Dictionary<string, Dictionary<string, OptionHostState>> _states = new(StringComparer.OrdinalIgnoreCase);

	public OptionActivationStore(string path)
	{
		_path = path ?? throw new ArgumentNullException(nameof(path));
		Load();
	}

	// Strict read-only loader for headless callers. Unlike the live store's
	// constructor it never treats malformed state as an empty state, because a
	// CLI/agent must not silently infer that every option is disabled.
	public static async ValueTask<OptionActivationSnapshot> TryLoadReadOnlySnapshotAsync(
		string path,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		if (!File.Exists(path))
			return new OptionActivationSnapshot(new Dictionary<string, IReadOnlyDictionary<string, OptionHostState>>(StringComparer.OrdinalIgnoreCase));

		var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
		try
		{
			using var document = JsonDocument.Parse(json);
			if (document.RootElement.ValueKind != JsonValueKind.Object)
				throw new InvalidDataException("Option activation data must be a JSON object.");

			if (document.RootElement.TryGetProperty("version", out var versionElement))
			{
				if (!versionElement.TryGetInt32(out var version)
					|| version < FirstVersionedFormatVersion
					|| version > CurrentVersion)
				{
					throw new InvalidDataException(
						$"Unsupported option activation data version. Supported versions are {FirstVersionedFormatVersion} through {CurrentVersion}.");
				}

				var persisted = JsonSerializer.Deserialize<PersistedState>(document.RootElement.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
				if (persisted?.Options is null)
					throw new InvalidDataException("Option activation data is missing options.");
				return new OptionActivationSnapshot(persisted.Options.ToDictionary(
					option => option.Key,
					option => (IReadOnlyDictionary<string, OptionHostState>)(option.Value ?? new Dictionary<string, PersistedHostState>())
						.ToDictionary(
							host => host.Key,
							host => new OptionHostState(host.Value?.Enabled ?? false, host.Value?.EffectiveOrder),
							StringComparer.OrdinalIgnoreCase),
					StringComparer.OrdinalIgnoreCase));
			}

			// v1 compatibility: { optionId: [ hostId, ... ] }
			var legacy = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(document.RootElement.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
			if (legacy is null)
				throw new InvalidDataException("Option activation data is invalid.");
			return new OptionActivationSnapshot(legacy.ToDictionary(
				option => option.Key,
				option => (IReadOnlyDictionary<string, OptionHostState>)(option.Value ?? [])
					.Where(host => !string.IsNullOrWhiteSpace(host))
					.ToDictionary(host => host, _ => new OptionHostState(true, null), StringComparer.OrdinalIgnoreCase),
				StringComparer.OrdinalIgnoreCase));
		}
		catch (JsonException exception)
		{
			throw new InvalidDataException("Option activation data is invalid JSON.", exception);
		}
		catch (ArgumentException exception)
		{
			throw new InvalidDataException("Option activation data contains conflicting keys.", exception);
		}
	}

	public IReadOnlySet<string> GetEnabledHosts(string optionId)
	{
		lock (_gate)
			return _states.TryGetValue(optionId, out var hosts)
				? hosts.Where(pair => pair.Value.Enabled).Select(pair => pair.Key).ToHashSet(StringComparer.OrdinalIgnoreCase)
				: new HashSet<string>(StringComparer.OrdinalIgnoreCase);
	}

	public int? GetEffectiveOrder(string optionId, string hostId)
	{
		lock (_gate)
			return _states.TryGetValue(optionId, out var hosts) && hosts.TryGetValue(hostId, out var state)
				? state.EffectiveOrder
				: null;
	}

	// A persisted disabled state is meaningful: it records that import defaults
	// have already been applied and must not silently re-enable a user's choice.
	public bool HasState(string optionId, string hostId)
	{
		lock (_gate)
			return _states.TryGetValue(optionId, out var hosts) && hosts.ContainsKey(hostId);
	}

	public OptionActivationSnapshot CreateSnapshot()
	{
		lock (_gate)
		{
			var options = _states.ToDictionary(
				option => option.Key,
				option => (IReadOnlyDictionary<string, OptionHostState>)option.Value.ToDictionary(host => host.Key, host => host.Value, StringComparer.OrdinalIgnoreCase),
				StringComparer.OrdinalIgnoreCase);
			return new OptionActivationSnapshot(options);
		}
	}

	public async Task SetEnabledHostsAsync(string optionId, IEnumerable<string> hosts, CancellationToken cancellationToken = default)
	{
		var normalized = hosts.Where(value => !string.IsNullOrWhiteSpace(value)).ToHashSet(StringComparer.OrdinalIgnoreCase);
		lock (_gate)
		{
			if (normalized.Count == 0) _states.Remove(optionId);
			else
			{
				if (!_states.TryGetValue(optionId, out var current))
					_states[optionId] = current = new Dictionary<string, OptionHostState>(StringComparer.OrdinalIgnoreCase);
				foreach (var host in current.Keys.Where(host => !normalized.Contains(host)).ToArray()) current.Remove(host);
				foreach (var host in normalized)
					current[host] = current.TryGetValue(host, out var existing) ? existing with { Enabled = true } : new OptionHostState(true, null);
			}
		}
		await SaveAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task SetEnabledAsync(string optionId, string hostId, bool enabled, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(optionId);
		ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
		lock (_gate)
		{
			if (!_states.TryGetValue(optionId, out var hosts))
				_states[optionId] = hosts = new Dictionary<string, OptionHostState>(StringComparer.OrdinalIgnoreCase);
			hosts[hostId] = hosts.TryGetValue(hostId, out var existing)
				? existing with { Enabled = enabled }
				: new OptionHostState(enabled, null);
		}
		await SaveAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task SetEnabledForHostsAsync(string optionId, IEnumerable<string> hostIds, bool enabled, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(optionId);
		var normalized = hostIds.Where(host => !string.IsNullOrWhiteSpace(host)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		if (normalized.Length == 0) return;
		lock (_gate)
		{
			if (!_states.TryGetValue(optionId, out var hosts))
				_states[optionId] = hosts = new Dictionary<string, OptionHostState>(StringComparer.OrdinalIgnoreCase);
			foreach (var hostId in normalized)
			{
				hosts[hostId] = hosts.TryGetValue(hostId, out var existing)
					? existing with { Enabled = enabled }
					: new OptionHostState(enabled, null);
			}
		}
		await SaveAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task<int> InitializeMissingStatesAsync(
		IEnumerable<OptionActivationInitialization> initializations,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(initializations);
		var normalizedByRelation = new Dictionary<string, OptionActivationInitialization>(StringComparer.OrdinalIgnoreCase);
		foreach (var item in initializations.Where(item => !string.IsNullOrWhiteSpace(item.OptionId) && !string.IsNullOrWhiteSpace(item.HostId)))
			// GUIDs cannot contain the separator, so this remains an unambiguous
			// case-insensitive relation key without allocating a custom comparer.
			normalizedByRelation[item.OptionId + "\u001F" + item.HostId] = item;
		var normalized = normalizedByRelation.Values.ToArray();
		var changed = 0;
		lock (_gate)
		{
			foreach (var item in normalized)
			{
				if (!_states.TryGetValue(item.OptionId, out var hosts))
					_states[item.OptionId] = hosts = new Dictionary<string, OptionHostState>(StringComparer.OrdinalIgnoreCase);
				if (hosts.ContainsKey(item.HostId)) continue;
				hosts[item.HostId] = new OptionHostState(item.Enabled, item.EffectiveOrder);
				changed++;
			}
		}
		if (changed > 0) await SaveAsync(cancellationToken).ConfigureAwait(false);
		return changed;
	}

	public async Task SetEffectiveOrderAsync(string optionId, string hostId, int? effectiveOrder, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(optionId);
		ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
		await SetEffectiveOrdersAsync([new OptionEffectiveOrderMutation(optionId, hostId, effectiveOrder)], cancellationToken).ConfigureAwait(false);
	}

	// Persist a complete local ordering transaction with one JSON write. Ordering is
	// scoped to one option/host relation, so this does not alter portable manifest
	// order or any other parent that happens to expose the same option.
	public async Task<int> SetEffectiveOrdersAsync(
		IEnumerable<OptionEffectiveOrderMutation> mutations,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(mutations);
		var normalizedByRelation = new Dictionary<string, OptionEffectiveOrderMutation>(StringComparer.OrdinalIgnoreCase);
		foreach (var mutation in mutations.Where(item => !string.IsNullOrWhiteSpace(item.OptionId) && !string.IsNullOrWhiteSpace(item.HostId)))
			normalizedByRelation[mutation.OptionId + "\u001F" + mutation.HostId] = mutation;

		var changed = 0;
		lock (_gate)
		{
			foreach (var mutation in normalizedByRelation.Values)
			{
				if (!_states.TryGetValue(mutation.OptionId, out var hosts))
					_states[mutation.OptionId] = hosts = new Dictionary<string, OptionHostState>(StringComparer.OrdinalIgnoreCase);
				if (hosts.TryGetValue(mutation.HostId, out var existing)
					&& existing.EffectiveOrder == mutation.EffectiveOrder)
					continue;
				hosts[mutation.HostId] = hosts.TryGetValue(mutation.HostId, out existing)
					? existing with { EffectiveOrder = mutation.EffectiveOrder }
					: new OptionHostState(false, mutation.EffectiveOrder);
				changed++;
			}
		}

		if (changed > 0) await SaveAsync(cancellationToken).ConfigureAwait(false);
		return changed;
	}

	public async Task RemoveAsync(string optionId, CancellationToken cancellationToken = default)
	{
		lock (_gate) _states.Remove(optionId);
		await SaveAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Removes one host-scoped activation relation without disturbing the same
	/// Option under any other parent. This is required when a shared Option is
	/// detached from only one host.
	/// </summary>
	public async Task<bool> RemoveRelationAsync(
		string optionId,
		string hostId,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(optionId);
		ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
		var changed = false;
		lock (_gate)
		{
			if (_states.TryGetValue(optionId, out var hosts) && hosts.Remove(hostId))
			{
				changed = true;
				if (hosts.Count == 0) _states.Remove(optionId);
			}
		}
		if (changed) await SaveAsync(cancellationToken).ConfigureAwait(false);
		return changed;
	}

	/// <summary>
	/// Removes several host-scoped relations with one persisted JSON write.
	/// </summary>
	public async Task<int> RemoveRelationsAsync(
		IEnumerable<(string OptionId, string HostId)> relations,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(relations);
		var normalized = relations
			.Where(relation => !string.IsNullOrWhiteSpace(relation.OptionId) && !string.IsNullOrWhiteSpace(relation.HostId))
			.Distinct()
			.ToArray();
		var changed = 0;
		lock (_gate)
		{
			foreach (var (optionId, hostId) in normalized)
			{
				if (!_states.TryGetValue(optionId, out var hosts) || !hosts.Remove(hostId)) continue;
				changed++;
				if (hosts.Count == 0) _states.Remove(optionId);
			}
		}
		if (changed > 0) await SaveAsync(cancellationToken).ConfigureAwait(false);
		return changed;
	}

	/// <summary>
	/// Restores a previously captured in-memory/file state for a failed compound
	/// relationship transaction. This is intentionally public for the Core
	/// mutation service, not for UI callers.
	/// </summary>
	public async Task RestoreSnapshotAsync(
		OptionActivationSnapshot snapshot,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		lock (_gate)
		{
			_states = snapshot.Options.ToDictionary(
				option => option.Key,
				option => option.Value.ToDictionary(host => host.Key, host => host.Value, StringComparer.OrdinalIgnoreCase),
				StringComparer.OrdinalIgnoreCase);
		}
		await SaveAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task RemoveHostAsync(string hostId, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(hostId)) return;
		lock (_gate)
		{
			foreach (var hosts in _states.Values) hosts.Remove(hostId);
			_states = _states.Where(item => item.Value.Count > 0)
				.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
		}
		await SaveAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Removes persisted state for deleted Options and strips deleted hosts from
	/// every surviving Option in one atomic in-memory transaction. Unlike the
	/// legacy single-item removal APIs, this method only writes when state
	/// actually changes, so deletion executors can safely batch their cleanup.
	/// </summary>
	public async Task<OptionActivationCleanupResult> CleanupAsync(
		IEnumerable<string> optionIds,
		IEnumerable<string> hostIds,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(optionIds);
		ArgumentNullException.ThrowIfNull(hostIds);
		cancellationToken.ThrowIfCancellationRequested();

		var optionsToRemove = optionIds
			.Where(optionId => !string.IsNullOrWhiteSpace(optionId))
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var hostsToRemove = hostIds
			.Where(hostId => !string.IsNullOrWhiteSpace(hostId))
			.ToHashSet(StringComparer.OrdinalIgnoreCase);

		var removedOptionRecordCount = 0;
		var removedHostRelationshipCount = 0;
		lock (_gate)
		{
			// An explicitly deleted Option is counted as a removed record; its
			// contained host relationships are part of that record rather than
			// separately reported as host-relation removals.
			foreach (var optionId in optionsToRemove)
			{
				if (_states.Remove(optionId)) removedOptionRecordCount++;
			}

			foreach (var optionId in _states.Keys.ToArray())
			{
				var hosts = _states[optionId];
				foreach (var hostId in hostsToRemove)
				{
					if (hosts.Remove(hostId)) removedHostRelationshipCount++;
				}

				if (hosts.Count == 0)
				{
					_states.Remove(optionId);
					removedOptionRecordCount++;
				}
			}
		}

		var changed = removedOptionRecordCount > 0 || removedHostRelationshipCount > 0;
		if (changed) await SaveAsync(cancellationToken).ConfigureAwait(false);
		return new OptionActivationCleanupResult(
			removedOptionRecordCount,
			removedHostRelationshipCount,
			changed);
	}

	public IReadOnlyDictionary<string, IReadOnlySet<string>> Snapshot()
		=> CreateSnapshot().Options.ToDictionary(
			option => option.Key,
			option => (IReadOnlySet<string>)option.Value.Where(host => host.Value.Enabled).Select(host => host.Key).ToHashSet(StringComparer.OrdinalIgnoreCase),
			StringComparer.OrdinalIgnoreCase);

	private void Load()
	{
		try
		{
			if (!File.Exists(_path)) return;
			using var document = JsonDocument.Parse(File.ReadAllText(_path));
			if (document.RootElement.TryGetProperty("version", out _))
			{
				var persisted = JsonSerializer.Deserialize<PersistedState>(document.RootElement.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
				if (persisted?.Options is not null)
					_states = persisted.Options.ToDictionary(
						option => option.Key,
						option => option.Value.ToDictionary(host => host.Key, host => new OptionHostState(host.Value.Enabled, host.Value.EffectiveOrder), StringComparer.OrdinalIgnoreCase),
						StringComparer.OrdinalIgnoreCase);
				return;
			}

			// v1 compatibility: { optionId: [ hostId, ... ] }
			var legacy = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(document.RootElement.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
			if (legacy is null) return;
			_states = legacy.ToDictionary(
				option => option.Key,
				option => option.Value.Where(host => !string.IsNullOrWhiteSpace(host)).ToDictionary(host => host, _ => new OptionHostState(true, null), StringComparer.OrdinalIgnoreCase),
				StringComparer.OrdinalIgnoreCase);
		}
		catch (JsonException) { }
		catch (IOException) { }
	}

	private async Task SaveAsync(CancellationToken cancellationToken)
	{
		PersistedState snapshot;
		lock (_gate)
		{
			snapshot = new PersistedState
			{
				Version = CurrentVersion,
				Options = _states.ToDictionary(
					option => option.Key,
					option => option.Value.ToDictionary(host => host.Key, host => new PersistedHostState { Enabled = host.Value.Enabled, EffectiveOrder = host.Value.EffectiveOrder }, StringComparer.OrdinalIgnoreCase),
					StringComparer.OrdinalIgnoreCase)
			};
		}
		Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
		var temporary = _path + ".tmp";
		await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), cancellationToken).ConfigureAwait(false);
		File.Move(temporary, _path, overwrite: true);
	}

	private sealed class PersistedState
	{
		public int Version { get; set; } = CurrentVersion;
		public Dictionary<string, Dictionary<string, PersistedHostState>> Options { get; set; } = new(StringComparer.OrdinalIgnoreCase);
	}

	private sealed class PersistedHostState
	{
		public bool Enabled { get; set; }
		public int? EffectiveOrder { get; set; }
	}
}

public sealed record OptionHostState(bool Enabled, int? EffectiveOrder);

public sealed record OptionActivationInitialization(string OptionId, string HostId, bool Enabled, int? EffectiveOrder = null);

public sealed record OptionEffectiveOrderMutation(string OptionId, string HostId, int? EffectiveOrder);

/// <summary>
/// Result of one batched Option-activation cleanup transaction. Host-relation
/// removals count only relations removed from Option records that survived the
/// explicit Option cleanup; relations inside explicitly deleted Option records
/// are represented by <see cref="RemovedOptionRecordCount"/> instead.
/// </summary>
public sealed record OptionActivationCleanupResult(
	int RemovedOptionRecordCount,
	int RemovedHostRelationshipCount,
	bool Changed);

public sealed record OptionActivationSnapshot(IReadOnlyDictionary<string, IReadOnlyDictionary<string, OptionHostState>> Options)
{
	public bool HasState(string optionId, string hostId)
		=> Options.TryGetValue(optionId, out var hosts) && hosts.ContainsKey(hostId);

	public bool IsEnabled(string optionId, string hostId)
		=> Options.TryGetValue(optionId, out var hosts)
			&& hosts.TryGetValue(hostId, out var state)
			&& state.Enabled;

	public int? GetEffectiveOrder(string optionId, string hostId)
		=> Options.TryGetValue(optionId, out var hosts) && hosts.TryGetValue(hostId, out var state)
			? state.EffectiveOrder
			: null;
}
