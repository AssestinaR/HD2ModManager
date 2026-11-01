using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using FileDecryptor;

namespace HD2ModCore.Infrastructure;

/// <summary>来源件表里的一项。外观映射只关心这四个字段。</summary>
public sealed record ArmorRegistryDataSourcePiece(uint BodyType, uint Slot, uint PieceType, ulong Unit);

/// <summary>
/// 真映射模式的一条映射。
///
/// 来源有两种：<paramref name="SourceItemId"/> 指向一套游戏护甲（件表从注册表里读），
/// 或者 <paramref name="SourcePieces"/> 直接给出件表（自定义素体没有 kit，只能这样传）。
/// </summary>
public sealed record ArmorRegistryDataMapping(
	uint TargetItemId,
	uint? SourceItemId = null,
	IReadOnlyList<ArmorRegistryDataSourcePiece>? SourcePieces = null);

/// <summary>真映射模式的执行结果。</summary>
public sealed record ArmorRegistryDataMappingResult(
	int Applied,
	int Skipped,
	bool Written,
	string? Message = null)
{
	public static readonly ArmorRegistryDataMappingResult Nothing = new(0, 0, false, null);
}

/// <summary>
/// 真映射模式：把外观直接写进 <c>generated_customization_armor_sets.dl_bin</c>。
///
/// 文件的内容校验和是 MurmurHash64A，游戏用**原版文件的哈希**比对，所以改完内容必须把
/// 哈希伪造回原值。求解是闭式的：只要有一个容器忽略的、8 字节对齐的字，它的取值就唯一
/// 确定——件记录尾部 89..111 这 23 字节从未被解码，正好提供它。单次伪造 &lt; 2 ms。
///
/// 信任模型：**备份是唯一可信的原件**。每次应用都从备份重建，因此增删改映射都是幂等的，
/// 也能干净地撤销。备份只会在三个时机重建：首次运行、游戏更新（buildid 变化）、
/// Steam 校验完成之后。
/// </summary>
public sealed class ArmorRegistryDataMapper
{
	public const string FileName = "generated_customization_armor_sets.dl_bin";
	public const string BackupFileName = "generated_customization_armor_sets.original.dl_bin";
	public const string StateFileName = "armor-data-mapping.state";

	const int SubdataHeaderSize = 24;
	const uint ArmorCustomizationTypeId = 0xd9a55aa0;
	const int PieceSize = 112;
	const int BodySize = 24;
	const int PieceFreeStart = 89;
	const ulong Mul = 0xC6A4A7935BD1E995UL;
	const ulong Seed = 16045690983977655786UL;

	sealed record PieceSlot(int Offset, uint BodyType, int BodyIndex, uint Slot, uint PieceType, uint Weight, ulong Unit);
	sealed record BodyInfo(uint BodyType, long PieceOffset, long PieceCount);
	sealed record Kit(uint ItemId, int PayloadStart, int PayloadSize, long BodyOffset, List<BodyInfo> Bodies, List<PieceSlot> Pieces);

	// ---- 哈希 -----------------------------------------------------------------------

	// 模 2^64 的乘法逆元。模数不是素数，费马小定理不适用；牛顿迭代每轮把正确位数翻倍，
	// 六轮到达 64 位。MUL 是奇数，逆元存在。
	static ulong Inverse(ulong v)
	{
		var inv = 1UL;
		for (var i = 0; i < 6; i++) unchecked { inv *= 2 - v * inv; }
		return inv;
	}

	static ulong Mix(ulong v)
	{
		unchecked
		{
			v *= Mul;
			return (v ^ (v >> 47)) * Mul;
		}
	}

	static ulong Unmix(ulong y)
	{
		unchecked
		{
			var u = y * Inverse(Mul);
			u ^= u >> 47;
			return u * Inverse(Mul);
		}
	}

