using System.Buffers.Binary;
using HD2SourceModel;

namespace HD2SourceMigration.VpkConversion;

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

public sealed record MaterialSynthesisResult(
	SynthesizedMaterial? Material,
	IReadOnlyList<SynthesizedTexture> Textures,
	IReadOnlyList<string> Diagnostics)
{
	public bool IsValid => Material is not null && Diagnostics.Count == 0;

	public static MaterialSynthesisResult Failed(string reason) => new(null, [], [reason]);
}

/// <summary>
/// 源材质（VMT + VTF）→ HD2 <c>Material</c> + <c>Texture</c>。
/// <para>
/// 这是"每个 Unit 引用自己的材质"这条要求的<b>素材来源</b>：转换器产出的
/// <see cref="SynthesizedMaterial.Id"/> 就是塞进 <c>CanonicalMaterialSectionProvenance.MaterialId</c>
/// 的那个值——在那之前它一直是外壳护甲的材质。
/// </para>
/// <para>
/// <b>本轮只做底色。</b>模板固定 <c>advanced</c>，只填槽 5（<c>AlbedoEmissive</c>）。
/// 法线/金属/粗糙/alpha 遮罩与 alphaclip 模板切换都还没做——
/// 这些是"外观修正"那一步的事，且都必须有对应贴图才谈得上。
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
		IReadOnlyDictionary<string, double[]>? matcapMeans = null)
	{
		this.source = source;
		this.encoder = encoder;
		this.templates = templates;
		this.albedoAdjustment = albedoAdjustment;
		this.matcapMeans = matcapMeans;
	}

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

	/// <summary>把一个源材质转成 HD2 材质 + 它的贴图。</summary>
	/// <param name="materialName">源材质的裸名（MDL 材质表里那个）。</param>
	public MaterialSynthesisResult Convert(string materialName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(materialName);

		var stem = Path.GetFileNameWithoutExtension(VmtDocument.NormalizePath(materialName));
		if (!source.TryReadVmt(stem, out var vmtBytes))
			return MaterialSynthesisResult.Failed($"{stem}：材质来源里找不到 .vmt");
		var vmt = VmtDocument.Parse(vmtBytes);
		var baseTexture = vmt.BaseTexture;
		if (string.IsNullOrWhiteSpace(baseTexture))
			return MaterialSynthesisResult.Failed($"{stem}：VMT 里没有 $basetexture");

		var textureName = Path.GetFileNameWithoutExtension(baseTexture);
		if (!source.TryReadVtf(textureName, out var vtfBytes))
			return MaterialSynthesisResult.Failed($"{stem}：贴图来源里找不到 {textureName}.vtf");

		// matcap 规则：源侧整片底色近黑、观感全来自 matcap（tint ≥ 0.9），要烘进底色。
		// 拿不到平均色就**不烘**——烘一个错的值比不烘更糟。
		// 声明在 try **外面**：下面补金属/粗糙槽时还要用这两个值。
		var verdict = MaterialAppearance.Evaluate(vmt);
		double[]? matcapMean = null;
		if (verdict.MatCapIsTheSurface && matcapMeans is not null)
			matcapMeans.TryGetValue(stem, out matcapMean);

		// 发光声明。**必须在 try 之前读**：它既参与下面的底色构造，又参与材质身份判定
		// （第 36 轮：`$emissiontexture` 存在时身份要带 `|emission`，否则发光变体会被合并掉）。
		// 上一轮我把这段写进了 `BuildBaseColorTexture`，那里看不到 `stem`/`vmt` —— 作用域错了。
		var emissionTexture = vmt.Value("$emissiontexture");
		var emissionTint = MaterialAppearance.ParseTint(vmt.Value("$emissiontint"));

		SynthesizedTexture texture;
		SynthesizedTexture? opacityMask = null;
		bool useAlphaClip;
		try
		{
			var built = BuildBaseColorTexture(
				textureName, vtfBytes, wantAlphaClip: vmt.WantsTransparency,
				matcapMean, verdict.Tint, emissionTexture, emissionTint);
			texture = built.BaseColor;
			opacityMask = built.OpacityMask;
			useAlphaClip = built.UseAlphaClip;
		}
		catch (Exception exception) when (exception is InvalidDataException or IOException or InvalidOperationException)
		{
			return MaterialSynthesisResult.Failed($"{stem}：贴图 {textureName} 转换失败——{exception.Message}");
		}

		// 模板选择：**VMT 开关 ∧ 贴图真有 alpha** 才切 alphaclip。
		// 只看开关会误判——装饰件常写 `$alphatest 1` 但贴图是 1×1 无 alpha（M7 就漏了 3 组）。
		var templateName = useAlphaClip ? "alphaclip" : "advanced";
		if (!templates.Contains(templateName))
			return MaterialSynthesisResult.Failed($"材质模板库缺少 {templateName}，无法造材质");

		var material = templates.Load(templateName);
		var textures = new List<SynthesizedTexture> { texture };
		if (useAlphaClip)
		{
			if (opacityMask is null)
				return MaterialSynthesisResult.Failed($"{stem}：判定要切 alphaclip，但没有产出遮罩贴图");
			if (material.TextureIds.Count <= AlphaClipBaseColorSlot)
				return MaterialSynthesisResult.Failed(
					$"alphaclip 模板只有 {material.TextureIds.Count} 个槽，放不下下标 {AlphaClipBaseColorSlot} 的底色");
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
			// ⚠️ 默认金属是**近黑 (6,6,6)**；191 属于 matcap 那条路（金属光泽烘进底色之后
			// 才需要把金属度顶上去）。两者语义不同，不能混。
			// 烘了 matcap 时这里让位，由下面的 matcap 分支写自己的值。
			if (matcapMean is null
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

			// 烘了 matcap 的金属件还要补两个标量槽，否则金属光泽仍然出不来：
			//   槽 3 = 金属度（纯色 191）；槽 2 = 平面法线 + alpha 粗糙度 89。
			// 这两个值是照参考模组实测定的（MaterialAppearance.MetallicValue / RoughnessValue）。
			if (matcapMean is not null)
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

		// 材质 id 用 $basetexture 的全路径（与 Python 侧一致；贴图 id 只用 basename）。
		// 这个不对称是已知隐患，见 Hd2AssetId 的注释。
		//
		// ⚠️ **但只按 $basetexture 定 id 会把"发光变体"与本体合并掉。**
		// 实测：`Shinano_face` 与 `Shinano_face_e` 共用同一张底色，
		// 差异只在 `$emissiontexture` / `$emissiontint`（第 35 轮确认）。
		// 两者指向同一个 id → 被去重合并 → **发光那层静默丢失**，不报错、不警告。
		//
		// 它们**本来就是两个不同的材质**（一个自发光、一个不发光），所以身份必须包含这一点。
		// 加后缀让 id 分开：合并消失，两个材质各自存活。
		// 这也让"加发光"变成纯增量 —— 不必再和合并逻辑纠缠。
		// （`emissionTexture` 已在上面声明——那里靠近 matcap 判定，所以两边都看得到。）
		var identity = string.IsNullOrWhiteSpace(emissionTexture)
			? baseTexture
			: baseTexture + "|emission";
		var materialId = Hd2AssetId.Material(identity);

		return new MaterialSynthesisResult(
			new SynthesizedMaterial(materialId, material.Write(), templateName, baseTexture, material.TextureIds),
			textures,
			[]);
	}

	/// <summary>底色 + （可选）alpha 遮罩的产出。</summary>
	private sealed record BaseColorBuild(SynthesizedTexture BaseColor, SynthesizedTexture? OpacityMask, bool UseAlphaClip);

	/// <summary>
	/// VTF 的 mip0 → 底色贴图（+ 需要时一张 alpha 遮罩）→ HD2 <c>Texture</c>。
	/// <para>
	/// <b>alpha 必须在清零之前量。</b>清 alpha 是为了"底色不触发自发光"，但同一片 alpha
	/// 正是镂空信息的载体——顺序反了就只能看到一片全零的 alpha，永远切不到 alphaclip。
	/// </para>
	/// </summary>
	/// <param name="wantAlphaClip">VMT 是否声明要透明（<c>$translucent</c>/<c>$alphatest</c>）。</param>
	private BaseColorBuild BuildBaseColorTexture(
		string textureName, byte[] vtfBytes, bool wantAlphaClip, double[]? matcapMean, double matcapTint,
		string? emissionTexture, double emissionTint)
	{
		var vtf = VtfFile.Read(vtfBytes);

		// 把源 mip0 包成一份带 DX10 头的 DDS——texconv 只吃 DDS。
		// 压缩格式直接搬运；非压缩格式要先扩成 R8G8B8A8_UNORM（texconv 不认 RGB888 这种
		// 没有 DXGI 对应的布局），否则整条路会卡在"这张贴图是未压缩的"。
		var sourceDds = BuildSourceDds(vtf);

		// 两步：解成未压缩 RGBA → 量 alpha → （按需做遮罩）→ 抹掉 alpha → 编回 BC7(sRGB)。
		var rgba = encoder.DecodeToRgba(sourceDds);

		// ⚠️ 量 alpha 必须在清零之前。
		var stats = TextureAlpha.Measure(rgba);
		var useAlphaClip = wantAlphaClip && stats.IsMeaningful;
		var opacityMask = useAlphaClip ? BuildOpacityMask(textureName, rgba, vtf.Width, vtf.Height) : null;

		// 压暗必须在**清零 alpha 之前、编码 sRGB 之前**：
		// 前者是因为 alpha 的用途不同（镂空走遮罩贴图），后者是因为
		// 压暗要作用在 sRGB 编码的像素上（见 AlbedoAdjustment 的注释）。
		// matcap 烘焙也必须在清零 alpha 之前（它只改 RGB，但顺序上这里最自然）。
		if (matcapMean is not null)
			MaterialAppearance.BakeMatCap(rgba, matcapMean, matcapTint);

		albedoAdjustment?.Apply(rgba, vtf.Width, vtf.Height);

		ZeroAlpha(rgba);

		// 发光：`advanced` 槽 5 的 **alpha 就是 emission**，而上面刚把它清零了。
		// 所以这里按声明把 `$emissiontexture` 的亮度写回去。
		//
		// ⚠️ 尺寸必须一致才逐像素写。不一致就**明确失败**，不重采样、更不静默取一角
		// ——静默取一角会产出一张"看起来有发光、位置全错"的图，比不支持更难查。
		if (!string.IsNullOrWhiteSpace(emissionTexture))
		{
			var emissionStem = Path.GetFileNameWithoutExtension(VmtDocument.NormalizePath(emissionTexture));
			if (!source.TryReadVtf(emissionStem, out var emissionBytes))
				throw new InvalidDataException($"声明了 $emissiontexture={emissionTexture}，但材质来源里找不到它");

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
			var tint = emissionTint > 0 ? emissionTint : 1;
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

		// ⚠️ 发光尚未实现。`advanced` 槽 5 的 **alpha 就是 emission**，所以实现方式很清楚：
		// 按 `$emissiontexture` 把亮度写回这个 alpha。**但两张贴图的尺寸不一定相同**，
		// 不同就得重采样 —— 那是我还没做、也没验证过的部分。
		//
		// 第 36 轮已经把"发光变体"做成了独立材质（身份含 `|emission`），所以加发光是**纯增量**：
		// 只要在这里对 `Convert` 里那个 emissionTexture 非空的材质写 alpha 即可，
		// 不必再动合并逻辑——把它作为参数传进来的那一步尚未完成。
		// DecodeToRgba 返回的是**裸像素**，编码前要重新包一层 DDS——texconv 只吃 DDS。
		var bc7 = encoder.EncodeToBc7(
			Hd2TextureAsset.BuildDds(vtf.Width, vtf.Height, Rgba8Unorm, [rgba]), srgb: true);

		var texture = new Hd2TextureAsset();
		texture.FromDds(bc7);
		return new BaseColorBuild(
			new SynthesizedTexture(
				Hd2AssetId.Texture(textureName),
				texture.WriteToc(),
				texture.Pixels,
				textureName,
				texture.FormatName,
				texture.Width,
				texture.Height),
			opacityMask,
			useAlphaClip);
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
