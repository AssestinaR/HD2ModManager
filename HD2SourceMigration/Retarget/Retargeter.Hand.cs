using HD2SourceModel;

namespace HD2SourceMigration.Retarget;

public sealed partial class Retargeter
{
	/// <summary>HD2 手指链的五个茎名与源侧 <c>FingerN</c> 后缀。</summary>
	private static readonly (string Stem, string SourceSuffix)[] FingerStems =
	[
		("thumb", "Finger0"), ("index", "Finger1"), ("middle", "Finger2"),
		("ring", "Finger3"), ("pinky", "Finger4"),
	];

	/// <summary>
	/// HD2 <b>自己</b>的中指屈曲平面法线。全部来自发货骨架，所以没有逐模型常量、也没有跨模型参考集。
	/// 实测该轴与 <c>rest_hd2</c> 的小臂轴成 88–92°，也就是说它是一次绕腕的刚性<b>转手</b>而不是旋前滚转；
	/// 绕小臂滚转在同一目标函数上实测只有它的 1/4 到 1/6 有效。
	/// </summary>
	private Vec3 HandReorientAxis(string side)
	{
		var points = new Vec3[3];
		for (var k = 0; k < 3; k++)
			points[k] = _restHd2[_byName[$"{side}_middle_finger{k + 1}"].Index].Origin;
		var normal = Vec3.Cross(points[1] - points[0], points[2] - points[1]);
		return normal / Math.Max(normal.Length, 1e-12);
	}

	/// <summary>
	/// HD2 自己的指骨段（头, 尾）。末节没有子节点，所以它的尾沿骨自己的 +X 轴延长一个指节长度——
	/// 与 <c>apply_finger_pose</c> 处理末端目标方向用的是同一套约定。
	/// </summary>
	private List<(Vec3 Head, Vec3 Tail)> FingerTargetSegments(string side)
	{
		var segments = new List<(Vec3, Vec3)>();
		foreach (var (stem, _) in FingerStems)
		{
			var names = new[] { 1, 2, 3 }.Select(k => $"{side}_{stem}_finger{k}").ToArray();
			if (names.Any(name => !_byName.ContainsKey(name)))
				continue;
			var points = names.Select(name => _restHd2[_byName[name].Index].Origin).ToArray();
			segments.Add((points[0], points[1]));
			segments.Add((points[1], points[2]));
			var bone = _restHd2[_byName[names[2]].Index];
			var axis = new Vec3(bone.M11, bone.M21, bone.M31);
			axis /= Math.Max(axis.Length, 1e-12);
			segments.Add((points[2], points[2] + (axis * (points[2] - points[1]).Length)));
		}

		return segments;
	}

	/// <summary>承载手/手指影响（权重 ≥ 0.20）的顶点，单侧。</summary>
	private bool[] HandReorientInfluence(string side)
	{
		var ids = new HashSet<int>();
		foreach (var name in new[] { $"{side}_hand", $"{side}_hand_twist" })
		{
			if (_byName.TryGetValue(name, out var bone))
				ids.Add(bone.Index);
		}

		foreach (var (stem, _) in FingerStems)
		{
			for (var k = 1; k <= 3; k++)
			{
				if (_byName.TryGetValue($"{side}_{stem}_finger{k}", out var bone))
					ids.Add(bone.Index);
			}
		}

		var result = new bool[_sourcePoints.Length];
		for (var k = 0; k < result.Length; k++)
		{
			for (var j = 0; j < 3; j++)
			{
				if (_weights[k][j] < RetargetConstants.HandInfluenceWeight)
					continue;
				if (_resolved.TryGetValue(_boneIndices[k][j], out var hd2) && ids.Contains(hd2))
				{
					result[k] = true;
					break;
				}
			}
		}

		return result;
	}

