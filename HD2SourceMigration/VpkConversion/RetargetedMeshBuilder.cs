using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2SourceModel;
using HD2SourceMigration.Retarget;
using HD2SourceMigration.Segmentation;

namespace HD2SourceMigration.VpkConversion;

/// <summary>
/// 把一份 <see cref="MeshGroup"/>（源几何的下标分组）+ M1 的重定向结果，变成一份可以直接写进 Unit 的几何。
/// <para>
/// 这里做三件事，每一件都有"不做会怎样"：
/// </para>
/// <list type="number">
/// <item><b>按 HD2 骨合并权重</b>：多个源骨解到同一根 HD2 骨是常态（头发、裙摆的 helper 骨全都塌到同一根），
/// 不合并就会在同一根骨上出现两条影响，游戏按四槽读时会把它当成两根不同的骨。</item>
/// <item><b>把骨下标换成名字哈希</b>：调色板是每个 Unit 自己的，用哈希才能跨 Unit 稳定。</item>
/// <item><b>用 <c>D</c> 的旋转部变换法线</b>：源法线在源空间里，直接搬过来会让明暗整体歪掉。</item>
/// </list>
/// </summary>
public static class RetargetedMeshBuilder
{
	/// <summary>HD2 每顶点最多四条影响（<c>vec4_half</c> 权重 + <c>vec4_uint8</c> 下标）。</summary>
	private const int MaxInfluences = 4;

	public static RetargetedMesh Build(
		MeshGroup group,
		SourceGeometry geometry,
		RetargetResult result,
		UnitTransformInfo avatarTransformInfo)
	{
		ArgumentNullException.ThrowIfNull(group);
		ArgumentNullException.ThrowIfNull(geometry);
		ArgumentNullException.ThrowIfNull(result);
		ArgumentNullException.ThrowIfNull(avatarTransformInfo);

		// 本份用到的源顶点 → 本份内的紧凑下标。分组保证互不重叠，所以这里不会串台。
		var compact = new Dictionary<int, int>(group.VertexIndices.Count);
		for (var i = 0; i < group.VertexIndices.Count; i++)
			compact[group.VertexIndices[i]] = i;

		var vertices = new RetargetedVertex[group.VertexIndices.Count];
		var influencesByVertex = new IReadOnlyList<RetargetedInfluence>[group.VertexIndices.Count];
		for (var i = 0; i < vertices.Length; i++)
		{
			var source = geometry.Vertices[group.VertexIndices[i]];
			influencesByVertex[i] = CollectInfluences(
				source, result, avatarTransformInfo, result.Output[group.VertexIndices[i]]);
		}

		// 三角形按**源材质**分段。源侧归属来自 SourceTriangle.MaterialIndex，是数据不是猜的。
		// 一条三角形可能不多不少属于一个材质，所以这里不会重复计入。
		var byMaterial = new Dictionary<int, List<(int A, int B, int C)>>();
		var materialOrder = new List<int>();
		foreach (var triangleIndex in group.TriangleIndices)
		{
			var triangle = geometry.Triangles[triangleIndex];
			if (!byMaterial.TryGetValue(triangle.MaterialIndex, out var bucket))
			{
				bucket = [];
				byMaterial[triangle.MaterialIndex] = bucket;
				materialOrder.Add(triangle.MaterialIndex);
			}

			// ⚠️ **绕序在这里翻转：交换后两个下标。**
			//
			// 源侧（L4D2 的 VTX）索引块是**朝内**绕的。Python 侧实测过：
			// `cross(v1-v0, v2-v0)` 与顶点法线只有 5.1% 一致，交换 v1/v2 后 94.9%；
			// 有符号体积从 -0.486 m³（朝内）变成 +0.486 m³（朝外），
			// 而出货参考躯干是 +0.100 m³。
			//
			// **HD2 会背面剔除**，所以不翻绕序就会渲染成内外翻转 ——
			// 表现是"内部可见、外部不可见"。这一步在 Python 侧叫 `WINDING_FLIP = (0, 2, 1)`
			// （`hd2_vertex.py:28`、`build_patch.py:405`），移植时漏掉了。
			bucket.Add((compact[triangle.A], compact[triangle.C], compact[triangle.B]));
		}

		// 法线用 Python 的 `final_normals`：按**每个三角形自己的刚性标架**把源法线搬到最终网格，
		// 再按面积加权平均到顶点。
		//
		// 为什么不是 `TransformNormal`（各影响骨旋转部加权）：实测那样得到的法线**朝内**
		// （背离质心的比例只有 20.9%），而绕序已经翻成朝外 —— 法线与绕序矛盾。
		// 根源是重定向里含镜像（L4D2 与 HD2 的坐标系手性不同），而 `TransformNormal`
		// 用旋转部直接搬法线，对镜像不免疫；`final_normals` 从**最终几何**推法线，所以免疫。
		var normals = ComputeFinalNormals(group, geometry, result, byMaterial);
		for (var i = 0; i < vertices.Length; i++)
		{
			var sourceIndex = group.VertexIndices[i];
			var source = geometry.Vertices[sourceIndex];
			vertices[i] = new RetargetedVertex(
				result.Output[sourceIndex],
				normals[i],
				source.U,
				source.V,
				influencesByVertex[i]);
		}

		var sections = materialOrder
			.Select(material => new RetargetedSection(material, byMaterial[material]))
			.ToArray();

		return new RetargetedMesh(vertices, sections);
	}

