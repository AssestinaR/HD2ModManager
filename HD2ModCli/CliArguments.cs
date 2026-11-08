namespace HD2ModCli;

public sealed record CliGlobalOptions(
	string? ManagerRoot,
	string? Library,
	string? GameData,
	string? RegistryRoot,
	string? Output,
	string? Plan,
	string? Host,
	string? AddToProfile,
	string? Keys,
	string? AddKeys,
	string? RemoveKeys,
	string? ArchiveId,
	string? Kind,
	string? Header,
	IReadOnlyList<string> Picks,
	int? Position,
	int? Limit,
	string? Cursor,
	bool SetActive,
	bool WithUnsharedOptions,
	bool WithEffective,
	bool OnlyCompetition,
	bool Overwrite,
	bool Confirm,
	bool Json,
	// 重定向对拍用的显式输入：HD2 avatar Unit 的 TocData 与 Bones 资源的 hash→名字表。
	// 生产路径应当从 `--game-data` 读（CanonicalAvatarRigReader + UnitBoneNamesReader）；
	// 这两个开关存在的唯一理由是"让 C# 与 Python 实验线吃同一份输入"，对拍才有意义。
	// 追加在末尾并带默认值，是为了不让已有的位置构造调用点被迫改动。
	string? AvatarToc = null,
	string? BoneHash = null,
	// `source convert` 要带哪些配件：`all` = 全部、`none`/空 = 只要七份本体、否则逗号分隔的 bodygroup 名。
	string? Components = null,
	// source convert 生成的素体显示名。
	string? Name = null,
	// source convert 的材质来源：分号分隔的 VPK 文件或目录。材质在**别的** VPK 里（见 docs 第 3 轮）。
	string? MaterialSource = null,
	// source convert 的底色压暗系数（0..1）。不给就不压。
	double? AlbedoFactor = null,
	// matcap 怎么处理：`bake`（默认，把平均色烘进底色）或 `albedo`（直接拿 matcap 当底色）。
	// `albedo` 让"只换 matcap"的包（C1/C2 那种）变成真正可换的 patch，代价是观感需要实机对照。
	string? Matcap = null,
	// 强制指定每个部位用哪个 Unit 当**外壳**：`Head=0x…,Chest=0x…`。
	//
	// 为什么需要它：外壳决定 Unit 内部**网格表的排法**（lod0 落在第几格）。原版护甲件的 Piece
	// 会指定"用这个 Unit 的第几个网格"——Armor/头盔要索引 4、Undergarment 要索引 5。
	// 随便挑外壳时排法对不上，写进同键位置后就会出现"只有头、没有身体"。
	// 指定成目标护甲自己的 Unit，排法就天然一致。
	string? Donor = null,
	// `wardrobe validate` 要校验的掩码文件（`wardrobe.masks`）。
	// 单独给一个开关而不是复用 --plan：掩码不是计划，它是部署产物，误把计划文件传进来必须报错。
	string? File = null,
	// 构建期 section 映射表（`sections.json`）。`wardrobe compile` 缺省时退回规格里的相对路径。
	string? Sections = null,
	// `vpk2patch --dry-run`：只算"哪些能转、哪些跳过、哪些是同一个模型"，不真的转换。
	// 管理器导入时要先知道结果才能决定进度条与提示，所以这一步必须能单独跑。
	bool DryRun = false,
	// 材质/贴图 id 的命名空间（见 Hd2AssetId.NamespaceFor）。**一批必须共用同一个值**：
	// 模型 patch 引用的材质 id 要与材质 patch 写出的相等。不给 = 旧行为（id 只由名字决定）。
	string? IdNamespace = null,
	// `vpk rekey` 要烘进产物的禁用 section：`unit:1,2;unit:3`（unit 是来源键，带不带 0x 都行）。
	// 换甲会把 Unit 换到目标护甲的键上，所以这件事必须在换甲**里面**做——只有那里有完整映射。
	string? Disabled = null,
	// 镂空材质的落地方式（见 CutoutStrategy）：`a`（默认，alphaclip + 阈值 0.5）、
	// `b`（alphaclip + 阈值 0.144）、`c`（留在 advanced，遮罩进 SclarFieldOpacity + 硬边）。
	// 只影响"源侧要求透明且 alpha 真在表达形状"的那批材质，其余材质三档完全一致。
	string? Cutout = null,
	// 直接给命名空间 **token**（形如 `vpk-060d479e4bc677d0`），跳过"名字 → token"那一步。
	//
	// 存在的理由只有一个：**只重转一批里的某一个包**。管理器给的 `--id-namespace` 是来源压缩包名，
	// 而用户日后重转单个 VPK 时手上只有那个 VPK、没有原压缩包名——拿不到同一个 token，
	// 算出的材质 id 与模型 patch 里引用的对不上，材质静默失效。
	// 有了它就能把那次导入的 token 原样接上。
	//
	// 正常调用方（管理器）仍然传 `--id-namespace <来源名>`，让派生规则只有一份。
	string? IdNamespaceToken = null,
	// matcap 烘进底色时的亮度增益（默认 1.0 = 不加）。
	// 实机反馈"金属太黑、没反光"，而 matcap 是加法高光图、平均色偏暗，所以留这个旋钮。
	double? MatcapGain = null,
	// matcap 承载面（金属装饰件）的粗糙度，0..1。0.08 = 镀铬，0.35 = 拉丝/氧化。
	// 只能靠实机定，所以留成旋钮；省略时用 MaterialAppearance.DefaultMetalRoughness。
	double? MetalRoughness = null,
	// "既要镂空又是金属"时怎么取舍：cutout = 保镂空（默认）、metal = 保金属。
	// HD2 的 advanced 有金属度没镂空、alphaclip 有镂空没金属度，两者无法共存（实机已验证）。
	string? MetalCutout = null,
	// 镂空件用哪个模板：alphaclip（默认，无天气掩码）/ original（有天气掩码，无金属度）。
	string? CutoutTemplate = null,
	// 把每个 Unit 的**身份**钉成某件游戏护甲自己的那一行（id + 身形/槽位/重量/层）。
	// 见 VpkUnitIdentityOverride：不给就是"自定义素体"形态（随机 id + BodyType_Any）。
	string? UnitIds = null,
	// 重构建用：显式给一份 `custom-model.json`，沿用它的**素体 key 与 unit id 基**。
	//
	// 为什么需要它：unit id 是 `seed + index` 生成的，而 seed 平时来自"输出目录里已有的
	// custom-model.json"（重跑同一个目录就复用）。重构建为了不毁掉现有产物，先转到临时目录、
	// 校验通过才换进去——那个临时目录里当然没有旧文件，于是 id 会重新随机，
	// 结果就是 wardrobe 里记的开关掩码、真映射写进 dl_bin 的件表全部失效。
	// 有了这个参数，重构建只要把旧的那份指过来，产出的 id 就与原来逐位相同。
	string? ReuseModel = null,
	// `vpk rekey` 的头身合并：`on` 把来源的头 Unit 落到护甲套件的胸部槽位上，	// 让头盔存档一件模型都不剩；`off`（默认）是老做法（头由头盔套件承载，玩家得穿配套头盔）。
	//
	// 默认关的理由：它改变的是"头画在哪"，还会让那件头盔 item 变成空模型——必须由用户明确打开。
	// 层号不是写死的：按目标护甲胸部槽位实际有几层算（最外层放头、次外层放身体），
	// 只有一层时自动退回老做法。见 CliVpkRekey.ResolveHeadBodyMerge。
	string? HeadMerge = null,
	// **头身合并的用户同意标记**：`--head-merge on` 必须同时带 `--head-merge-consent`，
	// 否则 CLI 直接拒绝。理由：这个功能改的是"头画在哪"，出问题要用户自己承担——
	// 调用它的 agent 必须先把这个风险讲清楚、拿到用户明确同意，再带上这个标记。
	bool HeadMergeConsent = false,
	// 从**清单文件**读要转换的 VPK（每行一个绝对路径，空行与 `#` 开头的行忽略）。
	//
	// 为什么需要它：Windows 命令行上限是 32767 字符，而一个中文整合包里 140 个 VPK 的绝对路径
	// 就有近 3 万字符——拼进参数会在 CreateProcess 那一步失败，报的还是「文件名或扩展名太长」
	// （Win32 206），看起来像路径太深，实际是命令行太长。
	string? VpkList = null,
	// 同上，给 `--material-source` 用的清单文件（解析集合）。
	string? MaterialSourceList = null,
	// 组件开关包的清单（只用于**识别开关**，不进解析集合）。分主体转换时，开关包在别的组里，
	// 不单独指过来就翻译不出 components.json。
	string? ComponentSourceList = null,
	// 强制这批输入按"模型"还是"材质包"转换（`model` / `material`）。
	// 不传 = 按"有没有 .mdl"自动判断（历史行为）。
	//
	// 为什么需要它：整合包里常有"材质包里塞了摆姿势的展示模型 / 动画模型"的包，自动判断会把整包
	// 当角色去转换，然后死在模型解析上（实测报 `MDL 骨骼数量异常：0`），而它其实是整只角色的主材质。
	// 规划阶段靠材质目录已经能认出来，这里把那个结论显式交给转换器，不在转换阶段再猜一次。
	string? TreatAs = null,
	// 逐 VPK 的类型清单（`<绝对路径>\t model|material`，每行一个）。
	//
	// 为什么需要第二个开关：`--treat-as` 的效果作用于**整次调用的每一个输入**，而一次"整批转换"
	// 里模型和材质包是**混着**的（模型出 unit patch、材质包出材质 patch）。混着传 `--treat-as model`
	// 会把材质包当角色去转（死在 `MDL 骨骼数量异常：0`），不传又会把"塞了展示模型的材质包"当角色。
	// 规划阶段本来就知道每个 VPK 是什么，这个清单就是把那份结论原样交给转换器。
	string? TreatAsList = null,
	// 半透明落地方式的实验开关（none / translucent / advanced-opacity）。不传 = 现有行为。
	//
	// 三个变体必须**同命名空间**才有意义：实验开关不改材质 id，模型 patch 才能只换一次，
	// 之后换材质包即可对照。见 AlphaExperiment。
	string? AlphaExperiment = null,
	// 抖动的点大小（1 = 每像素一个点，2 = 2×2 的点，默认 2）。只影响颗粒粗细，不影响等效透明度。
	int? DitherScale = null,
	// 抖动密度（0.05..1，默认 0.75）：乘在 alpha 上，越小点越少、越透。点大小与密度是两个独立旋钮。
	double? DitherDensity = null,
	// 抖动阈值图案（ign = 无可见结构，默认；ayer = 8×8 有序矩阵，会看出方格）。
	string? DitherPattern = null,
	// 源法线图的 Y（绿）通道要不要翻：DirectX 与 OpenGL 的绿通道相反，而 HD2 的法线约定没有文档。
	// 不传 = 不翻（照源侧原样）。猜错的表现是"凸的看起来是凹的"，只能在实机上看出来。
	bool? NormalFlipY = null,
	// 源侧 `$no_draw`（"这一块不画"）怎么落地：show（默认，照常显示）/ hide（几何保留+不显示）/ drop。
	// L4D2 里它只是材质层开关，网格仍在文件里，作者常拿它做变体（猫耳/无猫耳）；
	// 直接丢几何会把数据一起删掉，导入之后只能改包重导。
	string? NoDraw = null,
	// --cutout-geometry <材质,材质>：把这些材质的「镂空」裁成几何（见 CutoutMesh），
	// 于是它们能走 advanced 拿到金属度。空 = 不做，行为与以前逐字节一致。
	string? CutoutGeometry = null);

