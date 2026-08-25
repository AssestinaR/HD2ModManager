using System.Numerics;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;

namespace HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;

// Purpose: Rebuilds one target LOD BoneInfo and rewrites source vertex fake bone indices for target sections.
// SDK reference: GetMeshData resolves source fake -> RealIndices -> TransformInfo.NameHashes; BoneInfo.SetRemap
// creates target RealIndices/Remaps; GetMeshData writes inverse joint matrices relative to the mesh transform.
public sealed record CanonicalBoneRebuildResult(
	UnitBoneInfo? BoneInfo,
	UnitRawMeshData? Mesh,
	IReadOnlyList<CanonicalPlanDiagnostic> Diagnostics)
{
	public bool IsValid => BoneInfo is not null && Mesh is not null && Diagnostics.Count == 0;
}

public sealed class CanonicalBoneRebuilder
{
	private static void Trace(string message) => System.Diagnostics.Trace.WriteLine($"[CanonicalBoneRebuilder] {message}");

	public CanonicalBoneRebuildResult TryRebuild(
		UnitMeshModel source,
		UnitRawMeshData sourceMesh,
		UnitMeshModel target,
		UnitRawMeshData targetMesh)
	{
		ArgumentNullException.ThrowIfNull(source);
		ArgumentNullException.ThrowIfNull(sourceMesh);
		ArgumentNullException.ThrowIfNull(target);
		ArgumentNullException.ThrowIfNull(targetMesh);
		var diagnostics = new List<CanonicalPlanDiagnostic>();
		if (target.CompositeRef != 0)
			diagnostics.Add(new("UnsupportedCompositeLayout", "Canonical BoneInfo rebuilding supports one target mesh and rejects Composite-backed targets."));
		var targetInfo = target.Meshes.Where(mesh => mesh.Index == targetMesh.MeshInfoIndex).ToArray();
		if (targetInfo.Length != 1)
			diagnostics.Add(new("TargetMeshCardinality", $"Target MeshInfo {targetMesh.MeshInfoIndex} must identify exactly one mesh."));
		if (targetMesh.LodIndex < 0 || targetMesh.LodIndex >= target.BoneInfos.Count)
			diagnostics.Add(new("TargetBoneInfoMissing", "The target mesh does not identify one valid BoneInfo/Lod."));
		var sourceInfo = FindBoneInfo(source, sourceMesh, "source", diagnostics);
		var targetBoneInfo = FindBoneInfo(target, targetMesh, "target", diagnostics);
		if (sourceInfo is null || targetBoneInfo is null || diagnostics.Count != 0)
			return new(null, null, Array.AsReadOnly(diagnostics.ToArray()));

		var sourceHashes = ResolveRealHashes(sourceInfo, source.TransformNameHashes, diagnostics, "source");
		var sectionLayout = CanonicalSectionLayout.TryCreate(sourceMesh, targetMesh);
		diagnostics.AddRange(sectionLayout.Diagnostics);
		if (!sectionLayout.IsValid)
			return new(null, null, Array.AsReadOnly(diagnostics.ToArray()));
		var sourceVertexMaterials = CanonicalVertexMaterialResolver.Resolve(sourceMesh);
		diagnostics.AddRange(sourceVertexMaterials.Diagnostics);
		if (!sourceVertexMaterials.IsValid)
			return new(null, null, Array.AsReadOnly(diagnostics.ToArray()));
		var finalTargetMesh = targetMesh with
		{
			Sections = sectionLayout.OutputSections,
			Triangles = sectionLayout.OutputSections.SelectMany(section => section.Triangles).ToArray()
		};
		var materialLayout = CanonicalFinalMaterialLayout.TryCreate(finalTargetMesh);
		diagnostics.AddRange(materialLayout.Diagnostics);
		if (!materialLayout.IsValid)
			return new(null, null, Array.AsReadOnly(diagnostics.ToArray()));
		var activeByMaterial = materialLayout.Slots
			.Select(slot => slot.MaterialOrdinal)
			.ToDictionary(materialIndex => materialIndex, _ => new HashSet<uint>());
		foreach (var assignment in sectionLayout.Assignments)
		{
			var hashes = GetSectionHashes(assignment.SourceSection, sourceMesh, sourceInfo, sourceHashes, sourceVertexMaterials, diagnostics);
			activeByMaterial[materialLayout.GetMaterialOrdinal(assignment.TargetSectionIndex)].UnionWith(hashes);
		}
		if (diagnostics.Count != 0)
			return new(null, null, Array.AsReadOnly(diagnostics.ToArray()));
		Trace($"sourceMesh={sourceMesh.MeshInfoIndex} targetMesh={targetMesh.MeshInfoIndex} sourceSections={sourceMesh.Sections.Count} finalSections={finalTargetMesh.Sections.Count} sourceBones={sourceInfo.RealIndices.Count} targetBones={targetBoneInfo.RealIndices.Count} activeHashes={activeByMaterial.Values.SelectMany(value => value).Distinct().Count()}");

		var targetHashes = target.TransformNameHashes;
		var activeHashes = activeByMaterial.Values.SelectMany(hashes => hashes).Distinct().ToArray();
		var realIndices = new List<uint>();
		foreach (var hash in activeHashes)
		{
			var targetIndex = IndexOf(targetHashes, hash);
			if (targetIndex < 0)
			{
				diagnostics.Add(new("MissingTargetBone", $"Active source bone hash 0x{hash:x8} is absent from target TransformInfo."));
				continue;
			}
			realIndices.Add(checked((uint)targetIndex));
		}
		Trace($"targetMesh={targetMesh.MeshInfoIndex} palette={realIndices.Count} targetHashCount={targetHashes.Count}");
		if (diagnostics.Count != 0)
			return new(null, null, Array.AsReadOnly(diagnostics.ToArray()));

		var materialIndices = activeByMaterial.Keys.OrderBy(index => index).ToArray();
		var remapOffset = checked((uint)(4 + materialIndices.Length * 8));
		var remaps = new List<UnitBoneRemap>();
		foreach (var materialIndex in materialIndices)
		{
			var fakeIndices = activeByMaterial[materialIndex].OrderBy(hash => hash)
				.Select(hash => checked((uint)IndexOf(realIndices, checked((uint)IndexOf(targetHashes, hash)))))
				.ToArray();
			remaps.Add(new UnitBoneRemap(checked((int)materialIndex), remapOffset, fakeIndices));
			remapOffset = checked(remapOffset + checked((uint)(fakeIndices.Length * sizeof(uint))));
		}
		Trace($"targetMesh={targetMesh.MeshInfoIndex} remaps={remaps.Count} remapBytes={remapOffset}");

		var matrices = BuildInverseJointMatrices(target, targetInfo[0], realIndices, diagnostics);
		if (diagnostics.Count != 0)
			return new(null, null, Array.AsReadOnly(diagnostics.ToArray()));
		var rebuiltInfo = targetBoneInfo with
		{
			NumBones = checked((uint)realIndices.Count),
			RealIndices = realIndices,
			Remaps = remaps,
			BoneMatrices = matrices
		};
		var rewritten = RewriteVertices(sourceMesh, finalTargetMesh, sourceInfo, sourceHashes, sourceVertexMaterials, targetHashes, realIndices, remaps, diagnostics);
		if (diagnostics.Count != 0)
			return new(null, null, Array.AsReadOnly(diagnostics.ToArray()));
		return new(rebuiltInfo, rewritten, Array.Empty<CanonicalPlanDiagnostic>());
	}

