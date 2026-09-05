using System.Text.Json;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// Durable on-disk markers used by logical library nodes and Option relations.
/// Keeping these names and writes in one place makes imported and user-created
/// logical entries follow exactly the same storage protocol.
/// </summary>
public static class ModNodeStorageProtocol
{
	public const int CurrentVersion = 1;
	public const string LogicalMarkerFileName = ".hd2modmanager-logical.json";
	public const string OptionRelationFileName = "option.json";

	private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = true,
		Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	public static string LogicalMarkerPath(string directory)
		=> Path.Combine(directory ?? throw new ArgumentNullException(nameof(directory)), LogicalMarkerFileName);

	public static string OptionRelationPath(string directory)
		=> Path.Combine(directory ?? throw new ArgumentNullException(nameof(directory)), OptionRelationFileName);

	public static bool IsLogicalDirectory(string directory)
		=> TryReadLogicalNodeId(directory, out _);

	/// <summary>
	/// Reads the stable identity written into a logical-directory marker. The
	/// identity is part of the storage protocol: synchronization must reuse it
	/// when the library snapshot is missing, otherwise child Option relations
	/// would continue pointing at the old parent GUID.
	/// </summary>
	public static bool TryReadLogicalNodeId(string directory, out ModNodeId nodeId)
	{
		nodeId = default;
		if (string.IsNullOrWhiteSpace(directory)) return false;
		var path = LogicalMarkerPath(directory);
		if (!File.Exists(path)) return false;
		try
		{
			var marker = JsonSerializer.Deserialize<LogicalMarkerDocument>(File.ReadAllText(path), SerializerOptions);
			if (marker is null
				|| marker.Version != CurrentVersion
				|| !string.Equals(marker.Kind, "LogicalContainer", StringComparison.OrdinalIgnoreCase)
				|| !Guid.TryParse(marker.NodeId, out var parsed)
				|| parsed == Guid.Empty)
				return false;

			nodeId = new ModNodeId(parsed);
			return true;
		}
		catch (JsonException) { return false; }
		catch (IOException) { return false; }
		catch (UnauthorizedAccessException) { return false; }
	}

	public static void WriteLogicalMarker(string directory, ModNodeId nodeId, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directory);
		cancellationToken.ThrowIfCancellationRequested();
		Directory.CreateDirectory(directory);
		WriteAtomic(
			LogicalMarkerPath(directory),
			new LogicalMarkerDocument(CurrentVersion, "LogicalContainer", nodeId.Value.ToString("D")),
			cancellationToken);
	}

	public static void WriteOptionRelation(string directory, ModNodeMetadata metadata, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directory);
		ArgumentNullException.ThrowIfNull(metadata);
		cancellationToken.ThrowIfCancellationRequested();
		var path = OptionRelationPath(directory);
		var hosts = (metadata.HostModGuids ?? Array.Empty<string>())
			.Where(host => !string.IsNullOrWhiteSpace(host))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();
		if (metadata.Kind != ModNodeKind.Option || hosts.Count == 0)
		{
			DeleteFileIfPresent(path);
			return;
		}

		WriteAtomic(
			path,
			new OptionRelationDocument
			{
				Version = CurrentVersion,
				Kind = "Option",
				HostModGuids = hosts,
				SourcePackageGuid = metadata.SourcePackageGuid,
				SourcePath = metadata.SourcePackagePath,
				OptionOrder = metadata.OptionOrder,
				SuppressDefaultDirectOptionActivation = metadata.SuppressDefaultDirectOptionActivation,
			},
			cancellationToken);
	}

	public static void DeleteOptionRelation(string directory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directory);
		DeleteFileIfPresent(OptionRelationPath(directory));
	}

	public static void DeleteLogicalMarker(string directory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directory);
		DeleteFileIfPresent(LogicalMarkerPath(directory));
	}

	/// <summary>
	/// Makes the two relationship-sidecar files agree with a persisted Mod
	/// node.  Import, relationship edits, and deletion cleanup use this shared
	/// operation so a logical/Option transition cannot leave a stale marker.
	/// </summary>
	public static void SynchronizeNodeMetadata(string directory, ModNode node, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directory);
		ArgumentNullException.ThrowIfNull(node);
		cancellationToken.ThrowIfCancellationRequested();
		if (node.Metadata.IsLogicalContainer)
			WriteLogicalMarker(directory, node.Id, cancellationToken);
		else
			DeleteLogicalMarker(directory);
		WriteOptionRelation(directory, node.Metadata, cancellationToken);
	}

	private static void WriteAtomic<T>(string path, T value, CancellationToken cancellationToken)
	{
		var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
		var hadReadOnly = false;
		try
		{
			if (File.Exists(path))
			{
				var attributes = File.GetAttributes(path);
				hadReadOnly = (attributes & FileAttributes.ReadOnly) != 0;
				if (hadReadOnly) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
			}
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(temporary, JsonSerializer.Serialize(value, SerializerOptions));
			File.Move(temporary, path, overwrite: true);
		}
		finally
		{
			if (File.Exists(temporary)) File.Delete(temporary);
			if (hadReadOnly && File.Exists(path))
			{
				try { File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly); }
				catch (IOException) { }
				catch (UnauthorizedAccessException) { }
			}
		}
	}

	private static void DeleteFileIfPresent(string path)
	{
		if (!File.Exists(path)) return;
		try
		{
			var attributes = File.GetAttributes(path);
			if ((attributes & FileAttributes.ReadOnly) != 0)
				File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
			File.Delete(path);
		}
		catch (FileNotFoundException) { }
	}

	private sealed record LogicalMarkerDocument(int Version, string Kind, string NodeId);
}
