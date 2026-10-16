using HD2SourceModel;

namespace HD2SourceMigration.Retarget;

/// <summary>
/// L4D2 / Source 角色网格 → HD2 原生骨架的<b>重定向核心</b>。
/// <para>
/// 这是 <c>tmp/rig-lab/hd2_style.py</c> 的 <c>retarget()</c> 的 C# 移植，只保留发货配置所走的路径。
/// 数学上的三步（BatchTool 的做法，不是旧的逐骨相似映射 <c>T = H·S⁻¹</c>）：
/// </para>
/// <code>
/// v0 源顶点（米，源空间）
/// v1 = R_align · (scale · (v0 − sole)) + t              一次统一缩放 + 一次平移
/// head_HD2[b] = 对齐后的源关节（对应到 b）                只挪 HD2 骨的头，朝向与骨长保持 HD2 自己的
/// rest[b]  = HD2 rest（轴对称 + 头已 snap，父先子后）
/// D[b]     = D[parent] · rest[b] · basis[b] · inv(rest[b])   T→A 姿态
/// v'       = Σ w_i · D[b_i] · v1                          每顶点 LBS
/// </code>
/// <para>
/// <b>为什么这样就对了</b>：snap 发生在 bind <b>之前</b>，网格重新绑到已经 snap 过的骨架上，
/// 所以 bind pose 是恒等、rest 网格根本不变形——<b>不可能被拉伸</b>。因此没有长脖子、没有身高膨胀。
/// 发货的骨架是 HD2 原生的，运行时变形 <c>rest_hd2 · inv(S_bind)</c> 恰好是恒等，
/// 于是"离线摆成什么样，游戏里就是什么样"。
/// </para>
/// <para>
/// <b>移植纪律</b>：阶段的<b>顺序</b>是承重的。任何一条错了都不会报错，只会长得不对，
/// 所以下面的注释里保留了 Python 侧记录下来的症状与测量数字。
/// </para>
/// </summary>
public sealed partial class Retargeter
{
	private readonly SourceModelDocument _model;
	private readonly SourceGeometry _geometry;
	private readonly Hd2Rig _rig;
	private readonly BodyPoseProfile _profile;
	private readonly RetargetOptions _options;
	private readonly Action<string, Vec3[]>? _stageCallback;

	// ---- 源侧（按骨下标索引） ---------------------------------------------
	private readonly string[] _sourceNames;
	private readonly int[] _sourceParents;
	private readonly Mat4[] _sourceWorldMetres;
	private readonly Dictionary<string, int> _shortByName;
	private readonly Dictionary<int, int> _sourceToHd2 = [];

	// ---- HD2 侧 -------------------------------------------------------------
	private readonly Hd2RigBone[] _bonesByIndex;
	private readonly Dictionary<string, Hd2RigBone> _byName;
	private readonly string?[] _nameByIndex;
	private readonly Mat4[] _restHd2;
	private readonly int[] _parentOf;
	private readonly List<int>?[] _children;
	private readonly int[] _parents;
	private readonly int[] _order;

	// ---- 网格（按顶点索引） -------------------------------------------------
	private readonly Vec3[] _sourcePoints;
	private readonly double[][] _weights;
	private readonly int[][] _boneIndices;

	// ---- 阶段之间传递的状态 -------------------------------------------------
	private readonly Dictionary<string, Vec3> _newHead = new(StringComparer.Ordinal);
	private readonly Dictionary<int, Mat4> _restWorking = [];
	private readonly Dictionary<int, Mat4> _shippedRest = [];
	private readonly Dictionary<string, Mat4> _basis = new(StringComparer.Ordinal);
	private readonly Dictionary<int, Mat4> _deform = [];
	private Dictionary<int, int> _resolved = [];
	private Dictionary<int, int> _hops = [];
	private double _scale;
	private Mat3 _alignment = Mat3.Identity;
	private Vec3 _sole;
	private Vec3 _translation;
	private Vec3 _sourceHeadJoint;

