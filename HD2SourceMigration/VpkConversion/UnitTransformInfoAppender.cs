using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2SourceMigration.Retarget;

namespace HD2SourceMigration.VpkConversion;

/// <summary>
/// 把 jiggle 骨的**合成节点**追加到 Unit 的 TransformInfo 末尾。
/// <para>
/// 为什么必须在建网格**之前**追加：<c>RetargetedMeshBuilder</c> 是靠
/// <c>avatarTransformInfo.NameHashes[hd2Index]</c> 把"目标骨下标"换成"骨名哈希"的。
/// 合成骨下标不在表里时那条路径会拿到越界下标，网格只好退回报错。
/// </para>
/// <para>
/// 约定全部来自实测（工作补丁里 120 个节点逐个核对过）：
/// <list type="bullet">
/// <item><c>LocalTransform</c>：旋转单位阵、位置 = 世界原点、缩放 1（= 64 字节，与文件布局一致）。</item>
/// <item><c>Matrix</c>：**行向量**存储，平移在平坦下标 12/13/14（与 <c>Hd2RigReader.FromHd2RowVector</c> 的入口约定一致）。</item>
/// <item><c>Entry.Increment</c> 恒为 <b>1</b>（工作补丁里每个节点都是 1，包括以前手工插进去的 6 根）。</item>
/// <item>父下标：调用方保证"父的下标小于自身下标"（原生骨 &lt; 合成骨，且链按父→子顺序追加）。</item>
/// </list>
/// </para>
/// </summary>
public static class UnitTransformInfoAppender
{
	/// <summary>单位旋转（3×3，按行铺开）。</summary>
	private static readonly float[] IdentityRotation = [1, 0, 0, 0, 1, 0, 0, 0, 1];

	/// <summary>追加合成节点；没有要追加的节点时原样返回同一个对象（避免无意义复制）。</summary>
	/// <para>
	/// **null 进 null 出**：纯材质 patch 没有 Unit 计划，装配器允许传 <c>null</c>；
	/// 这条路径以前能跑通，所以这里必须保持"null 就是 null"，不能抛。
	/// （2026-10-31 踩过：这里无条件 <c>ThrowIfNull</c> 会让所有材质 VPK 转换挂在 <c>Parameter 'info'</c>。）
	/// </para>
	public static UnitTransformInfo? Append(UnitTransformInfo? info, IReadOnlyList<RetargetExtraBone>? extras)
	{
		if (info is null)
			return null;
		if (extras is null || extras.Count == 0)
			return info;

		var locals = new List<UnitLocalTransform>(info.LocalTransforms);
		var matrices = new List<UnitTransformMatrix>(info.Matrices);
		var entries = new List<UnitTransformEntry>(info.Entries);
		var hashes = new List<uint>(info.NameHashes);

		foreach (var extra in extras)
		{
			// 父下标必须已存在：原生骨在表里，合成骨按父→子顺序追加，所以这里一定成立。
			if (extra.ParentIndex < 0 || extra.ParentIndex >= hashes.Count)
			{
				throw new InvalidOperationException(
					$"合成骨 {extra.Name} 的父下标 {extra.ParentIndex} 越界（当前 {hashes.Count} 个节点）。");
			}

			// Vec3 是 double，Unit 的字段是 float —— 显式转，别让隐式转换规则在不同编译单元里翻脸。
			var x = (float)extra.WorldOrigin.X;
			var y = (float)extra.WorldOrigin.Y;
			var z = (float)extra.WorldOrigin.Z;

			// ⚠️ 两张表必须**互相自洽**：`Matrices` 是世界 bind，`LocalTransforms` 是**父空间**。
			// 曾经把世界坐标直接塞进 `LocalTransforms.Position`，于是 FK 出来的骨位等于
			// "父骨再平移一次一个父骨距离"，网格被拉长 1~3 米（用户实机所见：所有带物理骨的部位被扯开）。
			//
			// 行向量约定：world = local · parent。
			//   · 世界旋转**沿用父骨**（局部旋转取单位阵）——这正是运行时假定的
			//     "bind local 旋转 = 单位阵"契约；
			//   · 局部平移 = (P − t_parent) · R_parentᵀ，即 L = W · parentWorld⁻¹ 的平移部分。
			var parent = matrices[extra.ParentIndex].Values;
			var world = new float[16];
			for (var k = 0; k < 16; k++)
				world[k] = parent[k];
			world[12] = x;
			world[13] = y;
			world[14] = z;
			world[15] = 1f;

			var dx = x - parent[12];
			var dy = y - parent[13];
			var dz = z - parent[14];
			// t_L = d · P_rotᵀ（行向量约定下就是"按行取分量和"）：
			// 校验方式是回代 —— (t_L · P_rot) + t_P 必须等于世界位置。
			var localX = dx * parent[0] + dy * parent[1] + dz * parent[2];
			var localY = dx * parent[4] + dy * parent[5] + dz * parent[6];
			var localZ = dx * parent[8] + dy * parent[9] + dz * parent[10];

			locals.Add(new UnitLocalTransform(
				IdentityRotation,
				[localX, localY, localZ],
				[1f, 1f, 1f],
				0f));
			matrices.Add(new UnitTransformMatrix(world));
			entries.Add(new UnitTransformEntry(1, checked((ushort)extra.ParentIndex)));
			hashes.Add(extra.NameHash);
		}

		return new UnitTransformInfo(
			info.Reserved0,
			info.Reserved1,
			info.Reserved2,
			locals,
			matrices,
			entries,
			hashes);
	}

