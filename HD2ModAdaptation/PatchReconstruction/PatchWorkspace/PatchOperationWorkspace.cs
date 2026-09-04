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
		var stagedToc = StagePayload(tocPath, entry.TocData, entry.TocDataPath, entry.TocDataSource);
		var stagedGpu = StagePayload(gpuPath, entry.GpuData, entry.GpuDataPath, entry.GpuDataSource);
		var stagedStream = StagePayload(streamPath, entry.StreamData, entry.StreamDataPath, entry.StreamDataSource);
		var staged = entry with
		{
			TocData = stagedToc.Path is null ? entry.TocData : null,
			GpuData = stagedGpu.Path is null ? entry.GpuData : null,
			StreamData = stagedStream.Path is null ? entry.StreamData : null,
			TocDataPath = stagedToc.Path,
			GpuDataPath = stagedGpu.Path,
			StreamDataPath = stagedStream.Path,
			TocDataSource = stagedToc.Source,
			GpuDataSource = stagedGpu.Source,
			StreamDataSource = stagedStream.Source
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
		manifestEntries.RemoveAll(existing => existing.Key == entry.Key);
		manifestEntries.Add(new PatchOperationManifestEntry(
			entry.Key,
			entry.Ownership.ToString(),
			DescribePayload(entry.TocDataPath, entry.TocDataSource, entry.TocData),
			DescribePayload(entry.GpuDataPath, entry.GpuDataSource, entry.GpuData),
			DescribePayload(entry.StreamDataPath, entry.StreamDataSource, entry.StreamData)));
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

	// Source ranges are already durable and may be very large.  Staging them by first
	// materializing Effective*Data would defeat the workspace's disk-backed boundary.
	private static (string? Path, CanonicalPayloadSourceRange? Source) StagePayload(
		string stagedPath,
		byte[]? data,
		string? existingPath,
		CanonicalPayloadSourceRange? source)
	{
		if (data is not null)
		{
			WriteAtomically(stagedPath, data);
			return (stagedPath, null);
		}
		if (existingPath is not null)
		{
			CopyAtomically(stagedPath, existingPath);
			return (stagedPath, null);
		}
		if (source is not null)
			return (null, source);
		throw new InvalidOperationException("A workspace entry must own a payload or reference a valid source range.");
	}

	private static string DescribePayload(string? path, CanonicalPayloadSourceRange? source, byte[]? data)
		=> path is not null
			? Path.GetFileName(path)
			: source is not null
				? $"source:{Path.GetFileName(source.FilePath)}@{source.Offset}+{source.Length}"
				: $"memory:{data?.Length ?? 0}";

	private static void CopyAtomically(string destination, string source)
	{
		for (var attempt = 1; ; attempt++)
		{
			var temporaryPath = destination + ".tmp-" + Guid.NewGuid().ToString("N");
			try
			{
				File.Copy(source, temporaryPath, overwrite: true);
				File.Move(temporaryPath, destination, overwrite: true);
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
