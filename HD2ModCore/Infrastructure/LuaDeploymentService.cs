using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using HD2ModAdaptation.PatchReconstruction.Lua;
// 只引入构建双条目补丁需要的三个类型：整个命名空间会与 HD2ModCore.Domain.AssetKey 撞名。
using PatchArchiveAdditionalEntry = HD2ModAdaptation.PatchReconstruction.PatchArchiveAdditionalEntry;
using PatchArchiveWriter = HD2ModAdaptation.PatchReconstruction.PatchArchiveWriter;
using PatchUnitMeshEditResult = HD2ModAdaptation.PatchReconstruction.PatchUnitMeshEditResult;
using PatchAssetKey = HD2ModAdaptation.PatchReconstruction.AssetKey;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure.KitInstance;

namespace HD2ModCore.Infrastructure;

// Purpose: Converts the active profile's Lua components into one derived Lua
// patch and resolves safe external (ReShade) files before ordinary planning.
// It deliberately does not execute or mutate the game directory.
public sealed class LuaDeploymentService : ILuaDeploymentService
{
	private readonly LuaPatchComposer _luaComposer;
	private readonly LuaPatchRuntimeValidator _luaRuntimeValidator;
	private readonly IExternalInstallFileFactsService _externalFiles;
	private readonly RuntimeArmorRegistryService? _armorRegistry;
	private readonly string? _kitInstanceRegistryRoot;
	private readonly IKitPollutionService? _kitPollution;
	private static readonly ModNodeId ArmorRegistryNodeId = new(Guid.Parse("6dd5cb37-6b45-4f47-a57c-65e3f2f9e0ca"));
	private static readonly ModNodeId PhysicsRuntimeNodeId = new(Guid.Parse("7c1e2b90-5f3a-4d68-9a11-2b7c4e5f6a01"));
	private const string ArmorRegistryPackageGuid = "6dd5cb37-6b45-4f47-a57c-65e3f2f9e0ca";

	/// <summary>
	/// 隐藏 unit 补丁的落位顺序：恒定排在 **boot 补丁之前一个**。
	/// <para>
	/// 用户口径（2026-11-12）："这个 patch 文件如果是独占，则总是出现在倒数第二个序号，
	/// 而 lua patch 总是出现在最后一个最大的序号。"
	/// </para>
	/// <para>
	/// 注意这是 <see cref="GeneratedPatchGroup.PlacementOrder"/>（相对顺序），不是最终 patch 序号 ——
	/// 真实序号由 <c>ApplyPlanner</c> 按它递增分配，所以"顺序"才是不依赖现有文件数量的表达方式。
	/// </para>
	/// </summary>
	private const int HiddenUnitPlacementOrder = int.MaxValue - 1;

	/// <summary>
	/// 「按实例隐藏」Hider 独立 addon 补丁的落位顺序：比 boot（<c>int.MaxValue</c>）、隐藏 unit 补丁
	/// （<see cref="HiddenUnitPlacementOrder"/>）、外置入口的护甲 addon（<c>int.MaxValue</c>）都小，
	/// 不与这三者里任何一个撞序号。Hider addon 只在管理器不占 boot 时出现，不含 boot 资源，
	/// 所以排在它们前面不会改变"boot 永远最大"的口径。
	/// </summary>
	private const int KitInstanceHiderPlacementOrder = int.MaxValue - 2;

	/// <summary>
	/// 护甲映射的落地方式。默认 <see cref="ArmorMappingModes.Default"/>（关闭）。
	///
	/// 只有需要运行时 Lua 的模式才会投放护甲映射补丁：独占入口写 boot，外置入口走独立补丁。
	/// 「真映射」（写注册表数据）已于 2026-11-11 停用——游戏改了文件校验，写过的文件会闪退。
	/// </summary>
	public static ArmorMappingMode MappingMode { get; set; } = ArmorMappingModes.Default;

	/// <summary>
	/// 总开关：是否启用「管理器内置 lua 类功能」。
	///
	/// 这是所有内置 Lua 功能的**总闸**：关闭时管理器**永不占用 boot 入口**，
	/// 于是 <see cref="ArmorMappingMode.ExclusiveLuaEntry"/> 与
	/// <see cref="PhysicsMode.ExclusiveLuaEntry"/> 在物理上无法实现，会被收敛到外置入口。
	/// 设置页据此过滤下拉选项，避免用户选了之后发现无效。
	/// </summary>
	public static bool BuiltInLuaEnabled { get; set; } = true;

	/// <summary>
	/// 物理骨骼的落地方式。默认 <see cref="PhysicsModes.Default"/>（关闭）。
	/// 见 <see cref="Domain.PhysicsMode"/>：物理没有"真映射"这一档，只有 关闭 / 独占 / 外置。
	/// </summary>
	public static PhysicsMode PhysicsMode { get; set; } = PhysicsModes.Default;


	/// <summary>
	/// 实际生效的映射模式（把总开关约束算进去）。
	/// 计划阶段一律用这个，不要直接读 <see cref="MappingMode"/>。
	/// </summary>
	public static ArmorMappingMode EffectiveMappingMode
		=> !BuiltInLuaEnabled && MappingMode == ArmorMappingMode.ExclusiveLuaEntry
			? ArmorMappingMode.ExternalLuaEntry
			: MappingMode;

	/// <summary>实际生效的物理模式（把总开关约束算进去）。</summary>
	public static PhysicsMode EffectivePhysicsMode
		=> BuiltInLuaEnabled ? PhysicsMode : PhysicsModes.Normalize((int)PhysicsMode, false);

	public LuaDeploymentService(
		LuaPatchComposer? luaComposer = null,
		LuaPatchRuntimeValidator? luaRuntimeValidator = null,
		IExternalInstallFileFactsService? externalFiles = null,
		RuntimeArmorRegistryService? armorRegistry = null,
		string? kitInstanceRegistryRoot = null,
		IKitPollutionService? kitPollution = null)
	{
		_luaComposer = luaComposer ?? new LuaPatchComposer();
		_luaRuntimeValidator = luaRuntimeValidator ?? new LuaPatchRuntimeValidator();
		_externalFiles = externalFiles ?? new ExternalInstallFileFactsService();
		_armorRegistry = armorRegistry;
		// 规则文件与 wardrobe.links 同一个 ArmorRegistry 目录。没有注册表服务又没显式给目录时不写盘
		// （避免单元测试之类不带注册表的调用去碰真实的 %LOCALAPPDATA%）。
		_kitInstanceRegistryRoot = kitInstanceRegistryRoot ?? armorRegistry?.RegistryRoot;
		// 污染提示（P2）：没给服务就不分析，单元测试与不带索引的调用不会去碰索引库。
		_kitPollution = kitPollution;
	}

	public async ValueTask<LuaDeploymentPlan> PlanAsync(
		Profile profile,
		LibrarySnapshot snapshot,
		PatchFileIndex patchIndex,
		string modsRootDirectory,
		string gameDataDirectory,
		IReadOnlyDictionary<ModNodeId, ModContentFacts> contentFacts,
		CancellationToken cancellationToken = default)
	{
		// 关闭"独占映射 Lua"时**不改变标准部署流程**：其它 mod 的 Lua 照常按配置表部署，
		// 只是不再额外投放护甲映射运行时那个补丁（见 BuildAsync 里对
		// IsManagedDeploymentEnabledAsync 的判断）。运行时脚本会由设置页导出到一个文件夹，
		// 用户可以交给 MDL / Bingus 之类的第三方加载器自行加载。

		var result = await BuildAsync(
			profile,
			snapshot,
			patchIndex,
			modsRootDirectory,
			gameDataDirectory,
			contentFacts,
			materialize: false,
			validationMode: null,
			cancellationToken: cancellationToken).ConfigureAwait(false);
		return new LuaDeploymentPlan(
			result.PatchIndex,
			result.GeneratedPatchGroups,
			result.ExternalFiles,
			result.Issues,
			result.RequiresMaterialization,
			result.CompositionFingerprint,
			result.InputCount);
	}

	public async ValueTask<LuaDeploymentPreparation> PrepareAsync(
		Profile profile,
		LibrarySnapshot snapshot,
		PatchFileIndex patchIndex,
		string modsRootDirectory,
		string gameDataDirectory,
		IReadOnlyDictionary<ModNodeId, ModContentFacts> contentFacts,
		CancellationToken cancellationToken = default)
		=> await MaterializeAsync(
			profile,
			snapshot,
			patchIndex,
			modsRootDirectory,
			gameDataDirectory,
			contentFacts,
			LuaDeploymentValidationMode.CompileAndSmoke,
			cancellationToken).ConfigureAwait(false);

