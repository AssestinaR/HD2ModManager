using System.Buffers.Binary;
using HD2SourceModel;

namespace HD2SourceMigration.VpkConversion;

/// <summary>材质身份串里的 matcap 后缀要不要加，以及底色到底用哪张图。</summary>
public enum MatCapMode
{
	/// <summary>
	/// 把 matcap 的<b>面积加权平均色</b>烘进源底色。这是 M3 实机验证过的做法。
	/// <para>
	/// 代价：matcap 的<b>内容</b>跨 VPK 参与了烘焙，于是"金还是银"在转换时就固定了——
	/// C1/C2 这类"只换 matcap"的 VPK 没法成为独立 patch。
	/// </para>
	/// </summary>
	Bake = 0,

	/// <summary>
	/// 直接把 <b>matcap 本身当底色</b>（贴图 id = <c>hash(matcap 名)</c>），不烘焙。
	/// <para>
	/// 好处：matcap 由哪个 VPK 提供就归哪个 patch，于是 C1/C2 变成真正可换的 patch，
	/// 「一个 VPK 一个 patch」这条约定在金属色上也成立。
	/// 代价：观感从"平色金属"变成"整张 matcap 图当 albedo"——需要实机对照。
	/// </para>
	/// </summary>
	Albedo = 1,
}

/// <summary>一个自造贴图的产物：新 id + 可直接进包的 TocData 与 GPU 侧车字节。</summary>
public sealed record SynthesizedTexture(
	ulong Id,
	byte[] TocData,
	byte[] GpuData,
	string SourcePath,
	string FormatName,
	int Width,
	int Height);

/// <summary>一个自造材质的产物。</summary>
public sealed record SynthesizedMaterial(
	ulong Id,
	byte[] TocData,
	string TemplateName,
	string BaseTexture,
	IReadOnlyList<ulong> TextureIds);

/// <summary>
/// 材质的<b>身份</b>——不编码任何贴图就能得到的那部分。
/// <para>
/// 单独拿出来的用途：模型 patch 必须让 Unit 引用<b>正确的</b>材质 id，
/// 但那些材质与贴图归材质 patch 所有，不该被写进模型 patch。
/// </para>
/// </summary>
public sealed record MaterialIdentity(
	ulong Id,
	string TemplateName,
	string BaseTexture,
	bool UseAlphaClip,
	string? MatcapTexture,
	bool MatcapBaked)
{
	/// <summary>
	/// matcap 的<b>内容</b>会跨 VPK 参与烘焙，而材质 id 只由名字决定。
	/// <para>
	/// 这是整条链上<b>唯一</b>打破"patch 之间互不依赖"的地方：`$matcaptint ≥ 0.9` 时
	/// 底色是"源底色 + matcap 的面积加权平均色"，而那张 matcap 可能在<b>另一个</b> VPK 里
	/// （实测：A1 的 40 个金属装饰件都指向 C1/C2 的 <c>11.vtf</c>）。
	/// 于是"选金色还是银色"在<b>转换时</b>就被固定了，光换 patch 换不掉。
	/// </para>
	/// <para>
	/// 调用方（装配器）应当据此报警，而不是让用户以为换个 patch 就能换颜色。
	/// </para>
	/// </summary>
	public bool MatcapComesFromAnotherVpk(Func<string, bool> ownsTexture)
		=> MatcapBaked && MatcapTexture is not null && !ownsTexture(MatcapTexture);
}

public sealed record MaterialSynthesisResult(
	SynthesizedMaterial? Material,
	IReadOnlyList<SynthesizedTexture> Textures,
	IReadOnlyList<string> Diagnostics)
{
	public bool IsValid => Material is not null && Diagnostics.Count == 0;

	/// <summary>
	/// 这个源材质<b>不该被转换</b>（不是"转换失败"）的理由。非 <c>null</c> 时 <see cref="Material"/> 为 null。
	/// <para>
	/// 目前唯一的来源是 <c>$no_draw 1</c>：L4D2 用它把整个材质藏起来，源侧根本不绘制。
	/// 把它当失败处理会让整个 patch 写不出来；当成功处理又会造出一个"看得见"的材质——
	/// 两者都错。所以单列一类。
	/// </para>
	/// </summary>
	public string? SkipReason { get; init; }

	public bool IsSkipped => SkipReason is not null;

	/// <summary>这个材质的身份（id / 模板 / 底色名 / matcap 来源）。转换成功时非 null。</summary>
	public MaterialIdentity? Identity { get; init; }

	public static MaterialSynthesisResult Failed(string reason) => new(null, [], [reason]);

	public static MaterialSynthesisResult Skipped(string reason) => new(null, [], []) { SkipReason = reason };
}

/// <summary>一次"只解析身份、不编码贴图"的结果。</summary>
public enum MaterialResolveOutcome
{
	/// <summary>拿到了 id。</summary>
	Resolved = 0,

	/// <summary>这个材质不该转换（见 <see cref="MaterialSynthesisResult.SkipReason"/>）。</summary>
	Skipped = 1,

	/// <summary>解析失败——读不到 VMT/VTF，或 VMT 缺少必要声明。</summary>
	Failed = 2,
}

public sealed record MaterialResolveReport(MaterialResolveOutcome Outcome, MaterialIdentity? Identity, string Reason);

