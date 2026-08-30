using System.Text.Json;

namespace HD2ModCore.Infrastructure;

// Persists host-scoped decoration activation state. The on-disk format stays
// intentionally small and portable within a Mod library:
// { decorationId: [ hostId, ... ] }.
public sealed class DecorationActivationStore
{
	private readonly string _path;
	private readonly SemaphoreSlim _writeGate = new(1, 1);
	private readonly object _stateGate = new();
	private Dictionary<string, HashSet<string>> _enabledByDecoration = new(StringComparer.OrdinalIgnoreCase);

	// The live store is intentionally tolerant so a damaged local state file
	// cannot prevent the WPF application from opening. Headless callers must use
	// TryLoadReadOnlySnapshotAsync instead, which is deliberately strict.
	public DecorationActivationStore(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		_path = path;
		LoadTolerantly();
	}

	public static async ValueTask<DecorationActivationSnapshot> TryLoadReadOnlySnapshotAsync(
		string path,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		if (!File.Exists(path)) return DecorationActivationSnapshot.Empty;

		var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
		try
		{
			return ParseStrictSnapshot(json);
		}
		catch (JsonException exception)
		{
			throw new InvalidDataException("Decoration activation data is invalid JSON.", exception);
		}
	}

	public IReadOnlySet<string> GetEnabledHosts(string decorationId)
	{
		if (string.IsNullOrWhiteSpace(decorationId))
			return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		lock (_stateGate)
		{
			return _enabledByDecoration.TryGetValue(decorationId, out var hosts)
				? new HashSet<string>(hosts, StringComparer.OrdinalIgnoreCase)
				: new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		}
	}

	public DecorationActivationSnapshot CreateSnapshot()
	{
		lock (_stateGate)
			return new DecorationActivationSnapshot(CloneAsReadOnly(_enabledByDecoration));
	}

