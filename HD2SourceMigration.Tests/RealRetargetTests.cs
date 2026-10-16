using HD2SourceModel;
using HD2SourceMigration.Retarget;
using HD2SourceMigration.Segmentation;

namespace HD2SourceMigration.Tests;

/// <summary>
/// M1 验收闸门：C# 的 <see cref="Retargeter"/> 与 Python 实验线（<c>tmp/rig-lab/hd2_style.py</c>）
/// 在同一份输入上的输出必须一致到 <b>1e-6 m</b>。
/// <para>
/// 为什么必须有这道闸门：管线有 22 个阶段，阶段的<b>顺序</b>、<c>post</c>/<c>unpost</c> 的空间纪律、
/// <c>basis</c> 的局部共轭、4f→4g→4h→4i 的先后——任何一条错了都<b>不报错</b>，只是长得不对。
/// 没有这个对拍，移植就是在猜。
/// </para>
/// <para>
/// <b>唯一的例外</b>是 <see cref="InheritedArmVertices"/> 标出的那批顶点：挂在手臂上的无名辅助骨
/// （<c>source_extra_78</c> 挂在手上，顶点就在腕上）。Python 把它们钉死，C# 让它们跟着宿主肢体走，
/// 因为钉死会在腕上折出一层翻过来的皮。例外是<b>按顶点</b>给的，不是放宽容差——那批顶点之外的
/// 每一个顶点仍然必须逐位一致。
/// </para>
/// <para>
/// 样本表刻意放了三个模型，而不是一个：它们各自压着管线的不同部位。
/// MourningVeil 是"管闸曾拒绝全部手/指顶点"的那个，Lilium 有两条 1.8 m 的缎带链
/// （链式规则的靶子）。只测一个模型，等于只测了一条路径。
/// </para>
/// <para>
/// 每个样本需要三样东西，缺任何一样就<b>静默跳过</b>（与 <see cref="RealRigMapTests"/> 同一约定）：
/// 样本 VPK、HD2 avatar Unit 的 TocData、Bones 资源的 hash→名字表，外加该样本的 Python 基准 npz。
/// VPK 路径可用 <c>HD2_M1_VPK_&lt;TAG&gt;</c> 覆盖，基准根目录用 <c>HD2_M1_REFERENCE_ROOT</c>。
/// </para>
/// <para>
/// 基准的生成方式（每个样本一次）：
/// <c>python tmp/rig-lab/_m1_reference.py &lt;vpk&gt; &lt;model&gt; &lt;基准根&gt;\m1-ref-&lt;TAG&gt; full</c>
/// </para>
/// </summary>
public sealed class RealRetargetTests
{
	private const string DefaultAvatarTocPath =
		@"E:\Data\source\repos\WpfApp1\tmp\rig-lab\avatar\unit_0x4d1c334d294dfa97.toc";

	private const string DefaultBoneHashPath =
		@"E:\Data\source\repos\WpfApp1\tmp\pb-ref\HD2PhysBone-ReferenceSource-main\kit\tools\bonehash.txt";

	private const string DefaultReferenceRoot = @"E:\tmp";

	private const string L4D2Addons =
		@"D:\SteamLibrary\steamapps\common\Left 4 Dead 2\left4dead2\addons";

	/// <summary>闸门。1e-6 m 是"同一个 double 算法"应当达到的量级：差异只来自浮点求和次序。</summary>
	private const double ToleranceMetres = 1e-6;

	/// <summary>
	/// 手臂上允许的传播量（米）。见 <see cref="ArmVertexMasks"/>：辅助骨归位之后，掌围与手掌转正
	/// 这两步会<b>按新几何重新求解</b>，整只手跟着挪一点。0.5 度的搜索步在 100 mm 力臂上是 0.9 mm，
	/// 实测 MourningVeil 最差 1.53 mm，所以取 2 mm——它只是"解变了"的量级，不是"可以随便差"。
	/// </summary>
	private const double ArmPropagationTolerance = 2e-3;

