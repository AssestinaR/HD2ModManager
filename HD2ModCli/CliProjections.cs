using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

public static class CliProjections
{
	public const string ProtocolVersion = "1.2";

	public static object CreateCapabilities()
		=> new
		{
			protocolVersion = ProtocolVersion,
			command = "hd2mod",
			mode = "plan-gated-write",
			supports = new[]
			{
				new { name = "version", safety = "read-only", description = "读取 CLI 与协议版本" },
				new { name = "capabilities", safety = "read-only", description = "读取当前可用命令与安全边界" },
				new { name = "status", safety = "read-only", description = "读取管理器、Mod 库与 GameData 路径状态" },
				new { name = "mod list", safety = "read-only", description = "列出已登记 Mod" },
				new { name = "mod show", safety = "read-only", description = "读取指定 Mod、选项关系和激活状态" },
				new { name = "profile list", safety = "read-only", description = "列出配置文件" },
				new { name = "profile show", safety = "read-only", description = "读取逻辑顺序与实际部署展开顺序" },
				new { name = "import inspect", safety = "read-only", description = "扫描待导入文件夹或压缩包，并输出导入前规划，不写入 Mod 库" },
				new { name = "import plan", safety = "explicit-export", description = "仅在给定 --output 后写出带来源/导入树指纹的可审计导入计划，不写入 Mod 库" },
				new { name = "import validate", safety = "read-only", description = "复扫来源并检查目标库、计划指纹和管理器进程；不写入 Mod 库" },
				new { name = "import apply", safety = "explicit-confirm", description = "仅在 validate 通过且传入 --confirm 后导入 Mod 库；不部署、不修改配置文件" },
				new { name = "context export", safety = "explicit-export", description = "仅在给定 --output 后写出 AI 上下文 JSON" },
				new { name = "agent skill list", safety = "read-only", description = "兼容别名：列出可按需导出的能力 Skill" },
				new { name = "agent skill export", safety = "explicit-export", description = "兼容单文件导出：仅在给定 --output 后导出指定能力 Skill" },
				new { name = "agent skills list", safety = "read-only", description = "列出可安装的能力 Skill 目录" },
				new { name = "agent skills export", safety = "explicit-export", description = "仅在给定 --output 后导出一个或全部独立能力 Skill 目录与 SHA-256 清单" },
				new { name = "agent skill", safety = "explicit-export", description = "兼容旧命令：导出轻量 Agent Skill 路由器" },
			},
			notSupportedYet = new[]
			{
				"删除、修改配置、部署、重构 Patch、替换护甲和任何原始 Patch 字节操作。",
			},
			workflow = new[] { "inspect", "plan", "validate", "apply" },
			safetyRules = new[]
			{
				"import apply 只会在计划复核成功、目标库可信、管理器未运行且显式 --confirm 时写入 Mod 库。",
				"import apply 不自动部署、不修改 Profile、不写 Patch 字节，也不会启动昂贵的派生信息分析。",
			},
		};

