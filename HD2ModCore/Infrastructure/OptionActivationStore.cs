using System.Text.Json;

namespace HD2ModCore.Infrastructure;

// Persists host-scoped option state. EffectiveOrder is local to one host, so a
// future drag/drop reorder never mutates the portable source-package order.
public sealed class OptionActivationStore
{
	private const int CurrentVersion = 3;
	private readonly string _path;
	private readonly object _gate = new();
	private Dictionary<string, Dictionary<string, OptionHostState>> _states = new(StringComparer.OrdinalIgnoreCase);

	public OptionActivationStore(string path)
	{
		_path = path ?? throw new ArgumentNullException(nameof(path));
		Load();
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
