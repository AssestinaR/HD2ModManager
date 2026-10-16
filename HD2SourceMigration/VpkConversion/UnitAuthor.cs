using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using HD2SourceModel;

namespace HD2SourceMigration.VpkConversion;

/// <param name="Donor">结构外壳：一个游戏本体的 Unit（inline 布局、当前 ABI）。</param>
/// <param name="DonorTocData">外壳的原始 TocData，作为"未知区段"的字节保留基线。</param>
/// <param name="AvatarTransformInfo">HD2 avatar 骨架。外壳自带的 TransformInfo 只有十几根骨，必须并进去。</param>
/// <param name="Mesh">本份几何。</param>
/// <param name="Customization">要写进 Unit 的槽位/层级/身形。</param>
/// <param name="DonorUnitFileId">外壳 Unit 的 fileId，用作材质来源身份的命名空间。</param>
/// <param name="MaterialIdsBySourceMaterial">
/// 源材质序号 → 新造的 MaterialId。给了就用它（每个 Unit 引用<b>自己的</b>材质）；
/// 没给（或该序号缺失）才回落到外壳的材质——那条回落是 M2 的过渡状态，会静默产生"贴图是原版护甲的"。
/// </param>
public sealed record UnitAuthorRequest(
	UnitMeshModel Donor,
	byte[] DonorTocData,
	UnitTransformInfo AvatarTransformInfo,
	RetargetedMesh Mesh,
	UnitCustomizationInfo Customization,
	ulong DonorUnitFileId = 0,
	IReadOnlyDictionary<int, ulong>? MaterialIdsBySourceMaterial = null);

public sealed record UnitAuthorResult(
	byte[]? TocData,
	byte[]? GpuData,
	UnitMeshModel? Model,
	IReadOnlyList<string> Diagnostics)
{
	public bool IsValid => TocData is not null && GpuData is not null && Diagnostics.Count == 0;
}

/// <summary>
/// 用游戏本体的一个 Unit 当结构外壳，把一份重定向后的几何写成同结构的 Unit。
/// <para>
/// 步骤刻意与 <c>SameKeyCanonicalUnitRebuilder</c> 同序（骨架扩展 → 逐网格准备 → 材质布局 →
/// 每 LOD 调色板 → 流契约 → 顶点编码 → 重建 → 改槽位），因为那条顺序是已经被真实护甲替换验证过的：
/// 调色板会重写骨骼下标、流契约会定 stride，两者都必须在最终编码之前定下来。
/// </para>
/// <para>
/// 与它不同的一点：我们的"来源"不是另一个 Unit，而是重定向后的裸几何，所以
/// <c>CanonicalMeshSkinningRouter</c> / <c>CanonicalBoneRebuilder</c> 那条源→目标重映射链用不上——
/// 顶点已经直接带着 HD2 骨的<b>名字哈希</b>，调色板按哈希查目标 TransformInfo 即可。
/// </para>
/// </summary>
public sealed class UnitAuthor
{
	private readonly CanonicalPlaceholderMinifier minifier = new();
	private readonly CanonicalLodBonePaletteCompiler paletteCompiler = new();
	private readonly CanonicalStreamContractCompiler streamCompiler = new();
	private readonly CanonicalMeshPreparation preparation = new();
	private readonly CanonicalUnitRebuilder unitRebuilder = new();

	public UnitAuthorResult Author(UnitAuthorRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);
		var diagnostics = new List<string>();

		var capability = CanonicalUnitCapabilityDetector.Analyze(request.Donor);
		if (capability.Layout != CanonicalUnitLayoutKind.Inline || !capability.HasSupportedStreamAbi)
		{
			diagnostics.Add($"外壳不是 inline / 当前 ABI：{capability.Reason}");
			return new(null, null, null, diagnostics);
		}

