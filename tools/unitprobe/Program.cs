using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;

// Offline read-only probe for HD2 patch Unit resources.
//
// Two jobs:
//   1. dump every Unit entry with its stream descriptors, per-mesh section
//      counts and semantics, so we can reason about how geometry is laid out;
//   2. emit a section map (--emit-map) that tells a runtime which TOC dwords
//      control the visibility of each decoration.
//
// Why the map exists: a Unit's sections are material batches inside one
// shared vertex pool. When several decorations share a material they also
// share nothing else, so the section boundary is the only usable toggle
// point, and it cannot be recovered from the built asset alone. The build
// side (vpk2patch's manifest) knows the grouping, so this tool re-derives it
// and freezes it into a sidecar file.
//
// Section record layout (24 bytes, verified against UnitMeshReader.cs:679-686
// and the composite path at :415-422):
//   +0  materialIndex (u32)
//   +4  vertexOffset  (u32)
//   +8  numVertices   (u32)
//   +12 indexOffset   (u32)
//   +16 numIndices    (u32)   <- the draw count we toggle
//   +20 groupIndex    (u32)
//
// usage: unitprobe <patch toc file> [--max-units N] [--meshes N] [--sections]
//                                 [--manifest <manifest.json>] [--emit-map <out.json>]

if (args.Length < 1)
{
	Console.Error.WriteLine("usage: unitprobe <patch toc file> [--max-units N] [--meshes N] [--sections] [--manifest <json>] [--emit-map <json>]");
	return 2;
}

var path = Path.GetFullPath(args[0]);
var maxUnits = ArgInt(args, "--max-units", 12);
var maxMeshes = ArgInt(args, "--meshes", 4);
var dumpSections = args.Contains("--sections", StringComparer.Ordinal);
var manifestPath = ArgValue(args, "--manifest");
var emitMapPath = ArgValue(args, "--emit-map");
var emitSectionsPath = ArgValue(args, "--emit-sections");
var groupsPath = ArgValue(args, "--groups");
var onlyFileId = ArgValue(args, "--only");
var patchFileId = ArgValue(args, "--patch-numindices");
var patchValue = ArgInt(args, "--patch-value", 0);

const int SectionRecordSize = 24;
const int NumIndicesOffsetInSection = 16;

// Field offsets inside a 24-byte section record (UnitMeshReader.cs:679-686).
var patchFieldName = (ArgValue(args, "--patch-field") ?? "numindices").ToLowerInvariant();
var patchFieldOffset = patchFieldName switch
{
	"materialindex" => 0,
	"vertexoffset" => 4,
	"numvertices" => 8,
	"indexoffset" => 12,
	"numindices" => 16,
	"groupindex" => 20,
	_ => -1,
};
if (patchFieldOffset < 0)
{
	Console.Error.WriteLine($"unknown --patch-field '{patchFieldName}'; expected materialindex|vertexoffset|numvertices|indexoffset|numindices|groupindex");
	return 2;
}

var jsonOptions = new JsonSerializerOptions
{
	WriteIndented = true,
	DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};

var scanner = new PatchTocScanner();
var entries = await scanner.ScanEntriesAsync(path);
Console.WriteLine($"file     {path}");
Console.WriteLine($"entries  {entries.Count}");

var manifest = manifestPath is null ? null : ReadManifest(manifestPath);
if (manifest is not null)
{
	Console.WriteLine($"manifest {manifestPath} groups={manifest.Groups.Count}");
}

var reader = new PatchUnitMeshReader();
var units = 0;
var failures = 0;
var vertexIds = new Dictionary<ulong, List<string>>();
var indexIds = new Dictionary<ulong, List<string>>();
var collected = new List<UnitMap>();
var tocByFileId = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

