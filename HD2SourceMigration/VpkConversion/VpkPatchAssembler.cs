using System.Security.Cryptography;
using System.Text;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using HD2SourceModel;
using HD2SourceMigration.Retarget;
using HD2SourceMigration.Segmentation;

namespace HD2SourceMigration.VpkConversion;

/// <summary>某个部位/配件用哪个游戏本体 Unit 当结构外壳。</summary>
/// <param name="ArchiveName">游戏存档名。</param>
/// <param name="FileId">Unit 的 fileId。</param>
/// <param name="Model">已解析的 Unit 模型。</param>
/// <param name="TocData">原始 TocData，作为字节保留基线。</param>
/// <param name="Unknown1To4">原条目的四个未知字段，原样带过去。</param>
public sealed record UnitDonor(
	string ArchiveName,
	ulong FileId,
	UnitMeshModel Model,
	byte[] TocData,
	ulong Unknown1,
	ulong Unknown2,
	uint Unknown3,
	uint Unknown4);

/// <summary>一个要写成 Unit 的部件：它的网格分组 + 目标槽位 + 用哪个外壳。</summary>
/// <summary>
/// 把一个 Unit 的**身份**钉死成"某件目标护甲自己的那一行"。
/// <para>
/// 默认情况下 Unit id = 随机种子 + 序号，<c>CustomizationInfo.BodyType</c> 一律写
/// <c>Any</c>——那是"自定义素体"的形态。而当这个 Unit 要**直接顶替某件游戏护甲自己的
/// Unit 槽位**时，这两样都必须跟着目标走：id 用目标的那个，身形/槽位/重量/层也用目标
/// 那一行的真实值。否则产出的 Unit 与它所占的槽位在字段上互相矛盾。
/// </para>
/// <para>
/// 四个 <c>*Name</c> 都是**完整的** customization 字符串（含
/// <c>HelldiverCustomization…_</c> 前缀），直接取自目标 Unit 的 TocData，避免在这里
/// 再维护一份枚举 ↔ 字符串的映射。
/// </para>
/// </summary>
public sealed record VpkUnitIdentityOverride(
	string UnitId,
	string BodyTypeName,
	string SlotName,
	string PieceTypeName,
	string WeightName);

public sealed record VpkUnitPlan(
	MeshGroup Group,
	UnitDonor Donor,
	string SlotName,
	string PieceTypeName,
	VpkUnitIdentityOverride? Identity = null);

/// <summary>
/// 一条运行时护甲注册表的 piece。
/// <para>
/// <b>为什么 body 不能统一填 3（Any）。</b>注册表不是"独立声明"，而是<b>注入目标护甲套件的行</b>：
/// <c>armor_registry_composition.lua</c> 用
/// <c>by_position[body..':'..slot..':'..piece_type]</c> 去目标套件里找模板，
/// 找不到就 <c>string.rep('\0', 96)</c> 造一个全零 Piece——Unit ID 之外的东西（资源引用、
/// 色调、保留位）全没了，上机就是一团乱七八糟的三角形。
/// </para>
/// <para>
/// 所以本体部位必须<b>按目标原生行</b>声明：HD2 的原生护甲是按身形分行存的，
/// 一个本体部位要出 <c>body=0</c>（Stocky）与 <c>body=1</c>（Slim）两条，各指向同一个 Unit。
/// 头盔是例外——Lua 会把 <c>body=3</c> 的 slot 0 展开到目标的原生 body 行。
/// 配件才用 <c>body=3</c>（Any），层号从 1 起，那是真正新增的位点。
/// </para>
/// </summary>
public sealed record VpkRegistryPiece(uint Body, uint Slot, uint PieceType, bool IsDefaultBase);

/// <summary>
/// 一个 section 在源侧是谁：材质裸名 + 它算出来的材质 id。
/// <para>
/// <b>为什么要逐节记。</b>多个源材质可能算出同一个材质 id（同一张底色、同样的模板与后缀）——
/// 实测 Manuka 的 <c>c_white_a</c> / <c>c_white_a_ear</c> / <c>c_white_a_tail</c> 共用一张
/// <c>C_White_A</c>。只按 id 反查名字，"关猫耳"会连尾巴一起关；逐节记才能把它们分开。
/// </para>
/// </summary>
public sealed record VpkSectionSource(string MaterialName, ulong MaterialId);

/// <summary>
/// 一个部件的产出记录。
/// <para>
/// <see cref="RegistryPieces"/> 是运行时护甲注册表的条目，不是 Unit 自己的 <c>CustomizationInfo</c>
/// ——两者是不同的轴：前者决定"游戏里哪个槽位/哪一层显示什么"，后者是游戏自带护甲组合用的语义标签。
/// 把它们一起返回，是为了让调用方能<b>登记素体</b>（写工作区），否则 Patch 里有一堆没人引用的 Unit。
/// </para>
/// </summary>
public sealed record VpkUnitOutcome(
	string Name,
	string UnitId,
	BodyRegion Region,
	uint Slot,
	IReadOnlyList<VpkRegistryPiece> RegistryPieces,
	int VertexCount,
	int TriangleCount,
	int TocBytes,
	int GpuBytes,
	IReadOnlyList<string> Diagnostics)
{
	public bool IsValid => Diagnostics.Count == 0;

	/// <summary>
	/// 逐 section 的源材质（下标 = section 下标）。空表示"这次转换没有材质表可对"。
	/// <para>用途是给 <c>sections.json</c> 起名，从而让组件开关能精确到"猫耳"而不是"共用这张底色的所有东西"。</para>
	/// </summary>
	public IReadOnlyList<VpkSectionSource> SectionSources { get; init; } = [];
}

/// <summary>
/// 一个源材质 → 一个 HD2 材质的对应关系。
/// <para>
/// <b>为什么要把这个报出来。</b>没有它，"某个部位贴图不对"这条反馈就无从下手 ——
/// 材质 id 是从 <c>$basetexture</c> 哈希出来的，**看 id 完全认不出它是谁**。
/// 有了这张对照表，就能从"耳朵不对"直接落到某个源材质名上。
/// </para>
/// </summary>
public sealed record VpkMaterialOutcome(
	int SourceMaterialIndex,
	string SourceMaterialName,
	ulong MaterialId,
	string TemplateName,
	string BaseTexture,
	int? MergedIntoSourceIndex)
{
	/// <summary>
	/// 这个材质有没有被<b>写进本次产出的 patch</b>。
	/// <para>
	/// 模型 patch 会算出所有被引用材质的 id（Unit 必须引用它们），但一个都不写——
	/// 材质归材质 patch 所有。这一列就是为了让"算过但没写"和"根本没算出来"分得开。
	/// </para>
	/// </summary>
	public bool Emitted { get; init; }

	/// <summary>
	/// 这个材质是不是<b>兜底灰占位</b>（模型要它、但没有任何来源提供）。
	/// <para>
	/// 单独一列而不是靠"名字看着像不像"去猜：占位材质<b>确实写进了 patch</b>，
	/// 所以它在 <see cref="Emitted"/> 上是 true —— 只看那一列会把占位当成"真实落地"。
	/// 用户看着报告逐行核对时，这一列是唯一能指出"这行的颜色不是源侧那个"的东西。
	/// </para>
	/// </summary>
	public bool Placeholder { get; init; }

	/// <summary><c>-1</c> 表示这个材质不是模型引用的，而是本 VPK 自己带的（模型没用到的那些）。</summary>
	public bool UsedByModel => SourceMaterialIndex >= 0;
}

/// <summary>一张贴图的产出记录。同样是为了让"没写"有明确理由，而不是靠猜。</summary>
public sealed record VpkTextureOutcome(string Name, ulong Id, string Origin, bool Emitted, string Reason);

