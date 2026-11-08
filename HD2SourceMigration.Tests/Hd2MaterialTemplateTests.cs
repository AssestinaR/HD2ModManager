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
	/// **父 id 表必须与 SDK 的模板文件逐条吻合**，不只是"认得出来"。
	/// <para>
	/// 上面那条只验"父 id 在表里"，于是表里<b>写错名字</b>也照样通过：
	/// 实测 <c>basic+OLD</c> 的父 id <c>0x2f9c50d9129837fe</c> 被标成了 <c>basic+</c>，
	/// 而真正的 <c>basic+</c> 是 <c>0x7712ee7808e0ec50</c> —— 诊断打印因此把两个模板混成一个名字。
	/// </para>
	/// <para>
	/// 这条把"表"降级成"被验证的断言"：新增或改名模板时，要么改表、要么它红。
	/// 表的来源是 SDK（社区版 <c>constants.py</c> 加上模板二进制本身），不是我们的记忆。
	/// </para>
	/// </summary>
	[Fact]
	public void EveryTemplateParentIdMapsBackToItsOwnName()
	{
		var library = Locate();
		if (library is null)
			return;
		var mismatched = library.Names
			.Select(name => (Name: name, Material: library.Load(name)))
			.Select(item => new
			{
				item.Name,
				ParentId = item.Material.ParentMaterialId,
				Actual = Hd2MaterialAsset.KnownParentIds.TryGetValue(item.Material.ParentMaterialId, out var mapped)
					? mapped
					: "<不在表里>",
			})
			.Where(item => !string.Equals(item.Name, item.Actual, StringComparison.Ordinal))
			.Select(item => $"{item.Name} 的父 id 0x{item.ParentId:x16} 在表里写成「{item.Actual}」")
			.ToArray();

		Assert.True(mismatched.Length == 0,
			"父 id 表与 SDK 模板对不上（KnownParentIds 需要改正）：" + string.Join("；", mismatched));
	}

	/// <summary>反向检查：两个不同 id 不能共用一个模板名（诊断打印会混）。</summary>
	[Fact]
	public void NoTwoParentIdsShareTheSameName()
	{
		var duplicates = Hd2MaterialAsset.KnownParentIds
			.GroupBy(pair => pair.Value, StringComparer.Ordinal)
			.Where(group => group.Count() > 1)
			.Select(group => $"{group.Key}: " + string.Join("、", group.Select(pair => $"0x{pair.Key:x16}")))
			.ToArray();

		Assert.True(duplicates.Length == 0,
			"父 id 表里有重名（两个不同 id 用了同一个模板名）：" + string.Join("；", duplicates));
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

	/// <summary>
	/// <b><see cref="Hd2MaterialTemplateLibrary.HasTextureSlot"/> 必须能并发调用。</b>
	/// <para>
	/// 它被 <c>VpkPatchAssembler</c> 在 <c>Parallel.For</c> 里每个材质问一次
	/// （`VpkPatchAssembler.cs:652`）。缓存用普通 <c>Dictionary</c> 时，并发写会抛
	/// <c>Operations that change non-concurrent collections must have exclusive access</c>，
	/// 再被包成 "One or more errors occurred." —— 用户看到整批导入失败，
	/// 附一句跟材质毫无关系的英文。
	/// </para>
	/// <para>
	/// 实测代价（2026-11-03 管理器日志）：一次导入里 5 个包这样挂掉，其中一个还是模型主体。
	/// 这条测试用多线程反复问同一个模板，把那次事故钉住。
	/// </para>
	/// </summary>
	[Fact]
	public void HasTextureSlotIsSafeToCallFromManyThreads()
	{
		var library = Locate();
		if (library is null)
			return;

		var key = SourceMaterialConverter.MetallicSlotKey;
		var templates = new[] { "advanced", "alphaclip", "original", "basic", "translucent" };
		var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();

		// 关键：**从空缓存开始**并发写。先串行预热一次就测不出问题了。
		Parallel.For(0, 400, new ParallelOptions { MaxDegreeOfParallelism = 8 }, index =>
		{
			try
			{
				var name = templates[index % templates.Length];
				var has = library.HasTextureSlot(name, key);
				// 结果必须稳定：同一个模板无论问多少次都是同一个答案。
				if (string.Equals(name, "advanced", StringComparison.Ordinal))
					Assert.True(has);
				else
					Assert.False(has);
			}
			catch (Exception exception)
			{
				errors.Add(exception);
			}
		});

		Assert.True(errors.IsEmpty,
			"HasTextureSlot 并发调用抛异常：" + string.Join("；", errors.Select(error => error.GetType().Name + ": " + error.Message)));
	}

	/// <summary>
	/// <c>cutoutmetal</c> 是<b>从游戏根材质生成</b>的模板，不是 SDK 随包发的那些 ——
	/// 所以它的结构必须被钉住，否则"哪天谁改了一行生成脚本"会让整档材质悄悄错位。
	/// <para>
	/// 三个槽的顺序是**契约**：转换器按 <c>AlphaClipNormalSlot=0 / AlphaClipOpacitySlot=1 /
	/// AlphaClipBaseColorSlot=2</c> 写，而这个模板就是照这个顺序生成的（与 <c>alphaclip</c> 同名同序）。
	/// 顺序一错，底色会写进镂空槽、遮罩会写进金属槽 —— 症状是"整件东西穿了孔还发白"。
	/// </para>
	/// </summary>
	[Fact]
	public void CutoutMetalTemplateKeepsTheSlotOrderTheConverterWritesByIndex()
	{
		var library = Hd2MaterialTemplateLibrary.Locate();
		Assert.Contains("cutoutmetal", library.Names);

		var template = library.Load("cutoutmetal");

		// 父材质 = 游戏里的根材质 0xec555165044bb60a（BaseColorMetalMap + OpacityClipMap + …）。
		Assert.Equal(0xec555165044bb60aUL, template.ParentMaterialId);

		Assert.Equal(
			[
				0x1d57dcf3U, // NormalXyAoRoughMap
				0xcbde381bU, // OpacityClipMap
				0x848ba63bU, // BaseColorMetalMap
				0x13ce14bbU, // ParallaxMap（我们不用，留空）
			],
			template.TextureKeys.Select(key => key).ToArray());

		// 槽里的贴图 id 必须全空：模板只带着色器契约，贴图由每个材质自己填。
		Assert.All(template.TextureIds, id => Assert.Equal(0UL, id));

		// 变量表跟着根材质走（7 个）：这四个 *Opacity 是"哪张图的 alpha 参与不透明度"的开关。
		Assert.Equal(7, template.Variables.Count);
	}
}