	public static object CreateStatus(CliWorkspace workspace)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		var libraryPath = string.IsNullOrWhiteSpace(workspace.LibraryRoot)
			? null
			: Path.Combine(workspace.LibraryRoot, "library.json");
		return new
		{
			protocolVersion = ProtocolVersion,
			mode = "read-only",
			manager = new
			{
				root = workspace.ManagerRoot,
				settingsPath = workspace.SettingsPath,
				settingsExists = workspace.SettingsPath is not null && File.Exists(workspace.SettingsPath),
				selectedProfileKey = workspace.Settings?.SelectedProfileKey,
			},
			library = new
			{
				root = workspace.LibraryRoot,
				libraryPath,
				directoryExists = workspace.LibraryRoot is not null && Directory.Exists(workspace.LibraryRoot),
				librarySnapshotExists = libraryPath is not null && File.Exists(libraryPath),
			},
			gameData = new
			{
				root = workspace.GameDataRoot,
				directoryExists = workspace.GameDataRoot is not null && Directory.Exists(workspace.GameDataRoot),
			},
			issues = workspace.Issues,
		};
	}

	public static object CreateImportPlanProjection(ImportPlan plan)
	{
		ArgumentNullException.ThrowIfNull(plan);
		return new
		{
			sourcePath = plan.SourcePath,
			displayName = plan.DisplayName,
			mode = plan.Mode.ToString().ToLowerInvariant(),
			requiresDerivedInformation = plan.RequiresDerivedInformation,
			items = plan.Items.Select(item => new
			{
				sourcePath = item.SourcePath,
				displayName = item.DisplayName,
				role = item.Role.ToString().ToLowerInvariant(),
				relativePath = item.RelativePath,
				patchGroups = item.PatchGroups.Select(group => new
				{
					archiveHex16 = group.ArchiveHex16,
					patchIndex = group.PatchIndex,
				}).ToArray(),
				isTemporarySource = item.IsTemporarySource,
				plannedFiles = item.PlannedFiles ?? Array.Empty<string>(),
				diagnostics = ProjectImportDiagnostics(item.Diagnostics),
				planNodeKey = item.PlanNodeKey,
				parentPlanNodeKeys = item.ParentPlanNodeKeys ?? Array.Empty<string>(),
				optionOrder = item.OptionOrder,
				isLogicalNode = item.IsLogicalNode,
			}).ToArray(),
			diagnostics = ProjectImportDiagnostics(plan.Diagnostics),
		};
	}

	public static object CreateModProjection(ModNode node, CliWorkspace workspace, CliLibraryState state, OptionRelationshipGraph graph)
	{
		ArgumentNullException.ThrowIfNull(node);
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(state);
		ArgumentNullException.ThrowIfNull(graph);
		var id = FormatId(node.Id);
		var optionStates = ReadOptionStates(node.Id, state.OptionActivations);
		var decorationHosts = ReadDecorationHosts(node.Id, state.DecorationActivations);
		return new
		{
			id,
			kind = node.Metadata.Kind.ToString().ToLowerInvariant(),
			name = node.Metadata.Name,
			notes = node.Metadata.Notes,
			relativePath = node.RelativePath,
			fullPath = ResolveNodePath(workspace.LibraryRoot, node.RelativePath),
			patchGroups = node.PatchGroups.Select(group => new
			{
				archiveHex16 = group.ArchiveHex16,
				patchIndex = group.PatchIndex,
			}).ToArray(),
			patchGroupCount = node.PatchGroups.Count,
			isDeployable = node.PatchGroups.Count > 0,
			optionOrder = node.Metadata.OptionOrder,
			hostModIds = NormalizeIds(node.Metadata.HostModGuids),
			directOptionIds = graph.GetDirectOptions(node.Id).Select(option => FormatId(option.Id)).ToArray(),
			parentOptionIds = graph.GetParents(node.Id).Select(FormatId).ToArray(),
			declaredChildIds = node.Children.Select(FormatId).ToArray(),
			optionActivation = node.Metadata.Kind == ModNodeKind.Option ? optionStates : Array.Empty<object>(),
			decorationEnabledHostIds = node.Metadata.Kind == ModNodeKind.Decoration ? decorationHosts : Array.Empty<string>(),
			sourcePackage = new
			{
				guid = node.Metadata.SourcePackageGuid,
				path = node.Metadata.SourcePackagePath,
			},
			helmetsMergedIntoArmor = node.Metadata.HelmetToArmorMerged,
			contentFingerprint = node.ContentFingerprint,
			createdUtc = node.Metadata.CreatedUtc,
			modifiedUtc = node.Metadata.ModifiedUtc,
		};
	}

	public static object CreateProfileProjection(Profile profile, CliWorkspace workspace, CliLibraryState state)
	{
		ArgumentNullException.ThrowIfNull(profile);
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(state);
		var effective = ProfileEffectiveProfileResolver.Resolve(profile, state.Snapshot, state.OptionActivations);
		return new
		{
			id = FormatId(profile.Id),
			name = profile.Name,
			active = state.Snapshot.ActiveProfileId == profile.Id,
			selectedInManager = IsSelectedByManager(profile, workspace.Settings?.SelectedProfileKey),
			createdUtc = profile.CreatedUtc,
			modifiedUtc = profile.ModifiedUtc,
			revision = profile.Revision,
			logicalEntries = profile.Entries
				.OrderBy(entry => entry.LoadOrder)
				.ThenBy(entry => entry.AddedUtc)
				.ThenBy(entry => entry.NodeId.Value)
				.Select((entry, index) => CreateProfileEntryProjection(entry, index, state.Snapshot))
				.ToArray(),
			effectiveDeploymentEntries = effective.Entries
				.Select((entry, index) => CreateProfileEntryProjection(entry, index, state.Snapshot))
				.ToArray(),
		};
	}

	public static object CreateContext(CliWorkspace workspace, CliLibraryState state)
	{
		var graph = OptionRelationshipGraph.Create(state.Snapshot);
		var mods = state.Snapshot.Nodes.Values
			.OrderBy(node => node.Metadata.Kind)
			.ThenBy(node => node.Metadata.Name, StringComparer.OrdinalIgnoreCase)
			.ThenBy(node => node.Id.Value)
			.Select(node => CreateModProjection(node, workspace, state, graph))
			.ToArray();
		var profiles = state.Snapshot.Profiles
			.OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
			.ThenBy(profile => profile.Id.Value)
			.Select(profile => CreateProfileProjection(profile, workspace, state))
			.ToArray();
		return new
		{
			protocolVersion = ProtocolVersion,
			exportedUtc = DateTimeOffset.UtcNow,
			mode = "read-only",
			paths = new
			{
				managerRoot = workspace.ManagerRoot,
				settingsPath = workspace.SettingsPath,
				libraryRoot = workspace.LibraryRoot,
				libraryPath = workspace.LibraryRoot is null ? null : Path.Combine(workspace.LibraryRoot, "library.json"),
				gameDataRoot = workspace.GameDataRoot,
			},
			manager = new
			{
				selectedProfileKey = workspace.Settings?.SelectedProfileKey,
			},
			library = new
			{
				version = state.Snapshot.Version,
				savedUtc = state.Snapshot.SavedUtc,
				modCount = state.Snapshot.Nodes.Count,
				profileCount = state.Snapshot.Profiles.Count,
				activeProfileId = state.Snapshot.ActiveProfileId is { } active ? FormatId(active) : null,
			},
			capabilities = CreateCapabilities(),
			mods,
			profiles,
			orphanedOptionActivationIds = GetOrphanedActivationIds(state.OptionActivations.Options.Keys, state.Snapshot.Nodes),
			orphanedDecorationActivationIds = GetOrphanedActivationIds(state.DecorationActivations.Keys, state.Snapshot.Nodes),
			issues = state.ReadIssues,
		};
	}

	public static ModNode ResolveMod(IReadOnlyDictionary<ModNodeId, ModNode> nodes, string selector)
	{
		if (string.IsNullOrWhiteSpace(selector))
			throw new CliUserException("ModSelectorRequired", "请提供 Mod 的完整 GUID 或唯一 GUID 前缀。");
		var normalized = NormalizeGuidPrefix(selector);
		if (normalized is null)
			throw new CliUserException("ModSelectorInvalid", $"Mod 标识必须是 GUID 或 GUID 前缀：{selector}");
		var matches = nodes.Values
			.Where(node => node.Id.Value.ToString("N").StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
			.OrderBy(node => node.Id.Value)
			.ToArray();
		return matches.Length switch
		{
			0 => throw new CliUserException("ModNotFound", $"未找到 Mod：{selector}"),
			1 => matches[0],
			_ => throw new CliUserException("ModSelectorAmbiguous", $"Mod GUID 前缀不唯一：{selector}。候选：{string.Join(", ", matches.Select(match => FormatId(match.Id)))}"),
		};
	}

	public static Profile ResolveProfile(IReadOnlyList<Profile> profiles, ProfileId? activeProfileId, string? selector)
	{
		if (string.IsNullOrWhiteSpace(selector))
		{
			if (activeProfileId is { } active && profiles.FirstOrDefault(profile => profile.Id == active) is { } activeProfile)
				return activeProfile;
			if (profiles.Count == 1) return profiles[0];
			throw new CliUserException("ProfileSelectorRequired", "当前没有可唯一推断的配置文件；请提供名称、完整 GUID 或唯一 GUID 前缀。");
		}

		var exactNames = profiles.Where(profile => string.Equals(profile.Name, selector, StringComparison.OrdinalIgnoreCase)).ToArray();
		if (exactNames.Length == 1) return exactNames[0];
		if (exactNames.Length > 1)
			throw new CliUserException("ProfileSelectorAmbiguous", $"存在多个同名配置文件：{selector}");

		var normalized = NormalizeGuidPrefix(selector);
		if (normalized is null)
			throw new CliUserException("ProfileNotFound", $"未找到配置文件：{selector}");
		var matches = profiles.Where(profile => profile.Id.Value.ToString("N").StartsWith(normalized, StringComparison.OrdinalIgnoreCase)).ToArray();
		return matches.Length switch
		{
			0 => throw new CliUserException("ProfileNotFound", $"未找到配置文件：{selector}"),
			1 => matches[0],
			_ => throw new CliUserException("ProfileSelectorAmbiguous", $"配置文件 GUID 前缀不唯一：{selector}。"),
		};
	}

	public static string FormatId(ModNodeId id) => id.Value.ToString("D");
	public static string FormatId(ProfileId id) => id.Value.ToString("D");

	private static object CreateProfileEntryProjection(ProfileEntry entry, int effectivePosition, LibrarySnapshot snapshot)
	{
		snapshot.Nodes.TryGetValue(entry.NodeId, out var node);
		return new
		{
			effectivePosition,
			loadOrder = entry.LoadOrder,
			addedUtc = entry.AddedUtc,
			nodeId = FormatId(entry.NodeId),
			nodeName = node?.Metadata.Name,
			nodeKind = node?.Metadata.Kind.ToString().ToLowerInvariant(),
			patchGroupCount = node?.PatchGroups.Count ?? 0,
			isDeployable = node?.PatchGroups.Count > 0,
		};
	}

	private static object[] ReadOptionStates(ModNodeId optionId, OptionActivationSnapshot states)
	{
		var key = optionId.Value.ToString("N");
		if (!states.Options.TryGetValue(key, out var hosts)) return Array.Empty<object>();
		return hosts
			.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
			.Select(pair => (object)new
			{
				hostModId = FormatRawGuid(pair.Key),
				enabled = pair.Value.Enabled,
				effectiveOrder = pair.Value.EffectiveOrder,
			})
			.ToArray();
	}

	private static string[] ReadDecorationHosts(ModNodeId decorationId, IReadOnlyDictionary<string, IReadOnlySet<string>> states)
	{
		var key = decorationId.Value.ToString("N");
		return states.TryGetValue(key, out var hosts)
			? hosts.OrderBy(host => host, StringComparer.OrdinalIgnoreCase).Select(FormatRawGuid).ToArray()
			: Array.Empty<string>();
	}

	private static string[] NormalizeIds(IEnumerable<string>? values)
		=> (values ?? Array.Empty<string>())
			.Where(value => !string.IsNullOrWhiteSpace(value))
			.Select(FormatRawGuid)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();

	private static string[] GetOrphanedActivationIds(IEnumerable<string> activationIds, IReadOnlyDictionary<ModNodeId, ModNode> nodes)
	{
		var known = nodes.Keys.Select(id => id.Value.ToString("N")).ToHashSet(StringComparer.OrdinalIgnoreCase);
		return activationIds
			.Where(id => !known.Contains(NormalizeGuidPrefix(id) ?? id))
			.OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
			.Select(FormatRawGuid)
			.ToArray();
	}

	private static bool IsSelectedByManager(Profile profile, string? selectedKey)
		=> !string.IsNullOrWhiteSpace(selectedKey)
			&& (string.Equals(profile.Name, selectedKey, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(FormatId(profile.Id), selectedKey, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(profile.Id.Value.ToString("N"), NormalizeGuidPrefix(selectedKey), StringComparison.OrdinalIgnoreCase));

	private static string? NormalizeGuidPrefix(string? raw)
	{
		if (string.IsNullOrWhiteSpace(raw)) return null;
		var normalized = raw.Replace("-", string.Empty, StringComparison.Ordinal).Trim();
		return normalized.Length is > 0 and <= 32 && normalized.All(Uri.IsHexDigit) ? normalized : null;
	}

	private static string FormatRawGuid(string raw)
		=> Guid.TryParse(raw, out var value) ? value.ToString("D") : raw;

	private static string? ResolveNodePath(string? libraryRoot, string relativePath)
	{
		if (string.IsNullOrWhiteSpace(libraryRoot)) return null;
		try { return Path.GetFullPath(Path.Combine(libraryRoot, relativePath)); }
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
	}

	private static object[] ProjectImportDiagnostics(IReadOnlyList<ImportPlanDiagnostic>? diagnostics)
		=> (diagnostics ?? Array.Empty<ImportPlanDiagnostic>())
			.Select(diagnostic => (object)new
			{
				code = diagnostic.Code,
				message = diagnostic.Message,
				sourcePath = diagnostic.SourcePath,
				isBlocking = diagnostic.IsBlocking,
			})
			.ToArray();
}
