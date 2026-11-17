using System.Buffers.Binary;
using System.Text;
using HD2SourceMigration.VpkConversion;
using Xunit;

namespace HD2SourceMigration.Tests;

/// <summary>
/// 针对原生 <c>VertexLitGeneric</c> 角色模组（如 <c>3621723720</c>）四类还原缺陷的端到端合成回归测试：
/// <list type="number">
/// <item><c>$additive 1</c> 全黑待机帧转为 <c>alphaclip</c> 全零遮罩（不再渲染成实心黑片）；</item>
/// <item><c>FullyTransparent</c>（≥95% 像素透明）正确选用 <c>translucent</c> 模板并写入 <c>BaseColor</c>（不再报错退成兜底灰）；</item>
/// <item>多材质共用同一张底图时，<c>$color2</c> 与 <c>$lightwarptexture</c> 产出各自独立的贴图 id 与真实色偏；</item>
/// <item>底图来自外部纯贴图换色包（0 个 VMT）时，保持原贴图名不改名烘死，确保换色包同号互换不受影响。</item>
/// </list>
/// </summary>
public sealed class VertexLitGenericFidelityTests : IDisposable
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
	public void AdditiveBlackFrameConvertsToInvisibleAlphaClipInsteadOfOpaqueBlack()
	{
		var encoderPath = Bc7Encoder.Locate(Environment.GetEnvironmentVariable("HD2_TEXCONV"));
		var templates = Hd2MaterialTemplateLibrary.Locate();
		Assert.NotNull(encoderPath);
		Assert.NotNull(templates);

		var blackVtf = BuildRgbaVtf(4, 4, (_, _) => (0, 0, 0, 255));
		var vmt = Encoding.UTF8.GetBytes("""
			"VertexlitGeneric"
			{
				"$basetexture" "models/test/flash_1"
				"$color2" "[0.82 0.75 1]"
				"$additive" "1"
			}
			""");
		var vpk = WriteVpk("additive",
			("flash_1.vmt", "materials/models/test", vmt),
			("flash_1.vtf", "materials/models/test", blackVtf));

		var encoder = new Bc7Encoder(encoderPath);
		var converter = new SourceMaterialConverter(SourceMaterialSource.Open([vpk]), encoder, templates);
		var result = converter.Convert("flash_1");

		Assert.True(result.IsValid, string.Join(" / ", result.Diagnostics));
		Assert.Equal("alphaclip", result.Material!.TemplateName);

		var maskId = result.Material.TextureIds[SourceMaterialConverter.AlphaClipOpacitySlot];
		var mask = result.Textures.Single(t => t.Id == maskId);
		var decodedMask = encoder.DecodeToRgba(
			Hd2TextureAsset.BuildDds(mask.Width, mask.Height, Hd2TextureAsset.Bc7Unorm, [mask.GpuData]));
		Assert.All(Enumerable.Range(0, decodedMask.Length / 4), idx => Assert.Equal(0, decodedMask[idx * 4]));
	}

	[Fact]
	public void EffectivelyTransparentMaterialLoadsTranslucentTemplateWithoutFallingBack()
	{
		var encoderPath = Bc7Encoder.Locate(Environment.GetEnvironmentVariable("HD2_TEXCONV"));
		var templates = Hd2MaterialTemplateLibrary.Locate();
		Assert.NotNull(encoderPath);
		Assert.NotNull(templates);

		// 16×16 = 256 像素，仅 1 个像素不透明（>99% 透明，且 Max=255 故 IsEmpty=false、IsEffectivelyTransparent=true）。
		var mostlyClearVtf = BuildRgbaVtf(16, 16, (x, y) => x == 0 && y == 0
			? ((byte)220, (byte)200, (byte)255, (byte)255)
			: ((byte)220, (byte)200, (byte)255, (byte)0));
		var vmt = Encoding.UTF8.GetBytes("""
			"VertexlitGeneric"
			{
				"$basetexture" "models/test/faceeffect_excited_2"
				"$translucent" "1"
			}
			""");
		var vpk = WriteVpk("translucent",
			("faceeffect_excited_2.vmt", "materials/models/test", vmt),
			("faceeffect_excited_2.vtf", "materials/models/test", mostlyClearVtf));

		var converter = new SourceMaterialConverter(
			SourceMaterialSource.Open([vpk]), new Bc7Encoder(encoderPath), templates);
		var result = converter.Convert("faceeffect_excited_2");

		Assert.True(result.IsValid, string.Join(" / ", result.Diagnostics));
		Assert.Equal("translucent", result.Material!.TemplateName);
		Assert.Single(result.Textures);
	}

	[Fact]
	public void Color2AndLightwarpSeparateSharedBaseTextureAndPreservePureTexturePackSwapping()
	{
		var encoderPath = Bc7Encoder.Locate(Environment.GetEnvironmentVariable("HD2_TEXCONV"));
		var templates = Hd2MaterialTemplateLibrary.Locate();
		Assert.NotNull(encoderPath);
		Assert.NotNull(templates);

		var sharedAtlas = BuildRgbaVtf(4, 4, (_, _) => (200, 200, 200, 255));
		var violetWarp = BuildRgbaVtf(16, 4, (x, _) =>
		{
			var t = x / 15.0;
			return (
				(byte)Math.Round(40 + (60 * t)),
				(byte)Math.Round(40 + (60 * t)),
				(byte)Math.Round(70 + (80 * t)),
				(byte)255);
		});

		var hairVmt = Encoding.UTF8.GetBytes("""
			"VertexlitGeneric"
			{
				"$basetexture" "models/test/shared_base"
				"$color2" "[0.85 0.85 1.0]"
				"$lightwarptexture" "models/test/violet_warp"
			}
			""");
		var metalVmt = Encoding.UTF8.GetBytes("""
			"VertexlitGeneric"
			{
				"$basetexture" "models/test/shared_base"
				"$color2" "[0.5 0.5 0.5]"
			}
			""");

		var singlePack = WriteVpk("single",
			("hair_mat.vmt", "materials/models/test", hairVmt),
			("metal_mat.vmt", "materials/models/test", metalVmt),
			("shared_base.vtf", "materials/models/test", sharedAtlas),
			("violet_warp.vtf", "materials/models/test", violetWarp));

		var encoder = new Bc7Encoder(encoderPath);
		var converter = new SourceMaterialConverter(SourceMaterialSource.Open([singlePack]), encoder, templates);
		var hair = converter.Convert("hair_mat");
		var metal = converter.Convert("metal_mat");

		Assert.True(hair.IsValid);
		Assert.True(metal.IsValid);

		var hairTex = hair.Textures[0];
		var metalTex = metal.Textures[0];
		Assert.NotEqual(hairTex.Id, metalTex.Id);

		var hairPixels = encoder.DecodeToRgba(
			Hd2TextureAsset.BuildDds(hairTex.Width, hairTex.Height, Hd2TextureAsset.Bc7UnormSrgb, [hairTex.GpuData]));
		// 紫发：蓝通道显著高于红绿通道，且蓝通道保持原明度附近（~200）。
		Assert.InRange(hairPixels[2], 195, 205);
		Assert.True(hairPixels[2] - hairPixels[0] >= 25, $"期望明显偏蓝紫，实际 RGB=({hairPixels[0]},{hairPixels[1]},{hairPixels[2]})");

		// 当底图由外部纯贴图换色包（0 个 VMT）提供时，材质包不改写贴图裸名，保证换色包同号替换有效。
		var vmtPack = WriteVpk("vmt_only", ("metal_mat.vmt", "materials/models/test", metalVmt));
		var pureTexPack = WriteVpk("tex_only", ("shared_base.vtf", "materials/models/test", sharedAtlas));
		var scoped = SourceMaterialSource.Open([vmtPack, pureTexPack]).ScopedTo(vmtPack);
		var scopedConverter = new SourceMaterialConverter(scoped, encoder, templates);
		var scopedMetal = scopedConverter.Convert("metal_mat");
		Assert.True(scopedMetal.IsValid);
		Assert.Equal("shared_base", scopedMetal.Textures[0].SourcePath);
	}

	private string WriteVpk(string stem, params (string Name, string Directory, byte[] Content)[] files)
	{
		var path = Path.Combine(Path.GetTempPath(), $"hd2fidelity-{stem}-{Guid.NewGuid():N}.vpk");
		File.WriteAllBytes(path, BuildVpk(files));
		temporaryFiles.Add(path);
		return path;
	}

	private static byte[] BuildRgbaVtf(int width, int height, Func<int, int, (byte R, byte G, byte B, byte A)> pixel)
	{
		const int headerSize = 80;
		var payload = new byte[width * height * 4];
		for (var y = 0; y < height; y++)
		{
			for (var x = 0; x < width; x++)
			{
				var (r, g, b, a) = pixel(x, y);
				var at = ((y * width) + x) * 4;
				payload[at + 0] = r;
				payload[at + 1] = g;
				payload[at + 2] = b;
				payload[at + 3] = a;
			}
		}

		var bytes = new byte[headerSize + payload.Length];
		bytes[0] = (byte)'V';
		bytes[1] = (byte)'T';
		bytes[2] = (byte)'F';
		bytes[3] = 0;
		BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 7);
		BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 2);
		BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), headerSize);
		BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(16), (ushort)width);
		BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18), (ushort)height);
		BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(24), 1);
		BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(52), 0); // RGBA8888
		bytes[56] = 1; // mipCount = 1
		bytes[57] = 0xFF; // no thumbnail
		BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(63), 1); // depth = 1
		payload.CopyTo(bytes.AsSpan(headerSize));
		return bytes;
	}

	private static byte[] BuildVpk((string Name, string Directory, byte[] Content)[] files)
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
			payloads.AddRange(files[index].Content);
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
					BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)files[index].Content.Length);
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