	private static UnitBoneInfo? FindBoneInfo(UnitMeshModel model, UnitRawMeshData mesh, string role, List<CanonicalPlanDiagnostic> diagnostics)
	{
		if (mesh.LodIndex < 0 || mesh.LodIndex >= model.BoneInfos.Count)
		{
			diagnostics.Add(new("MissingBoneInfo", $"The {role} mesh has no valid BoneInfo/Lod."));
			return null;
		}
		return model.BoneInfos[mesh.LodIndex];
	}

	private static uint[] ResolveRealHashes(UnitBoneInfo info, IReadOnlyList<uint> hashes, List<CanonicalPlanDiagnostic> diagnostics, string role)
	{
		var result = new uint[info.RealIndices.Count];
		for (var index = 0; index < result.Length; index++)
		{
			if (info.RealIndices[index] >= hashes.Count)
				diagnostics.Add(new("InvalidRealBoneIndex", $"The {role} BoneInfo real index is absent from TransformInfo."));
			else result[index] = hashes[(int)info.RealIndices[index]];
		}
		return result;
	}

	private static IEnumerable<uint> GetSectionHashes(UnitRawMeshSectionData section, UnitRawMeshData mesh, UnitBoneInfo info, IReadOnlyList<uint> realHashes, CanonicalVertexMaterialMap vertexMaterials, List<CanonicalPlanDiagnostic> diagnostics)
	{
		var reported = new HashSet<string>(StringComparer.Ordinal);
		foreach (var vertexIndex in section.Triangles.SelectMany(triangle => new[] { triangle.A, triangle.B, triangle.C }).Distinct())
		{
			if (vertexIndex >= mesh.Vertices.Count)
			{
				diagnostics.Add(new("IndexOutOfRange", "A source section references a vertex outside the source mesh."));
				continue;
			}
			if (!vertexMaterials.TryGetMaterial(vertexIndex, out var materialIndex))
			{
				diagnostics.Add(new("MissingSourceVertexMaterial", $"Source vertex {vertexIndex} is used by geometry but has no material assignment."));
				continue;
			}
			var boneComponent = mesh.Vertices[(int)vertexIndex].Components.FirstOrDefault(component => component.Type == 6);
			var indices = boneComponent?.UIntValues ?? Array.Empty<uint>();
			foreach (var fake in indices)
			{
				var remap = info.Remaps.FirstOrDefault(candidate => candidate.MaterialIndex == materialIndex);
				if (remap is null || fake >= remap.FakeIndices.Count || remap.FakeIndices[(int)fake] >= realHashes.Count)
				{
					var reason = remap is null
						? $"no remap for material {materialIndex}"
						: fake >= remap.FakeIndices.Count
							? $"fake index {fake} exceeds remap length {remap.FakeIndices.Count}"
							: $"remap value {remap.FakeIndices[(int)fake]} exceeds RealIndices length {realHashes.Count}";
					var remapSummary = string.Join(',', info.Remaps.Select(item => $"{item.MaterialIndex}:{item.FakeIndices.Count}"));
					var componentDescription = boneComponent is null
						? "component=missing"
						: $"component=format {boneComponent.Format} ({boneComponent.FormatName}), index {boneComponent.Index}, values=[{string.Join(',', boneComponent.UIntValues)}]";
					var message = $"Source mesh {mesh.MeshInfoIndex}, LOD {mesh.LodIndex}, vertex {vertexIndex}, material {materialIndex}, fake {fake}: {reason}; {componentDescription}; remaps={remapSummary}.";
					if (reported.Add(message))
						diagnostics.Add(new("InvalidSourceBoneRemap", message));
					continue;
				}
				yield return realHashes[(int)remap.FakeIndices[(int)fake]];
			}
		}
	}

