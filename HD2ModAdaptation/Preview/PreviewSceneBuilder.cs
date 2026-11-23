using System.Globalization;
using System.Numerics;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;

namespace HD2ModAdaptation.Preview;

/// <summary>一个已经解码好的 unit，交给场景构建器。</summary>
public sealed record PreviewUnitSource(string UnitHex, UnitMeshModel Model);

/// <summary>
/// 把 patch / unit 里的网格整理成渲染器能直接吃的场景（规划 P0）。
/// <para>
/// 刻意<b>不做</b>的事：不解材质、不解贴图、不驱动骨骼、不猜 section 语义。
/// 它只回答"这些 unit 里有哪些可见网格、顶点在哪、三角形怎么连、整体多大"。
/// </para>
/// </summary>
public static class PreviewSceneBuilder
{
	/// <summary>顶点分量类型（<c>UnitMeshReader.GetComponentTypeName</c>）：0 = position。</summary>
	public const uint PositionComponentType = 0;

	/// <summary>顶点分量类型：1 = normal（打包八面体法线，读取时已解成 3 个 float）。</summary>
	public const uint NormalComponentType = 1;

	/// <summary>顶点分量类型：4 = uv。</summary>
	public const uint UvComponentType = 4;

	/// <summary>
	/// 从一组 patch 文件构建场景。后出现的 patch 覆盖先出现的同 id unit
	/// （调用方负责把生效层排在后面，见规划 §5 的"必须走生效载荷"）。
	/// </summary>
	/// <param name="patchPaths">要读的 patch 文件（不含 <c>.gpu_resources</c> / <c>.stream</c> 侧车）。</param>
	/// <param name="unitFilter">只读这些 unit id；<c>null</c> = 全部。</param>
	public static async ValueTask<PreviewScene> BuildAsync(
		IReadOnlyList<string> patchPaths,
		IReadOnlyCollection<ulong>? unitFilter = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(patchPaths);
		var notes = new List<string>();
		var units = new List<PreviewUnitSource>();
		var slotByUnit = new Dictionary<ulong, int>();
		var scanner = new PatchTocScanner();
		var reader = new PatchUnitMeshReader();
		var entriesSeen = 0;
		var failed = 0;

		foreach (var path in patchPaths)
		{
			cancellationToken.ThrowIfCancellationRequested();
			IReadOnlyList<PatchTocEntry> entries;
			try
			{
				entries = await scanner.ScanEntriesAsync(path, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is IOException
				or InvalidDataException
				or UnauthorizedAccessException)
			{
				notes.Add($"读不出 patch 的目录表：{Path.GetFileName(path)}：{exception.Message}");
				continue;
			}

			foreach (var entry in entries)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (entry.AssetKey.TypeId != PatchUnitMeshReader.UnitTypeId) continue;
				entriesSeen++;
				if (unitFilter is not null && !unitFilter.Contains(entry.AssetKey.FileId)) continue;

				var hex = entry.AssetKey.FileId.ToString("x16", CultureInfo.InvariantCulture);
				try
				{
					// AllowExternalCompositeReference：替换类 patch 常常不带 Composite，
					// 预览只要几何，没必要因此整份失败。
					var mesh = await reader.ReadCanonicalSourceAsync(
						entry, entries, PatchUnitDependencyPolicy.AllowExternalCompositeReference, cancellationToken)
						.ConfigureAwait(false);
					var source = new PreviewUnitSource(hex, mesh.Model);
					if (slotByUnit.TryGetValue(entry.AssetKey.FileId, out var slot)) units[slot] = source;
					else
					{
						slotByUnit[entry.AssetKey.FileId] = units.Count;
						units.Add(source);
					}
				}
				catch (Exception exception) when (exception is IOException
					or InvalidDataException
					or UnauthorizedAccessException
					or NotSupportedException
					or OverflowException)
				{
					failed++;
					notes.Add($"unit 0x{hex} 读不出网格：{exception.Message}");
				}
				catch (ArgumentException exception)
				{
					// 坏偏移会从 Slice/Span 里冒 ArgumentException；按同样口径记一笔，不让它打断整份载荷。
					failed++;
					notes.Add($"unit 0x{hex} 的网格数据越界：{exception.Message}");
				}
			}
		}

		return BuildScene(units, notes, patchPaths.Count, entriesSeen, failed);
	}

