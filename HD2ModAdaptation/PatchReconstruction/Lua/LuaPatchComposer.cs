using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace HD2ModAdaptation.PatchReconstruction.Lua;

// Reads the single-entry Lua Patch format used by the game and by the community
// Lua tooling. The resource header contains an eight-byte Stingray payload prefix;
// the actual UTF-8 Lua chunk starts at 0xC0, not at the TOC entry's 0xB8 offset.
public sealed class LuaPatchInspector
{
	private const int HeaderLength = 0xC0;
	private const uint PatchMagic = 0xF0000011;
	private static readonly UTF8Encoding StrictUtf8 = new(false, true);
	private static readonly Regex BootLine = new(
		"^local\\s+BC\\s*=\\s*\"(?<bytes>(?:\\\\\\d{1,3})+)\"\\s*$",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);
	private static readonly Regex PcallLine = new(
		"(?<ok>[A-Za-z_]\\w*)\\s*,\\s*(?<ret>[A-Za-z_]\\w*)\\s*=\\s*pcall\\s*\\(\\s*chunk\\s*\\)",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);
	private static readonly Regex ReturnLine = new(
		"^return\\s+(?<name>[A-Za-z_]\\w*)\\s*$",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);
	private static readonly Regex LegacyBlockLine = new(
		"^do\\s+--\\s+----\\s*(?<name>.*?)\\s*$",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);
	private static readonly Regex MarkerLine = new(
		"^--@@(?<prefix>HD2PB|HD2LUA)-(?<kind>LEDGER|BEGIN|HEAD|BOOT|BODY|END)(?:\\s+(?<id>\\d+))?(?:\\s+(?<json>.*))?$",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);
	private static readonly Regex BootAliasLine = new(
		"^local\\s+(?<ok>[A-Za-z_]\\w*)\\s*,\\s*(?<ret>[A-Za-z_]\\w*)\\s*=\\s*HD2PB_BOOT_OK\\s*,\\s*HD2PB_BOOT_RET\\s*$",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private readonly PatchTocScanner scanner;

	public LuaPatchInspector(PatchTocScanner? scanner = null)
	{
		this.scanner = scanner ?? new PatchTocScanner();
	}

	public async ValueTask<LuaPatchDocument> InspectAsync(string patchPath, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(patchPath);
		var sourcePath = Path.GetFullPath(patchPath);
		var bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
		if (bytes.Length <= HeaderLength || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4)) != PatchMagic)
			throw new LuaPatchFormatException($"Not a supported Lua patch archive: {sourcePath}");

