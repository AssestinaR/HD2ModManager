using HD2SourceMigration.Retarget;
using HD2SourceMigration.VpkConversion;
using HD2SourceModel;

namespace HD2SourceMigration.Tests;

/// <summary>
/// 物理骨按件切分：每个 unit 只带自己蒙到的那几根（外加父链）。
/// <para>
/// <b>为什么这一层要单独钉死。</b>切多了只是没省下性能，切**少了**是"某块布料不动了"，
/// 而且只有实机才看得出来（运行时会安静地跳过缺节点的链，日志上只是一行数字变小）。
/// 所以父链闭包、父下标重映射、以及"结构不对就退回全量"这三条必须在这里验。
/// </para>
/// </summary>
public sealed class ExtraBoneSubsetTests
{
	/// <summary>合成骨在 TransformInfo 里的起始下标（= 原生骨根数，与 <c>PlanJiggleBones</c> 的约定一致）。</summary>
	private const int BaseCount = 100;

	/// <summary>一根原生骨的下标（例如 <c>head</c>）。合成骨的父落在 <see cref="BaseCount"/> 之前就是它。</summary>
	private const int NativeParent = 10;

	private static RetargetExtraBone Extra(string name, int parent) =>
		new(name, Hash(name), parent, Vec3.Zero);

	private static uint Hash(string name)
	{
		// 用途与 Hd2NameHash 相同，这里只需要"同名同值、异名异值"。
		var value = 2166136261u;
		foreach (var character in name)
		{
			value ^= character;
			value *= 16777619u;
		}

		return value;
	}

	/// <summary>一份只关心"顶点影响里出现哪些骨名哈希"的网格。</summary>
	private static RetargetedMesh MeshWithHashes(params uint[] hashes)
	{
		var vertices = new List<RetargetedVertex>(hashes.Length);
		foreach (var hash in hashes)
		{
			vertices.Add(new RetargetedVertex(
				Vec3.Zero, Vec3.Zero, 0f, 0f, [new RetargetedInfluence(hash, 1f)]));
		}

		return new RetargetedMesh(vertices, []);
	}

	[Fact]
	public void Select_KeepsOnlyTheBonesThisUnitIsSkinnedTo()
	{
		var extras = new[] { Extra("a", NativeParent), Extra("b", NativeParent), Extra("c", NativeParent) };
		var mesh = MeshWithHashes(Hash("a"), Hash("c"));

		var selection = ExtraBoneSubset.Select(extras, BaseCount, mesh);

		Assert.Null(selection.Warning);
		Assert.Equal(["a", "c"], selection.Extras.Select(extra => extra.Name));
		Assert.Equal(1, selection.Dropped);
		Assert.True(selection.Subsetted);
	}

	[Fact]
	public void Select_PullsInAncestorsSoTheChainStaysWhole()
	{
		// 只用到了链的末节，但运行时是从**链根**开始驱动的：不带祖先，这条链根本不存在。
		var extras = new[]
		{
			Extra("head_1", NativeParent),
			Extra("hair_1", BaseCount),          // 挂在 head_1 上
			Extra("hair_2", BaseCount + 1),      // 挂在 hair_1 上
		};
		var mesh = MeshWithHashes(Hash("hair_2"));

		var selection = ExtraBoneSubset.Select(extras, BaseCount, mesh);

		Assert.Equal(["head_1", "hair_1", "hair_2"], selection.Extras.Select(extra => extra.Name));
		Assert.Equal(NativeParent, selection.Extras[0].ParentIndex);
		Assert.Equal(BaseCount, selection.Extras[1].ParentIndex);
		Assert.Equal(BaseCount + 1, selection.Extras[2].ParentIndex);
	}

	[Fact]
	public void Select_RemapsParentIndicesAfterDroppingBonesInFrontOfThem()
	{
		// "foot" 的父是 "leg_2"（原下标 101），而 "leg_1"（原下标 100）没人用到 ⇒ 被丢掉。
		// 重排之后 "leg_2" 落到 100，父下标必须跟着改；不改就是"挂到别人的父上"。
		var extras = new[]
		{
			Extra("leg_1", NativeParent),
			Extra("leg_2", NativeParent),
			Extra("foot", BaseCount + 1),
		};
		var mesh = MeshWithHashes(Hash("foot"));

		var selection = ExtraBoneSubset.Select(extras, BaseCount, mesh);

		Assert.Equal(["leg_2", "foot"], selection.Extras.Select(extra => extra.Name));
		Assert.Equal(NativeParent, selection.Extras[0].ParentIndex);
		Assert.Equal(BaseCount, selection.Extras[1].ParentIndex);   // 101 → 100
	}

	[Fact]
	public void Select_RefusesToDropAnythingWhenTheParentStructureMakesNoSense()
	{
		// 父下标指向一个不存在的合成骨：任何切分都可能把链弄断 ⇒ 退回全量，并说明原因。
		var extras = new[] { Extra("a", NativeParent), Extra("b", BaseCount + 99) };
		var mesh = MeshWithHashes(Hash("a"));

		var selection = ExtraBoneSubset.Select(extras, BaseCount, mesh);

		Assert.NotNull(selection.Warning);
		Assert.Equal(extras.Length, selection.Extras.Count);
		Assert.Equal(0, selection.Dropped);
		Assert.False(selection.Subsetted);
	}

	[Fact]
	public void Select_DropsEveryBoneWhenNoVertexTouchesThem()
	{
		// 一个只渲染身体、完全没有布料几何的 unit：一根物理骨都不该带。
		var extras = new[] { Extra("a", NativeParent), Extra("b", NativeParent) };
		var mesh = MeshWithHashes(Hash("native_head"));

		var selection = ExtraBoneSubset.Select(extras, BaseCount, mesh);

		Assert.Empty(selection.Extras);
		Assert.Equal(2, selection.Dropped);
	}

	[Fact]
	public void Select_KeepsTheOriginalOrderSoChainsStayParentBeforeChild()
	{
		// 输入顺序就是"父在前、子在后"；输出必须保持它，否则追加时父亲还没进表。
		var extras = new[]
		{
			Extra("a", NativeParent),
			Extra("b", NativeParent),
			Extra("c", BaseCount + 1),          // 挂在 b 上
			Extra("d", BaseCount + 2),          // 挂在 c 上
		};
		var mesh = MeshWithHashes(Hash("d"), Hash("b"));

		var selection = ExtraBoneSubset.Select(extras, BaseCount, mesh);

		Assert.Equal(["b", "c", "d"], selection.Extras.Select(extra => extra.Name));
		for (var index = 0; index < selection.Extras.Count; index++)
		{
			var parent = selection.Extras[index].ParentIndex;
			Assert.True(parent < BaseCount || parent < BaseCount + index,
				$"{selection.Extras[index].Name} 的父 {parent} 必须排在它前面");
		}
	}

	[Fact]
	public void Select_IgnoresHashesThatBelongToNoExtraBone()
	{
		var extras = new[] { Extra("a", NativeParent) };
		var mesh = MeshWithHashes(0u, uint.MaxValue);

		var selection = ExtraBoneSubset.Select(extras, BaseCount, mesh);

		Assert.Empty(selection.Extras);
		Assert.Equal(1, selection.Dropped);
	}

	[Fact]
	public void Select_WithoutExtrasIsANoOp()
	{
		var selection = ExtraBoneSubset.Select([], BaseCount, MeshWithHashes(1u));

		Assert.Empty(selection.Extras);
		Assert.Equal(0, selection.Dropped);
		Assert.Null(selection.Warning);
	}
}
