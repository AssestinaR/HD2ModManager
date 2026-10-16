using HD2ModAdaptation.PatchReconstruction;
using HD2SourceModel;
using HD2SourceMigration.VpkConversion;

namespace HD2SourceMigration.Tests;

/// <summary>
/// 小臂/手腕权重重分配的两条前提。
/// <para>
/// <b>为什么必须测这两条。</b>`ForearmWeightReweight.Apply` 找不到骨头时会**静默跳过**
/// （这是有意的：不含手臂的 Unit 不该被改动）。但代价是"哈希写错了"也会静默跳过 ——
/// 实机上表现为"完全没变化"，而那种失败最难查。
/// </para>
/// </summary>
public sealed class ForearmReweightTests
{
	private static UnitTransformInfo? LoadAvatar()
	{
		var path = Environment.GetEnvironmentVariable("HD2_AVATAR_TOC");
		if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
			path = @"E:\Data\source\repos\WpfApp1\tmp\rig-lab\avatar\unit_0x4d1c334d294dfa97.toc";
		if (!File.Exists(path))
			return null;
		return UnitMeshReader.ReadTransformInfoFromUnitToc(File.ReadAllBytes(path));
	}

	/// <summary>
	/// 六根臂骨（肘/手/手扭转 × 左右）都必须能在骨架里找到。
	/// <para>
	/// 哈希值来自 <c>docs/HD2SDK-CommunityEdition-3.8.0/hashlists/bonehash.txt</c>：
	/// <c>480431462 l_elbow</c>、<c>836980140 r_elbow</c>、<c>1544317139 l_hand</c>、
	/// <c>612369653 r_hand</c>、<c>4135087676 l_hand_twist</c>、<c>2467345260 r_hand_twist</c>。
	/// </para>
	/// </summary>
	[Fact]
	public void TheAvatarCarriesAllSixArmBones()
	{
		var avatar = LoadAvatar();
		if (avatar is null)
			return;

		var expected = new (uint Hash, string Name)[]
		{
			(480431462, "l_elbow"),
			(836980140, "r_elbow"),
			(1544317139, "l_hand"),
			(612369653, "r_hand"),
			(4135087676, "l_hand_twist"),
			(2467345260, "r_hand_twist"),
		};

		foreach (var (hash, name) in expected)
		{
			Assert.True(
				avatar.NameHashes.Contains(hash),
				$"骨架里找不到 {name}（哈希 {hash}）—— ForearmWeightReweight 会静默跳过，实机上表现为完全没变化");
		}
	}

	/// <summary>
	/// 重分配确实会改动权重：在肘→手轴线上取一个 t/L=0.7 的点，
	/// 权重从"整条挂在手上"应当变成"大部分挂在肘上"。
	/// </summary>
	[Fact]
	public void ARelaxedForearmWeightMovesFromHandToElbow()
	{
		var avatar = LoadAvatar();
		if (avatar is null)
			return;

		// 取骨头的辅助：与实现同一套规则（矩阵平移在最后一行）。
		static (double X, double Y, double Z)? Head(UnitTransformInfo info, uint hash)
		{
			for (var i = 0; i < info.NameHashes.Count; i++)
			{
				if (info.NameHashes[i] != hash || i >= info.Matrices.Count)
					continue;
				var values = info.Matrices[i].Values;
				return values.Count < 15 ? null : (values[12], values[13], values[14]);
			}

			return null;
		}

		var elbow = Head(avatar, 480431462);
		var hand = Head(avatar, 1544317139);
		if (elbow is null || hand is null)
			return;

		// 沿轴线 70% 处（在 A=0.47..B=0.88 的斜坡内）。
		const double t = 0.7;
		var position = new Vec3(
			elbow.Value.X + ((hand.Value.X - elbow.Value.X) * t),
			elbow.Value.Y + ((hand.Value.Y - elbow.Value.Y) * t),
			elbow.Value.Z + ((hand.Value.Z - elbow.Value.Z) * t));

		// 起点：整条小臂 100% 挂在手上（源侧的"橡皮筋"斜坡）。
		var weights = new Dictionary<uint, double> { [1544317139u] = 1.0 };
		ForearmWeightReweight.Apply(weights, position, avatar);

		Assert.True(weights.ContainsKey(480431462u), "重分配之后肘部应当拿到权重");

		// 期望值按实现的定义手算：
		//   s = smoothstep(A=0.47, B=0.88, t/L=0.7)
		//     = u²(3-2u)，u = (0.7-0.47)/(0.88-0.47) = 0.56098
		//     = 0.31470 * 1.87805 = 0.59093
		//   肘 = 1 * (1 - s) = 0.40907
		// 实测 0.409 —— 逐位吻合。这里写成断言，把"斜坡参数"钉住：
		// 改 A/B 会让这条红，提醒改的人那是**出货参数**（`_r79_assemble.py:60`）。
		Assert.Equal(0.409, weights[480431462u], 3);
		Assert.True(weights.GetValueOrDefault(1544317139u) < 0.5, "手部权重应当降下来");
	}

	/// <summary>
	/// 肘→手的距离必须与出货 manifest 对得上。
	/// <para>
	/// 出货包的 manifest（<c>hd2mod.rig-lab-forearm-twist</c>）记着
	/// <c>length_m = 0.28735595387782237</c> —— 那是脚本里 <c>L = |handHead − elbowHead|</c> 的实测值。
	/// 我这边是<b>自己从骨架矩阵里取骨头</b>（下标 12..14），所以这个数字同时验证了
	/// "哈希找对了骨"和"平移取对了位置"两件事。差一点就说明整条链路有偏。
	/// </para>
	/// </summary>
	[Fact]
	public void TheElbowToHandLengthMatchesTheShippedManifest()
	{
		var avatar = LoadAvatar();
		if (avatar is null)
			return;

		static (double X, double Y, double Z)? Head(UnitTransformInfo info, uint hash)
		{
			for (var i = 0; i < info.NameHashes.Count; i++)
			{
				if (info.NameHashes[i] != hash || i >= info.Matrices.Count)
					continue;
				var values = info.Matrices[i].Values;
				return values.Count < 15 ? null : (values[12], values[13], values[14]);
			}

			return null;
		}

		var elbow = Head(avatar, 480431462u);
		var hand = Head(avatar, 1544317139u);
		if (elbow is null || hand is null)
			return;

		var dx = hand.Value.X - elbow.Value.X;
		var dy = hand.Value.Y - elbow.Value.Y;
		var dz = hand.Value.Z - elbow.Value.Z;
		var length = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));

		// 出货 manifest 实测 0.287356 m。允许 1 mm 的容差（不同 Unit 的骨架可能有细微差别）。
		Assert.True(Math.Abs(length - 0.287356) < 0.001,
			$"肘→手距离是 {length:F6} m，出货 manifest 记的是 0.287356 m —— 差得多说明骨头或平移取错了");
	}

	/// <summary>远离手臂的地方（例如头部）不该被改动。</summary>
	[Fact]
	public void VerticesOutsideTheArmAreLeftAlone()
	{
		var avatar = LoadAvatar();
		if (avatar is null)
			return;

		var weights = new Dictionary<uint, double> { [123456u] = 1.0 };
		ForearmWeightReweight.Apply(weights, new Vec3(0, 10, 0), avatar);

		Assert.Single(weights);
		Assert.Equal(123456u, weights.Keys.First());
		Assert.Equal(1.0, weights[123456u]);
	}
}
