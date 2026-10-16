using HD2SourceModel;

namespace HD2SourceMigration.Retarget;

public sealed partial class Retargeter
{
	/// <summary>
	/// 按<b>主骨</b>把一个顶点分到 (上臂 / 远端 / 手臂) 三类。
	/// <para>
	/// 归属由顶点的<b>主骨</b>（三组权重里最大的那个）决定，<b>绝不</b>用"任意权重大于零"。
	/// 一个肩部顶点可以带 0.9994 的 UpperArm 和 0.0006 的 Forearm，把这点痕迹算成"远端"会强行
	/// 让上臂<b>根部</b>的 <c>f = 1</c>，位移在一条边上跳 3.7 cm。用主骨，每个顶点就恰好属于
	/// 一侧、一组。
	/// </para>
	/// </summary>
	private (bool[] Upper, bool[] Distal, bool[] Arm) ArmVertexPartition(string side)
	{
		var prefix = side.ToUpperInvariant() + "_";
		var count = _sourcePoints.Length;
		var upper = new bool[count];
		var distal = new bool[count];
		var arm = new bool[count];
		for (var k = 0; k < count; k++)
		{
			if (!TryPrimarySourceName(k, out var name))
				continue;
			if (!name.StartsWith(prefix, StringComparison.Ordinal))
				continue;
			var tail = name[prefix.Length..];
			if (tail.StartsWith("UpperArm", StringComparison.Ordinal))
			{
				upper[k] = true;
				arm[k] = true;
			}
			else if (tail.StartsWith("Forearm", StringComparison.Ordinal)
				|| tail.StartsWith("Hand", StringComparison.Ordinal)
				|| tail.StartsWith("Finger", StringComparison.Ordinal))
			{
				distal[k] = true;
				arm[k] = true;
			}
			else if (tail.StartsWith("Clavicle", StringComparison.Ordinal)
				|| tail.StartsWith("Shoulder", StringComparison.Ordinal))
			{
				arm[k] = true;
			}
		}

		return (upper, distal, arm);
	}

	/// <summary>
	/// 主骨是<b>手</b>或<b>手指</b>的顶点。
	/// <para>
	/// 这些顶点在<b>拓扑上</b>位于腕的远端，而不是按半径。这个区别就是全部要点：手指比小臂长度补偿的
	/// 圆管长（实测三个模型上拇指在腕后 0.0418–0.1437 m），所以径向闸会从远端指节中间切过去——
	/// 里面那半跟着补偿平移、外面那半不动，拇指尖就被剪了。那次剪切就是"拇指反曲"：
	/// MourningVeil/Lunalice 上远端指节被弯了 12.8–13.2°，而 Lilium（补偿只有 −1.0 mm）留在 1.2°。
	/// </para>
	/// </summary>
	private bool[] HandFingerMask(string side)
	{
		var prefix = side.ToUpperInvariant() + "_";
		var count = _sourcePoints.Length;
		var result = new bool[count];
		for (var k = 0; k < count; k++)
		{
			if (!TryPrimarySourceName(k, out var name))
				continue;
			if (!name.StartsWith(prefix, StringComparison.Ordinal))
				continue;
			var tail = name[prefix.Length..];
			if (tail.StartsWith("Hand", StringComparison.Ordinal)
				|| tail.StartsWith("Finger", StringComparison.Ordinal))
				result[k] = true;
		}

		return result;
	}

	/// <summary>
	/// 3b 的位移斜坡 <c>f</c>：
	/// <code>
	/// s = dot(v - SH, u)         u = (EL - SH)/|EL - SH|,  L = |EL - SH|
	/// f = clamp(s / L, 0, 1)     线性斜坡 → 均匀轴向拉伸
	/// f[distal] = 1              小臂/手/手指：刚性平移
	/// f[关节靠躯干一侧] = 0       只有肩关节<b>内侧</b>的东西不动
	/// </code>
	/// 最后一行刻意是"在关节内侧"，而不是"分区没认领的一切"：<c>source_extra_78</c> 是挂在<b>手</b>上的
	/// 辅助骨，它的顶点在手臂上但主骨分区叫不出名字。把它们钉住会让邻居整体移动一个补偿量而它们不动，
	/// 外壳直接撕开（实测 5 cm 跳变）。
	/// </summary>
	private (double[] Field, bool[] Upper, bool[] Distal, bool[] Arm) ArmStretchField(
		IReadOnlyList<Vec3> points, string side, Vec3 shoulder, Vec3 elbow)
	{
		var direction = elbow - shoulder;
		var length = direction.Length;
		var (upper, distal, arm) = ArmVertexPartition(side);
		var field = new double[points.Count];
		if (length < 1e-9)
			return (field, upper, distal, arm);

		var axis = direction / length;
		for (var i = 0; i < points.Count; i++)
		{
			var along = Vec3.Dot(points[i] - shoulder, axis);
			var value = RetargetMath.Clamp(along / length, 0, 1);
			if (distal[i])
				value = 1.0;
			else if (!arm[i] && along <= 0.0)
				value = 0.0;
			field[i] = value;
		}

		return (field, upper, distal, arm);
	}

	/// <summary>顶点的主骨名（权重最大者），剥离 <c>ValveBiped.Bip01_</c> 前缀。权重全为零或下标越界时返回 false。</summary>
	private bool TryPrimarySourceName(int vertexIndex, out string name)
	{
		var weights = _weights[vertexIndex];
		var bones = _boneIndices[vertexIndex];
		var best = weights[0] >= weights[1] && weights[0] >= weights[2] ? 0 : weights[1] >= weights[2] ? 1 : 2;
		if (weights[best] <= 0.0)
		{
			name = string.Empty;
			return false;
		}

		var index = bones[best];
		if (index < 0 || index >= _sourceNames.Length)
		{
			name = string.Empty;
			return false;
		}

		name = _sourceNames[index];
		if (name.StartsWith(RetargetConstants.SourceBonePrefix, StringComparison.Ordinal))
			name = name[RetargetConstants.SourceBonePrefix.Length..];
		return true;
	}

	/// <summary>点到线段的距离。</summary>
	private static double PointSegmentDistance(Vec3 point, Vec3 a, Vec3 b)
	{
		var direction = b - a;
		var denominator = Vec3.Dot(direction, direction);
		var t = RetargetMath.Clamp(Vec3.Dot(point - a, direction) / Math.Max(denominator, 1e-15), 0, 1);
		return (point - (a + (direction * t))).Length;
	}
}
