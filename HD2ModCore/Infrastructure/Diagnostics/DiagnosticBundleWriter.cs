using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HD2ModCore.Infrastructure.Diagnostics;

public static class DiagnosticBundleWriter
{
	public const int MaxBundleBytes = 10 * 1024 * 1024;
	public const int MaxSourceBytes = 2 * 1024 * 1024;
	public static readonly TimeSpan History = TimeSpan.FromHours(72);
	public const int MaxEvents = 500;
	internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
	{
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
	};

	public static byte[] Serialize(object value) => JsonSerializer.SerializeToUtf8Bytes(value, Json);

	public static async Task WriteAsync(string destination, IReadOnlyDictionary<string, byte[]> entries, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (entries.Count == 0 || entries.Values.Sum(bytes => (long)bytes.Length) > MaxBundleBytes)
			throw new InvalidDataException("Invalid diagnostic bundle size.");
		foreach (var name in entries.Keys)
		{
			if (name.Length == 0 || name is "." or ".." || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-')))
				throw new InvalidDataException("Invalid diagnostic entry name.");
		}

		var target = Path.GetFullPath(destination);
		var temporary = Path.Combine(Path.GetDirectoryName(target)!, $".hd2-diagnostics-{Guid.NewGuid():N}.tmp");
		bool ownsTemporary = false;
		try
		{
			await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
			{
				ownsTemporary = true;
				using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
				foreach (var (name, bytes) in entries)
				{
					cancellationToken.ThrowIfCancellationRequested();
					await using var entry = archive.CreateEntry(name, CompressionLevel.Fastest).Open();
					await entry.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
				}
			}
			using (var archive = ZipFile.OpenRead(temporary))
			{
				if (archive.Entries.Count != entries.Count)
					throw new InvalidDataException("Incomplete diagnostic bundle.");
				foreach (var (name, bytes) in entries)
				{
					var entry = archive.GetEntry(name);
					if (entry is null || entry.Length != bytes.Length)
						throw new InvalidDataException("Incomplete diagnostic bundle entry.");
					await using var reader = entry.Open();
					await reader.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
				}
			}
			cancellationToken.ThrowIfCancellationRequested();
			File.Move(temporary, target, overwrite: false);
			ownsTemporary = false;
		}
		finally
		{
			if (ownsTemporary) File.Delete(temporary);
		}
	}
}