	/// <summary>
	/// 把三组源权重解到 HD2 骨上，<b>按骨哈希合并</b>，取最大的四条并归一化。
	/// 归一化放在这里而不是交给编码器：编码器也会归一化，但先归一化能让"权重和"这个诊断数字有意义。
	/// </summary>
	private static IReadOnlyList<RetargetedInfluence> CollectInfluences(
		SourceVertex vertex, RetargetResult result, UnitTransformInfo avatarTransformInfo, Vec3 finalPosition)
	{
		var byHash = new Dictionary<uint, double>();
		foreach (var (boneIndex, weight) in new[]
		{
			(vertex.Weight0.BoneIndex, vertex.Weight0.Weight),
			(vertex.Weight1.BoneIndex, vertex.Weight1.Weight),
			(vertex.Weight2.BoneIndex, vertex.Weight2.Weight),
		})
		{
			if (weight <= 0)
				continue;
			if (!result.Resolved.TryGetValue(boneIndex, out var hd2))
				continue;
			if (hd2 < 0 || hd2 >= avatarTransformInfo.NameHashes.Count)
				continue;
			var hash = avatarTransformInfo.NameHashes[hd2];
			byHash[hash] = byHash.GetValueOrDefault(hash) + weight;
		}

		// 小臂/手腕的权重重分配（`reweight_forearm_twist.py`）。
		// 必须在**归一化之前**做：它按"三根臂骨占多少"重新切分，前提是权重还是原始比例。
		// 判据用**最终位置**与**最终骨架的骨头**，所以放在这里（两者都已就绪）。
		if (byHash.Count > 0)
			ForearmWeightReweight.Apply(byHash, finalPosition, avatarTransformInfo);

		if (byHash.Count == 0)
			return [];

		var ordered = byHash.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).Take(MaxInfluences).ToArray();
		var total = ordered.Sum(pair => pair.Value);
		if (total <= 0)
			return [];

		// ⚠️ **权重必须吸附到 k/2048，且整数和恰好为 2048。**
		//
		// SDK 的 `normalize_half4` 契约要求 `fsum(half(w)) == 1` ——
		// Python 侧 `hd2_unitbuild.py:62-73` 的 `half4_close` 就是**断言**这一条，
		// 并说明 `dump_blender` 已把权重吸附到 k/2048（binary16 里精确可表示）。
		// 直接"浮点归一化 → 转 half"**不保证**成立。
		//
		// 为什么这条表现为"静止正常、一动就散"：
		// **静止姿势下所有骨骼变换都是恒等**，顶点无论权重如何都停在绑定位置，看不出来；
		// 一旦动起来，权重和不为 1 会让顶点被整体缩放/平移，相邻顶点各走各的 ——
		// 用户描述的"每个面就好像有自己的想法"。
		// 单骨骼区域（脸、脚）权重恒为 1.0，不受影响，与实机观察一致。
		var scaled = new int[ordered.Length];
		for (var i = 0; i < ordered.Length; i++)
			scaled[i] = (int)Math.Round(ordered[i].Value / total * 2048.0);
		var drift = 2048 - scaled.Sum();
		if (drift != 0)
		{
			// 把舍入误差补给权重最大的那个 —— 它被改动的**相对**幅度最小。
			var largest = 0;
			for (var i = 1; i < scaled.Length; i++)
			{
				if (scaled[i] > scaled[largest])
					largest = i;
			}

			scaled[largest] += drift;
		}