	/// <param name="Tag">基准目录后缀，同时也是 <c>HD2_M1_VPK_&lt;TAG&gt;</c> 的标签。</param>
	/// <param name="VpkPath">样本 VPK 的默认位置。</param>
	/// <param name="ModelStem">要挑哪个 <c>.mdl</c>；同一个内部模型名可能出现在多个 VPK 里。</param>
	private sealed record RetargetSample(string Tag, string VpkPath, string ModelStem)
	{
		public string? ResolveVpk()
		{
			var configured = Environment.GetEnvironmentVariable($"HD2_M1_VPK_{Tag}");
			if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
				return configured;
			return File.Exists(VpkPath) ? VpkPath : null;
		}

		public string ReferenceDirectory(string root) => Path.Combine(root, $"m1-ref-{Tag}");
	}

	private static readonly RetargetSample[] Samples =
	[
		new("LU", Path.Combine(L4D2Addons, "Lunalice_Zoey.vpk"), "survivor_teenangst"),
		new("MV", @"E:\tmp\mourning\MourningVeil_Zoey.vpk", "survivor_teenangst"),
		new("LI", Path.Combine(L4D2Addons, "Lilium_Sancta_Bill.vpk"), "survivor_namvet"),
	];

	private static string? ResolveFile(string variable, string fallback)
	{
		var configured = Environment.GetEnvironmentVariable(variable);
		if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
			return configured;
		return File.Exists(fallback) ? fallback : null;
	}

	/// <summary>把源模型与 HD2 骨架读齐；骨架输入缺任何一份就返回 <c>null</c>（跳过全部样本）。</summary>
	private static (string TocPath, string BoneHashPath, string ReferenceRoot)? LoadSharedInputs()
	{
		var tocPath = ResolveFile("HD2_AVATAR_TOC", DefaultAvatarTocPath);
		var boneHashPath = ResolveFile("HD2_BONEHASH", DefaultBoneHashPath);
		var referenceRoot = Environment.GetEnvironmentVariable("HD2_M1_REFERENCE_ROOT");
		if (string.IsNullOrWhiteSpace(referenceRoot) || !Directory.Exists(referenceRoot))
			referenceRoot = DefaultReferenceRoot;
		if (tocPath is null || boneHashPath is null)
			return null;
		return (tocPath, boneHashPath, referenceRoot);
	}

	private static (RetargetInputs Inputs, Hd2Rig Rig) Load(RetargetSample sample, string tocPath, string boneHashPath)
	{
		var vpkPath = sample.ResolveVpk() ?? throw new InvalidOperationException("VPK 缺失，调用方应先检查。");
		return (
			SourceRetargetPipeline.LoadSource(vpkPath, sample.ModelStem),
			SourceRetargetPipeline.LoadRig(tocPath, boneHashPath));
	}

