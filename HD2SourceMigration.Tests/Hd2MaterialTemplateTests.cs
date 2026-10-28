using HD2SourceMigration.VpkConversion;

namespace HD2SourceMigration.Tests;

/// <summary>
/// 拿社区 SDK 自带的**真实** <c>*.material</c> 模板验材质读写。
/// <para>
/// 这比合成测试强得多，也正是 Python 侧 <c>_probe_matfix.py</c> 的做法：
/// 断言读写**逐字节回环**、断言槽位键与贴图 id 落在**原始字节偏移**上、
/// 断言前缀与后缀没被动过。合成数据证明不了真实模板的字段组合。
/// </para>
/// <para>找不到模板目录时整体跳过（它来自社区 SDK，不是每个 checkout 都有）。</para>
/// </summary>
public sealed class Hd2MaterialTemplateTests
{
	private static Hd2MaterialTemplateLibrary? Locate() => Hd2MaterialTemplateLibrary.Locate();

	/// <summary>
	/// 磁盘上找不到模板目录时必须用**程序集内置副本**，而不是返回 null。
	/// <para>
	/// 这条钉住一次真实事故：发布包里既没有 texconv 也没有 <c>*.material</c>，
	/// 所有用户的 VPK 材质转换都变成"转换 0 个"，而开发机上一切正常。
	/// 现在模板随程序集内置，转换不依赖任何外置目录。
	/// </para>
	/// <para>
	/// 测试从一个临时目录出发（它的祖先链里没有任何 HD2SDK materials），从而真正走到内嵌分支。
	/// </para>
	/// </summary>
	[Fact]
	public void EmbeddedTemplatesAreUsedWhenNoSdkDirectoryExists()
	{
		var empty = Path.Combine(Path.GetTempPath(), "hd2-no-templates-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(empty);
		try
		{
			var library = Hd2MaterialTemplateLibrary.Locate(explicitDirectory: null, startDirectory: empty);

			Assert.Equal("embedded", library.Source);
			Assert.Contains("advanced", library.Names);
			Assert.Contains("alphaclip", library.Names);
			// 内置副本也必须是能解析的真模板，不是占位空文件。
			Assert.True(library.LoadBytes("advanced").Length > 0);
			Assert.NotNull(library.Load("advanced"));
		}
		finally
		{
			try { Directory.Delete(empty, true); } catch (IOException) { }
		}
	}

	[Fact]
	public void EverySdkTemplateRoundTripsByteIdentically()
	{
		var library = Locate();
		if (library is null)
			return;

		Assert.NotEmpty(library.Names);
		foreach (var name in library.Names)
		{
			var original = library.LoadBytes(name);
			var material = Hd2MaterialAsset.Read(original);
			var rewritten = material.Write();

			// 逐字节回环：读→写必须完全复现。EndOffset 被重算、值区被重建、
			// 槽位键被改动，任何一处都会让这条红。
			Assert.Equal(original.Length, rewritten.Length);
			Assert.True(original.AsSpan().SequenceEqual(rewritten),
				$"模板 {name} 读写不是逐字节相同");
		}
	}

	/// <summary>
	/// 槽位键与贴图 id 必须落在<b>原始字节偏移</b>上：键区 <c>136 + 4i</c>、
	/// id 区 <c>136 + 4N + 8i</c>。按交错读会把除最后一条外的 id 全部错位，
	/// 而自洽的回环检测不到——所以这条必须直接看字节。
	/// </summary>
	[Fact]
	public void TextureKeysAndIdsSitAtTheirRawOffsetsInEveryTemplate()
	{
		var library = Locate();
		if (library is null)
			return;

		foreach (var name in library.Names)
		{
			var bytes = library.LoadBytes(name);
			var material = Hd2MaterialAsset.Read(bytes);
			var count = material.TextureIds.Count;
			Assert.Equal(material.TextureKeys.Count, count);
			Assert.True(count > 0, $"模板 {name} 一个槽都没有");

			for (var i = 0; i < count; i++)
			{
				Assert.Equal(
					material.TextureKeys[i],
					System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(136 + (4 * i))));
				Assert.Equal(
					material.TextureIds[i],
					System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(136 + (4 * count) + (8 * i))));
			}
		}
	}

	/// <summary>
	/// <c>EndOffset</c> 与文件长度之差必须是实测的那几个值（24/28/32/36）之一。
	/// 这条同时是"禁止重算 EndOffset"的反向证据：如果哪天有人按公式重算，
	/// 某些模板的差值会跳出这个集合。
	/// </summary>
	[Fact]
	public void EndOffsetDeficitStaysWithinTheMeasuredSet()
	{
		var library = Locate();
		if (library is null)
			return;

		var measured = new HashSet<long> { 24, 28, 32, 36 };
		foreach (var name in library.Names)
		{
			var bytes = library.LoadBytes(name);
			var material = Hd2MaterialAsset.Read(bytes);
			var deficit = bytes.Length - material.EndOffset;
			Assert.Contains(deficit, measured);
		}
	}

	/// <summary>
	/// 每个模板声明的父 id 都必须在已知全局父类表里。
	/// <para>
	/// 这条测试当天就抓出了表的缺口：<c>alphaclip+.material</c> 的父 id
	/// <c>0xc6042e3403385d40</c> 不在 Python 侧那张从 SDK <c>constants.py</c> 抄来的表里
	/// —— 那张表<b>不完整</b>。所以这条断言的作用是让"表落后于 SDK"这件事立刻可见，
	/// 而不是等到运行时把父 id 当未知值打印成 <c>0x...</c>。
	/// </para>
	/// </summary>
	[Fact]
	public void EveryTemplateDeclaresAKnownGlobalParent()
	{
		var library = Locate();
		if (library is null)
			return;

		var unknown = library.Names
			.Select(name => (Name: name, Material: library.Load(name)))
			.Where(item => !Hd2MaterialAsset.KnownParentIds.ContainsKey(item.Material.ParentMaterialId))
			.Select(item => $"{item.Name}=0x{item.Material.ParentMaterialId:x16}")
			.ToArray();

		Assert.True(unknown.Length == 0,
			$"这些模板的父 id 不在已知全局父类表里（需要补 Hd2MaterialAsset.KnownParentIds）：{string.Join("、", unknown)}");
	}

	/// <summary>
	/// 换掉一个槽的贴图 id 之后：<b>只有 id 区变</b>，前缀（头 + 键区）与后缀
	/// （id 区之后的所有内容）都必须逐字节不变。这是"只改 TexIDs"这条契约的判据。
	/// </summary>
	[Fact]
	public void ReplacingOneTextureLeavesPrefixAndSuffixUntouched()
	{
		var library = Locate();
		if (library is null || !library.Contains("advanced"))
			return;

		var original = library.LoadBytes("advanced");
		var material = Hd2MaterialAsset.Read(original);
		var count = material.TextureIds.Count;
		const ulong sentinel = 0x5A5A5A5A5A5A5A5A;

		material.SetTexture(1, sentinel);
		var rewritten = material.Write();

		Assert.Equal(original.Length, rewritten.Length);
		var keyEnd = 136 + (4 * count);
		// 前缀：头 + 键区。
		Assert.True(original.AsSpan(0, keyEnd).SequenceEqual(rewritten.AsSpan(0, keyEnd)));
		// 后缀：id 区之后。
		var idsEnd = keyEnd + (8 * count);
		Assert.True(original.AsSpan(idsEnd).SequenceEqual(rewritten.AsSpan(idsEnd)));
		// 换掉的那个槽真的变了，其余 id 没变。
		Assert.Equal(sentinel, Hd2MaterialAsset.Read(rewritten).TextureIds[1]);
		for (var i = 0; i < count; i++)
		{
			if (i != 1)
				Assert.Equal(material.TextureIds[i], Hd2MaterialAsset.Read(rewritten).TextureIds[i]);
		}
	}

	[Fact]
	public void LoadRefusesAnUnknownTemplateNameInsteadOfFallingBack()
	{
		var library = Locate();
		if (library is null)
			return;

		Assert.Throws<FileNotFoundException>(() => library.Load("no-such-template"));
	}
}