		// 外壳自带的骨架只有十几根骨，而我们的网格蒙在 HD2 原生骨架的一百多根上。
		// includeAvatarSkeleton 会把整条 avatar 骨架并进来，同名骨用 avatar 的定义覆盖。
		var target = new CanonicalTransformInfoExpander()
			.Expand(request.Donor, [], request.AvatarTransformInfo, includeAvatarSkeleton: true);

		// ⚠️ 外壳的网格变换是"网格局部 → HD2 世界"，必须归一成恒等。
		// 依据：Python 侧 `armor-normalize` 输出的 unit「mesh 变换 = 恒等」，顶点缓冲与
		// `dump_blender.py` 的 `ours_bind` 逐位相同（见 `set_unit_positions.py` 抬头注释），
		// 而 `review_unit_contract.py` 断言逆绑定 `B[i] · M[realIndices[i]] == 单位阵`——里面没有网格项。
		// 不归一的话这个矩阵会被游戏再作用一次：躯干再上移 1.17 m、手臂再外移 0.57 m、左右腿再镜像。
		target = NormalizeMeshTransforms(target);

		var visualLods = target.Meshes.Where(mesh => mesh.LodIndex >= 0).OrderBy(mesh => mesh.LodIndex).ToArray();
		if (visualLods.Length == 0)
		{
			diagnostics.Add("外壳没有任何可视 LOD 网格。");
			return new(null, null, null, diagnostics);
		}

		var finalMeshes = new List<UnitRawMeshData>(target.Meshes.Count);
		var provisional = new List<CanonicalLodBoneInput>();
		var boundsByMesh = new Dictionary<int, UnitMeshInfo>();
		var materialProvenance = new List<CanonicalMaterialSectionProvenance>();
		var boneInfoByMesh = new Dictionary<int, UnitBoneInfo>();

		foreach (var mesh in target.Meshes)
		{
			var stream = target.Streams.SingleOrDefault(candidate => candidate.Index == (int)mesh.StreamIndex);
			if (stream is null)
			{
				diagnostics.Add($"网格 #{mesh.Index} 的流 #{mesh.StreamIndex} 不存在。");
				return new(null, null, null, diagnostics);
			}

			if (mesh.LodIndex < 0)
			{
				// 剔除网格（LodIndex = -1）不参与 LOD 调色板，按外壳原有内容极小化即可。
				var existing = target.RawMeshData.FirstOrDefault(raw => raw.MeshInfoIndex == mesh.Index);
				if (existing is null)
				{
					diagnostics.Add($"网格 #{mesh.Index} 没有可读的 RawMesh。");
					return new(null, null, null, diagnostics);
				}

				var hidden = minifier.TryMinify(existing, stream);
				if (hidden.Mesh is not null)
					finalMeshes.Add(hidden.Mesh);
				continue;
			}

			var built = BuildRawMesh(request, target, mesh, stream, diagnostics);
			if (built is null)
				return new(null, null, null, diagnostics);
			finalMeshes.Add(built.Raw);
			materialProvenance.AddRange(built.Provenance);
			boneInfoByMesh[mesh.Index] = built.BoneInfo;
			boundsByMesh[mesh.Index] = UpdateCullingBounds(mesh, request.Mesh.Vertices);
		}

		// 包围盒必须按我们自己的顶点重算，否则留下的是外壳的盒子（见 UpdateCullingBounds）。
		if (boundsByMesh.Count != 0)
			target = target with
			{
				Meshes = target.Meshes
					.Select(mesh => boundsByMesh.TryGetValue(mesh.Index, out var updated) ? updated : mesh)
					.ToArray(),
			};

		// 材质编译必须在骨骼调色板**之前**：调色板按 section 的槽位建共享材质序号，
		// 而材质编译会重写槽位/序号。顺序反了会静默退回源槽位。
		var compiledMaterials = new CanonicalUnitMaterialLayoutCompiler().TryCompile(target, finalMeshes, materialProvenance);
		if (!compiledMaterials.IsValid)
		{
			diagnostics.AddRange(compiledMaterials.Diagnostics.Select(diagnostic => $"[{diagnostic.Code}] {diagnostic.Message}"));
			return new(null, null, null, diagnostics);
		}