/// <summary>
/// 源材质（VMT + VTF）→ HD2 <c>Material</c> + <c>Texture</c>。
/// <para>
/// 这是"每个 Unit 引用自己的材质"这条要求的<b>素材来源</b>：转换器产出的
/// <see cref="SynthesizedMaterial.Id"/> 就是塞进 <c>CanonicalMaterialSectionProvenance.MaterialId</c>
/// 的那个值——在那之前它一直是外壳护甲的材质。
/// </para>
/// <para>
/// <b>底色、alpha 遮罩与模板切换。</b>模板在 <c>advanced</c> 与 <c>alphaclip</c> 之间选：
/// 判据是"VMT 声明要透明（<c>$translucent</c>/<c>$alphatest</c>）<b>且</b>贴图 alpha 真的在表达镂空"
/// 两条同时成立。HD2 的 <c>advanced</c> 模板<b>没有 alpha 槽</b>，做不到镂空；
/// <c>alphaclip</c> 的槽 1 就是 <c>OpacityClipMap</c>（白=可见、黑=透明）。
/// </para>
/// <para>
/// <b>⚠️ 底色 alpha 必须清零。</b>实测参考模组的槽 5 是 <c>(209,197,199,0)</c>；
/// 我们的源底色 alpha 在 170..255 之间，直接用会让<b>整件护甲自发光</b>
/// （<c>fix_materials.py:28-33</c>）。所以走两步：先解成未压缩 RGBA 把 alpha 抹掉，再编 BC7
/// —— BC7 是压缩格式，直接改它的 alpha 得自己实现编码器。
/// </para>
/// </summary>
public sealed class SourceMaterialConverter
{
	/// <summary><c>advanced</c> 模板里底色/自发光的槽位下标（<c>fix_appearance.py:86</c>）。</summary>
	public const int AdvancedAlbedoSlot = 5;

	/// <summary><c>alphaclip</c> 模板的三个槽（<c>fix_alphaclip.py:194</c> 断言过这三个键）。</summary>
	public const int AlphaClipNormalSlot = 0;

	public const int AlphaClipOpacitySlot = 1;

	public const int AlphaClipBaseColorSlot = 2;

	/// <summary><c>advanced</c> 槽 2 = <c>Nar</c>（RGB 法线、<b>A 粗糙度</b>）。</summary>
	public const int AdvancedNarSlot = 2;

	/// <summary><c>advanced</c> 槽 3 = <c>IlluminateData</c>（金属度）。</summary>
	public const int AdvancedMetallicSlot = 3;

	/// <summary>
	/// 共享 PBR 贴图里 <b>alpha</b> 通道的粗糙度值（<c>fix_materials.py:89</c> 的 <c>PBR_ROUGHNESS = 0.62</c>）。
	/// </summary>
	public const byte PbrRoughness = 158;   // 0.62 × 255 四舍五入

	/// <summary>
	/// 共享 PBR 贴图的金属度（<c>fix_materials.py:90</c> 的 <c>DEFAULT_METALLIC = (6,6,6,255)</c>，
	/// "参考模组的金属图实测 6.4"）。
	/// <para><b>不是 191。</b>191 是 matcap 那条路的值。</para>
	/// </summary>
	public const byte DefaultMetallic = 6;

	private readonly SourceMaterialSource source;
	private readonly Bc7Encoder encoder;
	private readonly Hd2MaterialTemplateLibrary templates;

	public SourceMaterialConverter(
		SourceMaterialSource source,
		Bc7Encoder encoder,
		Hd2MaterialTemplateLibrary templates,
		AlbedoAdjustment? albedoAdjustment = null,
		IReadOnlyDictionary<string, double[]>? matcapMeans = null,
		MatCapMode matcapMode = MatCapMode.Bake)
	{
		this.source = source;
		this.encoder = encoder;
		this.templates = templates;
		this.albedoAdjustment = albedoAdjustment;
		this.matcapMeans = matcapMeans;
		this.matcapMode = matcapMode;
	}

	/// <summary>matcap 怎么处理：烘进底色，还是直接当底色。见 <see cref="MatCapMode"/>。</summary>
	private readonly MatCapMode matcapMode;

	/// <summary>材质裸名 → 该材质上 matcap 贴图的面积加权平均色（<see cref="MatCapSampler"/> 算出来的）。</summary>
	private readonly IReadOnlyDictionary<string, double[]>? matcapMeans;

	/// <summary>
	/// 为一批源材质预算 matcap 平均色，供构造转换器时传入。
	/// <para>
	/// 放在<b>构造之前</b>算，是因为平均色需要<b>几何</b>（按三角形面积加权），
	/// 而转换器是按材质名逐个工作的、拿不到"整个模型的三角形"。调用方（CLI/装配器）
	/// 同时握有几何与材质表，所以这一步归它。
	/// </para>
	/// <para>
	/// <b>只有真的命中 matcap 规则（tint ≥ 0.9）的材质才会被算</b>——
	/// 其余的 matcap 只是视角相关的叠加高光，不需要平均色，也就不去解码它们的贴图（省一次 BC 解码）。
	/// </para>
	/// </summary>
	public static IReadOnlyDictionary<string, double[]> ComputeMatCapMeans(
		SourceMaterialSource source,
		SourceGeometry geometry,
		IReadOnlyList<string> materialNames,
		Bc7Encoder encoder)
	{
		ArgumentNullException.ThrowIfNull(source);
		ArgumentNullException.ThrowIfNull(geometry);
		ArgumentNullException.ThrowIfNull(materialNames);
		ArgumentNullException.ThrowIfNull(encoder);

		var means = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
		for (var index = 0; index < materialNames.Count; index++)
		{
			var stem = Path.GetFileNameWithoutExtension(VmtDocument.NormalizePath(materialNames[index]));
			if (string.IsNullOrWhiteSpace(stem) || means.ContainsKey(stem))
				continue;
			if (!source.TryReadVmt(stem, out var vmtBytes))
				continue;

			var vmt = VmtDocument.Parse(vmtBytes);
			if (!MaterialAppearance.Evaluate(vmt).MatCapIsTheSurface)
				continue;

			// matcap 贴图的名字与底色同源：`$matcaptexture` 缺省时回落到 `$basetexture`
			// （见 VmtDocument.MatCapTexture）。
			var matcapName = Path.GetFileNameWithoutExtension(VmtDocument.NormalizePath(vmt.MatCapTexture ?? string.Empty));
			if (string.IsNullOrWhiteSpace(matcapName) || !source.TryReadVtf(matcapName, out var vtfBytes))
				continue;

			try
			{
				var mean = ComputeOneMatCapMean(geometry, index, vtfBytes, encoder);
				if (mean is not null)
					means[stem] = mean;
			}
			catch (Exception exception) when (exception is InvalidDataException or IOException or InvalidOperationException)
			{
				// 单张 matcap 解码失败不该让整批转换停下；拿不到平均色就只是不烘。
				continue;
			}
		}

		return means;
	}

