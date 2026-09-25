using System.Globalization;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

public static class RuntimeArmorResourceFilter
{
	// Scan only actual deployed TOCs, not inactive library files.
	public static async Task<IReadOnlySet<ulong>> ReadDeployedUnitsAsync(string gameData, CancellationToken cancellationToken = default)
	{
		if (!Directory.Exists(gameData)) throw new DirectoryNotFoundException("无法检查已部署 Unit：游戏 data 目录不存在。");
		var units = new HashSet<ulong>();
		var parser = new PatchFileNameParser();
		var patches = Directory.EnumerateFiles(gameData, "*.patch_*", SearchOption.TopDirectoryOnly)
			.Select(path => (Path: path, Info: parser.TryParse(Path.GetFileName(path), out var info) ? info : null))
			.Where(item => item.Info?.SidecarKind == PatchSidecarKind.Base)
			.OrderBy(item => item.Info!.PatchIndex).ThenBy(item => item.Info!.ArchiveHex16, StringComparer.OrdinalIgnoreCase);
		foreach (var patch in patches)
		{
			var entries = await new PatchTocScanner().ScanEntriesAsync(patch.Path, cancellationToken).ConfigureAwait(false);
			var tocLength = (ulong)new FileInfo(patch.Path).Length;
			var streamLength = File.Exists(patch.Path + ".stream") ? (ulong)new FileInfo(patch.Path + ".stream").Length : 0UL;
			var gpuLength = File.Exists(patch.Path + ".gpu_resources") ? (ulong)new FileInfo(patch.Path + ".gpu_resources").Length : 0UL;
			foreach (var entry in entries.Where(e => e.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId))
			{
				if (entry.TocDataSize > 0
					&& Fits(entry.TocDataOffset, entry.TocDataSize, tocLength)
					&& Fits(entry.StreamOffset, entry.StreamSize, streamLength)
					&& Fits(entry.GpuResourceOffset, entry.GpuResourceSize, gpuLength))
					units.Add(entry.AssetKey.FileId);
				else units.Remove(entry.AssetKey.FileId);
			}
		}
		return units;
	}

	private static bool Fits(ulong offset, uint size, ulong length)
		=> size == 0 || offset <= length && size <= length - offset;

	public static RuntimeArmorRegistryDocument Compile(RuntimeArmorRegistryDocument source, IReadOnlySet<ulong> available, bool rejectEmpty = true)
	{
		var models = source.ModelDefinitions.Select(model => model.SourceItemId is not null ? model : model with
		{
			Pieces = RuntimeArmorModelStructure.Active(model.Pieces ?? [])
				.Where(p => available.Contains(ulong.Parse(p.Unit, NumberStyles.HexNumber, CultureInfo.InvariantCulture))).ToArray()
		}).ToArray();
		var empty = models.Where(m => m.SourceItemId is null && m.Pieces!.Count == 0).Select(m => m.Key).ToHashSet(StringComparer.Ordinal);
		if (source.Enabled && rejectEmpty && source.Mappings.Any(m => m.IsModelMapping && empty.Contains(m.ModelKey!)))
			throw new InvalidDataException("映射使用的自定义素体没有已启用且实际部署的 Unit。请先启用并部署模型包；未修改源定义或运行时协议。");
		return source with
		{
			ModelDefinitions = models.Where(m => !empty.Contains(m.Key)).ToArray(),
			Mappings = source.Mappings.Where(m => !m.IsModelMapping || !empty.Contains(m.ModelKey!)).ToArray()
		};
	}
}