/// <summary>
/// 材质的**总账**：模型要多少、落地多少、差的是哪几类。
/// <para>
/// <b>为什么必须有。</b>「少女前线2 夏安」那种包上机后一片紫块，而转换报告只写了
/// "31 个材质"——没写"模型要 51 个"。差的那 20 个里有两类完全不同的东西：
/// </para>
/// <list type="bullet">
/// <item><b><see cref="NoDraw"/></b>：源侧自己写着 <c>$no_draw</c>。在 L4D2 里这是
/// "这块网格不绘制"，常常被用来做<b>网格变体选择</b>（P1/P2/P3 三套衣服共存，靠材质决定哪套可见）。
/// HD2 没有等价物，所以按 <see cref="NoDrawPolicy"/> 落地：默认<b>保留几何 + 材质不显示</b>
/// （画面与源侧一致，数据不丢），`--no-draw show` 则照常显示、`drop` 才连几何一起丢。</item>
/// <item><b><see cref="Missing"/></b>：模型引用了、但谁都解析不出来（包里没有 / 合成失败）。这是真缺失。</item>
/// </list>
/// </summary>
public sealed record VpkMaterialGap(
	int RequiredCount,
	int ResolvedCount,
	IReadOnlyList<string> NoDraw,
	IReadOnlyList<string> Missing,
	IReadOnlyList<string>? Fallback = null)
{
	/// <summary>用了兜底灰材质的源材质名（模型要它、但谁都没提供）。</summary>
	public IReadOnlyList<string> FallbackMaterials => Fallback ?? [];

	/// <summary>
	/// 模型要的材质里，有多少<b>没有真实材质</b>
	/// （<see cref="NoDraw"/> ∪ <see cref="Missing"/> ∪ <see cref="FallbackMaterials"/>）。
	/// <para>
	/// ⚠️ 兜底材质虽然是"看得见的占位"，但它<b>不是</b>源侧那个材质 —— 颜色、图案、金属度
	/// 全都不对。把它算进"解决"会让总账显示"全部落地"，而那正是需要被看见的那种问题。
	/// </para>
	/// </summary>
	public int UnresolvedCount => RequiredCount - ResolvedCount;

	public bool IsClean => UnresolvedCount <= 0;
}

public sealed record VpkPackageResult(
	string? TocPath,
	IReadOnlyList<VpkUnitOutcome> Units,
	IReadOnlyList<string> Diagnostics)
{
	/// <summary>
	/// 这次产出<b>至少写出了一个 patch</b> 且没有诊断。
	/// <para>
	/// 不能要求 <see cref="TocPath"/> 非空：材质 patch（纯贴图包、纯材质包）没有 Unit，
	/// 但它们是完全合法的产出。
	/// </para>
	/// </summary>
	public bool IsValid => Diagnostics.Count == 0 && (TocPath is not null || MaterialPackPath is not null);

	/// <summary>本次生成的素体 Key。登记时用它把 Unit 与素体关联起来。</summary>
	public string? ModelKey { get; init; }

	/// <summary>
	/// 材质包的 patch 路径（**与 Unit patch 分开的文件**）。
	/// <para>
	/// 约定：一次导入 = 一个 VPK = 一个 patch。材质来自<b>别的</b> VPK，所以它们是外部材质包，
	/// 不能塞进 Unit patch。Unit patch 里对 MaterialId 的引用在部署时由材质包解析。
	/// </para>
	/// </summary>
	public string? MaterialPackPath { get; init; }

	public int MaterialCount { get; init; }

	public int TextureCount { get; init; }

	/// <summary>源材质 → HD2 材质的对照表。诊断"某部位贴图不对"时按它定位。</summary>
	public IReadOnlyList<VpkMaterialOutcome> Materials { get; init; } = [];

	/// <summary>贴图对照表（含"没写进去"的那些及其理由）。</summary>
	public IReadOnlyList<VpkTextureOutcome> Textures { get; init; } = [];

	/// <summary>
	/// <b>降级</b>问题：patch 写出来了，但有东西没到位（某个材质解析不出来、某张贴图缺来源）。
	/// <para>
	/// 与 <see cref="Diagnostics"/> 分开是刻意的。诊断是"这件事做不成"，必须中止；
	/// 而一百个材质里有两个读不到，正确的反应是<b>把包写出来、把问题喊出来</b>——
	/// 中止只会让人连"到底哪几个坏了"都看不到。
	/// </para>
	/// </summary>
	public IReadOnlyList<string> Warnings { get; init; } = [];

	/// <summary>
	/// <b>跳过</b>的东西：源侧本来就写着"别画"（<c>$no_draw</c>），不是问题，也不该进 <see cref="Warnings"/>。
	/// <para>
	/// 分开的理由是<b>别训练人忽略警告</b>。把"源侧要求不绘制"和"某个材质读不到"混在一列里，
	/// 时间一长这一列就没人看了——而那正是真正的问题藏进去的地方。
	/// </para>
	/// </summary>
	public IReadOnlyList<string> Skipped { get; init; } = [];

	/// <summary>
	/// 材质总账：模型要多少、落地多少、差的是哪几类（<c>$no_draw</c> vs 真缺失）。
	/// <para>
	/// <b>这是"上机紫块"的唯一诚实答案。</b>以前只报"写了 N 个材质"，读者无从判断够不够；
	/// 有了这份总账，"要 51 / 落地 39 / 12 个是源侧 <c>$no_draw</c>"一行就能说清问题在哪。
	/// </para>
	/// </summary>
	public VpkMaterialGap? MaterialGap { get; init; }
}

