using HD2SourceModel;

namespace HD2SourceMigration.VpkConversion;

/// <summary>一个 VPK 在这次导入里算什么。</summary>
public enum VpkImportKind
{
	/// <summary>含角色模型：要转成 Unit patch。</summary>
	CharacterModel,

	/// <summary>不含模型、只带材质/贴图：要转成材质包。</summary>
	Material,

	/// <summary>
	/// 组件开关包：几份 <c>$no_draw</c> 材质，用来"让某个部件不画"。
	/// <para>
	/// 它<b>不产出 patch</b>，但也不是"不支持"——HD2 侧的等价物是 section 开关
	/// （<c>WardrobeSectionMap</c> + 烘焙）。识别它，是为了把它的名字与材质表交给
	/// 后续步骤翻译成"关掉哪几个 section"，并在界面上显示成"猫耳 关/开"而不是"Section 7"。
	/// </para>
	/// </summary>
	ComponentSwitch,

	/// <summary>
	/// 换贴图 / 换渲染参数的<b>材质覆盖包</b>：很小、不带贴图、VMT 也不是 <c>$no_draw</c>。
	/// <para>
	/// 实测样本：Manuka 的 <c>文身关</c> / <c>切换纹身</c> 把 <c>$basetexture</c> 从
	/// <c>Manuka_body_Tattoo</c> 换回 <c>Manuka_body</c>——它在源侧是一个开关，但换的是<b>图</b>，
	/// 不是可见性。HD2 的 section 开关只能"不画"，表达不了"换一张图"，所以它也不该被当成
	/// 一份能用的材质包（那会产出一个没有任何 Unit 引用的材质，用户看到的是一个什么都不做的 Mod）。
	/// </para>
	/// </summary>
	MaterialVariant,

	/// <summary>不是我们能忠实地翻译的东西（道具、武器、其它 <c>$no_draw</c> 用途…）。</summary>
	Unsupported,
}

/// <summary>
/// 一个模型的几何指纹。
/// <para>
/// <b>为什么要指纹。</b>L4D2 的"8 人替换包"是同一个角色按 8 个 survivor 各导出一份，
/// 8 个 VPK 里的模型<b>几何完全一致</b>（实测 92 材质 / 121511 顶点 / 159363 三角形），
/// 差别只在 mdl 里的骨架名与校验和。逐份转换会得到 8 套一模一样的 Unit，
/// 既浪费又会在部署时互相覆盖——所以必须认出"这是同一个模型"。
/// </para>
/// <para>
/// <b>为什么不用 mdl 的校验和</b>：那个字段每份都不同（骨架名不同就会变），
/// 它区分的是"文件不同"，而我们要的是"模型相同"。
/// </para>
/// </summary>
public sealed record VpkModelSignature(int MaterialCount, int Lod0VertexCount, int TriangleCount)
{
	public override string ToString()
		=> $"{MaterialCount} 材质 / {Lod0VertexCount} 顶点 / {TriangleCount} 三角形";
}

/// <summary>规划结果里的一条：这个 VPK 要转、跳过，或者与别人是同一个模型。</summary>
public sealed record VpkImportCandidate(
	string VpkPath,
	VpkImportKind Kind,
	VpkModelSignature? Signature,
	string? SkipReason,
	string? DuplicateOfVpkPath,
	IReadOnlyCollection<string> OwnedMaterialNames,
	IReadOnlyCollection<string> OwnedTextureNames)
{
	/// <summary>这次要不要真的转换它。</summary>
	public bool IsConvertible
		=> Kind is VpkImportKind.CharacterModel or VpkImportKind.Material
			&& SkipReason is null
			&& DuplicateOfVpkPath is null;

	/// <summary>
	/// 给人看的名字。目前只有 <see cref="VpkImportKind.ComponentSwitch"/> 用它
	/// （<c>猫耳</c>、<c>披风</c>），其余情况为 <c>null</c>，调用方退回文件名。
	/// </summary>
	public string? DisplayName { get; init; }

	/// <summary>
	/// 这个包里有没有 <c>.mdl</c>（不论它最后被判成什么）。
	/// <para>
	/// 用来区分"模型层面的失败"和"材质层面的失败"：前者（模型读不出来 / 不是角色）在一份
	/// 材质包里只是附属内容，可以整包改判成材质包；后者（<c>$no_draw</c> 之类）改判没有意义。
	/// </para>
	/// </summary>
	public bool HasModelContent { get; init; }
}

/// <summary>
/// 一个组件开关包：<b>名字</b> + 它要藏起来的<b>材质裸名</b>。
/// <para>
/// 源侧靠"覆盖同名材质为 <c>$no_draw</c>"来隐藏部件（Manuka 这类包用 <c>opt/</c> 影子目录，
/// 橘雪莉那类直接覆盖同名路径）。所以"一个开关 = 一组材质名"，把它翻成 HD2 的 section 掩码
/// 只需要再知道"这些材质落在哪几个 section"——那由 <c>sections.json</c> 的
/// <c>sourceMaterial</c> 回答。
/// </para>
/// </summary>
public sealed record VpkComponentSwitch(
	string VpkPath,
	string DisplayName,
	IReadOnlyList<string> MaterialStems);

/// <summary>一次导入的规划：每个 VPK 的去向与理由。</summary>
public sealed record VpkImportPlan(IReadOnlyList<VpkImportCandidate> Candidates)
{
	public IReadOnlyList<VpkImportCandidate> Convertible
		=> Candidates.Where(candidate => candidate.IsConvertible).ToArray();

	public IReadOnlyList<VpkImportCandidate> Skipped
		=> Candidates.Where(candidate => !candidate.IsConvertible).ToArray();

	public IReadOnlyList<VpkImportCandidate> CharacterModels
		=> Candidates.Where(candidate => candidate.IsConvertible && candidate.Kind == VpkImportKind.CharacterModel).ToArray();

	public IReadOnlyList<VpkImportCandidate> Materials
		=> Candidates.Where(candidate => candidate.IsConvertible && candidate.Kind == VpkImportKind.Material).ToArray();