		var entries = scanner.ScanEntries(bytes, sourcePath);
		if (entries.Count != 1 || !LuaAssetKeys.IsBoot(entries[0].AssetKey))
			throw new LuaPatchFormatException("A Lua patch must contain exactly one boot.lua AssetKey entry.");
		return CreateDocument(bytes, sourcePath, entries[0]);
	}

	// Deployment normally already has the lightweight AssetInventory fact for the
	// exact boot entry. Reuse that fact so the merge path does not scan the TOC a
	// second time; the bytes are still read from disk and parsed strictly below.
	public async ValueTask<LuaPatchDocument> InspectAsync(
		string patchPath,
		PatchTocEntry knownBootEntry,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(patchPath);
		ArgumentNullException.ThrowIfNull(knownBootEntry);
		if (!LuaAssetKeys.IsBoot(knownBootEntry.AssetKey))
			throw new LuaPatchFormatException("The supplied AssetKey is not boot.lua.");
		var sourcePath = Path.GetFullPath(patchPath);
		var bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
		return CreateDocument(bytes, sourcePath, knownBootEntry);
	}

	public async ValueTask<LuaPatchDocument> InspectOpaqueAsync(
		string patchPath,
		PatchTocEntry knownBootEntry,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(patchPath);
		ArgumentNullException.ThrowIfNull(knownBootEntry);
		if (!LuaAssetKeys.IsBoot(knownBootEntry.AssetKey))
			throw new LuaPatchFormatException("The supplied AssetKey is not boot.lua.");
		var sourcePath = Path.GetFullPath(patchPath);
		var bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
		return CreateOpaqueDocument(bytes, sourcePath, knownBootEntry);
	}

	private static LuaPatchDocument CreateDocument(byte[] bytes, string sourcePath, PatchTocEntry sourceEntry)
	{
		if (bytes.Length <= HeaderLength || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4)) != PatchMagic)
			throw new LuaPatchFormatException($"Not a supported Lua patch archive: {sourcePath}");

		var payloadBytes = bytes.AsSpan(HeaderLength).ToArray();
		if (payloadBytes.Length == 0)
			throw new LuaPatchFormatException("Lua patch payload is empty.");

		var entry = sourceEntry with
		{
			SourceFilePath = sourcePath,
			SourceFileName = Path.GetFileName(sourcePath),
			TocDataOffset = HeaderLength - 8,
			TocDataSize = checked((uint)(payloadBytes.Length + 8)),
			StreamOffset = 0,
			GpuResourceOffset = 0,
			StreamSize = 0,
			GpuResourceSize = 0,
		};
		var payload = new PatchEntryPayload(entry, payloadBytes, Array.Empty<byte>(), Array.Empty<byte>());
		var header = bytes.AsSpan(0, HeaderLength).ToArray();

		if (IsLua51Bytecode(payloadBytes))
		{
			// The chunk is whole and self contained; parsing it here is a real
			// structural check, not a formality, because a truncated chunk must be
			// rejected before anything tries to replay it.
			Lua51Bytecode.Read(payloadBytes);
			return new LuaPatchDocument(
				sourcePath,
				entry,
				payload,
				header,
				payloadBytes,
				Array.Empty<LuaPatchBlock>(),
				LuaPatchWrapperKind.Bytecode,
				null,
				string.Empty);
		}

		string sourceText;
		try
		{
			sourceText = StrictUtf8.GetString(payloadBytes).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
		}
		catch (DecoderFallbackException exception)
		{
			throw new LuaPatchFormatException($"Lua patch payload is not valid UTF-8: {exception.Message}");
		}

		var parsed = ParseSource(sourceText, sourcePath);
		return new LuaPatchDocument(
			sourcePath,
			entry,
			payload,
			header,
			parsed.BootBytecode,
			parsed.Blocks,
			parsed.WrapperKind,
			parsed.Ledger,
			sourceText,
			parsed.BootOkVariable,
			parsed.BootReturnVariable);
	}

	private static bool IsLua51Bytecode(ReadOnlySpan<byte> payload)
		=> payload.Length >= Lua51Bytecode.HeaderLength
			&& payload[0] == 0x1B
			&& payload[1] == (byte)'L'
			&& payload[2] == (byte)'u'
			&& payload[3] == (byte)'a'
			&& payload[4] == 0x51;

	private static LuaPatchDocument CreateOpaqueDocument(byte[] bytes, string sourcePath, PatchTocEntry sourceEntry)
	{
		if (bytes.Length <= HeaderLength || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4)) != PatchMagic)
			throw new LuaPatchFormatException($"Not a supported Lua patch archive: {sourcePath}");
		var payloadBytes = bytes.AsSpan(HeaderLength).ToArray();
		if (payloadBytes.Length == 0) throw new LuaPatchFormatException("Lua patch payload is empty.");
		string sourceText;
		try
		{
			sourceText = StrictUtf8.GetString(payloadBytes).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
		}
		catch (DecoderFallbackException exception)
		{
			throw new LuaPatchFormatException($"Lua patch payload is not valid UTF-8: {exception.Message}");
		}
		var entry = sourceEntry with
		{
			SourceFilePath = sourcePath,
			SourceFileName = Path.GetFileName(sourcePath),
			TocDataOffset = HeaderLength - 8,
			TocDataSize = checked((uint)(payloadBytes.Length + 8)),
			StreamOffset = 0,
			GpuResourceOffset = 0,
			StreamSize = 0,
			GpuResourceSize = 0,
		};
		var payload = new PatchEntryPayload(entry, payloadBytes, Array.Empty<byte>(), Array.Empty<byte>());
		return new LuaPatchDocument(sourcePath, entry, payload, bytes.AsSpan(0, HeaderLength).ToArray(), Array.Empty<byte>(),
			Array.Empty<LuaPatchBlock>(), LuaPatchWrapperKind.Opaque, null, sourceText);
	}

	private static ParsedSource ParseSource(string source, string sourcePath)
	{
		var lines = source.Split('\n');
		var bootLineIndex = -1;
		Match? bootMatch = null;
		for (var index = 0; index < lines.Length; index++)
		{
			var match = BootLine.Match(lines[index]);
			if (!match.Success) continue;
			if (bootLineIndex >= 0) throw new LuaPatchFormatException($"{sourcePath}: multiple embedded boot bytecode lines.");
			bootLineIndex = index;
			bootMatch = match;
		}
		if (bootLineIndex < 0 || bootMatch is null)
			throw new LuaPatchFormatException($"{sourcePath}: no supported `local BC = ...` boot line.");

		var boot = DecodeDecimalEscapes(bootMatch.Groups["bytes"].Value, sourcePath);
		var pcallIndex = -1;
		string? bootOk = null;
		string? bootReturn = null;
		for (var index = bootLineIndex; index < Math.Min(lines.Length, bootLineIndex + 13); index++)
		{
			var match = PcallLine.Match(lines[index]);
			if (!match.Success) continue;
			if (pcallIndex >= 0) throw new LuaPatchFormatException($"{sourcePath}: multiple boot pcall lines.");
			pcallIndex = index;
			bootOk = match.Groups["ok"].Value;
			bootReturn = match.Groups["ret"].Value;
		}
		if (pcallIndex < 0 || bootOk is null || bootReturn is null)
			throw new LuaPatchFormatException($"{sourcePath}: boot pcall line was not found near the embedded bytecode.");

		var returnLines = lines
			.Select((line, index) => (line, index))
			.Where(item => item.index > pcallIndex && ReturnLine.IsMatch(item.line))
			.ToArray();
		if (returnLines.Length != 1)
			throw new LuaPatchFormatException($"{sourcePath}: expected exactly one top-level return after boot, found {returnLines.Length}.");
		var returnIndex = returnLines[0].index;
		if (returnIndex <= pcallIndex) throw new LuaPatchFormatException($"{sourcePath}: top-level return precedes the boot pcall.");

		for (var index = 0; index < bootLineIndex; index++)
		{
			var line = lines[index];
			if (!string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith("--", StringComparison.Ordinal))
				throw new LuaPatchFormatException($"{sourcePath}: executable code appears before the embedded boot.");
		}

		var markerLines = lines
			.Select((line, index) => (line, index, match: MarkerLine.Match(line)))
			.Where(item => item.match.Success)
			.ToArray();
		var ledgerMarkers = markerLines
			.Where(item => string.Equals(item.match.Groups["kind"].Value, "LEDGER", StringComparison.Ordinal))
			.ToArray();
		if (ledgerMarkers.Length > 1)
			throw new LuaPatchFormatException($"{sourcePath}: multiple Lua merge ledger markers.");
		var ledgerMarker = ledgerMarkers.FirstOrDefault();
		var ledgerText = ledgerMarker.match?.Groups["json"].Value;
		LuaMergeLedger? ledger = null;
		if (!string.IsNullOrWhiteSpace(ledgerText))
			ledger = LuaMergeLedgerSerializer.Deserialize(ledgerText);

		if (markerLines.Any(item => string.Equals(item.match.Groups["kind"].Value, "BEGIN", StringComparison.Ordinal)))
		{
			var blocks = ParseMarkedBlocks(lines, markerLines, ledger, sourcePath);
			ledger?.Validate(boot, blocks);
			return new ParsedSource(boot, blocks, ledger is not null && string.Equals(ledger.Format, "HD2LuaMergeV1", StringComparison.OrdinalIgnoreCase)
				? LuaPatchWrapperKind.Hd2LuaMergeV1
				: LuaPatchWrapperKind.LegacyHd2PbMarkerMerge,
				bootOk,
				bootReturn,
				ledger);
		}

		var legacyBlocks = ParseLegacyBlocks(lines, pcallIndex, returnIndex, bootOk, bootReturn, sourcePath);
		if (legacyBlocks.Count > 0)
			return new ParsedSource(boot, legacyBlocks, LuaPatchWrapperKind.LegacyHd2PbMerge, bootOk, bootReturn, ledger);

		var head = string.Join('\n', lines[..bootLineIndex]).Trim('\n');
		var body = string.Join('\n', lines[(pcallIndex + 1)..returnIndex]).Trim('\n');
		var block = new LuaPatchBlock(
			Name: FindName(head, body, Path.GetFileName(sourcePath)),
			Head: RemoveManagerMarkers(head),
			Body: body,
			WrapBootReturn: !IsCanonicalBootAlias(bootOk, bootReturn),
			BootOkVariable: bootOk,
			BootReturnVariable: bootReturn);
		return new ParsedSource(boot, [block], LuaPatchWrapperKind.Single, bootOk, bootReturn, ledger);
	}

	private static IReadOnlyList<LuaPatchBlock> ParseMarkedBlocks(
		string[] lines,
		IReadOnlyList<(string line, int index, Match match)> markers,
		LuaMergeLedger? ledger,
		string sourcePath)
	{
		var groups = markers
			.Where(item => !string.Equals(item.match.Groups["kind"].Value, "LEDGER", StringComparison.Ordinal))
			.GroupBy(item => item.match.Groups["id"].Value, StringComparer.Ordinal)
			.OrderBy(group => int.TryParse(group.Key, out var number) ? number : int.MaxValue)
			.ToArray();
		var blocks = new List<LuaPatchBlock>(groups.Length);
		foreach (var group in groups)
		{
			var byKind = new Dictionary<string, int>(StringComparer.Ordinal);
			foreach (var item in group)
			{
				var kind = item.match.Groups["kind"].Value;
				if (!byKind.TryAdd(kind, item.index))
					throw new LuaPatchFormatException($"{sourcePath}: Lua block {group.Key} contains duplicate {kind} markers.");
			}
			if (!byKind.TryGetValue("BEGIN", out var begin)
				|| !byKind.TryGetValue("BODY", out var bodyMarker)
				|| !byKind.TryGetValue("END", out var end)
				|| begin >= bodyMarker
				|| bodyMarker >= end)
				throw new LuaPatchFormatException($"{sourcePath}: Lua block markers are incomplete or out of order.");

			var hasHeadMarker = byKind.TryGetValue("HEAD", out var headMarker);
			var hasBootMarker = byKind.TryGetValue("BOOT", out var bootMarker);
			var headStart = hasHeadMarker ? headMarker + 1 : begin + 1;
			var headEnd = hasBootMarker ? bootMarker : bodyMarker;
			var head = string.Join('\n', lines[headStart..headEnd]).Trim('\n');
			var body = string.Join('\n', lines[(bodyMarker + 1)..end]).Trim('\n');
			var ordinal = int.TryParse(group.Key, out var parsedOrdinal) ? parsedOrdinal : blocks.Count + 1;
			var metadata = ledger?.Blocks.FirstOrDefault(item => item.Order == ordinal);
			var name = metadata?.Name ?? FindName(head, body, $"block-{ordinal}");
			var wrap = hasBootMarker || metadata?.WrapBootReturn == true;
			var ok = metadata?.BootOkVariable ?? "boot_ok";
			var ret = metadata?.BootReturnVariable ?? "boot_ret";
			blocks.Add(new LuaPatchBlock(name, head, body, wrap, true, metadata?.SourceModId, metadata?.SourcePatchGroup, ok, ret));
		}
		return blocks;
	}

	private static IReadOnlyList<LuaPatchBlock> ParseLegacyBlocks(
		string[] lines,
		int pcallIndex,
		int returnIndex,
		string bootOk,
		string bootReturn,
		string sourcePath)
	{
		var starts = Enumerable.Range(pcallIndex + 1, returnIndex - pcallIndex - 1)
			.Where(index => LegacyBlockLine.IsMatch(lines[index]))
			.ToArray();
		if (starts.Length == 0) return Array.Empty<LuaPatchBlock>();

		var blocks = new List<LuaPatchBlock>(starts.Length);
		for (var blockIndex = 0; blockIndex < starts.Length; blockIndex++)
		{
			var start = starts[blockIndex];
			var stop = blockIndex + 1 < starts.Length ? starts[blockIndex + 1] : returnIndex;
			var segment = lines[(start + 1)..stop].ToList();
			while (segment.Count > 0 && string.IsNullOrWhiteSpace(segment[^1])) segment.RemoveAt(segment.Count - 1);
			if (segment.Count == 0 || !string.Equals(segment[^1], "end", StringComparison.Ordinal))
				throw new LuaPatchFormatException($"{sourcePath}: legacy Lua block does not end with a column-zero `end`.");
			segment.RemoveAt(segment.Count - 1);
			var aliasIndex = segment.FindIndex(line => BootAliasLine.IsMatch(line));
			var wrap = aliasIndex >= 0;
			var aliasOk = bootOk;
			var aliasReturn = bootReturn;
			if (aliasIndex >= 0)
			{
				var alias = BootAliasLine.Match(segment[aliasIndex]);
				aliasOk = alias.Groups["ok"].Value;
				aliasReturn = alias.Groups["ret"].Value;
				segment.RemoveAt(aliasIndex);
			}
			var headLines = wrap ? lines[(start + 1)..(start + 1 + aliasIndex)] : Array.Empty<string>();
			var head = string.Join('\n', headLines).Trim('\n');
			var bodyStart = wrap ? aliasIndex : 0;
			var body = string.Join('\n', segment.Skip(bodyStart)).Trim('\n');
			var nameMatch = LegacyBlockLine.Match(lines[start]);
			var name = nameMatch.Groups["name"].Value.Trim().Trim('-').Trim();
			if (string.IsNullOrWhiteSpace(name)) name = FindName(head, body, $"block-{blockIndex + 1}");
			blocks.Add(new LuaPatchBlock(name, head, body, wrap, true, BootOkVariable: aliasOk, BootReturnVariable: aliasReturn));
		}
		return blocks;
	}

	private static byte[] DecodeDecimalEscapes(string value, string sourcePath)
	{
		var bytes = new List<byte>();
		for (var index = 0; index < value.Length;)
		{
			if (value[index] != '\\') throw new LuaPatchFormatException($"{sourcePath}: boot bytecode contains a non-decimal escape.");
			index++;
			var start = index;
			while (index < value.Length && char.IsDigit(value[index]) && index - start < 3) index++;
			if (start == index || !byte.TryParse(value[start..index], out var number))
				throw new LuaPatchFormatException($"{sourcePath}: invalid decimal boot byte escape.");
			bytes.Add(number);
		}
		return bytes.ToArray();
	}

	private static bool IsCanonicalBootAlias(string ok, string ret)
		=> string.Equals(ok, "HD2PB_BOOT_OK", StringComparison.Ordinal)
			&& string.Equals(ret, "HD2PB_BOOT_RET", StringComparison.Ordinal);

	private static string FindName(string head, string body, string fallback)
	{
		foreach (var line in (head + "\n" + body).Split('\n'))
		{
			var value = line.Trim().TrimStart('-').Trim();
			if (value.Length == 0 || value.StartsWith("@@", StringComparison.Ordinal)) continue;
			return value.Length > 120 ? value[..120] : value;
		}
		return fallback;
	}

	private static string RemoveManagerMarkers(string value)
		=> string.Join('\n', value.Split('\n').Where(line => !line.StartsWith("--@@HD2", StringComparison.Ordinal))).Trim('\n');

	private sealed record ParsedSource(
		byte[] BootBytecode,
		IReadOnlyList<LuaPatchBlock> Blocks,
		LuaPatchWrapperKind WrapperKind,
		string BootOkVariable,
		string BootReturnVariable,
		LuaMergeLedger? Ledger);
}

