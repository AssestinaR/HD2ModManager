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
		if (command.Length >= 4 && command[0] == "mod" && command[1] == "delete" && command[2] == "preview")
		{
			EnsureDeletionPreviewOptions(invocation.Options);
			var (workspace, state) = await LoadLibraryAsync(invocation.Options).ConfigureAwait(false);
			var plan = CliLibraryMutationOperations.CreateDeletionPlan(
				state,
				invocation.Command.Skip(3).ToArray(),
				invocation.Options.WithUnsharedOptions ? SafeModDeletionScope.WithUnsharedOptions : SafeModDeletionScope.Selected);
			Emit(invocation.Options,
				new { ok = !plan.IsBlocked, deletion = CreateDeletionPlanProjection(plan) },
				() => FormatDeletionPlan(plan));
			return plan.IsBlocked ? 2 : 0;
		}
		if (command.Length >= 3 && command[0] == "mod" && command[1] == "delete")
		{
			EnsureDeletionMutationOptions(invocation.Options);
			var result = await ExecuteMutationAsync(
				invocation.Options,
				session => CliLibraryMutationOperations.DeleteAsync(
					session,
					invocation.Command.Skip(2).ToArray(),
					invocation.Options.WithUnsharedOptions ? SafeModDeletionScope.WithUnsharedOptions : SafeModDeletionScope.Selected)).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = result.Status == CliModDeletionStatus.Completed, deletion = CreateDeletionExecutionProjection(result) },
				() => FormatDeletionExecution(result));
			return result.Status == CliModDeletionStatus.Completed ? 0 : 2;
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
		if (command.Length == 3 && command[0] == "profile" && command[1] == "create")
		{
			EnsureProfileMutationOptions(invocation.Options, "profile create", allowSetActive: true);
			var result = await ExecuteMutationAsync(
				invocation.Options,
				session => CliLibraryMutationOperations.CreateProfileAsync(session, invocation.Command[2], invocation.Options.SetActive)).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = true, mutation = CreateProfileMutationProjection(result) },
				() => result.Mutation.Changed ? "已创建配置文件。未自动部署。" : "配置文件未变化。未自动部署。");
			return 0;
		}
		if (command.Length == 3 && command[0] == "profile" && command[1] == "delete")
		{
			EnsureProfileMutationOptions(invocation.Options, "profile delete");
			var result = await ExecuteMutationAsync(
				invocation.Options,
				session => CliLibraryMutationOperations.DeleteProfileAsync(session, invocation.Command[2])).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = true, mutation = CreateProfileMutationProjection(result) },
				() => "已删除配置文件。未自动部署；如它此前处于部署状态，请显式执行后续部署操作。");
			return 0;
		}
		if (command.Length == 4 && command[0] == "profile" && command[1] == "rename")
		{
			EnsureProfileMutationOptions(invocation.Options, "profile rename");
			var result = await ExecuteMutationAsync(
				invocation.Options,
				session => CliLibraryMutationOperations.RenameProfileAsync(session, invocation.Command[2], invocation.Command[3])).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = true, mutation = CreateProfileMutationProjection(result) },
				() => result.Mutation.Changed ? "已重命名配置文件。未自动部署。" : "配置文件名称未变化。未自动部署。");
			return 0;
		}
		if (command.Length == 3 && command[0] == "profile" && command[1] == "set-active")
		{
			EnsureProfileMutationOptions(invocation.Options, "profile set-active");
			var result = await ExecuteMutationAsync(
				invocation.Options,
				session => CliLibraryMutationOperations.SetActiveProfileAsync(session, invocation.Command[2])).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = true, mutation = CreateProfileMutationProjection(result) },
				() => result.Mutation.Changed ? "已设为活动配置。未自动部署。" : "该配置本来就是活动配置。未自动部署。");
			return 0;
		}
		if (Matches(command, "profile", "clear-active"))
		{
			EnsureProfileMutationOptions(invocation.Options, "profile clear-active");
			var result = await ExecuteMutationAsync(
				invocation.Options,
				session => CliLibraryMutationOperations.ClearActiveProfileAsync(session)).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = true, mutation = CreateProfileMutationProjection(result) },
				() => result.Mutation.Changed ? "已清除活动配置。未自动部署。" : "当前本来没有活动配置。未自动部署。");
			return 0;
		}
		if (command.Length >= 4 && command[0] == "profile" && command[1] is "add" or "remove" or "insert" or "move" or "reorder")
		{
			var operation = command[1];
			var requiresPosition = operation is "insert" or "move";
			EnsureProfileMutationOptions(invocation.Options, $"profile {operation}", allowPosition: requiresPosition);
			if (requiresPosition && invocation.Options.Position is null)
				throw new CliUserException("ProfilePositionRequired", $"profile {operation} 必须提供 --position <从 1 开始的插入槽位>。末尾位置为当前逻辑条目数 + 1。");
			if (!requiresPosition && invocation.Options.Position is not null)
				throw new CliUserException("CommandOptionNotSupported", $"profile {operation} 不接受 --position。");

			var profileSelector = invocation.Command[2];
			var nodeSelectors = invocation.Command.Skip(3).ToArray();
			var result = await ExecuteMutationAsync(
				invocation.Options,
				session => operation switch
				{
					"add" => CliLibraryMutationOperations.AddProfileEntriesAsync(session, profileSelector, nodeSelectors),
					"remove" => CliLibraryMutationOperations.RemoveProfileEntriesAsync(session, profileSelector, nodeSelectors),
					"insert" => CliLibraryMutationOperations.InsertProfileEntriesAsync(session, profileSelector, nodeSelectors, invocation.Options.Position!.Value),
					"move" => CliLibraryMutationOperations.MoveProfileEntriesAsync(session, profileSelector, nodeSelectors, invocation.Options.Position!.Value),
					"reorder" => CliLibraryMutationOperations.ReorderProfileEntriesAsync(session, profileSelector, nodeSelectors),
					_ => throw new InvalidOperationException($"Unexpected profile operation: {operation}"),
				}).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = true, mutation = CreateProfileMutationProjection(result) },
				() => result.Mutation.Changed ? "已更新配置文件逻辑顺序。未自动部署。" : "配置文件条目未变化。未自动部署。");
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
		if (Matches(command, "deploy", "deactivate", "plan"))
		{
			EnsureDeactivationPlanOptions(invocation.Options);
			var output = RequireOutput(invocation.Options, "deploy deactivate plan");
			var build = await CliDeactivationPlanArtifacts.BuildAsync(CliWorkspace.Open(invocation.Options)).ConfigureAwait(false);
			await CliOutput.WriteExplicitFileAsync(output, CliOutput.ToJson(build.Document), invocation.Options.Overwrite).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = build.Document.IsReady, output = Path.GetFullPath(output), deactivation = CreateDeactivationPlanProjection(build.Document) },
				() => FormatDeactivationPlan(build.Document, Path.GetFullPath(output)));
			return build.Document.IsReady ? 0 : 2;
		}
		if (Matches(command, "deploy", "deactivate", "validate"))
		{
			EnsureDeactivationValidateOptions(invocation.Options);
			var document = await CliDeactivationPlanArtifacts.ReadDocumentAsync(RequireDeactivationPlan(invocation.Options)).ConfigureAwait(false);
			var validation = await CliDeactivationPlanArtifacts.ValidateAsync(document, CliWorkspace.Open(invocation.Options), includeProcessCheck: true).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = validation.IsValid, validation = CreateDeactivationValidationProjection(validation) },
				() => FormatDeactivationValidation(validation));
			return validation.IsValid ? 0 : 2;
		}
		if (Matches(command, "deploy", "deactivate", "apply"))
		{
			EnsureDeactivationApplyOptions(invocation.Options);
			if (!invocation.Options.Confirm)
				throw new CliUserException("DeactivationApplyConfirmationRequired", "deploy deactivate apply 会删除已证明归属的 GameData Patch；请显式传入 --confirm。 ");
			var document = await CliDeactivationPlanArtifacts.ReadDocumentAsync(RequireDeactivationPlan(invocation.Options)).ConfigureAwait(false);
			var execution = await CliDeactivationExecutor.ApplyAsync(document, CliWorkspace.Open(invocation.Options)).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = execution.Success, execution = CreateDeploymentExecutionProjection(execution) },
				() => FormatDeploymentExecution(execution));
			return execution.Success ? 0 : 2;
		}
		if (command.Length is 2 or 3 && command[0] == "deploy" && command[1] == "plan")
		{
			EnsureDeploymentPlanOptions(invocation.Options);
			var output = RequireOutput(invocation.Options, "deploy plan");
			var workspace = CliWorkspace.Open(invocation.Options);
			var selector = invocation.Command.Count == 3 ? invocation.Command[2] : null;
			var build = await CliDeploymentPlanArtifacts.BuildAsync(workspace, selector).ConfigureAwait(false);
			await CliOutput.WriteExplicitFileAsync(output, CliOutput.ToJson(build.Document), invocation.Options.Overwrite).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = build.Document.IsReady, output = Path.GetFullPath(output), deployment = CreateDeploymentPlanProjection(build.Document) },
				() => FormatDeploymentPlan(build.Document, Path.GetFullPath(output)));
			return build.Document.IsReady ? 0 : 2;
		}
		if (Matches(command, "deploy", "validate"))
		{
			EnsureDeploymentValidateOptions(invocation.Options);
			var document = await CliDeploymentPlanArtifacts.ReadDocumentAsync(RequireDeploymentPlan(invocation.Options)).ConfigureAwait(false);
			var validation = await CliDeploymentPlanArtifacts.ValidateAsync(document, CliWorkspace.Open(invocation.Options), includeProcessCheck: true).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = validation.IsValid, validation = CreateDeploymentValidationProjection(validation) },
				() => FormatDeploymentValidation(validation));
			return validation.IsValid ? 0 : 2;
		}
		if (Matches(command, "deploy", "apply"))
		{
			EnsureDeploymentApplyOptions(invocation.Options);
			if (!invocation.Options.Confirm)
				throw new CliUserException("DeploymentApplyConfirmationRequired", "deploy apply 会写入 GameData；请在人工确认后显式传入 --confirm。 ");
			var document = await CliDeploymentPlanArtifacts.ReadDocumentAsync(RequireDeploymentPlan(invocation.Options)).ConfigureAwait(false);
			var execution = await CliDeploymentExecutor.ApplyAsync(document, CliWorkspace.Open(invocation.Options)).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = execution.Success, execution = CreateDeploymentExecutionProjection(execution) },
				() => FormatDeploymentExecution(execution));
			return execution.Success ? 0 : 2;
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
			|| !string.IsNullOrWhiteSpace(options.Host) || options.Position is not null || options.SetActive || options.WithUnsharedOptions)
			throw new CliUserException("CommandOptionNotSupported", "此命令不接受写入确认、导出、宿主、排序或删除范围参数。");
	}

	private static void EnsureNoPlanOrConfirm(CliGlobalOptions options, string command)
	{
		if (!string.IsNullOrWhiteSpace(options.Plan) || options.Confirm || !string.IsNullOrWhiteSpace(options.Host) || options.Position is not null || options.SetActive || options.WithUnsharedOptions)
			throw new CliUserException("CommandOptionNotSupported", $"{command} 不接受 --plan、--confirm、--host、--position、--set-active 或 --with-unshared-options。");
	}

	private static void EnsureValidateOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || options.Confirm || !string.IsNullOrWhiteSpace(options.Host) || options.Position is not null || options.SetActive || options.WithUnsharedOptions)
			throw new CliUserException("CommandOptionNotSupported", "import validate 仅接受 --plan、--manager-root、--library、--game-data 和 --json。");
	}

	private static void EnsureApplyOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || !string.IsNullOrWhiteSpace(options.Host) || options.Position is not null || options.SetActive || options.WithUnsharedOptions)
			throw new CliUserException("CommandOptionNotSupported", "import apply 不接受 --output、--overwrite、--host 或 --position。");
	}

	private static void EnsureDeploymentPlanOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Plan) || options.Confirm || !string.IsNullOrWhiteSpace(options.Host)
			|| options.Position is not null || options.SetActive || options.WithUnsharedOptions)
		{
			throw new CliUserException("CommandOptionNotSupported", "deploy plan 仅接受配置标识、--manager-root、--library、--game-data、--output、--overwrite 与 --json。");
		}
	}

	private static void EnsureDeploymentValidateOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || options.Confirm || !string.IsNullOrWhiteSpace(options.Host)
			|| options.Position is not null || options.SetActive || options.WithUnsharedOptions)
		{
			throw new CliUserException("CommandOptionNotSupported", "deploy validate 仅接受 --plan、--manager-root、--library、--game-data 与 --json。");
		}
	}

	private static void EnsureDeploymentApplyOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || !string.IsNullOrWhiteSpace(options.Host)
			|| options.Position is not null || options.SetActive || options.WithUnsharedOptions)
		{
			throw new CliUserException("CommandOptionNotSupported", "deploy apply 仅接受 --plan、--manager-root、--library、--game-data、--confirm 与 --json。");
		}
	}

	private static void EnsureDeactivationPlanOptions(CliGlobalOptions options)
		=> EnsureDeploymentPlanOptions(options);

	private static void EnsureDeactivationValidateOptions(CliGlobalOptions options)
		=> EnsureDeploymentValidateOptions(options);

	private static void EnsureDeactivationApplyOptions(CliGlobalOptions options)
		=> EnsureDeploymentApplyOptions(options);

	private static void EnsureMetadataMutationOptions(CliGlobalOptions options, string command)
	{
		EnsureMutationBoundary(options, command);
		if (!string.IsNullOrWhiteSpace(options.Host) || options.Position is not null || options.SetActive || options.WithUnsharedOptions)
			throw new CliUserException("CommandOptionNotSupported", $"{command} 不接受 --host、--position、--set-active 或 --with-unshared-options。");
	}

	private static void EnsureOptionMutationOptions(CliGlobalOptions options, string command, bool requirePosition)
	{
		EnsureMutationBoundary(options, command);
		_ = RequireHost(options);
		if (requirePosition && options.Position is null)
			throw new CliUserException("OptionPositionRequired", $"{command} 必须提供 --position <从 1 开始的插入槽位>。末尾位置为直属选项数量 + 1。");
		if (!requirePosition && options.Position is not null)
			throw new CliUserException("CommandOptionNotSupported", $"{command} 不接受 --position。");
		if (options.SetActive || options.WithUnsharedOptions)
			throw new CliUserException("CommandOptionNotSupported", $"{command} 不接受 --set-active 或 --with-unshared-options。");
	}

	private static void EnsureProfileMutationOptions(
		CliGlobalOptions options,
		string command,
		bool allowPosition = false,
		bool allowSetActive = false)
	{
		EnsureMutationBoundary(options, command);
		if (!string.IsNullOrWhiteSpace(options.Host))
			throw new CliUserException("CommandOptionNotSupported", $"{command} 不接受 --host。");
		if (!allowPosition && options.Position is not null)
			throw new CliUserException("CommandOptionNotSupported", $"{command} 不接受 --position。");
		if (!allowSetActive && options.SetActive)
			throw new CliUserException("CommandOptionNotSupported", $"{command} 不接受 --set-active。");
		if (options.WithUnsharedOptions)
			throw new CliUserException("CommandOptionNotSupported", $"{command} 不接受 --with-unshared-options。");
	}

	private static void EnsureDeletionPreviewOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || !string.IsNullOrWhiteSpace(options.Plan)
			|| options.Confirm || !string.IsNullOrWhiteSpace(options.Host) || options.Position is not null || options.SetActive
			|| !string.IsNullOrWhiteSpace(options.GameData))
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"mod delete preview 仅接受 Mod 标识、--library/--manager-root、--with-unshared-options 与 --json。");
		}
	}

	private static void EnsureDeletionMutationOptions(CliGlobalOptions options)
	{
		EnsureMutationBoundary(options, "mod delete");
		if (!string.IsNullOrWhiteSpace(options.Host) || options.Position is not null || options.SetActive)
			throw new CliUserException("CommandOptionNotSupported", "mod delete 不接受 --host、--position 或 --set-active。");
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

	private static string RequireDeploymentPlan(CliGlobalOptions options)
	{
		if (string.IsNullOrWhiteSpace(options.Plan))
			throw new CliUserException("DeploymentPlanRequired", "请通过 --plan <部署计划.json> 提供已导出的部署计划。");
		return options.Plan;
	}

	private static string RequireDeactivationPlan(CliGlobalOptions options)
	{
		if (string.IsNullOrWhiteSpace(options.Plan))
			throw new CliUserException("DeactivationPlanRequired", "请通过 --plan <停用计划.json> 提供已导出的停用计划。");
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

	private static object CreateDeploymentPlanProjection(CliDeploymentPlanDocument document)
		=> new
		{
			document.Schema,
			document.Version,
			document.CreatedUtc,
			document.PlanId,
			ready = document.IsReady,
			target = document.Target,
			profile = document.Profile,
			ownership = document.Ownership,
			inputFingerprint = document.InputFingerprint,
			patches = document.Patches,
			externalFiles = document.ExternalFiles,
			lua = document.Lua,
			diagnostics = document.Diagnostics,
		};

	private static object CreateDeploymentValidationProjection(CliDeploymentValidationResult validation)
		=> new
		{
			isValid = validation.IsValid,
			requestedPlanId = validation.Document.PlanId,
			recomputedPlanId = validation.Current?.Document.PlanId,
			current = validation.Current is null ? null : CreateDeploymentPlanProjection(validation.Current.Document),
			diagnostics = validation.Diagnostics,
		};

	private static object CreateDeploymentExecutionProjection(CliDeploymentExecutionResult execution)
		=> new
		{
			execution.Success,
			execution.DeploymentMethod,
			operations = execution.ApplyResult?.Operations.Select(operation => new
			{
				kind = operation.Operation.Kind.ToString().ToLowerInvariant(),
				operation.Operation.TargetPath,
				operation.Success,
				method = operation.Method?.ToString().ToLowerInvariant(),
				operation.ErrorCode,
				operation.Message,
			}).ToArray() ?? Array.Empty<object>(),
			state = execution.ApplyResult?.StateReport,
			diagnostics = execution.Diagnostics,
		};

	private static object CreateDeactivationPlanProjection(CliDeactivationPlanDocument document)
		=> new
		{
			document.Schema,
			document.Version,
			document.CreatedUtc,
			document.PlanId,
			ready = document.IsReady,
			target = document.Target,
			ownership = document.Ownership,
			patches = document.Patches,
			externalFiles = document.ExternalFiles,
			diagnostics = document.Diagnostics,
		};

	private static object CreateDeactivationValidationProjection(CliDeactivationValidationResult validation)
		=> new
		{
			isValid = validation.IsValid,
			requestedPlanId = validation.Document.PlanId,
			recomputedPlanId = validation.Current?.Document.PlanId,
			current = validation.Current is null ? null : CreateDeactivationPlanProjection(validation.Current.Document),
			diagnostics = validation.Diagnostics,
		};

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

	private static object CreateProfileMutationProjection(CliProfileMutationExecutionResult result)
	{
		var profileId = result.Mutation.ProfileId;
		var profile = profileId is { } id
			? result.After.Profiles.FirstOrDefault(item => item.Id == id)
			: null;
		return new
		{
			operation = result.Mutation.Operation.ToString(),
			changed = result.Mutation.Changed,
			profileId = profileId is { } value ? CliProjections.FormatId(value) : null,
			profile = profile is null
				? null
				: new
				{
					id = CliProjections.FormatId(profile.Id),
					name = profile.Name,
					logicalEntryCount = profile.Entries.Count,
					revision = profile.Revision,
				},
			affectedModIds = result.Mutation.EntryIds.Select(CliProjections.FormatId).ToArray(),
			activeProfileId = result.After.ActiveProfileId is { } active ? CliProjections.FormatId(active) : null,
			deployment = "not-performed",
		};
	}

	private static object CreateDeletionPlanProjection(SafeModDeletionPlan plan)
		=> new
		{
			scope = plan.Scope.ToString(),
			isBlocked = plan.IsBlocked,
			requestedNodes = plan.RequestedNodes.Select(item => new
			{
				id = CliProjections.FormatId(item.NodeId),
				item.Exists,
				item.WillRemove,
				item.RequestCount,
			}).ToArray(),
			libraryNodeRemovals = plan.LibraryNodeRemovals.Select(item => new
			{
				id = CliProjections.FormatId(item.NodeId),
				item.Name,
				kind = item.Kind.ToString().ToLowerInvariant(),
				item.RelativePath,
				reason = item.Reason.ToString(),
			}).ToArray(),
			retainedSharedOptions = plan.PreservedSharedOptions.Select(item => new
			{
				optionId = CliProjections.FormatId(item.OptionId),
				item.Name,
				removedHostIds = item.RemovedHostIds.Select(CliProjections.FormatId).ToArray(),
				survivingHostIds = item.SurvivingHostIds.Select(CliProjections.FormatId).ToArray(),
			}).ToArray(),
			profileEntriesToClean = plan.ProfileEntriesToClean.Select(item => new
			{
				profileId = CliProjections.FormatId(item.ProfileId),
				item.ProfileName,
				nodeId = CliProjections.FormatId(item.NodeId),
				item.LoadOrder,
			}).ToArray(),
			physicalPaths = plan.PhysicalRelativePaths.Select(item => new
			{
				item.RelativePath,
				nodeIds = item.NodeIds.Select(CliProjections.FormatId).ToArray(),
				item.IsSafeRelativePath,
			}).ToArray(),
			blockers = plan.Blockers.Select(item => new
			{
				item.Code,
				severity = item.Severity.ToString().ToLowerInvariant(),
				item.Message,
				relatedNodeIds = item.RelatedNodeIds.Select(CliProjections.FormatId).ToArray(),
				item.RelativePaths,
			}).ToArray(),
		};

	private static object CreateDeletionExecutionProjection(CliModDeletionExecutionResult result)
		=> new
		{
			status = result.Status.ToString().ToLowerInvariant(),
			plan = CreateDeletionPlanProjection(result.Plan),
			appliedPlan = result.AppliedPlan is null ? null : CreateDeletionPlanProjection(result.AppliedPlan),
			removedNodeIds = result.RemovedNodeIds.Select(CliProjections.FormatId).ToArray(),
			failedNodeIds = result.FailedNodeIds.Select(CliProjections.FormatId).ToArray(),
			skippedNodeIds = result.SkippedNodeIds.Select(CliProjections.FormatId).ToArray(),
			storageResults = result.StorageResults.Select(item => new
			{
				item.RelativePath,
				state = item.State.ToString().ToLowerInvariant(),
				item.Detail,
				canRemoveLibraryNode = item.CanRemoveLibraryNode,
			}).ToArray(),
			optionActivationCleanup = result.OptionActivationCleanup is null ? null : new
			{
				result.OptionActivationCleanup.RemovedOptionRecordCount,
				result.OptionActivationCleanup.RemovedHostRelationshipCount,
				result.OptionActivationCleanup.Changed,
			},
			decorationActivationCleanup = result.DecorationActivationCleanup is null ? null : new
			{
				result.DecorationActivationCleanup.RemovedDecorationCount,
				result.DecorationActivationCleanup.RemovedHostReferenceCount,
				result.DecorationActivationCleanup.Changed,
			},
			derivedInformation = result.Status == CliModDeletionStatus.Completed ? "requires-manager-refresh" : "not-started",
			deployment = "not-performed",
			result.PostCommitError,
		};

	private static string FormatImportValidation(CliImportValidationResult validation)
	{
		var status = validation.IsValid ? "导入验证通过。" : "导入验证未通过。";
		var issues = validation.Issues.Count == 0
			? "无阻断项。"
			: string.Join(Environment.NewLine, validation.Issues.Select(issue => $"- [{issue.Code}] {issue.Message}"));
		return $"{status}{Environment.NewLine}目标 Mod 库：{validation.Target.LibraryRoot ?? "（未确定）"}{Environment.NewLine}{issues}";
	}

	private static string FormatDeploymentPlan(CliDeploymentPlanDocument document, string outputPath)
	{
		var blocking = document.Diagnostics.Count(diagnostic => diagnostic.Blocking);
		return string.Join(Environment.NewLine,
		[
			$"已导出部署计划：{outputPath}",
			$"配置：{document.Profile.Name}；有效条目：{document.Profile.EffectiveEntryIds.Count}",
			$"Patch：{document.Patches.Count}；外部文件：{document.ExternalFiles.Count}；Lua 合成：{(document.Lua.RequiresMaterialization ? "需要" : "不需要")}",
			$"当前归属：{document.Ownership.Status}；阻断诊断：{blocking}",
		]);
	}

	private static string FormatDeactivationPlan(CliDeactivationPlanDocument document, string outputPath)
	{
		var blocking = document.Diagnostics.Count(diagnostic => diagnostic.Blocking);
		return string.Join(Environment.NewLine,
		[
			$"已导出停用计划：{outputPath}",
			$"将移除 Patch：{document.Patches.Count}；外部文件：{document.ExternalFiles.Count}",
			$"当前归属：{document.Ownership.Status}；阻断诊断：{blocking}",
		]);
	}

	private static string FormatDeploymentValidation(CliDeploymentValidationResult validation)
	{
		var status = validation.IsValid ? "部署验证通过。" : "部署验证未通过。";
		var issues = validation.Diagnostics.Count == 0
			? "无诊断。"
			: string.Join(Environment.NewLine, validation.Diagnostics.Select(diagnostic => $"- [{diagnostic.Code}] {diagnostic.Message}"));
		return $"{status}{Environment.NewLine}计划：{validation.Document.PlanId}{Environment.NewLine}{issues}";
	}

	private static string FormatDeactivationValidation(CliDeactivationValidationResult validation)
	{
		var status = validation.IsValid ? "停用验证通过。" : "停用验证未通过。";
		var issues = validation.Diagnostics.Count == 0
			? "无诊断。"
			: string.Join(Environment.NewLine, validation.Diagnostics.Select(diagnostic => $"- [{diagnostic.Code}] {diagnostic.Message}"));
		return $"{status}{Environment.NewLine}计划：{validation.Document.PlanId}{Environment.NewLine}{issues}";
	}

	private static string FormatDeploymentExecution(CliDeploymentExecutionResult execution)
	{
		var errors = execution.Diagnostics.Where(diagnostic => diagnostic.Blocking).ToArray();
		if (execution.Success)
		{
			var patchCount = execution.ApplyResult?.Operations.Count(operation => operation.Operation.Kind == ApplyOperationKind.DeployPatch && operation.Success) ?? 0;
			return $"部署完成：{patchCount} 个 Patch 操作成功；方式：{execution.DeploymentMethod ?? "未知"}。";
		}
		return errors.Length == 0
			? "部署未完成；请查看 JSON 结果或日志。"
			: $"部署被拒绝或失败：{string.Join("；", errors.Take(5).Select(error => $"[{error.Code}] {error.Message}"))}";
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

	private static string FormatDeletionPlan(SafeModDeletionPlan plan)
	{
		var headings = new List<string>
		{
			$"删除预览：计划移除 {plan.LibraryNodeRemovals.Count} 个 Mod 节点。",
			$"范围：{(plan.Scope == SafeModDeletionScope.WithUnsharedOptions ? "所选节点及无共享选项后代" : "仅所选节点")}。",
		};
		if (plan.PreservedSharedOptions.Count > 0)
			headings.Add($"将保留 {plan.PreservedSharedOptions.Count} 个仍有其它主体的共享选项。");
		if (plan.Blockers.Count > 0)
			headings.AddRange(plan.Blockers.Select(item => $"- [{item.Code}] {item.Message}"));
		else headings.Add("可执行：未发现阻断项。执行仍需 --confirm，且不会自动部署。");
		return string.Join(Environment.NewLine, headings);
	}

	private static string FormatDeletionExecution(CliModDeletionExecutionResult result)
	{
		return result.Status switch
		{
			CliModDeletionStatus.Blocked => $"删除被安全规则阻断：{string.Join("；", result.Plan.Blockers.Where(item => item.Severity == SafeModDeletionBlockerSeverity.Blocking).Select(item => item.Code))}",
			CliModDeletionStatus.StorageNotRemoved => "没有任何目标 Mod 存储目录被安全删除；库记录未改动。请查看 storageResults。",
			CliModDeletionStatus.PostCommitIncomplete => $"部分删除已提交，但后续状态清理未完成：{result.PostCommitError ?? "请查看 JSON 结果。"}",
			_ => $"已删除 {result.RemovedNodeIds.Count} 个 Mod 节点；失败 {result.FailedNodeIds.Count} 个，跳过 {result.SkippedNodeIds.Count} 个。未自动部署。",
		};
	}

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
	  hd2mod mod delete preview <Mod GUID或唯一前缀> [...] [--with-unshared-options] --library <Mod库目录> --json
	  hd2mod mod delete <Mod GUID或唯一前缀> [...] [--with-unshared-options] --manager-root <管理器目录> --confirm --json
	  hd2mod option enable <Option GUID或唯一前缀> --host <主体 GUID或唯一前缀> --manager-root <管理器目录> --confirm --json
	  hd2mod option disable <Option GUID或唯一前缀> --host <主体 GUID或唯一前缀> --manager-root <管理器目录> --confirm --json
  hd2mod option move <Option GUID或唯一前缀> [...] --host <主体 GUID或唯一前缀> --position <插入槽位> --manager-root <管理器目录> --confirm --json
	  hd2mod option insert <Option GUID或唯一前缀> [...] --host <主体 GUID或唯一前缀> --position <插入槽位> --manager-root <管理器目录> --confirm --json
  hd2mod profile list --library <Mod库目录> --json
	  hd2mod profile show [名称或GUID前缀] --library <Mod库目录> --json
	  hd2mod profile create <名称> [--set-active] --manager-root <管理器目录> --confirm --json
	  hd2mod profile delete <名称或GUID前缀> --manager-root <管理器目录> --confirm --json
	  hd2mod profile rename <名称或GUID前缀> <新名称> --manager-root <管理器目录> --confirm --json
	  hd2mod profile set-active <名称或GUID前缀> --manager-root <管理器目录> --confirm --json
	  hd2mod profile clear-active --manager-root <管理器目录> --confirm --json
	  hd2mod profile add <配置> <Mod GUID或前缀> [...] --manager-root <管理器目录> --confirm --json
	  hd2mod profile remove <配置> <Mod GUID或前缀> [...] --manager-root <管理器目录> --confirm --json
	  hd2mod profile insert <配置> <Mod GUID或前缀> [...] --position <插入槽位> --manager-root <管理器目录> --confirm --json
	  hd2mod profile move <配置> <Mod GUID或前缀> [...] --position <插入槽位> --manager-root <管理器目录> --confirm --json
	  hd2mod profile reorder <配置> <按完整逻辑顺序的Mod GUID或前缀> [...] --manager-root <管理器目录> --confirm --json
	  hd2mod deploy plan [配置名称、GUID或active] --manager-root <管理器目录> --game-data <游戏data目录> --output deployment-plan.json --json
	  hd2mod deploy validate --plan deployment-plan.json --manager-root <管理器目录> --game-data <游戏data目录> --json
	  hd2mod deploy apply --plan deployment-plan.json --manager-root <管理器目录> --game-data <游戏data目录> --confirm --json
	  hd2mod deploy deactivate plan --manager-root <管理器目录> --game-data <游戏data目录> --output deactivation-plan.json --json
	  hd2mod deploy deactivate validate --plan deactivation-plan.json --manager-root <管理器目录> --game-data <游戏data目录> --json
	  hd2mod deploy deactivate apply --plan deactivation-plan.json --manager-root <管理器目录> --game-data <游戏data目录> --confirm --json
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
	  --game-data <目录>      GameData 覆盖路径；部署命令要求真实游戏的 data 目录。
  --json                  输出机器可读 JSON。
	  --output <文件或目录>   import/deploy plan、context export、Agent Skill 导出使用。
	  --plan <文件>           import/deploy validate 与 apply 所需的计划。
	  --overwrite             显式允许覆盖已有导出文件或已命名 Skill 目录中的 SKILL.md。
	  --host <GUID或前缀>      Option 操作的明确主体；不支持隐式或全主体修改。
	  --position <正整数>      Option 排序的从 1 开始插入槽位；末尾为直属选项数 + 1。
	  --set-active              创建配置时同时将它设为活动配置。
	  --with-unshared-options   删除主体时同时删除没有幸存主体的递归 Option（仅 delete 命令可用）。
	  --confirm               所有写入命令的明确确认。

安全边界：Mod 库写入要求桌面端关闭、严格库读取、跨进程库租约和显式 --confirm。deploy apply 额外要求游戏退出、真实 GameData、双租约与完整 Patch 归属证明；不会接管未知 Patch，也不会执行 Mod Lua。
""";
}