foreach (var entry in entries)
{
	if (entry.AssetKey.TypeId != PatchUnitMeshReader.UnitTypeId)
	{
		continue;
	}

	if (onlyFileId is not null)
	{
		var wanted = onlyFileId.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? onlyFileId[2..] : onlyFileId;
		if (!entry.AssetKey.FileId.ToString("x16").Equals(wanted, StringComparison.OrdinalIgnoreCase))
		{
			continue;
		}
	}

	if (units >= maxUnits)
	{
		Console.WriteLine($"... stop after {maxUnits} units");
		break;
	}

	PatchUnitMesh unit;
	try
	{
		unit = await reader.ReadAsync(entry, entries);
	}
	catch (Exception exception)
	{
		failures++;
		Console.WriteLine($"UNIT {entry.EntryIndex} fileId=0x{entry.AssetKey.FileId:x16} READ-FAIL {exception.GetType().Name}: {exception.Message}");
		continue;
	}

	units++;
	var model = unit.Model;
	var payload = unit.Payload;
	var head = model.Meshes.FirstOrDefault(mesh => mesh.LodIndex == 0) ?? model.Meshes.FirstOrDefault();
	var semantic = head?.SemanticInfo ?? UnitMeshSemanticInfo.Empty(0, 0);

	Console.WriteLine();
	Console.WriteLine($"UNIT {entry.EntryIndex} fileId=0x{entry.AssetKey.FileId:x16}");
	Console.WriteLine($"  toc={payload.TocData.Length} gpu={payload.GpuResourceData.Length} stream={payload.StreamData.Length} ver=0x{model.Version:x8} meshes={model.Meshes.Count} streams={model.Streams.Count} materials={model.Materials.Count} boneInfos={model.BoneInfos.Count} transforms={model.TransformInfo.Entries.Count}");
	Console.WriteLine($"  compositeRef=0x{model.CompositeRef:x16} compositePayload={(unit.CompositePayload is not null)} bonesRef=0x{model.BonesRef:x16} lodGroupList={model.UnreversedLodGroupListData.Length}B streamInfoOffset=0x{model.StreamInfoOffset:x} meshInfoOffset=0x{model.MeshInfoOffset:x}");

	foreach (var stream in model.Streams)
	{
		Console.WriteLine($"  s{stream.Index} vId=0x{stream.VertexBufferId:x16} iId=0x{stream.IndexBufferId:x16} vOff={stream.VertexBufferOffset} vSize={stream.VertexBufferSize} iOff={stream.IndexBufferOffset} iSize={stream.IndexBufferSize} nv={stream.NumVertices} ni={stream.NumIndices} stride={stream.VertexStride} ibt={stream.IndexBufferType}");
		Track(vertexIds, stream.VertexBufferId, $"{entry.EntryIndex}:s{stream.Index}");
		Track(indexIds, stream.IndexBufferId, $"{entry.EntryIndex}:s{stream.Index}");
	}

	foreach (var mesh in model.Meshes.Take(maxMeshes))
	{
		var info = mesh.SemanticInfo;
		Console.WriteLine($"  m{mesh.Index} meshId={mesh.MeshId} lod={mesh.LodIndex} xf={mesh.TransformIndex} st={mesh.StreamIndex} nsec={mesh.NumSections} secOff={mesh.SectionsOffset} nmat={mesh.NumMaterials} matOff={mesh.MaterialOffset} sem=[slot={info.Slot} type={info.PieceType} body={info.BodyType} weight={info.Weight} name={info.Name}]");
	}

	if (model.Meshes.Count > maxMeshes)
	{
		Console.WriteLine($"  ... {model.Meshes.Count - maxMeshes} more meshes");
	}

	var materialBySlot = model.Materials
		.GroupBy(binding => binding.SectionId)
		.ToDictionary(group => group.Key, group => group.First().MaterialId);

	var meshMaps = new List<MeshMap>();
	foreach (var mesh in model.Meshes
		.OrderBy(mesh => mesh.LodIndex == 0 ? 0 : 1)
		.ThenBy(mesh => mesh.LodIndex))
	{
		var sectionMaps = mesh.Sections.Select((section, index) => new SectionMap(
			index,
			Hex(section.Offset),
			Hex(section.Offset + NumIndicesOffsetInSection),
			section.NumIndices,
			section.NumIndices / 3,
			section.MaterialIndex,
			section.MaterialSlotId,
			"0x" + (materialBySlot.TryGetValue(section.MaterialSlotId, out var materialId) ? materialId.ToString("x16") : "0"),
			section.GroupIndex,
			section.VertexOffset,
			section.NumVertices,
			section.IndexOffset)).ToArray();

		meshMaps.Add(new MeshMap(mesh.Index, mesh.LodIndex, mesh.MeshId, sectionMaps));

		if (dumpSections && mesh.LodIndex == 0)
		{
			Console.WriteLine($"  SECTIONS of mesh {mesh.Index} (lod={mesh.LodIndex}) count={sectionMaps.Length}");
			foreach (var section in sectionMaps)
			{
				Console.WriteLine($"    sec{section.TocOffset} idxOff={section.IndexOffset,9} nIdx={section.NumIndices,7} vtxOff={section.VertexOffset,9} nVtx={section.NumVertices,7} matIdx={section.MaterialIndex,4} matSlot={section.MaterialSlotId,5} group={section.GroupIndex,4} matId={section.MaterialId}");
			}
		}
	}

	collected.Add(new UnitMap(
		(int)entry.EntryIndex,
		"0x" + entry.AssetKey.TypeId.ToString("x16"),
		"0x" + entry.AssetKey.FileId.ToString("x16"),
		payload.TocData.Length,
		payload.GpuResourceData.Length,
		semantic.Slot,
		semantic.PieceType,
		semantic.BodyType,
		semantic.Weight,
		meshMaps));

	tocByFileId["0x" + entry.AssetKey.FileId.ToString("x16")] = payload.TocData;
}