	public static ulong HashContent(ReadOnlySpan<byte> data)
	{
		var whole = data.Length / 8 * 8;
		var h = Seed ^ ((ulong)data.Length * Mul);
		for (var at = 0; at < whole; at += 8)
			unchecked { h = (h ^ Mix(BinaryPrimitives.ReadUInt64LittleEndian(data[at..]))) * Mul; }
		if (whole < data.Length)
		{
			ulong tail = 0;
			for (var i = 0; i < data.Length - whole; i++) tail |= (ulong)data[whole + i] << (8 * i);
			unchecked { h = (h ^ tail) * Mul; }
		}
		h ^= h >> 47;
		unchecked { h *= Mul; }
		return h ^ (h >> 47);
	}

	static ulong StateBefore(ReadOnlySpan<byte> data, int slot)
	{
		var h = Seed ^ ((ulong)data.Length * Mul);
		for (var at = 0; at < slot; at += 8)
			unchecked { h = (h ^ Mix(BinaryPrimitives.ReadUInt64LittleEndian(data[at..]))) * Mul; }
		return h;
	}

	static ulong StateAfter(ReadOnlySpan<byte> data, int slot, ulong target)
	{
		var inv = Inverse(Mul);
		var h = target ^ (target >> 47);
		unchecked { h *= inv; }
		h ^= h >> 47;
		var whole = data.Length / 8 * 8;
		if (whole < data.Length)
		{
			ulong tail = 0;
			for (var i = 0; i < data.Length - whole; i++) tail |= (ulong)data[whole + i] << (8 * i);
			unchecked { h = h * inv ^ tail; }
		}
		for (var at = whole - 8; at > slot; at -= 8)
			unchecked { h = h * inv ^ Mix(BinaryPrimitives.ReadUInt64LittleEndian(data[at..])); }
		return h;
	}

	// ---- 结构 -----------------------------------------------------------------------