	private static IReadOnlyList<byte[]> BuildInverseJointMatrices(UnitMeshModel target, UnitMeshInfo mesh, IReadOnlyList<uint> realIndices, List<CanonicalPlanDiagnostic> diagnostics)
	{
		if (mesh.TransformIndex >= target.TransformInfo.Matrices.Count)
		{
			diagnostics.Add(new("MissingMeshTransform", "The target mesh TransformInfo matrix is absent."));
			return Array.Empty<byte[]>();
		}
		var meshMatrix = ToMatrix(target.TransformInfo.Matrices[(int)mesh.TransformIndex], diagnostics);
		if (meshMatrix is null || !TryCreateSdkInverseJointContext(meshMatrix.Value, out var originTransform))
		{
			diagnostics.Add(new("NonInvertibleMeshTransform", "The target mesh TransformInfo matrix is not invertible."));
			return Array.Empty<byte[]>();
		}
		var output = new List<byte[]>(realIndices.Count);
		foreach (var index in realIndices)
		{
			if (index >= target.TransformInfo.Matrices.Count) { diagnostics.Add(new("MissingBoneTransform", "A target bone TransformInfo matrix is absent.")); continue; }
			var bone = ToMatrix(target.TransformInfo.Matrices[(int)index], diagnostics);
			if (bone is null || !TryCreateSdkInverseJoint(bone.Value, originTransform, out var inverseJoint))
			{
				diagnostics.Add(new("NonInvertibleBoneTransform", "A target inverse-joint matrix cannot be generated safely."));
				continue;
			}
			output.Add(Serialize(inverseJoint));
		}
		return output;
	}

