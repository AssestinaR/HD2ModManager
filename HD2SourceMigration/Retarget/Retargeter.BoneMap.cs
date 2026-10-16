using HD2SourceModel;

namespace HD2SourceMigration.Retarget;

/// <summary>源骨 → HD2 骨的绑定结果。</summary>
/// <param name="Resolved">源骨下标 → HD2 骨下标。<b>没有键</b>表示无对端（等价于 Python 的 <c>None</c>）。</param>
/// <param name="Hops">回退时沿源链上溯了几跳；直接命中为 0。</param>
/// <param name="ByDistance">按距离回退的骨，供日志排序展示。</param>
public sealed record BoneMapResult(
	Dictionary<int, int> Resolved,
	Dictionary<int, int> Hops,
	IReadOnlyList<(string Source, string? Hd2, double Distance)> ByDistance);

public sealed partial class Retargeter
{
	/// <summary>
	/// 源骨下标 → HD2 骨下标。直接命中之外还要给没对端的骨一个回退答案。
	/// <para>
	/// <c>region</c>（生产配置）是 <c>distance</c> 的加强版：把候选限制在"与该骨最近的<b>已映射祖先</b>
	/// 处在<b>同一肢体</b>"之内。肢体是照着 HD2 自己的层级读出来的——一根骨在往上走的路上经过
	/// <c>l_clavicle</c>/<c>r_clavicle</c>/<c>l_thigh</c>/<c>r_thigh</c>/<c>neck</c> 中的哪一个，就属于哪一条肢体。
	/// </para>
	/// <para>
	/// 这是保住源骨架结构的关键：<c>source_extra_73/76/85</c> 是挂在肩膀旁边的头发与发带骨，
	/// 它们的祖先是 <c>Bip01_Head1</c>，所以只能落到头上——裸的 <c>distance</c> 把它们交给了
	/// <c>l_shoulder</c>/<c>r_shoulder</c>，随后 A-pose 烘焙就把头发连着胳膊一起甩了下去。
	/// </para>
	/// <para>
	/// 单纯的"祖先子树"规则在这里<b>不成立</b>：HD2 把 <c>hips</c> 和 <c>spine1</c> 作为<b>兄弟</b>挂在
	/// <c>boss</c> 下，<c>hips</c> 的子树只有腿，那会把裙子甩到大腿上。是肢体判据（而不是子树判据）
	/// 让骨盆留在了躯干里。
	/// </para>
	/// <para>
	/// <b>链式规则</b>：挂在同一个已映射祖先下的一<b>串</b>未映射骨是<b>一个</b>整体，不是一组独立骨——
	/// 串里每一根都必须落到同一根 HD2 骨上，否则它下面的关节一动，这块东西就撕成两半。由串的<b>根</b>
	/// （自己的父级已映射的那一根）决定，因为它才是真正接触骨架的那一根，所以它最近的 HD2 关节
	/// <b>就是</b>附着点。Lilium 有两条 1.8 m 的缎带/袖子链，逐骨解会让比例 <c>d_best/d_ancestor</c>
	/// 平滑地从 0.25 爬到 0.96，任何阈值都会从中间切断它（实测 1430 个缎带顶点骑在小指上、
	/// 离骨最远 1.9 m，外加肘部 0.39 m 的撕裂）。
	/// </para>
	/// </summary>
	private BoneMapResult ResolveBoneMap()
	{
		var resolved = new Dictionary<int, int>();
		var hops = new Dictionary<int, int>();
		var byDistance = new List<(string Source, string? Hd2, double Distance)>();

		if (_options.Fallback is RetargetFallback.Parent)
		{
			foreach (var bone in _model.Bones)
			{
				var index = bone.Index;
				if (_sourceToHd2.TryGetValue(index, out var direct))
				{
					resolved[index] = direct;
					hops[index] = 0;
					continue;
				}

				var (ancestor, count) = WalkToMappedAncestor(index);
				hops[index] = count;
				if (ancestor >= 0 && _sourceToHd2.TryGetValue(ancestor, out var mapped))
					resolved[index] = mapped;
			}

			return new BoneMapResult(resolved, hops, byDistance);
		}

		var candidates = new List<(int Hd2, Vec3 Joint)>();
		foreach (var bone in _model.Bones)
		{
			// ⚠️ 候选点用的是**对齐后的源关节**（`ajoint`），不是 HD2 的原生关节。
			// HD2 的 rest 与源关节相差最多 10 cm（手、肩、肘都是），用错会让最近邻判定整体偏移，
			// 表现成"围裙/头发/武器骨绑到了旁边那根骨上"——不报错，只是那些顶点跟着别的骨动。
			if (_sourceToHd2.TryGetValue(bone.Index, out var hd2))
				candidates.Add((hd2, AlignedSourceJoint(bone.Index)));
		}

		var subtreeCache = new Dictionary<int, HashSet<int>>();
		var limbCache = new Dictionary<int, string>();
		var chain = new HashSet<int>();

		foreach (var bone in _model.Bones)
		{
			var index = bone.Index;
			if (_sourceToHd2.TryGetValue(index, out var direct))
			{
				resolved[index] = direct;
				hops[index] = 0;
				continue;
			}

			var (ancestor, count) = WalkToMappedAncestor(index);
			hops[index] = _options.Fallback == RetargetFallback.Distance ? 0 : count;

			// 串<b>内部</b>的骨：答案由串根决定，在下面统一传播。
			if (_sourceParents[index] >= 0 && !_sourceToHd2.ContainsKey(_sourceParents[index]))
			{
				chain.Add(index);
				continue;
			}

			var pool = candidates;
			if (_options.Fallback == RetargetFallback.Region
				&& ancestor >= 0
				&& _sourceToHd2.TryGetValue(ancestor, out var root))
			{
				var allowed = Subtree(root);
				var limb = Limb(root);
				pool = candidates.Where(c => allowed.Contains(c.Hd2) && Limb(c.Hd2) == limb).ToList();
				if (pool.Count == 0)
					pool = candidates;
			}

			var point = AlignedSourceJoint(index);
			var best = -1;
			var bestDistance = double.MaxValue;
			foreach (var (hd2, joint) in pool)
			{
				var distance = (point - joint).Length;
				if (distance >= bestDistance)
					continue;
				bestDistance = distance;
				best = hd2;
			}

			if (best >= 0)
			{
				resolved[index] = best;
				byDistance.Add((_sourceNames[index], _nameByIndex[best], bestDistance));
			}
		}

		// 从串根往下传播。用扫而不是递归，答案就不依赖 `src` 恰好是父先子后排列。
		for (var pass = 0; pass <= _model.Bones.Count; pass++)
		{
			var left = chain.Where(index => !resolved.ContainsKey(index)).ToArray();
			if (left.Length == 0)
				break;
			foreach (var index in left)
			{
				var parent = _sourceParents[index];
				if (parent >= 0 && resolved.TryGetValue(parent, out var answer))
					resolved[index] = answer;
			}
		}

		// 父级始终没解出来的串（环 / 截断文件）退回朴素的祖先答案，而不是保持未绑定。
		foreach (var index in chain)
		{
			if (resolved.ContainsKey(index))
				continue;
			var (ancestor, _) = WalkToMappedAncestor(index);
			if (ancestor >= 0 && _sourceToHd2.TryGetValue(ancestor, out var mapped))
				resolved[index] = mapped;
		}

		return new BoneMapResult(resolved, hops, byDistance);

		HashSet<int> Subtree(int root)
		{
			if (subtreeCache.TryGetValue(root, out var cached))
				return cached;
			var seen = new HashSet<int>();
			var stack = new Stack<int>();
			stack.Push(root);
			while (stack.Count > 0)
			{
				var current = stack.Pop();
				if (!seen.Add(current))
					continue;
				var children = _children[current];
				if (children is null)
					continue;
				foreach (var child in children)
					stack.Push(child);
			}

			subtreeCache[root] = seen;
			return seen;
		}

		string Limb(int hd2Index)
		{
			if (limbCache.TryGetValue(hd2Index, out var cached))
				return cached;
			var current = hd2Index;
			var result = "torso";
			for (var step = 0; current >= 0 && step < 64; step++)
			{
				var name = _nameByIndex[current];
				if (name is not null && LimbMarks.TryGetValue(name, out var mark))
				{
					result = mark;
					break;
				}

				current = _parents[current];
			}

			limbCache[hd2Index] = result;
			return result;
		}
	}

	private static readonly Dictionary<string, string> LimbMarks = new(StringComparer.Ordinal)
	{
		["l_clavicle"] = "l_arm",
		["r_clavicle"] = "r_arm",
		["l_thigh"] = "l_leg",
		["r_thigh"] = "r_leg",
		["neck"] = "head",
	};

	/// <summary>沿源链上溯到最近的<b>已映射</b>祖先；返回 (祖先下标, 跳数)。找不到返回 (-1, 跳数)。</summary>
	private (int Ancestor, int Hops) WalkToMappedAncestor(int sourceBoneIndex)
	{
		var current = _sourceParents[sourceBoneIndex];
		var count = 1;
		while (current >= 0 && !_sourceToHd2.ContainsKey(current) && count <= 64)
		{
			if (current >= _sourceParents.Length)
				return (-1, count);
			current = _sourceParents[current];
			count++;
		}

		return (current, count);
	}

	/// <summary>源骨对齐后的关节位置（= <c>ajoint</c>）。</summary>
	private Vec3 AlignedSourceJoint(int sourceBoneIndex) =>
		_alignment.Transform(_sourceWorldMetres[sourceBoneIndex].Origin - _sole) * _scale + _translation;
}