	public Retargeter(
		SourceModelDocument model,
		SourceGeometry geometry,
		Hd2Rig rig,
		BodyPoseProfile? profile = null,
		RetargetOptions? options = null,
		Action<string, Vec3[]>? stageCallback = null)
	{
		_model = model ?? throw new ArgumentNullException(nameof(model));
		_geometry = geometry ?? throw new ArgumentNullException(nameof(geometry));
		_rig = rig ?? throw new ArgumentNullException(nameof(rig));
		_profile = profile ?? BodyPoseProfile.Default;
		_options = options ?? new RetargetOptions();
		_stageCallback = stageCallback;

		_sourceNames = new string[model.Bones.Count];
		_sourceParents = new int[model.Bones.Count];
		_sourceWorldMetres = new Mat4[model.Bones.Count];
		_shortByName = new Dictionary<string, int>(StringComparer.Ordinal);
		for (var i = 0; i < model.Bones.Count; i++)
		{
			var bone = model.Bones[i];
			_sourceNames[i] = bone.DisplayName;
			_sourceParents[i] = bone.ParentIndex;
			// 源侧单位是英寸：先把 bind 求逆，再把平移缩到米。不缩放不会报错，只会让整张对应表差 39.37 倍。
			_sourceWorldMetres[i] = RetargetMath.ScaleTranslation(
				bone.WorldBind, RetargetConstants.InchesToMetres);
			if (bone.HasName && bone.Name!.StartsWith(RetargetConstants.SourceBonePrefix, StringComparison.Ordinal))
				_shortByName.TryAdd(bone.Name[RetargetConstants.SourceBonePrefix.Length..], i);
		}

		var boneCount = rig.Bones.Count;
		_bonesByIndex = new Hd2RigBone[boneCount];
		_byName = new Dictionary<string, Hd2RigBone>(StringComparer.Ordinal);
		_nameByIndex = new string?[boneCount];
		_restHd2 = new Mat4[boneCount];
		_parentOf = new int[boneCount];
		_children = new List<int>?[boneCount];
		_parents = new int[boneCount];
		foreach (var bone in rig.Bones)
		{
			_bonesByIndex[bone.Index] = bone;
			_nameByIndex[bone.Index] = bone.Name;
			_restHd2[bone.Index] = bone.WorldBind;
			_byName.TryAdd(bone.DisplayName, bone);
		}

		_order = rig.Bones.Select(bone => bone.Index).OrderBy(index => index).ToArray();
		for (var i = 0; i < boneCount; i++)
		{
			_parentOf[i] = -1;
			_parents[i] = -1;
		}

		foreach (var bone in rig.Bones)
		{
			var parent = ResolveParent(bone);
			if (parent is null)
				continue;
			(_children[parent.Index] ??= []).Add(bone.Index);
			_parents[bone.Index] = parent.Index;
		}

		var vertexCount = geometry.Vertices.Count;
		_sourcePoints = new Vec3[vertexCount];
		_weights = new double[vertexCount][];
		_boneIndices = new int[vertexCount][];
		for (var i = 0; i < vertexCount; i++)
		{
			var vertex = geometry.Vertices[i];
			// Source 存的是英寸。
			_sourcePoints[i] = vertex.Position * RetargetConstants.InchesToMetres;
			_weights[i] = [vertex.Weight0.Weight, vertex.Weight1.Weight, vertex.Weight2.Weight];
			_boneIndices[i] = [vertex.Weight0.BoneIndex, vertex.Weight1.BoneIndex, vertex.Weight2.BoneIndex];
		}
	}

	private Hd2RigBone? ResolveParent(Hd2RigBone bone)
	{
		var parent = bone.ParentIndex;
		if (parent < 0 || parent == bone.Index || parent >= _bonesByIndex.Length)
			return null;
		return _bonesByIndex[parent];
	}