	/// <summary>最终网格的逐顶点对拍：绑定姿态、游戏内 idle 姿态，以及两个中间点集。</summary>
	[Fact]
	public void RealData_MatchesThePythonPipelineVertexForVertex()
	{
		var shared = LoadSharedInputs();
		if (shared is null)
			return;
		var (tocPath, boneHashPath, referenceRoot) = shared.Value;

		var report = new List<string>();
		var failed = false;
		var tested = 0;
		foreach (var sample in Samples)
		{
			if (sample.ResolveVpk() is null)
				continue;
			var referencePath = Path.Combine(sample.ReferenceDirectory(referenceRoot), "m1-full.npz");
			if (!File.Exists(referencePath))
				continue;

			tested++;
			var (inputs, rig) = Load(sample, tocPath, boneHashPath);
			var result = SourceRetargetPipeline.Run(inputs, rig);
			var reference = NpzFile.Read(referencePath);
			Assert.Equal(inputs.Geometry.Vertices.Count, result.Output.Count);

			var masks = ArmVertexMasks(inputs);
			var exempt = masks.Inherited.Count(flag => flag);
			var armCount = masks.OnArm.Count(flag => flag);

			// 四个阶段一起报：只报第一个失败的那一个，会把"误差是在对齐、snap、手臂还是手指引入的"
			// 这条线索藏起来——而这条线索正是定位问题的全部。
			foreach (var (key, actual) in new (string, IReadOnlyList<Vec3>)[]
			{
				("src_tpose", result.SourcePoints),
				("snapped", result.Aligned),
				("ours_bind", result.Output),
				("ours_game", result.IdleOutput),
			})
			{
				// 第一档：手臂之外逐位对拍。移植错误正是在那里现形，所以这一档不放任何水。
				var (offArm, offArmIndex, offArmOver, _) = Compare(reference[key], actual, masks.OnArm);
				// 第二档：手臂上只要求"解变了"的量级。
				var (worst, worstIndex, over, skipped) = Compare(reference[key], actual, masks.Inherited);
				report.Add(
					$"{sample.Tag}/{key}: 臂外最差 {offArm * 1000:F9} mm @#{offArmIndex}（超差 {offArmOver}）；"
					+ $"臂上最差 {worst * 1000:F6} mm @#{worstIndex}（超差 {over}，辅助骨豁免 {skipped}/{exempt}，臂顶点 {armCount}）");
				failed |= offArm >= ToleranceMetres;
				failed |= worst >= ArmPropagationTolerance;
			}
		}

		Assert.True(tested > 0, "没有任何样本可用：请检查 VPK 与 Python 基准 npz 是否存在。");
		Assert.False(failed, string.Join(Environment.NewLine, report));
	}

	/// <summary>
	/// 逐<b>阶段</b>对拍。最终网格差了若干微米时，只看网格只能知道"误差在某个阶段之前或之中"；
	/// 把 Python 侧 <c>stage_callback</c> 抓到的中间数组与 C# 的同一阶段逐个比，就能定位到
	/// 是"对齐"、"腿姿"、"上臂拉伸"还是"小臂拉伸"引入的。移植期间正是靠它抓到了
	/// "小臂斜坡误用未归一化轴"这个把补偿量压掉七成的错误——上臂阶段的每一个标量都逐位相同，
	/// 只有最终位移不同。
	/// </summary>
	[Fact]
	public void RealData_StagePointsMatchPython()
	{
		var shared = LoadSharedInputs();
		if (shared is null)
			return;
		var (tocPath, boneHashPath, referenceRoot) = shared.Value;

		var report = new List<string>();
		var failed = false;
		foreach (var sample in Samples)
		{
			if (sample.ResolveVpk() is null)
				continue;
			var stagesPath = Path.Combine(sample.ReferenceDirectory(referenceRoot), "stages-full.npz");
			if (!File.Exists(stagesPath))
				continue;

			var (inputs, rig) = Load(sample, tocPath, boneHashPath);
			var captured = new Dictionary<string, Vec3[]>(StringComparer.Ordinal);
			SourceRetargetPipeline.Run(inputs, rig, stageCallback: (name, points) => captured[name] = points);

			var masks = ArmVertexMasks(inputs);
			var exempt = masks.Inherited.Count(flag => flag);
			var reference = NpzFile.Read(stagesPath);
			foreach (var (key, actual) in captured.OrderBy(pair => pair.Key, StringComparer.Ordinal))
			{
				if (!reference.TryGetValue(key, out var array))
				{
					report.Add($"{sample.Tag}/{key}: Python 侧没有这个阶段");
					failed = true;
					continue;
				}

				// 阶段是"手臂补偿"本身，还没有下游按几何重新求解的那一层，所以这里只有一档：
				// 辅助骨之外必须逐位一致。
				var (worst, worstIndex, over, skipped) = Compare(array, actual, masks.Inherited);
				report.Add($"{sample.Tag}/{key}: 最差 {worst * 1000:F9} mm @#{worstIndex}，超差 {over}/{actual.Length}，辅助骨豁免 {skipped}/{exempt}");
				failed |= worst >= ToleranceMetres;
			}
		}

		Assert.False(failed, string.Join(Environment.NewLine, report));
	}

