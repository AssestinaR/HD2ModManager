using System.Security.Cryptography;
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
public sealed record VpkUnitPlan(
	MeshGroup Group,
	UnitDonor Donor,
	string SlotName,
	string PieceTypeName);

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

	/// <summary><c>-1</c> 表示这个材质不是模型引用的，而是本 VPK 自己带的（模型没用到的那些）。</summary>
	public bool UsedByModel => SourceMaterialIndex >= 0;
}

/// <summary>一张贴图的产出记录。同样是为了让"没写"有明确理由，而不是靠猜。</summary>
public sealed record VpkTextureOutcome(string Name, ulong Id, string Origin, bool Emitted, string Reason);

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
	private readonly UnitAuthor author = new();

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
		string? idNamespace = null)
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

		// 与 CustomModelConversionService 同构：一个随机块 + 递增，保证同批产物不撞号。
		// 给了 `unitIdSeed` 就用它 —— 用来**原地替换**一个已经登记过的包：
		// unit id 保持不变，工作区里的映射就不用重做，改动只剩"几何变了"这一个变量。
		var seed = unitIdSeed ?? (BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)) & 0x7fffffffffff0000UL);
		var index = 1UL;
		// 素体只由一个模型构成。本体部位要按目标原生行声明（见 VpkRegistryPiece 的说明），
		// 配件才用 Any。层号按槽位递增：本体占 0，配件从 1 起。
		var layerBySlot = new Dictionary<uint, uint>();
		foreach (var plan in plans)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var unitId = (seed + index).ToString("x16");
			index++;
			while (forbidden.Contains(unitId))
			{
				unitId = (seed + index).ToString("x16");
				index++;
			}

			forbidden.Add(unitId);

			var slot = plan.Group.Region.Slot();
			IReadOnlyList<VpkRegistryPiece> registryPieces;
			if (plan.Group.Kind == MeshGroupKind.BodyPart)
			{
				layerBySlot[slot] = 1;
				registryPieces = plan.Group.Region == BodyRegion.Head
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
				registryPieces = [new VpkRegistryPiece(3, slot, layer, false)];
			}

			var mesh = RetargetedMeshBuilder.Build(plan.Group, geometry!, retarget!, avatarTransformInfo!);
			var customization = new UnitCustomizationInfo(
				"HelldiverCustomizationBodyType_Any",
				$"HelldiverCustomizationSlot_{plan.SlotName}",
				plan.Donor.Model.CustomizationInfo.Weight,
				$"HelldiverCustomizationPieceType_{plan.PieceTypeName}");

			var authored = author.Author(new UnitAuthorRequest(
				plan.Donor.Model, plan.Donor.TocData, avatarTransformInfo!, mesh, customization,
				plan.Donor.FileId, materialIdsBySourceMaterial));

			units.Add(new VpkUnitOutcome(
				plan.Group.Name, unitId, plan.Group.Region, slot, registryPieces,
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
		}

		if (diagnostics.Count != 0)
			return new(null, units, diagnostics);

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
			if (sourceMaterialNames is not null)
			{
				foreach (var materialIndex in UsedMaterialIndices(plans, geometry!))
				{
					if (materialIndex < 0 || materialIndex >= sourceMaterialNames.Count)
						continue;
					var stem = StemOf(sourceMaterialNames[materialIndex]);
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
			foreach (var stem in stems)
			{
				var owned = scope is null || scope.OwnsVmt(stem);
				if (owned)
				{
					var synthesized = converter.Convert(stem);
					if (synthesized.IsSkipped)
					{
						// 源侧自己写着"别画"，不是问题——单列，别混进警告。
						skipped.Add(synthesized.SkipReason!);
						continue;
					}

					if (!synthesized.IsValid || synthesized.Material is null)
					{
						warnings.Add($"源材质 {stem} 合成失败：{string.Join("；", synthesized.Diagnostics)}");
						continue;
					}

					idByStem[stem] = synthesized.Material.Id;
					productByStem[stem] = synthesized;
				}
				else
				{
					var report = converter.Resolve(stem);
					switch (report.Outcome)
					{
						case MaterialResolveOutcome.Resolved:
							idByStem[stem] = report.Identity!.Id;
							identityByStem[stem] = report.Identity!;
							break;
						case MaterialResolveOutcome.Skipped:
							skipped.Add(report.Reason);
							break;
						default:
							// 这个材质不属于本 patch，但 Unit 要靠它的 id。解析不出来 = 那一段会
							// 退回外壳护甲的材质，上机表现为"某处贴图是原版护甲的"。
							warnings.Add($"源材质 {stem} 解析失败（本 patch 不写它，但 Unit 需要它的 id）：{report.Reason}");
							break;
					}
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
				foreach (var name in scope.OwnedVtfNames.Order(StringComparer.OrdinalIgnoreCase))
				{
					if (addedTextureIds.Contains(Hd2AssetId.Texture(name, idNamespace)))
						continue;
					if (!converter.TryConvertTexture(name, out var texture, out var failure))
					{
						warnings.Add(failure);
						continue;
					}

					TryEmitTexture(texture);
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
				{ Emitted = addedMaterialIds.Contains(materialId) };
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

			if (!addedTextureIds.Add(texture.Id))
				return;

			// ⚠️ 贴图 id **只用 basename** 派生（Python 侧的现状，见 Hd2AssetId 的注释），
			// 所以不同目录下的同名贴图会撞同一个 id。撞了如果只是去重，就会**静默合并成一张**，
			// 表现为"某个部件用了别的部件的贴图"。来源不一致时必须报错，不能吞。
			if (textureSourcesById.TryGetValue(texture.Id, out var existingSource)
				&& !string.Equals(existingSource, texture.SourcePath, StringComparison.OrdinalIgnoreCase))
			{
				diagnostics.Add(
					$"贴图 id 冲突：0x{texture.Id:x16} 同时来自 '{existingSource}' 与 '{texture.SourcePath}'" +
					"（贴图 id 只按 basename 派生，不同目录同名会撞）");
				return;
			}

			textureSourcesById[texture.Id] = texture.SourcePath;
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