	private static double[]? ComputeOneMatCapMean(SourceGeometry geometry, int materialIndex, byte[] vtfBytes, Bc7Encoder encoder)
	{
		var vtf = VtfFile.Read(vtfBytes);
		var dds = BuildSourceDds(vtf);
		var rgba = encoder.DecodeToRgba(dds);
		var image = MatCapImage.TryCreate(rgba, vtf.Width, vtf.Height);
		return image is null ? null : MatCapSampler.Compute(geometry, materialIndex, image);
	}

	/// <summary>
	/// 把 VTF 的 mip0 包成一份带 DX10 头的 DDS——texconv 只吃 DDS。
	/// <para>
	/// 压缩格式直接搬运；非压缩格式要先扩成 <c>R8G8B8A8_UNORM</c>
	/// （texconv 不认 RGB888 这种没有 DXGI 对应的布局），否则整条路会卡在"这张贴图是未压缩的"。
	/// </para>
	/// </summary>
	private static byte[] BuildSourceDds(VtfFile vtf)
	{
		var dxgi = VtfFile.ToDxgi(vtf.HighResFormat);
		return dxgi is not null
			? Hd2TextureAsset.BuildDds(vtf.Width, vtf.Height, dxgi.Value, [vtf.Mips[0]])
			: Hd2TextureAsset.BuildDds(vtf.Width, vtf.Height, Rgba8Unorm, [ExpandToRgba(vtf)]);
	}

	/// <summary>
	/// 底色压暗。默认关闭（<c>null</c>）——它改的是观感，不是正确性，
	/// 所以在能看到实际效果之前不擅自打开。
	/// </summary>
	private readonly AlbedoAdjustment? albedoAdjustment;

	/// <summary>
	/// 一个源材质读齐之后的状态：<b>模板判定已经做完，但还没有编码任何贴图</b>。
	/// <para>
	/// 拆出这一层，是因为"只为了拿到材质 id"和"真的要把贴图写进包"是两种不同的需求。
	/// 模型 patch 只需要前者（它的贴图归材质 patch 所有），而编码一张 2048² 的 BC7
	/// 要一两秒——几十个材质乘上去就是几分钟的纯浪费。
	/// </para>
	/// </summary>
	private sealed record LoadedMaterial(
		string Stem,
		string BaseTexture,
		string TextureName,
		VtfFile Vtf,
		byte[] Rgba,
		bool UseAlphaClip,
		string TemplateName,
		string Identity,
		ulong MaterialId,
		string? EmissionTexture,
		double EmissionTint,
		double[]? MatcapMean,
		double MatcapTint,
		string? MatcapTextureName,
		bool MatcapIsSurface)
	{
		public MaterialIdentity ToIdentity() => new(
			MaterialId, TemplateName, BaseTexture, UseAlphaClip, MatcapTextureName, MatcapMean is not null);
	}

	/// <summary>
	/// 只解析材质身份（id + 模板 + 底色名），<b>不编码任何贴图</b>。
	/// <para>
	/// 用在"这个材质不属于本 patch、但 Unit 必须引用它的 id"的场合。id 只由
	/// <c>$basetexture</c> 与两个行为后缀决定，其中 <c>|alphaclip</c> 需要量贴图 alpha，
	/// 所以这里仍然要<b>解码</b>——但<b>不重新编码</b>，省掉的正是最贵的那一步。
	/// </para>
	/// </summary>
	public bool TryResolveIdentity(string materialName, out MaterialIdentity identity, out string failure)
	{
		var report = Resolve(materialName);
		identity = report.Identity!;
		failure = report.Reason;
		return report.Outcome == MaterialResolveOutcome.Resolved;
	}

	/// <summary>
	/// 只解析材质身份，并把"跳过"与"失败"分开报出来。
	/// <para>
	/// 调用方需要这个区分：<b>跳过</b>是源侧本来的意思（<c>$no_draw</c>），不是问题；
	/// <b>失败</b>会让 Unit 退回外壳护甲的材质，是真问题。
	/// </para>
	/// </summary>
	public MaterialResolveReport Resolve(string materialName)
	{
		var outcome = TryLoad(materialName, out var loaded, out var reason);
		return outcome switch
		{
			LoadOutcome.Loaded => new(MaterialResolveOutcome.Resolved, loaded.ToIdentity(), string.Empty),
			LoadOutcome.Skipped => new(MaterialResolveOutcome.Skipped, null, reason),
			_ => new(MaterialResolveOutcome.Failed, null, reason),
		};
	}

