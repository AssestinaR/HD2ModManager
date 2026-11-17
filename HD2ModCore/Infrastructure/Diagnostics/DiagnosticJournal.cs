using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HD2ModCore.Infrastructure.Diagnostics;

public enum DiagnosticAction
{
	ManagerSession, ManagerException, Import, ActivateProfile, DeactivateProfile, AddMod, RemoveMod, ReorderMods,
	ChangeModOption, ChangeSettings, Deploy, LaunchGame, RebuildVpk, BuildAssetIndex, UpdateAssets, UpdateManager,
}

public enum DiagnosticResult { Started, Waiting, Completed, Failed, Canceled, NoChange, Requested }
public enum DiagnosticTrigger { User, Automatic, Unspecified }

public sealed record DiagnosticEvent(
	DateTimeOffset AtUtc, Guid Session, long Sequence, Guid Operation, Guid? ParentOperation,
	DiagnosticAction Action, DiagnosticResult Result, DiagnosticTrigger Trigger,
	Guid? Profile = null, long? Revision = null, Guid? Mod = null, int? Count = null,
	string? Code = null, int? ErrorNumber = null, long? DurationMs = null);

public sealed record DiagnosticHistory(IReadOnlyList<DiagnosticEvent> Events, bool Truncated, bool StorageUnavailable, int InvalidRecords);