	public async Task SetEnabledHostsAsync(
		string decorationId,
		IEnumerable<string> hostIds,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(decorationId);
		ArgumentNullException.ThrowIfNull(hostIds);
		await SetEnabledHostsBatchAsync(
			new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase)
			{
				[decorationId] = NormalizeHostIds(hostIds).ToArray()
			},
			cancellationToken).ConfigureAwait(false);
	}

	// Commits related decoration state changes in one file write. The caller owns
	// planning and supplies complete host sets for the affected decoration IDs.
	public async Task SetEnabledHostsBatchAsync(
		IReadOnlyDictionary<string, IReadOnlyCollection<string>> hostsByDecoration,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(hostsByDecoration);
		if (hostsByDecoration.Count == 0) return;

		var normalized = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
		foreach (var item in hostsByDecoration)
		{
			if (string.IsNullOrWhiteSpace(item.Key)) continue;
			// Preserve the old store's last-mutation-wins behavior even when a
			// custom IReadOnlyDictionary exposes keys that differ only by case.
			normalized[item.Key] = NormalizeHostIds(item.Value ?? Array.Empty<string>());
		}
		if (normalized.Count == 0) return;

		await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			Dictionary<string, HashSet<string>> previous;
			lock (_stateGate)
			{
				previous = CloneMutable(_enabledByDecoration);
				foreach (var item in normalized)
					SetEnabledHostsCore(item.Key, item.Value);
			}

			try
			{
				await SaveAsync(cancellationToken).ConfigureAwait(false);
			}
			catch
			{
				lock (_stateGate) _enabledByDecoration = previous;
				throw;
			}
		}
		finally
		{
			_writeGate.Release();
		}
	}

	public async Task RemoveDecorationAsync(string decorationId, CancellationToken cancellationToken = default)
	{
		await CleanupAsync([decorationId], Array.Empty<string>(), cancellationToken).ConfigureAwait(false);
	}

	public async Task RemoveDecorationsAsync(IEnumerable<string> decorationIds, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(decorationIds);
		await CleanupAsync(decorationIds, Array.Empty<string>(), cancellationToken).ConfigureAwait(false);
	}

	public async Task RemoveHostAsync(string hostId, CancellationToken cancellationToken = default)
	{
		await CleanupAsync(Array.Empty<string>(), [hostId], cancellationToken).ConfigureAwait(false);
	}

	// Removes complete decoration records and/or host references across every
	// remaining decoration as one state transition and one file write. This is
	// intended for library deletion transactions where a node can appear in both
	// roles at the same time.
	public async Task<DecorationActivationCleanupResult> CleanupAsync(
		IEnumerable<string> decorationIds,
		IEnumerable<string> hostIds,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(decorationIds);
		ArgumentNullException.ThrowIfNull(hostIds);
		var requestedDecorations = NormalizeIds(decorationIds);
		var requestedHosts = NormalizeIds(hostIds);
		if (requestedDecorations.Count == 0 && requestedHosts.Count == 0)
			return DecorationActivationCleanupResult.NoChange;

		await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			Dictionary<string, HashSet<string>> previous;
			var removedDecorationCount = 0;
			var removedHostReferenceCount = 0;
			lock (_stateGate)
			{
				previous = CloneMutable(_enabledByDecoration);
				foreach (var decorationId in requestedDecorations)
				{
					if (_enabledByDecoration.Remove(decorationId)) removedDecorationCount++;
				}

				foreach (var item in _enabledByDecoration.ToArray())
				{
					foreach (var hostId in requestedHosts)
						if (item.Value.Remove(hostId)) removedHostReferenceCount++;
					if (item.Value.Count == 0 && _enabledByDecoration.Remove(item.Key))
						removedDecorationCount++;
				}
			}

			var result = new DecorationActivationCleanupResult(removedDecorationCount, removedHostReferenceCount);
			if (!result.Changed) return result;
			try
			{
				await SaveAsync(cancellationToken).ConfigureAwait(false);
			}
			catch
			{
				lock (_stateGate) _enabledByDecoration = previous;
				throw;
			}
			return result;
		}
		finally
		{
			_writeGate.Release();
		}
	}

	private void LoadTolerantly()
	{
		try
		{
			if (!File.Exists(_path)) return;
			var snapshot = ParseStrictSnapshot(File.ReadAllText(_path));
			lock (_stateGate) _enabledByDecoration = CloneMutable(snapshot.EnabledHostsByDecoration);
		}
		catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
		{
			lock (_stateGate) _enabledByDecoration.Clear();
		}
	}

	private void SetEnabledHostsCore(string decorationId, IReadOnlySet<string> hostIds)
	{
		if (hostIds.Count == 0) _enabledByDecoration.Remove(decorationId);
		else _enabledByDecoration[decorationId] = new HashSet<string>(hostIds, StringComparer.OrdinalIgnoreCase);
	}

	private async Task SaveAsync(CancellationToken cancellationToken)
	{
		Dictionary<string, List<string>> values;
		lock (_stateGate)
		{
			values = _enabledByDecoration.ToDictionary(
				item => item.Key,
				item => item.Value.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList(),
				StringComparer.OrdinalIgnoreCase);
		}

		var directory = Path.GetDirectoryName(_path);
		if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
		var temporaryPath = _path + ".tmp";
		await File.WriteAllTextAsync(
			temporaryPath,
			JsonSerializer.Serialize(values, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }),
			cancellationToken).ConfigureAwait(false);
		File.Move(temporaryPath, _path, overwrite: true);
	}

	private static DecorationActivationSnapshot ParseStrictSnapshot(string json)
	{
		using var document = JsonDocument.Parse(json);
		if (document.RootElement.ValueKind != JsonValueKind.Object)
			throw new InvalidDataException("Decoration activation data must be a JSON object.");

		var activations = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
		foreach (var property in document.RootElement.EnumerateObject())
		{
			if (!Guid.TryParse(property.Name, out _))
				throw new InvalidDataException($"Decoration activation data contains an invalid decoration ID '{property.Name}'.");
			if (property.Value.ValueKind != JsonValueKind.Array)
				throw new InvalidDataException($"Decoration activation entry '{property.Name}' must be an array of host IDs.");

			var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var hostElement in property.Value.EnumerateArray())
			{
				if (hostElement.ValueKind != JsonValueKind.String || !Guid.TryParse(hostElement.GetString(), out _))
					throw new InvalidDataException($"Decoration activation entry '{property.Name}' contains an invalid host ID.");
				hosts.Add(hostElement.GetString()!);
			}

			if (!activations.TryAdd(property.Name, hosts))
				throw new InvalidDataException($"Decoration activation data contains duplicate decoration ID '{property.Name}'.");
		}

		return new DecorationActivationSnapshot(activations);
	}

	private static HashSet<string> NormalizeHostIds(IEnumerable<string> hostIds)
		=> hostIds
			.Where(id => Guid.TryParse(id, out _))
			.ToHashSet(StringComparer.OrdinalIgnoreCase);

	private static HashSet<string> NormalizeIds(IEnumerable<string> ids)
		=> ids
			.Where(id => !string.IsNullOrWhiteSpace(id))
			.ToHashSet(StringComparer.OrdinalIgnoreCase);

	private static Dictionary<string, HashSet<string>> CloneMutable(IReadOnlyDictionary<string, IReadOnlySet<string>> source)
		=> source.ToDictionary(
			item => item.Key,
			item => new HashSet<string>(item.Value, StringComparer.OrdinalIgnoreCase),
			StringComparer.OrdinalIgnoreCase);

	private static Dictionary<string, HashSet<string>> CloneMutable(Dictionary<string, HashSet<string>> source)
		=> source.ToDictionary(
			item => item.Key,
			item => new HashSet<string>(item.Value, StringComparer.OrdinalIgnoreCase),
			StringComparer.OrdinalIgnoreCase);

	private static IReadOnlyDictionary<string, IReadOnlySet<string>> CloneAsReadOnly(Dictionary<string, HashSet<string>> source)
		=> source.ToDictionary(
			item => item.Key,
			item => (IReadOnlySet<string>)new HashSet<string>(item.Value, StringComparer.OrdinalIgnoreCase),
			StringComparer.OrdinalIgnoreCase);
}

public sealed record DecorationActivationSnapshot(IReadOnlyDictionary<string, IReadOnlySet<string>> EnabledHostsByDecoration)
{
	public static DecorationActivationSnapshot Empty => new(
		new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase));

	public IReadOnlySet<string> GetEnabledHosts(string decorationId)
		=> !string.IsNullOrWhiteSpace(decorationId) && EnabledHostsByDecoration.TryGetValue(decorationId, out var hosts)
			? new HashSet<string>(hosts, StringComparer.OrdinalIgnoreCase)
			: new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

public sealed record DecorationActivationCleanupResult(int RemovedDecorationCount, int RemovedHostReferenceCount)
{
	public static DecorationActivationCleanupResult NoChange { get; } = new(0, 0);
	public bool Changed => RemovedDecorationCount > 0 || RemovedHostReferenceCount > 0;
}