	/// <summary>把一个源材质转成 HD2 材质 + 它的贴图。</summary>
	/// <param name="materialName">源材质的裸名（MDL 材质表里那个）。</param>
	public MaterialSynthesisResult Convert(string materialName)
	{
		var outcome = TryLoad(materialName, out var loaded, out var failure);
		if (outcome == LoadOutcome.Skipped)
			return MaterialSynthesisResult.Skipped(failure);
		if (outcome == LoadOutcome.Failed)
			return MaterialSynthesisResult.Failed(failure);

		var stem = loaded.Stem;
		var material = templates.Load(loaded.TemplateName);
		var textureName = loaded.TextureName;

		SynthesizedTexture texture;
		SynthesizedTexture? opacityMask;
		try
		{
			(texture, opacityMask) = BuildBaseColorTexture(loaded);
		}
		catch (Exception exception) when (exception is InvalidDataException or IOException or InvalidOperationException)
		{
			return MaterialSynthesisResult.Failed($"{stem}：贴图 {textureName} 转换失败——{exception.Message}");
		}

		var textures = new List<SynthesizedTexture> { texture };
		if (loaded.UseAlphaClip)
		{
			if (opacityMask is null)
				return MaterialSynthesisResult.Failed($"{stem}：判定要切 alphaclip，但没有产出遮罩贴图");
			if (material.TextureIds.Count <= AlphaClipBaseColorSlot)
				return MaterialSynthesisResult.Failed(
					$"alphaclip 模板只有 {material.TextureIds.Count} 个槽，放不下下标 {AlphaClipBaseColorSlot} 的底色");

			// 槽 0 是 NormalXyAoRoughMap，语义与 advanced 的槽 2 相同（RGB 法线 / A 粗糙度）。
			// **必须填**：模板文件里这个槽是空的（SDK 的模板只带着色器契约，不带贴图 id），
			// 留空就是"没有法线图"。用与 advanced 那条路**同一张**全局共享平面法线——
			// 名字相同 → id 相同 → 装配器按 id 去重会并成一条，不会把包撑大。
			if (material.TextureIds.Count > AlphaClipNormalSlot)
			{
				var flatNormal = BuildSolidTexture("shared_flat_normal_", string.Empty, 128, 128, 255, PbrRoughness);
				material.SetTexture(AlphaClipNormalSlot, flatNormal.Id);
				textures.Add(flatNormal);
			}

			material.SetTexture(AlphaClipOpacitySlot, opacityMask.Id);
			material.SetTexture(AlphaClipBaseColorSlot, texture.Id);
			textures.Add(opacityMask);
		}
		else
		{
			if (material.TextureIds.Count <= AdvancedAlbedoSlot)
				return MaterialSynthesisResult.Failed(
					$"advanced 模板只有 {material.TextureIds.Count} 个槽，放不下下标 {AdvancedAlbedoSlot} 的底色");
			material.SetTexture(AdvancedAlbedoSlot, texture.Id);

			// 槽 2（Nar = RGB 法线 / A 粗糙度）与槽 3（金属度）也要写 ——
			// 用的是**全局共享**的一对（`fix_materials.py:25-27` 的 shared_flat_normal / shared_pbr），
			// 不是每材质各一张：普通件的法线与金属度本来就不随材质变，各写一张只会把包撑大。
			// 名字固定 → id 固定 → 装配器按 id 去重会并成一条 ✓
			//
			// ⚠️ 默认金属是**近黑 (6,6,6)**；191 属于 matcap 那条路（观感来自 matcap 时
			// 才需要把金属度顶上去）。两者语义不同，不能混。
			// matcap 承载表面时这里让位，由下面的 matcap 分支写自己的值。
			if (!loaded.MatcapIsSurface
				&& material.TextureIds.Count > AdvancedNarSlot
				&& material.TextureIds.Count > AdvancedMetallicSlot)
			{
				var flatNormal = BuildSolidTexture("shared_flat_normal_", string.Empty, 128, 128, 255, PbrRoughness);
				var sharedPbr = BuildSolidTexture("shared_pbr_", string.Empty, DefaultMetallic, DefaultMetallic, DefaultMetallic, 255);
				material.SetTexture(AdvancedNarSlot, flatNormal.Id);
				material.SetTexture(AdvancedMetallicSlot, sharedPbr.Id);
				textures.Add(flatNormal);
				textures.Add(sharedPbr);
			}

			// 观感来自 matcap 的金属件还要补两个标量槽，否则金属光泽仍然出不来：
			//   槽 3 = 金属度（纯色 191）；槽 2 = 平面法线 + alpha 粗糙度 89。
			// 这两个值是照参考模组实测定的（MaterialAppearance.MetallicValue / RoughnessValue）。
			//
			// ⚠️ 判据是 `MatcapIsSurface`（**VMT 说了算**），不是"有没有烘平均色"。
			// Albedo 模式不烘，但金属度/粗糙度照样要写——否则整片金属会变成哑光塑料。
			if (loaded.MatcapIsSurface)
			{
				var metallic = BuildSolidTexture(textureName, "_metallic", MaterialAppearance.MetallicValue, MaterialAppearance.MetallicValue, MaterialAppearance.MetallicValue, 255);
				var normalRoughness = BuildSolidTexture(textureName, "_nar", 128, 128, 255, MaterialAppearance.RoughnessValue);
				if (material.TextureIds.Count <= AdvancedMetallicSlot || material.TextureIds.Count <= AdvancedNarSlot)
					return MaterialSynthesisResult.Failed($"{stem}：advanced 模板槽不够，放不下金属/粗糙槽");
				material.SetTexture(AdvancedMetallicSlot, metallic.Id);
				material.SetTexture(AdvancedNarSlot, normalRoughness.Id);
				textures.Add(metallic);
				textures.Add(normalRoughness);
			}
		}

		return new MaterialSynthesisResult(
			new SynthesizedMaterial(loaded.MaterialId, material.Write(), loaded.TemplateName, loaded.BaseTexture, material.TextureIds),
			textures,
			[])
		{ Identity = loaded.ToIdentity() };
	}

	/// <summary>
	/// 把"名字 → 材质身份"这条链走完：读 VMT、读 VTF、解码、量 alpha 定模板、算 id。
	/// <para>
	/// <b>读 VMT/VTF 走的是 <c>source</c> 这个作用域视图</b>（自身优先、整批兜底）。
	/// 所以同一个材质在"它自己那个 VPK 的 patch"里读到的是自己的那份，
	/// 在别的 patch 里读到的是整批里最先出现的那个——但两者算出的 id 一定相同，
	/// 因为 id 只由名字决定。
	/// </para>
	/// </summary>
	/// <summary>加载的三个结果。**"跳过"与"失败"必须分开**——见 <see cref="MaterialResolveOutcome"/>。</summary>
	private enum LoadOutcome
	{
		Loaded = 0,
		Skipped = 1,
		Failed = 2,
	}