		finalMeshes = compiledMaterials.Meshes.ToList();
		var materialBindings = compiledMaterials.Bindings;

		// ⚠️ 可见段必须都绑到**非零**材质。
		// CanonicalUnitRebuilder.BuildUsedMaterialBindings 会**有意丢弃**未绑定的槽位，
		// 所以"MeshInfo 里有 slot"绝不等于"材质已绑好"；SameKey 路径也只把这种情况记成
		// observation、不算失败。这里把它变成硬失败——否则又会退化成"外观正常但贴图不对"。
		//
		// 只查**可视 LOD**：剔除网格（LodIndex < 0）按规范落在 SDK 默认槽
		// （155175220 / Lambert1），那是游戏自带的资源，本来就允许不在本包内。
		foreach (var raw in finalMeshes.Where(item => item.LodIndex >= 0))
		{
			foreach (var slot in raw.Sections.Where(section => section.Triangles.Count != 0).Select(section => section.MaterialSlotId).Distinct())
			{
				if (materialBindings.Any(binding => binding.SectionId == slot && binding.MaterialId != 0))
					continue;
				diagnostics.Add($"网格 #{raw.MeshInfoIndex} 的可见段槽位 {slot} 没有非零材质绑定。");
				return new(null, null, null, diagnostics);
			}
		}

		// 用**编译后**的 mesh 重建 provisional。沿用编译前的会把源槽位悄悄写回去
		// （CanonicalCrossArmorOrchestrator.cs:658-665 记过这个坑）。
		provisional.Clear();
		foreach (var raw in finalMeshes.Where(item => boneInfoByMesh.ContainsKey(item.MeshInfoIndex)))
			provisional.Add(new CanonicalLodBoneInput(raw, boneInfoByMesh[raw.MeshInfoIndex]));

		// 每 LOD 一次调色板编译：它会把 Type=6 的"假下标"重写成共享调色板里的位置，
		// 并算出逆绑定矩阵。必须在最终顶点编码之前跑。
		var rebuiltBones = target.BoneInfos.Select((bone, index) => new CanonicalBoneInfoRebuild(index, bone))
			.ToDictionary(item => item.LodIndex);
		foreach (var group in provisional.GroupBy(item => item.Mesh.LodIndex))
		{
			var compiled = paletteCompiler.TryCompile(target, group.Key, group.ToArray());
			if (!compiled.IsValid || compiled.BoneInfo is null)
			{
				diagnostics.AddRange(compiled.Diagnostics.Select(diagnostic => $"[{diagnostic.Code}] {diagnostic.Message}"));
				return new(null, null, null, diagnostics);
			}

			rebuiltBones[group.Key] = new CanonicalBoneInfoRebuild(group.Key, compiled.BoneInfo);
			var byMesh = compiled.Meshes.ToDictionary(mesh => mesh.MeshInfoIndex);
			for (var i = 0; i < finalMeshes.Count; i++)
			{
				if (byMesh.TryGetValue(finalMeshes[i].MeshInfoIndex, out var rewritten))
					finalMeshes[i] = rewritten;
			}
		}

		var streams = streamCompiler.TryCompile(target, finalMeshes);
		if (!streams.IsValid)
		{
			diagnostics.AddRange(streams.Diagnostics.Select(diagnostic => $"[{diagnostic.Code}] {diagnostic.Message}"));
			return new(null, null, null, diagnostics);
		}