// Composes already-inspected Lua documents into one standard game Patch. It does
// not execute Lua and never treats a failed parse as an ordinary Patch.
public sealed class LuaPatchComposer
{
	private const int HeaderLength = 0xC0;
	private const int SizePlusEightOffset = 0xA0;
	private const int PayloadSizeOffset = 0xB8;
	private const int ResourceVersionOffset = 0xBC;
	private const uint PatchMagic = 0xF0000011;
	private const string Marker = "--@@HD2LUA-";
	private static readonly UTF8Encoding Utf8 = new(false);

	public LuaPatchComposition Compose(IReadOnlyList<LuaPatchDocument> documents)
	{
		ArgumentNullException.ThrowIfNull(documents);
		if (documents.Count == 0) throw new LuaPatchFormatException("Cannot compose an empty Lua document list.");

		var first = documents[0];
		var bootHash = LuaHash.Sha256(first.BootBytecode);
		var blocks = new List<LuaPatchBlock>();
		foreach (var document in documents)
		{
			if (!first.BootBytecode.AsSpan().SequenceEqual(document.BootBytecode))
				throw new LuaPatchFormatException("Lua patches embed different boot bytecode and cannot be merged safely.");
			if (!LuaAssetKeys.IsBoot(document.Entry.AssetKey))
				throw new LuaPatchFormatException("Lua composition received a non-boot AssetKey.");
			foreach (var block in document.Blocks)
			{
				if (block.Head.Contains(Marker, StringComparison.Ordinal) || block.Body.Contains(Marker, StringComparison.Ordinal)
					|| block.Head.Contains("--@@HD2PB-", StringComparison.Ordinal) || block.Body.Contains("--@@HD2PB-", StringComparison.Ordinal))
					throw new LuaPatchFormatException($"Lua block '{block.Name}' contains reserved merge markers.");
				// Do not deduplicate by body hash. Two Mods may intentionally carry the
				// same body with different setup/banner context; silently dropping one
				// would change the user's requested composition.
				blocks.Add(block);
			}
		}
		if (blocks.Count == 0) throw new LuaPatchFormatException("Lua composition produced no unique bodies.");

		var ledgerBlocks = blocks.Select((block, index) => new LuaMergeLedgerBlock(
			index + 1,
			block.Name,
			block.WrapBootReturn,
			block.HeadSha256,
			block.BodySha256,
			block.SourceModId,
			block.SourcePatchGroup,
			block.BootOkVariable,
			block.BootReturnVariable)).ToArray();
		var ledger = new LuaMergeLedger(
			"HD2LuaMergeV1",
			1,
			LuaAssetKeys.BootArchiveId,
			LuaAssetKeys.LuaTypeId,
			LuaAssetKeys.BootFileId,
			first.BootBytecode.Length,
			bootHash,
			"BootWrapperV1",
			ledgerBlocks);
		var source = BuildSource(first.BootBytecode, blocks, ledger);
		return new LuaPatchComposition(first.HeaderTemplate.ToArray(), first.BootBytecode.ToArray(), source, blocks, ledger);
	}