	/// <summary>
	/// 腕部接缝羽化：腕平面近端 <paramref name="halfWidth"/> 之内不动，远端满效果。
	/// <para>
	/// 4 mm 太陡会剪切接缝：MourningVeil 上实测 4 mm 带在 3° 时多翻了 2 个三角形
	/// （属于 <c>source_extra_80/81</c> 辅助骨，1.07 mm² → 1.50 mm²），所以带宽是个参数，25 mm 能去掉它们。
	/// </para>
	/// </summary>
	private double[] HandReorientFeather(IReadOnlyList<Vec3> points, string side, double halfWidth)
	{
		var wrist = _restHd2[_byName[$"{side}_hand"].Index].Origin;
		var elbow = _restHd2[_byName[$"{side}_elbow"].Index].Origin;
		var axis = (wrist - elbow) / Math.Max((wrist - elbow).Length, 1e-12);
		var width = Math.Max(halfWidth, 1e-6);
		var result = new double[points.Count];
		for (var i = 0; i < points.Count; i++)
		{
			var axial = Vec3.Dot(points[i] - wrist, axis);
			var t = RetargetMath.Clamp((axial + width) / (2.0 * width), 0, 1);
			result[i] = t * t * (3.0 - (2.0 * t));
		}

		return result;
	}

	/// <summary>
	/// 解出使"我们的手指肉"到"HD2 自己的指骨段"平均距离最小的刚性转手角度。
	/// <para>
	/// 这就是 <c>hand_inward_ab.py</c> 已经算过、却丢弃掉去换 <c>min(15°, 5.43·shortfall·hd2_curl)</c>
	/// 的那个目标函数（后者需要一个跨模型的"最大手"参考）。直接解掉它就没有拟合增益、没有 15° 硬上限、
	/// 也没有硬编码符号，搜索区间只是对<b>搜索</b>的限制，不是对答案的上限。
	/// </para>
	/// <para>
	/// <paramref name="points"/> 必须是<b>手指已摆姿</b>的网格：在平的 T-pose 手上同一个目标函数会
	/// 在 15–20° 触底，因为它在补偿 4f 即将消除的那约 45° 手指方向误差；手指摆好之后诚实的答案是 2–7°。
	/// </para>
	/// </summary>
	private (double Degrees, double Before, double After) SolveHandReorient(
		IReadOnlyList<Vec3> points, bool[] influence, string side, double low, double high)
	{
		var wrist = _restHd2[_byName[$"{side}_hand"].Index].Origin;
		var axis = HandReorientAxis(side);
		var segments = FingerTargetSegments(side);
		var indices = new List<int>();
		// 紧凑下标 → 全量下标。目标函数要在"只含受影响顶点"的数组上求值（那才是被转动的部分），
		// 所以两张下标表必须一起用，混用就会读到别人家的顶点。
		var positionOf = new int[points.Count];
		Array.Fill(positionOf, -1);
		for (var i = 0; i < influence.Length; i++)
		{
			if (!influence[i])
				continue;
			positionOf[i] = indices.Count;
			indices.Add(i);
		}

		if (indices.Count == 0 || segments.Count == 0)
			return (0.0, 0.0, 0.0);

		double Objective(Func<int, Vec3> pointAt)
		{
			var total = 0.0;
			foreach (var (head, tail) in segments)
			{
				var sum = 0.0;
				foreach (var index in indices)
					sum += PointSegmentDistance(pointAt(index), head, tail);
				total += sum / indices.Count;
			}

			return total / segments.Count;
		}

		var before = Objective(index => points[index]);
		var bestDegrees = 0.0;
		var best = before;
		var moved = new Vec3[indices.Count];
		for (var degrees = low; degrees <= high + 1e-9; degrees += RetargetConstants.HandReorientStepDegrees)
		{
			if (Math.Abs(degrees) < 1e-12)
				continue;
			var rotation = RetargetMath.RotAxisAngle(axis, degrees * Math.PI / 180.0);
			for (var i = 0; i < indices.Count; i++)
				moved[i] = wrist + rotation.Transform(points[indices[i]] - wrist);
			var value = Objective(index => moved[positionOf[index]]);
			if (value >= best)
				continue;
			bestDegrees = degrees;
			best = value;
		}

		return (bestDegrees, before, best);
	}

