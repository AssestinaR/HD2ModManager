using HD2GameItemAssets;
using HD2ModAdaptation.Analysis;

namespace HD2ModCore.Domain;

// =============================================================================
//  护甲结构 —— 唯一权威说明（动这块代码之前，先读完这一整段）
// =============================================================================
//
// 一件护甲在游戏里是一个 **kit**：注册文件（generated_customization_armor_sets）里的一条
// item。它的全部视觉内容由**四个维度**寻址：
//
//     (itemId, body, slot, pieceType)  →  一个 unit
//
// 少一个维度都不唯一。项目里绝大多数"部位显示不对"的 bug，根因都是只用了其中三个。
// 下面的数字全部是对本机 422 个 kit / 3527 个件的实测结果，不是推断。
//
//
// 1. 套装 kit —— 实测 422 个：Armor 138、Helmet 161、Cape 123
//    kit_kind：0 = Armor（身体套装）、1 = Helmet（头盔套装）、2 = Cape（披风套装）
//    一套装备 ≈ 1 个身体 kit + 1 个头盔 kit，**两者是不同 archive，一般同名**。
//    实测：421 个 archive 里没有任何一个同时承载两种 category；109 个 setId 同时含
//    身体与头盔，archiveId 各不相同、displayName 相同。
//    每个 kit 对应一个 archiveId；组合护甲是多个 itemId，要按成员逐个取。
//
// 2. 身形 body —— 实测每个 kit 恰好 3 行 body
//    原始编码：0 = Stocky（健壮）、1 = Slim（纤细）、3 = Any（通用）
//    实测计数：Stocky 1148 件、Slim 1148 件、Any 1231 件，没有第四种取值。
//    "两套身形"指的是**视觉上**的 Stocky / Slim 两套；Any 是第三行 body，表示
//    "这一个 unit 同时服务两种身形"，与 Stocky/Slim **互斥**。
//    实测互斥成立：同一 (kit, slot, pieceType) 上 Any 与 Stocky/Slim 共存的次数为 **0**。
//    管理器的"合并为 Any / 拆分为双身形"就是在两者之间转换。
//
//    ⚠ 原始 0/1/3 与 UnitMeshBodyVariant 枚举（Unknown=0, Slim=1, Stocky=2, Any=3, Other=4）
//      **不是同一套编号**：1 和 3 恰好相等，0 错位（原始 0 = Stocky，枚举 0 = Unknown）。
//      任何 `(UnitMeshBodyVariant)rawBody` 强转都会把 Stocky 变成 Unknown，而 Slim/Any
//      又恰好正确——这种 bug 只在部分护甲上发作。必须走 ToBodyVariant。
//
// 3. 部位 slot（区域）—— 编号 0..9
//    0 头盔、1 披风、2 躯干、3 胯、4 左腿、5 右腿、6 左臂、7 右臂、8 左肩、9 右肩
//    身体套装的**核心 6 个区域**是 2..7：躯干、胯、左腿、右腿、左臂、右臂。
//    实测：138 个身体 kit **每一个都带 slot 1（披风）和 2..7 全部 6 个核心区域**；
//    双肩（8/9）只有 125/138 有，13 套完全没有，2 套只有左肩、4 套只有右肩。
//    头盔（slot 0）只出现在头盔 kit 里（实测 161 个头盔 kit 各有 1 件 0/0）。
//    所以"一套护甲有 6 个区域"说的是核心 6 个，披风与双肩是另外两类部位。
//
//    ⚠ 展示用的 UnitMeshPartKind 枚举**数值顺序与这里不同**：
//        UnitMeshPartKind = Unknown, Head, Torso, Pelvis, LeftArm, RightArm, LeftLeg, RightLeg, …
//        CustomizationKitSlot = 0 Helmet, 1 Cape, 2 Torso, 3 Hips, 4 LeftLeg, 5 RightLeg, 6 LeftArm, 7 RightArm
//      也就是说 slot 4 是 LeftLeg，而枚举 4 是 LeftArm。`(UnitMeshPartKind)slot` 会
//      静默把腿和臂对调——编译通过、数值看起来合理、只有玩家能看出来。必须走显式映射
//      （正确的参照实现：GameDataAuthorityProvider.ToPartKind；RuntimeArmorRegistryService
//      里那份漏了披风与双肩，需要收编）。
//      同理 `(UnitMeshBodyVariant)rawBody` 会把 Stocky 变成 Unknown（见第 2 条）。
//
// 4. 层 pieceType（= 该部位的第几个 unit 槽）—— 实测只有 0/1/2
//    每个部位最多挂 **3 个 unit 槽**，pieceType 就是槽序号（不是部位编号）。
//    实测：同一 (kit, slot) 上出现 3 个不同 pieceType 的有 96 组，2 个的 691 组，
//    1 个的 709 组；从未出现 3 以上。
//    约定用法：
//      0 = 内层：玩家角色身体 + 内衣（undergarment）
//      1 = 外层：防弹衣 / 护甲本体
//      2 = 装饰小件：有概率出现，大多数护甲不用
//    实测分布：pt=0 1960 件、pt=1 1380 件、pt=2 187 件。
//    分部位看：pt=1 **只出现在 2..7**（核心 6 区域），头盔/披风/双肩从不用 pt=1；
//    双肩只用 pt=0（另有 2 件 pt=2）。
//
//    ⚠ 这是**约定**，不是定律。同一个 pieceType 在不同护甲上可能承载不同层。
//      要判断"这一件是内层还是外层"，看 unit 自己 CustomizationInfo 里的 PieceType
//      字符串（Undergarment / Armor），不要看 pieceType 的数字。
//
// 5. 件 piece（最小单位）
//    一件 = 某个 (body, slot, pieceType) 上的一个 unit + 8 个资源引用 + weight + tone。
//    记录长度 **112 字节**（2026-10-20 的游戏更新从 96 加宽，字段偏移不变，尾部 +16）。
//      +0    unit            8 字节
//      +8    slot            4
//      +12   pieceType       4
//      +16   weight          4   ← gameplay 数值，**不是外观**
//      +20..23               未定
//      +24..80  八个资源引用  8 × 8 字节（LUT / 材质 / 贴图）
//      +88   tone            1
//      +89..111 保留         23 字节，恒为 7×00 7×FF 9×00（从未使用，可安全借用）
//
//    ⚠ weight 是**逐件**的，不是整套护甲的等级：实测 138 套里 61 套全 1，但 18 套是
//      {0,1}、17 套是 {1,2}、9 套是 {0,1,2}，同一套内并不一致。取值 0/1/2，
//      全库 912 / 1841 / 774 件。具体语义（防护层？伤害分配？）未确认，但改它一定会
//      影响 gameplay —— 外观映射永远不许碰 +16。
//
// 6. 共享 unit 属于**所有**引用它的护甲，不属于其中任何一件
//    这是事实，不是近似。实测：同一个 unit id 会出现在多件护甲里，也会在**同一件护甲**
//    里出现在两个位置——最常见的是 Stocky 与 Slim 两行写同一个 unit id
//    （例如 kit=530315896 的 unit 0x41fb972b82d4cd2d 同时是 2/2/Stocky 和 2/2/Slim）。
//    所以：
//      - 事实层必须是 unit ↔ kit 的**多对多**关系，不能给 unit 指定唯一的"所属护甲"；
//      - 不能靠"这个 unit 只被一件护甲引用"来推断部位——那是巧合，不是规则；
//      - 改一个共享 unit 会同时改变所有引用它的护甲，界面必须如实说明这一点。
//    任何试图把共享 unit 归给单一护甲的设计都是错的。
//
// 7. 两套事实源，不许混用
//    件表（注册文件）给出 (body, slot, pieceType, unit) —— 权威、**件级**。
//    unit 自己的 CustomizationInfo（TocData +0x4C）给出 (BodyType, Slot, PieceType) 字符串
//      —— 权威、**unit 级**，但一个 unit 只有一个 slot，覆盖不了"一个 unit 挂多个件"的情况。
//    两者在 5/6/7 档**编号并不一致**（见 ArmourUnitTableSource / WardrobeLinkCompiler 的注释）。
//    凡是需要"某件护甲的某个部位挂了哪个 unit"，一律以件表为准。
//
// 8. 规范名
//    名字 = {part}_{pieceType}_{body}，全小写 ASCII：
//      helmet_0_any        头盔，第 0 槽，通用身形
//      torso_0_slim        躯干，第 0 槽（内层），纤细
//      torso_1_slim        躯干，第 1 槽（护甲），纤细
//      torso_2_slim        躯干，第 2 槽（装饰），纤细
//      left_shoulder_0_slim
//    pieceType 是"部位内的第几个 unit 槽"，所以它排在部位之后，**不是部位编号**。
//    不带 lod：unit 级身份与 LOD 无关。
//
// =============================================================================