	public LuaPatchComposition ComposeOpaque(IReadOnlyList<LuaPatchDocument> documents)
	{
		ArgumentNullException.ThrowIfNull(documents);
		if (documents.Count == 0) throw new LuaPatchFormatException("Opaque Lua composition requires at least one document.");
		var first = documents[0];
		var builder = new StringBuilder();
		builder.AppendLine("-- Generated by HD2ModManager opaque Lua compatibility mode.");
		builder.AppendLine("-- This wrapper is not a sandbox; global state and module caches remain shared.");
		for (var index = 0; index < documents.Count; index++)
		{
			var document = documents[index];
			// A bytecode document has no source text on purpose: it is replayed from
			// its bytes, so only a genuinely opaque document must carry text here.
			if (document.IsOpaque && string.IsNullOrWhiteSpace(document.SourceText))
				throw new LuaPatchFormatException("Opaque Lua document has an empty source.");
			var runner = $"__hd2_opaque_runner_{index + 1}";
			var source = document.IsOpaque ? document.SourceText : BuildControlledChunk(document);
			builder.Append("local ").Append(runner).AppendLine(" = function(...)");
			builder.Append(source.TrimEnd('\n')).AppendLine();
			builder.AppendLine("end");
			if (index == documents.Count - 1)
				builder.Append("return ").Append(runner).AppendLine("(...)");
			else
				builder.Append(runner).AppendLine("(...)");
		}
		var ledger = new LuaMergeLedger("HD2LuaOpaqueV1", 1, LuaAssetKeys.BootArchiveId, LuaAssetKeys.LuaTypeId, LuaAssetKeys.BootFileId, 0, string.Empty, "OpaqueFunctionWrapperV1", Array.Empty<LuaMergeLedgerBlock>());
		return new LuaPatchComposition(first.HeaderTemplate.ToArray(), Array.Empty<byte>(), builder.ToString(), Array.Empty<LuaPatchBlock>(), ledger);
	}

