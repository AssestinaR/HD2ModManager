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
	public static bool IsLuaEntry(PatchTocEntry entry) => entry is not null && IsBoot(entry.AssetKey);
}

// Bytecode is a first-class document kind: since the 2026-10-20 update a boot
// patch may carry a compiled chunk instead of text, and such a chunk cannot be
// merged by concatenation. It is replayed whole, exactly like the game's own
// boot resource, so it needs its own wrapper kind rather than being mistaken for
// an unparsable Patch.
public enum LuaPatchWrapperKind { Single, LegacyHd2PbMarkerMerge, LegacyHd2PbMerge, Hd2LuaMergeV1, Opaque, Bytecode }

// Every Lua resource starts with an eight-byte prefix: the payload length and a
// format version. The 2026-10-20 game update switched the version from 2 to 1
// and started shipping PUC-Lua 5.1 bytecode instead of source text; a version-2
// source resource is what now makes the game crash on startup, so the manager
// must emit version 1 with a compiled chunk.
public static class LuaResourceFormat
{
	public const int PrefixLength = 8;

	/// <summary>Current format: an eight-byte prefix followed by Lua 5.1 bytecode.</summary>
	public const uint BytecodeVersion = 1;

	/// <summary>Pre-update format: an eight-byte prefix followed by UTF-8 source text.</summary>
	public const uint LegacySourceVersion = 2;
}


public sealed class LuaPatchFormatException(string message) : Exception(message);

public sealed record LuaPatchBlock(
	string Name,
	string Head,
	string Body,
	bool WrapBootReturn,
	bool IsManagedBlock = false,
	string? SourceModId = null,
	string? SourcePatchGroup = null,
	string BootOkVariable = "boot_ok",
	string BootReturnVariable = "boot_ret")
{
	public string BodySha256 => LuaHash.Sha256Text(Body);
	public string HeadSha256 => LuaHash.Sha256Text(Head);
}

public sealed record LuaMergeLedgerBlock(
	int Order,
	string Name,
	bool WrapBootReturn,
	string HeadSha256,
	string BodySha256,
	string? SourceModId = null,
	string? SourcePatchGroup = null,
	string BootOkVariable = "boot_ok",
	string BootReturnVariable = "boot_ret");

public sealed record LuaMergeLedger(string Format, int Version, ulong ArchiveId, ulong TypeId, ulong FileId, int BootLength, string BootSha256, string Wrapper, IReadOnlyList<LuaMergeLedgerBlock> Blocks)
{
	public void Validate(byte[] bootBytecode, IReadOnlyList<LuaPatchBlock> blocks)
	{
		if (!string.Equals(Format, "HD2LuaMergeV1", StringComparison.OrdinalIgnoreCase)
			&& !string.Equals(Format, "HD2PB", StringComparison.OrdinalIgnoreCase))
			throw new LuaPatchFormatException($"Unsupported Lua merge ledger format: {Format}.");
		if (Version != 1) throw new LuaPatchFormatException($"Unsupported Lua merge ledger version: {Version}.");
		if (ArchiveId != LuaAssetKeys.BootArchiveId
			|| TypeId != LuaAssetKeys.LuaTypeId
			|| FileId != LuaAssetKeys.BootFileId)
			throw new LuaPatchFormatException("Lua merge ledger identifies a different AssetKey.");
		if (!string.IsNullOrWhiteSpace(Wrapper)
			&& !string.Equals(Wrapper, "BootWrapperV1", StringComparison.OrdinalIgnoreCase))
			throw new LuaPatchFormatException($"Unsupported Lua merge wrapper: {Wrapper}.");
		if (BootLength != bootBytecode.Length || !string.Equals(BootSha256, LuaHash.Sha256(bootBytecode), StringComparison.OrdinalIgnoreCase)) throw new LuaPatchFormatException("Lua merge ledger boot bytecode hash does not match the embedded boot bytecode.");
		if (Blocks.Count != blocks.Count) throw new LuaPatchFormatException("Lua merge ledger block count does not match the payload.");
		for (var i = 0; i < blocks.Count; i++)
		{
			var expected = Blocks[i]; var actual = blocks[i];
			if (expected.Order != i + 1
				|| !string.Equals(expected.BodySha256, actual.BodySha256, StringComparison.OrdinalIgnoreCase)
				|| !string.Equals(expected.HeadSha256, actual.HeadSha256, StringComparison.OrdinalIgnoreCase)
				|| expected.WrapBootReturn != actual.WrapBootReturn
				|| !string.Equals(expected.BootOkVariable, actual.BootOkVariable, StringComparison.Ordinal)
				|| !string.Equals(expected.BootReturnVariable, actual.BootReturnVariable, StringComparison.Ordinal))
				throw new LuaPatchFormatException($"Lua merge ledger block {i + 1} does not match the payload.");
		}
	}
}

public sealed record LuaPatchDocument(
	string SourcePatchPath,
	PatchTocEntry Entry,
	PatchEntryPayload Payload,
	byte[] HeaderTemplate,
	byte[] BootBytecode,
	IReadOnlyList<LuaPatchBlock> Blocks,
	LuaPatchWrapperKind WrapperKind,
	LuaMergeLedger? Ledger,
	string SourceText,
	string BootOkVariable = "boot_ok",
	string BootReturnVariable = "boot_ret")
{
	public bool IsOpaque => WrapperKind == LuaPatchWrapperKind.Opaque;
}

public sealed record LuaPatchComposition(byte[] HeaderTemplate, byte[] BootBytecode, string SourceText, IReadOnlyList<LuaPatchBlock> Blocks, LuaMergeLedger Ledger)
{
	/// <summary>
	/// Compiled chunk emitted in place of the UTF-8 source. The source text is
	/// still kept because it is what the merge ledger, the runtime validator and
	/// every diagnostic talk about; only the bytes written to the Patch change.
	/// </summary>
	public byte[]? CompiledPayload { get; init; }

	public byte[] PayloadBytes => CompiledPayload ?? Encoding.UTF8.GetBytes(SourceText);
}

internal static class LuaHash
{
	public static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
	public static string Sha256Text(string text) => Sha256(Encoding.UTF8.GetBytes(text));
}
