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

		// 载荷（根 + vpk2patch/）+ Overwrite 顶替，全部由 ModNodePayloadLayout 定义。
		// 生成产物（装饰重建、换甲变体）是"顶掉同名的那一份"，不是额外的一层。
		var files = new List<string>();
		foreach (var filePath in ModNodePayloadLayout.EnumerateEffectiveFilesByName(nodeDir).Values)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var name = Path.GetFileName(filePath);
			if (_fileNameParser.TryParse(name, out var info) && info?.SidecarKind == PatchSidecarKind.Base)
			{
				files.Add(filePath);
			}
		}

		return ValueTask.FromResult<IReadOnlyList<string>>(files);
	}
}