	public async ValueTask<LuaDeploymentPreparation> MaterializeAsync(
		Profile profile,
		LibrarySnapshot snapshot,
		PatchFileIndex patchIndex,
		string modsRootDirectory,
		string gameDataDirectory,
		IReadOnlyDictionary<ModNodeId, ModContentFacts> contentFacts,
		LuaDeploymentValidationMode validationMode,
		CancellationToken cancellationToken = default)
	{
		var result = await BuildAsync(
			profile,
			snapshot,
			patchIndex,
			modsRootDirectory,
			gameDataDirectory,
			contentFacts,
			materialize: true,
			validationMode: validationMode,
			cancellationToken: cancellationToken).ConfigureAwait(false);
		return new LuaDeploymentPreparation(result.PatchIndex, result.GeneratedPatchGroups, result.ExternalFiles, result.Issues);
	}

	private async ValueTask<LuaBuildResult> BuildAsync(
		Profile profile,
		LibrarySnapshot snapshot,
		PatchFileIndex patchIndex,
		string modsRootDirectory,
		string gameDataDirectory,
		IReadOnlyDictionary<ModNodeId, ModContentFacts> contentFacts,
		bool materialize,
		LuaDeploymentValidationMode? validationMode,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(profile);
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(patchIndex);
		ArgumentNullException.ThrowIfNull(contentFacts);
		ArgumentException.ThrowIfNullOrWhiteSpace(modsRootDirectory);
		ArgumentException.ThrowIfNullOrWhiteSpace(gameDataDirectory);

		var issues = new List<CoreIssue>();
		var documents = new List<LuaDocumentSource>();
		// 只登记"谁提供了 boot"，解析推迟到确认必须合并之后再做（见下方 mergeRequired）。
		var bootProviders = new List<LuaBootProvider>();
		var managedRemovedGroups = new HashSet<PatchGroupIdentity>();
		var orderedEntries = profile.Entries
			.OrderBy(entry => entry.LoadOrder)
			.ThenBy(entry => entry.AddedUtc)
			.ThenBy(entry => entry.NodeId.Value)
			.ToArray();

		foreach (var profileEntry in orderedEntries)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!snapshot.Nodes.ContainsKey(profileEntry.NodeId)
				|| !patchIndex.FilesByNode.TryGetValue(profileEntry.NodeId, out var files))
				continue;

