using System.IO;
using System.Text.Json;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Infrastructure;

/// <summary>一次「从来源 VPK 重构建这个 VPK 模组」的结果。</summary>
/// <param name="Units">产物里有几个 Unit。</param>
/// <param name="Materials">
/// 被 section 实际引用的**不同**材质 id 个数。<b>不是</b>报告里的 material 条目数——
/// 模型 patch 本来就不含材质定义（材质写在同批的材质包里），那个数恒为 0，照搬只会误导。
/// </param>
public sealed record VpkRebuildOutcome(
	bool Ok,
	string? Error,
	int Units,
	int Materials,
	bool Rekeyed,
	string? BackupDirectory,
	IReadOnlyList<string> Notes)
{
	public static VpkRebuildOutcome Failed(string error, IReadOnlyList<string>? notes = null)
		=> new(false, error, 0, 0, false, null, notes ?? []);

	/// <summary>这次换到了哪件护甲（显示名）；<c>null</c> = 没做换甲。</summary>
	public string? TargetName { get; init; }

	/// <summary>
	/// 该做却没做成的那一步的原因（目前只有"生效层没重建"）。
	/// <para>
	/// <b>为什么要有它。</b>来源产物已经换好了，所以整次重构建不能算失败；但生效层还停在旧状态，
	/// 于是"用户以为修好了、游戏里还是旧样子"。只写在 Notes 里会被折叠进细节——实测有用户
	/// 重构建了全部 vpk2patch Mod、每次其实都失败在换甲那一步，界面却只说"重构建完成"。
	/// </para>
	/// </summary>
	public string? IncompleteReason { get; init; }
}

/// <summary>
/// 用 Mod 根上那份**来源 VPK** 重跑一次转换，把 <c>vpk2patch/</c> 换掉，
/// 然后**按当前配置**（目标护甲 + section 勾选）重新生成 <c>Overwrite/</c>。
/// <para>
/// <b>它解决的是"重来一次"的代价问题。</b>没有它，作者发新版模型、或者我们改了转换器，
/// 用户唯一的路是"重新导入"——而重新导入会丢掉三样已经配好的东西：换到哪件护甲、
/// 哪些 section 被关掉、素体登记的 unit id。三样都得手工重来一遍。
/// </para>
/// <para><b>必须保住的四样东西，以及各自的机制：</b></para>
/// <list type="number">
/// <item><b>命名空间</b>（材质/贴图 id 的一半输入）——从 <c>vpk2patch.json</c> 的
/// <c>idNamespace</c> 原样接上，用 <c>--id-namespace-token</c> 传。</item>
/// <item><b>unit id</b>——用 <c>--reuse-model</c> 把旧的 <c>custom-model.json</c> 指过去，
/// 转换器沿用它记下的素体 key 与 id 基。少了这一条，id 全部重新随机，下面两条立刻失效。</item>
/// <item><b>关掉的 section</b>——<c>wardrobe.json</c> 里按来源 Unit 键记着，换甲时原样烘进去。</item>
/// <item><b>换到的目标护甲</b>——同样在 <c>wardrobe.json</c> 里，重构建据此重跑换甲。</item>
/// </list>
/// <para>
/// <b>为什么先转到临时目录、校验通过才换。</b><c>vpk2patch/</c> 是唯一能让用户"再换一件护甲"
/// 的输入。就地转换一旦中途失败（源包损坏、缺 texconv、模型读不出来），用户手上就只剩一份
/// 半成品，而原来的产物已经没了。临时目录 + 通过后整批替换，失败时原样不动。
/// </para>
/// <para>
/// <b>不碰游戏数据。</b>重构建只写 Mod 目录：不写 <c>generated_customization_armor_sets.dl_bin</c>，
/// 不动护甲映射状态。真映射的件表按 unit id 记，而 id 在这一步被保住了，所以它继续有效；
/// 需要重进游戏让引擎重新读 Mod。
/// </para>
/// </summary>
public static class VpkRebuildService
{
	/// <summary>
	/// 产物里**没有**记下转换参数时（旧版转换器产的）用的那套。
	/// <para>
	/// <b>为什么不是"什么都不传、让 CLI 用自己的默认值"。</b>CLI 的默认是
	/// <c>--matcap bake</c>（matcap 直接烘进底色，Python 时代的行为），而管理器导入用的是
	/// <c>--matcap metal</c>（把 matcap 的色相抬到金属 F0，实机选出来的那一档）。
	/// 照 CLI 默认重跑，等于把每个旧 VPK Mod 的金属件退回"又黑又没反光"——用户会觉得
	/// "重构建把东西弄坏了"，而原因是参数没记下来。
	/// </para>
	/// <para>
	/// <b>为什么用"今天的默认"而不是猜当年那组。</b>重构建的常见动机就是"用现在的转换器重出一遍"，
	/// 所以按今天的默认走最接近用户预期；代价是观感可能与原来不同，这一点会写进回报里，
	/// 不静默。
	/// </para>
	/// </summary>
	private static readonly VpkConversionSettings CurrentImportDefaults = new("merge", "metal", null, null, null);