Console.WriteLine();
Console.WriteLine($"units={units} failures={failures}");
ReportDuplicates("VertexBufferId", vertexIds);
ReportDuplicates("IndexBufferId", indexIds);

if (emitMapPath is not null)
{
	var map = BuildSectionMap(path, collected, manifest, manifestPath);
	if (groupsPath is not null && map.Decorations.Count > 0)
	{
		var grouped = ApplyGrouping(map.Decorations, ReadGrouping(groupsPath));
		if (grouped.Count > 0)
		{
			map = map with { Decorations = grouped };
			Console.WriteLine($"grouping applied from {groupsPath}: {grouped.Count} decorations");
		}
	}
	var fullMapPath = Path.GetFullPath(emitMapPath);
	var directory = Path.GetDirectoryName(fullMapPath);
	if (!string.IsNullOrEmpty(directory))
	{
		Directory.CreateDirectory(directory);
	}

	var (verified, mismatched) = VerifySectionMap(map, tocByFileId);
	await File.WriteAllTextAsync(fullMapPath, JsonSerializer.Serialize(map, jsonOptions));
	Console.WriteLine($"map written {fullMapPath} units={map.Units.Count} decorations={map.Decorations.Count} offsetsVerified={verified} mismatched={mismatched}");
}

if (emitSectionsPath is not null)
{
	var document = BuildSectionDocument(path, collected);
	var fullSectionPath = Path.GetFullPath(emitSectionsPath);
	var sectionDirectory = Path.GetDirectoryName(fullSectionPath);
	if (!string.IsNullOrEmpty(sectionDirectory))
	{
		Directory.CreateDirectory(sectionDirectory);
	}

	await File.WriteAllTextAsync(fullSectionPath, JsonSerializer.Serialize(document, jsonOptions));
	Console.WriteLine($"section map written {fullSectionPath} units={document.Units.Count} sections={document.Units.Sum(unit => unit.Sections.Count)} lodConsistent={document.Units.Count(unit => unit.LodConsistent)}");
}

