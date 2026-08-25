using System.Buffers.Binary;
using System.Security.Cryptography;

namespace HD2ModAdaptation.PatchReconstruction;

public sealed record CanonicalMaterialFact(
	AssetKey Material,
	bool SourcePresent,
	bool OutputPresent,
	bool SourcePayloadReadable,
	bool OutputPayloadReadable,
	ulong? ParentMaterialId,
	int TextureCount,
	IReadOnlyList<AssetKey> TextureReferences,
	IReadOnlyList<AssetKey> MissingOutputTextures,
	bool OutputPayloadPreserved,
	string? Diagnostic);

public sealed record CanonicalMaterialCompatibilityReport(
	int SourceMaterialCount,
	int OutputMaterialCount,
	int PreservedMaterialCount,
	int MissingOutputMaterialCount,
	int MissingOutputTextureCount,
	int ParseFailureCount,
	bool IsSafeToPreserveSourceMaterials,
	IReadOnlyList<CanonicalMaterialFact> Materials,
	IReadOnlyList<string> Diagnostics)
{
	public bool IsValid => ParseFailureCount == 0 && MissingOutputMaterialCount == 0;
}

/// <summary>
/// Checks source Material preservation and the Material -> Texture closure without
/// replacing Mod materials with GameData materials. The field offsets follow the
/// community SDK StingrayMaterial.Serialize implementation.
/// </summary>
public sealed class CanonicalMaterialCompatibilityAnalyzer
{
	private const int ParentMaterialOffset = 24;
	private const int TextureCountOffset = 64;
	private const int VariableCountOffset = 104;
	private const int TextureRecordsOffset = 136;
	private const int TextureRecordSize = 12;

	private readonly IPatchEntryPayloadReader payloadReader;

	public CanonicalMaterialCompatibilityAnalyzer(IPatchEntryPayloadReader? payloadReader = null)
		=> this.payloadReader = payloadReader ?? new PatchEntryPayloadReader();

	public async ValueTask<CanonicalMaterialCompatibilityReport> AnalyzeAsync(
		IReadOnlyList<PatchTocEntry> sourceEntries,
		IReadOnlyList<PatchTocEntry> outputEntries,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(sourceEntries);
		ArgumentNullException.ThrowIfNull(outputEntries);

		var sourceMaterials = sourceEntries
			.Where(entry => entry.AssetKey.TypeId == MaterialDependencyResolver.MaterialTypeId)
			.GroupBy(entry => entry.AssetKey)
			.ToDictionary(group => group.Key, group => group.Last());
		var outputMaterials = outputEntries
			.Where(entry => entry.AssetKey.TypeId == MaterialDependencyResolver.MaterialTypeId)
			.GroupBy(entry => entry.AssetKey)
			.ToDictionary(group => group.Key, group => group.Last());
		var outputTextures = outputEntries
			.Where(entry => entry.AssetKey.TypeId == MaterialDependencyResolver.TextureTypeId)
			.Select(entry => entry.AssetKey)
			.ToHashSet();

		var facts = new List<CanonicalMaterialFact>();
		var diagnostics = new List<string>();
		foreach (var materialKey in sourceMaterials.Keys.Union(outputMaterials.Keys).OrderBy(key => key.TypeId).ThenBy(key => key.FileId))
		{
			sourceMaterials.TryGetValue(materialKey, out var sourceEntry);
			outputMaterials.TryGetValue(materialKey, out var outputEntry);
			MaterialPayloadInfo? sourceInfo = null;
			MaterialPayloadInfo? outputInfo = null;
			string? diagnostic = null;

			if (sourceEntry is not null)
			{
				try { sourceInfo = Parse((await payloadReader.ReadPayloadAsync(sourceEntry, cancellationToken).ConfigureAwait(false)).TocData); }
				catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or IOException)
				{
					diagnostic = $"来源 Material 解析失败：{exception.Message}";
				}
			}
			if (outputEntry is not null)
			{
				try { outputInfo = Parse((await payloadReader.ReadPayloadAsync(outputEntry, cancellationToken).ConfigureAwait(false)).TocData); }
				catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or IOException)
				{
					diagnostic = string.IsNullOrWhiteSpace(diagnostic)
						? $"输出 Material 解析失败：{exception.Message}"
						: $"{diagnostic}；输出 Material 解析失败：{exception.Message}";
				}
			}