	/// <param name="modSelector">库里的 Mod 标识（GUID 或前缀）。换甲要用它定位工作区里的节点。</param>
	/// <param name="targetArchiveIds">
	/// 这次要烘到哪件护甲上。<b>给 <c>null</c> 就读 <c>wardrobe.json</c> 记着的那件</b>
	/// （= "上次用的是哪件"）。管理器传的是下拉框里**当前选中**的那件：用户可能刚改了选择
	/// 还没点「切换」，重构建该按他眼前看到的配置来，而不是按文件里上一次的。
	/// </param>
	/// <param name="disabledSections">
	/// 这次要关掉哪些 section（来源 Unit 键空间）。<c>null</c> 时读 <c>wardrobe.json</c>。
	/// 管理器传的是界面内存里的勾选——同样可能还没点「应用」。
	/// </param>
	public static async Task<VpkRebuildOutcome> RunAsync(
		string modSelector,
		string modDirectory,
		VpkHostContext context,
		IReadOnlyList<string>? targetArchiveIds,
		IReadOnlyDictionary<string, IReadOnlyCollection<int>>? disabledSections,
		Action<string>? onStage,
		CancellationToken cancellationToken = default,
		string? idNamespaceOverride = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(modSelector);
		ArgumentException.ThrowIfNullOrWhiteSpace(modDirectory);

		var notes = new List<string>();
		if (!Directory.Exists(modDirectory))
			return VpkRebuildOutcome.Failed($"Mod 目录不存在：{modDirectory}");

		var gameData = context.GameDataFolder;
		if (string.IsNullOrWhiteSpace(gameData) || !Directory.Exists(gameData))
			return VpkRebuildOutcome.Failed("没有配置游戏 data 目录：转换要读外壳 Unit 与 avatar 骨架。");

		var executable = Hd2ModCliLocator.TryResolve();
		if (executable is null)
		{
			return VpkRebuildOutcome.Failed(
				"找不到 hd2mod.exe：重构建依赖它（发布包自带）。"
				+ $"已查找：{Hd2ModCliLocator.DescribeSearchedLocations()}");
		}

		// ① 来源 VPK 必须还在 Mod 根上。管理器导入时专门留了这一份，就是为了今天。
		var sources = FindSourceVpks(modDirectory);
		if (sources.Count == 0)
		{
			return VpkRebuildOutcome.Failed(
				"这个 Mod 的根目录里没有来源 VPK，无法重构建。"
				+ "把最初导入的那个 .vpk 放回 Mod 文件夹根上（和 vpk2patch 同级），再点一次。");
		}

		// 读侧两个名字都认：老库里基底目录还叫 vpk2patch/，只认新名会让
		// reuseModel 与 hadSections 同时为假 —— 重构建会把这个 Mod 当纯材质包处理，
		// unit id 重新随机，已登记的映射与件表全部失效。
		var payloadDirectory = ModNodePayloadLayout.TryGetExistingVpkPatchDirectory(modDirectory)
			?? ModNodePayloadLayout.GetVpkPatchDirectory(modDirectory);
		var previousModelPath = Path.Combine(payloadDirectory, "custom-model.json");
		var reuseModel = File.Exists(previousModelPath) ? previousModelPath : null;

		// 这个 Mod 是"带 Unit 的模型产物"还是"纯材质包"？两种情况的可选步骤完全不同：
		// 材质包没有 sections.json、没有素体、没有换甲，硬按模型那条路走会一路报错。
		var hadSections = File.Exists(Path.Combine(payloadDirectory, WardrobeSectionMapFile.FileName));
		var isUnitMod = hadSections || reuseModel is not null;

		// ② 上一份转换报告：命名空间与参数都从这里接上。
		var report = VpkConversionReport.TryRead(modDirectory);
		// 记了参数就照原样重跑；没记（旧产物）就按今天的导入默认（见 CurrentImportDefaults）。
		var settings = report is { HasSettings: true } ? report.Settings : CurrentImportDefaults;

		if (report is null)
		{
			notes.Add("这份 Mod 里没有可读的 vpk2patch.json（多半是旧版转换器产的）："
				+ "本次按今天的导入默认重跑（配件合并、matcap 当金属 F0）。"
				+ "如果它当初用别的参数转过，观感会变；材质 id 也可能与库里其它 Mod 共用。");
		}
		else if (!report.HasSettings)
		{
			notes.Add("转换报告里没有记下转换参数（旧版产物）：本次按今天的导入默认重跑"
				+ "（配件合并、matcap 当金属 F0）。如果这份 Mod 当初用过特殊参数"
				+ "（例如不同的镂空档），重构建后的观感会变——那种情况请手工重跑一次 "
				+ "vpk2patch 再导入。");
		}

		if (reuseModel is null && isUnitMod)
		{
			notes.Add("没有找到上一份 custom-model.json：unit id 会重新生成。"
				+ "已经登记过的素体映射、真映射件表、组件开关都会对不上——"
				+ "这种情况下请改用「重新导入」。");
		}

		var paths = context.Paths;
		// ③ 解析集合：自己 + 同批转换的兄弟 Mod（靠命名空间认亲）。
		// 为什么必须带上兄弟：模型 VPK 往往不含材质，材质在另一份 VPK 里。
		// 少了它们，重构建会以"材质解析不到"的方式把材质整体丢掉。
		var materialSources = new List<string>(sources);
		if (report?.IdNamespace is { Length: > 0 } idNamespace)
			materialSources.AddRange(FindSiblingVpks(paths.ModsRootDirectory, modDirectory, idNamespace, notes));

		var stagingRoot = Path.Combine(
			Path.GetTempPath(),
			"hd2modmanager-vpk-rebuild",
			Guid.NewGuid().ToString("N"));
		var convertRoot = Path.Combine(stagingRoot, "convert");
		var backupRoot = Path.Combine(stagingRoot, "backup");
		Directory.CreateDirectory(convertRoot);
		// 失败时要把临时产物留下：那是唯一能看"CLI 到底写了什么"的东西，
		// 而回报里已经承诺了"保留在 <路径>"。
		var preserveStaging = false;

		try
		{
			// ④ 转换。
			onStage?.Invoke($"正在重新转换 {Path.GetFileName(sources[0])}…");
			var arguments = new List<string> { "vpk2patch" };
			arguments.AddRange(sources);
			arguments.AddRange(["--output", convertRoot]);
			arguments.AddRange(["--material-source", string.Join(';', materialSources)]);
			arguments.AddRange(["--manager-root", paths.AppRootDirectory]);
			arguments.AddRange(["--library", paths.ModsRootDirectory ?? paths.AppRootDirectory]);
			arguments.AddRange(["--game-data", gameData]);
			// 命名空间：默认接上原 token（材质 id 逐位一致，重构建就靠这个）。
			// 给了覆盖就是**换一个新命名空间**——复制出来的变体必须与本体 id 不撞，
			// 否则两套外观会互相覆盖，那正是复制要解决的问题。
			if (!string.IsNullOrWhiteSpace(idNamespaceOverride))
				arguments.AddRange(["--id-namespace", idNamespaceOverride]);
			else if (report?.IdNamespace is { Length: > 0 } token)
				arguments.AddRange(["--id-namespace-token", token]);
			arguments.AddRange(settings.ToArguments());
			if (reuseModel is not null)
				arguments.AddRange(["--reuse-model", reuseModel]);
			arguments.Add("--json");

			// 进度：CLI 的 `#progress` 行已经带人话文案（"材质 3/12"），直接当状态行用。
			// 不接的话，重构建这几分钟里卡片上只有一句"正在重新转换…"，看不出它是不是卡住了。
			var conversion = await Hd2ModProcess.RunAsync(
				executable, arguments, cancellationToken,
				onProgress: (_, _, _, text) => onStage?.Invoke(text)).ConfigureAwait(false);

			// ⚠️ **一次转换的产出是「每个输入 VPK 一个子目录」**（模型一个、每个材质包各一个）。
			// 而存储约定要求 `sectional/` 里全部 patch 组平铺按序号，所以这里必须先收成
			// 一份平铺载荷：模型 → `patch_0`，材质 → `patch_1..patch_N`。
			//
			// 以前这一步是"找第一个含 patch 的子目录"，于是**模型 patch 进来了、材质全丢** ——
			// 表现是"重构完成、游戏里材质没了"，重转多少次都一样（2026-11-07 用户实测）。
			var fresh = VpkConversionReport.TryParse(conversion.Output);
			var collectedPayload = Path.Combine(stagingRoot, "payload");
			var collected = VpkPayloadCollector.Collect(convertRoot, collectedPayload, fresh);
			if (!collected.Succeeded)
			{
				preserveStaging = true;
				return VpkRebuildOutcome.Failed(
					(conversion.ExitCode == 0 ? string.Empty : ExplainConversionFailure(conversion))
					+ "收集转换产物失败：" + collected.Error
					+ $"（临时产物保留在：{stagingRoot}）", notes);
			}

			notes.AddRange(collected.Notes);
			var produced = collectedPayload;
			if (isUnitMod && !File.Exists(Path.Combine(produced, WardrobeSectionMapFile.FileName)))
			{
				preserveStaging = true;
				return VpkRebuildOutcome.Failed(
					"新产物里没有 sections.json：组件树与 section 开关都靠它，"
					+ $"继续替换只会得到一个关不掉任何东西的 Mod。临时产物保留在：{stagingRoot}", notes);
			}

			var units = fresh?.UnitCount ?? 0;
			var previousMaterials = await CountDistinctMaterialsAsync(payloadDirectory, cancellationToken).ConfigureAwait(false);
			var freshMaterials = await CountDistinctMaterialsAsync(produced, cancellationToken).ConfigureAwait(false);

			// 材质塌陷守门。
			//
			// 重构建最容易踩的坑是"这次没找到同批的材质包"：模型 VPK 通常是**零材质**，
			// 材质写在同批的材料包里；那些包不在解析集合时，每个 section 引用的材质会
			// 全部塌成同一个兜底 id。而 Unit 数、section 数、patch 大小全都正常——
			// 文件层面完全看不出来，进游戏才发现一身衣服共用一张图。
			// 所以只要"被引用的不同材质个数"比上一次少，就不替换（临时产物留着给人看）。
			if (previousMaterials > 0 && freshMaterials < previousMaterials)
			{
				preserveStaging = true;
				return VpkRebuildOutcome.Failed(
					$"新产物只引用了 {freshMaterials} 种材质，而原来有 {previousMaterials} 种。"
					+ "这几乎一定是同批的材质包 VPK 没进解析集合（模型 VPK 本身不带材质），"
					+ "继续替换会让整个模型共用极少几张图。已保留原产物，没有改动任何文件。"
					+ "请把同一批的材质 VPK 放回 Mod 库里，或者手工重跑 vpk2patch 再导入。"
					+ $"（本次临时产物：{stagingRoot}）", notes);
			}

			// 材质包没有 sections.json，上一条守门对它不适用：改用报告里的材质条目数。
			// 意义一样——"这次少写了一堆材质"绝不能当成成功。
			//
			// ⚠️ **必须按 VPK 对 VPK 比，不能拿"本 VPK 的数"去比"整批的合计"。**
			//
			// 实测事故（2026-11-03）：MourningVeil 的 `B3服装色白紫` 那份旧报告是
			// **A1主体 + B3 两个 VPK 一次转出来的**（合计 101 个材质），而重构建只重转
			// 本 Mod 那一个 VPK（98 个）。`98 < 101` 于是被判成"材质塌陷"，**永久拒绝替换**——
			// 用户点了重构建、产物时间戳却一直不变（还是 11-02），连续三轮测试都在看旧产物，
			// 还在怀疑代码没生效。这条守门本身是对的，错的是比较的范围。
			var previousPackMaterials = report?.FindPatch(Path.GetFileName(sources[0]))?.MaterialCount
				?? (report?.Patches.Count > 1 ? 0 : report?.MaterialCount ?? 0);
			if (!isUnitMod && previousPackMaterials > 0
				&& fresh is { MaterialCount: >= 0 } currentPack
				&& currentPack.MaterialCount < previousPackMaterials)
			{
				preserveStaging = true;
				return VpkRebuildOutcome.Failed(
					$"新产物只写出 {currentPack.MaterialCount} 个材质，而这个 VPK 原来有 {previousPackMaterials} 个。"
					+ "多半是解析集合不完整（贴图或 VMT 没找到）。已保留原产物，没有改动任何文件。"
					+ $"（本次临时产物：{stagingRoot}）", notes);
			}

			// ⑤ 换进 vpk2patch/：先把旧载荷备份到临时目录，再整批替换。
			// 规则与"同一来源再次导入"共用一份（见 VpkPayloadSwap），否则两条路会各自漂移。
			onStage?.Invoke("正在替换 vpk2patch/…");
			var swap = VpkPayloadSwap.Swap(payloadDirectory, produced, backupRoot);
			if (!swap.Succeeded) return VpkRebuildOutcome.Failed(swap.Error!, notes);
			var backupDirectory = swap.BackupDirectory;
			var restoreReadOnly = swap.RestoreReadOnly;

			// ⑤b **刷新 `vpk2patch.json`。**
			//
			// ⚠️ 不刷新是个隐蔽的坑：报告是管理器展示材质/贴图/警告的**唯一来源**，
			// 也是"这次到底改了没有"的判断依据。只换 `vpk2patch/` 不换报告，界面会一直
			// 显示上一次的数字——实测因此误判过一次：补丁已经是新的、报告还是旧的，
			// 看上去像"改动没生效"。（`fresh` 就是从这次进程输出解析出来的，非 null
			// 说明输出是合法 JSON，可以原样落盘。）
			if (fresh is not null && !string.IsNullOrWhiteSpace(conversion.Output))
			{
				// ⚠️ **`vpk2patch.json` 在 Mod 根上，而且是只读的。**
				//
				// 库里的 Mod 根文件（`.vpk` 与 `vpk2patch.json`）被管理器标成只读
				// （换包时 `SetDirectoryReadOnly(true)`），而 `SwapPayload` 只清 `vpk2patch/`
				// 里那些文件的属性。于是写报告这一步会拿到
				// 「Access to the path '…\vpk2patch.json' is denied.」——那句话一个字都没提"只读"，
				// 第一眼必然怀疑权限或占用（实测就是这样报了"重构建失败"）。
				// 这里先清属性、写完再补回去，保持这个 Mod 目录原来的保护状态。
				var reportPath = VpkConversionReport.GetPath(modDirectory);
				var restoreReportReadOnly = VpkPayloadSwap.ClearReadOnlyForWrite(reportPath);
				try
				{
					await File.WriteAllTextAsync(reportPath, conversion.Output, cancellationToken)
						.ConfigureAwait(false);
				}
				catch (UnauthorizedAccessException exception)
				{
					// 报告写不进去**不该被判成重构建失败**：产物已经换好了，报告只是"界面上显示的数字"。
					notes.Add($"`vpk2patch.json` 写不进去（{exception.Message}）：产物已换成新的，"
						+ "但界面上的材质/贴图计数与警告仍是上一次的。多半是别的程序占着这个文件。");
				}
				finally
				{
					if (restoreReportReadOnly) VpkPayloadSwap.MarkReadOnly(reportPath);
				}
			}
			else
			{
				notes.Add("这次没能解析出转换报告，`vpk2patch.json` 保持旧内容——"
					+ "界面上的材质/贴图计数与警告可能已经过时。");
			}

			// ⑥ 物理参数容器：转换阶段刻意不嵌（会让受管步骤记账错位），这里才是它该来的地方。
			onStage?.Invoke("正在写入物理参数…");
			notes.Add(await VpkPhysBoneEmbedder.RunAsync(executable, modDirectory, cancellationToken).ConfigureAwait(false));

			// ⑥b **生效层**：基底里现在可能有多个材质 patch（模型自带的 + 归拢进来的材质包），
			// 而部署在"没有 Overwrite"时会按基底全上，同名资源互相覆盖。
			// 重构建之后必须重新物化一次：unit（基底那份；已有换甲产物时物化器会沿用它）+ 启用的材质。
			var rebuiltLayer = OverwriteMaterializer.Materialize(modDirectory, OverwriteSelection.Default);
			if (!rebuiltLayer.Succeeded)
				notes.Add("生效层没能重建（" + (rebuiltLayer.Error ?? "原因未知")
					+ "）：材质会按基底目录里的全部变体一起上。");
			else
				foreach (var warning in rebuiltLayer.Warnings) notes.Add(warning);

			// ⑦ **按当前配置**重新生成 Overwrite/。
			//
			// "当前配置"= 调用方给的目标与勾选（界面上正显示着的那份），没给才退回
			// wardrobe.json 记着的。这条是刻意的：用户可能刚改了下拉里的目标、刚勾掉几个
			// section，都还没点「切换」/「应用」。重构建一并把它们做掉，
			// 否则"点完重构建，界面上的配置还没生效"会再困惑一次。
			var rekeyed = false;
			string? targetName = null;
			string? incompleteReason = null;
			var wardrobe = await VpkModWardrobeService.ReadAsync(modDirectory, cancellationToken).ConfigureAwait(false);
			var targets = targetArchiveIds is { Count: > 0 }
				? targetArchiveIds
				: ResolveRekeyTargets(modDirectory, wardrobe);
			var disabled = disabledSections ?? wardrobe.State.Disabled;
			if (!isUnitMod)
			{
				// 材质包没有 Unit，也没有 "换甲 / 组件开关" 这些概念：只更新转换产物就结束。
				notes.Add("这是材质包（没有 Unit）：只更新了转换产物（vpk2patch/），没有换甲这一步。");
			}
			else if (targets is null)
			{
				notes.Add("这个 Mod 还没有选过目标护甲：本次只更新了来源产物（vpk2patch/），"
					+ "没有生成 Overwrite。到详情页选一件目标护甲再点「切换」即可。");
			}
			else
			{
				onStage?.Invoke("正在按当前配置重新生成换甲产物…");
				var rekey = await VpkRekeyService.RunAsync(
					modSelector, modDirectory, context, targets, disabled,
					stage => onStage?.Invoke(stage), cancellationToken,
					// 头身合并也照 Mod 自己的设置来：重构建不该把用户关掉的开关又打开。
					headMergeIntoBody: wardrobe.HeadMergeIntoBody).ConfigureAwait(false);
				if (!rekey.Ok)
				{
					// 来源产物已经换好了，换甲失败不该把这件事说成"什么都没做"；
					// 但也不能报成"做完了"——生效层还停在旧状态，用户会以为已经修好。
					incompleteReason = rekey.Error ?? "原因未知";
					notes.Add("来源产物已更新，但重新生成 Overwrite 失败：" + incompleteReason
						+ "。可以在详情页点「应用」重试。");
				}
				else
				{
					rekeyed = true;
					targetName = rekey.TargetNames.FirstOrDefault()
						?? rekey.TargetArchiveIds.FirstOrDefault();
					var target = targetName ?? string.Empty;
					notes.Add(rekey.RestoredFromSource
						? "换甲产物已按自定义素体模式重新生成。"
						: $"换甲产物已重新生成（目标 {target}）：替换 {rekey.ReplacedUnits} 个 Unit、隐藏 {rekey.HiddenMeshes} 个网格。");
					var offCount = disabled.Values.Sum(sections => sections.Count);
					if (offCount != 0) notes.Add($"已把 {offCount} 个 section 烘成关闭状态。");
					foreach (var warning in rekey.Warnings) notes.Add("注意：" + warning);
				}
			}

			notes.Add("重启游戏后才会生效（引擎启动时读 Mod）。");
			// 库里的 Mod 文件本来是被标成只读的（管理器换包时会 SetDirectoryReadOnly(true)），
			// 我们在替换时临时清掉了它——这里补回去，别让 Mod 目录从此变成可随意改写。
			// **必须放在最后**：中间那两步（嵌物理参数、重建 Overwrite）都要往产物里写字。
			if (restoreReadOnly) VpkPayloadSwap.SetPayloadReadOnly(payloadDirectory);
			return new VpkRebuildOutcome(true, null, units, isUnitMod ? freshMaterials : (fresh?.MaterialCount ?? 0),
				rekeyed, backupDirectory, notes)
			{
				TargetName = targetName,
				IncompleteReason = incompleteReason,
			};
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException
			or InvalidOperationException or UnauthorizedAccessException or JsonException)
		{
			return VpkRebuildOutcome.Failed("重构建失败：" + exception.Message, notes);
		}
		finally
		{
			// 成功时暂存目录里只剩备份（单独回报给用户），转换目录就没用了；
			// 失败时整份留着，回报里已经说明了路径。
			if (!preserveStaging) DeleteQuietly(convertRoot);
		}
	}

