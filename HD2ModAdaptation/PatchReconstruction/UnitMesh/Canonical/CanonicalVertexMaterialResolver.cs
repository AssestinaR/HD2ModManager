namespace HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;

/// <summary>
/// Reconstructs the vertex-to-material lookup used by the community SDK.
/// The SDK does not select a BoneInfo remap from the section alone; it selects
/// it from the material owning each indexed vertex.
/// </summary>
public sealed record CanonicalVertexMaterialMap(
	IReadOnlyDictionary<uint, uint> MaterialByVertex,
	IReadOnlyList<CanonicalPlanDiagnostic> Diagnostics)
{
	public bool IsValid => Diagnostics.Count == 0;

	public bool TryGetMaterial(uint vertexIndex, out uint materialIndex)
		=> MaterialByVertex.TryGetValue(vertexIndex, out materialIndex);
}

public static class CanonicalVertexMaterialResolver
{
	public static CanonicalVertexMaterialMap Resolve(UnitRawMeshData mesh)
	{
		ArgumentNullException.ThrowIfNull(mesh);

		var materialByVertex = new Dictionary<uint, uint>();
		var diagnostics = new List<CanonicalPlanDiagnostic>();

		// SDK behaviour: vertex_to_material_index is initialized as unknown and
		// then overwritten while iterating material faces. If a malformed mesh
		// references the same vertex from multiple material sections, preserving
		// the later assignment matches that behaviour instead of inventing a
		// section-wide remap.
		foreach (var section in mesh.Sections)
		{
			foreach (var triangle in section.Triangles)
			{
				Assign(triangle.A, section.MaterialIndex, mesh, materialByVertex, diagnostics);
				Assign(triangle.B, section.MaterialIndex, mesh, materialByVertex, diagnostics);
				Assign(triangle.C, section.MaterialIndex, mesh, materialByVertex, diagnostics);
			}
		}

		return new(materialByVertex, diagnostics.AsReadOnly());
	}

	private static void Assign(
		uint vertexIndex,
		uint materialIndex,
		UnitRawMeshData mesh,
		IDictionary<uint, uint> materialByVertex,
		ICollection<CanonicalPlanDiagnostic> diagnostics)
	{
		if (vertexIndex >= mesh.Vertices.Count)
		{
			diagnostics.Add(new(
				"IndexOutOfRange",
				$"A mesh section references vertex {vertexIndex}, but the mesh contains only {mesh.Vertices.Count} vertices."));
			return;
		}

		materialByVertex[vertexIndex] = materialIndex;
	}
}
