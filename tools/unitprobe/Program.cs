using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;

// Offline read-only probe: dump every Unit entry of a patch archive together
// with its stream descriptors, so we can tell whether vertex buffers are
// addressed per entry (offset relative to the entry's own .gpu_resources
// slice) or through an archive-global identifier.
//
// usage: unitprobe <patch toc file> [--max-units N] [--meshes N]

if (args.Length < 1)
{
	Console.Error.WriteLine("usage: unitprobe <patch toc file> [--max-units N] [--meshes N]");
	return 2;
}

var path = Path.GetFullPath(args[0]);
var maxUnits = ArgInt(args, "--max-units", 12);
var maxMeshes = ArgInt(args, "--meshes", 4);
var dumpSections = args.Contains("--sections", StringComparer.Ordinal);

var scanner = new PatchTocScanner();
var entries = await scanner.ScanEntriesAsync(path);
Console.WriteLine($"file     {path}");
Console.WriteLine($"entries  {entries.Count}");

var reader = new PatchUnitMeshReader();
var units = 0;
var failures = 0;
var vertexIds = new Dictionary<ulong, List<string>>();
var indexIds = new Dictionary<ulong, List<string>>();

foreach (var entry in entries)
{
	if (entry.AssetKey.TypeId != PatchUnitMeshReader.UnitTypeId)
	{
		continue;
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
	Console.WriteLine();
	Console.WriteLine($"UNIT {entry.EntryIndex} fileId=0x{entry.AssetKey.FileId:x16}");
	Console.WriteLine($"  toc={payload.TocData.Length} gpu={payload.GpuResourceData.Length} stream={payload.StreamData.Length} ver=0x{model.Version:x8} meshes={model.Meshes.Count} streams={model.Streams.Count} materials={model.Materials.Count} boneInfos={model.BoneInfos.Count} transforms={model.TransformInfo.Entries.Count}");

	foreach (var stream in model.Streams)
	{
		Console.WriteLine($"  s{stream.Index} vId=0x{stream.VertexBufferId:x16} iId=0x{stream.IndexBufferId:x16} vOff={stream.VertexBufferOffset} vSize={stream.VertexBufferSize} iOff={stream.IndexBufferOffset} iSize={stream.IndexBufferSize} nv={stream.NumVertices} ni={stream.NumIndices} stride={stream.VertexStride} ibt={stream.IndexBufferType}");
		Track(vertexIds, stream.VertexBufferId, $"{entry.EntryIndex}:s{stream.Index}");
		Track(indexIds, stream.IndexBufferId, $"{entry.EntryIndex}:s{stream.Index}");
	}

	foreach (var mesh in model.Meshes.Take(maxMeshes))
	{
		var semantic = mesh.SemanticInfo;
		Console.WriteLine($"  m{mesh.Index} meshId={mesh.MeshId} lod={mesh.LodIndex} xf={mesh.TransformIndex} st={mesh.StreamIndex} nsec={mesh.NumSections} secOff={mesh.SectionsOffset} nmat={mesh.NumMaterials} matOff={mesh.MaterialOffset} sem=[slot={semantic.Slot} type={semantic.PieceType} body={semantic.BodyType} weight={semantic.Weight} name={semantic.Name}]");
	}

	if (model.Meshes.Count > maxMeshes)
	{
		Console.WriteLine($"  ... {model.Meshes.Count - maxMeshes} more meshes");
	}

	if (dumpSections)
	{
		var lod0 = model.Meshes.FirstOrDefault(mesh => mesh.LodIndex == 0) ?? model.Meshes.FirstOrDefault();
		if (lod0 is not null)
		{
			Console.WriteLine($"  SECTIONS of mesh {lod0.Index} (lod={lod0.LodIndex}) count={lod0.Sections.Count}");
			var bySlot = model.Materials
				.GroupBy(binding => binding.SectionId)
				.ToDictionary(group => group.Key, group => group.First().MaterialId);
			foreach (var section in lod0.Sections)
			{
				bySlot.TryGetValue(section.MaterialSlotId, out var materialId);
				Console.WriteLine($"    sec{section.Offset:x8} idxOff={section.IndexOffset,9} nIdx={section.NumIndices,7} vtxOff={section.VertexOffset,9} nVtx={section.NumVertices,7} matIdx={section.MaterialIndex,4} matSlot={section.MaterialSlotId,5} group={section.GroupIndex,4} matId=0x{materialId:x16}");
			}
		}
	}
}

Console.WriteLine();
Console.WriteLine($"units={units} failures={failures}");
ReportDuplicates("VertexBufferId", vertexIds);
ReportDuplicates("IndexBufferId", indexIds);
return 0;

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
