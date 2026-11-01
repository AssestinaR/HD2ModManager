using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2SourceMigration.Retarget;
using HD2SourceMigration.VpkConversion;
using HD2SourceModel;

namespace HD2SourceMigration.Tests;

/// <summary>
/// 合成节点追加：约定错一个字段，游戏不会报错，只会让新骨不跟手或把网格撕开。
/// 这里的期望值来自工作补丁的实测（120 个节点逐个核对：Increment 恒 1、矩阵平移在 12/13/14、局部旋转单位阵）。
/// </summary>
public sealed class UnitTransformInfoAppenderTests
{
	private static UnitTransformInfo Native(int count)
	{
		var locals = new List<UnitLocalTransform>();
		var matrices = new List<UnitTransformMatrix>();
		var entries = new List<UnitTransformEntry>();
		var hashes = new List<uint>();
		for (var i = 0; i < count; i++)
		{
			// 每个原生节点：世界 (i,0,0)；局部（父空间）是 (1,0,0)（根为 0）。两张表必须自洽。
			var localX = i == 0 ? 0f : 1f;
			locals.Add(new UnitLocalTransform([1, 0, 0, 0, 1, 0, 0, 0, 1], [localX, 0, 0], [1, 1, 1], 0));
			matrices.Add(new UnitTransformMatrix([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, i, 0, 0, 1]));
			entries.Add(new UnitTransformEntry(1, checked((ushort)Math.Max(i - 1, 0))));
			hashes.Add(Hd2NameHash.Of($"native_{i}"));
		}

		return new UnitTransformInfo(0, 0, 0, locals, matrices, entries, hashes);
	}

	[Fact]
	public void Append_AddsOneNodePerExtraBoneAtTheEnd()
	{
		var info = Native(3);
		var extras = new[]
		{
			new RetargetExtraBone("source_extra_64", Hd2NameHash.Of("source_extra_64"), 2, new Vec3(1, 2, 3)),
			new RetargetExtraBone("source_extra_65", Hd2NameHash.Of("source_extra_65"), 3, new Vec3(4, 5, 6)),
		};

		var extended = UnitTransformInfoAppender.Append(info, extras)!;

		Assert.Equal(5, extended.NameHashes.Count);
		Assert.Equal(5, extended.LocalTransforms.Count);
		Assert.Equal(5, extended.Matrices.Count);
		Assert.Equal(5, extended.Entries.Count);

		// 原生部分一个字节都不动
		for (var i = 0; i < 3; i++)
		{
			Assert.Equal(info.NameHashes[i], extended.NameHashes[i]);
			Assert.Equal(info.Entries[i], extended.Entries[i]);
		}

		// 新节点接在末尾，哈希按顺序追加
		Assert.Equal(Hd2NameHash.Of("source_extra_64"), extended.NameHashes[3]);
		Assert.Equal(Hd2NameHash.Of("source_extra_65"), extended.NameHashes[4]);
	}

	[Fact]
	public void Append_KeepsWorldAndLocalConsistent_WithAnUnrotatedParent()
	{
		var info = Native(2);   // 节点 1 在 (1,0,0)
		var extras = new[] { new RetargetExtraBone("source_extra_64", 0xdeadbeefu, 1, new Vec3(0.5, -0.25, 1.5)) };

		var extended = UnitTransformInfoAppender.Append(info, extras)!;

		// 世界矩阵：旋转沿用父骨（单位阵），平移是世界位置
		var world = extended.Matrices[2].Values;
		Assert.Equal(16, world.Count);
		Assert.Equal(0.5f, world[12]);
		Assert.Equal(-0.25f, world[13]);
		Assert.Equal(1.5f, world[14]);
		Assert.Equal(1f, world[15]);

		// 局部平移是**父空间**：世界位置减去父骨位置
		var local = extended.LocalTransforms[2];
		Assert.Equal([1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f], local.Rotation);   // 局部旋转 = 单位阵
		Assert.Equal(-0.5f, local.Position[0]);
		Assert.Equal(-0.25f, local.Position[1]);
		Assert.Equal(1.5f, local.Position[2]);
		Assert.Equal([1f, 1f, 1f], local.Scale);
	}

	[Fact]
	public void Append_ComposesBackToTheWorldPosition_WithARotatedParent()
	{
		// 父骨绕 +Z 转 90°（行向量存储：row0=(c,s,0)、row1=(-s,c,0)），父平移 (2,3,0)。
		var info = BuildWithRotatedParent();
		var extras = new[] { new RetargetExtraBone("source_extra_64", 0x1u, 1, new Vec3(3, 3, 0)) };

		var extended = UnitTransformInfoAppender.Append(info, extras)!;
		var local = extended.LocalTransforms[2].Position;
		var parent = extended.Matrices[1].Values;

		// 局部 → 世界：p' = p·R_parent + t_parent
		var wx = local[0] * parent[0] + local[1] * parent[4] + local[2] * parent[8] + parent[12];
		var wy = local[0] * parent[1] + local[1] * parent[5] + local[2] * parent[9] + parent[13];
		var wz = local[0] * parent[2] + local[1] * parent[6] + local[2] * parent[10] + parent[14];

		Assert.Equal(3f, wx, 3);
		Assert.Equal(3f, wy, 3);
		Assert.Equal(0f, wz, 3);

		// 世界矩阵的旋转部分必须**等于父骨旋转**（不是单位阵）
		var world = extended.Matrices[2].Values;
		Assert.Equal(parent[0], world[0], 4);
		Assert.Equal(parent[1], world[1], 4);
		Assert.Equal(parent[4], world[4], 4);
	}