	/// <summary>
	/// 「模型主体 + 它自己的材质包」。一个整合包里有几套角色时，它就是"库里有几个主体、
	/// 每个主体下有哪些材质选项"的答案。见 <c>VpkImportPlanner.BuildModelGroups</c>。
	/// </summary>
	public IReadOnlyList<VpkModelGroup> Groups { get; init; } = [];

	/// <summary>认不出属于哪个模型的材质包：跳过并点名（见 <see cref="Groups"/> 的判据）。</summary>
	public IReadOnlyList<string> UnassignedMaterials { get; init; } = [];
}

/// <summary>
/// 一个「模型主体组」：一个模型 VPK + 属于它的材质包。
/// <para>
/// <b>为什么要按主体分组而不是整批一起转。</b>整合包里几套角色的材质<b>裸名相同</b>，
/// 而解析是按裸名先到先得的——不分组就会串味；材质 id 又只在同一命名空间内唯一，
/// 不分组两套角色也装不到一起。所以每组各用一个命名空间、各用一份解析集合。
/// </para>
/// </summary>
public sealed record VpkModelGroup(
	string ModelVpkPath,
	string DisplayName,
	string IdNamespaceSource,
	IReadOnlyList<string> MaterialVpkPaths);

/// <summary>
/// 决定"这批 VPK 里哪些值得转、哪些跳过、哪些是同一个模型"。
/// <para>
/// <b>为什么需要它。</b>一个 L4D2 角色包（实测 60 个 VPK / 357 MB）里通常只有
/// 9 个模型包 + 14 个材质包是可翻译的，剩下的是道具、武器和 <c>$no_draw</c> 开关。
/// 全部硬转既慢又会产出一堆空包，所以先把"能不能转"算清楚，再让转换器只干该干的活。
/// </para>
/// <para><b>三条规则</b></para>
/// <list type="number">
/// <item>含 <c>.mdl</c> 且骨架是 ValveBiped 角色骨架 → 模型；否则（道具/武器）跳过。</item>
/// <item>几何指纹相同的模型包是同一个模型的复制品 → 只转第一份（按路径序），其余合并说明。</item>
/// <item>只有材质的包：它定义的名字必须被某个角色模型（直接或经 <c>$basetexture</c> 等间接）引用，
/// 否则跳过；整包都是 <c>$no_draw</c> 的跳过（HD2 没有"不绘制"这种材质）。</item>
/// </list>
/// <para>
/// <b>健壮身形与纤细身形算同一个模型</b>：指纹只看几何，不看身形；身形是 HD2 外壳的属性，
/// 同一份源模型无论落到哪种身形上都是同一个模型。
/// </para>
/// </summary>
public static class VpkImportPlanner
{
	/// <summary>角色骨架的前缀。重定向的全部依据就是这套骨名。</summary>
	private const string ValveBipedPrefix = "ValveBiped.Bip01_";

	/// <summary>判定"这是角色骨架"至少要有的骨（短名，已剥前缀）。</summary>
	private static readonly string[] RequiredBones = ["Pelvis", "Spine", "Head1", "L_Thigh", "L_UpperArm"];

	/// <summary>
	/// 一个真正带顶点数据的 <c>.vvd</c> 至少要有 64 字节头（<see cref="SourceVertexReader"/> 的门槛）。
	/// <para>
	/// 实测有一类中文模组包把 <c>.vvd</c>/<c>.vtx</c> 打成 <b>28 字节的占位文件</b>：里面只有
	/// <c>IDSV</c> + 版本 + 与 mdl 对得上的校验和 + <c>lodCount=1</c>，一个顶点都没有。
	/// 真正的几何在配套的「基础组件」包里。这种包报"VVD magic 不是 IDSV"纯属误导
	/// （magic 其实是对的，是文件本身没有内容），所以在这里先认出来、给一句能看懂的理由。
	/// </para>
	/// </summary>
	private const int MinimumVertexDataBytes = 64;