	/// <summary>
	/// 骨架中间量对拍：<c>rest_sym</c> / <c>rest_shipped</c> / <c>D</c> 三张表逐骨比，
	/// 外加源骨 → HD2 骨的绑定表。三张表用的是 1e-9 的绝对界——它们是 double 递推的产物，
	/// 差异只应来自求和次序。
	/// </summary>
	[Fact]
	public void RealData_RigStagesMatchPython()
	{
		var shared = LoadSharedInputs();
		if (shared is null)
			return;
		var (tocPath, boneHashPath, referenceRoot) = shared.Value;

		var report = new List<string>();
		var failed = false;
		foreach (var sample in Samples)
		{
			if (sample.ResolveVpk() is null)
				continue;
			var rigPath = Path.Combine(sample.ReferenceDirectory(referenceRoot), "rig-full.npz");
			if (!File.Exists(rigPath))
				continue;

			var (inputs, rig) = Load(sample, tocPath, boneHashPath);
			var result = SourceRetargetPipeline.Run(inputs, rig);
			var reference = NpzFile.Read(rigPath);

			foreach (var (key, actual) in new (string, IReadOnlyDictionary<int, Mat4>)[]
			{
				("rest_working", result.WorkingRest),
				("rest_shipped", result.ShippedRest),
				("deform", result.Deform),
			})
			{
				var expected = reference[key].AsDoubleMatrix();
				var worst = 0.0;
				var worstBone = -1;
				for (var i = 0; i < expected.GetLength(0); i++)
				{
					if (!actual.TryGetValue(i, out var matrix))
						continue;
					var values = new[]
					{
						matrix.M11, matrix.M12, matrix.M13, matrix.M14,
						matrix.M21, matrix.M22, matrix.M23, matrix.M24,
						matrix.M31, matrix.M32, matrix.M33, matrix.M34,
						matrix.M41, matrix.M42, matrix.M43, matrix.M44,
					};
					for (var k = 0; k < 16; k++)
					{
						var distance = Math.Abs(values[k] - expected[i, k]);
						if (distance <= worst)
							continue;
						worst = distance;
						worstBone = i;
					}
				}

				var name = worstBone >= 0 && worstBone < rig.Bones.Count
					? rig.Bones[worstBone].DisplayName
					: "?";
				report.Add($"{sample.Tag}/{key}: 最差元素差 {worst:E3} @骨 {worstBone}（{name}）");
				failed |= worst >= 1e-9;
			}

			var expectedResolved = reference["resolved"].AsDoubleMatrix();
			var mismatched = 0;
			for (var i = 0; i < expectedResolved.GetLength(0); i++)
			{
				var expectedValue = (int)expectedResolved[i, 0];
				var actualValue = result.Resolved.TryGetValue(i, out var mapped) ? mapped : -1;
				if (actualValue != expectedValue)
					mismatched++;
			}

			report.Add($"{sample.Tag}/resolved: 不一致 {mismatched} 根");
			failed |= mismatched > 0;
		}

		Assert.False(failed, string.Join(Environment.NewLine, report));
	}