// In-place edit of section NumIndices values inside the patch file.
//
// Patch TOC payloads are stored raw (PatchEntryPayloadReader reads them straight
// from TocDataOffset with no decompression), so a section's draw count is a plain
// dword we can rewrite without rebuilding the archive. The absolute file offset is
// TocDataOffset + section.Offset + 16.
//
// Spec is <fileId>:<lod>:<sectionIndex> where lod and sectionIndex accept "*".
// Use "*:*" for the decisive experiment: if zeroing every section of every LOD
// still renders, the renderer simply does not read this field.
if (patchFileId is not null)
{
	var parts = patchFileId.Split(':');
	if (parts.Length != 3)
	{
		Console.Error.WriteLine("--patch-numindices expects <fileId>:<lod>:<sectionIndex> (lod and sectionIndex accept *)");
		return 2;
	}

	var allLods = parts[1] == "*";
	var allSections = parts[2] == "*";
	var sectionRange = ParseSectionRange(parts[2]);
	if (!allLods && !int.TryParse(parts[1], out _))
	{
		Console.Error.WriteLine("--patch-numindices expects numeric lod or *");
		return 2;
	}

	if (!allSections && sectionRange is null)
	{
		Console.Error.WriteLine("--patch-numindices expects numeric section, a-b range, or *");
		return 2;
	}

	var patchLod = allLods ? 0 : int.Parse(parts[1]);
	var patchSectionIndex = allSections ? 0 : sectionRange?.From ?? 0;

	var normalized = parts[0].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? parts[0][2..] : parts[0];
	var allUnits = normalized == "*";
	var matchedEntries = entries
		.Where(candidate => candidate.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId
			&& (allUnits || candidate.AssetKey.FileId.ToString("x16").Equals(normalized, StringComparison.OrdinalIgnoreCase)))
		.ToArray();
	if (matchedEntries.Length == 0)
	{
		Console.Error.WriteLine($"no unit entry with fileId {parts[0]}");
		return 1;
	}

	var patchBytes = File.ReadAllBytes(path);
	var backupPath = path + ".bak";
	if (!File.Exists(backupPath))
	{
		File.Copy(path, backupPath);
		Console.WriteLine($"backup written {backupPath}");
	}

	var patched = 0;
	foreach (var target in matchedEntries)
	{
		var patchedUnit = await reader.ReadAsync(target, entries);
		var targets = new List<(UnitMeshInfo Mesh, int SectionIndex, UnitMeshSectionInfo Section)>();
		foreach (var mesh in patchedUnit.Model.Meshes)
		{
			if (allLods || mesh.LodIndex == patchLod)
			{
				for (var index = 0; index < mesh.Sections.Count; index++)
				{
					if (allSections
						|| (sectionRange is { } range && index >= range.From && index <= range.To))
					{
						targets.Add((mesh, index, mesh.Sections[index]));
					}
				}
			}
		}

		if (targets.Count == 0)
		{
			continue;
		}

		var unitPatched = 0;
		foreach (var (mesh, sectionIndex, section) in targets)
		{
			var absolute = (long)target.TocDataOffset + section.Offset + patchFieldOffset;
			if (absolute + 4 > patchBytes.Length)
			{
				Console.Error.WriteLine($"offset 0x{absolute:x} is past the end of {path}");
				continue;
			}

			var previous = BitConverter.ToUInt32(patchBytes, (int)absolute);
			BitConverter.GetBytes((uint)patchValue).CopyTo(patchBytes, absolute);
			patched++;
			unitPatched++;
			if (matchedEntries.Length == 1)
			{
				Console.WriteLine($"  lod={mesh.LodIndex,-2} section={sectionIndex,-2} record=0x{section.Offset:x6} field=0x{absolute:x8} numIndices {previous} -> {patchValue}");
			}
		}

		Console.WriteLine($"  unit 0x{target.AssetKey.FileId:x16} tocOffset=0x{target.TocDataOffset:x} fields={unitPatched}");
	}

	File.WriteAllBytes(path, patchBytes);
	Console.WriteLine($"patched {path}");
	Console.WriteLine($"  units={matchedEntries.Length} fields={patched}");
}

return 0;

static SectionMapFile BuildSectionMap(
	string patchPath,
	IReadOnlyList<UnitMap> units,
	Manifest? manifest,
	string? manifestPath)
{
	var tocBytes = File.ReadAllBytes(patchPath);
	var gpuPath = patchPath + ".gpu_resources";
	var decorations = new List<DecorationMap>();

	if (manifest is not null)
	{
		// The manifest lists one group per decoration in draw order. Verify that
		// claim against every unit's LOD0 triangle counts before trusting it:
		// a silent mismatch would attach names to the wrong geometry.
		foreach (var unit in units)
		{
			var lod0 = Lod0(unit);
			if (lod0 is null || lod0.Sections.Count != manifest.Groups.Count)
			{
				Console.Error.WriteLine($"warning: unit {unit.FileId} has {lod0?.Sections.Count ?? 0} LOD0 sections but manifest has {manifest.Groups.Count} groups; names skipped for this unit");
				continue;
			}

			for (var index = 0; index < lod0.Sections.Count; index++)
			{
				var section = lod0.Sections[index];
				var group = manifest.Groups[index];
				if (section.Triangles != group.Triangles)
				{
					Console.Error.WriteLine($"warning: unit {unit.FileId} section {index} has {section.Triangles} triangles, manifest group '{group.Name}' has {group.Triangles}");
				}
			}
		}

		foreach (var group in manifest.Groups)
		{
			var targets = new List<DecorationTarget>();
			foreach (var unit in units)
			{
				var lod0 = Lod0(unit);
				if (lod0 is null || lod0.Sections.Count != manifest.Groups.Count)
				{
					continue;
				}

				var toggles = new List<SectionToggle>();
				foreach (var mesh in unit.Meshes)
				{
					// Only the real LOD family carries the decoration's sections. The
					// extra lod=-1 meshes are separate single-section statics and would
					// otherwise be mistaken for section 0 of the first decoration.
					if (mesh.Lod < 0 || mesh.Sections.Count != lod0.Sections.Count)
					{
						continue;
					}

					if (group.Order >= mesh.Sections.Count)
					{
						continue;
					}

					var section = mesh.Sections[group.Order];
					toggles.Add(new SectionToggle(mesh.Lod, mesh.MeshIndex, section.Index, section.NumIndicesOffset, section.NumIndices));
				}

				targets.Add(new DecorationTarget(unit.FileId, unit.Slot, unit.PieceType, unit.BodyType, toggles));
			}

			decorations.Add(new DecorationMap(group.Name, "manifest", group.MaterialId, group.Triangles, targets));
		}
	}

	return new SectionMapFile(
		"hd2mod.unit-section-map/1",
		new PatchInfo(patchPath, Sha256(tocBytes), tocBytes.LongLength,
			File.Exists(gpuPath) ? Sha256(File.ReadAllBytes(gpuPath)) : null),
		manifest is null ? null : new ManifestInfo(manifestPath!, Sha256(File.ReadAllBytes(manifestPath!)), manifest.Groups.Count),
		SectionRecordSize,
		NumIndicesOffsetInSection,
		"tocRelative",
		units,
		decorations);
}

