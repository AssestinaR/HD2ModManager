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

	/// <summary>
	/// 把 matcap <b>归一成金属的 F0</b>，而不是原样当底色 —— 这是唯一的物理上成立的那条。
	/// <para>
	/// <b>为什么 <see cref="Albedo"/> 不够。</b>在 PBR 里，金属表面的底色<b>就是</b>它的
	/// 镜面反射率 F0（银 0.97 / 铝 0.91 / 铁 0.56，线性）。实测 serenade 的
	/// <c>matcap_metal7</c> 平均亮度只有 sRGB 106（线性 F0 ≈ 0.14）—— 一块"反射率 14% 的金属"，
	/// 于是又黑又没有反光。
	/// </para>
	/// <para>
	/// <b>为什么不能靠乘增益救。</b>这张图是双峰的：中位数 66、p90 231。
	/// 乘 1.6 就有 32% 的像素被压到 255，而平均亮度只从 105 涨到 138（F0 0.25）；
	/// 乘到 5.0 才到 F0 0.66，那时 64% 的像素已经是纯白，图案全毁。
	/// 换算关系不是线性的，因为高光那一半先撞顶。
	/// </para>
	/// <para>
	/// <b>这个档做什么。</b>丢掉 matcap 的亮度图案，只保留它的<b>色相</b>，
	/// 把整片抬到目标 F0。金属的反光应该由 PBR 的环境反射算出来，而不是由一张烘死的图假装。
	/// 这是一个刻意"信息损失"的近似：源侧的 matcap 本来就是<b>叠加在受光表面上的高光图</b>，
	/// 而不是表面颜色本身。
	/// </para>
	/// </summary>
	Metal = 2,
}

/// <summary>
/// 源侧要镂空（<c>$alphatest</c>/<c>$translucent</c> 且 alpha 真在表达形状）时，
/// 这个材质在 HD2 里怎么落地。
/// <para>
/// <b>为什么需要三档而不是一档。</b>实测扫遍 <c>HD2ModManager\mods</c> 的 128 个 <c>advanced</c>
/// 与 76 个 <c>alphaclip</c> 材质之后发现：<c>alphaclip</c> 的 76 次里 <b>60 次来自"隐藏物品/
/// 隐藏 NPC"这类模组</b>——那些模组的全部目的就是让东西消失。也就是说 <c>alphaclip</c> 在 HD2
/// 里实际上是"隐形着色器"，而它只有 3 个槽、<b>没有任何 PBR 输入</b>（没有 <c>Nar</c> 法线/粗糙度、
/// 没有 <c>IlluminateData</c> 金属度、没有 LUT），切过去就等于把材质降级成最小着色器。
/// </para>
/// <para>
/// 三档对应三种假设，需要实机对照才能定：
/// </para>
/// </summary>
public enum CutoutStrategy
{
	/// <summary>
	/// <b>A（历史行为）</b>：切到 <c>alphaclip</c> 模板，<c>OpacityThreshold</c> 留模板默认
	/// <b>0.5</b>。
	/// <para>
	/// 已知现象：镂空件在军械库里**隐形**、并且被环境着色器污染。0.5 这个阈值本身也可疑——
	/// 源侧蕾丝的 alpha 大量落在 0.2～0.4 的半透明过渡区，一刀切到 0.5 会把整片裁光。
	/// </para>
	/// </summary>
	AlphaClip = 0,

	/// <summary>
	/// <b>B</b>：仍用 <c>alphaclip</c>，只把 <c>OpacityThreshold</c> 降到 <b>0.144</b>。
	/// <para>
	/// 0.144 不是猜的：它是 SDK <c>alphaclip+</c> 模板的默认值，而 <c>HD2ModManager\mods</c> 里
	/// 唯一一个拿 <c>alphaclip+</c> 做衣服的模组（<c>VRchat_Milltina 可脱穿 材质球-衣服-*</c>）
	/// 原样保留了它。B 与 A 只差这一个变量，所以能干净地回答"是不是阈值切太狠"。
	/// </para>
	/// </summary>
	AlphaClipTuned = 1,

	/// <summary>
	/// 遮罩<b>取反</b>：白=透明、黑=可见。
	/// <para>
	/// 用来实测 <c>OpacityClipMap</c> 的极性。我们的遮罩现在是「源侧 alpha 直接搬过来」
	/// （白=可见），而 A≈B 说明阈值不是杠杆——那么下一个嫌疑就是极性反了：
	/// 极性反了会让"几乎不透明"的件被整片裁掉，现象正是"镂空件隐形"。
	/// </para>
	/// </summary>
	MaskInverted = 2,

