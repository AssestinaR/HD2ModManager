using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2SourceModel;

namespace HD2SourceMigration;

/// <summary>
/// 把 HD2 的 TransformInfo 表读成 <see cref="Hd2Rig"/>。
/// <para>
/// 这里唯一容易搞错、而且<b>搞错了不会报错</b>的东西是存储约定：
/// HD2 的 16 个 float 是<b>行向量</b>约定（平移落在平坦下标 12/13/14），
/// 而本项目其余部分统一用列向量约定（平移在第 4 列）。
/// 不转置的后果是每一段骨长都静默变成 0.0000——不是异常，是一个看起来"很整齐"的错答案。
/// </para>
/// </summary>
public static class Hd2RigReader
{
	/// <summary>
	/// 由 TransformInfo 表构造骨架。<paramref name="names"/> 可为 <c>null</c>——那只是拿不到节点名，
	/// 变换与父子关系仍然完整（这正是"名字读不到也要能用"的底线）。
	/// </summary>
	public static Hd2Rig FromTransformInfo(UnitTransformInfo transforms, UnitBoneNames? names = null)
	{
		ArgumentNullException.ThrowIfNull(transforms);

		var nameByHash = BuildNameLookup(names);
		var bones = new Hd2RigBone[transforms.Matrices.Count];
		for (var i = 0; i < bones.Length; i++)
		{
			var hash = i < transforms.NameHashes.Count ? transforms.NameHashes[i] : 0u;
			var parent = i < transforms.Entries.Count ? transforms.Entries[i].ParentIndex : 0;
			bones[i] = new Hd2RigBone(
				i,
				nameByHash.TryGetValue(hash, out var name) ? name : null,
				parent,
				FromHd2RowVector(transforms.Matrices[i].Values));
		}

		return new Hd2Rig(bones);
	}

	/// <summary>
	/// 把 HD2 的行向量矩阵（平移在平坦下标 12/13/14）读成列向量约定的 <see cref="Mat4"/>。
	/// <para>
	/// 行向量矩阵的"按行存储"恰好是列向量矩阵的转置，所以先按行读进来、再转置即可。
	/// 这个方法刻意留在本工程而不是 <see cref="Mat4"/> 里：<c>HD2SourceModel</c> 必须保持
	/// "不认识 HD2"，把 HD2 的存储细节写进那一层就是边界侵蚀。
	/// </para>
	/// </summary>
	public static Mat4 FromHd2RowVector(IReadOnlyList<float> values)
	{
		ArgumentNullException.ThrowIfNull(values);
		if (values.Count < 16)
			throw new ArgumentException("An HD2 transform needs 16 floats.", nameof(values));

		return new Mat4(
			values[0], values[1], values[2], values[3],
			values[4], values[5], values[6], values[7],
			values[8], values[9], values[10], values[11],
			values[12], values[13], values[14], values[15]).Transposed();
	}

	private static IReadOnlyDictionary<uint, string> BuildNameLookup(UnitBoneNames? names)
	{
		if (names is null || !names.HasValue)
			return EmptyNameLookup;

		var count = Math.Min(names.Hashes.Count, names.Names.Count);
		var map = new Dictionary<uint, string>(count);
		for (var i = 0; i < count; i++)
			map.TryAdd(names.Hashes[i], names.Names[i]);

		return map;
	}

	private static readonly IReadOnlyDictionary<uint, string> EmptyNameLookup = new Dictionary<uint, string>();
}