// Reads every recorded NumIndices offset back out of the owning entry's TOC
// slice. Section offsets are relative to that slice (UnitMeshReader resolves
// them as itemStart + relativeOffset inside the entry's TocData), not to the
// patch file, so the runtime must add the in-memory TOC base of the unit.
static (int Checked, int Mismatched) VerifySectionMap(
	SectionMapFile map,
	IReadOnlyDictionary<string, byte[]> tocByFileId)
{
	var checkedCount = 0;
	var mismatched = 0;
	foreach (var decoration in map.Decorations)
	{
		foreach (var target in decoration.Targets)
		{
			if (!tocByFileId.TryGetValue(target.FileId, out var tocBytes))
			{
				Console.Error.WriteLine($"verify: no TOC captured for unit {target.FileId}");
				mismatched += target.Sections.Count;
				continue;
			}

			foreach (var toggle in target.Sections)
			{
				var offset = Convert.ToUInt32(toggle.NumIndicesOffset["0x".Length..], 16);
				checkedCount++;
				if (offset + 4 > tocBytes.Length)
				{
					Console.Error.WriteLine($"verify: {decoration.Name} offset {toggle.NumIndicesOffset} is past the TOC end ({tocBytes.Length} bytes)");
					mismatched++;
					continue;
				}

				var actual = BitConverter.ToUInt32(tocBytes, (int)offset);
				if (actual != toggle.NumIndices)
				{
					Console.Error.WriteLine($"verify: {decoration.Name} offset {toggle.NumIndicesOffset} holds {actual}, map says {toggle.NumIndices}");
					mismatched++;
				}
			}
		}
	}

	return (checkedCount, mismatched);
}

static MeshMap? Lod0(UnitMap unit)
	=> unit.Meshes.FirstOrDefault(mesh => mesh.Lod == 0) ?? unit.Meshes.FirstOrDefault();

static GroupingSpec ReadGrouping(string groupingPath)
{
	using var document = JsonDocument.Parse(File.ReadAllBytes(groupingPath));
	var entries = new List<GroupingEntry>();
	if (document.RootElement.TryGetProperty("decorations", out var decorations)
		&& decorations.ValueKind == JsonValueKind.Array)
	{
		foreach (var decoration in decorations.EnumerateArray())
		{
			var name = decoration.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty;
			var members = new List<string>();
			if (decoration.TryGetProperty("members", out var membersElement) && membersElement.ValueKind == JsonValueKind.Array)
			{
				foreach (var member in membersElement.EnumerateArray())
				{
					var value = member.GetString();
					if (!string.IsNullOrEmpty(value))
					{
						members.Add(value);
					}
				}
			}

			if (name.Length > 0 && members.Count > 0)
			{
				entries.Add(new GroupingEntry(name, members));
			}
		}
	}

	return new GroupingSpec(entries);
}

