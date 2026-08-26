using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using System.Text.Json;

namespace HD2ModAdaptation.PatchReconstruction.PatchWorkspace;

// Purpose: Owns one tool operation's disk-backed payload staging area.
// Jobs may use memory while rebuilding, but their results cross the operation boundary as files.
public interface IPatchOperationWorkspaceFactory
{
	IPatchOperationWorkspace Create(string outputDirectoryPath, string operationKind);
}

public interface IPatchOperationWorkspace : IDisposable
{
	string DirectoryPath { get; }
	string ManifestPath { get; }
	CanonicalPatchSessionEntry Stage(CanonicalPatchSessionEntry entry);
	CanonicalPatchSessionEntry Reference(CanonicalPatchSessionEntry stagedEntry, AssetKey key);
	PatchWorkspaceJobResult Stage(PatchWorkspaceJobResult job);
}

public sealed record PatchOperationManifest(
	string OperationId,
	string OperationKind,
	DateTimeOffset CreatedAt,
	IReadOnlyList<PatchOperationManifestEntry> Outputs);

public sealed record PatchOperationManifestEntry(
	AssetKey Key,
	string Ownership,
	string TocFile,
	string GpuFile,
	string StreamFile);

public sealed class PatchOperationWorkspaceFactory : IPatchOperationWorkspaceFactory
{
	public IPatchOperationWorkspace Create(string outputDirectoryPath, string operationKind)
		=> new PatchOperationWorkspace(outputDirectoryPath, operationKind);
}

public sealed class PatchOperationWorkspace : IPatchOperationWorkspace
{
	private const int ManifestFlushBatchSize = 64;
	private const int AtomicWriteAttempts = 5;
	private bool disposed;
	private bool manifestDirty;
	private int stagedEntriesSinceManifest;
	private readonly string operationId = Guid.NewGuid().ToString("N");
	private readonly string operationKind;
	private readonly DateTimeOffset createdAt = DateTimeOffset.UtcNow;
	private readonly List<PatchOperationManifestEntry> manifestEntries = [];

	public PatchOperationWorkspace(string outputDirectoryPath, string operationKind)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectoryPath);
		ArgumentException.ThrowIfNullOrWhiteSpace(operationKind);
		this.operationKind = operationKind;
		var safeOperationKind = string.Concat(operationKind.Select(character =>
			Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
		DirectoryPath = Path.Combine(Path.GetFullPath(outputDirectoryPath), ".patch-operation-workspace", $"{safeOperationKind}-{operationId}");
		Directory.CreateDirectory(DirectoryPath);
		ManifestPath = Path.Combine(DirectoryPath, "manifest.json");
		WriteManifest();
	}

	public string DirectoryPath { get; }
	public string ManifestPath { get; }

	public CanonicalPatchSessionEntry Stage(CanonicalPatchSessionEntry entry)
	{
		ObjectDisposedException.ThrowIf(disposed, this);
		ArgumentNullException.ThrowIfNull(entry);
		var prefix = $"{entry.Key.TypeId:x16}-{entry.Key.FileId:x16}";
		var tocPath = Path.Combine(DirectoryPath, prefix + ".toc");
		var gpuPath = Path.Combine(DirectoryPath, prefix + ".gpu");
		var streamPath = Path.Combine(DirectoryPath, prefix + ".stream");
		WriteAtomically(tocPath, entry.EffectiveTocData);
		WriteAtomically(gpuPath, entry.EffectiveGpuData);
		WriteAtomically(streamPath, entry.EffectiveStreamData);
		var staged = entry with
		{
			TocData = null,
			GpuData = null,
			StreamData = null,
			TocDataPath = tocPath,
			GpuDataPath = gpuPath,
			StreamDataPath = streamPath
		};
		RegisterManifestEntry(staged);
		return staged;
	}

	public CanonicalPatchSessionEntry Reference(CanonicalPatchSessionEntry stagedEntry, AssetKey key)
	{
		ObjectDisposedException.ThrowIf(disposed, this);
		ArgumentNullException.ThrowIfNull(stagedEntry);
		if (key == default)
			throw new ArgumentException("A staged payload reference requires an explicit AssetKey.", nameof(key));
		if (stagedEntry.TocDataPath is null || stagedEntry.GpuDataPath is null || stagedEntry.StreamDataPath is null)
			throw new InvalidOperationException("Only a fully staged payload can be referenced without copying its bytes.");

		var referenced = stagedEntry with
		{
			Key = key,
			TocData = null,
			GpuData = null,
			StreamData = null
		};
		RegisterManifestEntry(referenced);
		return referenced;
	}

	public PatchWorkspaceJobResult Stage(PatchWorkspaceJobResult job)
	{
		ObjectDisposedException.ThrowIf(disposed, this);
		ArgumentNullException.ThrowIfNull(job);
		return job with { Outputs = job.Outputs.Select(Stage).ToArray() };
	}

	private void RegisterManifestEntry(CanonicalPatchSessionEntry entry)
	{
		if (entry.TocDataPath is null || entry.GpuDataPath is null || entry.StreamDataPath is null)
			throw new InvalidOperationException("A staged workspace entry must have TOC, GPU, and Stream paths.");

		manifestEntries.RemoveAll(existing => existing.Key == entry.Key);
		manifestEntries.Add(new PatchOperationManifestEntry(
			entry.Key,
			entry.Ownership.ToString(),
			Path.GetFileName(entry.TocDataPath),
			Path.GetFileName(entry.GpuDataPath),
			Path.GetFileName(entry.StreamDataPath)));
		manifestDirty = true;
		stagedEntriesSinceManifest++;
		// Keep a recoverable manifest after the first output, then avoid rewriting an
		// ever-growing JSON document for every Unit in large operations.
		if (manifestEntries.Count == 1 || stagedEntriesSinceManifest >= ManifestFlushBatchSize)
			FlushManifest();
	}

	public void Dispose()
	{
		if (disposed) return;
		disposed = true;
		if (manifestDirty)
			FlushManifest();
		try
		{
			if (Directory.Exists(DirectoryPath))
				Directory.Delete(DirectoryPath, recursive: true);

			// The operation directory is nested below a shared workspace root. Remove
			// that root only when it is empty, so concurrent operations remain safe.
			var workspaceRoot = Directory.GetParent(DirectoryPath)?.FullName;
			if (workspaceRoot is not null
				&& Directory.Exists(workspaceRoot)
				&& !Directory.EnumerateFileSystemEntries(workspaceRoot).Any())
				Directory.Delete(workspaceRoot, recursive: false);
		}
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}

	private static void WriteAtomically(string path, byte[] data)
	{
		for (var attempt = 1; ; attempt++)
		{
			var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
			try
			{
				File.WriteAllBytes(temporaryPath, data);
				File.Move(temporaryPath, path, overwrite: true);
				return;
			}
			catch (Exception exception) when (attempt < AtomicWriteAttempts && exception is IOException or UnauthorizedAccessException)
			{
				Thread.Sleep(TimeSpan.FromMilliseconds(50 * (1 << (attempt - 1))));
			}
			finally
			{
				try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
				catch (IOException) { }
				catch (UnauthorizedAccessException) { }
			}
		}
	}

	private void FlushManifest()
	{
		WriteManifest();
		manifestDirty = false;
		stagedEntriesSinceManifest = 0;
	}

	private void WriteManifest()
		=> WriteAtomically(ManifestPath, JsonSerializer.SerializeToUtf8Bytes(
			new PatchOperationManifest(operationId, operationKind, createdAt, manifestEntries.ToArray()),
			new JsonSerializerOptions { WriteIndented = true }));
}
