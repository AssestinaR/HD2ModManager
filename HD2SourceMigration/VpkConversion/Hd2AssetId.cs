using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace HD2SourceMigration.VpkConversion;

/// <summary>
/// 自造资产 id 的派生。移植自 <c>tmp/rig-lab/build_patch.py:176-180</c>
/// （副本见 <c>fix_materials.py:94-98</c>、<c>fix_appearance.py:102-106</c>）。
/// <para>
/// <c>id = (prefix &lt;&lt; 60) | (sha256(name)[0..8] 按小端读成 u64 &amp; 0x0FFF_FFFF_FFFF_FFFF)</c>
/// </para>
/// <para>
/// <b>必须与 Python 逐位一致</b>：材质与贴图的 id 就是它们互相引用的键，
/// 差一位就变成"引用了不存在的资产"，而那种错在包里看不出来、只在游戏里表现为白模。
/// </para>
/// <para>
/// ⚠️ <b>命名不对称是有意的，也是已知隐患</b>：材质 id 用 <c>$basetexture</c> 的
/// <b>全路径</b>，贴图 id 只用 <b>basename</b>（<c>MATERIAL_CONFLICT_ANALYSIS.md:20-34</c>）。
/// 后者的后果是不同目录下同名的贴图会撞同一个 id。这里照抄现状，但调用方应当知道这件事。
/// </para>
/// </summary>
public static class Hd2AssetId
{
	/// <summary>材质资产的高 4 位。</summary>
	public const ulong MaterialPrefix = 0xA;

	/// <summary>贴图资产的高 4 位。</summary>
	public const ulong TexturePrefix = 0xB;

	/// <summary>Unit 资产的高 4 位。</summary>
	public const ulong UnitPrefix = 0xD;

	/// <summary>低 60 位的掩码。</summary>
	public const ulong PayloadMask = 0x0FFF_FFFF_FFFF_FFFF;

	public static ulong Derive(ulong prefix, string name)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		if (prefix > 0xF)
			throw new ArgumentOutOfRangeException(nameof(prefix), "高 4 位只能放 0..0xF。");

		var digest = SHA256.HashData(Encoding.UTF8.GetBytes(name));
		// 取前 8 字节按**小端**读成 u64 —— 与 Python 的 int.from_bytes(digest[:8], 'little') 一致。
		var payload = BinaryPrimitives.ReadUInt64LittleEndian(digest) & PayloadMask;
		return (prefix << 60) | payload;
	}

	/// <summary>材质 id：名字用 <c>$basetexture</c> 的<b>全路径</b>。</summary>
	public static ulong Material(string baseTexturePath) => Derive(MaterialPrefix, baseTexturePath);

	/// <summary>贴图 id：名字只用 <b>basename</b>（Python 侧的现状，见类型注释里的隐患）。</summary>
	public static ulong Texture(string textureBaseName) => Derive(TexturePrefix, textureBaseName);

	/// <summary>取出高 4 位，用来判断一个 id 大概是哪类资产。</summary>
	public static ulong PrefixOf(ulong id) => id >> 60;
}
