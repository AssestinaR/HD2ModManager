using System.Buffers.Binary;
using System.Text;
using HD2SourceMigration.VpkConversion;
using HD2SourceModel;

namespace HD2SourceMigration.Tests;

/// <summary>
/// 转换期产出的物理参数 rig（<c>physbone-rig.bin</c>）：把"链根锚点名"带进容器。
/// <para>
/// 为什么需要中间文件：往 <c>.gpu_resources</c> 追加字节必须在整批产物落盘之后
/// （转换阶段追加会让后续受管步骤记账错位，历史症状是材质整体投递失败、面部品红），
/// 但锚点名只有重定向那一刻算得出来 —— <c>embed-mod</c> 只看得到来源 VPK，看不到 HD2 骨架。
/// 所以转换期落在产物旁边，嵌入期读。
/// </para>
/// </summary>
public sealed class PhysBoneRigSidecarTests
{
	private static SourceJiggleBone Parameters() =>
		new(Flags: 0x31, Length: 30, TipMass: 200,
			YawStiffness: 50, YawDamping: 7, PitchStiffness: 50, PitchDamping: 7,
			AlongStiffness: 0, AlongDamping: 0, AngleLimit: 0.611f,
			MinYaw: 0, MaxYaw: 0, YawFriction: 0, YawBounce: 0,
			MinPitch: 0, MaxPitch: 0, PitchFriction: 0, PitchBounce: 0,
			BaseMass: 0, BaseStiffness: 0, BaseDamping: 0,
			BaseMinLeft: 0, BaseMaxLeft: 0, BaseLeftFriction: 0,
			BaseMinUp: 0, BaseMaxUp: 0, BaseUpFriction: 0,
			BaseMinForward: 0, BaseMaxForward: 0, BaseForwardFriction: 0);

	private static SourceJiggleBoneFact Fact(int index, string name, string parentName) =>
		new(index, name, index - 1, parentName, 0x31, ["flexible"], new Vec3(0, 0, 1), Parameters());

	/// <summary>独立按布局把某一根骨的父骨名读回来（刻意不复用写入方的知识）。</summary>
	private static string ParentNameAt(byte[] rig, int recordIndex)
	{
		var nameOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(rig.AsSpan(16));
		var nameSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(rig.AsSpan(20));
		var names = rig.AsSpan(nameOffset, nameSize);
		var at = 32 + recordIndex * 148;
		var local = (int)BinaryPrimitives.ReadUInt32LittleEndian(rig.AsSpan(at + 12));
		var end = names[local..].IndexOf((byte)0);
		return Encoding.UTF8.GetString(names[local..(local + end)]);
	}

	[Fact]
	public void ApplyAnchors_RewritesOnlyTheParentsThatHaveAnAnchor()
	{
		// ValveBiped 名运行时有映射表，保留原名更稳（映射表将来变了，运行时那一份是活的）。
		// 只有"HD2 侧查不到"的那些才必须换成锚点名。
		SourceJiggleBoneFact[] facts =
		[
			Fact(72, "jig_a", "ValveBiped.Bip01_Head1"),
			Fact(73, "jig_b", "source_extra_90"),
			Fact(74, "jig_c", "UN_Skirt_R_02"),
		];
		var anchors = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["source_extra_90"] = "hips",
			["UN_Skirt_R_02"] = "l_thigh",
		};

		var applied = PhysBoneRigSidecar.ApplyAnchors(facts, anchors);

		Assert.Equal("ValveBiped.Bip01_Head1", applied[0].ParentName);
		Assert.Equal("hips", applied[1].ParentName);
		Assert.Equal("l_thigh", applied[2].ParentName);
		// 原表不能被就地改写：调用方可能还要拿它做报告。
		Assert.Equal("source_extra_90", facts[1].ParentName);
	}

	[Fact]
	public void ApplyAnchors_ReturnsTheSameTableWhenThereIsNothingToApply()
	{
		SourceJiggleBoneFact[] facts = [Fact(72, "jig_a", "head")];
		Assert.Same(facts, PhysBoneRigSidecar.ApplyAnchors(facts, null));
		Assert.Same(facts, PhysBoneRigSidecar.ApplyAnchors(facts, new Dictionary<string, string>()));
	}

	[Fact]
	public void Write_PutsTheAnchoredParentNameIntoTheRig()
	{
		var directory = Directory.CreateTempSubdirectory("hd2pbx-sidecar-");
		try
		{
			var path = PhysBoneRigSidecar.Write(
				directory.FullName,
				[Fact(72, "jig_a", "source_extra_90")],
				new Dictionary<string, string>(StringComparer.Ordinal) { ["source_extra_90"] = "hips" });

			Assert.NotNull(path);
			Assert.Equal(Path.Combine(directory.FullName, PhysBoneRigSidecar.FileName), path);
			var rig = File.ReadAllBytes(path!);
			Assert.Equal("HD2SRG01"u8.ToArray(), rig[..8]);
			Assert.Equal("hips", ParentNameAt(rig, 0));
		}
		finally
		{
			directory.Delete(recursive: true);
		}
	}

	[Fact]
	public void Write_SkipsWhenTheModelHasNoJiggleBones()
	{
		// 空文件会让 embed-mod 走"有 rig"这条分支，于是不再报告"来源模型没有 jigglebone"。
		var directory = Directory.CreateTempSubdirectory("hd2pbx-sidecar-empty-");
		try
		{
			Assert.Null(PhysBoneRigSidecar.Write(directory.FullName, [], null));
			Assert.False(File.Exists(Path.Combine(directory.FullName, PhysBoneRigSidecar.FileName)));
		}
		finally
		{
			directory.Delete(recursive: true);
		}
	}

	[Fact]
	public void Locate_PrefersTheConvertedPatchDirectoryOverOverwrite()
	{
		var directory = Directory.CreateTempSubdirectory("hd2pbx-sidecar-locate-");
		try
		{
			Assert.Null(PhysBoneRigSidecar.Locate(directory.FullName));

			var overwrite = Directory.CreateDirectory(Path.Combine(directory.FullName, "Overwrite"));
			File.WriteAllBytes(Path.Combine(overwrite.FullName, PhysBoneRigSidecar.FileName), [1, 2, 3]);

			var converted = Directory.CreateDirectory(Path.Combine(directory.FullName, "vpk2patch"));
			var expected = Path.Combine(converted.FullName, PhysBoneRigSidecar.FileName);
			File.WriteAllBytes(expected, [1, 2, 3]);

			Assert.Equal(expected, PhysBoneRigSidecar.Locate(directory.FullName));
		}
		finally
		{
			directory.Delete(recursive: true);
		}
	}

	[Fact]
	public void TryRead_RejectsAFileThatIsNotARig()
	{
		var directory = Directory.CreateTempSubdirectory("hd2pbx-sidecar-read-");
		try
		{
			var path = Path.Combine(directory.FullName, "bad.bin");
			File.WriteAllBytes(path, "NOTARIG!"u8.ToArray());

			// 宁可回退到"重读来源 VPK"，也不把一份坏数据嵌进 patch。
			Assert.Null(PhysBoneRigSidecar.TryRead(path));
			Assert.Null(PhysBoneRigSidecar.TryRead(Path.Combine(directory.FullName, "missing.bin")));
		}
		finally
		{
			directory.Delete(recursive: true);
		}
	}
}
