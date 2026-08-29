using System.Text.Json;

namespace HD2ModAdaptation.PatchReconstruction.Lua;

public static class LuaMergeLedgerSerializer
{
	private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };

	public static string Serialize(LuaMergeLedger ledger) => JsonSerializer.Serialize(ledger, Options);

	public static LuaMergeLedger Deserialize(string json)
	{
		if (string.IsNullOrWhiteSpace(json)) throw new LuaPatchFormatException("Lua merge ledger is empty.");
		try
		{
			using var document = JsonDocument.Parse(json); var root = document.RootElement;
			var format = GetString(root, "format") ?? (root.TryGetProperty("v", out _) ? "HD2PB" : throw new LuaPatchFormatException("Lua merge ledger has no format."));
			var version = GetInt(root, "version") ?? GetInt(root, "v") ?? throw new LuaPatchFormatException("Lua merge ledger has no version.");
			var archive = GetUInt64(root, "archiveId") ?? GetUInt64(root, "archive_id") ?? LuaAssetKeys.BootArchiveId;
			var type = GetUInt64(root, "typeId") ?? GetUInt64(root, "type_id") ?? LuaAssetKeys.LuaTypeId;
			var file = GetUInt64(root, "fileId") ?? GetUInt64(root, "file_id") ?? LuaAssetKeys.BootFileId;
			var length = GetInt(root, "bootLength") ?? GetInt(root, "boot_len") ?? throw new LuaPatchFormatException("Lua merge ledger has no boot length.");
			var hash = GetString(root, "bootSha256") ?? GetString(root, "boot_sha256") ?? throw new LuaPatchFormatException("Lua merge ledger has no boot hash.");
			var wrapper = GetString(root, "wrapper") ?? "BootWrapperV1";
			if (!root.TryGetProperty("blocks", out var array) || array.ValueKind != JsonValueKind.Array) throw new LuaPatchFormatException("Lua merge ledger has no blocks array.");
			var blocks = new List<LuaMergeLedgerBlock>();
			foreach (var item in array.EnumerateArray())
			{
				var order = GetInt(item, "order") ?? GetInt(item, "i") ?? throw new LuaPatchFormatException("Lua merge ledger block has no order.");
				var name = GetString(item, "name") ?? "unnamed";
				var wrap = GetBool(item, "wrapBootReturn") ?? GetBool(item, "wrap") ?? false;
				var head = GetString(item, "headSha256") ?? GetString(item, "head_sha256") ?? throw new LuaPatchFormatException("Lua merge ledger block has no head hash.");
				var body = GetString(item, "bodySha256") ?? GetString(item, "body_sha256") ?? throw new LuaPatchFormatException("Lua merge ledger block has no body hash.");
				blocks.Add(new(
					order,
					name,
					wrap,
					head,
					body,
					GetString(item, "sourceModId") ?? GetString(item, "src"),
					GetString(item, "sourcePatchGroup") ?? GetString(item, "patch"),
					GetString(item, "bootOkVariable") ?? "boot_ok",
					GetString(item, "bootReturnVariable") ?? "boot_ret"));
			}
			return new(format, version, archive, type, file, length, hash, wrapper, blocks);
		}
		catch (JsonException ex) { throw new LuaPatchFormatException($"Lua merge ledger JSON is invalid: {ex.Message}"); }
	}

	public static void Validate(string json, byte[] bootBytecode, IReadOnlyList<LuaPatchBlock> blocks) => Deserialize(json).Validate(bootBytecode, blocks);
	private static string? GetString(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
	private static int? GetInt(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.TryGetInt32(out var value) ? value : null;
	private static ulong? GetUInt64(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.TryGetUInt64(out var value) ? value : null;
	private static bool? GetBool(JsonElement e, string name) => e.TryGetProperty(name, out var p) && (p.ValueKind == JsonValueKind.True || p.ValueKind == JsonValueKind.False) ? p.GetBoolean() : null;
}