	/// <summary>只从已解码的 unit 构景（不碰盘）。CLI / GUI 复用同一份。</summary>
	public static PreviewScene BuildScene(IReadOnlyList<PreviewUnitSource> units)
		=> BuildScene(units, null, 0, units.Count, 0);

	/// <summary>构景核心。</summary>
	public static PreviewScene BuildScene(
		IReadOnlyList<PreviewUnitSource> units,
		IReadOnlyList<string>? notes,
		int patchCount,
		int unitEntries,
		int unitsFailed)
	{
		ArgumentNullException.ThrowIfNull(units);

		var meshes = new List<PreviewMesh>(units.Count);
		var notesOut = notes is null ? new List<string>() : [.. notes];
		var meshesSeen = 0;
		var cullingBodies = 0;
		var staticMeshes = 0;
		var higherLods = 0;
		var fallbackLods = 0;
		var placeholders = 0;
		var droppedTriangles = 0;
		var min = new Vector3(float.PositiveInfinity);
		var max = new Vector3(float.NegativeInfinity);

		foreach (var unit in units)
		{
			ArgumentNullException.ThrowIfNull(unit);

			// 每个 unit 只画**一个 LOD 层**：优先 LOD0；整份 unit 没有 LOD0 时才回退到 -1（单 LOD 的 unit）。
			// 不能两种都画：实测有 unit 同时带 lod=0 与一份 lod=-1 的 3 顶点占位网格，
			// 两个都留就会在模型上多出一个三角形碎片。
			// "有没有真几何"用仓库已有的权威判据（UnitGeometryFactsBuilder），不在这里另立阈值。
			var hasLod0 = false;
			foreach (var raw in unit.Model.RawMeshData)
			{
				if (raw.LodIndex != 0) continue;
				if (ResolveKind(unit.Model, raw) != PreviewMeshKind.Visual) continue;
				if (!UnitGeometryFactsBuilder.HasRenderableGeometry(raw)) continue;
				hasLod0 = true;
				break;
			}

			foreach (var raw in unit.Model.RawMeshData)
			{
				meshesSeen++;

				if (raw.LodIndex > 0)
				{
					higherLods++;
					continue;
				}

				if (raw.LodIndex < 0 && hasLod0)
				{
					fallbackLods++;
					continue;
				}

				var kind = ResolveKind(unit.Model, raw);
				if (kind == PreviewMeshKind.CullingBody) { cullingBodies++; continue; }
				if (kind == PreviewMeshKind.StaticMesh) { staticMeshes++; continue; }

				if (!UnitGeometryFactsBuilder.HasRenderableGeometry(raw))
				{
					placeholders++;
					continue;
				}

				var (vertices, remap) = ReadVertices(raw);
				if (vertices.Length == 0)
				{
					notesOut.Add($"unit 0x{unit.UnitHex} 的 mesh {raw.MeshInfoIndex} 解不出任何顶点（没有 position 分量？）");
					continue;
				}

				var triangles = new List<PreviewTriangle>(raw.Sections.Count * 8);
				foreach (var section in raw.Sections)
				{
					foreach (var triangle in section.Triangles)
					{
						if (!remap.TryGetValue(triangle.A, out var a)
							|| !remap.TryGetValue(triangle.B, out var b)
							|| !remap.TryGetValue(triangle.C, out var c)
							|| a == b || b == c || a == c)
						{
							droppedTriangles++;
							continue;
						}

						triangles.Add(new PreviewTriangle(a, b, c, section.MaterialSlotId));
					}
				}

				if (triangles.Count == 0)
				{
					notesOut.Add($"unit 0x{unit.UnitHex} 的 mesh {raw.MeshInfoIndex} 有 {vertices.Length} 个顶点但 0 个可用三角形");
					continue;
				}

				foreach (var vertex in vertices)
				{
					min = Vector3.Min(min, vertex.Position);
					max = Vector3.Max(max, vertex.Position);
				}

				meshes.Add(new PreviewMesh(unit.UnitHex, raw.LodIndex, raw.MeshInfoIndex, kind, vertices, triangles));
			}
		}

		var bounds = ComposeBounds(meshes, min, max);
		var verticesTotal = 0;
		var trianglesTotal = 0;
		foreach (var mesh in meshes)
		{
			verticesTotal += mesh.Vertices.Count;
			trianglesTotal += mesh.Triangles.Count;
		}

		return new PreviewScene(
			meshes,
			bounds,
			new PreviewSceneStats(
				patchCount,
				unitEntries,
				units.Count,
				unitsFailed,
				meshesSeen,
				meshes.Count,
				verticesTotal,
				trianglesTotal,
				cullingBodies,
				staticMeshes,
				higherLods,
				fallbackLods,
				placeholders,
				droppedTriangles)
			{
				Notes = notesOut,
			});
	}