	/// <summary>
	/// Mod 根上的来源 VPK（只取顶层、跳过纯数字分卷）。
	/// 分卷由 <c>_dir.vpk</c> 引用，单列出来会被当成另一个包重复转换。
	/// </summary>
	/// <summary>
	/// 这个 Mod 根上有几份来源 VPK。<b>0 表示无法重构建</b> ——
	/// 汉堡菜单据此决定要不要显示「用来源重构建」（转出来的分件模组没有来源包）。
	/// </summary>
	public static int FindSourceVpkCount(string modDirectory)
		=> string.IsNullOrWhiteSpace(modDirectory) || !Directory.Exists(modDirectory)
			? 0
			: FindSourceVpks(modDirectory).Count;

	private static List<string> FindSourceVpks(string modDirectory)
		=> Directory.EnumerateFiles(modDirectory, "*.vpk", SearchOption.TopDirectoryOnly)
			.Where(path => !IsVolume(path))
			.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
			.ToList();

	/// <summary>
	/// 同一个命名空间下别的 Mod 根上的 VPK：它们与这个 Mod 是**同一批**转换出来的，
	/// 模型 patch 引用的材质很可能就写在那几份里。
	/// </summary>
	private static IEnumerable<string> FindSiblingVpks(
		string? modsRoot,
		string ownDirectory,
		string idNamespace,
		List<string> notes)
	{
		if (string.IsNullOrWhiteSpace(modsRoot) || !Directory.Exists(modsRoot))
		{
			notes.Add("找不到 Mod 库目录：只把本 Mod 自己的 VPK 当作解析集合，"
				+ "同批的材质包若在别的 Mod 里，相关材质会解析不到。");
			return [];
		}

		var result = new List<string>();
		foreach (var directory in Directory.EnumerateDirectories(modsRoot))
		{
			if (string.Equals(Path.GetFullPath(directory), Path.GetFullPath(ownDirectory), StringComparison.OrdinalIgnoreCase))
				continue;
			var sibling = VpkConversionReport.TryRead(directory);
			if (sibling is null || !string.Equals(sibling.IdNamespace, idNamespace, StringComparison.Ordinal))
				continue;
			result.AddRange(FindSourceVpks(directory));
		}

		return result;
	}

