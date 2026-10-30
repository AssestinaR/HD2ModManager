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
	string? Disabled = null);

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
				case "--disabled":
					disabled = ReadValue(args, ref index, argument);
					break;
				case "--matcap":
					var rawMatcap = ReadValue(args, ref index, argument);
					if (!rawMatcap.Equals("bake", StringComparison.OrdinalIgnoreCase)
						&& !rawMatcap.Equals("albedo", StringComparison.OrdinalIgnoreCase))
					{
						throw new CliUserException("InvalidOption",
							$"--matcap 只接受 bake 或 albedo，实际是 '{rawMatcap}'。" +
							"bake = 把 matcap 的平均色烘进底色（旧行为）；albedo = 直接拿 matcap 当底色。");
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
		return new CliInvocation(command, new CliGlobalOptions(managerRoot, library, gameData, registryRoot, output, plan, host, addToProfile, keys, addKeys, removeKeys, archiveId, kind, header, picks, position, limit, cursor, setActive, withUnsharedOptions, withEffective, onlyCompetition, overwrite, confirm, json, avatarToc, boneHash, components, name, materialSource, albedoFactor, matcap, donor, file, sections, dryRun, idNamespace, disabled));
	}

	private static string ReadValue(string[] args, ref int index, string option)
	{
		if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]) || args[index].StartsWith("-", StringComparison.Ordinal))
			throw new CliUserException("MissingOptionValue", $"参数 {option} 需要一个值。");
		return args[index];
	}
}
