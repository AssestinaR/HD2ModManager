using HD2SourceModel;

namespace HD2SourceMigration.Retarget;

/// <summary>
/// 为 jiggle 骨分配的**合成骨**：它不属于 HD2 原生骨架，是这个 Unit 自己新开的节点。
/// <para>
/// 为什么要它：以前 jiggle 骨在重定向时被回退到最近的 HD2 骨（头发落 <c>head</c>、裙摆落 <c>hips</c>），
/// 顶点权重随之被合并，于是"头发是刚性的"。合成骨让这些顶点保留在<b>自己的骨</b>上，
/// 运行时就能驱动它们。
/// </para>
/// </summary>
/// <param name="Name">节点名 = 源骨显示名（如 <c>source_extra_72</c>）；运行时按这个名字找节点。</param>
/// <param name="NameHash"><see cref="Hd2NameHash.Of"/> 算出的骨名哈希（写进 TransformInfo.NameHashes）。</param>
/// <param name="ParentIndex">父节点下标；可能是另一根合成骨（链套链）。</param>
/// <param name="WorldOrigin">对齐后的源关节位置（HD2 世界空间，米）。局部旋转按契约取单位阵。</param>
/// <param name="AnchorIndex">
/// **顶点烘焙**时该用的原生骨下标（= 插骨之前这根 jiggle 骨会落到的 HD2 骨，例如 <c>head</c>）。
/// <para>
/// 为什么必须有它：<c>SkinAll</c> 按骨下标去 `_deform` 里取形变矩阵，而合成骨没有条目，
/// 查不到就退回 <see cref="Mat4.Identity"/> —— 这些顶点会**完全绕开锚点的姿态**（A-pose、上臂缩放、头部缩放…），
/// 与相邻顶点错开，网格上就是一道折痕。2026-10-31 实测：Lilium 有 24.3% 顶点位移，最大 1.42 m。
/// </para>
/// </param>
public sealed record RetargetExtraBone(
	string Name,
	uint NameHash,
	int ParentIndex,
	Vec3 WorldOrigin,
	int AnchorIndex = -1);

public sealed partial class Retargeter
{
	/// <summary>
	/// 给每根 jiggle 骨分配合成骨，并产出"源骨下标 → 合成节点下标"的**权重目标表**。
	/// <para>
	/// ⚠️ **不改动 <paramref name="resolved"/> 本身**。曾经的实现直接把 jiggle 骨改写成合成骨下标，
	/// 结果几何变了：`SkinAll` 在 `_deform` 里查不到合成骨 → 退回单位矩阵，这些顶点绕开了
	/// A-pose/缩放等全部姿态处理（实测 Lilium 24.3% 顶点位移、最大 1.42 m，网格上是一道折痕）。
	/// 现在 `resolved` 保持"插骨之前"的答案，几何与不插骨时**逐位相同**，
	/// 只有写权重那一层（<c>RetargetedMeshBuilder</c>）按本表把目标换成合成节点。
	/// </para>
	/// </summary>
	private (List<RetargetExtraBone> Extras, Dictionary<int, int> WeightTargets) CollectJiggleBones(
		IReadOnlyDictionary<int, int> resolved)
	{
		if (!_options.IncludeJiggleBones)
			return ([], []);

		var scratch = new Dictionary<int, int>(resolved);
		var extras = PlanJiggleBones(_model.Bones, scratch, _rig.Bones.Count, AlignedSourceJoint);
		var targets = new Dictionary<int, int>();
		foreach (var (sourceIndex, target) in scratch)
		{
			// 只有被改写到"原生骨之后"的那些才是合成骨。
			if (target >= _rig.Bones.Count)
				targets[sourceIndex] = target;
		}

		return (extras, targets);
	}

	/// <summary>
	/// 纯逻辑版：给定源骨表、已解析的映射、合成骨起始下标、以及"源骨下标 → 关节位置"的取法，
	/// 产出合成骨清单并就地改写 <paramref name="resolved"/>。
	/// <para>
	/// 顺序必须<b>父→子</b>：锚点本身也可能是 jiggle 骨（这套资产里实测有 6 条这样的"链套链"）。
	/// 这里用"反复扫到不动为止"做拓扑排序，而不是假设"源骨下标递增"—— 那个假设在别的模型上不一定成立。
	/// </para>
	/// <para>
	/// 合成骨下标从 <paramref name="baseCount"/> 往后排，因此**不会动到任何原生下标**；
	/// 上层只要按顺序把节点追加到 TransformInfo 即可。
	/// </para>
	/// </summary>
	public static List<RetargetExtraBone> PlanJiggleBones(
		IReadOnlyList<SourceBone> bones,
		Dictionary<int, int> resolved,
		int baseCount,
		Func<int, Vec3> jointOf)
	{
		var extras = new List<RetargetExtraBone>();
		var jiggle = bones.Where(bone => bone.Jiggle is not null).OrderBy(bone => bone.Index).ToList();
		if (jiggle.Count == 0)
			return extras;

		var pending = new HashSet<int>(jiggle.Select(bone => bone.Index));
		var assigned = new Dictionary<int, int>();   // 源 jiggle 下标 → 合成骨下标

		var progress = true;
		while (pending.Count > 0 && progress)
		{
			progress = false;
			foreach (var bone in jiggle)
			{
				if (!pending.Contains(bone.Index))
					continue;

				// 父必须先有答案：要么是已映射的原生骨，要么是已经分配好的合成骨。
				var parent = bone.ParentIndex;
				if (parent >= 0 && pending.Contains(parent))
					continue;

				int parentIndex;
				if (parent >= 0 && assigned.TryGetValue(parent, out var chainParent))
					parentIndex = chainParent;
				else if (parent >= 0 && resolved.TryGetValue(parent, out var nativeParent))
					parentIndex = nativeParent;
				else
					continue;   // 父既没映射也不是 jiggle：这一轮先跳过

				var index = baseCount + extras.Count;
				// **先取再改**：此刻 resolved[bone.Index] 还是"插骨之前它会落到的原生骨"（如 head），
				// 那就是这类顶点在烘焙时该用的形变来源（见 RetargetExtraBone.AnchorIndex 的说明）。
				var anchorIndex = resolved.TryGetValue(bone.Index, out var fallback) ? fallback : -1;
				extras.Add(new RetargetExtraBone(
					bone.DisplayName,
					Hd2NameHash.Of(bone.DisplayName),
					parentIndex,
					jointOf(bone.Index),
					anchorIndex));
				assigned[bone.Index] = index;
				resolved[bone.Index] = index;
				pending.Remove(bone.Index);
				progress = true;
			}
		}

		return extras;
	}
}
