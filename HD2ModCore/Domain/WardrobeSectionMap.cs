namespace HD2ModCore.Domain;

// 构建期 section 映射表（`hd2mod.section-map/2`）的领域模型。
//
// 它回答一个问题：**这个 patch 里有哪些 section，它们各自的身份和落点是什么。**
// 编辑器用它画树（unit → LOD0 section），CLI 用它把"关掉第 N 个 section"翻译成指纹，
// 也用它校验一份 `wardrobe.masks` 是否还跟当前构建产物对得上。
//
// 为什么身份是 `(unit, LOD0 section 下标, 指纹)` 而不是偏移：
// 重建一次 mod，偏移几乎必然变，身份大概率不变。掩码只记身份，偏移留在这里，
// 两边靠指纹对齐——重建后要么继续有效，要么安全失效，不会悄悄关错几何。
//
// 为什么 `Toggles` 要列全各级 LOD：同一个 section 在每级 LOD 上是**另一条**节记录，
// 各有自己的 `numIndices` 地址。少写一级，玩家在对应距离档位就会看到"关不掉"。
//
// The build-time section map. It answers one question: which sections exist in this patch,
// what identifies them, and where each of them lands. Section identity is
// (unit, LOD0 section index, fingerprint) rather than an offset, because a rebuild moves
// offsets but usually not identities; the two sides line up by fingerprint, so a stale mask
// either keeps working or fails safely instead of hiding the wrong geometry.

/// <summary>
/// 映射表对应的构建产物指纹。CLI 用它确认"这份表"和"这份 patch"是同一份东西，
/// 否则掩码里的指纹会指向另一份资产的同名 section。
/// </summary>
public sealed record WardrobeSectionMapPatch(
	string Path,
	string Sha256,
	long Size,
	string? GpuResourcesSha256);

/// <summary>
/// 一个 section 在某一级 LOD 上的落点。`NumIndicesOffset` 是 TOC 内的绝对偏移
/// （十六进制字符串，带 0x），运行时靠它找到要清零的 dword。
/// </summary>
public sealed record WardrobeSectionToggle(
	int Lod,
	int MeshIndex,
	int SectionIndex,
	string NumIndicesOffset,
	uint NumIndices);

/// <summary>
/// 一个 section 的完整描述：身份 + 各级落点。
/// `Fingerprint` 必须由 <see cref="WardrobeSectionFingerprint"/> 生成，绝不手写。
/// </summary>
public sealed record WardrobeSectionMapEntry(
	int Index,
	string MaterialId,
	uint Triangles,
	string Fingerprint,
	IReadOnlyList<WardrobeSectionToggle> Toggles)
{
	/// <summary>与掩码文件里 `off=` 行对齐用的身份。</summary>
	public WardrobeSectionIdentity ToIdentity(string unit) => new(unit, Index, Fingerprint);
}

/// <summary>
/// 一个 unit 的 section 表。`LodConsistent` 为假时只保留与 LOD0 节数相同的 LOD。
/// `FileId` 一律是 16 位小写 hex、**不带 0x**，和 `wardrobe.masks` 里的写法一致——
/// 两份文件之间可以整段复制 unit ID，不必手工去前缀。
/// </summary>
public sealed record WardrobeSectionMapUnit(
	string FileId,
	string Slot,
	string PieceType,
	string BodyType,
	string Weight,
	int SectionCount,
	bool LodConsistent,
	IReadOnlyList<WardrobeSectionMapEntry> Sections);

/// <summary>
/// `sections.json` 的内存表示。`Schema` 是版本闸门：格式变了就换字符串，
/// 让旧文件显式失效，而不是被新代码误读成"零个 section"。
/// </summary>
public sealed record WardrobeSectionMapDocument(
	string Schema,
	WardrobeSectionMapPatch Patch,
	int SectionRecordSize,
	int NumIndicesOffsetInSection,
	string Offsets,
	IReadOnlyList<WardrobeSectionMapUnit> Units)
{
	/// <summary>当前格式标识。</summary>
	public const string CurrentSchema = "hd2mod.section-map/2";

	/// <summary>节记录固定 24 字节（见 <c>UnitMeshReader</c> 的 section 解析）。</summary>
	public const int ExpectedSectionRecordSize = 24;

	/// <summary>`numIndices` 在节记录内的偏移，也是运行时唯一要写的字段。</summary>
	public const int ExpectedNumIndicesOffset = 16;

	/// <summary>偏移一律相对 TOC 起点，和运行时的内存副本口径一致。</summary>
	public const string TocRelativeOffsets = "tocRelative";

	/// <summary>全部 section 数，用来在日志里给一个"这份表有多大"的直观数字。</summary>
	public int TotalSectionCount => Units.Sum(unit => unit.Sections.Count);

	/// <summary>按 unit ID 找一张表；接受带不带 0x 两种写法，找不到返回 null。</summary>
	public WardrobeSectionMapUnit? FindUnit(string? fileId)
	{
		if (string.IsNullOrWhiteSpace(fileId))
			return null;
		// 文件里存的是规范形式；调用方可能从别处抄来带 0x 或大写的写法，这里统一收口。
		var normalized = WardrobeSectionFingerprint.TryNormalizeUnit(fileId, out var unit)
			? unit
			: fileId.Trim().ToLowerInvariant();
		foreach (var candidate in Units)
		{
			if (string.Equals(candidate.FileId, normalized, StringComparison.Ordinal))
				return candidate;
		}
		return null;
	}

	/// <summary>按 `(unit, section 下标)` 找一条 section；找不到返回 null。</summary>
	public WardrobeSectionMapEntry? FindSection(string? fileId, int index)
	{
		var unit = FindUnit(fileId);
		if (unit is null || index < 0)
			return null;
		foreach (var section in unit.Sections)
		{
			if (section.Index == index)
				return section;
		}
		return null;
	}
}
