using System.Security.Cryptography;
using System.Text;

namespace HD2ModAdaptation.PatchReconstruction.Lua;

public static class LuaAssetKeys
{
	public const ulong LuaTypeId = 0xA14E8DFA2CD117E2;
	public const ulong BootFileId = 0xF476DF93691895FA;
	public const ulong BootArchiveId = 0x9BA626AFA44A3AA3;
	public static readonly AssetKey BootAssetKey = new(LuaTypeId, BootFileId);
	public static bool IsLuaType(AssetKey key) => key.TypeId == LuaTypeId;
	public static bool IsBoot(AssetKey key) => key == BootAssetKey;
}

// 资源载荷的前八字节是"长度 + 格式版本"。2026-10-20 更新后游戏自己发的是
// version 1 + Lua 5.1 字节码；version 2 是更新前的明文源码格式。
//
// 实测结论（HD2RuntimeArmorOverride/notes/lua-entry-capacity-investigation-20261028.md）：
// 引擎两种都接受，因为载荷最终交给 loadstring，它会自己识别 \x1bLua 头。
// 管理器仍然发 version 1 + 编译后的 chunk，只是为了和游戏自己那份保持同一种形态。
public static class LuaResourceFormat
{
	public const int PrefixLength = 8;

	/// <summary>Current format: an eight-byte prefix followed by Lua 5.1 bytecode.</summary>
	public const uint BytecodeVersion = 1;

	/// <summary>Pre-update format: an eight-byte prefix followed by UTF-8 source text.</summary>
	public const uint LegacySourceVersion = 2;
}

public sealed class LuaPatchFormatException(string message) : Exception(message);

// 管理器自己的运行时在启动脚本里的那一段。合并功能已经删除，所以这里不再需要
// 用于重新解析的标记、head/body 分离、boot 返回值别名——只留"名字 + 正文"。
public sealed record LuaPatchBlock(string Name, string Body);

// 一份待投放的 Lua 启动脚本：重放游戏原始 boot 字节码，然后跑上面那些块。
// WrapperKind / Ledger / SourceText 这些字段随合并功能一起删掉了——它们原本只服务于
// "把多个 mod 的启动脚本拼起来再重新解析"。
public sealed record LuaPatchDocument(
	string SourcePatchPath,
	PatchTocEntry Entry,
	PatchEntryPayload Payload,
	byte[] HeaderTemplate,
	byte[] BootBytecode,
	IReadOnlyList<LuaPatchBlock> Blocks);

public sealed record LuaPatchComposition(byte[] HeaderTemplate, byte[] BootBytecode, string SourceText)
{
	/// <summary>
	/// Compiled chunk emitted in place of the UTF-8 source. The source text is
	/// still kept because it is what the validator and every diagnostic talk
	/// about; only the bytes written to the Patch change.
	/// </summary>
	public byte[]? CompiledPayload { get; init; }

	public byte[] PayloadBytes => CompiledPayload ?? Encoding.UTF8.GetBytes(SourceText);
}
