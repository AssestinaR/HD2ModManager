using HD2ModAdaptation.Processing;

namespace HD2ModAdaptation.PatchReconstruction.UnitMesh;

// Purpose: Builds a current target Unit shell as new patch entries from explicit source-to-target mesh mappings.
[Obsolete("已过时：替换护甲生产统一使用 DirectSourceUnitReuse；Canonical 仅由通用 Patch 重构流程调用。")]
public sealed class TargetShellUnitOutputBuilder
{
	private readonly MeshTransfer transfer;
	private readonly UnitMeshWriter writer;

	public TargetShellUnitOutputBuilder(MeshTransfer? transfer = null, UnitMeshWriter? writer = null)
	{
		this.transfer = transfer ?? new MeshTransfer(allowTargetLayoutConversion: true);
		this.writer = writer ?? new UnitMeshWriter();
	}

	public TargetShellUnitOutput Build(
		GameDataUnitMesh targetShell,
		IReadOnlyCollection<PatchUnitMesh> sourceUnits,
		IReadOnlyCollection<TargetShellMeshMapping> mappings,
		TargetShellDependencyPolicy dependencyPolicy)
	{
		ArgumentNullException.ThrowIfNull(targetShell);
		ArgumentNullException.ThrowIfNull(sourceUnits);
		ArgumentNullException.ThrowIfNull(mappings);
		if (mappings.Count == 0)
		{
			throw new InvalidDataException("At least one explicit source-to-target mesh mapping is required.");
		}

		var sourceByKey = sourceUnits.ToDictionary(unit => unit.Entry.AssetKey);
		var targetIndexes = new HashSet<int>();
		var model = targetShell.Model;
		foreach (var mapping in mappings)
		{
			if (!targetIndexes.Add(mapping.TargetMeshInfoIndex))
			{
				throw new InvalidDataException($"Target mesh {mapping.TargetMeshInfoIndex} has more than one explicit source mapping.");
			}
			if (!sourceByKey.TryGetValue(mapping.SourceUnitAssetKey, out var source))
			{
				throw new KeyNotFoundException($"Source Unit 0x{mapping.SourceUnitAssetKey.FileId:x16} was not supplied for the explicit mapping.");
			}

			model = transfer.Transfer(model, mapping.TargetMeshInfoIndex, source.Model, mapping.SourceMeshInfoIndex).Model;
		}

		var writeResult = targetShell.CompositePayload is null
			? writer.Write(model, targetShell.Payload.TocData)
			: writer.Write(model, targetShell.Payload.TocData, targetShell.CompositePayload.TocData);
		var additions = new List<PatchArchiveAdditionalEntry>
		{
			CreateAdditionalEntry(targetShell.Payload.Entry, writeResult.TocData, Array.Empty<byte>(), writeResult.GpuData)
		};
		if (dependencyPolicy == TargetShellDependencyPolicy.EmbedReferencedComposite && writeResult.CompositeTocData is not null && targetShell.CompositePayload is not null)
		{
			additions.Add(CreateAdditionalEntry(targetShell.CompositePayload.Entry, writeResult.CompositeTocData, Array.Empty<byte>(), writeResult.CompositeGpuData ?? Array.Empty<byte>()));
		}
		else if (dependencyPolicy == TargetShellDependencyPolicy.EmbedReferencedComposite && targetShell.CompositePayload is null)
		{
			throw new InvalidDataException("The requested embedded Composite output is unavailable from the selected target shell.");
		}

		return new TargetShellUnitOutput(targetShell.AssetKey, additions, mappings.Select(mapping => mapping.SourceUnitAssetKey).Distinct().ToArray());
	}

	private static PatchArchiveAdditionalEntry CreateAdditionalEntry(PatchTocEntry entry, byte[] tocData, byte[] streamData, byte[] gpuData)
		=> new(entry.AssetKey, tocData, streamData, gpuData, entry.Unknown1, entry.Unknown2, entry.Unknown3, entry.Unknown4);
}

public enum TargetShellDependencyPolicy
{
	ReferenceCurrentGame,
	EmbedReferencedComposite
}

public sealed record TargetShellMeshMapping(
	AssetKey SourceUnitAssetKey,
	int SourceMeshInfoIndex,
	int TargetMeshInfoIndex);

public sealed record TargetShellUnitOutput(
	AssetKey TargetUnitAssetKey,
	IReadOnlyCollection<PatchArchiveAdditionalEntry> AdditionalEntries,
	IReadOnlyCollection<AssetKey> ReplacedSourceUnitAssetKeys);
