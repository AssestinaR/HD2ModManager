using System.Buffers.Binary;
using System.Globalization;
using HD2SourceModel;

namespace HD2SourceMigration.VpkConversion;

/// <summary>
/// 镂空件用哪个模板落地。
/// <para>
/// <b>为什么这是一个选项而不是定论。</b>HD2 没有任何模板同时具备
/// 「逐像素不透明度 + 金属度 + 天气掩码」（见 docs/vpk-cutout-strategies-20261101.md §6）：
/// </para>
/// <list type="bullet">
/// <item><c>alphaclip</c>：有 <c>OpacityClipMap</c>，但<b>一个天气/雪槽都没有</b>
/// —— 实测"所有镂空件在雪地会被雪图层覆盖"就是它。</item>
/// <item><c>original</c>：有 <c>SubsurfaceOpacity</c>（逐像素不透明度）<b>和</b>
/// <c>WeatheringSpecial</c>（天气掩码），但<b>没有金属度</b>。</item>
/// </list>
/// <para>
/// 所以 <c>original</c> 严格优于 <c>alphaclip</c>（后者两样都没有），但它的槽位语义
/// （哪个槽是底色、要不要配 <c>OpacitySharpness</c>）只能靠实机定。
/// </para>
/// </summary>
public enum CutoutTemplate
{
	/// <summary>历史行为：<c>alphaclip</c>（有镂空、无金属、无天气掩码）。</summary>
	AlphaClip = 0,

	/// <summary>
	/// <c>original</c>：本体角色材质用的那个 13 槽模板。
	/// <para>
	/// <b>⚠️ 已实机证伪，别再用。</b>把镂空遮罩写进 <c>SubsurfaceOpacity</c>（槽 7）之后，
	/// 游戏里<b>完全没有镂空</b>。原因后来想通了：那份本体材质自己的贴图 alpha 全是 0～4
	/// —— 游戏角色根本不靠材质做镂空，<c>SubsurfaceOpacity</c> 是次表面散射用的，
	/// 不是逐像素 alpha。
	/// </para>
	/// <para>
	/// 所以 <c>alphaclip</c> 是 HD2 里<b>唯一</b>支持逐像素镂空的模板；它的雪污染已经通过
	/// 关掉 <c>DirtGlobalAmount</c> 解决（见 <see cref="DirtGlobalAmountVariableId"/>）。
	/// 这个枚举值保留只为留档。
	/// </para>
	/// </summary>
	Original = 1,
}

/// <summary>
/// 一个材质<b>既要镂空、又是金属</b>时怎么取舍。
/// <para>
/// <b>为什么必须二选一（实机实测，不是设计选择）。</b>
/// <c>advanced</c> 有金属度（槽 3 <c>IlluminateData</c>）但<b>没有逐像素不透明度</b>
/// （<c>SclarFieldOpacity</c> 是 1×1 标量，A/B/C 三档已证伪）；
/// <c>alphaclip</c> 有逐像素镂空但<b>没有金属度</b>——
/// 它的 <c>BaseColorMetalMap.alpha</c> 曾被当成金属度写入，实机对照<b>完全没有效果</b>
/// （见 <c>docs/vpk-cutout-strategies-20261101.md</c> §5）。
/// </para>
/// </summary>
public enum MetalCutoutPolicy
{
	/// <summary>
	/// <b>保镂空</b>（历史行为）：走 <c>alphaclip</c>，金属度丢失。
	/// <para>对"靠 alpha 才能成形"的件（蕾丝、镂空花纹）是唯一正确选择。</para>
	/// </summary>
	CutoutWins = 0,

	/// <summary>
	/// <b>保金属</b>：走 <c>advanced</c>，金属度生效，镂空丢失。
	/// <para>
	/// <b>为什么这可能是对的。</b>NekoToon 的装饰件贴图是一张<b>图集</b>，alpha 里那些透明区域
	/// 很可能只是"图集空白"，而网格 UV 本来就只落在不透明区——那种情况下镂空是<b>冗余的</b>。
	/// 是否成立只能实机看"有没有露出整块 UV 方片"。
	/// </para>
	/// </summary>
	MetalWins = 1,
}

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
	/// 初衷是让 C1/C2 这类"只换 matcap"的 VPK 变成真正可换的 patch。
	/// </para>
	/// <para>
	/// <b>⚠️ 已证伪（实机对照），留着只为留档，不要再启用。</b>matcap 的 UV 是
	/// <b>视角法线</b>，不是模型 UV；把它当底色贴图，模型 UV 采到的是球面上<b>一块随机区域</b>
	/// （实测鞋底正好落在暗部）。结果与"这是不是金属"完全无关，只取决于那块 UV 碰巧在哪：
	/// 实机观感是"<b>粗糙的生铁，完全无反光</b>"。
	/// </para>
	/// <para>
	/// 现在的发布路径是 <see cref="Metal"/>：金属色统一成中性常量，见
	/// <c>MaterialAppearance.FlattenToMetalF0</c>。
	/// </para>
	/// </summary>
	Albedo = 1,

	/// <summary>
	/// 把 matcap 涂成<b>中性金属的 F0</b>：丢掉亮度图案与色相，整片抬到目标反射率。
	/// <para>
	/// <b>这是发布路径。</b>金属度顶到 1.0、粗糙度按 <c>--metal-roughness</c>（默认 0.20），
	/// 反光交给 PBR 的环境反射算。
	/// </para>
	/// <para>
	/// <b>为什么不能保留色相。</b>matcap 由<b>可选的颜色变体包</b>提供（C1/C2 提供
	/// <c>11</c>/<c>Matcap_diamond4</c>，B1/B2/B3 提供 <c>Matcap_metal8</c>/<c>diamond5/6/7</c>），
	/// 取色相就等于"同一份源装不同变体包出不同金属色"——那对转换器是错的。
	/// 所以金属色是一个与可选包无关的常量。详见 <c>MaterialAppearance.FlattenToMetalF0</c>。
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

	/// <summary>
	/// <b>D：抖动透明（alpha-to-coverage 的等价做法）</b>。
	/// <para>
	/// 渐变 alpha 在 <c>alphaclip</c> 下只有"在/不在"两态，于是源侧 76% 的中间值被压成硬边。
	/// 这一档把遮罩**抖成二值**：每个像素按 <c>alpha</c> 与 8×8 Bayer 阈值比较决定画不画，
	/// 远处被 TAA 累积平均成正确的透明度，近处看到细密噪点。
	/// </para>
	/// <para>
	/// <b>为什么这是"还原"而不是"发明"：</b>源侧这些衣服本来就写着
	/// <c>$alphatest 1</c> + <c>$allowalphatocoverage 1</c>——Source 2013 用 MSAA 的
	/// 覆盖位做同样的事。我们只是把那个手段搬到了贴图空间。
	/// </para>
	/// <para>
	/// 只抖中间值（5%～95%），两端保持纯实/纯透：镂空边缘不变，只有"薄纱"那层变成半透。
	/// </para>
	/// </summary>
	AlphaClipDithered = 5,

	/// <summary>
	/// <b>E：抖动加强</b>——与 D 同一套抖动，但 alpha 先乘 1.35、并且整片都参与抖动。
	/// <para>
	/// 用来回答"是不是抖得太保守"：如果 D 看着还是偏薄/偏硬，E 会把观感往实里推。
	/// </para>
	/// </summary>
	AlphaClipDitheredStrong = 6,
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
/// 一个源材质的法线图最终会怎么样（<c>$bumpmap</c> → HD2 的 <c>Nar</c> 通道）。
/// <para>
/// <b>为什么有第三档。</b>NekoToon 那套包的 VMT 几乎每个都写 <c>$bumpmap</c>，
/// 但**贴图本身**常常在另一个包里（材质包只换色、法线留在大包里）。只报
/// "声明了多少个"会把"其实一半没接上"说成好消息。
/// </para>
/// </summary>
public enum NormalDelivery
{
	/// <summary>这个材质没有 <c>$bumpmap</c>（或读不到 VMT）。</summary>
	None = 0,

	/// <summary>声明了法线图，而且那张图在来源里——会真的写进 HD2。</summary>
	Wired = 1,