	static List<Kit> ParseKits(byte[] plain)
	{
		var kits = new List<Kit>();
		var position = 4;
		while (position + SubdataHeaderSize <= plain.Length)
		{
			if (!new ReadOnlySpan<byte>(plain, position, 4).SequenceEqual("LDLD"u8)) break;
			var type = BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(position + 8));
			var size = BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(position + 12));
			var payloadStart = position + SubdataHeaderSize;
			if (payloadStart + size > plain.Length) break;
			if (type == ArmorCustomizationTypeId && size >= 64)
			{
				var payload = plain.AsSpan(payloadStart, (int)size);
				var itemId = BinaryPrimitives.ReadUInt32LittleEndian(payload);
				var bodyOffset = BinaryPrimitives.ReadInt64LittleEndian(payload[48..]);
				var bodyCount = BinaryPrimitives.ReadInt64LittleEndian(payload[56..]);
				var bodies = new List<BodyInfo>();
				var pieces = new List<PieceSlot>();
				for (var b = 0; b < bodyCount; b++)
				{
					var bodyAt = (int)bodyOffset + b * BodySize;
					if (bodyAt + BodySize > payload.Length) break;
					var bodyType = BinaryPrimitives.ReadUInt32LittleEndian(payload[bodyAt..]);
					var pieceOffset = BinaryPrimitives.ReadInt64LittleEndian(payload[(bodyAt + 8)..]);
					var pieceCount = BinaryPrimitives.ReadInt64LittleEndian(payload[(bodyAt + 16)..]);
					bodies.Add(new BodyInfo(bodyType, pieceOffset, pieceCount));
					for (var p = 0; p < pieceCount; p++)
					{
						var at = payloadStart + (int)pieceOffset + p * PieceSize;
						if (at + PieceSize > plain.Length) break;
						pieces.Add(new PieceSlot(
							at, bodyType, b,
							BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(at + 8)),
							BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(at + 12)),
							BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(at + 16)),
							BinaryPrimitives.ReadUInt64LittleEndian(plain.AsSpan(at))));
					}
				}
				kits.Add(new Kit(itemId, payloadStart, (int)size, bodyOffset, bodies, pieces));
			}
			position = payloadStart + (int)size;
		}
		return kits;
	}

	/// <summary>
	/// 件记录的字段来源规则：**只有 unit / slot / piece_type 取自来源**，其余一律沿用目标。
	///
	///   +0 unit、+8 slot、+12 piece_type —— 这就是"映射"本身；
	///   +16 weight —— 玩法字段（护甲等级），必须留目标的；
	///   +24..+80 资源哈希、+88 tone —— 属于目标这套护甲自己的配色，留目标的；
	///   +20..+23、+89..+111 —— 未解码/保留，留目标的。
	///
	/// 目标没有同槽位件时，用最接近的结构邻居当模板（同 body 同 slot → 同 body）。
	/// </summary>
	static PieceSlot? FindTemplate(Kit target, ArmorRegistryDataSourcePiece sourcePiece)
	{
		PieceSlot? exact = null;
		PieceSlot? sameSlot = null;
		PieceSlot? sameBody = null;
		foreach (var piece in target.Pieces)
		{
			if (piece.BodyType != sourcePiece.BodyType) continue;
			sameBody ??= piece;
			if (piece.Slot != sourcePiece.Slot) continue;
			sameSlot ??= piece;
			if (piece.PieceType != sourcePiece.PieceType) continue;
			exact = piece;
			break;
		}
		return exact ?? sameSlot ?? sameBody;
	}

	/// <summary>
	/// 用来源的件表重建目标套件：header + bodies + 连续件数组。
	/// bodyOffset 恒为 64，第一个件数组紧接在 body 数组之后——这是游戏自己的排布。
	/// </summary>
	/// <summary>
	/// 用来源件表替换目标套件的件表：header + bodies + 连续件数组。
	/// <para>
	/// <b>这是"替换"不是"叠加"。</b>映射完成后这套装备只应有来源的件（加上披风那一格占位），
	/// 目标自己的其余件（别的层、我们没有的部位）一律不写——留着就成了缝合怪。空行也不写。
	/// </para>
	/// <para>
	/// <b>槽位族必须按套件分开。</b>自定义素体是一个包着「7 个部位 + 1 个头盔」的整体，
	/// 而游戏里护甲套件与头盔套件是两个独立装备。把整套件表原样铺进去，头盔件会落进护甲套件
	/// （装身体也冒出头盔）、身体件会落进头盔套件（装头盔也冒出身体）。所以这里先看目标套件
	/// 自己有哪些槽位族，只接受属于该族的来源件。
	/// </para>
	/// </summary>
	static byte[] CloneKit(byte[] plain, Kit target, IReadOnlyList<ArmorRegistryDataSourcePiece> sourcePieces)
	{
		const uint CapeSlot = 1;
		var headerSize = (int)target.BodyOffset;
		var targetHasHelmet = target.Pieces.Any(piece => piece.Slot == 0);
		var targetHasBody = target.Pieces.Any(piece => piece.Slot >= 2 && piece.Slot <= 9);
		var wanted = sourcePieces
			.Where(piece => (piece.Slot == 0 && targetHasHelmet)
				|| (piece.Slot >= 2 && piece.Slot <= 9 && targetHasBody))
			.ToArray();

		// 逐条身体行收集；空行直接丢弃（不进入 kept），最后同步写回 bodyCount。
		var kept = new List<(int OriginalIndex, uint BodyType, List<byte[]> Records)>();
		for (var b = 0; b < target.Bodies.Count; b++)
		{
			var body = target.Bodies[b];
			// body 精确匹配 + Any(3) 展开进每一行；同一 (slot, layer) 只留一条，精确 body 优先于 Any。
			var chosen = wanted
				.Where(piece => piece.BodyType == body.BodyType || piece.BodyType == 3)
				.GroupBy(piece => (piece.Slot, piece.PieceType))
				.Select(group => group.OrderBy(piece => piece.BodyType == 3 ? 1 : 0).First())
				.OrderBy(piece => piece.Slot).ThenBy(piece => piece.PieceType)
				.ToList();

			var records = new List<byte[]>(chosen.Count + 1);
			foreach (var sourcePiece in chosen)
			{
				var template = FindTemplate(target, sourcePiece);
				var record = new byte[PieceSize];
				// 先把目标模板整条抄过来（拿到目标的 weight / 资源哈希 / tone / 保留区）。
				// 目标完全没有该部位时 record 保持全零——那是"给这个槽位新建一条"的情形，
				// 注册表是权威来源，别的护甲允许这个槽位存在，引擎就会接受它。
				if (template is not null)
					Array.Copy(plain, template.Offset, record, 0, PieceSize);
				// 只覆盖映射真正要换的三个字段。
				BinaryPrimitives.WriteUInt64LittleEndian(record, sourcePiece.Unit);
				BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(8), sourcePiece.Slot);
				BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(12), sourcePiece.PieceType);
				records.Add(record);
			}

			// 披风是独立装备、只占位，按约定原样保留这一行里的披风件。
			foreach (var cape in target.Pieces.Where(piece => piece.Slot == CapeSlot && piece.BodyType == body.BodyType))
			{
				var record = new byte[PieceSize];
				Array.Copy(plain, cape.Offset, record, 0, PieceSize);
				records.Add(record);
			}

			// 空行会让军械库遍历身形时拿到空数组而崩溃，直接不写这一行。
			if (records.Count == 0) continue;
			kept.Add((b, body.BodyType, records));
		}

		// 一个件都给不出来（来源里没有属于这套装备的槽位）时**原样不动**：
		// 把整份装备清成空壳比"没映射上"更糟，而且空表本身就是军械库崩溃的来源。
		if (kept.Count == 0)
		{
			var untouched = new byte[target.PayloadSize];
			Array.Copy(plain, target.PayloadStart, untouched, 0, target.PayloadSize);
			return untouched;
		}

		var totalPieces = kept.Sum(entry => entry.Records.Count);
		var payload = new byte[headerSize + kept.Count * BodySize + totalPieces * PieceSize];
		Array.Copy(plain, target.PayloadStart, payload, 0, headerSize);
		// 行数变了就必须写回：bodyOffset 恒为 64，bodyCount 紧跟其后（见 ParseKits）。
		BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(48), headerSize);
		BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(56), kept.Count);

		var cursor = headerSize + kept.Count * BodySize;
		for (var i = 0; i < kept.Count; i++)
		{
			var srcBodyAt = target.PayloadStart + headerSize + kept[i].OriginalIndex * BodySize;
			var dstBodyAt = headerSize + i * BodySize;
			// 整条 24 字节抄过来（保留未知字段），再覆盖三个已知字段。
			Array.Copy(plain, srcBodyAt, payload, dstBodyAt, BodySize);
			BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(dstBodyAt), kept[i].BodyType);
			BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(dstBodyAt + 8), cursor);
			BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(dstBodyAt + 16), kept[i].Records.Count);
			foreach (var record in kept[i].Records)
			{
				Array.Copy(record, 0, payload, cursor, PieceSize);
				cursor += PieceSize;
			}
		}

		var result = new byte[plain.Length - target.PayloadSize + payload.Length];
		Array.Copy(plain, 0, result, 0, target.PayloadStart);
		Array.Copy(payload, 0, result, target.PayloadStart, payload.Length);
		Array.Copy(plain, target.PayloadStart + target.PayloadSize, result, target.PayloadStart + payload.Length,
			plain.Length - target.PayloadStart - target.PayloadSize);
		BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(target.PayloadStart - 12), (uint)payload.Length);
		return result;
	}

	static bool SamePieceSet(Kit target, IReadOnlyList<ArmorRegistryDataSourcePiece> sourcePieces)
		=> target.Pieces.Count == sourcePieces.Count
			&& target.Pieces.Select(p => (p.BodyType, p.Slot, p.PieceType, p.Unit))
				.OrderBy(k => k)
				.SequenceEqual(sourcePieces
					.Select(p => (p.BodyType, p.Slot, p.PieceType, p.Unit))
					.OrderBy(k => k));

	/// <summary>把哈希伪造回 <paramref name="target"/>：解出一个对齐自由字，其余字节不动。</summary>
	static bool TryForge(byte[] edited, ulong target)
	{
		if (HashContent(edited) == target) return true;
		foreach (var kit in ParseKits(edited))
		{
			foreach (var piece in kit.Pieces)
			{
				var freeStart = piece.Offset + PieceFreeStart;
				var freeEnd = piece.Offset + PieceSize;
				var slot = (freeStart + 7) / 8 * 8;
				if (slot + 8 > freeEnd) continue;
				var stateBefore = StateBefore(edited, slot);
				var need = StateAfter(edited, slot, target);
				ulong solved;
				unchecked { solved = Unmix(((need * Inverse(Mul)) & ulong.MaxValue) ^ stateBefore); }
				var previous = BinaryPrimitives.ReadUInt64LittleEndian(edited.AsSpan(slot));
				BinaryPrimitives.WriteUInt64LittleEndian(edited.AsSpan(slot), solved);
				if (HashContent(edited) == target) return true;
				BinaryPrimitives.WriteUInt64LittleEndian(edited.AsSpan(slot), previous);
			}
		}
		return false;
	}

	// ---- 对外 -----------------------------------------------------------------------

	/// <summary>
	/// 丢弃记录的原始备份与状态，让下一次应用以当前文件为新基准。
	/// </summary>
	public static void ClearBaseline(string? registryRoot = null)
	{
		try
		{
			var directory = Path.Combine(
				registryRoot ?? RuntimeArmorRegistryService.ResolveRegistryRoot(),
				"backup");
			File.Delete(Path.Combine(directory, StateFileName));
			File.Delete(Path.Combine(directory, BackupFileName));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// 删不掉不影响主流程：下次应用会因状态缺失而重新播种。
		}
	}

	/// <summary>
	/// 丢弃旧基准，并**立刻**用当前位置的文件重建一份新备份。
	///
	/// 这是 Steam 校验完成后的动作：那一刻游戏文件一定是官方版本，是整条链路里唯一
	/// 可信的时刻。旧备份和旧状态一律不可信，必须整个换掉。
	/// </summary>
	public static async ValueTask<ArmorRegistryDataMappingResult> ResetBaselineAsync(
		string gameDataDirectory,
		CancellationToken cancellationToken = default)
	{
		ClearBaseline();
		return await new ArmorRegistryDataMapper().ApplyAsync(
			gameDataDirectory,
			Array.Empty<ArmorRegistryDataMapping>(),
			Path.Combine(RuntimeArmorRegistryService.ResolveRegistryRoot(), "backup"),
			cancellationToken).ConfigureAwait(false);
	}

	public static string ResolveTargetPath(string gameDataDirectory)
	{
		var nested = Path.Combine(gameDataDirectory, "game", FileName);
		return File.Exists(nested) ? nested : Path.Combine(gameDataDirectory, FileName);
	}

	/// <summary>
	/// 用 <paramref name="mappings"/> 重建注册表数据。始终从原始备份出发，所以重复调用结果
	/// 一致，删掉映射也会还原。
	/// </summary>
	public async ValueTask<ArmorRegistryDataMappingResult> ApplyAsync(
		string gameDataDirectory,
		IReadOnlyList<ArmorRegistryDataMapping> mappings,
		string backupDirectory,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(mappings);
		if (string.IsNullOrWhiteSpace(gameDataDirectory)) return ArmorRegistryDataMappingResult.Nothing;
		var targetPath = ResolveTargetPath(gameDataDirectory);
		if (!File.Exists(targetPath))
			return new ArmorRegistryDataMappingResult(0, mappings.Count, false, "找不到护甲注册表数据文件。");

		Directory.CreateDirectory(backupDirectory);
		var backupPath = Path.Combine(backupDirectory, BackupFileName);
		var statePath = Path.Combine(backupDirectory, StateFileName);
		var current = await File.ReadAllBytesAsync(targetPath, cancellationToken).ConfigureAwait(false);

		var currentHash = Convert.ToHexString(SHA256.HashData(current));
		var recorded = File.Exists(statePath)
			? (await File.ReadAllTextAsync(statePath, cancellationToken).ConfigureAwait(false)).Trim()
			: null;

		// 游戏版本：备份只对同一个版本有效。读不到 manifest 时跳过这条判据。
		var currentBuildId = SteamAppManifest
			.TryRead(SteamAppManifest.ResolveManifestPath(gameDataDirectory))?.TrustedBuildId;
		var recordedParts = recorded?.Split('|');
		var recordedPristine = recordedParts is { Length: > 0 } ? recordedParts[0] : null;
		var recordedWritten = recordedParts is { Length: > 1 } ? recordedParts[1] : null;
		var recordedBuild = recordedParts is { Length: > 3 } ? recordedParts[3] : null;

		// 状态说"当前文件是我们写的"，但备份不见了：原件已经丢了。此时绝不能拿被改过的
		// 文件当新基准——那会把改动固化成"原版"，之后再也分不清哪份才是官方的。
		var stateSaysOurs = recordedWritten is { Length: > 0 }
			&& string.Equals(currentHash, recordedWritten, StringComparison.OrdinalIgnoreCase);
		if (stateSaysOurs && !File.Exists(backupPath))
		{
			return new ArmorRegistryDataMappingResult(0, mappings.Count, false,
				"原始备份丢失，而当前注册表是被改写过的版本。请先执行「Steam 校验完整性」恢复官方文件。");
		}

		// 备份的归属判定。任一成立就按当前文件重建基准：
		//   1. 还没有备份；
		//   2. 没有状态文件（无法证明备份属于哪个原始文件）；
		//   3. buildid 变了——游戏更新过，旧备份必然过期（这条是文件哈希看不出来的）；
		//   4. 当前文件既不是备份、也不是我们写的（Steam 校验 / 手动换文件）。
		var reseed =
			!File.Exists(backupPath)
			|| recorded is null
			|| (recordedBuild is { Length: > 0 }
				&& currentBuildId is { Length: > 0 }
				&& !string.Equals(recordedBuild, currentBuildId, StringComparison.Ordinal))
			|| (!string.Equals(currentHash, recordedPristine, StringComparison.OrdinalIgnoreCase)
				&& !string.Equals(currentHash, recordedWritten, StringComparison.OrdinalIgnoreCase));
		if (reseed)
			await File.WriteAllBytesAsync(backupPath, current, cancellationToken).ConfigureAwait(false);

		var pristine = await File.ReadAllBytesAsync(backupPath, cancellationToken).ConfigureAwait(false);
		var pristineSha = Convert.ToHexString(SHA256.HashData(pristine));

#pragma warning disable CS0618
		var decryptor = Decryptor.CreateWithEmbeddedKeys();
#pragma warning restore CS0618
		var plain = decryptor.DecodeData(pristine);
		var contentHash = HashContent(plain);
		// 供 ArmorDataFileIdentity 使用：让索引指纹看到"未改动"的内容身份。
		var pristineContent = Convert.ToHexString(SHA256.HashData(plain)).ToLowerInvariant();

		var applied = 0;
		var skipped = 0;
		var edited = plain;
		foreach (var mapping in mappings)
		{
			// 每次重建后偏移都会变，重新解析一次，保证后续映射仍落在正确位置。
			var currentKits = ParseKits(edited)
				.GroupBy(kit => kit.ItemId)
				.ToDictionary(group => group.Key, group => group.First());
			if (!currentKits.TryGetValue(mapping.TargetItemId, out var targetKit))
			{
				skipped++;
				continue;
			}

			// 来源有两种：显式件表（自定义素体）或一套游戏护甲。
			IReadOnlyList<ArmorRegistryDataSourcePiece> sourcePieces;
			if (mapping.SourcePieces is { Count: > 0 })
			{
				sourcePieces = mapping.SourcePieces;
			}
			else if (mapping.SourceItemId is uint sourceId && currentKits.TryGetValue(sourceId, out var sourceKit))
			{
				sourcePieces = sourceKit.Pieces
					.Select(piece => new ArmorRegistryDataSourcePiece(
						piece.BodyType, piece.Slot, piece.PieceType, piece.Unit))
					.ToArray();
			}
			else
			{
				skipped++;
				continue;
			}

			// 目标件集已经与来源一致时不必重写。
			if (SamePieceSet(targetKit, sourcePieces)) { skipped++; continue; }
			edited = CloneKit(edited, targetKit, sourcePieces);
			applied++;
		}

		// 没有任何改动时把游戏文件还原成原始备份（支持"删掉映射就撤销"）。
		if (applied == 0)
		{
			// 只有当前文件**确实是我们写的那份**才用备份还原。若它既不是备份、也不是我们
			// 写的，说明文件被外部替换过（游戏更新 / Steam 校验完整性）。这时把旧备份写
			// 回去，等于用一份可能已被污染的基准覆盖干净文件。
			var weWroteIt = recordedWritten is { Length: > 0 }
				&& string.Equals(currentHash, recordedWritten, StringComparison.OrdinalIgnoreCase);
			if (weWroteIt && !string.Equals(currentHash, pristineSha, StringComparison.OrdinalIgnoreCase))
			{
				await WriteAtomicAsync(targetPath, pristine, cancellationToken).ConfigureAwait(false);
				await WriteStateAsync(statePath, pristineSha, pristineSha, pristineContent, currentBuildId,
					gameDataDirectory, cancellationToken).ConfigureAwait(false);
				return new ArmorRegistryDataMappingResult(0, skipped, true, "已还原原始注册表数据。");
			}
			return new ArmorRegistryDataMappingResult(0, skipped, false, null);
		}

		if (!TryForge(edited, contentHash))
			return new ArmorRegistryDataMappingResult(applied, skipped, false, "无法把内容校验和伪造回原值，已放弃写入。");

		// 结果与磁盘上已有的完全一致时不重写：反复应用同一份映射不应产生任何 I/O。
		// 只能比较明文——NaCl 密封箱每次加密都会重新生成临时密钥，密文永远不同。
		var currentPlain = decryptor.DecodeData(current);
		if (currentPlain.AsSpan().SequenceEqual(edited))
		{
			await WriteStateAsync(statePath, pristineSha, currentHash, pristineContent, currentBuildId,
				gameDataDirectory, cancellationToken).ConfigureAwait(false);
			return new ArmorRegistryDataMappingResult(applied, skipped, false, null);
		}

		var sealedBytes = decryptor.EncodeData(edited);
		await WriteAtomicAsync(targetPath, sealedBytes, cancellationToken).ConfigureAwait(false);
		var sealedSha = Convert.ToHexString(SHA256.HashData(sealedBytes));
		await WriteStateAsync(statePath, pristineSha, sealedSha, pristineContent, currentBuildId,
			gameDataDirectory, cancellationToken).ConfigureAwait(false);
		return new ArmorRegistryDataMappingResult(applied, skipped, true, null);
	}

	/// <summary>状态文件：<c>原始密文SHA|写出密文SHA|原始内容SHA|buildid|游戏数据目录</c>。</summary>
	static async ValueTask WriteStateAsync(
		string statePath,
		string pristineSha,
		string writtenSha,
		string pristineContent,
		string? buildId,
		string dataDirectory,
		CancellationToken cancellationToken)
		=> await File.WriteAllTextAsync(
			statePath,
			$"{pristineSha}|{writtenSha}|{pristineContent}|{buildId ?? string.Empty}|{dataDirectory}",
			new UTF8Encoding(false),
			cancellationToken).ConfigureAwait(false);

	static async ValueTask WriteAtomicAsync(string path, byte[] content, CancellationToken cancellationToken)
	{
		var temp = path + ".tmp";
		await File.WriteAllBytesAsync(temp, content, cancellationToken).ConfigureAwait(false);
		File.Move(temp, path, overwrite: true);
	}
}