	public static VpkImportPlan Plan(IReadOnlyList<string> vpkPaths)
	{
		ArgumentNullException.ThrowIfNull(vpkPaths);
		var paths = vpkPaths
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.Select(Path.GetFullPath)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
			.ToArray();

		var probes = paths.Select(Probe).ToArray();

		// ② 同一个模型只留一份。代表取路径序第一个——它由"导入时看到的文件名"决定，不是随机的。
		var representativeBySignature = new Dictionary<VpkModelSignature, string>();
		var candidates = new List<VpkImportCandidate>(probes.Length);
		foreach (var probe in probes)
		{
			if (probe.Kind != VpkImportKind.CharacterModel || probe.SkipReason is not null)
			{
				candidates.Add(probe);
				continue;
			}

			var signature = probe.Signature!;
			if (representativeBySignature.TryGetValue(signature, out var representative))
			{
				candidates.Add(probe with
				{
					DuplicateOfVpkPath = representative,
					SkipReason = $"与 {Path.GetFileName(representative)} 是同一个模型（{signature}），只转换一份。",
				});
				continue;
			}

			representativeBySignature[signature] = probe.VpkPath;
			candidates.Add(probe);
		}

		// ③ 材质包：必须被角色模型引用（直接名字或经 VMT 里的贴图键间接引用）。
		//    这一批里一个角色模型都没有时，理由要指向"少导入了模型包"这个真正的原因——
		//    否则用户看到"没有被任何角色模型引用"只会以为这个材质包没用。
		var hasCharacterModel = candidates.Any(candidate =>
			candidate.Kind == VpkImportKind.CharacterModel && candidate.DuplicateOfVpkPath is null);
		var referenced = CollectReferencedNames(candidates, paths);
		for (var index = 0; index < candidates.Count; index++)
		{
			var candidate = candidates[index];
			if (candidate.Kind != VpkImportKind.Material || candidate.SkipReason is not null)
				continue;
			if (candidate.OwnedMaterialNames.Any(referenced.Contains) || candidate.OwnedTextureNames.Any(referenced.Contains))
				continue;
			candidates[index] = candidate with
			{
				SkipReason = hasCharacterModel
					? "它带的材质/贴图没有被任何角色模型引用（多半是给别的角色或道具用的）。"
					: "这批里没有可转换的角色模型：材质包要跟引用它的模型包一起导入才有意义。",
			};
		}

		var groups = BuildModelGroups(candidates, out var providerPaths);

		// 供应了某个主体材质的包 = **材质包**，哪怕它里面塞了模型（姿势/动画模型）或那个模型读不出来。
		// 不这样改判，本样本的「基础材质」（78 VMT + 5 个读不出的 mdl）会被判成"不支持"，
		// 而它正是整只角色的主材质——模型那边就一个材质都没有。
		if (providerPaths.Count != 0)
		{
			var providers = providerPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
			for (var index = 0; index < candidates.Count; index++)
			{
				var candidate = candidates[index];
				if (!providers.Contains(candidate.VpkPath)) continue;
				if (candidate.Kind is VpkImportKind.CharacterModel)
				{
					candidates[index] = candidate with
					{
						Kind = VpkImportKind.Material,
						Signature = null,
						SkipReason = null,
					};
				}
				else if (candidate.Kind is VpkImportKind.Unsupported && candidate.HasModelContent)
				{
					// "模型读不出来 / 不是角色"这类理由对材质包不成立：它的模型只是附属内容。
					// $no_draw 之类的**材质层面**失败不在这里改判——那包确实表达不了。
					candidates[index] = candidate with
					{
						Kind = VpkImportKind.Material,
						Signature = null,
						SkipReason = null,
					};
				}
			}
		}

		var assigned = groups
			.SelectMany(group => group.MaterialVpkPaths)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var unassigned = candidates
			.Where(candidate => candidate.Kind == VpkImportKind.Material
				&& candidate.IsConvertible
				&& !assigned.Contains(candidate.VpkPath))
			.Select(candidate => candidate.VpkPath)
			.ToArray();

		// **组里只留真正能转换的材质包。**
		//
		// 分组的判据是"它提供的材质目录对得上这个模型"，而那与"它能不能被翻译成 HD2 材质"是两件事：
		// 组件开关包（`附加材质X关`）和"只换渲染参数"的变体（`组件材质b1/b2/b3/c2`）同样提供
		// 同名目录，但它们要么只是 `$no_draw` 开关、要么只是改几个着色器参数——前者该走组件开关、
		// 后者 HD2 表达不了。把它们留在组里，转换队列就会多出一堆没意义的 Mod。
		var convertiblePaths = candidates
			.Where(candidate => candidate.IsConvertible)
			.Select(candidate => candidate.VpkPath)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var filteredGroups = groups
			.Select(group => group with
			{
				MaterialVpkPaths = group.MaterialVpkPaths
					.Where(convertiblePaths.Contains)
					.ToArray(),
			})
			.ToArray();

		// 过滤之后重新算"没归属的"：只报真正该转却没归属的材质包，不把开关/变体算进去。
		var stillAssigned = filteredGroups
			.SelectMany(group => group.MaterialVpkPaths)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		unassigned = candidates
			.Where(candidate => candidate.Kind == VpkImportKind.Material
				&& candidate.IsConvertible
				&& !stillAssigned.Contains(candidate.VpkPath))
			.Select(candidate => candidate.VpkPath)
			.ToArray();

		return new VpkImportPlan(candidates)
		{
			Groups = filteredGroups,
			UnassignedMaterials = unassigned,
		};
	}

	/// <summary>
	/// 角色模型"直接用到"的材质名，加上这些材质在 VMT 里引用到的贴图名。
	/// <para>
	/// 间接这一步是必须的：A1 的 <c>white.vmt</c> 指向的 <c>white.vtf</c> 只存在于 D1，
	/// 少了这一步 D1 会被判成"没人引用"而跳过，A1 转出来的材质就会缺贴图。
	/// </para>
	/// </summary>
	private static HashSet<string> CollectReferencedNames(
		IReadOnlyList<VpkImportCandidate> candidates,
		IReadOnlyList<string> vpkPaths)
	{
		var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var models = candidates
			.Where(candidate => candidate.Kind == VpkImportKind.CharacterModel && candidate.DuplicateOfVpkPath is null)
			.ToArray();
		if (models.Length == 0)
			return referenced;

		var source = SourceMaterialSource.Open(vpkPaths);
		var pending = new List<string>();
		foreach (var model in models)
		{
			foreach (var name in model.OwnedMaterialNames)
			{
				// 模型的 OwnedMaterialNames 在这里被复用为"模型引用的材质名"。
				if (referenced.Add(name))
					pending.Add(name);
			}
		}

		// 只展开一层：VMT 引用的是贴图，不会再去引用别的 VMT。
		foreach (var name in pending)
		{
			if (!source.TryReadVmt(name, out var bytes))
				continue;
			VmtDocument document;
			try
			{
				document = VmtDocument.Parse(bytes, source.CreateIncludeReader());
			}
			catch (InvalidDataException)
			{
				continue;
			}

			foreach (var (key, value) in document.Values)
			{
				if (!key.StartsWith('$'))
					continue;
				if (!key.EndsWith("texture", StringComparison.OrdinalIgnoreCase)
					&& !key.EndsWith("map", StringComparison.OrdinalIgnoreCase))
					continue;
				var stem = TextureStem(value);
				if (stem.Length != 0)
					referenced.Add(stem);
			}
		}

		return referenced;
	}

	/// <summary>
	/// 单个 VPK 探测失败时，哪些异常该降级成"跳过"而不是让整批失败。
	/// <para>
	/// <b>为什么这个集合必须够宽。</b>探测的职责是<b>分类</b>，不是保证数据合法：用户丢进来的是
	/// 一整包（实测 60 个 VPK），其中任何<b>一个</b>坏文件都不该让另外 59 个导不进来。
	/// 二进制解析遇到畸形数据时抛什么是实现细节——<see cref="NotSupportedException"/>（版本不认识）、
	/// <see cref="IndexOutOfRangeException"/>（偏移越界）、<see cref="OverflowException"/>（长度字段离谱）
	/// 都是"这个文件不对"，不是"程序有 bug"。理由里带上异常类型，真有 bug 时仍然看得见。
	/// </para>
	/// </summary>
	private static bool IsProbeFailure(Exception exception) => exception is
		IOException or InvalidDataException or NotSupportedException or InvalidOperationException
		or UnauthorizedAccessException or KeyNotFoundException or ArgumentException
		or IndexOutOfRangeException or OverflowException or FormatException;

