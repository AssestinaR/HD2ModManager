using HD2SourceModel;

namespace HD2SourceMigration.Tests;

/// <summary>合成两侧骨架用的小工具——测试里只关心变换与名字，不关心文件格式。</summary>
internal static class TestRig
{
	/// <summary>HD2 行向量存储：只有平移，落在平坦下标 12/13/14。</summary>
	public static float[] RowVectorAt(double x, double y, double z) =>
	[
		1, 0, 0, 0,
		0, 1, 0, 0,
		0, 0, 1, 0,
		(float)x, (float)y, (float)z, 1,
	];

	/// <summary>列向量存储的纯平移矩阵（平移在第 4 列）。</summary>
	public static Mat4 TranslationAt(double x, double y, double z) => new(
		1, 0, 0, x,
		0, 1, 0, y,
		0, 0, 1, z,
		0, 0, 0, 1);

	public static UnitTransformInfo TransformInfo(IReadOnlyList<float[]> matrices, IReadOnlyList<uint>? hashes = null)
	{
		var count = matrices.Count;
		var entries = new UnitTransformEntry[count];
		for (var i = 0; i < count; i++)
			entries[i] = new UnitTransformEntry(0, (ushort)i);

		return new UnitTransformInfo(
			0,
			0,
			0,
			Array.Empty<UnitLocalTransform>(),
			[.. matrices.Select(matrix => new UnitTransformMatrix(matrix))],
			entries,
			hashes ?? new uint[count]);
	}

	/// <summary>源骨：名字带 <c>ValveBiped.Bip01_</c> 前缀，世界 bind 是给定位置的纯平移（单位英寸）。</summary>
	public static SourceBone SourceBone(int index, string shortName, double x = 0, double y = 0, double z = 0)
	{
		var world = TranslationAt(x, y, z);
		return new SourceBone(
			index,
			SourceToHd2RigMap.SourceBonePrefix + shortName,
			-1,
			world,
			world,
			null);
	}

	/// <summary>名字读不出来的源骨（<c>source_extra_N</c> 的成因）。</summary>
	public static SourceBone UnnamedSourceBone(int index) =>
		new(index, null, -1, Mat4.Identity, Mat4.Identity, null);

	public static SourceModelDocument SourceModel(IReadOnlyList<SourceBone> bones) =>
		new(53, "test.mdl", 0, bones, [], [], []);

	/// <summary>HD2 节点：给定名字与世界 bind 位置（列向量约定）。</summary>
	public static Hd2RigBone Hd2Bone(int index, string name, double x = 0, double y = 0, double z = 0) =>
		new(index, name, index, TranslationAt(x, y, z));

	/// <summary>按对应表造一份"两侧都齐全"的骨架，位置沿 X 轴按序号铺开。</summary>
	public static (SourceModelDocument Source, Hd2Rig Hd2) MatchingRigs()
	{
		var sourceBones = new List<SourceBone>();
		var hd2Bones = new List<Hd2RigBone>();
		for (var i = 0; i < SourceToHd2RigMap.Correspondence.Count; i++)
		{
			var (sourceName, hd2Name) = SourceToHd2RigMap.Correspondence[i];
			sourceBones.Add(SourceBone(i, sourceName, x: i));
			hd2Bones.Add(Hd2Bone(i, hd2Name, x: i));
		}

		return (SourceModel(sourceBones), new Hd2Rig(hd2Bones));
	}
}