/// <summary>
/// 护甲结构的常量与语义。<b>这是全项目关于"unit 属于哪件护甲的哪个部位"的唯一权威出处</b>；
/// 别处再出现一套 slot 编号、身形编码或部位名，都是需要收编的重复实现。
/// </summary>
/// <remarks>
/// 寻址是四元组 <c>(itemId, body, slot, pieceType)</c>。缺一维就不唯一。
/// 共享 unit 是多对多关系，不属于单一护甲。完整说明见本文件顶部。
/// </remarks>
public static class ArmorStructure
{
	/// <summary>
	/// 件记录长度。2026-10-20 的游戏更新从 96 加宽到 112：字段偏移不变，多出的 16 字节追加在尾部。
	/// 用 96 读会让每行早 16 字节，症状是槽位越界、weight 天文数字、unit 落到 0/1/0xFFFF。
	/// </summary>
	public const int PieceSize = 112;

	/// <summary>一个部位最多挂几个 unit 槽（实测上限，从未出现 3 个以上）。</summary>
	public const int UnitSlotsPerRegion = 3;

	/// <summary>pieceType 的合法上限（实测只有 0/1/2）。</summary>
	public const uint MaxPieceType = 2;

	/// <summary>原始身形编码里"通用"的值。它不是第三种身形，而是与 Stocky/Slim 互斥的写法。</summary>
	public const uint RawBodyAny = 3;