	/// <summary>
	/// 掌板围度：到<b>掌轴</b>距离的 90 分位。三条性质让它成为自动缩放的量尺：
	/// 在 <b>T-pose</b> 上测，所以是纯尺寸、不被手指蜷曲污染；<b>排除拇指</b>（拇指侧伸会把 9 cm 的掌
	/// 吹成 14.6 cm 的"掌宽"）；它同时含宽度与厚度，所以窄掌和扁掌都算"小"。
	/// 实测（T-pose，mm）：MourningVeil 40.6、Lunalice 40.9、Lilium 37.4。
	/// </summary>
	private (double Radius, int Count) PalmPerpRadius(IReadOnlyList<Vec3> points, string side)
	{
		var wrist = _restHd2[_byName[$"{side}_hand"].Index].Origin;
		var knuckle = _restHd2[_byName[$"{side}_middle_finger1"].Index].Origin;
		var axis = knuckle - wrist;
		var palmLength = axis.Length;
		if (palmLength < 1e-9)
			return (0.0, 0);
		axis /= palmLength;

		var influence = HandReorientInfluence(side);
		if (!influence.Any(value => value))
			return (0.0, 0);

		var thumbIds = new HashSet<int>();
		for (var k = 1; k <= 3; k++)
		{
			if (_byName.TryGetValue($"{side}_thumb_finger{k}", out var bone))
				thumbIds.Add(bone.Index);
		}

		var perpendicular = new List<double>();
		for (var i = 0; i < points.Count; i++)
		{
			if (!influence[i])
				continue;
			var relative = points[i] - wrist;
			var along = Vec3.Dot(relative, axis);
			if (along <= RetargetConstants.PalmSlabStart * palmLength
				|| along >= RetargetConstants.PalmSlabEnd * palmLength)
				continue;
			var isThumb = false;
			for (var j = 0; j < 3; j++)
			{
				if (_weights[i][j] < RetargetConstants.HandInfluenceWeight)
					continue;
				if (_resolved.TryGetValue(_boneIndices[i][j], out var hd2) && thumbIds.Contains(hd2))
				{
					isThumb = true;
					break;
				}
			}

			if (isThumb)
				continue;
			perpendicular.Add((relative - (axis * along)).Length);
		}

		if (perpendicular.Count == 0)
			return (0.0, 0);
		return (Percentile(perpendicular, 90.0), perpendicular.Count);
	}

	/// <summary>
	/// 线性插值分位数，与 NumPy <c>np.percentile</c> 的默认方法一致：
	/// 下标 <c>q/100 · (n−1)</c>，落在两个样本之间时线性插值。
	/// </summary>
	private static double Percentile(List<double> values, double percentile)
	{
		values.Sort();
		if (values.Count == 1)
			return values[0];
		var position = percentile / 100.0 * (values.Count - 1);
		var low = (int)Math.Floor(position);
		var high = (int)Math.Ceiling(position);
		if (low == high)
			return values[low];
		return values[low] + ((values[high] - values[low]) * (position - low));
	}

	/// <summary>
	/// 阶段 4g：仅网格的手掌转正。
	/// <para>
	/// ⚠️ <b>顺序是承重的</b>：必须在 4f <b>之后</b>跑，作为腕远端的<b>刚性</b>转动。在 4f 之前解不行，
	/// 原因不微妙：4f 把每一节指骨重新瞄到 HD2 的<b>绝对</b>方向，所以手指<b>不会</b>继承手的转动，
	/// 整个增益被扔掉。那样实测目标函数反而变差 0.5–2.3 mm，Lilium 的折叠三角形从 2 涨到 35。
	/// </para>
	/// </summary>
	private Vec3[] ApplyHandReorient(Vec3[] points, List<HandReorientRow> rows)
	{
		foreach (var side in new[] { "l", "r" })
		{
			var influence = HandReorientInfluence(side);
			var count = influence.Count(value => value);
			if (count == 0)
				continue;

			double degrees;
			double? before = null;
			double? after = null;
			if (_options.HandReorient == HandReorientMode.Forced)
			{
				degrees = _options.HandReorientDegrees;
			}
			else
			{
				var solved = SolveHandReorient(
					points, influence, side, _options.HandReorientRangeLow, _options.HandReorientRangeHigh);
				degrees = solved.Degrees;
				before = solved.Before;
				after = solved.After;
			}

			rows.Add(new HandReorientRow(side, degrees, before, after, count));
			if (Math.Abs(degrees) < 1e-9)
				continue;

			var wrist = _restHd2[_byName[$"{side}_hand"].Index].Origin;
			var rotation = RetargetMath.RotAxisAngle(HandReorientAxis(side), degrees * Math.PI / 180.0);
			var feather = HandReorientFeather(points, side, _options.HandReorientFeather);
			for (var i = 0; i < points.Length; i++)
			{
				if (!influence[i])
					continue;
				var offset = points[i] - wrist;
				points[i] += (rotation.Transform(offset) - offset) * feather[i];
			}
		}

		return points;
	}

