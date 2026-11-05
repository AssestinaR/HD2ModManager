using static HD2SourceModel.Tests.SourceFormatFixtures;

namespace HD2SourceModel.Tests;

/// <summary>
/// 「骨架与几何分开打包」的定位回归。
/// <para>
/// 实测 L4D2-3831123094（NekoVpk 包装的重定向包）：<c>survivor_namvet.mdl</c> 的**内部名**
/// 指向 <c>arcpav_nte_akane_fashion4/bone128.mdl</c>，而它同名的 vvd/vtx 只有 **28 字节**
/// ——一个合法的 <c>IDSV</c> 头 + 零数据。几何只留一份在共用模型上。
/// </para>
/// <para>
/// 只按文件名找就会读到那个壳，报「VVD magic 不是 IDSV」，看起来像文件坏了；
/// 真实原因是"该去内部名指向的那个模型取几何"。
/// </para>
/// </summary>
public sealed class SourceModelLocatorTests
{
	private static byte[] RealVvd()
	{
		// 定位只验"头 + 长度自洽"，所以造一个最小的合法头：IDSV、version 4、
		// lod0VertexCount = 1（偏移 16）、vertexOffset = 64（偏移 56）⇒ 64 + 1×48 ≤ 256。
		var bytes = new byte[256];
		"IDSV"u8.CopyTo(bytes);
		BitConverter.GetBytes(4).CopyTo(bytes, 4);
		BitConverter.GetBytes(1).CopyTo(bytes, 16);
		BitConverter.GetBytes(64).CopyTo(bytes, 56);
		return bytes;
	}

	private static byte[] RealVtx()
	{
		var bytes = new byte[256];
		BitConverter.GetBytes(7).CopyTo(bytes, 0);
		return bytes;
	}

	/// <summary>28 字节的壳：只有头，没有数据（实测就是这个形状，见 L4D2-3831123094）。</summary>
	private static byte[] StubVvd()
	{
		var bytes = new byte[28];
		"IDSV"u8.CopyTo(bytes);
		BitConverter.GetBytes(4).CopyTo(bytes, 4);
		return bytes;
	}

	private static void WithArchive(byte[] content, Action<VpkArchive> assert)
	{
		var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n") + ".vpk");
		File.WriteAllBytes(path, content);
		try
		{
			using var archive = VpkArchive.Open(path);
			assert(archive);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void Resolve_FallsBackToTheMdlInternalName_WhenTheSiblingVvdIsAStub()
	{
		// 骨架在 survivor 上（内部名指向共享几何模型），几何只有一份在 shared/geom 上。
		var mdl = BuildMdl("shared/geom.mdl", 49, [new BoneSpec("root", -1)], []);
		var vpk = BuildVpk(1, 18,
		[
			new VpkFileSpec("mdl", "models/survivors", "survivor_x", [], mdl),
			new VpkFileSpec("vvd", "models/survivors", "survivor_x", [], StubVvd()),
			new VpkFileSpec("dx90.vtx", "models/survivors", "survivor_x", [], new byte[28]),
			new VpkFileSpec("vvd", "models/shared", "geom", [], RealVvd()),
			new VpkFileSpec("dx90.vtx", "models/shared", "geom", [], RealVtx()),
		]);

		WithArchive(vpk, archive =>
		{
			var files = SourceModelLocator.Resolve(archive, "survivor_x");
			Assert.Equal("models/survivors/survivor_x.mdl", files.Mdl.Path);
			Assert.Equal("models/shared/geom.vvd", files.Vvd.Path);
			Assert.Equal("models/shared/geom.dx90.vtx", files.Vtx.Path);
		});
	}

	[Fact]
	public void Resolve_PrefersTheSiblingFiles_WhenTheyAreReal()
	{
		var mdl = BuildMdl("survivors/survivor_x.mdl", 49, [new BoneSpec("root", -1)], []);
		var vpk = BuildVpk(1, 18,
		[
			new VpkFileSpec("mdl", "models/survivors", "survivor_x", [], mdl),
			new VpkFileSpec("vvd", "models/survivors", "survivor_x", [], RealVvd()),
			new VpkFileSpec("dx90.vtx", "models/survivors", "survivor_x", [], RealVtx()),
		]);

		WithArchive(vpk, archive =>
		{
			var files = SourceModelLocator.Resolve(archive, "survivor_x");
			Assert.Equal("models/survivors/survivor_x.vvd", files.Vvd.Path);
			Assert.Equal("models/survivors/survivor_x.dx90.vtx", files.Vtx.Path);
		});
	}

	[Fact]
	public void FindModel_SkipsZeroBoneShells_EvenWhenTheyAreTheLargest()
	{
		// 重打包工具会用"头里 0 根骨、body 原样"的 mdl 占位/封印，而且它常是最"大"的那个。
		var shell = BuildMdl("shared/shell.mdl", 49, [], [],
			Enumerable.Range(0, 64).Select(index => $"materials/filler{index}.vmt").ToArray());
		var real = BuildMdl("survivors/survivor_x.mdl", 49, [new BoneSpec("root", -1)], []);
		Assert.True(shell.Length > real.Length, "构造前提：壳必须比真模型大，否则这条测试证明不了任何事");

		var vpk = BuildVpk(1, 18,
		[
			new VpkFileSpec("mdl", "models/shared", "shell", [], shell),
			new VpkFileSpec("mdl", "models/survivors", "survivor_x", [], real),
		]);

		WithArchive(vpk, archive =>
			Assert.Equal("models/survivors/survivor_x.mdl", SourceModelLocator.FindModel(archive, null).Path));
	}

	[Fact]
	public void Resolve_FailsWithBothCandidatePaths_WhenNeitherWorks()
	{
		var mdl = BuildMdl("shared/geom.mdl", 49, [new BoneSpec("root", -1)], []);
		var vpk = BuildVpk(1, 18,
		[
			new VpkFileSpec("mdl", "models/survivors", "survivor_x", [], mdl),
			new VpkFileSpec("vvd", "models/survivors", "survivor_x", [], StubVvd()),
		]);

		WithArchive(vpk, archive =>
		{
			var error = Assert.Throws<InvalidDataException>(
				() => SourceModelLocator.Resolve(archive, "survivor_x"));
			Assert.Contains("models/survivors/survivor_x.vvd", error.Message);
			Assert.Contains("models/shared/geom.vvd", error.Message);
		});
	}
}