	/// <summary>每个 kit 都恰好有几行 body（实测：Stocky / Slim / Any）。</summary>
	public const int BodyRowsPerKit = 3;

	/// <summary>头盔只出现在头盔 kit 的 slot 0 上。</summary>
	public static bool IsHelmetSlot(uint slot) => slot == CustomizationKitSlot.Helmet;

	/// <summary>身体套装的核心 6 个区域：躯干、胯、左腿、右腿、左臂、右臂。每个身体 kit 都有。</summary>
	public static bool IsCoreBodyRegion(uint slot)
		=> slot is CustomizationKitSlot.Torso or CustomizationKitSlot.Hips
			or CustomizationKitSlot.LeftLeg or CustomizationKitSlot.RightLeg
			or CustomizationKitSlot.LeftArm or CustomizationKitSlot.RightArm;

	/// <summary>肩甲与披风：独立部位。披风每个身体 kit 都有，双肩只有 125/138 有。</summary>
	public static bool IsOptionalRegion(uint slot)
		=> slot is CustomizationKitSlot.Cape or CustomizationKitSlot.LeftShoulder or CustomizationKitSlot.RightShoulder;

	/// <summary>
	/// 原始身形编码（0/1/3）→ 公开枚举。<b>这是唯一允许做这个转换的地方</b>；
	/// 别处一律调用它，不要写 <c>(UnitMeshBodyVariant)raw</c>（原始 0 = Stocky，枚举 0 = Unknown）。
	/// </summary>
	public static UnitMeshBodyVariant ToBodyVariant(uint rawBody) => rawBody switch
	{
		0 => UnitMeshBodyVariant.Stocky,
		1 => UnitMeshBodyVariant.Slim,
		RawBodyAny => UnitMeshBodyVariant.Any,
		_ => UnitMeshBodyVariant.Other,
	};
}