	/// <summary>开关包的条目上限。实测样本是 2–4 条（1–3 份 VMT + addoninfo），留三倍余量。</summary>
	private const int MaximumSwitchEntries = 12;

	/// <summary>
	/// 开关包的"材质字节"上限：只算 <c>.vmt</c>，<b>不算别的文件</b>。
	/// <para>
	/// 实测有包在里面塞了一张 1.5 MB 的 <c>addonimage.jpg</c>（橘雪莉那两个开关），
	/// 按"整包字节"判会把它们判成"不是开关"，于是退回"不支持 <c>$no_draw</c>"的老结论。
	/// 判据只该看材质本身——开关的全部信息就是那几份几百字节的 VMT。
	/// </para>
	/// </summary>
	private const int MaximumSwitchMaterialBytes = 64 * 1024;

	/// <summary>
	/// 一批 VPK 里哪些是<b>组件开关包</b>（<c>$no_draw</c> 覆盖）。
	/// <para>
	/// <b>为什么单独给一个入口而不是让调用方读 <see cref="Plan"/>。</b><see cref="Plan"/> 为了
	/// 判重会去解析每个模型（读 vvd/vtx、数三角形），在几百个包的 L4D2 addons 目录上很贵；
	/// 而"这个包是不是开关"只看目录树与几份几百字节的 VMT。转换前必须知道这件事，
	/// 否则开关包会进材质解析集合，把真材质顶成 <c>$no_draw</c>（见 <see cref="SourceMaterialSource"/>）。
	/// </para>
	/// </summary>
	public static IReadOnlyList<VpkComponentSwitch> FindComponentSwitches(IReadOnlyList<string> vpkPaths)
	{
		ArgumentNullException.ThrowIfNull(vpkPaths);
		var found = new List<VpkComponentSwitch>();
		foreach (var path in vpkPaths)
		{
			if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
				continue;
			try
			{
				using var archive = VpkArchive.Open(path);
				var materials = archive.Entries
					.Where(entry => entry.Extension.Equals("vmt", StringComparison.OrdinalIgnoreCase))
					.Select(entry => entry.FileName)
					.Distinct(StringComparer.OrdinalIgnoreCase)
					.ToArray();
				if (TryDescribeComponentSwitch(archive, path, materials, out var displayName))
					found.Add(new VpkComponentSwitch(Path.GetFullPath(path), displayName, materials));
			}
			catch (Exception exception) when (IsProbeFailure(exception))
			{
				// 打不开的包不在这里报错：它在规划阶段会带着原因出现在"跳过"里。
			}
		}

		return found;
	}

	/// <summary>
	/// 判据（全部来自实测样本，不是猜的）：<b>没有贴图、条目很少、总字节很小、每份 VMT 都只写
	/// <c>$no_draw</c></b>。
	/// <para>
	/// 只看"全是 <c>$no_draw</c>"是不够的：源侧还有别的用法（隐藏血贴花、隐藏描边层），
	/// 那些包可能很大。加上"没有 vtf 且很小"这两条，命中的才真的是"一个开关"。
	/// </para>
	/// </summary>
	private static bool TryDescribeComponentSwitch(
		VpkArchive archive,
		string vpkPath,
		IReadOnlyList<string> materials,
		out string displayName)
	{
		displayName = string.Empty;
		if (materials.Count == 0 || archive.Entries.Count > MaximumSwitchEntries)
			return false;
		if (archive.Entries.Any(entry => entry.Extension.Equals("vtf", StringComparison.OrdinalIgnoreCase)))
			return false;
		if (!MaterialBytesWithinLimit(archive))
			return false;

		foreach (var material in materials)
		{
			if (!IsNoDraw(archive, material))
				return false;
		}

		displayName = DescribeSwitchName(ReadAddonTitle(archive), Path.GetFileNameWithoutExtension(vpkPath));
		return true;
	}

	/// <summary>这份 VMT 是否真的声明了底色（而不是只写着色器名的空壳）。</summary>
	private static bool HasBaseTexture(VpkArchive archive, string materialName)
	{
		var entry = archive.Entries.FirstOrDefault(candidate =>
			candidate.Extension.Equals("vmt", StringComparison.OrdinalIgnoreCase)
			&& candidate.FileName.Equals(materialName, StringComparison.OrdinalIgnoreCase));
		if (entry is null)
			return false;
		try
		{
			var value = VmtDocument.Parse(archive.Read(entry)).Value("$basetexture");
			return !string.IsNullOrWhiteSpace(value);
		}
		catch (InvalidDataException)
		{
			return false;
		}
	}

	/// <summary>只算 <c>.vmt</c> 的字节数是否在开关包的范围内（见 <see cref="MaximumSwitchMaterialBytes"/>）。</summary>
	private static bool MaterialBytesWithinLimit(VpkArchive archive)
	{
		long materialBytes = 0;
		foreach (var entry in archive.Entries)
		{
			if (!entry.Extension.Equals("vmt", StringComparison.OrdinalIgnoreCase))
				continue;
			materialBytes += entry.TotalLength;
			if (materialBytes > MaximumSwitchMaterialBytes)
				return false;
		}

		return true;
	}

	/// <summary>读 <c>addoninfo.txt</c> 里的 <c>addonTitle</c>；读不到返回 <c>null</c>。</summary>
	private static string? ReadAddonTitle(VpkArchive archive)
	{
		var entry = archive.Entries.FirstOrDefault(candidate =>
			candidate.Extension.Equals("txt", StringComparison.OrdinalIgnoreCase)
			&& candidate.FileName.Equals("addoninfo", StringComparison.OrdinalIgnoreCase));
		if (entry is null)
			return null;
		try
		{
			// addoninfo 是键值文本，不是 UTF-8 保证的：中文标题在实测样本里有 GBK 的。
			var bytes = archive.Read(entry);
			var text = System.Text.Encoding.UTF8.GetString(bytes);
			if (text.Contains('\uFFFD', StringComparison.Ordinal))
				text = System.Text.Encoding.GetEncoding(936).GetString(bytes);
			foreach (var line in text.Split('\n'))
			{
				var trimmed = line.Trim();
				var lower = trimmed.ToLowerInvariant();
				if (!lower.StartsWith("addontitle", StringComparison.Ordinal))
					continue;
				var start = trimmed.IndexOf('"');
				var end = trimmed.LastIndexOf('"');
				if (start >= 0 && end > start)
					return trimmed[(start + 1)..end].Trim();
			}
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException)
		{
			return null;
		}

		return null;
	}

