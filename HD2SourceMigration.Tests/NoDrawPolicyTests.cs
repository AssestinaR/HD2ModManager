using System.Buffers.Binary;
using System.Text;
using HD2SourceMigration.VpkConversion;

namespace HD2SourceMigration.Tests;

/// <summary>
/// 源侧 <c>$no_draw</c>（"这一块不画"）怎么落地。
/// <para>
/// <b>为什么要有三档。</b>L4D2 里 <c>$no_draw</c> 只是<b>材质层</b>开关：VMT 写了不让画，
/// 网格仍在文件里，所以作者拿它做变体（猫耳/无猫耳）。移植时"丢掉几何"虽然忠实，
/// 但把数据一起删了——导入之后只能改包重导。实测 L4D2 2829646325（Coach-Ayame）的
/// <c>cat</c> / <c>facial</c> / <c>basebody2</c> 都是这种桩，合计 −6,598 个三角形。
/// </para>
/// <para>这里钉住三件事：默认档位、三档下转换器给出的"跳过/不跳过"、以及不显示材质的形状。</para>
/// </summary>
public sealed class NoDrawPolicyTests : IDisposable
{
	private readonly List<string> temporaryFiles = [];

	public void Dispose()
	{
		foreach (var path in temporaryFiles)
		{
			try { File.Delete(path); } catch (IOException) { }
		}
	}

	[Fact]
	public void DefaultPolicyIsShowSoHiddenPartsStayVisible()
	{
		var converter = CreateConverter(SourceMaterialSource.Open([]));

		// 默认是 Show：源侧的 $no_draw 只是材质层开关（网格仍在文件里），
		// 导入时替用户藏掉是不可逆的；"要不要看"用 section 组件编辑器随时改。
		Assert.Equal(NoDrawPolicy.Show, converter.NoDrawPolicy);
	}

	[Fact]
	public void HideSkipsTheMaterial()
	{
		var converter = CreateConverter(SingleMaterial("cat", """
			VertexLitGeneric
			{
				$no_draw 1
				$nodecal 1
			}
			"""), NoDrawPolicy.Hide);

		var result = converter.Convert("cat");

		Assert.True(result.IsSkipped);
		Assert.Contains("$no_draw", result.SkipReason!, StringComparison.Ordinal);
	}

	[Fact]
	public void ShowLetsTheMaterialThrough()
	{
		var converter = CreateConverter(SingleMaterial("cat", """
			VertexLitGeneric
			{
				$no_draw 1
				$nodecal 1
			}
			"""), NoDrawPolicy.Show);

		var result = converter.Convert("cat");

		// **不是跳过**就够了：它会走到"没有底色"那条正常路径（这里必然失败，因为
		// 夹具里没有贴图）。关键是 `--no-draw show` 之后这块**有机会**被画出来。
		Assert.False(result.IsSkipped);
		Assert.Null(result.Material);
		Assert.Contains("$basetexture", string.Join("；", result.Diagnostics), StringComparison.Ordinal);
	}

	[Fact]
	public void ASyntheticNoDrawMaterialIsAnAlphaClipThatClipsEverything()
	{
		var converter = CreateConverter(SourceMaterialSource.Open([]));
		if (!TexconvAvailable())
			return;

		var result = converter.SynthesizeNoDrawMaterial("cat");

		Assert.True(result.IsValid);
		var material = result.Material!;
		// 只能用 alphaclip：advanced 的 SclarFieldOpacity 是 1×1 标量，做不了"整段裁掉"。
		Assert.Equal("alphaclip", material.TemplateName);
		// 遮罩 + 平面法线，两张；遮罩是所有 $no_draw 材质**共用**的（id 固定 ⇒ 只写一份）。
		Assert.Equal(2, result.Textures.Count);
		Assert.Contains(result.Notices, notice => notice.Contains("不显示", StringComparison.Ordinal));
	}

	[Fact]
	public void TheNoDrawMaterialHasItsOwnIdentitySoShowAndHideDoNotCollide()
	{
		var converter = CreateConverter(SourceMaterialSource.Open([]));
		if (!TexconvAvailable())
			return;

		var hidden = converter.SynthesizeNoDrawMaterial("cat");
		var fallback = converter.SynthesizeFallbackMaterial("cat");

		Assert.True(hidden.IsValid && fallback.IsValid);
		// 同一个 stem 的"不显示"材质与"兜底灰"材质必须是两个 id：
		// 否则 `--no-draw show` 转出来的产物会和 hide 的撞在同一个 id 上。
		Assert.NotEqual(hidden.Material!.Id, fallback.Material!.Id);
	}

	private static bool TexconvAvailable()
		=> Bc7Encoder.Locate(Environment.GetEnvironmentVariable("HD2_TEXCONV")) is not null;

	private static SourceMaterialConverter CreateConverter(
		SourceMaterialSource source, NoDrawPolicy policy = NoDrawPolicy.Show)
	{
		var encoder = Bc7Encoder.Locate(Environment.GetEnvironmentVariable("HD2_TEXCONV"))
			?? Path.Combine(Path.GetTempPath(), "texconv.exe");
		return new SourceMaterialConverter(source, new Bc7Encoder(encoder),
			Hd2MaterialTemplateLibrary.Locate(), noDrawPolicy: policy);
	}

	/// <summary>一个只含这份 VMT 的 VPK 视图。</summary>
	private SourceMaterialSource SingleMaterial(string stem, string vmt)
		=> SourceMaterialSource.Open([WriteVpk(stem, (stem + ".vmt", "materials/ayame", vmt))]);

	private string WriteVpk(string stem, params (string Name, string Directory, string Content)[] files)
	{
		var path = Path.Combine(Path.GetTempPath(), $"hd2nodraw-{stem}-{Guid.NewGuid():N}.vpk");
		File.WriteAllBytes(path, BuildVpk(files));
		temporaryFiles.Add(path);
		return path;
	}

	/// <summary>
	/// VPK v1（18 字节条目）：扩展名 → 目录 → 文件名三层嵌套，各自空串终止，
	/// 条目头后面紧跟 preload 字节。布局必须与 <c>VpkArchive</c> 的读法一致。
	/// </summary>
	private static byte[] BuildVpk((string Name, string Directory, string Content)[] files)
	{
		const int headerSize = 12;
		const int entrySize = 18;

		var payloads = new List<byte>();
		var offsets = new int[files.Length];
		var split = new (string Extension, string Directory, string Stem)[files.Length];
		for (var index = 0; index < files.Length; index++)
		{
			var name = files[index].Name;
			var dot = name.LastIndexOf('.');
			split[index] = (
				dot < 0 ? " " : name[(dot + 1)..],
				files[index].Directory,
				dot < 0 ? name : name[..dot]);
			offsets[index] = payloads.Count;
			payloads.AddRange(Encoding.UTF8.GetBytes(files[index].Content));
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
					var header = new byte[entrySize];
					BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), 0);
					BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), 0x7FFF);
					BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)offsets[index]);
					BinaryPrimitives.WriteUInt32LittleEndian(
						header.AsSpan(12), (uint)Encoding.UTF8.GetByteCount(files[index].Content));
					BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(16), 0xFFFF);
					tree.AddRange(header);
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