		return ordered
			.Select((pair, index) => new RetargetedInfluence(pair.Key, scaled[index] / 2048f))
			.ToArray();
	}

	/// <summary>
	/// 按**每个三角形自己的刚性标架**把源法线搬到最终网格，再按面积加权平均到顶点上。
	/// 移植自 <c>hd2_vertex.final_normals</c>。
	/// <para>
	/// <b>为什么不能用一次全局旋转。</b>管线对上身（1.12、绕 z=1.15）、头部（8/7）、
	/// 四肢和手各做了不同的缩放与旋转，所以**没有任何一个全局旋转**能把源法线搬对
	/// ——Python 侧实测全局 Kabsch 拟合的 p99 位置残差有 0.43 m。
	/// </para>
	/// <para>
	/// <b>做法。</b>对每条三角形建两个正交标架（源空间一个、最终空间一个），
	/// <c>R = Mfᵀ·Ms</c> 把源向量映射到最终空间。这个映射对该三角形所在的平面是**精确的**。
	/// 然后把每个顶点相邻三角形的 <c>R·n</c> 按**面积加权**平均。
	/// </para>
	/// <para>
	/// 展开写就是 <c>R·n = (es·n)·ef + (fs·n)·ff + (gs·n)·gf</c>
	/// ——不必真的构造 3×3 矩阵。
	/// </para>
	/// </summary>
	private static Vec3[] ComputeFinalNormals(
		MeshGroup group,
		SourceGeometry geometry,
		RetargetResult result,
		Dictionary<int, List<(int A, int B, int C)>> byMaterial)
	{
		var count = group.VertexIndices.Count;
		var sourcePositions = new Vec3[count];
		var sourceNormals = new Vec3[count];
		var finalPositions = new Vec3[count];
		for (var i = 0; i < count; i++)
		{
			var sourceIndex = group.VertexIndices[i];
			sourcePositions[i] = geometry.Vertices[sourceIndex].Position;
			// ⚠️ **源法线要归一化**（Python 侧 `build_patch.py:228` 明确做了 `src_nrm /= ‖src_nrm‖`）。
			// 不归一化的话，面积加权平均会被"长度大的那些法线"带偏 —— 大片噪声正是这个形状。
			sourceNormals[i] = Unit(geometry.Vertices[sourceIndex].Normal);
			finalPositions[i] = result.Output[sourceIndex];
		}

		var accumulated = new Vec3[count];
		var weights = new double[count];
		foreach (var bucket in byMaterial.Values)
		{
			foreach (var (a, b, c) in bucket)
			{
				var s0 = sourcePositions[a];
				var s1 = sourcePositions[b];
				var s2 = sourcePositions[c];
				var f0 = finalPositions[a];
				var f1 = finalPositions[b];
				var f2 = finalPositions[c];

				var sourceEdge = Unit(s1 - s0);
				var sourceFace = Unit(Cross(s1 - s0, s2 - s0));
				var sourceThird = Cross(sourceEdge, sourceFace);
				var finalEdge = Unit(f1 - f0);
				var finalFace = Cross(f1 - f0, f2 - f0);
				var area = finalFace.Length * 0.5;
				var finalUnitFace = Unit(finalFace);
				var finalThird = Cross(finalEdge, finalUnitFace);
				if (area <= 0)
					continue;

				// ⚠️ 这里**不能**写 `foreach (var corner in stackalloc[] { a, b, c })`：
				// 循环体里的 stackalloc 每一轮都会再占一份栈，直到方法返回才回收。
				// 19 万个三角形 × 12 字节就是 2.3 MB，默认 1 MB 栈直接 Stack overflow
				// （2026-10-28 用户样本 `秋桦 仁心仁术 shader 基础组件.vpk`：201 个条目、
				// 129558 顶点 / 190530 三角形，转换进程当场崩掉，整包转换失败）。
				AccumulateCorner(accumulated, weights, sourceNormals, a, sourceEdge, sourceFace, sourceThird, finalEdge, finalUnitFace, finalThird, area);
				AccumulateCorner(accumulated, weights, sourceNormals, b, sourceEdge, sourceFace, sourceThird, finalEdge, finalUnitFace, finalThird, area);
				AccumulateCorner(accumulated, weights, sourceNormals, c, sourceEdge, sourceFace, sourceThird, finalEdge, finalUnitFace, finalThird, area);
			}
		}

		var normals = new Vec3[count];
		for (var i = 0; i < count; i++)
		{
			// 没有相邻三角形的顶点（理论上不该有）保留源法线，而不是变成零向量。
			normals[i] = weights[i] > 0 ? Unit(accumulated[i] / weights[i]) : sourceNormals[i];
		}

		return normals;
	}

	private static Vec3 Cross(Vec3 a, Vec3 b) => new(
		(a.Y * b.Z) - (a.Z * b.Y),
		(a.Z * b.X) - (a.X * b.Z),
		(a.X * b.Y) - (a.Y * b.X));

	/// <summary>把一个三角形角上的源法线按面积加权累加进最终法线（见 <see cref="ComputeFinalNormals"/>）。</summary>
	private static void AccumulateCorner(
		Vec3[] accumulated,
		double[] weights,
		Vec3[] sourceNormals,
		int corner,
		Vec3 sourceEdge,
		Vec3 sourceFace,
		Vec3 sourceThird,
		Vec3 finalEdge,
		Vec3 finalUnitFace,
		Vec3 finalThird,
		double area)
	{
		var normal = sourceNormals[corner];
		var rotated = (finalEdge * Dot(sourceEdge, normal))
			+ (finalUnitFace * Dot(sourceFace, normal))
			+ (finalThird * Dot(sourceThird, normal));
		accumulated[corner] += rotated * area;
		weights[corner] += area;
	}

	private static double Dot(Vec3 a, Vec3 b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);

	private static Vec3 Unit(Vec3 value)
	{
		var length = value.Length;
		return length > 1e-30 ? value / length : Vec3.Zero;
	}

	/// <summary>
	/// 法线 = 各影响骨的旋转部加权作用于源法线。用的是 <c>D</c> 的旋转部——与顶点走的是同一条变形链，
	/// 所以明暗跟着网格一起转，不会出现"形状对了但光照是旧的"。
	/// </summary>
	private static Vec3 TransformNormal(
		Vec3 normal, IReadOnlyList<RetargetedInfluence> influences, RetargetResult result, UnitTransformInfo avatarTransformInfo)
	{
		if (influences.Count == 0 || normal.Length <= 1e-12)
			return normal;

		var accumulated = Vec3.Zero;
		foreach (var influence in influences)
		{
			var transformIndex = IndexOfHash(avatarTransformInfo, influence.BoneHash);
			if (transformIndex < 0 || !result.Deform.TryGetValue(transformIndex, out var deform))
				continue;
			var rotated = new Vec3(
				(deform.M11 * normal.X) + (deform.M12 * normal.Y) + (deform.M13 * normal.Z),
				(deform.M21 * normal.X) + (deform.M22 * normal.Y) + (deform.M23 * normal.Z),
				(deform.M31 * normal.X) + (deform.M32 * normal.Y) + (deform.M33 * normal.Z));
			accumulated += rotated * influence.Weight;
		}

		var length = accumulated.Length;
		return length > 1e-12 ? accumulated / length : normal;
	}

	private static int IndexOfHash(UnitTransformInfo info, uint hash)
	{
		for (var i = 0; i < info.NameHashes.Count; i++)
		{
			if (info.NameHashes[i] == hash)
				return i;
		}

		return -1;
	}
}