	/// <summary>造一个"节点 1 已带旋转"的表：绕 +Z 转 90°，平移 (2,3,0)。</summary>
	private static UnitTransformInfo BuildWithRotatedParent()
	{
		(float C, float S) = (0f, 1f);   // cos90=0, sin90=1
		var locals = new List<UnitLocalTransform>
		{
			new([1, 0, 0, 0, 1, 0, 0, 0, 1], [0, 0, 0], [1, 1, 1], 0),
			new([C, S, 0, -S, C, 0, 0, 0, 1], [2, 3, 0], [1, 1, 1], 0),
		};
		var matrices = new List<UnitTransformMatrix>
		{
			new([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]),
			new([C, S, 0, 0, -S, C, 0, 0, 0, 0, 1, 0, 2, 3, 0, 1]),
		};
		var entries = new List<UnitTransformEntry>
		{
			new(1, 0),
			new(1, 0),
		};
		return new UnitTransformInfo(0, 0, 0, locals, matrices, entries,
			[Hd2NameHash.Of("native_0"), Hd2NameHash.Of("native_1")]);
	}

	[Fact]
	public void AssertLocalAndWorldAgree_AcceptsWhatWeAppend()
	{
		var info = BuildWithRotatedParent();
		var extras = new[]
		{
			new RetargetExtraBone("source_extra_64", 0x1u, 1, new Vec3(3, 3, 0)),
			new RetargetExtraBone("source_extra_65", 0x2u, 2, new Vec3(3, 4, 0)),   // 挂在上一根合成骨上
		};

		var extended = UnitTransformInfoAppender.Append(info, extras)!;

		UnitTransformInfoAppender.AssertLocalAndWorldAgree(extended);
	}

	[Fact]
	public void AssertLocalAndWorldAgree_RejectsTheOldBrokenLayout()
	{
		// 复现旧 bug：把**世界**坐标直接当 local 位置写进去（少了父平移那一项）。
		var locals = new List<UnitLocalTransform>
		{
			new([1, 0, 0, 0, 1, 0, 0, 0, 1], [0, 0, 0], [1, 1, 1], 0),
			new([1, 0, 0, 0, 1, 0, 0, 0, 1], [2, 3, 0], [1, 1, 1], 0),
			new([1, 0, 0, 0, 1, 0, 0, 0, 1], [5, 5, 0], [1, 1, 1], 0),
		};
		var matrices = new List<UnitTransformMatrix>
		{
			new([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]),
			new([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 2, 3, 0, 1]),
			new([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 5, 5, 0, 1]),
		};
		var entries = new List<UnitTransformEntry> { new(1, 0), new(1, 0), new(1, 1) };
		var broken = new UnitTransformInfo(0, 0, 0, locals, matrices, entries,
			[Hd2NameHash.Of("a"), Hd2NameHash.Of("b"), Hd2NameHash.Of("c")]);

		Assert.Throws<InvalidOperationException>(() =>
			UnitTransformInfoAppender.AssertLocalAndWorldAgree(broken));
	}

	[Fact]
	public void Append_KeepsChainsParentedToEarlierExtraBones()
	{
		var info = Native(2);
		var extras = new[]
		{
			new RetargetExtraBone("source_extra_64", 0x1u, 1, Vec3.Zero),
			new RetargetExtraBone("source_extra_65", 0x2u, 2, Vec3.Zero),   // 父 = 前一个合成骨
		};

		var extended = UnitTransformInfoAppender.Append(info, extras)!;

		Assert.Equal(1, extended.Entries[2].ParentIndex);
		Assert.Equal(2, extended.Entries[3].ParentIndex);
	}

	[Fact]
	public void Append_ReturnsNullForNullInput()
	{
		// 纯材质 patch 没有 Unit 计划，装配器会传 null —— 这条路径必须原样通过。
		Assert.Null(UnitTransformInfoAppender.Append(null, null));
		Assert.Null(UnitTransformInfoAppender.Append(null, Array.Empty<RetargetExtraBone>()));
		Assert.Null(UnitTransformInfoAppender.Append(null,
			new[] { new RetargetExtraBone("source_extra_64", 0x1u, 0, Vec3.Zero) }));
	}

	[Fact]
	public void Append_ReturnsTheSameInstanceWhenThereIsNothingToAppend()
	{
		var info = Native(2);

		Assert.Same(info, UnitTransformInfoAppender.Append(info, null));
		Assert.Same(info, UnitTransformInfoAppender.Append(info, Array.Empty<RetargetExtraBone>()));
	}

	[Fact]
	public void Append_RejectsAnOutOfRangeParent()
	{
		var info = Native(2);
		var extras = new[] { new RetargetExtraBone("source_extra_64", 0x1u, 7, Vec3.Zero) };

		var error = Assert.Throws<InvalidOperationException>(() => UnitTransformInfoAppender.Append(info, extras));
		Assert.Contains("父下标 7 越界", error.Message, StringComparison.Ordinal);
	}
}