	private LoadOutcome TryLoad(string materialName, out LoadedMaterial loaded, out string reason)
	{
		loaded = default!;
		if (string.IsNullOrWhiteSpace(materialName))
		{
			reason = "材质名是空的";
			return LoadOutcome.Failed;
		}

		var stem = Path.GetFileNameWithoutExtension(VmtDocument.NormalizePath(materialName));
		if (!source.TryReadVmt(stem, out var vmtBytes))
		{
			reason = $"{stem}：材质来源里找不到 .vmt";
			return LoadOutcome.Failed;
		}

		var vmt = VmtDocument.Parse(vmtBytes);

		// `$no_draw 1`：源侧根本不绘制这个材质（L4D2 用它隐藏部位）。
		// HD2 没有对应概念，所以**不硬造**——把它当"跳过"报出去。
		// 硬造两种结果都是错的：当失败会让整个 patch 写不出来；当成功会造出一个看得见的材质。
		if (!string.IsNullOrWhiteSpace(vmt.Value("$no_draw")))
		{
			reason = $"{stem}：源材质声明 $no_draw（源侧不绘制），跳过";
			return LoadOutcome.Skipped;
		}

		var baseTexture = vmt.BaseTexture;
		if (string.IsNullOrWhiteSpace(baseTexture))
		{
			reason = $"{stem}：VMT 里没有 $basetexture";
			return LoadOutcome.Failed;
		}

		var textureName = Path.GetFileNameWithoutExtension(baseTexture);
		if (!source.TryReadVtf(textureName, out var vtfBytes))
		{
			reason = $"{stem}：贴图来源里找不到 {textureName}.vtf";
			return LoadOutcome.Failed;
		}

		// matcap 规则：源侧整片底色近黑、观感全来自 matcap（tint ≥ 0.9）。
		var verdict = MaterialAppearance.Evaluate(vmt);
		var matcapIsSurface = verdict.MatCapIsTheSurface;
		double[]? matcapMean = null;

		// matcap 贴图名要留到身份里：**它的内容可能来自别的 VPK**，而 id 只由名字决定。
		// 装配器据此报警——否则"换个 patch 就能换金属色"是个假承诺。
		var matcapTextureName = string.IsNullOrWhiteSpace(vmt.MatCapTexture)
			? null
			: Path.GetFileNameWithoutExtension(VmtDocument.NormalizePath(vmt.MatCapTexture));

		if (matcapIsSurface && matcapMode == MatCapMode.Albedo)
		{
			// matcap 当底色：真正决定观感的是 matcap 那张图，不是底色
			// （实测 A1 的 `Ornament_metal_Underwear.vtf` 是 1×1 占位图）。
			// 贴图 id 于是变成 `hash(matcap 名)` —— 它由**哪个 VPK 提供**就归哪个 patch，
			// C1/C2 这类"只换 matcap"的包成了真正可换的 patch。
			if (string.IsNullOrWhiteSpace(matcapTextureName))
			{
				reason = $"{stem}：判定 matcap 承载表面，但 VMT 里没有 $matcaptexture";
				return LoadOutcome.Failed;
			}

			if (!source.TryReadVtf(matcapTextureName, out vtfBytes))
			{
				reason = $"{stem}：matcap 贴图来源里找不到 {matcapTextureName}.vtf";
				return LoadOutcome.Failed;
			}

			textureName = matcapTextureName;
		}
		else if (matcapIsSurface && matcapMeans is not null)
		{
			// Bake 模式：拿平均色烘进底色。拿不到就**不烘**——烘一个错的值比不烘更糟。
			matcapMeans.TryGetValue(stem, out matcapMean);
		}

		// 发光声明。它既参与底色构造，又参与材质身份判定
		// （`$emissiontexture` 存在时身份要带 `|emission`，否则发光变体会被合并掉）。
		var emissionTexture = vmt.Value("$emissiontexture");
		var emissionTint = MaterialAppearance.ParseTint(vmt.Value("$emissiontint"));

		VtfFile vtf;
		byte[] rgba;
		bool useAlphaClip;
		try
		{
			vtf = VtfFile.Read(vtfBytes);
			// ⚠️ 量 alpha 必须在清零之前（清零发生在编码那一步，见 BuildBaseColorTexture）。
			rgba = encoder.DecodeToRgba(BuildSourceDds(vtf));
			// 模板选择：**VMT 开关 ∧ 贴图真有 alpha** 才切 alphaclip。
			// 只看开关会误判——装饰件常写 `$alphatest 1` 但贴图是 1×1 无 alpha（M7 就漏了 3 组）。
			//
			// ⚠️ **Albedo 模式下不切 alphaclip，理由有两条，都指向同一个结论：**
			//
			// 1. 语义上：源侧这些材质写着 `$alphatest 1`，但镂空信息在**底色的 alpha** 里，
			//    而它们的底色是 1×1 不透明占位图（实测 `Ornament_metal_Underwear.vtf` 91 字节）
			//    —— 也就是说源侧**实际什么都没裁掉**。matcap 是光泽图，不是遮罩。
			//    照抄"没裁"才是忠实的；拿 matcap 的 alpha 当遮罩会把金属件穿出洞。
			// 2. 不变量上：金 / 银两版的 `11.vtf` 内容不同。模板判定一旦依赖内容，
			//    两版就会得到不同的 TOC —— 而它们必须同号互换。
			useAlphaClip = matcapIsSurface && matcapMode == MatCapMode.Albedo
				? false
				: vmt.WantsTransparency && TextureAlpha.Measure(rgba).IsMeaningful;
		}
		catch (Exception exception) when (exception is InvalidDataException or IOException or InvalidOperationException)
		{
			reason = $"{stem}：贴图 {textureName} 读不出来——{exception.Message}";
			return LoadOutcome.Failed;
		}

		var templateName = useAlphaClip ? "alphaclip" : "advanced";
		if (!templates.Contains(templateName))
		{
			reason = $"材质模板库缺少 {templateName}，无法造材质";
			return LoadOutcome.Failed;
		}

		// 材质 id 用 $basetexture 的全路径（与 Python 侧一致；贴图 id 只用 basename）。
		// 这个不对称是已知隐患，见 Hd2AssetId 的注释。
		//
		// ⚠️ **但只按 $basetexture 定 id 会把"发光变体"与本体合并掉。**
		// 实测：`Shinano_face` 与 `Shinano_face_e` 共用同一张底色，
		// 差异只在 `$emissiontexture` / `$emissiontint`（第 35 轮确认）。
		// 两者指向同一个 id → 被去重合并 → **发光那层静默丢失**，不报错、不警告。
		//
		// 它们**本来就是两个不同的材质**（一个自发光、一个不发光），所以身份必须包含这一点。
		//
		// ⚠️ **模板也必须进身份，理由同上、后果更难看。**
		// NekoToon 这套模型里大量材质共用同一张"贴图表"底色，而蕾丝与非蕾丝是**两个材质**：
		// 实测 `lower_white01_underwear` 一张底色被 11 个材质用（7 个蕾丝 + 4 个普通），
		// `bra_white01_bra_inner` 被 7 个用。身份不含模板时它们撞成同一个 id，
		// 装配器去重"先到先得"——**每一处冲突都是 advanced 赢**，
		// 于是蕾丝的 alphaclip 模板被静默丢掉、镂空消失（用户报的"镂空材质忘了做"就是这个）。
		// 反过来如果 alphaclip 赢了，普通件会被蕾丝的遮罩**穿出洞**，同样错。
		var identity = BuildMaterialIdentity(
			baseTexture, emissionTexture, useAlphaClip,
			verdict.MatCapIsTheSurface ? vmt.MatCapTexture : null);

		loaded = new LoadedMaterial(
			stem, baseTexture, textureName, vtf, rgba, useAlphaClip, templateName,
			identity, Hd2AssetId.Material(identity), emissionTexture, emissionTint, matcapMean, verdict.Tint,
			matcapTextureName, matcapIsSurface);
		reason = string.Empty;
		return LoadOutcome.Loaded;
	}

