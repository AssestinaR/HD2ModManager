using System.Buffers.Binary;
using System.Text;
using HD2SourceMigration.VpkConversion;
using Xunit;

namespace HD2SourceMigration.Tests;

/// <summary>
/// 规划阶段的职责是<b>分类</b>，不是让整批导入失败。
/// <para>
/// 实测反馈：用户的一个角色包里有一个模型版本号不认识（v13617），结果整个包导入直接变成
/// <c>UnhandledError</c>——另外几十个本来能转的 VPK 也一起没了。<c>SourceModelReader</c>
/// 遇到不认识的版本抛的是 <see cref="NotSupportedException"/>，而规划器的 catch 只收了
/// <see cref="IOException"/> / <see cref="InvalidDataException"/> / <see cref="KeyNotFoundException"/>。
/// </para>
/// <para>
/// 这里用合成 VPK 把三条降级路径钉住：版本不认识、模型太小、几何缺失，都必须变成
/// "跳过这一个包 + 一句能看懂的理由"，而不是异常。
/// </para>
/// </summary>
public sealed class VpkImportPlannerTests : IDisposable
{
	private readonly List<string> temporaryFiles = [];

	public void Dispose()
	{
		foreach (var path in temporaryFiles)
		{
			try { File.Delete(path); }
			catch (IOException) { }
		}
	}

