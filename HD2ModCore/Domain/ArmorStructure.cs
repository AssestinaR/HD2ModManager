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
// 3. 部位 slot（区域）—— 编号 0..9，名字以游戏的注册表为准
//    0 头盔 Helmet、1 披风 Cape、2 躯干 Torso、3 胯 Hips、4 左腿 LeftLeg、5 右腿 RightLeg、
//    6 左臂 LeftArm、7 右臂 RightArm、8 左肩 LeftShoulder、9 右肩 RightShoulder
//    身体套装的**核心 6 个区域**是 2..7：躯干、胯、左腿、右腿、左臂、右臂。
//    实测：138 个身体 kit **每一个都带 slot 1（披风）和 2..7 全部 6 个核心区域**；
//    双肩（8/9）只有 125/138 有，13 套完全没有，2 套只有左肩、4 套只有右肩。
//    头盔（slot 0）只出现在头盔 kit 里（实测 161 个头盔 kit 各有 1 件 0/0）。
//    所以"一套护甲有 6 个区域"说的是核心 6 个，披风与双肩是另外两类部位。
//
//    ⚠ 胯部的唯一名字是 **Hips**。注册表字段叫 Hips、枚举叫 Hips、显示叫"胯部"。
//      "Pelvis"、"Hip" 只作为**输入别名**存在于 TryParse，不允许再出现在输出里。
//
//    ⚠ 展示用的 UnitMeshPartKind 枚举与这里**同名同义**（Helmet/Cape/Torso/Hips/…），
//      但它是按值序列化进 SQLite 的，数值一旦发布就不能改。转换只走 ToPartKind。
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
/// 护甲部位的**唯一枚举**。名字与游戏的注册表一致（<see cref="CustomizationKitSlot"/>），
/// 中文显示名也由这里统一给出，别处不许再自己写一套。
/// </summary>
public enum ArmorPart
{
	Unknown = 0,
	Helmet,
	Cape,
	Torso,
	Hips,
	LeftLeg,
	RightLeg,
	LeftArm,
	RightArm,
	LeftShoulder,
	RightShoulder,
}