		target = target with { Streams = streams.Streams };
		var prepared = new List<UnitRawMeshData>(finalMeshes.Count);
		foreach (var raw in finalMeshes)
		{
			var stream = target.Streams.Single(candidate => candidate.Index == (int)raw.StreamIndex);
			var encoded = preparation.TryPrepare(raw, stream);
			if (!encoded.IsValid || encoded.Mesh is null)
			{
				diagnostics.AddRange(encoded.Diagnostics.Select(diagnostic => $"[{diagnostic.Code}] {diagnostic.Message}"));
				return new(null, null, null, diagnostics);
			}

			prepared.Add(encoded.Mesh);
		}

		var rebuilt = unitRebuilder.TryRebuild(
			target, request.DonorTocData, prepared, rebuiltBones.Values.ToArray(), materialBindings);
		if (!rebuilt.IsValid || rebuilt.Output is null || rebuilt.Model is null)
		{
			diagnostics.AddRange(rebuilt.Diagnostics.Select(diagnostic => $"[{diagnostic.Code}] {diagnostic.Message}"));
			return new(null, null, null, diagnostics);
		}

		// 槽位是变长记录，只能拼字节；见 UnitCustomizationRewriter 的说明。
		var (tocData, model) = UnitCustomizationRewriter.Rewrite(
			rebuilt.Model, rebuilt.Output.TocData, request.Customization, diagnostics);
		if (diagnostics.Count != 0)
			return new(null, null, null, diagnostics);