	[Fact]
	public void Plan_UnsupportedModelVersion_IsSkippedWithTheVersionInTheReason()
	{
		var vpk = WriteVpk(
			("survivor.mdl", "models/survivors", BuildMdl(version: 13617)),
			("survivor.vvd", "models/survivors", new byte[64]),
			("survivor.dx90.vtx", "models/survivors", new byte[64]));

		var plan = VpkImportPlanner.Plan([vpk]);

		var candidate = Assert.Single(plan.Candidates);
		Assert.False(candidate.IsConvertible);
		Assert.Equal(VpkImportKind.Unsupported, candidate.Kind);
		Assert.Contains("13617", candidate.SkipReason, StringComparison.Ordinal);
		// 理由必须带上具体是哪个文件，否则支持侧无从下手。
		Assert.Contains("survivor.mdl", candidate.SkipReason, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void Plan_TruncatedModel_IsSkippedInsteadOfThrowing()
	{
		var vpk = WriteVpk(
			("survivor.mdl", "models/survivors", Encoding.ASCII.GetBytes("IDST")),
			("survivor.vvd", "models/survivors", new byte[64]),
			("survivor.dx90.vtx", "models/survivors", new byte[64]));

		var plan = VpkImportPlanner.Plan([vpk]);

		var candidate = Assert.Single(plan.Candidates);
		Assert.False(candidate.IsConvertible);
		Assert.False(string.IsNullOrWhiteSpace(candidate.SkipReason));
	}

	[Fact]
	public void Plan_OneBadVpk_DoesNotStopTheOthersFromBeingClassified()
	{
		var badModel = WriteVpk(
			("survivor.mdl", "models/survivors", BuildMdl(version: 13617)),
			("survivor.vvd", "models/survivors", new byte[64]),
			("survivor.dx90.vtx", "models/survivors", new byte[64]));
		var material = WriteVpk(("white.vmt", "materials/lunalice", Encoding.UTF8.GetBytes("LightmappedGeneric")));

		var plan = VpkImportPlanner.Plan([badModel, material]);

		Assert.Equal(2, plan.Candidates.Count);
		Assert.Contains(plan.Candidates, item =>
			item.VpkPath.Equals(badModel, StringComparison.OrdinalIgnoreCase)
			&& item.SkipReason is not null
			&& item.SkipReason.Contains("13617", StringComparison.Ordinal));
	}

	[Fact]
	public void Plan_MaterialPackWithoutAnyModel_SaysToImportItTogetherWithTheModel()
	{
		// 单独导入一个"基础材质"包是常见用法，而材质必须被模型引用才有意义。
		// 这时的理由必须指向"少了模型包"，而不是含糊地说"没人引用"。
		var material = WriteVpk(("white.vmt", "materials/lunalice", Encoding.UTF8.GetBytes("LightmappedGeneric")));

		var plan = VpkImportPlanner.Plan([material]);

		var candidate = Assert.Single(plan.Candidates);
		Assert.False(candidate.IsConvertible);
		Assert.Contains("一起导入", candidate.SkipReason, StringComparison.Ordinal);
	}

	/// <summary>
	/// <c>.vvd</c>/<c>.vtx</c> 是 28 字节占位文件时，理由必须说"几何是占位、去用基础组件包"，
	/// 而不是把 <c>VVD magic 不是 IDSV</c> 甩给用户——magic 其实是对的，是文件里没有顶点。
	/// <para>
	/// 实测：<c>【少女前线2：追放】夏安</c> 一包 20 个 VPK 里有 9 个 survivor 包是这种形状，
	/// 日志上就是 9 行看不懂的 "VVD magic 不是 IDSV"。
	/// </para>
	/// </summary>
	[Fact]
	public void Plan_PlaceholderVvd_SaysTheGeometryIsAStubInsteadOfComplainingAboutTheMagic()
	{
		var vpk = WriteVpk(
			("survivor.mdl", "models/survivors", BuildMdl(version: 49)),
			("survivor.vvd", "models/survivors", BuildPlaceholderVvd()),
			("survivor.dx90.vtx", "models/survivors", new byte[28]));

		var plan = VpkImportPlanner.Plan([vpk]);

		var candidate = Assert.Single(plan.Candidates);
		Assert.False(candidate.IsConvertible);
		Assert.Contains("占位", candidate.SkipReason, StringComparison.Ordinal);
		Assert.Contains("28 字节", candidate.SkipReason, StringComparison.Ordinal);
		Assert.DoesNotContain("IDSV", candidate.SkipReason, StringComparison.Ordinal);
	}

	/// <summary>实测的占位 .vvd：IDSV + 版本 4 + 与 mdl 对得上的校验和 + lodCount=1，共 28 字节。</summary>
	private static byte[] BuildPlaceholderVvd()
	{
		var vvd = new byte[28];
		Encoding.ASCII.GetBytes("IDSV").CopyTo(vvd, 0);
		BinaryPrimitives.WriteInt32LittleEndian(vvd.AsSpan(4), 4);
		BinaryPrimitives.WriteInt32LittleEndian(vvd.AsSpan(8), 0x7537E69A);
		BinaryPrimitives.WriteInt32LittleEndian(vvd.AsSpan(12), 1);
		return vvd;
	}

	/// <summary>一个只有头部的 MDL：magic + 版本号 + 补零，够 408 字节的门槛。</summary>
	private static byte[] BuildMdl(int version)
	{
		var mdl = new byte[512];
		Encoding.ASCII.GetBytes("IDST").CopyTo(mdl, 0);
		BinaryPrimitives.WriteInt32LittleEndian(mdl.AsSpan(4), version);
		return mdl;
	}

	// ── 组件开关包（$no_draw 覆盖） ──

	private const string NoDrawVmt = "VertexLitGeneric\n{\n\t$no_draw 1\n\t$zero 0\n}\n";

	private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

	/// <summary>
	/// 开关包的判据来自实测样本：小、无贴图、每份 VMT 都只写 <c>$no_draw</c>。
	/// 识别它的意义是<b>不把它当材质包转</b>，并给出可读名字（<c>[猫耳关]</c> → <c>猫耳</c>）。
	/// </summary>
	[Fact]
	public void Plan_RecognisesADollarNoDrawSwitchPackAndNamesItFromTheAddonTitle()
	{
		var vpk = WriteVpk(
			("c_white_a_ear.vmt", "materials/mod/opt", Utf8(NoDrawVmt)),
			("addoninfo.txt", " ", Utf8("AddonInfo\n{\naddonTitle \"Manuka JABIS[猫耳关]\"\n}\n")));

		var plan = VpkImportPlanner.Plan([vpk]);
		var candidate = Assert.Single(plan.Candidates);

		Assert.True(candidate.Kind == VpkImportKind.ComponentSwitch, candidate.SkipReason ?? "没有理由");
		Assert.Equal("猫耳", candidate.DisplayName);
		Assert.Contains("c_white_a_ear", candidate.OwnedMaterialNames);
		// 它不产出 patch，但也不是"不支持"：理由要说清它被认成了开关。
		Assert.False(candidate.IsConvertible);
		Assert.Contains("组件开关", candidate.SkipReason!, StringComparison.Ordinal);
	}

	/// <summary>
	/// 有贴图的包<b>不是</b>开关包，哪怕它的 VMT 里也写了 <c>$no_draw</c>。
	/// 这条挡住的是"把一整个材质包误判成开关、于是材质一张都不写"。
	/// </summary>
	[Fact]
	public void Plan_DoesNotTreatAPackWithTexturesAsASwitch()
	{
		var vpk = WriteVpk(
			("c_white_a_ear.vmt", "materials/mod/opt", Utf8(NoDrawVmt)),
			("body.vtf", "materials/mod", new byte[512]));

		var plan = VpkImportPlanner.Plan([vpk]);
		var candidate = Assert.Single(plan.Candidates);

		Assert.NotEqual(VpkImportKind.ComponentSwitch, candidate.Kind);
	}

	/// <summary>
	/// 换贴图型的开关（实测 `文身关` / `切换纹身`）：不写 `$no_draw`，而是把 `$basetexture`
	/// 换回另一张图。它也不是"能用的材质包"——自身不带贴图，转出来会是一个没有任何 Unit
	/// 引用的材质，用户看到的是"一个什么都不做的 Mod"。
	/// </summary>
	[Fact]
	public void Plan_ReportsATextureSwapSwitchAsAMaterialVariantInsteadOfConvertingIt()
	{
		var vpk = WriteVpk(
			("manuka_body.vmt", "materials/mod/opt", Utf8(
				"\"VertexLitGeneric\"\n{\n\"$basetexture\" \"mod/Manuka_body\"\n}\n")),
			("addoninfo.txt", " ", Utf8("AddonInfo\n{\naddonTitle \"Manuka JABIS[文身关]\"\n}\n")));

		var candidate = Assert.Single(VpkImportPlanner.Plan([vpk]).Candidates);

		Assert.Equal(VpkImportKind.MaterialVariant, candidate.Kind);
		Assert.False(candidate.IsConvertible);
		Assert.Contains("表达不了", candidate.SkipReason!, StringComparison.Ordinal);
	}

	/// <summary>带贴图的材质包不能被误判成"换贴图型开关"，否则整包材质都不会写出来。</summary>
	[Fact]
	public void Plan_KeepsAPackWithTexturesAsAnOrdinaryMaterialPack()
	{
		var vpk = WriteVpk(
			("body.vmt", "materials/mod", Utf8("\"VertexLitGeneric\"\n{\n\"$basetexture\" \"mod/body\"\n}\n")),
			("body.vtf", "materials/mod", new byte[512]));

		var candidate = Assert.Single(VpkImportPlanner.Plan([vpk]).Candidates);

		// 这一批里没有模型，所以它仍然会被跳过——但跳过理由是"要跟模型一起导入"，
		// 而不是"这个开关表达不了"。两者是不同的诊断。
		Assert.Equal(VpkImportKind.Material, candidate.Kind);
		Assert.Contains("模型", candidate.SkipReason!, StringComparison.Ordinal);
	}
	/// <summary>一批里的开关包能被单独列出来（转换前要用它把解析集合剔干净）。</summary>
	[Fact]
	public void FindComponentSwitches_ReturnsOnlyTheSwitchPacks()
	{
		var switchPack = WriteVpk(
			("c_white_a_ear.vmt", "materials/mod/opt", Utf8(NoDrawVmt)),
			("addoninfo.txt", " ", Utf8("addoninfo\n{\naddontitle \"Manuka JABIS[猫耳关]\"\n}\n")));
		var materialPack = WriteVpk(
			("body.vmt", "materials/mod", Utf8("\"VertexLitGeneric\"\n{\n\"$basetexture\" \"mod/body\"\n}\n")),
			("body.vtf", "materials/mod", new byte[512]));

		var found = VpkImportPlanner.FindComponentSwitches([switchPack, materialPack]);

		var single = Assert.Single(found);
		Assert.Equal(Path.GetFullPath(switchPack), single.VpkPath);
		Assert.Equal("猫耳", single.DisplayName);
		Assert.Equal(["c_white_a_ear"], single.MaterialStems);
	}

	/// <summary>
	/// 名字解析要吃掉实测里出现过的四种写法。写错的表现是"开关名字变成一串文件路径"，
	/// 用户不会知道那是猫耳。
	/// </summary>
	[Theory]
	[InlineData("Manuka JABIS[猫耳关]", "x", "猫耳")]
	[InlineData("Manuka JABIS猫耳关", "x", "Manuka JABIS猫耳")]
	[InlineData("【VRM】橘雪莉 披风 关", "x", "橘雪莉 披风")]
	[InlineData("Manuka_LaFiore切换纹身", "x", "Manuka_LaFiore切换纹身")]
	[InlineData(null, "Manuka_JABIS猫耳关", "Manuka_JABIS猫耳")]
	[InlineData("", "Manuka_LaFiore切换纹身", "Manuka_LaFiore切换纹身")]   // 「切换」只在开头才算前缀
	public void DescribeSwitchName_HandlesTheShapesSeenInRealPacks(string? title, string fileName, string expected)
	{
		Assert.Equal(expected, VpkImportPlanner.DescribeSwitchName(title, fileName));
	}

	private string WriteVpk(params (string Name, string Directory, byte[] Content)[] files)
	{
		var path = Path.Combine(Path.GetTempPath(), "hd2-planner-" + Guid.NewGuid().ToString("N") + ".vpk");
		File.WriteAllBytes(path, BuildVpk(files));
		temporaryFiles.Add(path);
		return path;
	}

	/// <summary>VPK v1：12 字节头 + 目录树 + 数据区；条目 18 字节。</summary>
	private static byte[] BuildVpk((string Name, string Directory, byte[] Content)[] files)
	{
		const int headerSize = 12;
		const int entrySize = 18;

		var split = files.Select(file =>
		{
			var dot = file.Name.LastIndexOf('.');
			return (file.Directory, Extension: file.Name[(dot + 1)..], Stem: file.Name[..dot], file.Content);
		}).ToArray();

		var offsets = new int[split.Length];
		var payloads = new List<byte>();
		for (var index = 0; index < split.Length; index++)
		{
			offsets[index] = payloads.Count;
			payloads.AddRange(split[index].Content);
		}

		var tree = new List<byte>();
		foreach (var extensionGroup in split.Select((item, index) => (item, index)).GroupBy(pair => pair.item.Extension))
		{
			WriteString(tree, extensionGroup.Key);
			foreach (var directoryGroup in extensionGroup.GroupBy(pair => pair.item.Directory))
			{
				WriteString(tree, directoryGroup.Key);
				foreach (var (item, index) in directoryGroup)
				{
					WriteString(tree, item.Stem);
					var entry = new byte[entrySize];
					BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(4), 0);
					BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(6), 0x7FFF);
					BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(8), (uint)offsets[index]);
					BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(12), (uint)item.Content.Length);
					BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(16), 0xFFFF);
					tree.AddRange(entry);
				}

				WriteString(tree, string.Empty);
			}

			WriteString(tree, string.Empty);
		}

		WriteString(tree, string.Empty);

		var result = new byte[headerSize + tree.Count + payloads.Count];
		BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0), 0x55AA1234);
		BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), 1);
		BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), (uint)tree.Count);
		tree.CopyTo(result, headerSize);
		payloads.CopyTo(result, headerSize + tree.Count);
		return result;
	}

	private static void WriteString(List<byte> buffer, string value)
	{
		buffer.AddRange(Encoding.UTF8.GetBytes(value));
		buffer.Add(0);
	}
}