	/// <summary>
	/// 回归闸门：腕部不能出现"翻过去的一层皮"。
	/// <para>
	/// 判据是<b>二面角</b>，不是截图：一块面折回去时，共享那条边的两个三角形必然背对背，
	/// 二面角趋近 180 度。光滑肢体上一条边转过 90 度以上本身就已经不正常，所以阈值取 90 度。
	/// </para>
	/// <para>
	/// 这一条对应的是用户实机看到的现象（腕上"一层皮折了一下变成 3 层"，静止就能看到）。
	/// 根因是 <c>source_extra_78</c> 这类挂在手上的无名辅助骨被长度补偿钉死，
	/// 详见 <see cref="SourceArmBones"/>。修复前腕窗 t/L 0.95–1.10 里有 22 条边超过 150 度
	/// （最大 175.2 度），修复后是 0 条、最大 43.5 度。
	/// </para>
	/// <para>
	/// 它不依赖任何 Python 基准，所以即使没有 <c>m1-ref-*</c> 目录也照样守着。
	/// </para>
	/// </summary>
	[Fact]
	public void RealData_WristHasNoFoldedSurface()
	{
		var shared = LoadSharedInputs();
		if (shared is null)
			return;
		var (tocPath, boneHashPath, _) = shared.Value;

		const double foldThresholdDegrees = 90.0;
		const int minimumWindowEdges = 50;
		var report = new List<string>();
		var failed = false;
		var tested = 0;

		foreach (var sample in Samples)
		{
			if (sample.ResolveVpk() is null)
				continue;
			tested++;
			var (inputs, rig) = Load(sample, tocPath, boneHashPath);
			var result = SourceRetargetPipeline.Run(inputs, rig);

			foreach (var side in new[] { "l", "r" })
			{
				var elbow = rig.Find($"{side}_elbow");
				var hand = rig.Find($"{side}_hand");
				if (elbow is null || hand is null
					|| !result.ShippedRest.TryGetValue(elbow.Index, out var elbowRest)
					|| !result.ShippedRest.TryGetValue(hand.Index, out var handRest))
				{
					report.Add($"{sample.Tag}/{side}: 骨架里没有 {side}_elbow / {side}_hand，跳过");
					continue;
				}

				var shipped = ShippedBodyPartTriangles(inputs, rig, result);
				var census = WristFoldCensus(inputs, result.Output, elbowRest, handRest, shipped);
				report.Add($"{sample.Tag}/{side}: 腕窗边 {census.Count} 条，最大二面角 {census.WorstDegrees:F1} 度 "
					+ $"@t/L {census.WorstT:F3}，超 {foldThresholdDegrees:F0} 度 {census.Over} 条"
					+ (census.Over == 0 ? string.Empty : $"（最差那条：材质 {census.WorstMaterials}，主骨 {census.WorstBones}）"));
				failed |= census.Count < minimumWindowEdges || census.WorstDegrees >= foldThresholdDegrees;
			}
		}

		Assert.True(tested > 0, "没有任何样本可用：请检查 VPK 与骨架输入是否存在。");
		Assert.False(failed,
			$"腕部出现翻折（阈值 {foldThresholdDegrees} 度，窗口至少要 {minimumWindowEdges} 条边）："
			+ Environment.NewLine + string.Join(Environment.NewLine, report));
	}

	/// <summary>
	/// 腕部窗口里"一条边转过多少度"的普查。
	/// <para>
	/// 窗口刻意是<b>腕骨附近的一条带</b>（沿肘→腕轴 t/L 0.95–1.10、离轴 &lt; 45 mm），
	/// 而不是整条手臂：肘部本来就有真实的硬折边（本模型 172.6 度），把它算进来会让闸门永远红。
	/// </para>
	/// </summary>
	private static WristFoldReport WristFoldCensus(
		RetargetInputs inputs, IReadOnlyList<Vec3> points, Mat4 elbow, Mat4 hand, bool[] shipped)
	{
		var origin = elbow.Origin;
		var axis = hand.Origin - origin;
		var length = axis.Length;
		if (length < 1e-9)
			return new WristFoldReport(0, 0.0, 0.0, 0, string.Empty, string.Empty);
		axis /= length;

		var triangles = inputs.Geometry.Triangles;
		var first = new Dictionary<(int A, int B), int>();
		var count = 0;
		var over = 0;
		var worst = 0.0;
		var worstT = 0.0;
		var worstMaterials = string.Empty;
		var worstBones = string.Empty;
		var bones = inputs.Model.Bones;

		for (var i = 0; i < triangles.Count; i++)
		{
			if (!shipped[i])
				continue;
			var triangle = triangles[i];
			Span<int> corners = [triangle.A, triangle.B, triangle.C];
			var normal = FaceNormal(points, triangle);
			for (var k = 0; k < 3; k++)
			{
				var a = corners[k];
				var b = corners[(k + 1) % 3];
				var key = a < b ? (a, b) : (b, a);
				if (!first.Remove(key, out var other))
				{
					first[key] = i;
					continue;
				}

				var mid = (Axial(points[a], origin, axis, length) + Axial(points[b], origin, axis, length)) * 0.5;
				var radius = (Perpendicular(points[a], origin, axis) + Perpendicular(points[b], origin, axis)) * 0.5;
				if (mid < 0.95 || mid > 1.10 || radius > 0.045)
					continue;

				var cosine = Math.Clamp(Dot(normal, FaceNormal(points, triangles[other])), -1.0, 1.0);
				var degrees = Math.Acos(cosine) * 180.0 / Math.PI;
				count++;
				if (degrees > 90.0)
					over++;
				if (degrees <= worst)
					continue;
				worst = degrees;
				worstT = mid;
				worstMaterials = $"{triangles[i].MaterialIndex}/{triangles[other].MaterialIndex}";
				worstBones = string.Join('+', new[] { a, b }.Select(index =>
					PrimaryBoneName(inputs, bones, index) ?? "?"));
			}
		}

		return new WristFoldReport(count, worst, worstT, over, worstMaterials, worstBones);
	}