	/// <summary>跑完整条管线。返回的坐标一律是米、HD2 世界空间。</summary>
	public RetargetResult Run()
	{
		// ---- 0. 姿态参考表 + HD2 骨架 -------------------------------------
		// arm_scratch 下 BatchTool 的三张表全部<b>不参与</b>：它们 100% 只碰手臂链
		// （锁骨/肩/肘/手/手指，零躯干零腿零头），所以"别让 BatchTool 摆手臂"精确地等于
		// "三张全丢掉"，而且按构造不可能影响手臂以外的任何东西。
		var axisDeltas = _options.ArmScratch
			? new Dictionary<string, Mat4>(StringComparer.Ordinal)
			: new Dictionary<string, Mat4>(_profile.SymmetricAxisDeltas, StringComparer.Ordinal);
		var bodyNames = _profile.BodyBones;
		var basisT = _options.AxisRoundtrip && !_options.ArmScratch
			? new Dictionary<string, Mat4>(_profile.TposeBasisRotations, StringComparer.Ordinal)
			: new Dictionary<string, Mat4>(StringComparer.Ordinal);

		Mat4 AxisDelta(Hd2RigBone bone) =>
			_options.AxisDeltas
			&& bone.HasName
			&& bodyNames.Contains(bone.Name!)
			&& axisDeltas.TryGetValue(bone.Name!, out var delta)
				? delta
				: Mat4.Identity;

		// ---- 1a. 对齐旋转（源侧左 = +X，HD2 左侧 = −X） --------------------
		var body = SnapBody(_options.SpineMap);
		var sourceJoints = new List<Vec3>(body.Count);
		var hd2Joints = new List<Vec3>(body.Count);
		foreach (var (sourceName, hd2Name) in body)
		{
			if (!_shortByName.TryGetValue(sourceName, out var sourceIndex))
				continue;
			if (!_byName.TryGetValue(hd2Name, out var hd2Bone))
				continue;
			sourceJoints.Add(_sourceWorldMetres[sourceIndex].Origin);
			hd2Joints.Add(_restHd2[hd2Bone.Index].Origin);
		}

		_alignment = _options.Alignment switch
		{
			RetargetAlignment.Kabsch => RetargetMath.Kabsch(sourceJoints, hd2Joints),
			RetargetAlignment.Yaw => RetargetMath.YawRotation(sourceJoints, hd2Joints),
			RetargetAlignment.RotZ180 => new Mat3(-1, 0, 0, 0, -1, 0, 0, 0, 1),
			_ => Mat3.Identity,
		};

		// ---- 1b. 一次统一缩放 + 一次平移 -----------------------------------
		// 高度在 HD2 空间沿 +Z 量，与 BatchTool 的 `(head - ground)·local_z_world` 完全一致。
		var minimumZ = _sourcePoints.Min(point => point.Z);
		var soleSum = Vec3.Zero;
		var soleCount = 0;
		foreach (var point in _sourcePoints)
		{
			if (point.Z >= minimumZ + 0.01)
				continue;
			soleSum += point;
			soleCount++;
		}

		_sole = soleCount > 0 ? soleSum / soleCount : Vec3.Zero;
		_sourceHeadJoint = _shortByName.TryGetValue("Head1", out var headIndex)
			? _sourceWorldMetres[headIndex].Origin
			: Vec3.Zero;
		var headHd2 = _byName.TryGetValue("head", out var headBone)
			? _restHd2[headBone.Index].Origin
			: Vec3.Zero;
		const double referenceGroundZ = 0.0; // HD2 自己的脚底在 z = 0
		var sourceHeight = (_alignment.Transform(_sourceHeadJoint)).Z - (_alignment.Transform(_sole)).Z;
		_scale = _options.Scale > 0
			? _options.Scale
			: (headHd2.Z + _options.HeadOffset - referenceGroundZ) / Math.Max(sourceHeight, 1e-6);

		Vec3 AlignPoint(Vec3 point) => _alignment.Transform((point - _sole) * _scale);
		var headAligned = AlignPoint(_sourceHeadJoint);
		_translation = (headHd2 + new Vec3(0, 0, _options.HeadOffset)) - headAligned;
		var aligned = new Vec3[_sourcePoints.Length];
		for (var i = 0; i < aligned.Length; i++)
			aligned[i] = AlignPoint(_sourcePoints[i]) + _translation;
		_stageCallback?.Invoke("01_aligned", (Vec3[])aligned.Clone());

		// ---- 2. snap：HD2 骨的头 := 对齐后的源关节 -------------------------
		// 永远 snap。A-pose 是绕这些头旋转网格的，而只有源模型自己的关节才落在源网格内部。
		// 发货哪一套骨架是另一个决定，见 4b。
		Vec3 Joint(string sourceName) =>
			AlignPoint(_sourceWorldMetres[_shortByName[sourceName]].Origin) + _translation;

		var pairs = new List<(string Source, string Hd2)>(body);
		pairs.AddRange(FingerPairs());
		foreach (var (sourceName, hd2Name) in pairs.Concat(SnapToes()))
		{
			if (_shortByName.TryGetValue(sourceName, out var sourceBone)
				&& _byName.TryGetValue(hd2Name, out var hd2Bone))
				_sourceToHd2[sourceBone] = hd2Bone.Index;
		}

		var missing = new List<string>();
		foreach (var (sourceName, hd2Name) in pairs)
		{
			if (_shortByName.ContainsKey(sourceName) && _byName.ContainsKey(hd2Name))
				_newHead[hd2Name] = Joint(sourceName);
			else
				missing.Add($"{sourceName}->{hd2Name}");
		}

		foreach (var (sourceName, hd2Name) in SnapToes())
		{
			if (_shortByName.ContainsKey(sourceName) && _byName.ContainsKey(hd2Name))
				_newHead[hd2Name] = Joint(sourceName);
		}

		FillHd2OnlyHeads();

		// ---- 2b / 2c. 站姿 --------------------------------------------------
		if (Math.Abs(_options.LegTilt) > 1e-9)
			aligned = ApplyLegTilt(aligned);
		if (Math.Abs(_options.LegSplay) > 1e-9)
			aligned = ApplyLegSplay(aligned);

		// ---- 3. HD2 工作 rest：轴对称 + 工作头，一趟父先子后 ----------------
		foreach (var index in _order)
		{
			var bone = _bonesByIndex[index];
			var parent = ResolveParent(bone);
			var delta = AxisDelta(bone);
			var basis = basisT.TryGetValue(bone.DisplayName, out var b) ? b : Mat4.Identity;
			var current = parent is null
				? _restHd2[index] * delta
				: _restWorking[parent.Index] * _restHd2[parent.Index].Inverse() * _restHd2[index] * delta;
			current *= basis;
			if (_newHead.TryGetValue(bone.DisplayName, out var target))
				current = RetargetMath.Trans(target - current.Origin) * current;
			_restWorking[index] = current;
		}

		// ---- 3b / 4. T-pose → A-pose ---------------------------------------
		// `basis` 与 `D` 在这一步建起来；手臂的两处长度补偿同时改写 `aligned`。
		aligned = BuildPose(aligned);

		// ---- 4b. 发货的骨架 -------------------------------------------------
		foreach (var index in _order)
			_shippedRest[index] = _options.Skeleton == RetargetSkeleton.Native
				? _restHd2[index]
				: BuildShippedRest(index, AxisDelta);

		// ---- 蒙皮：源骨 → HD2 骨 -------------------------------------------
		var (resolved, hops, _) = ResolveBoneMap();
		_resolved = resolved;
		_hops = hops;

		var outPoints = SkinAll(aligned, _deform);

		// ---- 4e. 仅网格的手臂重拟合 -----------------------------------------
		if (_options.ArmFit != 0.0 && (!_options.ArmScratch || _options.ArmRefitScratch))
			outPoints = ApplyArmRefit(outPoints);

		// ---- 4c. 仅网格的平移 ----------------------------------------------
		if (_options.MeshShift != Vec3.Zero)
		{
			for (var i = 0; i < outPoints.Length; i++)
				outPoints[i] += _options.MeshShift;
		}

		// ---- 4d. 仅网格的上半身缩放 -----------------------------------------
		if (Math.Abs(_options.UpperScale - 1.0) > 1e-12)
			outPoints = ApplyUpperScale(outPoints);

		// ---- 4e(head). 仅网格的头部缩放 -------------------------------------
		if (Math.Abs(_options.HeadScale - 1.0) > 1e-9)
			outPoints = ApplyHeadScale(outPoints);

		// ---- 掌围必须在 4f 之前测 -------------------------------------------
		// 掌板外缘与指根重叠，4f 之后再测会把选定的蜷曲折进尺寸比值，缩放就会随蜷曲变化。
		var palmGirth = new Dictionary<string, double>(StringComparer.Ordinal);
		if (_options.PalmScaleAuto)
		{
			foreach (var side in new[] { "l", "r" })
			{
				var (radius, _) = PalmPerpRadius(outPoints, side);
				if (radius > 1e-9)
					palmGirth[side] = radius;
			}
		}

		// ---- 4f. 仅网格的手指残差姿态 ---------------------------------------
		var fingerRows = new List<FingerPoseRow>();
		if (_options.FingerPose)
			outPoints = ApplyFingerPose(outPoints, fingerRows);

		// ---- 4g. 仅网格的手掌转正（解出来的） --------------------------------
		var handRows = new List<HandReorientRow>();
		if (_options.HandReorient != HandReorientMode.Off)
			outPoints = ApplyHandReorient(outPoints, handRows);

		// ---- 4h. 仅网格的绕掌法线转手 ---------------------------------------
		if (Math.Abs(_options.HandTurn) > 1e-12)
			outPoints = ApplyHandTurn(outPoints);

		// ---- 4i. 仅网格的整手缩放（带羽化） ----------------------------------
		if (Math.Abs(_options.PalmScale - 1.0) > 1e-12 || _options.PalmScaleAuto)
			outPoints = ApplyPalmScale(outPoints, palmGirth);

		// ---- 5. 游戏真正显示的姿态 -------------------------------------------
		var idleOutput = ApplyIdle(outPoints);

		var metrics = new RetargetMetrics(
			_scale,
			sourceHeight,
			_sourcePoints.Max(p => p.Z) - _sourcePoints.Min(p => p.Z),
			outPoints.Max(p => p.Z) - outPoints.Min(p => p.Z),
			FootSpread(_sourcePoints),
			FootSpread(aligned),
			FootSpread(outPoints),
			_newHead.Count,
			missing,
			CountByHops(hops));

		return new RetargetResult(
			_sourcePoints,
			aligned,
			outPoints,
			idleOutput,
			_restWorking,
			_shippedRest,
			_deform,
			resolved,
			hops,
			fingerRows,
			handRows,
			palmGirth,
			metrics);
	}