public sealed class DiagnosticJournal(string filePath)
{
	private readonly object gate = new();
	private readonly Guid session = Guid.NewGuid();
	private readonly Queue<DiagnosticEvent> recent = new();
	private readonly string mutexName = "HD2ModManager-Diagnostics-" + Convert.ToHexString(SHA256.HashData(
		Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? Path.GetFullPath(filePath).ToUpperInvariant() : Path.GetFullPath(filePath))));
	private long sequence;
	private long persisted;
	private bool storageUnavailable;
	private bool truncated;
	private Task? pump;

	public Guid Record(DiagnosticAction action, DiagnosticResult result, DiagnosticTrigger trigger = DiagnosticTrigger.Unspecified,
		Guid? operation = null, Guid? parent = null, Guid? profile = null, long? revision = null, Guid? mod = null,
		int? count = null, string? code = null, int? errorNumber = null, long? durationMs = null)
	{
		var operationId = operation ?? Guid.NewGuid();
		lock (gate)
		{
			var now = DateTimeOffset.UtcNow;
			while (recent.TryPeek(out var first) && (recent.Count >= DiagnosticBundleWriter.MaxEvents || first.AtUtc < now - DiagnosticBundleWriter.History))
			{
				recent.Dequeue();
				truncated = true;
			}
			recent.Enqueue(new DiagnosticEvent(now, session, ++sequence, operationId, parent, action, result, trigger,
				profile, revision, mod, count, SafeCode(code), errorNumber, durationMs));
			pump ??= Task.Run(Persist);
		}
		return operationId;
	}

	public async Task<DiagnosticHistory> ReadAsync(DateTimeOffset since, CancellationToken cancellationToken)
	{
		DiagnosticEvent[] memory;
		bool previousWriteFailed;
		bool wasTruncated;
		lock (gate)
		{
			memory = recent.ToArray();
			previousWriteFailed = storageUnavailable;
			wasTruncated = truncated;
		}
		var disk = await ReadDiskAsync(cancellationToken).ConfigureAwait(false);
		var events = Merge(since, disk.Events, memory, out var capped);
		return new DiagnosticHistory(events, wasTruncated || disk.Truncated || capped, previousWriteFailed || disk.StorageUnavailable, disk.InvalidRecords);
	}

	public async Task FlushAsync(CancellationToken cancellationToken)
	{
		Task? current;
		lock (gate) current = pump;
		if (current is not null) await current.WaitAsync(cancellationToken).ConfigureAwait(false);
	}

	private void Persist()
	{
		while (true)
		{
			DiagnosticEvent[] pending;
			lock (gate)
			{
				pending = recent.Where(item => item.Sequence > persisted).ToArray();
				if (pending.Length == 0)
				{
					pump = null;
					return;
				}
			}
			try
			{
				PersistBatch(pending);
				lock (gate)
				{
					persisted = pending[^1].Sequence;
					storageUnavailable = false;
				}
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or TimeoutException or WaitHandleCannotBeOpenedException)
			{
				lock (gate)
				{
					storageUnavailable = true;
					pump = null;
				}
				return;
			}
		}
	}

	private void PersistBatch(DiagnosticEvent[] pending)
	{
		var directory = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
		DiagnosticFiles.GetLocalAttributes(Path.GetDirectoryName(directory)!);
		Directory.CreateDirectory(directory);
		DiagnosticFiles.GetLocalAttributes(directory);
		using var mutex = new Mutex(false, mutexName);
		try
		{
			if (!mutex.WaitOne(TimeSpan.FromSeconds(1))) throw new TimeoutException("Diagnostic journal is busy.");
		}
		catch (AbandonedMutexException) { } // The last complete file is still valid.
		try
		{
			using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(2));
			DiagnosticHistory previous;
			try { previous = ReadDiskAsync(budget.Token).GetAwaiter().GetResult(); }
			catch (OperationCanceledException) { throw new TimeoutException("Diagnostic journal read timed out."); }
			if (previous.StorageUnavailable) throw new IOException("Diagnostic journal is unavailable.");
			var records = Merge(DateTimeOffset.UtcNow - DiagnosticBundleWriter.History, previous.Events, pending, out var capped);
			if (capped || previous.Truncated)
				lock (gate) truncated = true;
			var temporary = Path.Combine(directory, $".hd2-events-{Guid.NewGuid():N}.tmp");
			bool ownsTemporary = false;
			try
			{
				using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
				{
					ownsTemporary = true;
					foreach (var record in records)
					{
						file.Write(DiagnosticBundleWriter.Serialize(record));
						file.WriteByte((byte)'\n');
					}
					file.Flush(flushToDisk: true);
				}
				File.Move(temporary, filePath, overwrite: true);
				ownsTemporary = false;
			}
			finally
			{
				if (ownsTemporary) File.Delete(temporary);
			}
		}
		finally { mutex.ReleaseMutex(); }
	}

	private async Task<DiagnosticHistory> ReadDiskAsync(CancellationToken cancellationToken)
	{
		try
		{
			var (text, capped) = await DiagnosticFiles.ReadTextAsync(filePath, DiagnosticBundleWriter.MaxSourceBytes, tail: true, cancellationToken).ConfigureAwait(false);
			var events = new List<DiagnosticEvent>();
			var invalid = 0;
			foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
			{
				cancellationToken.ThrowIfCancellationRequested();
				try
				{
					if (line.Length <= 1024 && JsonSerializer.Deserialize<DiagnosticEvent>(line, DiagnosticBundleWriter.Json) is { } item
						&& item.Session != Guid.Empty && item.Operation != Guid.Empty && item.Sequence > 0)
					{
						events.Add(item with { Code = SafeCode(item.Code), AtUtc = item.AtUtc.ToUniversalTime() });
						continue;
					}
				}
				catch (JsonException) { }
				invalid++;
			}
			return new DiagnosticHistory(events, capped, false, invalid);
		}
		catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
		{
			return new DiagnosticHistory([], false, false, 0);
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
		{
			return new DiagnosticHistory([], false, true, 0);
		}
	}

	private static DiagnosticEvent[] Merge(DateTimeOffset since, IEnumerable<DiagnosticEvent> old, IEnumerable<DiagnosticEvent> added, out bool capped)
	{
		var ordered = old.Concat(added).Where(item => item.AtUtc >= since)
			.DistinctBy(item => (item.Session, item.Sequence)).OrderBy(item => item.AtUtc).ThenBy(item => item.Sequence).ToArray();
		capped = ordered.Length > DiagnosticBundleWriter.MaxEvents;
		return ordered.TakeLast(DiagnosticBundleWriter.MaxEvents).ToArray();
	}

	public static string? SafeCode(string? value)
		=> value is { Length: > 0 and <= 96 } && char.IsAsciiLetter(value[0])
			&& value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.') ? value : null;
}