	/// <summary>
	/// 我们<b>真正会写进包</b>的三角形：<c>source convert</c> 不带 <c>--components</c> 时只出七份本体，
	/// 那 30 个可切换 bodygroup（头发/衣服/配件）根本不进包。
	/// <para>
	/// 这一点必须在这里挡住：源 LOD0 有 159363 个三角形，七份本体只占约 39k，其余都是配件。
	/// 拿整份源网格去普查，会把配件的硬折边（披风、袖口，实测 MourningVeil 178 度）算成
	/// "腕部翻折"——而那些几何我们从来没写出去过，游戏里也不存在。
	/// </para>
	/// </summary>
	private static bool[] ShippedBodyPartTriangles(RetargetInputs inputs, Hd2Rig rig, RetargetResult result)
	{
		var split = SourceSegmentation.Split(
			inputs.Model, inputs.Geometry, new BodyRegionResolver(rig), result.Resolved);
		var shipped = new bool[inputs.Geometry.Triangles.Count];
		foreach (var group in split.BodyParts)
		{
			foreach (var index in group.TriangleIndices)
				shipped[index] = true;
		}

		return shipped;
	}

	/// <summary>腕部窗口的普查结果；超阈值时带上"最差那条边是谁"的诊断。</summary>
	private sealed record WristFoldReport(
		int Count,
		double WorstDegrees,
		double WorstT,
		int Over,
		string WorstMaterials,
		string WorstBones);

	/// <summary>某顶点主骨（权重最大者）的显示名。</summary>
	private static string? PrimaryBoneName(RetargetInputs inputs, IReadOnlyList<SourceBone> bones, int index)
	{
		var vertex = inputs.Geometry.Vertices[index];
		var best = vertex.Weight0.Weight >= vertex.Weight1.Weight && vertex.Weight0.Weight >= vertex.Weight2.Weight
			? vertex.Weight0
			: vertex.Weight1.Weight >= vertex.Weight2.Weight ? vertex.Weight1 : vertex.Weight2;
		return best.Weight <= 0f || best.BoneIndex < 0 || best.BoneIndex >= bones.Count
			? null
			: bones[best.BoneIndex].DisplayName;
	}

	private static double Axial(Vec3 point, Vec3 origin, Vec3 axis, double length)
		=> Dot(point - origin, axis) / length;

	private static double Perpendicular(Vec3 point, Vec3 origin, Vec3 axis)
	{
		var relative = point - origin;
		return (relative - (axis * Dot(relative, axis))).Length;
	}

	private static Vec3 FaceNormal(IReadOnlyList<Vec3> points, SourceTriangle triangle)
	{
		var a = points[triangle.A];
		var normal = Vec3.Cross(points[triangle.B] - a, points[triangle.C] - a);
		return normal.Length < 1e-18 ? new Vec3(0, 0, 0) : normal.Normalized();
	}

	private static double Dot(Vec3 a, Vec3 b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);

