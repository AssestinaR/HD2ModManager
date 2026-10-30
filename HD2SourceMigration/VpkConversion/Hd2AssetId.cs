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
/// <para>
/// <b>命名空间（<c>idNamespace</c>）就是为了补上这条隐患。</b>没有它时，任何两个 VPK Mod 里
/// 叫 <c>hair</c> 的贴图都是同一个 id——实测两个不同作者的包（<c>EkU/ICSUT/Hair</c> 4096² DXT5
/// 与 <c>Milfy_Maid/Hair</c> 2048² DXT1）算出的 id 完全相同，两个 Mod 同时装上就只有一个能生效，
/// 另一个 Mod 的头发会采样到别人的图集（用户报的"头发上散落一堆小头发贴图"）。
/// 加上命名空间后，同一个名字在不同来源里得到不同 id，两个 Mod 可以共存。
/// </para>
/// <para>
/// <b>命名空间只在"一次导入"这一层统一</b>：同批的模型包与材质包必须用同一个 token，
/// 否则模型 patch 引用的材质 id 与材质 patch 写出的对不上。不传（<c>null</c>）时
/// 派生结果与历史版本<b>逐位相同</b>——旧产物、Python 对拍、已有测试都不受影响。
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

	public static ulong Derive(ulong prefix, string name, string? idNamespace = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		if (prefix > 0xF)
			throw new ArgumentOutOfRangeException(nameof(prefix), "高 4 位只能放 0..0xF。");

		var digest = SHA256.HashData(Encoding.UTF8.GetBytes(Scope(name, idNamespace)));
		// 取前 8 字节按**小端**读成 u64 —— 与 Python 的 int.from_bytes(digest[:8], 'little') 一致。
		var payload = BinaryPrimitives.ReadUInt64LittleEndian(digest) & PayloadMask;
		return (prefix << 60) | payload;
	}

	/// <summary>材质 id：名字用 <c>$basetexture</c> 的<b>全路径</b>。</summary>
	public static ulong Material(string baseTexturePath, string? idNamespace = null)
		=> Derive(MaterialPrefix, baseTexturePath, idNamespace);

	/// <summary>贴图 id：名字只用 <b>basename</b>（Python 侧的现状，见类型注释里的隐患）。</summary>
	public static ulong Texture(string textureBaseName, string? idNamespace = null)
		=> Derive(TexturePrefix, textureBaseName, idNamespace);

	/// <summary>取出高 4 位，用来判断一个 id 大概是哪类资产。</summary>
	public static ulong PrefixOf(ulong id) => id >> 60;

	/// <summary>
	/// 一个导入来源（压缩包名 / 目录名 / 单个 VPK 名）→ 命名空间 token。
	/// <para>
	/// <b>为什么用"来源包名"而不是随机 GUID。</b>用户重转同一份包时应当得到同一套 id：
	/// 否则同一个 Mod 重导一次就变成另一套资产，库里的重复项会越来越多。
	/// 代价是两个不同的包恰好同名时仍会撞——概率极低，而且撞了也只是回到今天的行为。
	/// </para>
	/// <para>
	/// <b>为什么哈希而不是直接用名字。</b>token 会进 id 的哈希输入，用短 token 让"这个 id 属于
	/// 哪个来源"不至于一眼可读，但也避免了名字里的分隔符、大小写、Unicode 归一化差异带来的歧义。
	/// 归一化只做两件可预期的事：去首尾空白、转小写（大小写不同的同一个包名应当同 token）。
	/// </para>
	/// </summary>
	public static string NamespaceFor(string sourceName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
		var normalized = sourceName.Trim().ToLowerInvariant();
		var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
		return "vpk-" + Convert.ToHexString(digest, 0, 8).ToLowerInvariant();
	}

	/// <summary>把命名空间拼进被哈希的名字。空命名空间时原样返回，保证旧行为逐位不变。</summary>
	private static string Scope(string name, string? idNamespace)
		=> string.IsNullOrWhiteSpace(idNamespace) ? name : idNamespace + "/" + name;
}
