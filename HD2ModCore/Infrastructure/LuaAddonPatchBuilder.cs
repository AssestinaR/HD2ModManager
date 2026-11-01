using System.Buffers.Binary;
using System.Text;
using HD2GameItemAssets;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 生成"加载器通用"的 Lua 补丁包：单个 <c>9ba626afa44a3aa3.patch_0</c>，
/// 里面装一个资源名为 <c>mods/&lt;作者&gt;/&lt;名字&gt;</c> 的 Lua 文本。
///
/// <para>
/// 这套约定是从三家加载器的一手资料里核对出来的（2026-11-01）：
/// <list type="bullet">
/// <item><b>Boot Mod Loader</b>：替换 boot，然后自己用 <c>io</c> 读
/// <c>data\9ba626afa44a3aa3.patch_0..N</c>，只看"纯文本 Lua 资源"，
/// 要求资源名是 <c>mods/&lt;author&gt;/&lt;name&gt;</c> 且<b>首行</b>是
/// <c>-- HD2-Addon: mods/&lt;author&gt;/&lt;name&gt;</c>；重名时高号 patch 胜出。</item>
/// <item><b>Chef Shared Loader</b>：boot 是一个 stub，真正的 loader 在
/// <c>mods/chef_shared_loader/core_1</c>，用 <c>require</c> 起；其余 mod 同样走 addon 约定。</item>
/// <item><b>MDL3</b>：它自带的 Data Hub 就是 <c>-- HD2-Addon: mods/codex/hd2_data_hub</c>。</item>
/// </list>
/// 所以"patch 版"是三家通吃的格式，而"裸 .lua 版"留给按文件夹读脚本的加载器。
/// </para>
///
/// <para>
/// 布局与 <c>tmp/lua-boot/build_probe.py</c> 完全一致（同一套 magic / 记录偏移），
/// 只是资源名换成了 addon 名、且只有一个资源。**不要在这里"顺手改一下布局"**：
/// 差一个字段游戏就会静默忽略整个 patch。
/// </para>
/// </summary>
public static class LuaAddonPatchBuilder
{
	/// <summary>补丁归档的固定文件名前缀（游戏只认这一个家族）。</summary>
	public const string ArchiveId = "9ba626afa44a3aa3";

	/// <summary>Lua 资源在 TOC 里的类型 id。</summary>
	private const ulong LuaTypeId = 0xA14E8DFA2CD117E2;

	private const uint Magic = 0xF0000011;
	private const int HeaderLength = 104;
	private const int EntryLength = 80;
	private const int TypeRecordSize = 32;
	private const int PayloadOffset = 192;   // align16(HeaderLength + EntryLength)

	/// <summary>
	/// 造一份单资源补丁。返回主文件名（<c>9ba626afa44a3aa3.patch_0</c>），
	/// 并顺手写出空的 <c>.stream</c> / <c>.gpu_resources</c>——
	/// 缺这两个文件时游戏会在某些路径上认为补丁不完整。
	/// </summary>
	public static string Write(string directory, string addonId, string source)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directory);
		ArgumentException.ThrowIfNullOrWhiteSpace(addonId);
		ArgumentNullException.ThrowIfNull(source);

		var full = Path.GetFullPath(directory);
		Directory.CreateDirectory(full);

		var body = Encoding.UTF8.GetBytes(source);
		var archive = BuildArchive(addonId, body);
		var name = $"{ArchiveId}.patch_0";
		var path = Path.Combine(full, name);
		File.WriteAllBytes(path, archive);
		File.WriteAllBytes(path + ".stream", []);
		File.WriteAllBytes(path + ".gpu_resources", []);
		return path;
	}

	private static byte[] BuildArchive(string addonId, byte[] body)
	{
		// 资源块 = u32 长度 + u32 版本 + 正文（版本 2 与探针一致）。
		var resource = new byte[8 + body.Length];
		BinaryPrimitives.WriteUInt32LittleEndian(resource.AsSpan(0), (uint)body.Length);
		BinaryPrimitives.WriteUInt32LittleEndian(resource.AsSpan(4), 2);
		body.CopyTo(resource, 8);

		var archiveLength = Align16(PayloadOffset + resource.Length);
		var archive = new byte[archiveLength];

		BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(0), Magic);
		BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(4), 1);   // typeCount
		BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(8), 1);   // fileCount
		BinaryPrimitives.WriteUInt64LittleEndian(archive.AsSpan(32), (ulong)archiveLength);

		// 类型记录从 72 开始：+8 类型 id，+16 该类型的条目数，+24/+28 两个 16。
		BinaryPrimitives.WriteUInt64LittleEndian(archive.AsSpan(72 + 8), LuaTypeId);
		BinaryPrimitives.WriteUInt64LittleEndian(archive.AsSpan(72 + 16), 1);
		BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(72 + 24), 16);
		BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(72 + 28), 16);

		// 条目记录从 104 开始。
		var entry = HeaderLength;
		BinaryPrimitives.WriteUInt64LittleEndian(archive.AsSpan(entry), StingrayHash.Murmur64A(addonId));
		BinaryPrimitives.WriteUInt64LittleEndian(archive.AsSpan(entry + 8), LuaTypeId);
		BinaryPrimitives.WriteUInt64LittleEndian(archive.AsSpan(entry + 16), PayloadOffset);
		BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(entry + 56), (uint)resource.Length);  // TocDataSize
		BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(entry + 68), 16);
		BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(entry + 72), 16);

		resource.CopyTo(archive, PayloadOffset);
		return archive;
	}

	private static int Align16(int value) => (value + 15) & ~15;
}
