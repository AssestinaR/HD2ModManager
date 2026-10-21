using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// 作用：默认的对象节点文件解析器：根据节点相对路径枚举该目录下所有 patch 文件（包含 sidecar）。
// Purpose: Default node file resolver: enumerates patch files under the node directory (including sidecars).
public sealed class ModFileResolver : IModFileResolver
{
	private readonly IPatchFileNameParser _fileNameParser;

	public ModFileResolver(IPatchFileNameParser fileNameParser)
	{
		_fileNameParser = fileNameParser ?? throw new ArgumentNullException(nameof(fileNameParser));
	}

	public ValueTask<IReadOnlyList<string>> ResolvePatchFilesAsync(ModNode node, string modsRootDirectory, CancellationToken cancellationToken = default)
	{
		if (node is null)
		{
			throw new ArgumentNullException(nameof(node));
		}
		if (string.IsNullOrWhiteSpace(modsRootDirectory))
		{
			throw new ArgumentException("Value cannot be null or whitespace.", nameof(modsRootDirectory));
		}

		var nodeDir = Path.Combine(modsRootDirectory, node.RelativePath);
		if (!Directory.Exists(nodeDir))
		{
			return ValueTask.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
		}

		// 载荷目录 = 节点根 + （存在时的）`vpk2patch/`，同名以后者为准
		// （转换产物就是这个节点的内容，见 ModNodePayloadLayout）。
		var files = new List<string>();
		foreach (var filePath in ModNodePayloadLayout.EnumeratePayloadFilesByName(nodeDir).Values)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var name = Path.GetFileName(filePath);
			if (_fileNameParser.TryParse(name, out var info) && info?.SidecarKind == PatchSidecarKind.Base)
			{
				files.Add(filePath);
			}
		}

		// Generated decoration output is an in-place replacement for a host patch, not
		// an extra patch layer. Only a same-named, structurally recognizable TOC may
		// override the root file; unrelated files in Overwrite are never deployed.
		var overwriteDirectory = ModNodePayloadLayout.GetOverwriteDirectory(nodeDir);
		if (Directory.Exists(overwriteDirectory))
		{
			var overridden = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (var filePath in Directory.EnumerateFiles(overwriteDirectory))
			{
				cancellationToken.ThrowIfCancellationRequested();
				var name = Path.GetFileName(filePath);
				if (_fileNameParser.TryParse(name, out var info) && info?.SidecarKind == PatchSidecarKind.Base && IsPatchToc(filePath))
					overridden[name] = filePath;
			}
			for (var index = 0; index < files.Count; index++)
			{
				var name = Path.GetFileName(files[index]);
				if (overridden.TryGetValue(name, out var replacement)) files[index] = replacement;
			}
		}

		return ValueTask.FromResult<IReadOnlyList<string>>(files);
	}

	private static bool IsPatchToc(string path)
	{
		try
		{
			using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
			Span<byte> header = stackalloc byte[sizeof(uint)];
			return stream.Read(header) == header.Length && BitConverter.ToUInt32(header) == 4026531857;
		}
		catch (IOException) { return false; }
		catch (UnauthorizedAccessException) { return false; }
	}
}