	/// <summary>
	/// 材质身份串：<c>$basetexture</c> 全路径，外加所有<b>会改变材质行为</b>的后缀。
	/// <para>
	/// 后缀不是装饰，每一个都对应一次实测过的静默合并：
	/// </para>
	/// <list type="bullet">
	/// <item><c>|emission</c>：自发光变体与本体共用底色，不加就丢掉发光那层。</item>
	/// <item><c>|alphaclip</c>：镂空与非镂空共用底色，不加就丢掉镂空（或者反过来给普通件穿洞）。</item>
	/// <item><c>|matcap:&lt;贴图&gt;</c>：matcap 烘进底色时，<b>不同的 matcap 就是不同的底色</b>。
	/// 实测 A1 的 40 个金属装饰件与 <c>jewel</c> 共用同一张 <c>Ornament_metal_Underwear</c> 底色，
	/// 但前者指向 <c>11</c>（金/银）、后者指向 <c>6</c>（宝石）——不分开就会并成一个材质，
	/// 于是宝石和金属同色。</item>
	/// </list>
	/// <para>
	/// 顺序固定（emission → alphaclip → matcap），因为 id 是哈希，串变了 id 就变——顺序必须稳定可复现。
	/// </para>
	/// <para>
	/// ⚠️ <c>|matcap:</c> 只在<b>真的会烘</b>（<c>$matcaptint ≥ 0.9</c>）时加，而那个判定<b>只看 VMT</b>，
	/// 不看"队列里有没有模型能算平均色"。否则同一个 VMT 在不同队列里会算出不同的 id——
	/// 那会让 patch 之间再也对不上。
	/// </para>
	/// </summary>
	public static string BuildMaterialIdentity(
		string baseTexture, string? emissionTexture, bool useAlphaClip, string? matcapTexture = null)
	{
		var identity = string.IsNullOrWhiteSpace(emissionTexture) ? baseTexture : baseTexture + "|emission";
		if (useAlphaClip)
			identity += "|alphaclip";
		if (!string.IsNullOrWhiteSpace(matcapTexture))
			identity += "|matcap:" + matcapTexture;
		return identity;
	}

	/// <summary>
	/// 已解码的底色（+ 需要时一张 alpha 遮罩）→ HD2 <c>Texture</c>。
	/// <para>
	/// <b>入参是 <see cref="LoadedMaterial"/>，不是 VTF 字节</b>：解码已经在 <c>TryLoad</c> 里做过一次
	/// （量 alpha 定模板需要它），这里再解一次就是白烧一次 texconv。
	/// </para>
	/// <para>
	/// <b>alpha 必须在清零之前量。</b>清 alpha 是为了"底色不触发自发光"，但同一片 alpha
	/// 正是镂空信息的载体——顺序反了就只能看到一片全零的 alpha，永远切不到 alphaclip。
	/// 量的那一步在 <c>TryLoad</c>，所以这里的顺序天然是对的。
	/// </para>
	/// </summary>
	private (SynthesizedTexture BaseColor, SynthesizedTexture? OpacityMask) BuildBaseColorTexture(LoadedMaterial loaded)
	{
		var vtf = loaded.Vtf;
		var rgba = loaded.Rgba;
		var textureName = loaded.TextureName;

		var opacityMask = loaded.UseAlphaClip
			? BuildOpacityMask(textureName, rgba, vtf.Width, vtf.Height)
			: null;

		// 压暗必须在**清零 alpha 之前、编码 sRGB 之前**：
		// 前者是因为 alpha 的用途不同（镂空走遮罩贴图），后者是因为
		// 压暗要作用在 sRGB 编码的像素上（见 AlbedoAdjustment 的注释）。
		// matcap 烘焙也必须在清零 alpha 之前（它只改 RGB，但顺序上这里最自然）。
		if (loaded.MatcapMean is not null)
			MaterialAppearance.BakeMatCap(rgba, loaded.MatcapMean, loaded.MatcapTint);

		albedoAdjustment?.Apply(rgba, vtf.Width, vtf.Height);

		ZeroAlpha(rgba);

		// 发光：`advanced` 槽 5 的 **alpha 就是 emission**，而上面刚把它清零了。
		// 所以这里按声明把 `$emissiontexture` 的亮度写回去。
		//
		// ⚠️ 尺寸必须一致才逐像素写。不一致就**明确失败**，不重采样、更不静默取一角
		// ——静默取一角会产出一张"看起来有发光、位置全错"的图，比不支持更难查。
		if (!string.IsNullOrWhiteSpace(loaded.EmissionTexture))
		{
			var emissionStem = Path.GetFileNameWithoutExtension(VmtDocument.NormalizePath(loaded.EmissionTexture));
			if (!source.TryReadVtf(emissionStem, out var emissionBytes))
				throw new InvalidDataException($"声明了 $emissiontexture={loaded.EmissionTexture}，但材质来源里找不到它");

			var emissionVtf = VtfFile.Read(emissionBytes);
			var emissionRgba = encoder.DecodeToRgba(BuildSourceDds(emissionVtf));

			// ⚠️ **尺寸不一致是常态，不是例外。** 实测 `shinano_face_e` 是 1024×1024、
			// 底色是 2048×2048 —— 发光图常常只有底色的一半分辨率。
			// 所以这里按**最近邻**缩放到底色尺寸。
			//
			// 不能"取一角"（产出的发光位置全错，比不支持更难查），
			// 也不能直接失败（那会让整个材质造不出来，上面实测就撞到了这一条）。
			// 最近邻在放大时是块状，但发光图本身低频、且这里只取亮度，观感上够用；
			// 双线性会更好，代价是要处理边界权重——留待有实机对照之后再换。
			var tint = loaded.EmissionTint > 0 ? loaded.EmissionTint : 1;
			for (var y = 0; y < vtf.Height; y++)
			{
				var sourceY = Math.Min(emissionVtf.Height - 1, y * emissionVtf.Height / vtf.Height);
				for (var x = 0; x < vtf.Width; x++)
				{
					var sourceX = Math.Min(emissionVtf.Width - 1, x * emissionVtf.Width / vtf.Width);
					var at = ((sourceY * emissionVtf.Width) + sourceX) * 4;
					var luminance = (emissionRgba[at] * 0.299)
						+ (emissionRgba[at + 1] * 0.587)
						+ (emissionRgba[at + 2] * 0.114);
					rgba[(((y * vtf.Width) + x) * 4) + 3] =
						(byte)Math.Clamp(Math.Round(luminance * tint), 0, 255);
				}
			}
		}

		// DecodeToRgba 返回的是**裸像素**，编码前要重新包一层 DDS——texconv 只吃 DDS。
		var bc7 = encoder.EncodeToBc7(
			Hd2TextureAsset.BuildDds(vtf.Width, vtf.Height, Rgba8Unorm, [rgba]), srgb: true);

		var texture = new Hd2TextureAsset();
		texture.FromDds(bc7);
		return (
			new SynthesizedTexture(
				Hd2AssetId.Texture(textureName),
				texture.WriteToc(),
				texture.Pixels,
				textureName,
				texture.FormatName,
				texture.Width,
				texture.Height),
			opacityMask);
	}

