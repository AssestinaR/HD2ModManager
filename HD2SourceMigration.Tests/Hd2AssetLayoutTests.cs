using System.Buffers.Binary;
using HD2SourceMigration.VpkConversion;

namespace HD2SourceMigration.Tests;

/// <summary>
/// HD2 <c>Material</c> / <c>Texture</c> 资产的字节布局契约。
/// <para>
/// 断言抄自 Python 侧的独立审查脚本 <c>tmp/rig-lab/_probe_matfix.py</c>（18 条）与
/// <c>review_round60_contract.py</c>（9 条），它们**不引用生产代码**，只按原始字节偏移验。
/// </para>
/// <para>
/// <b>为什么必须有"负面对照"那一条。</b>材质贴图表是两段连续数组
/// （<c>u32 键×N</c> 然后 <c>u64 id×N</c>），不是 N 条交错的 <c>(u32,u64)</c> 记录。
/// 读成交错只会让<b>除最后一条以外</b>的所有 id 位移——而<b>自洽的读写回环完全检测不到</b>，
/// 因为写和读用的是同一个错误假设。Python 侧因此把错误版本冻结成 <c>_legacy_matwriter.py</c> 留档。
/// 这里用互不相同的哨兵 id 把这条钉死：只要有人把偏移改成交错，下面的测试立刻红。
/// </para>
/// </summary>
public sealed class Hd2AssetLayoutTests
{
	/// <summary>键区起点。SDK <c>stingray/material.py</c> 与 C# `StingrayMaterialReferenceReader` 一致。</summary>
	private const int TextureKeyOffset = 136;

	/// <summary>造一个带互异哨兵 id 的材质：N=3，键与 id 都互不相同，错位不可能碰巧正确。</summary>
	private static Hd2MaterialAsset BuildSentinelMaterial() => new()
	{
		Undat1 = new byte[12],
		EndOffset = 0xDEADBEEF,
		Undat2 = 0,
		ParentMaterialId = 17265463703140804126,   // advanced
		Undat3 = new byte[32],
		Undat4 = new byte[36],
		Undat5 = new byte[12],
		Undat6 = new byte[12],
		VariableDataSize = 0,
		TextureKeys = [0x0f5ff78d, 0xa17b45a8, 0xe67ac0c7],
		TextureIds =
		[
			0x1111111111111111UL,
			0x2222222222222222UL,
			0x3333333333333333UL,
		],
		Variables = [],
		ValueArea = [],
	};

	[Fact]
	public void Material_WriteRoundTripsByteIdentically()
	{
		var material = BuildSentinelMaterial();
		var bytes = material.Write();
		var reread = Hd2MaterialAsset.Read(bytes);
		Assert.Equal(bytes, reread.Write());
		Assert.Equal(material.TextureKeys, reread.TextureKeys);
		Assert.Equal(material.TextureIds, reread.TextureIds);
		Assert.Equal(material.EndOffset, reread.EndOffset);
		Assert.Equal(material.ParentMaterialId, reread.ParentMaterialId);
		Assert.Equal("advanced", reread.ParentName);
	}

	[Fact]
	public void Material_TextureTableIsTwoContiguousArraysAtTheirRawOffsets()
	{
		var material = BuildSentinelMaterial();
		var bytes = material.Write();
		var count = material.TextureIds.Count;   // 3

		// 键区：136 + 4*i
		for (var i = 0; i < count; i++)
			Assert.Equal(material.TextureKeys[i], BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(TextureKeyOffset + (4 * i))));