		return new(tocData, rebuilt.Output.GpuData, model, []);
	}

	private sealed record BuiltRawMesh(
		UnitRawMeshData Raw,
		UnitBoneInfo BoneInfo,
		IReadOnlyList<CanonicalMaterialSectionProvenance> Provenance);

	/// <summary>
	/// 把<b>网格所指向的那根变换骨</b>改写成恒等（矩阵 + 局部变换 + 自父），使网格变换归一。
	/// <para>
	/// 外壳自带的网格变换是"网格局部 → 世界"（躯干 +1.17 m、左臂 −0.565 m、左腿绕 Z 180°…），
	/// 而我们的顶点已经是世界坐标。Python 侧的 <c>armor-normalize</c> 就是先把这一步做掉，
	/// 之后 <c>set_unit_positions.py</c> 才能"只换位置字节、别的一律不动"。归一后逆绑定
	/// <c>B[i] · M[realIndices[i]]</c> 正好退化成 <c>bone⁻¹</c>，与
	/// <c>review_unit_contract.py</c> 断言的契约一致。
	/// </para>
	/// <para>
	/// 为什么是"改写"而不是"追加一根新骨再指过去"：<c>CanonicalUnitRebuilder</c> 保留原
	/// <c>MeshInfo.TransformIndex</c>，追加新骨根本不会被用上（实测过，网格变换仍是非恒等）。
	/// 这些 <c>g_helmet</c> / <c>torso_culling</c> 之类是<b>网格专用节点</b>，不是骨架骨，
	/// 我们把外壳几何整份换掉，改它们不会碰到 avatar 骨架。
	/// </para>
	/// </summary>
	private static UnitMeshModel NormalizeMeshTransforms(UnitMeshModel target)
	{
		var info = target.TransformInfo;
		var matrices = info.Matrices.ToArray();
		var locals = info.LocalTransforms.ToArray();
		var entries = info.Entries.ToArray();
		foreach (var index in target.Meshes.Select(mesh => (int)mesh.TransformIndex).Distinct())
		{
			if (index < 0 || index >= matrices.Length)
				continue;
			matrices[index] = new UnitTransformMatrix([1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f]);
			locals[index] = new UnitLocalTransform([1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f], [0f, 0f, 0f], [1f, 1f, 1f], locals[index].Padding);
			entries[index] = new UnitTransformEntry(entries[index].Increment, checked((ushort)index));
		}

		return target with
		{
			TransformInfo = new UnitTransformInfo(info.Reserved0, info.Reserved1, info.Reserved2, locals, matrices, entries, info.NameHashes),
		};
	}

	/// <summary>
	/// 重算网格包围盒（<c>MeshInfo</c> 里那 7 个 float：min xyz、max xyz、最大原点到顶点的距离）。
	/// <para>
	/// <c>CanonicalUnitRebuilder</c> 不动这几个字段，所以不重算的话留下的是<b>外壳的</b>包围盒；
	/// Python 的 <c>review_unit_contract.py</c> 会断言 <c>vertices_outside == 0</c>——
	/// 顶点跑到包围盒外，游戏的剔除就会把网格丢掉。
	/// </para>
	/// </summary>
	private static UnitMeshInfo UpdateCullingBounds(UnitMeshInfo mesh, IReadOnlyList<RetargetedVertex> vertices)
	{
		if (vertices.Count == 0)
			return mesh;
		var minX = double.MaxValue;
		var minY = double.MaxValue;
		var minZ = double.MaxValue;
		var maxX = double.MinValue;
		var maxY = double.MinValue;
		var maxZ = double.MinValue;
		var radius = 0.0;
		foreach (var vertex in vertices)
		{
			minX = Math.Min(minX, vertex.Position.X);
			minY = Math.Min(minY, vertex.Position.Y);
			minZ = Math.Min(minZ, vertex.Position.Z);
			maxX = Math.Max(maxX, vertex.Position.X);
			maxY = Math.Max(maxY, vertex.Position.Y);
			maxZ = Math.Max(maxZ, vertex.Position.Z);
			radius = Math.Max(radius, vertex.Position.Length);
		}

		return mesh with
		{
			CullingBounds = new UnitMeshCullingBounds(
			[
				(float)minX, (float)minY, (float)minZ,
				(float)maxX, (float)maxY, (float)maxZ,
				(float)radius,
			]),
		};
	}

	private static BuiltRawMesh? BuildRawMesh(
		UnitAuthorRequest request,
		UnitMeshModel target,
		UnitMeshInfo mesh,
		UnitStreamInfo stream,
		List<string> diagnostics)
	{
		// 本份用到的 HD2 骨哈希 → 目标 TransformInfo 下标。调色板按这个顺序排，稳定可复现。
		var palette = new List<uint>();
		foreach (var vertex in request.Mesh.Vertices)
		{
			foreach (var influence in vertex.Influences)
			{
				if (!palette.Contains(influence.BoneHash))
					palette.Add(influence.BoneHash);
			}
		}

		palette.Sort();
		var realIndices = new uint[palette.Count];
		for (var i = 0; i < palette.Count; i++)
		{
			var index = IndexOfHash(target.TransformNameHashes, palette[i]);
			if (index < 0)
			{
				diagnostics.Add($"骨哈希 0x{palette[i]:x8} 不在外壳扩展后的 TransformInfo 里。");
				return null;
			}

			realIndices[i] = checked((uint)index);
		}

		var fakeByHash = palette.Select((hash, index) => (hash, index)).ToDictionary(item => item.hash, item => checked((uint)item.index));

		// ⚠️ 网格变换已经在 NormalizeMeshTransforms 里归一成恒等，所以这里不再需要逆矩阵。
		// 顶点保持 HD2 世界坐标出货，与 Python 侧 `ours_bind` 直接进顶点缓冲的做法一致。
		var toMeshLocal = Mat4.Identity;

		// 材质：外壳 LOD0 第一个段的槽位 + 该槽绑定的 MaterialId。
		// M2 遗留的写法是"把整份压成一段、沿用外壳的槽、不传绑定表"——那会落到
		// CanonicalUnitRebuilder 的 `finalMaterialBindings ?? target.Materials` 上，
		// **静默沿用外壳材质且外观完全正常**，这正是"形状对、贴图错却毫无报错"的根因。
		// 现在改成显式构造 provenance 并显式传绑定表。
		var donorVisualMesh = target.Meshes.First(candidate => candidate.LodIndex == 0);
		var donorSection = donorVisualMesh.Sections.FirstOrDefault();
		var materialIndex = donorSection?.MaterialIndex ?? 0;
		var materialSlotId = donorSection?.MaterialSlotId ?? 0;
		var donorMaterialId = target.Materials.FirstOrDefault(binding => binding.SectionId == materialSlotId)?.MaterialId ?? 0;
		if (donorMaterialId == 0)
		{
			diagnostics.Add($"外壳槽位 {materialSlotId} 没有材质绑定，无法确定段材质。");
			return null;
		}

		var vertices = new UnitRawVertexRecord[request.Mesh.Vertices.Count];
		for (var i = 0; i < vertices.Length; i++)
		{
			var vertex = request.Mesh.Vertices[i];
			vertices[i] = new UnitRawVertexRecord(checked((uint)i), [], BuildComponents(stream, vertex, fakeByHash, toMeshLocal));
		}

		// 每个源材质一段。段是"这段三角形用哪个材质"的唯一载体，压成一段就等于放弃按材质分组。
		// 优先用调用方给的**新造材质**；只有没给该序号时才回落到外壳材质
		// （那条回落会静默产生"贴图是原版护甲的"，所以调用方应当尽量给全）。
		var sections = new UnitRawMeshSectionData[request.Mesh.Sections.Count];
		var provenance = new CanonicalMaterialSectionProvenance[request.Mesh.Sections.Count];
		var allTriangles = new List<UnitTriangleIndices>(request.Mesh.TriangleCount);
		for (var index = 0; index < request.Mesh.Sections.Count; index++)
		{
			var source = request.Mesh.Sections[index];
			var triangles = source.Triangles
				.Select(triangle => new UnitTriangleIndices(checked((uint)triangle.A), checked((uint)triangle.B), checked((uint)triangle.C)))
				.ToArray();
			allTriangles.AddRange(triangles);

			var materialId = donorMaterialId;
			if (request.MaterialIdsBySourceMaterial is not null
				&& request.MaterialIdsBySourceMaterial.TryGetValue(source.SourceMaterialIndex, out var synthesized))
				materialId = synthesized;

			sections[index] = new UnitRawMeshSectionData(materialIndex, materialSlotId, triangles);
			provenance[index] = new CanonicalMaterialSectionProvenance(
				mesh.Index,
				index,
				request.DonorUnitFileId,
				materialSlotId,
				materialSlotId,
				materialId,
				UsesTargetUnitMaterialSlotLookup: true);
		}

		var raw = new UnitRawMeshData(mesh.Index, mesh.MeshId, mesh.LodIndex, mesh.StreamIndex, sections, allTriangles, vertices);

		// 临时调色板：RealIndices = 本份用到的骨，每个材质序号的假下标就是它在 RealIndices 里的位置。
		// 真正的共享调色板与逆绑定矩阵由 CanonicalLodBonePaletteCompiler 算。
		//
		// ⚠️ 每个**材质序号**都要有一条 remap，编号用 0..(段数-1)：材质编译器会把
		// section.MaterialIndex 重写成连续序号（CanonicalFinalMaterialLayout 按槽位首次出现顺序编号），
		// 调色板再按那个序号去 Remaps 里找。只给一条、且编号填外壳槽位，就会报
		// MissingProvisionalBoneRemap。
		var template = target.BoneInfos.FirstOrDefault(info => info.Index == mesh.LodIndex) ?? target.BoneInfos[0];
		var fake = Enumerable.Range(0, palette.Count).Select(index => checked((uint)index)).ToArray();
		var materialOrdinals = sections.Length == 0 ? 1 : sections.Length;
		var remapOffset = checked((uint)(4 + (materialOrdinals * 8)));
		var remaps = Enumerable.Range(0, materialOrdinals)
			.Select(ordinal => new UnitBoneRemap(ordinal, remapOffset, fake))
			.ToArray();
		var boneInfo = template with
		{
			NumBones = checked((uint)realIndices.Length),
			RealIndices = realIndices,
			Remaps = remaps,
		};

		return new BuiltRawMesh(raw, boneInfo, provenance);
	}

	/// <summary>
	/// 按<b>目标流的组件表</b>逐项造源组件：目标要什么就给什么，没见过的类型给零。
	/// 这样做的好处是编码器永远找得到同名同序号的来源，不需要为每种 ABI 写一份特例。
	/// </summary>
	private static UnitVertexComponentValue[] BuildComponents(
		UnitStreamInfo stream, RetargetedVertex vertex, IReadOnlyDictionary<uint, uint> fakeByHash, Mat4 toMeshLocal)
	{
		var weights = new float[4];
		var indices = new uint[4];
		for (var i = 0; i < vertex.Influences.Count && i < 4; i++)
		{
			weights[i] = vertex.Influences[i].Weight;
			indices[i] = fakeByHash.GetValueOrDefault(vertex.Influences[i].BoneHash);
		}

		// 位置按网格局部空间出货；法线只走矩阵的线性部分（不能带平移）。
		var position = toMeshLocal.TransformPoint(vertex.Position);
		var normal = new Vec3(
			(toMeshLocal.M11 * vertex.Normal.X) + (toMeshLocal.M12 * vertex.Normal.Y) + (toMeshLocal.M13 * vertex.Normal.Z),
			(toMeshLocal.M21 * vertex.Normal.X) + (toMeshLocal.M22 * vertex.Normal.Y) + (toMeshLocal.M23 * vertex.Normal.Z),
			(toMeshLocal.M31 * vertex.Normal.X) + (toMeshLocal.M32 * vertex.Normal.Y) + (toMeshLocal.M33 * vertex.Normal.Z));

		var components = new UnitVertexComponentValue[stream.Components.Count];
		for (var i = 0; i < components.Length; i++)
		{
			var target = stream.Components[i];
			var (floats, uints) = target.Type switch
			{
				0 => (new[] { (float)position.X, (float)position.Y, (float)position.Z }, Array.Empty<uint>()),
				1 => (new[] { (float)normal.X, (float)normal.Y, (float)normal.Z }, Array.Empty<uint>()),
				// ⚠️ **uv0 用真实 UV，uv1/uv2 用常量 (0, 1)。**
				//
				// 实测依据（`set_uv12.py` 的 `_probe_uvch.py` 对比）：
				//   出货参考模组（实机正常）  uv0 = 真实 UV（全在 0..1 内）
				//                             uv1 = (0,1)、uv2 = (0,1)，每个顶点都是常量
				//   我们之前的做法            uv0 = uv1 = uv2 = 源 UV
				//
				// 而源 UV 的范围是 **-21.06 .. 22.98**（L4D2 的平铺 UV）。
				// 把这种坐标喂给 uv1/uv2，游戏拿它们做的任何采样都会变成**高频噪声**
				// ——表现就是"大片网格坎坷、锯齿极多"。
				//
				// 所以这里按通道下标区分：只有 uv0 拿真实 UV，其余固定 (0, 1)。
				4 => (target.Index == 0 ? new[] { vertex.U, vertex.V } : new[] { 0f, 1f }, Array.Empty<uint>()),
				5 => (Array.Empty<float>(), new uint[] { 255, 255, 255, 255 }),
				6 => (Array.Empty<float>(), indices),
				7 => (weights, Array.Empty<uint>()),
				_ => (Array.Empty<float>(), Array.Empty<uint>()),
			};
			components[i] = new UnitVertexComponentValue(
				target.Type, target.TypeName, target.Format, target.FormatName, target.Index, floats, uints, []);
		}

		return components;
	}

	private static int IndexOfHash(IReadOnlyList<uint> hashes, uint hash)
	{
		for (var i = 0; i < hashes.Count; i++)
		{
			if (hashes[i] == hash)
				return i;
		}

		return -1;
	}
}