	/// <summary>重跑换甲的目标：记过的游戏护甲优先，其次这个 Mod 自己登记的素体。都没有就返回 <c>null</c>。</summary>
	private static IReadOnlyList<string>? ResolveRekeyTargets(string modDirectory, VpkWardrobeFile wardrobe)
	{
		if (!string.IsNullOrWhiteSpace(wardrobe.TargetArmorArchiveId))
			return [wardrobe.TargetArmorArchiveId];

		var ownKey = VpkModWardrobeService.TryReadOwnCustomModelKey(modDirectory);
		return ownKey is null ? null : [RuntimeArmorRegistryService.CustomModelArchiveId(ownKey)];
	}

	private static string ExplainConversionFailure(Hd2ModProcess.Result result)
	{
		if (!string.IsNullOrWhiteSpace(result.Output))
		{
			try
			{
				using var document = JsonDocument.Parse(result.Output);
				if (document.RootElement.TryGetProperty("error", out var error)
					&& error.ValueKind == JsonValueKind.Object
					&& error.TryGetProperty("message", out var message)
					&& message.GetString() is { Length: > 0 } text)
				{
					return "重新转换失败：" + text;
				}
			}
			catch (JsonException)
			{
				// 不是 JSON：落回下面那句。
			}
		}

		return Hd2ModProcess.Explain(result, "重新转换");
	}