	/// <summary>
	/// 回退跳数直方图：<b>跳数 → 骨数</b>（不是骨下标 → 跳数）。用它一眼看出"有多少根骨是直接命中、
	/// 有多少根要靠回退"，而回退链越长越可疑——长链通常意味着源骨架里有一大块辅助骨没有对端。
	/// </summary>
	private static IReadOnlyDictionary<int, int> CountByHops(IReadOnlyDictionary<int, int> hops)
	{
		var result = new Dictionary<int, int>();
		foreach (var value in hops.Values)
			result[value] = result.GetValueOrDefault(value) + 1;
		return result;
	}

	private static double FootSpread(IReadOnlyList<Vec3> points)	{
		var minimum = points.Min(point => point.Z);
		double low = double.MaxValue, high = double.MinValue;
		foreach (var point in points)
		{
			if (point.Z >= minimum + 0.06)
				continue;
			low = Math.Min(low, point.X);
			high = Math.Max(high, point.X);
		}

		return high >= low ? high - low : 0.0;
	}

	/// <summary>
	/// 源 → HD2 的骨名对应表（20 身体骨 + 30 手指骨 = 50 对）。脊椎三项按 <paramref name="spineMap"/> 替换。
	/// </summary>
	private static List<(string Source, string Hd2)> SnapBody(RetargetSpineMap spineMap)
	{
		var result = new List<(string, string)>(20);
		foreach (var (source, hd2) in BodyBones)
		{
			if (source is "Spine" or "Spine2" or "Spine4")
				continue;
			result.Add((source, hd2));
		}

		var spine = spineMap == RetargetSpineMap.Mesh
			? new[] { ("Spine", "spine2"), ("Spine2", "chest"), ("Spine4", "chest") }
			: new[] { ("Spine", "spine1"), ("Spine2", "spine2"), ("Spine4", "chest") };
		for (var i = 0; i < spine.Length; i++)
			result.Insert(1 + i, spine[i]);
		return result;
	}

