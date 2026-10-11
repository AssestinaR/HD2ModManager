using HD2SourceModel;

namespace HD2SourceMigration.Tests;

/// <summary>
/// 真实数据回归：把 A2 的实测数字钉在 C# 这一侧。
/// <para>
/// 需要三样东西，缺任何一样就静默跳过：求生之路 2 的样本 VPK、从游戏里提取的 avatar Unit TocData、
/// 以及 Bones 资源的 hash→名字表。三者都可用环境变量覆盖。
/// </para>
/// <para>
/// 合成样本验证"求解器对不对"，这个回归验证"对应表与单位换算对不对"——后者只有真实数据能证伪。
/// </para>
/// </summary>
public sealed class RealRigMapTests
{
	private const string DefaultVpkPath =
		@"D:\SteamLibrary\steamapps\common\Left 4 Dead 2\left4dead2\addons\Lunalice_Zoey.vpk";

	private const string DefaultAvatarTocPath =
		@"E:\Data\source\repos\WpfApp1\tmp\rig-lab\avatar\unit_0x4d1c334d294dfa97.toc";

	private const string DefaultBoneHashPath =
		@"E:\Data\source\repos\WpfApp1\tmp\pb-ref\HD2PhysBone-ReferenceSource-main\kit\tools\bonehash.txt";

	private const string ModelStem = "survivor_teenangst";

	private static string? Resolve(string variable, string fallback)
	{
		var configured = Environment.GetEnvironmentVariable(variable);
		if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
			return configured;

		return File.Exists(fallback) ? fallback : null;
	}

	private static SourceModelDocument? ReadSourceModel()
	{
		var vpkPath = Resolve("HD2_LEGACY_TEST_VPK", DefaultVpkPath);
		if (vpkPath is null)
			return null;

		using var archive = VpkArchive.Open(vpkPath);
		var files = SourceModelLocator.Resolve(archive, ModelStem);
		return SourceModelReader.Read(archive.Read(files.Mdl));
	}

	private static Hd2Rig? ReadHd2Rig()
	{
		var tocPath = Resolve("HD2_AVATAR_TOC", DefaultAvatarTocPath);
		var hashPath = Resolve("HD2_BONEHASH", DefaultBoneHashPath);
		if (tocPath is null || hashPath is null)
			return null;

		var transforms = UnitMeshReader.ReadTransformInfoFromUnitToc(File.ReadAllBytes(tocPath));
		return Hd2RigReader.FromTransformInfo(transforms, ReadBoneNames(hashPath));
	}

	private static UnitBoneNames ReadBoneNames(string path)
	{
		var hashes = new List<uint>();
		var names = new List<string>();
		foreach (var line in File.ReadLines(path))
		{
			var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length >= 2 && uint.TryParse(parts[0], out var hash))
			{
				hashes.Add(hash);
				names.Add(parts[1]);
			}
		}

		return new UnitBoneNames(hashes, names);
	}

	[Fact]
	public void RealData_Zoey_MapsTheWholeCorrespondenceTable()
	{
		var source = ReadSourceModel();
		var hd2 = ReadHd2Rig();
		if (source is null || hd2 is null)
			return;

		var map = SourceToHd2RigMap.Build(source, hd2);

		// A2 的实测结果：50/50 命中、0 缺失。对应表对五个幸存者模型都成立，这里只钉住 Zoey。
		Assert.Empty(map.Missing);
		Assert.Equal(50, map.Entries.Count);
	}

	[Fact]
	public void RealData_Zoey_LandsEveryMappedOriginOnItsHd2Node()
	{
		var source = ReadSourceModel();
		var hd2 = ReadHd2Rig();
		if (source is null || hd2 is null)
			return;

		var map = SourceToHd2RigMap.Build(source, hd2);

		// 源侧是 float32、HD2 侧也是 float32，所以 1e-5 m（0.01 mm）是宽松但仍有意义的界。
		var worst = map.Entries
			.Select(entry => Vec3.Distance(entry.Transform.TransformPoint(entry.SourceOriginMetres), entry.Hd2Origin))
			.Max();

		Assert.True(worst < 1e-5, $"worst origin error was {worst} m");
	}

	[Fact]
	public void RealData_Zoey_ReportsTheStretchThatCausesTheVisibleDistortion()
	{
		var source = ReadSourceModel();
		var hd2 = ReadHd2Rig();
		if (source is null || hd2 is null)
			return;

		var map = SourceToHd2RigMap.Build(source, hd2);
		var bySegment = map.Stretches.ToDictionary(item => item.Segment, StringComparer.Ordinal);

		Assert.Equal(7, bySegment.Count);

		// 这些数字就是"比例失调"的来源，也是后续五项修正的靶子：
		// 躯干偏短（0.93/0.87）、颈到头长 2.6 倍（长脖子）、手臂略长（1.04）、腿偏短（0.91）。
		AssertStretch(bySegment, "Pelvis->Spine4", 0.9258);
		AssertStretch(bySegment, "Spine4->Neck1", 0.8678);
		AssertStretch(bySegment, "Neck1->Head1", 2.6154);
		AssertStretch(bySegment, "L_UpperArm->L_Hand", 1.0352);
		AssertStretch(bySegment, "R_UpperArm->R_Hand", 1.0352);
		AssertStretch(bySegment, "L_Thigh->L_Foot", 0.9092);
		AssertStretch(bySegment, "R_Thigh->R_Foot", 0.9092);
	}

	private static void AssertStretch(
		IReadOnlyDictionary<string, RigSegmentStretch> bySegment, string segment, double expected)
	{
		Assert.True(bySegment.TryGetValue(segment, out var actual), $"segment {segment} was not reported");
		Assert.InRange(actual.Stretch, expected - 5e-4, expected + 5e-4);
	}
}