	/// <summary>按 mesh 的语义信息判定它属于哪一类；找不到对应 mesh 信息时按可见处理（宁可多画，也不要静默丢件）。</summary>
	private static PreviewMeshKind ResolveKind(UnitMeshModel model, UnitRawMeshData raw)
	{
		var info = model.Meshes.FirstOrDefault(candidate => candidate.Index == raw.MeshInfoIndex);
		if (info is null) return PreviewMeshKind.Visual;
		if (info.SemanticInfo.IsCullingBody) return PreviewMeshKind.CullingBody;
		if (info.SemanticInfo.IsStaticMesh) return PreviewMeshKind.StaticMesh;
		return PreviewMeshKind.Visual;
	}

	/// <summary>
	/// 顶点：按分量类型取 position / normal / uv，缺失 position 的顶点直接丢掉，
	/// 其余压成连续数组（三角形的索引跟着重映射）。
	/// </summary>
	private static (PreviewVertex[] Vertices, Dictionary<uint, int> Remap) ReadVertices(UnitRawMeshData raw)
	{
		var parsed = new Dictionary<uint, PreviewVertex>(raw.Vertices.Count);
		foreach (var record in raw.Vertices)
		{
			Vector3? position = null;
			var normal = Vector3.Zero;
			var uv = Vector2.Zero;
			foreach (var component in record.Components)
			{
				switch (component.Type)
				{
					case PositionComponentType when component.FloatValues.Length >= 3:
						position = new Vector3(
							component.FloatValues[0], component.FloatValues[1], component.FloatValues[2]);
						break;
					case NormalComponentType when component.FloatValues.Length >= 3:
						normal = new Vector3(
							component.FloatValues[0], component.FloatValues[1], component.FloatValues[2]);
						break;
					case UvComponentType when component.Index == 0 && component.FloatValues.Length >= 2:
						uv = new Vector2(component.FloatValues[0], component.FloatValues[1]);
						break;
				}
			}

			if (position is null) continue;
			parsed[record.Index] = new PreviewVertex(position.Value, normal, uv);
		}

		if (parsed.Count == 0) return ([], []);

		var order = new uint[parsed.Count];
		parsed.Keys.CopyTo(order, 0);
		Array.Sort(order);

		var vertices = new PreviewVertex[order.Length];
		var remap = new Dictionary<uint, int>(order.Length);
		for (var index = 0; index < order.Length; index++)
		{
			vertices[index] = parsed[order[index]];
			remap[order[index]] = index;
		}

		return (vertices, remap);
	}

	/// <summary>包围球：中心取包围盒中心，半径取"最远顶点到中心的距离"（于是任何角度都框得住）。</summary>
	private static PreviewBoundingSphere ComposeBounds(IReadOnlyList<PreviewMesh> meshes, Vector3 min, Vector3 max)
	{
		if (meshes.Count == 0) return PreviewBoundingSphere.Degenerate;

		var center = (min + max) * 0.5f;
		var radiusSquared = 0f;
		foreach (var mesh in meshes)
		{
			foreach (var vertex in mesh.Vertices)
			{
				var distance = Vector3.DistanceSquared(vertex.Position, center);
				if (distance > radiusSquared) radiusSquared = distance;
			}
		}

		var radius = MathF.Sqrt(radiusSquared);
		// 退化几何（所有顶点重合）也要给个正半径：相机取景会拿它当除数。
		if (!(radius > 1e-6f) || float.IsNaN(radius) || float.IsInfinity(radius)) radius = 1f;
		return new PreviewBoundingSphere(center, radius) { Min = min, Max = max };
	}
}