static Manifest? ReadManifest(string manifestPath)
{
	using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
	if (!document.RootElement.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
	{
		return null;
	}

	var list = new List<ManifestGroup>();
	var order = 0;
	foreach (var group in groups.EnumerateArray())
	{
		var name = group.TryGetProperty("group", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty;
		var materialId = group.TryGetProperty("material", out var materialElement) ? materialElement.GetString() ?? string.Empty : string.Empty;
		var triangles = group.TryGetProperty("triangles", out var triangleElement) && triangleElement.TryGetInt32(out var value) ? value : 0;
		list.Add(new ManifestGroup(order++, name, materialId, triangles));
	}

	return new Manifest(manifestPath, list);
}

// Applies an author-supplied grouping on top of the per-material decorations.
//
// The manifest splits geometry by material, which is not always the unit a
// player thinks in: a ribbon and the metal studs on it are two materials but
// one decoration, while a glove and a sleeve may share nothing yet be two.
// Only a human (or the source model's bodygroup structure) can decide that, so
// the grouping is an explicit input rather than something we guess.
//
// Input:  { "decorations": [ { "name": "ribbon", "members": ["ribbon_1", "mat_ornament_pearl"] } ] }
// A member that does not exist is reported and skipped.
static IReadOnlyList<DecorationMap> ApplyGrouping(
	IReadOnlyList<DecorationMap> baseDecorations,
	GroupingSpec grouping)
{
	var byName = baseDecorations.ToDictionary(decoration => decoration.Name, StringComparer.OrdinalIgnoreCase);
	var result = new List<DecorationMap>();
	foreach (var entry in grouping.Decorations)
	{
		var members = new List<DecorationMap>();
		foreach (var name in entry.Members)
		{
			if (byName.TryGetValue(name, out var member))
			{
				members.Add(member);
			}
			else
			{
				Console.Error.WriteLine($"grouping: decoration '{entry.Name}' references unknown member '{name}'");
			}
		}

		if (members.Count == 0)
		{
			continue;
		}

		var targets = members
			.SelectMany(member => member.Targets)
			.GroupBy(target => target.FileId, StringComparer.OrdinalIgnoreCase)
			.Select(group => new DecorationTarget(
				group.Key,
				group.First().Slot,
				group.First().PieceType,
				group.First().BodyType,
				group.SelectMany(target => target.Sections)
					.OrderBy(toggle => toggle.Lod)
					.ThenBy(toggle => toggle.MeshIndex)
					.ThenBy(toggle => toggle.SectionIndex)
					.ToArray()))
			.ToArray();

		result.Add(new DecorationMap(
			entry.Name,
			"grouping",
			string.Join(",", members.Select(member => member.MaterialId)),
			members.Sum(member => member.Triangles),
			targets,
			members.Select(member => member.Name).ToArray()));
	}

	return result;
}

static (int From, int To)? ParseSectionRange(string text)
{
	if (text == "*")
	{
		return null;
	}

	var dash = text.IndexOf('-', StringComparison.Ordinal);
	if (dash > 0
		&& int.TryParse(text[..dash], out var from)
		&& int.TryParse(text[(dash + 1)..], out var to)
		&& to >= from)
	{
		return (from, to);
	}

	return int.TryParse(text, out var single) ? (single, single) : null;
}

// 构建期 section 映射表（v2）：编辑器与 CLI 的输入，运行时不需要它。
// 与 --emit-map 的 v1 区别：以「unit + LOD0 section 下标」为身份，附内容指纹，
// 并把每个 section 在各级 LOD 上的落点列全——编辑器据此画树、CLI 据此校验。
static SectionDocument BuildSectionDocument(string patchPath, IReadOnlyList<UnitMap> units)
{
	var tocBytes = File.ReadAllBytes(patchPath);
	var gpuPath = patchPath + ".gpu_resources";
	var list = new List<SectionUnit>();
	foreach (var unit in units)
	{
		// 只有 lod>=0 的 mesh 属于 LOD 家族；lod=-1 的那些是独立的单节静态件。
		var family = unit.Meshes.Where(mesh => mesh.Lod >= 0).OrderBy(mesh => mesh.Lod).ToArray();
		if (family.Length == 0)
		{
			continue;
		}

		// 基准 mesh = 0 级 LOD 里 section 最多的那个，与运行时的 canonical_mesh 规则一致。
		var canonical = family.Where(mesh => mesh.Lod == 0)
			.OrderByDescending(mesh => mesh.Sections.Count)
			.FirstOrDefault();
		if (canonical is null)
		{
			continue;
		}

		var count = canonical.Sections.Count;
		var consistent = family.All(mesh => mesh.Sections.Count == count);
		var sections = new List<SectionEntry>(count);
		for (var index = 0; index < count; index++)
		{
			var section = canonical.Sections[index];
			var toggles = new List<SectionToggle>();
			// 只收数量与基准相等的 LOD：数量不等说明节序不可对齐，按同一下标写会关错几何。
			foreach (var mesh in family.Where(mesh => mesh.Sections.Count == count))
			{
				var target = mesh.Sections[index];
				toggles.Add(new SectionToggle(mesh.Lod, mesh.MeshIndex, target.Index,
					target.NumIndicesOffset, target.NumIndices));
			}

			sections.Add(new SectionEntry(
				index,
				NormalizeMaterialId(section.MaterialId),
				section.Triangles,
				SectionFingerprint(section.MaterialId, section.Triangles),
				toggles));
		}

		list.Add(new SectionUnit(unit.FileId, unit.Slot, unit.PieceType, unit.BodyType, unit.Weight,
			count, consistent, sections));
	}

	return new SectionDocument(
		"hd2mod.section-map/2",
		new PatchInfo(patchPath, Sha256(tocBytes), tocBytes.LongLength,
			File.Exists(gpuPath) ? Sha256(File.ReadAllBytes(gpuPath)) : null),
		SectionRecordSize,
		NumIndicesOffsetInSection,
		"tocRelative",
		list);
}

// 材质 ID 统一成 16 位小写 hex、不带 0x；解析不出来（全零）时写成 none。
static string NormalizeMaterialId(string? materialId)
{
	var text = (materialId ?? string.Empty).Trim().ToLowerInvariant();
	if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
	{
		text = text[2..];
	}

	if (text.Length != 16 || text.All(character => character == '0'))
	{
		return "none";
	}

	return text;
}

// 指纹 = 材质 ID + 三角形数，按字符串拼接。绝不做数值运算：
// MaterialId 是 64 位，一旦经手浮点就会丢掉低位（Lua 侧已实测踩过）。
static string SectionFingerprint(string? materialId, uint triangles)
	=> NormalizeMaterialId(materialId) + "-" + triangles.ToString();

static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

static string Hex(uint value) => "0x" + value.ToString("x8");

static void Track(Dictionary<ulong, List<string>> map, ulong id, string where)
{
	if (!map.TryGetValue(id, out var list))
	{
		list = [];
		map[id] = list;
	}

	list.Add(where);
}

static void ReportDuplicates(string label, Dictionary<ulong, List<string>> map)
{
	var shared = map.Where(pair => pair.Value.Count > 1).ToArray();
	Console.WriteLine($"{label}: distinct={map.Count} shared-across-streams={shared.Length}");
	foreach (var pair in shared.Take(6))
	{
		Console.WriteLine($"  0x{pair.Key:x16} -> {string.Join(",", pair.Value.Take(6))}");
	}
}

static string? ArgValue(string[] argv, string name)
{
	for (var i = 0; i < argv.Length - 1; i++)
	{
		if (string.Equals(argv[i], name, StringComparison.Ordinal))
		{
			return argv[i + 1];
		}
	}

	return null;
}

static int ArgInt(string[] argv, string name, int fallback)
{
	for (var i = 0; i < argv.Length - 1; i++)
	{
		if (string.Equals(argv[i], name, StringComparison.Ordinal)
			&& int.TryParse(argv[i + 1], out var value))
		{
			return value;
		}
	}

	return fallback;
}

internal sealed record PatchInfo(
	[property: JsonPropertyName("path")] string Path,
	[property: JsonPropertyName("sha256")] string Sha256,
	[property: JsonPropertyName("size")] long Size,
	[property: JsonPropertyName("gpuResourcesSha256")] string? GpuResourcesSha256);

internal sealed record ManifestInfo(
	[property: JsonPropertyName("path")] string Path,
	[property: JsonPropertyName("sha256")] string Sha256,
	[property: JsonPropertyName("groupCount")] int GroupCount);

internal sealed record SectionMap(
	[property: JsonPropertyName("index")] int Index,
	[property: JsonPropertyName("tocOffset")] string TocOffset,
	[property: JsonPropertyName("numIndicesOffset")] string NumIndicesOffset,
	[property: JsonPropertyName("numIndices")] uint NumIndices,
	[property: JsonPropertyName("triangles")] uint Triangles,
	[property: JsonPropertyName("materialIndex")] uint MaterialIndex,
	[property: JsonPropertyName("materialSlotId")] uint MaterialSlotId,
	[property: JsonPropertyName("materialId")] string MaterialId,
	[property: JsonPropertyName("groupIndex")] uint GroupIndex,
	[property: JsonPropertyName("vertexOffset")] uint VertexOffset,
	[property: JsonPropertyName("numVertices")] uint NumVertices,
	[property: JsonPropertyName("indexOffset")] uint IndexOffset);

internal sealed record MeshMap(
	[property: JsonPropertyName("meshIndex")] int MeshIndex,
	[property: JsonPropertyName("lod")] int Lod,
	[property: JsonPropertyName("meshId")] uint MeshId,
	[property: JsonPropertyName("sections")] IReadOnlyList<SectionMap> Sections);

internal sealed record UnitMap(
	[property: JsonPropertyName("entryIndex")] int EntryIndex,
	[property: JsonPropertyName("typeId")] string TypeId,
	[property: JsonPropertyName("fileId")] string FileId,
	[property: JsonPropertyName("tocSize")] int TocSize,
	[property: JsonPropertyName("gpuSize")] int GpuSize,
	[property: JsonPropertyName("slot")] string Slot,
	[property: JsonPropertyName("pieceType")] string PieceType,
	[property: JsonPropertyName("bodyType")] string BodyType,
	[property: JsonPropertyName("weight")] string Weight,
	[property: JsonPropertyName("meshes")] IReadOnlyList<MeshMap> Meshes);

internal sealed record SectionToggle(
	[property: JsonPropertyName("lod")] int Lod,
	[property: JsonPropertyName("meshIndex")] int MeshIndex,
	[property: JsonPropertyName("sectionIndex")] int SectionIndex,
	[property: JsonPropertyName("numIndicesOffset")] string NumIndicesOffset,
	[property: JsonPropertyName("numIndices")] uint NumIndices);

internal sealed record DecorationTarget(
	[property: JsonPropertyName("fileId")] string FileId,
	[property: JsonPropertyName("slot")] string Slot,
	[property: JsonPropertyName("pieceType")] string PieceType,
	[property: JsonPropertyName("bodyType")] string BodyType,
	[property: JsonPropertyName("sections")] IReadOnlyList<SectionToggle> Sections);

internal sealed record DecorationMap(
	[property: JsonPropertyName("name")] string Name,
	[property: JsonPropertyName("source")] string Source,
	[property: JsonPropertyName("materialId")] string MaterialId,
	[property: JsonPropertyName("triangles")] int Triangles,
	[property: JsonPropertyName("targets")] IReadOnlyList<DecorationTarget> Targets,
	[property: JsonPropertyName("members")] IReadOnlyList<string>? Members = null);

internal sealed record GroupingSpec(IReadOnlyList<GroupingEntry> Decorations);

internal sealed record GroupingEntry(string Name, IReadOnlyList<string> Members);

internal sealed record SectionMapFile(
	[property: JsonPropertyName("schema")] string Schema,
	[property: JsonPropertyName("patch")] PatchInfo Patch,
	[property: JsonPropertyName("manifest")] ManifestInfo? Manifest,
	[property: JsonPropertyName("sectionRecordSize")] int SectionRecordSize,
	[property: JsonPropertyName("numIndicesOffsetInSection")] int NumIndicesOffsetInSection,
	[property: JsonPropertyName("offsets")] string Offsets,
	[property: JsonPropertyName("units")] IReadOnlyList<UnitMap> Units,
	[property: JsonPropertyName("decorations")] IReadOnlyList<DecorationMap> Decorations);

internal sealed record SectionDocument(
	[property: JsonPropertyName("schema")] string Schema,
	[property: JsonPropertyName("patch")] PatchInfo Patch,
	[property: JsonPropertyName("sectionRecordSize")] int SectionRecordSize,
	[property: JsonPropertyName("numIndicesOffsetInSection")] int NumIndicesOffsetInSection,
	[property: JsonPropertyName("offsets")] string Offsets,
	[property: JsonPropertyName("units")] IReadOnlyList<SectionUnit> Units);

internal sealed record SectionUnit(
	[property: JsonPropertyName("fileId")] string FileId,
	[property: JsonPropertyName("slot")] string Slot,
	[property: JsonPropertyName("pieceType")] string PieceType,
	[property: JsonPropertyName("bodyType")] string BodyType,
	[property: JsonPropertyName("weight")] string Weight,
	[property: JsonPropertyName("sectionCount")] int SectionCount,
	[property: JsonPropertyName("lodConsistent")] bool LodConsistent,
	[property: JsonPropertyName("sections")] IReadOnlyList<SectionEntry> Sections);

internal sealed record SectionEntry(
	[property: JsonPropertyName("index")] int Index,
	[property: JsonPropertyName("materialId")] string MaterialId,
	[property: JsonPropertyName("triangles")] uint Triangles,
	[property: JsonPropertyName("fingerprint")] string Fingerprint,
	[property: JsonPropertyName("toggles")] IReadOnlyList<SectionToggle> Toggles);

internal sealed record Manifest(string Path, IReadOnlyList<ManifestGroup> Groups);

internal sealed record ManifestGroup(int Order, string Name, string MaterialId, int Triangles);
