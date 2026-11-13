using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using Xunit;

namespace HD2ModAdaptation.Tests;

// Purpose: Verifies Canonical Avatar-rig expansion.
public sealed class CanonicalTransformInfoExpanderTests
{
	[Fact]
	public void Expand_AddsMissingPaletteBoneWithAvatarParentChain()
	{
		var target = Model([10]);
		var source = Model([10, 20, 30], new UnitBoneInfo(0, 0, 1, 0, 0, 0, [2], []));
		var sourceRaw = new UnitRawMeshData(0, 1, 0, 0, [], [], []);
		var avatar = TransformInfo([10, 20, 30], [0, 0, 1]);

		var expanded = new CanonicalTransformInfoExpander().Expand(target, [(source, sourceRaw)], avatar);

		Assert.Equal(new uint[] { 10, 20, 30 }, expanded.TransformNameHashes);
		Assert.Equal((ushort)0, expanded.TransformInfo.Entries[1].ParentIndex);
		Assert.Equal((ushort)1, expanded.TransformInfo.Entries[2].ParentIndex);
		Assert.Equal(3, expanded.TransformInfo.Matrices.Count);
	}

	[Fact]
	public void Expand_RejectsPaletteBoneMissingFromAvatar()
	{
		var target = Model([10]);
		var source = Model([10, 99], new UnitBoneInfo(0, 0, 1, 0, 0, 0, [1], []));
		var sourceRaw = new UnitRawMeshData(0, 1, 0, 0, [], [], []);

		var exception = Assert.Throws<InvalidDataException>(() => new CanonicalTransformInfoExpander().Expand(target, [(source, sourceRaw)], TransformInfo([10], [0])));

		Assert.Contains("0x00000063", exception.Message);
	}

	[Fact]
	public void Expand_ReusesCustomBoneTheHostAlreadyHas_OnlyWhenAllowed()
	{
		// 99 不在 Avatar 里，但宿主（target）自己已经有它：P4b 合并 VPK 部位 unit 的情形。
		var target = Model([10, 99]);
		var source = Model([10, 99], new UnitBoneInfo(0, 0, 1, 0, 0, 0, [1], []));
		var sourceRaw = new UnitRawMeshData(0, 1, 0, 0, [], [], []);
		var avatar = TransformInfo([10], [0]);

		var expanded = new CanonicalTransformInfoExpander().Expand(target, [(source, sourceRaw)], avatar, allowTargetOwnedBones: true);

		Assert.Equal(new uint[] { 10, 99 }, expanded.TransformNameHashes);   // 宿主节点原样保留，没有新增
		Assert.Equal(2, expanded.TransformInfo.Matrices.Count);

		// 关闭（默认）时行为不变：照旧抛。
		var exception = Assert.Throws<InvalidDataException>(() => new CanonicalTransformInfoExpander().Expand(target, [(source, sourceRaw)], avatar));
		Assert.Contains("0x00000063", exception.Message);
	}

	[Fact]
	public void Expand_ImportsCustomBonesOnlyTheDonorHas_WithTheirParentChain_OnlyWhenAllowed()
	{
		// 宿主只有 10；donor 的 palette 用 99，而 99 挂在自定义骨 98 下，98 又挂在 Avatar 骨 10 下。
		// P4b 合并别的部位（宿主没有 donor 的自定义骨）：连同父链从 donor 搬进宿主。
		var target = Model([10]);
		var source = ModelWithParents([10, 98, 99], [0, 0, 1], new UnitBoneInfo(0, 0, 1, 0, 0, 0, [2], []));
		var sourceRaw = new UnitRawMeshData(0, 1, 0, 0, [], [], []);
		var avatar = TransformInfo([10], [0]);

		var expanded = new CanonicalTransformInfoExpander().Expand(target, [(source, sourceRaw)], avatar, allowTargetOwnedBones: true);

		Assert.Equal(new uint[] { 10, 98, 99 }, expanded.TransformNameHashes);   // 先搬父 98，再搬 99
		Assert.Equal(0, expanded.TransformInfo.Entries[1].ParentIndex);          // 98 → 宿主里的 10
		Assert.Equal(1, expanded.TransformInfo.Entries[2].ParentIndex);          // 99 → 刚搬进来的 98
		Assert.Equal(3, expanded.TransformInfo.Matrices.Count);
		Assert.Equal(3, expanded.TransformInfo.LocalTransforms.Count);

		// 关闭（默认）时行为不变：宿主没有这根骨，照旧抛。
		var exception = Assert.Throws<InvalidDataException>(() => new CanonicalTransformInfoExpander().Expand(target, [(source, sourceRaw)], avatar));
		Assert.Contains("0x00000063", exception.Message);
	}

	[Fact]
	public void Expand_StillRejectsACustomBoneWhoseDonorChainIsBroken_EvenWhenAllowed()
	{
		// 99 在 donor 里自己当自己的父（没有可用的父链）：搬不进来，开关打开也照旧抛，不硬塞。
		var target = Model([10]);
		var source = ModelWithParents([10, 99], [0, 1], new UnitBoneInfo(0, 0, 1, 0, 0, 0, [1], []));
		var sourceRaw = new UnitRawMeshData(0, 1, 0, 0, [], [], []);

		Assert.Throws<InvalidDataException>(() => new CanonicalTransformInfoExpander()
			.Expand(target, [(source, sourceRaw)], TransformInfo([10], [0]), allowTargetOwnedBones: true));
	}

	private static UnitMeshModel ModelWithParents(IReadOnlyList<uint> hashes, IReadOnlyList<int> parents, UnitBoneInfo bone)
		=> new(1, 1, 0, 0, 0, 0, 1, 1, 0, 0, UnitCustomizationInfo.Empty, [bone], [], [], [], [], [])
		{
			TransformInfoOffset = 1,
			TransformInfo = TransformInfo(hashes, parents),
			TransformNameHashes = hashes
		};

	private static UnitMeshModel Model(IReadOnlyList<uint> hashes, UnitBoneInfo? bone = null)
		=> new(1, 1, 0, 0, 0, 0, 1, 1, 0, 0, UnitCustomizationInfo.Empty, bone is null ? [] : [bone], [], [], [], [], [])
		{
			TransformInfoOffset = 1,
			TransformInfo = TransformInfo(hashes, Enumerable.Repeat(0, hashes.Count).ToArray()),
			TransformNameHashes = hashes
		};

	private static UnitTransformInfo TransformInfo(IReadOnlyList<uint> hashes, IReadOnlyList<int> parents)
	{
		var local = new UnitLocalTransform([1, 0, 0, 0, 1, 0, 0, 0, 1], [0, 0, 0], [1, 1, 1], 0);
		var matrix = new UnitTransformMatrix([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]);
		return new UnitTransformInfo(0, 0, 0,
			Enumerable.Repeat(local, hashes.Count).ToArray(),
			Enumerable.Repeat(matrix, hashes.Count).ToArray(),
			parents.Select((parent, index) => new UnitTransformEntry(1, checked((ushort)parent))).ToArray(), hashes);
	}
}