	/// <summary>
	/// 阶段 4h：仅网格的绕<b>掌法线</b>转手。
	/// <code>
	/// u = normalize(中指尖 − 腕)          掌轴
	/// v = normalize(食指根 − 小指根)
	/// n = normalize(cross(u, v))          掌法线
	/// v' = 腕 + R(n, turn)·(v − 腕)
	/// </code>
	/// 与 4g/4i 用同一条腕缝规则：羽化到腕平面处恰好为 0，所以接缝不可能出现台阶。
	/// </summary>
	private Vec3[] ApplyHandTurn(Vec3[] points)
	{
		foreach (var side in new[] { "l", "r" })
		{
			var influence = HandReorientInfluence(side);
			if (!influence.Any(value => value))
				continue;

			var wrist = _restHd2[_byName[$"{side}_hand"].Index].Origin;
			var knuckle = _restHd2[_byName[$"{side}_middle_finger1"].Index].Origin;
			var index = _restHd2[_byName[$"{side}_index_finger1"].Index].Origin;
			var pinky = _restHd2[_byName[$"{side}_pinky_finger1"].Index].Origin;
			var u = (knuckle - wrist) / Math.Max((knuckle - wrist).Length, 1e-12);
			var v = (index - pinky) / Math.Max((index - pinky).Length, 1e-12);
			var normal = Vec3.Cross(u, v);
			normal /= Math.Max(normal.Length, 1e-12);
			var rotation = RetargetMath.RotAxisAngle(normal, _options.HandTurn * Math.PI / 180.0);
			var feather = HandReorientFeather(points, side, _options.HandReorientFeather);
			for (var i = 0; i < points.Length; i++)
			{
				if (!influence[i])
					continue;
				var offset = points[i] - wrist;
				points[i] += (rotation.Transform(offset) - offset) * feather[i];
			}
		}

		return points;
	}

	/// <summary>
	/// 阶段 4i：仅网格的整手缩放（带羽化）。
	/// <code>
	/// u  = normalize(中指尖 − 腕)          掌轴
	/// a  = (v − 腕)·u
	/// f  = smoothstep(clip(a / 掌长, 0, 1))   腕处 f 恰好为 0
	/// v' = 腕 + (v − 腕)·(1 + (S − 1)·f)
	/// </code>
	/// "怎么缩才不会让腕缝看起来突然"是这个构造的<b>性质</b>而不是调出来的值：接缝处 <c>f</c> 精确为 0，
	/// 所以那里的位移精确为 0，台阶不可能出现；<c>f</c> 是 C1 的（smoothstep），所以也没有折痕。
	/// <para>
	/// ⚠️ 它<b>最后</b>跑，在 4f 与 4g 之后。放在 4g 之前会让 4g 解出的角度依赖 S，而那次重解本身
	/// 就把腕挪了约 0.5 mm——那之后与接缝缺陷就分不清了。绕同一中心的旋转与缩放可交换，所以放最后不亏。
	/// </para>
	/// </summary>
	private Vec3[] ApplyPalmScale(Vec3[] points, IReadOnlyDictionary<string, double> palmGirth)
	{
		foreach (var side in new[] { "l", "r" })
		{
			var influence = HandReorientInfluence(side);
			if (!influence.Any(value => value))
				continue;

			var wrist = _restHd2[_byName[$"{side}_hand"].Index].Origin;
			var knuckle = _restHd2[_byName[$"{side}_middle_finger1"].Index].Origin;
			var axis = knuckle - wrist;
			var palmLength = axis.Length;
			if (palmLength < 1e-9)
				continue;
			axis /= palmLength;

			var scale = _options.PalmScale;
			if (_options.PalmScaleAuto
				&& palmGirth.TryGetValue(side, out var girth)
				&& girth > 1e-9)
				scale *= RetargetConstants.PalmReferenceGirth / girth;

			if (Math.Abs(scale - 1.0) <= 1e-12)
				continue;

			for (var i = 0; i < points.Length; i++)
			{
				if (!influence[i])
					continue;
				var offset = points[i] - wrist;
				var t = RetargetMath.Clamp(Vec3.Dot(offset, axis) / palmLength, 0, 1);
				var feather = t * t * (3.0 - (2.0 * t));
				points[i] = wrist + (offset * (1.0 + ((scale - 1.0) * feather)));
			}
		}

		return points;
	}
}
