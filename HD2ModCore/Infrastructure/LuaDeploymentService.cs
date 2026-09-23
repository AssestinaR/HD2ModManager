using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using HD2ModAdaptation.PatchReconstruction.Lua;
using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// Purpose: Converts the active profile's Lua components into one derived Lua
// patch and resolves safe external (ReShade) files before ordinary planning.
// It deliberately does not execute or mutate the game directory.
public sealed class LuaDeploymentService : ILuaDeploymentService
{
	private readonly LuaPatchInspector _luaInspector;
	private readonly LuaPatchComposer _luaComposer;
	private readonly LuaPatchRuntimeValidator _luaRuntimeValidator;
	private readonly IExternalInstallFileFactsService _externalFiles;
	private readonly RuntimeArmorRegistryService? _armorRegistry;
	private static readonly ModNodeId ArmorRegistryNodeId = new(Guid.Parse("6dd5cb37-6b45-4f47-a57c-65e3f2f9e0ca"));
	private const string ArmorRegistryPackageGuid = "6dd5cb37-6b45-4f47-a57c-65e3f2f9e0ca";
	private const string BingusLoaderPackageGuid = "612eaf70-d682-43c7-9efd-16dcc695f977";

	public LuaDeploymentService(
		LuaPatchInspector? luaInspector = null,
		LuaPatchComposer? luaComposer = null,
		LuaPatchRuntimeValidator? luaRuntimeValidator = null,
		IExternalInstallFileFactsService? externalFiles = null,
		RuntimeArmorRegistryService? armorRegistry = null)
	{
		_luaInspector = luaInspector ?? new LuaPatchInspector();
		_luaComposer = luaComposer ?? new LuaPatchComposer();
		_luaRuntimeValidator = luaRuntimeValidator ?? new LuaPatchRuntimeValidator();
		_externalFiles = externalFiles ?? new ExternalInstallFileFactsService();
		_armorRegistry = armorRegistry;
	}

