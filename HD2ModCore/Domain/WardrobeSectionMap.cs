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
/// <para>
/// <b><see cref="SourceMaterial"/> 是给人看的名字，不是身份。</b>它来自源侧的材质裸名
/// （<c>Hair</c>、<c>manuka_costume</c>…），只用于界面显示与"哪个开关关掉了什么"的解释。
/// 身份仍然是 <see cref="Fingerprint"/>：名字在重建后可能变（换了贴图目录），身份不该跟着变。
/// </para>
/// <para>
/// 没有它时，界面只能显示 <c>Section 0/1/2</c>——用户看不出哪一块是猫耳、哪一块是外套。
/// 这就是"section 开关没有预设只有数字"的直接原因。
/// </para>
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

	/// <summary>源侧材质裸名（不含目录与扩展名）。旧表没有这一项，所以可为空。</summary>
	public string? SourceMaterial { get; init; }
}

/// <summary>
/// 一个 section 在<b>源侧</b>是谁：材质裸名 + 它算出来的材质 id。
/// <para>
/// <b>为什么要带 id。</b>名字与 section 是按顺序传进来的，顺序一旦错位，开关就会关掉别的部件
/// ——而且不报错。带上 id 就能在写入前自校验：这一节实际绑的材质 id 必须与源材质算出来的
/// 那个相等，不等就<b>不采信名字</b>（退回按材质 id 的通用名字），宁可显示得粗糙，也不要关错。
/// </para>
/// <para>
/// <b>为什么不能只按材质 id 反查名字。</b>多个源材质可能算出同一个 id（同一张底色、同样的模板
/// 与后缀），比如 Manuka 的 <c>c_white_a</c> / <c>c_white_a_ear</c> / <c>c_white_a_tail</c>
/// 共用一张 <c>C_White_A</c>。按 id 反查只能得到其中"第一个"，于是"关猫耳"会连尾巴一起关。
/// 逐节记录才能把三个部件分开。
/// </para>
/// </summary>
public sealed record WardrobeSectionSourceName(string Name, string MaterialId);

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
	IReadOnlyList<WardrobeSectionMapEntry> Sections)
{
	/// <summary>
	/// 源侧的 unit 名：bodypart 名（<c>猫耳CatEars</c>）或部位名（<c>Chest</c>）。
	/// <para>
	/// 只在 <c>--components all</c> 下才等于"一个可切换的组件"；默认的 <c>merge</c> 会把配件
	/// 并进身躯槽位，那时它只是"这份 Unit 由哪个 bodypart 主导"。所以它<b>是提示，不是身份</b>，
	/// 旧表没有这一项。
	/// </para>
	/// </summary>
	public string? SourceName { get; init; }
}

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
	/// <remarks>
	/// <b>/3 相对 /2 只加了两项可选字段</b>（unit 的 <c>sourceName</c>、section 的 <c>sourceMaterial</c>），
	/// 布局常量一个都没动。所以 /2 的表仍然可以安全读取——只是界面只能显示数字。
	/// 反过来（新表给旧程序）会明确拒绝，因为旧程序不知道这两个字段的存在也无所谓，
	/// 但它写回时会丢掉名字；宁可让它报"请用当前版本重新生成"。
	/// </remarks>
	public const string CurrentSchema = "hd2mod.section-map/3";

	/// <summary>读取时接受的格式版本。新增字段的版本必须在这里登记，否则旧表会被判成"格式不对"。</summary>
	public static readonly IReadOnlyList<string> ReadableSchemas = ["hd2mod.section-map/2", "hd2mod.section-map/3"];

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
