using System.Reflection;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

public static class CliApplication
{
	public static async Task<int> RunAsync(string[] args)
	{
		CliInvocation? invocation = null;
		try
		{
			invocation = CliArgumentParser.Parse(args);
			return await DispatchAsync(invocation).ConfigureAwait(false);
		}
		catch (CliUserException exception)
		{
			WriteError(invocation?.Options.Json == true || args.Any(argument => string.Equals(argument, "--json", StringComparison.OrdinalIgnoreCase)), exception.Code, exception.Message);
			return 2;
		}
		catch (OperationCanceledException)
		{
			WriteError(invocation?.Options.Json == true || args.Any(argument => string.Equals(argument, "--json", StringComparison.OrdinalIgnoreCase)), "Cancelled", "命令已取消。");
			return 130;
		}
		catch (Exception exception)
		{
			WriteError(invocation?.Options.Json == true || args.Any(argument => string.Equals(argument, "--json", StringComparison.OrdinalIgnoreCase)), "UnhandledError", exception.Message);
			return 1;
		}
	}

	private static async Task<int> DispatchAsync(CliInvocation invocation)
	{
		var command = invocation.Command.Select(value => value.Trim().ToLowerInvariant()).ToArray();
		if (Matches(command, "help"))
		{
			EnsureNoExplicitOutput(invocation.Options);
			Emit(invocation.Options, new { command = "hd2mod", help = HelpText }, () => HelpText);
			return 0;
		}
		if (Matches(command, "version"))
		{
			EnsureNoExplicitOutput(invocation.Options);
			var version = GetVersion();
			Emit(invocation.Options, new { command = "hd2mod", version, protocolVersion = CliProjections.ProtocolVersion }, () => $"hd2mod {version}（协议 {CliProjections.ProtocolVersion}）");
			return 0;
		}
		if (Matches(command, "capabilities"))
		{
			EnsureNoExplicitOutput(invocation.Options);
			Emit(invocation.Options, CliProjections.CreateCapabilities(), () => "当前 CLI 支持安全导入，以及经显式确认的 Mod 元数据与 Option 管理。请使用 capabilities --json 获取完整机器可读能力清单。");
			return 0;
		}
		if (Matches(command, "status"))
		{
			EnsureNoExplicitOutput(invocation.Options);
			var workspace = CliWorkspace.Open(invocation.Options);
			Emit(invocation.Options, CliProjections.CreateStatus(workspace), () => FormatStatus(workspace));
			return 0;
		}
		if (Matches(command, "mod", "list"))
		{
			EnsureNoExplicitOutput(invocation.Options);
			var (workspace, state) = await LoadLibraryAsync(invocation.Options).ConfigureAwait(false);
			var graph = OptionRelationshipGraph.Create(state.Snapshot);
			var mods = state.Snapshot.Nodes.Values
				.OrderBy(node => node.Metadata.Kind)
				.ThenBy(node => node.Metadata.Name, StringComparer.OrdinalIgnoreCase)
				.ThenBy(node => node.Id.Value)
				.Select(node => CliProjections.CreateModProjection(node, workspace, state, graph))
				.ToArray();
			Emit(invocation.Options, new { count = mods.Length, mods, issues = state.ReadIssues }, () => FormatModList(state.Snapshot));
			return 0;
		}
		if (command.Length == 3 && command[0] == "mod" && command[1] == "show")
		{
			EnsureNoExplicitOutput(invocation.Options);
			var (workspace, state) = await LoadLibraryAsync(invocation.Options).ConfigureAwait(false);
			var node = CliProjections.ResolveMod(state.Snapshot.Nodes, invocation.Command[2]);
			var graph = OptionRelationshipGraph.Create(state.Snapshot);
			Emit(invocation.Options, new { mod = CliProjections.CreateModProjection(node, workspace, state, graph), issues = state.ReadIssues }, () => FormatMod(node, state.Snapshot));
			return 0;
		}
		if (command.Length == 4 && command[0] == "mod" && command[1] == "rename")
		{
			EnsureMetadataMutationOptions(invocation.Options, "mod rename");
			var result = await ExecuteMutationAsync(
				invocation.Options,
				session => CliLibraryMutationOperations.RenameAsync(session, invocation.Command[2], invocation.Command[3])).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = true, mutation = CreateNodeMetadataMutationProjection(result) },
				() => result.Changed
					? $"已重命名 Mod：{result.Before.Metadata.Name} → {result.After.Metadata.Name}。未自动部署。"
					: "Mod 名称未变化。未自动部署。");
			return 0;
		}
		if (command.Length == 5 && command[0] == "mod" && command[1] == "notes" && command[2] == "set")
		{
			EnsureMetadataMutationOptions(invocation.Options, "mod notes set");
			var result = await ExecuteMutationAsync(
				invocation.Options,
				session => CliLibraryMutationOperations.SetNotesAsync(session, invocation.Command[3], invocation.Command[4])).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = true, mutation = CreateNodeMetadataMutationProjection(result) },
				() => result.Changed ? "已更新 Mod 备注。未自动部署。" : "Mod 备注未变化。未自动部署。");
			return 0;
		}
		if (command.Length == 4 && command[0] == "mod" && command[1] == "notes" && command[2] == "clear")
		{
			EnsureMetadataMutationOptions(invocation.Options, "mod notes clear");
			var result = await ExecuteMutationAsync(
				invocation.Options,
				session => CliLibraryMutationOperations.SetNotesAsync(session, invocation.Command[3], null)).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = true, mutation = CreateNodeMetadataMutationProjection(result) },
				() => result.Changed ? "已清空 Mod 备注。未自动部署。" : "Mod 备注本来就是空的。未自动部署。");
			return 0;
		}
		if (command.Length == 3 && command[0] == "option" && command[1] is "enable" or "disable")
		{
			EnsureOptionMutationOptions(invocation.Options, $"option {command[1]}", requirePosition: false);
			var enabled = command[1] == "enable";
			var host = RequireHost(invocation.Options);
			var result = await ExecuteMutationAsync(
				invocation.Options,
				session => CliLibraryMutationOperations.SetOptionEnabledAsync(session, invocation.Command[2], host, enabled)).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = true, mutation = CreateOptionRelationshipMutationProjection(result) },
				() => $"已{(enabled ? "启用" : "禁用")}当前主体下的选项。未自动部署。");
			return 0;
		}
		if (command.Length >= 3 && command[0] == "option" && command[1] is "move" or "insert")
		{
			EnsureOptionMutationOptions(invocation.Options, $"option {command[1]}", requirePosition: true);
			var host = RequireHost(invocation.Options);
			var position = invocation.Options.Position!.Value;
			var result = await ExecuteMutationAsync(
				invocation.Options,
				session => CliLibraryMutationOperations.MoveOptionsAsync(session, invocation.Command.Skip(2).ToArray(), host, position)).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = true, mutation = CreateOptionRelationshipMutationProjection(result) },
				() => $"已重排当前主体的直属选项。自动补齐选项状态：{result.InitializedStateCount}。未自动部署。");
			return 0;
		}
		if (Matches(command, "profile", "list"))
		{
			EnsureNoExplicitOutput(invocation.Options);
			var (workspace, state) = await LoadLibraryAsync(invocation.Options).ConfigureAwait(false);
			var profiles = state.Snapshot.Profiles
				.OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
				.ThenBy(profile => profile.Id.Value)
				.Select(profile => CliProjections.CreateProfileProjection(profile, workspace, state))
				.ToArray();
			Emit(invocation.Options, new { count = profiles.Length, profiles, issues = state.ReadIssues }, () => FormatProfileList(state.Snapshot));
			return 0;
		}
		if (command.Length == 3 && command[0] == "import" && command[1] == "inspect")
		{
			EnsureNoExplicitOutput(invocation.Options);
			var inspection = await CliImportPlanArtifacts.InspectAsync(invocation.Command[2]).ConfigureAwait(false);
			Emit(invocation.Options, new { import = CliProjections.CreateImportPlanProjection(inspection.Import), source = inspection.Source, sourceFingerprint = inspection.SourceFingerprint, importFingerprint = inspection.ImportFingerprint }, () => FormatImportPlan(inspection.Import));
			return 0;
		}
		if (command.Length == 3 && command[0] == "import" && command[1] == "plan")
		{
			EnsureNoPlanOrConfirm(invocation.Options, "import plan");
			var output = RequireOutput(invocation.Options, "import plan");
			var inspection = await CliImportPlanArtifacts.InspectAsync(invocation.Command[2]).ConfigureAwait(false);
			var document = CliImportPlanArtifacts.CreateDocument(inspection);
			await CliOutput.WriteExplicitFileAsync(output, CliOutput.ToJson(document), invocation.Options.Overwrite).ConfigureAwait(false);
			Emit(invocation.Options, new { ok = true, output = Path.GetFullPath(output), schema = document.Schema, version = document.Version, sourceFingerprint = document.SourceFingerprint, importFingerprint = document.ImportFingerprint }, () => $"已导出可验证导入计划：{Path.GetFullPath(output)}");
			return 0;
		}
		if (Matches(command, "import", "validate"))
		{
			EnsureValidateOptions(invocation.Options);
			var document = await CliImportPlanArtifacts.ReadDocumentAsync(RequirePlan(invocation.Options)).ConfigureAwait(false);
			var workspace = CliWorkspace.Open(invocation.Options);
			var validation = await CliImportValidator.ValidateAsync(document, workspace).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = validation.IsValid, validation = CreateImportValidationProjection(validation) },
				() => FormatImportValidation(validation));
			return validation.IsValid ? 0 : 2;
		}
		if (Matches(command, "import", "apply"))
		{
			EnsureApplyOptions(invocation.Options);
			if (!invocation.Options.Confirm)
				throw new CliUserException("ImportApplyConfirmationRequired", "import apply 会写入 Mod 库；请在人工确认后显式传入 --confirm。");
			var document = await CliImportPlanArtifacts.ReadDocumentAsync(RequirePlan(invocation.Options)).ConfigureAwait(false);
			var workspace = CliWorkspace.Open(invocation.Options);
			var validation = await CliImportValidator.ValidateAsync(document, workspace).ConfigureAwait(false);
			if (!validation.IsValid)
			{
				Emit(invocation.Options,
					new { ok = false, validation = CreateImportValidationProjection(validation) },
					() => FormatImportValidation(validation));
				return 2;
			}
			var libraryRoot = validation.Target.LibraryRoot;
			if (validation.CurrentInspection is null || string.IsNullOrWhiteSpace(libraryRoot) || string.IsNullOrWhiteSpace(workspace.ManagerRoot))
				throw new CliUserException("ImportApplyValidationIncomplete", "导入验证未提供可执行的来源或目标库。请重新运行 import validate。");

			var paths = new StoragePaths(workspace.ManagerRoot, libraryRoot);
			try
			{
				await using var lease = await CoreServices.CreateLibraryWriteLeaseProvider()
					.AcquireAsync(paths.ModsDirectory)
					.ConfigureAwait(false);
				// A second validation occurs while the cross-process lease is held. It
				// closes the window between initial validate and the actual library write.
				validation = await CliImportValidator.ValidateAsync(document, workspace).ConfigureAwait(false);
				if (!validation.IsValid || validation.CurrentInspection is null)
				{
					Emit(invocation.Options,
						new { ok = false, validation = CreateImportValidationProjection(validation) },
						() => FormatImportValidation(validation));
					return 2;
				}
				var execution = CoreServices.CreateModImportExecutionService(paths);
				var current = validation.CurrentInspection.Source;
				var result = string.Equals(current.Kind, "archive", StringComparison.Ordinal)
					? await execution.ImportArchiveAsync(current.EffectivePath).ConfigureAwait(false)
					: await execution.ImportFolderAsync(current.EffectivePath).ConfigureAwait(false);
				Emit(invocation.Options,
					new { ok = true, applied = CreateImportExecutionProjection(result, libraryRoot) },
					() => FormatImportExecution(result, libraryRoot));
				return 0;
			}
			catch (ModImportPostCommitException exception)
			{
				Emit(invocation.Options,
					new { ok = false, status = "post-commit-incomplete", partial = CreateImportExecutionProjection(exception.PartialResult, libraryRoot), error = new { code = "ImportPostCommitIncomplete", message = exception.InnerException?.Message ?? exception.Message } },
					() => $"Mod 文件已导入，但默认选项状态未完成：{exception.InnerException?.Message ?? exception.Message}");
				return 1;
			}
			catch (LibraryWriteLeaseUnavailableException exception)
			{
				throw new CliUserException("LibraryWriteBusy", $"目标 Mod 库正被另一条 CLI 写入操作占用：{exception.LibraryRoot}。请等待当前操作完成后重试。");
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or DirectoryNotFoundException or FileNotFoundException)
			{
				throw new CliUserException("ImportApplyFailed", $"导入未完成：{exception.Message}");
			}
		}
		if (command.Length is 2 or 3 && command[0] == "profile" && command[1] == "show")
		{
			EnsureNoExplicitOutput(invocation.Options);
			var (workspace, state) = await LoadLibraryAsync(invocation.Options).ConfigureAwait(false);
			var selector = invocation.Command.Count == 3 ? invocation.Command[2] : null;
			var profile = CliProjections.ResolveProfile(state.Snapshot.Profiles, state.Snapshot.ActiveProfileId, selector);
			Emit(invocation.Options, new { profile = CliProjections.CreateProfileProjection(profile, workspace, state), issues = state.ReadIssues }, () => FormatProfile(profile, state));
			return 0;
		}
		if (Matches(command, "context", "export"))
		{
			var output = RequireOutput(invocation.Options, "context export");
			var (workspace, state) = await LoadLibraryAsync(invocation.Options).ConfigureAwait(false);
			var context = CliProjections.CreateContext(workspace, state);
			await CliOutput.WriteExplicitFileAsync(output, CliOutput.ToJson(context), invocation.Options.Overwrite).ConfigureAwait(false);
			Emit(invocation.Options, new { ok = true, output = Path.GetFullPath(output), protocolVersion = CliProjections.ProtocolVersion }, () => $"已导出只读 AI 上下文：{Path.GetFullPath(output)}");
			return 0;
		}
		if (Matches(command, "agent", "skills", "list"))
		{
			EnsureNoExplicitOutput(invocation.Options);
			var skills = CliSkillCatalog.ListCapabilitySkills()
				.Select(skill => new { name = skill.Name, directoryName = skill.DirectoryName, description = skill.Description })
				.ToArray();
			Emit(invocation.Options, new { count = skills.Length, skills }, () => "可安装 Agent Skills：" + Environment.NewLine + string.Join(Environment.NewLine, skills.Select(skill => $"- {skill.name} → {skill.directoryName}：{skill.description}")));
			return 0;
		}
		if (command.Length is 3 or 4 && command[0] == "agent" && command[1] == "skills" && command[2] == "export")
		{
			EnsureNoPlanOrConfirm(invocation.Options, "agent skills export");
			var output = RequireOutput(invocation.Options, "agent skills export");
			var selected = command.Length == 4 ? new[] { command[3] } : null;
			var export = await CliSkillExporter.ExportAsync(output, selected, invocation.Options.Overwrite).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = true, outputDirectory = export.OutputDirectory, manifestPath = export.ManifestPath, skills = export.Skills, protocolVersion = CliProjections.ProtocolVersion },
				() => $"已导出 {export.Skills.Count} 个 Agent Skill：{export.OutputDirectory}");
			return 0;
		}
		if (Matches(command, "agent", "skill", "list"))
		{
			EnsureNoExplicitOutput(invocation.Options);
			var skills = CliSkillCatalog.ListCapabilitySkills()
				.Select(skill => new { name = skill.Name, description = skill.Description })
				.ToArray();
			Emit(invocation.Options, new { count = skills.Length, skills, compatibilityAlias = "agent skills list" }, () => "可用 Agent Skill：" + Environment.NewLine + string.Join(Environment.NewLine, skills.Select(skill => $"- {skill.name}：{skill.description}")));
			return 0;
		}
		if (command.Length == 4 && command[0] == "agent" && command[1] == "skill" && command[2] == "export")
		{
			EnsureNoPlanOrConfirm(invocation.Options, "agent skill export");
			var output = RequireOutput(invocation.Options, "agent skill export");
			var definition = CliSkillCatalog.ResolveCapability(invocation.Command[3]);
			var content = CliSkillCatalog.GetContent(definition.Name);
			await CliOutput.WriteExplicitFileAsync(output, content, invocation.Options.Overwrite).ConfigureAwait(false);
			Emit(invocation.Options, new { ok = true, skill = definition.Name, output = Path.GetFullPath(output), protocolVersion = CliProjections.ProtocolVersion }, () => $"已导出 Agent Skill（{definition.Name}）：{Path.GetFullPath(output)}");
			return 0;
		}
		if (Matches(command, "agent", "skill"))
		{
			EnsureNoPlanOrConfirm(invocation.Options, "agent skill");
			var output = RequireOutput(invocation.Options, "agent skill");
			await CliOutput.WriteExplicitFileAsync(output, AgentSkillTemplate.Create(), invocation.Options.Overwrite).ConfigureAwait(false);
			Emit(invocation.Options, new { ok = true, skill = "cli-router", output = Path.GetFullPath(output), protocolVersion = CliProjections.ProtocolVersion }, () => $"已导出 Agent Skill 路由器：{Path.GetFullPath(output)}");
			return 0;
		}

		throw new CliUserException("UnknownCommand", $"不支持的命令：{string.Join(' ', invocation.Command)}。请运行 hd2mod help。");
	}

	private static async ValueTask<(CliWorkspace Workspace, CliLibraryState State)> LoadLibraryAsync(CliGlobalOptions options)
	{
		var workspace = CliWorkspace.Open(options);
		var state = await workspace.LoadLibraryAsync().ConfigureAwait(false);
		return (workspace, state);
	}

	private static void EnsureNoExplicitOutput(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || !string.IsNullOrWhiteSpace(options.Plan) || options.Confirm
			|| !string.IsNullOrWhiteSpace(options.Host) || options.Position is not null)
			throw new CliUserException("CommandOptionNotSupported", "此命令不接受 --output、--overwrite、--plan、--confirm、--host 或 --position。");
	}

	private static void EnsureNoPlanOrConfirm(CliGlobalOptions options, string command)
	{
		if (!string.IsNullOrWhiteSpace(options.Plan) || options.Confirm || !string.IsNullOrWhiteSpace(options.Host) || options.Position is not null)
			throw new CliUserException("CommandOptionNotSupported", $"{command} 不接受 --plan、--confirm、--host 或 --position。");
	}

	private static void EnsureValidateOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || options.Confirm || !string.IsNullOrWhiteSpace(options.Host) || options.Position is not null)
			throw new CliUserException("CommandOptionNotSupported", "import validate 仅接受 --plan、--manager-root、--library、--game-data 和 --json。");
	}

	private static void EnsureApplyOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || !string.IsNullOrWhiteSpace(options.Host) || options.Position is not null)
			throw new CliUserException("CommandOptionNotSupported", "import apply 不接受 --output、--overwrite、--host 或 --position。");
	}

	private static void EnsureMetadataMutationOptions(CliGlobalOptions options, string command)
	{
		EnsureMutationBoundary(options, command);
		if (!string.IsNullOrWhiteSpace(options.Host) || options.Position is not null)
			throw new CliUserException("CommandOptionNotSupported", $"{command} 不接受 --host 或 --position。");
	}

	private static void EnsureOptionMutationOptions(CliGlobalOptions options, string command, bool requirePosition)
	{
		EnsureMutationBoundary(options, command);
		_ = RequireHost(options);
		if (requirePosition && options.Position is null)
			throw new CliUserException("OptionPositionRequired", $"{command} 必须提供 --position <从 1 开始的插入槽位>。末尾位置为直属选项数量 + 1。");
		if (!requirePosition && options.Position is not null)
			throw new CliUserException("CommandOptionNotSupported", $"{command} 不接受 --position。");
	}

	private static void EnsureMutationBoundary(CliGlobalOptions options, string command)
	{
		if (!options.Confirm)
			throw new CliUserException("MutationConfirmationRequired", $"{command} 会写入 Mod 库；请在人工确认后显式传入 --confirm。");
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || !string.IsNullOrWhiteSpace(options.Plan))
			throw new CliUserException("CommandOptionNotSupported", $"{command} 不接受 --output、--overwrite 或 --plan。");
		if (!string.IsNullOrWhiteSpace(options.GameData))
			throw new CliUserException("CommandOptionNotSupported", $"{command} 不读取或修改 GameData，不接受 --game-data。");
	}

	private static string RequireHost(CliGlobalOptions options)
	{
		if (string.IsNullOrWhiteSpace(options.Host))
			throw new CliUserException("OptionHostRequired", "选项操作必须显式提供 --host <主体 Mod GUID 或唯一前缀>。");
		return options.Host;
	}

	private static async ValueTask<T> ExecuteMutationAsync<T>(
		CliGlobalOptions options,
		Func<CliLibraryWriteSession, ValueTask<T>> mutation)
	{
		ArgumentNullException.ThrowIfNull(mutation);
		try
		{
			var workspace = CliWorkspace.Open(options);
			await using var session = await CliWriteTargetGuard.AcquireSessionAsync(workspace).ConfigureAwait(false);
			return await mutation(session).ConfigureAwait(false);
		}
		catch (CliUserException) { throw; }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or DirectoryNotFoundException or FileNotFoundException)
		{
			throw new CliUserException("LibraryMutationFailed", $"Mod 库修改未完成：{exception.Message}");
		}
	}

	private static string RequireOutput(CliGlobalOptions options, string command)
	{
		if (string.IsNullOrWhiteSpace(options.Output))
			throw new CliUserException("OutputRequired", $"{command} 必须显式提供 --output <文件路径>。");
		return options.Output;
	}

	private static string RequirePlan(CliGlobalOptions options)
	{
		if (string.IsNullOrWhiteSpace(options.Plan))
			throw new CliUserException("ImportPlanRequired", "请通过 --plan <计划文件.json> 提供已导出的导入计划。");
		return options.Plan;
	}

	private static bool Matches(IReadOnlyList<string> actual, params string[] expected)
		=> actual.Count == expected.Length && actual.SequenceEqual(expected, StringComparer.Ordinal);

	private static void Emit(CliGlobalOptions options, object json, Func<string> human)
	{
		if (options.Json) CliOutput.WriteJson(json);
		else Console.WriteLine(human());
	}

	private static void WriteError(bool json, string code, string message)
	{
		if (json) CliOutput.WriteJson(new { ok = false, error = new { code, message } });
		else Console.Error.WriteLine($"错误 [{code}]：{message}");
	}

	private static string GetVersion()
		=> Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
			?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
			?? "dev";

	private static string FormatStatus(CliWorkspace workspace)
		=> string.Join(Environment.NewLine,
		[
			$"管理器目录：{workspace.ManagerRoot ?? "（未提供）"}",
			$"Mod 库：{workspace.LibraryRoot ?? "（未配置）"}",
			$"GameData：{workspace.GameDataRoot ?? "（未配置）"}",
			workspace.Issues.Count == 0 ? "状态：命令行检查就绪。" : $"提示：{string.Join("；", workspace.Issues)}",
		]);

	private static string FormatModList(LibrarySnapshot snapshot)
	{
		var lines = snapshot.Nodes.Values
			.OrderBy(node => node.Metadata.Kind)
			.ThenBy(node => node.Metadata.Name, StringComparer.OrdinalIgnoreCase)
			.Select(node => $"[{node.Metadata.Kind}] {CliProjections.FormatId(node.Id)}  {node.Metadata.Name}  Patch 组：{node.PatchGroups.Count}");
		return $"Mod 共 {snapshot.Nodes.Count} 个：{Environment.NewLine}" + string.Join(Environment.NewLine, lines);
	}

	private static string FormatMod(ModNode node, LibrarySnapshot snapshot)
	{
		var graph = OptionRelationshipGraph.Create(snapshot);
		return string.Join(Environment.NewLine,
		[
			$"{node.Metadata.Name} [{node.Metadata.Kind}]",
			$"ID：{CliProjections.FormatId(node.Id)}",
			$"Patch 组：{node.PatchGroups.Count}",
			$"直接选项：{graph.GetDirectOptions(node.Id).Count}",
			$"父选项：{graph.GetParents(node.Id).Count}",
		]);
	}

	private static string FormatProfileList(LibrarySnapshot snapshot)
	{
		var lines = snapshot.Profiles
			.OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
			.Select(profile => $"{(snapshot.ActiveProfileId == profile.Id ? "* " : string.Empty)}{CliProjections.FormatId(profile.Id)}  {profile.Name}  逻辑条目：{profile.Entries.Count}");
		return $"配置文件共 {snapshot.Profiles.Count} 个：{Environment.NewLine}" + string.Join(Environment.NewLine, lines);
	}

	private static string FormatProfile(Profile profile, CliLibraryState state)
	{
		var effective = ProfileEffectiveProfileResolver.Resolve(profile, state.Snapshot, state.OptionActivations);
		return string.Join(Environment.NewLine,
		[
			$"{profile.Name}",
			$"ID：{CliProjections.FormatId(profile.Id)}",
			$"逻辑条目：{profile.Entries.Count}",
			$"实际部署展开条目：{effective.Entries.Count}",
		]);
	}

	private static string FormatImportPlan(ImportPlan plan)
	{
		var blocking = plan.Diagnostics.Count(diagnostic => diagnostic.IsBlocking)
			+ plan.Items.Sum(item => item.Diagnostics?.Count(diagnostic => diagnostic.IsBlocking) ?? 0);
		return string.Join(Environment.NewLine,
		[
			$"导入前规划：{plan.DisplayName}",
			$"模式：{plan.Mode}",
			$"计划条目：{plan.Items.Count}",
			$"阻断诊断：{blocking}",
		]);
	}

	private static object CreateImportValidationProjection(CliImportValidationResult validation)
		=> new
		{
			isValid = validation.IsValid,
			target = new
			{
				managerRoot = validation.Target.ManagerRoot,
				settingsPath = validation.Target.SettingsPath,
				libraryRoot = validation.Target.LibraryRoot,
				libraryExists = validation.Target.LibraryExists,
				librarySnapshotExists = validation.Target.LibrarySnapshotExists,
				runningManagerProcessIds = validation.Target.RunningManagerProcessIds,
			},
			currentImport = validation.CurrentInspection is null ? null : CliProjections.CreateImportPlanProjection(validation.CurrentInspection.Import),
			issues = validation.Issues.Select(issue => new { code = issue.Code, message = issue.Message, isBlocking = issue.IsBlocking }).ToArray(),
		};

	private static object CreateImportExecutionProjection(ModImportExecutionResult result, string libraryRoot)
		=> new
		{
			libraryRoot,
			sourceDisplayName = result.SourceDisplayName,
			addedNodeIds = result.AddedNodeIds.Select(CliProjections.FormatId).ToArray(),
			changedNodeIds = result.ChangedNodeIds.Select(CliProjections.FormatId).ToArray(),
			affectedNodeIds = result.AffectedNodeIds.Select(CliProjections.FormatId).ToArray(),
			initializedOptionActivationCount = result.InitializedOptionActivationCount,
			hasOptionPathConflicts = result.HasOptionPathConflicts,
			cacheInvalidation = result.CacheInvalidationStatus.ToString().ToLowerInvariant(),
			cacheInvalidationFailedNodeIds = result.CacheInvalidationFailedNodeIds.Select(CliProjections.FormatId).ToArray(),
			derivedInformation = "not-started",
			deployment = "not-performed",
		};

	private static object CreateNodeMetadataMutationProjection(CliNodeMetadataMutationResult result)
		=> new
		{
			changed = result.Changed,
			modId = CliProjections.FormatId(result.After.Id),
			before = new
			{
				name = result.Before.Metadata.Name,
				notes = result.Before.Metadata.Notes,
				modifiedUtc = result.Before.Metadata.ModifiedUtc,
			},
			after = new
			{
				name = result.After.Metadata.Name,
				notes = result.After.Metadata.Notes,
				modifiedUtc = result.After.Metadata.ModifiedUtc,
			},
			deployment = "not-performed",
		};

	private static object CreateOptionRelationshipMutationProjection(CliOptionRelationshipMutationResult result)
		=> new
		{
			changed = result.Mutation.Changed,
			hostModId = CliProjections.FormatId(result.Mutation.Host.HostId),
			initializedOptionActivationCount = result.InitializedStateCount,
			directOptions = result.Mutation.Host.DirectOptions.Select(state => new
			{
				optionId = CliProjections.FormatId(state.OptionId),
				enabled = state.Enabled,
				hasExplicitActivationState = state.HasExplicitActivationState,
				effectiveOrder = state.EffectiveOrder,
				position = state.Position + 1,
			}).ToArray(),
			deployment = "not-performed",
		};

	private static string FormatImportValidation(CliImportValidationResult validation)
	{
		var status = validation.IsValid ? "导入验证通过。" : "导入验证未通过。";
		var issues = validation.Issues.Count == 0
			? "无阻断项。"
			: string.Join(Environment.NewLine, validation.Issues.Select(issue => $"- [{issue.Code}] {issue.Message}"));
		return $"{status}{Environment.NewLine}目标 Mod 库：{validation.Target.LibraryRoot ?? "（未确定）"}{Environment.NewLine}{issues}";
	}

	private static string FormatImportExecution(ModImportExecutionResult result, string libraryRoot)
		=> $"已导入：{result.SourceDisplayName}{Environment.NewLine}目标 Mod 库：{libraryRoot}{Environment.NewLine}新增 Mod：{result.AddedNodeIds.Count}；更新 Mod：{result.ChangedNodeIds.Count}；初始化选项状态：{result.InitializedOptionActivationCount}。"
			+ (result.HasOptionPathConflicts
				? $"{Environment.NewLine}检测到选项目录冲突：仅共享 Patch 内容未默认选择；其余选项已按通常规则初始化。"
				: string.Empty)
			+ (result.CacheInvalidationStatus == ImportCacheInvalidationStatus.Completed
				? string.Empty
				: $"{Environment.NewLine}缓存失效状态：{result.CacheInvalidationStatus}；失败节点：{result.CacheInvalidationFailedNodeIds.Count}。可在下次管理器同步时重试。")
			+ $"{Environment.NewLine}未自动部署。";

	private const string HelpText = """
HD2 Mod Manager 命令行

用法：
  hd2mod capabilities --json
  hd2mod status --manager-root <管理器目录> --json
  hd2mod mod list --library <Mod库目录> --json
  hd2mod mod show <GUID或唯一前缀> --library <Mod库目录> --json
	  hd2mod mod rename <Mod GUID或唯一前缀> <新名称> --manager-root <管理器目录> --confirm --json
	  hd2mod mod notes set <Mod GUID或唯一前缀> <备注> --manager-root <管理器目录> --confirm --json
	  hd2mod mod notes clear <Mod GUID或唯一前缀> --manager-root <管理器目录> --confirm --json
	  hd2mod option enable <Option GUID或唯一前缀> --host <主体 GUID或唯一前缀> --manager-root <管理器目录> --confirm --json
	  hd2mod option disable <Option GUID或唯一前缀> --host <主体 GUID或唯一前缀> --manager-root <管理器目录> --confirm --json
	  hd2mod option move <Option GUID或唯一前缀> [...] --host <主体 GUID或唯一前缀> --position <插入槽位> --manager-root <管理器目录> --confirm --json
	  hd2mod option insert <Option GUID或唯一前缀> [...] --host <主体 GUID或唯一前缀> --position <插入槽位> --manager-root <管理器目录> --confirm --json
  hd2mod profile list --library <Mod库目录> --json
	  hd2mod profile show [名称或GUID前缀] --library <Mod库目录> --json
	  hd2mod import inspect <文件夹、压缩包或文件> --json
	  hd2mod import plan <文件夹、压缩包或文件> --output import-plan.json
	  hd2mod import validate --plan import-plan.json --manager-root <管理器目录> --json
	  hd2mod import apply --plan import-plan.json --manager-root <管理器目录> --confirm --json
	  hd2mod context export --library <Mod库目录> --output context.json
	  hd2mod agent skills list --json
	  hd2mod agent skills export [能力名称] --output <Skill父目录>
	  hd2mod agent skill export <能力名称> --output SKILL.md
	  hd2mod agent skill --output SKILL.md

全局参数：
  --manager-root <目录>   包含管理器 settings.json 的目录。
  --library <目录>        显式指定 Mod 库；优先级高于 settings.json。
  --game-data <目录>      只读上下文中的 GameData 覆盖路径。
  --json                  输出机器可读 JSON。
	  --output <文件或目录>   import plan / context export / Agent Skill 导出使用。
	  --plan <文件>           import validate / apply 所需的导入计划。
	  --overwrite             显式允许覆盖已有导出文件或已命名 Skill 目录中的 SKILL.md。
	  --host <GUID或前缀>      Option 操作的明确主体；不支持隐式或全主体修改。
	  --position <正整数>      Option 排序的从 1 开始插入槽位；末尾为直属选项数 + 1。
	  --confirm               所有写入命令的明确确认。

安全边界：写入命令均要求桌面端关闭、严格库读取、跨进程库租约和显式 --confirm；不会自动部署、修改配置、重构或直接修改 Patch 内容。
""";
}