	private static readonly (string Source, string Hd2)[] BodyBones =
	[
		("Pelvis", "hips"), ("Spine", "spine1"), ("Spine2", "spine2"), ("Spine4", "chest"),
		("Neck1", "neck"), ("Head1", "head"),
		("L_Clavicle", "l_clavicle"), ("L_UpperArm", "l_shoulder"),
		("L_Forearm", "l_elbow"), ("L_Hand", "l_hand"),
		("R_Clavicle", "r_clavicle"), ("R_UpperArm", "r_shoulder"),
		("R_Forearm", "r_elbow"), ("R_Hand", "r_hand"),
		("L_Thigh", "l_thigh"), ("L_Calf", "l_knee"), ("L_Foot", "l_foot"),
		("R_Thigh", "r_thigh"), ("R_Calf", "r_knee"), ("R_Foot", "r_foot"),
	];

	/// <summary>HD2 的脚掌/脚尖骨。并非每个源骨架都有脚趾（Lunalice_Zoey 就没有），
	/// 所以缺失是<b>正常</b>，不报错。刻意不进 50 对表，以保住"20 + 30"这个契约。</summary>
	private static IEnumerable<(string Source, string Hd2)> SnapToes()
	{
		yield return ("L_Toe0", "l_ball");
		yield return ("R_Toe0", "r_ball");
	}

