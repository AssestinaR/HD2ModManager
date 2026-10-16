using HD2SourceModel;

namespace HD2SourceMigration.Retarget;

public sealed partial class Retargeter
{
	/// <summary>
	/// 阶段 4：注入 <c>basis</c>（T→A 姿态）、建 <c>D</c> 递归，并顺带做手臂的两处长度补偿。
	/// 返回的数组可能与入参不同（长度补偿是就地改写 <c>src1</c> 的）。
	/// </summary>
	private Vec3[] BuildPose(Vec3[] aligned)
	{
		if (_options.ArmScratch)
			aligned = BuildArmScratchPose(aligned);
		else
		{
			foreach (var (name, rotation) in _profile.InitialBasisRotations)
				_basis[name] = rotation;
		}

		foreach (var index in _order)
		{
			var bone = _bonesByIndex[index];
			var parent = ResolveParent(bone);
			var parentDeform = parent is null ? Mat4.Identity : _deform[parent.Index];
			var rest = _restWorking[index];
			var basis = _basis.TryGetValue(bone.DisplayName, out var b) ? b : Mat4.Identity;
			_deform[index] = parentDeform * rest * basis * rest.Inverse();
		}

		return aligned;
	}

	/// <summary>
	/// 手臂：绕<b>源</b>关节旋转<b>源</b>骨架（<c>arm_scratch</c> 路径）。
	/// <para>
	/// 为什么注入在 <c>D</c> 层而不是顶点上：<c>D[b] = D[parent]·rest[b]·basis[b]·inv(rest[b])</c>
	/// 是<b>父先子后</b>递归的，所以 <c>l_shoulder</c> 上的一个旋转会被小臂、手和每一根手指继承。
	/// 整条肢体因此作为一个刚体转动、肘部按构造焊死，而 LBS 权重自己会把它平滑地混进胸腔。
	/// 直接旋转顶点做不到这件事——它会把上臂外壳挪走而把小臂外壳留在原地，那正是我们一直在追的裂缝。
	/// </para>
	/// <para>
	/// 枢轴是骨自己的头：用 <c>rest[b]</c> 共轭会把 <c>basis[b]</c> 变成绕 <c>rest[b]</c> 原点的旋转，
	/// 也就是绕 <b>snap 过的</b>肩关节——源模型自己的肩。
	/// </para>
	/// </summary>
	private Vec3[] BuildArmScratchPose(Vec3[] aligned)
	{
		foreach (var side in new[] { "l", "r" })
		{
			var upper = side.ToUpperInvariant();
			var sUpperArm = $"{upper}_UpperArm";
			var sForearm = $"{upper}_Forearm";
			var sHand = $"{upper}_Hand";
			var hShoulder = $"{side}_shoulder";
			var hElbow = $"{side}_elbow";
			var hHand = $"{side}_hand";
			if (!_shortByName.ContainsKey(sUpperArm) || !_shortByName.ContainsKey(sForearm)
				|| !_byName.ContainsKey(hShoulder) || !_byName.ContainsKey(hElbow))
				continue;
			if (!_byName.ContainsKey(hHand))
				throw new InvalidDataException($"HD2 骨架缺少 {hHand}，手臂无法重定向。");

			var shoulderIndex = _byName[hShoulder].Index;
			var elbowIndex = _byName[hElbow].Index;
			var handIndex = _byName[hHand].Index;

			var shoulderPivot = _restWorking[shoulderIndex].Origin;
			var elbowBind = _restWorking[elbowIndex].Origin;
			var upperDirection = elbowBind - shoulderPivot;
			var sourceUpperLength = upperDirection.Length;
			if (sourceUpperLength < 1e-9)
				continue;

			// 目标肘位置必须走一遍 unpost，才能与 rest_sym 处在同一个（pre-post）空间。
			var targetElbow = Unpost([_restHd2[elbowIndex].Origin])[0];
			var targetDirection = targetElbow - shoulderPivot;
			var targetUpperLength = targetDirection.Length;
			if (targetUpperLength < 1e-9)
				continue;

			var shoulderRotation = RetargetMath.MinArcRotation(upperDirection, targetDirection);
			var upperGain = targetUpperLength - sourceUpperLength;
			var upperAxis = upperDirection / sourceUpperLength;

			// 3b：把网格沿<b>源</b>上臂轴做线性轴向拉伸，肩端钉住、肘及以外整体平移。
			var (field, _, _, _) = ArmStretchField(aligned, side, shoulderPivot, elbowBind);
			for (var i = 0; i < aligned.Length; i++)
				aligned[i] += upperAxis * (upperGain * field[i]);
			_stageCallback?.Invoke("03_upper_arm_length", (Vec3[])aligned.Clone());

			// 把肘以下的<b>全部</b>工作枢轴一起搬走，否则后面的手会绕一个过期的枢轴转。
			var shift = RetargetMath.Trans(upperAxis * upperGain);
			foreach (var boneIndex in Subtree(elbowIndex))
				_restWorking[boneIndex] = shift * _restWorking[boneIndex];

			// `D[b] = rest[b]·basis[b]·inv(rest[b])`，而 basis 是纯旋转 Rb 时展开为
			// `T(ts)·(Rs·Rb·Rs⁻¹)·T(-ts)`——绕骨头旋转。所以绕<b>世界</b>轴转 R 需要
			// `Rb = Rs⁻¹·R·Rs`。传完整的 4x4 `rest` 会把平移抵消掉，于是整条手臂绕<b>世界原点</b>
			// 旋转，把手甩到一米开外。
			var shoulderRest = Mat3.FromMat4(_restWorking[shoulderIndex]);
			_basis[hShoulder] = (shoulderRest.Inverse() * shoulderRotation * shoulderRest).ToMat4();

			// ---- 肘：同样的想法，再往外一层 --------------------------------
			// 肩的旋转已被每个后代继承，所以小臂此时已经沿着<b>上臂骨</b>。还错的是肘自己的铰链：
			// HD2 的小臂并不延续上臂，它向前摆（俯视 46.5° 对 3.3°）。
			var forearmGain = _options.ArmForearmGain;
			Vec3 forearmTarget = Vec3.Zero;
			var haveForearmTarget = false;
			var elbowNow = Vec3.Zero;

			if (_shortByName.ContainsKey(sHand))
			{
				var elbowSource = _restWorking[elbowIndex].Origin;
				var wristSource = _restWorking[handIndex].Origin;
				var forearmSource = wristSource - elbowSource;

				if (_options.ArmAimWrist)
				{
					// 枢轴用的是小臂此刻真正绕的那个点：经过腿部站姿、上臂增益（连同整棵子树平移了
					// rest_sym[l_elbow]）和上面那次肩旋转之后的<b>源</b>肘。
					// 从 T-pose 的肘去量会差掉整个肩部移动。
					var shoulderNow = _restWorking[shoulderIndex].Origin;
					elbowNow = shoulderNow + shoulderRotation.Transform(elbowSource - shoulderNow);
					var wristTarget = Unpost([_restHd2[handIndex].Origin])[0];
					forearmTarget = wristTarget - elbowNow;
					haveForearmTarget = true;
				}
				else
				{
					forearmTarget = _restHd2[handIndex].Origin - _restHd2[elbowIndex].Origin;
					haveForearmTarget = true;
				}

				if (haveForearmTarget && Math.Abs(_options.ArmElbowOpen) > 1e-9)
				{
					var normal = Vec3.Cross(targetDirection, forearmTarget);
					if (normal.Length > 1e-9)
					{
						forearmTarget = RetargetMath.RotAxisAngle(
							normal.Normalized(), -(_options.ArmElbowOpen * Math.PI / 180.0)).Transform(forearmTarget);
					}
				}

				if (haveForearmTarget)
				{
					var elbowRotation = RetargetMath.MinArcRotation(
						forearmSource, shoulderRotation.Transposed().Transform(forearmTarget));
					var elbowRest = Mat3.FromMat4(_restWorking[elbowIndex]);
					_basis[hElbow] = (elbowRest.Inverse() * elbowRotation * elbowRest).ToMat4();

					// ---- 小臂长度补偿（仅网格） -----------------------------
					var forearmSourceLength = forearmSource.Length;
					var rayOrigin = _options.ArmAimWrist ? elbowNow : elbowSource;
					var wristTargetPoint = Unpost([_restHd2[handIndex].Origin])[0];
					var forearmTargetLength = (wristTargetPoint - rayOrigin).Length;
					forearmGain ??= forearmTargetLength - forearmSourceLength;

					if (forearmSourceLength >= 1e-9)
					{
						var axis = forearmSource / forearmSourceLength;
						var (_, far, arm) = ArmVertexPartition(side);
						var distal = HandFingerMask(side);
						var along = new double[aligned.Length];
						var tube = new bool[aligned.Length];
						for (var i = 0; i < aligned.Length; i++)
						{
							var segment = aligned[i] - elbowSource;
							// ⚠️ 必须用**单位**轴：`s_f = (v − E)·u_s` 是米，斜坡是 `s_f / L_src`（无量纲）。
							// 用未归一化的 `forearmSource` 会把斜坡整体乘上 L_src（≈0.29），
							// 于是肘部附近的顶点补偿量被压掉七成——不报错，只是"小臂短了一点点"。
							along[i] = Vec3.Dot(segment, axis);
							var projection = RetargetMath.Clamp(along[i] / Math.Max(forearmSourceLength, 1e-12), 0, 1);
							var perpendicular = segment - (forearmSource * projection);
							tube[i] = perpendicular.Length < RetargetConstants.ForearmGateRadius;
						}

						// ⚠️ 管闸绝不能作用到手和手指。闸是绕"肘→腕"线段半径 0.12 m 的圆管，
						// 而拇指远端在腕后 0.0418–0.1437 m——圆管会把远端指节拦腰切断并剪切，
						// 这就是"拇指反曲"（MourningVeil/Lunalice 上远端相对旋转 12.8°→13.2°）。
						// 实测：该闸拒绝 2186/2186 手+指顶点、0 个小臂顶点，也就是说它根本不保护披风，
						// 只切手指。生产配置下对手/指豁免。
						if (_options.ForearmGateHandExempt)
						{
							for (var i = 0; i < tube.Length; i++)
								tube[i] |= distal[i];
						}

						var weight = new double[aligned.Length];
						for (var i = 0; i < aligned.Length; i++)
						{
							var ramp = RetargetMath.Clamp(along[i] / forearmSourceLength, 0, 1)
								* (arm[i] && tube[i] ? 1.0 : 0.0);
							weight[i] = far[i] && arm[i] && tube[i] ? 1.0 : ramp;
						}

						for (var i = 0; i < aligned.Length; i++)
							aligned[i] += axis * (forearmGain.Value * weight[i]);
						_stageCallback?.Invoke($"06_forearm_length_{side}", (Vec3[])aligned.Clone());

						// 腕部枢轴一起搬。⚠️ `l_hand_twist` 是 `l_hand` 的<b>兄弟</b>（都挂在 `l_elbow` 下），
						// 只搬 `hd2_children` 会把 twist 枢轴留下，手外壳会落到内侧 23 mm 而不是外侧 39 mm。
						var seeds = new List<int> { handIndex };
						if (_byName.TryGetValue($"{side}_hand_twist", out var twist))
							seeds.Add(twist.Index);
						var shiftForearm = RetargetMath.Trans(axis * forearmGain.Value);
						foreach (var seed in seeds)
						{
							foreach (var boneIndex in Subtree(seed))
							{
								if (_restWorking.ContainsKey(boneIndex))
									_restWorking[boneIndex] = shiftForearm * _restWorking[boneIndex];
							}
						}
					}
				}
			}

			// ---- 手：绕腕的世界轴倾斜 --------------------------------------
			// 源 T-pose 里手臂是一根轴为 z 的钢管，拇指（张开时）永远指向管面的法线 x，y 是剩下的。
			// 实测两只拇指都指 +Y，也就是两只手掌都是旋前的（掌心朝下），所以手背在左是 +Z、在右是 -Z。
			// 把手的长轴朝手背方向翘（腕伸展，与手指蜷曲相反）因此是左侧绕 +Y 转 +tilt、右侧转 −tilt。
			if (Math.Abs(_options.ArmHandTilt) > 1e-9)
			{
				var sign = side == "l" ? 1.0 : -1.0;
				var handRotation = RetargetMath.RotAxisAngle(
					new Vec3(0, 1, 0), sign * _options.ArmHandTilt * Math.PI / 180.0);
				var handRest = Mat3.FromMat4(_restWorking[handIndex]);
				_basis[hHand] = (handRest.Inverse() * handRotation * handRest).ToMat4();
			}
		}

		return aligned;
	}