	/// <summary>
	/// 遮罩放在 <b>alpha 通道</b>（RGB 恒 255），而不是 RGB。
	/// <para>
	/// 实测 HD2 的贴图文档明确写着"Alpha 是整体透明度，0 全透明、255 全不透明"
	/// （HD2 Modding Wiki / Textures），所以着色器很可能采样的是 <c>.a</c>。
	/// 我们一直把 alpha 写成 255，如果它读的是 alpha，那遮罩就<b>完全没生效</b>。
	/// </para>
	/// </summary>
	MaskInAlpha = 3,

	/// <summary>遮罩放 alpha 通道，并取反。</summary>
	MaskInAlphaInverted = 4,
}

/// <summary>
/// <b>已证伪的假设，留档以免重走：把遮罩塞进 <c>advanced</c> 的 <c>SclarFieldOpacity</c>。</b>
/// <para>
/// 曾经假设「<c>advanced</c> 靠 <c>SclarFieldOpacity</c> + <c>OpacitySharpness/Offset</c>
/// 做硬边镂空」，实机 A/B/C 三档对照的结果是<b>完全没效果</b>——那些件仍然是不透明的。
/// </para>
/// <para>
/// 原因后来查清了：现役模组里这个槽装的是 <b>1×1 <c>R8_UNORM</c> 标量</b>
/// （实测 Crimson Kitty 的两张 <c>0xc0b94ae8509a1bd4</c> / <c>0x812b33c5a3ebdb86</c>
/// 都是 1×1 R8）。也就是说 <c>SclarFieldOpacity</c> 是<b>整体不透明度标量</b>，
/// 不是逐像素遮罩——它根本没有能力做镂空。
/// </para>
/// </summary>
internal static class RejectedCutoutStrategies
{
	/// <summary>这一档已删除。相关槽位常量见 <c>SourceMaterialConverter.AdvancedOpacitySlot</c>。</summary>
	internal const string AdvancedScalarOpacity = "SclarFieldOpacity 是 1×1 R8 标量，做不了镂空。";
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

	/// <summary>
	/// <b>降级提示</b>：材质造出来了，但有一样东西无法忠实表达。
	/// <para>
	/// 与 <see cref="Diagnostics"/> 分开是刻意的：诊断是"这件事做不成"，必须中止；
	/// 而"<c>$translucent</c> 的半透明 HD2 做不到、我们退成了不透明"是
	/// <b>做成了、但不是原样</b>——它该出现在报告的警告里，让人一眼看到，
	/// 而不该把整个材质判失败（那会让整片部件消失，比不忠实更糟）。
	/// </para>
	/// </summary>
	public IReadOnlyList<string> Notices { get; init; } = [];

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
	/// <c>advanced</c> 槽 1 = <c>SclarFieldOpacity</c>（0x479fb1ef）。
	/// <para>
	/// 这是 <c>advanced</c> 唯一的不透明度来源。SDK 模板里它指向一张占位贴图，
	/// 所有现成模组都原样留着——<see cref="CutoutStrategy.AdvancedOpacity"/> 才把它当遮罩用。
	/// </para>
	/// </summary>
	public const int AdvancedOpacitySlot = 1;

	/// <summary><c>OpacityThreshold</c>（0x529a4aaf）：<c>alphaclip</c> 族的镂空阈值。</summary>
	public const uint OpacityThresholdVariableId = 0x529a4aaf;

	/// <summary><c>OpacitySharpness</c>（0xcb8c5728）：<c>advanced</c> 的不透明度过渡陡度。</summary>
	public const uint OpacitySharpnessVariableId = 0xcb8c5728;

	/// <summary><c>OpacityOffset</c>（0xccddd706）：<c>advanced</c> 的不透明度阈值偏移。</summary>
	public const uint OpacityOffsetVariableId = 0xccddd706;

	/// <summary>
	/// <see cref="CutoutStrategy.AlphaClipTuned"/> 用的阈值 = SDK <c>alphaclip+</c> 模板的默认值。
	/// <para>来源：<c>tools/ref/HD2SDK-CommunityEdition/materials/alphaclip+.material</c> 的
	/// <c>OpacityThreshold</c> 实测 0.144（<c>alphaclip</c> 是 0.5）。</para>
	/// </summary>
	public const float TunedOpacityThreshold = 0.144f;

	/// <summary>
	/// <see cref="MatCapMode.Metal"/> 归一到的目标反射率（<b>线性</b>）。
	/// <para>
	/// <b>0.96 是实机 A/B/C/D 四档对照选出来的</b>：银（0.97）这一档观感最好，
	/// 铝（0.90）偏灰。源侧这些"金属件"本来就以亮银为主。
	/// 要更暗或更亮用 <c>--matcap-gain</c> 微调（它是这个值的乘数）。
	/// </para>
	/// <para>
	/// 对照实测：原来的做法（matcap 原样当底色）等效 F0 ≈ <b>0.14</b>，
	/// 所以用户看到的"金属又黑又不反光"是反射率问题，不是亮度问题。
	/// </para>
	/// </summary>
	public const double MetalF0 = 0.96;