	// SDK GetMeshData uses Blender matrices, not the raw Stingray matrix layout:
	// origin = inverse(mesh.ToBlenderMatrix()), then
	// output = inverse(origin * bone.ToBlenderMatrix()).transposed().
	// Keep the conversions explicit here so this path cannot silently diverge from
	// the community exporter when matrix conventions are changed elsewhere.
	private static bool TryCreateSdkInverseJointContext(Matrix4x4 rawMesh, out Matrix4x4 originTransform)
	{
		var blenderMesh = Matrix4x4.Transpose(rawMesh);
		return Matrix4x4.Invert(blenderMesh, out originTransform);
	}

	private static bool TryCreateSdkInverseJoint(Matrix4x4 rawBone, Matrix4x4 originTransform, out Matrix4x4 rawInverseJoint)
	{
		var blenderBone = Matrix4x4.Transpose(rawBone);
		if (!Matrix4x4.Invert(originTransform * blenderBone, out var blenderInverseJoint))
		{
			rawInverseJoint = default;
			return false;
		}
		rawInverseJoint = Matrix4x4.Transpose(blenderInverseJoint);
		return true;
	}

	private static UnitRawMeshData RewriteVertices(UnitRawMeshData sourceMesh, UnitRawMeshData targetMesh, UnitBoneInfo sourceInfo, IReadOnlyList<uint> sourceHashes, CanonicalVertexMaterialMap vertexMaterials, IReadOnlyList<uint> targetHashes, IReadOnlyList<uint> realIndices, IReadOnlyList<UnitBoneRemap> remaps, List<CanonicalPlanDiagnostic> diagnostics)
	{
		var layout = CanonicalSectionLayout.TryCreate(sourceMesh, targetMesh);
		diagnostics.AddRange(layout.Diagnostics);
		if (!layout.IsValid) return sourceMesh;
		var materialLayout = CanonicalFinalMaterialLayout.TryCreate(targetMesh);
		diagnostics.AddRange(materialLayout.Diagnostics);
		if (!materialLayout.IsValid) return sourceMesh;
		var byKey = new Dictionary<(uint Vertex, uint Material), uint>();
		var vertices = new List<UnitRawVertexRecord>();
		var sections = targetMesh.Sections.Select((section, index) => new UnitRawMeshSectionData(materialLayout.GetMaterialOrdinal(index), section.MaterialSlotId, Array.Empty<UnitTriangleIndices>())).ToArray();
		foreach (var assignment in layout.Assignments)
		{
			var section = assignment.SourceSection;
			var targetMaterial = materialLayout.GetMaterialOrdinal(assignment.TargetSectionIndex);
			var targetRemap = remaps.FirstOrDefault(remap => remap.MaterialIndex == targetMaterial);
			if (targetRemap is null) { diagnostics.Add(new("MissingTargetMaterialRemap", "A target material section has no rebuilt BoneInfo remap.")); continue; }
			uint Encode(uint index)
			{
				if (index >= sourceMesh.Vertices.Count) { diagnostics.Add(new("IndexOutOfRange", "A source section references a vertex outside the source mesh.")); return 0; }
				if (!vertexMaterials.TryGetMaterial(index, out var sourceMaterial))
				{
					diagnostics.Add(new("MissingSourceVertexMaterial", $"Source vertex {index} has no material assignment while rewriting bone indices."));
					return 0;
				}
				var key = (index, sourceMaterial);
				if (byKey.TryGetValue(key, out var existing)) return existing;
				var source = sourceMesh.Vertices[(int)index];
				var components = source.Components.Select(component => component.Type != 6 ? component : component with { UIntValues = component.UIntValues.Select(fake => ResolveFake(fake, sourceMaterial, sourceInfo, sourceHashes, targetHashes, realIndices, targetRemap, diagnostics)).ToArray(), RawData = Array.Empty<byte>() }).ToArray();
				var output = checked((uint)vertices.Count); vertices.Add(source with { Index = output, Components = components }); byKey.Add(key, output); return output;
			}
			var triangles = section.Triangles.Select(triangle => new UnitTriangleIndices(Encode(triangle.A), Encode(triangle.B), Encode(triangle.C))).ToArray();
			sections[assignment.TargetSectionIndex] = new UnitRawMeshSectionData(targetMaterial, assignment.TargetSection.MaterialSlotId, triangles);
		}
		return targetMesh with { Sections = sections, Triangles = sections.SelectMany(section => section.Triangles).ToArray(), Vertices = vertices };
	}