	/// <summary>某根 HD2 骨及其全部后代（含自身）。</summary>
	private List<int> Subtree(int root)
	{
		var result = new List<int>();
		var stack = new Stack<int>();
		stack.Push(root);
		while (stack.Count > 0)
		{
			var current = stack.Pop();
			result.Add(current);
			var children = _children[current];
			if (children is null)
				continue;
			foreach (var child in children)
				stack.Push(child);
		}

		return result;
	}

	/// <summary>
	/// 用一组骨变换做一次线性混合蒙皮（LBS）。权重保持文件原值、不重新归一化，只做加权平均——
	/// 这与 Source 的运行时一致，也是"根节点不动、网格不断"的前提。
	/// </summary>
	private Vec3[] SkinAll(IReadOnlyList<Vec3> points, IReadOnlyDictionary<int, Mat4> frames)
	{
		var result = new Vec3[points.Count];
		for (var k = 0; k < points.Count; k++)
		{
			var accumulated = Vec3.Zero;
			var total = 0.0;
			for (var j = 0; j < 3; j++)
			{
				var weight = _weights[k][j];
				if (weight <= 0)
					continue;
				var frame = Mat4.Identity;
				if (_resolved.TryGetValue(_boneIndices[k][j], out var boneIndex)
					&& frames.TryGetValue(boneIndex, out var matrix))
					frame = matrix;
				accumulated += frame.TransformPoint(points[k]) * weight;
				total += weight;
			}

			result[k] = total > 0 ? accumulated / total : points[k];
		}

		return result;
	}