	/// <summary>
	/// 一个 <c>sections.json</c> 里被 section 实际引用的**不同**材质 id 个数；读不到就是 0。
	/// <para>
	/// <b>为什么用这张表而不是转换报告。</b>模型 patch 里不含材质定义（材质写在同批的材质包里），
	/// 报告上的 <c>materials</c> 恒为 0；而"每个 section 绑了哪个材质 id"这件事，
	/// <c>sections.json</c> 逐节都记着。两个产物比这个数字，才问得出一句实话：
	/// 材质是不是塌成一张了。
	/// </para>
	/// </summary>
	private static async Task<int> CountDistinctMaterialsAsync(string directory, CancellationToken cancellationToken)
	{
		var path = Path.Combine(directory, WardrobeSectionMapFile.FileName);
		if (!File.Exists(path)) return 0;
		try
		{
			var document = await WardrobeSectionMapFile.ReadAsync(path, cancellationToken).ConfigureAwait(false);
			return document.Units
				.SelectMany(unit => unit.Sections)
				.Select(section => section.MaterialId)
				.Where(id => !string.IsNullOrWhiteSpace(id))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.Count();
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException
			or JsonException or UnauthorizedAccessException)
		{
			// 读不懂就当"没有可比数字"：守门不会因为旧表损坏而误拦。
			return 0;
		}
	}

	private static bool IsVolume(string path)
	{
		var stem = Path.GetFileNameWithoutExtension(path);
		return stem.Length > 4
			&& stem[^4] == '_'
			&& stem[^3..].All(char.IsAsciiDigit);
	}

	private static void DeleteQuietly(string directory)
	{
		try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}
}
