using System.Text;

namespace HD2ModCore.Infrastructure.Diagnostics;

public static class DiagnosticFiles
{
	public static FileAttributes GetLocalAttributes(string path)
	{
		var fullPath = Path.GetFullPath(path);
		var root = Path.GetPathRoot(fullPath)!;
		if (OperatingSystem.IsWindows() && (root.StartsWith("\\\\", StringComparison.Ordinal) || new DriveInfo(root).DriveType == DriveType.Network))
			throw new NotSupportedException("Remote diagnostic source.");

		var current = root;
		var attributes = File.GetAttributes(current);
		foreach (var part in fullPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
		{
			current = Path.Combine(current, part);
			attributes = File.GetAttributes(current);
			if (attributes.HasFlag(FileAttributes.ReparsePoint))
				throw new NotSupportedException("Linked diagnostic source.");
		}
		return attributes;
	}

	public static async Task<(string Text, bool Truncated)> ReadTextAsync(string file, int maxBytes, bool tail, CancellationToken cancellationToken)
	{
		if (GetLocalAttributes(file).HasFlag(FileAttributes.Directory))
			throw new InvalidDataException("Expected a diagnostic file.");
		await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 8192, FileOptions.Asynchronous);
		var length = stream.Length;
		if (!tail && length > maxBytes)
			throw new InvalidDataException("Diagnostic source exceeds the size limit.");
		if (tail && length > maxBytes)
			stream.Seek(length - maxBytes, SeekOrigin.Begin);
		var buffer = new byte[(int)Math.Min(length, maxBytes)];
		var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, false, cancellationToken).ConfigureAwait(false);
		var offset = 0;
		if (tail && length > maxBytes)
		{
			var firstLine = Array.IndexOf(buffer, (byte)'\n', 0, read);
			offset = firstLine < 0 ? read : firstLine + 1;
		}
		var endOfRead = read;
		if (tail)
		{
			// A writer can append part of a line while we take the snapshot.
			while (read > offset && buffer[read - 1] != '\n') read--;
		}
		return (Encoding.UTF8.GetString(buffer, offset, read - offset).TrimStart('\uFEFF'), length > maxBytes || read != endOfRead);
	}

	public static string? SelectNewestExistingPath(params string?[] candidates)
	{
		string? fallback = null;
		string? bestPath = null;
		var bestTime = DateTime.MinValue;
		foreach (var candidate in candidates)
		{
			if (string.IsNullOrWhiteSpace(candidate)) continue;
			fallback ??= candidate;
			try
			{
				var info = new FileInfo(candidate);
				if (!info.Exists) continue;
				var modified = info.LastWriteTimeUtc;
				if (bestPath is null || modified > bestTime)
				{
					bestPath = candidate;
					bestTime = modified;
				}
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
		}
		return bestPath ?? fallback;
	}
}