	/// <summary>阶段 5：游戏 idle 帧真正显示的位置。native 骨架下这一步是恒等。</summary>
	private Vec3[] ApplyIdle(IReadOnlyList<Vec3> points)
	{
		var idle = new Dictionary<int, Mat4>(_order.Length);
		foreach (var index in _order)
			idle[index] = _restHd2[index] * _shippedRest[index].Inverse();
		return SkinAll(points, idle);
	}

	/// <summary>阶段 4c/4d 的共享后处理：先平移，再绕髋部做带 smoothstep 羽化的上半身缩放。</summary>
	private Vec3 Post(Vec3 point)
	{
		var result = point;
		if (_options.MeshShift != Vec3.Zero)
			result += _options.MeshShift;
		if (Math.Abs(_options.UpperScale - 1.0) > 1e-12)
		{
			var pivot = UpperPivot();
			var z0 = pivot.Z - _options.UpperBlend;
			var z1 = pivot.Z + _options.UpperBlend;
			var t = RetargetMath.Clamp((result.Z - z0) / Math.Max(z1 - z0, 1e-9), 0, 1);
			var w = t * t * (3.0 - (2.0 * t));
			var s = 1.0 + ((_options.UpperScale - 1.0) * w);
			result = pivot + ((result - pivot) * s);
		}

		return result;
	}

