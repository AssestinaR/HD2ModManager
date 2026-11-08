using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using FileDecryptor;
using HD2ModCore.Domain;

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

/// <summary>真映射模式的执行结果（类型在 <c>HD2ModCore.Domain</c>，因为要放进 ApplyResult）。</summary>

/// <summary>
/// 真映射模式：把外观直接写进 <c>generated_customization_armor_sets.dl_bin</c>。
///
/// <para>
/// <b>只改件记录第 0 字节起那 8 个 unit。别的 104 字节一个都不动。</b>
/// 这条约束是整个实现的地基，它一次性消掉了四大类事故：
/// </para>
/// <list type="number">
/// <item><b>护甲值被改。</b><c>+16 weight</c>（轻/中/重 = 50/100/150 护甲值）属于玩法字段，
/// 只有"整条记录从别处拷"才会连带改掉它 —— 现在没有任何一条代码路径会写它。</item>
/// <item><b>配色/花纹被改。</b><c>+24..80</c> 八个资源哈希（MaterialLut / PatternLut / …）同理。</item>
/// <item><b>第二张表被删。</b>件记录 <c>+96..103</c> 是指向 payload 内另一张表的偏移、
/// <c>+104</c> 是条目数（实测 6 套护甲/头盔带这张表）。只要不重建 payload，偏移永远有效。</item>
/// <item><b>撞 20 件预算。</b>原生消费者把每个 (套件, 身形) 的件收进一个 20 项定长数组，
/// 第 21 件起静默丢弃，而行序是 Any 在前 —— 所以"多写件"必然先挤掉身形专属的身体件。
/// 件数一个都不增，这条从"要小心"变成"不会发生"。</item>
/// </list>
///
/// <para>
/// <b>覆盖语义（用户口径 2026-11-08）：目标套件里每一个格子都要有结论。</b>
/// 来源覆盖到的格子写来源的 unit；覆盖不到的一律写 <c>0</c>（隐藏）。
/// 例如素体没有头盔件时，头盔套件的 slot 0 会被写成 0 —— 否则会露出原版头盔，
/// 那就成了"素体 + 原版头盔"的缝合怪。写 0 是游戏自己的写法：123 套披风套件里
/// 有 118 套的披风格就是 <c>unit = 0</c>，记录照样留着。
/// </para>
///
/// <para>
/// 文件的内容校验和是 MurmurHash64A，游戏用**原版文件的哈希**比对，所以改完内容必须把
/// 哈希伪造回原值。求解是闭式的：只要有一个容器忽略的、8 字节对齐的字，它的取值就唯一
/// 确定。**这个字放在扩充后的 payload 末尾**（见 <see cref="AppendForgeWord"/>），
/// 不再借用件记录里的任何字节。
/// </para>
///
/// <para>
/// 信任模型：**备份是唯一可信的原件**。每次应用都从备份重建，因此增删改映射都是幂等的，
/// 也能干净地撤销。备份只会在三个时机重建：首次运行、游戏更新（buildid 变化）、
/// Steam 校验完成之后。
/// </para>
/// </summary>
public sealed class ArmorRegistryDataMapper
{
	public const string FileName = "generated_customization_armor_sets.dl_bin";
	public const string BackupFileName = "generated_customization_armor_sets.original.dl_bin";

	const int SubdataHeaderSize = 24;
	const uint ArmorCustomizationTypeId = 0xd9a55aa0;
	const int PieceSize = 112;
	const int BodySize = 24;
	/// <summary>原始身形编码里"通用"的值。它不是第三种身形，而是与 Stocky(0)/Slim(1) 互斥的写法。</summary>
	const uint AnyBodyType = 3;
	/// <summary>伪造用的自由字宽度。一个 64 位未知量才有闭式解；7 字节（56 位）不够。</summary>
	const int ForgeWordSize = 8;
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
		// ⚠️ **起点不能写死成 4。** 这个文件的明文是「4 字节前缀 + 若干 LDLD 子数据块」，
		// 但前缀长度属于**未公开格式**，游戏更新完全可能改它。写死会让整个解析循环在第一次
		// 迭代就 break，返回 0 个套件 —— 而调用方只会看到"目标护甲找不到"，把一次
		// "读不懂文件"伪装成"映射配错了"（实测 2026-11-05：237 条映射全部跳过）。
		// 所以在前 64 字节里找第一个 LDLD 当起点。
		var position = FindFirstSubdataHeader(plain);
		if (position < 0) return kits;
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
	/// 明文里第一个 <c>LDLD</c> 子数据块的位置；找不到返回 -1。
	/// <para>只看前 64 字节：前缀是定长小结构，扫太远会把某段碰巧等于 "LDLD" 的数据当起点。</para>
	/// </summary>
	private static int FindFirstSubdataHeader(byte[] plain)
	{
		var limit = Math.Min(plain.Length - 4, 64);
		for (var offset = 0; offset <= limit; offset++)
		{
			if (plain[offset] == (byte)'L' && plain[offset + 1] == (byte)'D'
				&& plain[offset + 2] == (byte)'L' && plain[offset + 3] == (byte)'D')
			{
				return offset;
			}
		}

		return -1;
	}

