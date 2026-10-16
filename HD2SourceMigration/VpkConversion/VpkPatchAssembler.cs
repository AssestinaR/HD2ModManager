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
	int? MergedIntoSourceIndex);

public sealed record VpkPackageResult(
	string? TocPath,
	IReadOnlyList<VpkUnitOutcome> Units,
	IReadOnlyList<string> Diagnostics)
{
	public bool IsValid => TocPath is not null && Diagnostics.Count == 0;

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
}

/// <summary>
/// 把切分好的部件写成一个新的 Patch 包。
/// <para>
/// 每个 Unit 拿一个<b>全新的随机 AssetKey</b>——这是"自定义素体"的前提：它是新资产，不是覆盖原版。
/// ID 用固定种子的递增块（与 <c>CustomModelConversionService</c> 同一思路），这样同一次转换的产物
/// 之间不会撞号，而且整体可复现。调用方负责再与原版资产做一次冲突检查。
/// </para>
/// </summary>
public sealed class VpkPatchAssembler
{
	private readonly UnitAuthor author = new();

	/// <param name="avatarTransformInfo">HD2 avatar 骨架，会被并进每个外壳的 TransformInfo。</param>
	/// <param name="headerTemplateTocData">输出 Patch 的头部模板（用外壳所在存档的 TOC）。</param>
	/// <param name="patchFileName">输出文件名，形如 <c>9ba626afa44a3aa3.patch_0</c>。</param>
	/// <param name="modelKey">素体 Key（形如 <c>custom-&lt;guid&gt;</c>）。由调用方生成并负责登记。</param>
	/// <param name="sourceMaterialNames">
	/// 源模型的材质裸名表（下标 = <c>RetargetedSection.SourceMaterialIndex</c>），
	/// 来自 <c>SourceModelDocument.MaterialPaths</c>。给了它并且给了
	/// <paramref name="materialConverter"/> 时，每个 Unit 的每一段都会引用<b>自己新造的材质</b>，
	/// 并把 Material + Texture 作为依赖条目写进包；没给就沿用外壳护甲的材质（M2 的过渡状态）。
	/// </param>
	public async ValueTask<VpkPackageResult> AssembleAsync(
		IReadOnlyList<VpkUnitPlan> plans,
		RetargetResult retarget,
		SourceGeometry geometry,
		UnitTransformInfo avatarTransformInfo,
		byte[]? headerTemplateTocData,
		string outputDirectory,
		string patchFileName,
		string modelKey,
		IReadOnlyList<string>? forbiddenUnitIds = null,
		CancellationToken cancellationToken = default,
		IReadOnlyList<string>? sourceMaterialNames = null,
		SourceMaterialConverter? materialConverter = null,
		string? materialPackFileName = null,
		ulong? unitIdSeed = null)
	{
		ArgumentNullException.ThrowIfNull(plans);
		ArgumentNullException.ThrowIfNull(retarget);
		ArgumentNullException.ThrowIfNull(geometry);
		ArgumentNullException.ThrowIfNull(avatarTransformInfo);

		var diagnostics = new List<string>();
		var units = new List<VpkUnitOutcome>();
		var session = new CanonicalPatchSession();
		var forbidden = new HashSet<string>(forbiddenUnitIds ?? [], StringComparer.OrdinalIgnoreCase);

		// 素体只由一个模型构成。本体部位要按目标原生行声明（见 VpkRegistryPiece 的说明），
		// 配件才用 Any。层号按槽位递增：本体占 0，配件从 1 起。
		var layerBySlot = new Dictionary<uint, uint>();

		// 先把**选中部件真正用到的**源材质各合成一次：同一个源材质被多个 Unit 共用，
		// 重复合成会产出同 id 但重复的条目（容器层会因此报重复键）。
		var materialIdsBySourceMaterial = new Dictionary<int, ulong>();
		var synthesizedTextures = new List<SynthesizedTexture>();
		var textureSourcesById = new Dictionary<ulong, string>();
		var materialOutcomes = new List<VpkMaterialOutcome>();
		// 材质 id → 第一个产生它的源材质序号。用来把"被去重合并"显式报出来
		// （材质 id 只由 $basetexture 决定，不同源材质指向同一张底色时必然撞号）。
		var firstSourceIndexByMaterialId = new Dictionary<ulong, int>();
		// ⚠️ 材质也必须按 id 去重。`SourceModelDocument.MaterialPaths` 里**同名材质会出现多次**
		// （不同源序号、同一个名字），只按源序号去重会让同一个 MaterialId 被 AddEntry 两次，
		// 而容器层对 `(type_id, file_id)` 重复是**直接抛错**的（`hd2_patchw.py:159-163`）。
		var addedMaterialIds = new HashSet<ulong>();
		// ⚠️ 材质**不能**进 Unit patch。约定是"一次导入 = 一个 VPK = 一个 patch"，
		// 而材质来自**别的** VPK，所以它们是外部材质包，要写进**另一个** patch 文件；
		// Unit patch 只保留对 MaterialId 的引用。
		var materialSession = new CanonicalPatchSession();
		var materialCount = 0;
		var textureCount = 0;
		if (materialConverter is not null && sourceMaterialNames is not null)
		{
			foreach (var sourceIndex in plans
				.SelectMany(plan => plan.Group.TriangleIndices)
				.Select(triangle => geometry.Triangles[triangle].MaterialIndex)
				.Distinct()
				.OrderBy(index => index))
			{
				if (sourceIndex < 0 || sourceIndex >= sourceMaterialNames.Count)
					continue;
				var synthesized = materialConverter.Convert(sourceMaterialNames[sourceIndex]);
				if (!synthesized.IsValid || synthesized.Material is null)
				{
					diagnostics.Add($"源材质 #{sourceIndex}（{sourceMaterialNames[sourceIndex]}）合成失败：" +
						string.Join("；", synthesized.Diagnostics));
					continue;
				}

				materialIdsBySourceMaterial[sourceIndex] = synthesized.Material.Id;
				// 每个源材质都出一条记录，**包括被合并掉的**——静默合并才是真正的问题。
				int? mergedInto = null;
				if (firstSourceIndexByMaterialId.TryGetValue(synthesized.Material.Id, out var firstIndex))
					mergedInto = firstIndex;
				else
					firstSourceIndexByMaterialId[synthesized.Material.Id] = sourceIndex;
				materialOutcomes.Add(new VpkMaterialOutcome(
					sourceIndex, sourceMaterialNames[sourceIndex], synthesized.Material.Id,
					synthesized.Material.TemplateName, synthesized.Material.BaseTexture, mergedInto));
				if (!addedMaterialIds.Add(synthesized.Material.Id))
					continue;
				foreach (var texture in synthesized.Textures)
				{
					// ⚠️ 贴图 id **只用 basename** 派生（Python 侧的现状，见 Hd2AssetId 的注释），
					// 所以不同目录下的同名贴图会撞同一个 id。撞了如果只是去重，就会**静默合并成一张**，
					// 表现为"某个部件用了别的部件的贴图"。来源不一致时必须报错，不能吞。
					if (textureSourcesById.TryGetValue(texture.Id, out var existingSource))
					{
						if (!string.Equals(existingSource, texture.SourcePath, StringComparison.OrdinalIgnoreCase))
						{
							diagnostics.Add(
								$"贴图 id 冲突：0x{texture.Id:x16} 同时来自 '{existingSource}' 与 '{texture.SourcePath}'" +
								"（贴图 id 只按 basename 派生，不同目录同名会撞）");
						}

						continue;
					}

					textureSourcesById[texture.Id] = texture.SourcePath;
					synthesizedTextures.Add(texture);
				}

				// 在材质包里，材质/贴图**就是**这个包提供的东西，所以是 TargetOutput 而不是依赖
				// （CanonicalPatchSession.Finalize 要求至少一条 TargetOutput）。
				materialSession.AddEntry(new CanonicalPatchSessionEntry(
					new AssetKey(MaterialDependencyResolver.MaterialTypeId, synthesized.Material.Id),
					CanonicalPatchEntryOwnership.TargetOutput,
					synthesized.Material.TocData,
					[],
					[],
					0, 0, 0, 0));
				materialCount++;
			}

			foreach (var texture in synthesizedTextures)
			{
				materialSession.AddEntry(new CanonicalPatchSessionEntry(
					new AssetKey(MaterialDependencyResolver.TextureTypeId, texture.Id),
					CanonicalPatchEntryOwnership.TargetOutput,
					texture.TocData,
					texture.GpuData,
					[],
					0, 0, 0, 0));
				textureCount++;
			}
		}

		// 与 CustomModelConversionService 同构：一个随机块 + 递增，保证同批产物不撞号。
		// 给了 `unitIdSeed` 就用它 —— 用来**原地替换**一个已经登记过的包：
		// unit id 保持不变，工作区里的映射就不用重做，改动只剩"几何变了"这一个变量。
		var seed = unitIdSeed ?? (BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)) & 0x7fffffffffff0000UL);
		var index = 1UL;
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

			var mesh = RetargetedMeshBuilder.Build(plan.Group, geometry, retarget, avatarTransformInfo);
			var customization = new UnitCustomizationInfo(
				"HelldiverCustomizationBodyType_Any",
				$"HelldiverCustomizationSlot_{plan.SlotName}",
				plan.Donor.Model.CustomizationInfo.Weight,
				$"HelldiverCustomizationPieceType_{plan.PieceTypeName}");

			var authored = author.Author(new UnitAuthorRequest(
				plan.Donor.Model, plan.Donor.TocData, avatarTransformInfo, mesh, customization,
				plan.Donor.FileId, materialIdsBySourceMaterial));

			units.Add(new VpkUnitOutcome(
				plan.Group.Name, unitId, plan.Group.Region, slot, registryPieces,
				mesh.VertexCount, mesh.TriangleCount,
				authored.TocData?.Length ?? 0, authored.GpuData?.Length ?? 0, authored.Diagnostics));

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

		session.Finalize(CanonicalDependencyClosureValidation.Valid);
		var written = await new CanonicalPatchWriter()
			.WriteAsync(session, outputDirectory, patchFileName, headerTemplateTocData, overwriteExisting: true, cancellationToken)
			.ConfigureAwait(false);

		// 材质包写进**另一个** patch 文件（同一个存档的下一个 patch 序号）。
		string? materialPackPath = null;
		if (materialCount != 0 || textureCount != 0)
		{
			materialSession.Finalize(CanonicalDependencyClosureValidation.Valid);
			var packName = materialPackFileName ?? NextPatchIndexName(patchFileName);
			var packWritten = await new CanonicalPatchWriter()
				.WriteAsync(materialSession, outputDirectory, packName, headerTemplateTocData, overwriteExisting: true, cancellationToken)
				.ConfigureAwait(false);
			materialPackPath = packWritten.TocFilePath;
		}

		return new(written.TocFilePath, units, [])
		{
			ModelKey = modelKey,
			MaterialPackPath = materialPackPath,
			MaterialCount = materialCount,
			TextureCount = textureCount,
			Materials = materialOutcomes,
		};
	}

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