	/// <summary>
	/// <see cref="Post"/> 的逆。用 48 次二分而不是解析解，因为 smoothstep 羽化后的映射
	/// 在 z 上不是全局单调的闭式——二分与 Python 侧逐位一致，而"自己推一个解析近似"会引入
	/// 毫米级的静默偏差。
	/// </summary>
	private Vec3 Unpost(Vec3 point)
	{
		var result = point;
		if (Math.Abs(_options.UpperScale - 1.0) > 1e-12)
		{
			var pivot = UpperPivot();
			var z0 = pivot.Z - _options.UpperBlend;
			var z1 = pivot.Z + _options.UpperBlend;
			var targetZ = result.Z;
			var radius = (Math.Abs(_options.UpperScale - 1.0)
				* (Math.Abs(targetZ - pivot.Z) + _options.UpperBlend)) + 1e-6;
			var lo = targetZ - radius;
			var hi = targetZ + radius;
			for (var iteration = 0; iteration < 48; iteration++)
			{
				var mid = (lo + hi) * 0.5;
				var tm = RetargetMath.Clamp((mid - z0) / Math.Max(z1 - z0, 1e-9), 0, 1);
				var wm = tm * tm * (3.0 - (2.0 * tm));
				var mapped = pivot.Z + ((mid - pivot.Z) * (1.0 + ((_options.UpperScale - 1.0) * wm)));
				if (mapped < targetZ)
					lo = mid;
				else
					hi = mid;
			}

			var z = (lo + hi) * 0.5;
			var t = RetargetMath.Clamp((z - z0) / Math.Max(z1 - z0, 1e-9), 0, 1);
			var w = t * t * (3.0 - (2.0 * t));
			var scale = 1.0 + ((_options.UpperScale - 1.0) * w);
			result = new Vec3(
				pivot.X + ((result.X - pivot.X) / scale),
				pivot.Y + ((result.Y - pivot.Y) / scale),
				z);
		}

		if (_options.MeshShift != Vec3.Zero)
			result -= _options.MeshShift;
		return result;
	}