	/// <summary>
	/// 开关的显示名。优先级：<c>addonTitle</c> 方括号里的内容 → <c>addonTitle</c> → 文件名。
	/// <para>
	/// 两种写法都要吃（实测样本里都有）：<c>Manuka JABIS[猫耳关]</c> 与
	/// <c>Manuka JABIS猫耳关</c>、<c>【VRM】橘雪莉 披风 关</c>；还有一种不是"关"而是"切换"：
	/// <c>Manuka_LaFiore切换纹身</c>。所以清理规则是"去掉 关/关闭/off 后缀、去掉 切换 前缀"，
	/// 而不是只按"删掉一个关字"。
	/// </para>
	/// <para>清不出东西时退回原始文本——宁可显示得啰嗦，也不要显示空名字。</para>
	/// </summary>
	public static string DescribeSwitchName(string? addonTitle, string fileNameWithoutExtension)
	{
		var raw = addonTitle;
		if (!string.IsNullOrWhiteSpace(raw))
		{
			var open = raw.IndexOf('[');
			var close = raw.LastIndexOf(']');
			if (open >= 0 && close > open)
				raw = raw[(open + 1)..close];
		}

		if (string.IsNullOrWhiteSpace(raw))
			raw = fileNameWithoutExtension;
		if (string.IsNullOrWhiteSpace(raw))
			return string.Empty;

		var label = raw.Trim();
		// 去掉开头的 【…】 / […] 前缀（橘雪莉那类把模组名写在最前面）。
		if (label.StartsWith('【'))
		{
			var end = label.IndexOf('】');
			if (end > 0)
				label = label[(end + 1)..].Trim();
		}

		foreach (var prefix in new[] { "切换", "switch " })
		{
			if (label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
			{
				label = label[prefix.Length..].Trim();
				break;
			}
		}

		foreach (var suffix in new[] { "关闭", "关掉", "关", " off", "off" })
		{
			if (label.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
			{
				label = label[..^suffix.Length].Trim();
				break;
			}
		}

		return label.Length == 0 ? raw.Trim() : label;
	}

	private static VpkImportCandidate Probe(string path)
	{
		try
		{
			using var archive = VpkArchive.Open(path);
			var materials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var textures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var hasModel = false;
			foreach (var entry in archive.Entries)
			{
				if (entry.Extension.Equals("mdl", StringComparison.OrdinalIgnoreCase))
					hasModel = true;
				else if (entry.Extension.Equals("vmt", StringComparison.OrdinalIgnoreCase))
					materials.Add(entry.FileName);
				else if (entry.Extension.Equals("vtf", StringComparison.OrdinalIgnoreCase))
					textures.Add(entry.FileName);
			}

			if (hasModel)
				return ProbeModel(path, archive, materials, textures) with { HasModelContent = true };

			if (materials.Count == 0 && textures.Count == 0)
			{
				return new VpkImportCandidate(path, VpkImportKind.Unsupported, null,
					"里面既没有 .mdl，也没有 .vmt/.vtf —— 没有可转换的内容。", null, materials, textures);
			}

			// 组件开关包：不产出 patch，但**要被识别出来**（翻译成 HD2 的 section 开关）。
			if (TryDescribeComponentSwitch(archive, path, materials.ToArray(), out var switchName))
			{
				return new VpkImportCandidate(path, VpkImportKind.ComponentSwitch, null,
					$"这是一个组件开关包（{switchName}）：它靠 $no_draw 隐藏部件，不单独产出 patch；"
					+ "已识别为组件开关，会出现在组件树的开关列表里。", null, materials, textures)
				{
					DisplayName = switchName,
				};
			}

			// 整包都是 `$no_draw`：源侧靠"不绘制"来隐藏部件，HD2 的材质模板没有这个槽。
			if (materials.Count != 0 && materials.All(name => IsNoDraw(archive, name)))
			{
				return new VpkImportCandidate(path, VpkImportKind.Unsupported, null,
					"它是一组 `$no_draw` 材质（源侧靠不绘制来隐藏部件），而 HD2 没有\"不绘制\"这种材质，"
					+ "所以「开关」在 HD2 里没有等价物。", null, materials, textures);
			}

			// 换贴图型的开关：同样不产出 patch，但理由完全不同（不是"不支持"，是"表达不了"）。
			// 判据要"真的在换图"——只看"小 + 无贴图"会把只写着着色器名的空壳也误判进来。
			if (textures.Count == 0 && materials.Count is >= 1 and <= MaximumSwitchEntries
				&& MaterialBytesWithinLimit(archive)
				&& materials.Any(name => HasBaseTexture(archive, name)))
			{
				return new VpkImportCandidate(path, VpkImportKind.MaterialVariant, null,
					"它是一组材质覆盖（换贴图或换渲染参数），自身不带贴图。section 开关只能隐藏几何，"
					+ "表达不了「换一张图」，所以这个开关在 HD2 里没有等价物。", null, materials, textures);
			}

			return new VpkImportCandidate(path, VpkImportKind.Material, null, null, null, materials, textures);
		}
		catch (Exception exception) when (IsProbeFailure(exception))
		{
			return new VpkImportCandidate(path, VpkImportKind.Unsupported, null,
				$"打不开（{exception.GetType().Name}）：{exception.Message}", null,
				Array.Empty<string>(), Array.Empty<string>());
		}
	}

	/// <summary>
	/// 路径快筛：这个 <c>.mdl</c> 看起来是不是角色。
	/// <para>
	/// 只看路径，因为这一步要能在**没有游戏数据**的 plan 阶段跑。判据全是实测样本里出现过的
	/// 非角色目录：道具、武器/手模、动画、特效、碎块。真正把关的是转换时的骨架校验
	/// （骨架对不上 HD2 Biped 就不转），这里只是把明显的先挡掉，省掉几十分钟空转。
	/// </para>
	/// <para>
	/// <b>不限定只认生还者目录</b>：敌人模型（<c>models/infected/…</c>）只要骨架合适照样是角色。
	/// </para>
	/// </summary>
	private static string? DescribeNonCharacterModel(string modelPath)
	{
		var path = modelPath.Replace('\\', '/').ToLowerInvariant();
		var reason = path.Contains("anims_", StringComparison.Ordinal) ? "动画模型（anims_*）"
			: path.Contains("models/props", StringComparison.Ordinal) || path.Contains("props_unique", StringComparison.Ordinal)
				? "道具模型（models/props*）"
			: path.Contains("models/weapons/", StringComparison.Ordinal) ? "武器/手模（models/weapons/*）"
			: path.Contains("models/effects/", StringComparison.Ordinal) || path.Contains("models/particles/", StringComparison.Ordinal)
				? "特效模型"
			: path.Contains("models/gibs/", StringComparison.Ordinal) ? "碎块模型"
			: null;

		return reason is null
			? null
			: $"里面的模型是{reason}（{Path.GetFileNameWithoutExtension(modelPath)}），不是角色："
				+ "转换成护甲没有意义，已跳过。";
	}

	/// <summary>
	/// 从 mdl 里读出它**声明的材质目录**（<c>ko_chiffon_aw\</c> 这种）。
	/// <para>
	/// <b>为什么要读它。</b>同一个包里常常有几套"实际相同、只是来源不同"的材质（本样本里 chiffon
	/// 与 plum 的 <c>tights_socks.vmt</c> 裸名完全相同），靠文件名分组会系统性判错。
	/// 而模型自己写着"我要 <c>ko_chiffon_aw</c> 目录下的材质"，材质包写着"我提供
	/// <c>materials/ko_chiffon_aw</c> 的 VMT"——两边一对就命中。这是**内容**，不是文件名。
	/// </para>
	/// <para>
	/// 做法是扫字符串而不是走结构解析：mdl 里的材质目录是 <c>mstudiomodel_t</c> 的 cdtexture
	/// 字符串（以分隔符结尾），而"以分隔符结尾的可打印串"在我们关心的范围内没有歧义
	/// （模型路径以 <c>.mdl</c> 结尾、材质裸名不带分隔符）。这样不依赖版本相关的结构偏移，
	/// 读坏了最多是"读不出目录"，不会读错。
	/// </para>
	/// </summary>
	private static IReadOnlySet<string> ReadModelMaterialFolders(VpkArchive archive)
	{
		var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		byte[] bytes;
		try
		{
			bytes = archive.Read(SourceModelLocator.FindModel(archive, null));
		}
		catch (Exception exception) when (IsProbeFailure(exception))
		{
			return folders;
		}

		var run = new System.Text.StringBuilder(64);
		foreach (var value in bytes)
		{
			if (value is >= 0x20 and < 0x7F)
			{
				if (run.Length > 128) run.Clear();
				run.Append((char)value);
				continue;
			}

			AddFolder(run.ToString(), folders);
			run.Clear();
		}

		AddFolder(run.ToString(), folders);
		return folders;

		static void AddFolder(string candidate, HashSet<string> target)
		{
			// 只认"以分隔符结尾"的串：那就是 cdtexture 条目的形状。
			if (candidate.Length is < 3 or > 96) return;
			if (!candidate.EndsWith('\\') && !candidate.EndsWith('/')) return;
			foreach (var ch in candidate)
			{
				if (char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '\\' or '/' or '.') continue;
				return;
			}

			var normalized = NormalizeFolder(candidate);
			if (normalized.Length != 0) target.Add(normalized);
		}
	}

	/// <summary>
	/// 材质包提供的材质目录：<b>VMT 与 VTF 都算</b>（<c>materials/ko_chiffon_aw</c> → <c>ko_chiffon_aw</c>）。
	/// <para>
	/// 为什么要连 VTF 一起看：实测有包只带一张 <c>warp.vtf</c>（卡通渐变条）就构成一个渲染风格选项，
	/// 一个 VMT 都没有。只看 VMT 会把这种包判成"认不出属于谁"而丢掉。
	/// </para>
	/// </summary>
	private static IReadOnlySet<string> ReadProvidedMaterialFolders(VpkArchive archive)
	{
		var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var entry in archive.Entries)
		{
			var isMaterialOrTexture = entry.Extension.Equals("vmt", StringComparison.OrdinalIgnoreCase)
				|| entry.Extension.Equals("vtf", StringComparison.OrdinalIgnoreCase);
			if (!isMaterialOrTexture) continue;
			var normalized = NormalizeFolder(entry.Directory);
			if (normalized.Length != 0) folders.Add(normalized);
		}

		return folders;
	}

	/// <summary>统一材质目录写法：去 <c>materials/</c> 前缀、去首尾分隔符、小写、正斜杠。</summary>
	private static string NormalizeFolder(string? folder)
	{
		if (string.IsNullOrWhiteSpace(folder)) return string.Empty;
		var value = folder.Trim().Replace('\\', '/');
		while (value.StartsWith("materials/", StringComparison.OrdinalIgnoreCase))
			value = value["materials/".Length..];
		return value.Trim('/').ToLowerInvariant();
	}

	/// <summary>
	/// 把这一批分成「模型主体 + 它自己的材质包」。
	/// <para>
	/// <b>为什么必须有分组。</b>一个整合包里可以有几套完全不同的角色，它们的材质<b>裸名相同</b>
	/// （<c>tights_socks.vmt</c> 谁都有），而材质 id 只在同一命名空间内唯一。不分组的后果是：
	/// 模型会吃到别人的材质（解析按裸名先到先得），两套角色也没法共存。
	/// </para>
	/// <para><b>归属判据（内容，不看文件名）：</b></para>
	/// <list type="number">
	/// <item>模型侧：mdl 声明的材质目录（见 <see cref="ReadModelMaterialFolders"/>）。</item>
	/// <item>材质侧：它提供的 VMT/VTF 目录（见 <see cref="ReadProvidedMaterialFolders"/>）。</item>
	/// <item>两边目录有交集 ⇒ 它是这个模型的材质包。一个包可以同时属于多个模型（共用材质是正常的）。</item>
	/// <item>对不上任何模型的材质包 ⇒ 不回退、不猜，跳过并点名。</item>
	/// </list>
	/// <para>
	/// <b>带模型的材质包会被改判成材质包。</b>实测有包把"摆姿势的展示模型 / 动画模型"塞在材质包里
	/// （本样本的「基础材质」有 5 个 mdl + 78 个 VMT，其中 <c>ko_*_aw/3.mdl</c> 甚至读不出来），
	/// 于是它先被判成"模型"、再因为模型读不出来判成"不支持"——而这个包其实是整只角色的**主材质**。
	/// 只要它提供的材质目录能对上某个模型，就把整包改判成材质包、内部的模型当辅助内容忽略。
	/// </para>
	/// </summary>
	private static IReadOnlyList<VpkModelGroup> BuildModelGroups(
		IReadOnlyList<VpkImportCandidate> candidates,
		out IReadOnlyList<string> providerVpkPaths)
	{
		providerVpkPaths = [];
		var modelCandidates = candidates
			.Where(candidate => candidate.Kind == VpkImportKind.CharacterModel && candidate.IsConvertible)
			.ToArray();
		if (modelCandidates.Length == 0) return [];

		var modelFolders = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
		var providedFolders = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
		foreach (var candidate in candidates)
		{
			if (candidate.VpkPath.Length == 0) continue;
			try
			{
				using var archive = VpkArchive.Open(candidate.VpkPath);
				var folders = ReadProvidedMaterialFolders(archive);
				if (folders.Count != 0) providedFolders[candidate.VpkPath] = folders;
				if (candidate.Kind == VpkImportKind.CharacterModel && candidate.IsConvertible)
					modelFolders[candidate.VpkPath] = ReadModelMaterialFolders(archive);
			}
			catch (Exception exception) when (IsProbeFailure(exception))
			{
				// 打不开的包在规划阶段已经带着原因被跳过了，这里不重复报。
			}
		}

		// 每个提供材质目录的候选 → 它匹配到哪些模型。**排除自己**：自带材质的模型包不算别人的材质包。
		//
		// **自己就是可用模型的包，一律不降级。** 以前只看"目录对得上别人"，于是两个各自带真几何的
		// 模型包会互相把对方判成材质包——实测「少女前线2 夏安」包里 `基础组件` 与 `L4N 基础组件`
		// 共用同一批材质目录，两个 VPK 的模型都完整可用，却双双被降级 ⇒ 一个主体都不剩，
		// 整个角色没导进来、只导进一堆材质。
		// 降级的本意只针对"模型是摆姿势 / 动画 / 读不出来的附属内容"那种包，所以判据是
		// **它自己的模型能不能用**，不是"它的目录有没有被别人声明"。
		var usableModelPaths = modelCandidates
			.Select(candidate => candidate.VpkPath)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var matchedModelsByProvider = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
		foreach (var (providerPath, folders) in providedFolders)
		{
			if (usableModelPaths.Contains(providerPath)) continue;
			foreach (var (modelPath, modelFolderSet) in modelFolders)
			{
				if (string.Equals(providerPath, modelPath, StringComparison.OrdinalIgnoreCase)) continue;
				if (!folders.Overlaps(modelFolderSet)) continue;
				if (!matchedModelsByProvider.TryGetValue(providerPath, out var list))
					matchedModelsByProvider[providerPath] = list = [];
				list.Add(modelPath);
			}
		}

		var providerPaths = matchedModelsByProvider.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
		providerVpkPaths = providerPaths.ToArray();

		// 主体 = 模型候选里**不是**别人材质包的那些。
		var groups = new List<VpkModelGroup>(modelCandidates.Length);
		foreach (var subject in modelCandidates.Where(candidate => !providerPaths.Contains(candidate.VpkPath)))
		{
			var materials = matchedModelsByProvider
				.Where(pair => pair.Value.Contains(subject.VpkPath, StringComparer.OrdinalIgnoreCase))
				.Select(pair => pair.Key)
				.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
				.ToArray();
			var displayName = Path.GetFileNameWithoutExtension(subject.VpkPath);
			groups.Add(new VpkModelGroup(
				subject.VpkPath,
				displayName,
				// 命名空间来源标识：**每个主体各一个**。于是"实际相同、但属于不同角色"的材质会得到
				// 不同的 id，几套角色可以同时装而不互相覆盖。
				IdNamespaceSource: displayName,
				MaterialVpkPaths: materials));
		}

		return groups;
	}

	private static VpkImportCandidate ProbeModel(
		string path,
		VpkArchive archive,
		HashSet<string> materials,
		HashSet<string> textures)
	{
		SourceModelFiles files;
		VpkEntry mdl;
		try
		{
			files = SourceModelLocator.Resolve(archive, null);
			mdl = files.Mdl;
		}
		catch (Exception exception) when (IsProbeFailure(exception))
		{
			// 占位几何必须在这里认回来：SourceModelLocator 会校验 .vvd 里到底有没有顶点数据，
			// 只有 28 字节头的壳因此在 resolve 阶段就被跳过，走不到下面——报出来又会变回
			// "找不到顶点/索引文件"这种技术话（用例：Plan_PlaceholderVvdSaysTheGeometryIsAStub）。
			var stub = DescribeStubGeometry(archive);
			return new VpkImportCandidate(path, VpkImportKind.Unsupported, null,
				stub ?? $"模型读不出来（{exception.GetType().Name}）：{exception.Message}",
				null, materials, textures);
		}

		// **不是角色就不要排进转换队列。** 中文整合包里大量"有 .mdl 但不是角色"的包：
		// 道具（`models/props_unique/…`）、武器/手模（`models/weapons/…`）、
		// 动画模型（`anims_*`，只有动画没有网格）。它们走角色链路只会白转几十分钟并产出垃圾 Mod。
		// 这里只做**路径快筛**（便宜、可在 plan 阶段做）；真正的判据是转换时的骨架校验。
		var notACharacter = DescribeNonCharacterModel(mdl.Path);
		if (notACharacter is not null)
		{
			return new VpkImportCandidate(path, VpkImportKind.Unsupported, null,
				notACharacter, null, materials, textures);
		}

		SourceModelDocument model;
		try
		{
			model = SourceModelReader.Read(archive.Read(mdl));
		}
		catch (Exception exception) when (IsProbeFailure(exception))
		{
			// 版本号不认识时 SourceModelReader 抛 NotSupportedException。这是"这个模型不属于
			// L4D2 / Source 2013 那一档"，不是程序出错：必须降级成"跳过这一个包"。
			// 之前它逃到 CLI 的兜底处理器，整批导入直接变成 UnhandledError（实测用户反馈）。
			return new VpkImportCandidate(path, VpkImportKind.Unsupported, null,
				$"模型 {mdl.Path} 读不出来（{exception.GetType().Name}）：{exception.Message}", null, materials, textures);
		}

		if (!IsCharacterRig(model))
		{
			return new VpkImportCandidate(path, VpkImportKind.Unsupported, null,
				$"含模型，但骨架不是角色骨架（缺 {string.Join("、", RequiredBones)} 中的骨）——"
				+ "它多半是道具或武器，而这一轮只支持角色模型。", null, materials, textures);
		}

		int lod0Vertices;
		int triangles;
		try
		{
			SourceVertexReader.Read(archive.Read(files.Vvd), out _, out lod0Vertices);
			var parts = SourceIndexReader.Read(archive.Read(files.Vtx), out _, out _);
			// 只数 LOD0：它是"这个模型有多大"的稳定口径，低 LOD 是派生的。
			triangles = parts.Sum(part => part.Models.Sum(model => model.Lods.Count == 0
				? 0
				: model.Lods[0].Meshes.Sum(mesh => mesh.Groups.Sum(group => group.Indices.Count)))) / 3;
		}
		catch (Exception exception) when (IsProbeFailure(exception))
		{
			return new VpkImportCandidate(path, VpkImportKind.Unsupported, null,
				$"几何读不出来（{exception.GetType().Name}）：{exception.Message}", null, materials, textures);
		}

		var signature = new VpkModelSignature(model.MaterialPaths.Count, lod0Vertices, triangles);
		// 模型"引用"的材质名放在 OwnedMaterialNames 里：规划阶段它就是引用集合。
		var referenced = model.MaterialPaths
			.Select(MaterialStem)
			.Where(name => name.Length != 0)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		return new VpkImportCandidate(path, VpkImportKind.CharacterModel, signature, null, null, referenced, textures);
	}

	/// <summary>
	/// 这个包的几何是不是"只有头的占位文件"：认出来就给人话理由，而不是"找不到顶点/索引文件"。
	/// <para>
	/// "是不是真几何"的判据不在这里重复——那是 <see cref="SourceModelLocator.LooksLikeRealVvd"/> 的事。
	/// 这里只补一个更便宜的筛子：装不下顶点（小于 <see cref="MinimumVertexDataBytes"/>）又带
	/// <c>IDSV</c> 头的，就是实测那种 28 字节的壳；其真正的模型在配套的基础组件包里。
	/// </para>
	/// </summary>
	private static string? DescribeStubGeometry(VpkArchive archive)
	{
		foreach (var vvd in archive.WithExtension("vvd"))
		{
			if (vvd.Length >= MinimumVertexDataBytes) continue;
			if (SourceModelLocator.LooksLikeRealVvd(archive, vvd)) continue;
			if (!archive.TryRead(vvd, out var head) || head.Length < 4) continue;
			if (head[0] != (byte)'I' || head[1] != (byte)'D' || head[2] != (byte)'S' || head[3] != (byte)'V') continue;

			var vtxLength = archive.WithExtension("vtx").Select(entry => (long)entry.Length).DefaultIfEmpty(0L).Max();
			return $"模型几何是占位文件：{vvd.Path} 只有 {vvd.Length} 字节"
				+ $"（.vtx {vtxLength} 字节），不含顶点数据。这类包通常只带材质和菜单图，"
				+ "真正的模型在配套的「基础组件」包里；单独导入它不会出模型。";
		}

		return null;
	}

	private static bool IsCharacterRig(SourceModelDocument model)
	{
		var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var bone in model.Bones)
		{
			var name = bone.Name;
			if (name is null)
				continue;
			if (name.StartsWith(ValveBipedPrefix, StringComparison.OrdinalIgnoreCase))
				names.Add(name[ValveBipedPrefix.Length..]);
		}

		return RequiredBones.All(names.Contains);
	}