	public async ValueTask<LuaDeploymentPlan> PlanAsync(
		Profile profile,
		LibrarySnapshot snapshot,
		PatchFileIndex patchIndex,
		string modsRootDirectory,
		string gameDataDirectory,
		IReadOnlyDictionary<ModNodeId, ModContentFacts> contentFacts,
		CancellationToken cancellationToken = default,
		LuaDeploymentPermission permission = LuaDeploymentPermission.DenyOpaque)
	{
		var result = await BuildAsync(
			profile,
			snapshot,
			patchIndex,
			modsRootDirectory,
			gameDataDirectory,
			contentFacts,
			materialize: false,
			validationMode: null,
			permission: permission,
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
		CancellationToken cancellationToken = default,
		LuaDeploymentPermission permission = LuaDeploymentPermission.DenyOpaque)
		=> await MaterializeAsync(
			profile,
			snapshot,
			patchIndex,
			modsRootDirectory,
			gameDataDirectory,
			contentFacts,
			LuaDeploymentValidationMode.CompileAndSmoke,
			cancellationToken,
			permission).ConfigureAwait(false);

	public async ValueTask<LuaDeploymentPreparation> MaterializeAsync(
		Profile profile,
		LibrarySnapshot snapshot,
		PatchFileIndex patchIndex,
		string modsRootDirectory,
		string gameDataDirectory,
		IReadOnlyDictionary<ModNodeId, ModContentFacts> contentFacts,
		LuaDeploymentValidationMode validationMode,
		CancellationToken cancellationToken = default,
		LuaDeploymentPermission permission = LuaDeploymentPermission.DenyOpaque)
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
			permission: permission,
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
		LuaDeploymentPermission permission,
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
		var removedGroups = new HashSet<PatchGroupIdentity>();
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
					removedGroups.Add(group.Key);
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
				var luaKeys = factGroup?.AssetKeys.Where(IsLuaAssetKey).Distinct().ToArray() ?? Array.Empty<AssetKey>();
				// A lightweight AssetInventory may exist but still be incomplete for
				// a legacy or freshly generated boot archive. Do not silently deploy
				// such a Patch as ordinary content: the boot archive has one exact
				// identity, so a direct TOC read is a safe, narrow fallback.
				if (factGroup is not null && luaKeys.Length == 0 && IsLuaBootArchive(group.Key.ArchiveHex16))
					factGroup = null;
				HD2ModAdaptation.PatchReconstruction.PatchTocEntry? knownLuaEntry = null;
				IReadOnlyList<HD2ModAdaptation.PatchReconstruction.PatchTocEntry>? entries = null;
				if (factGroup is not null)
				{
					if (luaKeys.Length == 0) continue;
					if (luaKeys.Length != 1 || factGroup.AssetKeys.Count != 1)
					{
						issues.Add(new CoreIssue(
							CoreIssueSeverity.Error,
							"LuaPatchMultipleEntrypointsUnsupported",
							"A patch group contains multiple Lua boot AssetKeys and cannot be merged safely.",
							baseFile.FilePath,
							profileEntry.NodeId));
						continue;
					}
					knownLuaEntry = new HD2ModAdaptation.PatchReconstruction.PatchTocEntry(
						new HD2ModAdaptation.PatchReconstruction.AssetKey(luaKeys[0].TypeId, luaKeys[0].FileId),
						baseFile.FilePath,
						baseFile.FileName);
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
					if (entries.Count != 1 || luaEntries.Length != 1)
					{
						issues.Add(new CoreIssue(
							CoreIssueSeverity.Error,
							"LuaPatchMixedResourcesUnsupported",
							"A Lua patch that also contains non-Lua resources cannot be merged automatically.",
							baseFile.FilePath,
							profileEntry.NodeId));
						continue;
					}
					knownLuaEntry = luaEntries[0];
				}

				if (knownLuaEntry is null) continue;
				if (entries is not null && entries.Count != 1)
				{
					issues.Add(new CoreIssue(
						CoreIssueSeverity.Error,
						"LuaPatchMixedResourcesUnsupported",
						"A Lua patch that also contains non-Lua resources cannot be merged automatically.",
						baseFile.FilePath,
						profileEntry.NodeId));
					continue;
				}

				if (group.Any(file => file.SidecarKind != PatchSidecarKind.Base && file.Length > 0))
				{
					issues.Add(new CoreIssue(
						CoreIssueSeverity.Error,
						"LuaPatchSidecarUnsupported",
						"Lua patches with non-empty Stream or GPU sidecars are not supported by the safe merge path.",
						baseFile.FilePath,
						profileEntry.NodeId));
					continue;
				}

				try
				{
					var document = await _luaInspector.InspectAsync(baseFile.FilePath, knownLuaEntry, cancellationToken).ConfigureAwait(false);
					var patchGroupName = $"{baseFile.ArchiveHex16}.patch_{baseFile.SourcePatchIndex}";
					var blocks = document.Blocks
						.Select(block => block with
						{
							SourceModId = profileEntry.NodeId.Value.ToString("N"),
							SourcePatchGroup = patchGroupName,
						})
						.ToArray();
					document = document with { Blocks = blocks };
					documents.Add(new LuaDocumentSource(profileEntry.NodeId, profileEntry.LoadOrder, group.Key, document));
					removedGroups.Add(group.Key);
				}
				catch (Exception exception) when (exception is LuaPatchFormatException or IOException or UnauthorizedAccessException)
				{
					if (exception is LuaPatchFormatException)
					{
						try
						{
							var opaque = await _luaInspector.InspectOpaqueAsync(baseFile.FilePath, knownLuaEntry, cancellationToken).ConfigureAwait(false);
							var patchGroupName = $"{baseFile.ArchiveHex16}.patch_{baseFile.SourcePatchIndex}";
							var opaqueBlocks = opaque.Blocks
								.Select(block => block with { SourceModId = profileEntry.NodeId.Value.ToString("N"), SourcePatchGroup = patchGroupName })
							.ToArray();
							opaque = opaque with { Blocks = opaqueBlocks };
							documents.Add(new LuaDocumentSource(profileEntry.NodeId, profileEntry.LoadOrder, group.Key, opaque));
							removedGroups.Add(group.Key);
							continue;
						}
						catch (LuaPatchFormatException)
						{
							// Fall through to the original structural error below.
						}
					}
					issues.Add(new CoreIssue(
						CoreIssueSeverity.Error,
						"LuaPatchUnsupportedFormat",
						$"The Lua Patch was detected but could not be safely parsed: {exception.Message}",
						baseFile.FilePath,
						profileEntry.NodeId,
						ExceptionMessage: exception.ToString()));
				}
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

		var armorRuntimeEnabled = false;
		GeneratedPatchGroup? armorAddon = null;
		if (_armorRegistry is not null)
		{
			try
			{
				var registrySnapshot = await _armorRegistry.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
				armorRuntimeEnabled = registrySnapshot.Document.Enabled
					&& await _armorRegistry.IsManagedDeploymentEnabledAsync(cancellationToken).ConfigureAwait(false);
				if (armorRuntimeEnabled)
				{
					if (HasActiveBingusLoader(orderedEntries, snapshot))
					{
						armorAddon = await CreateArmorRegistryAddonAsync(
							modsRootDirectory,
							materialize,
							cancellationToken).ConfigureAwait(false);
					}
					else
					{
						var runtimeDocument = await CreateArmorRegistryDocumentAsync(gameDataDirectory, cancellationToken).ConfigureAwait(false);
						documents.Add(new LuaDocumentSource(
							ArmorRegistryNodeId,
							int.MaxValue,
							new PatchGroupIdentity(ArmorRegistryNodeId, $"{LuaAssetKeys.BootArchiveId:x16}", 0, int.MaxValue),
							runtimeDocument));
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

		var filteredIndex = FilterPatchIndex(patchIndex, removedGroups);
		if (documents.Count < 2 && (!armorRuntimeEnabled || armorAddon is not null))
		{
			var addonGroups = armorAddon is null ? Array.Empty<GeneratedPatchGroup>() : new[] { armorAddon };
			return new LuaBuildResult(
				FilterPatchIndex(patchIndex, managedRemovedGroups),
				addonGroups,
				externalMappings,
				issues,
				armorAddon is not null,
				armorAddon is null ? null : ArmorRegistrySourceFingerprint(),
				documents.Count);
		}

		var opaqueDocuments = documents.Where(document => document.Document.IsOpaque).ToArray();
		if (opaqueDocuments.Length > 0 && permission != LuaDeploymentPermission.AllowOpaque)
		{
			var names = string.Join("、", opaqueDocuments.Select(document => Path.GetFileName(document.Document.SourcePatchPath)));
			issues.Add(new CoreIssue(CoreIssueSeverity.Error, "LuaOpaqueMergeDisabled", $"检测到需要合并的不安全 Lua：{names}。请在设置页打开“允许不安全 Lua 合并部署”后重试。"));
			return new LuaBuildResult(patchIndex, Array.Empty<GeneratedPatchGroup>(), externalMappings, issues, false, null, documents.Count);
		}

		GeneratedPatchGroup generated;
		string? fingerprint = null;
		try
		{
			var orderedDocuments = documents
				.OrderBy(item => item.ProfileLoadOrder)
				.ThenBy(item => item.Group.NormalizedOrder)
				.ThenBy(item => item.Group.ArchiveHex16, StringComparer.OrdinalIgnoreCase)
				.Select(item => item.Document)
				.ToArray();
			var composition = opaqueDocuments.Length == 0
				? _luaComposer.Compose(orderedDocuments)
				: _luaComposer.ComposeOpaque(orderedDocuments);
			var gameRoot = Directory.GetParent(Path.GetFullPath(gameDataDirectory))?.FullName;
			if (validationMode is not null && !string.IsNullOrWhiteSpace(gameRoot))
			{
				var luaLibraryPath = Path.Combine(gameRoot, "bin", "lua51.dll");
				var effectiveValidationMode = opaqueDocuments.Length > 0
					? LuaDeploymentValidationMode.CompileOnly
					: validationMode;
				var validation = effectiveValidationMode == LuaDeploymentValidationMode.CompileOnly
					? await _luaRuntimeValidator.ValidateCompilationAsync(composition.SourceText, luaLibraryPath, cancellationToken).ConfigureAwait(false)
					: await _luaRuntimeValidator.ValidateAsync(composition.SourceText, luaLibraryPath, cancellationToken).ConfigureAwait(false);
				if (!validation.Available)
				{
					issues.Add(new CoreIssue(CoreIssueSeverity.Warning, "LuaRuntimeValidationUnavailable", validation.Message ?? "The game's Lua runtime was unavailable; merged Lua was only structurally validated."));
				}
				else if (!validation.CompileSucceeded)
				{
					issues.Add(new CoreIssue(CoreIssueSeverity.Error, "LuaRuntimeCompileFailed", validation.Message ?? "The merged Lua chunk failed to compile."));
					return new LuaBuildResult(patchIndex, Array.Empty<GeneratedPatchGroup>(), externalMappings, issues, false, null, documents.Count);
				}
				else if (effectiveValidationMode == LuaDeploymentValidationMode.CompileAndSmoke && !validation.RuntimeSucceeded)
				{
					var severity = validation.EnvironmentLimited ? CoreIssueSeverity.Warning : CoreIssueSeverity.Error;
					issues.Add(new CoreIssue(severity, "LuaRuntimeSmokeTestFailed", validation.Message ?? "The merged Lua chunk failed its smoke test."));
					if (severity == CoreIssueSeverity.Error)
					return new LuaBuildResult(patchIndex, Array.Empty<GeneratedPatchGroup>(), externalMappings, issues, false, null, documents.Count);
				}
			}
			fingerprint = CreateCompositionFingerprint(documents);
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
				PlacementOrder: documents.Max(document => document.ProfileLoadOrder));
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

	private static bool IsLuaAssetKey(AssetKey key)
		=> key.TypeId == LuaAssetKeys.LuaTypeId && key.FileId == LuaAssetKeys.BootFileId;

	private static bool IsLuaBootArchive(string archiveHex16)
		=> string.Equals(archiveHex16, $"{LuaAssetKeys.BootArchiveId:x16}", StringComparison.OrdinalIgnoreCase);

	private static bool HasActiveBingusLoader(
		IReadOnlyList<ProfileEntry> entries,
		LibrarySnapshot snapshot)
		=> entries.Any(entry =>
			snapshot.Nodes.TryGetValue(entry.NodeId, out var node)
			&& string.Equals(
				node.Metadata.SourcePackageGuid,
				BingusLoaderPackageGuid,
				StringComparison.OrdinalIgnoreCase));

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
		CancellationToken cancellationToken)
	{
		var source = RuntimeArmorRegistryLuaSource.Load();
		var fingerprint = ArmorRegistrySourceFingerprint();
		var outputDirectory = Path.Combine(
			Path.GetFullPath(modsRootDirectory),
			".runtime",
			"armor-registry-addon",
			fingerprint);
		var outputPath = Path.Combine(outputDirectory, $"{LuaAssetKeys.BootArchiveId:x16}.patch_0");
		var archive = BuildArmorRegistryAddonArchive(source);
		GeneratedPatchFile[] files;
		if (materialize)
		{
			Directory.CreateDirectory(outputDirectory);
			await File.WriteAllBytesAsync(outputPath, archive, cancellationToken).ConfigureAwait(false);
			await File.WriteAllBytesAsync(outputPath + ".stream", [], cancellationToken).ConfigureAwait(false);
			await File.WriteAllBytesAsync(outputPath + ".gpu_resources", [], cancellationToken).ConfigureAwait(false);
			files =
			[
				CreateGeneratedFile(outputPath, PatchSidecarKind.Base),
				CreateGeneratedFile(outputPath + ".stream", PatchSidecarKind.Stream),
				CreateGeneratedFile(outputPath + ".gpu_resources", PatchSidecarKind.GpuResources),
			];
		}
		else
		{
			files =
			[
				new GeneratedPatchFile(PatchSidecarKind.Base, outputPath, Path.GetFileName(outputPath), archive.LongLength, DateTimeOffset.MinValue),
				new GeneratedPatchFile(PatchSidecarKind.Stream, outputPath + ".stream", Path.GetFileName(outputPath) + ".stream", 0, DateTimeOffset.MinValue),
				new GeneratedPatchFile(PatchSidecarKind.GpuResources, outputPath + ".gpu_resources", Path.GetFileName(outputPath) + ".gpu_resources", 0, DateTimeOffset.MinValue),
			];
		}
		return new GeneratedPatchGroup(
			$"{LuaAssetKeys.BootArchiveId:x16}",
			0,
			files,
			"HD2 Runtime Armor Registry managed addon",
			PlacementOrder: int.MaxValue);
	}

	private static byte[] BuildArmorRegistryAddonArchive(string source)
	{
		const string resourceName = "mods/codex/runtime_armor_registry";
		var declaration = $"-- HD2-Addon: {resourceName}\n";
		var body = Encoding.UTF8.GetBytes(
			source.StartsWith("-- HD2-Addon:", StringComparison.Ordinal)
				? source
				: declaration + source);
		var resource = new byte[8 + body.Length];
		BinaryPrimitives.WriteUInt32LittleEndian(resource.AsSpan(0), checked((uint)body.Length));
		BinaryPrimitives.WriteUInt32LittleEndian(resource.AsSpan(4), 2);
		body.CopyTo(resource.AsSpan(8));

		const int headerLength = 104;
		const int entryLength = 80;
		var payloadOffset = Align16(headerLength + entryLength);
		var archiveLength = Align16(payloadOffset + resource.Length);
		var archive = new byte[archiveLength];
		BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(0), 0xF0000011);
		BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(4), 1);
		BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(8), 1);
		BinaryPrimitives.WriteUInt64LittleEndian(archive.AsSpan(32), checked((ulong)archiveLength));
		BinaryPrimitives.WriteUInt64LittleEndian(archive.AsSpan(72 + 8), LuaAssetKeys.LuaTypeId);
		BinaryPrimitives.WriteUInt64LittleEndian(archive.AsSpan(72 + 16), 1);
		BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(72 + 24), 16);
		BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(72 + 28), 16);
		BinaryPrimitives.WriteUInt64LittleEndian(archive.AsSpan(104), StingrayHash(resourceName));
		BinaryPrimitives.WriteUInt64LittleEndian(archive.AsSpan(112), LuaAssetKeys.LuaTypeId);
		BinaryPrimitives.WriteUInt64LittleEndian(archive.AsSpan(120), checked((ulong)payloadOffset));
		BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(160), checked((uint)resource.Length));
		BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(172), 16);
		BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(176), 16);
		resource.CopyTo(archive.AsSpan(payloadOffset));
		return archive;
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

	private static string ArmorRegistrySourceFingerprint()
		=> Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(RuntimeArmorRegistryLuaSource.Load())))
			.ToLowerInvariant();

	private static async ValueTask<LuaPatchDocument> CreateArmorRegistryDocumentAsync(
		string gameDataDirectory,
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
		var declaredBootLength = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(0, 4));
		var bootKind = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(4, 4));
		if (bootKind != 2 || declaredBootLength != boot.Length - 8)
			throw new InvalidDataException("游戏原始 Lua 启动资源的封装格式不受支持。");
		boot = boot.AsSpan(8).ToArray();

		var header = CreateLuaHeaderTemplate();
		var syntheticEntry = new HD2ModAdaptation.PatchReconstruction.PatchTocEntry(
			LuaAssetKeys.BootAssetKey,
			"<manager:runtime-armor-registry>",
			$"{LuaAssetKeys.BootArchiveId:x16}.patch_0");
		var runtimeSource = Encoding.UTF8.GetBytes(RuntimeArmorRegistryLuaSource.Load());
		var body = "local __hd2_registry_loader = rawget(_G, \"loadstring\") or rawget(_G, \"load\")\n"
			+ "local __hd2_registry_chunk = __hd2_registry_loader and __hd2_registry_loader(\""
			+ EscapeLuaBytes(runtimeSource)
			+ "\", \"@mods/codex/runtime_armor_registry\")\n"
			+ "if __hd2_registry_chunk then pcall(__hd2_registry_chunk) end";
		return new LuaPatchDocument(
			syntheticEntry.SourceFilePath,
			syntheticEntry,
			new HD2ModAdaptation.PatchReconstruction.PatchEntryPayload(syntheticEntry, boot, [], []),
			header,
			boot,
			[
				new LuaPatchBlock(
					"HD2 Runtime Armor Registry",
					string.Empty,
					body,
					WrapBootReturn: false,
					IsManagedBlock: true,
					SourceModId: ArmorRegistryNodeId.Value.ToString("N"),
					SourcePatchGroup: "manager/runtime-armor-registry")
			],
			LuaPatchWrapperKind.Single,
			null,
			string.Empty);
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
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0xBC), 2);
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

	private static string CreateCompositionFingerprint(IEnumerable<LuaDocumentSource> sources)
	{
		var builder = new StringBuilder();
		foreach (var source in sources.OrderBy(item => item.ProfileLoadOrder).ThenBy(item => item.Group.NormalizedOrder).ThenBy(item => item.Group.ArchiveHex16, StringComparer.OrdinalIgnoreCase))
		{
			builder.Append(source.NodeId.Value.ToString("N"))
				.Append(':').Append(source.ProfileLoadOrder)
				.Append(':').Append(source.Group.ArchiveHex16)
				.Append(':').Append(source.Group.SourcePatchIndex)
				.Append(':').Append(LuaHashFor(source.Document.BootBytecode));
			foreach (var block in source.Document.Blocks) builder.Append(':').Append(block.BodySha256).Append(':').Append(block.HeadSha256);
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