	private Vec3[] Unpost(IReadOnlyList<Vec3> points)
	{
		var result = new Vec3[points.Count];
		for (var i = 0; i < points.Count; i++)
			result[i] = Unpost(points[i]);
		return result;
	}

	private Vec3 UpperPivot()
	{
		var pivot = _byName.TryGetValue("hips", out var hips) ? _restHd2[hips.Index].Origin : Vec3.Zero;
		if (_options.UpperPivotZ is { } z)
			pivot = new Vec3(pivot.X, pivot.Y, z);
		return pivot;
	}

	/// <summary>阶段 4d：仅网格的上半身缩放。骨架不动，所以"把躯干调成正确大小"只能是"改网格"。</summary>
	private Vec3[] ApplyUpperScale(IReadOnlyList<Vec3> points)
	{
		var pivot = UpperPivot();
		var z0 = pivot.Z - _options.UpperBlend;
		var z1 = pivot.Z + _options.UpperBlend;
		var result = new Vec3[points.Count];
		for (var i = 0; i < points.Count; i++)
		{
			var t = RetargetMath.Clamp((points[i].Z - z0) / Math.Max(z1 - z0, 1e-9), 0, 1);
			var w = t * t * (3.0 - (2.0 * t));
			result[i] = pivot + ((points[i] - pivot) * (1.0 + ((_options.UpperScale - 1.0) * w)));
		}

		return result;
	}

	/// <summary>
	/// 阶段 4e(head)：仅网格的头部缩放。绕颈/头接缝（两个关节的中点）<b>等比</b>缩放，
	/// 所以颅骨不会变尖；长发也权重在 <c>head</c> 上，于是免费地跟着长大。
	/// </summary>
	private Vec3[] ApplyHeadScale(IReadOnlyList<Vec3> points)
	{
		if (!_byName.TryGetValue("head", out var head) || !_byName.TryGetValue("neck", out var neck))
			return [.. points];

		var subtree = Subtree(head.Index).ToHashSet();
		var pivot = (_shippedRest[neck.Index].Origin + _shippedRest[head.Index].Origin) * 0.5;
		var result = new Vec3[points.Count];
		for (var k = 0; k < points.Count; k++)
		{
			var bestBone = -1;
			var bestWeight = -1.0;
			for (var j = 0; j < 3; j++)
			{
				if (_weights[k][j] <= bestWeight)
					continue;
				bestBone = _boneIndices[k][j];
				bestWeight = _weights[k][j];
			}

			var selected = bestBone >= 0
				&& _resolved.TryGetValue(bestBone, out var resolvedBone)
				&& subtree.Contains(resolvedBone);
			result[k] = selected
				? pivot + ((points[k] - pivot) * _options.HeadScale)
				: points[k];
		}

		return result;
	}

	/// <summary>
	/// 阶段 2b：双腿绕脚踝前倾。源模型是严格竖直的腿，HD2（以及参考包）的脚踝在髋部之后约 10 cm。
	/// 把脚踝<b>以上</b>的一切绕脚踝旋转、同时把躯干刚性带走（保持直立），就复现了这个站姿而不动网格自身形状。
	/// 逐骨只挪骨头会剪切腿部，所以网格必须一起变。
	/// </summary>
	private Vec3[] ApplyLegTilt(Vec3[] aligned)
	{
		if (!_newHead.ContainsKey("hips") || !_newHead.ContainsKey("l_foot"))
			return aligned;

		var theta = _options.LegTilt * Math.PI / 180.0;
		var (ct, st) = (Math.Cos(theta), Math.Sin(theta));
		var ankle = (_newHead["l_foot"] + _newHead["r_foot"]) * 0.5;
		var rotation = RetargetMath.Trans(ankle)
			* new Mat4(1, 0, 0, 0, 0, ct, st, 0, 0, -st, ct, 0, 0, 0, 0, 1)
			* RetargetMath.Trans(ankle * -1.0);
		var delta = rotation.TransformPoint(_newHead["hips"]) - _newHead["hips"];

		var turned = new HashSet<string>(StringComparer.Ordinal);
		var planted = new HashSet<string>(StringComparer.Ordinal);
		foreach (var side in new[] { "l", "r" })
		{
			turned.Add($"{side}_thigh");
			turned.Add($"{side}_knee");
			planted.Add($"{side}_foot");
			planted.Add($"{side}_ball");
			planted.Add($"{side}_toe");
		}

		Vec3 Move(string name, Vec3 point) =>
			turned.Contains(name) ? rotation.TransformPoint(point)
			: planted.Contains(name) ? point
			: point + delta;

		foreach (var name in _newHead.Keys.ToArray())
			_newHead[name] = Move(name, _newHead[name]);

		// 网格必须跟着变，否则腿会被剪切。
		var resolved = ResolveBoneMap().Resolved;
		var result = new Vec3[aligned.Length];
		for (var k = 0; k < aligned.Length; k++)
		{
			var accumulated = Vec3.Zero;
			var total = 0.0;
			for (var j = 0; j < 3; j++)
			{
				var weight = _weights[k][j];
				if (weight <= 0)
					continue;
				var name = resolved.TryGetValue(_boneIndices[k][j], out var hd2) ? _nameByIndex[hd2] : null;
				var transform = name is not null && turned.Contains(name) ? rotation
					: name is not null && planted.Contains(name) ? Mat4.Identity
					: RetargetMath.Trans(delta);
				accumulated += transform.TransformPoint(aligned[k]) * weight;
				total += weight;
			}

			result[k] = total > 0 ? accumulated / total : aligned[k];
		}

		_stageCallback?.Invoke("02a_leg_tilt", (Vec3[])result.Clone());
		return result;
	}