	/// <summary>
	/// <see cref="CutoutStrategy.AdvancedOpacity"/> 的陡度。抄自游戏本体角色材质
	/// （父 id <c>0x8f669f365f24594e</c>）的 <c>OpacitySharpness</c>。
	/// </summary>
	public const float CutoutOpacitySharpness = 64f;

	/// <summary>
	/// <see cref="CutoutStrategy.AdvancedOpacity"/> 的阈值偏移。抄自同一个材质：
	/// <c>saturate((mask - 0.5) × 64)</c> 才是硬边。
	/// </summary>
	public const float CutoutOpacityOffset = -0.5f;

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

	/// <summary>
	/// 纯色贴图的编码结果，按"名字 + 颜色"缓存。
	/// <para>
	/// <b>为什么必须有。</b>纯色槽（共享平面法线 / 共享 PBR / 金属度 / 法线粗糙度）是<b>每个材质各造一遍</b>的，
	/// 而它们的输入只有"名字 + 四个字节"。实测 4K 材质包 248 次 texconv 调用里有 <b>124 次</b>在重复编同一张
	/// 4×4 纯色图，合计 <b>27 秒</b>——这些产物按 id 去重后最终只写进包一份，重复的那 123 次纯属白烧。
	/// </para>
	/// <para>
	/// 键里带上颜色是必须的：同名不同色在源码里是合法的（将来若按材质调金属度就会撞上），
	/// 缓存成"只按名字"会静默把别人的颜色发给它。
	/// </para>
	/// <para>
	/// <b>并发安全</b>：值是不可变的 record + 只读字节数组，下游只读不写，所以多个材质共用一份产物是安全的。
	/// </para>
	/// </summary>
	private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Name, byte Red, byte Green, byte Blue, byte Alpha), SynthesizedTexture> solidTextures = new();

	public SourceMaterialConverter(
		SourceMaterialSource source,
		Bc7Encoder encoder,
		Hd2MaterialTemplateLibrary templates,
		AlbedoAdjustment? albedoAdjustment = null,
		IReadOnlyDictionary<string, double[]>? matcapMeans = null,
		MatCapMode matcapMode = MatCapMode.Bake,
		string? idNamespace = null,
		CutoutStrategy cutoutStrategy = CutoutStrategy.AlphaClip,
		double matcapBakeGain = 1.0)
	{
		this.source = source;
		this.encoder = encoder;
		this.templates = templates;
		this.albedoAdjustment = albedoAdjustment;
		this.matcapMeans = matcapMeans;
		this.matcapMode = matcapMode;
		this.idNamespace = string.IsNullOrWhiteSpace(idNamespace) ? null : idNamespace.Trim();
		this.cutoutStrategy = cutoutStrategy;
		this.matcapBakeGain = double.IsFinite(matcapBakeGain) && matcapBakeGain > 0 ? matcapBakeGain : 1.0;
	}

	/// <summary>
	/// matcap 烘进底色时的<b>亮度增益</b>（默认 1.0 = 不加）。
	/// <para>
	/// <b>为什么需要它。</b>matcap 是<b>加法的高光图</b>：它的面积加权平均色远低于它最亮的那片，
	/// 所以"把平均色烘进底色"得到的是偏暗的一层。而 HD2 那边我们同时把金属度顶到 ~1，
	/// 于是结果就是"黑金属 + 微弱高光"——实机反馈的"金属几乎没有反光、太黑了"。
	/// </para>
	/// <para>
	/// 物理上更对的近似是：<b>底色取金属本身的反射色（偏亮），高光交给 PBR 算</b>。
	/// 增益就是往这个方向调的一个旋钮；调到多少要看实机，所以做成参数而不是常量。
	/// </para>
	/// </summary>
	public double MatcapBakeGain => matcapBakeGain;

	private readonly double matcapBakeGain;

	/// <summary>
	/// 镂空材质怎么落地。见 <see cref="CutoutStrategy"/>。
	/// <para><b>默认 = <see cref="CutoutStrategy.AlphaClip"/>，即历史行为逐位不变</b>。</para>
	/// </summary>
	public CutoutStrategy CutoutStrategy => cutoutStrategy;

	private readonly CutoutStrategy cutoutStrategy;

	/// <summary>这个模板名是不是 <c>alphaclip</c> 族（槽表只有 3～4 个、没有 PBR 输入）。</summary>
	private static bool IsAlphaClipTemplate(string templateName)
		=> templateName.StartsWith("alphaclip", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// 这个 VMT 是不是<b>没有真底色</b>（<c>$basetexture</c> 缺失，或写着占位值
	/// <c>"basetexture"</c> / <c>"base"</c>）。
	/// <para>
	/// <b>这是源侧的合法写法，不是坏材质。</b>NekoToon 这套着色器里，金属 / 宝石件由作者
	/// 主动把 <c>$basetexture</c> 注释掉，观感 100% 来自 <c>$matcapture</c>
	/// （实测 serenade 有 20 个、mourningveil 有 4 个这样的材质）。
	/// 源侧渲染的其实就是「底色 = 0 + matcap × tint」。
	/// </para>
	/// <para>
	/// 以前这里直接判失败，后果是<b>整个金属件退回外壳护甲的材质</b>——用户报的
	/// 「金属材质丢失」就是它。正确的做法是合成一张纯黑底色，让 matcap 顶上去。
	/// </para>
	/// </summary>
	public static bool HasNoRealBaseTexture(VmtDocument vmt)
	{
		ArgumentNullException.ThrowIfNull(vmt);
		var baseTexture = vmt.BaseTexture;
		return string.IsNullOrWhiteSpace(baseTexture) || baseTexture is "basetexture" or "base";
	}

	/// <summary>
	/// 没有真底色时合成出来的底色在<b>身份串</b>里的名字。
	/// <para>
	/// 必须按材质名区分：同一批里 20 个金属件都没有底色，但它们的 matcap 各不相同，
	/// 用同一个合成名会把它们并成一个材质。带上 stem 之后，
	/// 再加上后面必有的 <c>|matcap:&lt;贴图&gt;</c>，身份就唯一了。
	/// </para>
	/// </summary>
	private static string SyntheticBaseName(string stem) => "synthetic-base:" + stem;

	/// <summary>
	/// 材质/贴图 id 的命名空间（见 <see cref="Hd2AssetId.NamespaceFor"/>）。
	/// <para>
	/// <b>为什么在转换器上而不是在 id 派生处按需传。</b>这个转换器一次只服务一个 VPK，
	/// 命名空间在整个生命周期里是常量；做成字段，五个派生点（材质 id、底色、遮罩、发光、
	/// 纯色标量图）就不可能漏掉一个——漏掉的那张会退回"全 Mod 同名共享"，正是要修的病。
	/// </para>
	/// <para><c>null</c> = 旧行为（与历史产物逐位相同）。</para>
	/// </summary>
	public string? IdNamespace => idNamespace;

	/// <summary>
	/// 材质 id：身份串 + 本转换器的命名空间。
	/// <para>
	/// 拆成方法而不是让五处各自调 <see cref="Hd2AssetId"/>：这五个派生点里<b>漏掉一个</b>，
	/// 漏掉的那张贴图就会退回"全库同名共享"，而现象只是"某个 Mod 的某个部位串味"——
	/// 看不出是哪一步漏的。统一收口之后，"忘了传命名空间"在类型上就写不出来。
	/// </para>
	/// </summary>
	public ulong DeriveMaterialId(string identity) => Hd2AssetId.Material(identity, idNamespace);

	/// <summary>贴图 id：名字 + 本转换器的命名空间（见 <see cref="DeriveMaterialId"/>）。</summary>
	public ulong DeriveTextureId(string textureName) => Hd2AssetId.Texture(textureName, idNamespace);

	private readonly string? idNamespace;

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

			var vmt = VmtDocument.Parse(vmtBytes, source.CreateIncludeReader());
			// 要算平均色的两类：
			//   ① matcap 就是表面（tint ≥ 0.9）；
			//   ② 根本没有底色（NekoToon 金属件）——那时 matcap 也是唯一的观感来源，
			//      与 tint 多大无关。少了 ② 这些件烘不出颜色，只能是纯黑。
			if (!MaterialAppearance.Evaluate(vmt).MatCapIsTheSurface && !HasNoRealBaseTexture(vmt))
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
		bool MatcapIsSurface,
		IReadOnlyList<string> Notices)
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
		// 分支看的是**模板**，不是"源侧要不要镂空"：C 档源侧仍然要镂空，但刻意留在 advanced。
		if (loaded.UseAlphaClip && IsAlphaClipTemplate(loaded.TemplateName))
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

			// B 档：只动阈值这一个变量（A 档保持模板默认 0.5）。
			if (cutoutStrategy == CutoutStrategy.AlphaClipTuned
				&& !material.TrySetScalar(OpacityThresholdVariableId, TunedOpacityThreshold))
			{
				return MaterialSynthesisResult.Failed(
					$"{stem}：alphaclip 模板里找不到 OpacityThreshold 变量，无法把阈值调到 {TunedOpacityThreshold}");
			}
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
		{ Identity = loaded.ToIdentity(), Notices = loaded.Notices };
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

		var vmt = VmtDocument.Parse(vmtBytes, source.CreateIncludeReader());

		// `$no_draw 1`：源侧根本不绘制这个材质（L4D2 用它隐藏部位）。
		// HD2 没有对应概念，所以**不硬造**——把它当"跳过"报出去。
		// 硬造两种结果都是错的：当失败会让整个 patch 写不出来；当成功会造出一个看得见的材质。
		if (!string.IsNullOrWhiteSpace(vmt.Value("$no_draw")))
		{
			reason = $"{stem}：源材质声明 $no_draw（源侧不绘制），跳过";
			return LoadOutcome.Skipped;
		}

		// 收敛成非 null：`HasNoRealBaseTexture` 把"缺失"和"占位值"都算作没有底色，
		// 所以下面的合成分支一定会给它赋一个真值。用 `?? string.Empty` 是为了让
		// 可空性在类型上闭合，而不是在每处调用点加 `!`。
		var baseTexture = vmt.BaseTexture ?? string.Empty;
		var verdict = MaterialAppearance.Evaluate(vmt);

		// matcap 贴图名要留到身份里：**它的内容可能来自别的 VPK**，而 id 只由名字决定。
		// 装配器据此报警——否则"换个 patch 就能换金属色"是个假承诺。
		var matcapTextureName = string.IsNullOrWhiteSpace(vmt.MatCapTexture)
			? null
			: Path.GetFileNameWithoutExtension(VmtDocument.NormalizePath(vmt.MatCapTexture));

		// ── 没有真底色的两种写法，处理方式不同 ──────────────────────────────
		//
		// ① `$basetexture "basetexture"` / `"base"`：作者显式写的"这里没有底色"占位值。
		// ② 干脆没写 `$basetexture`：NekoToon 的金属/宝石件就是这样（作者把它注释掉了）。
		//
		// 两种都是**源侧的合法写法**，不是坏材质。共同点是"观感全来自 matcap"：
		//   源侧实际渲染 = 底色(0) + matcap × tint
		// 所以我们合成一张**纯黑**底色，让 matcap 顶上去；判失败才是错的——
		// 判失败会让整个金属件退回外壳护甲的材质（用户报的"金属材质丢失"）。
		//
		// 没有 matcap 兜底时仍然是失败：那类材质（玻璃、纯 phong、着色器覆盖）的观感来自
		// envmap / phong，HD2 的模板没有对应槽，理由必须说清是"源侧没有底色"。
		// 诊断开关：`HD2_NO_SYNTHETIC_BASE=1` 时**恢复发布版行为**——没有真底色的材质直接判失败。
		//
		// 存在的理由：这些材质以前是"悬空 id"，游戏回退到外壳护甲的材质才看得见；
		// 现在它们有了自己的材质。用户报"手套/丝袜/臂环不见"，需要一次干净的对照来确认
		// 问题是否出在"新产出的材质"上。这条分支只为这个对照存在，不是产品行为。
		var allowSyntheticBase = Environment.GetEnvironmentVariable("HD2_NO_SYNTHETIC_BASE") != "1";

		var syntheticBase = false;
		if (HasNoRealBaseTexture(vmt))
		{
			if (string.IsNullOrWhiteSpace(matcapTextureName))
			{
				reason = string.IsNullOrWhiteSpace(baseTexture)
					? (vmt.IsPatch
						? $"{stem}：patch 材质展开后仍然没有 $basetexture（include={vmt.IncludePath ?? "缺失"}），也没有 $matcapture 可以兜底"
						: $"{stem}：VMT 里既没有 $basetexture 也没有 $matcapture（观感来自 envmap / phong），HD2 材质模板没有对应槽")
					: $"{stem}：$basetexture 是占位值（{baseTexture}），且没有 $matcapture 可以兜底"
						+ "（观感来自 envmap / phong），HD2 材质模板没有对应槽";
				return LoadOutcome.Failed;
			}

			if (!allowSyntheticBase)
			{
				reason = $"{stem}：没有 $basetexture，且诊断开关 HD2_NO_SYNTHETIC_BASE=1（恢复发布版行为）";
				return LoadOutcome.Failed;
			}

			syntheticBase = true;
			baseTexture = SyntheticBaseName(stem);
		}

		var textureName = syntheticBase
			? stem + "_matcapbase"
			: Path.GetFileNameWithoutExtension(baseTexture);

		if (!syntheticBase && !source.TryReadVtf(textureName, out _))
		{
			reason = $"{stem}：贴图来源里找不到 {textureName}.vtf";
			return LoadOutcome.Failed;
		}

		// 先解**底色**：既要它的像素（铸进 patch），也要它的 alpha 与亮度来判定
		// "matcap 是不是这个表面本身"。判定必须看内容，不能只看 tint —— 见下面的注释。
		VtfFile vtf;
		byte[] rgba;
		AlphaStats baseAlpha;
		if (syntheticBase)
		{
			// 纯黑 4×4。尺寸取 4×4 而不是 1×1：占位判据是"长宽都 ≤ 4"，两条都满足，
			// 而 4×4 让下游的 DDS / BC7 编码走正常路径。alpha 也置 0
			// ——"底色不承担形状"，这样下面不会被误判成 alphaclip。
			vtf = VtfFile.Synthetic(4, 4);
			rgba = MaterialAppearance.SolidRgba(4, 0, 0, 0, 0);
			baseAlpha = TextureAlpha.Measure(rgba);
		}
		else
		{
			source.TryReadVtf(textureName, out var vtfBytes);
			try
			{
				vtf = VtfFile.Read(vtfBytes);
				// ⚠️ 量 alpha 必须在清零之前（清零发生在编码那一步，见 BuildBaseColorTexture）。
				rgba = encoder.DecodeToRgba(BuildSourceDds(vtf));
				baseAlpha = TextureAlpha.Measure(rgba);
			}
			catch (Exception exception) when (exception is InvalidDataException or IOException or InvalidOperationException)
			{
				reason = $"{stem}：贴图 {textureName} 读不出来——{exception.Message}";
				return LoadOutcome.Failed;
			}
		}

		// matcap 承载表面 = **tint 高 ∧ 底色没有自己的外观 ∧ alpha 不承担形状**。
		//
		// 后两条缺一不可。原来只有 tint ≥ 0.9 一条，于是装饰件（花瓣、蕾丝、宝石）
		// 被大面积误判：它们普遍写 `$matcaptint "[1 1 1 1]"`（= 全强度叠加），
		// 但底色是一张真图、alpha 里就是花瓣的形状。误判的后果是两件事同时发生：
		//   ① 底色被 matcap 顶掉 → 花瓣渲染成 matcap 的金属/菱形渐变；
		//   ② alphaclip 被强制关掉 → 花瓣的镂空变成不透明白方块（"白色渐变"）。
		// 实测就是 Lilium_Sancta 的花瓣材质报的那两条现象。
		//
		// ⚠️ 合成底色的情况**无条件**算"matcap 承载表面"：底色根本不存在，
		// 观感只可能来自 matcap，与 tint 多大无关（实测 mourningveil 的
		// `mat_gem1_shoes` 就没有底色而 tint 只有 0.9）。
		var matcapIsSurface = syntheticBase
			|| (verdict.MatCapIsTheSurface
				&& MaterialAppearance.IsContentFreeBase(vtf.Width, vtf.Height, rgba)
				&& !baseAlpha.IsMeaningful);
		double[]? matcapMean = null;
		var notices = new List<string>();

		// ⚠️ **Metal 模式只对"没有真底色"的材质生效**（`syntheticBase`）。
		//
		// 原来写成 `matcapMode is Albedo or Metal`，于是 metal 模式会把**所有** matcap 承载表面
		// 的材质换掉底色——包括那些底色是一张"纯色但真实"的布料的材质（`IsContentFreeBase`
		// 把均匀色也算作"无内容"）。结果是布料丢掉自己的贴图。
		//
		// 现在的分工：
		//   · Albedo（发布版行为）：matcap 承载表面 → 拿 matcap 当底色
		//   · Metal：只有**没有 $basetexture** 的件才归一成金属 F0，有底色的布料保持原样
		if (matcapIsSurface && (matcapMode == MatCapMode.Albedo || (matcapMode == MatCapMode.Metal && syntheticBase)))
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

			// ⚠️ **Metal 模式下拿不到 matcap 不算失败。**
			// 金属档只需要 matcap 的"色相"，整片亮度会被归一成 F0——那张图缺了，
			// 退成中性金属就好。判失败会让整个部件丢掉材质（现象就是"部件不见了"），
			// 而"中性金属"至少是对的颜色。实测 serenade 的 `jem_red` / `jem_red_earring`
			// 引用的 `m7` 就不在包里。
			var matcapAvailable = source.TryReadVtf(matcapTextureName, out var matcapBytes);
			if (!matcapAvailable && matcapMode != MatCapMode.Metal)
			{
				reason = $"{stem}：matcap 贴图来源里找不到 {matcapTextureName}.vtf";
				return LoadOutcome.Failed;
			}

			if (matcapAvailable)
			{
				try
				{
					vtf = VtfFile.Read(matcapBytes);
					rgba = encoder.DecodeToRgba(BuildSourceDds(vtf));
					baseAlpha = TextureAlpha.Measure(rgba);
					textureName = matcapTextureName;
				}
				catch (Exception exception) when (exception is InvalidDataException or IOException or InvalidOperationException)
				{
					if (matcapMode != MatCapMode.Metal)
					{
						reason = $"{stem}：matcap 贴图 {matcapTextureName} 读不出来——{exception.Message}";
						return LoadOutcome.Failed;
					}

					// Metal：读不出来就用现有的（合成）底色，色相取中性。
					notices.Add($"{stem}：matcap 贴图 {matcapTextureName} 读不出来，金属色按中性处理。");
				}
			}
			else
			{
				notices.Add($"{stem}：找不到 matcap 贴图 {matcapTextureName}.vtf，金属色按中性处理。");
			}
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

		bool useAlphaClip;
		{
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
			// Metal 模式同样不切 alphaclip：底色将被整片归一成 F0，源侧的 alpha 不再有意义。
			//
			// ⚠️ **`$translucent` 仍然映射到 alphaclip —— 这是实机 A/B 逼出来的结论，别"修正"它。**
			//
			// 我曾认为"`$translucent` 是 alpha 混合、HD2 表达不了，当硬裁会删掉 58.8% 的
			// 像素"，于是把它改成"退成不透明（advanced）"。实机结果是：
			// **布料件（袖套 / 美甲底布）整片不见了，改动之前是好的。**
			// 拿发布版二进制做同参数 A/B 也证实：发布版把这些布料做成 alphaclip，显示正常；
			// 改成 advanced 之后看不见。也就是说那些 alpha 图案不是"多余的花纹"，
			// 而是这些件**唯一**的形状来源；不透明反而露出了不该画的整片 UV 区域。
			//
			// 所以这里按"源侧要不要透明"来判（`WantsTransparency`），与发布版一致。
			// `WantsAlphaTest` / `WantsTranslucency` 的区分仍然保留（报告里要说清是哪种），
			// 但**不改变模板选择**。
			useAlphaClip = matcapIsSurface && matcapMode is MatCapMode.Albedo or MatCapMode.Metal
				? false
				: vmt.WantsTransparency && baseAlpha.IsMeaningful;
		}

		// 降级提示：半透明在 HD2 里表达不了，我们退成了不透明。
		// 必须说出来——否则用户只会看到"袖套没透明"，或者更糟：以为我们又把东西弄丢了。
		if (useAlphaClip && vmt.WantsTranslucency && !vmt.WantsAlphaTest)
		{
			notices.Add(
				$"{stem}：源侧声明 $translucent（alpha 混合半透明），HD2 没有混合模板，"
				+ "已按硬裁（alphaclip）处理——形状正确，但没有真正的半透明过渡。");
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
		// ⚠️ **matcap 只要 VMT 声明了就必须进身份串，不看 tint。**
		//
		// 原来只在 tint ≥ 0.9 时才加，于是"同底色 + 不同 matcap + tint < 0.9"的材质
		// 全部塌成同一个 id。实测 mourningveil 的 `mat_cloth_shoes`（Matcap_highlight1 @ .3）、
		// `mat_leather_shoes` 与 `mat_metal`（Matcap_cloths4 @ .2）共用 `Mat_Metal` 底色——
		// 装配器按 id 去重"先到先得"，三件里两件拿到别人的 matcap。
		// 用户看到的就是"鞋跟上有一部分材质颜色错误"。
		//
		// tint 也进：Bake 模式下烘进底色的强度**就是** tint，强度不同就是不同观感。
		// ⚠️ 身份串恢复成**发布版形式**（只在 matcap 就是表面时才带 matcap 名，且不带 tint）。
		//
		// 原因：这一条改动会把几乎所有带 matcap 的材质 id 全部改写一遍。
		// A/B 已经排除"多出的 39 个金属材质"（撤掉后手套仍不显示），
		// 于是与发布版剩下的唯一差别就是**这些 id 值**本身。
		// 先把 id 改回去做单变量验证：如果手套回来 ⇒ id 就是原因；如果还不回来 ⇒ 与 id 无关，
		// 差异在我们的产物之外（Overwrite / 注册表 / 部署状态），那时再换方向。
		var identity = BuildMaterialIdentity(
			baseTexture, emissionTexture, useAlphaClip,
			verdict.MatCapIsTheSurface ? vmt.MatCapTexture : null);

		loaded = new LoadedMaterial(
			stem, baseTexture, textureName, vtf, rgba, useAlphaClip, templateName,
			identity, DeriveMaterialId(identity), emissionTexture, emissionTint, matcapMean, verdict.Tint,
			matcapTextureName, matcapIsSurface, notices);
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
	/// <para>
	/// <b>为什么 matcap 与 tint 都要进。</b>它们决定底色长什么样，而材质 id 是
	/// "同 id 就被合并"。实测 mourningveil 的 <c>mat_cloth_shoes</c> /
	/// <c>mat_leather_shoes</c> / <c>mat_metal</c> 共用 <c>Mat_Metal</c> 底色、
	/// matcap 各不相同（<c>Matcap_highlight1</c> @ .3 与 <c>Matcap_cloths4</c> @ .2）——
	/// 身份不含 matcap 时它们塌成一个材质，用户看到"鞋跟上有颜色错误"。
	/// </para>
	/// <para>
	/// ⚠️ 判据是 <b>VMT 声明了 matcap</b>（<c>$matcap</c> 或 <c>$matcaptexture</c>），
	/// 不看"队列里有没有模型能算平均色"——后者会让同一个 VMT 在不同队列里得到不同 id。
	/// </para>
	/// <para>
	/// ⚠️ <b>但 <see cref="CutoutStrategy"/> 不进身份串。</b>它和 <see cref="MatCapMode"/> 一样是
	/// <b>转换器的全局模式</b>，不是某个材质的属性：同一份源材质无论用哪一档转，都必须是同一个 id。
	/// 否则"只重转某一个材质包"会让 id 全变一遍，而模型 patch 里引用的还是旧 id，材质静默失效。
	/// </para>
	/// </summary>
	public static string BuildMaterialIdentity(
		string baseTexture, string? emissionTexture, bool useAlphaClip, string? matcapTexture = null,
		double? matcapTint = null)
	{
		var identity = string.IsNullOrWhiteSpace(emissionTexture) ? baseTexture : baseTexture + "|emission";
		if (useAlphaClip)
			identity += "|alphaclip";
		if (!string.IsNullOrWhiteSpace(matcapTexture))
			identity += "|matcap:" + matcapTexture;
		if (matcapTint is { } tint)
		{
			// 固定小数位 + 不变文化：id 是哈希，浮点表示法一漂移 id 就变。
			identity += "|matcaptint:" + tint.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
		}

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
			MaterialAppearance.BakeMatCap(rgba, loaded.MatcapMean, loaded.MatcapTint * matcapBakeGain);
		else if (loaded.MatcapIsSurface && matcapMode == MatCapMode.Metal)
		{
			// 归一成金属 F0：保留色相、整片抬到目标反射率。
			// 目标值 = MetalF0 × matcapBakeGain，这样 gain 在这里退化成"F0 微调"。
			MaterialAppearance.LevelToMetalF0(rgba, MetalF0 * matcapBakeGain);
		}
		else if (loaded.MatcapIsSurface && matcapMode == MatCapMode.Albedo)
			MaterialAppearance.ScaleRgb(rgba, matcapBakeGain);

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
				DeriveTextureId(textureName),
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
				DeriveTextureId(stem), asset.WriteToc(), asset.Pixels, stem,
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
		//
		// 通道与极性由 <see cref="CutoutStrategy"/> 决定：A/B 档是"RGB = alpha、A = 255"
		// （历史行为），另外三档用来实测 HD2 到底采样哪个通道、什么极性。
		var inAlpha = cutoutStrategy is CutoutStrategy.MaskInAlpha or CutoutStrategy.MaskInAlphaInverted;
		var inverted = cutoutStrategy is CutoutStrategy.MaskInverted or CutoutStrategy.MaskInAlphaInverted;
		var mask = new byte[checked(width * height * 4)];
		for (var offset = 0; offset < mask.Length; offset += 4)
		{
			var value = rgba[offset + 3];
			if (inverted)
				value = (byte)(255 - value);
			var grey = inAlpha ? (byte)255 : value;
			mask[offset + 0] = grey;
			mask[offset + 1] = grey;
			mask[offset + 2] = grey;
			mask[offset + 3] = inAlpha ? value : (byte)255;
		}

		var dds = Hd2TextureAsset.BuildDds(width, height, Rgba8Unorm, [mask]);
		var bc7 = encoder.EncodeToBc7(dds, srgb: false);
		var texture = new Hd2TextureAsset();
		texture.FromDds(bc7);
		return new SynthesizedTexture(
			DeriveTextureId(textureName + "_opacity"),
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
		// 缓存见 solidTextures 的注释：同一张纯色图只编一次。
		// 注意 texconv 在极端情况下会被同键的并发请求重复调用（GetOrAdd 的工厂可能跑多次），
		// 但那只是白编一次 4×4 图，产物逐字节相同，不影响正确性。
		=> solidTextures.GetOrAdd(
			(owner + suffix, red, green, blue, alpha),
			static (key, state) => state.self.EncodeSolidTexture(key.Name, state.suffix, key.Red, key.Green, key.Blue, key.Alpha),
			(self: this, suffix));

	private SynthesizedTexture EncodeSolidTexture(string name, string suffix, byte red, byte green, byte blue, byte alpha)
	{
		const int size = 4;
		var dds = Hd2TextureAsset.BuildDds(size, size, Rgba8Unorm, [MaterialAppearance.SolidRgba(size, red, green, blue, alpha)]);
		var bc7 = encoder.EncodeToBc7(dds, srgb: false);
		var texture = new Hd2TextureAsset();
		texture.FromDds(bc7);
		return new SynthesizedTexture(
			DeriveTextureId(name),
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