	// ---- 格子取值 -------------------------------------------------------------------

	/// <summary>
	/// 来源件表按 <c>(身形, 槽位, 层)</c> 建索引。<c>Any</c> 是"两身形共用"那一行，
	/// 与 Stocky/Slim **互斥**（原生 138/138 套零共存）。
	/// </summary>
	static Dictionary<(uint Body, uint Slot, uint PieceType), ulong> IndexSource(
		IReadOnlyList<ArmorRegistryDataSourcePiece> pieces)
	{
		var index = new Dictionary<(uint, uint, uint), ulong>(pieces.Count);
		foreach (var piece in pieces)
		{
			// 同一格出现两件时先来的赢。协议层已经禁止同格重复，这里只是不让坏输入炸掉。
			index.TryAdd((piece.BodyType, piece.Slot, piece.PieceType), piece.Unit);
		}
		return index;
	}

	/// <summary>
	/// 单个格子的取值。<paramref name="bodyType"/> 是**目标那一行**的身形，不是来源的身形。
	/// <para>
	/// 顺序：① 同身形同格 → ② 共用(Any)格 → ③ 目标是共用行时接受来源按身形拆出来的件
	/// → ④ 都没有就返回 0（隐藏）。
	/// </para>
	/// <para>
	/// <b>③ 为什么要容忍。</b>原生把腿、披风、臂内层放在共用行（因为它们的网格不随身形变），
	/// 而素体可能把同一个槽位声明成按身形两份。这时共用格表达不了"两份不同的网格"：
	/// 两份相同就照用，不同就让这一格隐藏 —— 绝不挑一份塞给两种身形（那会让一半身形穿错网格）。
	/// 只声明了一份的，说明这个网格两种身形通用，直接用。
	/// </para>
	/// </summary>
	static ulong ResolveUnit(
		Dictionary<(uint Body, uint Slot, uint PieceType), ulong> index,
		uint bodyType, uint slot, uint pieceType)
	{
		if (index.TryGetValue((bodyType, slot, pieceType), out var unit)) return unit;
		if (bodyType != AnyBodyType && index.TryGetValue((AnyBodyType, slot, pieceType), out unit)) return unit;
		if (bodyType == AnyBodyType)
		{
			var hasStocky = index.TryGetValue((0u, slot, pieceType), out var stocky);
			var hasSlim = index.TryGetValue((1u, slot, pieceType), out var slim);
			if (hasStocky && hasSlim) return stocky == slim ? stocky : 0UL;
			if (hasStocky) return stocky;
			if (hasSlim) return slim;
		}
		return 0UL;
	}

	// ---- 伪造 -----------------------------------------------------------------------

	/// <summary>
	/// 给最后一条 subdata 块的 payload 追加一个**8 字节对齐的自由字**，返回新缓冲区与字偏移。
	/// <para>
	/// <b>为什么必须动文件长度。</b>件记录里没有任何连续 8 个自由字节：<c>+89..95</c> 与
	/// <c>+105..111</c> 各只有 7 字节（56 位 &lt; 64 位，闭式解不成立），中间 <c>+96..104</c>
	/// 是第二张表的偏移与条目数（实测 6 套带真实数据）。旧实现按 <c>+89</c> 对齐求解，
	/// 落点是 <c>件+92..99</c> —— 每次都写脏第二张表偏移的低 4 字节。
	/// </para>
	/// <para>
	/// <b>为什么追加在末尾是安全的。</b>块的解析只依赖 <c>size</c> 与 body/piece 的偏移计数，
	/// payload 末尾多出来的字节永远不会被读到；实测原生文件里就有 9 套 payload 带
	/// 64 / 960 / 1408 / 1536 字节的多余数据，游戏正常。
	/// </para>
	/// </summary>
	static (byte[] Buffer, int WordOffset) AppendForgeWord(byte[] plain, Kit lastKit)
	{
		var payloadEnd = lastKit.PayloadStart + lastKit.PayloadSize;
		if (payloadEnd != plain.Length)
		{
			throw new InvalidDataException(
				"注册表末尾还有解析不到的多余数据，无法安放校验和占位字，已放弃写入。");
		}

		var pad = (8 - (payloadEnd % 8)) % 8;
		var wordOffset = payloadEnd + pad;
		var grew = plain.Length + pad + ForgeWordSize;
		var buffer = new byte[grew];
		Array.Copy(plain, buffer, plain.Length);
		// 块头就在 payload 前面 24 字节；size 字段在块头 +12。
		BinaryPrimitives.WriteUInt32LittleEndian(
			buffer.AsSpan(lastKit.PayloadStart - SubdataHeaderSize + 12),
			(uint)(lastKit.PayloadSize + pad + ForgeWordSize));
		return (buffer, wordOffset);
	}