/// <summary>
/// 把切分好的部件写成一个新的 Patch 包。
/// <para>
/// 每个 Unit 拿一个<b>全新的随机 AssetKey</b>——这是"自定义素体"的前提：它是新资产，不是覆盖原版。
/// ID 用固定种子的递增块（与 <c>CustomModelConversionService</c> 同一思路），这样同一次转换的产物
/// 之间不会撞号，而且整体可复现。调用方负责再与原版资产做一次冲突检查。
/// </para>
/// <para>
/// <b>输出范围（<c>materialScope</c>）。</b>这是"一个 VPK 一个 patch，只含它自己的资源"这条约定的落点：
/// </para>
/// <list type="bullet">
/// <item>给 <c>null</c>：旧行为，把用到的材质全部写进一个合并材质包（只有 <c>source convert</c> 用它）。</item>
/// <item>给一个 <see cref="SourceMaterialSource.ScopedTo"/> 视图：<b>只写这个 VPK 自己的</b>材质与贴图。
/// 模型 VPK 因此产出"零材质零贴图"的纯 unit patch；材质 VPK 产出纯材质/纯贴图 patch。</item>
/// </list>
/// </summary>
public sealed class VpkPatchAssembler
{
	/// <param name="avatarTransformInfo">HD2 avatar 骨架，会被并进每个外壳的 TransformInfo。</param>
	/// <param name="headerTemplateTocData">输出 Patch 的头部模板（用外壳所在存档的 TOC）。</param>
	/// <param name="patchFileName">
	/// Unit patch 的输出文件名，形如 <c>9ba626afa44a3aa3.patch_0</c>。
	/// <c>null</c> 表示<b>本次不出 Unit patch</b>（纯材质/纯贴图 VPK）；此时 <paramref name="plans"/> 必须为空。
	/// </param>
	/// <param name="modelKey">素体 Key（形如 <c>custom-&lt;guid&gt;</c>）。由调用方生成并负责登记。</param>
	/// <param name="sourceMaterialNames">
	/// 源模型的材质裸名表（下标 = <c>RetargetedSection.SourceMaterialIndex</c>），
	/// 来自 <c>SourceModelDocument.MaterialPaths</c>。
	/// </param>
	/// <param name="materialScope">
	/// 输出范围。见类型注释。给定时它<b>必须</b>是一个 <see cref="SourceMaterialSource.ScopedTo"/> 视图，
	/// 否则"这个 VPK 自己带了什么"无从判断。
	/// </param>
	/// <param name="idNamespace">
	/// 材质/贴图 id 的命名空间（见 <see cref="Hd2AssetId.NamespaceFor"/>）。
	/// 给了 <paramref name="materialConverter"/> 时以它的为准——两边不一致会让"去重判断用的 id"
	/// 与"真正写进包的 id"不是同一个，那种错只会表现为"同一张图写了两遍"或"漏写"。
	/// </param>
	/// <param name="onProgress">
	/// 进度回调：<c>(阶段键, 已完成, 总数)</c>。阶段键是稳定的英文标识：<c>materials</c> / <c>textures</c> / <c>units</c>。
	/// <para>
	/// <b>为什么由装配器报而不是让调用方猜。</b>一次转换里最长的两段（逐材质跑 texconv、逐 Unit
	/// 写几何）在装配器内部，而且都是并行的——调用方只能看到"进了这一步、还没出来"。4K 材质包这一段
	/// 有 55 秒，没有计数的话进度条就是钉在原地的。
	/// </para>
	/// <para>回调会在<b>工作线程</b>上被调用，实现方自己负责封送。</para>
	/// </param>
	public async ValueTask<VpkPackageResult> AssembleAsync(
		IReadOnlyList<VpkUnitPlan> plans,
		RetargetResult? retarget,
		SourceGeometry? geometry,
		UnitTransformInfo? avatarTransformInfo,
		byte[]? headerTemplateTocData,
		string outputDirectory,
		string? patchFileName,
		string modelKey,
		IReadOnlyList<string>? forbiddenUnitIds = null,
		CancellationToken cancellationToken = default,
		IReadOnlyList<string>? sourceMaterialNames = null,
		SourceMaterialConverter? materialConverter = null,
		string? materialPackFileName = null,
		ulong? unitIdSeed = null,
		SourceMaterialSource? materialScope = null,
		string? idNamespace = null,
		NoDrawPolicy noDrawPolicy = NoDrawPolicy.Show,
		Action<string, int, int>? onProgress = null)
	{
		ArgumentNullException.ThrowIfNull(plans);
		ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
		if (plans.Count != 0)
		{
			ArgumentNullException.ThrowIfNull(retarget);
			ArgumentNullException.ThrowIfNull(geometry);
			ArgumentNullException.ThrowIfNull(avatarTransformInfo);
			ArgumentException.ThrowIfNullOrWhiteSpace(patchFileName);
		}

		if (materialScope is not null && materialScope.PrimaryVpkPath is null)
			throw new ArgumentException(
				"materialScope 必须是一个 ScopedTo() 视图——整批视图没有\"自己带了什么\"这个概念。",
				nameof(materialScope));

		var diagnostics = new List<string>();
		var warnings = new List<string>();
		var skipped = new List<string>();
		// 成功写进 session 的 Unit 数（= `units` 里 `IsValid` 的那些）。
		// 用来区分"部分 Unit 失败"（可继续）与"一条都没写成"（结构性失败）——见 ⑤ 之后的判断。
		var unitsWritten = 0;
		// 材质总账（见 VpkMaterialGap 的注释）：由 ResolveAndEmitMaterials 填。
		VpkMaterialGap? materialGap = null;
		// 源侧声明 `$no_draw` 的材质下标（= sourceMaterialNames 的下标），由 ResolveAndEmitMaterials 填。
		var noDrawMaterialIndices = new HashSet<int>();

		// 命名空间以转换器为准（见参数注释）：转换器有它就一定用它派生，装配器必须跟着用，
		// 否则下面两处"按 id 去重"的键与真正写出的 id 不是同一个。
		idNamespace = materialConverter?.IdNamespace ?? (string.IsNullOrWhiteSpace(idNamespace) ? null : idNamespace.Trim());
		var units = new List<VpkUnitOutcome>();
		var session = new CanonicalPatchSession();
		var forbidden = new HashSet<string>(forbiddenUnitIds ?? [], StringComparer.OrdinalIgnoreCase);

		var materialSession = new CanonicalPatchSession();
		var materialCount = 0;
		var textureCount = 0;
		var materialOutcomes = new List<VpkMaterialOutcome>();
		var textureOutcomes = new List<VpkTextureOutcome>();
		var addedMaterialIds = new HashSet<ulong>();
		var addedTextureIds = new HashSet<ulong>();
		var textureSourcesById = new Dictionary<ulong, string>();
		// 同一张"不属于本 patch"的贴图会被多个材质引用（`shinano_face` 被两个材质用），
		// 逐次记一行会把对照表刷成重复项。按名字去重，只留第一条。
		var reportedForeignTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		// 源材质下标 → 材质 id。Unit 的作者要靠它把每一段指向正确的材质。
		var materialIdsBySourceMaterial = new Dictionary<int, ulong>();

		if (materialConverter is not null && (materialScope is not null || sourceMaterialNames is not null))
			ResolveAndEmitMaterials();

		// 眼球：把 UV 按 `$iris` 的平面投影重算一遍（见 EyeUv）。
		//
		// 必须在写几何之前、且在材质解析之后：判据是"这个材质的颜色来自 `$iris`"，
		// 那要求读 VMT 并量出贴图里画着眼睛的那块矩形——也就是 ResolveAndEmitMaterials
		// 手上那批 VMT。漏掉这一步的表现是"眼球不是没有材质，而是整颗只剩一个像素的颜色"。
		if (materialConverter is not null && geometry is not null && sourceMaterialNames is not null)
		{
			var (reprojected, eyeNotes) = EyeUv.Reproject(
				geometry, sourceMaterialNames, materialConverter.ResolveEyeColorSource);
			geometry = reprojected;
			warnings.AddRange(eyeNotes);
		}

		// 源侧写着 `$no_draw` 的部件：**连几何一起不写**。
		//
		// 源侧语义就是"这块不画"，最常用的场合是**网格变体选择**：P1/P2/P3 三套衣服全部烘在
		// 同一个模型里，靠"哪几个材质声明 $no_draw"决定当前显示哪一套。
		// 以前我们在 HD2 里把它当"无等价物"只跳过材质、三角形照写 → 那些 section 指向一个谁都
		// 没有的材质 id → 上机紫块（实测「少女前线2 夏安」49 个材质里有 24 个是这种）。
		// 正确做法：这些三角形不写进 patch——既忠实于源侧，也不会留下没人认领的 section。
		var effectivePlans = FilterNoDrawPlans(plans, geometry, noDrawMaterialIndices, warnings);

		// 与 CustomModelConversionService 同构：一个随机块 + 递增，保证同批产物不撞号。
		// 给了 `unitIdSeed` 就用它 —— 用来**原地替换**一个已经登记过的包：
		// unit id 保持不变，工作区里的映射就不用重做，改动只剩"几何变了"这一个变量。
		var seed = unitIdSeed ?? (BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)) & 0x7fffffffffff0000UL);
		var index = 1UL;
		// 素体只由一个模型构成。本体部位要按目标原生行声明（见 VpkRegistryPiece 的说明），
		// 配件才用 Any。层号按槽位递增：本体占 0，配件从 1 起。
		var layerBySlot = new Dictionary<uint, uint>();

		// ① 逐 Unit 里"便宜且必须有顺序"的部分先串行算掉：unit id 的分配顺序决定产物可复现，
		//    配件层号也依赖遍历顺序。把它们塞进并行区只会把顺序搞乱，而它们本身不花时间。
		var unitIds = new string[effectivePlans.Count];
		var slots = new uint[effectivePlans.Count];
		var registryPiecesByPlan = new IReadOnlyList<VpkRegistryPiece>[effectivePlans.Count];
		var customizations = new UnitCustomizationInfo[effectivePlans.Count];
		for (var planIndex = 0; planIndex < effectivePlans.Count; planIndex++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var plan = effectivePlans[planIndex];
			// 给了 Identity 就用目标护甲自己的 Unit id：这条 Unit 会直接顶替那个槽位，
			// 用生成出来的 id 会让"内容说 Any、占位说 Slim"这种自相矛盾的产物成立。
			var unitId = plan.Identity?.UnitId;
			if (string.IsNullOrWhiteSpace(unitId))
			{
				unitId = (seed + index).ToString("x16");
				index++;
				while (forbidden.Contains(unitId))
				{
					unitId = (seed + index).ToString("x16");
					index++;
				}
			}
			else
			{
				unitId = unitId.Trim().ToLowerInvariant();
			}

			forbidden.Add(unitId);
			unitIds[planIndex] = unitId;

			var slot = plan.Group.Region.Slot();
			slots[planIndex] = slot;
			if (plan.Group.Kind == MeshGroupKind.BodyPart)
			{
				layerBySlot[slot] = 1;
				registryPiecesByPlan[planIndex] = plan.Group.Region == BodyRegion.Head
					// 头盔：Lua 会把 body=3 的 slot 0 展开进目标的原生 body 行，所以这里就该是 Any。
					? [new VpkRegistryPiece(3, slot, 0, false)]
					// 本体：目标原生行是 body=0（Stocky）与 body=1（Slim）两行，
					// 必须各出一条才能命中真正的 Piece 模板。
					: [new VpkRegistryPiece(0, slot, 0, true), new VpkRegistryPiece(1, slot, 0, true)];
			}
			else
			{
				var layer = layerBySlot.GetValueOrDefault(slot, 1u);
				layerBySlot[slot] = layer + 1;
				registryPiecesByPlan[planIndex] = [new VpkRegistryPiece(3, slot, layer, false)];
			}

			// 默认是"自定义素体"形态：身形 Any、槽位/层来自计划、重量沿用外壳护甲。
			// 钉了 Identity 时整套换成目标护甲那一行的真实值——四个字段必须**一起**换，
			// 只换 id 不换身形就会留下"占 Slim 的槽、自称 Any"的 Unit。
			customizations[planIndex] = plan.Identity is { } identity
				? new UnitCustomizationInfo(
					identity.BodyTypeName,
					identity.SlotName,
					identity.WeightName,
					identity.PieceTypeName)
				: new UnitCustomizationInfo(
					"HelldiverCustomizationBodyType_Any",
					$"HelldiverCustomizationSlot_{plan.SlotName}",
					plan.Donor.Model.CustomizationInfo.Weight,
					$"HelldiverCustomizationPieceType_{plan.PieceTypeName}");
		}

		// ①b 把 jiggle 骨的**合成节点**追加进 TransformInfo（必须在建网格之前）。
		// 网格是按 `NameHashes[目标骨下标]` 取哈希的：合成骨不进表，那条路径就会拿到越界下标，
		// 表现是"骨哈希 … 不在外壳扩展后的 TransformInfo 里"。追加只影响末尾，原生下标一个不动。
		var transformInfo = UnitTransformInfoAppender.Append(avatarTransformInfo!, retarget?.ExtraBones);
		if (transformInfo is not null && retarget?.ExtraBones is { Count: > 0 })
		{
			// 转换时就把"locals ↔ world 自洽"这条不变量钉住：
			// 不一致时游戏不报错，只会把那些骨上的网格拉长 1~3 米（2026-10-31 实机踩过）。
			UnitTransformInfoAppender.AssertLocalAndWorldAgree(transformInfo, avatarTransformInfo!.Matrices.Count);
		}

		// ② 逐 Unit 的"重活"并行：建网格 + 写 Unit 互不相干（输入只读、产物各归各的数组）。
		//    实测一份 42 万顶点的模型，7 个 Unit 串行 18.3 s，并行之后约 3 s。
		var meshes = new RetargetedMesh[effectivePlans.Count];
		var authoredUnits = new UnitAuthorResult[effectivePlans.Count];
		var unitsDone = 0;
		Parallel.For(0, effectivePlans.Count, VpkParallel.Options, planIndex =>
		{
			var plan = effectivePlans[planIndex];
			var mesh = RetargetedMeshBuilder.Build(plan.Group, geometry!, retarget!, transformInfo!);
			// ⚠️ **每份一个作者对象。** UnitAuthor 内部持有几个 Canonical* 编译器实例，
			// 它们没有跨调用状态，但共用一个实例就等于把"它们确实无状态"变成并行正确性的前提。
			// 多造几个小对象比赌这一条便宜得多。
			var unitAuthor = new UnitAuthor();
			var authored = unitAuthor.Author(new UnitAuthorRequest(
				plan.Donor.Model, plan.Donor.TocData, transformInfo!, mesh, customizations[planIndex],
				plan.Donor.FileId, materialIdsBySourceMaterial));
			meshes[planIndex] = mesh;
			authoredUnits[planIndex] = authored;
			onProgress?.Invoke("units", Interlocked.Increment(ref unitsDone), effectivePlans.Count);
		});

		// ③ 按计划顺序收口：诊断、产物记录、session 条目都要保持顺序（重跑要逐字节可复现）。
		for (var planIndex = 0; planIndex < effectivePlans.Count; planIndex++)
		{
			var plan = effectivePlans[planIndex];
			var unitId = unitIds[planIndex];
			var mesh = meshes[planIndex];
			var authored = authoredUnits[planIndex];
			var slot = slots[planIndex];

			units.Add(new VpkUnitOutcome(
				plan.Group.Name, unitId, plan.Group.Region, slot, registryPiecesByPlan[planIndex],
				mesh.VertexCount, mesh.TriangleCount,
				authored.TocData?.Length ?? 0, authored.GpuData?.Length ?? 0, authored.Diagnostics)
			{
				SectionSources = BuildSectionSources(mesh, sourceMaterialNames, materialIdsBySourceMaterial),
			});

			if (!authored.IsValid)
			{
				diagnostics.Add($"{plan.Group.Name} 写 Unit 失败：{string.Join("；", authored.Diagnostics.Take(3))}");
				continue;
			}

			session.AddEntry(new CanonicalPatchSessionEntry(
				new AssetKey(PatchUnitMeshReader.UnitTypeId, ulong.Parse(unitId, System.Globalization.NumberStyles.HexNumber)),
				CanonicalPatchEntryOwnership.TargetOutput,
				authored.TocData!,
				authored.GpuData!,
				[],
				plan.Donor.Unknown1,
				plan.Donor.Unknown2,
				plan.Donor.Unknown3,
				plan.Donor.Unknown4));
			unitsWritten++;
		}

		// **失败粒度：一条 Unit 写不出来，不该把整个 patch 弄没。**
		//
		// 原来只要 `diagnostics` 非空就 `return null`，于是"某个部件的顶点有问题"会让
		// **整份 patch 消失**——用户看到的是"这个 VPK 里没有能用的资源"，
		// 而真实原因可能只是其中一个 Unit 的法线是零向量（实测 `影鸢女武神shinano`）。
		// 那一次排查里，这句话把用户和排查者一起带偏了方向。
		//
		// 现在分成两档：
		//   · 一条 Unit 都没写出来 ⇒ 仍然整体失败（没有 patch 可交，这是结构性失败）；
		//   · 部分写成 ⇒ 写出来，把失败的那些**逐条**报成警告。
		// 后者在游戏里表现为"那些部件退回外壳护甲的外观"，比"整件角色都不对"好得多，
		// 而且报告里能直接看到是哪几个部件。
		if (diagnostics.Count != 0 && unitsWritten == 0)
			return new(null, units, diagnostics);

		if (diagnostics.Count != 0)
		{
			warnings.Add(
				$"{diagnostics.Count} 个部件没能写出来（{string.Join("；", diagnostics.Take(3))}"
				+ (diagnostics.Count > 3 ? "…" : string.Empty)
				+ $")；其余 {unitsWritten} 个部件与全部材质仍然写进本 patch —— "
				+ "这些部件在游戏里会退回外壳护甲的外观，请结合上面的诊断确认原因是几何问题还是材质问题。");
		}

		string? tocPath = null;
		if (session.Entries.Count != 0)
		{
			session.Finalize(CanonicalDependencyClosureValidation.Valid);
			var written = await new CanonicalPatchWriter()
				.WriteAsync(session, outputDirectory, patchFileName!, headerTemplateTocData, overwriteExisting: true, cancellationToken)
				.ConfigureAwait(false);
			tocPath = written.TocFilePath;
		}

		// 材质包写进**另一个** patch 文件（同一个存档的下一个 patch 序号）。
		string? materialPackPath = null;
		if (materialSession.Entries.Count != 0)
		{
			materialSession.Finalize(CanonicalDependencyClosureValidation.Valid);
			var packName = materialPackFileName
				?? (patchFileName is null ? "9ba626afa44a3aa3.patch_0" : NextPatchIndexName(patchFileName));
			var packWritten = await new CanonicalPatchWriter()
				.WriteAsync(materialSession, outputDirectory, packName, headerTemplateTocData, overwriteExisting: true, cancellationToken)
				.ConfigureAwait(false);
			materialPackPath = packWritten.TocFilePath;
		}

		if (materialScope is not null && materialConverter is not null)
			ReportSkippedTextures();

		return new(tocPath, units, [])
		{
			ModelKey = modelKey,
			MaterialPackPath = materialPackPath,
			MaterialCount = materialCount,
			TextureCount = textureCount,
			Materials = materialOutcomes,
			Textures = textureOutcomes,
			Warnings = warnings,
			Skipped = skipped,
			MaterialGap = materialGap,
		};

		// ── 局部函数：材质与贴图的解析与写出 ──────────────────────────────────
		//
		// 拆成四步是刻意的，因为"知道 id"与"写出资源"是两件事：
		//   ① 决定要解析哪些材质名（模型用到的 ∪ 本 VPK 自己带的）
		//   ② 解析：属于本 patch 的走完整 Convert（要贴图字节），不属于的只取 id（不编码）
		//   ③ 写出材质 + 它带出来的贴图，按"本 VPK 是否提供这张贴图"过滤
		//   ④ 纯贴图包补上本 VPK 自己带的贴图（没有 VMT 可问，只能全写）
		void ResolveAndEmitMaterials()
		{
			var converter = materialConverter!;
			var scope = materialScope;

			// ① 要解析的材质名。**去重**：`MaterialPaths` 里同名材质会出现多次。
			var stems = new List<string>();
			var seenStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			// 模型真正引用到的那些（= "必须落地"的集合），用来算材质总账。
			var requiredStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			if (sourceMaterialNames is not null)
			{
				foreach (var materialIndex in UsedMaterialIndices(plans, geometry!))
				{
					if (materialIndex < 0 || materialIndex >= sourceMaterialNames.Count)
						continue;
					var stem = StemOf(sourceMaterialNames[materialIndex]);
					requiredStems.Add(stem);
					if (seenStems.Add(stem))
						stems.Add(stem);
				}
			}

			if (scope is not null)
			{
				foreach (var stem in scope.OwnedVmtNames.Order(StringComparer.OrdinalIgnoreCase))
				{
					if (seenStems.Add(stem))
						stems.Add(stem);
				}
			}

			// ② 解析。**这一步决定 Unit 引用哪个材质 id**，所以失败必须报出来——
			// 静默失败会让 Unit 沿用外壳护甲的材质，上机就是"形状对、贴图是别人的"。
			//
			// ⚠️ 但**不中止**：一百个材质里坏两个，正确的反应是把包写出来、把问题喊出来。
			// 中止只会让人连"哪几个坏了"都看不到。
			var idByStem = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
			var identityByStem = new Dictionary<string, MaterialIdentity>(StringComparer.OrdinalIgnoreCase);
			var productByStem = new Dictionary<string, MaterialSynthesisResult>(StringComparer.OrdinalIgnoreCase);
			// 每个没落地的材质名 → 理由（用来把 $no_draw 和真缺失分开）。
			var skipReasonByStem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

			// ⚠️ **这一段必须并行。** 每个材质背后是 texconv 的进程调用（解成 RGBA 量 alpha、编成 BC7），
			// 单张 4K 图约 3 s；串行时几十个材质就是一两分钟，而它们彼此完全独立。
			// 实测 4K 材质包 141 s 里有 132 s 花在这条链上。
			//
			// 结果按**下标**收集、按**原顺序**收口：skipped / warnings 的次序属于可复现产物的一部分
			// （报告逐条打印给人看），并行只允许影响"谁先算完"，不允许影响"报告怎么写"。
			var ownedByStem = new bool[stems.Count];
			for (var stemIndex = 0; stemIndex < stems.Count; stemIndex++)
				ownedByStem[stemIndex] = scope is null || scope.OwnsVmt(stems[stemIndex]);

			var synthesizedByStem = new MaterialSynthesisResult?[stems.Count];
			var reportByStem = new MaterialResolveReport?[stems.Count];
			var materialsDone = 0;
			Parallel.For(0, stems.Count, VpkParallel.Options, stemIndex =>
			{
				if (ownedByStem[stemIndex])
					synthesizedByStem[stemIndex] = converter.Convert(stems[stemIndex]);
				else
					reportByStem[stemIndex] = converter.Resolve(stems[stemIndex]);
				onProgress?.Invoke("materials", Interlocked.Increment(ref materialsDone), stems.Count);
			});

			for (var stemIndex = 0; stemIndex < stems.Count; stemIndex++)
			{
				var stem = stems[stemIndex];
				if (ownedByStem[stemIndex])
				{
					var synthesized = synthesizedByStem[stemIndex]!;
					if (synthesized.IsSkipped)
					{
						// 源侧自己写着"别画"，不是问题——单列，别混进警告。
						skipped.Add(synthesized.SkipReason!);
						skipReasonByStem[stem] = synthesized.SkipReason!;
						continue;
					}

					if (!synthesized.IsValid || synthesized.Material is null)
					{
						warnings.Add($"源材质 {stem} 合成失败：{string.Join("；", synthesized.Diagnostics)}");
						skipReasonByStem[stem] = string.Join("；", synthesized.Diagnostics);
						continue;
					}

					// 降级提示（材质造出来了，但有东西表达不了）：进警告，不算失败。
					// 单独走一条是因为"做成了但不忠实"和"没做成"混在一起，会让人分不清
					// 到底该不该重新导——而这两件事的处理方式完全不同。
					foreach (var notice in synthesized.Notices)
						warnings.Add(notice);

					idByStem[stem] = synthesized.Material.Id;
					productByStem[stem] = synthesized;
				}
				else
				{
					var report = reportByStem[stemIndex]!;
					switch (report.Outcome)
					{
						case MaterialResolveOutcome.Resolved:
							idByStem[stem] = report.Identity!.Id;
							identityByStem[stem] = report.Identity!;
							break;
						case MaterialResolveOutcome.Skipped:
							skipped.Add(report.Reason);
							skipReasonByStem[stem] = report.Reason;
							break;
						default:
							// 这个材质不属于本 patch，但 Unit 要靠它的 id。解析不出来 = 那一段会
							// 退回外壳护甲的材质，上机表现为"某处贴图是原版护甲的"。
							warnings.Add($"源材质 {stem} 解析失败（本 patch 不写它，但 Unit 需要它的 id）：{report.Reason}");
							break;
					}
				}
			}

			// ②.5 **真缺失的材质给兜底灰材质，不再删几何。**
			//
			// 场景：模型引用了某个材质，而所有来源里都没有它（源包打包缺陷，或者它引用了
			// 别的包里的材质、而那个包没一起导入）。以前的做法是把它当成"不绘制"，
			// 连三角形一起剔掉 ⇒ 那个部件**整块消失**：用户看到"缺材质/部件没了"，
			// 而报告里只有一行"某材质没落地"，两边都对不上。
			//
			// 按「不能缺失」的口径：给一块**看得见的哑光中性灰**。
			// 颜色不对是可诊断的（一眼能看出"这块没材质"），部件消失是不可诊断的。
			//
			// ⚠️ `$no_draw` **不走这条** —— 那是源侧明确要求不画，交给 FilterNoDrawPlans 剔几何。
			var fallbackStems = new List<string>();
			foreach (var required in requiredStems.Order(StringComparer.OrdinalIgnoreCase))
			{
				if (idByStem.ContainsKey(required))
					continue;   // 已经落地（真实材质或另一个包提供）

				var reason = skipReasonByStem.TryGetValue(required, out var text) ? text : string.Empty;
				if (reason.Contains("$no_draw", StringComparison.OrdinalIgnoreCase) || DeclaresNoDraw(required))
					continue;   // 源侧要求不绘制，不是缺失

				var fallback = converter.SynthesizeFallbackMaterial(required);
				if (!fallback.IsValid || fallback.Material is null)
				{
					// 兜底都造不出来（模板库缺 advanced / texconv 失败）：这时只能退回去剔几何，
					// 但必须**大声说**，否则又变成"部件悄悄不见了"。
					warnings.Add(
						$"源材质 {required} 缺失，且兜底材质也造不出来（{string.Join("；", fallback.Diagnostics)}）；"
						+ "这个部件会被移除，请在游戏里确认它是不是本来就不该显示。");
					continue;
				}

				idByStem[required] = fallback.Material.Id;
				productByStem[required] = fallback;
				fallbackStems.Add(required);
				foreach (var notice in fallback.Notices)
					warnings.Add(notice);
			}

			// ②.6 `$no_draw`：**保留几何**，材质做成"整段被裁掉"。
			//
			// 老做法是"连三角形一起剔掉"（见 FilterNoDrawPlans）。它忠实于源侧，但不可逆：
			// 数据一旦不在 patch 里，导入之后只能改包重导才能找回来。
			// 实测 L4D2 2829646325（Coach-Ayame）的 cat / facial / basebody2 三份 VMT 都是这种桩，
			// 合计 6,598 个三角形整块消失，而包里三张对应贴图都在。
			//
			// 默认（NoDrawPolicy.Hide）走这条：看得见的东西不变，但几何/材质/段都在 patch 里。
			var invisibleStems = new List<string>();
			if (noDrawPolicy == NoDrawPolicy.Hide)
			{
				foreach (var required in requiredStems.Order(StringComparer.OrdinalIgnoreCase))
				{
					if (idByStem.ContainsKey(required))
						continue;
					var reason = skipReasonByStem.TryGetValue(required, out var text) ? text : string.Empty;
					if (!reason.Contains("$no_draw", StringComparison.OrdinalIgnoreCase) && !DeclaresNoDraw(required))
						continue;

					var invisible = converter.SynthesizeNoDrawMaterial(required);
					if (!invisible.IsValid || invisible.Material is null)
					{
						warnings.Add(
							$"源材质 {required} 声明 $no_draw，但「不显示」材质也造不出来"
							+ $"（{string.Join("；", invisible.Diagnostics)}）；这块会被移除。");
						continue;
					}

					idByStem[required] = invisible.Material.Id;
					productByStem[required] = invisible;
					invisibleStems.Add(required);
					foreach (var notice in invisible.Notices)
						warnings.Add(notice);
				}

				// 这些材质不再算"跳过"：它们有材质、有几何，只是画面上看不见。
				// 两份都报会让报告自相矛盾（"跳过 cat" + "cat 已做成不显示材质"）。
				if (invisibleStems.Count != 0)
				{
					skipped.RemoveAll(line => invisibleStems.Exists(stem =>
						line.StartsWith(stem + "：", StringComparison.OrdinalIgnoreCase)));
				}
			}

			// 把 id 分派回源材质下标（Unit 作者按下标取）。
			if (sourceMaterialNames is not null)
			{
				foreach (var materialIndex in UsedMaterialIndices(plans, geometry!))
				{
					if (materialIndex < 0 || materialIndex >= sourceMaterialNames.Count)
						continue;
					if (idByStem.TryGetValue(StemOf(sourceMaterialNames[materialIndex]), out var materialId))
						materialIdsBySourceMaterial[materialIndex] = materialId;
				}
			}

			// ③ 写出。按 id 去重：`MaterialPaths` 里同名材质出现多次，
			// 而容器层对 `(type_id, file_id)` 重复是**直接抛错**的（`hd2_patchw.py:159-163`）。
			foreach (var stem in stems)
			{
				if (!productByStem.TryGetValue(stem, out var product) || product.Material is null)
					continue;
				var material = product.Material;
				if (!addedMaterialIds.Add(material.Id))
					continue;

				// 在材质包里，材质/贴图**就是**这个包提供的东西，所以是 TargetOutput 而不是依赖
				// （CanonicalPatchSession.Finalize 要求至少一条 TargetOutput）。
				materialSession.AddEntry(new CanonicalPatchSessionEntry(
					new AssetKey(MaterialDependencyResolver.MaterialTypeId, material.Id),
					CanonicalPatchEntryOwnership.TargetOutput,
					material.TocData,
					[],
					[],
					0, 0, 0, 0));
				materialCount++;

				foreach (var texture in product.Textures)
					TryEmitTexture(texture);

				// ⚠️ matcap 是唯一会**跨 VPK 读内容**的地方：`$matcaptint ≥ 0.9` 时底色 =
				// 源底色 + matcap 的面积加权平均色。那张 matcap 可能由别的 VPK 提供
				// （实测：A1 的 40 个金属装饰件都指向 C1/C2 的 `11.vtf`），
				// 于是"金色还是银色"在**转换时**就固定了，光换 patch 换不掉。
				// 不说清楚，用户会以为换个 patch 就能换颜色。
				if (scope is not null
					&& material is not null
					&& product.Identity is { } identity
					&& identity.MatcapComesFromAnotherVpk(scope.OwnsVtf))
				{
					warnings.Add(
						$"材质 {stem} 的金属观感来自 matcap「{identity.MatcapTexture}」，"
						+ $"而它由 {DescribeOrigin(identity.MatcapTexture!)} 提供——烘焙发生在转换时，"
						+ "所以这一项不能靠换 patch 改，必须换队列重新生成这个 patch。");
				}
			}

			// ④ **纯贴图包**：既不出 Unit、也没有自己的 VMT，本 VPK 带的贴图就是它的全部产出。
			//
			//    ⚠️ 两个条件缺一不可：
			//    · 只看"没有 Unit"会把 A1 那 23 张 `_n` 法线图、光照映射、matcap 全部写进包
			//      ——它们不是任何材质的底色，HD2 的模板也用不到，白白撑大几十 MB；
			//    · 只看"没有 VMT"会把模型 VPK 里那几张 vgui 面板图写进去
			//      ——契约说得很清楚：模型 patch 只有 unit，零材质零贴图。
			if (scope is not null && plans.Count == 0 && scope.OwnedVmtNames.Count == 0)
			{
				// 与 ② 同理：每张图都是一次 texconv，串行没有道理。转换并行、写出按名字顺序收口。
				var pureNames = scope.OwnedVtfNames.Order(StringComparer.OrdinalIgnoreCase).ToArray();
				var pureTextures = new SynthesizedTexture?[pureNames.Length];
				var pureFailures = new string?[pureNames.Length];
				var texturesDone = 0;
				Parallel.For(0, pureNames.Length, VpkParallel.Options, textureIndex =>
				{
					if (!addedTextureIds.Contains(Hd2AssetId.Texture(pureNames[textureIndex], idNamespace)))
					{
						if (converter.TryConvertTexture(pureNames[textureIndex], out var texture, out var failure))
							pureTextures[textureIndex] = texture;
						else
							pureFailures[textureIndex] = failure;
					}

					onProgress?.Invoke("textures", Interlocked.Increment(ref texturesDone), pureNames.Length);
				});

				for (var textureIndex = 0; textureIndex < pureNames.Length; textureIndex++)
				{
					if (pureFailures[textureIndex] is { } failure)
					{
						warnings.Add(failure);
						continue;
					}

					if (pureTextures[textureIndex] is { } texture)
						TryEmitTexture(texture);
				}
			}

			// ④.5 材质总账。**必须在 ①~④ 之后、⑤ 之前**：这时"哪些落地了、哪些没有、为什么"
			// 全都定了，而 ⑤ 只是把它们逐条翻成表。
			//
			// 这一份回答的是"上机紫块到底缺什么"，并把两类完全不同的缺口分开：
			//   · $no_draw ：源侧要求不绘制（常用来做网格变体选择）→ 该翻成 section 隐藏；
			//   · 其余     ：真解析不出来（包里没有 / 合成失败）→ 真缺失。
			var noDrawMaterials = new List<string>();
			var missingMaterials = new List<string>();
			foreach (var required in requiredStems.Order(StringComparer.OrdinalIgnoreCase))
			{
				// 做成「不显示」材质的仍然要进这份账：它有 id，但不是"看得见的材质"。
				if (idByStem.ContainsKey(required) && !invisibleStems.Contains(required)) continue;
				var reason = skipReasonByStem.TryGetValue(required, out var text) ? text : string.Empty;
				// **两类判据都要看。**
				// 转换器只在"自己解析到的那份 VMT 直接写着 $no_draw"时才给这个理由；
				// 而 `eye_l` 那类包的 VMT 先走了 include 展开、在别的地方先失败（"展开后没有底色"），
				// 于是理由里没有 $no_draw 三个字——按理由分类会把它误判成"真缺失"。
				// 这里再补一次**原始字节**检查：文件里写着 $no_draw 就是源侧要求不绘制。
				if (reason.Contains("$no_draw", StringComparison.OrdinalIgnoreCase) || DeclaresNoDraw(required))
					noDrawMaterials.Add(required);
				else
					missingMaterials.Add(required);
			}

			bool DeclaresNoDraw(string stem)
			{
				try
				{
					if (scope is null || !scope.TryReadVmt(stem, out var bytes) || bytes.Length == 0) return false;
					// ⚠️ **按 VMT 语法判，不能对原始文本 `Contains("$no_draw")`。**
					// 后者会把注释（`// $no_draw`）、以及 `$no_draw "0"`（= 关）也算成"不要画"，
					// 于是本该显示的部件被剔掉几何。`IsEnabled` 已经处理了"0/false/空值算关"。
					return VmtDocument.Parse(bytes).IsEnabled("$no_draw");
				}
				catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
				{
					return false;
				}
			}

			// 真实落地的数量**要扣掉兜底的**：兜底材质有 id、也确实写进了 patch，
			// 但它不是源侧那个材质。算进"解决"会让总账显示"全部落地"，
			// 而那恰恰是需要被看见的那一种问题（用户看到一块灰面，报告说一切正常）。
			var fallbackSet = fallbackStems.ToHashSet(StringComparer.OrdinalIgnoreCase);
			var invisibleSet = invisibleStems.ToHashSet(StringComparer.OrdinalIgnoreCase);
			materialGap = new VpkMaterialGap(
				RequiredCount: requiredStems.Count,
				ResolvedCount: requiredStems.Count(material =>
					idByStem.ContainsKey(material) && !fallbackSet.Contains(material) && !invisibleSet.Contains(material)),
				NoDraw: noDrawMaterials,
				Missing: missingMaterials,
				Fallback: fallbackStems);

			// `$no_draw` 到底怎么落地、各丢了多少面：**必须点名**。
			// 一份"少了几个材质"的总账看不出"少的是猫耳还是脸"，而这正是用户要判断的事。
			if (noDrawMaterials.Count != 0)
				warnings.Add(DescribeNoDrawParts(noDrawMaterials));

			string DescribeNoDrawParts(IReadOnlyList<string> stems)
			{
				var byStem = new Dictionary<string, (int Triangles, SortedSet<string> Parts)>(StringComparer.OrdinalIgnoreCase);
				if (sourceMaterialNames is not null && geometry is not null)
				{
					var partsByMaterial = new Dictionary<int, SortedSet<string>>();
					foreach (var part in geometry.Parts)
					{
						var end = Math.Min(part.TriangleStart + part.TriangleCount, geometry.Triangles.Count);
						for (var triangleIndex = part.TriangleStart; triangleIndex < end; triangleIndex++)
						{
							var materialIndex = geometry.Triangles[triangleIndex].MaterialIndex;
							if (!partsByMaterial.TryGetValue(materialIndex, out var set))
								partsByMaterial[materialIndex] = set = new SortedSet<string>(StringComparer.Ordinal);
							set.Add(part.BodyPart);
						}
					}

					for (var materialIndex = 0; materialIndex < sourceMaterialNames.Count; materialIndex++)
					{
						var stem = StemOf(sourceMaterialNames[materialIndex]);
						if (!stems.Contains(stem, StringComparer.OrdinalIgnoreCase))
							continue;
						var triangles = 0;
						foreach (var triangle in geometry.Triangles)
						{
							if (triangle.MaterialIndex == materialIndex)
								triangles++;
						}
						byStem[stem] = (triangles,
							partsByMaterial.TryGetValue(materialIndex, out var parts) ? parts : []);
					}
				}

				var detail = stems
					.Order(StringComparer.OrdinalIgnoreCase)
					.Select(stem => byStem.TryGetValue(stem, out var fact) && fact.Triangles != 0
						? $"{stem} {fact.Triangles} 面（{string.Join("/", fact.Parts)}）"
						: stem);
				var head = noDrawPolicy switch
				{
					NoDrawPolicy.Drop =>
						$"{stems.Count} 个材质声明了 $no_draw，已按 `--no-draw drop` **整段剔除**"
						+ "（几何不在 patch 里，导入后无法恢复）：",
					NoDrawPolicy.Hide =>
						$"{stems.Count} 个材质声明了 $no_draw，已按 `--no-draw hide` **保留几何**、材质做成不显示"
						+ "（画面上与 L4D2 一致）：",
					_ =>
						$"{stems.Count} 个材质声明了 $no_draw（源侧默认不绘制），已按默认策略**照常显示**"
						+ "（源侧只是材质层开关，网格仍在文件里；不想看就在 section 组件编辑器里关掉它）：",
				};
				var tail = noDrawPolicy == NoDrawPolicy.Hide
					? "。要看它们：`--no-draw show` 重转一次（管理器里设环境变量 HD2_NO_DRAW=show）。"
					: "。";
				return head + string.Join("、", detail) + tail;
			}

			// **法线图：按整包统计一次。**
			//
			// 法线图过去完全没有消费者——所有材质写进 HD2 的都是平面法线（128,128,255），
			// 而 NekoToon 那套模型普遍带 ATI2N/BC5 高清法线图。现在接上了，但**接了不等于都接上**：
			// 材质包常常只带 VMT 与底色，法线图留在大模型包里；那种情况下只能退回平面法线。
			// 所以必须分两个数报，而不是一句"已接入"。
			//
			// 逐条 notice 会淹没其它警告（几乎每个材质都有 `$bumpmap`），所以只出两行。
			var normalWired = 0;
			var normalMissing = 0;
			foreach (var requiredStem in requiredStems)
			{
				switch (converter.DescribeBumpMap(requiredStem))
				{
					case NormalDelivery.Wired:
						normalWired++;
						break;
					case NormalDelivery.TextureMissing:
						normalMissing++;
						break;
				}
			}

			if (normalWired != 0)
			{
				warnings.Add(
					$"{normalWired}/{requiredStems.Count} 个材质声明了法线图（$bumpmap），已接入 HD2 的法线通道"
					+ "（RGB = 法线、A = 粗糙度）。");
			}

			if (normalMissing != 0)
			{
				warnings.Add(
					$"{normalMissing}/{requiredStems.Count} 个材质声明了法线图，但那张 .vtf 不在这次导入的来源里"
					+ "（材质包通常只带 VMT 与底色），这些材质在游戏里用平面法线，细节立体感会有损失。");
			}

			// 转换之后才知道的两类异常：gamma 被压坏后复原的、以及真的不是切线空间法线的。
			// 合成一个"已接入 N 个"会把这两类藏起来 —— 前者是救回来的，后者是丢掉的。
			var (gammaRecovered, rejectedNormals) = converter.NormalAnomalies;
			if (gammaRecovered != 0)
			{
				warnings.Add(
					$"{gammaRecovered} 张法线图是被当成「颜色」压过 gamma 的（正常的平面法线被压到 ~55），"
					+ "已按「线性→sRGB」复原后接入 HD2 的法线通道。");
			}


			if (rejectedNormals != 0)
			{
				warnings.Add(
					$"{rejectedNormals} 张声明为 $bumpmap 的图看起来不是切线空间法线"
					+ "（XY 均值远离 0，gamma 复原也救不回来），已按无起伏处理。");
			}

			// 把 `$no_draw` 的材质名翻成**材质下标**，供装配阶段丢掉对应三角形的几何。
			//
			// ⚠️ **只有 `$no_draw` 才丢几何。** 真缺失的材质已经在上面的 ②.5 拿到兜底灰材质，
			// 几何必须保留——把它们也丢掉的后果是"部件整块消失"，那正是用户报的"缺材质"里
			// 最糟糕的一种表现：既看不见东西，也没有任何线索指向"是材质缺失"。
			if (sourceMaterialNames is not null && noDrawMaterials.Count != 0 && noDrawPolicy == NoDrawPolicy.Drop)
			{
				var noDrawStems = noDrawMaterials.ToHashSet(StringComparer.OrdinalIgnoreCase);
				for (var materialIndex = 0; materialIndex < sourceMaterialNames.Count; materialIndex++)
				{
					if (noDrawStems.Contains(StemOf(sourceMaterialNames[materialIndex])))
						noDrawMaterialIndices.Add(materialIndex);
				}
			}

			// ⑤ 对照表。**每一条都要有行**——包括"算过但没写进本 patch"和
			// "本 VPK 自己带、模型没用到"。没有这张表，"少了一个材质"只能靠人肉比对。
			var reportedStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			if (sourceMaterialNames is not null)
			{
				var firstSourceIndexByMaterialId = new Dictionary<ulong, int>();
				foreach (var materialIndex in UsedMaterialIndices(plans, geometry!))
				{
					if (materialIndex < 0 || materialIndex >= sourceMaterialNames.Count)
						continue;
					var name = sourceMaterialNames[materialIndex];
					var stem = StemOf(name);
					if (!idByStem.TryGetValue(stem, out var materialId))
						continue;
					var mergedInto = firstSourceIndexByMaterialId.TryGetValue(materialId, out var first) ? first : (int?)null;
					if (mergedInto is null)
						firstSourceIndexByMaterialId[materialId] = materialIndex;
					materialOutcomes.Add(Row(materialIndex, name, stem, materialId, mergedInto));
					reportedStems.Add(stem);
				}
			}

			foreach (var stem in stems)
			{
				if (reportedStems.Contains(stem) || !idByStem.TryGetValue(stem, out var materialId))
					continue;
				materialOutcomes.Add(Row(-1, stem, stem, materialId, null));
			}

			VpkMaterialOutcome Row(int sourceIndex, string name, string stem, ulong materialId, int? mergedInto)
			{
				productByStem.TryGetValue(stem, out var product);
				identityByStem.TryGetValue(stem, out var identity);
				return new VpkMaterialOutcome(
					sourceIndex, name, materialId,
					product?.Material?.TemplateName ?? identity?.TemplateName ?? string.Empty,
					product?.Material?.BaseTexture ?? identity?.BaseTexture ?? string.Empty,
					mergedInto)
				{ Emitted = addedMaterialIds.Contains(materialId), Placeholder = fallbackSet.Contains(stem) };
			}
		}

		void TryEmitTexture(SynthesizedTexture texture)
		{
			var scope = materialScope;
			var providedByVpk = scope is not null && scope.ContainsVtf(texture.SourcePath);
			var ownedHere = scope is null || scope.OwnsVtf(texture.SourcePath);

			// 贴图归谁：由本 VPK 提供的才写；派生出来的（遮罩 / 金属度 / 共享平面法线）
			// 不属于任何 VPK，它们是这个材质的一部分——材质在，它们就得在。
			if (!ownedHere && providedByVpk)
			{
				if (reportedForeignTextures.Add(texture.SourcePath))
				{
					textureOutcomes.Add(new VpkTextureOutcome(
						texture.SourcePath, texture.Id, DescribeOrigin(texture.SourcePath), false,
						"这张贴图由别的 VPK 提供，归那个 VPK 的 patch"));
				}

				return;
			}

			// ⚠️ **贴图 id 只用 basename 派生**（Python 侧的现状，见 Hd2AssetId 的注释），
			// 所以不同目录下的同名贴图会撞同一个 id。
			//
			// **顺序很关键：先查冲突，再去重。**
			// 旧实现是 `if (!addedTextureIds.Add(id)) return;` 在前面、冲突检查在后面 ——
			// 于是同一个 id 第二次出现时**在到达冲突检查之前就返回了**，那一段检查成了死代码，
			// 撞名永远只表现为"静默合并成一张"，没有任何报告。
			//
			// 撞名按**警告**报，不按诊断报：诊断会让整个 patch 消失（见下面的 `diagnostics.Count != 0`
			// 提前返回），而"某张图撞了名"不该连带把整件护甲弄没。宁可出一份带着说明的产物。
			if (textureSourcesById.TryGetValue(texture.Id, out var existingSource))
			{
				if (!string.Equals(existingSource, texture.SourcePath, StringComparison.OrdinalIgnoreCase))
				{
					warnings.Add(
						$"贴图 id 冲突：0x{texture.Id:x16} 同时来自 '{existingSource}' 与 '{texture.SourcePath}'"
						+ "（贴图 id 只按 basename 派生，不同目录同名会撞）；已保留先出现的那张，"
						+ "引用后者的部件会显示成前者的图案");
				}

				return;
			}

			textureSourcesById[texture.Id] = texture.SourcePath;
			addedTextureIds.Add(texture.Id);
			materialSession.AddEntry(new CanonicalPatchSessionEntry(
				new AssetKey(MaterialDependencyResolver.TextureTypeId, texture.Id),
				CanonicalPatchEntryOwnership.TargetOutput,
				texture.TocData,
				texture.GpuData,
				[],
				0, 0, 0, 0));
			textureCount++;
			textureOutcomes.Add(new VpkTextureOutcome(
				texture.SourcePath, texture.Id,
				providedByVpk ? DescribeOrigin(texture.SourcePath) : "派生（随材质）",
				true, string.Empty));
		}

		// 本 VPK 自己带、但既不是任何材质的底色、也没被写进去的贴图。
		// **必须逐条报出来**，否则"少了一张"永远只能靠人肉比对。
		void ReportSkippedTextures()
		{
			foreach (var name in materialScope!.OwnedVtfNames.Order(StringComparer.OrdinalIgnoreCase))
			{
				var id = Hd2AssetId.Texture(name, idNamespace);
				if (addedTextureIds.Contains(id))
					continue;
				textureOutcomes.Add(new VpkTextureOutcome(
					name, id, DescribeOrigin(name), false,
					plans.Count != 0
						? "模型 patch 只出 unit，模型 VPK 里的贴图不进 patch"
						: "没有任何材质的 $basetexture 指向它（法线图、光照映射这类 HD2 模板用不到）"));
			}
		}

		string DescribeOrigin(string textureName)
		{
			if (materialScope is null || string.IsNullOrWhiteSpace(textureName))
				return string.Empty;
			return materialScope.TryGetVtfSource(textureName, out var vpkPath, out _)
				? Path.GetFileName(vpkPath)
				: string.Empty;
		}
	}

	/// <summary>Unit 真正用到的源材质下标（按三角形取，去重后排序）。</summary>
	/// <summary>
	/// 逐 section 的源材质：名字取自模型的材质表，id 取自这次转换算出来的对照表。
	/// <para>
	/// 对齐方式是<b>下标</b>：<c>mesh.Sections</c> 的顺序就是写进 Unit 的材质绑定顺序，
	/// 也是 <c>sections.json</c> 里 section 的顺序。写入方（<see cref="WardrobeSectionMapFile"/>）
	/// 会再用材质 id 自校验一次，所以即使这里错位，也不会把开关接到别的部件上。
	/// </para>
	/// </summary>
	private static IReadOnlyList<VpkSectionSource> BuildSectionSources(
		RetargetedMesh mesh,
		IReadOnlyList<string>? sourceMaterialNames,
		IReadOnlyDictionary<int, ulong> materialIdsBySourceMaterial)
	{
		if (sourceMaterialNames is null || sourceMaterialNames.Count == 0)
			return [];
		var sources = new List<VpkSectionSource>(mesh.Sections.Count);
		foreach (var section in mesh.Sections)
		{
			var index = section.SourceMaterialIndex;
			if (index < 0 || index >= sourceMaterialNames.Count)
			{
				sources.Add(new VpkSectionSource(string.Empty, 0));
				continue;
			}

			sources.Add(new VpkSectionSource(
				StemOf(sourceMaterialNames[index]),
				materialIdsBySourceMaterial.TryGetValue(index, out var id) ? id : 0UL));
		}

		return sources;
	}

	private static IEnumerable<int> UsedMaterialIndices(IReadOnlyList<VpkUnitPlan> plans, SourceGeometry geometry)
		=> plans
			.SelectMany(plan => plan.Group.TriangleIndices)
			.Select(triangle => geometry.Triangles[triangle].MaterialIndex)
			.Distinct()
			.Order();

	/// <summary>
	/// 把"整块都用 <c>$no_draw</c> 材质"的三角形从计划里剔掉。
	/// <para>
	/// 剔除的是<b>三角形</b>而不是整个 Unit：一个 Unit 里通常既有要画的部件、也有 `$no_draw` 的，
	/// 按材质逐段剔除才是源侧的意思。剔除之后 section 自然只剩要画的那些——
	/// 不会留下指向"不存在的材质"的 section（那正是紫块的来源）。
	/// </para>
	/// <para>
	/// 整条 Unit 的三角形全被剔掉时，这条 Unit 不产出（记录一条警告，别让"少了几个 Unit"无声无息）。
	/// </para>
	/// </summary>
	private static IReadOnlyList<VpkUnitPlan> FilterNoDrawPlans(
		IReadOnlyList<VpkUnitPlan> plans,
		SourceGeometry? geometry,
		IReadOnlySet<int> noDrawMaterialIndices,
		List<string> warnings)
	{
		if (plans.Count == 0 || geometry is null || noDrawMaterialIndices.Count == 0) return plans;

		var filtered = new List<VpkUnitPlan>(plans.Count);
		var dropped = new List<string>();
		foreach (var plan in plans)
		{
			var kept = plan.Group.TriangleIndices
				.Where(triangle => !noDrawMaterialIndices.Contains(geometry.Triangles[triangle].MaterialIndex))
				.ToArray();
			if (kept.Length == 0)
			{
				dropped.Add(plan.Group.Name);
				continue;
			}

			filtered.Add(kept.Length == plan.Group.TriangleIndices.Count
				? plan
				: plan with { Group = plan.Group with { TriangleIndices = kept } });
		}

		if (dropped.Count != 0)
		{
			warnings.Add($"{dropped.Count} 个部件整块都是源侧 $no_draw（不绘制），已按 `--no-draw drop` 不产出："
				+ $"{string.Join("、", dropped.Take(8))}"
				+ (dropped.Count > 8 ? $"…（共 {dropped.Count} 个）" : string.Empty));
		}

		return filtered;
	}

	private static string StemOf(string materialName)
		=> Path.GetFileNameWithoutExtension(VmtDocument.NormalizePath(materialName));

	/// <summary>
	/// 由 Unit patch 名推出材质包名：把结尾的 <c>_N</c> 加一
	/// （<c>9ba626afa44a3aa3.patch_0</c> → <c>…_1</c>）。同一个存档用不同 patch 序号是常规做法。
	/// </summary>
	internal static string NextPatchIndexName(string patchFileName)
	{
		var separator = patchFileName.LastIndexOf('_');
		if (separator < 0 || separator == patchFileName.Length - 1
			|| !int.TryParse(patchFileName[(separator + 1)..], out var index))
			return patchFileName + ".materials";
		return $"{patchFileName[..separator]}_{index + 1}";
	}
}