	/// <summary>
	/// 自检：从 <c>LocalTransforms</c> 沿父子链做一遍 FK，结果必须等于 <c>Matrices</c> 里的世界矩阵。
	/// <para>
	/// 为什么值得写：这两张表一旦不一致，游戏不会报错，只会让挂在那些骨上的网格被拉长
	/// （2026-10-31 实测：把世界坐标塞进 local 位置，所有物理骨部位被扯开 1~3 米）。
	/// 转换时就把这条不变量钉住，比进游戏看快得多。
	/// </para>
	/// </summary>
	public static void AssertLocalAndWorldAgree(UnitTransformInfo info, int fromIndex = 0, float tolerance = 1e-3f)
	{
		ArgumentNullException.ThrowIfNull(info);
		var count = info.NameHashes.Count;
		var world = new float[count][];
		for (var i = 0; i < count; i++)
		{
			var local = info.LocalTransforms[i];
			var parent = info.Entries[i].ParentIndex;
			var parentWorld = i == 0 || parent == i ? null : world[parent];
			var composed = Compose(local, parentWorld);
			world[i] = composed;

			// 只对**追加进来的那一段**做判定：原生节点来自游戏本体，
			// 万一它们用了别的存储细节（非均匀缩放等），不该让我们这里误报。
			if (i < fromIndex)
				continue;

			var stored = info.Matrices[i].Values;
			// ⚠️ 必须比**全部 16 个**：平移在 12/13/14，只比前 12 个正好漏掉平移，
			// 于是"世界坐标塞进 local"这种错会被自检放过去（写这条时踩过一次）。
			for (var k = 0; k < 16; k++)
			{
				if (Math.Abs(composed[k] - stored[k]) > tolerance)
				{
					throw new InvalidOperationException(
						$"节点 {i} 的 locals 与 world 不自洽：下标 {k} 算出 {composed[k]:F4}，表里是 {stored[k]:F4}。");
				}
			}
		}
	}

	/// <summary>行向量约定下把局部变换接到父世界后面：world = local · parent。</summary>
	private static float[] Compose(UnitLocalTransform local, IReadOnlyList<float>? parentWorld)
	{
		var result = new float[16];
		for (var row = 0; row < 3; row++)
		{
			for (var column = 0; column < 3; column++)
			{
				var value = 0f;
				for (var k = 0; k < 3; k++)
					value += local.Rotation[row * 3 + k] * local.Scale[k] * (parentWorld is null ? (k == column ? 1f : 0f) : parentWorld[k * 4 + column]);
				result[row * 4 + column] = value;
			}

			var translation = 0f;
			for (var k = 0; k < 3; k++)
				translation += local.Position[k] * (parentWorld is null ? (k == row ? 1f : 0f) : parentWorld[k * 4 + row]);
			var parentTranslation = parentWorld is null ? 0f : parentWorld[12 + row];
			result[12 + row] = translation + parentTranslation;
		}

		result[3] = result[7] = result[11] = 0f;
		result[15] = 1f;
		return result;
	}
}