	/// <summary>
	/// 阶段 2c：正视图中双腿外张。HD2 自己的骨架每只脚踝在髋部外侧 7.07°（踝距 0.382 m 对髋距 0.174 m），
	/// 而 L4D2 源是严格 T-pose 的竖直腿，所以正视看起来很僵。
	/// <para>
	/// 动作是 2b 的正视孪生：把腿绕它自己的<b>根</b>（大腿头）转 ±θ，再把脚绕脚踝反向转 −θ 让脚掌保持水平。
	/// 因为脚踝刚性随腿走，那次反向旋转塌缩成"脚平移 (踝新 − 踝旧)"——也就是"把每只脚往外挪半只脚"
	/// 那一步，只不过它是从几何里自己出来的，不是一个魔数。
	/// </para>
	/// </summary>
	private Vec3[] ApplyLegSplay(Vec3[] aligned)
	{
		if (!_newHead.ContainsKey("l_thigh") || !_newHead.ContainsKey("r_thigh"))
			return aligned;

		var theta = _options.LegSplay * Math.PI / 180.0;
		var transforms = new Dictionary<string, Mat4>(StringComparer.Ordinal);
		foreach (var (side, sign) in new[] { ("l", 1.0), ("r", -1.0) })
		{
			var root = _newHead[$"{side}_thigh"];
			var angle = sign * theta;
			var (ca, sa) = (Math.Cos(angle), Math.Sin(angle));
			var rotation = RetargetMath.Trans(root)
				* new Mat4(ca, 0, sa, 0, 0, 1, 0, 0, -sa, 0, ca, 0, 0, 0, 0, 1)
				* RetargetMath.Trans(root * -1.0);
			transforms[$"{side}_thigh"] = rotation;
			transforms[$"{side}_knee"] = rotation;
			var ankle = _newHead[$"{side}_foot"];
			transforms[$"{side}_foot"] = RetargetMath.Trans(rotation.TransformPoint(ankle) - ankle);
			foreach (var joint in new[] { "ball", "toe" })
			{
				if (_newHead.ContainsKey($"{side}_{joint}"))
					transforms[$"{side}_{joint}"] = transforms[$"{side}_foot"];
			}
		}

		foreach (var name in _newHead.Keys.ToArray())
		{
			if (transforms.TryGetValue(name, out var transform))
				_newHead[name] = transform.TransformPoint(_newHead[name]);
		}

		var resolved = ResolveBoneMap().Resolved;
		var result = new Vec3[aligned.Length];
		for (var k = 0; k < aligned.Length; k++)
		{
			var accumulated = Vec3.Zero;
			var total = 0.0;
			for (var j = 0; j < 3; j++)
			{
				var weight = _weights[k][j];
				if (weight <= 0)
					continue;
				var name = resolved.TryGetValue(_boneIndices[k][j], out var hd2) ? _nameByIndex[hd2] : null;
				var transform = name is not null && transforms.TryGetValue(name, out var found)
					? found
					: Mat4.Identity;
				accumulated += transform.TransformPoint(aligned[k]) * weight;
				total += weight;
			}

			result[k] = total > 0 ? accumulated / total : aligned[k];
		}

		_stageCallback?.Invoke("02_legs", (Vec3[])result.Clone());
		return result;
	}
}