	private static uint ResolveFake(uint fake, uint material, UnitBoneInfo sourceInfo, IReadOnlyList<uint> sourceHashes, IReadOnlyList<uint> targetHashes, IReadOnlyList<uint> realIndices, UnitBoneRemap targetRemap, List<CanonicalPlanDiagnostic> diagnostics)
	{
		var sourceRemap = sourceInfo.Remaps.FirstOrDefault(remap => remap.MaterialIndex == material);
		if (sourceRemap is null || fake >= sourceRemap.FakeIndices.Count || sourceRemap.FakeIndices[(int)fake] >= sourceInfo.RealIndices.Count) { diagnostics.Add(new("InvalidSourceBoneRemap", $"A source Type=6 index cannot be remapped for material {material}.")); return 0; }
		var hash = sourceHashes[(int)sourceRemap.FakeIndices[(int)fake]];
		var targetIndex = IndexOf(targetHashes, hash);
		if (targetIndex < 0)
		{
			diagnostics.Add(new("MissingTargetBone", $"Source bone hash 0x{hash:x8} is absent from target TransformInfo."));
			return 0;
		}
		var realPosition = IndexOf(realIndices, checked((uint)targetIndex));
		if (targetIndex < 0 || realPosition < 0) { diagnostics.Add(new("MissingTargetBone", "An active source bone is absent from the target palette.")); return 0; }
		var fakePosition = IndexOf(targetRemap.FakeIndices, checked((uint)realPosition));
		if (fakePosition < 0) { diagnostics.Add(new("MissingTargetBoneRemap", "An active target bone is absent from the target material remap.")); return 0; }
		return checked((uint)fakePosition);
	}

	private static Matrix4x4? ToMatrix(UnitTransformMatrix matrix, List<CanonicalPlanDiagnostic> diagnostics)
	{
		if (matrix.Values.Count != 16 || matrix.Values.Any(value => !float.IsFinite(value))) { diagnostics.Add(new("InvalidTransformInfoMatrix", "TransformInfo matrix is not a finite 4x4 matrix.")); return null; }
		var v = matrix.Values; return new Matrix4x4(v[0],v[1],v[2],v[3],v[4],v[5],v[6],v[7],v[8],v[9],v[10],v[11],v[12],v[13],v[14],v[15]);
	}
	private static byte[] Serialize(Matrix4x4 matrix) => [.. new[] { matrix.M11,matrix.M12,matrix.M13,matrix.M14,matrix.M21,matrix.M22,matrix.M23,matrix.M24,matrix.M31,matrix.M32,matrix.M33,matrix.M34,matrix.M41,matrix.M42,matrix.M43,matrix.M44 }.SelectMany(BitConverter.GetBytes)];
	private static int IndexOf(IReadOnlyList<uint> values, uint value) { for (var i=0;i<values.Count;i++) if (values[i]==value) return i; return -1; }
}