		// id 区：136 + 4*N + 8*i —— 这才是正确偏移，不是 136 + 8*i。
		for (var i = 0; i < count; i++)
			Assert.Equal(material.TextureIds[i], BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(TextureKeyOffset + (4 * count) + (8 * i))));
	}

	/// <summary>
	/// 负面对照：把同一段字节按<b>交错</b>读，必须得到不同的 id。
	/// 若哪天有人把布局改成/读成交错，这条会失败——这正是它的用途。
	/// 只有最后一条 id 可能碰巧正确，所以断言前 N-1 条必然不同。
	/// </summary>
	[Fact]
	public void Material_InterleavedLayoutIsDetectablyWrongForEverySlotButTheLast()
	{
		var material = BuildSentinelMaterial();
		var bytes = material.Write();
		var count = material.TextureIds.Count;

		var wrong = 0;
		for (var i = 0; i < count - 1; i++)
		{
			var interleaved = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(TextureKeyOffset + (8 * i)));
			if (interleaved != material.TextureIds[i])
				wrong++;
		}

		Assert.Equal(count - 1, wrong);
	}

	/// <summary>
	/// <c>EndOffset</c> 必须逐字节复制、<b>绝不重算</b>。实测 10 个 SDK 模板的 <c>size − EndOffset</c>
	/// 有 24/28/32/36 四种值，任何 <c>136 + 12N + 20M</c> 公式都会静默改坏头部后那一个字节。
	/// </summary>
	[Fact]
	public void Material_EndOffsetIsCopiedVerbatimAndIsNotDerivedFromSize()
	{
		var material = BuildSentinelMaterial();
		material.EndOffset = 0x12345678;
		var bytes = material.Write();
		Assert.Equal(0x12345678u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)));

		// 与"按长度推出来"的值刻意不同，确保实现没有偷偷重算。
		var derived = (uint)(bytes.Length - 36);
		Assert.True(derived != material.EndOffset, $"EndOffset 被重算成了 size-36（{derived}）");
		Assert.Equal(0x12345678u, Hd2MaterialAsset.Read(bytes).EndOffset);
	}

	/// <summary>只能替换已存在的槽；凭空造槽会得到游戏解析不了的材质，所以必须越界报错。</summary>
	[Fact]
	public void Material_SetTextureRefusesToInventASlot()
	{
		var material = BuildSentinelMaterial();
		material.SetTexture(2, 0x4444444444444444UL);
		Assert.Equal(0x4444444444444444UL, material.TextureIds[2]);
		Assert.Equal(3, material.TextureKeys.Count);
		Assert.Throws<ArgumentOutOfRangeException>(() => material.SetTexture(3, 1));
		Assert.Throws<ArgumentOutOfRangeException>(() => material.SetTexture(-1, 1));
	}

	[Fact]
	public void Material_RejectsMismatchedTextureKeyAndIdCounts()
	{
		var material = BuildSentinelMaterial();
		material.TextureIds = [0x1, 0x2];   // 键 3 个、id 2 个
		Assert.Throws<InvalidDataException>(() => material.Write());
	}

	[Fact]
	public void Texture_TocIs340BytesWithTheMipmapTableCleared()
	{
		var texture = new Hd2TextureAsset { UnkId = 0x1234 };
		texture.FromDds(Hd2TextureAsset.BuildDds(4, 4, Hd2TextureAsset.Bc7Unorm, [new byte[16]]));

		var toc = texture.WriteToc();
		Assert.Equal(340, toc.Length);
		Assert.Equal(0x1234u, BinaryPrimitives.ReadUInt32LittleEndian(toc));
		Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(toc.AsSpan(4)));
		Assert.Equal(0xFFFFFFFFu, BinaryPrimitives.ReadUInt32LittleEndian(toc.AsSpan(8)));

		// 15 条 StingrayMipmapInfo(12B) 必须整段清零，与 SDK 的写法一致。
		for (var i = 12; i < 12 + (15 * 12); i++)
			Assert.Equal(0, toc[i]);
	}

	[Fact]
	public void Texture_ReadRejectsADdsHeaderWithoutTheDx10Extension()
	{
		var texture = new Hd2TextureAsset();
		texture.FromDds(Hd2TextureAsset.BuildDds(8, 8, Hd2TextureAsset.Bc7UnormSrgb, [new byte[64]]));
		var toc = texture.WriteToc();
		Assert.Equal(340, toc.Length);

		// 抹掉 "DX10" 四字节后必须拒绝——没有它就拿不到 dxgiFormat，后续格式判断全会错。
		var broken = toc.ToArray();
		broken[192 + 84] = (byte)'D';
		broken[192 + 85] = (byte)'X';
		broken[192 + 86] = (byte)'9';
		broken[192 + 87] = (byte)'9';
		Assert.Throws<InvalidDataException>(() => Hd2TextureAsset.Read(broken));
	}

	[Fact]
	public void Texture_DdsHeaderCarriesTheExpectedFields()
	{
		var pixels = new byte[16];   // 1×1 的 BC7 也是整整一个 16 字节块
		var dds = Hd2TextureAsset.BuildDds(1, 1, Hd2TextureAsset.Bc7UnormSrgb, [pixels]);
		Assert.Equal(148 + 16, dds.Length);
		Assert.Equal("DDS ", System.Text.Encoding.ASCII.GetString(dds, 0, 4));
		Assert.Equal(124u, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(4)));
		Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(12)));   // height
		Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(16)));   // width
		Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(28)));   // mipCount = 1
		Assert.Equal("DX10", System.Text.Encoding.ASCII.GetString(dds, 84, 4));
		Assert.Equal(Hd2TextureAsset.Bc7UnormSrgb, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(128)));
		Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(132)));  // TEXTURE2D
		Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(140)));  // arraySize
	}

	[Fact]
	public void Texture_OneByOneBc7StillOccupiesAFullBlock()
	{
		// 按 w*h 估算长度会算成 0；必须按块数算，1×1 也是 16 字节。
		var dds = Hd2TextureAsset.BuildDds(1, 1, Hd2TextureAsset.Bc7Unorm, [new byte[16]]);
		var texture = Hd2TextureAsset.Read(
			new Hd2TextureAsset { DdsHeader = dds[..148] }.WriteToc(),
			dds[148..]);
		Assert.Equal(1, texture.Width);
		Assert.Equal(1, texture.Height);
		Assert.Equal(16, texture.Pixels.Length);
		Assert.Equal("BC7_UNORM", texture.FormatName);
	}
}