	private static string BuildControlledChunk(LuaPatchDocument document)
	{
		var builder = new StringBuilder();
		builder.Append("local BC = \"").Append(EscapeLua(document.BootBytecode)).AppendLine("\"");
		builder.AppendLine("local loader = rawget(_G, \"loadstring\") or rawget(_G, \"load\")");
		builder.AppendLine("local chunk = loader and loader(BC)");
		builder.AppendLine("local boot_ok, boot_ret = false, nil");
		builder.AppendLine("if chunk then boot_ok, boot_ret = pcall(chunk) end");
		foreach (var block in document.Blocks)
		{
			if (!string.IsNullOrWhiteSpace(block.Head)) builder.AppendLine(block.Head);
			if (block.WrapBootReturn)
				builder.Append("local ").Append(block.BootOkVariable).Append(", ").Append(block.BootReturnVariable).AppendLine(" = boot_ok, boot_ret");
			if (!string.IsNullOrWhiteSpace(block.Body)) builder.AppendLine(block.Body);
		}
		builder.AppendLine("return boot_ret");
		return builder.ToString();
	}

	public async ValueTask<LuaPatchArchiveWriteResult> WriteAsync(
		string outputPatchPath,
		LuaPatchComposition composition,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(outputPatchPath);
		ArgumentNullException.ThrowIfNull(composition);
		var output = Path.GetFullPath(outputPatchPath);
		var directory = Path.GetDirectoryName(output);
		if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
		var payload = composition.PayloadBytes;
		if (payload.LongLength > uint.MaxValue - 8L) throw new LuaPatchFormatException("Merged Lua payload is too large for a Patch resource.");
		var header = CreateHeader(composition.HeaderTemplate, payload.LongLength);
		var patchBytes = new byte[header.Length + payload.Length];
		Buffer.BlockCopy(header, 0, patchBytes, 0, header.Length);
		Buffer.BlockCopy(payload, 0, patchBytes, header.Length, payload.Length);
		await File.WriteAllBytesAsync(output, patchBytes, cancellationToken).ConfigureAwait(false);
		await File.WriteAllBytesAsync(output + ".stream", Array.Empty<byte>(), cancellationToken).ConfigureAwait(false);
		await File.WriteAllBytesAsync(output + ".gpu_resources", Array.Empty<byte>(), cancellationToken).ConfigureAwait(false);
		return new LuaPatchArchiveWriteResult(output, output + ".stream", output + ".gpu_resources", patchBytes.LongLength, payload.LongLength);
	}