			var textureReferences = sourceInfo?.TextureReferences ?? outputInfo?.TextureReferences ?? [];
			var missingTextures = textureReferences.Where(key => !outputTextures.Contains(key)).Distinct().ToArray();
			var preserved = sourceInfo is not null && outputInfo is not null && sourceInfo.PayloadHash.SequenceEqual(outputInfo.PayloadHash);
			if (sourceEntry is not null && outputEntry is null)
				diagnostic = Append(diagnostic, "输出缺少来源 Material AssetKey");
			if (missingTextures.Length != 0)
				diagnostic = Append(diagnostic, $"输出缺少 {missingTextures.Length} 个来源 Texture");
			if (sourceInfo is not null && outputInfo is not null && !preserved)
				diagnostic = Append(diagnostic, "输出 Material Payload 与来源不一致");

			facts.Add(new(
				materialKey,
				sourceEntry is not null,
				outputEntry is not null,
				sourceInfo is not null,
				outputInfo is not null,
				sourceInfo?.ParentMaterialId ?? outputInfo?.ParentMaterialId,
				(sourceInfo ?? outputInfo)?.TextureReferences.Count ?? 0,
				textureReferences,
				missingTextures,
				preserved,
				diagnostic));
			if (!string.IsNullOrWhiteSpace(diagnostic)) diagnostics.Add($"Material 0x{materialKey.FileId:x16}: {diagnostic}");
		}

		var sourceCount = sourceMaterials.Count;
		var outputCount = outputMaterials.Count;
		var preservedCount = facts.Count(fact => fact.OutputPayloadPreserved);
		var missingMaterialCount = facts.Count(fact => fact.SourcePresent && !fact.OutputPresent);
		var missingTextureCount = facts.Sum(fact => fact.MissingOutputTextures.Count);
		var parseFailureCount = facts.Count(fact => fact.SourcePresent && !fact.SourcePayloadReadable)
			+ facts.Count(fact => fact.OutputPresent && !fact.OutputPayloadReadable);
		return new(
			sourceCount,
			outputCount,
			preservedCount,
			missingMaterialCount,
			missingTextureCount,
			parseFailureCount,
			missingMaterialCount == 0 && missingTextureCount == 0 && parseFailureCount == 0,
			facts,
			diagnostics);
	}

	private static MaterialPayloadInfo Parse(byte[] tocData)
	{
		if (tocData.Length < TextureRecordsOffset)
			throw new InvalidDataException("Material payload header is truncated.");
		var parent = BinaryPrimitives.ReadUInt64LittleEndian(tocData.AsSpan(ParentMaterialOffset, 8));
		var textureCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(tocData.AsSpan(TextureCountOffset, 4)));
		var variableCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(tocData.AsSpan(VariableCountOffset, 4)));
		var textureIdsOffset = checked(TextureRecordsOffset + textureCount * 4);
		var textureIdsEnd = checked(textureIdsOffset + textureCount * 8);
		var variableRecordsEnd = checked(textureIdsEnd + variableCount * 20);
		if (variableRecordsEnd > tocData.Length)
			throw new InvalidDataException($"Material tables exceed payload size: textures={textureCount}, variables={variableCount}.");
		var textures = new List<AssetKey>(textureCount);
		for (var index = 0; index < textureCount; index++)
		{
			var id = BinaryPrimitives.ReadUInt64LittleEndian(tocData.AsSpan(textureIdsOffset + index * 8, 8));
			if (id != 0) textures.Add(new AssetKey(MaterialDependencyResolver.TextureTypeId, id));
		}
		return new(parent, textures, SHA256.HashData(tocData));
	}

	private static string Append(string? current, string next)
		=> string.IsNullOrWhiteSpace(current) ? next : $"{current}；{next}";

	private sealed record MaterialPayloadInfo(ulong ParentMaterialId, IReadOnlyList<AssetKey> TextureReferences, byte[] PayloadHash);
}