	/// <summary>声明了法线图，但来源里找不到那张 <c>.vtf</c>，只能退回平面法线。</summary>
	TextureMissing = 2,
}

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

	/// <summary>
	/// <c>original</c> 模板（13 槽）里我们关心的三个槽位下标。
	/// <para>
	/// 顺序取自本体角色材质 <c>0x8f669f365f24594e</c> 的实例（材质按模板槽序存）：
	/// 0 <c>ClearcoatRoughnessAoSpec</c> / 1 <c>EmissiveColor</c> / 2 <c>BloodTiler</c> /
	/// 3 <c>SclarFieldOpacity</c> / 4 <c>DetailNormal1</c> / <b>5 <c>Normal</c></b> /
	/// 6 <c>WeatheringSpecial</c> / <b>7 <c>SubsurfaceOpacity</c></b> /
	/// 8 <c>TextureMap02</c> / <b>9 <c>AlbedoIridescence</c></b> /
	/// 10–12 <c>WoundNormal</c> / <c>WoundData</c> / <c>WoundDerivative</c>。
	/// </para>
	/// <para>
	/// <b>这三个映射是待实机验证的假设</b>：底色取 <c>AlbedoIridescence</c>（名字含 albedo）、
	/// 遮罩取 <c>SubsurfaceOpacity</c>（唯一的逐像素不透明度候选）、法线取 <c>Normal</c>。
	/// <c>WeatheringSpecial</c>（6）刻意<b>不动</b>——留模板默认，让天气掩码按游戏原意工作。
	/// </para>
	/// </summary>
	public const int OriginalNormalSlot = 5;

	/// <inheritdoc cref="OriginalNormalSlot"/>
	public const int OriginalOpacitySlot = 7;

	/// <inheritdoc cref="OriginalNormalSlot"/>
	public const int OriginalBaseColorSlot = 9;

	/// <summary><c>advanced</c> 槽 2 = <c>Nar</c>（RGB 法线、<b>A 粗糙度</b>）。</summary>
	public const int AdvancedNarSlot = 2;

	/// <summary><c>advanced</c> 槽 3 = <c>IlluminateData</c>（金属度）。</summary>
	public const int AdvancedMetallicSlot = 3;

	/// <summary>
	/// 金属度槽的<b>键</b>（<c>IlluminateData</c>，0x90b84a53）。
	/// <para>
	/// 用它去问"某个模板能不能表达金属度"（<see cref="Hd2MaterialTemplateLibrary.HasTextureSlot"/>），
	/// 而<b>不是</b>按模板名写死 <c>"advanced"</c>：名字只是命名，能力由槽决定。
	/// 换 SDK 版本、模板改名、或将来用上 <c>basic</c> 族的 <c>Mra</c> 时，
	/// 按名字判断会静默失效。
	/// </para>
	/// </summary>
	public const uint MetallicSlotKey = 0x90b84a53;

	/// <summary>
	/// <c>advanced</c> 槽 1 = <c>SclarFieldOpacity</c>（0x479fb1ef）。
	/// <para>
	/// 这是 <c>advanced</c> 唯一的不透明度来源。SDK 模板里它指向一张占位贴图，
	/// 所有现成模组都原样留着——<see cref="CutoutStrategy.AdvancedOpacity"/> 才把它当遮罩用。
	/// </para>
	/// </summary>
	public const int AdvancedOpacitySlot = 1;

	/// <summary>
	/// 金属件 F0 底色贴图的名字后缀。
	/// <para>
	/// 存在的唯一理由：让这张图<b>不沿用 matcap 的名字</b>。贴图 id 由名字派生，
	/// 而"该由哪个 patch 产出"也按名字判——沿用 matcap 名会让它被两边都推给对方，
	/// 最终<b>谁都不产出</b>，材质在游戏里没有底色。见 <c>TryLoad</c> 里赋值处的注释。
	/// </para>
	/// </summary>
	private const string MetalF0Suffix = "_f0";

	/// <summary>
	/// <c>DirtGlobalAmount</c>（0xa3351311）：<b>脏污/天气的总开关</b>（标量）。
	/// <para>
	/// <b>这就是"镂空件在雪地被雪图层覆盖"的根因。</b>实测 <c>alphaclip</c> 模板的默认值是
	/// <b>1</b>（满脏污），配套还有 <c>WeatheringCoverage = 0.5</c>、
	/// <c>WeatheringMasking = 1</c>、<c>DirtDetailMasking = 1</c>、<c>DirtAoCoverage = 1</c>；
	/// 而 <c>advanced</c> 模板<b>根本没有这些变量</b>，它的 <c>WeatheringTiling</c> 还是 0。
	/// </para>
	/// <para>
	/// 所以同一件衣服，做成 <c>advanced</c> 不受天气影响、做成 <c>alphaclip</c> 就被雪盖住
	/// —— 不是"alphaclip 缺掩码槽"，而是它的默认值把天气拉满了。
	/// 源侧模组没有"天气/脏污"这个概念，所以转换时应当关掉。
	/// </para>
	/// </summary>
	public const uint DirtGlobalAmountVariableId = 0xa3351311;

	/// <summary><c>WeatheringCoverage</c>（0x4a7cd0ef）：天气覆盖率（标量，alphaclip 默认 0.5）。</summary>
	public const uint WeatheringCoverageVariableId = 0x4a7cd0ef;

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
	/// 被别的 VMT <c>include</c> 的材质短名集合（懒加载，只在本批**自己带的** VMT 里找）。
	/// <para>
	/// 用途只有一个：把"include 链的中间层"从"合成失败"降级成"跳过"。实测 `lime_witch.vpk`
	/// （模型与材质同包）15 份 VMT 里有 5 份是这种：模板根 `basic` 把 <c>$basetexture</c> 写成
	/// 占位值，`part_selfillum` / `part_emiss` / `body_phong` / `face_phong` 只给下游补参数，
	/// 真正的底色由引用它们的那份用 <c>replace</c> 写。模型从不直接引用它们，
	/// 所以那 5 条"合成失败"只是噪音。
	/// </para>
	/// </summary>
	private readonly Lazy<IReadOnlySet<string>> includeTargets;

	/// <summary>
	/// 模型**直接引用**的材质短名（可空，装配器在 <c>AssembleAsync</c> 之前塞进来）。
	/// 用来防止"中间层跳过"误伤：某份 VMT 既被 include、模型又直接引用它时，
	/// 没有底色是**真失败**，不能悄悄跳过（那会让模型引用一个没写出去的材质 id）。
	/// </summary>
	public IReadOnlyCollection<string>? ModelMaterialNames { get; set; }

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

	/// <summary>
	/// 法线图缓存。键是（法线图名, 强度, 粗糙度, 是否翻 Y）——<b>四样都会改变产物的字节</b>。
	/// <para>
	/// 值和 <see cref="solidTextures"/> 一样是不可变 record，可以安全共享；
	/// <c>Texture = null</c> 的条目表示"这张图读不出来/不在来源里"，也缓存下来，
	/// 免得同一个坏贴图被几十个材质各解一遍。
	/// </para>
	/// </summary>
	private readonly System.Collections.Concurrent.ConcurrentDictionary<
		(string Name, double Strength, byte Roughness, bool FlipY),
		(SynthesizedTexture? Texture, string? Notice)> normalTextures = new();

	/// <summary>
	/// 与**内容**绑定的 BC7 缓存：同一份像素只编一次。
	/// <para>
	/// <b>为什么需要它。</b>编码是这条链上最贵的一步（texconv 编 2048² 约 0.6 s，实测），
	/// 而材质是**按名字**各编一遍的。实测 Lunalice B1：53 个材质只用到 25 张源贴图——
	/// 也就是说大约一半的编码在重复编同一份字节。整个包 38 s，砍掉重复的就砍掉三分之一以上。
	/// </para>
	/// <para>
	/// <b>键为什么是内容哈希而不是贴图名。</b>同一张源图在 matcap 烘焙、压暗、擦 alpha
	/// 之后可能得到不同字节（不同材质不同参数）。按名字缓存会把"别人的那张"发过来，
	/// 那种错在产物里看不出来，只会让人对着"某个件颜色莫名其妙"查半天。
	/// </para>
	/// </summary>
	private readonly System.Collections.Concurrent.ConcurrentDictionary<
		(ulong Hash, int Length, bool Srgb), byte[]> bc7ByContent = new();

	/// <summary>按内容去重的 BC7 编码，返回完整 DDS（148 字节头 + 像素）。</summary>
	private byte[] EncodeBc7Deduplicated(byte[] rgba, int width, int height, bool srgb)
	{
		var key = (HashRgba(rgba), rgba.Length, srgb);
		return bc7ByContent.GetOrAdd(
			key,
			_ => encoder.EncodeToBc7(Hd2TextureAsset.BuildDds(width, height, Rgba8Unorm, [rgba]), srgb));
	}

	/// <summary>与内容绑定的解码缓存：同一张 VTF 只解一次（texconv 解 2048² 约 0.1 s）。</summary>
	private readonly System.Collections.Concurrent.ConcurrentDictionary<
		(string Name, int Length, ulong Hash), byte[]> decodedTextures = new();

	/// <summary>
	/// 解码一张 VTF 成裸像素，<b>命中缓存时返回副本</b>。
	/// <para>
	/// <b>为什么必须复制。</b>调用方会就地改像素——擦 alpha、烘 matcap、涂金属 F0、压暗。
	/// 共享同一份数组会让"后一个材质改了前一个材质的底色"，那种错在产物里表现为
	/// 某几件颜色莫名其妙，而且只在多材质共用一张图时出现，极难查。
	/// </para>
	/// <para>
	/// 键里带字节长度与前 8 字节哈希之外的整体哈希：同名不同内容的贴图（不同包里的
	/// `white.vtf`）不会被当成同一张。
	/// </para>
	/// </summary>
	private byte[] DecodeTextureCached(string textureName, byte[] vtfBytes, VtfFile vtf)
	{
		var key = (textureName, vtfBytes.Length, HashRgba(vtfBytes));
		if (decodedTextures.TryGetValue(key, out var cached))
			return (byte[])cached.Clone();

		var rgba = encoder.DecodeToRgba(BuildSourceDds(vtf));
		decodedTextures.TryAdd(key, (byte[])rgba.Clone());
		return rgba;
	}

	/// <summary>
	/// 64 位 FNV-1a（按 8 字节读）。<b>只为去重服务</b>：33 MB 约 15 ms，
	/// 而 SHA256 要 90 ms——省下的时间被哈希吃掉就没意义了。
	/// 键里同时带长度与 srgb，进一步压缩碰撞面；真要撞上，代价是"两张图被当成同一张"。
	/// </summary>
	private static ulong HashRgba(byte[] rgba)
	{
		const ulong prime = 1099511628211UL;
		var hash = 14695981039346656037UL;
		var span = rgba.AsSpan();
		var index = 0;
		for (; index + 8 <= span.Length; index += 8)
			hash = (hash ^ BinaryPrimitives.ReadUInt64LittleEndian(span[index..])) * prime;
		for (; index < span.Length; index++)
			hash = (hash ^ span[index]) * prime;
		return hash == 0 ? 1 : hash;   // 0 留给"没算过"，虽然这里用不到
	}

	public SourceMaterialConverter(
		SourceMaterialSource source,
		Bc7Encoder encoder,
		Hd2MaterialTemplateLibrary templates,
		AlbedoAdjustment? albedoAdjustment = null,
		IReadOnlyDictionary<string, double[]>? matcapMeans = null,
		MatCapMode matcapMode = MatCapMode.Bake,
		string? idNamespace = null,
		CutoutStrategy cutoutStrategy = CutoutStrategy.AlphaClip,
		double matcapBakeGain = 1.0,
		AlphaExperiment alphaExperiment = AlphaExperiment.None,
		int ditherScale = 1,
		double ditherDensity = 0.75,
		DitherPattern ditherPattern = DitherPattern.InterleavedGradient,
		double metalRoughness = MaterialAppearance.DefaultMetalRoughness,
		MetalCutoutPolicy metalCutout = MetalCutoutPolicy.CutoutWins,
		CutoutTemplate cutoutTemplate = CutoutTemplate.AlphaClip,
		bool normalFlipY = false,
		// 逐材质的「UV 空间模型法线」（见 UvNormalBake）：matcap 与 lightwarp 都需要**世界方向**的
		// 法线，而法线贴图给的是切线空间的方向。没有它时这两样退回"取正中"。
		Func<string, UvNormalMap?>? worldNormalFactory = null)
	{
		this.source = source;
		this.encoder = encoder;
		this.templates = templates;
		this.albedoAdjustment = albedoAdjustment;
		this.matcapMeans = matcapMeans;
		this.worldNormalFactory = worldNormalFactory;
		this.matcapMode = matcapMode;
		this.idNamespace = string.IsNullOrWhiteSpace(idNamespace) ? null : idNamespace.Trim();
		this.cutoutStrategy = cutoutStrategy;
		this.matcapBakeGain = double.IsFinite(matcapBakeGain) && matcapBakeGain > 0 ? matcapBakeGain : 1.0;
		this.alphaExperiment = alphaExperiment;
		this.ditherScale = Math.Clamp(ditherScale, 1, 8);
		this.ditherDensity = double.IsFinite(ditherDensity) ? Math.Clamp(ditherDensity, 0.05, 1.0) : 1.0;
		this.ditherPattern = ditherPattern;
		this.metalRoughness = (byte)Math.Clamp(
			Math.Round((double.IsFinite(metalRoughness) ? Math.Clamp(metalRoughness, 0, 1) : MaterialAppearance.DefaultMetalRoughness) * 255.0),
			0, 255);
		this.metalCutout = metalCutout;
		this.cutoutTemplate = cutoutTemplate;
		this.normalFlipY = normalFlipY;
		this.includeTargets = new Lazy<IReadOnlySet<string>>(BuildIncludeTargets, LazyThreadSafetyMode.ExecutionAndPublication);
	}

	/// <summary>
	/// 源法线图的 <b>Y 方向要不要翻</b>（DirectX ↔ OpenGL）。
	/// <para>
	/// <b>为什么要做成开关。</b>NekoToon / <c>pbr.vmt</c> 明确要求 DirectX 格式的法线
	/// （<c>--invert-green</c> 用来把 OpenGL 的转过来），而 HD2 侧的法线约定<b>没有文档</b>。
	/// 猜错的表现是"受光面凹下去"，而且只在斜面上看得出来——这种错只能实机 A/B 定，
	/// 所以给一个可切换的旋钮，而不是在代码里赌一边。
	/// </para>
	/// </summary>
	public bool NormalFlipY => normalFlipY;

	private readonly bool normalFlipY;

	/// <summary>
	/// matcap 承载面在 <c>advanced</c> 槽 2 <b>alpha</b> 里写的粗糙度（0..255）。
	/// <para>
	/// <b>这是"够不够金属"最直接的旋钮。</b>0.35（89）是拉丝/氧化的观感，0.08（20）才是镀铬。
	/// 做成参数而不是常量，是因为它只能靠实机定——每个引擎的环境探针质量不同。
	/// </para>
	/// </summary>
	public byte MetalRoughness => metalRoughness;

	private readonly byte metalRoughness;

	/// <summary>"既要镂空又是金属"时怎么取舍。见 <see cref="MetalCutoutPolicy"/>。</summary>
	public MetalCutoutPolicy MetalCutout => metalCutout;

	private readonly MetalCutoutPolicy metalCutout;

	/// <summary>镂空件用哪个模板。见 <see cref="CutoutTemplate"/>。</summary>
	public CutoutTemplate CutoutTemplate => cutoutTemplate;

	private readonly CutoutTemplate cutoutTemplate;

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

	/// <summary>
	/// 半透明落地方式的实验开关（默认 <see cref="AlphaExperiment.None"/>）。
	/// <para>
	/// 打开时**模板不再由 VMT 决定**，而是由这个开关决定；同时材质身份串里**不再带模板后缀**
	/// ——三个变体必须算出同一个 id，模型 patch 才能一次换好、之后只换材质包做对照。
	/// </para>
	/// </summary>
	private readonly AlphaExperiment alphaExperiment;

	/// <summary>
	/// 抖动的**点大小**（1 = 每像素一个点，2 = 2×2 的点）。
	/// <para>
	/// 实机反馈：2 太粗——放大点只会让颗粒更明显，不会更"轻"。所以默认回到 1，
	/// "稀不稀"交给 <see cref="ditherDensity"/>。
	/// </para>
	/// </summary>
	private readonly int ditherScale;

	/// <summary>
	/// 抖动的**密度**（乘在 alpha 上）：1.0 = 忠实还原源侧 alpha；越小点越少、越透。
	/// <para>
	/// 点大小与密度是两个独立旋钮。实机结论是"点保持 1 像素、只降密度"观感最好：
	/// 源侧 alpha 直接抖出来的点偏密、偏"厚"，压到 0.75 之后薄纱感更像原版。
	/// </para>
	/// </summary>
	private readonly double ditherDensity;

	/// <summary>
	/// 抖动的**阈值图案**：默认交错梯度噪声（无可见重复结构）；Bayer 保留用于对照。
	/// </summary>
	private readonly DitherPattern ditherPattern;

	/// <summary>
	/// 实验开关是否真的改这个材质的模板：**只对"需要透明"的材质生效**。
	/// <para>
	/// 不能对所有材质一律改——那会把整只角色（金属装饰、脸、头发）都变成平涂半透明，
	/// 对照就失去意义了。不透明件保持原有模板（<c>advanced</c>），只有本来会走
	/// <c>alphaclip</c> 的那些才换成实验模板。
	/// </para>
	/// </summary>
	private bool ExperimentApplies(bool useAlphaClip)
		=> alphaExperiment != AlphaExperiment.None && useAlphaClip;

	/// <summary><c>translucent</c> 模板的变量 id（SDK <c>hashlists/shadervariables.txt</c>）。</summary>
	private const uint TranslucentOpacityBaseVariableId = 0xab58e70d;

	private const uint TranslucentBaseColorVariableId = 0xcb577b8f;

	/// <summary><c>advanced</c> 模板里那条"不透明度场"的变量（本实验用来试探它是否混合）。</summary>
	private const uint AdvancedOpacityOffsetVariableId = 0xccddd706;

	/// <summary><c>advanced</c> 模板的 <c>SclarFieldOpacity</c> 槽下标。</summary>
	public const int AdvancedOpacityFieldSlot = 1;

	/// <summary>这个模板名是不是 <c>alphaclip</c> 族（槽表只有 3～4 个、没有 PBR 输入）。</summary>
	/// <summary>镂空件落到哪个模板；不需要镂空时一律 <c>advanced</c>。</summary>
	private string CutoutTemplateName(bool useAlphaClip)
	{
		if (!useAlphaClip)
			return "advanced";
		return cutoutTemplate == CutoutTemplate.Original ? "original" : "alphaclip";
	}

	/// <summary>这个模板是不是 <c>original</c>（本体角色材质那一套 13 槽）。</summary>
	private static bool IsOriginalTemplate(string templateName)
		=> string.Equals(templateName, "original", StringComparison.OrdinalIgnoreCase);

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

	/// <summary>逐材质的 UV 空间模型法线（key = 材质裸名）。</summary>
	private readonly Func<string, UvNormalMap?>? worldNormalFactory;

	/// <summary>取这个材质的「UV 空间世界法线」；没有就 <c>null</c>（调用方退回无方向方案）。</summary>
	private UvNormalMap? WorldNormalFor(string stem)
		=> worldNormalFactory?.Invoke(stem);

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

	/// <summary>模型是否直接引用了这个材质短名（含没设 <see cref="ModelMaterialNames"/> 时永远为 false）。</summary>
	private bool IsReferencedByModel(string stem)
	{
		var names = ModelMaterialNames;
		if (names is null || names.Count == 0) return false;
		foreach (var name in names)
		{
			if (string.Equals(
				Path.GetFileNameWithoutExtension(VmtDocument.NormalizePath(name)),
				stem,
				StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>被本批自带 VMT <c>include</c> 的材质短名集合。见 <see cref="includeTargets"/>。</summary>
	private IReadOnlySet<string> BuildIncludeTargets()
	{
		var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var name in source.OwnedVmtNames)
		{
			var stem = Path.GetFileNameWithoutExtension(VmtDocument.NormalizePath(name));
			if (string.IsNullOrWhiteSpace(stem) || !source.TryReadVmt(stem, out var vmtBytes))
				continue;

			// **不展开 include**：这里只问"它 include 了谁"。展开会让每个中间层的链
			// 都被走一遍，而这条判据只需要第一层。
			var document = VmtDocument.Parse(vmtBytes);
			if (string.IsNullOrWhiteSpace(document.IncludePath))
				continue;

			var included = Path.GetFileNameWithoutExtension(VmtDocument.NormalizePath(document.IncludePath));
			if (!string.IsNullOrWhiteSpace(included)) targets.Add(included);
		}

		return targets;
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
		/// <summary>
		/// 这个材质是不是<b>金属</b>——由 matcap 的名字判（见
		/// <see cref="MaterialAppearance.IsMetallicMatcapName"/>），与底色亮度无关。
		/// <para>
		/// 与 <see cref="MatcapIsSurface"/> 刻意分开：后者是"底色该不该被 matcap 顶掉"，
		/// 前者是"金属度给不给"。两者在宝石/玻璃上会分叉——底色要顶掉（源底色是近黑占位图），
		/// 但不该给金属度。
		/// </para>
		bool MatcapIsMetal,
		/// <summary>
		/// <b>这个材质最终用的模板能不能表达金属度</b>（= 模板里有没有 <c>IlluminateData</c> 槽）。
		/// <para>
		/// 由 <see cref="Hd2MaterialTemplateLibrary.HasTextureSlot"/> 按<b>槽键</b>问出来，
		/// 不是按模板名猜。用途有二：决定要不要把底色涂成金属 F0；决定要不要告诉用户
		/// "这块在源侧是金属，但这个模板表达不了"。
		/// </para>
		/// <para>
		/// 实测 MourningVeil：判成金属的 10 个材质<b>全部</b>因为"镂空优先"进了 alphaclip，
		/// 一个都没拿到金属度；而它们此前仍被涂成 F0 0.96（亮银），在游戏里就是
		/// "一块不像金属的亮银塑料"。有了这条判据，那种涂法根本不会发生。
		/// </para>
		/// </summary>
		bool MetalDeliverable,
		/// <summary>
		/// **源底色的像素**（不是被 matcap 替换过的那份），专门用来生成镂空/半透明遮罩。
		/// <para>
		/// 与 <see cref="Rgba"/> 分开的理由见 <c>TryLoad</c> 里 <c>sourceBaseRgba</c> 的注释：
		/// matcap 承载表面会把底色换成 matcap 图像，而形状信息留在源底色的 alpha 里。
		/// </para>
		/// </summary>
		byte[] SourceBaseRgba,
		/// <summary>源底色的宽度（与 <see cref="SourceBaseRgba"/> 配套；matcap 尺寸通常不同）。</summary>
		int SourceBaseWidth,
		/// <summary>源底色的高度。</summary>
		int SourceBaseHeight,
		IReadOnlyList<string> Notices,
		// 遮罩要不要抖成二值（近似半透明）。由**源侧意图**决定：
		// `$translucent`（要混合）或 `$allowalphatocoverage`（自己就写着抖动）。
		bool UseDither)
	{
		/// <summary>
		/// 源侧声明的法线图裸名（<c>$bumpmap</c> 的最后一段）。<c>null</c> = 这个材质没有法线图。
		/// <para>
		/// <b>为什么单独留一个字段。</b>法线原来<b>完全没有接线</b>：所有材质写进 HD2 的都是
		/// 平面法线（128,128,255），而 NekoToon 那套模型普遍带 ATI2N/BC5 高清法线图。
		/// 现在它决定"这张材质要不要配一张自己的法线贴图"。
		/// </para>
		/// </summary>
		public string? BumpTexture { get; init; }

		/// <summary>
		/// <c>$bumpstrength</c>（凹凸强度，NekoToon 默认 1.0）。只缩放 XY 的偏离量，Z 重新归一化。
		/// </summary>
		public double BumpStrength { get; init; } = 1.0;

		/// <summary>
		/// <c>$matcap</c> 的混合模式（NekoToon 编号）。<see cref="MaterialAppearance.MatcapBlendMode.Off"/>
		/// = 这个材质不用 matcap（源侧没写或写了 0）。
		/// </summary>
		public MaterialAppearance.MatcapBlendMode MatcapBlend { get; init; }

		/// <summary><c>$matcaptint</c> 的四个分量（逐通道强度 + alpha 整体强度）。</summary>
		public double[] MatcapTintRgba { get; init; } = [1, 1, 1, 1];

		/// <summary>
		/// <c>$mask1</c> 的贴图裸名。<b>它的 R 通道是 matcap 的逐像素强度</b>
		/// （NekoToon 官方手册：<c>r通道用于matcap蒙版，g通道是lightwarp的V映射，b通道是matcap2蒙版，a通道AO</c>）。
		/// </summary>
		public string? Mask1Texture { get; init; }

		/// <summary>
		/// <c>$MATCAP2</c> 的混合模式（第二层 matcap）。
		/// <para>
		/// <b>必须按模式判，不能按"有没有 `$MATCAP2TEXTURE`"判。</b>实测
		/// <c>mat_ornament_petal_shoes</c> 只写了图没写模式，源侧那张图<b>根本没参与渲染</b>；
		/// 照"有图就加"处理会把花瓣鞋凭空提亮一档。
		/// </para>
		/// </summary>
		public MaterialAppearance.MatcapBlendMode Matcap2Blend { get; init; }

		/// <summary><c>$MATCAP2TEXTURE</c> 的贴图裸名。</summary>
		public string? Matcap2Texture { get; init; }

		/// <summary>
		/// 这张法线图要不要翻 Y —— **OpenGL 约定**的法线图（文件名里带 <c>_opengl</c> 的那类）
		/// 绿通道方向与起源默认的 DirectX 相反，照原样接进去会让凹凸"看着是反的"。
		/// 判据来自文件名，与 <c>--normal-flip-y</c> 的显式开关做异或。
		/// </summary>
		public bool BumpFlipY { get; init; }

		/// <summary><c>$MATCAP2TINT</c> 的四个分量（缺省全 1.0）。</summary>
		public double[] Matcap2TintRgba { get; init; } = [1, 1, 1, 1];

		/// <summary>
		/// <c>$lightwarptexture</c> 的贴图裸名（光照曲线）。
		/// <para>
		/// HD2 没有 ramp 槽也没有逐像素光照，所以只能取它<b>受光端</b>的颜色当整片倍率
		/// ——实测这些 ramp 的受光段是 <c>(76,76,76)</c>，也就是"受光 = 底色 × 0.30"。
		/// 见 <see cref="MaterialAppearance.LightwarpFactor"/>。
		/// </para>
		/// </summary>
		public string? LightwarpTexture { get; init; }

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

	/// <summary>
	/// 这条材质的法线图**最终会怎么样**：没有声明 / 已接入 / 声明了但图不在。
	/// <para>
	/// <b>为什么不是一个 bool。</b>报告要说的是两句话，而不是一句：
	/// 「N 个材质声明了法线图，其中 X 个已接入 HD2 法线通道」和
	/// 「Y 个因为贴图不在来源里，仍然用平面法线」。合成一个 bool 就只能报前者，
	/// 用户看到"已接入"却依旧糊的时候没有任何线索。
	/// </para>
	/// <para>
	/// 逐条 notice 会淹没其它警告（NekoToon 的材质几乎每个都有 <c>$bumpmap</c>），
	/// 所以只提供查询，由装配器按**整包**统计成两行。
	/// </para>
	/// </summary>
	/// <param name="materialName">源材质的裸名（可带目录，会取 basename）。</param>
	public NormalDelivery DescribeBumpMap(string materialName)
	{
		var stem = Path.GetFileNameWithoutExtension(VmtDocument.NormalizePath(materialName));
		if (string.IsNullOrWhiteSpace(stem) || !source.TryReadVmt(stem, out var bytes))
			return NormalDelivery.None;

		string? bump;
		try
		{
			// 带 include 展开：法线常常写在被 include 的基材质里。
			bump = VmtDocument.Parse(bytes, source.CreateIncludeReader()).Value("$bumpmap");
		}
		catch (InvalidDataException)
		{
			return NormalDelivery.None;
		}

		if (string.IsNullOrWhiteSpace(bump))
			return NormalDelivery.None;

		var bumpStem = Path.GetFileNameWithoutExtension(VmtDocument.NormalizePath(bump));
		if (string.IsNullOrWhiteSpace(bumpStem))
			return NormalDelivery.TextureMissing;

		// **判据是"来源里有没有这张图"**，不是"能不能解出来"——解码失败要跑一次 texconv，
		// 在统计阶段做这件事会把整包扫描变成几分钟。真正的解码失败会走
		// `TryNormalRoughnessTexture` 的保底路径（退回平面法线），结果一样是不接法线。
		return source.ContainsVtf(bumpStem) ? NormalDelivery.Wired : NormalDelivery.TextureMissing;
	}

	/// <summary>见 <see cref="DescribeBumpMap"/>。</summary>
	public bool DeclaresBumpMap(string materialName) => DescribeBumpMap(materialName) != NormalDelivery.None;

	/// <summary>
	/// 为「源包里根本没有这个材质」的情形造一个**兜底灰材质**。
	/// <para>
	/// <b>为什么需要它。</b>装配器原来的做法是：模型引用了某个材质、而它没被任何 VPK 提供（源包
	/// 打包缺陷、或者引用了别的包里的材质而那个包没一起导入）⇒ 把这个材质下标当成
	/// "不绘制"，<b>连三角形一起剔掉</b>。结果是那个部件**整块消失**——用户看到的是
	/// "缺材质/部件没了"，而报告里只有一行"某材质没落地"。
	/// </para>
	/// <para>
	/// 按「不能缺失」的口径，正确做法是：<b>给一个看得见的中性灰材质</b>，让它上机是
	/// 一块哑光灰面（能立刻看出"这块没材质"），而不是整块不见。颜色不对是可诊断的，
	/// 部件消失是不可诊断的。
	/// </para>
	/// <para>
	/// 身份串用 <c>stem</c>（与真实材质同一套规则），所以同一个源材质无论在哪个 patch 里
	/// 兜底，算出的 id 都一致——模型 patch 引用它、材质 patch 产出它，两边对得上。
	/// </para>
	/// </summary>
	/// <param name="stem">源材质的裸名（<c>$basetexture</c> 之前那一层）。</param>
	public MaterialSynthesisResult SynthesizeFallbackMaterial(string stem)
	{
		const string templateName = "advanced";
		if (string.IsNullOrWhiteSpace(stem))
			return MaterialSynthesisResult.Failed("兜底材质需要一个非空的材质名");
		if (!templates.Contains(templateName))
			return MaterialSynthesisResult.Failed($"{stem}：材质模板库缺少 {templateName}，造不出兜底材质");

		var material = templates.Load(templateName);
		var textures = new List<SynthesizedTexture>();
		try
		{
			if (material.TextureIds.Count <= AdvancedAlbedoSlot
				|| material.TextureIds.Count <= AdvancedNarSlot
				|| material.TextureIds.Count <= AdvancedMetallicSlot)
			{
				return MaterialSynthesisResult.Failed($"{stem}：advanced 模板槽数不够，放不下兜底底色");
			}

			// 半亮中性灰。与"底图缺失时退成中性色"取同一个值（NonMetallicSurfaceF0），
			// 于是两种"没有真材质"的观感一致，用户不必区分是哪种原因。
			var grey = MaterialAppearance.LinearToSrgbByte(MaterialAppearance.NonMetallicSurfaceF0);
			// alpha = 0：`advanced` 槽 5 的 A 是**自发光**，不清零会让整件发光。
			var baseColor = BuildSolidTexture(stem + "_fallback", string.Empty, grey, grey, grey, 0);
			material.SetTexture(AdvancedAlbedoSlot, baseColor.Id);
			textures.Add(baseColor);

			// 法线与金属度用**全局共享**那一对（名字固定 → id 固定 → 装配器按 id 去重并成一条），
			// 于是兜底材质不会把包撑大。电介质而非金属：没有依据时不要给金属度。
			var flatNormal = BuildSolidTexture("shared_flat_normal_", string.Empty, 128, 128, 255, PbrRoughness);
			var sharedPbr = BuildSolidTexture("shared_pbr_", string.Empty, DefaultMetallic, DefaultMetallic, DefaultMetallic, 255);
			material.SetTexture(AdvancedNarSlot, flatNormal.Id);
			material.SetTexture(AdvancedMetallicSlot, sharedPbr.Id);
			textures.Add(flatNormal);
			textures.Add(sharedPbr);
		}
		catch (Exception exception) when (exception is InvalidDataException or IOException or InvalidOperationException)
		{
			return MaterialSynthesisResult.Failed($"{stem}：兜底材质造不出来——{exception.Message}");
		}

		var identity = BuildMaterialIdentity(stem, null, useAlphaClip: false);
		var id = DeriveMaterialId(identity);
		return new MaterialSynthesisResult(
			new SynthesizedMaterial(id, material.Write(), templateName, stem + "_fallback", material.TextureIds),
			textures,
			[])
		{
			Identity = new MaterialIdentity(id, templateName, stem + "_fallback", UseAlphaClip: false,
				MatcapTexture: null, MatcapBaked: false),
			Notices =
			[
				$"{stem}：源包里没有这个材质，已用**兜底灰材质**占位（哑光中性灰）——"
				+ "部件会显示成一块灰面，不会整块消失。这通常意味着源包缺文件，或它引用了别的包里的材质而那个包没有一起导入。",
			],
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
		SynthesizedTexture? translucentMap;
		try
		{
			(texture, opacityMask, translucentMap) = BuildBaseColorTexture(loaded);
		}
		catch (Exception exception) when (exception is InvalidDataException or IOException or InvalidOperationException)
		{
			return MaterialSynthesisResult.Failed($"{stem}：贴图 {textureName} 转换失败——{exception.Message}");
		}

		// ===== 实验路线 1：translucent 模板 =====
		// 槽只有 1 个（NormalXyRoughnessOpacity），没有反照率槽 ⇒ 颜色只能靠 BaseColor 平涂。
		if (alphaExperiment == AlphaExperiment.Translucent && loaded.UseAlphaClip)
		{
			if (translucentMap is null)
				return MaterialSynthesisResult.Failed($"{stem}：实验路线 1 需要不透明度贴图，但没有产出");
			if (material.TextureIds.Count == 0)
				return MaterialSynthesisResult.Failed($"{stem}：translucent 模板没有贴图槽");

			material.SetTexture(0, translucentMap.Id);
			// 平涂色取**源底色**的平均值（不是 `rgba`）：布料这种大面积渐变底色取平均后
			// 与整体观感接近。⚠️ 不能用 `rgba` —— matcap 承载表面的件那里已经是球面图，
			// 平均出来是 matcap 的色调，不是这块布的颜色。
			var average = AverageColor(loaded.SourceBaseRgba);
			if (!material.TrySetVector(TranslucentBaseColorVariableId, average[0], average[1], average[2]))
				return MaterialSynthesisResult.Failed($"{stem}：translucent 模板里找不到 BaseColor 变量");
			// OpacityBase 是整体不透明度倍率：源 alpha 已经在贴图里，这里给 1 不做二次衰减。
			material.TrySetScalar(TranslucentOpacityBaseVariableId, 1.0f);

			return new MaterialSynthesisResult(
				new SynthesizedMaterial(loaded.MaterialId, material.Write(), loaded.TemplateName, loaded.BaseTexture, material.TextureIds),
				[translucentMap],
				[])
			{
				Identity = loaded.ToIdentity(),
				Notices = [.. loaded.Notices, $"{stem}：实验路线 1（translucent 模板，平涂色 + 贴图 alpha）"],
			};
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
				// 有法线图就用它自己的，没有才用全局共享平面法线（见 TryNormalRoughnessTexture）。
				var flatNormal = TryNormalRoughnessTexture(loaded, PbrRoughness)
					?? BuildSolidTexture("shared_flat_normal_", string.Empty, 128, 128, 255, PbrRoughness);
				material.SetTexture(AlphaClipNormalSlot, flatNormal.Id);
				textures.Add(flatNormal);
			}

			material.SetTexture(AlphaClipOpacitySlot, opacityMask.Id);
			material.SetTexture(AlphaClipBaseColorSlot, texture.Id);
			textures.Add(opacityMask);

			// 关掉脏污/天气。`alphaclip` 模板的默认值是**满脏污**（`DirtGlobalAmount = 1`），
			// 于是镂空件在雪地会被雪图层整个盖住，而 `advanced` 的件不受影响。
			// 源侧模组没有"天气/脏污"这个概念，转换器应当关掉。
			if (!material.TrySetScalar(DirtGlobalAmountVariableId, 0f))
				return MaterialSynthesisResult.Failed(
					$"{stem}：alphaclip 模板里找不到 DirtGlobalAmount 变量，无法关闭天气/脏污");
			material.TrySetScalar(WeatheringCoverageVariableId, 0f);

			// B 档：只动阈值这一个变量（A 档保持模板默认 0.5）。
			if (cutoutStrategy == CutoutStrategy.AlphaClipTuned
				&& !material.TrySetScalar(OpacityThresholdVariableId, TunedOpacityThreshold))
			{
				return MaterialSynthesisResult.Failed(
					$"{stem}：alphaclip 模板里找不到 OpacityThreshold 变量，无法把阈值调到 {TunedOpacityThreshold}");
			}
		}
		else if (loaded.UseAlphaClip && IsOriginalTemplate(loaded.TemplateName))
		{
			// ===== `original`：本体角色材质那套 13 槽 =====
			//
			// 换它的**唯一理由**：`alphaclip` 一个天气/雪槽都没有，于是镂空件在雪地会被雪图层
			// 整个盖住；`original` 有 `WeatheringSpecial`（掩码）+ `SubsurfaceOpacity`
			// （逐像素不透明度）。代价仍然是没有金属度（13 槽里没有 `IlluminateData`）。
			//
			// ⚠️ 三个槽位映射是**待实机验证的假设**，见 OriginalBaseColorSlot 的注释。
			if (opacityMask is null)
				return MaterialSynthesisResult.Failed($"{stem}：判定要镂空，但没有产出遮罩贴图");
			if (material.TextureIds.Count <= OriginalBaseColorSlot)
				return MaterialSynthesisResult.Failed(
					$"original 模板只有 {material.TextureIds.Count} 个槽，放不下下标 {OriginalBaseColorSlot} 的底色");

			// 槽 5 `Normal`：RGB 法线。有法线图就用它自己的，没有才用全局共享平面法线
			// （名字相同 → id 相同 → 装配器按 id 去重）。
			var flatNormal = TryNormalRoughnessTexture(loaded, PbrRoughness)
				?? BuildSolidTexture("shared_flat_normal_", string.Empty, 128, 128, 255, PbrRoughness);
			material.SetTexture(OriginalNormalSlot, flatNormal.Id);
			textures.Add(flatNormal);

			material.SetTexture(OriginalOpacitySlot, opacityMask.Id);
			material.SetTexture(OriginalBaseColorSlot, texture.Id);
			textures.Add(opacityMask);

			// 遮罩是二值（硬裁或抖动），所以锐度取 1.0、偏移 0.0 就能原样通过。
			// 本体那份实例是 0/0 —— 它不靠这个槽做镂空，所以那两个值对我们没有参考价值。
			if (!material.TrySetScalar(OpacitySharpnessVariableId, 1.0f))
				return MaterialSynthesisResult.Failed(
					$"{stem}：original 模板里找不到 OpacitySharpness 变量");
			material.TrySetScalar(OpacityOffsetVariableId, 0.0f);
		}
		else
		{
			if (material.TextureIds.Count <= AdvancedAlbedoSlot)
				return MaterialSynthesisResult.Failed(
					$"advanced 模板只有 {material.TextureIds.Count} 个槽，放不下下标 {AdvancedAlbedoSlot} 的底色");
			material.SetTexture(AdvancedAlbedoSlot, texture.Id);

			// ===== 实验路线 2：advanced + SclarFieldOpacity =====
			// 想知道的是：advanced 会不会**混合**，还是只做裁切。
			// 遮罩走它自己那个"不透明度场"槽；OpacityOffset 保持模板默认（0 = 不额外偏移）。
			if (alphaExperiment == AlphaExperiment.AdvancedOpacity && loaded.UseAlphaClip && opacityMask is not null)
			{
				if (material.TextureIds.Count <= AdvancedOpacityFieldSlot)
					return MaterialSynthesisResult.Failed(
						$"{stem}：advanced 模板只有 {material.TextureIds.Count} 个槽，放不下 SclarFieldOpacity");
				material.SetTexture(AdvancedOpacityFieldSlot, opacityMask.Id);
				textures.Add(opacityMask);
				material.TrySetScalar(AdvancedOpacityOffsetVariableId, 0.0f);
			}

			// 槽 2（Nar = RGB 法线 / A 粗糙度）与槽 3（金属度）也要写 ——
			// 用的是**全局共享**的一对（`fix_materials.py:25-27` 的 shared_flat_normal / shared_pbr），
			// 不是每材质各一张：普通件的法线与金属度本来就不随材质变，各写一张只会把包撑大。
			// 名字固定 → id 固定 → 装配器按 id 去重会并成一条 ✓
			//
			// ⚠️ 默认金属是**近黑 (6,6,6)**；191 属于 matcap 那条路（观感来自 matcap 时
			// 才需要把金属度顶上去）。两者语义不同，不能混。
			//
			// ⚠️ **判据必须是「不是金属」，不能是「matcap 不是表面」。**
			//
			// 原来写的是 `!MatcapIsSurface`，于是出现了第三种情况**没人管**：
			//   `MatcapIsSurface == true` 且 `MatcapIsMetal == false`
			// —— 也就是宝石 / 玻璃 / 珍珠这些"底色被顶掉、但不该给金属度"的件。
			// 它们既不进这一支（因为 surface 为真），也不进下面的金属分支（因为不是金属），
			// 于是槽 2 与槽 3 **保持模板默认值**，两个 PBR 输入是游戏自己定的未知数。
			// 这正是"玻璃鞋跟变成金属/塑料"那条现象的机制之一。
			//
			// 现在按"是不是金属"二分，两种情况都有明确赋值，不留空洞。
			if (!loaded.MatcapIsMetal
				&& material.TextureIds.Count > AdvancedNarSlot
				&& material.TextureIds.Count > AdvancedMetallicSlot)
			{
				// 有 `$bumpmap` 就用**这个材质自己的**法线图；没有才用全局共享的平面法线。
				var normal = TryNormalRoughnessTexture(loaded, PbrRoughness);
				var flatNormal = normal ?? BuildSolidTexture("shared_flat_normal_", string.Empty, 128, 128, 255, PbrRoughness);
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
			if (loaded.MatcapIsMetal)
			{
				// 金属度/粗糙度**跟着 matcap 档位走** —— 这是"黄紫"那个 bug 的修法。
				//
				// · Metal（发布版）：底色已被 `FlattenToMetalF0` 涂成一块**中性平色 F0**，
				//   所以必须把金属度顶到 1.0，让 PBR 用环境反射把高光算出来。
				// · Albedo：**已证伪，留着只为留档。** 它把 matcap 那张图直接当底色，
				//   而 matcap 的 UV 是**视角法线**、不是模型 UV —— 模型 UV 采到的是球面上
				//   一块随机区域（实测鞋底正好落在暗部），实机观感是"粗糙的生铁，完全无反光"。
				//   所以这里按电介质处理，至少不会因为金属度顶满而把随机色放大成黄紫。
				var metallicValue = matcapMode == MatCapMode.Metal
					? MaterialAppearance.MetallicValue
					: DefaultMetallic;
				var roughnessValue = matcapMode == MatCapMode.Metal
					? MetalRoughness
					: PbrRoughness;

				var metallic = BuildSolidTexture(textureName, "_metallic", metallicValue, metallicValue, metallicValue, 255);
				var normalRoughness = TryNormalRoughnessTexture(loaded, roughnessValue)
					?? BuildSolidTexture(textureName, "_nar", 128, 128, 255, roughnessValue);
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
		//
		// ⚠️ 判据是「开关为真」，不是「值非空」。
		// 写 `$no_draw "0"` 是**明确打开绘制**，按"非空即跳过"会把它当成不要画，
		// 于是那个部件连几何一起消失——一个本该显示的部件静默不见，
		// 在游戏里没有任何线索指向"是 $no_draw 判反了"。
		if (vmt.IsEnabled("$no_draw"))
		{
			reason = $"{stem}：源材质声明 $no_draw（源侧不绘制），跳过";
			return LoadOutcome.Skipped;
		}

		// 收敛成非 null：`HasNoRealBaseTexture` 把"缺失"和"占位值"都算作没有底色，
		// 所以下面的合成分支一定会给它赋一个真值。用 `?? string.Empty` 是为了让
		// 可空性在类型上闭合，而不是在每处调用点加 `!`。
		var baseTexture = vmt.BaseTexture ?? string.Empty;
		var verdict = MaterialAppearance.Evaluate(vmt);

		// 源侧写在 VMT 里的平涂底色（`$color`）。见 MaterialAppearance.TryParseFlatColor：
		// 这一键是"没有底色图"那类材质**唯一**的颜色线索，也是下面合成底色的首选颜色。
		var declaredFlatRgb = MaterialAppearance.TryParseFlatColor(
			vmt.Value("$color") ?? vmt.Value("$color2"),
			out var flatRed, out var flatGreen, out var flatBlue)
			? new[] { flatRed, flatGreen, flatBlue }
			: null;

		// matcap 贴图名要留到身份里：**它的内容可能来自别的 VPK**，而 id 只由名字决定。
		// 装配器据此报警——否则"换个 patch 就能换金属色"是个假承诺。
		var matcapTextureName = string.IsNullOrWhiteSpace(vmt.MatCapTexture)
			? null
			: Path.GetFileNameWithoutExtension(VmtDocument.NormalizePath(vmt.MatCapTexture));

		// 法线图：同样只留裸名（`$bumpmap "path\Mat_Underwear_N"` → `Mat_Underwear_N`）。
		//
		// **这一条以前完全没有接线。** 所有材质写进 HD2 的都是平面法线（128,128,255），
		// 而 NekoToon 那套模型普遍带 ATI2N/BC5 高清法线图（官方文档 `pbr.vmt` /
		// `nekotoon.vmt` 都写着 `$bumpmap` + `$bumpstrength`）。现在它在装配时决定
		// "这张材质要不要配一张自己的法线贴图"，见 `TryBuildNormalTexture`。
		var bumpTextureName = string.IsNullOrWhiteSpace(vmt.Value("$bumpmap"))
			? null
			: Path.GetFileNameWithoutExtension(VmtDocument.NormalizePath(vmt.Value("$bumpmap")!));
		var bumpStrength = MaterialAppearance.ParseBumpStrength(vmt.Value("$bumpstrength"));

		// matcap 的**合成参数**（N2）：模式 + 逐通道强度 + 蒙版。
		//
		// 以前只把 `$matcap` 当成"有/没有 matcap"的布尔——模式、tint 的 alpha、`$mask1`
		// 全部丢弃，于是那层亮光要么被压成一个平均色、要么被整片涂平。
		// 源侧的实际公式（lilToon / NekoToon）见 MaterialAppearance.CompositeMatcap。
		var matcapBlend = MaterialAppearance.ParseMatcapBlend(vmt.Value("$matcap"));
		var matcapTintRgba = MaterialAppearance.ParseTintRgba(vmt.Value("$matcaptint"));
		var mask1Texture = string.IsNullOrWhiteSpace(vmt.Value("$mask1"))
			? null
			: Path.GetFileNameWithoutExtension(VmtDocument.NormalizePath(vmt.Value("$mask1")!));

		// 第二层 matcap（`$MATCAP2`）。**判据是模式**，不是"有没有写贴图"——
		// 官方手册里这一层是加法叠加，通常比第一张更亮，负责把金属推到"镀铬"。
		// 它的逐像素强度由 `$mask1` 的 **B** 通道控制（R 是给第一层的）。
		var matcap2Blend = MaterialAppearance.ParseMatcapBlend(vmt.Value("$MATCAP2"));
		var matcap2Texture = string.IsNullOrWhiteSpace(vmt.Value("$MATCAP2TEXTURE"))
			? null
			: Path.GetFileNameWithoutExtension(VmtDocument.NormalizePath(vmt.Value("$MATCAP2TEXTURE")!));
		var matcap2TintRgba = MaterialAppearance.ParseTintRgba(vmt.Value("$MATCAP2TINT"));

		// 光照曲线（`$lightwarptexture`）。它的**受光端**就是 NekoToon 里那个著名的"×0.30"上限。
		var lightwarpTexture = string.IsNullOrWhiteSpace(vmt.Value("$lightwarptexture"))
			? null
			: Path.GetFileNameWithoutExtension(VmtDocument.NormalizePath(vmt.Value("$lightwarptexture")!));

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
		// 合成底色要用的 RGB（0..255）；null = 用默认中性灰。
		//
		// 之所以要三通道而不是一个字节：源侧有一类材质把颜色写在 `$color` 里
		// （`$basetexture "vgui/white"` + `$color 0.462 0.322 0`），那是**带色相**的颜色。
		// 只留灰度就只能得到"一块灰"——实测 milltina 的银 / 金 / 宝石红会变成同一块灰。
		byte[]? syntheticBaseRgb = null;
		if (HasNoRealBaseTexture(vmt))
		{
			if (string.IsNullOrWhiteSpace(matcapTextureName))
			{
				// 被别的 VMT include 了 = 它只是给下游补参数的模板，真正的底色在引用它的那份里。
				// 这类材质不出现在模型的材质名里，跳过而不是报"合成失败"；
				// 但模型直接引用它时不能跳——那种情况"没有底色"是真失败。
				if (includeTargets.Value.Contains(stem) && !IsReferencedByModel(stem))
				{
					reason = $"{stem}：include 链的中间层（真正的底色由引用它的那份 VMT 用 replace 写），跳过";
					return LoadOutcome.Skipped;
				}

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
			// 这一支的底色图本来就不存在，`$color` 是唯一的颜色线索。
			// matcap 顶掉底色时用不上它（顶掉后底色只是 F0 载体），但 matcap **也缺失**时
			// 它就是全部——实测 milltina 的 6 个金属件同时缺这两样。
			syntheticBaseRgb = declaredFlatRgb;
		}

		var textureName = syntheticBase
			? stem + "_matcapbase"
			: Path.GetFileNameWithoutExtension(baseTexture);

		// 底图缺失时的提示，等 `notices` 建好之后再塞进去（它声明在后面）。
		string? missingBaseNotice = null;

		if (!syntheticBase && !source.TryReadVtf(textureName, out _))
		{
			// ⚠️ **底图找不到不再整块失败**——退成中性色，并大声报告。
			//
			// 原来的行为是 `return LoadOutcome.Failed`，于是这个材质在 patch 里根本不存在，
			// 模型引用的 section 找不到材质 → 游戏里显示**品红**。实测代价：MourningVeil 的
			// B3（服装色白紫）包里 `mat_glovepreal*` 三个材质的 `$basetexture` 写的是 `black`，
			// 而 `black.vtf` **在任何包里都不存在**（B1/B2 用的是 `Mat_GloveCloth`）——
			// 那是源包的打包缺陷，但用户看到的是"右臂珍珠臂环中心一块品红"。
			//
			// 对转换器来说，**一块颜色不对但看得见的材质，永远好过一块品红**。
			// 所以这里退成中性色继续走，并把这件事写进 warnings（管理器会展示）。
			//
			// 颜色优先级：**作者的 `$color` > 图片名（`black`/`white`/`gray`）> 中性灰**。
			// `$color` 排第一是因为它是唯一"作者明确写下的颜色"；图片名只是文件名，只有在
			// `$color` 缺席时才有意义。实测 MourningVeil B3 引用的是不存在的 `…/black`（要纯黑），
			// 而 milltina 的金属件引用的是 `vgui/white` 并另写 `$color`（要银/金/宝石红）——
			// 两者都必须对，所以不能只按名字猜。
			var namedGrey = MaterialAppearance.TryParseFlatColourName(textureName);
			syntheticBase = true;
			baseTexture = SyntheticBaseName(stem);
			syntheticBaseRgb = declaredFlatRgb
				?? (namedGrey is { } grey ? new[] { grey, grey, grey } : null);
			missingBaseNotice =
				$"{stem}：底图 {textureName}.vtf 在材质来源里找不到（源包缺这张图），"
				+ (syntheticBaseRgb is { } rgb
					? $"已按平涂色 ({rgb[0]},{rgb[1]},{rgb[2]}) 处理"
						+ $"（来源 {(declaredFlatRgb is null ? "图片名" : "$color")}）"
					: $"已退成中性色 ({MaterialAppearance.LinearToSrgbByte(MaterialAppearance.NonMetallicSurfaceF0)})")
				+ " —— 游戏里不会显示品红，但颜色不一定对。";
		}

		// 先解**底色**：既要它的像素（铸进 patch），也要它的 alpha 与亮度来判定
		// "matcap 是不是这个表面本身"。判定必须看内容，不能只看 tint —— 见下面的注释。
		VtfFile vtf;
		byte[] rgba;
		AlphaStats baseAlpha;
		// **源底色的 alpha 到底存不存在。**
		//
		// 合成的中性底色是"没有源图"的替补，它的 alpha 是我们自己填 0 —— 那**不是**信息，
		// 不能拿去参与镂空判定。曾经用 `SolidRgba(..., 0)` 造底色，于是
		// `Spread 0 < 3 ∧ Max 0 ≤ 200` 让 `IsUniformlyTranslucent` 恒为真，
		// **任何"底图缺失 + 写了 $alphatest 1"的材质都被强制切成 alphaclip**，
		// 即使它是金属件（金属件没有 IlluminateData 槽，切过去就既没镂空也没金属）。
		// 旧注释写的是"这样不会被误判成 alphaclip"，与实际行为正好相反。
		//
		// 语义上正确的区分是四种状态，不是两种：
		//   ① 源图在、alpha 有形状     → 镂空（IsMeaningful）
		//   ② 源图在、alpha 均匀 < 255 → 整体半透明（IsUniformlyTranslucent）
		//   ③ 源图在、alpha 恒 255     → 不透明
		//   ④ **源图不在**             → 不知道（≠ 半透明）
		// 这条布尔量就是 ④ 与 ①②③ 的分界线。
		var hasSourceOpacity = !syntheticBase;
		if (syntheticBase)
		{
			// 4×4 平色。尺寸取 4×4 而不是 1×1：占位判据是"长宽都 ≤ 4"，两条都满足，
			// 而 4×4 让下游的 DDS / BC7 编码走正常路径。
			// alpha 置 0 只是为了让"这张图不承担形状"这件事在数据上显式，
			// **判定不读它**（见上面的 hasSourceOpacity）。
			var neutralBase = MaterialAppearance.LinearToSrgbByte(MaterialAppearance.NonMetallicSurfaceF0);
			vtf = VtfFile.Synthetic(4, 4);
			rgba = syntheticBaseRgb is { } baseRgb
				? MaterialAppearance.SolidRgba(4, baseRgb[0], baseRgb[1], baseRgb[2], 0)
				: MaterialAppearance.SolidRgba(4,
					neutralBase, neutralBase, neutralBase, 0);
			baseAlpha = TextureAlpha.Measure(rgba);
		}
		else
		{
			source.TryReadVtf(textureName, out var vtfBytes);
			try
			{
				vtf = VtfFile.Read(vtfBytes);
				// ⚠️ 量 alpha 必须在清零之前（清零发生在编码那一步，见 BuildBaseColorTexture）。
				rgba = DecodeTextureCached(textureName, vtfBytes, vtf);
				baseAlpha = TextureAlpha.Measure(rgba);
			}
			catch (Exception exception) when (exception is InvalidDataException or IOException or InvalidOperationException)
			{
				reason = $"{stem}：贴图 {textureName} 读不出来——{exception.Message}";
				return LoadOutcome.Failed;
			}
		}

		// **源底色的像素与尺寸必须在这里定格。**
		//
		// 下面的 matcap 分支会把 `rgba` / `vtf` **整份换成 matcap 的图像**
		// （它只改引用，不动原数组），变的是这两个变量、不是底色数据本身。
		// 而镂空遮罩要的恰恰是**源底色的 alpha** —— 曾经直接从 `rgba` 取，
		// 于是 matcap 承载表面的件拿到的是那张球面图的 alpha（通常全不透明）
		// ⇒ 判定说"要镂空"、遮罩却是全白 ⇒ **镂空静默失效**。
		//
		// 同一个变量两种语义（"底色像素" vs "形状像素"）是这条链上最容易复发的一类缺陷，
		// 所以名字上就分开：`rgba` = 会被 matcap 换掉的**渲染用底色**，
		// `sourceBaseRgba` = 永远指向源底色的那一份（镂空遮罩、平涂色都从这里取）。
		// 尺寸也必须一起定格：matcap 常是 256²，底色可能 2048²。
		var sourceBaseRgba = rgba;
		var sourceBaseWidth = vtf.Width;
		var sourceBaseHeight = vtf.Height;

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
		// 金属：**按名字**判，与底色亮度无关。
		// 曾经用"底色近黑"当判据，实测同一条 mat_metal 在 B1（黑）是 17.2、B3（白紫）是 128.4
		// —— 它测的是颜色变体的属性，不是材质的属性。详见 MaterialAppearance.IsMetallicMatcapName。
		//
		// ⚠️ **第二个参数不是可选的装饰。** 判据先看 matcap 名；当它说不出所以然时（例如
		// 素材把金属 matcap 命名成 `11`），退回源材质名。实测 Lunalice 的 21 个
		// `ornament_metal_*` 正是因为 matcap 叫 `11` 而**全部**判成非金属，
		// 而它们的模板是 advanced（本来给得了金属度）。
		var matcapIsMetal = verdict.MatCapIsTheSurface
			&& MaterialAppearance.IsMetallicMatcapName(matcapTextureName, stem);

		// "matcap 承载表面"= 底色该被顶掉。
		// Metal 档：金属件、没有底色的件、以及**底色根本没有颜色**（占位图/近黑）的件。
		//   最后那条不能省：Lilium 的钻石件（`mat_diamond_*_tiara`）matcap 名字是宝石系 →
		//   非金属，但底色是 1×1 的 `m_metal`(15,15,15)——不顶掉就是**纯黑**。
		//   非金属的顶掉用中性浅灰（见 NonMetallicSurfaceF0），不给金属度。
		// 其它档沿用"底色有没有颜色"这条老判据。
		var matcapIsSurface = matcapMode == MatCapMode.Metal
			? matcapIsMetal || syntheticBase
				|| (verdict.MatCapIsTheSurface
					&& MaterialAppearance.IsContentFreeBase(vtf.Width, vtf.Height, rgba))
			: syntheticBase
				|| (verdict.MatCapIsTheSurface
					&& MaterialAppearance.IsContentFreeBase(vtf.Width, vtf.Height, rgba));

		// 分类诊断：`HD2MOD_MATCAP_DEBUG=1` 时把判定输入打到 stderr。
		//
		// 存在的理由：金属/玻璃/布料这三类的判定全是从像素统计**猜**出来的（NekoToon 没有
		// 金属度输入），一旦猜错，游戏里的表现只是"看着不对"，没有任何线索。有了这一行，
		// "哪个材质判错了、因为哪一条"当场可查。
		if (Environment.GetEnvironmentVariable("HD2MOD_MATCAP_DEBUG") is { Length: > 0 })
		{
			var pixels = Math.Max(1, rgba.Length / 4);
			double lumSum = 0;
			for (var offset = 0; offset < rgba.Length; offset += 4)
				lumSum += (0.299 * rgba[offset]) + (0.587 * rgba[offset + 1]) + (0.114 * rgba[offset + 2]);
			Console.Error.WriteLine(
				$"[matcap] {stem,-34} base={textureName,-20} {vtf.Width}x{vtf.Height} "
				+ $"meanLum={lumSum / pixels,6:F1} tint={verdict.Tint:F2} "
				+ $"alpha=[{baseAlpha.Min},{baseAlpha.Max} cov={baseAlpha.Coverage:P1}] "
				+ $"synthetic={syntheticBase} contentFree={MaterialAppearance.IsContentFreeBase(vtf.Width, vtf.Height, rgba)} "
				// matcap 的名字必须打出来：金属/宝石的判定**只由它决定**，
				// 而它常常和材质名完全不同（实测 `mat_metal_gem_earings` 用的是另一张 matcap）。
				// 没有这一列，"这个件为什么没被当成金属"就只能靠猜。
				+ $"matcap={matcapTextureName ?? "<无>"} "
				+ $"=> metal={matcapIsMetal} matcapIsSurface={matcapIsSurface}");
		}

		// ⚠️ **只要判成"matcap 承载表面"，Metal 档就整片换成中性 F0 —— 不再按"是不是占位图"分叉。**
		//
		// 曾经分叉过：只有 `syntheticBase` / 占位图才换，其余保留源底色。理由是"换掉就等于
		// 布料丢掉自己的颜色"。但那个理由依赖 `IsContentFreeBase` 把"纯色"也算作没内容——
		// 那条已经删掉了（纯色仍然是一个颜色，见 `IsContentFreeBase` 的注释）。
		//
		// 现在 `IsContentFreeBase` 只剩"占位图 ∨ 近黑"，两者都**没有颜色可保**：
		// 近黑的底色本来就是"颜色来自 matcap"，保留它反而会得到一块黑金属。
		// 分叉留下的实际后果是**不一致**：判成金属却保留了源底色，金属度顶到 1.0
		// 打在一块有内容的底色上，观感介于"金属"和"塑料"之间，最难查。
		double[]? matcapMean = null;
		var notices = new List<string>();
		if (missingBaseNotice is not null)
			notices.Add(missingBaseNotice);

		// ── 先算一次"这个材质最终会用哪个模板"，以及那个模板能不能表达金属度 ────────
		//
		// **为什么必须提前算。** 下面的 matcap 分支会**整份换掉 `rgba`**（换成 matcap 图像）。
		// 换掉之后底色就只剩"F0 载体"这一个用途了 —— 只有在模板**能写金属度**时才有意义。
		// 金属度写不进去时换掉底色，等于把作者画的那张图换成一张球面图：
		// 既没有金属（没槽），也丢了自己的颜色。
		//
		// ⚠️ **上一版就栽在这里。** 当时的写法是"金属做不到就不涂 F0"，但执行顺序上
		// `rgba` 早已被换成 matcap 图像，于是"保留源底色"实际保留的是**那张金属球面图**
		// （亮度与亮银接近）—— 用户实测"MourningVeil 无变化"，因为确实几乎没变。
		// 修正办法不是调数值，而是把顺序理顺：**先知道模板能不能表达金属，再决定要不要换底色**。
		var provisionalUseAlphaClip = ShouldUseAlphaClip(vmt.WantsTransparency, hasSourceOpacity, baseAlpha);
		if (provisionalUseAlphaClip && matcapIsMetal && metalCutout == MetalCutoutPolicy.MetalWins)
			provisionalUseAlphaClip = false;   // MetalWins 会放弃镂空、回到 advanced
		var provisionalTemplate = ResolveTemplate(provisionalUseAlphaClip);
		var metalDeliverable = templates.Contains(provisionalTemplate)
			&& templates.HasTextureSlot(provisionalTemplate, MetallicSlotKey);

		// 源侧声明了、但 HD2 表达不了的特性（双面 / 自发光 / 高光反射 / 细节层 …）。
		//
		// 这一条不改变任何产物，只是把"为什么看着不一样"接上因果：这些键此前要么
		// 读了没人用、要么根本没读，而用户看到的是"内侧不见了/没有光泽/不发光"，
		// 报告里却一个字都没有 —— 那种情况下唯一的出路就是猜。
		if (MaterialAppearance.DescribeUnexpressibleFeatures(vmt) is { } unexpressible)
			notices.Add($"{stem}：{unexpressible}");

		// ⚠️ **Metal 模式只对"没有真底色"的材质生效**（`syntheticBase`）。
		//
		// 原来写成 `matcapMode is Albedo or Metal`，于是 metal 模式会把**所有** matcap 承载表面
		// 的材质换掉底色——包括那些底色是一张"纯色但真实"的布料的材质（`IsContentFreeBase`
		// 把均匀色也算作"无内容"）。结果是布料丢掉自己的贴图。
		//
		// 现在的分工：
		//   · Albedo（发布版行为）：matcap 承载表面 → 拿 matcap 当底色
		//   · Metal：只有**没有 $basetexture** 的件才归一成金属 F0，有底色的布料保持原样
		//
		// ⚠️ **金属件还多一条闸：模板写不进金属度时，不许换底色。**
		// 换成 matcap 图像之后它既没有金属（没槽）也丢了自己的颜色，是两头不讨好；
		// 保留源底色至少颜色是作者画的那个。
		// ⚠️ **"没有 matcap 可换"不是失败，只是换不成。**
		//
		// 这一支原来直接 `return LoadOutcome.Failed`，于是材质在 patch 里根本不存在，
		// 模型引用的那一格找不到材质 —— 用户看到的是**白模 / 品红**，报告里写的却是
		// "判定 matcap 承载表面，但 VMT 里没有 $matcaptexture"。
		//
		// 实测代价：milltina 的 6 个金属件（`$basetexture "vgui/white"` + `$color` +
		// `$phong 1` + `$envmap`）全落在这一支 —— `vgui/white` 是**引擎内置图**，
		// 任何 VPK 里都没有它，于是走了"合成底色"这条路，被判成 matcap 承载表面，
		// 而 VMT 里压根没有 matcap。整批导入后这几个件只剩兜底材质。
		//
		// 但**合成底色本身就是一个可用的颜色**（`$color` 或中性灰），并没有东西需要被顶掉。
		// 所以这里改成"保留底色继续走"，与上面"底图缺失不整块失败"同一条口径：
		// 一块颜色不一定对的材质，永远好过一块白模。
		var takesMatcapAsBase = matcapIsSurface
			&& matcapMode is MatCapMode.Albedo or MatCapMode.Metal
			&& (!matcapIsMetal || metalDeliverable);
		if (takesMatcapAsBase && string.IsNullOrWhiteSpace(matcapTextureName))
		{
			notices.Add(
				$"{stem}：既没有 $basetexture 也没有 $matcaptexture —— 没有东西可以顶掉底色，"
				+ "已按合成底色继续（颜色来自 $color 或中性灰）。");
		}
		else if (takesMatcapAsBase)
		{
			// matcap 当底色：真正决定观感的是 matcap 那张图，不是底色
			// （实测 A1 的 `Ornament_metal_Underwear.vtf` 是 1×1 占位图）。
			// 贴图 id 于是变成 `hash(matcap 名)` —— 它由**哪个 VPK 提供**就归哪个 patch，
			// C1/C2 这类"只换 matcap"的包成了真正可换的 patch。
			//
			// ⚠️ **拿不到 matcap 的图也不算失败。**
			// 金属档只需要 matcap 的"色相"，整片亮度会被归一成 F0——那张图缺了，
			// 退成中性金属就好。判失败会让整个部件丢掉材质（现象就是"部件不见了"），
			// 而"中性金属"至少是对的颜色。实测 serenade 的 `jem_red` / `jem_red_earring`
			// 引用的 `m7` 就不在包里。
			//
			// 非金属档（Albedo）改成同一条口径：保留已经解出来的底色继续走。
			// 判失败换来的只是"整件退回外壳护甲材质"，那不是更忠实，那是什么都没有。
			//
			// 上面那条 `if` 已经把"没有 matcap 名"排掉了，所以这里可以把它固定成非空局部量。
			var matcapName = matcapTextureName!;
			var matcapAvailable = source.TryReadVtf(matcapName, out var matcapBytes);
			if (!matcapAvailable)
			{
				notices.Add(
					$"{stem}：matcap 贴图 {matcapName}.vtf 在材质来源里找不到，"
					+ "底色按现有的（源底色或合成底色）处理。");
			}
			else
			{
				try
				{
					vtf = VtfFile.Read(matcapBytes);
					rgba = DecodeTextureCached(matcapName, matcapBytes, vtf);
					// ⚠️ **不要重新测 `baseAlpha`。** 它描述的是**源底色**的 alpha，
					// 而下面 `useAlphaClip` 正是靠它决定"这个件要不要镂空"。
					// 曾经在这里 `baseAlpha = TextureAlpha.Measure(rgba)` —— 那一行把它换成了
					// **matcap 的** alpha（一张不透明的球面图），于是镂空判定恒为 false，
					// 所有"靠 alpha 镂空的金属件"都被判成 advanced：既没有镂空也没有金属。
					// 实测 MourningVeil 的金属饰品全是这种写法。
					// ⚠️ 贴图名要**加后缀**，不能沿用 matcap 的名字。
					//
					// 原因：贴图 id 由名字派生，而"归谁产出"由名字**是不是本 VPK 带的 VTF** 决定。
					// 沿用 matcap 名 → 这张图会被判成"由别的 VPK 提供，归那个 patch" → 本 patch 不写它；
					// 而那个 VPK 又因为"自己没材质用这张图"也不写它 → **两边都不产出**，
					// 材质在游戏里没有底色。
					//
					// 实测代价：MourningVeil 的 `mat_gem1` 底色指向 `matcap_jewel7`（由 A1 提供），
					// 用它的是 B1 的材质 —— 结果那张贴图谁都没产出，手臂上的金属装饰**整块丢了材质**。
					//
					// 加后缀 = 声明"这是本材质派生出来的图"，于是它一定跟着材质走。
					textureName = matcapTextureName + MetalF0Suffix;
				}
				catch (Exception exception) when (exception is InvalidDataException or IOException or InvalidOperationException)
				{
					// 读不出来就用现有的（源或合成）底色。判失败会让整件丢掉材质，
					// 而"颜色不一定对的金属"远好过"部件不见了"。
					notices.Add(
						$"{stem}：matcap 贴图 {matcapTextureName} 读不出来（{exception.Message}），"
						+ "底色按现有的处理。");
				}
			}

			// ⚠️ **第二张 matcap（`$MATCAP2 2` + `$MATCAP2TEXTURE`）刻意不参与计算。**
			//
			// 它曾经被实现过：按源侧 `$MATCAP2 2`（加法）把第二张叠到第一张上，再取平均色
			// 算色相——这样"金色系 / 银色系"（C1/C2）就真的能换出金/银两种金属。
			//
			// 但那是**错误的通用性**：`11.vtf` / `Matcap_diamond4.vtf` 由 C1/C2 提供，
			// 而 C1/C2 是"可选的颜色变体包"。于是同一份源，装哪个变体包金属色就不同，
			// 转换结果不再是源的函数。详见 `MaterialAppearance.FlattenToMetalF0` 的注释。
			//
			// 现在的做法：金属色是常量（中性银）。`$MATCAP2` 只作为"源侧语法"被
			// `VmtDocument` 解析出来留档，不参与任何像素计算。
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
			// ⚠️ **金属件现在也会走 alphaclip**（曾经在这里被强行关掉）。
			//
			// 当时关掉的理由是"Metal 模式下底色会被整片归一成 F0，源侧的 alpha 不再有意义"。
			// 现在不再一刀切：**镂空和金属在 HD2 里无法共存**（见 <see cref="MetalCutoutPolicy"/>），
			// 所以取舍交给 `--metal-cutout`：默认保镂空，可选保金属。
			// 实测 MourningVeil 的金属饰品全是 `$alphatest 1` + alpha 镂空，
			// 一刀切成 advanced 就等于既没有镂空也没有金属——那是最坏的一种。
			useAlphaClip = ShouldUseAlphaClip(vmt.WantsTransparency, hasSourceOpacity, baseAlpha);

			// 源侧要透明、但源图不在 ⇒ 我们**做不到**镂空。这必须说出来：
			// 否则用户看到的是"这件该透的地方是实心的"，而报告里一个字都没有。
			if (!useAlphaClip && !hasSourceOpacity && vmt.WantsTransparency)
			{
				notices.Add(
					$"{stem}：源侧要透明（$alphatest/$translucent），但底色贴图不在来源里 —— "
					+ "做不出镂空遮罩，已按不透明处理（保住底色与金属度，不会整片消失）。");
			}

			// "既要镂空又是金属"只能二选一：选 MetalWins 就放弃镂空、走 advanced 换回金属度。
			if (useAlphaClip && matcapIsMetal && metalCutout == MetalCutoutPolicy.MetalWins)
			{
				useAlphaClip = false;
				notices.Add(
					$"{stem}：既是金属又要镂空，HD2 的模板无法同时表达"
					+ "（advanced 有金属度没镂空、alphaclip 有镂空没金属度），"
					+ "按 --metal-cutout metal 选择**保金属**，镂空已放弃。");
			}
		}

		// 半透明落地方式的说明。**必须说出来**——否则用户只会看到"袖套没透明"，
		// 或者更糟：以为我们又把东西弄丢了。
		//
		// 现在的做法是抖动（见 DitherAlpha）：源侧的渐变 alpha 被抖成二值点阵，
		// 远处由 TAA 累积成等效透明度。实机 A/B 已确认观感与源侧薄纱一致。
		if (useAlphaClip && vmt.WantsTranslucency && !vmt.WantsAlphaTest)
		{
			notices.Add(
				$"{stem}：源侧声明 $translucent（alpha 混合半透明），HD2 没有混合模板，"
				+ "已按**抖动透明**处理（点阵近似）——远处观感接近，近看有细密噪点。");
		}

		var templateName = ResolveTemplate(useAlphaClip);
		if (!templates.Contains(templateName))
		{
			reason = $"材质模板库缺少 {templateName}，无法造材质";
			return LoadOutcome.Failed;
		}

		// 正式判定用的是**最终**模板（可能被 MetalWins / 实验档改过），
		// 而上面那个 provisional 只在"要不要换底色"时用。两者不一致时以这里为准。
		metalDeliverable = templates.HasTextureSlot(templateName, MetallicSlotKey);

		// **金属与镂空在 HD2 里确实不能并存**，而且这条结论现在是结构性的、不是经验性的：
		//
		//   · 带 `IlluminateData`（金属度）槽的模板（目前只有 `advanced`）**没有**逐像素
		//     不透明度槽：`SclarFieldOpacity` 实测默认贴图是 1×1 R8，是标量；
		//   · 带 `OpacityClipMap`（逐像素镂空）的模板是 `alphaclip` / `alphaclip+`，
		//     而它们的 11 / 12 个变量里**一个金属相关参数都没有**
		//     （逐条解过：全是不透明度/脏污/天气/分辨率）。
		//
		// 实测代价（MourningVeil 58 个材质）：判成金属的 10 个**全部**落进 alphaclip ——
		// 项链、头冠、耳坠、袖口、腿环、鞋，正好是"金属 + 镂空"那一批。
		// 一个都没拿到金属度，因为那个槽在这个模板里根本不存在。
		//
		// 取舍按用户定的口径：**镂空优先**。所以这里必须把"金属被放弃了"说出来，
		// 否则用户看到的就是"我的金属没了"，而报告里写着一切正常。
		if (matcapIsMetal && !metalDeliverable)
		{
			notices.Add(
				$"{stem}：源侧是金属**又要镂空**，HD2 里没有同时支持两者的模板"
				+ $"（{templateName} 有逐像素镂空但没有金属度槽，advanced 反之）。"
				+ "已按**镂空优先**处理，并保留源底色（不再涂成亮银）——"
				+ "金属光泽这一项在这个模板里无法表达。");
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
			// ⚠️ **身份串 = 材质名，不带任何后缀。**
			//
			// 实测（Lilium_Sancta 的服装色变体）：同一个源材质 `mat_ornament_pearl_tiara`
			// 在白/粉款里 `$basetexture` 是 `.../gray`、在黑款里是 `.../pearl`，matcap 也不同。
			// 只要 id 里掺了"底色"或"matcap"，同名材质在不同颜色包里就是不同 id，
			// 而模型 patch 的材质槽只能烘一套 ⇒ 换另一个颜色包时那些 section 找不到材质，
			// 游戏里就是**品红**（实测 12 处装饰件：tiara / 发饰 / 耳坠 / 项链 / 裙饰 / 鞋）。
			//
			// 以前那三个后缀（`|emission`、`|alphaclip`、`|matcap:`）存在的唯一理由，是
			// **根是 `$basetexture`** 时"不同名字共用一张底色"会塌成同一个材质。
			// 根换成材质名之后这个理由消失了：不同名字天然不同 id，
			// 而同一个名字就是**同一个槽**——变体之间的底色/matcap 差异正是我们要折叠掉的东西。
			//
			// 代价：材质条目略多（不再按底色去重）。贴图仍然按 basename 共享，包不会明显变大。
			stem,
			emissionTexture: null,
			useAlphaClip: false,
			matcapTexture: null);

		loaded = new LoadedMaterial(
			stem, baseTexture, textureName, vtf, rgba, useAlphaClip, templateName,
			identity, DeriveMaterialId(identity), emissionTexture, emissionTint, matcapMean, verdict.Tint,
			matcapTextureName, matcapIsSurface, matcapIsMetal, metalDeliverable, sourceBaseRgba, sourceBaseWidth, sourceBaseHeight, notices,
			// 抖动是**默认**的半透明落地方式，但只给"源侧真的要半透明"的材质：
			// `$translucent`（想混合，HD2 表达不了）与 `$allowalphatocoverage`（源侧自己就在抖）。
			// 纯 `$alphatest` 继续硬裁——那是 Source 的原语义（低于参考值丢弃），蕾丝/镂空靠它。
			UseDither: useAlphaClip && vmt.WantsDitheredTransparency)
		{
			BumpTexture = bumpTextureName,
			BumpStrength = bumpStrength,
			// **只认显式开关，不再按文件名猜。**
			//
			// 曾经加过"名字里带 opengl 就翻绿通道"（huahua 的 Chiyu 整套法线都叫
			// `*_Normal_OpenGL`）。实机结果是**比不翻更奇怪**：针织的明暗整个反掉、成为一块块暗斑。
			// 结论：那些图在 L4D2 里就是照原样用的，文件名只是命名习惯，不能当约定来推断。
			// 要看另一版就显式传 `--normal-flip-y on`。
			BumpFlipY = normalFlipY,
			MatcapBlend = matcapBlend,
			MatcapTintRgba = matcapTintRgba,
			Mask1Texture = mask1Texture,
			Matcap2Blend = matcap2Blend,
			Matcap2Texture = matcap2Texture,
			Matcap2TintRgba = matcap2TintRgba,
			LightwarpTexture = lightwarpTexture,
		};
		reason = string.Empty;
		return LoadOutcome.Loaded;

		// **模板选择只有这一处实现。**
		// 上面有两次要用到它：一次是"要不要换底色"（提前算，因为换底色会改 `rgba`），
		// 一次是最终产物。两处必须同一套规则，否则会出现"按 A 模板决定换底色、
		// 最终却用 B 模板"这种自相矛盾的组合——那正是上一版"保留源底色却保留了球面图"的成因。
		string ResolveTemplate(bool alphaClip)
			=> ExperimentApplies(alphaClip)
				? alphaExperiment switch
				{
					AlphaExperiment.Translucent => "translucent",
					AlphaExperiment.AdvancedOpacity => "advanced",
					_ => CutoutTemplateName(alphaClip),
				}
				: CutoutTemplateName(alphaClip);
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
	private (SynthesizedTexture BaseColor, SynthesizedTexture? OpacityMask, SynthesizedTexture? TranslucentMap)
		BuildBaseColorTexture(LoadedMaterial loaded)
	{
		var vtf = loaded.Vtf;
		var rgba = loaded.Rgba;
		var textureName = loaded.TextureName;

		// ⚠️ **遮罩取 `SourceBaseRgba`（源底色），不是 `rgba`（可能已被换成 matcap）。**
		// 这两者在 matcap 承载表面的件上是**两张完全不同的图**：
		//   · `rgba`           = 渲染用底色（matcap 承载表面时**就是**那张球面图）
		//   · `SourceBaseRgba` = 源底色的原始像素，它里面的 **alpha** 是唯一编码了
		//                        "哪块是镂空"的那份数据
		// 曾经两者都用 `rgba`，于是那些件进了 alphaclip、遮罩却是全白 —— 镂空静默失效。
		// 尺寸也必须用 `SourceBaseWidth/SourceBaseHeight`：matcap 常是 256²，底色可能 2048²。
		var opacityMask = loaded.UseAlphaClip
			? BuildOpacityMask(textureName, loaded.SourceBaseRgba, loaded.SourceBaseWidth, loaded.SourceBaseHeight, loaded.UseDither)
			: null;

		// 实验路线 1：`translucent` 模板唯一那个槽是 `NormalXyRoughnessOpacity`
		// （RGB = 平面法线 XY + 粗糙度，A = 不透明度），必须在**清零 alpha 之前**合成出来，
		// 同样取源底色的 alpha。
		var translucentMap = alphaExperiment == AlphaExperiment.Translucent && loaded.UseAlphaClip
			? BuildTranslucentMap(textureName, loaded.SourceBaseRgba, loaded.SourceBaseWidth, loaded.SourceBaseHeight)
			: null;

		// ===== N3（默认关闭，见 LightwarpShadingEnabled）：把 lightwarp 当卡通阴影曲线烘进底色 =====
		//
		// 作者填的这些数是有意义的，不能丢：ramp 的暗端编码了"阴影该是什么颜色"
		// （basewarp 偏冷 (50,55,58)、skinwarp 偏暖 (60,45,49)、facewarp 也偏暖），
		// 受光段与暗端之间那道台阶编码了"明暗过渡有多硬"。卡通渲染里阴影从来不是灰的。
		//
		// **但绝对值不能照搬**：源侧是 `albedo × lightwarp(NdotL) × lightColor`，ramp 描述的是
		// 光照曲线，它的 0.30 由 HDR 光照与自动曝光补偿。HD2 的 PBR 光照自己就算这一层——
		// 直接整片乘 0.30 等于乘两次（实测后果：全身深灰到近黑）。
		//
		// 所以先除以 ramp 自己的受光段（受光 = 1.0），再按**法线的 N·L** 查这条归一化曲线：
		// 受光面保持原样，背光面带作者指定的色偏，整体亮度不变。
		if (LightwarpShadingEnabled
			&& loaded.LightwarpTexture is { Length: > 0 } warpName
			&& TryReadTextureRgba(warpName, out var warpRgba, out var warpWidth, out var warpHeight))
		{
			// **必须用世界方向的法线**（见 UvNormalBake）：拿切线空间法线算 N·L 会让阴影
			// 跟着贴图细节一块块跳。没有烘焙结果时按"正面朝光"算 ⇒ 等于不动。
			var shadingNormal = WorldNormalFor(loaded.Stem);
			MaterialAppearance.BakeLightwarpShading(
				rgba, vtf.Width, vtf.Height,
				warpRgba, warpWidth, warpHeight,
				shadingNormal?.Rgba,
				shadingNormal?.Width ?? 0,
				shadingNormal?.Height ?? 0);
		}

		// 压暗必须在**清零 alpha 之前、编码 sRGB 之前**：
		// 前者是因为 alpha 的用途不同（镂空走遮罩贴图），后者是因为
		// 压暗要作用在 sRGB 编码的像素上（见 AlbedoAdjustment 的注释）。
		// matcap 烘焙也必须在清零 alpha 之前（它只改 RGB，但顺序上这里最自然）。
		if (loaded.MatcapMean is not null)
			MaterialAppearance.BakeMatCap(rgba, loaded.MatcapMean, loaded.MatcapTint * matcapBakeGain);
		else if (loaded.MatcapIsSurface && !loaded.MatcapIsMetal && matcapMode == MatCapMode.Metal
			&& !string.IsNullOrWhiteSpace(loaded.MatcapTextureName))
		{
			// matcap 承载表面但**不是**金属（宝石/玻璃/珍珠）：底色同样要顶掉
			// （源底色可能是近黑占位图，保留就是一块纯黑），但用中性浅灰而不是金属 F0。
			//
			// ⚠️ **必须要求"真的有 matcap"。** 这一条以前只判 `MatcapIsSurface`，而那个标志
			// 还有一个来源是 `syntheticBase`（底图缺失/占位 → 我们合成了底色）。于是
			// "合成底色 + 没有 matcap"的材质会被涂成中性浅灰 —— 恰好把 `$color` 这个
			// **作者明确写的颜色**抹掉。实测 milltina 的 6 个金属件（银/金/宝石红）全中，
			// 全部变成同一块浅灰。
			MaterialAppearance.FlattenToMetalF0(rgba, MaterialAppearance.NonMetallicSurfaceF0);
		}
		else if (loaded.MatcapIsMetal && matcapMode == MatCapMode.Metal)
		{
			// 涂成中性金属 F0：丢掉 matcap 的亮度图案**和色相**，整片抬到目标反射率。
			// 目标值 = MetalF0 × matcapBakeGain，这样 gain 在这里退化成"F0 微调"。
			//
			// ⚠️ 连色相一起丢是刻意的：matcap 由**可选的颜色变体包**提供（C1/C2、B1/B2/B3），
			// 取色相就等于"同一份源的转换结果取决于装了哪个变体包"。详见
			// `MaterialAppearance.FlattenToMetalF0` 的注释。
			//
			// ⚠️ **但只有 advanced 才配得上这个 F0。**
			//
			// 实测（MourningVeil，58 个材质里 10 个判成金属）：**10/10 全部落进 alphaclip**
			// ——它们的 alpha 都是真实形状（cov 31~65%），所以"镂空优先"必然把它们从
			// 金属模板里挤出去。而 `alphaclip` 的 11 个变量里**没有任何金属参数**
			// （逐条解过模板：只有不透明度/脏污/天气/分辨率），那块金属度无处可写。
			//
			// 于是原来的 F0 0.96 会产出一块**亮银塑料**：在游戏里比"什么都不做"更显眼。
			//
			// **做不到金属时，我们不再动底色。** 保留作者画的那张（实测这批件共享
			// `Mat_Metal`，平均亮度 17.2 —— 近黑的中性色）：观感是一块深色金属剪影，
			// 不抢眼、也不会被误认成"金属做坏了"。这比涂成亮银或统一灰都更接近源意。
			var metalDeliverable = loaded.MetalDeliverable;
			if (metalDeliverable)
				MaterialAppearance.FlattenToMetalF0(rgba, MetalF0 * matcapBakeGain);
		}

		// ===== N2：把 matcap 层合进底色 =====
		//
		// 源侧的观感 = 底色 ⊕ matcap：模式由 `$matcap` 决定（0 禁用 / 1 乘 / 2 加 / 3 正常 / 4 屏幕），
		// 强度 = `$matcaptint`.a × `$mask1`.r × matcap 自己的 alpha。逐字实现见
		// `MaterialAppearance.CompositeMatcap`（lilToon `lilBlendColor`）。
		//
		// **这是我们此前丢掉的那一层。** 之前 `$matcap` 只是个布尔：模式、tint 的 alpha、蒙版全部
		// 丢弃，最后要么压成一个平均色（Bake），要么整片涂成中性 F0（Metal）。于是"局部亮光"
		// 这种 matcap 最典型的观感在产物里根本不存在 —— 用户说"matcap 看起来没被支持"就是这个。
		//
		// 采样坐标：源侧用**视角法线**（lilToon `lilCalcMatCapUV`），HD2 没有 view-normal 采样。
		// 我们试过直接拿切线空间法线当代替——法线贴图里的细节一抖，颜色就跟着跳，
		// 实机表现是脸上/发梢/衣褶上一块块色斑。正确的前提是**世界方向的法线**，
		// 也就是 `UvNormalBake` 把几何烘进 UV 空间得到的那张；没有它就退回取正中
		// （整片一个颜色，配合逐像素蒙版仍能表达"局部亮光"，但不会出色斑）。
		if (loaded.MatcapBlend != MaterialAppearance.MatcapBlendMode.Off
			&& !MatcapLayerDisabled
			// 底色本身就是 matcap 的那条路（Bake/Albedo）不能叠自己，会自我加强一倍。
			&& !(loaded.MatcapIsSurface && matcapMode != MatCapMode.Metal)
			&& loaded.MatcapTextureName is { Length: > 0 } matcapName
			&& TryReadTextureRgba(matcapName, out var matcapRgba, out var matcapWidth, out var matcapHeight))
		{
			byte[]? maskRgba = null;
			var maskWidth = 0;
			var maskHeight = 0;
			if (loaded.Mask1Texture is { Length: > 0 } maskName)
				TryReadTextureRgba(maskName, out maskRgba, out maskWidth, out maskHeight);

			var worldNormal = WorldNormalFor(loaded.Stem);
			MaterialAppearance.CompositeMatcap(
				rgba, vtf.Width, vtf.Height,
				matcapRgba, matcapWidth, matcapHeight,
				normal: worldNormal?.Rgba,
				normalWidth: worldNormal?.Width ?? 0,
				normalHeight: worldNormal?.Height ?? 0,
				maskRgba, maskWidth, maskHeight,
				loaded.MatcapTintRgba, loaded.MatcapBlend, maskChannel: 0, sourceAxes: true);

			// ===== 第二层（`$MATCAP2`）：叠在第一层之上，蒙版用 `$mask1` 的 **B** 通道 =====
			//
			// 官方手册把两个通道分得很清楚：`r通道用于matcap蒙版 … b通道是matcap2蒙版`。
			// 这一层负责"镀铬亮度"——实测 98 个材质里 93 个写了它，少了它金属会停在
			// "氧化过的铝"那一档。
			if (loaded.Matcap2Blend != MaterialAppearance.MatcapBlendMode.Off
				&& loaded.Matcap2Texture is { Length: > 0 } matcap2Name
				&& TryReadTextureRgba(matcap2Name, out var matcap2Rgba, out var matcap2Width, out var matcap2Height))
			{
				MaterialAppearance.CompositeMatcap(
					rgba, vtf.Width, vtf.Height,
					matcap2Rgba, matcap2Width, matcap2Height,
					normal: null, normalWidth: 0, normalHeight: 0,
					maskRgba, maskWidth, maskHeight,
					loaded.Matcap2TintRgba, loaded.Matcap2Blend, maskChannel: 2, sourceAxes: true);
			}
		}
		else if (loaded.MatcapIsSurface && matcapMode == MatCapMode.Albedo)
			MaterialAppearance.ScaleRgb(rgba, matcapBakeGain);

		albedoAdjustment?.Apply(rgba, vtf.Width, vtf.Height);

		// alpha 一律清零。
		//
		// ⚠️ **曾经以为 `alphaclip` 的 `BaseColorMetalMap.alpha` 是金属度，在这里写过 255 ——
		// 实机对照完全没有效果，已证伪**（docs/vpk-cutout-strategies-20261101.md §5）。
		// `advanced` 那边 alpha 是**自发光**，清零是对的；`alphaclip` 那边既然不参与金属度，
		// 也不该往里面塞东西 —— 未知通道塞值只会制造"看着不对但查不出来"的问题。
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
		var bc7 = EncodeBc7Deduplicated(rgba, vtf.Width, vtf.Height, srgb: true);

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
			opacityMask,
			translucentMap);
	}

	/// <summary>
	/// 底色的平均 RGB（0..1）。给 <c>translucent</c> 模板的平涂色用。
	/// <para>
	/// 按 8 位采样值直接平均，不转线性——这个值只做"整体色调"用，
	/// 实机对照要看的也不是色准，而是"到底会不会混合"。
	/// </para>
	/// </summary>
	private static float[] AverageColor(byte[] rgba)
	{
		double red = 0, green = 0, blue = 0;
		var pixels = rgba.Length / 4;
		if (pixels == 0) return [1f, 1f, 1f];
		for (var offset = 0; offset < rgba.Length; offset += 4)
		{
			red += rgba[offset + 0];
			green += rgba[offset + 1];
			blue += rgba[offset + 2];
		}

		return
		[
			(float)(red / pixels / 255.0),
			(float)(green / pixels / 255.0),
			(float)(blue / pixels / 255.0),
		];
	}

	/// <summary>
	/// 合成 <c>translucent</c> 模板要的那张 <c>NormalXyRoughnessOpacity</c>：
	/// <b>RGB = 平面法线 XY + 粗糙度，A = 源底色 alpha</b>。
	/// <para>
	/// 这是实验路线 1 的核心：HD2 把"半透明强度"放在这张图的 A 通道里，
	/// 而不是像 Source 那样由 <c>$translucent</c> 开关加底色的 alpha 决定。
	/// 法线用平面法线（128,128）——布料没有法线图时照抄平面值不会引入假起伏。
	/// </para>
	/// </summary>
	private SynthesizedTexture BuildTranslucentMap(string textureName, byte[] rgba, int width, int height)
	{
		var map = new byte[checked(width * height * 4)];
		for (var offset = 0; offset < map.Length; offset += 4)
		{
			map[offset + 0] = 128;                 // 法线 X
			map[offset + 1] = 128;                 // 法线 Y
			map[offset + 2] = PbrRoughness;        // 粗糙度
			map[offset + 3] = rgba[offset + 3];    // 不透明度 = 源 alpha
		}

		// ⚠️ 这里**刻意不接源法线图**，两个理由：
		//  1. 这张图按**源底色的尺寸**建（R,G 是法线、B 是粗糙度、A 是不透明度，四样挤在一个
		//     BC7 块里），而法线图常常是另一个分辨率——尺寸不同就没法逐像素搬。
		//  2. 这条 `translucent` 路线本身还没有实机结论（见 AlphaExperiment 的注释）。
		// 等它实机站住了再谈接线；现在接等于给一条未验证的路加复杂度。
		// 平面法线（128,128）在这里是安全的默认值：观感差一点，但不会歪。

		var bc7 = EncodeBc7Deduplicated(map, width, height, srgb: false);
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
			// 纯贴图包（只换色）里同一张 VTF 常被多个 patch 引用，解码结果可以复用。
			var rgba = DecodeTextureCached(stem, vtfBytes, vtf);
			ZeroAlpha(rgba);
			var bc7 = EncodeBc7Deduplicated(rgba, vtf.Width, vtf.Height, srgb: true);
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
	/// <summary>
	/// <b>这个材质要不要切到 <c>alphaclip</c>（逐像素镂空）？</b>
	/// <para>
	/// 三个条件缺一不可，而且各自都对应一次实测过的误判：
	/// </para>
	/// <list type="number">
	/// <item>
	/// <paramref name="wantsTransparency"/> —— 源侧写了 <c>$alphatest</c>/<c>$translucent</c>。
	/// 只看这个会误判：装饰件常写 <c>$alphatest 1</c> 而贴图是 1×1 无 alpha（M7 就漏了 3 组）。
	/// </item>
	/// <item>
	/// <paramref name="hasSourceOpacity"/> —— <b>源底色的 alpha 真的存在</b>。
	/// <para>
	/// 合成底色（底图缺失 / 源包根本没写 <c>$basetexture</c>）的 alpha 是我们自己填的 0，
	/// 那<b>不是信息</b>。旧实现拿它去算，于是 <c>Spread 0 &lt; 3 ∧ Max 0 ≤ 200</c> 让
	/// 「整片均匀半透明」恒成立 ⇒ <b>任何"底图缺失 + 写了 <c>$alphatest 1</c>"的材质都被强制
	/// 切成 alphaclip</b>，哪怕它是金属件（alphaclip 没有金属度槽，切过去既没镂空也没金属）。
	/// </para>
	/// <para>
	/// 语义上要区分的是<b>四种</b>状态而不是两种：源图在且 alpha 有形状 / 源图在且 alpha 均匀
	/// &lt; 255 / 源图在且 alpha 恒 255 / <b>源图不在（= 不知道，≠ 半透明）</b>。
	/// 这个布尔量就是最后一条的分界线。
	/// </para>
	/// </item>
	/// <item>
	/// <paramref name="alpha"/> —— alpha 真在表达形状（<see cref="AlphaStats.IsMeaningful"/>）
	/// 或真在表达整体半透明（<see cref="AlphaStats.IsUniformlyTranslucent"/>）。
	/// </item>
	/// </list>
	/// <para>
	/// <b>为什么它必须是纯函数。</b>这条规则改过很多轮，每一轮都是实机看出来的；把它抽出来
	/// 才能用单测把"哪种输入得到哪种模板"钉死，而不必每次都进游戏。
	/// </para>
	/// </summary>
	public static bool ShouldUseAlphaClip(bool wantsTransparency, bool hasSourceOpacity, AlphaStats alpha)
		=> wantsTransparency
			&& hasSourceOpacity
			&& (alpha.IsMeaningful || alpha.IsUniformlyTranslucent);

	private SynthesizedTexture BuildOpacityMask(string textureName, byte[] rgba, int width, int height, bool ditherBySource)
	{
		// 抖动与否：**源侧意图优先**（`$translucent` / `$allowalphatocoverage`），
		// 而显式的 cutout 档位可以强制打开（dither / dither-strong）或强制关掉（其余档）。
		var dithered = cutoutStrategy switch
		{
			CutoutStrategy.AlphaClipDithered => true,
			CutoutStrategy.AlphaClipDitheredStrong => true,
			// A/B/rgb-inv/alpha/alpha-inv 是"显式指定硬裁"的诊断档，不参与自动抖动。
			_ => ditherBySource,
		};
		var ditherGain = cutoutStrategy == CutoutStrategy.AlphaClipDitheredStrong ? 1.35 : 1.0;

		// 纯像素部分的实现被抽到 <see cref="BuildOpacityMaskPixels"/>，为的是能脱离 texconv 单测
		// （这条链历史上最贵的一次 bug 就发生在"字节级"——见那个方法上的注释）。
		var mask = BuildOpacityMaskPixels(
			rgba, width, height,
			inAlpha: cutoutStrategy is CutoutStrategy.MaskInAlpha or CutoutStrategy.MaskInAlphaInverted,
			inverted: cutoutStrategy is CutoutStrategy.MaskInverted or CutoutStrategy.MaskInAlphaInverted,
			dithered: dithered,
			ditherGain: ditherGain,
			ditherScale: ditherScale,
			ditherDensity: ditherDensity,
			ditherPattern: ditherPattern);

		var bc7 = EncodeBc7Deduplicated(mask, width, height, srgb: false);
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

	/// <summary>
	/// 把源底色的 <b>alpha</b> 通道转成 HD2 的镂空遮罩像素（RGBA8888，长 <c>width×height×4</c>）。
	/// <para>
	/// <b><paramref name="sourceRgba"/> 必须是裸像素（每像素 4 字节），不是带 DDS 头的整份文件。</b>
	/// 曾经这里按"含 128 字节头"处理，产出了一份"头写着 w×h、后面多出 128 字节"的 DDS；
	/// texconv 读不出自洽输入就**把输入原样退回且退出码仍是 0**，最终表现为
	/// "某材质转换失败、产物只有 132 字节"——第 23~26 轮追了四轮才定位。
	/// </para>
	/// <para>
	/// <b>通道与极性由 <see cref="CutoutStrategy"/> 决定</b>：A/B 档是"RGB = alpha、A = 255"
	/// （历史行为），另外三档用来实测 HD2 到底采样哪个通道、什么极性。
	/// </para>
	/// <para>
	/// <b>为什么要能单测。</b>这是整条材质链上唯一大量处理裸字节的地方，也是历史上 bug
	/// 最集中的地方；把它抽成纯函数之后，"遮罩的 alpha 到底来自哪份数据"就变成一条
	/// 不需要游戏数据、不需要 texconv 的断言。
	/// </para>
	/// </summary>
	internal static byte[] BuildOpacityMaskPixels(
		byte[] sourceRgba,
		int width,
		int height,
		bool inAlpha,
		bool inverted,
		bool dithered,
		double ditherGain,
		int ditherScale,
		double ditherDensity,
		DitherPattern ditherPattern)
	{
		ArgumentNullException.ThrowIfNull(sourceRgba);
		var pixelCount = checked(width * height);
		// 少一个像素都不行：下面按 4 字节跨步读 alpha，越界会读到别的数据而不是抛异常。
		if (sourceRgba.Length < checked(pixelCount * 4))
		{
			throw new InvalidDataException(
				$"遮罩源像素不足：需要 {pixelCount * 4} 字节（{width}×{height}×4），实际 {sourceRgba.Length}。");
		}

		var mask = new byte[checked(pixelCount * 4)];
		for (var offset = 0; offset < mask.Length; offset += 4)
		{
			var value = sourceRgba[offset + 3];
			if (dithered)
				value = DitherAlpha(value, offset / 4, width, ditherGain, ditherScale, ditherDensity, ditherPattern);
			if (inverted)
				value = (byte)(255 - value);
			var grey = inAlpha ? (byte)255 : value;
			mask[offset + 0] = grey;
			mask[offset + 1] = grey;
			mask[offset + 2] = grey;
			mask[offset + 3] = inAlpha ? value : (byte)255;
		}

		return mask;
	}

	/// <summary>
	/// 把 0～255 的渐变 alpha 抖成二值（0 或 255），用来在 <c>alphaclip</c> 下近似半透明。
	/// <para>
	/// <b>为什么这么算。</b>取 8×8 Bayer 阈值矩阵的归一化值 <c>t ∈ [0,1)</c>，
	/// 若 <c>alpha &gt; t</c> 就画、否则不画。于是"这一小块区域里被画出来的像素比例 ≈ alpha"，
	/// 远处缩小/被 TAA 累积后就是这个 alpha 的等效透明度。
	/// </para>
	/// <para>
	/// <b>只抖中间值。</b>低于 <c>lowCut</c>（≈5%）一律不画、高于 <c>highCut</c>（≈95%）一律画——
	/// 镂空边缘与实心区完全保持原样，只有"薄纱"那一段变成半透。
	/// 加强档（gain &gt; 1）会把整片都纳入抖动范围，用于把观感往实里推。
	/// </para>
	/// <para>
	/// <b>抖动必须在贴图空间做，而且遮罩不能带 mip。</b>一旦生成 mip，缩小后抖动会被平均回
	/// 原始渐变 alpha，再被 <c>OpacityThreshold 0.5</c> 一裁就白做了。
	/// 我们合成的遮罩本来就只有 1 级 mip（<c>BuildDds(..., [mask])</c> 单元素 mip 链）。
	/// </para>
	/// </summary>
	private static byte DitherAlpha(byte alpha, int pixelIndex, int width, double gain, int dotScale, double density, DitherPattern pattern)
	{
		const int lowCut = 13;    // ≈5%
		const int highCut = 242;  // ≈95%
		var scaled = gain == 1.0 ? alpha : Math.Min(255.0, alpha * gain);
		if (scaled <= lowCut) return 0;
		// **密度**：乘在 alpha 上，也就是"同样大小的一小块区域里画几个点"。
		// 1.0 = 忠实还原源侧 alpha（点阵密度 = 透明度）；< 1 就是整体更透（点更少）。
		// 实机调参结论：源侧 alpha 直接抖出来的点**偏密**，观感偏"厚"，
		// 而玩家要的是"点还是那么小、但更稀"——所以点大小保持 1 像素、密度单独压。
		if (density != 1.0) scaled *= Math.Clamp(density, 0.05, 1.0);
		if (scaled >= highCut && gain == 1.0 && density == 1.0) return 255;

		// **点的大小**：阈值按 dotScale×dotScale 个像素共用一个格子。
		// 1 = 每像素一个点（最细，近看像细噪点）；2 = 2×2 的点（像细网纱，但颗粒更粗）。
		// 实机反馈：2 太粗，观感不如 1 —— 所以默认 1，靠 density 控制"稀不稀"。
		var scale = Math.Clamp(dotScale, 1, 8);
		var x = pixelIndex % width / scale;
		var y = pixelIndex / width / scale;
		var threshold = pattern == DitherPattern.Bayer
			// Bayer 8×8 的阈值归一到 (0,1)：矩阵里 0..63 取 (v + 0.5) / 64，避免 0 与 1 两个端点。
			? (Bayer8x8[(y & 7) * 8 + (x & 7)] + 0.5) / 64.0
			: InterleavedGradientNoise(x, y);
		return scaled / 255.0 > threshold ? (byte)255 : (byte)0;
	}

	/// <summary>
	/// 交错梯度噪声（Interleaved Gradient Noise，Jorge Jimenez 2014）：**无可见重复结构**的抖动阈值。
	/// <para>
	/// <b>为什么需要它。</b>Bayer 矩阵是规则的 8×8 网格，同一张 2048² 的贴图铺满就是几百个相同的方块，
	/// 实机看就是"点排得太整齐"——一眼能看出织法，像纱窗而不是薄纱。
	/// IGN 每像素一个独立阈值，没有周期性，观感接近细噪点；它也是 UE / CoD 这类引擎做
	/// 抖动透明与接触阴影时的标准选择。
	/// </para>
	/// <para>
	/// 确定性：只由 (x, y) 决定，所以同一张贴图每次转换得到逐字节相同的结果（id 与产物都可复现）。
	/// </para>
	/// </summary>
	private static double InterleavedGradientNoise(int x, int y)
	{
		var value = 0.06711056 * x + 0.00583715 * y;
		value -= Math.Floor(value);
		value *= 52.9829189;
		return value - Math.Floor(value);
	}

	/// <summary>标准 8×8 Bayer 有序抖动矩阵（值域 0..63，确定性、可复现）。</summary>
	private static readonly int[] Bayer8x8 =
	[
		 0, 32,  8, 40,  2, 34, 10, 42,
		48, 16, 56, 24, 50, 18, 58, 26,
		12, 44,  4, 36, 14, 46,  6, 38,
		60, 28, 52, 20, 62, 30, 54, 22,
		 3, 35, 11, 43,  1, 33,  9, 41,
		51, 19, 59, 27, 49, 17, 57, 25,
		15, 47,  7, 39, 13, 45,  5, 37,
		63, 31, 55, 23, 61, 29, 53, 21,
	];

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
	/// 读一张贴图并解成裸像素（带 <see cref="DecodeTextureCached"/> 的复用）。
	/// 供 matcap 合成读 matcap / 蒙版 / 法线用——它们只需要像素，不需要走材质那一套。
	/// </summary>
	private bool TryReadTextureRgba(string textureName, out byte[] rgba, out int width, out int height)
	{
		rgba = [];
		width = 0;
		height = 0;
		if (string.IsNullOrWhiteSpace(textureName) || !source.TryReadVtf(textureName, out var bytes) || bytes.Length == 0)
			return false;

		try
		{
			var vtf = VtfFile.Read(bytes);
			width = vtf.Width;
			height = vtf.Height;
			rgba = DecodeTextureCached(textureName, bytes, vtf);
			return width > 0 && height > 0;
		}
		catch (Exception exception) when (exception is InvalidDataException or IOException or InvalidOperationException)
		{
			return false;
		}
	}

	/// <summary>
	/// 这个材质的「法线 + 粗糙度」图（<b>RGB = 法线、A = 粗糙度</b>）；
	/// <c>null</c> = 它没有可用的法线图，调用方继续用原来的平面法线。
	/// <para>
	/// <b>为什么返回 null 而不是直接给平面法线。</b>四个模板分支的平面法线**各是各的 id**
	/// （<c>advanced</c> 用全局共享的 <c>shared_flat_normal_</c>，金属件用 <c>&lt;贴图&gt;_nar</c>），
	/// 直接在这里统一会改动"没有法线图的材质"的产物字节——那批 Mod 全部要重构建一次，
	/// 而收益为零。所以只替换"真有的那部分"。
	/// </para>
	/// </summary>
	private SynthesizedTexture? TryNormalRoughnessTexture(LoadedMaterial loaded, byte roughness)
		=> NoNormalMapRequested || loaded.BumpTexture is not { Length: > 0 } bump
			? null
			: TryBuildNormalTexture(bump, loaded.BumpStrength, roughness, loaded.BumpFlipY);

	/// <summary>
	/// 诊断开关 <c>HD2_NO_NORMAL_MAP=1</c>：**退回平面法线**，用来做 A/B 与计时。
	/// <para>
	/// 与 <c>HD2_NO_SYNTHETIC_BASE</c> 同一个用途：把"新功能到底改变了多少"变成一次干净对照，
	/// 不用改代码、不用切分支。它也回答"转得慢是不是法线造成的"——这个问题只能量，不能猜。
	/// </para>
	/// </summary>
	private static readonly bool NoNormalMapRequested =
		Environment.GetEnvironmentVariable("HD2_NO_NORMAL_MAP") == "1";

	/// <summary>
	/// 诊断开关 <c>HD2_NO_MATCAP_LAYER=1</c>：不把 matcap 层合进底色（退回 N2 之前的行为）。
	/// <para>
	/// 与 <c>HD2_NO_NORMAL_MAP</c> 同一族：把"这一层到底改变了什么"变成一次干净的对照。
	/// 也是万一合成观感不对时的**就地退路**——不用回滚构建。
	/// </para>
	/// </summary>
	private static readonly bool MatcapLayerDisabled =
		Environment.GetEnvironmentVariable("HD2_NO_MATCAP_LAYER") == "1";

	/// <summary>
	/// 开关 <c>HD2_LIGHTWARP=1</c>：把 lightwarp 当卡通阴影曲线烘进底色（<b>默认关闭</b>）。
	/// <para>
	/// <b>为什么默认关。</b>同 matcap 那条：源侧用**视角相关**的光照方向，而我们只能用切线空间法线
	/// 配一个固定光向去近似。实机结果是**脸上、发梢、衣褶上出现一块块色斑**——切线法线是
	/// "相对这个面"的方向，拿它当世界方向算 N·L，法线一抖阴影就跳。
	/// </para>
	/// <para>
	/// 保留开关是因为这条数据本身有价值（ramp 的暗端编码了"阴影该是什么颜色"：
	/// basewarp 偏冷、skinwarp 偏暖）。要真正用起来，得先拿到**模型空间的法线**或
	/// 逐顶点的世界方向，而不是现在这张切线空间法线图。
	/// </para>
	/// </summary>
	private static readonly bool LightwarpShadingEnabled =
		Environment.GetEnvironmentVariable("HD2_NO_LIGHTWARP") != "1";

	/// <summary>
	/// 把 <c>$bumpmap</c> 转成一张 HD2 法线贴图；读不出来返回 <c>null</c>（调用方退回平面法线）。
	/// <para>缓存键含强度/粗糙度/Y 翻转，因为这三样都会改变产物字节。</para>
	/// </summary>
	/// <summary>
	/// 量一张法线图的 XY 均值与标准差（都在 −1..1 域）。
	/// <para>
	/// 用途只有一个：判断它**能不能当切线空间法线用**。正常的法线图XY 均值应接近 0；
	/// 整张图整体歪向一边（均值绝对值很大）说明它编码在别的空间/别的约定里，
	/// 接进去只会把明暗歪成一块块斑。
	/// </para>
	/// </summary>
	internal static (double MeanX, double MeanY, double SigmaX, double SigmaY) MeasureNormal(byte[] rgba)
	{
		ArgumentNullException.ThrowIfNull(rgba);
		double sumX = 0, sumY = 0, sumX2 = 0, sumY2 = 0;
		long count = 0;
		for (var offset = 0; offset + 1 < rgba.Length; offset += 4)
		{
			var x = ((rgba[offset] / 255.0) * 2) - 1;
			var y = ((rgba[offset + 1] / 255.0) * 2) - 1;
			sumX += x;
			sumY += y;
			sumX2 += x * x;
			sumY2 += y * y;
			count++;
		}

		if (count == 0)
			return (0, 0, 0, 0);

		var meanX = sumX / count;
		var meanY = sumY / count;
		return (
			meanX,
			meanY,
			Math.Sqrt(Math.Max(0, (sumX2 / count) - (meanX * meanX))),
			Math.Sqrt(Math.Max(0, (sumY2 / count) - (meanY * meanY))));
	}

	private SynthesizedTexture? TryBuildNormalTexture(string bumpTexture, double strength, byte roughness, bool flipY)
	{
		var (texture, _) = normalTextures.GetOrAdd(
			(bumpTexture, strength, roughness, flipY),
			static (key, self) => self.EncodeNormalTexture(key.Name, key.Strength, key.Roughness, key.FlipY),
			this);
		return texture;
	}


	private (SynthesizedTexture? Texture, string? Notice) EncodeNormalTexture(
		string bumpTexture, double strength, byte roughness, bool flipY)
	{
		if (!source.TryReadVtf(bumpTexture, out var bytes) || bytes.Length == 0)
			return (null, $"{bumpTexture}：法线图不在材质来源里");

		byte[] rgba;
		int width;
		int height;
		try
		{
			var vtf = VtfFile.Read(bytes);
			width = vtf.Width;
			height = vtf.Height;
			// 法线是**线性**数据：DXT5/ATI2N 都按线性 DXGI 格式交给 texconv，绝不能走 sRGB。
			rgba = DecodeTextureCached(bumpTexture, bytes, vtf);
		}
		catch (Exception exception) when (exception is InvalidDataException or IOException or InvalidOperationException)
		{
			return (null, $"{bumpTexture}：法线图读不出来（{exception.Message}）");
		}

		if (width <= 0 || height <= 0 || rgba.Length != checked(width * height * 4))
			return (null, $"{bumpTexture}：法线图尺寸与像素数不符（{width}×{height}，{rgba.Length} 字节）");

		// **先验一眼"这到底是不是正常的切线空间法线"。**
		//
		// 实测（2026-11-04）：正常法线图的 XY 均值都在 0 附近（Lunalice 13 张全在 ±0.01 内，
		// 且全是 BC5/ATI2N）。而 Chiyu 与 Manuka 那批 DXT1/DXT5 存的"法线"，
		// XY 均值是 **(−0.55,−0.55)** —— 一开始我以为它们编码在别的空间里。
		//
		// 后来量出来真因：**这些图被当成"颜色"做了 sRGB→线性 转换**（正常的 flat 128 被压到 ~55）。
		// 证据：把存的值按"线性→sRGB"反算回去，8 张图的 XY 均值**全部精确回到 0.00**。
		// 所以不拒绝，先试一次 gamma 复原；复原后仍居中就照用，否则才退回平面法线。
		{
			var (meanX, meanY, sigmaX, sigmaY) = MeasureNormal(rgba);
			const double MeanLimit = 0.25;
			const double SigmaLimit = 0.65;
			if (Math.Abs(meanX) > MeanLimit || Math.Abs(meanY) > MeanLimit
				|| sigmaX > SigmaLimit || sigmaY > SigmaLimit)
			{
				var recovered = (byte[])rgba.Clone();
				for (var offset = 0; offset < recovered.Length; offset += 4)
				{
					recovered[offset] = MaterialAppearance.LinearToSrgbByte(recovered[offset] / 255.0);
					recovered[offset + 1] = MaterialAppearance.LinearToSrgbByte(recovered[offset + 1] / 255.0);
				}

				var (fixedX, fixedY, fixedSx, fixedSy) = MeasureNormal(recovered);
				if (Math.Abs(fixedX) <= 0.10 && Math.Abs(fixedY) <= 0.10
					&& fixedSx <= SigmaLimit && fixedSy <= SigmaLimit)
				{
					rgba = recovered;
				}
				else
				{
					return (null,
						$"{bumpTexture}：这张图看起来不是切线空间法线"
						+ $"（XY 均值 {meanX:F2},{meanY:F2}、σ {sigmaX:F2},{sigmaY:F2}），已按无起伏处理");
				}
			}
		}

		MaterialAppearance.RebuildTangentNormal(rgba, strength, flipY, roughness);

		var name = $"{bumpTexture}_nar{DescribeNormalVariant(strength, roughness, flipY)}";
		var bc7 = EncodeBc7Deduplicated(rgba, width, height, srgb: false);
		var asset = new Hd2TextureAsset();
		asset.FromDds(bc7);
		return (new SynthesizedTexture(
			DeriveTextureId(name),
			asset.WriteToc(),
			asset.Pixels,
			name,
			asset.FormatName,
			asset.Width,
			asset.Height), null);
	}

	/// <summary>
	/// 法线贴图名字里的变体后缀。**只放会改变字节的那些量**——名字进 id，
	/// 而 id 决定装配器会不会把两张图并成一张。
	/// </summary>
	private static string DescribeNormalVariant(double strength, byte roughness, bool flipY)
	{
		var parts = new List<string>(3);
		if (Math.Abs(strength - 1.0) > 1e-6)
			parts.Add("s" + strength.ToString("0.##", CultureInfo.InvariantCulture));
		parts.Add("r" + roughness.ToString(CultureInfo.InvariantCulture));
		if (flipY)
			parts.Add("fy");
		return "_" + string.Join('_', parts);
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















