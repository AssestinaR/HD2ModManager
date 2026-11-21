using HD2SourceModel;

namespace HD2SourceMigration.Retarget;

public sealed partial class Retargeter
{
	/// <summary>在膝骨高度测腿的中心纵剖面。只在腿截面偏后的情况下向前修正。</summary>
	private IReadOnlyList<uint> AlignKneeDepth(
		Vec3[] points,
		IReadOnlyDictionary<int, int> jiggleTargets,
		List<RetargetExtraBone> extraBones)
	{
		var adjusted = new List<uint>(2);
		var shifts = new double[points.Length];
		foreach (var side in new[] { "l", "r" })
		{
			if (!_byName.TryGetValue($"{side}_thigh", out var thigh)
				|| !_byName.TryGetValue($"{side}_knee", out var calf))
				continue;

			var center = calf.WorldBind.Origin;
			var hip = thigh.WorldBind.Origin.Z;
			if (hip <= center.Z)
				continue;

			// 截面判定继续使用强绑定的大腿和小腿，避免把宽松衣料量进腿的厚度。
			var sectionInfluence = new double[points.Length];
			for (var i = 0; i < sectionInfluence.Length; i++)
			{
				for (var slot = 0; slot < _weights[i].Length; slot++)
				{
					var source = _boneIndices[i][slot];
					if (_resolved.TryGetValue(source, out var joint) && (joint == thigh.Index || joint == calf.Index)
						&& !jiggleTargets.ContainsKey(source))
						sectionInfluence[i] += _weights[i][slot];
				}
			}

			var back = double.PositiveInfinity;
			var front = double.NegativeInfinity;
			foreach (var triangle in _geometry.Triangles)
			{
				if (sectionInfluence[triangle.A] < 0.85 || sectionInfluence[triangle.B] < 0.85 || sectionInfluence[triangle.C] < 0.85)
					continue;
				if (KneeSectionDepth(points[triangle.A] - center, points[triangle.B] - center, points[triangle.C] - center)
					is not { } depth || depth < -0.17 || depth > 0.13)
					continue;
				back = Math.Min(back, depth);
				front = Math.Max(front, depth);
			}

			var correction = KneeDepthCorrection(back, front);
			if (correction <= 0)
				continue;

			var applied = false;
			for (var i = 0; i < points.Length; i++)
			{
				if (points[i].Z >= hip)
					continue;
				var move = KneeDepthShift(correction, points[i].Z, hip, center.Z, LegInfluence(i, side).Leg);
				if (move <= 0)
					continue;
				shifts[i] += move;
				applied = true;
			}

			if (!applied)
				continue;

			adjusted.Add(Hd2NameHash.Of($"{side}_knee"));
			for (var e = 0; e < extraBones.Count; e++)
			{
				var extra = extraBones[e];
				if (extra.AnchorIndex < 0 || _nameByIndex[extra.AnchorIndex] is not { } name || !IsLegBoneOf(name, side))
					continue;

				// 腿部重拟合已更新原点的高度；与网格一样在整体抬升后的高度求位移。
				var height = Post(extra.WorldOrigin).Z + _options.GroundLift;
				var move = KneeDepthShift(correction, height, hip, center.Z, 1);
				extraBones[e] = extra with { WorldOrigin = extra.WorldOrigin + new Vec3(0, move, 0) };
			}
		}

		for (var i = 0; i < points.Length; i++)
		{
			if (shifts[i] > 0)
				points[i] += new Vec3(0, shifts[i], 0);
		}

		return adjusted;
	}

	/// <summary>与经过枢轴的 Y 向直线求交，三角形坐标以膝骨为原点。</summary>
	internal static double? KneeSectionDepth(Vec3 a, Vec3 b, Vec3 c)
	{
		var ab = b - a;
		var ac = c - a;
		var det = (ab.X * ac.Z) - (ab.Z * ac.X);
		if (Math.Abs(det) < 1e-12)
			return null;

		var u = ((a.Z * ac.X) - (a.X * ac.Z)) / det;
		var v = ((a.X * ab.Z) - (a.Z * ab.X)) / det;
		const double tolerance = 1e-8;
		return u >= -tolerance && v >= -tolerance && u + v <= 1 + tolerance
			? a.Y + (ab.Y * u) + (ac.Y * v)
			: null;
	}

	internal static double KneeDepthCorrection(double back, double front)
	{
		// 太薄或过宽的截面可能只打到一层衣料，保留绑定姿势。上限避免为了对齐转轴而改写腿形。
		var thickness = front - back;
		return thickness < 0.035 || thickness > 0.22
			? 0
			: Math.Clamp(-0.015 - ((back + front) * 0.5), 0, 0.035);
	}

	// 按截面高度沿髋到膝插值，各截面平移量一致；膝以下保持常数，鞋底不再折回。
	internal static double KneeDepthShift(double correction, double z, double hip, double knee, double influence)
	{
		var t = Math.Clamp((hip - z) / (hip - knee), 0, 1);
		return correction * (t * t * (3 - (2 * t))) * influence;
	}
}