	private static (double Worst, int WorstIndex, int Over, int Allowed) Compare(
		NpyArray expected, IReadOnlyList<Vec3> actual, bool[]? allowed = null)
	{
		var matrix = expected.AsDoubleMatrix();
		Assert.Equal(matrix.GetLength(0), actual.Count);
		var worst = 0.0;
		var worstIndex = -1;
		var over = 0;
		var skipped = 0;
		for (var i = 0; i < actual.Count; i++)
		{
			// 允许分歧的顶点整点跳过：它们既不进最差值，也不进超差计数。
			// 为什么不是"放宽容差"：容差一放宽，同一批顶点上的<b>新</b>错误也会被吞掉；
			// 按顶点豁免则只放过已知的那一族，别处仍然是 1e-6 m。
			if (allowed is not null && allowed[i])
			{
				skipped++;
				continue;
			}

			var dx = actual[i].X - matrix[i, 0];
			var dy = actual[i].Y - matrix[i, 1];
			var dz = actual[i].Z - matrix[i, 2];
			var distance = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
			if (distance >= ToleranceMetres)
				over++;
			if (distance <= worst)
				continue;
			worst = distance;
			worstIndex = i;
		}

		return (worst, worstIndex, over, skipped);
	}

	/// <summary>
	/// C# 与 Python 之间<b>已知且刻意</b>的那一处差异：挂在手臂上的辅助骨。
	/// <para>
	/// 这些模型带 60 根读不出名字的 helper 骨（<c>source_extra_{index}</c>），其中
	/// <c>source_extra_78</c> 挂在手上、顶点就落在腕上。Python 只按骨名判"算不算手臂"，
	/// 于是把它钉死、让它的 <c>L_Hand</c> 邻居整体平移一个长度补偿量——同一条边上权重跳 1.0，
	/// 腕上折出 22 条超过 150 度的边（源网格同一窗口只有 2 条）。
	/// C# 改成沿源骨架上溯到宿主肢体（<see cref="SourceArmBones"/>），腕区回到 0 条。
	/// </para>
	/// <para>
	/// <b>为什么最终网格要分两档判。</b> 修正后的手臂几何会继续往下游走：掌围是在 4f 之前按
	/// <c>outPoints</c> 量的，4g 的手掌转正是"解一个让手肉贴合 HD2 指骨的刚性旋转"。
	/// 这两步都会<b>按几何</b>重新求解，所以腕上动了之后整只手的解都会跟着挪一点点。
	/// 于是：
	/// </para>
	/// <list type="bullet">
	/// <item>手臂<b>之外</b>（躯干、腿、头、披风）仍然逐位对拍 1e-6 m——移植错误正是在那里现形；</item>
	/// <item>手臂上允许到 <see cref="ArmPropagationTolerance"/>，用来盖住"解变了"这一层。</item>
	/// </list>
	/// </summary>
	private static (bool[] Inherited, bool[] OnArm) ArmVertexMasks(RetargetInputs inputs)
	{
		var bones = inputs.Model.Bones;
		var names = bones.Select(bone => bone.DisplayName).ToArray();
		var parents = bones.Select(bone => bone.ParentIndex).ToArray();
		var vertices = inputs.Geometry.Vertices;
		var inherited = new bool[vertices.Count];
		var onArm = new bool[vertices.Count];
		for (var i = 0; i < vertices.Count; i++)
		{
			var vertex = vertices[i];
			var best = vertex.Weight0.Weight >= vertex.Weight1.Weight
				&& vertex.Weight0.Weight >= vertex.Weight2.Weight
				? vertex.Weight0
				: vertex.Weight1.Weight >= vertex.Weight2.Weight ? vertex.Weight1 : vertex.Weight2;
			if (best.Weight <= 0f || best.BoneIndex < 0 || best.BoneIndex >= bones.Count)
				continue;

			foreach (var side in new[] { "l", "r" })
			{
				if (SourceArmBones.Classify(side, names, parents, best.BoneIndex) == SourceArmPart.None)
					continue;
				onArm[i] = true;
				// 自己名字认不出、靠祖先才归到手臂上 = 辅助骨，就是两边刻意不一致的那一族。
				if (SourceArmBones.ClassifySelf(side, names[best.BoneIndex]) == SourceArmPart.None)
					inherited[i] = true;
				break;
			}
		}

		return (inherited, onArm);
	}
}