	/// <summary>HD2 的手指链：源 <c>L_Finger2 / L_Finger21 / L_Finger22</c> ↔ HD2 <c>l_middle_finger1/2/3</c>。</summary>
	private static IEnumerable<(string Source, string Hd2)> FingerPairs()
	{
		var names = new[] { "thumb", "index", "middle", "ring", "pinky" };
		foreach (var (side, prefix) in new[] { ("L", "l"), ("R", "r") })
		{
			for (var finger = 0; finger < names.Length; finger++)
			{
				for (var joint = 0; joint < 3; joint++)
				{
					var suffix = joint == 0 ? string.Empty : joint.ToString(System.Globalization.CultureInfo.InvariantCulture);
					yield return ($"{side}_Finger{finger}{suffix}", $"{prefix}_{names[finger]}_finger{joint + 1}");
				}
			}
		}
	}

	/// <summary>HD2 独有骨（源侧没有对端）的确定性插值填充，对应 BatchTool 的 <c>build_snap_heads</c>。</summary>
	private void FillHd2OnlyHeads()
	{
		if (!_newHead.ContainsKey("spine1") && _newHead.TryGetValue("spine2", out var spine2))
			_newHead["spine1"] = Lerp(_newHead["hips"], spine2, 0.50);
		if (!_newHead.ContainsKey("spine2"))
			_newHead["spine2"] = Lerp(_newHead["spine1"], _newHead["neck"], 0.50);
		_newHead.TryAdd("chest", _newHead["spine2"]);

		foreach (var side in new[] { "l", "r" })
		{
			var shoulder = _newHead[$"{side}_shoulder"];
			var elbow = _newHead[$"{side}_elbow"];
			var wrist = _newHead[$"{side}_hand"];
			_newHead.TryAdd($"{side}_shoulder_twist", Lerp(shoulder, elbow, 0.42));
			_newHead.TryAdd($"{side}_hand_twist", wrist);
			if (_byName.ContainsKey($"{side}_shoulderarmour"))
				_newHead[$"{side}_shoulderarmour"] = Lerp(_newHead[$"{side}_clavicle"], shoulder, 0.90);
			if (_newHead.TryGetValue($"{side}_ball", out var ball) && _byName.ContainsKey($"{side}_toe"))
				_newHead[$"{side}_toe"] = ball + ((ball - _newHead[$"{side}_foot"]) * 0.35);
		}

		if (_byName.ContainsKey("head_tip"))
			_newHead["head_tip"] = _newHead["head"] + (_newHead["head"] - _newHead["neck"]);
	}

	private static Vec3 Lerp(Vec3 from, Vec3 to, double factor) => from + ((to - from) * factor);

	private Mat4 BuildShippedRest(int index, Func<Hd2RigBone, Mat4> axisDelta)
	{
		var bone = _bonesByIndex[index];
		var parent = ResolveParent(bone);
		var sa = _deform[index] * _restWorking[index];
		var local = parent is null
			? sa
			: (_deform[parent.Index] * _restWorking[parent.Index]).Inverse() * sa;
		if (_options.AxisRoundtrip)
			local *= axisDelta(bone).Inverse();
		var chain = parent is null ? local : _shippedRest[parent.Index] * local;
		return RetargetMath.Trans(sa.Origin - chain.Origin) * chain;
	}
}
