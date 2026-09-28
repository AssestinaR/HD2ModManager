using System.Reflection;
using System.Text.Json;
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
		if (command.Length == 3 && command[0] == "diagnostics" && command[1] == "armor"
			&& command[2] is "wizard" or "recover" or "quick" or "matrix" or "composition" or "memory")
		{
			if (command[2] == "memory")
				return await ArmorMemoryDiagnostic.RunAsync(invocation.Options).ConfigureAwait(false);
			if (command[2] is "matrix" or "composition")
				return await ArmorDiagnosticMatrix.RunAsync(invocation.Options, command[2] == "composition").ConfigureAwait(false);
			return await ArmorDiagnosticWizard.RunAsync(invocation.Options, command[2]).ConfigureAwait(false);
		}
		var addToProfileSupported = (command.Length == 3 && command[0] == "import" && command[1] == "plan")
			|| (command.Length >= 4 && command[0] == "import" && command[1] == "batch" && command[2] == "plan");
		if (!string.IsNullOrWhiteSpace(invocation.Options.AddToProfile) && !addToProfileSupported)
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"--add-to-profile 仅可用于 import plan 或 import batch plan；它会被固定在计划中，并在 validate/apply 时重新校验。");
		}
		if ((invocation.Options.Limit is not null
			|| !string.IsNullOrWhiteSpace(invocation.Options.Cursor)
			|| invocation.Options.WithEffective
			|| invocation.Options.OnlyCompetition)
			&& (command.Length == 0 || command[0] != "gamedata"))
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"--limit、--cursor、--with-effective 与 --only-competition 仅适用于 gamedata 只读查询。 ");
		}
		if (!string.IsNullOrWhiteSpace(invocation.Options.RegistryRoot)
			&& (command.Length == 0 || command[0] != "armor-registry"))
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"--registry-root 仅适用于 armor-registry 命令。");
		}
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
		if (command.Length >= 3 && command[0] == "custom-model" && command[1] is "inspect" or "convert")
		{
			var convert = command[1] == "convert";
			if (command.Length != (convert ? 5 : 3))
				throw new CliUserException("InvalidArguments", "custom-model inspect <mod>；custom-model convert <mod> <archive> <name> --output <目录> --confirm");
			if (convert && !invocation.Options.Confirm)
				throw new CliUserException("ConfirmationRequired", "转换会输出新 Patch 并在当前 Mod 库登记素体，请指定 --confirm。");
			var workspace = CliWorkspace.Open(invocation.Options);
			if (workspace.ManagerRoot is null || workspace.LibraryRoot is null || workspace.GameDataRoot is null)
				throw new CliUserException("WorkspaceRequired", "请指定 --manager-root、--library 和 --game-data。");
			var state = await workspace.LoadLibraryAsync();
			var source = CliProjections.ResolveMod(state.Snapshot.Nodes, invocation.Command[2]);
			var paths = new StoragePaths(workspace.ManagerRoot, workspace.LibraryRoot);
			await using var reader = CoreServices.CreateModInformationReader();
			var facts = await CoreServices.CreateDirectModEquipmentSourceFactsReader(paths, reader).ReadAsync(
				source, workspace.LibraryRoot,
				new ModInformationRequest(ModInformationKind.AssetInventory, "CustomModelConversion",
					Context: ModInformationRequestContext.Create(ModInformationCacheScope.None, operationName: "CustomModelConversion"))
				{ Property = ModInformationPropertyKind.UnitPartMapping });
			var converter = new CustomModelConversionService(paths, reader);
			if (!convert)
			{
				EnsureNoExplicitOutput(invocation.Options);
				var sources = await converter.InspectAsync(facts);
				Emit(invocation.Options, new { ok = true, sources }, () => string.Join(Environment.NewLine,
					sources.Select(item => $"{item.ArchiveId}  {item.DisplayName}  {item.Parts.Select(p => p.UnitAssetKey).Distinct().Count()} Units")));
			}
			else
			{
				var result = await converter.GenerateAsync(facts, invocation.Command[3].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), invocation.Command[4],
					RequireOutput(invocation.Options, "custom-model convert"), workspace.GameDataRoot);
				Emit(invocation.Options, new { ok = true, result }, () => $"已生成并登记素体：{result.OutputDirectory}");
			}
			return 0;
		}
		if (Matches(command, "armor-registry", "status"))
		{
			EnsureArmorRegistryReadOptions(invocation.Options, "armor-registry status");
			var root = CliArmorRegistryProtocol.ResolveRegistryRoot(invocation.Options.RegistryRoot);
			var status = await CliArmorRegistryProtocol.ReadStatusAsync(root).ConfigureAwait(false);
			Emit(
				invocation.Options,
				new { ok = true, armorRegistry = status },
				() => $"护甲注册运行时状态目录：{root}");
			return 0;
		}
		if (Matches(command, "armor-registry", "show"))
		{
			EnsureArmorRegistryReadOptions(invocation.Options, "armor-registry show");
			var root = CliArmorRegistryProtocol.ResolveRegistryRoot(invocation.Options.RegistryRoot);
			var document = await CliArmorRegistryProtocol.ReadProtocolAsync(root, allowMissing: false).ConfigureAwait(false);
			Emit(
				invocation.Options,
				new
				{
					ok = true,
					registryRoot = root,
					protocolPath = Path.Combine(root, CliArmorRegistryProtocol.ProtocolFileName),
					protocol = document,
				},
				() => $"护甲注册协议 revision={document.Revision}，enabled={document.Enabled}，映射 {document.Mappings.Count} 条。");
			return 0;
		}
		if (command.Length == 3 && command[0] == "armor-registry" && command[1] == "plan")
		{
			EnsureArmorRegistryPlanOptions(invocation.Options);
			var output = RequireOutput(invocation.Options, "armor-registry plan");
			var root = CliArmorRegistryProtocol.ResolveRegistryRoot(invocation.Options.RegistryRoot);
			var plan = await CliArmorRegistryProtocol.BuildPlanAsync(invocation.Command[2], root).ConfigureAwait(false);
			await CliOutput.WriteExplicitFileAsync(output, CliOutput.ToJson(plan), invocation.Options.Overwrite).ConfigureAwait(false);
			Emit(
				invocation.Options,
				new
				{
					ok = true,
					output = Path.GetFullPath(output),
					plan = new
					{
						plan.Schema,
						plan.RegistryRoot,
						plan.InputFingerprint,
						plan.Desired,
					},
				},
				() => $"已导出护甲注册计划：{Path.GetFullPath(output)}");
			return 0;
		}
		if (Matches(command, "armor-registry", "validate"))
		{
			EnsureArmorRegistryValidateOptions(invocation.Options);
			var plan = await CliArmorRegistryProtocol.ReadPlanAsync(RequireArmorRegistryPlan(invocation.Options)).ConfigureAwait(false);
			var validation = await CliArmorRegistryProtocol.ValidateAsync(plan, invocation.Options.RegistryRoot).ConfigureAwait(false);
			Emit(
				invocation.Options,
				new { ok = validation.IsValid, validation },
				() => validation.IsValid
					? $"护甲注册计划状态：{validation.Status}。"
					: $"护甲注册计划已失效：{string.Join("；", validation.Diagnostics.Select(item => item.Message))}");
			return validation.IsValid ? 0 : 2;
		}
		if (Matches(command, "armor-registry", "apply"))
		{
			EnsureArmorRegistryApplyOptions(invocation.Options);
			if (!invocation.Options.Confirm)
				throw new CliUserException(
					"ArmorRegistryApplyConfirmationRequired",
					"armor-registry apply 会原子替换外置护甲注册协议并触发运行时热更新；请审阅计划后显式传入 --confirm。");
			var plan = await CliArmorRegistryProtocol.ReadPlanAsync(RequireArmorRegistryPlan(invocation.Options)).ConfigureAwait(false);
			var execution = await CliArmorRegistryProtocol.ApplyAsync(plan, invocation.Options.RegistryRoot).ConfigureAwait(false);
			Emit(
				invocation.Options,
				new { ok = execution.Success, execution },
				() => $"护甲注册协议状态：{execution.Status}，revision={execution.Revision}，活动映射 {execution.ActiveMappingCount} 条。");
			return execution.Success ? 0 : 2;
		}
		if (Matches(command, "assets", "metadata", "inspect"))
		{
			EnsureAssetMetadataInspectOptions(invocation.Options);
			var inspection = await CliAssetMetadataPlanArtifacts
				.InspectAsync(CliWorkspace.Open(invocation.Options))
				.ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = inspection.IsReady, metadata = CreateAssetMetadataInspectionProjection(inspection) },
				() => FormatAssetMetadataInspection(inspection));
			return inspection.IsReady ? 0 : 2;
		}
		if ((command.Length == 3 || command.Length == 4)
			&& command[0] == "assets" && command[1] == "metadata" && command[2] == "plan")
		{
			EnsureAssetMetadataPlanOptions(invocation.Options);
			var output = RequireOutput(invocation.Options, "assets metadata plan");
			var build = await CliAssetMetadataPlanArtifacts
				.BuildAsync(CliWorkspace.Open(invocation.Options), command.Length == 4 ? invocation.Command[3] : null, output, invocation.Options.Overwrite)
				.ConfigureAwait(false);
			await CliOutput.WriteExplicitFileAsync(output, CliOutput.ToJson(build.Document), invocation.Options.Overwrite).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = build.Document.IsReady, output = Path.GetFullPath(output), metadata = CreateAssetMetadataPlanProjection(build.Document) },
				() => FormatAssetMetadataPlan(build.Document, Path.GetFullPath(output)));
			return build.Document.IsReady ? 0 : 2;
		}
		if (Matches(command, "assets", "metadata", "validate"))
		{
			EnsureAssetMetadataValidateOptions(invocation.Options);
			var document = await CliAssetMetadataPlanArtifacts.ReadDocumentAsync(RequireAssetMetadataPlan(invocation.Options)).ConfigureAwait(false);
			var validation = await CliAssetMetadataPlanArtifacts.ValidateAsync(document, CliWorkspace.Open(invocation.Options)).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = validation.IsValid, validation = CreateAssetMetadataValidationProjection(validation) },
				() => FormatAssetMetadataValidation(validation));
			return validation.IsValid ? 0 : 2;
		}
		if (Matches(command, "assets", "metadata", "apply"))
		{
			EnsureAssetMetadataApplyOptions(invocation.Options);
			if (!invocation.Options.Confirm)
				throw new CliUserException("AssetMetadataApplyConfirmationRequired", "assets metadata apply 会替换管理器缓存的在线资产元数据并更新最后检查时间；请在人工确认后显式传入 --confirm。 ");
			var document = await CliAssetMetadataPlanArtifacts.ReadDocumentAsync(RequireAssetMetadataPlan(invocation.Options)).ConfigureAwait(false);
			var execution = await CliAssetMetadataExecutor.ApplyAsync(document, CliWorkspace.Open(invocation.Options)).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = execution.Success, execution = CreateAssetMetadataExecutionProjection(execution) },
				() => FormatAssetMetadataExecution(execution));
			return execution.Success ? 0 : 2;
		}
		if (Matches(command, "gamedata", "status"))
		{
			EnsureGameDataReadOnlyOptions(invocation.Options, "gamedata status");
			var inspection = await CliGameDataInspection.BuildIndexAsync(CliWorkspace.Open(invocation.Options)).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = inspection.IsReady, inspection = CreateGameDataIndexProjection(inspection) },
				() => FormatGameDataIndex(inspection));
			return inspection.IsReady ? 0 : 2;
		}
		if (Matches(command, "gamedata", "archive", "list"))
		{
			EnsureGameDataReadOnlyOptions(invocation.Options, "gamedata archive list", allowPaging: true);
			var inspection = await CliGameDataInspection.ListArchivesAsync(CliWorkspace.Open(invocation.Options)).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = inspection.IsReady, archives = CreateGameDataArchiveListProjection(inspection, invocation.Options) },
				() => FormatGameDataArchiveList(inspection));
			return inspection.IsReady ? 0 : 2;
		}
		if (command.Length == 4 && command[0] == "gamedata" && command[1] == "archive" && command[2] == "show")
		{
			EnsureGameDataReadOnlyOptions(invocation.Options, "gamedata archive show", allowPaging: true, allowWithEffective: true);
			var workspace = CliWorkspace.Open(invocation.Options);
			var inspection = await CliGameDataInspection.ShowArchiveAsync(workspace, invocation.Command[3]).ConfigureAwait(false);
			var overrides = invocation.Options.WithEffective
				? await CliGameDataInspection.InspectOverridesAsync(workspace).ConfigureAwait(false)
				: null;
			var ready = inspection.IsReady && (overrides?.IsReadable ?? true);
			Emit(invocation.Options,
				new { ok = ready, archive = CreateGameDataArchiveDetailsProjection(inspection, invocation.Options, overrides) },
				() => FormatGameDataArchiveDetails(inspection));
			return ready ? 0 : 2;
		}
		if (command.Length >= 3 && command[0] == "gamedata" && command[1] == "asset" && command[2] == "find")
		{
			EnsureGameDataReadOnlyOptions(invocation.Options, "gamedata asset find");
			var inspection = await CliGameDataInspection.FindAssetsAsync(CliWorkspace.Open(invocation.Options), invocation.Command.Skip(3).ToArray()).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = inspection.IsReady, assets = CreateGameDataAssetLookupProjection(inspection) },
				() => FormatGameDataAssetLookup(inspection));
			return inspection.IsReady ? 0 : 2;
		}
		if (Matches(command, "gamedata", "deployment", "inspect"))
		{
			EnsureGameDataReadOnlyOptions(invocation.Options, "gamedata deployment inspect");
			var inspection = await CliGameDataInspection.InspectDeploymentAsync(CliWorkspace.Open(invocation.Options)).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = inspection.IsReadable, overrides = CreateGameDataOverrideStatusProjection(inspection) },
				() => FormatGameDataOverrideStatus(inspection));
			return inspection.IsReadable ? 0 : 2;
		}
		if (Matches(command, "gamedata", "override", "status"))
		{
			EnsureGameDataReadOnlyOptions(invocation.Options, "gamedata override status");
			var inspection = await CliGameDataInspection.InspectOverridesAsync(CliWorkspace.Open(invocation.Options)).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = inspection.IsReadable, overrides = CreateGameDataOverrideStatusProjection(inspection) },
				() => FormatGameDataOverrideStatus(inspection));
			return inspection.IsReadable ? 0 : 2;
		}
		if (Matches(command, "gamedata", "override", "list"))
		{
			EnsureGameDataReadOnlyOptions(invocation.Options, "gamedata override list", allowPaging: true, allowOnlyCompetition: true);
			var inspection = await CliGameDataInspection.InspectOverridesAsync(CliWorkspace.Open(invocation.Options)).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = inspection.IsReadable, overrides = CreateGameDataOverrideListProjection(inspection, invocation.Options) },
				() => FormatGameDataOverrideList(inspection, invocation.Options.OnlyCompetition));
			return inspection.IsReadable ? 0 : 2;
		}
		if (command.Length == 5 && command[0] == "gamedata" && command[1] == "override" && command[2] == "show")
		{
			EnsureGameDataReadOnlyOptions(invocation.Options, "gamedata override show");
			var key = CliGameDataInspection.ParseAssetKeyPair(invocation.Command[3], invocation.Command[4]);
			var inspection = await CliGameDataInspection.InspectOverridesAsync(CliWorkspace.Open(invocation.Options)).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = inspection.IsReadable, overrides = CreateGameDataOverrideShowProjection(inspection, key) },
				() => FormatGameDataOverrideShow(inspection, key));
			return inspection.IsReadable ? 0 : 2;
		}
		if (Matches(command, "gamedata", "browser"))
		{
			EnsureGameDataReadOnlyOptions(invocation.Options, "gamedata browser", allowPaging: true);
			var inspection = await CliGameDataInspection.BuildBrowserAsync(CliWorkspace.Open(invocation.Options)).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = inspection.IsReady, browser = CreateGameDataBrowserProjection(inspection, invocation.Options) },
				() => FormatGameDataBrowser(inspection));
			return inspection.IsReady ? 0 : 2;
		}
		if (Matches(command, "gamedata", "index", "inspect"))
		{
			EnsureGameDataIndexInspectOptions(invocation.Options);
			var build = await CliGameDataIndexPlanArtifacts
				.BuildAsync(CliWorkspace.Open(invocation.Options), "rebuild")
				.ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = build.Document.IsReady, inspection = CreateGameDataIndexPlanProjection(build.Document) },
				() => FormatGameDataIndexMaintenanceInspection(build.Document));
			return 0;
		}
		if (command.Length == 4 && command[0] == "gamedata" && command[1] == "index" && command[2] == "plan")
		{
			EnsureGameDataIndexPlanOptions(invocation.Options);
			var output = RequireOutput(invocation.Options, "gamedata index plan");
			var build = await CliGameDataIndexPlanArtifacts
				.BuildAsync(CliWorkspace.Open(invocation.Options), command[3])
				.ConfigureAwait(false);
			await CliOutput.WriteExplicitFileAsync(output, CliOutput.ToJson(build.Document), invocation.Options.Overwrite).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = build.Document.IsReady, output = Path.GetFullPath(output), index = CreateGameDataIndexPlanProjection(build.Document) },
				() => FormatGameDataIndexMaintenancePlan(build.Document, Path.GetFullPath(output)));
			return build.Document.IsReady ? 0 : 2;
		}
		if (Matches(command, "gamedata", "index", "validate"))
		{
			EnsureGameDataIndexValidateOptions(invocation.Options);
			var document = await CliGameDataIndexPlanArtifacts.ReadDocumentAsync(RequireGameDataIndexPlan(invocation.Options)).ConfigureAwait(false);
			var validation = await CliGameDataIndexPlanArtifacts.ValidateAsync(document, CliWorkspace.Open(invocation.Options)).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = validation.IsValid, validation = CreateGameDataIndexValidationProjection(validation) },
				() => FormatGameDataIndexMaintenanceValidation(validation));
			return validation.IsValid ? 0 : 2;
		}
		if (Matches(command, "gamedata", "index", "apply"))
		{
			EnsureGameDataIndexApplyOptions(invocation.Options);
			if (!invocation.Options.Confirm)
				throw new CliUserException("GameDataIndexApplyConfirmationRequired", "gamedata index apply 会重建管理器的资产索引或 Unit 部位事实；请在人工确认后显式传入 --confirm。 ");
			var document = await CliGameDataIndexPlanArtifacts.ReadDocumentAsync(RequireGameDataIndexPlan(invocation.Options)).ConfigureAwait(false);
			var execution = await CliGameDataIndexExecutor.ApplyAsync(document, CliWorkspace.Open(invocation.Options)).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = execution.Success, execution = CreateGameDataIndexExecutionProjection(execution) },
				() => FormatGameDataIndexMaintenanceExecution(execution));
			return execution.Success ? 0 : 2;
		}
		if (Matches(command, "library", "sync", "inspect"))
		{
			EnsureLibrarySyncInspectOptions(invocation.Options);
			var build = await CliLibrarySynchronizationPlanArtifacts
				.BuildAsync(CliWorkspace.Open(invocation.Options))
				.ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = build.Document.IsReady, inspection = CreateLibrarySyncPlanProjection(build.Document) },
				() => FormatLibrarySyncInspection(build.Document));
			return 0;
		}
		if (Matches(command, "library", "sync", "plan"))
		{
			EnsureLibrarySyncPlanOptions(invocation.Options);
			var output = RequireOutput(invocation.Options, "library sync plan");
			var build = await CliLibrarySynchronizationPlanArtifacts
				.BuildAsync(CliWorkspace.Open(invocation.Options))
				.ConfigureAwait(false);
			await CliOutput.WriteExplicitFileAsync(output, CliOutput.ToJson(build.Document), invocation.Options.Overwrite).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = build.Document.IsReady, output = Path.GetFullPath(output), synchronization = CreateLibrarySyncPlanProjection(build.Document) },
				() => FormatLibrarySyncPlan(build.Document, Path.GetFullPath(output)));
			return build.Document.IsReady ? 0 : 2;
		}
		if (Matches(command, "library", "sync", "validate"))
		{
			EnsureLibrarySyncValidateOptions(invocation.Options);
			var document = await CliLibrarySynchronizationPlanArtifacts.ReadDocumentAsync(RequireLibrarySyncPlan(invocation.Options)).ConfigureAwait(false);
			var validation = await CliLibrarySynchronizationPlanArtifacts
				.ValidateAsync(document, CliWorkspace.Open(invocation.Options))
				.ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = validation.IsValid, validation = CreateLibrarySyncValidationProjection(validation) },
				() => FormatLibrarySyncValidation(validation));
			return validation.IsValid ? 0 : 2;
		}
		if (Matches(command, "library", "sync", "apply"))
		{
			EnsureLibrarySyncApplyOptions(invocation.Options);
			if (!invocation.Options.Confirm)
				throw new CliUserException("LibrarySyncApplyConfirmationRequired", "library sync apply 会更新 library.json；请在人工确认后显式传入 --confirm。 ");
			var document = await CliLibrarySynchronizationPlanArtifacts.ReadDocumentAsync(RequireLibrarySyncPlan(invocation.Options)).ConfigureAwait(false);
			var execution = await CliLibrarySynchronizationExecutor
				.ApplyAsync(document, CliWorkspace.Open(invocation.Options))
				.ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = execution.Success, execution = CreateLibrarySyncExecutionProjection(execution) },
				() => FormatLibrarySyncExecution(execution));
			return execution.Success ? 0 : 2;
		}
		if (command.Length == 4 && command[0] == "decoration" && command[1] == "inspect" && command[2] == "source")
		{
			EnsureDecorationSourceInspectOptions(invocation.Options);
			var inspection = await CliDecorationSourceInspectionArtifacts
				.BuildAsync(CliWorkspace.Open(invocation.Options), invocation.Command[3])
				.ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = inspection.IsReady, inspection },
				() => FormatDecorationSourceInspection(inspection));
			return inspection.IsReady ? 0 : 2;
		}
		if (command.Length == 3 && command[0] == "decoration" && command[1] == "show")
		{
			EnsureDecorationShowOptions(invocation.Options);
			var inspection = await CliDecorationManagementInspectionArtifacts
				.BuildAsync(CliWorkspace.Open(invocation.Options), invocation.Command[2])
				.ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = inspection.IsReadable, decoration = inspection },
				() => FormatDecorationManagementInspection(inspection));
			return inspection.IsReadable ? 0 : 2;
		}
		if (command.Length == 4 && command[0] == "decoration" && command[1] == "package" && command[2] == "plan")
		{
			EnsureDecorationPackagePlanOptions(invocation.Options);
			var output = RequireOutput(invocation.Options, "decoration package plan");
			var build = await CliDecorationPackagePlanArtifacts
				.BuildAsync(CliWorkspace.Open(invocation.Options), invocation.Command[3], RequireHost(invocation.Options))
				.ConfigureAwait(false);
			await CliOutput.WriteExplicitFileAsync(output, CliOutput.ToJson(build.Document), invocation.Options.Overwrite).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = build.Document.IsReady, output = Path.GetFullPath(output), package = build.Document },
				() => FormatDecorationPackagePlan(build.Document, Path.GetFullPath(output)));
			return build.Document.IsReady ? 0 : 2;
		}
		if (Matches(command, "decoration", "package", "validate"))
		{
			EnsureDecorationPackageValidateOptions(invocation.Options);
			var document = await CliDecorationPackagePlanArtifacts.ReadDocumentAsync(RequireDecorationPackagePlan(invocation.Options)).ConfigureAwait(false);
			var validation = await CliDecorationPackagePlanArtifacts
				.ValidateAsync(document, CliWorkspace.Open(invocation.Options))
				.ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = validation.IsValid, validation },
				() => FormatDecorationPackageValidation(validation));
			return validation.IsValid ? 0 : 2;
		}
		if (Matches(command, "decoration", "package", "apply"))
		{
			EnsureDecorationPackageApplyOptions(invocation.Options);
			if (!invocation.Options.Confirm)
				throw new CliUserException("DecorationPackageApplyConfirmationRequired", "decoration package apply 会写出新的外部装饰包；请在人工确认后显式传入 --confirm。 ");
			var output = RequireOutput(invocation.Options, "decoration package apply");
			var document = await CliDecorationPackagePlanArtifacts.ReadDocumentAsync(RequireDecorationPackagePlan(invocation.Options)).ConfigureAwait(false);
			var execution = await CliDecorationPackageExecutor
				.ApplyAsync(document, CliWorkspace.Open(invocation.Options), output)
				.ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = execution.Success, execution },
				() => FormatDecorationPackageExecution(execution));
			return execution.Success ? 0 : 2;
		}
		if (command.Length == 4 && command[0] == "decoration" && command[1] == "edit" && command[2] == "export")
		{
			EnsureDecorationEditExportOptions(invocation.Options);
			var output = RequireOutput(invocation.Options, "decoration edit export");
			var specification = await CliDecorationEditPlanArtifacts
				.ExportSpecAsync(CliWorkspace.Open(invocation.Options), invocation.Command[3])
				.ConfigureAwait(false);
			await CliOutput.WriteExplicitFileAsync(output, CliOutput.ToJson(specification), invocation.Options.Overwrite).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = specification.IsReady, output = Path.GetFullPath(output), specification },
				() => FormatDecorationEditSpecification(specification, Path.GetFullPath(output)));
			return specification.IsReady ? 0 : 2;
		}
		if (command.Length == 4 && command[0] == "decoration" && command[1] == "edit" && command[2] == "inspect")
		{
			EnsureDecorationEditInspectOptions(invocation.Options);
			var specification = await CliDecorationEditPlanArtifacts.ReadSpecAsync(invocation.Command[3]).ConfigureAwait(false);
			var build = await CliDecorationEditPlanArtifacts
				.BuildAsync(CliWorkspace.Open(invocation.Options), new CliDecorationEditRequest(
					specification.Source.DecorationNodeId,
					specification.Selections,
					specification.Attachment,
					specification.Source.CurrentPlanSha256))
				.ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = build.Document.IsReady, inspection = build.Document },
				() => FormatDecorationEditInspection(build.Document));
			return build.Document.IsReady ? 0 : 2;
		}
		if (command.Length == 4 && command[0] == "decoration" && command[1] == "edit" && command[2] == "plan")
		{
			EnsureDecorationEditPlanOptions(invocation.Options);
			var output = RequireOutput(invocation.Options, "decoration edit plan");
			var specification = await CliDecorationEditPlanArtifacts.ReadSpecAsync(invocation.Command[3]).ConfigureAwait(false);
			var build = await CliDecorationEditPlanArtifacts
				.BuildAsync(CliWorkspace.Open(invocation.Options), new CliDecorationEditRequest(
					specification.Source.DecorationNodeId,
					specification.Selections,
					specification.Attachment,
					specification.Source.CurrentPlanSha256))
				.ConfigureAwait(false);
			await CliOutput.WriteExplicitFileAsync(output, CliOutput.ToJson(build.Document), invocation.Options.Overwrite).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = build.Document.IsReady, output = Path.GetFullPath(output), edit = build.Document },
				() => FormatDecorationEditPlan(build.Document, Path.GetFullPath(output)));
			return build.Document.IsReady ? 0 : 2;
		}
		if (Matches(command, "decoration", "edit", "validate"))
		{
			EnsureDecorationEditValidateOptions(invocation.Options);
			var document = await CliDecorationEditPlanArtifacts.ReadDocumentAsync(RequireDecorationEditPlan(invocation.Options)).ConfigureAwait(false);
			var validation = await CliDecorationEditPlanArtifacts
				.ValidateAsync(document, CliWorkspace.Open(invocation.Options))
				.ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = validation.IsValid, validation },
				() => FormatDecorationEditValidation(validation));
			return validation.IsValid ? 0 : 2;
		}
		if (Matches(command, "decoration", "edit", "apply"))
		{
			EnsureDecorationEditApplyOptions(invocation.Options);
			if (!invocation.Options.Confirm)
				throw new CliUserException("DecorationEditApplyConfirmationRequired", "decoration edit apply 会更新库内 decoration.json、回建所有已启用主体的 Overwrite，并可能清理已移除主体的装饰启用状态；请审阅计划后显式传入 --confirm。 ");
			var document = await CliDecorationEditPlanArtifacts.ReadDocumentAsync(RequireDecorationEditPlan(invocation.Options)).ConfigureAwait(false);
			var execution = await CliDecorationEditExecutor
				.ApplyAsync(document, CliWorkspace.Open(invocation.Options))
				.ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = execution.Success, execution },
				() => FormatDecorationEditExecution(execution));
			return execution.Success ? 0 : 2;
		}
		if (command.Length == 4 && command[0] == "mod" && command[1] == "export" && command[2] == "plan")
		{
			EnsureModExportPlanOptions(invocation.Options);
			var output = RequireOutput(invocation.Options, "mod export plan");
			var build = await CliModExportPlanArtifacts
				.BuildAsync(CliWorkspace.Open(invocation.Options), invocation.Command[3])
				.ConfigureAwait(false);
			await CliOutput.WriteExplicitFileAsync(output, CliOutput.ToJson(build.Document), invocation.Options.Overwrite).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = build.Document.IsReady, output = Path.GetFullPath(output), export = build.Document },
				() => FormatModExportPlan(build.Document, Path.GetFullPath(output)));
			return build.Document.IsReady ? 0 : 2;
		}
		if (Matches(command, "mod", "export", "validate"))
		{
			EnsureModExportValidateOptions(invocation.Options);
			var document = await CliModExportPlanArtifacts.ReadDocumentAsync(RequireModExportPlan(invocation.Options)).ConfigureAwait(false);
			var validation = await CliModExportPlanArtifacts
				.ValidateAsync(document, CliWorkspace.Open(invocation.Options))
				.ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = validation.IsValid, validation = CreateModExportValidationProjection(validation) },
				() => FormatModExportValidation(validation));
			return validation.IsValid ? 0 : 2;
		}
		if (Matches(command, "mod", "export", "apply"))
		{
			EnsureModExportApplyOptions(invocation.Options);
			if (!invocation.Options.Confirm)
				throw new CliUserException("ModExportApplyConfirmationRequired", "mod export apply 会在指定目录写出 Mod 压缩包；请审阅计划后显式传入 --confirm。 ");
			var document = await CliModExportPlanArtifacts.ReadDocumentAsync(RequireModExportPlan(invocation.Options)).ConfigureAwait(false);
			var execution = await CliModExportExecutor
				.ApplyAsync(document, CliWorkspace.Open(invocation.Options), RequireOutput(invocation.Options, "mod export apply"), invocation.Options.Overwrite)
				.ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = execution.Success, execution },
				() => FormatModExportExecution(execution));
			return execution.Success ? 0 : 2;
		}
		if (command.Length >= 3 && command[0] == "mod" && command[1] == "create")
		{
			EnsureLogicalModCreateOptions(invocation.Options);
			var name = string.Join(" ", invocation.Command.Skip(2)).Trim();
			if (string.IsNullOrWhiteSpace(name))
				throw new CliUserException("ModNameRequired", "mod create 必须提供逻辑 Mod 名称。 ");
			var result = await ExecuteMutationAsync(
				invocation.Options,
				session => CliLibraryMutationOperations.CreateLogicalModAsync(session, name)).ConfigureAwait(false);
			Emit(invocation.Options,
				new
				{
					ok = result.Changed,
					mod = new
					{
						id = CliProjections.FormatId(result.Node.Id),
						name = result.Node.Metadata.Name,
						kind = result.Node.Metadata.Kind.ToString().ToLowerInvariant(),
						isLogicalContainer = result.Node.Metadata.IsLogicalContainer,
						relativePath = result.Node.RelativePath,
					},
				},
				() => $"已创建逻辑 Mod：{result.Node.Metadata.Name}（{CliProjections.FormatId(result.Node.Id)}）。");
			return result.Changed ? 0 : 2;
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
		if (command.Length == 3 && command[0] == "mod" && command[1] == "analyze")
		{
			EnsureModAnalyzeOptions(invocation.Options);
			var analysis = await CliModAnalysis.BuildAsync(CliWorkspace.Open(invocation.Options), invocation.Command[2]).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = analysis.IsReady, analysis },
				() => FormatModAnalysis(analysis));
			return analysis.IsReady ? 0 : 2;
		}
		if (command.Length == 4 && command[0] == "mod" && command[1] == "external" && command[2] == "inspect")
		{
			EnsureModExternalInspectOptions(invocation.Options);
			var inspection = await CliModExternalInspection.BuildAsync(CliWorkspace.Open(invocation.Options), invocation.Command[3]).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = inspection.IsReady, external = inspection },
				() => FormatModExternalInspection(inspection));
			return inspection.IsReady ? 0 : 2;
		}
		if (command.Length == 4 && command[0] == "mod" && command[1] == "image" && command[2] == "show")
		{
			EnsureModImageShowOptions(invocation.Options);
			var (workspace, state) = await LoadLibraryAsync(invocation.Options).ConfigureAwait(false);
			var inspection = CliModImageOperations.Inspect(state, workspace, invocation.Command[3]);
			Emit(invocation.Options,
				new { image = CreateModImageInspectionProjection(inspection), issues = state.ReadIssues },
				() => FormatModImageInspection(inspection));
			return 0;
		}
		if (command.Length == 5 && command[0] == "mod" && command[1] == "image" && command[2] == "set")
		{
			EnsureMetadataMutationOptions(invocation.Options, "mod image set");
			var result = await ExecuteMutationAsync(
				invocation.Options,
				session => CliModImageOperations.SetAsync(session, invocation.Command[3], invocation.Command[4])).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = true, image = CreateModImageMutationProjection(result) },
				() => FormatModImageMutation(result));
			return 0;
		}
		if (command.Length == 4 && command[0] == "mod" && command[1] == "image" && command[2] == "clear")
		{
			EnsureMetadataMutationOptions(invocation.Options, "mod image clear");
			var result = await ExecuteMutationAsync(
				invocation.Options,
				session => CliModImageOperations.ClearAsync(session, invocation.Command[3])).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = true, image = CreateModImageMutationProjection(result) },
				() => FormatModImageMutation(result));
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
		if (command.Length >= 4 && command[0] == "mod" && command[1] == "delete" && command[2] == "plan")
		{
			EnsureModDeletionPlanOptions(invocation.Options);
			var output = RequireOutput(invocation.Options, "mod delete plan");
			var build = await CliModDeletionPlanArtifacts
				.BuildAsync(
					CliWorkspace.Open(invocation.Options),
					invocation.Command.Skip(3).ToArray(),
					invocation.Options.WithUnsharedOptions ? SafeModDeletionScope.WithUnsharedOptions : SafeModDeletionScope.Selected)
				.ConfigureAwait(false);
			await CliOutput.WriteExplicitFileAsync(output, CliOutput.ToJson(build.Document), invocation.Options.Overwrite).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = build.Document.IsReady, output = Path.GetFullPath(output), deletion = CreateModDeletionPlanDocumentProjection(build.Document) },
				() => FormatModDeletionPlanDocument(build.Document, Path.GetFullPath(output)));
			return build.Document.IsReady ? 0 : 2;
		}
		if (Matches(command, "mod", "delete", "validate"))
		{
			EnsureModDeletionValidateOptions(invocation.Options);
			var document = await CliModDeletionPlanArtifacts.ReadDocumentAsync(RequireModDeletionPlan(invocation.Options)).ConfigureAwait(false);
			var validation = await CliModDeletionPlanArtifacts.ValidateAsync(document, CliWorkspace.Open(invocation.Options)).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = validation.IsValid, validation = CreateModDeletionValidationProjection(validation) },
				() => FormatModDeletionValidation(validation));
			return validation.IsValid ? 0 : 2;
		}
		if (Matches(command, "mod", "delete", "apply"))
		{
			EnsureModDeletionApplyOptions(invocation.Options);
			if (!invocation.Options.Confirm)
				throw new CliUserException("ModDeletionApplyConfirmationRequired", "mod delete apply 会删除库内文件与状态；请在人工确认后显式传入 --confirm。 ");
			var document = await CliModDeletionPlanArtifacts.ReadDocumentAsync(RequireModDeletionPlan(invocation.Options)).ConfigureAwait(false);
			var execution = await CliModDeletionExecutor.ApplyAsync(document, CliWorkspace.Open(invocation.Options)).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = execution.Success, execution = CreateModDeletionApplyProjection(execution) },
				() => FormatModDeletionApply(execution));
			return execution.Success ? 0 : 2;
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
		if (command.Length >= 3 && command[0] == "mod" && command[1] == "convert-to-option")
		{
			EnsureRelationshipMutationOptions(invocation.Options, "mod convert-to-option");
			var host = RequireHost(invocation.Options);
			var result = await ExecuteMutationAsync(
				invocation.Options,
				session => CliLibraryMutationOperations.AddOptionParentsAsync(
					session,
					host,
					invocation.Command.Skip(2).ToArray())).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = result.Changed, mutation = CreateModRelationshipMutationProjection(result) },
				() => FormatModRelationshipMutation(result, adding: true));
			return result.Changed ? 0 : 2;
		}
		if (command.Length == 3 && command[0] == "option" && command[1] == "show")
		{
			EnsureOptionShowOptions(invocation.Options);
			var (_, state) = await LoadLibraryAsync(invocation.Options).ConfigureAwait(false);
			var option = CliProjections.ResolveMod(state.Snapshot.Nodes, invocation.Command[2]);
			var scope = CliLibraryMutationOperations.InspectOptionAllHosts(state, invocation.Command[2]);
			Emit(invocation.Options,
				new { option = CreateOptionAllHostsInspectionProjection(option, scope), issues = state.ReadIssues },
				() => FormatOptionAllHostsInspection(option, scope));
			return scope.CanMutate ? 0 : 2;
		}
		if (command.Length == 3 && command[0] == "option" && command[1] is "enable-all" or "disable-all")
		{
			EnsureOptionAllHostsMutationOptions(invocation.Options, $"option {command[1]}");
			var enabled = command[1] == "enable-all";
			var result = await ExecuteMutationAsync(
				invocation.Options,
				session => CliLibraryMutationOperations.SetOptionEnabledForAllHostsAsync(session, invocation.Command[2], enabled)).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = true, mutation = CreateOptionAllHostsMutationProjection(result) },
				() => FormatOptionAllHostsMutation(result));
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
		if (command.Length >= 4 && command[0] == "option" && command[1] == "parent" && command[2] is "add" or "remove")
		{
			var adding = command[2] == "add";
			EnsureRelationshipMutationOptions(invocation.Options, $"option parent {command[2]}");
			var host = RequireHost(invocation.Options);
			var optionSelectors = invocation.Command.Skip(3).ToArray();
			var result = await ExecuteMutationAsync(
				invocation.Options,
				session => adding
					? CliLibraryMutationOperations.AddOptionParentsAsync(session, host, optionSelectors)
					: CliLibraryMutationOperations.RemoveOptionParentsAsync(session, host, optionSelectors)).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = result.Changed, mutation = CreateModRelationshipMutationProjection(result) },
				() => FormatModRelationshipMutation(result, adding));
			return result.Changed ? 0 : 2;
		}
		if (command.Length == 4 && command[0] == "profile" && command[1] == "align" && command[2] == "export")
		{
			EnsureProfileAlignExportOptions(invocation.Options);
			var output = RequireOutput(invocation.Options, "profile align export");
			var specification = await CliProfileAlignPlanArtifacts
				.ExportSpecAsync(CliWorkspace.Open(invocation.Options), invocation.Command[3])
				.ConfigureAwait(false);
			await CliOutput.WriteExplicitFileAsync(output, CliOutput.ToJson(specification), invocation.Options.Overwrite).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = true, output = Path.GetFullPath(output), specification },
				() => FormatProfileAlignSpecification(specification, Path.GetFullPath(output)));
			return 0;
		}
		if (command.Length == 4 && command[0] == "profile" && command[1] == "align" && command[2] == "inspect")
		{
			EnsureProfileAlignInspectOptions(invocation.Options);
			var specification = await CliProfileAlignPlanArtifacts.ReadSpecAsync(invocation.Command[3]).ConfigureAwait(false);
			var build = await CliProfileAlignPlanArtifacts
				.BuildAsync(CliWorkspace.Open(invocation.Options), specification)
				.ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = build.Document.IsReady, inspection = build.Document },
				() => FormatProfileAlignInspection(build.Document));
			return build.Document.IsReady ? 0 : 2;
		}
		if (command.Length == 4 && command[0] == "profile" && command[1] == "align" && command[2] == "plan")
		{
			EnsureProfileAlignPlanOptions(invocation.Options);
			var output = RequireOutput(invocation.Options, "profile align plan");
			var specification = await CliProfileAlignPlanArtifacts.ReadSpecAsync(invocation.Command[3]).ConfigureAwait(false);
			var build = await CliProfileAlignPlanArtifacts
				.BuildAsync(CliWorkspace.Open(invocation.Options), specification)
				.ConfigureAwait(false);
			await CliOutput.WriteExplicitFileAsync(output, CliOutput.ToJson(build.Document), invocation.Options.Overwrite).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = build.Document.IsReady, output = Path.GetFullPath(output), alignment = build.Document },
				() => FormatProfileAlignPlan(build.Document, Path.GetFullPath(output)));
			return build.Document.IsReady ? 0 : 2;
		}
		if (Matches(command, "profile", "align", "validate"))
		{
			EnsureProfileAlignValidateOptions(invocation.Options);
			var document = await CliProfileAlignPlanArtifacts.ReadDocumentAsync(RequireProfileAlignPlan(invocation.Options)).ConfigureAwait(false);
			var validation = await CliProfileAlignPlanArtifacts
				.ValidateAsync(document, CliWorkspace.Open(invocation.Options))
				.ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = validation.IsValid, validation = new { status = validation.Status.ToString().ToLowerInvariant(), document = validation.Document, diagnostics = validation.Diagnostics } },
				() => FormatProfileAlignValidation(validation));
			return validation.IsValid ? 0 : 2;
		}
		if (Matches(command, "profile", "align", "apply"))
		{
			EnsureProfileAlignApplyOptions(invocation.Options);
			if (!invocation.Options.Confirm)
				throw new CliUserException("ProfileAlignApplyConfirmationRequired", "profile align apply 会仅更新 profiles.json；请在审阅计划后显式传入 --confirm。 ");
			var document = await CliProfileAlignPlanArtifacts.ReadDocumentAsync(RequireProfileAlignPlan(invocation.Options)).ConfigureAwait(false);
			var execution = await CliProfileAlignExecutor.ApplyAsync(document, CliWorkspace.Open(invocation.Options)).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = execution.Success, execution },
				() => FormatProfileAlignExecution(execution));
			return execution.Success ? 0 : 2;
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
			var build = await CliDeploymentPlanArtifacts.BuildAsync(workspace, selector, allowUnsafeLua: invocation.Options.AllowUnsafeLua).ConfigureAwait(false);
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
			var validation = await CliDeploymentPlanArtifacts.ValidateAsync(document, CliWorkspace.Open(invocation.Options), includeProcessCheck: true, allowUnsafeLua: invocation.Options.AllowUnsafeLua).ConfigureAwait(false);
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
			var execution = await CliDeploymentExecutor.ApplyAsync(document, CliWorkspace.Open(invocation.Options), allowUnsafeLua: invocation.Options.AllowUnsafeLua).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = execution.Success, execution = CreateDeploymentExecutionProjection(execution) },
				() => FormatDeploymentExecution(execution));
			return execution.Success ? 0 : 2;
		}
		if (command.Length >= 4 && command[0] == "patch" && command[1] == "repackage" && command[2] == "inspect")
		{
			EnsurePatchRepackagingInspectOptions(invocation.Options);
			var build = await CliPatchResourceRepackagingPlanArtifacts
				.BuildAsync(CliWorkspace.Open(invocation.Options), invocation.Command.Skip(3).ToArray())
				.ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = build.Document.IsReady, inspection = CreatePatchResourceRepackagingPlanProjection(build.Document) },
				() => FormatPatchResourceRepackagingInspection(build.Document));
			return 0;
		}
		if (command.Length >= 4 && command[0] == "patch" && command[1] == "repackage" && command[2] == "plan")
		{
			EnsurePatchRepackagingPlanOptions(invocation.Options);
			var output = RequireOutput(invocation.Options, "patch repackage plan");
			var build = await CliPatchResourceRepackagingPlanArtifacts
				.BuildAsync(CliWorkspace.Open(invocation.Options), invocation.Command.Skip(3).ToArray())
				.ConfigureAwait(false);
			await CliOutput.WriteExplicitFileAsync(output, CliOutput.ToJson(build.Document), invocation.Options.Overwrite).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = build.Document.IsReady, output = Path.GetFullPath(output), repackage = CreatePatchResourceRepackagingPlanProjection(build.Document) },
				() => FormatPatchResourceRepackagingPlan(build.Document, Path.GetFullPath(output)));
			return build.Document.IsReady ? 0 : 2;
		}
		if (Matches(command, "patch", "repackage", "validate"))
		{
			EnsurePatchRepackagingValidateOptions(invocation.Options);
			var document = await CliPatchResourceRepackagingPlanArtifacts.ReadDocumentAsync(RequirePatchResourcePlan(invocation.Options)).ConfigureAwait(false);
			var validation = await CliPatchResourceRepackagingPlanArtifacts.ValidateAsync(document, CliWorkspace.Open(invocation.Options)).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = validation.IsValid, validation = CreatePatchResourceRepackagingValidationProjection(validation) },
				() => FormatPatchResourceRepackagingValidation(validation));
			return validation.IsValid ? 0 : 2;
		}
		if (Matches(command, "patch", "repackage", "apply"))
		{
			EnsurePatchRepackagingApplyOptions(invocation.Options);
			if (!invocation.Options.Confirm)
				throw new CliUserException("PatchResourceApplyConfirmationRequired", "patch repackage apply 会写出一个新的独立 Patch 目录；请在人工确认后显式传入 --confirm。 ");
			var document = await CliPatchResourceRepackagingPlanArtifacts.ReadDocumentAsync(RequirePatchResourcePlan(invocation.Options)).ConfigureAwait(false);
			var execution = await CliPatchResourceRepackagingExecutor.ApplyAsync(document, CliWorkspace.Open(invocation.Options), RequireOutput(invocation.Options, "patch repackage apply")).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = execution.Success, execution = CreatePatchResourceRepackagingExecutionProjection(execution) },
				() => FormatPatchResourceRepackagingExecution(execution));
			return execution.Success ? 0 : 2;
		}
		if (command.Length >= 4 && command[0] == "equipment" && command[1] == "helmet-merge" && command[2] == "inspect")
		{
			EnsureHelmetMergeInspectOptions(invocation.Options);
			var build = await CliHelmetToArmorMergePlanArtifacts
				.BuildAsync(CliWorkspace.Open(invocation.Options), invocation.Command.Skip(3).ToArray())
				.ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = build.Document.IsReady, inspection = CreateHelmetMergePlanProjection(build.Document) },
				() => FormatHelmetMergeInspection(build.Document));
			return 0;
		}
		if (command.Length >= 4 && command[0] == "equipment" && command[1] == "helmet-merge" && command[2] == "plan")
		{
			EnsureHelmetMergePlanOptions(invocation.Options);
			var output = RequireOutput(invocation.Options, "equipment helmet-merge plan");
			var build = await CliHelmetToArmorMergePlanArtifacts
				.BuildAsync(CliWorkspace.Open(invocation.Options), invocation.Command.Skip(3).ToArray())
				.ConfigureAwait(false);
			await CliOutput.WriteExplicitFileAsync(output, CliOutput.ToJson(build.Document), invocation.Options.Overwrite).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = build.Document.IsReady, output = Path.GetFullPath(output), helmetMerge = CreateHelmetMergePlanProjection(build.Document) },
				() => FormatHelmetMergePlan(build.Document, Path.GetFullPath(output)));
			return build.Document.IsReady ? 0 : 2;
		}
		if (Matches(command, "equipment", "helmet-merge", "validate"))
		{
			EnsureHelmetMergeValidateOptions(invocation.Options);
			var document = await CliHelmetToArmorMergePlanArtifacts.ReadDocumentAsync(RequireHelmetMergePlan(invocation.Options)).ConfigureAwait(false);
			var validation = await CliHelmetToArmorMergePlanArtifacts.ValidateAsync(document, CliWorkspace.Open(invocation.Options)).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = validation.IsValid, validation = CreateHelmetMergeValidationProjection(validation) },
				() => FormatHelmetMergeValidation(validation));
			return validation.IsValid ? 0 : 2;
		}
		if (Matches(command, "equipment", "helmet-merge", "apply"))
		{
			EnsureHelmetMergeApplyOptions(invocation.Options);
			if (!invocation.Options.Confirm)
				throw new CliUserException("HelmetMergeApplyConfirmationRequired", "equipment helmet-merge apply 会写出新的独立 Patch；请在人工确认后显式传入 --confirm。 ");
			var document = await CliHelmetToArmorMergePlanArtifacts.ReadDocumentAsync(RequireHelmetMergePlan(invocation.Options)).ConfigureAwait(false);
			var execution = await CliHelmetToArmorMergeExecutor.ApplyAsync(
				document,
				CliWorkspace.Open(invocation.Options),
				RequireOutput(invocation.Options, "equipment helmet-merge apply"))
				.ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = execution.Success, execution = CreateHelmetMergeExecutionProjection(execution) },
				() => FormatHelmetMergeExecution(execution));
			return execution.Success ? 0 : 2;
		}
		if (command.Length == 4 && command[0] == "equipment" && command[1] == "armor-normalize" && command[2] == "inspect")
		{
			EnsureArmorNormalizeInspectOptions(invocation.Options);
			var build = await CliArmorNormalizationPlanArtifacts.BuildAsync(CliWorkspace.Open(invocation.Options), command[3]).ConfigureAwait(false);
			Emit(invocation.Options, new { ok = build.Document.IsReady, inspection = CreateArmorNormalizationPlanProjection(build.Document) }, () => FormatArmorNormalizationInspection(build.Document));
			return 0;
		}
		if (command.Length == 4 && command[0] == "equipment" && command[1] == "armor-normalize" && command[2] == "plan")
		{
			EnsureArmorNormalizePlanOptions(invocation.Options);
			var output = RequireOutput(invocation.Options, "equipment armor-normalize plan");
			var build = await CliArmorNormalizationPlanArtifacts.BuildAsync(CliWorkspace.Open(invocation.Options), command[3]).ConfigureAwait(false);
			await CliOutput.WriteExplicitFileAsync(output, CliOutput.ToJson(build.Document), invocation.Options.Overwrite).ConfigureAwait(false);
			Emit(invocation.Options, new { ok = build.Document.IsReady, output = Path.GetFullPath(output), armorNormalize = CreateArmorNormalizationPlanProjection(build.Document) }, () => FormatArmorNormalizationPlan(build.Document, Path.GetFullPath(output)));
			return build.Document.IsReady ? 0 : 2;
		}
		if (Matches(command, "equipment", "armor-normalize", "validate"))
		{
			EnsureArmorNormalizeValidateOptions(invocation.Options);
			var document = await CliArmorNormalizationPlanArtifacts.ReadDocumentAsync(RequireArmorNormalizePlan(invocation.Options)).ConfigureAwait(false);
			var validation = await CliArmorNormalizationPlanArtifacts.ValidateAsync(document, CliWorkspace.Open(invocation.Options)).ConfigureAwait(false);
			Emit(invocation.Options, new { ok = validation.IsValid, validation = CreateArmorNormalizationValidationProjection(validation) }, () => FormatArmorNormalizationValidation(validation));
			return validation.IsValid ? 0 : 2;
		}
		if (Matches(command, "equipment", "armor-normalize", "apply"))
		{
			EnsureArmorNormalizeApplyOptions(invocation.Options);
			if (!invocation.Options.Confirm)
				throw new CliUserException("ArmorNormalizeApplyConfirmationRequired", "equipment armor-normalize apply 会写出新的独立 Mod；请审阅计划后显式传入 --confirm。 ");
			var document = await CliArmorNormalizationPlanArtifacts.ReadDocumentAsync(RequireArmorNormalizePlan(invocation.Options)).ConfigureAwait(false);
			var execution = await CliArmorNormalizationExecutor.ApplyAsync(document, CliWorkspace.Open(invocation.Options), RequireOutput(invocation.Options, "equipment armor-normalize apply")).ConfigureAwait(false);
			Emit(invocation.Options, new { ok = execution.Success, execution }, () => $"{(execution.Success ? "护甲规整输出完成" : "护甲规整输出失败")}：{execution.OutputDirectory ?? "未发布"}；Patch {execution.ProcessedPatches}；重建 {execution.RebuiltUnits}；隐藏 {execution.HiddenUnits}。");
			return execution.Success ? 0 : 2;
		}
		if (command.Length >= 4 && command[0] == "equipment" && command[1] == "cross-armor" && command[2] == "inspect")
		{
			EnsureCrossArmorInspectOptions(invocation.Options);
			if (command.Length != 4)
				throw new CliUserException("CrossArmorSourceRequired", "equipment cross-armor inspect 只接受一个来源 Mod GUID 或唯一前缀。 ");
			var build = await CliCrossArmorTransferPlanArtifacts
				.BuildAsync(CliWorkspace.Open(invocation.Options), command[3])
				.ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = build.Document.IsReady, inspection = CreateCrossArmorPlanProjection(build.Document) },
				() => FormatCrossArmorInspection(build.Document));
			return 0;
		}
		if (command.Length >= 4 && command[0] == "equipment" && command[1] == "cross-armor" && command[2] == "plan")
		{
			EnsureCrossArmorPlanOptions(invocation.Options);
			if (command.Length != 4)
				throw new CliUserException("CrossArmorSourceRequired", "equipment cross-armor plan 只接受一个来源 Mod GUID 或唯一前缀。 ");
			var output = RequireOutput(invocation.Options, "equipment cross-armor plan");
			var build = await CliCrossArmorTransferPlanArtifacts
				.BuildAsync(CliWorkspace.Open(invocation.Options), command[3])
				.ConfigureAwait(false);
			await CliOutput.WriteExplicitFileAsync(output, CliOutput.ToJson(build.Document), invocation.Options.Overwrite).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = build.Document.IsReady, output = Path.GetFullPath(output), crossArmor = CreateCrossArmorPlanProjection(build.Document) },
				() => FormatCrossArmorPlan(build.Document, Path.GetFullPath(output)));
			return build.Document.IsReady ? 0 : 2;
		}
		if (Matches(command, "equipment", "cross-armor", "validate"))
		{
			EnsureCrossArmorValidateOptions(invocation.Options);
			var document = await CliCrossArmorTransferPlanArtifacts.ReadDocumentAsync(RequireCrossArmorPlan(invocation.Options)).ConfigureAwait(false);
			var validation = await CliCrossArmorTransferPlanArtifacts.ValidateAsync(document, CliWorkspace.Open(invocation.Options)).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = validation.IsValid, validation = CreateCrossArmorValidationProjection(validation) },
				() => FormatCrossArmorValidation(validation));
			return validation.IsValid ? 0 : 2;
		}
		if (Matches(command, "equipment", "cross-armor", "apply"))
		{
			EnsureCrossArmorApplyOptions(invocation.Options);
			if (!invocation.Options.Confirm)
				throw new CliUserException("CrossArmorApplyConfirmationRequired", "equipment cross-armor apply 会写出新的独立 Patch；请在人工确认后显式传入 --confirm。 ");
			var document = await CliCrossArmorTransferPlanArtifacts.ReadDocumentAsync(RequireCrossArmorPlan(invocation.Options)).ConfigureAwait(false);
			var execution = await CliCrossArmorTransferExecutor.ApplyAsync(
				document,
				CliWorkspace.Open(invocation.Options),
				RequireOutput(invocation.Options, "equipment cross-armor apply"))
				.ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = execution.Success, execution = CreateCrossArmorExecutionProjection(execution) },
				() => FormatCrossArmorExecution(execution));
			return execution.Success ? 0 : 2;
		}
		if (command.Length >= 3 && command[0] == "host-derived" && command[1] == "inspect")
		{
			EnsureHostDerivedInspectOptions(invocation.Options);
			var workspace = CliWorkspace.Open(invocation.Options);
			var build = await CliHostDerivedOverwritePlanArtifacts
				.BuildAsync(workspace, CreateHostDerivedRequest(
					CliHostDerivedOperation.Rebuild,
					CliHostDerivedAction.Rebuild,
					invocation.Command.Skip(2).ToArray(),
					Array.Empty<string>(),
					false,
					workspace.GameDataRoot))
				.ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = build.Document.IsReady, inspection = CreateHostDerivedPlanProjection(build.Document) },
				() => FormatHostDerivedInspection(build.Document));
			return 0;
		}
		if (command.Length >= 5 && command[0] == "host-derived" && command[1] == "plan" && command[2] == "decoration")
		{
			var allDeclaredHosts = command[3] is "enable-all" or "disable-all";
			var action = ParseHostDerivedToggleAction(command[3], "装饰");
			EnsureHostDerivedDecorationPlanOptions(invocation.Options, allDeclaredHosts);
			var workspace = CliWorkspace.Open(invocation.Options);
			var build = await CliHostDerivedOverwritePlanArtifacts
				.BuildAsync(workspace, CreateHostDerivedRequest(
					CliHostDerivedOperation.Decoration,
					action,
					allDeclaredHosts ? Array.Empty<string>() : [RequireHostDerivedDecorationHost(invocation.Options)],
					invocation.Command.Skip(4).ToArray(),
					allDeclaredHosts,
					workspace.GameDataRoot))
				.ConfigureAwait(false);
			var output = RequireOutput(invocation.Options, "host-derived plan decoration");
			await CliOutput.WriteExplicitFileAsync(output, CliOutput.ToJson(build.Document), invocation.Options.Overwrite).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = build.Document.IsReady, output = Path.GetFullPath(output), hostDerived = CreateHostDerivedPlanProjection(build.Document) },
				() => FormatHostDerivedPlan(build.Document, Path.GetFullPath(output)));
			return build.Document.IsReady ? 0 : 2;
		}
		if (command.Length >= 5 && command[0] == "host-derived" && command[1] == "plan" && command[2] == "helmet")
		{
			EnsureHostDerivedHelmetPlanOptions(invocation.Options);
			var workspace = CliWorkspace.Open(invocation.Options);
			var build = await CliHostDerivedOverwritePlanArtifacts
				.BuildAsync(workspace, CreateHostDerivedRequest(
					CliHostDerivedOperation.Helmet,
					ParseHostDerivedToggleAction(command[3], "头盔合并"),
					invocation.Command.Skip(4).ToArray(),
					Array.Empty<string>(),
					false,
					workspace.GameDataRoot))
				.ConfigureAwait(false);
			var output = RequireOutput(invocation.Options, "host-derived plan helmet");
			await CliOutput.WriteExplicitFileAsync(output, CliOutput.ToJson(build.Document), invocation.Options.Overwrite).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = build.Document.IsReady, output = Path.GetFullPath(output), hostDerived = CreateHostDerivedPlanProjection(build.Document) },
				() => FormatHostDerivedPlan(build.Document, Path.GetFullPath(output)));
			return build.Document.IsReady ? 0 : 2;
		}
		if (command.Length >= 4 && command[0] == "host-derived" && command[1] == "plan" && command[2] == "rebuild")
		{
			EnsureHostDerivedRebuildPlanOptions(invocation.Options);
			var workspace = CliWorkspace.Open(invocation.Options);
			var build = await CliHostDerivedOverwritePlanArtifacts
				.BuildAsync(workspace, CreateHostDerivedRequest(
					CliHostDerivedOperation.Rebuild,
					CliHostDerivedAction.Rebuild,
					invocation.Command.Skip(3).ToArray(),
					Array.Empty<string>(),
					false,
					workspace.GameDataRoot))
				.ConfigureAwait(false);
			var output = RequireOutput(invocation.Options, "host-derived plan rebuild");
			await CliOutput.WriteExplicitFileAsync(output, CliOutput.ToJson(build.Document), invocation.Options.Overwrite).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = build.Document.IsReady, output = Path.GetFullPath(output), hostDerived = CreateHostDerivedPlanProjection(build.Document) },
				() => FormatHostDerivedPlan(build.Document, Path.GetFullPath(output)));
			return build.Document.IsReady ? 0 : 2;
		}
		if (Matches(command, "host-derived", "validate"))
		{
			EnsureHostDerivedValidateOptions(invocation.Options);
			var document = await CliHostDerivedOverwritePlanArtifacts.ReadDocumentAsync(RequireHostDerivedPlan(invocation.Options)).ConfigureAwait(false);
			var validation = await CliHostDerivedOverwritePlanArtifacts.ValidateAsync(document, CliWorkspace.Open(invocation.Options)).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = validation.IsValid, validation = CreateHostDerivedValidationProjection(validation) },
				() => FormatHostDerivedValidation(validation));
			return validation.IsValid ? 0 : 2;
		}
		if (Matches(command, "host-derived", "apply"))
		{
			EnsureHostDerivedApplyOptions(invocation.Options);
			if (!invocation.Options.Confirm)
				throw new CliUserException("HostDerivedApplyConfirmationRequired", "host-derived apply 会重建库内主体的 Overwrite 并提交相应状态；请在人工确认后显式传入 --confirm。 ");
			var document = await CliHostDerivedOverwritePlanArtifacts.ReadDocumentAsync(RequireHostDerivedPlan(invocation.Options)).ConfigureAwait(false);
			var execution = await CliHostDerivedOverwriteExecutor.ApplyAsync(document, CliWorkspace.Open(invocation.Options)).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = execution.Success, execution = CreateHostDerivedExecutionProjection(execution) },
				() => FormatHostDerivedExecution(execution));
			return execution.Success ? 0 : 2;
		}
		if (command.Length >= 4 && command[0] == "patch" && command[1] == "repair" && command[2] == "batch" && command[3] == "inspect")
		{
			EnsureBatchPatchRepairInspectOptions(invocation.Options);
			var build = await CliBatchPatchRepairPlanArtifacts
				.BuildAsync(CliWorkspace.Open(invocation.Options), invocation.Command.Skip(4).ToArray())
				.ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = build.Document.IsReady, inspection = CreateBatchPatchRepairPlanProjection(build.Document) },
				() => FormatBatchPatchRepairInspection(build.Document));
			return build.Document.IsReady ? 0 : 2;
		}
		if (command.Length >= 4 && command[0] == "patch" && command[1] == "repair" && command[2] == "batch" && command[3] == "plan")
		{
			EnsureBatchPatchRepairPlanOptions(invocation.Options);
			var output = RequireOutput(invocation.Options, "patch repair batch plan");
			var build = await CliBatchPatchRepairPlanArtifacts
				.BuildAsync(CliWorkspace.Open(invocation.Options), invocation.Command.Skip(4).ToArray())
				.ConfigureAwait(false);
			await CliOutput.WriteExplicitFileAsync(output, CliOutput.ToJson(build.Document), invocation.Options.Overwrite).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = build.Document.IsReady, output = Path.GetFullPath(output), repair = CreateBatchPatchRepairPlanProjection(build.Document) },
				() => FormatBatchPatchRepairPlan(build.Document, Path.GetFullPath(output)));
			return build.Document.IsReady ? 0 : 2;
		}
		if (Matches(command, "patch", "repair", "batch", "validate"))
		{
			EnsureBatchPatchRepairValidateOptions(invocation.Options);
			var document = await CliBatchPatchRepairPlanArtifacts.ReadDocumentAsync(RequireBatchPatchRepairPlan(invocation.Options)).ConfigureAwait(false);
			var validation = await CliBatchPatchRepairPlanArtifacts.ValidateAsync(document, CliWorkspace.Open(invocation.Options)).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = validation.IsValid, validation = CreateBatchPatchRepairValidationProjection(validation) },
				() => FormatBatchPatchRepairValidation(validation));
			return validation.IsValid ? 0 : 2;
		}
		if (Matches(command, "patch", "repair", "batch", "apply"))
		{
			EnsureBatchPatchRepairApplyOptions(invocation.Options);
			if (!invocation.Options.Confirm)
				throw new CliUserException("BatchRepairApplyConfirmationRequired", "patch repair batch apply 会备份并原地替换计划中选中的 Mod Patch；请在审阅计划后显式传入 --confirm。 ");
			var document = await CliBatchPatchRepairPlanArtifacts.ReadDocumentAsync(RequireBatchPatchRepairPlan(invocation.Options)).ConfigureAwait(false);
			var execution = await CliBatchPatchRepairExecutor.ApplyAsync(document, CliWorkspace.Open(invocation.Options)).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = execution.Success, execution = CreateBatchPatchRepairExecutionProjection(execution) },
				() => FormatBatchPatchRepairExecution(execution));
			return execution.Success ? 0 : 2;
		}
		if (command.Length is 4 or 5 && command[0] == "patch" && command[1] == "repair" && command[2] == "inspect")
		{
			EnsureSameKeyInspectOptions(invocation.Options);
			var inspection = await CliSameKeyReconstructionPlanArtifacts
				.InspectAsync(CliWorkspace.Open(invocation.Options), invocation.Command[3], command.Length == 5 ? invocation.Command[4] : null)
				.ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = inspection.Inspection.Summary?.CanAttemptGenerate == true && !inspection.Inspection.Diagnostics.Any(diagnostic => diagnostic.Blocking), inspection = CreateSameKeyInspectionProjection(inspection.Inspection) },
				() => FormatSameKeyInspection(inspection.Inspection));
			return 0;
		}
		if (command.Length == 5 && command[0] == "patch" && command[1] == "repair" && command[2] == "plan")
		{
			EnsureSameKeyPlanOptions(invocation.Options);
			var output = RequireOutput(invocation.Options, "patch repair plan");
			var build = await CliSameKeyReconstructionPlanArtifacts
				.BuildAsync(CliWorkspace.Open(invocation.Options), invocation.Command[3], invocation.Command[4])
				.ConfigureAwait(false);
			await CliOutput.WriteExplicitFileAsync(output, CliOutput.ToJson(build.Document), invocation.Options.Overwrite).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = build.Document.IsReady, output = Path.GetFullPath(output), repair = CreateSameKeyPlanProjection(build.Document) },
				() => FormatSameKeyPlan(build.Document, Path.GetFullPath(output)));
			return build.Document.IsReady ? 0 : 2;
		}
		if (Matches(command, "patch", "repair", "validate"))
		{
			EnsureSameKeyValidateOptions(invocation.Options);
			var document = await CliSameKeyReconstructionPlanArtifacts.ReadDocumentAsync(RequireSameKeyPlan(invocation.Options)).ConfigureAwait(false);
			var validation = await CliSameKeyReconstructionPlanArtifacts.ValidateAsync(document, CliWorkspace.Open(invocation.Options)).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = validation.IsValid, validation = CreateSameKeyValidationProjection(validation) },
				() => FormatSameKeyValidation(validation));
			return validation.IsValid ? 0 : 2;
		}
		if (Matches(command, "patch", "repair", "apply"))
		{
			EnsureSameKeyApplyOptions(invocation.Options);
			if (!invocation.Options.Confirm)
				throw new CliUserException("SameKeyApplyConfirmationRequired", "patch repair apply 会写出一个新的 Same-key 候选 Patch；请在人工确认后显式传入 --confirm。 ");
			var document = await CliSameKeyReconstructionPlanArtifacts.ReadDocumentAsync(RequireSameKeyPlan(invocation.Options)).ConfigureAwait(false);
			var execution = await CliSameKeyReconstructionExecutor.ApplyAsync(document, CliWorkspace.Open(invocation.Options), RequireOutput(invocation.Options, "patch repair apply")).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = execution.Success, execution = CreateSameKeyExecutionProjection(execution) },
				() => FormatSameKeyExecution(execution));
			return execution.Success ? 0 : 2;
		}
		if (command.Length >= 4 && command[0] == "import" && command[1] == "batch" && command[2] == "inspect")
		{
			EnsureBatchImportInspectOptions(invocation.Options);
			var build = await CliBatchImportPlanArtifacts
				.BuildAsync(invocation.Command.Skip(3).ToArray(), addToProfile: null)
				.ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = true, batch = CreateBatchImportPlanProjection(build.Document) },
				() => FormatBatchImportInspection(build.Document));
			return 0;
		}
		if (command.Length >= 4 && command[0] == "import" && command[1] == "batch" && command[2] == "plan")
		{
			EnsureBatchImportPlanOptions(invocation.Options);
			var output = RequireOutput(invocation.Options, "import batch plan");
			var profileTarget = await ResolveImportProfileTargetAsync(invocation.Options).ConfigureAwait(false);
			var build = await CliBatchImportPlanArtifacts
				.BuildAsync(invocation.Command.Skip(3).ToArray(), profileTarget)
				.ConfigureAwait(false);
			await CliOutput.WriteExplicitFileAsync(output, CliOutput.ToJson(build.Document), invocation.Options.Overwrite).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = true, output = Path.GetFullPath(output), batch = CreateBatchImportPlanProjection(build.Document) },
				() => FormatBatchImportPlan(build.Document, Path.GetFullPath(output)));
			return 0;
		}
		if (Matches(command, "import", "batch", "validate"))
		{
			EnsureBatchImportValidateOptions(invocation.Options);
			var document = await CliBatchImportPlanArtifacts.ReadDocumentAsync(RequireBatchImportPlan(invocation.Options)).ConfigureAwait(false);
			var validation = await CliBatchImportPlanArtifacts.ValidateAsync(document, CliWorkspace.Open(invocation.Options)).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = validation.IsValid, validation = CreateBatchImportValidationProjection(validation) },
				() => FormatBatchImportValidation(validation));
			return validation.IsValid ? 0 : 2;
		}
		if (Matches(command, "import", "batch", "apply"))
		{
			EnsureBatchImportApplyOptions(invocation.Options);
			if (!invocation.Options.Confirm)
				throw new CliUserException("BatchImportApplyConfirmationRequired", "import batch apply 会依次写入多个 Mod；请在人工确认后显式传入 --confirm。 ");
			var document = await CliBatchImportPlanArtifacts.ReadDocumentAsync(RequireBatchImportPlan(invocation.Options)).ConfigureAwait(false);
			var execution = await CliBatchImportExecutor.ApplyAsync(document, CliWorkspace.Open(invocation.Options)).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = execution.Success, execution = CreateBatchImportExecutionProjection(execution) },
				() => FormatBatchImportExecution(execution));
			return execution.Success ? 0 : execution.Status == "validation-failed" ? 2 : 1;
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
			var profileTarget = await ResolveImportProfileTargetAsync(invocation.Options).ConfigureAwait(false);
			var document = CliImportPlanArtifacts.CreateDocument(inspection, profileTarget);
			await CliOutput.WriteExplicitFileAsync(output, CliOutput.ToJson(document), invocation.Options.Overwrite).ConfigureAwait(false);
			Emit(invocation.Options,
				new { ok = true, output = Path.GetFullPath(output), schema = document.Schema, version = document.Version, sourceFingerprint = document.SourceFingerprint, importFingerprint = document.ImportFingerprint, addToProfile = document.AddToProfile },
				() => $"已导出可验证导入计划：{Path.GetFullPath(output)}" + (document.AddToProfile is null ? string.Empty : $"；导入后将加入配置：{document.AddToProfile.ProfileName}。"));
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
				CliImportProfileAddition? profileAddition = null;
				try
				{
					profileAddition = await CliImportProfileIntegration
						.AddAsync(paths, document.AddToProfile, GetImportedNodeIds(result))
						.ConfigureAwait(false);
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
				{
					Emit(invocation.Options,
						new
						{
							ok = false,
							status = "post-commit-incomplete",
							partial = CreateImportExecutionProjection(result, libraryRoot, profileAddition),
							error = new { code = "ImportProfilePostCommitIncomplete", message = exception.Message },
						},
						() => $"Mod 文件已导入，但导入后加入配置未完成：{exception.Message}");
					return 1;
				}
				Emit(invocation.Options,
					new { ok = true, applied = CreateImportExecutionProjection(result, libraryRoot, profileAddition) },
					() => FormatImportExecution(result, libraryRoot, profileAddition));
				return 0;
			}
			catch (ModImportPostCommitException exception)
			{
				Emit(invocation.Options,
					new { ok = false, status = "post-commit-incomplete", partial = CreateImportExecutionProjection(exception.PartialResult, libraryRoot, null), error = new { code = "ImportPostCommitIncomplete", message = exception.InnerException?.Message ?? exception.Message } },
					() => $"Mod 文件已导入，但默认选项状态未完成：{exception.InnerException?.Message ?? exception.Message}");
				return 1;
			}
			catch (LibraryWriteLeaseUnavailableException exception)
			{
				throw new CliUserException("LibraryWriteBusy", $"目标 Mod 库正被另一条 CLI 写入操作占用：{exception.LibraryRoot}。请等待当前操作完成后重试。");
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or DirectoryNotFoundException or FileNotFoundException)
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

	private static void EnsureArmorRegistryReadOptions(CliGlobalOptions options, string command)
	{
		if (!string.IsNullOrWhiteSpace(options.ManagerRoot)
			|| !string.IsNullOrWhiteSpace(options.Library)
			|| !string.IsNullOrWhiteSpace(options.GameData)
			|| !string.IsNullOrWhiteSpace(options.Output)
			|| !string.IsNullOrWhiteSpace(options.Plan)
			|| options.Overwrite
			|| options.Confirm
			|| HasUnrelatedArmorRegistryOptions(options))
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				$"{command} 仅接受 --registry-root 与 --json。");
		}
	}

	private static void EnsureArmorRegistryPlanOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.ManagerRoot)
			|| !string.IsNullOrWhiteSpace(options.Library)
			|| !string.IsNullOrWhiteSpace(options.GameData)
			|| !string.IsNullOrWhiteSpace(options.Plan)
			|| options.Confirm
			|| HasUnrelatedArmorRegistryOptions(options))
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"armor-registry plan 仅接受规格文件、--registry-root、--output、--overwrite 与 --json。");
		}
	}

	private static void EnsureArmorRegistryValidateOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.ManagerRoot)
			|| !string.IsNullOrWhiteSpace(options.Library)
			|| !string.IsNullOrWhiteSpace(options.GameData)
			|| !string.IsNullOrWhiteSpace(options.Output)
			|| options.Overwrite
			|| options.Confirm
			|| HasUnrelatedArmorRegistryOptions(options))
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"armor-registry validate 仅接受 --plan、--registry-root 与 --json。");
		}
	}

	private static void EnsureArmorRegistryApplyOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.ManagerRoot)
			|| !string.IsNullOrWhiteSpace(options.Library)
			|| !string.IsNullOrWhiteSpace(options.GameData)
			|| !string.IsNullOrWhiteSpace(options.Output)
			|| options.Overwrite
			|| HasUnrelatedArmorRegistryOptions(options))
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"armor-registry apply 仅接受 --plan、--registry-root、--confirm 与 --json。");
		}
	}

	private static bool HasUnrelatedArmorRegistryOptions(CliGlobalOptions options)
		=> !string.IsNullOrWhiteSpace(options.Host)
			|| !string.IsNullOrWhiteSpace(options.AddToProfile)
			|| options.Position is not null
			|| options.Limit is not null
			|| !string.IsNullOrWhiteSpace(options.Cursor)
			|| options.SetActive
			|| options.WithUnsharedOptions
			|| options.WithEffective
			|| options.OnlyCompetition
			|| options.AllowUnsafeLua;

	private static string RequireArmorRegistryPlan(CliGlobalOptions options)
		=> string.IsNullOrWhiteSpace(options.Plan)
			? throw new CliUserException("ArmorRegistryPlanRequired", "请通过 --plan <护甲注册计划.json> 提供计划文件。")
			: options.Plan;

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

	private static void EnsureBatchImportInspectOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || !string.IsNullOrWhiteSpace(options.Plan) || options.Confirm
			|| !string.IsNullOrWhiteSpace(options.Host) || options.Position is not null || options.SetActive || options.WithUnsharedOptions
			|| !string.IsNullOrWhiteSpace(options.GameData))
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"import batch inspect 仅接受一个或多个来源路径与 --json。 ");
		}
	}

	private static void EnsureBatchImportPlanOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Plan) || options.Confirm || !string.IsNullOrWhiteSpace(options.Host)
			|| options.Position is not null || options.SetActive || options.WithUnsharedOptions || !string.IsNullOrWhiteSpace(options.GameData))
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"import batch plan 仅接受一个或多个来源路径、--manager-root、--library、可选 --add-to-profile、--output、--overwrite 与 --json。 ");
		}
	}

	private static void EnsureBatchImportValidateOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || options.Confirm || !string.IsNullOrWhiteSpace(options.Host)
			|| options.Position is not null || options.SetActive || options.WithUnsharedOptions || !string.IsNullOrWhiteSpace(options.GameData))
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"import batch validate 仅接受 --plan、--manager-root、--library 与 --json。 ");
		}
	}

	private static void EnsureBatchImportApplyOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || !string.IsNullOrWhiteSpace(options.Host)
			|| options.Position is not null || options.SetActive || options.WithUnsharedOptions || !string.IsNullOrWhiteSpace(options.GameData))
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"import batch apply 仅接受 --plan、--manager-root、--library、--confirm 与 --json。 ");
		}
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

	private static void EnsureLibrarySyncInspectOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || !string.IsNullOrWhiteSpace(options.Plan) || options.Confirm
			|| !string.IsNullOrWhiteSpace(options.Host) || options.Position is not null || options.SetActive || options.WithUnsharedOptions || !string.IsNullOrWhiteSpace(options.GameData))
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"library sync inspect 仅接受 --manager-root、--library 与 --json。 ");
		}
	}

	private static void EnsureLibrarySyncPlanOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Plan) || options.Confirm || !string.IsNullOrWhiteSpace(options.Host)
			|| options.Position is not null || options.SetActive || options.WithUnsharedOptions || !string.IsNullOrWhiteSpace(options.GameData))
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"library sync plan 仅接受 --manager-root、--library、--output、--overwrite 与 --json。 ");
		}
	}

	private static void EnsureLibrarySyncValidateOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || options.Confirm || !string.IsNullOrWhiteSpace(options.Host)
			|| options.Position is not null || options.SetActive || options.WithUnsharedOptions || !string.IsNullOrWhiteSpace(options.GameData))
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"library sync validate 仅接受 --plan、--manager-root、--library 与 --json。 ");
		}
	}

	private static void EnsureLibrarySyncApplyOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || !string.IsNullOrWhiteSpace(options.Host)
			|| options.Position is not null || options.SetActive || options.WithUnsharedOptions || !string.IsNullOrWhiteSpace(options.GameData))
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"library sync apply 仅接受 --plan、--manager-root、--library、--confirm 与 --json。 ");
		}
	}

	private static void EnsureDecorationSourceInspectOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || !string.IsNullOrWhiteSpace(options.Plan) || options.Confirm
			|| !string.IsNullOrWhiteSpace(options.Host) || options.Position is not null || options.SetActive || options.WithUnsharedOptions)
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"decoration inspect source 仅接受来源 Mod 标识、--manager-root、--library、--game-data 与 --json。 ");
		}
	}

	private static void EnsureDecorationShowOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || !string.IsNullOrWhiteSpace(options.Plan) || options.Confirm
			|| !string.IsNullOrWhiteSpace(options.Host) || options.Position is not null || options.SetActive || options.WithUnsharedOptions || !string.IsNullOrWhiteSpace(options.GameData))
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"decoration show 仅接受装饰 Mod 标识、--manager-root、--library 与 --json。 ");
		}
	}

	private static void EnsureDecorationPackagePlanOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Plan) || options.Confirm || options.Position is not null || options.SetActive || options.WithUnsharedOptions)
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"decoration package plan 仅接受来源 Mod 标识、--host、--manager-root、--library、--game-data、--output、--overwrite 与 --json。 ");
		}
		_ = RequireHost(options);
	}

	private static void EnsureDecorationPackageValidateOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || options.Confirm || !string.IsNullOrWhiteSpace(options.Host)
			|| options.Position is not null || options.SetActive || options.WithUnsharedOptions)
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"decoration package validate 仅接受 --plan、--manager-root、--library、--game-data 与 --json。 ");
		}
	}

	private static void EnsureDecorationPackageApplyOptions(CliGlobalOptions options)
	{
		if (options.Overwrite || !string.IsNullOrWhiteSpace(options.Host) || options.Position is not null || options.SetActive || options.WithUnsharedOptions)
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"decoration package apply 仅接受 --plan、--manager-root、--library、--game-data、--output、--confirm 与 --json。输出必须是新的库外且游戏目录外目录。 ");
		}
	}

	private static void EnsureDecorationEditExportOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Plan) || options.Confirm || !string.IsNullOrWhiteSpace(options.Host)
			|| options.Position is not null || options.SetActive || options.WithUnsharedOptions)
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"decoration edit export 仅接受装饰 Mod 标识、--manager-root、--library、--game-data、--output、--overwrite 与 --json。 ");
		}
	}

	private static void EnsureDecorationEditInspectOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || !string.IsNullOrWhiteSpace(options.Plan) || options.Confirm
			|| !string.IsNullOrWhiteSpace(options.Host) || options.Position is not null || options.SetActive || options.WithUnsharedOptions)
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"decoration edit inspect 仅接受编辑规格文件、--manager-root、--library、--game-data 与 --json。 ");
		}
	}

	private static void EnsureDecorationEditPlanOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Plan) || options.Confirm || !string.IsNullOrWhiteSpace(options.Host)
			|| options.Position is not null || options.SetActive || options.WithUnsharedOptions)
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"decoration edit plan 仅接受编辑规格文件、--manager-root、--library、--game-data、--output、--overwrite 与 --json。 ");
		}
	}

	private static void EnsureDecorationEditValidateOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || options.Confirm || !string.IsNullOrWhiteSpace(options.Host)
			|| options.Position is not null || options.SetActive || options.WithUnsharedOptions)
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"decoration edit validate 仅接受 --plan、--manager-root、--library、--game-data 与 --json。 ");
		}
	}

	private static void EnsureDecorationEditApplyOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || !string.IsNullOrWhiteSpace(options.Host)
			|| options.Position is not null || options.SetActive || options.WithUnsharedOptions)
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"decoration edit apply 仅接受 --plan、--manager-root、--library、--game-data、--confirm 与 --json。 ");
		}
	}

	private static void EnsurePatchRepackagingInspectOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || !string.IsNullOrWhiteSpace(options.Plan) || options.Confirm
			|| !string.IsNullOrWhiteSpace(options.Host) || options.Position is not null || options.SetActive || options.WithUnsharedOptions || !string.IsNullOrWhiteSpace(options.GameData))
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"patch repackage inspect 仅接受 Mod 标识、--library/--manager-root 与 --json。 ");
		}
	}

	private static void EnsurePatchRepackagingPlanOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Plan) || options.Confirm || !string.IsNullOrWhiteSpace(options.Host)
			|| options.Position is not null || options.SetActive || options.WithUnsharedOptions || !string.IsNullOrWhiteSpace(options.GameData))
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"patch repackage plan 仅接受 Mod 标识、--library/--manager-root、--output、--overwrite 与 --json。 ");
		}
	}

	private static void EnsurePatchRepackagingValidateOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || options.Confirm || !string.IsNullOrWhiteSpace(options.Host)
			|| options.Position is not null || options.SetActive || options.WithUnsharedOptions || !string.IsNullOrWhiteSpace(options.GameData))
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"patch repackage validate 仅接受 --plan、--library/--manager-root 与 --json。 ");
		}
	}

	private static void EnsurePatchRepackagingApplyOptions(CliGlobalOptions options)
	{
		if (options.Overwrite || !string.IsNullOrWhiteSpace(options.Host) || options.Position is not null || options.SetActive || options.WithUnsharedOptions || !string.IsNullOrWhiteSpace(options.GameData))
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"patch repackage apply 仅接受 --plan、--library/--manager-root、--output、--confirm 与 --json。输出必须是一个新的目录。 ");
		}
	}

	private static void EnsureHelmetMergeInspectOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || !string.IsNullOrWhiteSpace(options.Plan) || options.Confirm
			|| !string.IsNullOrWhiteSpace(options.Host) || options.Position is not null || options.SetActive || options.WithUnsharedOptions)
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"equipment helmet-merge inspect 仅接受 Mod 标识、--manager-root、--library、--game-data 与 --json。 ");
		}
	}

	private static void EnsureHelmetMergePlanOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Plan) || options.Confirm || !string.IsNullOrWhiteSpace(options.Host)
			|| options.Position is not null || options.SetActive || options.WithUnsharedOptions)
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"equipment helmet-merge plan 仅接受 Mod 标识、--manager-root、--library、--game-data、--output、--overwrite 与 --json。 ");
		}
	}

	private static void EnsureHelmetMergeValidateOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || options.Confirm || !string.IsNullOrWhiteSpace(options.Host)
			|| options.Position is not null || options.SetActive || options.WithUnsharedOptions)
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"equipment helmet-merge validate 仅接受 --plan、--manager-root、--library、--game-data 与 --json。 ");
		}
	}

	private static void EnsureHelmetMergeApplyOptions(CliGlobalOptions options)
	{
		if (options.Overwrite || !string.IsNullOrWhiteSpace(options.Host) || options.Position is not null || options.SetActive || options.WithUnsharedOptions)
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"equipment helmet-merge apply 仅接受 --plan、--manager-root、--library、--game-data、--output、--confirm 与 --json。输出必须是一个新的库外且游戏目录外的目录。 ");
		}
	}

	private static void EnsureArmorNormalizeInspectOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || !string.IsNullOrWhiteSpace(options.Plan) || options.Confirm || !string.IsNullOrWhiteSpace(options.Host) || options.Position is not null || options.SetActive || options.WithUnsharedOptions)
			throw new CliUserException("CommandOptionNotSupported", "equipment armor-normalize inspect 仅接受 Mod 标识、--manager-root、--library、--game-data 与 --json。 ");
	}
	private static void EnsureArmorNormalizePlanOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Plan) || options.Confirm || !string.IsNullOrWhiteSpace(options.Host) || options.Position is not null || options.SetActive || options.WithUnsharedOptions)
			throw new CliUserException("CommandOptionNotSupported", "equipment armor-normalize plan 仅接受 Mod 标识、--manager-root、--library、--game-data、--output、--overwrite 与 --json。 ");
	}
	private static void EnsureArmorNormalizeValidateOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || options.Confirm || !string.IsNullOrWhiteSpace(options.Host) || options.Position is not null || options.SetActive || options.WithUnsharedOptions)
			throw new CliUserException("CommandOptionNotSupported", "equipment armor-normalize validate 仅接受 --plan、--manager-root、--library、--game-data 与 --json。 ");
	}
	private static void EnsureArmorNormalizeApplyOptions(CliGlobalOptions options)
	{
		if (options.Overwrite || !string.IsNullOrWhiteSpace(options.Host) || options.Position is not null || options.SetActive || options.WithUnsharedOptions)
			throw new CliUserException("CommandOptionNotSupported", "equipment armor-normalize apply 仅接受 --plan、--manager-root、--library、--game-data、--output、--confirm 与 --json。 ");
	}

	private static void EnsureCrossArmorInspectOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || !string.IsNullOrWhiteSpace(options.Plan) || options.Confirm
			|| !string.IsNullOrWhiteSpace(options.Host) || options.Position is not null || options.SetActive || options.WithUnsharedOptions)
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"equipment cross-armor inspect 仅接受 Mod 标识、--manager-root、--library、--game-data 与 --json。 ");
		}
	}

	private static void EnsureCrossArmorPlanOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Plan) || options.Confirm || !string.IsNullOrWhiteSpace(options.Host)
			|| options.Position is not null || options.SetActive || options.WithUnsharedOptions)
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"equipment cross-armor plan 仅接受 Mod 标识、--manager-root、--library、--game-data、--output、--overwrite 与 --json。来源/目标/手工映射在导出的 request 中审阅与编辑。 ");
		}
	}

	private static void EnsureCrossArmorValidateOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || options.Confirm || !string.IsNullOrWhiteSpace(options.Host)
			|| options.Position is not null || options.SetActive || options.WithUnsharedOptions)
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"equipment cross-armor validate 仅接受 --plan、--manager-root、--library、--game-data 与 --json。 ");
		}
	}

	private static void EnsureCrossArmorApplyOptions(CliGlobalOptions options)
	{
		if (options.Overwrite || !string.IsNullOrWhiteSpace(options.Host) || options.Position is not null || options.SetActive || options.WithUnsharedOptions)
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"equipment cross-armor apply 仅接受 --plan、--manager-root、--library、--game-data、--output、--confirm 与 --json。输出必须是一个新的库外且游戏目录外的目录。 ");
		}
	}

	private static void EnsureHostDerivedInspectOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || !string.IsNullOrWhiteSpace(options.Plan) || options.Confirm
			|| !string.IsNullOrWhiteSpace(options.Host) || options.Position is not null || options.SetActive || options.WithUnsharedOptions)
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"host-derived inspect 仅接受主体标识、--manager-root、--library、--game-data 与 --json。 ");
		}
	}

	private static void EnsureHostDerivedDecorationPlanOptions(CliGlobalOptions options, bool allDeclaredHosts)
	{
		EnsureHostDerivedPlanCommonOptions(options, "host-derived plan decoration");
		if (allDeclaredHosts)
		{
			if (!string.IsNullOrWhiteSpace(options.Host))
			{
				throw new CliUserException(
					"HostDerivedDecorationTargetAmbiguous",
					"host-derived plan decoration enable-all/disable-all 不能同时提供 --host。 ");
			}
			return;
		}
		_ = RequireHostDerivedDecorationHost(options);
	}

	private static void EnsureHostDerivedHelmetPlanOptions(CliGlobalOptions options)
	{
		EnsureHostDerivedPlanCommonOptions(options, "host-derived plan helmet");
		if (!string.IsNullOrWhiteSpace(options.Host))
			throw new CliUserException("CommandOptionNotSupported", "host-derived plan helmet 使用位置参数指定主体，不接受 --host。 ");
	}

	private static void EnsureHostDerivedRebuildPlanOptions(CliGlobalOptions options)
	{
		EnsureHostDerivedPlanCommonOptions(options, "host-derived plan rebuild");
		if (!string.IsNullOrWhiteSpace(options.Host))
			throw new CliUserException("CommandOptionNotSupported", "host-derived plan rebuild 使用位置参数指定主体，不接受 --host。 ");
	}

	private static void EnsureHostDerivedPlanCommonOptions(CliGlobalOptions options, string command)
	{
		if (!string.IsNullOrWhiteSpace(options.Plan) || options.Confirm || options.Position is not null || options.SetActive || options.WithUnsharedOptions)
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				$"{command} 仅接受目标标识、--manager-root、--library、--game-data、--output、--overwrite 与 --json。 ");
		}
	}

	private static void EnsureHostDerivedValidateOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || options.Confirm || !string.IsNullOrWhiteSpace(options.Host)
			|| options.Position is not null || options.SetActive || options.WithUnsharedOptions)
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"host-derived validate 仅接受 --plan、--manager-root、--library、--game-data 与 --json。 ");
		}
	}

	private static void EnsureHostDerivedApplyOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || !string.IsNullOrWhiteSpace(options.Host)
			|| options.Position is not null || options.SetActive || options.WithUnsharedOptions)
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"host-derived apply 仅接受 --plan、--manager-root、--library、--game-data、--confirm 与 --json。 ");
		}
	}

	private static void EnsureModExportPlanOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Plan) || options.Confirm || !string.IsNullOrWhiteSpace(options.Host)
			|| options.Position is not null || options.SetActive || options.WithUnsharedOptions || !string.IsNullOrWhiteSpace(options.GameData))
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"mod export plan 仅接受根 Mod 标识、--manager-root、--library、--output、--overwrite 与 --json。 ");
		}
	}

	private static void EnsureModExportValidateOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || options.Confirm || !string.IsNullOrWhiteSpace(options.Host)
			|| options.Position is not null || options.SetActive || options.WithUnsharedOptions || !string.IsNullOrWhiteSpace(options.GameData))
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"mod export validate 仅接受 --plan、--manager-root、--library 与 --json。 ");
		}
	}

	private static void EnsureModExportApplyOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Host) || options.Position is not null || options.SetActive || options.WithUnsharedOptions
			|| !string.IsNullOrWhiteSpace(options.GameData))
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"mod export apply 仅接受 --plan、--manager-root、--library、--output、--overwrite、--confirm 与 --json。 ");
		}
	}

	private static void EnsureGameDataReadOnlyOptions(
		CliGlobalOptions options,
		string command,
		bool allowPaging = false,
		bool allowWithEffective = false,
		bool allowOnlyCompetition = false)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || !string.IsNullOrWhiteSpace(options.Plan) || options.Confirm
			|| !string.IsNullOrWhiteSpace(options.Host) || options.Position is not null || options.SetActive || options.WithUnsharedOptions)
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				$"{command} 仅接受位置参数、--manager-root、可选 --library、--game-data 与 --json。 ");
		}
		if (!allowPaging && (options.Limit is not null || !string.IsNullOrWhiteSpace(options.Cursor)))
			throw new CliUserException("CommandOptionNotSupported", $"{command} 不接受 --limit 或 --cursor。 ");
		if (!allowWithEffective && options.WithEffective)
			throw new CliUserException("CommandOptionNotSupported", $"{command} 不接受 --with-effective。 ");
		if (!allowOnlyCompetition && options.OnlyCompetition)
			throw new CliUserException("CommandOptionNotSupported", $"{command} 不接受 --only-competition。 ");
	}

	private static void EnsureGameDataIndexInspectOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || !string.IsNullOrWhiteSpace(options.Plan) || options.Confirm
			|| !string.IsNullOrWhiteSpace(options.Host) || options.Position is not null || options.SetActive || options.WithUnsharedOptions
			|| options.Limit is not null || !string.IsNullOrWhiteSpace(options.Cursor) || options.WithEffective || options.OnlyCompetition)
		{
			throw new CliUserException("CommandOptionNotSupported", "gamedata index inspect 仅接受 --manager-root、可选 --library、--game-data 与 --json。 ");
		}
	}

	private static void EnsureGameDataIndexPlanOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Plan) || options.Confirm || !string.IsNullOrWhiteSpace(options.Host)
			|| options.Position is not null || options.SetActive || options.WithUnsharedOptions
			|| options.Limit is not null || !string.IsNullOrWhiteSpace(options.Cursor) || options.WithEffective || options.OnlyCompetition)
		{
			throw new CliUserException("CommandOptionNotSupported", "gamedata index plan 仅接受 rebuild、units-all 或 units-missing、--manager-root、可选 --library、--game-data、--output、--overwrite 与 --json。 ");
		}
	}

	private static void EnsureGameDataIndexValidateOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || options.Confirm || !string.IsNullOrWhiteSpace(options.Host)
			|| options.Position is not null || options.SetActive || options.WithUnsharedOptions
			|| options.Limit is not null || !string.IsNullOrWhiteSpace(options.Cursor) || options.WithEffective || options.OnlyCompetition)
		{
			throw new CliUserException("CommandOptionNotSupported", "gamedata index validate 仅接受 --plan、--manager-root、可选 --library、--game-data 与 --json。 ");
		}
	}

	private static void EnsureGameDataIndexApplyOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || !string.IsNullOrWhiteSpace(options.Host)
			|| options.Position is not null || options.SetActive || options.WithUnsharedOptions
			|| options.Limit is not null || !string.IsNullOrWhiteSpace(options.Cursor) || options.WithEffective || options.OnlyCompetition)
		{
			throw new CliUserException("CommandOptionNotSupported", "gamedata index apply 仅接受 --plan、--manager-root、可选 --library、--game-data、--confirm 与 --json。 ");
		}
	}

	private static void EnsureAssetMetadataInspectOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || !string.IsNullOrWhiteSpace(options.Plan) || options.Confirm
			|| !string.IsNullOrWhiteSpace(options.Host) || options.Position is not null || options.SetActive || options.WithUnsharedOptions
			|| !string.IsNullOrWhiteSpace(options.Library) || !string.IsNullOrWhiteSpace(options.GameData)
			|| options.Limit is not null || !string.IsNullOrWhiteSpace(options.Cursor) || options.WithEffective || options.OnlyCompetition)
		{
			throw new CliUserException("CommandOptionNotSupported", "assets metadata inspect 仅接受 --manager-root 与 --json。 ");
		}
	}

	private static void EnsureAssetMetadataPlanOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Plan) || options.Confirm || !string.IsNullOrWhiteSpace(options.Host)
			|| options.Position is not null || options.SetActive || options.WithUnsharedOptions
			|| !string.IsNullOrWhiteSpace(options.Library) || !string.IsNullOrWhiteSpace(options.GameData)
			|| options.Limit is not null || !string.IsNullOrWhiteSpace(options.Cursor) || options.WithEffective || options.OnlyCompetition)
		{
			throw new CliUserException("CommandOptionNotSupported", "assets metadata plan 仅接受可选在线资产索引源、--manager-root、--output、--overwrite 与 --json。 ");
		}
	}

	private static void EnsureAssetMetadataValidateOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || options.Confirm || !string.IsNullOrWhiteSpace(options.Host)
			|| options.Position is not null || options.SetActive || options.WithUnsharedOptions
			|| !string.IsNullOrWhiteSpace(options.Library) || !string.IsNullOrWhiteSpace(options.GameData)
			|| options.Limit is not null || !string.IsNullOrWhiteSpace(options.Cursor) || options.WithEffective || options.OnlyCompetition)
		{
			throw new CliUserException("CommandOptionNotSupported", "assets metadata validate 仅接受 --plan、--manager-root 与 --json。 ");
		}
	}

	private static void EnsureAssetMetadataApplyOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || !string.IsNullOrWhiteSpace(options.Host)
			|| options.Position is not null || options.SetActive || options.WithUnsharedOptions
			|| !string.IsNullOrWhiteSpace(options.Library) || !string.IsNullOrWhiteSpace(options.GameData)
			|| options.Limit is not null || !string.IsNullOrWhiteSpace(options.Cursor) || options.WithEffective || options.OnlyCompetition)
		{
			throw new CliUserException("CommandOptionNotSupported", "assets metadata apply 仅接受 --plan、--manager-root、--confirm 与 --json。 ");
		}
	}

	private static void EnsureSameKeyInspectOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || !string.IsNullOrWhiteSpace(options.Plan) || options.Confirm
			|| !string.IsNullOrWhiteSpace(options.Host) || options.Position is not null || options.SetActive || options.WithUnsharedOptions)
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"patch repair inspect 仅接受 Mod、可选 Patch 文件名、--manager-root、--library、--game-data 与 --json。 ");
		}
	}

	private static void EnsureBatchPatchRepairInspectOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || !string.IsNullOrWhiteSpace(options.Plan) || options.Confirm
			|| !string.IsNullOrWhiteSpace(options.Host) || options.Position is not null || options.SetActive || options.WithUnsharedOptions)
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"patch repair batch inspect 仅接受可选 Mod 标识、--manager-root、--library、--game-data 与 --json。省略 Mod 时检查整个库。 ");
		}
	}

	private static void EnsureBatchPatchRepairPlanOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Plan) || options.Confirm || !string.IsNullOrWhiteSpace(options.Host)
			|| options.Position is not null || options.SetActive || options.WithUnsharedOptions)
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"patch repair batch plan 仅接受可选 Mod 标识、--manager-root、--library、--game-data、--output、--overwrite 与 --json。省略 Mod 时冻结整个库。 ");
		}
	}

	private static void EnsureBatchPatchRepairValidateOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || options.Confirm || !string.IsNullOrWhiteSpace(options.Host)
			|| options.Position is not null || options.SetActive || options.WithUnsharedOptions)
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"patch repair batch validate 仅接受 --plan、--manager-root、--library、--game-data 与 --json。 ");
		}
	}

	private static void EnsureBatchPatchRepairApplyOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || !string.IsNullOrWhiteSpace(options.Host)
			|| options.Position is not null || options.SetActive || options.WithUnsharedOptions)
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"patch repair batch apply 仅接受 --plan、--manager-root、--library、--game-data、--confirm 与 --json。它会原地替换计划中选中的 Mod Patch。 ");
		}
	}

	private static void EnsureSameKeyPlanOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Plan) || options.Confirm || !string.IsNullOrWhiteSpace(options.Host)
			|| options.Position is not null || options.SetActive || options.WithUnsharedOptions)
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"patch repair plan 仅接受 Mod、Patch 文件名、--manager-root、--library、--game-data、--output、--overwrite 与 --json。 ");
		}
	}

	private static void EnsureSameKeyValidateOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || options.Confirm || !string.IsNullOrWhiteSpace(options.Host)
			|| options.Position is not null || options.SetActive || options.WithUnsharedOptions)
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"patch repair validate 仅接受 --plan、--manager-root、--library、--game-data 与 --json。 ");
		}
	}

	private static void EnsureSameKeyApplyOptions(CliGlobalOptions options)
	{
		if (options.Overwrite || !string.IsNullOrWhiteSpace(options.Host) || options.Position is not null || options.SetActive || options.WithUnsharedOptions)
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"patch repair apply 仅接受 --plan、--manager-root、--library、--game-data、--output、--confirm 与 --json。输出必须是一个新的目录。 ");
		}
	}

	private static void EnsureMetadataMutationOptions(CliGlobalOptions options, string command)
	{
		EnsureMutationBoundary(options, command);
		if (!string.IsNullOrWhiteSpace(options.Host) || options.Position is not null || options.SetActive || options.WithUnsharedOptions)
			throw new CliUserException("CommandOptionNotSupported", $"{command} 不接受 --host、--position、--set-active 或 --with-unshared-options。");
	}

	private static void EnsureLogicalModCreateOptions(CliGlobalOptions options)
	{
		EnsureMutationBoundary(options, "mod create");
		if (!string.IsNullOrWhiteSpace(options.Host) || options.Position is not null || options.SetActive || options.WithUnsharedOptions)
			throw new CliUserException("CommandOptionNotSupported", "mod create 不接受 --host、--position、--set-active 或 --with-unshared-options。");
	}

	private static void EnsureRelationshipMutationOptions(CliGlobalOptions options, string command)
	{
		EnsureMutationBoundary(options, command);
		_ = RequireHost(options);
		if (options.Position is not null || options.SetActive || options.WithUnsharedOptions)
			throw new CliUserException("CommandOptionNotSupported", $"{command} 不接受 --position、--set-active 或 --with-unshared-options。");
	}

	private static void EnsureModAnalyzeOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || !string.IsNullOrWhiteSpace(options.Plan) || options.Confirm
			|| !string.IsNullOrWhiteSpace(options.Host) || options.Position is not null || options.SetActive || options.WithUnsharedOptions
			|| !string.IsNullOrWhiteSpace(options.GameData))
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"mod analyze 仅接受 Mod 标识、--manager-root/--library 与 --json；它直接只读扫描 Patch，不会写缓存、SQLite、Mod 库或 GameData。 ");
		}
	}

	private static void EnsureModExternalInspectOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || !string.IsNullOrWhiteSpace(options.Plan) || options.Confirm
			|| !string.IsNullOrWhiteSpace(options.Host) || options.Position is not null || options.SetActive || options.WithUnsharedOptions
			|| !string.IsNullOrWhiteSpace(options.GameData))
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"mod external inspect 仅接受 Mod 标识、--manager-root/--library 与 --json；它直接只读扫描受支持的外部组件，不会写缓存、SQLite、Mod 库或 GameData。 ");
		}
	}

	private static void EnsureModImageShowOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || !string.IsNullOrWhiteSpace(options.Plan) || options.Confirm
			|| !string.IsNullOrWhiteSpace(options.Host) || options.Position is not null || options.SetActive || options.WithUnsharedOptions
			|| !string.IsNullOrWhiteSpace(options.GameData))
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"mod image show 仅接受 Mod 标识、--library/--manager-root 与 --json。 ");
		}
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

	private static void EnsureOptionShowOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || !string.IsNullOrWhiteSpace(options.Plan) || options.Confirm
			|| !string.IsNullOrWhiteSpace(options.Host) || options.Position is not null || options.SetActive || options.WithUnsharedOptions || !string.IsNullOrWhiteSpace(options.GameData))
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"option show 仅接受选项标识、--library/--manager-root 与 --json。 ");
		}
	}

	private static void EnsureOptionAllHostsMutationOptions(CliGlobalOptions options, string command)
	{
		EnsureMutationBoundary(options, command);
		if (!string.IsNullOrWhiteSpace(options.Host) || options.Position is not null || options.SetActive || options.WithUnsharedOptions)
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				$"{command} 只作用于该选项的全部已验证直属主体；不接受 --host、--position、--set-active 或 --with-unshared-options。 ");
		}
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

	private static void EnsureProfileAlignExportOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Plan) || options.Confirm || !string.IsNullOrWhiteSpace(options.Host)
			|| options.Position is not null || options.SetActive || options.WithUnsharedOptions || !string.IsNullOrWhiteSpace(options.GameData))
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"profile align export 仅接受 Profile 标识、--manager-root/--library、--output、--overwrite 与 --json。 ");
		}
	}

	private static void EnsureProfileAlignInspectOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || !string.IsNullOrWhiteSpace(options.Plan) || options.Confirm
			|| !string.IsNullOrWhiteSpace(options.Host) || options.Position is not null || options.SetActive || options.WithUnsharedOptions
			|| !string.IsNullOrWhiteSpace(options.GameData))
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"profile align inspect 仅接受规格文件、--manager-root、--library 与 --json。 ");
		}
	}

	private static void EnsureProfileAlignPlanOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Plan) || options.Confirm || !string.IsNullOrWhiteSpace(options.Host)
			|| options.Position is not null || options.SetActive || options.WithUnsharedOptions || !string.IsNullOrWhiteSpace(options.GameData))
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"profile align plan 仅接受规格文件、--manager-root、--library、--output、--overwrite 与 --json。 ");
		}
	}

	private static void EnsureProfileAlignValidateOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || options.Confirm || !string.IsNullOrWhiteSpace(options.Host)
			|| options.Position is not null || options.SetActive || options.WithUnsharedOptions || !string.IsNullOrWhiteSpace(options.GameData))
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"profile align validate 仅接受 --plan、--manager-root、--library 与 --json。 ");
		}
	}

	private static void EnsureProfileAlignApplyOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || !string.IsNullOrWhiteSpace(options.Host)
			|| options.Position is not null || options.SetActive || options.WithUnsharedOptions || !string.IsNullOrWhiteSpace(options.GameData))
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"profile align apply 仅接受 --plan、--manager-root、--library、--confirm 与 --json。 ");
		}
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

	private static void EnsureModDeletionPlanOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Plan) || options.Confirm || !string.IsNullOrWhiteSpace(options.Host)
			|| options.Position is not null || options.SetActive || !string.IsNullOrWhiteSpace(options.GameData))
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"mod delete plan 仅接受 Mod 标识、--manager-root、--library、--with-unshared-options、--output、--overwrite 与 --json。 ");
		}
	}

	private static void EnsureModDeletionValidateOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || options.Confirm || !string.IsNullOrWhiteSpace(options.Host)
			|| options.Position is not null || options.SetActive || options.WithUnsharedOptions || !string.IsNullOrWhiteSpace(options.GameData))
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"mod delete validate 仅接受 --plan、--manager-root、--library 与 --json。删除范围已固定在计划中。 ");
		}
	}

	private static void EnsureModDeletionApplyOptions(CliGlobalOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.Output) || options.Overwrite || !string.IsNullOrWhiteSpace(options.Host)
			|| options.Position is not null || options.SetActive || options.WithUnsharedOptions || !string.IsNullOrWhiteSpace(options.GameData))
		{
			throw new CliUserException(
				"CommandOptionNotSupported",
				"mod delete apply 仅接受 --plan、--manager-root、--library、--confirm 与 --json。删除范围已固定在计划中。 ");
		}
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

	private static string RequireHostDerivedDecorationHost(CliGlobalOptions options)
	{
		if (string.IsNullOrWhiteSpace(options.Host))
		{
			throw new CliUserException(
				"HostDerivedDecorationHostRequired",
				"host-derived plan decoration enable/disable 必须显式提供 --host <主体 Mod GUID 或唯一前缀>。 ");
		}
		return options.Host;
	}

	private static CliHostDerivedAction ParseHostDerivedToggleAction(string rawAction, string subject)
		=> rawAction.Trim().ToLowerInvariant() switch
		{
			"enable" or "enable-all" => CliHostDerivedAction.Enable,
			"disable" or "disable-all" => CliHostDerivedAction.Disable,
			_ => throw new CliUserException(
				"HostDerivedActionInvalid",
				$"{subject}操作只能使用 enable、disable（装饰也可使用 enable-all、disable-all）。"),
		};

	private static CliHostDerivedRequest CreateHostDerivedRequest(
		CliHostDerivedOperation operation,
		CliHostDerivedAction action,
		IReadOnlyList<string> hostNodeIds,
		IReadOnlyList<string> decorationNodeIds,
		bool allDeclaredDecorationHosts,
		string? gameDataDirectory)
		=> new(
			operation,
			action,
			hostNodeIds,
			decorationNodeIds,
			allDeclaredDecorationHosts,
			gameDataDirectory ?? string.Empty);

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
		catch (ModRelationshipMutationException exception)
		{
			throw new CliUserException($"ModRelationship{exception.Failure}", exception.Message);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or DirectoryNotFoundException or FileNotFoundException)
		{
			throw new CliUserException("LibraryMutationFailed", $"Mod 库修改未完成：{exception.Message}");
		}
	}

	private static async ValueTask<CliImportProfileTarget?> ResolveImportProfileTargetAsync(CliGlobalOptions options)
	{
		if (string.IsNullOrWhiteSpace(options.AddToProfile)) return null;
		var workspace = CliWorkspace.Open(options);
		if (string.IsNullOrWhiteSpace(workspace.ManagerRoot))
		{
			throw new CliUserException(
				"ImportProfileManagerRootRequired",
				"import plan 使用 --add-to-profile 时需要 --manager-root <管理器目录>，以固定可写入的 Mod 库与配置文件。 ");
		}
		var state = await workspace.LoadLibraryAsync().ConfigureAwait(false);
		var selector = options.AddToProfile.Trim();
		Profile profile;
		if (string.Equals(selector, "active", StringComparison.OrdinalIgnoreCase))
		{
			if (state.Snapshot.ActiveProfileId is not { } activeProfileId
				|| state.Snapshot.Profiles.FirstOrDefault(item => item.Id == activeProfileId) is not { } activeProfile)
			{
				throw new CliUserException(
					"ImportActiveProfileMissing",
					"--add-to-profile active 需要当前存在活动配置文件；请先显式创建或选择一个 Profile。 ");
			}
			profile = activeProfile;
		}
		else
		{
			profile = CliProjections.ResolveProfile(state.Snapshot.Profiles, state.Snapshot.ActiveProfileId, selector);
		}

		return new CliImportProfileTarget(
			CliProjections.FormatId(profile.Id),
			profile.Name,
			profile.Revision,
			Path.GetFullPath(workspace.LibraryRoot ?? throw new InvalidOperationException("导入目标配置缺少 Mod 库路径。")));
	}

	private static IReadOnlyList<ModNodeId> GetImportedNodeIds(ModImportExecutionResult import)
		=> import.ImportedNodeIds.Count != 0 ? import.ImportedNodeIds : import.AddedNodeIds;

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

	private static string RequireBatchImportPlan(CliGlobalOptions options)
	{
		if (string.IsNullOrWhiteSpace(options.Plan))
			throw new CliUserException("BatchImportPlanRequired", "请通过 --plan <批量导入计划.json> 提供计划文件。 ");
		return options.Plan;
	}

	private static string RequireModDeletionPlan(CliGlobalOptions options)
	{
		if (string.IsNullOrWhiteSpace(options.Plan))
			throw new CliUserException("ModDeletionPlanRequired", "请通过 --plan <删除计划.json> 提供已导出的删除计划。 ");
		return options.Plan;
	}

	private static string RequireProfileAlignPlan(CliGlobalOptions options)
	{
		if (string.IsNullOrWhiteSpace(options.Plan))
			throw new CliUserException("ProfileAlignPlanRequired", "请通过 --plan <Profile对齐计划.json> 提供已导出的对齐计划。 ");
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

	private static string RequireLibrarySyncPlan(CliGlobalOptions options)
	{
		if (string.IsNullOrWhiteSpace(options.Plan))
			throw new CliUserException("LibrarySyncPlanRequired", "请通过 --plan <库对账计划.json> 提供计划文件。 ");
		return options.Plan;
	}

	private static string RequireDecorationPackagePlan(CliGlobalOptions options)
	{
		if (string.IsNullOrWhiteSpace(options.Plan))
			throw new CliUserException("DecorationPackagePlanRequired", "请通过 --plan <装饰外部包计划.json> 提供计划文件。 ");
		return options.Plan;
	}

	private static string RequireDecorationEditPlan(CliGlobalOptions options)
	{
		if (string.IsNullOrWhiteSpace(options.Plan))
			throw new CliUserException("DecorationEditPlanRequired", "请通过 --plan <装饰编辑计划.json> 提供计划文件。 ");
		return options.Plan;
	}

	private static string RequireModExportPlan(CliGlobalOptions options)
	{
		if (string.IsNullOrWhiteSpace(options.Plan))
			throw new CliUserException("ModExportPlanRequired", "请通过 --plan <Mod导出计划.json> 提供计划文件。 ");
		return options.Plan;
	}

	private static string RequirePatchResourcePlan(CliGlobalOptions options)
	{
		if (string.IsNullOrWhiteSpace(options.Plan))
			throw new CliUserException("PatchResourcePlanRequired", "请通过 --plan <Patch资源重打包计划.json> 提供计划文件。 ");
		return options.Plan;
	}

	private static string RequireHelmetMergePlan(CliGlobalOptions options)
	{
		if (string.IsNullOrWhiteSpace(options.Plan))
			throw new CliUserException("HelmetMergePlanRequired", "请通过 --plan <头盔合并计划.json> 提供计划文件。 ");
		return options.Plan;
	}

	private static string RequireCrossArmorPlan(CliGlobalOptions options)
	{
		if (string.IsNullOrWhiteSpace(options.Plan))
			throw new CliUserException("CrossArmorPlanRequired", "请通过 --plan <跨护甲计划.json> 提供计划文件。 ");
		return options.Plan;
	}

	private static string RequireArmorNormalizePlan(CliGlobalOptions options)
	{
		if (string.IsNullOrWhiteSpace(options.Plan)) throw new CliUserException("ArmorNormalizePlanRequired", "请通过 --plan <护甲规整计划.json> 提供计划文件。 ");
		return options.Plan;
	}

	private static string RequireGameDataIndexPlan(CliGlobalOptions options)
	{
		if (string.IsNullOrWhiteSpace(options.Plan))
			throw new CliUserException("GameDataIndexPlanRequired", "请通过 --plan <GameData索引计划.json> 提供计划文件。 ");
		return options.Plan;
	}

	private static string RequireAssetMetadataPlan(CliGlobalOptions options)
	{
		if (string.IsNullOrWhiteSpace(options.Plan))
			throw new CliUserException("AssetMetadataPlanRequired", "请通过 --plan <资产元数据计划.json> 提供计划文件。 ");
		return options.Plan;
	}

	private static string RequireSameKeyPlan(CliGlobalOptions options)
	{
		if (string.IsNullOrWhiteSpace(options.Plan))
			throw new CliUserException("SameKeyPlanRequired", "请通过 --plan <Same-key重构计划.json> 提供计划文件。 ");
		return options.Plan;
	}

	private static string RequireBatchPatchRepairPlan(CliGlobalOptions options)
	{
		if (string.IsNullOrWhiteSpace(options.Plan))
			throw new CliUserException("BatchRepairPlanRequired", "请通过 --plan <批量 Patch 修复计划.json> 提供计划文件。 ");
		return options.Plan;
	}

	private static string RequireHostDerivedPlan(CliGlobalOptions options)
	{
		if (string.IsNullOrWhiteSpace(options.Plan))
			throw new CliUserException("HostDerivedPlanRequired", "请通过 --plan <主体派生 Overwrite 计划.json> 提供计划文件。 ");
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

	private static string FormatModExportPlan(CliModExportPlanDocument document, string outputPath)
	{
		var status = document.IsReady
			? $"Mod 导出计划已写出：根节点 {document.Nodes.FirstOrDefault()?.Name ?? document.Request.NodeId}；节点 {document.Nodes.Count} 个；文件 {document.Nodes.Sum(node => node.Files.Count)} 个。"
			: $"Mod 导出计划已写出，但当前不可执行：{document.Diagnostics.Count(diagnostic => diagnostic.Blocking)} 个阻断项。";
		return $"{status}{Environment.NewLine}计划：{outputPath}{Environment.NewLine}apply 会输出带 manifest.json 的压缩包，不会修改 Mod 库或部署。";
	}

	private static string FormatModExportValidation(CliModExportValidationResult validation)
	{
		if (validation.IsValid)
			return $"Mod 导出计划验证通过：节点 {validation.Current!.Document.Nodes.Count} 个；文件 {validation.Current.Document.Nodes.Sum(node => node.Files.Count)} 个。";
		var blocking = validation.Diagnostics.Where(diagnostic => diagnostic.Blocking).Take(5).ToArray();
		return blocking.Length == 0
			? "Mod 导出计划验证未通过；请查看 JSON 结果。"
			: $"Mod 导出计划验证未通过：{string.Join("；", blocking.Select(diagnostic => $"[{diagnostic.Code}] {diagnostic.Message}"))}";
	}

	private static string FormatModExportExecution(CliModExportExecutionResult execution)
	{
		if (execution.Success)
			return $"Mod 已导出：节点 {execution.ExportedNodeCount} 个；文件 {execution.ExportedFileCount} 个；压缩包：{execution.ZipPath}。"
			+ (execution.OverwroteExisting ? " 已替换同名旧压缩包。" : string.Empty);
		var blocking = execution.Diagnostics.Where(diagnostic => diagnostic.Blocking).Take(5).ToArray();
		return blocking.Length == 0
			? "Mod 导出未完成；请查看 JSON 结果或日志。"
			: $"Mod 导出未完成：{string.Join("；", blocking.Select(diagnostic => $"[{diagnostic.Code}] {diagnostic.Message}"))}";
	}

	private static string FormatModAnalysis(CliModAnalysisDocument analysis)
	{
		var entries = analysis.PatchGroups.Sum(group => group.Entries.Count);
		var references = analysis.PatchGroups.Sum(group => group.References.Count);
		var units = analysis.PatchGroups.Sum(group => group.Entries.Count(entry => entry.Unit is not null));
		var blocking = analysis.Diagnostics.Count(diagnostic => diagnostic.Blocking)
			+ analysis.PatchGroups.Sum(group => group.Diagnostics.Count(diagnostic => diagnostic.Blocking));
		return $"Patch 深度分析：{analysis.Source.Name}；Patch 组 {analysis.PatchGroups.Count}；条目 {entries}；引用 {references}；Unit {units}；阻断诊断 {blocking}。"
			+ (analysis.IsReady
				? " 未写入 Mod 库、缓存、SQLite 或 GameData。"
				: " 返回了可读的部分结果；请查看 JSON 中的 diagnostics。");
	}

	private static string FormatModExternalInspection(CliModExternalInspectionDocument inspection)
	{
		var blocking = inspection.Diagnostics.Count(diagnostic => diagnostic.Blocking);
		return $"外部组件检查：{inspection.Source.Name}；可安全部署 {inspection.Files.Count} 个；未识别候选 {inspection.UnrecognizedExternalPaths.Count} 个；同 Mod 冲突 {inspection.Conflicts.Count} 个；阻断诊断 {blocking}。"
			+ (inspection.IsReady
				? " 未写入缓存、SQLite、Mod 库或 GameData。"
				: " 已拒绝把存在冲突或无法读取的组件纳入自动部署；请查看 JSON 中的 diagnostics。");
	}

	private static object CreateModImageInspectionProjection(CliModImageInspection inspection)
		=> new
		{
			modId = CliProjections.FormatId(inspection.Node.Id),
			modName = inspection.Node.Metadata.Name,
			directory = inspection.Directory,
			resolvedImagePath = inspection.ResolvedImagePath,
			canonicalImagePaths = inspection.CanonicalImagePaths,
		};

	private static object CreateModImageMutationProjection(CliModImageMutationResult result)
		=> new
		{
			operation = result.Operation,
			modId = CliProjections.FormatId(result.Node.Id),
			modName = result.Node.Metadata.Name,
			sourcePath = result.SourcePath,
			destinationPath = result.DestinationPath,
			removedPaths = result.RemovedPaths,
			resolvedImagePath = result.ResolvedImagePath,
			deployment = "not-performed",
		};

	private static string FormatModImageInspection(CliModImageInspection inspection)
		=> string.Join(Environment.NewLine,
		[
			$"{inspection.Node.Metadata.Name} 的图像",
			$"目录：{inspection.Directory}",
			$"当前图像：{inspection.ResolvedImagePath ?? "（未找到）"}",
			$"规范 icon 文件：{(inspection.CanonicalImagePaths.Count == 0 ? "（无）" : string.Join("；", inspection.CanonicalImagePaths))}",
		]);

	private static string FormatModImageMutation(CliModImageMutationResult result)
		=> string.Join(Environment.NewLine,
		new[]
		{
			$"已{(result.Operation == "set" ? "更新" : "清除")} Mod 图像：{result.Node.Metadata.Name}",
			result.DestinationPath is null ? string.Empty : $"目标：{result.DestinationPath}",
			$"移除规范 icon 文件：{result.RemovedPaths.Count}",
			$"当前解析图像：{result.ResolvedImagePath ?? "（未找到）"}",
			"未修改 Patch、配置文件、选项状态或部署。",
		}.Where(line => !string.IsNullOrWhiteSpace(line)));

	private static string FormatOptionAllHostsInspection(ModNode option, OptionAllHostsRelationshipSnapshot scope)
	{
		var state = scope.CanMutate ? "可批量修改" : "不可批量修改";
		var enabled = scope.EligibleHosts.Count(host => host.Enabled);
		return $"选项主体范围：{option.Metadata.Name}；{state}；已验证直属主体 {scope.EligibleHosts.Count}；已启用 {enabled}；问题 {scope.Issues.Count}；孤儿状态 {scope.OrphanActivationStates.Count}。";
	}

	private static string FormatOptionAllHostsMutation(CliOptionAllHostsMutationResult result)
	{
		var hosts = result.Mutation.After.EligibleHosts.Count;
		if (!result.Mutation.Changed)
			return $"选项“{result.Option.Metadata.Name}”的 {hosts} 个已验证直属主体已处于目标状态。未自动部署。";
		return $"已为 {hosts} 个已验证直属主体{(result.RequestedEnabled ? "启用" : "禁用")}选项“{result.Option.Metadata.Name}”。未自动部署。";
	}

	private static string FormatModRelationshipMutation(ModRelationshipMutationResult result, bool adding)
	{
		if (!result.Changed)
			return "Mod 父子关系未发生变化。未自动部署。";
		var relationCount = adding ? result.AddedRelationOptionIds.Count : result.RemovedRelationOptionIds.Count;
		var profileText = adding && result.AffectedProfileIds.Count > 0
			? $"已从 {result.AffectedProfileIds.Count} 个配置文件释放这些新选项。"
			: string.Empty;
		return $"已{(adding ? "添加" : "移除")} {relationCount} 条 Option 父级关系；{profileText}未自动部署。";
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

	private static string FormatProfileAlignSpecification(CliProfileAlignSpecDocument specification, string outputPath)
		=> $"已导出 Profile 对齐规格：{outputPath}{Environment.NewLine}Profile：{specification.ExpectedProfileName}；当前逻辑条目：{specification.EntryNodeIds.Count}；活动状态策略：{specification.ActiveMode}。";

	private static string FormatProfileAlignInspection(CliProfileAlignPlanDocument document)
	{
		var status = document.IsReady ? "可生成计划" : "存在阻断项";
		return $"Profile 对齐检查：{status}；当前条目 {document.Changes.CurrentEntryCount} → 目标条目 {document.Changes.TargetEntryCount}；成员变化={(document.Changes.EntriesChanged ? "是" : "否")}；活动配置变化={(document.Changes.ActiveProfileChanged ? "是" : "否")}；阻断诊断 {document.Diagnostics.Count(diagnostic => diagnostic.Blocking)}。";
	}

	private static string FormatProfileAlignPlan(CliProfileAlignPlanDocument document, string outputPath)
		=> $"已导出 Profile 对齐计划：{outputPath}{Environment.NewLine}" + FormatProfileAlignInspection(document) + Environment.NewLine + "validate 通过后，apply --confirm 才会仅写入 profiles.json。";

	private static string FormatProfileAlignValidation(CliProfileAlignValidationResult validation)
	{
		var status = validation.Status switch
		{
			CliProfileAlignValidationStatus.Ready => "Profile 对齐计划验证通过。",
			CliProfileAlignValidationStatus.AlreadyAligned => "Profile 已达到计划目标；无需写入。",
			_ => "Profile 对齐计划验证未通过。",
		};
		var issues = validation.Diagnostics.Count == 0
			? "无诊断。"
			: string.Join(Environment.NewLine, validation.Diagnostics.Select(diagnostic => $"- [{diagnostic.Code}] {diagnostic.Message}"));
		return $"{status}{Environment.NewLine}{issues}";
	}

	private static string FormatProfileAlignExecution(CliProfileAlignExecutionResult execution)
	{
		if (execution.Success && execution.Status == "already-aligned")
			return "Profile 已达到计划目标；未改写 profiles.json，也未部署。";
		if (execution.Success)
			return $"Profile 对齐完成：{execution.Profile?.Name ?? execution.Profile?.ProfileId ?? "<unknown>"}；仅更新 profiles.json；未部署。";
		var blocking = execution.Diagnostics.Where(diagnostic => diagnostic.Blocking).Take(5).ToArray();
		return blocking.Length == 0
			? "Profile 对齐未完成；请查看 JSON 结果或日志。"
			: $"Profile 对齐被拒绝：{string.Join("；", blocking.Select(diagnostic => $"[{diagnostic.Code}] {diagnostic.Message}"))}";
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

	private static string FormatBatchImportInspection(CliBatchImportPlanDocument document)
		=> $"批量导入检查完成：来源 {document.Imports.Count} 个；总计划条目 {document.Imports.Sum(item => item.Import.Items.Count)} 个。不会写入 Mod 库。";

	private static string FormatBatchImportPlan(CliBatchImportPlanDocument document, string outputPath)
		=> $"已导出批量导入计划：{outputPath}{Environment.NewLine}来源：{document.Imports.Count} 个；总计划条目：{document.Imports.Sum(item => item.Import.Items.Count)}。"
			+ (document.AddToProfile is null ? string.Empty : $"{Environment.NewLine}全部来源导入后将一次加入配置：{document.AddToProfile.ProfileName}。")
			+ $"{Environment.NewLine}执行为串行：已完成来源不会因后续失败被伪装为回滚。";

	private static string FormatBatchImportValidation(CliBatchImportPlanValidationResult validation)
	{
		if (validation.IsValid) return $"批量导入验证通过：{validation.Sources.Count} 个来源均可执行。";
		var failures = validation.Sources
			.Where(source => !source.Validation.IsValid)
			.Select(source => $"- #{source.Index + 1} {source.Document.Source.RequestedPath}：{string.Join("；", source.Validation.Issues.Where(issue => issue.IsBlocking).Select(issue => $"[{issue.Code}] {issue.Message}"))}")
			.Take(5)
			.ToArray();
		return "批量导入验证未通过。" + (failures.Length == 0 ? string.Empty : Environment.NewLine + string.Join(Environment.NewLine, failures));
	}

	private static string FormatBatchImportExecution(CliBatchImportExecutionResult execution)
	{
		var completed = execution.Sources.Count(source => source.Status == "completed");
		if (execution.Success)
		{
			return $"批量导入完成：{completed}/{execution.Sources.Count} 个来源已导入。"
				+ (execution.ProfileAddition is null
					? ""
					: $" 已加入配置：{execution.ProfileAddition.ProfileName}，新增 {execution.ProfileAddition.AddedNodeIds.Count} 个可部署 Mod。")
				+ " 未自动部署。";
		}
		return $"批量导入未完整完成：状态={execution.Status}；已完成来源={completed}；错误={execution.ErrorCode ?? "见验证结果"}。"
			+ " 已完成来源保持已导入状态，CLI 未尝试猜测性回滚。";
	}

	private static string FormatLibrarySyncInspection(CliLibrarySynchronizationPlanDocument document)
	{
		var inspection = document.Inspection;
		return $"库对账检查：新增 {inspection.Added.Count}；更新 {inspection.Changed.Count}；缺失 {inspection.Missing.Count}；可写快照变更：{(inspection.HasSnapshotChanges ? "是" : "否")}。";
	}

	private static string FormatLibrarySyncPlan(CliLibrarySynchronizationPlanDocument document, string outputPath)
	{
		var inspection = document.Inspection;
		var blocking = inspection.Diagnostics.Count(diagnostic => diagnostic.Blocking);
		return string.Join(Environment.NewLine,
		[
			$"已导出库对账计划：{outputPath}",
			$"新增 {inspection.Added.Count}；更新 {inspection.Changed.Count}；缺失 {inspection.Missing.Count}；已冻结新增 ID：{inspection.Added.Count}",
			$"阻断诊断：{blocking}。该流程只会更新 library.json 的节点记录，不会修改物理 Mod、Profile、激活状态或部署。",
		]);
	}

	private static string FormatLibrarySyncValidation(CliLibrarySynchronizationValidationResult validation)
	{
		var status = validation.IsValid ? "库对账验证通过。" : "库对账验证未通过。";
		var issues = validation.Diagnostics.Count == 0
			? "无诊断。"
			: string.Join(Environment.NewLine, validation.Diagnostics.Select(diagnostic => $"- [{diagnostic.Code}] {diagnostic.Message}"));
		return $"{status}{Environment.NewLine}{issues}";
	}

	private static string FormatLibrarySyncExecution(CliLibrarySynchronizationExecutionResult execution)
	{
		if (execution.Success)
		{
			return execution.Changed
				? $"库对账完成：新增 {execution.AddedNodeIds.Count}；更新 {execution.ChangedNodeIds.Count}。只更新了 library.json 节点记录。"
				: "库对账完成：当前库无需更新，未写入任何文件。";
		}
		var blocking = execution.Diagnostics.Where(diagnostic => diagnostic.Blocking).Take(5).ToArray();
		return blocking.Length == 0
			? "库对账未完成；未写入任何文件，请查看 JSON 结果或日志。"
			: $"库对账被拒绝：{string.Join("；", blocking.Select(diagnostic => $"[{diagnostic.Code}] {diagnostic.Message}"))}";
	}

	private static string FormatDecorationSourceInspection(CliDecorationSourceInspection inspection)
	{
		if (inspection.IsReady)
		{
			var defaults = inspection.UnitCandidates.Count(candidate => candidate.IsDefaultSelected);
			return $"装饰来源检查完成：{inspection.SourceModName}；可选 Unit {inspection.UnitCandidates.Count} 个；默认选择 {defaults} 个；建议目标 {inspection.SuggestedAttachment.TargetPart}/{inspection.SuggestedAttachment.TargetBodyVariant}。";
		}
		var blocking = inspection.Diagnostics.Where(diagnostic => diagnostic.Blocking).Take(5).ToArray();
		return blocking.Length == 0
			? $"装饰来源检查未找到可用 Unit：{inspection.SourceModName}。"
			: $"装饰来源检查未通过：{string.Join("；", blocking.Select(diagnostic => $"[{diagnostic.Code}] {diagnostic.Message}"))}";
	}

	private static string FormatDecorationManagementInspection(CliDecorationManagementInspection inspection)
	{
		if (!inspection.IsReadable)
		{
			var blocking = inspection.Diagnostics.Where(diagnostic => diagnostic.Blocking).Take(5).ToArray();
			return blocking.Length == 0
				? $"装饰详情不可读取：{inspection.DecorationName}。"
				: $"装饰详情不可读取：{string.Join("；", blocking.Select(diagnostic => $"[{diagnostic.Code}] {diagnostic.Message}"))}";
		}
		var available = inspection.DeclaredHosts.Count(host => host.IsAvailableForActivation);
		var enabled = inspection.DeclaredHosts.Count(host => host.IsEnabled);
		return $"装饰详情：{inspection.DecorationName}；来源 Unit {inspection.SourceUnits.Count} 个；声明主体 {inspection.DeclaredHosts.Count} 个（可启用 {available}，当前启用 {enabled}）。";
	}

	private static string FormatDecorationPackagePlan(CliDecorationPackagePlanDocument document, string outputPath)
	{
		var message = document.IsReady
			? $"装饰外部包计划已写出：来源 {document.Source.ModName}；默认 Unit {document.Selections.Count} 个；目标主体 {document.Attachment.TargetHostNodeIds.Count} 个。"
			: $"装饰外部包计划已写出，但当前不可执行：{document.Diagnostics.Count(diagnostic => diagnostic.Blocking)} 个阻断项。";
		return $"{message}{Environment.NewLine}计划：{outputPath}{Environment.NewLine}请审阅 selections 与 attachment，随后执行 decoration package validate。";
	}

	private static string FormatDecorationPackageValidation(CliDecorationPackageValidationResult validation)
	{
		if (validation.IsValid)
			return $"装饰外部包验证通过：已选择 {validation.ResolvedSelections.Count} 个来源 Unit。";
		var blocking = validation.Diagnostics.Where(diagnostic => diagnostic.Blocking).Take(5).ToArray();
		return blocking.Length == 0
			? "装饰外部包验证未通过；请查看 JSON 结果。"
			: $"装饰外部包验证未通过：{string.Join("；", blocking.Select(diagnostic => $"[{diagnostic.Code}] {diagnostic.Message}"))}";
	}

	private static string FormatDecorationPackageExecution(CliDecorationPackageExecutionResult execution)
	{
		if (execution.Success)
			return $"装饰外部包已输出：{execution.SelectedUnitCount} 个来源 Unit；目录：{execution.OutputDirectory}。结果尚未导入、启用或部署。";
		var blocking = execution.Diagnostics.Where(diagnostic => diagnostic.Blocking).Take(5).ToArray();
		return blocking.Length == 0
			? "装饰外部包未完成；请查看 JSON 结果或计划。"
			: $"装饰外部包未完成：{string.Join("；", blocking.Select(diagnostic => $"[{diagnostic.Code}] {diagnostic.Message}"))}";
	}

	private static string FormatDecorationEditSpecification(CliDecorationEditSpecDocument specification, string outputPath)
	{
		var status = specification.IsReady
			? $"装饰编辑规格已写出：{specification.Source.DecorationName}；可选 Unit {specification.Candidates.Count} 个；当前选择 {specification.Selections.Count} 个。"
			: $"装饰编辑规格已写出，但当前不可执行：{specification.Diagnostics.Count(diagnostic => diagnostic.Blocking)} 个阻断项。";
		return $"{status}{Environment.NewLine}规格：{outputPath}{Environment.NewLine}请只编辑 selections 与 attachment，然后执行 decoration edit inspect 或 decoration edit plan。";
	}

	private static string FormatDecorationEditInspection(CliDecorationEditPlanDocument document)
	{
		if (document.IsReady)
		{
			var active = document.ActiveHosts.Count(host => host.Exists);
			var detached = document.ActiveHosts.Count(host => host.Exists && host.WasDeclaredBeforeEdit && !host.WillBeDeclaredAfterEdit);
			return $"装饰编辑检查通过：选择来源 Unit {document.ProposedSourceUnits.Count} 个；目标主体 {document.ProposedAttachment.TargetHostNodeIds.Count} 个；需要回建的已启用主体 {active} 个；将移除 {detached} 个失效启用关联。";
		}
		var blocking = document.Diagnostics.Where(diagnostic => diagnostic.Blocking).Take(5).ToArray();
		return blocking.Length == 0
			? "装饰编辑检查未通过；请查看 JSON 结果。"
			: $"装饰编辑检查未通过：{string.Join("；", blocking.Select(diagnostic => $"[{diagnostic.Code}] {diagnostic.Message}"))}";
	}

	private static string FormatDecorationEditPlan(CliDecorationEditPlanDocument document, string outputPath)
	{
		var status = document.IsReady
			? $"装饰编辑计划已写出：来源 Unit {document.ProposedSourceUnits.Count} 个；目标主体 {document.ProposedAttachment.TargetHostNodeIds.Count} 个。"
			: $"装饰编辑计划已写出，但当前不可执行：{document.Diagnostics.Count(diagnostic => diagnostic.Blocking)} 个阻断项。";
		return $"{status}{Environment.NewLine}计划：{outputPath}{Environment.NewLine}apply 会更新 decoration.json，并回建所有当前启用的主体；被移出目标列表的已启用主体会清除该装饰关联。";
	}

	private static string FormatDecorationEditValidation(CliDecorationEditValidationResult validation)
	{
		if (validation.IsValid)
			return $"装饰编辑计划验证通过：来源 Unit {validation.Current!.Document.ProposedSourceUnits.Count} 个；已启用主体 {validation.Current.Document.ActiveHosts.Count(host => host.Exists)} 个。";
		var blocking = validation.Diagnostics.Where(diagnostic => diagnostic.Blocking).Take(5).ToArray();
		return blocking.Length == 0
			? "装饰编辑计划验证未通过；请查看 JSON 结果。"
			: $"装饰编辑计划验证未通过：{string.Join("；", blocking.Select(diagnostic => $"[{diagnostic.Code}] {diagnostic.Message}"))}";
	}

	private static string FormatDecorationEditExecution(CliDecorationEditExecutionResult execution)
	{
		if (execution.Success)
		{
			var rebuilt = execution.Hosts.Count(host => host.PlannedRebuildSucceeded);
			var state = execution.ActivationStateCommitted
				? $"；已清理 {execution.PrunedActivationHostCount} 个失效启用关联"
				: string.Empty;
			return $"装饰编辑已完成：已更新 decoration.json；已回建 {rebuilt} 个主体{state}。未自动部署。";
		}
		var blocking = execution.Diagnostics.Where(diagnostic => diagnostic.Blocking).Take(5).ToArray();
		return blocking.Length == 0
			? "装饰编辑未完成；已尝试回滚，请查看 JSON 结果。"
			: $"装饰编辑未完成：{string.Join("；", blocking.Select(diagnostic => $"[{diagnostic.Code}] {diagnostic.Message}"))}";
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

	private static object CreateModExportValidationProjection(CliModExportValidationResult validation)
		=> new
		{
			isValid = validation.IsValid,
			requestedInputFingerprint = validation.Document.InputFingerprint,
			recomputedInputFingerprint = validation.Current?.Document.InputFingerprint,
			requestedNodes = validation.Document.Nodes,
			recomputedNodes = validation.Current?.Document.Nodes,
			diagnostics = validation.Diagnostics,
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

	private static object CreateLibrarySyncPlanProjection(CliLibrarySynchronizationPlanDocument document)
		=> new
		{
			document.Schema,
			document.Version,
			document.CreatedUtc,
			ready = document.IsReady,
			document.InputFingerprint,
			request = document.Request,
			inspection = document.Inspection,
		};

	private static object CreateLibrarySyncValidationProjection(CliLibrarySynchronizationValidationResult validation)
		=> new
		{
			isValid = validation.IsValid,
			requestedInputFingerprint = validation.Document.InputFingerprint,
			recomputedInputFingerprint = validation.Current?.Document.InputFingerprint,
			current = validation.Current is null ? null : CreateLibrarySyncPlanProjection(validation.Current.Document),
			diagnostics = validation.Diagnostics,
		};

	private static object CreateLibrarySyncExecutionProjection(CliLibrarySynchronizationExecutionResult execution)
		=> new
		{
			execution.Success,
			execution.Changed,
			execution.AddedNodeIds,
			execution.ChangedNodeIds,
			execution.MissingNodeIds,
			diagnostics = execution.Diagnostics,
			physicalFiles = "not-modified",
			profiles = "not-modified",
			activationState = "not-modified",
			derivedData = "not-refreshed",
			deployment = "not-performed",
		};

	private static object CreatePatchResourceRepackagingPlanProjection(CliPatchResourceRepackagingPlanDocument document)
		=> new
		{
			document.Schema,
			document.Version,
			document.CreatedUtc,
			ready = document.IsReady,
			document.InputFingerprint,
			document.LibraryRoot,
			sourceNodeIds = document.SourceNodeIds,
			patchGroups = document.PatchGroups,
			candidates = document.Candidates,
			selections = document.Selections,
			diagnostics = document.Diagnostics,
		};

	private static object CreatePatchResourceRepackagingValidationProjection(CliPatchResourceRepackagingValidationResult validation)
		=> new
		{
			isValid = validation.IsValid,
			requestedInputFingerprint = validation.Document.InputFingerprint,
			recomputedInputFingerprint = validation.Current?.Document.InputFingerprint,
			resolvedSelectionCount = validation.ResolvedSelections.Count,
			current = validation.Current is null ? null : CreatePatchResourceRepackagingPlanProjection(validation.Current.Document),
			diagnostics = validation.Diagnostics,
		};

	private static object CreatePatchResourceRepackagingExecutionProjection(CliPatchResourceRepackagingExecutionResult execution)
		=> new
		{
			execution.Success,
			execution.OutputDirectory,
			execution.PatchTocPath,
			execution.SelectedAssetCount,
			diagnostics = execution.Diagnostics,
			libraryMutation = "not-performed",
			deployment = "not-performed",
		};

	private static object CreateHelmetMergePlanProjection(CliHelmetToArmorMergePlanDocument document)
		=> new
		{
			document.Schema,
			document.Version,
			document.CreatedUtc,
			ready = document.IsReady,
			document.InputFingerprint,
			request = document.Request,
			sources = document.Sources,
			candidates = document.Candidates,
			selections = document.Selections,
			diagnostics = document.Diagnostics,
		};

	private static object CreateHelmetMergeValidationProjection(CliHelmetToArmorMergeValidationResult validation)
		=> new
		{
			isValid = validation.IsValid,
			requestedInputFingerprint = validation.Document.InputFingerprint,
			recomputedInputFingerprint = validation.Current?.Document.InputFingerprint,
			resolvedSourceCount = validation.ResolvedSelections.Count,
			resolvedCandidateCount = validation.ResolvedSelections.Sum(pair => pair.Value.Count),
			current = validation.Current is null ? null : CreateHelmetMergePlanProjection(validation.Current.Document),
			diagnostics = validation.Diagnostics,
		};

	private static object CreateHelmetMergeExecutionProjection(CliHelmetToArmorMergeExecutionResult execution)
		=> new
		{
			execution.Success,
			execution.OutputDirectory,
			sources = execution.Sources,
			diagnostics = execution.Diagnostics,
			libraryMutation = "not-performed",
			deployment = "not-performed",
		};

	private static object CreateCrossArmorPlanProjection(CliCrossArmorTransferPlanDocument document)
		=> new
		{
			document.Schema,
			document.Version,
			document.CreatedUtc,
			ready = document.IsReady,
			document.InputFingerprint,
			request = document.Request,
			source = document.Source,
			targetCandidates = document.TargetCandidates,
			plan = document.Plan,
			diagnostics = document.Diagnostics,
	};

	private static object CreateArmorNormalizationPlanProjection(CliArmorNormalizationPlanDocument document)
		=> new { document.Schema, document.Version, document.CreatedUtc, ready = document.IsReady, document.InputFingerprint, document.SourceNodeId, document.SourceModName, document.SourcePatchPaths, armors = document.Armors, diagnostics = document.Diagnostics };

	private static object CreateArmorNormalizationValidationProjection(CliArmorNormalizationValidationResult validation)
		=> new { isValid = validation.IsValid, requestedInputFingerprint = validation.Document.InputFingerprint, recomputedInputFingerprint = validation.Current?.InputFingerprint, current = validation.Current is null ? null : CreateArmorNormalizationPlanProjection(validation.Current), diagnostics = validation.Diagnostics };

	private static object CreateCrossArmorValidationProjection(CliCrossArmorTransferValidationResult validation)
		=> new
		{
			isValid = validation.IsValid,
			requestedInputFingerprint = validation.Document.InputFingerprint,
			recomputedInputFingerprint = validation.Current?.Document.InputFingerprint,
			current = validation.Current is null ? null : CreateCrossArmorPlanProjection(validation.Current.Document),
			diagnostics = validation.Diagnostics,
		};

	private static object CreateCrossArmorExecutionProjection(CliCrossArmorTransferExecutionResult execution)
		=> new
		{
			execution.Success,
			execution.OutputDirectory,
			execution.ReportPath,
			execution.OutputUnitCount,
			execution.ReplacementMeshCount,
			execution.MinifiedMeshCount,
			diagnostics = execution.Diagnostics,
			libraryMutation = "not-performed",
			deployment = "not-performed",
		};

	private static object CreateHostDerivedPlanProjection(CliHostDerivedPlanDocument document)
		=> new
		{
			document.Schema,
			document.Version,
			document.CreatedUtc,
			ready = document.IsReady,
			noChanges = document.Hosts.Count == 0,
			document.InputFingerprint,
			request = document.Request,
			target = document.Target,
			hosts = document.Hosts.Select(host => new
			{
				host.NodeId,
				host.Name,
				host.Kind,
				host.RelativePath,
				host.PatchGroupCount,
				current = new
				{
					helmetToArmorMerged = host.CurrentHelmetToArmorMerged,
					decorations = host.CurrentDecorations,
				},
				planned = new
				{
					helmetToArmorMerged = host.PlannedHelmetToArmorMerged,
					decorations = host.PlannedDecorations,
				},
				overwrite = host.Overwrite,
			}).ToArray(),
			inputs = document.Inputs,
			diagnostics = document.Diagnostics,
		};

	private static object CreateHostDerivedValidationProjection(CliHostDerivedValidationResult validation)
		=> new
		{
			isValid = validation.IsValid,
			requestedInputFingerprint = validation.Document.InputFingerprint,
			recomputedInputFingerprint = validation.Current?.Document.InputFingerprint,
			current = validation.Current is null ? null : CreateHostDerivedPlanProjection(validation.Current.Document),
			diagnostics = validation.Diagnostics,
		};

	private static object CreateHostDerivedExecutionProjection(CliHostDerivedExecutionResult execution)
		=> new
		{
			execution.Success,
			execution.StateCommitted,
			execution.StateMutation,
			hosts = execution.Hosts,
			rebuiltHostIds = execution.Hosts.Where(host => host.PlannedRebuildSucceeded).Select(host => host.NodeId).ToArray(),
			rollbackAttemptedHostIds = execution.Hosts.Where(host => host.RollbackAttempted).Select(host => host.NodeId).ToArray(),
			rollbackSucceededHostIds = execution.Hosts.Where(host => host.RollbackSucceeded).Select(host => host.NodeId).ToArray(),
			diagnostics = execution.Diagnostics,
			gameData = "read-only",
		};

	private static object CreateGameDataIndexProjection(CliGameDataIndexInspection inspection)
		=> new
		{
			target = new
			{
				directory = inspection.GameDataDirectory,
				readability = inspection.TargetIsValid ? "readable" : "invalid",
				installationSignature = "unverified",
			},
			index = new
			{
				state = inspection.IndexState,
				mappingTrusted = inspection.IsCurrent,
				stored = CreateGameDataIndexFingerprintNullableProjection(inspection.StoredFingerprint),
				observedSourceFingerprint = inspection.CurrentSourceFingerprint,
				archiveHashesAvailable = inspection.ArchiveHashesAvailable,
			},
			diagnostics = CreateGameDataDiagnosticsProjection(inspection.Diagnostics),
		};

	private static object CreateGameDataIndexPlanProjection(CliGameDataIndexPlanDocument document)
		=> new
		{
			document.Schema,
			document.Version,
			document.CreatedUtc,
			ready = document.IsReady,
			document.InputFingerprint,
			request = document.Request,
			inputs = document.Inputs,
			diagnostics = CreateGameDataDiagnosticsProjection(document.Diagnostics),
		};

	private static object CreateGameDataIndexValidationProjection(CliGameDataIndexPlanValidationResult validation)
		=> new
		{
			isValid = validation.IsValid,
			requestedInputFingerprint = validation.Document.InputFingerprint,
			recomputedInputFingerprint = validation.Current?.Document.InputFingerprint,
			current = validation.Current is null ? null : CreateGameDataIndexPlanProjection(validation.Current.Document),
			diagnostics = CreateGameDataDiagnosticsProjection(validation.Diagnostics),
		};

	private static object CreateGameDataIndexExecutionProjection(CliGameDataIndexExecutionResult execution)
		=> new
		{
			execution.Success,
			execution.Operation,
			execution.BaseIndexCurrent,
			execution.AdvancedEquipmentFactsCurrent,
			diagnostics = CreateGameDataDiagnosticsProjection(execution.Diagnostics),
			gameDataMutation = "not-performed",
			libraryMutation = "not-performed",
		};

	private static object CreateAssetMetadataInspectionProjection(CliAssetMetadataInspection inspection)
		=> new
		{
			managerRoot = inspection.ManagerRoot,
			settingsPath = inspection.SettingsPath,
			effectiveRepository = inspection.EffectiveRepository,
			lastCheckUtc = inspection.LastCheckUtc,
			ready = inspection.IsReady,
			local = CreateAssetMetadataSnapshotProjection(inspection.Local),
			diagnostics = CreateAssetMetadataDiagnosticsProjection(inspection.Diagnostics),
		};

	private static object CreateAssetMetadataPlanProjection(CliAssetMetadataPlanDocument document)
		=> new
		{
			document.Schema,
			document.Version,
			document.CreatedUtc,
			ready = document.IsReady,
			document.InputFingerprint,
			request = new
			{
				document.Request.RepositoryInput,
				document.Request.RepositoryFromSettings,
				document.Request.StagingRootDirectory,
			},
			inputs = new
			{
				document.Inputs.ManagerRoot,
				document.Inputs.SettingsPath,
				current = CreateAssetMetadataSnapshotProjection(document.Inputs.Current),
			},
			staging = new
			{
				document.Staging.RepositoryRawBaseUrl,
				snapshot = CreateAssetMetadataSnapshotProjection(document.Staging.Snapshot),
			},
			diagnostics = CreateAssetMetadataDiagnosticsProjection(document.Diagnostics),
		};

	private static object CreateAssetMetadataValidationProjection(CliAssetMetadataPlanValidationResult validation)
		=> new
		{
			isValid = validation.IsValid,
			requestedInputFingerprint = validation.Document.InputFingerprint,
			current = validation.Current is null ? null : CreateAssetMetadataInspectionProjection(validation.Current),
			diagnostics = CreateAssetMetadataDiagnosticsProjection(validation.Diagnostics),
		};

	private static object CreateAssetMetadataExecutionProjection(CliAssetMetadataExecutionResult execution)
		=> new
		{
			execution.Success,
			execution.SettingsUpdated,
			execution.RollbackAttempted,
			execution.RollbackSucceeded,
			execution.PreservedBackupDirectory,
			local = execution.Local is null ? null : CreateAssetMetadataSnapshotProjection(execution.Local),
			diagnostics = CreateAssetMetadataDiagnosticsProjection(execution.Diagnostics),
			gameDataMutation = "not-performed",
			libraryMutation = "not-performed",
		};

	private static object CreateAssetMetadataSnapshotProjection(CliAssetMetadataSnapshot snapshot)
		=> new
		{
			snapshot.ResourcesDirectory,
			snapshot.ManifestReadable,
			snapshot.ManifestMatchesFiles,
			snapshot.ManifestSource,
			snapshot.ManifestUpdatedAtUtc,
			snapshot.Fingerprint,
			completeAndVerified = snapshot.IsCompleteAndVerified,
			files = snapshot.Files.Select(file => new { file.FileName, file.Exists, file.Length, file.Sha256 }).ToArray(),
			diagnostics = CreateAssetMetadataDiagnosticsProjection(snapshot.Diagnostics),
		};

	private static object[] CreateAssetMetadataDiagnosticsProjection(IEnumerable<CliAssetMetadataDiagnostic> diagnostics)
		=> diagnostics.Select(diagnostic => (object)new
		{
			diagnostic.Code,
			diagnostic.Severity,
			diagnostic.Blocking,
			diagnostic.Message,
			diagnostic.Path,
		}).ToArray();

	private static object CreateGameDataArchiveListProjection(CliGameDataArchiveListInspection inspection, CliGlobalOptions options)
	{
		var page = CliGameDataPaging.Slice(
			inspection.Archives.OrderBy(archive => archive.PackageName, StringComparer.OrdinalIgnoreCase).ToArray(),
			"archive-list",
			GetGameDataIndexGeneration(inspection.Index),
			options.Limit,
			options.Cursor);
		return new
		{
			index = CreateGameDataIndexProjection(inspection.Index),
			totalCount = page.TotalCount,
			offset = page.Offset,
			limit = page.Limit,
			items = page.Items.Select(CreateGameDataArchiveSummaryProjection).ToArray(),
			page.NextCursor,
			diagnostics = CreateGameDataDiagnosticsProjection(inspection.Diagnostics),
		};
	}

	private static object CreateGameDataArchiveDetailsProjection(
		CliGameDataArchiveDetailsInspection inspection,
		CliGlobalOptions options,
		CliGameDataOverrideInspection? overrides)
	{
		var assets = inspection.Archive?.Assets
			.OrderBy(asset => asset.AssetKey.TypeId)
			.ThenBy(asset => asset.AssetKey.FileId)
			.ToArray()
			?? Array.Empty<GameDataArchiveAssetEntry>();
		var page = CliGameDataPaging.Slice(
			assets,
			$"archive-show:{inspection.RequestedArchiveId.Trim().ToLowerInvariant()}",
			GetGameDataIndexGeneration(inspection.Index),
			options.Limit,
			options.Cursor);
		return new
		{
			index = CreateGameDataIndexProjection(inspection.Index),
			requestedArchiveId = inspection.RequestedArchiveId,
			archive = inspection.Archive is null ? null : CreateGameDataArchiveSummaryProjection(inspection.Archive.Summary),
			totalAssetCount = assets.Length,
			offset = page.Offset,
			limit = page.Limit,
			assets = page.Items.Select(asset => CreateGameDataArchiveAssetProjection(asset, overrides)).ToArray(),
			page.NextCursor,
			effectiveOverride = overrides is null ? null : new
			{
				available = overrides.IsReadable,
				trusted = overrides.IsTrusted,
				scope = "data-patch-only",
				generation = overrides.OverrideGraph?.DeploymentGeneration,
			},
			diagnostics = CreateGameDataDiagnosticsProjection(inspection.Diagnostics.Concat(overrides?.Diagnostics ?? Array.Empty<CliGameDataDiagnostic>())),
		};
	}

	private static object CreateGameDataAssetLookupProjection(CliGameDataAssetLookupInspection inspection)
		=> new
		{
			index = CreateGameDataIndexProjection(inspection.Index),
			matchCount = inspection.Matches.Count,
			matches = inspection.Matches.Select(match => new
			{
				assetKey = CreateGameDataAssetKeyProjection(match.AssetKey),
				match.Found,
				archives = match.Archives.Select(CreateGameDataArchiveReferenceProjection).ToArray(),
			}).ToArray(),
			diagnostics = CreateGameDataDiagnosticsProjection(inspection.Diagnostics),
		};

	private static object CreateGameDataOverrideStatusProjection(CliGameDataOverrideInspection inspection)
	{
		var graph = inspection.OverrideGraph;
		return new
		{
			target = new
			{
				directory = inspection.GameDataDirectory,
				readability = inspection.TargetIsValid ? "readable" : "invalid",
				installationSignature = inspection.InstallationSignature,
			},
			trusted = inspection.IsTrusted,
			scope = "data-patch-only",
			status = graph is null ? null : new
			{
				generation = graph.DeploymentGeneration,
				graph.BuiltUtc,
				recordedProfileId = graph.RecordedProfileId is { } profileId ? CliProjections.FormatId(profileId) : null,
				graph.RecordedProfileRevision,
				patchGroupCount = graph.PatchGroups.Count,
				assetChainCount = graph.AssetChains.Count,
				competitionCount = graph.AssetChains.Count(chain => chain.IsCompetition),
				invalidPatchGroupCount = graph.PatchGroups.Count(group => !group.IsValid),
			},
			diagnostics = CreateGameDataDiagnosticsProjection(inspection.Diagnostics),
		};
	}

	private static object CreateGameDataOverrideListProjection(CliGameDataOverrideInspection inspection, CliGlobalOptions options)
	{
		var graph = inspection.OverrideGraph;
		var chains = graph?.AssetChains
			.Where(chain => !options.OnlyCompetition || chain.IsCompetition)
			.OrderBy(chain => chain.AssetKey.TypeId)
			.ThenBy(chain => chain.AssetKey.FileId)
			.ToArray()
			?? Array.Empty<DeployedAssetOverrideChain>();
		var page = CliGameDataPaging.Slice(
			chains,
			options.OnlyCompetition ? "override-list:competition" : "override-list:all",
			graph?.DeploymentGeneration ?? "unavailable",
			options.Limit,
			options.Cursor);
		return new
		{
			status = CreateGameDataOverrideStatusProjection(inspection),
			totalCount = page.TotalCount,
			offset = page.Offset,
			limit = page.Limit,
			onlyCompetition = options.OnlyCompetition,
			items = page.Items.Select(CreateGameDataOverrideChainSummaryProjection).ToArray(),
			page.NextCursor,
		};
	}

	private static object CreateGameDataOverrideShowProjection(CliGameDataOverrideInspection inspection, AssetKey key)
	{
		var chain = inspection.OverrideGraph?.AssetChains.FirstOrDefault(candidate => candidate.AssetKey == key);
		return new
		{
			status = CreateGameDataOverrideStatusProjection(inspection),
			assetKey = CreateGameDataAssetKeyProjection(key),
			found = chain is not null,
			isCompetition = chain?.IsCompetition ?? false,
			entries = chain?.Entries.Select(entry => CreateGameDataOverrideEntryProjection(entry, inspection.OverrideGraph!)).ToArray() ?? Array.Empty<object>(),
		};
	}

	private static object CreateGameDataBrowserProjection(CliGameDataBrowserInspection inspection, CliGlobalOptions options)
	{
		var browser = inspection.Browser;
		var items = browser?.Archives
			.OrderBy(item => item.Archive.PackageName, StringComparer.OrdinalIgnoreCase)
			.ToArray()
			?? Array.Empty<GameDataArchiveBrowserItem>();
		var page = CliGameDataPaging.Slice(
			items,
			"browser-archives",
			browser?.Fingerprint.SourceFingerprint ?? GetGameDataIndexGeneration(inspection.Index),
			options.Limit,
			options.Cursor);
		return new
		{
			index = CreateGameDataIndexProjection(inspection.Index),
			browser = browser is null ? null : new
			{
				fingerprint = CreateGameDataIndexFingerprintProjection(browser.Fingerprint),
				activeProfileId = browser.ActiveProfileId is { } activeProfileId ? CliProjections.FormatId(activeProfileId) : null,
				totalArchiveCount = page.TotalCount,
				offset = page.Offset,
				limit = page.Limit,
				archives = page.Items.Select(item => CreateGameDataBrowserArchiveProjection(item, browser.ModNames)).ToArray(),
				page.NextCursor,
				issues = CreateCoreDiagnosticsProjection(browser.Issues),
			},
			diagnostics = CreateGameDataDiagnosticsProjection(inspection.Diagnostics),
		};
	}

	private static object CreateGameDataArchiveSummaryProjection(GameDataArchiveSummary archive)
		=> new
		{
			archive.PackageName,
			archive.DisplayName,
			archive.Category,
			assetKeyCount = archive.EntryCount,
			indexStatus = archive.Status,
		};

	private static object CreateGameDataArchiveAssetProjection(GameDataArchiveAssetEntry asset, CliGameDataOverrideInspection? overrides)
	{
		var chain = overrides?.OverrideGraph?.AssetChains.FirstOrDefault(candidate => candidate.AssetKey == asset.AssetKey);
		return new
		{
			assetKey = CreateGameDataAssetKeyProjection(asset.AssetKey),
			asset.TypeName,
			asset.FriendlyName,
			asset.PartSummary,
			sharedArchives = asset.SharedPackages.Zip(asset.SharedDisplayNames, (packageName, displayName) => new { packageName, displayName }).ToArray(),
			effectiveOverride = chain is null ? null : new
			{
				trusted = overrides!.IsTrusted,
				winner = CreateGameDataOverrideWinnerProjection(chain.Winner),
				chain.IsCompetition,
			},
		};
	}

	private static object CreateGameDataArchiveReferenceProjection(ArchiveMetadata archive)
		=> new { archiveId = archive.ArchiveId, archive.DisplayName, archive.Category };

	private static object CreateGameDataAssetKeyProjection(AssetKey key)
		=> new { typeId = $"0x{key.TypeId:x16}", fileId = $"0x{key.FileId:x16}" };

	private static object? CreateGameDataIndexFingerprintNullableProjection(GameDataIndexFingerprint? fingerprint)
		=> fingerprint is null ? null : CreateGameDataIndexFingerprintProjection(fingerprint);

	private static object CreateGameDataIndexFingerprintProjection(GameDataIndexFingerprint fingerprint)
		=> new
		{
			fingerprint.GameDataDirectory,
			fingerprint.BuiltUtc,
			fingerprint.ArchivesTotal,
			fingerprint.ArchivesIndexed,
			fingerprint.AssetKeysTotal,
			fingerprint.SourceFingerprint,
		};

	private static object CreateGameDataOverrideChainSummaryProjection(DeployedAssetOverrideChain chain)
		=> new
		{
			assetKey = CreateGameDataAssetKeyProjection(chain.AssetKey),
			entryCount = chain.Entries.Count,
			chain.IsCompetition,
			winner = CreateGameDataOverrideWinnerProjection(chain.Winner),
		};

	private static object CreateGameDataOverrideWinnerProjection(DeployedAssetOverrideEntry entry)
		=> new
		{
			entry.ArchiveHex16,
			entry.TargetPatchIndex,
			source = entry.SourcePatchGroupId is { } source ? new
			{
				nodeId = CliProjections.FormatId(source.NodeId),
				source.SourceArchiveHex,
				source.SourcePatchIndex,
			} : null,
			nodeId = entry.NodeId is { } nodeId ? CliProjections.FormatId(nodeId) : null,
		};

	private static object CreateGameDataOverrideEntryProjection(DeployedAssetOverrideEntry entry, DeployedOverrideGraph graph)
	{
		var group = graph.PatchGroups.FirstOrDefault(candidate => string.Equals(candidate.ArchiveHex16, entry.ArchiveHex16, StringComparison.OrdinalIgnoreCase) && candidate.TargetPatchIndex == entry.TargetPatchIndex);
		return new
		{
			entry.ArchiveHex16,
			entry.TargetPatchIndex,
			entry.IsWinner,
			source = entry.SourcePatchGroupId is { } source ? new
			{
				nodeId = CliProjections.FormatId(source.NodeId),
				source.SourceArchiveHex,
				source.SourcePatchIndex,
			} : null,
			nodeId = entry.NodeId is { } nodeId ? CliProjections.FormatId(nodeId) : null,
			sourceGroupValid = group?.IsValid ?? false,
			files = group?.Files.Select(file => new
			{
				fileName = Path.GetFileName(file.TargetPath),
				sidecarKind = file.SidecarKind.ToString().ToLowerInvariant(),
				file.Length,
			}).ToArray() ?? Array.Empty<object>(),
		};
	}

	private static object CreateGameDataBrowserArchiveProjection(GameDataArchiveBrowserItem item, IReadOnlyDictionary<ModNodeId, string> modNames)
		=> new
		{
			archive = CreateGameDataArchiveSummaryProjection(item.Archive),
			overlay = new
			{
				libraryMods = item.Overlay.LibraryModIds.Select(id => CreateGameDataModReferenceProjection(id, modNames)).ToArray(),
				activeMods = item.Overlay.ActiveModIds.Select(id => CreateGameDataModReferenceProjection(id, modNames)).ToArray(),
				effectiveMods = item.Overlay.EffectiveModIds.Select(id => CreateGameDataModReferenceProjection(id, modNames)).ToArray(),
				item.Overlay.EffectiveTargetPatchIndexes,
				effectiveAssets = item.Overlay.EffectiveAssets.Select(asset => new
				{
					assetKey = CreateGameDataAssetKeyProjection(asset.AssetKey),
					winner = asset.WinnerNodeId is { } nodeId ? CreateGameDataModReferenceProjection(nodeId, modNames) : null,
					asset.TargetPatchIndex,
					asset.HasCompetition,
				}).ToArray(),
				item.Overlay.HasCompetition,
				issues = CreateCoreDiagnosticsProjection(item.Overlay.Issues),
			},
		};

	private static object CreateGameDataModReferenceProjection(ModNodeId id, IReadOnlyDictionary<ModNodeId, string> modNames)
		=> new { nodeId = CliProjections.FormatId(id), name = modNames.GetValueOrDefault(id) ?? "未知 Mod" };

	private static object[] CreateGameDataDiagnosticsProjection(IEnumerable<CliGameDataDiagnostic> diagnostics)
		=> diagnostics.Select(diagnostic => (object)new
		{
			diagnostic.Code,
			diagnostic.Severity,
			diagnostic.Blocking,
			diagnostic.Message,
		}).ToArray();

	private static object[] CreateCoreDiagnosticsProjection(IEnumerable<CoreIssue> diagnostics)
		=> diagnostics.Select(issue => (object)new
		{
			issue.Code,
			severity = issue.Severity == CoreIssueSeverity.Error ? "error" : "warning",
			blocking = issue.Severity == CoreIssueSeverity.Error,
			issue.Message,
		}).ToArray();

	private static string GetGameDataIndexGeneration(CliGameDataIndexInspection inspection)
		=> inspection.CurrentSourceFingerprint
			?? inspection.StoredFingerprint?.SourceFingerprint
			?? $"{inspection.IndexState}:{inspection.GameDataDirectory}";

	private static object CreateBatchPatchRepairPlanProjection(CliBatchPatchRepairPlanDocument document)
		=> new
		{
			document.Schema,
			document.Version,
			document.CreatedUtc,
			ready = document.IsReady,
			document.InputFingerprint,
			document.ManagerRoot,
			document.LibraryRoot,
			gameData = document.GameData,
			selectedModCount = document.SelectedNodeIds.Count,
			outdatedModCount = document.Mods.Count(mod => mod.IsOutdated),
			eligibleModCount = document.Mods.Count(mod => mod.CanAttemptGenerate),
			mods = document.Mods,
			diagnostics = document.Diagnostics,
		};

	private static object CreateBatchPatchRepairValidationProjection(CliBatchPatchRepairValidationResult validation)
		=> new
		{
			isValid = validation.IsValid,
			requestedInputFingerprint = validation.Document.InputFingerprint,
			recomputedInputFingerprint = validation.Current?.Document.InputFingerprint,
			current = validation.Current is null ? null : CreateBatchPatchRepairPlanProjection(validation.Current.Document),
			diagnostics = validation.Diagnostics,
		};

	private static object CreateBatchPatchRepairExecutionProjection(CliBatchPatchRepairExecutionResult execution)
		=> new
		{
			execution.Success,
			execution.LibrarySnapshotUpdated,
			execution.SelectedModCount,
			execution.RepairedModCount,
			execution.SkippedModCount,
			execution.FailedModCount,
			execution.CanceledModCount,
			execution.NotStartedModCount,
			execution.BackupDirectory,
			execution.ManifestPath,
			mods = execution.Mods,
			overwrites = execution.Overwrites,
			diagnostics = execution.Diagnostics,
		};

	private static object CreateSameKeyInspectionProjection(CliSameKeyReconstructionInspection inspection)
		=> new
		{
			inspection.SourceNodeId,
			inspection.SourceModName,
			availableSourcePatchFileNames = inspection.AvailableSourcePatchFileNames,
			inspection.SelectedSourcePatchFileName,
			sourcePatch = inspection.SourcePatch,
			gameData = inspection.GameData,
			summary = inspection.Summary,
			units = inspection.Units,
			diagnostics = inspection.Diagnostics,
		};

	private static object CreateSameKeyPlanProjection(CliSameKeyReconstructionPlanDocument document)
		=> new
		{
			document.Schema,
			document.Version,
			document.CreatedUtc,
			ready = document.IsReady,
			document.InputFingerprint,
			request = document.Request,
			inspection = CreateSameKeyInspectionProjection(document.Inspection),
		};

	private static object CreateSameKeyValidationProjection(CliSameKeyReconstructionValidationResult validation)
		=> new
		{
			isValid = validation.IsValid,
			requestedInputFingerprint = validation.Document.InputFingerprint,
			recomputedInputFingerprint = validation.Current?.Document.InputFingerprint,
			current = validation.Current is null ? null : CreateSameKeyPlanProjection(validation.Current.Document),
			diagnostics = validation.Diagnostics,
		};

	private static object CreateSameKeyExecutionProjection(CliSameKeyReconstructionExecutionResult execution)
		=> new
		{
			execution.Success,
			execution.OutputDirectory,
			execution.ReportJsonPath,
			execution.ReportMarkdownPath,
			execution.OutputUnitCount,
			execution.ReplacementUnitCount,
			execution.MinifyOnlyUnitCount,
			execution.ReplacementMeshCount,
			execution.MinifiedMeshCount,
			diagnostics = execution.Diagnostics,
			libraryMutation = "not-performed",
			deployment = "not-performed",
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
			addToProfile = validation.Document.AddToProfile,
			resolvedProfile = validation.ResolvedProfile,
			currentImport = validation.CurrentInspection is null ? null : CliProjections.CreateImportPlanProjection(validation.CurrentInspection.Import),
			issues = validation.Issues.Select(issue => new { code = issue.Code, message = issue.Message, isBlocking = issue.IsBlocking }).ToArray(),
		};

	private static object CreateBatchImportPlanProjection(CliBatchImportPlanDocument document)
		=> new
		{
			document.Schema,
			document.Version,
			document.CreatedUtc,
			document.PlanFingerprint,
			sourceCount = document.Imports.Count,
			addToProfile = document.AddToProfile,
			sequential = true,
			rollback = "completed-sources-are-preserved",
			imports = document.Imports.Select((import, index) => new
			{
				index,
				source = import.Source,
				sourceFingerprint = import.SourceFingerprint,
				importFingerprint = import.ImportFingerprint,
				import = CliProjections.CreateImportPlanProjection(import.Import),
			}).ToArray(),
		};

	private static object CreateBatchImportValidationProjection(CliBatchImportPlanValidationResult validation)
		=> new
		{
			isValid = validation.IsValid,
			sourceCount = validation.Sources.Count,
			sources = validation.Sources.Select(source => new
			{
				source.Index,
				sourcePath = source.Document.Source.RequestedPath,
				sourceKind = source.Document.Source.Kind,
				isValid = source.Validation.IsValid,
				validation = CreateImportValidationProjection(source.Validation),
			}).ToArray(),
		};

	private static object CreateImportExecutionProjection(
		ModImportExecutionResult result,
		string libraryRoot,
		CliImportProfileAddition? profileAddition)
		=> new
		{
			libraryRoot,
			sourceDisplayName = result.SourceDisplayName,
			importedNodeIds = result.ImportedNodeIds.Select(CliProjections.FormatId).ToArray(),
			addedNodeIds = result.AddedNodeIds.Select(CliProjections.FormatId).ToArray(),
			changedNodeIds = result.ChangedNodeIds.Select(CliProjections.FormatId).ToArray(),
			affectedNodeIds = result.AffectedNodeIds.Select(CliProjections.FormatId).ToArray(),
			initializedOptionActivationCount = result.InitializedOptionActivationCount,
			hasOptionPathConflicts = result.HasOptionPathConflicts,
			cacheInvalidation = result.CacheInvalidationStatus.ToString().ToLowerInvariant(),
			cacheInvalidationFailedNodeIds = result.CacheInvalidationFailedNodeIds.Select(CliProjections.FormatId).ToArray(),
			profileAddition,
			derivedInformation = "not-started",
			deployment = "not-performed",
		};

	private static object CreateBatchImportExecutionProjection(CliBatchImportExecutionResult execution)
	{
		var libraryRoot = execution.Validation.Target?.LibraryRoot;
		return new
		{
			execution.Status,
			execution.Success,
			sequential = true,
			rollback = "completed-sources-are-preserved",
			profileAddition = execution.ProfileAddition,
			execution.ErrorCode,
			execution.ErrorMessage,
			sources = execution.Sources.Select(source => new
			{
				source.Index,
				source.SourcePath,
				source.Status,
				result = source.Result is null || string.IsNullOrWhiteSpace(libraryRoot)
					? null
					: CreateImportExecutionProjection(source.Result, libraryRoot, null),
				source.ErrorCode,
				source.ErrorMessage,
			}).ToArray(),
			validation = CreateBatchImportValidationProjection(execution.Validation),
		};
	}

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

	private static object CreateModRelationshipMutationProjection(ModRelationshipMutationResult result)
		=> new
		{
			changed = result.Changed,
			hostModId = CliProjections.FormatId(result.HostId),
			changedModIds = result.ChangedNodeIds.Select(CliProjections.FormatId).ToArray(),
			addedOptionParentModIds = result.AddedRelationOptionIds.Select(CliProjections.FormatId).ToArray(),
			removedOptionParentModIds = result.RemovedRelationOptionIds.Select(CliProjections.FormatId).ToArray(),
			affectedProfileIds = result.AffectedProfileIds.Select(CliProjections.FormatId).ToArray(),
			deployment = "not-performed",
		};

	private static object CreateOptionAllHostsInspectionProjection(
		ModNode option,
		OptionAllHostsRelationshipSnapshot scope)
		=> new
		{
			optionId = CliProjections.FormatId(option.Id),
			optionName = option.Metadata.Name,
			canMutate = scope.CanMutate,
			summary = new
			{
				eligibleHostCount = scope.EligibleHosts.Count,
				enabledHostCount = scope.EligibleHosts.Count(host => host.Enabled),
				disabledHostCount = scope.EligibleHosts.Count(host => !host.Enabled),
				missingExplicitStateCount = scope.EligibleHosts.Count(host => !host.HasExplicitActivationState),
				issueCount = scope.Issues.Count,
				orphanActivationStateCount = scope.OrphanActivationStates.Count,
			},
			eligibleDirectHosts = scope.EligibleHosts.Select(host => new
			{
				hostId = CliProjections.FormatId(host.HostId),
				host.HostName,
				hostKind = host.HostKind.ToString().ToLowerInvariant(),
				host.Enabled,
				host.HasExplicitActivationState,
				host.EffectiveOrder,
			}).ToArray(),
			issues = scope.Issues.Select(issue => new
			{
				issue.Code,
				issue.Message,
				hostId = issue.HostId is { } hostId ? CliProjections.FormatId(hostId) : null,
				cyclePath = issue.CyclePath?.Select(CliProjections.FormatId).ToArray() ?? Array.Empty<string>(),
			}).ToArray(),
			orphanActivationStates = scope.OrphanActivationStates,
		};

	private static object CreateOptionAllHostsMutationProjection(CliOptionAllHostsMutationResult result)
		=> new
		{
			optionId = CliProjections.FormatId(result.Option.Id),
			optionName = result.Option.Metadata.Name,
			requestedEnabled = result.RequestedEnabled,
			changed = result.Mutation.Changed,
			before = CreateOptionAllHostsScopeProjection(result.Mutation.Before),
			after = CreateOptionAllHostsScopeProjection(result.Mutation.After),
			deployment = "not-performed",
		};

	private static object CreateOptionAllHostsScopeProjection(OptionAllHostsRelationshipSnapshot scope)
		=> new
		{
			canMutate = scope.CanMutate,
			eligibleDirectHosts = scope.EligibleHosts.Select(host => new
			{
				hostId = CliProjections.FormatId(host.HostId),
				host.HostName,
				hostKind = host.HostKind.ToString().ToLowerInvariant(),
				host.Enabled,
				host.HasExplicitActivationState,
				host.EffectiveOrder,
			}).ToArray(),
			issues = scope.Issues.Select(issue => new
			{
				issue.Code,
				issue.Message,
				hostId = issue.HostId is { } hostId ? CliProjections.FormatId(hostId) : null,
				cyclePath = issue.CyclePath?.Select(CliProjections.FormatId).ToArray() ?? Array.Empty<string>(),
			}).ToArray(),
			orphanActivationStates = scope.OrphanActivationStates,
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

	private static object CreateModDeletionPlanDocumentProjection(CliModDeletionPlanDocument document)
		=> new
		{
			document.Schema,
			document.Version,
			document.CreatedUtc,
			document.InputFingerprint,
			isReady = document.IsReady,
			request = new
			{
				nodeIds = document.Request.NodeIds,
				scope = document.Request.Scope.ToString().ToLowerInvariant(),
			},
			target = document.Target,
			deletion = CreateDeletionPlanProjection(document.Deletion),
			inputs = document.Inputs,
			diagnostics = document.Diagnostics,
		};

	private static object CreateModDeletionValidationProjection(CliModDeletionValidationResult validation)
		=> new
		{
			isValid = validation.IsValid,
			requestedInputFingerprint = validation.Document.InputFingerprint,
			recomputedInputFingerprint = validation.Current?.Document.InputFingerprint,
			current = validation.Current is null ? null : CreateModDeletionPlanDocumentProjection(validation.Current.Document),
			diagnostics = validation.Diagnostics,
		};

	private static object CreateModDeletionApplyProjection(CliModDeletionApplyResult execution)
		=> new
		{
			execution.Success,
			deletion = execution.Deletion is null ? null : CreateDeletionExecutionProjection(execution.Deletion),
			diagnostics = execution.Diagnostics,
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

	private static string FormatPatchResourceRepackagingInspection(CliPatchResourceRepackagingPlanDocument document)
	{
		var conflicts = document.Candidates.Count(candidate => candidate.IsConflict);
		return $"Patch 资源扫描完成：来源 Mod {document.SourceNodeIds.Count} 个；Patch 组 {document.PatchGroups.Count} 个；资源变体 {document.Candidates.Count} 个；冲突变体 {conflicts} 个。";
	}

	private static string FormatPatchResourceRepackagingPlan(CliPatchResourceRepackagingPlanDocument document, string outputPath)
	{
		var blocking = document.Diagnostics.Count(diagnostic => diagnostic.Blocking);
		return string.Join(Environment.NewLine,
		[
			$"已导出 Patch 资源重打包计划：{outputPath}",
			$"来源 Mod：{document.SourceNodeIds.Count}；Patch 组：{document.PatchGroups.Count}；候选变体：{document.Candidates.Count}；已默认选择：{document.Selections.Count}",
			$"阻断诊断：{blocking}。请审阅或编辑 selections 后执行 validate。",
		]);
	}

	private static string FormatPatchResourceRepackagingValidation(CliPatchResourceRepackagingValidationResult validation)
	{
		var status = validation.IsValid ? "Patch 资源重打包验证通过。" : "Patch 资源重打包验证未通过。";
		var issues = validation.Diagnostics.Count == 0
			? "无诊断。"
			: string.Join(Environment.NewLine, validation.Diagnostics.Select(diagnostic => $"- [{diagnostic.Code}] {diagnostic.Message}"));
		return $"{status}{Environment.NewLine}已解析选择：{validation.ResolvedSelections.Count}{Environment.NewLine}{issues}";
	}

	private static string FormatPatchResourceRepackagingExecution(CliPatchResourceRepackagingExecutionResult execution)
	{
		if (execution.Success)
			return $"Patch 资源重打包完成：{execution.SelectedAssetCount} 个 AssetKey；输出：{execution.OutputDirectory}";
		var blocking = execution.Diagnostics.Where(diagnostic => diagnostic.Blocking).Take(5).ToArray();
		return blocking.Length == 0
			? "Patch 资源重打包未完成；请查看 JSON 结果或日志。"
			: $"Patch 资源重打包被拒绝：{string.Join("；", blocking.Select(diagnostic => $"[{diagnostic.Code}] {diagnostic.Message}"))}";
	}

	private static string FormatHelmetMergeInspection(CliHelmetToArmorMergePlanDocument document)
	{
		var sharedArmor = document.Candidates.Count(candidate => candidate.IsSharedArmorTarget);
		var sharedHelmet = document.Candidates.Count(candidate => candidate.IsSharedHelmetTarget);
		return $"头盔合并检查：来源 Mod {document.Sources.Count}；候选模型集合 {document.Candidates.Count}；默认选择 {document.Selections.Count}；共享胸部 {sharedArmor}；共享头盔 {sharedHelmet}。";
	}

	private static string FormatHelmetMergePlan(CliHelmetToArmorMergePlanDocument document, string outputPath)
	{
		var blocking = document.Diagnostics.Count(diagnostic => diagnostic.Blocking);
		return string.Join(Environment.NewLine,
		[
			$"已导出头盔合并计划：{outputPath}",
			$"来源 Mod：{document.Sources.Count}；模型集合：{document.Candidates.Count}；默认选择：{document.Selections.Count}",
			$"阻断诊断：{blocking}。请审阅共享影响与 selections 后执行 validate。",
		]);
	}

	private static string FormatHelmetMergeValidation(CliHelmetToArmorMergeValidationResult validation)
	{
		var status = validation.IsValid ? "头盔合并验证通过。" : "头盔合并验证未通过。";
		var issues = validation.Diagnostics.Count == 0
			? "无诊断。"
			: string.Join(Environment.NewLine, validation.Diagnostics.Select(diagnostic => $"- [{diagnostic.Code}] {diagnostic.Message}"));
		return $"{status}{Environment.NewLine}已解析来源：{validation.ResolvedSelections.Count}；模型集合：{validation.ResolvedSelections.Sum(pair => pair.Value.Count)}{Environment.NewLine}{issues}";
	}

	private static string FormatHelmetMergeExecution(CliHelmetToArmorMergeExecutionResult execution)
	{
		if (execution.Success)
		{
			var merged = execution.Sources.Sum(source => source.MergedArmorUnitCount);
			var hidden = execution.Sources.Sum(source => source.HiddenHelmetUnitCount);
			return $"头盔合并完成：来源 {execution.Sources.Count}；胸部 Unit {merged}；隐藏头盔 Unit {hidden}；输出：{execution.OutputDirectory}";
		}
		var blocking = execution.Diagnostics.Where(diagnostic => diagnostic.Blocking).Take(5).ToArray();
		return blocking.Length == 0
			? "头盔合并未完成；未发布任何输出，请查看 JSON 结果或日志。"
			: $"头盔合并被拒绝或失败：{string.Join("；", blocking.Select(diagnostic => $"[{diagnostic.Code}] {diagnostic.Message}"))}";
	}

	private static string FormatCrossArmorInspection(CliCrossArmorTransferPlanDocument document)
		=> $"跨护甲检查：来源 {document.Source.ModName}；来源装备 {document.Source.SourceCandidates.Count}；目标装备 {document.TargetCandidates.Count}；当前已选目标 {document.Request.SelectedTargetArchiveIds.Count}；映射 {document.Plan.Mappings.Count}。";

	private static string FormatArmorNormalizationInspection(CliArmorNormalizationPlanDocument document) => $"护甲规整检查：来源 {document.SourceModName}；护甲 {document.Armors.Count}；阻断诊断 {document.Diagnostics.Count(d => d.Blocking)}。";
	private static string FormatArmorNormalizationPlan(CliArmorNormalizationPlanDocument document, string outputPath) => $"已导出护甲规整计划：{outputPath}{Environment.NewLine}护甲 {document.Armors.Count}；阻断诊断 {document.Diagnostics.Count(d => d.Blocking)}。";
	private static string FormatArmorNormalizationValidation(CliArmorNormalizationValidationResult validation) => $"{(validation.IsValid ? "护甲规整验证通过。" : "护甲规整验证未通过。")}{Environment.NewLine}{string.Join(Environment.NewLine, validation.Diagnostics.Select(d => $"- [{d.Code}] {d.Message}"))}";

	private static string FormatCrossArmorPlan(CliCrossArmorTransferPlanDocument document, string outputPath)
	{
		var blocking = document.Diagnostics.Count(diagnostic => diagnostic.Blocking);
		return string.Join(Environment.NewLine,
		[
			$"已导出跨护甲计划：{outputPath}",
			$"来源：{document.Source.ModName}；来源装备：{document.Source.SourceCandidates.Count}；目标装备：{document.TargetCandidates.Count}；当前目标：{document.Request.SelectedTargetArchiveIds.Count}",
			$"映射：{document.Plan.Mappings.Count}；预计替换：{document.Plan.Mappings.Count(mapping => mapping.WillReplace)}；预计隐藏：{document.Plan.Mappings.Count(mapping => !mapping.WillReplace)}；阻断诊断：{blocking}。",
			"请仅编辑 request 中已列出的 ArchiveId/AssetKey 选择，再执行 validate。",
		]);
	}

	private static string FormatCrossArmorValidation(CliCrossArmorTransferValidationResult validation)
	{
		var status = validation.IsValid ? "跨护甲验证通过。" : "跨护甲验证未通过。";
		var issues = validation.Diagnostics.Count == 0
			? "无诊断。"
			: string.Join(Environment.NewLine, validation.Diagnostics.Select(diagnostic => $"- [{diagnostic.Code}] {diagnostic.Message}"));
		return $"{status}{Environment.NewLine}当前映射：{validation.Current?.Document.Plan.Mappings.Count ?? 0}；预计替换：{validation.Current?.Document.Plan.Mappings.Count(mapping => mapping.WillReplace) ?? 0}；预计隐藏：{validation.Current?.Document.Plan.Mappings.Count(mapping => !mapping.WillReplace) ?? 0}{Environment.NewLine}{issues}";
	}

	private static string FormatCrossArmorExecution(CliCrossArmorTransferExecutionResult execution)
	{
		if (execution.Success)
			return $"跨护甲候选已生成：Unit {execution.OutputUnitCount}；替换 mesh {execution.ReplacementMeshCount}；极小化 mesh {execution.MinifiedMeshCount}；输出：{execution.OutputDirectory}";
		var blocking = execution.Diagnostics.Where(diagnostic => diagnostic.Blocking).Take(5).ToArray();
		return blocking.Length == 0
			? "跨护甲候选未完成；未发布任何输出，请查看 JSON 结果或日志。"
			: $"跨护甲候选被拒绝或失败：{string.Join("；", blocking.Select(diagnostic => $"[{diagnostic.Code}] {diagnostic.Message}"))}";
	}

	private static string FormatHostDerivedInspection(CliHostDerivedPlanDocument document)
	{
		var unmanaged = document.Hosts.Count(host => host.Overwrite.Exists && !host.Overwrite.IsManaged);
		return $"主体派生检查：主体 {document.Hosts.Count}；已启用装饰 {document.Hosts.Sum(host => host.CurrentDecorations.Count)}；临时头盔合并 {document.Hosts.Count(host => host.CurrentHelmetToArmorMerged)}；非管理器 Overwrite {unmanaged}。";
	}

	private static string FormatHostDerivedPlan(CliHostDerivedPlanDocument document, string outputPath)
	{
		var blocking = document.Diagnostics.Count(diagnostic => diagnostic.Blocking);
		var noChanges = document.Hosts.Count == 0 ? "；本次无状态变化" : string.Empty;
		return string.Join(Environment.NewLine,
		[
			$"已导出主体派生 Overwrite 计划：{outputPath}",
			$"操作：{document.Request.Operation}/{document.Request.Action}；主体：{document.Hosts.Count}{noChanges}",
			$"阻断诊断：{blocking}。validate 通过后，apply 才会重建库内 Overwrite 并提交相应状态。",
		]);
	}

	private static string FormatHostDerivedValidation(CliHostDerivedValidationResult validation)
	{
		var status = validation.IsValid ? "主体派生 Overwrite 验证通过。" : "主体派生 Overwrite 验证未通过。";
		var issues = validation.Diagnostics.Count == 0
			? "无诊断。"
			: string.Join(Environment.NewLine, validation.Diagnostics.Select(diagnostic => $"- [{diagnostic.Code}] {diagnostic.Message}"));
		return $"{status}{Environment.NewLine}计划主体：{validation.Document.Hosts.Count}；当前可重建主体：{validation.Current?.Document.Hosts.Count ?? 0}{Environment.NewLine}{issues}";
	}

	private static string FormatHostDerivedExecution(CliHostDerivedExecutionResult execution)
	{
		if (execution.Success)
		{
			var rebuilt = execution.Hosts.Count(host => host.PlannedRebuildSucceeded);
			return execution.StateCommitted
				? $"主体派生 Overwrite 已完成：重建主体 {rebuilt}；已提交状态：{execution.StateMutation}。"
				: rebuilt == 0
					? "主体派生 Overwrite 已确认：当前状态无需变化。"
					: $"主体派生 Overwrite 已重建：主体 {rebuilt}；未修改状态文件。";
		}
		var blocking = execution.Diagnostics.Where(diagnostic => diagnostic.Blocking).Take(5).ToArray();
		return blocking.Length == 0
			? "主体派生 Overwrite 未完成；请查看 JSON 结果或日志。"
			: $"主体派生 Overwrite 被拒绝或失败：{string.Join("；", blocking.Select(diagnostic => $"[{diagnostic.Code}] {diagnostic.Message}"))}";
	}

	private static string FormatGameDataIndex(CliGameDataIndexInspection inspection)
	{
		if (inspection.IsReady && inspection.StoredFingerprint is { } fingerprint)
			return $"GameData 资产索引可用：Archive {fingerprint.ArchivesIndexed}/{fingerprint.ArchivesTotal}；AssetKey {fingerprint.AssetKeysTotal}。";
		var blocking = inspection.Diagnostics.FirstOrDefault(diagnostic => diagnostic.Blocking);
		return blocking is null
			? "GameData 资产索引不可用。"
			: $"GameData 资产索引不可用：[{blocking.Code}] {blocking.Message}";
	}

	private static string FormatGameDataIndexMaintenanceInspection(CliGameDataIndexPlanDocument document)
		=> $"GameData 索引维护检查：基础索引={document.Inputs.BaseIndexState}；Armor / Helmet Unit 部位事实={(document.Inputs.AdvancedEquipmentFactsCurrent ? "当前" : "缺失或过期")}；可选 archivehashes={(document.Inputs.ArchiveHashes.Exists ? "已提供" : "未提供")}。";

	private static string FormatGameDataIndexMaintenancePlan(CliGameDataIndexPlanDocument document, string outputPath)
	{
		var blocking = document.Diagnostics.Count(diagnostic => diagnostic.Blocking);
		return string.Join(Environment.NewLine,
		[
			$"已导出 GameData 索引计划：{outputPath}",
			$"操作：{document.Request.Operation}；基础索引：{document.Inputs.BaseIndexState}；高级 Unit 部位事实：{(document.Inputs.AdvancedEquipmentFactsCurrent ? "当前" : "缺失或过期")}",
			$"阻断诊断：{blocking}。validate 通过后，apply 才会写入管理器的索引数据库与派生缓存。",
		]);
	}

	private static string FormatGameDataIndexMaintenanceValidation(CliGameDataIndexPlanValidationResult validation)
	{
		var status = validation.IsValid ? "GameData 索引计划验证通过。" : "GameData 索引计划验证未通过。";
		var issues = validation.Diagnostics.Count == 0
			? "无诊断。"
			: string.Join(Environment.NewLine, validation.Diagnostics.Select(diagnostic => $"- [{diagnostic.Code}] {diagnostic.Message}"));
		return $"{status}{Environment.NewLine}{issues}";
	}

	private static string FormatGameDataIndexMaintenanceExecution(CliGameDataIndexExecutionResult execution)
	{
		if (execution.Success)
			return $"GameData 索引操作完成：{execution.Operation}；基础索引当前={execution.BaseIndexCurrent}；Armor / Helmet Unit 部位事实当前={execution.AdvancedEquipmentFactsCurrent}。";
		var blocking = execution.Diagnostics.Where(diagnostic => diagnostic.Blocking).Take(5).ToArray();
		return blocking.Length == 0
			? "GameData 索引操作未完成；请查看 JSON 结果或日志。"
			: $"GameData 索引操作被拒绝或失败：{string.Join("；", blocking.Select(diagnostic => $"[{diagnostic.Code}] {diagnostic.Message}"))}";
	}

	private static string FormatAssetMetadataInspection(CliAssetMetadataInspection inspection)
	{
		var files = inspection.Local.Files.Count(file => file.Exists);
		return inspection.IsReady
			? $"在线资产元数据缓存可用：{files}/{inspection.Local.Files.Count} 个文件；来源：{inspection.Local.ManifestSource ?? inspection.EffectiveRepository}。"
			: $"在线资产元数据缓存尚未就绪：{files}/{inspection.Local.Files.Count} 个文件；请执行 assets metadata plan → validate → apply。";
	}

	private static string FormatAssetMetadataPlan(CliAssetMetadataPlanDocument document, string outputPath)
	{
		var blocking = document.Diagnostics.Count(diagnostic => diagnostic.Blocking);
		return string.Join(Environment.NewLine,
		[
			$"已导出在线资产元数据计划：{outputPath}",
			$"来源：{document.Staging.RepositoryRawBaseUrl}；暂存：{document.Request.StagingRootDirectory}",
			$"暂存文件：{document.Staging.Snapshot.Files.Count(file => file.Exists)}/{document.Staging.Snapshot.Files.Count}；阻断诊断：{blocking}。",
			"远端内容已在计划阶段下载并固定；validate/apply 不会再次联网。",
		]);
	}

	private static string FormatAssetMetadataValidation(CliAssetMetadataPlanValidationResult validation)
	{
		var status = validation.IsValid ? "在线资产元数据计划验证通过。" : "在线资产元数据计划验证未通过。";
		var issues = validation.Diagnostics.Count == 0
			? "无诊断。"
			: string.Join(Environment.NewLine, validation.Diagnostics.Select(diagnostic => $"- [{diagnostic.Code}] {diagnostic.Message}"));
		return $"{status}{Environment.NewLine}{issues}";
	}

	private static string FormatAssetMetadataExecution(CliAssetMetadataExecutionResult execution)
	{
		if (execution.Success)
			return $"在线资产元数据已更新：{execution.Local?.Files.Count(file => file.Exists) ?? 0} 个文件；设置检查时间已同步。";
		var blocking = execution.Diagnostics.Where(diagnostic => diagnostic.Blocking).Take(5).ToArray();
		return blocking.Length == 0
			? "在线资产元数据未完成；请查看 JSON 结果或日志。"
			: $"在线资产元数据更新被拒绝或失败：{string.Join("；", blocking.Select(diagnostic => $"[{diagnostic.Code}] {diagnostic.Message}"))}";
	}

	private static string FormatGameDataArchiveList(CliGameDataArchiveListInspection inspection)
		=> inspection.IsReady
			? $"GameData archive 列表：{inspection.Archives.Count} 个。"
			: $"GameData archive 列表未完全可用：{inspection.Archives.Count} 个已读取条目；请查看诊断。";

	private static string FormatGameDataArchiveDetails(CliGameDataArchiveDetailsInspection inspection)
		=> inspection.Archive is { } archive
			? $"GameData archive：{archive.Summary.DisplayName} / {archive.Summary.PackageName}；AssetKey {archive.Assets.Count}。"
			: $"未能读取指定的 GameData archive；请查看诊断。";

	private static string FormatGameDataAssetLookup(CliGameDataAssetLookupInspection inspection)
	{
		var found = inspection.Matches.Count(match => match.Found);
		return inspection.IsReady
			? $"GameData AssetKey 查询：{inspection.Matches.Count} 个键；命中 archive {found} 个。"
			: $"GameData AssetKey 查询未完全可用；请查看诊断。";
	}

	private static string FormatGameDataOverrideStatus(CliGameDataOverrideInspection inspection)
	{
		if (inspection.OverrideGraph is not { } graph) return "无法读取当前 GameData Data-Patch 覆盖图；请查看诊断。";
		var competition = graph.AssetChains.Count(chain => chain.IsCompetition);
		return $"GameData Data-Patch 覆盖图：Patch 组 {graph.PatchGroups.Count}；AssetKey 链 {graph.AssetChains.Count}；竞争 {competition}；可信={inspection.IsTrusted}。";
	}

	private static string FormatGameDataOverrideList(CliGameDataOverrideInspection inspection, bool onlyCompetition)
	{
		if (inspection.OverrideGraph is not { } graph) return "无法读取当前 GameData Data-Patch 覆盖图；请查看诊断。";
		var count = onlyCompetition ? graph.AssetChains.Count(chain => chain.IsCompetition) : graph.AssetChains.Count;
		return $"GameData Data-Patch 覆盖链：{count} 条{(onlyCompetition ? "（仅竞争）" : string.Empty)}；可信={inspection.IsTrusted}。";
	}

	private static string FormatGameDataOverrideShow(CliGameDataOverrideInspection inspection, AssetKey key)
	{
		var chain = inspection.OverrideGraph?.AssetChains.FirstOrDefault(candidate => candidate.AssetKey == key);
		return chain is null
			? $"当前 Data-Patch 覆盖图中未找到 AssetKey 0x{key.TypeId:x16}:0x{key.FileId:x16}。"
			: $"Data-Patch 覆盖链：AssetKey 0x{key.TypeId:x16}:0x{key.FileId:x16}；条目 {chain.Entries.Count}；竞争={chain.IsCompetition}；可信={inspection.IsTrusted}。";
	}

	private static string FormatGameDataBrowser(CliGameDataBrowserInspection inspection)
	{
		if (inspection.Browser is not { } browser) return "无法构建 GameData 浏览器；请查看诊断。";
		var effective = browser.Archives.Count(item => item.Overlay.HasEffectiveReplacement);
		var competitions = browser.Archives.Count(item => item.Overlay.HasCompetition);
		return $"GameData 浏览器：archive {browser.Archives.Count}；已生效替换 {effective}；竞争 archive {competitions}。";
	}

	private static string FormatBatchPatchRepairInspection(CliBatchPatchRepairPlanDocument document)
		=> $"批量 Patch 修复检查：Mod {document.Mods.Count}；确认过时 {document.Mods.Count(mod => mod.IsOutdated)}；默认选择 {document.SelectedNodeIds.Count}；当前可尝试 Canonical 重构 {document.Mods.Count(mod => mod.CanAttemptGenerate)}。不会写入来源或 Mod 库。";

	private static string FormatBatchPatchRepairPlan(CliBatchPatchRepairPlanDocument document, string outputPath)
	{
		var blocking = document.Diagnostics.Count(diagnostic => diagnostic.Blocking)
			+ document.Mods.Sum(mod => mod.Diagnostics.Count(diagnostic => diagnostic.Blocking));
		return string.Join(Environment.NewLine,
		[
			$"已导出批量 Patch 修复计划：{outputPath}",
			$"范围：Mod {document.Mods.Count}；确认过时 {document.Mods.Count(mod => mod.IsOutdated)}；默认选择 {document.SelectedNodeIds.Count}；当前可尝试 Canonical 重构 {document.Mods.Count(mod => mod.CanAttemptGenerate)}。",
			$"阻断诊断：{blocking}。可在计划中仅修改 selected，随后运行 validate；apply 会备份并原地替换已选来源 Patch。",
		]);
	}

	private static string FormatBatchPatchRepairValidation(CliBatchPatchRepairValidationResult validation)
	{
		var status = validation.IsValid ? "批量 Patch 修复验证通过。" : "批量 Patch 修复验证未通过。";
		var issues = validation.Diagnostics.Count == 0
			? "无诊断。"
			: string.Join(Environment.NewLine, validation.Diagnostics.Select(diagnostic => $"- [{diagnostic.Code}] {diagnostic.Message}"));
		return $"{status}{Environment.NewLine}{issues}";
	}

	private static string FormatBatchPatchRepairExecution(CliBatchPatchRepairExecutionResult execution)
	{
		if (execution.Success)
		{
			return $"批量 Patch 修复完成：选择 {execution.SelectedModCount}；已修复 {execution.RepairedModCount}；跳过 {execution.SkippedModCount}；活动 Overwrite 回建 {execution.Overwrites.Count} 个；备份：{execution.BackupDirectory}";
		}
		var blocking = execution.Diagnostics.Where(diagnostic => diagnostic.Blocking).Take(5).ToArray();
		return blocking.Length == 0
			? $"批量 Patch 修复未完整完成：已修复 {execution.RepairedModCount}；失败 {execution.FailedModCount}；请查看 JSON 结果与备份清单。"
			: $"批量 Patch 修复未完整完成：{string.Join("；", blocking.Select(diagnostic => $"[{diagnostic.Code}] {diagnostic.Message}"))}";
	}

	private static string FormatSameKeyInspection(CliSameKeyReconstructionInspection inspection)
	{
		if (inspection.SelectedSourcePatchFileName is null)
			return $"Same-key 来源检查：{inspection.SourceModName}；可用 Patch：{inspection.AvailableSourcePatchFileNames.Count}。请明确选择一个 base Patch 文件名后再生成计划。";
		var summary = inspection.Summary;
		return $"Same-key 来源检查：{inspection.SourceModName} / {inspection.SelectedSourcePatchFileName}；Unit：{summary?.SourceUnitCount ?? 0}；可尝试生成：{(summary?.CanAttemptGenerate == true ? "是" : "否")}。";
	}

	private static string FormatSameKeyPlan(CliSameKeyReconstructionPlanDocument document, string outputPath)
	{
		var summary = document.Inspection.Summary;
		var blocking = document.Inspection.Diagnostics.Count(diagnostic => diagnostic.Blocking);
		return string.Join(Environment.NewLine,
		[
			$"已导出 Same-key 重构计划：{outputPath}",
			$"来源：{document.Inspection.SourceModName} / {document.Request.SourcePatchFileName}；Unit：{summary?.SourceUnitCount ?? 0}；可替换：{summary?.ReplacementUnitCount ?? 0}；极小化：{summary?.MinifyOnlyUnitCount ?? 0}",
			$"阻断诊断：{blocking}。仅当 validate 通过后，apply 才会输出新的独立候选 Patch。",
		]);
	}

	private static string FormatSameKeyValidation(CliSameKeyReconstructionValidationResult validation)
	{
		var status = validation.IsValid ? "Same-key 重构验证通过。" : "Same-key 重构验证未通过。";
		var issues = validation.Diagnostics.Count == 0
			? "无诊断。"
			: string.Join(Environment.NewLine, validation.Diagnostics.Select(diagnostic => $"- [{diagnostic.Code}] {diagnostic.Message}"));
		return $"{status}{Environment.NewLine}{issues}";
	}

	private static string FormatSameKeyExecution(CliSameKeyReconstructionExecutionResult execution)
	{
		if (execution.Success)
		{
			return $"Same-key 重构完成：Unit {execution.OutputUnitCount}；替换 mesh {execution.ReplacementMeshCount}；极小化 mesh {execution.MinifiedMeshCount}；输出：{execution.OutputDirectory}";
		}
		var blocking = execution.Diagnostics.Where(diagnostic => diagnostic.Blocking).Take(5).ToArray();
		return blocking.Length == 0
			? "Same-key 重构未完成；请查看 JSON 结果或报告。"
			: $"Same-key 重构被拒绝或失败：{string.Join("；", blocking.Select(diagnostic => $"[{diagnostic.Code}] {diagnostic.Message}"))}";
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

	private static string FormatImportExecution(
		ModImportExecutionResult result,
		string libraryRoot,
		CliImportProfileAddition? profileAddition)
		=> $"已导入：{result.SourceDisplayName}{Environment.NewLine}目标 Mod 库：{libraryRoot}{Environment.NewLine}新增 Mod：{result.AddedNodeIds.Count}；更新 Mod：{result.ChangedNodeIds.Count}；初始化选项状态：{result.InitializedOptionActivationCount}。"
			+ (result.HasOptionPathConflicts
				? $"{Environment.NewLine}检测到选项目录冲突：仅共享 Patch 内容未默认选择；其余选项已按通常规则初始化。"
				: string.Empty)
			+ (result.CacheInvalidationStatus == ImportCacheInvalidationStatus.Completed
				? string.Empty
				: $"{Environment.NewLine}缓存失效状态：{result.CacheInvalidationStatus}；失败节点：{result.CacheInvalidationFailedNodeIds.Count}。可在下次管理器同步时重试。")
			+ (profileAddition is null
				? string.Empty
				: $"{Environment.NewLine}已加入配置：{profileAddition.ProfileName}；新增 {profileAddition.AddedNodeIds.Count} 个可部署 Mod，跳过 {profileAddition.SkippedNodeIds.Count} 个非配置条目。")
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

	private static string FormatModDeletionPlanDocument(CliModDeletionPlanDocument document, string outputPath)
	{
		var blocking = document.Diagnostics.Where(diagnostic => diagnostic.Blocking).ToArray();
		return string.Join(Environment.NewLine,
		[
			$"已导出删除计划：{outputPath}",
			$"请求删除：{document.Request.NodeIds.Count} 个 Mod；实际节点：{document.Deletion.LibraryNodeRemovals.Count}；物理目录：{document.Inputs.Storage.Count}。",
			$"范围：{(document.Request.Scope == SafeModDeletionScope.WithUnsharedOptions ? "包含无共享选项后代" : "仅所选节点")}；阻断项：{blocking.Length}。",
			blocking.Length == 0 ? "计划可执行；仍需 validate 后再 apply --confirm。" : string.Join(Environment.NewLine, blocking.Take(5).Select(item => $"- [{item.Code}] {item.Message}")),
		]);
	}

	private static string FormatModDeletionValidation(CliModDeletionValidationResult validation)
	{
		var status = validation.IsValid ? "删除计划验证通过。" : "删除计划验证未通过。";
		var diagnostics = validation.Diagnostics.Count == 0
			? "无诊断。"
			: string.Join(Environment.NewLine, validation.Diagnostics.Select(item => $"- [{item.Code}] {item.Message}"));
		return $"{status}{Environment.NewLine}{diagnostics}";
	}

	private static string FormatModDeletionApply(CliModDeletionApplyResult execution)
	{
		if (execution.Success && execution.Deletion is not null)
			return FormatDeletionExecution(execution.Deletion);
		var errors = execution.Diagnostics.Where(item => item.Blocking).Take(5).ToArray();
		return errors.Length == 0
			? "删除计划未完成；请查看 JSON 结果。"
			: $"删除计划被拒绝或失败：{string.Join("；", errors.Select(item => $"[{item.Code}] {item.Message}"))}";
	}

	private const string HelpText = """
HD2 Mod Manager 命令行

用法：
	  hd2mod diagnostics armor wizard [--manager-root <管理器目录>]
	  hd2mod diagnostics armor quick [--manager-root <管理器目录>]
	  hd2mod diagnostics armor memory [--manager-root <管理器目录>]
	  hd2mod diagnostics armor matrix [--manager-root <管理器目录>]
	  hd2mod diagnostics armor composition [--manager-root <管理器目录>]
	  hd2mod diagnostics armor recover [--manager-root <管理器目录>]
	  hd2mod capabilities --json
	  hd2mod status --manager-root <管理器目录> --json
	  hd2mod custom-model inspect <Mod GUID> --manager-root <管理器目录> --library <Mod库目录> --game-data <游戏data目录> --json
	  hd2mod custom-model convert <Mod GUID> <来源Archive[,头盔Archive]> <素体名称> --manager-root <管理器目录> --library <Mod库目录> --game-data <游戏data目录> --output <输出根目录> --confirm --json
	  hd2mod armor-registry status [--registry-root <协议目录>] --json
	  hd2mod armor-registry show [--registry-root <协议目录>] --json
	  hd2mod armor-registry plan armor-registry-spec.json [--registry-root <协议目录>] --output armor-registry-plan.json --json
	  hd2mod armor-registry validate --plan armor-registry-plan.json [--registry-root <协议目录>] --json
	  hd2mod armor-registry apply --plan armor-registry-plan.json [--registry-root <协议目录>] --confirm --json
	  hd2mod assets metadata inspect --manager-root <管理器目录> --json
	  hd2mod assets metadata plan [https://raw.githubusercontent.com/.../branch] --manager-root <管理器目录> --output asset-metadata-plan.json --json
	  hd2mod assets metadata validate --plan asset-metadata-plan.json --manager-root <管理器目录> --json
	  hd2mod assets metadata apply --plan asset-metadata-plan.json --manager-root <管理器目录> --confirm --json
	  hd2mod gamedata status --manager-root <管理器目录> --game-data <游戏data目录> --json
	  hd2mod gamedata archive list --manager-root <管理器目录> --game-data <游戏data或副本目录> [--limit <1-500>] [--cursor <cursor>] --json
	  hd2mod gamedata archive show <archive ID> --manager-root <管理器目录> --game-data <游戏data或副本目录> [--with-effective] [--limit <1-500>] [--cursor <cursor>] --json
	  hd2mod gamedata asset find <TypeID十六进制> <FileID十六进制> [...] --manager-root <管理器目录> --game-data <游戏data目录> --json
	  hd2mod gamedata override status --game-data <游戏data或副本目录> --json
	  hd2mod gamedata override list --game-data <游戏data或副本目录> [--only-competition] [--limit <1-500>] [--cursor <cursor>] --json
	  hd2mod gamedata override show <TypeID十六进制> <FileID十六进制> --game-data <游戏data或副本目录> --json
	  hd2mod gamedata deployment inspect --game-data <游戏data或副本目录> --json  （兼容别名：同 override status）
	  hd2mod gamedata browser --manager-root <管理器目录> --library <Mod库目录> --game-data <游戏data或副本目录> [--limit <1-500>] [--cursor <cursor>] --json
	  hd2mod gamedata index inspect --manager-root <管理器目录> --game-data <游戏data目录> --json
	  hd2mod gamedata index plan rebuild|units-all|units-missing --manager-root <管理器目录> --game-data <游戏data目录> --output gamedata-index-plan.json --json
	  hd2mod gamedata index validate --plan gamedata-index-plan.json --manager-root <管理器目录> --game-data <游戏data目录> --json
	  hd2mod gamedata index apply --plan gamedata-index-plan.json --manager-root <管理器目录> --game-data <游戏data目录> --confirm --json
	  hd2mod library sync inspect --manager-root <管理器目录> --library <Mod库目录> --json
	  hd2mod library sync plan --manager-root <管理器目录> --library <Mod库目录> --output library-sync-plan.json --json
	  hd2mod library sync validate --plan library-sync-plan.json --manager-root <管理器目录> --library <Mod库目录> --json
	  hd2mod library sync apply --plan library-sync-plan.json --manager-root <管理器目录> --library <Mod库目录> --confirm --json
	  hd2mod decoration inspect source <Mod GUID或前缀> --manager-root <管理器目录> --game-data <游戏data目录> --json
	  hd2mod decoration show <装饰 Mod GUID或前缀> --manager-root <管理器目录> --json
	  hd2mod decoration package plan <来源 Mod GUID或前缀> --host <目标主体 GUID或前缀> --manager-root <管理器目录> --game-data <游戏data目录> --output decoration-package-plan.json --json
	  hd2mod decoration package validate --plan decoration-package-plan.json --manager-root <管理器目录> --game-data <游戏data目录> --json
	  hd2mod decoration package apply --plan decoration-package-plan.json --manager-root <管理器目录> --game-data <游戏data目录> --output <新的库外且游戏目录外目录> --confirm --json
	  hd2mod decoration edit export <装饰 Mod GUID或前缀> --manager-root <管理器目录> --game-data <游戏data目录> --output decoration-edit-spec.json --json
	  hd2mod decoration edit inspect decoration-edit-spec.json --manager-root <管理器目录> --game-data <游戏data目录> --json
	  hd2mod decoration edit plan decoration-edit-spec.json --manager-root <管理器目录> --game-data <游戏data目录> --output decoration-edit-plan.json --json
	  hd2mod decoration edit validate --plan decoration-edit-plan.json --manager-root <管理器目录> --game-data <游戏data目录> --json
	  hd2mod decoration edit apply --plan decoration-edit-plan.json --manager-root <管理器目录> --game-data <游戏data目录> --confirm --json
	  hd2mod mod export plan <根 Mod GUID或前缀> --manager-root <管理器目录> --library <Mod库目录> --output mod-export-plan.json --json
	  hd2mod mod export validate --plan mod-export-plan.json --manager-root <管理器目录> --library <Mod库目录> --json
	  hd2mod mod export apply --plan mod-export-plan.json --manager-root <管理器目录> --library <Mod库目录> --output <导出目录> --confirm --json
	  hd2mod mod list --library <Mod库目录> --json
	  hd2mod mod show <GUID或唯一前缀> --library <Mod库目录> --json
	  hd2mod mod analyze <GUID或唯一前缀> --library <Mod库目录> --json
	  hd2mod mod external inspect <GUID或唯一前缀> --library <Mod库目录> --json
	  hd2mod mod image show <Mod GUID或唯一前缀> --library <Mod库目录> --json
	  hd2mod mod image set <Mod GUID或唯一前缀> <PNG|JPG|JPEG|WEBP|BMP> --manager-root <管理器目录> --confirm --json
	  hd2mod mod image clear <Mod GUID或唯一前缀> --manager-root <管理器目录> --confirm --json
	  hd2mod mod create <逻辑 Mod 名称> --manager-root <管理器目录> --confirm --json
	  hd2mod mod rename <Mod GUID或唯一前缀> <新名称> --manager-root <管理器目录> --confirm --json
	  hd2mod mod notes set <Mod GUID或唯一前缀> <备注> --manager-root <管理器目录> --confirm --json
	  hd2mod mod notes clear <Mod GUID或唯一前缀> --manager-root <管理器目录> --confirm --json
	  hd2mod mod convert-to-option <Mod GUID或唯一前缀> --host <主体 GUID或唯一前缀> --manager-root <管理器目录> --confirm --json
	  hd2mod mod delete plan <Mod GUID或唯一前缀> [...] [--with-unshared-options] --manager-root <管理器目录> --output deletion-plan.json --json
	  hd2mod mod delete validate --plan deletion-plan.json --manager-root <管理器目录> --json
	  hd2mod mod delete apply --plan deletion-plan.json --manager-root <管理器目录> --confirm --json
	  hd2mod mod delete preview <Mod GUID或唯一前缀> [...] [--with-unshared-options] --library <Mod库目录> --json
	  hd2mod mod delete <Mod GUID或唯一前缀> [...] [--with-unshared-options] --manager-root <管理器目录> --confirm --json
	  hd2mod option show <Option GUID或唯一前缀> --library <Mod库目录> --json
	  hd2mod option enable <Option GUID或唯一前缀> --host <主体 GUID或唯一前缀> --manager-root <管理器目录> --confirm --json
	  hd2mod option disable <Option GUID或唯一前缀> --host <主体 GUID或唯一前缀> --manager-root <管理器目录> --confirm --json
	  hd2mod option enable-all <Option GUID或唯一前缀> --manager-root <管理器目录> --confirm --json
	  hd2mod option disable-all <Option GUID或唯一前缀> --manager-root <管理器目录> --confirm --json
	  hd2mod option move <Option GUID或唯一前缀> [...] --host <主体 GUID或唯一前缀> --position <插入槽位> --manager-root <管理器目录> --confirm --json
	  hd2mod option insert <Option GUID或唯一前缀> [...] --host <主体 GUID或唯一前缀> --position <插入槽位> --manager-root <管理器目录> --confirm --json
	  hd2mod option parent add <Mod GUID或唯一前缀> [...] --host <主体 GUID或唯一前缀> --manager-root <管理器目录> --confirm --json
	  hd2mod option parent remove <Option GUID或唯一前缀> [...] --host <主体 GUID或唯一前缀> --manager-root <管理器目录> --confirm --json
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
	  hd2mod profile align export <配置名称、GUID或active> --library <Mod库目录> --output profile-spec.json --json
	  hd2mod profile align inspect profile-spec.json --manager-root <管理器目录> --json
	  hd2mod profile align plan profile-spec.json --manager-root <管理器目录> --output profile-align-plan.json --json
	  hd2mod profile align validate --plan profile-align-plan.json --manager-root <管理器目录> --json
	  hd2mod profile align apply --plan profile-align-plan.json --manager-root <管理器目录> --confirm --json
	  hd2mod deploy plan [配置名称、GUID或active] --manager-root <管理器目录> --game-data <游戏data目录> --output deployment-plan.json --json
	  hd2mod deploy validate --plan deployment-plan.json --manager-root <管理器目录> --game-data <游戏data目录> --json
	  hd2mod deploy apply --plan deployment-plan.json --manager-root <管理器目录> --game-data <游戏data目录> --confirm --json
	  hd2mod deploy deactivate plan --manager-root <管理器目录> --game-data <游戏data目录> --output deactivation-plan.json --json
	  hd2mod deploy deactivate validate --plan deactivation-plan.json --manager-root <管理器目录> --game-data <游戏data目录> --json
	  hd2mod deploy deactivate apply --plan deactivation-plan.json --manager-root <管理器目录> --game-data <游戏data目录> --confirm --json
	  hd2mod patch repackage inspect <Mod GUID或前缀> [...] --library <Mod库目录> --json
	  hd2mod patch repackage plan <Mod GUID或前缀> [...] --library <Mod库目录> --output repackage-plan.json --json
	  hd2mod patch repackage validate --plan repackage-plan.json --library <Mod库目录> --json
	  hd2mod patch repackage apply --plan repackage-plan.json --library <Mod库目录> --output <新的库外目录> --confirm --json
	  hd2mod equipment helmet-merge inspect <Mod GUID或前缀> [...] --manager-root <管理器目录> --game-data <游戏data目录> --json
	  hd2mod equipment helmet-merge plan <Mod GUID或前缀> [...] --manager-root <管理器目录> --game-data <游戏data目录> --output helmet-merge-plan.json --json
	  hd2mod equipment helmet-merge validate --plan helmet-merge-plan.json --manager-root <管理器目录> --game-data <游戏data目录> --json
	  hd2mod equipment helmet-merge apply --plan helmet-merge-plan.json --manager-root <管理器目录> --game-data <游戏data目录> --output <新的库外且游戏目录外目录> --confirm --json
	  hd2mod equipment armor-normalize inspect <Mod GUID或前缀> --manager-root <管理器目录> --game-data <游戏data目录> --json
	  hd2mod equipment armor-normalize plan <Mod GUID或前缀> --manager-root <管理器目录> --game-data <游戏data目录> --output armor-normalize-plan.json --json
	  hd2mod equipment armor-normalize validate --plan armor-normalize-plan.json --manager-root <管理器目录> --game-data <游戏data目录> --json
	  hd2mod equipment armor-normalize apply --plan armor-normalize-plan.json --manager-root <管理器目录> --library <Mod库目录> --game-data <游戏data目录> --output <新目录> --confirm --json
	  hd2mod equipment cross-armor inspect <来源 Mod GUID或前缀> --manager-root <管理器目录> --game-data <游戏data目录> --json
	  hd2mod equipment cross-armor plan <来源 Mod GUID或前缀> --manager-root <管理器目录> --game-data <游戏data目录> --output cross-armor-plan.json --json
	  hd2mod equipment cross-armor validate --plan cross-armor-plan.json --manager-root <管理器目录> --game-data <游戏data目录> --json
	  hd2mod equipment cross-armor apply --plan cross-armor-plan.json --manager-root <管理器目录> --game-data <游戏data目录> --output <新的库外且游戏目录外目录> --confirm --json
	  hd2mod host-derived inspect <主体 GUID或前缀> [...] --manager-root <管理器目录> --game-data <游戏data目录> --json
	  hd2mod host-derived plan decoration enable|disable <装饰 GUID或前缀> [...] --host <主体 GUID或前缀> --manager-root <管理器目录> --game-data <游戏data目录> --output host-derived-plan.json --json
	  hd2mod host-derived plan decoration enable-all|disable-all <装饰 GUID或前缀> [...] --manager-root <管理器目录> --game-data <游戏data目录> --output host-derived-plan.json --json
	  hd2mod host-derived plan helmet enable|disable <主体 GUID或前缀> [...] --manager-root <管理器目录> --game-data <游戏data目录> --output host-derived-plan.json --json
	  hd2mod host-derived plan rebuild <主体 GUID或前缀> [...] --manager-root <管理器目录> --game-data <游戏data目录> --output host-derived-plan.json --json
	  hd2mod host-derived validate --plan host-derived-plan.json --manager-root <管理器目录> --game-data <游戏data目录> --json
	  hd2mod host-derived apply --plan host-derived-plan.json --manager-root <管理器目录> --game-data <游戏data目录> --confirm --json
	  hd2mod patch repair batch inspect [Mod GUID或前缀] [...] --manager-root <管理器目录> --game-data <游戏data目录> --json
	  hd2mod patch repair batch plan [Mod GUID或前缀] [...] --manager-root <管理器目录> --game-data <游戏data目录> --output batch-repair-plan.json --json
	  hd2mod patch repair batch validate --plan batch-repair-plan.json --manager-root <管理器目录> --game-data <游戏data目录> --json
	  hd2mod patch repair batch apply --plan batch-repair-plan.json --manager-root <管理器目录> --game-data <游戏data目录> --confirm --json
	  hd2mod patch repair inspect <Mod GUID或前缀> [base.patch_N] --manager-root <管理器目录> --game-data <游戏data目录> --json
	  hd2mod patch repair plan <Mod GUID或前缀> <base.patch_N> --manager-root <管理器目录> --game-data <游戏data目录> --output same-key-plan.json --json
	  hd2mod patch repair validate --plan same-key-plan.json --manager-root <管理器目录> --game-data <游戏data目录> --json
	  hd2mod patch repair apply --plan same-key-plan.json --manager-root <管理器目录> --game-data <游戏data目录> --output <新的库外目录> --confirm --json
	  hd2mod import batch inspect <文件夹、压缩包或文件> [...] --json
	  hd2mod import batch plan <文件夹、压缩包或文件> [...] [--add-to-profile <名称|GUID|active>] --manager-root <管理器目录> --output batch-import-plan.json --json
	  hd2mod import batch validate --plan batch-import-plan.json --manager-root <管理器目录> --json
	  hd2mod import batch apply --plan batch-import-plan.json --manager-root <管理器目录> --confirm --json
	  hd2mod import inspect <文件夹、压缩包或文件> --json
	  hd2mod import plan <文件夹、压缩包或文件> [--add-to-profile <名称|GUID|active>] --output import-plan.json
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
	  --registry-root <目录>  护甲注册协议目录；默认位于 LocalAppData 的 CowboyBingus/Helldivers2/ArmorRegistry。
	  --json                  输出机器可读 JSON。
	  --output <文件或目录>   import/deploy/patch plan、Patch 输出、context export、Agent Skill 导出使用。
	  --plan <文件>           import/deploy/patch validate 与 apply 所需的计划。
	  --overwrite             显式允许覆盖已有导出文件或已命名 Skill 目录中的 SKILL.md。
	  --host <GUID或前缀>      Option 操作的明确主体，或 decoration package plan 的明确目标主体。
	  --add-to-profile <目标>   import plan / import batch plan 专用；将目标配置身份与版本固定到导入计划，apply 只加入可部署条目，不自动部署。
	  --position <正整数>      Option 直属顺序或 Profile 逻辑顺序的从 1 开始插入槽位；末尾规则由对应命令的输出说明。
	  --limit <1-500>          gamedata 分页查询的单页条目数。
	  --cursor <cursor>        gamedata 返回的下一页游标；索引或覆盖图变化后会拒绝混用。
	  --with-effective         archive show 额外读取当前 Data-Patch 实际赢家。
	  --only-competition       override list 仅返回有竞争的 AssetKey 覆盖链。
	  --set-active              创建配置时同时将它设为活动配置。
	  --with-unshared-options   删除主体时同时删除没有幸存主体的递归 Option（仅 delete 命令可用）。
	  --confirm               所有写入命令的明确确认。

	安全边界：Mod 库写入要求桌面端关闭、严格库读取、跨进程库租约和显式 --confirm。mod delete plan 会冻结库状态、激活状态与待删目录内容，validate/apply 会重新确认。library sync 只更新 library.json 的节点记录，不会导入、删除、移动或部署 Mod，也不会写 Profile、激活状态或派生缓存。import --add-to-profile 仅加入明确计划中的配置，不自动部署。deploy apply 额外要求游戏退出、真实 GameData、双租约与完整 Patch 归属证明；不会接管未知 Patch，也不会执行 Mod Lua。批量 Patch 修复只会替换计划中仍确认过时且被 selected 的 Mod：每个 Patch 家族会先备份，候选失败会恢复原文件，成功后才更新对应库指纹并回建当前活动的管理器 Overwrite。主体派生 Overwrite 永不覆盖非管理器生成的 Overwrite。Patch 资源重打包、头盔合并、装饰外部包和单 Patch Same-key 修复都只写入新的库外目录；不会替换、自动导入、启用或部署结果。
""";
}
