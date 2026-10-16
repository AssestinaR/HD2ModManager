using HD2SourceModel;

namespace HD2SourceMigration.Retarget;

public sealed partial class Retargeter
{
	/// <summary>
	/// 阶段 4f：把手指摆到 HD2 原生的手指方向上（仅网格）。专家评审过的算法。
	/// <para>逐指、从根到尖，用<b>目标</b>链驱动正向运动学：</para>
	/// <code>
	/// Q  = Gp[:3, :3]
	/// Rl = min_arc(s[k], Qᵀ · t[k])          ← Q 这一对是全部要点
	/// Gk = Gp · T(J[k]) · R4(Rl) · T(−J[k])
	/// Gp = Gk
	/// </code>
	/// <list type="bullet">
	/// <item><c>J[k]</c> 是<b>未弯</b>输入 <c>P0</c> 的关节。父变换会移动它们；先把它们挪好再放到
	/// <c>Gp</c> 右边会把枢轴移动两次。</item>
	/// <item><c>s[k]</c> 是 <c>P0</c> <b>冻结</b>的段方向，<c>t[k]</c> 是目标。旋转必须用 <c>Q</c> 共轭，
	/// 因为世界系里解出的最小弧<b>不是</b>局部旋转——丢掉这一对会让指节偏最多 14°（专家在真实链上实测 11.3–14.3°）。</item>
	/// <item>掌/腕/根已经验收过，所以 <c>Gp</c> 从单位矩阵开始：这是<b>残差</b>姿态，不是重新摆整只手。</item>
	/// <item>只做旋转所以长度保持、根不动，于是网格仍走<b>原来那三组权重</b>蒙皮，不重新归一化、不在根处硬切。</item>
	/// </list>
	/// </summary>
	private Vec3[] ApplyFingerPose(Vec3[] points, List<FingerPoseRow> rows)
	{
		var wanted = _options.FingerPoseOnly is { Count: > 0 } only
			? new HashSet<string>(only.Select(item => item.Trim().ToLowerInvariant()), StringComparer.Ordinal)
			: null;

		var frames = new Dictionary<int, Mat4>();
		foreach (var side in new[] { "l", "r" })
		{
			foreach (var (stem, sourceSuffix) in FingerStems)
			{
				if (wanted is not null
					&& !wanted.Contains(stem)
					&& !wanted.Contains($"{side}_{stem}"))
					continue;

				var hd2Names = new[] { 1, 2, 3 }.Select(k => $"{side}_{stem}_finger{k}").ToArray();
				var sourceNames = new[] { string.Empty, "1", "2" }
					.Select(suffix => $"{side.ToUpperInvariant()}_{sourceSuffix}{suffix}").ToArray();
				if (hd2Names.Any(name => !_byName.ContainsKey(name))
					|| sourceNames.Any(name => !_shortByName.ContainsKey(name)))
					continue;

				var indices = hd2Names.Select(name => _byName[name].Index).ToArray();

				// 我们当前的关节：源关节先被管线自己的变形矩阵带走，再走一遍 `out` 经历过的
				// 仅网格后处理（平移 + 上半身缩放）。<b>不是</b> rest_sym——那是 post 之前的空间。
				var joints = new Vec3[3];
				for (var k = 0; k < 3; k++)
					joints[k] = Post(_deform[indices[k]].TransformPoint(_restWorking[indices[k]].Origin));

				var working = new Vec3[3];
				for (var k = 0; k < 3; k++)
					working[k] = _restWorking[indices[k]].Origin;
				var sourceBend = RetargetMath.SafeAcos(
					Vec3.Dot(
						RetargetMath.SafeNormalize(working[1] - working[0]),
						RetargetMath.SafeNormalize(working[2] - working[1]))) * (180.0 / Math.PI);

				var segments = new[]
				{
					RetargetMath.SafeNormalize(joints[1] - joints[0]),
					RetargetMath.SafeNormalize(joints[2] - joints[1]),
					RetargetMath.SafeNormalize(joints[2] - joints[1]),
				};

				// 目标：HD2 原生的手指链。末节没有子节点，所以它的方向就是骨自己的 +X 轴。
				var targets = new Vec3[3];
				targets[0] = RetargetMath.SafeNormalize(
					_restHd2[indices[1]].Origin - _restHd2[indices[0]].Origin);
				targets[1] = RetargetMath.SafeNormalize(
					_restHd2[indices[2]].Origin - _restHd2[indices[1]].Origin);
				var last = _restHd2[indices[2]];
				targets[2] = RetargetMath.SafeNormalize(new Vec3(last.M11, last.M21, last.M31));

				// 保住目标扇形，再在<b>已经解好的父坐标系</b>里按比例缩放每一节铰链角。
				// 这样既保持目标弯曲平面，又允许"放松的部分蜷曲"独立于张开。
				var curl = CurlAmountFor(stem);

				targets[0] = RetargetMath.SlerpDirection(segments[0], targets[0], _options.FingerPoseAmount);

				var chain = Mat4.Identity;
				var angles = new double[3];
				var achieved = new Vec3[3];
				for (var k = 0; k < 3; k++)
				{
					var rotation = Mat3.FromMat4(chain);
					var currentWorld = rotation.Transform(segments[k]);
					var targetWorld = targets[k];
					if (k > 0 && curl < 1.0)
					{
						var hinge = RetargetMath.MinArcRotation(currentWorld, targetWorld);
						var (hingeDegrees, hingeAxis) = RetargetMath.AngleAxis(hinge);
						if (hingeDegrees > 1e-9)
						{
							targetWorld = RetargetMath.RotAxisAngle(
								hingeAxis, hingeDegrees * curl * Math.PI / 180.0).Transform(currentWorld);
						}
					}

					achieved[k] = RetargetMath.SafeNormalize(targetWorld);
					var local = RetargetMath.MinArcRotation(
						segments[k], rotation.Transposed().Transform(targetWorld));
					angles[k] = RetargetMath.AngleAxis(local).Degrees;

					// Gp = Gp · T(J) · R · T(−J)：绕关节 J 的旋转。
					chain = chain
						* RetargetMath.Trans(joints[k])
						* local.ToMat4()
						* RetargetMath.Trans(joints[k] * -1.0);
					frames[indices[k]] = chain;
				}

				var residual = new double[3];
				for (var k = 0; k < 3; k++)
				{
					var got = Mat3.FromMat4(frames[indices[k]]).Transform(segments[k]);
					residual[k] = RetargetMath.SafeAcos(
						Vec3.Dot(RetargetMath.SafeNormalize(got), achieved[k])) * (180.0 / Math.PI);
				}

				rows.Add(new FingerPoseRow(side, stem, hd2Names, sourceNames, angles, residual, sourceBend));
			}
		}

		var result = new Vec3[points.Length];
		for (var k = 0; k < points.Length; k++)
		{
			var accumulated = Vec3.Zero;
			var total = 0.0;
			for (var j = 0; j < 3; j++)
			{
				var weight = _weights[k][j];
				if (weight <= 0)
					continue;
				var frame = Mat4.Identity;
				if (_resolved.TryGetValue(_boneIndices[k][j], out var hd2)
					&& frames.TryGetValue(hd2, out var matrix))
					frame = matrix;
				accumulated += frame.TransformPoint(points[k]) * weight;
				total += weight;
			}

			result[k] = total > 0 ? accumulated / total : points[k];
		}

		return result;
	}

	/// <summary>
	/// 单指的蜷曲比例。标量或按茎名的映射二选一，映射可含 <c>"*"</c> 作兜底——
	/// 拇指要 1.00 而其余保持 0.60 就是靠这个表达的。
	/// </summary>
	private double CurlAmountFor(string stem)
	{
		if (_options.FingerCurlMap is { } map)
		{
			if (map.TryGetValue(stem, out var exact))
				return exact;
			if (map.TryGetValue("*", out var fallback))
				return fallback;
			return _options.FingerPoseAmount;
		}

		return _options.FingerCurlAmount ?? _options.FingerPoseAmount;
	}
}
