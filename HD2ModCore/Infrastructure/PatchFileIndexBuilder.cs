using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// 作用：从库目录真实扫描每个 mod 的 patch 文件，并按 hex/编号生成临时事实索引。
// Purpose: Scans real patch files for each mod and builds a temporary fact index ordered by archive hex and patch index.
public sealed class PatchFileIndexBuilder : IPatchFileIndexBuilder
{
	private readonly IPatchFileNameParser _parser;

	public PatchFileIndexBuilder(IPatchFileNameParser parser)
	{
		_parser = parser ?? throw new ArgumentNullException(nameof(parser));
	}

	public ValueTask<PatchFileIndex> BuildAsync(LibrarySnapshot snapshot, string modsRootDirectory, CancellationToken cancellationToken = default)
	{
		if (snapshot is null)
		{
			throw new ArgumentNullException(nameof(snapshot));
		}
		if (string.IsNullOrWhiteSpace(modsRootDirectory))
		{
			throw new ArgumentException("Value cannot be null or whitespace.", nameof(modsRootDirectory));
		}

		var byNode = new Dictionary<ModNodeId, IReadOnlyList<IndexedPatchFile>>();
		var issues = new List<CoreIssue>();
		foreach (var node in snapshot.Nodes.Values)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var nodeDir = Path.Combine(modsRootDirectory, node.RelativePath);
			if (!Directory.Exists(nodeDir))
			{
				issues.Add(new CoreIssue(CoreIssueSeverity.Warning, "ModDirectoryMissing", $"Mod directory does not exist: {nodeDir}", nodeDir, node.Id));
				byNode[node.Id] = Array.Empty<IndexedPatchFile>();
				continue;
			}

			// 载荷（根 + vpk2patch/）+ Overwrite 顶替，全部由 ModNodePayloadLayout 定义。
			var selectedByName = ModNodePayloadLayout.EnumerateEffectiveFilesByName(nodeDir);

			var parsed = selectedByName.Values
				.Select(path => (Path: path, Name: Path.GetFileName(path), Info: TryParse(path)))
				.Where(x => x.Info is not null)
				.Select(x => (x.Path, x.Name, Info: x.Info!))
				.ToList();

			var files = new List<IndexedPatchFile>();

			// base 与 sidecar 必须来自同一个目录。
			//
			// 为什么：patch 的 TOC 里写的是 GPU 段在 sidecar 里的**偏移与长度**。拿 A 的 TOC 配 B 的
			// gpu_resources，游戏读到的是越界/错位的数据——它不会报错，只会"什么都不显示"。
			// 实测踩过一次：换甲变体只顶替了 base、sidecar 还留在 vpk2patch/，进游戏后 RE-2310 没变。
			// 这条检查把它变成一条看得见的警告，而不是一次靠猜的实机排查。
			foreach (var basePatch in parsed.Where(x => x.Info.SidecarKind == PatchSidecarKind.Base))
			{
				var baseDirectory = Path.GetDirectoryName(basePatch.Path)!;
				foreach (var suffix in new[] { ".gpu_resources", ".stream" })
				{
					var sidecarName = basePatch.Name + suffix;
					if (!selectedByName.TryGetValue(sidecarName, out var sidecarPath))
						continue;
					if (string.Equals(Path.GetDirectoryName(sidecarPath), baseDirectory, StringComparison.OrdinalIgnoreCase))
						continue;
					issues.Add(new CoreIssue(CoreIssueSeverity.Warning, "PatchSidecarFromAnotherDirectory",
						$"patch 与它的 {suffix} 来自不同目录（{baseDirectory} / {Path.GetDirectoryName(sidecarPath)}）；"
						+ "部署后游戏读到的会是错位的数据。",
						sidecarPath, node.Id));
				}
			}

			foreach (var group in parsed.GroupBy(x => x.Info.ArchiveHex16, StringComparer.OrdinalIgnoreCase))
			{
				var baseOrder = group
					.Where(x => x.Info.SidecarKind == PatchSidecarKind.Base)
					.Select(x => x.Info.PatchIndex)
					.Distinct()
					.OrderBy(x => x)
					.Select((source, normalized) => new { source, normalized })
					.ToDictionary(x => x.source, x => x.normalized);

				foreach (var file in group.OrderBy(x => x.Info.PatchIndex).ThenBy(x => x.Info.SidecarKind))
				{
					cancellationToken.ThrowIfCancellationRequested();
					if (!baseOrder.TryGetValue(file.Info.PatchIndex, out var normalizedOrder))
					{
						issues.Add(new CoreIssue(CoreIssueSeverity.Warning, "SidecarWithoutBase", $"Patch sidecar has no base patch: {file.Path}", file.Path, node.Id));
						continue;
					}

					var info = new FileInfo(file.Path);
					files.Add(new IndexedPatchFile(
						NodeId: node.Id,
						FilePath: file.Path,
						FileName: file.Name,
						ArchiveHex16: file.Info.ArchiveHex16,
						SourcePatchIndex: file.Info.PatchIndex,
						NormalizedOrder: normalizedOrder,
						SidecarKind: file.Info.SidecarKind,
						Length: info.Exists ? info.Length : 0,
						LastWriteTimeUtc: info.Exists ? info.LastWriteTimeUtc : DateTimeOffset.MinValue));
				}
			}

			byNode[node.Id] = files
				.OrderBy(f => f.ArchiveHex16, StringComparer.OrdinalIgnoreCase)
				.ThenBy(f => f.NormalizedOrder)
				.ThenBy(f => f.SidecarKind)
				.ToList();
		}

		return ValueTask.FromResult(new PatchFileIndex(DateTimeOffset.UtcNow, byNode, issues));
	}

	private PatchFileNameInfo? TryParse(string path)
		=> _parser.TryParse(Path.GetFileName(path), out var info) ? info : null;

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