	/// <summary>把内容校验和伪造回 <paramref name="target"/>：只解那一个自由字，其余字节不动。</summary>
	static bool TrySolveForgeWord(byte[] data, int slot, ulong target)
	{
		if (HashContent(data) == target) return true;
		var stateBefore = StateBefore(data, slot);
		var need = StateAfter(data, slot, target);
		ulong solved;
		unchecked { solved = Unmix(need * Inverse(Mul) ^ stateBefore); }
		BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(slot), solved);
		return HashContent(data) == target;
	}

	// ---- 对外 -----------------------------------------------------------------------

	/// <summary>
	/// 丢弃记录的原始备份与状态，让下一次应用以当前文件为新基准。
	/// <para>
	/// <b>只在「Steam 校验完整性之后」调用。</b>那一刻游戏文件一定是官方版本，是整条链里
	/// 唯一能确认"这就是原件"的时刻。平时调用会把一份可能被改过的文件当成原件。
	/// </para>
	/// </summary>
	public static void ClearBaseline(string? registryRoot = null)
	{
		try
		{
			var directory = Path.Combine(
				registryRoot ?? RuntimeArmorRegistryService.ResolveRegistryRoot(),
				"backup");
			File.Delete(Path.Combine(directory, BackupFileName));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// 删不掉不影响主流程：下一次应用会重新记一份原件。
		}
	}

	/// <summary>
	/// Steam 校验完成后的动作：把当前 dl_bin 记为原件，并按空结构编译一次（= 还原成原版）。
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

	/// <summary>一次编译的结果。</summary>
	/// <param name="Sealed">编译出来的密文（<see cref="Changed"/> 为假时等于基底）。</param>
	/// <param name="Changed">明文是否真的变了。没变就不必写盘。</param>
	/// <param name="Hidden">被写成 0（隐藏）的格子数。</param>
	/// <param name="Replaced">换成本源 unit 的格子数。</param>
	public sealed record ArmorRegistryCompileOutcome(
		int Applied,
		int Skipped,
		byte[] Sealed,
		bool Changed,
		int Hidden = 0,
		int Replaced = 0);

	/// <summary>
	/// **编译**：以 <paramref name="baseSealed"/>（原件密文）为基底，把 <paramref name="mappings"/>
	/// 叠上去，返回新的密文。纯函数：不读写任何文件。
	/// <para>
	/// 这是「应用」的全部逻辑。**没有状态、没有哈希协商、没有"这个文件是不是我们写的"** ——
	/// 受管 dl_bin 是派生事实：随时可以丢、随时可以重算，所以不需要记住它曾经是什么样。
	/// </para>
	/// <para>
	/// 结构（哪个 kit、哪些 piece、偏移在哪）来自<b>基底文件本身</b>；这正是"原件是编译输入"的
	/// 含义。数据库那份结构是给界面看的事实，不参与这里的字节写入。
	/// </para>
	/// </summary>
	/// <exception cref="InvalidDataException">基底不是可识别的护甲注册表文件。</exception>
	public static ArmorRegistryCompileOutcome Compile(
		byte[] baseSealed,
		IReadOnlyList<ArmorRegistryDataMapping> mappings)
	{
		ArgumentNullException.ThrowIfNull(baseSealed);
		ArgumentNullException.ThrowIfNull(mappings);
#pragma warning disable CS0618
		var decryptor = Decryptor.CreateWithEmbeddedKeys();
#pragma warning restore CS0618
		var plain = decryptor.DecodeData(baseSealed);
		var contentHash = HashContent(plain);

		var kits = ParseKits(plain);
		if (kits.Count == 0)
			throw new InvalidDataException("这份 dl_bin 解析不出任何护甲套件：它不是可识别的护甲注册表文件。");

		// 偏移永远不变（件数、顺序、payload 长度都不动），所以只解析一次。
		var byItem = new Dictionary<uint, Kit>(kits.Count);
		foreach (var kit in kits) byItem.TryAdd(kit.ItemId, kit);

		var applied = 0;
		var skipped = 0;
		var hidden = 0;
		var replaced = 0;
		var edited = (byte[])plain.Clone();
		foreach (var mapping in mappings)
		{
			if (!byItem.TryGetValue(mapping.TargetItemId, out var targetKit))
			{
				skipped++;
				continue;
			}

			// 来源有两种：显式件表（自定义素体）或一套游戏护甲。游戏护甲一律读**基底**里的那份 ——
			// 映射之间互不影响，A 改了外观不该连带改掉"B 拿 A 当来源"的结果。
			IReadOnlyList<ArmorRegistryDataSourcePiece> sourcePieces;
			if (mapping.SourcePieces is { Count: > 0 })
			{
				sourcePieces = mapping.SourcePieces;
			}
			else if (mapping.SourceItemId is uint sourceId && byItem.TryGetValue(sourceId, out var sourceKit))
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

			var index = IndexSource(sourcePieces);
			var changed = 0;
			foreach (var piece in targetKit.Pieces)
			{
				var unit = ResolveUnit(index, piece.BodyType, piece.Slot, piece.PieceType);
				if (unit == piece.Unit) continue;
				BinaryPrimitives.WriteUInt64LittleEndian(edited.AsSpan(piece.Offset), unit);
				changed++;
				if (unit == 0) hidden++;
				else replaced++;
			}

			if (changed > 0) applied++;
			else skipped++;
		}

		// 没有任何改动 ⇒ 输出就是基底本身（"删掉映射就还原"由此自动成立）。
		if (applied == 0)
			return new ArmorRegistryCompileOutcome(0, skipped, baseSealed, Changed: false, 0, 0);

		// payload 末尾追加一个对齐的自由字来承载伪造值，然后闭式求解。
		var (forged, wordOffset) = AppendForgeWord(edited, kits[^1]);
		if (!TrySolveForgeWord(forged, wordOffset, contentHash))
			throw new InvalidDataException("无法把内容校验和伪造回原值，已放弃写入。");

		return new ArmorRegistryCompileOutcome(
			applied, skipped, decryptor.EncodeData(forged), Changed: true, hidden, replaced);
	}

	/// <summary>
	/// **应用**：编译并写盘。基底优先用原件副本，没有就把当前 dl_bin 记一份当原件。
	/// <para>
	/// 只有两种失败会出现在这里：<b>找不到/读不懂 dl_bin</b>、<b>目标不可写</b>。
	/// 其它情况一律不做判断——受管 dl_bin 是派生事实，写坏了重编一次即可。
	/// </para>
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

		var backupPath = Path.Combine(backupDirectory, BackupFileName);
		byte[] baseSealed;
		try
		{
			if (File.Exists(backupPath))
			{
				baseSealed = await File.ReadAllBytesAsync(backupPath, cancellationToken).ConfigureAwait(false);
			}
			else
			{
				// 第一次应用：此刻文件还没被我们改过，记一份当原件（不写任何哈希状态）。
				baseSealed = await File.ReadAllBytesAsync(targetPath, cancellationToken).ConfigureAwait(false);
				Directory.CreateDirectory(backupDirectory);
				await File.WriteAllBytesAsync(backupPath, baseSealed, cancellationToken).ConfigureAwait(false);
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return new ArmorRegistryDataMappingResult(0, mappings.Count, false,
				$"读不了注册表文件（{exception.Message}）。");
		}

		ArmorRegistryCompileOutcome outcome;
		try
		{
			outcome = Compile(baseSealed, mappings);
		}
		catch (InvalidDataException exception)
		{
			return new ArmorRegistryDataMappingResult(0, mappings.Count, false, exception.Message);
		}

		// **每次都写。** 不拿"编译结果和原件一样"当跳过理由：关掉映射时编译结果就是原件，
		// 而磁盘上那份可能还是上一次写进去的改动 —— 跳过写入会让"关掉"变成没效果。
		// 派生文件本来就是"随时重算、随时覆盖"，没有需要保护的历史。
		try
		{
			await WriteAtomicAsync(targetPath, outcome.Sealed, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// 第二类、也是唯一需要用户动手的错误：目标不可写。
			return new ArmorRegistryDataMappingResult(outcome.Applied, outcome.Skipped, false,
				$"写不进注册表文件：{targetPath}（{exception.Message}）。请检查只读属性、占用或权限。")
			{
				Hidden = outcome.Hidden,
				Replaced = outcome.Replaced,
			};
		}

		return new ArmorRegistryDataMappingResult(outcome.Applied, outcome.Skipped, true, null)
		{
			Hidden = outcome.Hidden,
			Replaced = outcome.Replaced,
		};
	}

	static async ValueTask WriteAtomicAsync(string path, byte[] content, CancellationToken cancellationToken)
	{
		var temp = path + ".tmp";
		await File.WriteAllBytesAsync(temp, content, cancellationToken).ConfigureAwait(false);
		File.Move(temp, path, overwrite: true);
	}
}
