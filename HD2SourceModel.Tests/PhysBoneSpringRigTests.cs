using System.Buffers.Binary;
using System.Text;

namespace HD2SourceModel.Tests;

/// <summary>
/// <c>hd2_spring_rig.bin</c> 的布局回归。
/// <para>
/// 关键手法：造参数时**故意用 1,2,3,…29 这样带序号的数值**。
/// 只要字段顺序在 <see cref="SourceJiggleBone"/>、序列化器、读取器之间错位一格，
/// 断言就会立刻炸出来——这比"用同一份典型值"有效得多（典型值相等是巧合）。
/// </para>
/// <para>
/// A reader is written here on purpose instead of reusing the writer's knowledge:
/// the point is to pin the byte layout, so the test must independently know where each
/// field lives.
/// </para>
/// </summary>
public sealed class PhysBoneSpringRigTests
{
	/// <summary>按声明顺序给出 1..29 的参数值，下标即"第几个参数"。</summary>
	private static SourceJiggleBone OrderedParameters()
	{
		// 顺序必须与 SourceJiggleBone 的声明一致（Length … BaseForwardFriction）。
		return new SourceJiggleBone(
			Flags: 0x31,
			Length: 1, TipMass: 2,
			YawStiffness: 3, YawDamping: 4,
			PitchStiffness: 5, PitchDamping: 6,
			AlongStiffness: 7, AlongDamping: 8,
			AngleLimit: 9,
			MinYaw: 10, MaxYaw: 11, YawFriction: 12, YawBounce: 13,
			MinPitch: 14, MaxPitch: 15, PitchFriction: 16, PitchBounce: 17,
			BaseMass: 18, BaseStiffness: 19, BaseDamping: 20,
			BaseMinLeft: 21, BaseMaxLeft: 22, BaseLeftFriction: 23,
			BaseMinUp: 24, BaseMaxUp: 25, BaseUpFriction: 26,
			BaseMinForward: 27, BaseMaxForward: 28, BaseForwardFriction: 29);
	}

	private static SourceJiggleBoneFact Fact(int index, string name, int parent, SourceJiggleBone? parameters = null) =>
		new(index, name, parent, "parent_of_" + name, 0x31, ["flexible", "angleConstrained"],
			new Vec3(1.5, -2.5, 3.5), parameters ?? OrderedParameters());

	[Fact]
	public void Serialize_WritesTheDocumentedHeader()
	{
		var bytes = PhysBoneSpringRig.Serialize([Fact(7, "jig_a", 2)]);
		Assert.Equal("HD2SRG01"u8.ToArray(), bytes[..8]);
		Assert.Equal(PhysBoneSpringRig.Version, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)));
		Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)));
		Assert.Equal(148u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24)));
		// 名字表紧接记录区：32 字节头 + 1×144。
		Assert.Equal(32u + 148u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16)));
	}

	[Fact]
	public void Serialize_KeepsParameterOrderIntact()
	{
		var bytes = PhysBoneSpringRig.Serialize([Fact(0, "jig_a", -1)]);
		var recordAt = 32;

		Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(recordAt)));
		Assert.Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(recordAt + 4)));
		Assert.Equal(0x31u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(recordAt + 16)));

		Assert.Equal(1.5f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(recordAt + 20)), 5);
		Assert.Equal(-2.5f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(recordAt + 24)), 5);
		Assert.Equal(3.5f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(recordAt + 28)), 5);

		// 29 个参数必须依次等于 1..29；错位一格就会被抓到。
		for (var i = 0; i < 29; i++)
		{
			var actual = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(recordAt + 32 + i * 4));
			Assert.Equal(i + 1f, actual, 5);
		}
	}

	[Fact]
	public void Serialize_RoundTripsNamesAndParentsForAWholeChain()
	{
		SourceJiggleBoneFact[] bones =
		[
			Fact(72, "tail_root", 12),
			Fact(73, "tail_mid", 72),
			Fact(74, "tail_tip", 73),
		];
		var bytes = PhysBoneSpringRig.Serialize(bones);

		var nameOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16));
		var nameSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20));
		var names = bytes.AsSpan(nameOffset, nameSize);
		Assert.Equal(bytes.Length, nameOffset + nameSize);

		for (var i = 0; i < bones.Length; i++)
		{
			var recordAt = 32 + i * 148;
			Assert.Equal((uint)bones[i].BoneIndex, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(recordAt)));
			Assert.Equal(bones[i].ParentBoneIndex, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(recordAt + 4)));

			var localNameAt = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(recordAt + 8));
			var end = names[localNameAt..].IndexOf((byte)0);
			Assert.True(end > 0);
			Assert.Equal(bones[i].BoneName, Encoding.UTF8.GetString(names[localNameAt..(localNameAt + end)]));
		}
	}

	[Fact]
	public void Serialize_CarriesTheParentNameSoTheRuntimeCanFindTheAnchorBone()
	{
		// 运行时只知道「链根挂在哪根 HD2 骨上」，而父骨索引是源模型的骨下标，
		// HD2 侧根本没有那张表——只有名字能跨语言、跨引擎传过去。
		var bones = new[] { new SourceJiggleBoneFact(72, "tail_root", 12, "ValveBiped.Bip01_Head1", 0x31,
			["flexible"], new Vec3(1, 2, 3), OrderedParameters()) };
		var bytes = PhysBoneSpringRig.Serialize(bones);

		var nameOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16));
		var nameSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20));
		var names = bytes.AsSpan(nameOffset, nameSize);

		var parentNameAt = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(32 + 12));
		var end = names[parentNameAt..].IndexOf((byte)0);
		Assert.True(end > 0);
		Assert.Equal("ValveBiped.Bip01_Head1", Encoding.UTF8.GetString(names[parentNameAt..(parentNameAt + end)]));
	}

	[Fact]
	public void Serialize_HandlesEmptyInputAsAValidHeaderOnlyBlock()
	{
		// 空 rig 也要是一个自洽的文件：32 字节头、零记录、零长度名字表。
		var bytes = PhysBoneSpringRig.Serialize([]);
		Assert.Equal(32, bytes.Length);
		Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)));
		Assert.Equal(32u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16)));
		Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20)));
	}

	[Fact]
	public void Serialize_IsDeterministicForTheSameInput()
	{
		// 同一份输入必须产出逐字节相同的输出，否则补丁会莫名其妙地"每次都不一样"，
		// 指纹缓存、部署比较、mod 去重全部失效。
		var a = PhysBoneSpringRig.Serialize([Fact(1, "x", 0), Fact(2, "y", 1)]);
		var b = PhysBoneSpringRig.Serialize([Fact(1, "x", 0), Fact(2, "y", 1)]);
		Assert.Equal(a, b);
	}
}