public sealed record CliInvocation(IReadOnlyList<string> Command, CliGlobalOptions Options);

public static class CliArgumentParser
{
	public static CliInvocation Parse(string[] args)
	{
		ArgumentNullException.ThrowIfNull(args);
		string? managerRoot = null;
		string? library = null;
		string? gameData = null;
		string? registryRoot = null;
		string? output = null;
		string? plan = null;
		string? host = null;
		string? addToProfile = null;
		string? keys = null;
		string? addKeys = null;
		string? removeKeys = null;
		string? archiveId = null;
		string? kind = null;
		string? header = null;
		string? avatarToc = null;
		string? boneHash = null;
		string? components = null;
		string? name = null;
		string? materialSource = null;
		double? albedoFactor = null;
		string? matcap = null;
		string? donor = null;
		string? file = null;
		string? sections = null;
		string? idNamespace = null;
		string? disabled = null;
		string? cutout = null;
		string? idNamespaceToken = null;
		double? matcapGain = null;
		double? metalRoughness = null;
		string? metalCutout = null;
		string? cutoutTemplate = null;
		string? unitIds = null;
		string? reuseModel = null;
		string? headMerge = null;
		var headMergeConsent = false;
		string? vpkList = null;
		string? materialSourceList = null;
		string? componentSourceList = null;
		string? treatAs = null;
		string? treatAsList = null;
		string? alphaExperiment = null;
		int? ditherScale = null;
		double? ditherDensity = null;
		string? ditherPattern = null;
		bool? normalFlipY = null;
		string? noDraw = null;
		string? cutoutGeometry = null;
		var picks = new List<string>();
		int? position = null;
		int? limit = null;
		string? cursor = null;
		var setActive = false;
		var withUnsharedOptions = false;
		var withEffective = false;
		var onlyCompetition = false;
		var overwrite = false;
		var confirm = false;
		var json = false;
		var dryRun = false;
		var command = new List<string>();

		for (var index = 0; index < args.Length; index++)
		{
			var argument = args[index];
			switch (argument)
			{
				case "--manager-root":
					managerRoot = ReadValue(args, ref index, argument);
					break;
				case "--library":
					library = ReadValue(args, ref index, argument);
					break;
				case "--game-data":
					gameData = ReadValue(args, ref index, argument);
					break;
				case "--registry-root":
					registryRoot = ReadValue(args, ref index, argument);
					break;
				case "--output":
					output = ReadValue(args, ref index, argument);
					break;
				case "--plan":
					plan = ReadValue(args, ref index, argument);
					break;
				case "--host":
					host = ReadValue(args, ref index, argument);
					break;
				case "--add-to-profile":
					addToProfile = ReadValue(args, ref index, argument);
					break;
				case "--keys":
					keys = ReadValue(args, ref index, argument);
					break;
				case "--add-key":
					addKeys = ReadValue(args, ref index, argument);
					break;
				case "--remove-key":
					removeKeys = ReadValue(args, ref index, argument);
					break;
				case "--archive-id":
					archiveId = ReadValue(args, ref index, argument);
					break;
				case "--kind":
					kind = ReadValue(args, ref index, argument);
					break;
				case "--header":
					header = ReadValue(args, ref index, argument);
					break;
				case "--avatar-toc":
					avatarToc = ReadValue(args, ref index, argument);
					break;
				case "--bone-hash":
					boneHash = ReadValue(args, ref index, argument);
					break;
				case "--components":
					components = ReadValue(args, ref index, argument);
					break;
				case "--name":
					name = ReadValue(args, ref index, argument);
					break;
				case "--material-source":
					materialSource = ReadValue(args, ref index, argument);
					break;
				case "--albedo-factor":
					var rawAlbedoFactor = ReadValue(args, ref index, argument);
					if (!double.TryParse(rawAlbedoFactor, System.Globalization.NumberStyles.Float,
							System.Globalization.CultureInfo.InvariantCulture, out var parsedAlbedoFactor)
						|| parsedAlbedoFactor <= 0 || parsedAlbedoFactor > 4)
					{
						throw new CliUserException("InvalidOption",
							$"--albedo-factor 需要一个 (0, 4] 的数（底色压暗倍数），实际是 '{rawAlbedoFactor}'。");
					}

					albedoFactor = parsedAlbedoFactor;
					break;
				case "--donor":
					donor = ReadValue(args, ref index, argument);
					break;
				case "--file":
					file = ReadValue(args, ref index, argument);
					break;
				case "--sections":
					sections = ReadValue(args, ref index, argument);
					break;
				case "--id-namespace":
					idNamespace = ReadValue(args, ref index, argument);
					break;
				case "--id-namespace-token":
					var rawToken = ReadValue(args, ref index, argument).Trim();
					// 形状：`vpk-` + 16 位十六进制（= sha256 十六进制串的前 8 字节，见 Hd2AssetId.NamespaceFor）。
					if (!rawToken.StartsWith("vpk-", StringComparison.OrdinalIgnoreCase)
						|| rawToken.Length != 20
						|| !rawToken[4..].All(Uri.IsHexDigit))
					{
						throw new CliUserException("InvalidOption",
							$"--id-namespace-token 要的是 token 本身（形如 vpk-060d479e4bc677d0），实际是 '{rawToken}'。"
							+ "要按来源名派生请用 --id-namespace。");
					}

					idNamespaceToken = rawToken.ToLowerInvariant();
					break;
				case "--unit-ids":
					unitIds = ReadValue(args, ref index, argument);
					break;
				case "--reuse-model":
					reuseModel = ReadValue(args, ref index, argument);
					break;
				case "--vpk-list":
					vpkList = ReadValue(args, ref index, argument);
					break;
				case "--material-source-list":
					materialSourceList = ReadValue(args, ref index, argument);
					break;
				case "--component-source-list":
					componentSourceList = ReadValue(args, ref index, argument);
					break;
				case "--treat-as":
					var rawTreatAs = ReadValue(args, ref index, argument).Trim().ToLowerInvariant();
					if (rawTreatAs is not ("model" or "material"))
					{
						throw new CliUserException("InvalidOption",
							$"--treat-as 只接受 model / material，实际是 '{rawTreatAs}'。"
							+ "不传就按有没有 .mdl 自动判断。");
					}

					treatAs = rawTreatAs;
					break;
				case "--treat-as-list":
					treatAsList = ReadValue(args, ref index, argument);
					break;
				case "--dither-scale":
					var rawDitherScale = ReadValue(args, ref index, argument);
					if (!int.TryParse(rawDitherScale, out var parsedDitherScale) || parsedDitherScale is < 1 or > 8)
					{
						throw new CliUserException("InvalidOption",
							$"--dither-scale 需要 1..8 的整数，实际是 '{rawDitherScale}'。1 = 每像素一个点（最细），2 = 2×2 的点（默认，颗粒更轻）。");
					}

						ditherScale = parsedDitherScale;
						break;
					case "--dither-pattern":
					var rawPattern = ReadValue(args, ref index, argument).Trim().ToLowerInvariant();
					if (rawPattern is not ("ign" or "bayer"))
					{
						throw new CliUserException("InvalidOption",
							$"--dither-pattern 只接受 ign / bayer，实际是 '{rawPattern}'。ign = 无可见结构（默认），bayer = 8×8 有序矩阵（能看出方格）。");
					}

								ditherPattern = rawPattern;
						break;
					case "--normal-flip-y":
					var rawFlip = ReadValue(args, ref index, argument).Trim().ToLowerInvariant();
					normalFlipY = rawFlip switch
					{
						"1" or "on" or "true" or "yes" => true,
						"0" or "off" or "false" or "no" => false,
						_ => throw new CliUserException("InvalidOption",
							$"--normal-flip-y 只接受 on / off，实际是 '{rawFlip}'。"
							+ "它翻的是源法线图绿通道的方向（DirectX ↔ OpenGL）：猜错时凸面看起来像凹面，"
							+ "只能在实机上对比，所以做成开关。"),
					};
					break;
				case "--cutout-geometry":
					cutoutGeometry = ReadValue(args, ref index, argument);
					break;
				case "--no-draw":
					var rawNoDraw = ReadValue(args, ref index, argument).Trim().ToLowerInvariant();
					if (rawNoDraw is not ("hide" or "show" or "drop"))
					{
						throw new CliUserException("InvalidOption",
							$"--no-draw 只接受 hide / show / drop，实际是 '{rawNoDraw}'。"
							+ "源侧的 $no_draw 是「这一块不画」，L4D2 里它只是材质层开关（网格还在文件里），"
							+ "作者常拿它做变体；show = 照常显示（默认，想隐藏用 section 组件编辑器关掉），"
							+ "hide = 保留几何但做成不显示，drop = 连几何一起丢（不可恢复）。");
					}
					noDraw = rawNoDraw;
					break;
					case "--dither-density":
					var rawDitherDensity = ReadValue(args, ref index, argument);
					if (!double.TryParse(rawDitherDensity, System.Globalization.NumberStyles.Float,
							System.Globalization.CultureInfo.InvariantCulture, out var parsedDensity)
						|| parsedDensity is < 0.05 or > 1.0)
					{
						throw new CliUserException("InvalidOption",
							$"--dither-density 需要 0.05..1 的数，实际是 '{rawDitherDensity}'。1 = 忠实还原源侧 alpha，越小点越少越透（默认 0.75）。");
					}

								ditherDensity = parsedDensity;
						break;
					case "--alpha-experiment":
					var rawAlphaExperiment = ReadValue(args, ref index, argument).Trim().ToLowerInvariant();
					if (rawAlphaExperiment is not ("none" or "translucent" or "advanced-opacity"))
					{
						throw new CliUserException("InvalidOption",
							$"--alpha-experiment 只接受 none / translucent / advanced-opacity，实际是 '{rawAlphaExperiment}'。");
					}

					alphaExperiment = rawAlphaExperiment;
					break;
				case "--head-merge-consent":
					headMergeConsent = true;
					break;
				case "--head-merge":
					var rawHeadMerge = ReadValue(args, ref index, argument).Trim().ToLowerInvariant();
					if (rawHeadMerge is not ("on" or "off" or "true" or "false" or "1" or "0"))
					{
						throw new CliUserException("InvalidOption",
							$"--head-merge 只接受 on / off，实际是 '{rawHeadMerge}'。"
							+ "off（默认）= 头由头盔套件承载（玩家需要穿配套头盔）；"
							+ "on = 头并进护甲套件的胸部槽位、头盔 item 不再有模型。");
					}

					headMerge = rawHeadMerge is "on" or "true" or "1" ? "on" : "off";
					break;
				case "--disabled":
					disabled = ReadValue(args, ref index, argument);
					break;
				case "--cutout":
					var rawCutout = ReadValue(args, ref index, argument);
					var normalizedCutout = rawCutout.ToLowerInvariant();
					if (normalizedCutout is "c" or "advanced")
					{
						throw new CliUserException("InvalidOption",
							"--cutout c 已删除：实机对照证明把遮罩塞进 advanced 的 SclarFieldOpacity 完全没效果，"
							+ "而那个槽在现役模组里装的是 1×1 R8 标量（整体不透明度），本来就做不了镂空。");
					}

					if (normalizedCutout is not ("a" or "b" or "alphaclip" or "tuned"
						or "rgb-inv" or "alpha" or "alpha-inv" or "dither" or "dither-strong"))
					{
						throw new CliUserException("InvalidOption",
							$"--cutout 只接受 a/b/rgb-inv/alpha/alpha-inv/dither/dither-strong，实际是 '{rawCutout}'。"
							+ "a = RGB 存遮罩、阈值 0.5（旧行为）；b = 同 a 但阈值 0.144；"
							+ "rgb-inv = RGB 存遮罩并取反；alpha = 遮罩存 alpha 通道；alpha-inv = 遮罩存 alpha 并取反；"
							+ "dither = 把渐变 alpha 抖成二值以近似半透明（只抖中间值）；"
							+ "dither-strong = 同上但整片参与抖动、alpha 先乘 1.35。");
					}

					cutout = normalizedCutout;
					break;
				case "--matcap-gain":
					var rawGain = ReadValue(args, ref index, argument);
					if (!double.TryParse(rawGain, System.Globalization.NumberStyles.Float,
							System.Globalization.CultureInfo.InvariantCulture, out var parsedGain)
						|| parsedGain <= 0 || parsedGain > 16)
					{
						throw new CliUserException("InvalidOption",
							$"--matcap-gain 需要一个 (0, 16] 的数，实际是 '{rawGain}'。1.0 = 不增益（默认）。");
					}

					matcapGain = parsedGain;
					break;
				case "--metal-roughness":
					// matcap 承载面的粗糙度：0.35 = 拉丝/氧化，0.08 以下才是镀铬。
					// 只能靠实机定，所以留成旋钮（默认见 MaterialAppearance.DefaultMetalRoughness）。
					var rawRoughness = ReadValue(args, ref index, argument);
					if (!double.TryParse(rawRoughness, System.Globalization.NumberStyles.Float,
							System.Globalization.CultureInfo.InvariantCulture, out var parsedRoughness)
						|| parsedRoughness < 0 || parsedRoughness > 1)
					{
						throw new CliUserException("InvalidOption",
							$"--metal-roughness 需要 0..1 的数，实际是 '{rawRoughness}'。0.08 = 镀铬，0.35 = 拉丝/氧化。");
					}

					metalRoughness = parsedRoughness;
					break;
				case "--metal-cutout":
					var rawCutoutPolicy = ReadValue(args, ref index, argument);
					if (!rawCutoutPolicy.Equals("cutout", StringComparison.OrdinalIgnoreCase)
						&& !rawCutoutPolicy.Equals("metal", StringComparison.OrdinalIgnoreCase))
					{
						throw new CliUserException("InvalidOption",
							$"--metal-cutout 只接受 cutout / metal，实际是 '{rawCutoutPolicy}'。"
							+ "HD2 里镂空与金属无法共存：cutout = 保镂空（默认）、metal = 保金属。");
					}

					metalCutout = rawCutoutPolicy.ToLowerInvariant();
					break;
				case "--cutout-template":
					var rawCutoutTemplate = ReadValue(args, ref index, argument);
					if (!rawCutoutTemplate.Equals("alphaclip", StringComparison.OrdinalIgnoreCase)
						&& !rawCutoutTemplate.Equals("original", StringComparison.OrdinalIgnoreCase))
					{
						throw new CliUserException("InvalidOption",
							$"--cutout-template 只接受 alphaclip / original，实际是 '{rawCutoutTemplate}'。"
							+ "alphaclip 支持逐像素镂空但没有金属度；original 已实机证伪（没有镂空），仅留档。");
					}

					cutoutTemplate = rawCutoutTemplate.ToLowerInvariant();
					break;
				case "--matcap":
					var rawMatcap = ReadValue(args, ref index, argument);
					if (!rawMatcap.Equals("bake", StringComparison.OrdinalIgnoreCase)
						&& !rawMatcap.Equals("albedo", StringComparison.OrdinalIgnoreCase)
						&& !rawMatcap.Equals("metal", StringComparison.OrdinalIgnoreCase))
					{
						throw new CliUserException("InvalidOption",
							$"--matcap 只接受 bake / albedo / metal，实际是 '{rawMatcap}'。" +
							"bake = 把 matcap 的平均色烘进底色（旧行为）；albedo = 直接拿 matcap 当底色；" +
							"metal = 归一成金属 F0（保留色相、丢掉亮度图案）。");
					}

					matcap = rawMatcap.ToLowerInvariant();
					break;
				case "--pick":
					// Positional triple: <patch.toc> <0xtypeid> <0xfileid>. Repeated.
					picks.Add(ReadValue(args, ref index, argument));
					picks.Add(ReadValue(args, ref index, argument));
					picks.Add(ReadValue(args, ref index, argument));
					break;
				case "--position":
					var rawPosition = ReadValue(args, ref index, argument);
					if (!int.TryParse(rawPosition, out var parsedPosition) || parsedPosition < 1)
						throw new CliUserException("PositionInvalid", "参数 --position 必须是从 1 开始的正整数。");
					position = parsedPosition;
					break;
				case "--limit":
					var rawLimit = ReadValue(args, ref index, argument);
					if (!int.TryParse(rawLimit, out var parsedLimit) || parsedLimit < 1 || parsedLimit > 500)
						throw new CliUserException("LimitInvalid", "参数 --limit 必须是 1 到 500 之间的整数。 ");
					limit = parsedLimit;
					break;
				case "--cursor":
					cursor = ReadValue(args, ref index, argument);
					break;
				case "--set-active":
					setActive = true;
					break;
				case "--with-unshared-options":
					withUnsharedOptions = true;
					break;
				case "--with-effective":
					withEffective = true;
					break;
				case "--only-competition":
					onlyCompetition = true;
					break;
				case "--overwrite":
					overwrite = true;
					break;
				case "--confirm":
					confirm = true;
					break;
				case "--dry-run":
					dryRun = true;
					break;
				case "--json":
					json = true;
					break;
				case "--help":
				case "-h":
					command.Add("help");
					break;
				case "--version":
					command.Add("version");
					break;
				default:
					if (argument.StartsWith("-", StringComparison.Ordinal))
						throw new CliUserException("UnknownOption", $"不支持的参数：{argument}");
					command.Add(argument);
					break;
			}
		}

		if (command.Count == 0) command.Add("help");
		return new CliInvocation(command, new CliGlobalOptions(managerRoot, library, gameData, registryRoot, output, plan, host, addToProfile, keys, addKeys, removeKeys, archiveId, kind, header, picks, position, limit, cursor, setActive, withUnsharedOptions, withEffective, onlyCompetition, overwrite, confirm, json, avatarToc, boneHash, components, name, materialSource, albedoFactor, matcap, donor, file, sections, dryRun, idNamespace, disabled, cutout, idNamespaceToken, matcapGain, metalRoughness, metalCutout, cutoutTemplate, unitIds, reuseModel, headMerge, headMergeConsent, vpkList, materialSourceList, componentSourceList, treatAs, treatAsList, alphaExperiment, ditherScale, ditherDensity, ditherPattern, normalFlipY, noDraw, cutoutGeometry));
	}

	private static string ReadValue(string[] args, ref int index, string option)
	{
		if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]) || args[index].StartsWith("-", StringComparison.Ordinal))
			throw new CliUserException("MissingOptionValue", $"参数 {option} 需要一个值。");
		return args[index];
	}
}
