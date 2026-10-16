using HD2SourceMigration.Retarget;

namespace HD2SourceMigration.Segmentation;

/// <summary>
/// HD2 骨 → 身体部位。规则是"从这根骨沿父链往上走，撞到第一个地标为止"。
/// <para>
/// <b>地标是量出来的，不是拍的。</b>拿参考包 <c>【4.28】Chiffon-LUNALICE替换RE-2310</c>
/// （Lunalice 换 RE-2310，与我们的源是同一个模型）逐顶点统计它的槽位归属，得到：
/// </para>
/// <list type="table">
/// <item><term><c>hips</c> / <c>l_thigh</c> / <c>l_knee</c> / <c>l_foot</c> 及其镜像</term>
/// <description>Hip 槽 <b>100%</b>——所以腿是长在"胯"里的，这一点光看槽位名猜不出来。</description></item>
/// <item><term><c>spine1</c></term><description>Torso <b>73.6%</b> / Hip 26.4%——胸/胯的分界就在 spine1 与 hips 之间。</description></item>
/// <item><term><c>spine2</c> / <c>chest</c> / <c>neck</c> / <c>head</c></term><description>Torso <b>100%</b>。</description></item>
/// <item><term><c>l_shoulder</c> / <c>r_shoulder</c></term><description>对应侧的 Arm <b>93.6%</b> / Torso 6.4%——<b>肩算手臂</b>，不算躯干。</description></item>
/// <item><term><c>elbow</c> / <c>hand</c> / 全部指骨</term><description>对应侧的 Arm <b>100%</b>。</description></item>
/// </list>
/// <para>
/// 参考包只分了 4 份（躯干/胯/左右臂），所以它给不出 7 路；上面这张表给出的是它<b>能</b>证实的
/// 两条最容易搞错的边界（肩算手臂、胸胯分界在 spine1/hips）。剩下两条（头与胸、胯与腿）由 HD2 自己的
/// 层级决定，没有歧义：<c>head</c> 之下才是头，<c>l/r_thigh</c> 之下才是腿。
/// </para>
/// <para>
/// 与参考包的<b>有意</b>差异：参考包里 <c>l_shoulder</c> 有 6.4% 的顶点落在 Torso，那是三角肌的
/// 权重混合带；本规则把它整块判给手臂。这是对的——混合本来就在权重里，不在归属里；
/// 而且归到哪一份都不影响形变（见 <see cref="BodyRegion"/> 的说明）。
/// </para>
/// </summary>
public sealed class BodyRegionResolver
{
	/// <summary>沿父链上溯时撞到这些名字就停。顺序即优先级（同名不会同时命中）。</summary>
	private static readonly Dictionary<string, BodyRegion> Landmarks = new(StringComparer.Ordinal)
	{
		["head"] = BodyRegion.Head,
		["neck"] = BodyRegion.Chest,
		["chest"] = BodyRegion.Chest,
		["spine2"] = BodyRegion.Chest,
		["spine1"] = BodyRegion.Chest,
		["hips"] = BodyRegion.Hips,
		["l_thigh"] = BodyRegion.LeftLeg,
		["r_thigh"] = BodyRegion.RightLeg,
		["l_clavicle"] = BodyRegion.LeftArm,
		["r_clavicle"] = BodyRegion.RightArm,
	};

	private readonly BodyRegion[] _regionByBone;
	private readonly bool[] _resolved;

	public BodyRegionResolver(Hd2Rig rig)
	{
		ArgumentNullException.ThrowIfNull(rig);
		_regionByBone = new BodyRegion[rig.Bones.Count];
		_resolved = new bool[rig.Bones.Count];
		Bones = rig.Bones;
	}

	public IReadOnlyList<Hd2RigBone> Bones { get; }

	/// <summary>某根骨属于哪个部位。没撞到地标的骨（根、武器骨等）归 <see cref="BodyRegion.Chest"/>。</summary>
	public BodyRegion RegionOf(int boneIndex)
	{
		if (boneIndex < 0 || boneIndex >= _regionByBone.Length)
			return BodyRegion.Chest;
		if (_resolved[boneIndex])
			return _regionByBone[boneIndex];

		var current = boneIndex;
		var region = BodyRegion.Chest;
		for (var step = 0; current >= 0 && step < 64; step++)
		{
			var name = Bones[current].Name;
			if (name is not null && Landmarks.TryGetValue(name, out var landmark))
			{
				region = landmark;
				break;
			}

			var parent = Bones[current].ParentIndex;
			if (parent == current || parent < 0 || parent >= Bones.Count)
				break;
			current = parent;
		}

		_regionByBone[boneIndex] = region;
		_resolved[boneIndex] = true;
		return region;
	}

	/// <summary>
	/// 一个顶点归哪个部位：看它的<b>主骨</b>（三组权重里最大的那个）。和 M1 的分区用的是同一条规则——
	/// "任意权重大于零"会把三角肌的 0.0006 当成归属证据，那正是 M1 踩过的坑。
	/// </summary>
	public BodyRegion RegionOfVertex(IReadOnlyDictionary<int, int> resolvedSourceToHd2, int sourceBoneIndex)
	{
		ArgumentNullException.ThrowIfNull(resolvedSourceToHd2);
		return resolvedSourceToHd2.TryGetValue(sourceBoneIndex, out var hd2)
			? RegionOf(hd2)
			: BodyRegion.Chest;
	}
}
