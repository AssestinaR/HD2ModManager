using HD2SourceModel;
using HD2SourceMigration.Retarget;

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
				var (worst, worstIndex, over) = Compare(reference[key], actual);
				report.Add($"{sample.Tag}/{key}: 最差 {worst * 1000:F9} mm @#{worstIndex}，超差 {over}/{actual.Count}");
				failed |= worst >= ToleranceMetres;
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

			var reference = NpzFile.Read(stagesPath);
			foreach (var (key, actual) in captured.OrderBy(pair => pair.Key, StringComparer.Ordinal))
			{
				if (!reference.TryGetValue(key, out var array))
				{
					report.Add($"{sample.Tag}/{key}: Python 侧没有这个阶段");
					failed = true;
					continue;
				}

				var (worst, worstIndex, over) = Compare(array, actual);
				report.Add($"{sample.Tag}/{key}: 最差 {worst * 1000:F9} mm @#{worstIndex}，超差 {over}/{actual.Length}");
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

	private static (double Worst, int WorstIndex, int Over) Compare(NpyArray expected, IReadOnlyList<Vec3> actual)
	{
		var matrix = expected.AsDoubleMatrix();
		Assert.Equal(matrix.GetLength(0), actual.Count);
		var worst = 0.0;
		var worstIndex = -1;
		var over = 0;
		for (var i = 0; i < actual.Count; i++)
		{
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

		return (worst, worstIndex, over);
	}
}