	private static byte[] CreateHeader(byte[] template, long payloadLength)
	{
		if (template is null || template.Length < HeaderLength) throw new LuaPatchFormatException("Lua Patch header template is shorter than 0xC0 bytes.");
		var header = template.AsSpan(0, HeaderLength).ToArray();
		if (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4)) != PatchMagic)
			throw new LuaPatchFormatException("Lua Patch header template has an invalid magic.");
		BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(SizePlusEightOffset, 8), checked((ulong)payloadLength + 8));
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(PayloadSizeOffset, 4), checked((uint)payloadLength));
		// The template comes from whichever Patch we merged first, so it may still
		// carry the legacy source-text version. The payload we write is always a
		// compiled chunk now, and the game rejects the old version outright.
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(ResourceVersionOffset, 4), LuaResourceFormat.BytecodeVersion);
		return header;
	}

	private static string BuildSource(byte[] boot, IReadOnlyList<LuaPatchBlock> blocks, LuaMergeLedger ledger)
	{
		var builder = new StringBuilder();
		builder.AppendLine("-- Generated by HD2ModManager HD2LuaMergeV1. Do not edit this file.");
		builder.AppendLine("-- Lua bodies are retained verbatim inside independently delimited blocks.");
		builder.Append(Marker).Append("LEDGER ").AppendLine(LuaMergeLedgerSerializer.Serialize(ledger));
		builder.AppendLine("--");
		builder.Append("local BC = \"").Append(EscapeLua(boot)).AppendLine("\"");
		builder.AppendLine("local loader = rawget(_G, \"loadstring\") or rawget(_G, \"load\")");
		builder.AppendLine("local chunk = loader and loader(BC)");
		builder.AppendLine("HD2LUA_BOOT_OK, HD2LUA_BOOT_RET = false, nil");
		builder.AppendLine("if chunk then HD2LUA_BOOT_OK, HD2LUA_BOOT_RET = pcall(chunk) end");
		builder.AppendLine("HD2PB_BOOT_OK, HD2PB_BOOT_RET = HD2LUA_BOOT_OK, HD2LUA_BOOT_RET");
		builder.AppendLine();

		for (var index = 0; index < blocks.Count; index++)
		{
			var block = blocks[index];
			var ordinal = index + 1;
			builder.Append(Marker).Append("BEGIN ").AppendLine(ordinal.ToString());
			builder.Append("do   -- ---- ").Append(block.Name).AppendLine(" ");
			builder.Append(Marker).Append("HEAD ").AppendLine(ordinal.ToString());
			if (!string.IsNullOrEmpty(block.Head)) builder.AppendLine(block.Head);
			if (block.WrapBootReturn)
			{
				builder.Append(Marker).Append("BOOT ").AppendLine(ordinal.ToString());
				builder.Append("local ").Append(block.BootOkVariable).Append(", ").Append(block.BootReturnVariable)
					.AppendLine(" = HD2LUA_BOOT_OK, HD2LUA_BOOT_RET");
			}
			builder.Append(Marker).Append("BODY ").AppendLine(ordinal.ToString());
			if (!string.IsNullOrEmpty(block.Body)) builder.AppendLine(block.Body);
			builder.Append(Marker).Append("END ").AppendLine(ordinal.ToString());
			builder.AppendLine("end");
			builder.AppendLine();
		}
		builder.AppendLine("return HD2LUA_BOOT_RET");
		return builder.ToString();
	}

	private static string EscapeLua(byte[] bytes)
		=> string.Concat(bytes.Select(value => "\\" + value.ToString(System.Globalization.CultureInfo.InvariantCulture)));

}

public sealed record LuaPatchArchiveWriteResult(
	string PatchPath,
	string StreamPath,
	string GpuResourcesPath,
	long PatchLength,
	long PayloadLength);