			var groups = files
					.GroupBy(file => new PatchGroupIdentity(file.NodeId, file.ArchiveHex16, file.SourcePatchIndex, file.NormalizedOrder))
				.OrderBy(group => group.Min(file => file.NormalizedOrder))
				.ThenBy(group => group.Key.ArchiveHex16, StringComparer.OrdinalIgnoreCase)
				.ToArray();
			foreach (var group in groups)
			{
				if (snapshot.Nodes.TryGetValue(profileEntry.NodeId, out var managedNode)
					&& string.Equals(
						managedNode.Metadata.SourcePackageGuid,
						ArmorRegistryPackageGuid,
						StringComparison.OrdinalIgnoreCase))
				{
					// Migrate the old manually imported addon into the hidden
					// manager-owned component without deleting the user's library node.
					managedRemovedGroups.Add(group.Key);
					continue;
				}
				var baseFile = group.FirstOrDefault(file => file.SidecarKind == PatchSidecarKind.Base);
				if (baseFile is null) continue;

				contentFacts.TryGetValue(profileEntry.NodeId, out var nodeFacts);
				var factGroup = nodeFacts?.PatchGroups.FirstOrDefault(candidate =>
					string.Equals(candidate.Id.SourceArchiveHex, group.Key.ArchiveHex16, StringComparison.OrdinalIgnoreCase)
					&& candidate.Id.SourcePatchIndex == group.Key.SourcePatchIndex
					&& candidate.NormalizedOrder == group.Key.NormalizedOrder);
				var luaKeys = factGroup?.AssetKeys.Where(IsLuaBootAssetKey).Distinct().ToArray() ?? Array.Empty<AssetKey>();
				// A lightweight AssetInventory may exist but still be incomplete for
				// a legacy or freshly generated boot archive. Do not silently deploy
				// such a Patch as ordinary content: the boot archive has one exact
				// identity, so a direct TOC read is a safe, narrow fallback.
				if (factGroup is not null && luaKeys.Length == 0 && IsLuaBootArchive(group.Key.ArchiveHex16))
					factGroup = null;
				HD2ModAdaptation.PatchReconstruction.PatchTocEntry? knownLuaEntry = null;
				IReadOnlyList<HD2ModAdaptation.PatchReconstruction.PatchTocEntry>? entries = null;
				var assetCount = factGroup?.AssetKeys.Count ?? 1;
				if (factGroup is not null)
				{
					if (luaKeys.Length == 0) continue;
					if (luaKeys.Length > 1)
					{
						// 清单把同一个 boot 资源列了两次。这是清单本身坏了，不是某种 mod
						// 形态；按它合并只能是猜。
						issues.Add(new CoreIssue(
							CoreIssueSeverity.Error,
							"LuaPatchMultipleEntrypointsUnsupported",
							"A patch group declares the boot Lua resource more than once and cannot be deployed safely.",
							baseFile.FilePath,
							profileEntry.NodeId));
						continue;
					}
					if (assetCount == 1)
					{
						knownLuaEntry = new HD2ModAdaptation.PatchReconstruction.PatchTocEntry(
							new HD2ModAdaptation.PatchReconstruction.AssetKey(luaKeys[0].TypeId, luaKeys[0].FileId),
							baseFile.FilePath,
							baseFile.FileName);
					}
				}
				else
				{
					try
					{
						entries = await new HD2ModAdaptation.PatchReconstruction.PatchTocScanner()
							.ScanEntriesAsync(baseFile.FilePath, cancellationToken)
							.ConfigureAwait(false);
					}
					catch (Exception exception) when (IsExpectedReadFailure(exception))
					{
						if (IsLuaBootArchive(group.Key.ArchiveHex16))
						{
							issues.Add(new CoreIssue(
								CoreIssueSeverity.Error,
								"LuaDetectionFailed",
								$"The Lua entry Archive could not be inspected safely: {exception.Message}",
								baseFile.FilePath,
								profileEntry.NodeId,
								ExceptionMessage: exception.ToString()));
						}
						continue;
					}

					var luaEntries = entries.Where(entry => LuaAssetKeys.IsBoot(entry.AssetKey)).ToArray();
					if (luaEntries.Length == 0) continue;
					if (luaEntries.Length > 1)
					{
						issues.Add(new CoreIssue(
							CoreIssueSeverity.Error,
							"LuaPatchMultipleEntrypointsUnsupported",
							"A patch group declares the boot Lua resource more than once and cannot be deployed safely.",
							baseFile.FilePath,
							profileEntry.NodeId));
						continue;
					}
					knownLuaEntry = luaEntries[0];
					assetCount = entries.Count;
				}

				if (knownLuaEntry is null) continue;
				// 一个 patch 里除了 boot 还带别的资源，是加载器类 mod 的正常形态
				// （引导桩 + 自己的核心模块 + 材质/gpu）。管理器只登记"这个组提供了
				// boot"这一件事，既不解析载荷，也不打算动它——它会被原样部署。
				bootProviders.Add(new LuaBootProvider(
					profileEntry.NodeId,
					profileEntry.LoadOrder,
					group.Key,
					baseFile));
			}
		}

		var externalMappings = await BuildExternalMappingsAsync(
			orderedEntries,
			snapshot,
			contentFacts,
			modsRootDirectory,
			gameDataDirectory,
			issues,
			cancellationToken).ConfigureAwait(false);

		GeneratedPatchGroup? armorAddon = null;
		// 独占入口时运行时的 Lua 文档。先建好但**不**立刻塞进 documents：
		// 只有确认"必须生成合并补丁"之后它才有用，否则那个文档会被丢弃。
		LuaDocumentSource? armorBootDocument = null;
		LuaDocumentSource? physicsBootDocument = null;
		if (_armorRegistry is not null)
		{
			// **这里原本有一块"离开真映射时还原注册表数据"。2026-11-11 起整块删除。**
			// 还原的实现是"用空结构编译一次再写盘"，也就是**写 `.dl_bin`**；
			// 而游戏这一版改了该文件的校验方式，写过的文件一进游戏就闪退。
			// 也就是说，用户想"安全回退"的那个动作本身就是那次崩溃——必须阻断，
			// 而不是让它带着一条 Warning 继续跑。守卫在 ArmorRegistryDataMapper.ApplyAsync。
			try
			{
				var registrySnapshot = await _armorRegistry.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
				var mappingsEnabled = registrySnapshot.Document.Enabled
					&& await _armorRegistry.IsManagedDeploymentEnabledAsync(cancellationToken).ConfigureAwait(false);
				// 真映射模式不投放任何 Lua：外观由注册表数据承载，这里连隐藏 unit 补丁都不需要
				// （件表直接写成目标件集，没有"来源缺槽"的问题）。
				if (mappingsEnabled && EffectiveMappingMode.UsesRuntimeLua())
				{
					if (EffectiveMappingMode == ArmorMappingMode.ExternalLuaEntry)
					{
						// 外置入口：运行时 + 隐藏 unit 打包成一个独立补丁，管理器不碰 boot。
						armorAddon = await CreateArmorRegistryAddonAsync(
							modsRootDirectory,
							materialize,
							cancellationToken).ConfigureAwait(false);
					}
					else
					{
						// 独占入口：管理器自己占用 boot，由游戏自身的 Lua VM 加载运行时。
						var runtimeDocument = await CreateArmorRegistryDocumentAsync(gameDataDirectory, cancellationToken).ConfigureAwait(false);
						armorBootDocument = new LuaDocumentSource(
							ArmorRegistryNodeId,
							int.MaxValue,
							new PatchGroupIdentity(ArmorRegistryNodeId, $"{LuaAssetKeys.BootArchiveId:x16}", 0, int.MaxValue),
							runtimeDocument);

						// **隐藏 unit 仍然必须落地。** `wardrobe.links` 里"来源缺槽位"的条目指向它，
						// 而"指向不存在的 unit"会让引擎记住加载失败 ⇒ 军械库整页空白并崩溃
						// （判据见 WardrobeLinkCompiler.HiddenUnit 与本文件 CreateArmorRegistryAddonAsync 的注释）。
						// 这一路只需要那个 unit：运行时的 Lua 在上面那份 boot 文档里，不能再塞第二份
						// （引擎只执行一个 boot，多塞的那份不会跑，还白占一个 patch 序号）。
						// 2026-11-12 实测：独占模式下 2007 条 link 里有 948 条指向它，而它从没被投放
						// （外置模式的旧产物停在 11-01）⇒ 那 948 个槽位全指向一个不存在的 id。
						//
						// **序号必须是"倒数第二"**：unit 补丁排在 boot 补丁**之前**（见下面 placement），
						// 于是 boot 永远是最大的那一个序号。用户 2026-11-12 定的口径：
						// "这个 patch 文件如果是独占，则总是出现在倒数第二个序号，而 lua patch 总是出现在最后"。
						armorAddon = (await CreateArmorRegistryAddonAsync(
							modsRootDirectory,
							materialize,
							cancellationToken,
							includeRuntime: false).ConfigureAwait(false))
							with { PlacementOrder = HiddenUnitPlacementOrder };
					}
				}
			}
			catch (Exception exception) when (exception is IOException
				or UnauthorizedAccessException
				or InvalidDataException
				or LuaPatchFormatException)
			{
				issues.Add(new CoreIssue(
					CoreIssueSeverity.Error,
					"RuntimeArmorRegistryPreparationFailed",
					$"无法准备运行时护甲注册组件：{exception.Message}",
					ExceptionMessage: exception.ToString()));
			}
		}

		if (issues.Any(issue => issue.Severity == CoreIssueSeverity.Error))
			return new LuaBuildResult(patchIndex, Array.Empty<GeneratedPatchGroup>(), externalMappings, issues, false, null, documents.Count);

		// 按实例隐藏：把启用中的 Mod 的 workspace 记录编译成规则，真部署时整份原子写盘
		// （没有规则也写一份空的，覆盖上一版，否则运行时会继续执行过期规则）。
		var kitRuleCount = await PrepareKitInstanceRulesAsync(
			orderedEntries.Where(entry => snapshot.Nodes.ContainsKey(entry.NodeId)).Select(entry => entry.NodeId).ToArray(),
			snapshot,
			contentFacts,
			modsRootDirectory,
			gameDataDirectory,
			materialize,
			issues,
			cancellationToken).ConfigureAwait(false);
		if (issues.Any(issue => issue.Severity == CoreIssueSeverity.Error))
			return new LuaBuildResult(patchIndex, Array.Empty<GeneratedPatchGroup>(), externalMappings, issues, false, null, documents.Count);

		// 管理器不再合并任何第三方 Lua。
		//
		// 合并唯一买到的能力是"没有加载器时，让替换 boot 的第三方 mod 和我们的运行时
		// 共存"。代价是一整套第三方 boot 解析器、merge ledger、opaque 回退，以及一个
		// "允许不安全 Lua 合并"的用户开关；而且每出现一种新的 mod 形态就会撞墙
		// （2026-10-28 撞的是加载器包：引导桩 + 自身模块 + gpu 在同一个 patch 里）。
		// 让多个第三方 Lua mod 共存是**加载器**的职责，不是管理器的职责。
		//
		if (EffectivePhysicsMode == PhysicsMode.ExclusiveLuaEntry)
		{
			// 物理「独占 boot」：把物理运行时也塞进**同一个** boot 文档。
			// 引擎只执行一个 Lua 启动脚本，所以两路 Lua 功能必须合并成一个文档里的两个块，
			// 各占一份 patch 会互相顶掉（引擎只跑最高号 patch 的 boot）。
			try
			{
				var physicsDocument = await CreateBootDocumentAsync(
					gameDataDirectory,
					[new BootBlock(
						"HD2 Physics Bone Runtime",
						PhysBoneLuaSource.ComposeStandalonePackage(LuaAddonCatalog.PhysicsAddonId, mode: "boot"),
						LuaAddonCatalog.PhysicsAddonId)],
					cancellationToken).ConfigureAwait(false);
				physicsBootDocument = new LuaDocumentSource(
					PhysicsRuntimeNodeId,
					int.MaxValue,
					new PatchGroupIdentity(PhysicsRuntimeNodeId, $"{LuaAssetKeys.BootArchiveId:x16}", 0, int.MaxValue),
					physicsDocument);
			}
			catch (Exception exception) when (exception is IOException
				or UnauthorizedAccessException
				or InvalidDataException
				or LuaPatchFormatException)
			{
				issues.Add(new CoreIssue(
					CoreIssueSeverity.Error,
					"PhysicsRuntimePreparationFailed",
					$"无法准备物理运行时启动组件：{exception.Message}",
					ExceptionMessage: exception.ToString()));
			}
		}

		// 现在的规则只有两条：
		//   1. 第三方 Lua patch 一律原样部署，管理器不解析、不合并、不改一个字节；
		//   2. 独占模式下运行时必须独占 boot，别人也占就明确报错，让用户自己选。
		if (armorBootDocument is null && physicsBootDocument is null)
		{
			// 外置 / 真映射：管理器自己不占 boot，只把别人的争用情况说清楚。
			ReportBootEntryContention(bootProviders, issues, ownerIsManager: false);
			var addonGroups = new List<GeneratedPatchGroup>();
			if (armorAddon is not null) addonGroups.Add(armorAddon);
			string? hiderFingerprint = null;
			if (kitRuleCount > 0)
			{
				// 管理器不占 boot：Hider 做成独立 addon，交给第三方加载器加载。
				// 加载器可能装在库外（直接放进游戏目录），所以没有提供 boot 的库内 Mod 时照样投放，只加一条提示。
				try
				{
					hiderFingerprint = KitInstanceHiderFingerprint();
					addonGroups.Add(await CreateKitInstanceHiderAddonAsync(
						modsRootDirectory, hiderFingerprint, materialize, cancellationToken).ConfigureAwait(false));
				}
				catch (Exception exception) when (exception is IOException
					or UnauthorizedAccessException
					or InvalidDataException
					or InvalidOperationException)
				{
					issues.Add(new CoreIssue(
						CoreIssueSeverity.Error,
						"KitInstanceHiderPreparationFailed",
						$"无法准备「按实例隐藏」运行时：{exception.Message}",
						ExceptionMessage: exception.ToString()));
					return new LuaBuildResult(patchIndex, Array.Empty<GeneratedPatchGroup>(), externalMappings, issues, false, null, 0);
				}
				if (bootProviders.Count == 0)
				{
					issues.Add(new CoreIssue(
						CoreIssueSeverity.Warning,
						"KitInstanceHiderNeedsLoader",
						"按实例隐藏需要一个 Lua 加载器（如 MDL3），或把护甲映射/物理设为独占入口。"));
				}
			}
			var fingerprintParts = new List<string>();
			if (armorAddon is not null) fingerprintParts.Add(ArmorRegistrySourceFingerprint());
			if (hiderFingerprint is not null) fingerprintParts.Add(hiderFingerprint);
			return new LuaBuildResult(
				FilterPatchIndex(patchIndex, managedRemovedGroups),
				addonGroups,
				externalMappings,
				issues,
				addonGroups.Count > 0,
				fingerprintParts.Count == 0 ? null : fingerprintParts.Count == 1 ? fingerprintParts[0] : LuaHashFor(Encoding.UTF8.GetBytes(string.Join('|', fingerprintParts))),
				0);
		}

		if (bootProviders.Count > 0)
		{
			// 运行时独占 boot，但别的 mod 也要它：引擎只执行一个 Lua 启动脚本，
			// 两者不可能同时生效，必须让用户二选一。
			ReportBootEntryContention(bootProviders, issues, ownerIsManager: true);
			return new LuaBuildResult(patchIndex, Array.Empty<GeneratedPatchGroup>(), externalMappings, issues, false, null, 0);
		}

		// 两路都选独占时，把物理的块并进同一个文档：一个 boot patch 里跑两个运行时。
		if (armorBootDocument is not null && physicsBootDocument is not null)
		{
			var mergedBlocks = armorBootDocument.Document.Blocks
				.Concat(physicsBootDocument.Document.Blocks)
				.ToArray();
			armorBootDocument = armorBootDocument with
			{
				Document = armorBootDocument.Document with { Blocks = mergedBlocks },
			};
		}
		var bootDocument = armorBootDocument ?? physicsBootDocument!;
		if (kitRuleCount > 0)
		{
			// 管理器占着 boot：Hider 作为**最后一个**块并进同一个文档。放最后是因为它的 update 钩子
			// 要接在前面各块之后（映射运行时直接整体替换 update，Hider 装在它前面会被顶掉）。
			var hiderBlocks = bootDocument.Document.Blocks.ToList();
			hiderBlocks.Add(BuildBootBlock(
				hiderBlocks.Count,
				new BootBlock("HD2 Kit Instance Hider", KitInstanceHiderLuaSource.Load(), KitInstanceHiderLuaSource.ResourceName)));
			bootDocument = bootDocument with { Document = bootDocument.Document with { Blocks = hiderBlocks } };
		}
		documents.Add(bootDocument);
		var filteredIndex = patchIndex;

		GeneratedPatchGroup generated;
		string? fingerprint = null;
		try
		{
			// 只有一个文档：管理器自己的运行时。它由"重放游戏原始 boot 字节码 + 用
			// loadstring 跑运行时源码"封装而成——这正是任何加载器用的同一个套路，
			// 只是这里的派发者是我们自己。
			var composition = _luaComposer.Wrap(bootDocument.Document);
			// The game now only accepts a compiled chunk. Wrapping the source in one
			// fixed prototype keeps the bytes on disk in the same shape as the game's
			// own boot resource while the readable source stays available for
			// diagnostics and validation.
			composition = composition with { CompiledPayload = Lua51WrapperChunk.Build(composition.SourceText) };
			// ⚠️ 指纹必须覆盖**将要写出去的字节**，而不只是"输入有哪些 Mod"。
			// 2026-11-09 踩过：运行时从 2.0.1 改到 2.0.2，Mod 一个没动 ⇒ 指纹没变 ⇒
			// `.runtime/lua/<指纹>` 里那份旧包被原样发到游戏里，版本还是 2.0.1，
			// 而整条链上没有任何报错（部署成功、游戏里跑的是旧代码）。
			// 把最终源码的哈希拼进指纹，这类"缓存比源码新"的问题就不可能再出现。
			var composedSourceHash = LuaHashFor(Encoding.UTF8.GetBytes(composition.SourceText));
			var gameRoot = Directory.GetParent(Path.GetFullPath(gameDataDirectory))?.FullName;
			if (validationMode is not null && !string.IsNullOrWhiteSpace(gameRoot))
			{
				var luaLibraryPath = Path.Combine(gameRoot, "bin", "lua51.dll");
				var validation = validationMode == LuaDeploymentValidationMode.CompileOnly
					? await _luaRuntimeValidator.ValidateCompilationAsync(composition.SourceText, luaLibraryPath, cancellationToken).ConfigureAwait(false)
					: await _luaRuntimeValidator.ValidateAsync(composition.SourceText, luaLibraryPath, cancellationToken).ConfigureAwait(false);
				if (!validation.Available)
				{
					issues.Add(new CoreIssue(CoreIssueSeverity.Warning, "LuaRuntimeValidationUnavailable", validation.Message ?? "The game's Lua runtime was unavailable; the generated Lua was only structurally validated."));
				}
				else if (!validation.CompileSucceeded)
				{
					issues.Add(new CoreIssue(CoreIssueSeverity.Error, "LuaRuntimeCompileFailed", validation.Message ?? "The generated Lua chunk failed to compile."));
					return new LuaBuildResult(patchIndex, Array.Empty<GeneratedPatchGroup>(), externalMappings, issues, false, null, documents.Count);
				}
				else if (validationMode == LuaDeploymentValidationMode.CompileAndSmoke && !validation.RuntimeSucceeded)
				{
					var severity = validation.EnvironmentLimited ? CoreIssueSeverity.Warning : CoreIssueSeverity.Error;
					issues.Add(new CoreIssue(severity, "LuaRuntimeSmokeTestFailed", validation.Message ?? "The generated Lua chunk failed its smoke test."));
					if (severity == CoreIssueSeverity.Error)
					return new LuaBuildResult(patchIndex, Array.Empty<GeneratedPatchGroup>(), externalMappings, issues, false, null, documents.Count);
				}
			}
			fingerprint = CreateCompositionFingerprint(documents, composedSourceHash);
			var outputDirectory = Path.Combine(Path.GetFullPath(modsRootDirectory), ".runtime", "lua", fingerprint);
			var outputPath = Path.Combine(outputDirectory, $"{LuaAssetKeys.BootArchiveId:x16}.patch_0");
			GeneratedPatchFile[] outputFiles;
			if (materialize)
			{
				var write = await _luaComposer.WriteAsync(outputPath, composition, cancellationToken).ConfigureAwait(false);
				outputFiles =
				[
					CreateGeneratedFile(write.PatchPath, PatchSidecarKind.Base),
					CreateGeneratedFile(write.StreamPath, PatchSidecarKind.Stream),
					CreateGeneratedFile(write.GpuResourcesPath, PatchSidecarKind.GpuResources),
				];
			}
			else
			{
				var expectedPatchLength = checked(composition.HeaderTemplate.Length + composition.PayloadBytes.LongLength);
				outputFiles =
				[
					new GeneratedPatchFile(PatchSidecarKind.Base, outputPath, Path.GetFileName(outputPath), expectedPatchLength, DateTimeOffset.MinValue),
					new GeneratedPatchFile(PatchSidecarKind.Stream, outputPath + ".stream", Path.GetFileName(outputPath) + ".stream", 0, DateTimeOffset.MinValue),
					new GeneratedPatchFile(PatchSidecarKind.GpuResources, outputPath + ".gpu_resources", Path.GetFileName(outputPath) + ".gpu_resources", 0, DateTimeOffset.MinValue),
				];
			}
			generated = new GeneratedPatchGroup(
				$"{LuaAssetKeys.BootArchiveId:x16}",
				0,
				outputFiles,
				"HD2LuaMergeV1 generated Lua patch",
				// **boot 补丁恒定是最大序号**（用户口径）：它是"游戏启动脚本"这一件事的载体，
				// 排在最后既保证它压过一切，也保证隐藏 unit 补丁稳落在它前面（见 HiddenUnitPlacementOrder）。
				PlacementOrder: int.MaxValue);
		}
		catch (Exception exception) when (exception is LuaPatchFormatException or IOException or UnauthorizedAccessException)
		{
			issues.Add(new CoreIssue(CoreIssueSeverity.Error, "LuaMergeFailed", exception.Message, ExceptionMessage: exception.ToString()));
			return new LuaBuildResult(patchIndex, Array.Empty<GeneratedPatchGroup>(), externalMappings, issues, false, null, documents.Count);
		}

		var generatedGroups = armorAddon is null
			? new[] { generated }
			: new[] { generated, armorAddon };
		return new LuaBuildResult(filteredIndex, generatedGroups, externalMappings, issues, true, fingerprint, documents.Count);
	}

	// 注意：这判断的是"**引导脚本**这一个资源"，不是"任何 Lua 资源"。
	// 加载器类 mod 的 patch 里还有它自己的 Lua 模块，那些不该被当成入口。
	private static bool IsLuaBootAssetKey(AssetKey key)
		=> key.TypeId == LuaAssetKeys.LuaTypeId && key.FileId == LuaAssetKeys.BootFileId;

	/// <summary>
	/// 报告"Lua 启动入口被多个提供者争用"。
	///
	/// 引擎只执行 <c>data/settings.ini</c> 里 <c>boot_script</c> 指定的那一个 Lua 资源
	/// （实测见 <c>HD2RuntimeArmorOverride/notes/lua-entry-capacity-investigation-20261028.md</c>），
	/// 所以这里不存在"合并一下让它们都跑"的解法——只能让用户自己选。
	/// 管理器不再解析或合并第三方 Lua，因此这条诊断是唯一能防止"某个 mod 静默不生效"的东西。
	/// </summary>
	private static void ReportBootEntryContention(
		IReadOnlyList<LuaBootProvider> providers,
		ICollection<CoreIssue> issues,
		bool ownerIsManager)
	{
		if (providers.Count == 0) return;
		var names = string.Join("、", providers.Select(provider => Path.GetFileName(provider.BaseFile.FilePath)));
		if (ownerIsManager)
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				"LuaBootEntryConflict",
				$"独占模式下管理器要独占 Lua 启动入口 boot，但这些已启用的 mod 也提供了它：{names}。"
				+ "引擎只会执行一个 Lua 启动脚本，两者无法同时生效。请二选一："
				+ "把护甲映射模式改成“外置 lua 入口兼容”（由加载器统一派发），或禁用上面这些 mod。"));
			return;
		}

		if (providers.Count < 2) return;
		issues.Add(new CoreIssue(
			CoreIssueSeverity.Warning,
			"LuaBootEntryOverlap",
			$"以下已启用的 mod 都替换了 Lua 启动入口 boot：{names}。引擎只会执行补丁序号最大的那一份，其余不会生效。"
			+ "如需它们同时工作，请安装 Lua 加载器（MDL / boot-mod-loader 等）并把护甲映射模式设为“外置 lua 入口兼容”。"));
	}

	private static bool IsLuaBootArchive(string archiveHex16)
		=> string.Equals(archiveHex16, $"{LuaAssetKeys.BootArchiveId:x16}", StringComparison.OrdinalIgnoreCase);

	private static PatchFileIndex FilterPatchIndex(
		PatchFileIndex patchIndex,
		IReadOnlySet<PatchGroupIdentity> removedGroups)
	{
		if (removedGroups.Count == 0) return patchIndex;
		var filteredFiles = patchIndex.FilesByNode.ToDictionary(
			pair => pair.Key,
			pair => (IReadOnlyList<IndexedPatchFile>)pair.Value
				.Where(file => !removedGroups.Contains(new PatchGroupIdentity(
					file.NodeId,
					file.ArchiveHex16,
					file.SourcePatchIndex,
					file.NormalizedOrder)))
				.ToArray());
		return patchIndex with { FilesByNode = filteredFiles };
	}

	private static async ValueTask<GeneratedPatchGroup> CreateArmorRegistryAddonAsync(
		string modsRootDirectory,
		bool materialize,
		CancellationToken cancellationToken,
		bool includeRuntime = true)
	{
		var source = RuntimeArmorRegistryLuaSource.Load();
		var fingerprint = ArmorRegistrySourceFingerprint(includeRuntime);
		var outputDirectory = Path.Combine(
			Path.GetFullPath(modsRootDirectory),
			".runtime",
			"armor-registry-addon",
			fingerprint);
		var outputPath = Path.Combine(outputDirectory, $"{LuaAssetKeys.BootArchiveId:x16}.patch_0");

		// 一个 patch 两个条目：Lua 资源 + 空 unit（`includeRuntime: false` 时只有空 unit）。
		//
		// 空 unit 必须真实存在于游戏里：运行时把"来源缺的槽位"指向它，指向不存在的 id 会让
		// 引擎记住加载失败，之后军械库整页空白并崩溃。归档布局交给 PatchArchiveWriter，
		// 以嵌入的空 unit 补丁为模板追加 Lua 条目——不自己拼字节，避免出现第二套布局逻辑。
		//
		// ⚠️ 独占 boot 模式也走这里（只投放 unit，不投放 Lua）：那一路的运行时在 boot 文档里，
		// 但 `wardrobe.links` 里"来源缺槽位"的条目**仍然指向这个 unit**。2026-11-12 实测：
		// 独占模式下 2007 条 link 里有 948 条指向它，而它从没被投放（外置模式的旧产物停在 11-01），
		// 于是那 948 个槽位全都指向一个不存在的 id —— 正是上面那句注释描述的情形。
		var scratch = Path.Combine(Path.GetTempPath(), "hd2mod-armor-addon-" + Guid.NewGuid().ToString("N"));
		var templateDirectory = Path.Combine(scratch, "template");
		var buildDirectory = Path.Combine(scratch, "build");
		Directory.CreateDirectory(templateDirectory);
		try
		{
			var templatePath = Path.Combine(templateDirectory, $"{LuaAssetKeys.BootArchiveId:x16}.patch_0");
			await File.WriteAllBytesAsync(templatePath, WardrobeHiddenUnitTemplate.ReadPatch(), cancellationToken).ConfigureAwait(false);
			await File.WriteAllBytesAsync(templatePath + ".gpu_resources", WardrobeHiddenUnitTemplate.ReadGpu(), cancellationToken).ConfigureAwait(false);
			await File.WriteAllBytesAsync(templatePath + ".stream", [], cancellationToken).ConfigureAwait(false);

			// includeRuntime=false 时只投放那个 unit：用户关掉"独占映射 lua"后，引导脚本
			// 交回给游戏/加载器，映射本身（如果有别的链路在用）仍然需要这个 unit 存在。
			PatchArchiveAdditionalEntry[] additions = includeRuntime
				?
				[
					new PatchArchiveAdditionalEntry(
						new PatchAssetKey(LuaAssetKeys.LuaTypeId, StingrayHash(ArmorAddonResourceName)),
						BuildLuaResource(source),
						[],
						[]),
				]
				: [];
			var written = await new PatchArchiveWriter().WriteAsync(
				templatePath,
				buildDirectory,
				Array.Empty<PatchUnitMeshEditResult>(),
				additions,
				removedEntries: null,
				overwriteExisting: true,
				preserveOriginalStream: true,
				headerTemplateTocData: null,
				cancellationToken).ConfigureAwait(false);

			if (materialize)
			{
				Directory.CreateDirectory(outputDirectory);
				File.Copy(written.TocFilePath, outputPath, overwrite: true);
				File.Copy(written.GpuResourceFilePath, outputPath + ".gpu_resources", overwrite: true);
				await File.WriteAllBytesAsync(outputPath + ".stream", [], cancellationToken).ConfigureAwait(false);
			}

			GeneratedPatchFile[] files = materialize
				?
				[
					CreateGeneratedFile(outputPath, PatchSidecarKind.Base),
					CreateGeneratedFile(outputPath + ".stream", PatchSidecarKind.Stream),
					CreateGeneratedFile(outputPath + ".gpu_resources", PatchSidecarKind.GpuResources),
				]
				:
				[
					new GeneratedPatchFile(PatchSidecarKind.Base, outputPath, Path.GetFileName(outputPath), written.TocFileSize, DateTimeOffset.MinValue),
					new GeneratedPatchFile(PatchSidecarKind.Stream, outputPath + ".stream", Path.GetFileName(outputPath) + ".stream", 0, DateTimeOffset.MinValue),
					new GeneratedPatchFile(PatchSidecarKind.GpuResources, outputPath + ".gpu_resources", Path.GetFileName(outputPath) + ".gpu_resources", written.GpuResourceFileSize, DateTimeOffset.MinValue),
				];
			return new GeneratedPatchGroup(
				$"{LuaAssetKeys.BootArchiveId:x16}",
				0,
				files,
				"HD2 Runtime Armor Registry managed addon",
				PlacementOrder: int.MaxValue);
		}
		finally
		{
			try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
			catch (IOException) { }
			catch (UnauthorizedAccessException) { }
		}
	}

	/// <summary>
	/// 编译启用中的 Mod 的「按实例隐藏」记录；真部署（<paramref name="materialize"/>）时把规则整份原子写进
	/// ArmorRegistry 目录（没有规则也写空文件）。返回生效的规则条数。
	/// <para>
	/// workspace 读不出来（损坏）按「没有规则」处理并给 Warning，而不是阻断部署：此时写出空规则，
	/// 运行时不再隐藏任何东西，用户看得到问题而不是看到缺身体。
	/// </para>
	/// </summary>
	private async ValueTask<int> PrepareKitInstanceRulesAsync(
		IReadOnlyCollection<ModNodeId> enabledNodes,
		LibrarySnapshot snapshot,
		IReadOnlyDictionary<ModNodeId, ModContentFacts> contentFacts,
		string modsRootDirectory,
		string gameDataDirectory,
		bool materialize,
		ICollection<CoreIssue> issues,
		CancellationToken cancellationToken)
	{
		KitInstanceWorkspace workspace;
		try
		{
			workspace = await KitInstanceWorkspaceStore.ReadAsync(modsRootDirectory, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Warning,
				"KitInstanceWorkspaceUnreadable",
				$"无法读取「按实例隐藏」记录，本次按没有规则处理：{exception.Message}",
				KitInstanceWorkspaceStore.GetPath(modsRootDirectory),
				ExceptionMessage: exception.ToString()));
			workspace = KitInstanceWorkspace.Empty;
		}

		var previousRevision = _kitInstanceRegistryRoot is null ? 0 : KitInstanceRulesFile.ReadRevision(_kitInstanceRegistryRoot);
		// 内容指纹：记录生成之后 Mod 的基底被更新过（指纹不一致），这条记录本次不生效并给 Warning，让用户重新转换。
		var compiled = KitInstanceRulesCompiler.Compile(
			enabledNodes,
			workspace,
			previousRevision,
			nodeId => snapshot.Nodes.TryGetValue(nodeId, out var node)
				? KitInstanceContentFingerprint.Compute(Path.Combine(modsRootDirectory, node.RelativePath))
				: null);
		foreach (var issue in compiled.Issues) issues.Add(issue);

		await AddKitPollutionWarningsAsync(
			enabledNodes, snapshot, contentFacts, workspace, gameDataDirectory, issues, cancellationToken).ConfigureAwait(false);

		if (materialize && _kitInstanceRegistryRoot is not null)
		{
			try
			{
				await KitInstanceRulesFile.WriteAsync(_kitInstanceRegistryRoot, compiled.Document, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				issues.Add(new CoreIssue(
					CoreIssueSeverity.Error,
					"KitInstanceRulesWriteFailed",
					$"无法写入「按实例隐藏」规则文件，运行时可能继续使用上一版：{exception.Message}",
					KitInstanceRulesFile.GetPath(_kitInstanceRegistryRoot),
					ExceptionMessage: exception.ToString()));
			}
		}
		return compiled.Document.Rules.Count;
	}

	/// <summary>
	/// P2 发现污染：每个启用中、有污染（受影响的套 ≥ 2）、且 workspace 里没有记录的 Mod 加一条 Warning，不阻断部署。
	/// 材质缓存没好时只用 Unit 部分的结论，并在后台开始构建；这里<b>绝不等待构建</b>，分析本身任何失败都只是不报。
	/// 放在这里而不是 ProfileApplyService：「按实例隐藏」的记录（workspace）与 P1 的
	/// <c>KitInstanceHiderNeedsLoader</c> 等提示都在这个方法所在的 Lua 部署阶段汇总，
	/// 计划预览与真部署走同一条路径，界面已有的「部署问题」列表直接显示它。
	/// </summary>
	private async ValueTask AddKitPollutionWarningsAsync(
		IReadOnlyCollection<ModNodeId> enabledNodes,
		LibrarySnapshot snapshot,
		IReadOnlyDictionary<ModNodeId, ModContentFacts> contentFacts,
		KitInstanceWorkspace workspace,
		string gameDataDirectory,
		ICollection<CoreIssue> issues,
		CancellationToken cancellationToken)
	{
		if (_kitPollution is null) return;
		foreach (var nodeId in enabledNodes.Distinct().OrderBy(node => node.Value))
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (workspace.Records.ContainsKey(nodeId)) continue;
			if (!contentFacts.TryGetValue(nodeId, out var facts) || !snapshot.Nodes.TryGetValue(nodeId, out var node)) continue;

			KitPollutionResult? result;
			try
			{
				result = await _kitPollution.AnalyzeAsync(
					facts.Structure.Assets.Select(asset => asset.AssetKey), gameDataDirectory, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is not OperationCanceledException)
			{
				// 污染提示是锦上添花：分析失败不能让部署出问题，也不值得吓用户。
				continue;
			}
			if (result is not { IsPolluted: true }) continue;

			var name = string.IsNullOrWhiteSpace(node.Metadata.Name) ? node.RelativePath : node.Metadata.Name;
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Warning,
				"KitInstancePollution",
				KitPollutionAnalyzer.FormatDeploymentWarning(name, result),
				NodeId: nodeId));
		}
	}

	/// <summary>Hider 独立 addon 的内容指纹：覆盖将写出的全部字节（脚本源码 + 资源名 + 封装版本）。</summary>
	private static string KitInstanceHiderFingerprint()
		=> LuaHashFor(Encoding.UTF8.GetBytes(
			"kit-instance-hider-addon:v1:" + KitInstanceHiderLuaSource.ResourceName + "\n" + KitInstanceHiderLuaSource.Load()));

	/// <summary>
	/// 生成 Hider 的独立 addon 补丁：只有一个 Lua 资源，**不带**隐藏 unit
	/// （隐藏 unit 属于护甲映射，见 <see cref="CreateArmorRegistryAddonAsync"/>）。
	/// 打包仍走 <see cref="PatchArchiveWriter"/>：以嵌入的空 unit 补丁为模板，移除它自己的 unit 条目，
	/// 再追加 Lua 条目——不自己拼字节，避免出现第二套布局逻辑。
	/// </summary>
	private static async ValueTask<GeneratedPatchGroup> CreateKitInstanceHiderAddonAsync(
		string modsRootDirectory,
		string fingerprint,
		bool materialize,
		CancellationToken cancellationToken)
	{
		var source = KitInstanceHiderLuaSource.Load();
		var outputDirectory = Path.Combine(
			Path.GetFullPath(modsRootDirectory),
			".runtime",
			"kit-instance-hider",
			fingerprint);
		var outputPath = Path.Combine(outputDirectory, $"{LuaAssetKeys.BootArchiveId:x16}.patch_0");

		var scratch = Path.Combine(Path.GetTempPath(), "hd2mod-kit-hider-" + Guid.NewGuid().ToString("N"));
		var templateDirectory = Path.Combine(scratch, "template");
		var buildDirectory = Path.Combine(scratch, "build");
		Directory.CreateDirectory(templateDirectory);
		try
		{
			var templatePath = Path.Combine(templateDirectory, $"{LuaAssetKeys.BootArchiveId:x16}.patch_0");
			await File.WriteAllBytesAsync(templatePath, WardrobeHiddenUnitTemplate.ReadPatch(), cancellationToken).ConfigureAwait(false);
			await File.WriteAllBytesAsync(templatePath + ".gpu_resources", WardrobeHiddenUnitTemplate.ReadGpu(), cancellationToken).ConfigureAwait(false);
			await File.WriteAllBytesAsync(templatePath + ".stream", [], cancellationToken).ConfigureAwait(false);
			var templateEntries = await new HD2ModAdaptation.PatchReconstruction.PatchTocScanner()
				.ScanEntriesAsync(templatePath, cancellationToken).ConfigureAwait(false);

			PatchArchiveAdditionalEntry[] additions =
			[
				new PatchArchiveAdditionalEntry(
					new PatchAssetKey(LuaAssetKeys.LuaTypeId, StingrayHash(KitInstanceHiderLuaSource.ResourceName)),
					BuildLuaResource(source, KitInstanceHiderLuaSource.ResourceName),
					[],
					[]),
			];
			var written = await new PatchArchiveWriter().WriteAsync(
				templatePath,
				buildDirectory,
				Array.Empty<PatchUnitMeshEditResult>(),
				additions,
				removedEntries: templateEntries,
				overwriteExisting: true,
				preserveOriginalStream: false,
				headerTemplateTocData: null,
				cancellationToken).ConfigureAwait(false);

			if (materialize)
			{
				Directory.CreateDirectory(outputDirectory);
				File.Copy(written.TocFilePath, outputPath, overwrite: true);
				// 只有 Lua 资源时没有 gpu 数据，写器会删掉空的 .gpu_resources；补一个空的，三个旁文件一起部署。
				if (File.Exists(written.GpuResourceFilePath))
					File.Copy(written.GpuResourceFilePath, outputPath + ".gpu_resources", overwrite: true);
				else
					await File.WriteAllBytesAsync(outputPath + ".gpu_resources", [], cancellationToken).ConfigureAwait(false);
				await File.WriteAllBytesAsync(outputPath + ".stream", [], cancellationToken).ConfigureAwait(false);
			}

			GeneratedPatchFile[] files = materialize
				?
				[
					CreateGeneratedFile(outputPath, PatchSidecarKind.Base),
					CreateGeneratedFile(outputPath + ".stream", PatchSidecarKind.Stream),
					CreateGeneratedFile(outputPath + ".gpu_resources", PatchSidecarKind.GpuResources),
				]
				:
				[
					new GeneratedPatchFile(PatchSidecarKind.Base, outputPath, Path.GetFileName(outputPath), written.TocFileSize, DateTimeOffset.MinValue),
					new GeneratedPatchFile(PatchSidecarKind.Stream, outputPath + ".stream", Path.GetFileName(outputPath) + ".stream", 0, DateTimeOffset.MinValue),
					new GeneratedPatchFile(PatchSidecarKind.GpuResources, outputPath + ".gpu_resources", Path.GetFileName(outputPath) + ".gpu_resources", 0, DateTimeOffset.MinValue),
				];
			return new GeneratedPatchGroup(
				$"{LuaAssetKeys.BootArchiveId:x16}",
				0,
				files,
				"HD2 Kit Instance Hider managed addon",
				PlacementOrder: KitInstanceHiderPlacementOrder);
		}
		finally
		{
			try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
			catch (IOException) { }
			catch (UnauthorizedAccessException) { }
		}
	}

	/// <summary>
	/// 管理器的 Lua 运行时在补丁里的资源名。它的哈希就是引擎加载引导脚本用的 id
	/// （不是 <c>LuaAssetKeys.BootFileId</c>——那个是游戏自身引导脚本的 id）。
	/// </summary>
	private const string ArmorAddonResourceName = "mods/codex/runtime_armor_registry";

	/// <summary>
	/// Lua 资源的载荷：<c>u32 长度 + u32 版本(2) + 源码</c>。版本 2 表示载荷是源码文本，
	/// 这版引擎接受它，省掉一次字节码编译。
	/// </summary>
	private static byte[] BuildLuaResource(string source, string resourceName = ArmorAddonResourceName)
	{
		var declaration = $"-- HD2-Addon: {resourceName}\n";
		var body = Encoding.UTF8.GetBytes(
			source.StartsWith("-- HD2-Addon:", StringComparison.Ordinal)
				? source
				: declaration + source);
		var resource = new byte[8 + body.Length];
		BinaryPrimitives.WriteUInt32LittleEndian(resource.AsSpan(0), checked((uint)body.Length));
		BinaryPrimitives.WriteUInt32LittleEndian(resource.AsSpan(4), 2);
		body.CopyTo(resource.AsSpan(8));
		return resource;
	}

	private static int Align16(int value) => checked((value + 15) & ~15);

	private static ulong StingrayHash(string value)
	{
		const ulong mix = 0xC6A4A7935BD1E995;
		var data = Encoding.UTF8.GetBytes(value);
		var hash = unchecked((ulong)data.Length * mix);
		var offset = 0;
		while (offset + 8 <= data.Length)
		{
			var word = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset, 8));
			word = unchecked(word * mix);
			word ^= word >> 47;
			hash = unchecked((hash ^ unchecked(word * mix)) * mix);
			offset += 8;
		}
		if (offset < data.Length)
		{
			ulong tail = 0;
			for (var index = 0; offset + index < data.Length; index++)
				tail |= (ulong)data[offset + index] << (index * 8);
			hash = unchecked((hash ^ tail) * mix);
		}
		hash ^= hash >> 47;
		hash = unchecked(hash * mix);
		return hash ^ (hash >> 47);
	}

	/// <summary>
	/// 隐藏 unit 补丁的内容指纹。
	/// <para>
	/// <paramref name="includeRuntime"/> <b>参与哈希</b>：只投放 unit 的那一份（独占 boot 模式要用的）
	/// 与"unit + 运行时 Lua"那一份**字节不同**，共用一个目录会让两者互相覆盖。这与
	/// `.runtime/lua` 那条链在 2026-11-09 踩过的坑是同一类：指纹必须覆盖"将要写出去的字节"。
	/// </para>
	/// </summary>
	private static string ArmorRegistrySourceFingerprint(bool includeRuntime = true)
		=> Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
				includeRuntime
					? RuntimeArmorRegistryLuaSource.Load()
					: RuntimeArmorRegistryLuaSource.Load() + "\n-- hidden-unit-only (no runtime block)\n")))
			.ToLowerInvariant();

	/// <summary>boot 里的一个运行时块：名字（诊断用）+ 源码 + 给 loadstring 的 chunk 名。</summary>
	private sealed record BootBlock(string Name, string Source, string ChunkName);

	private static ValueTask<LuaPatchDocument> CreateArmorRegistryDocumentAsync(
		string gameDataDirectory,
		CancellationToken cancellationToken)
		=> CreateBootDocumentAsync(
			gameDataDirectory,
			[new BootBlock("HD2 Runtime Armor Registry", RuntimeArmorRegistryLuaSource.Load(), "mods/codex/runtime_armor_registry")],
			cancellationToken);

	/// <summary>
	/// 用游戏原始 boot 字节码 + 一组运行时块合成一个 boot 文档。
	///
	/// 引擎只执行**一个** Lua 启动脚本，所以映射和物理都选"独占 boot"时，
	/// 它们必须合并成同一个文档里的两个块——各占一份 patch 会互相顶掉。
	/// </summary>
	private static async ValueTask<LuaPatchDocument> CreateBootDocumentAsync(
		string gameDataDirectory,
		IReadOnlyList<BootBlock> blocks,
		CancellationToken cancellationToken)
	{
		var packageName = $"{LuaAssetKeys.BootArchiveId:x16}";
		var resolver = new HD2ModAdaptation.PatchReconstruction.GameDataPackageResolver(gameDataDirectory);
		var toc = await resolver.GetPackageTocAsync(packageName, cancellationToken).ConfigureAwait(false)
			?? throw new InvalidDataException($"游戏数据中找不到 Lua 启动 Archive：{packageName}");
		var entry = new HD2ModAdaptation.PatchReconstruction.PatchTocScanner()
			.ScanEntries(toc.Data, packageName, toc.UsesSlimEntryOffset)
			.SingleOrDefault(candidate => LuaAssetKeys.IsBoot(candidate.AssetKey))
			?? throw new InvalidDataException("游戏 Lua 启动 Archive 中找不到 boot AssetKey。");
		var boot = await resolver.GetPackageResourceAsync(
			packageName,
			entry.TocDataOffset,
			entry.TocDataSize,
			cancellationToken).ConfigureAwait(false)
			?? throw new InvalidDataException("无法读取游戏原始 Lua 启动资源。");
		if (boot.Length < 9)
			throw new InvalidDataException("游戏原始 Lua 启动资源过短。");
		// The game's own boot resource is the shape we have to reproduce: an
		// eight-byte prefix and then the chunk. Version 1 means the chunk is Lua
		// bytecode, version 2 was the source-text format used before the
		// 2026-10-20 update. Both are accepted here because the payload is embedded
		// as opaque bytes and replayed with load(), which does not care.
		var declaredBootLength = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(0, 4));
		var bootVersion = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(4, 4));
		if (declaredBootLength != boot.Length - 8)
			throw new InvalidDataException("游戏原始 Lua 启动资源的封装长度与实际长度不一致。");
		if (bootVersion is not (LuaResourceFormat.BytecodeVersion
			or LuaResourceFormat.LegacySourceVersion))
			throw new InvalidDataException($"游戏原始 Lua 启动资源的封装版本 {bootVersion} 不受支持。");
		boot = boot.AsSpan(8).ToArray();

		var header = CreateLuaHeaderTemplate();
		var syntheticEntry = new HD2ModAdaptation.PatchReconstruction.PatchTocEntry(
			LuaAssetKeys.BootAssetKey,
			"<manager:runtime-armor-registry>",
			$"{LuaAssetKeys.BootArchiveId:x16}.patch_0");
		var blockList = new List<LuaPatchBlock>(blocks.Count);
		for (var index = 0; index < blocks.Count; index++)
			blockList.Add(BuildBootBlock(index, blocks[index]));
		return new LuaPatchDocument(
			syntheticEntry.SourceFilePath,
			syntheticEntry,
			new HD2ModAdaptation.PatchReconstruction.PatchEntryPayload(syntheticEntry, boot, [], []),
			header,
			boot,
			blockList);
	}

	/// <summary>
	/// 把一个运行时块包成 boot 文档里的一段。每个块独立 pcall：一个功能装不上不能拖累另一个。
	/// <paramref name="index"/> 只用来给局部变量起不重名的名字。
	/// </summary>
	private static LuaPatchBlock BuildBootBlock(int index, BootBlock block)
	{
		var runtimeSource = Encoding.UTF8.GetBytes(block.Source);
		var body = "local __hd2_l" + index + " = rawget(_G, \"loadstring\") or rawget(_G, \"load\")\n"
			+ "local __hd2_c" + index + " = __hd2_l" + index + " and __hd2_l" + index + "(\""
			+ EscapeLuaBytes(runtimeSource)
			+ "\", \"@" + block.ChunkName + "\")\n"
			+ "if __hd2_c" + index + " then pcall(__hd2_c" + index + ") end";
		return new LuaPatchBlock(block.Name, body);
	}

	private static byte[] CreateLuaHeaderTemplate()
	{
		var header = new byte[0xC0];
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0), 0xF0000011);
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 1);
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 1);
		const int typeOffset = 72;
		BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(typeOffset + 8), LuaAssetKeys.LuaTypeId);
		BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(typeOffset + 16), 1);
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(typeOffset + 24), 16);
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(typeOffset + 28), 64);
		const int entryOffset = 104;
		BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(entryOffset), LuaAssetKeys.BootFileId);
		BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(entryOffset + 8), LuaAssetKeys.LuaTypeId);
		BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(entryOffset + 16), 0xB8);
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(entryOffset + 76), 1);
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0xBC), LuaResourceFormat.BytecodeVersion);
		return header;
	}

	private static string EscapeLuaBytes(IEnumerable<byte> bytes)
		=> string.Concat(bytes.Select(value => "\\" + value.ToString(System.Globalization.CultureInfo.InvariantCulture)));

	private async ValueTask<IReadOnlyList<ExternalFileMapping>> BuildExternalMappingsAsync(
		IReadOnlyList<ProfileEntry> entries,
		LibrarySnapshot snapshot,
		IReadOnlyDictionary<ModNodeId, ModContentFacts> contentFacts,
		string modsRootDirectory,
		string gameDataDirectory,
		ICollection<CoreIssue> issues,
		CancellationToken cancellationToken)
	{
		var gameRoot = Directory.GetParent(Path.GetFullPath(gameDataDirectory))?.FullName;
		if (string.IsNullOrWhiteSpace(gameRoot)) return Array.Empty<ExternalFileMapping>();
		var gameBin = Path.Combine(gameRoot, "bin");
		var mappings = new Dictionary<string, ExternalFileMapping>(StringComparer.OrdinalIgnoreCase);

		foreach (var entry in entries)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!snapshot.Nodes.TryGetValue(entry.NodeId, out var node)) continue;
			var sourceRoot = Path.GetFullPath(Path.Combine(modsRootDirectory, node.RelativePath));
			if (!Directory.Exists(sourceRoot)) continue;
			IReadOnlyList<ExternalInstallFileFact> detectedFiles;
			IReadOnlyList<ExternalInstallConflict> conflicts;
			if (contentFacts.TryGetValue(entry.NodeId, out var nodeFacts))
			{
				detectedFiles = nodeFacts.ExternalFiles;
				conflicts = detectedFiles
					.GroupBy(file => file.ConflictKey, StringComparer.OrdinalIgnoreCase)
					.Where(group => group.Count() > 1)
					.Select(group => new ExternalInstallConflict(group.Key, group.ToArray()))
					.ToArray();
			}
			else
			{
				ExternalInstallDetectionResult detection;
				try
				{
					detection = await _externalFiles.DetectAsync(sourceRoot, cancellationToken).ConfigureAwait(false);
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
				{
					issues.Add(new CoreIssue(CoreIssueSeverity.Error, "ExternalInstallScanFailed", exception.Message, sourceRoot, entry.NodeId, exception.ToString()));
					continue;
				}
				detectedFiles = detection.Files;
				conflicts = detection.Conflicts;
			}

			foreach (var conflict in conflicts)
				issues.Add(new CoreIssue(CoreIssueSeverity.Error, "ExternalInstallTargetConflict", $"Multiple files in one Mod target the same GameBin path: {conflict.ConflictKey}", sourceRoot, entry.NodeId));

			foreach (var fact in detectedFiles)
			{
				var sourcePath = Path.GetFullPath(Path.Combine(sourceRoot, fact.SourceRelativePath.Replace('/', Path.DirectorySeparatorChar)));
				var targetPath = Path.GetFullPath(Path.Combine(gameBin, fact.TargetRelativePath.Replace('/', Path.DirectorySeparatorChar)));
				if (!IsWithin(sourceRoot, sourcePath) || !IsWithin(gameBin, targetPath) || !File.Exists(sourcePath))
				{
					issues.Add(new CoreIssue(CoreIssueSeverity.Error, "ExternalInstallPathRejected", "An external install path escaped its allowed root.", sourcePath, entry.NodeId));
					continue;
				}

				var mapping = new ExternalFileMapping(entry.NodeId, sourcePath, targetPath, fact.TargetRoot, fact.TargetRelativePath, fact.Length, fact.Sha256)
				{
					Kind = fact.Kind,
				};
				if (mappings.TryGetValue(fact.ConflictKey, out var previous))
				{
					if (!string.Equals(previous.Sha256, mapping.Sha256, StringComparison.OrdinalIgnoreCase))
						issues.Add(new CoreIssue(CoreIssueSeverity.Error, "ExternalInstallTargetConflict", $"Enabled external files disagree at GameBin path: {fact.TargetRelativePath}", targetPath, entry.NodeId));
					continue;
				}
				mappings[fact.ConflictKey] = mapping;
			}
		}

		return mappings.Values.OrderBy(mapping => mapping.TargetPath, StringComparer.OrdinalIgnoreCase).ToArray();
	}

	private static GeneratedPatchFile CreateGeneratedFile(string path, PatchSidecarKind kind)
	{
		var info = new FileInfo(path);
		return new GeneratedPatchFile(kind, path, info.Name, info.Length, info.LastWriteTimeUtc);
	}

	/// <param name="composedSourceHash">
	/// 这次要写出去的最终源码的哈希。**必须**参与指纹：目录名是缓存键，
	/// 键里不含内容，就会出现"源码变了、发出去的还是旧包"（2026-11-09 实测）。
	/// </param>
	private static string CreateCompositionFingerprint(IEnumerable<LuaDocumentSource> sources, string composedSourceHash)
	{
		var builder = new StringBuilder();
		builder.Append("composed:").Append(composedSourceHash).AppendLine();
		foreach (var source in sources.OrderBy(item => item.ProfileLoadOrder).ThenBy(item => item.Group.NormalizedOrder).ThenBy(item => item.Group.ArchiveHex16, StringComparer.OrdinalIgnoreCase))
		{
			builder.Append(source.NodeId.Value.ToString("N"))
				.Append(':').Append(source.ProfileLoadOrder)
				.Append(':').Append(source.Group.ArchiveHex16)
				.Append(':').Append(source.Group.SourcePatchIndex)
				.Append(':').Append(LuaHashFor(source.Document.BootBytecode))
				// The emitted resource format is part of the identity: a format change
				// must produce a new output directory instead of reusing old bytes.
				.Append(":v").Append(LuaResourceFormat.BytecodeVersion);
			foreach (var block in source.Document.Blocks) builder.Append(':').Append(LuaHashFor(Encoding.UTF8.GetBytes(block.Body)));
			builder.AppendLine();
		}
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
	}

	private static string LuaHashFor(byte[] bytes)
		=> Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

	private static bool IsWithin(string root, string path)
	{
		var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
		return path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsExpectedReadFailure(Exception exception)
		=> exception is IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException;

	private sealed record LuaDocumentSource(ModNodeId NodeId, int ProfileLoadOrder, PatchGroupIdentity Group, LuaPatchDocument Document);
	// 一个"提供 boot 的 patch 组"。管理器只用它做一件事：判断 Lua 启动入口是否被争用。
	// 引擎只执行一个 Lua 启动脚本（settings.ini 的 boot_script），所以这个列表长度
	// 超过 1（或与运行时并存）就意味着有 mod 不会生效，必须告诉用户。
	private sealed record LuaBootProvider(
		ModNodeId NodeId,
		int LoadOrder,
		PatchGroupIdentity Group,
		IndexedPatchFile BaseFile);
	private readonly record struct PatchGroupIdentity(ModNodeId NodeId, string ArchiveHex16, int SourcePatchIndex, int NormalizedOrder);
	private sealed record LuaBuildResult(
		PatchFileIndex PatchIndex,
		IReadOnlyList<GeneratedPatchGroup> GeneratedPatchGroups,
		IReadOnlyList<ExternalFileMapping> ExternalFiles,
		IReadOnlyList<CoreIssue> Issues,
		bool RequiresMaterialization,
		string? CompositionFingerprint,
		int InputCount);
}