	/// <summary>
	/// 把<b>一张单独的 VTF</b> 转成 HD2 底色贴图，<b>不涉及任何材质</b>。
	/// <para>
	/// 用在"这个 VPK 只带贴图、不带 VMT"的情形（Lunalice 的 C/D/E/F 四组就是纯换色贴图包）。
	/// 产出的 id 与材质那条路对同一张贴图算出来的<b>完全一致</b>（都只由 basename 派生），
	/// 所以材质 patch 引用的底色能被贴图 patch 满足。
	/// </para>
	/// <para>
	/// 走的是同一条编码路径（解 DDS → 抹 alpha → BC7 sRGB），所以两边像素也一致。
	/// 唯一刻意不做的是 matcap 烘焙与发光写入——那两件事是<b>材质</b>的语义，贴图包里没有材质。
	/// </para>
	/// </summary>
	public bool TryConvertTexture(string textureName, out SynthesizedTexture texture, out string failure)
	{
		texture = default!;
		var stem = Path.GetFileNameWithoutExtension(VmtDocument.NormalizePath(textureName));
		if (string.IsNullOrWhiteSpace(stem) || !source.TryReadVtf(stem, out var vtfBytes))
		{
			failure = $"{stem}：贴图来源里找不到 .vtf";
			return false;
		}

		try
		{
			var vtf = VtfFile.Read(vtfBytes);
			var rgba = encoder.DecodeToRgba(BuildSourceDds(vtf));
			ZeroAlpha(rgba);
			var bc7 = encoder.EncodeToBc7(
				Hd2TextureAsset.BuildDds(vtf.Width, vtf.Height, Rgba8Unorm, [rgba]), srgb: true);
			var asset = new Hd2TextureAsset();
			asset.FromDds(bc7);
			texture = new SynthesizedTexture(
				Hd2AssetId.Texture(stem), asset.WriteToc(), asset.Pixels, stem,
				asset.FormatName, asset.Width, asset.Height);
			failure = string.Empty;
			return true;
		}
		catch (Exception exception) when (exception is InvalidDataException or IOException or InvalidOperationException)
		{
			failure = $"{stem}：贴图转换失败——{exception.Message}";
			return false;
		}
	}

	/// <summary>
	/// 把源 alpha 变成 HD2 的 <c>OpacityClipMap</c>：<b>RGB = (a,a,a)、A 固定 255</b>，
	/// 格式 <c>BC7_UNORM</c>（线性，不是 sRGB）。
	/// <para>
	/// 极性是一致的：VTF 里白=可见、黑=透明，HD2 的遮罩也是白=可见，所以直接用、不取反
	/// （<c>fix_alphaclip.py:117-155</c>）。
	/// </para>
	/// </summary>
	private SynthesizedTexture BuildOpacityMask(string textureName, byte[] rgba, int width, int height)
	{
		// ⚠️ `rgba` 是 **texconv 解出来的完整 DDS**（128 字节头 + 像素），不是裸像素。
		// 当成裸像素用会产出一份"头写着 w×h、后面却跟着 128+w*h*4 字节"的 DDS，
		// texconv 读不出自洽的输入就**走兜底路径把输入原样退回**（退出码仍是 0），
		// 最终表现为"某材质转换失败，产物只有 132 字节"。
		// 第 23–26 轮追了四轮的那个现象，根因就在这里。
		var mask = new byte[checked(width * height * 4)];
		for (var offset = 0; offset < mask.Length; offset += 4)
		{
			var alpha = rgba[offset + 3];
			mask[offset + 0] = alpha;
			mask[offset + 1] = alpha;
			mask[offset + 2] = alpha;
			mask[offset + 3] = 255;
		}

		var dds = Hd2TextureAsset.BuildDds(width, height, Rgba8Unorm, [mask]);
		var bc7 = encoder.EncodeToBc7(dds, srgb: false);
		var texture = new Hd2TextureAsset();
		texture.FromDds(bc7);
		return new SynthesizedTexture(
			Hd2AssetId.Texture(textureName + "_opacity"),
			texture.WriteToc(),
			texture.Pixels,
			textureName + "_opacity",
			texture.FormatName,
			texture.Width,
			texture.Height);
	}

	/// <summary><c>R8G8B8A8_UNORM</c> 的 DXGI 号；非压缩源统一扩成它。</summary>
	private const uint Rgba8Unorm = 28;