/// <summary>
/// 护甲结构的常量、映射与显示名。<b>这是全项目关于"unit 属于哪件护甲的哪个部位"的唯一权威出处。</b>
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

	// --- 部位表：唯一的 slot ↔ 部位 ↔ 名字 对照 -------------------------------

	private sealed record PartRow(
		uint Slot,
		ArmorPart Part,
		string Name,
		string Display,
		UnitMeshPartKind Kind,
		string[] Aliases);

	// 顺序即显示顺序：头盔 → 披风 → 躯干 → 胯 → 左右腿 → 左右臂 → 双肩。
	// Aliases 只用于**解析**（unit 自己的 CustomizationInfo 字符串、旧数据、搜索），
	// 绝不用来输出。输出一律用 Name / Display。
	private static readonly PartRow[] Rows =
	[
		new(CustomizationKitSlot.Helmet, ArmorPart.Helmet, "helmet", "头盔", UnitMeshPartKind.Helmet,
			["helmet", "head"]),
		new(CustomizationKitSlot.Cape, ArmorPart.Cape, "cape", "披风", UnitMeshPartKind.Cape,
			["cape"]),
		new(CustomizationKitSlot.Torso, ArmorPart.Torso, "torso", "胸部", UnitMeshPartKind.Torso,
			["torso", "chest"]),
		new(CustomizationKitSlot.Hips, ArmorPart.Hips, "hips", "胯部", UnitMeshPartKind.Hips,
			["hips", "hip", "pelvis", "legs_hips"]),
		new(CustomizationKitSlot.LeftLeg, ArmorPart.LeftLeg, "left_leg", "左腿", UnitMeshPartKind.LeftLeg,
			["leftleg", "left_leg", "l_leg", "leg_l", "leg_left", "lft_leg", "g_leg_l"]),
		new(CustomizationKitSlot.RightLeg, ArmorPart.RightLeg, "right_leg", "右腿", UnitMeshPartKind.RightLeg,
			["rightleg", "right_leg", "r_leg", "leg_r", "leg_right", "rgt_leg", "g_leg_r"]),
		new(CustomizationKitSlot.LeftArm, ArmorPart.LeftArm, "left_arm", "左臂", UnitMeshPartKind.LeftArm,
			["leftarm", "left_arm", "l_arm", "arm_l", "arm_left", "lft_arm", "torso_arm_l"]),
		new(CustomizationKitSlot.RightArm, ArmorPart.RightArm, "right_arm", "右臂", UnitMeshPartKind.RightArm,
			["rightarm", "right_arm", "r_arm", "arm_r", "arm_right", "rgt_arm", "torso_arm_r"]),
		new(CustomizationKitSlot.LeftShoulder, ArmorPart.LeftShoulder, "left_shoulder", "左肩", UnitMeshPartKind.LeftShoulder,
			["leftshoulder", "left_shoulder", "l_shoulder", "shoulder_l", "shoulder_left"]),
		new(CustomizationKitSlot.RightShoulder, ArmorPart.RightShoulder, "right_shoulder", "右肩", UnitMeshPartKind.RightShoulder,
			["rightshoulder", "right_shoulder", "r_shoulder", "shoulder_r", "shoulder_right"]),
	];

	/// <summary>全部部位，按显示顺序。</summary>
	public static IReadOnlyList<ArmorPart> All { get; } = Rows.Select(row => row.Part).ToArray();

	/// <summary>槽位编号 → 部位。未知编号返回 <see cref="ArmorPart.Unknown"/>，不猜。</summary>
	public static ArmorPart FromSlot(uint slot)
	{
		foreach (var row in Rows)
		{
			if (row.Slot == slot)
				return row.Part;
		}

		return ArmorPart.Unknown;
	}

	/// <summary>部位 → 槽位编号。未知部位返回 <c>null</c>。</summary>
	public static uint? SlotOf(ArmorPart part)
	{
		foreach (var row in Rows)
		{
			if (row.Part == part)
				return row.Slot;
		}

		return null;
	}

	/// <summary>规范英文名（小写、下划线），用于键、文件名、Lua 标识符。</summary>
	public static string Name(ArmorPart part)
		=> Rows.FirstOrDefault(row => row.Part == part)?.Name ?? "unknown";

	/// <summary>唯一的中文显示名。别处不要再写 switch。</summary>
	public static string Display(ArmorPart part)
		=> Rows.FirstOrDefault(row => row.Part == part)?.Display ?? "未标注部位";

	/// <summary>显示顺序（越小越靠前）。未知部位排在最后。</summary>
	public static int Order(ArmorPart part)
	{
		for (var index = 0; index < Rows.Length; index++)
		{
			if (Rows[index].Part == part)
				return index;
		}

		return Rows.Length + 1;
	}

	/// <summary>显示顺序（按槽位编号）。未知槽位排在最后，仍按编号稳定排序。</summary>
	public static int Order(uint slot) => Order(FromSlot(slot));

	/// <summary>
	/// 从任意文本里认出部位。<c>null</c> 表示认不出。
	/// 会剥掉游戏的前缀（<c>HelldiverCustomizationSlot_</c>）和 <c>g_</c> 骨名前缀，
	/// 然后按别名做**整词**匹配（不做子串匹配——子串匹配正是"hip 命中 hips"这类误判的根源）。
	/// </summary>
	public static ArmorPart? TryParse(string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
			return null;

		var value = text.Trim();
		foreach (var prefix in new[] { "HelldiverCustomizationSlot_", "HelldiverCustomizationPieceType_", "HelldiverCustomizationBodyType_", "g_" })
		{
			if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
				value = value[prefix.Length..];
		}

		// 骨名形如 g_torso_arm_l / g_leg_undergarment_l：先试整串，再试前两段、前三段。
		var segments = value.Split('_', StringSplitOptions.RemoveEmptyEntries);
		var candidates = new List<string> { value };
		for (var take = Math.Min(3, segments.Length); take >= 1; take--)
			candidates.Add(string.Join('_', segments.Take(take)));

		foreach (var candidate in candidates)
		{
			foreach (var row in Rows)
			{
				foreach (var alias in row.Aliases)
				{
					if (string.Equals(candidate, alias, StringComparison.OrdinalIgnoreCase))
						return row.Part;
				}
			}
		}

		return null;
	}

	// --- 部位 ↔ UnitMeshPartKind ---------------------------------------------

	/// <summary>部位 → 展示用枚举。**唯一允许的转换方向**，不要强转数值。</summary>
	public static UnitMeshPartKind ToPartKind(ArmorPart part)
		=> Rows.FirstOrDefault(row => row.Part == part)?.Kind ?? UnitMeshPartKind.Unknown;

	/// <summary>槽位编号 → 展示用枚举。</summary>
	public static UnitMeshPartKind ToPartKind(uint slot) => ToPartKind(FromSlot(slot));

	/// <summary>展示用枚举 → 部位。</summary>
	public static ArmorPart FromPartKind(UnitMeshPartKind kind)
	{
		foreach (var row in Rows)
		{
			if (row.Kind == kind)
				return row.Part;
		}

		return ArmorPart.Unknown;
	}

	// --- 身形 ----------------------------------------------------------------

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

	/// <summary>公开枚举 → 原始身形编码。未知/其他返回 <c>null</c>，调用方自己决定怎么处理。</summary>
	public static uint? ToRawBody(UnitMeshBodyVariant body) => body switch
	{
		UnitMeshBodyVariant.Stocky => 0,
		UnitMeshBodyVariant.Slim => 1,
		UnitMeshBodyVariant.Any => RawBodyAny,
		_ => null,
	};

	/// <summary>规范英文身形名：stocky / slim / any。</summary>
	public static string BodyName(UnitMeshBodyVariant body) => body switch
	{
		UnitMeshBodyVariant.Stocky => "stocky",
		UnitMeshBodyVariant.Slim => "slim",
		UnitMeshBodyVariant.Any => "any",
		UnitMeshBodyVariant.Other => "other",
		_ => "unknown",
	};

	/// <summary>唯一的中文身形名。</summary>
	public static string DisplayBody(UnitMeshBodyVariant body) => body switch
	{
		UnitMeshBodyVariant.Stocky => "健壮",
		UnitMeshBodyVariant.Slim => "纤细",
		UnitMeshBodyVariant.Any => "通用",
		UnitMeshBodyVariant.Other => "其他体型",
		_ => "未标注身形",
	};

	/// <summary>身形显示顺序：健壮 → 纤细 → 通用 → 其他。别处不要再排一次。</summary>
	public static int OrderBody(UnitMeshBodyVariant body) => body switch
	{
		UnitMeshBodyVariant.Stocky => 0,
		UnitMeshBodyVariant.Slim => 1,
		UnitMeshBodyVariant.Any => 2,
		UnitMeshBodyVariant.Other => 3,
		_ => 4,
	};

	/// <summary>从 unit 自己的 CustomizationInfo 字符串认出身形（Slim / Stocky / Any）。</summary>
	public static UnitMeshBodyVariant ParseBody(string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
			return UnitMeshBodyVariant.Unknown;
		var value = text.Trim();
		if (value.StartsWith("HelldiverCustomizationBodyType_", StringComparison.OrdinalIgnoreCase))
			value = value["HelldiverCustomizationBodyType_".Length..];
		return value.ToLowerInvariant() switch
		{
			"slim" or "female" => UnitMeshBodyVariant.Slim,
			"stocky" or "male" => UnitMeshBodyVariant.Stocky,
			"any" => UnitMeshBodyVariant.Any,
			_ => UnitMeshBodyVariant.Other,
		};
	}

	// --- 层 ----------------------------------------------------------------

	/// <summary>
	/// pieceType（部位内的第几个 unit 槽）的显示名。
	/// 0 = 内层、1 = 护甲、2 = 装饰；其余编号老实显示编号，不假装知道含义。
	/// </summary>
	public static string DisplayPieceSlot(uint pieceType) => pieceType switch
	{
		0 => "内层",
		1 => "护甲",
		2 => "装饰",
		_ => $"第 {pieceType} 槽",
	};

	/// <summary>组件卡/表格里用的一行完整描述：<c>第 0 槽 · 内层</c>。</summary>
	public static string DescribePieceSlot(uint pieceType)
		=> $"第 {pieceType} 槽 · {DisplayPieceSlot(pieceType)}";

	/// <summary>唯一的中文层级名。</summary>
	public static string DisplayLayer(UnitMeshPartLayer layer) => layer switch
	{
		UnitMeshPartLayer.Undergarment => "内衣",
		UnitMeshPartLayer.Armor => "护甲",
		UnitMeshPartLayer.Accessory => "附件",
		UnitMeshPartLayer.Culling => "隐藏壳",
		UnitMeshPartLayer.Static => "静态",
		_ => "未分类",
	};

	/// <summary>件级 PieceType 字符串（Undergarment / Armor）的中文名。</summary>
	public static string DisplayPieceType(string? pieceType)
	{
		if (string.IsNullOrWhiteSpace(pieceType))
			return "未标注套组";
		var value = pieceType.Trim();
		if (value.StartsWith("HelldiverCustomizationPieceType_", StringComparison.OrdinalIgnoreCase))
			value = value["HelldiverCustomizationPieceType_".Length..];
		return value.ToLowerInvariant() switch
		{
			"undergarment" => "内衣",
			"armor" or "armour" => "护甲",
			"accessory" => "附件",
			_ => pieceType,
		};
	}

	// --- 规范名 -------------------------------------------------------------

	/// <summary>
	/// <c>{part}_{pieceType}_{body}</c>，例如 <c>torso_1_slim</c>。
	/// unit 级身份与 LOD 无关，所以名字里不带 lod。
	/// </summary>
	public static string CanonicalName(ArmorPart part, uint pieceType, UnitMeshBodyVariant body)
		=> $"{Name(part)}_{pieceType}_{BodyName(body)}";

	// --- 结构判定 -----------------------------------------------------------

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
}
