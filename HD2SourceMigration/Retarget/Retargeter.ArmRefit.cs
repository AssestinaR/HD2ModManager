using HD2SourceModel;

namespace HD2SourceMigration.Retarget;

public sealed partial class Retargeter
{
	/// <summary>
	/// 阶段 4e：仅网格的手臂重拟合（标准平移重定向）。
	/// <para>
	/// 发货的骨架是 HD2 自己的，但网格是绕 <c>rest_sym</c> 烘焙的，而 <c>rest_sym</c> 的头是<b>源</b>关节。
	/// 源关节偏离 HD2 关节的地方（这里肩在手臂轴上偏内约 9 cm、偏上约 9 cm，肘偏内约 9.6 cm），
	/// 网格的手臂就落在骨的内侧，正视看起来"弓着"，尽管每一段的长度都对。
	/// </para>
	/// <para>
	/// 把每个顶点按权重混合的关节残差（<c>发货头 − 工作头</c>，非手臂骨为零）位移一下，
	/// 就把每个手臂关节放回 HD2 的骨上，并且平滑地穿过肩部，于是躯干原地不动、什么都不会撕开。
	/// 残差被拆成三个正交方向分别调，因为它们的可信度<b>不</b>相同：沿 <c>u</c> 的部分源肩确实在内侧，
	/// 1:1 保留；竖直分量是唯一能修"网格小臂/手掌相对骨骼偏高几厘米"的项；前后分量与
	/// <see cref="RetargetOptions.MeshShift"/> 双重计数，默认 0。
	/// </para>
	/// <para>在仅网格的位移/缩放（4c/4d）<b>之前</b>跑，这样残差与两套 rest 处在同一个空间里。</para>
	/// </summary>
	private Vec3[] ApplyArmRefit(Vec3[] points)
	{
		var up = new Vec3(0, 0, 1);
		var triangles = _geometry.Triangles;

		// 网格图的两向边表：平滑与度数都从它来。6 × 三角形数条有向边。
		var sources = new int[triangles.Count * 6];
		var destinations = new int[triangles.Count * 6];
		var degree = new int[points.Length];
		for (var t = 0; t < triangles.Count; t++)
		{
			var (a, b, c) = (triangles[t].A, triangles[t].B, triangles[t].C);
			var edges = new[] { (a, b), (b, c), (c, a) };
			for (var e = 0; e < 3; e++)
			{
				var (from, to) = edges[e];
				sources[(t * 6) + e] = from;
				destinations[(t * 6) + e] = to;
				sources[(t * 6) + 3 + e] = to;
				destinations[(t * 6) + 3 + e] = from;
				degree[from]++;
				degree[to]++;
			}
		}

		var delta = new Vec3[points.Length];
		var bonesUsed = 0;
		foreach (var side in new[] { "l", "r" })
		{
			if (!_byName.TryGetValue($"{side}_shoulder", out var shoulder)
				|| !_byName.TryGetValue($"{side}_hand", out var hand))
				continue;

			var pivot = _shippedRest[shoulder.Index].Origin;
			var axis = _shippedRest[hand.Index].Origin - pivot;
			axis /= Math.Max(axis.Length, 1e-9);
			var vertical = up - (axis * Vec3.Dot(axis, up));
			vertical /= Math.Max(vertical.Length, 1e-9);
			var forward = Vec3.Cross(axis, vertical);

			var residual = new Dictionary<int, Vec3>();
			foreach (var bone in _rig.Bones)
			{
				if (!IsArmBone(bone.Name) || !bone.Name!.StartsWith(side + "_", StringComparison.Ordinal))
					continue;
				var index = bone.Index;
				if (!_restWorking.ContainsKey(index) || !_shippedRest.ContainsKey(index))
					continue;
				var difference = _shippedRest[index].Origin - (_deform[index] * _restWorking[index]).Origin;
				var along = Vec3.Dot(difference, axis);
				var perpendicular = difference - (axis * along);
				residual[index] = (axis * along)
					+ (vertical * (_options.ArmUp * Vec3.Dot(perpendicular, vertical)))
					+ (forward * (_options.ArmPerp * Vec3.Dot(perpendicular, forward)));
			}

			if (residual.Count == 0)
				continue;
			bonesUsed += residual.Count;

			var field = new Vec3[points.Length];
			for (var k = 0; k < points.Length; k++)
			{
				var accumulated = Vec3.Zero;
				for (var j = 0; j < 3; j++)
				{
					var weight = _weights[k][j];
					if (weight <= 0)
						continue;
					if (_resolved.TryGetValue(_boneIndices[k][j], out var hd2)
						&& residual.TryGetValue(hd2, out var value))
						accumulated += value * weight;
				}

				field[k] = accumulated;
			}

			// 原始场跟着 LBS 权重走，而权重在胸/锁骨边界变化很快：一个在 `chest` 上的顶点拿到 0，
			// 它旁边在 `l_clavicle` 上的邻居拿到 0.115 m，1 cm 的边被拉长约 11 倍。几轮拉普拉斯平滑
			// 把这个跳变摊到几厘米而不是一条边上。
			for (var pass = 0; pass < _options.ArmSmooth; pass++)
			{
				var accumulator = new Vec3[points.Length];
				for (var e = 0; e < sources.Length; e++)
					accumulator[sources[e]] += field[destinations[e]];
				for (var k = 0; k < points.Length; k++)
				{
					var average = degree[k] > 0 ? accumulator[k] / degree[k] : Vec3.Zero;
					field[k] = (field[k] * 0.5) + (average * 0.5);
				}
			}

			// 渐入：手臂加权的点云会伸到肩关节内侧 8.5 cm（那是源权重错误地交给 `l_shoulder`
			// 的胸/腋几何），把它整体位移 18 cm 会滑出躯干并露出真实的缝。按沿本侧手臂轴的位置
			// 从 t0 的 0 渐到 t1 的满强度，接缝保持焊接，只有手臂本身滑出去。
			if (_options.ArmTaper is { } taper)
			{
				for (var k = 0; k < points.Length; k++)
				{
					var along = Vec3.Dot(points[k] - pivot, axis);
					var t = RetargetMath.Clamp((along - taper.Start) / Math.Max(taper.End - taper.Start, 1e-9), 0, 1);
					field[k] *= t * t * (3.0 - (2.0 * t));
				}
			}

			for (var k = 0; k < points.Length; k++)
				delta[k] += field[k];
		}

		if (bonesUsed == 0)
			return points;

		var factor = _options.ArmFit;
		var result = new Vec3[points.Length];
		for (var k = 0; k < points.Length; k++)
			result[k] = points[k] + (delta[k] * factor);
		return result;
	}

	/// <summary>是否属于左右手臂链（含手指）。<c>l_shoulderarmour</c> 与 <c>l_hand_twist</c> 按设计算在内。</summary>
	private static bool IsArmBone(string? name)
	{
		if (string.IsNullOrEmpty(name) || name.Length < 2)
			return false;
		if (name[..2] is not ("l_" or "r_"))
			return false;
		foreach (var key in new[] { "clavicle", "shoulder", "elbow", "hand", "finger" })
		{
			if (name.Contains(key, StringComparison.Ordinal))
				return true;
		}

		return false;
	}
}