	/// <summary>
	/// 非压缩 VTF 的 mip0 → <c>R8G8B8A8</c>。
	/// <para>
	/// <b>通道顺序必须按格式拆</b>：把 BGR888 当 RGB888 用会静默换掉红蓝
	/// （<c>hd2_texpack.py:118-149</c> 做同一件事）。这里只覆盖 8 位每通道的常见格式；
	/// 其余（调色板 <c>P8</c>、16 位、浮点）一律<b>抛</b>，不猜。
	/// </para>
	/// </summary>
	private static byte[] ExpandToRgba(VtfFile vtf)
	{
		var source = vtf.Mips[0];
		var pixels = checked(vtf.Width * vtf.Height);
		var output = new byte[checked(pixels * 4)];
		switch (vtf.HighResFormat)
		{
			case 0:    // RGBA8888 —— 原样
				Require(source, pixels * 4, vtf.FormatName);
				source.AsSpan(0, pixels * 4).CopyTo(output);
				break;
			case 1:    // ABGR8888 -> RGBA
				Require(source, pixels * 4, vtf.FormatName);
				Swizzle(source, output, pixels, 3, 2, 1, 0);
				break;
			case 11:   // ARGB8888 -> RGBA
				Require(source, pixels * 4, vtf.FormatName);
				Swizzle(source, output, pixels, 1, 2, 3, 0);
				break;
			case 12:   // BGRA8888 -> RGBA
			case 16:   // BGRX8888 -> RGBA（X 当 255）
				Require(source, pixels * 4, vtf.FormatName);
				Swizzle(source, output, pixels, 2, 1, 0, 3);
				if (vtf.HighResFormat == 16)
				{
					for (var offset = 3; offset < output.Length; offset += 4)
						output[offset] = 255;
				}

				break;
			case 2:    // RGB888 -> RGBA（alpha 补 255）
				Require(source, pixels * 3, vtf.FormatName);
				for (var i = 0; i < pixels; i++)
				{
					output[(i * 4) + 0] = source[(i * 3) + 0];
					output[(i * 4) + 1] = source[(i * 3) + 1];
					output[(i * 4) + 2] = source[(i * 3) + 2];
					output[(i * 4) + 3] = 255;
				}

				break;
			case 3:    // BGR888 -> RGBA
				Require(source, pixels * 3, vtf.FormatName);
				for (var i = 0; i < pixels; i++)
				{
					output[(i * 4) + 0] = source[(i * 3) + 2];
					output[(i * 4) + 1] = source[(i * 3) + 1];
					output[(i * 4) + 2] = source[(i * 3) + 0];
					output[(i * 4) + 3] = 255;
				}

				break;
			case 8:    // A8 -> 灰度 + alpha
				Require(source, pixels, vtf.FormatName);
				for (var i = 0; i < pixels; i++)
				{
					output[(i * 4) + 0] = source[i];
					output[(i * 4) + 1] = source[i];
					output[(i * 4) + 2] = source[i];
					output[(i * 4) + 3] = source[i];
				}

				break;
			case 6:    // IA88 -> 灰度 + alpha
				Require(source, pixels * 2, vtf.FormatName);
				for (var i = 0; i < pixels; i++)
				{
					output[(i * 4) + 0] = source[i * 2];
					output[(i * 4) + 1] = source[i * 2];
					output[(i * 4) + 2] = source[i * 2];
					output[(i * 4) + 3] = source[(i * 2) + 1];
				}

				break;
			default:
				throw new InvalidDataException(
					$"VTF 格式 {vtf.FormatName} 既不是 BC 压缩、也不在已实现的非压缩拆包列表里");
		}

		return output;
	}

	private static void Require(byte[] source, int expected, string formatName)
	{
		if (source.Length < expected)
			throw new InvalidDataException(
				$"VTF 格式 {formatName} 的 mip0 只有 {source.Length} 字节，至少要有 {expected}");
	}

	private static void Swizzle(byte[] source, byte[] output, int pixels, int r, int g, int b, int a)
	{
		for (var i = 0; i < pixels; i++)
		{
			var at = i * 4;
			output[at + 0] = source[at + r];
			output[at + 1] = source[at + g];
			output[at + 2] = source[at + b];
			output[at + 3] = source[at + a];
		}
	}

	/// <summary>
	/// 造一张 4×4 纯色贴图（金属度、粗糙度这类标量槽用它）。
	/// <para>
	/// 4×4 是能表达一个完整 BC 块的最小尺寸，而 <b>1×1 也占满一个块</b>——两者字节数相同，
	/// 但 4×4 让 texconv 少一次边界处理。
	/// </para>
	/// <para>线性格式（<c>BC7_UNORM</c>）：这些是 PBR 输入量，不是颜色，绝不能走 sRGB。</para>
	/// </summary>
	private SynthesizedTexture BuildSolidTexture(string owner, string suffix, byte red, byte green, byte blue, byte alpha)
	{
		const int size = 4;
		var dds = Hd2TextureAsset.BuildDds(size, size, Rgba8Unorm, [MaterialAppearance.SolidRgba(size, red, green, blue, alpha)]);
		var bc7 = encoder.EncodeToBc7(dds, srgb: false);
		var texture = new Hd2TextureAsset();
		texture.FromDds(bc7);
		return new SynthesizedTexture(
			Hd2AssetId.Texture(owner + suffix),
			texture.WriteToc(),
			texture.Pixels,
			suffix,
			texture.FormatName,
			texture.Width,
			texture.Height);
	}

	/// <summary>
	/// 把 <b>裸像素</b>的 alpha 通道全部清零（每像素 4 字节，第 4 个是 A）。
	/// <para>不清零的后果是整件护甲自发光，所以这里不提供"跳过"的开关。</para>
	/// </summary>
	private static void ZeroAlpha(byte[] rgba)
	{
		if (rgba.Length == 0 || rgba.Length % 4 != 0)
			throw new InvalidDataException($"RGBA 像素区长度 {rgba.Length} 不是 4 的倍数。");
		for (var offset = 3; offset < rgba.Length; offset += 4)
			rgba[offset] = 0;
	}
}