	private static bool IsNoDraw(VpkArchive archive, string materialName)
	{
		var entry = archive.Entries.FirstOrDefault(candidate =>
			candidate.Extension.Equals("vmt", StringComparison.OrdinalIgnoreCase)
			&& candidate.FileName.Equals(materialName, StringComparison.OrdinalIgnoreCase));
		if (entry is null)
			return false;
		try
		{
			return VmtDocument.Parse(archive.Read(entry)).IsEnabled("$no_draw");
		}
		catch (InvalidDataException)
		{
			return false;
		}
	}

	/// <summary>MDL 材质表里的裸名：可能带目录、可能带扩展名，统一取最后一段去扩展名。</summary>
	private static string MaterialStem(string path)
		=> Path.GetFileNameWithoutExtension(VmtDocument.NormalizePath(path));

	/// <summary>VMT 里贴图键的值：可能带目录、可能带扩展名，统一取最后一段去扩展名。</summary>
	private static string TextureStem(string value)
	{
		var trimmed = value.Trim().Trim('"');
		if (trimmed.Length == 0)
			return string.Empty;
		// 值不是路径（例如 `$matcap 2` 这种数字）时没有意义，别把它当成贴图名。
		if (!trimmed.Contains('/') && !trimmed.Contains('\\') && !trimmed.Contains('.'))
			return string.Empty;
		return Path.GetFileNameWithoutExtension(VmtDocument.NormalizePath(trimmed));
	}
}
