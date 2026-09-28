using System.Buffers.Binary;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using AdaptationLuaAssetKeys = HD2ModAdaptation.PatchReconstruction.Lua.LuaAssetKeys;
using AdaptationLuaPatchInspector = HD2ModAdaptation.PatchReconstruction.Lua.LuaPatchInspector;
using AdaptationPatchEntryPayloadReader = HD2ModAdaptation.PatchReconstruction.PatchEntryPayloadReader;
using AdaptationPatchTocScanner = HD2ModAdaptation.PatchReconstruction.PatchTocScanner;
using Microsoft.Data.Sqlite;

namespace HD2ModCore.Tests;

public sealed class RuntimeArmorRegistryServiceTests
{
	[Fact]
	public async Task ArmorPiecesUseRuntimeBodyEncodingNotCatalogEnum()
	{
		await using var fixture = await Fixture.CreateAsync(withCatalog: true);
		var paths = new StoragePaths(Path.Combine(fixture.Root, "app"));
		await using var connection = new SqliteConnection($"Data Source={paths.DbPath};Pooling=False");
		await connection.OpenAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = """
			CREATE TABLE game_data_authority_pieces (
				item_id INTEGER, raw_body_type INTEGER, body_variant INTEGER,
				unit_file_id INTEGER, slot INTEGER, piece_type INTEGER, tone_variations INTEGER);
			INSERT INTO game_data_authority_pieces VALUES
				(10, 0, 2, 101, 2, 0, 0),
				(10, 1, 1, 102, 2, 0, 0),
				(10, 3, 3, 103, 3, 0, 0);
			""";
		await command.ExecuteNonQueryAsync();
		var pieces = await fixture.Service.ReadArmorPiecesAsync(10);
		Assert.Equal(0u, pieces.Single(p => p.Unit == "0000000000000065").Body);
		Assert.Equal(1u, pieces.Single(p => p.Unit == "0000000000000066").Body);
		Assert.Equal(3u, pieces.Single(p => p.Unit == "0000000000000067").Body);
	}

	[Fact]
	public async Task EditorDefaultBase_RemainsInWorkspaceButNeverInRuntimeProtocol()
	{
		await using var fixture = await Fixture.CreateAsync();
		const string legacy = "schema=2\nrevision=27\nenabled=true\nmodel=custom,\n"
			+ "model_piece=custom,0,044e0dee2a7b0002,2,0,0,true,true\nmap_model=42,custom\n";
		var document = RuntimeArmorRegistryService.ParseProtocol(legacy);
		Assert.True(document.ModelDefinitions[0].Pieces![0].IsDefaultBase);
		await fixture.Service.SaveWorkspaceAsync(document);
		var reloaded = await fixture.Service.ReadSnapshotAsync();
		Assert.True(reloaded.Document.ModelDefinitions[0].Pieces![0].IsDefaultBase);
		var wire = RuntimeArmorRegistryService.SerializeProtocol(reloaded.Document);
		var line = Assert.Single(wire.Split('\n'), l => l.StartsWith("model_piece="));
		Assert.Equal("model_piece=custom,0,044e0dee2a7b0002,2,0,0,true", line);
		Assert.Equal(7, line.Split(',').Length);

		// Recreate an old release's successfully saved workspace + incompatible
		// protocol. The upgraded GUI must offer Apply even without further edits.
		var applied = await fixture.Service.ApplyAsync(reloaded.Fingerprint, document.Enabled,
			document.Mappings, document.ModelDefinitions);
		Assert.False((await fixture.Service.ReadSnapshotAsync()).HasUnappliedChanges);
		var protocolPath = Path.Combine(fixture.RegistryRoot, RuntimeArmorRegistryService.ProtocolFileName);
		var oldWire = (await File.ReadAllTextAsync(protocolPath)).Replace(
			"044e0dee2a7b0002,2,0,0,true\n", "044e0dee2a7b0002,2,0,0,true,true\n");
		await File.WriteAllTextAsync(protocolPath, oldWire);
		Assert.True((await fixture.Service.ReadSnapshotAsync()).HasUnappliedChanges);
	}

	[Fact]
	public async Task WorkspaceEdits_SurviveReloadWithoutCompilingAndStayPending()
	{
		await using var fixture = await Fixture.CreateAsync();
		var first = await fixture.Service.ReadSnapshotAsync();
		var model = new RuntimeArmorRegistryModel("model", null, [new(0, "0000000000000001", 2, 0), new(1, "0000000000000002", 2, 0)]);
		await fixture.Service.ApplyAsync(first.Fingerprint, false, [], [model]);
		var before = await File.ReadAllTextAsync(Path.Combine(fixture.RegistryRoot, RuntimeArmorRegistryService.ProtocolFileName));
		var snapshot = await fixture.Service.ReadSnapshotAsync();
		Assert.False(snapshot.HasUnappliedChanges);
		await fixture.Service.SaveWorkspaceAsync(snapshot.Document with
		{
			ModelDefinitions = [model with { Pieces = RuntimeArmorModelStructure.Merge(model.Pieces!, 2, 0) }]
		});
		var reloaded = await fixture.Service.ReadSnapshotAsync();
		Assert.True(reloaded.HasUnappliedChanges);
		Assert.Equal(2, reloaded.Document.ModelDefinitions.Single().Pieces!.Count);
		Assert.Single(RuntimeArmorModelStructure.Active(reloaded.Document.ModelDefinitions.Single().Pieces!));
		Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(fixture.RegistryRoot, RuntimeArmorRegistryService.ProtocolFileName)));
		var catalog = Assert.Single(await fixture.Service.ReadCustomModelCatalogAsync());
		Assert.Single(catalog.Parts);
		Assert.Equal(HD2ModAdaptation.Analysis.UnitMeshBodyVariant.Any, catalog.Parts[0].BodyVariant);
	}

	[Fact]
	public async Task Apply_PreservesWorkspaceCompositionMode()
	{
		await using var fixture = await Fixture.CreateAsync();
		var initial = await fixture.Service.ReadSnapshotAsync();
		await fixture.Service.SaveWorkspaceAsync(initial.Document with
		{
			CompositionMode = "existing_only",
		});

		var beforeApply = await fixture.Service.ReadSnapshotAsync();
		await fixture.Service.ApplyAsync(
			beforeApply.Fingerprint,
			false,
			beforeApply.Document.Mappings,
			beforeApply.Document.ModelDefinitions,
			beforeApply.Document.PieceOverrideDefinitions,
			beforeApply.Document.TargetModeDefinitions);

		var protocol = await File.ReadAllTextAsync(
			Path.Combine(fixture.RegistryRoot, RuntimeArmorRegistryService.ProtocolFileName));
		Assert.Contains("composition_mode=existing_only\n", protocol, StringComparison.Ordinal);
		Assert.Equal("existing_only", (await fixture.Service.ReadSnapshotAsync()).Document.CompositionMode);
	}

	[Fact]
	public async Task ApplyFilter_KeepsAllWorkspacePiecesAndOnlyCompilesPresentUnits()
	{
		await using var fixture = await Fixture.CreateAsync();
		var data = Path.Combine(fixture.Root, "data");
		Directory.CreateDirectory(data);
		var bytes = CreateGameBootArchive([1, 2, 3, 4]);
		BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(68), HD2ModAdaptation.PatchReconstruction.UnitMesh.PatchUnitMeshReader.UnitTypeId);
		BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(92), 1);
		BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(100), HD2ModAdaptation.PatchReconstruction.UnitMesh.PatchUnitMeshReader.UnitTypeId);
		await File.WriteAllBytesAsync(Path.Combine(data, "0000000000000001.patch_0"), bytes);
		var model = new RuntimeArmorRegistryModel("test", null, [new(3, "0000000000000001", 2, 0), new(3, "0000000000000002", 3, 0)]);
		var initial = await fixture.Service.ReadSnapshotAsync();
		await fixture.Service.ApplyAsync(initial.Fingerprint, true, [new(42, null, "test")], [model], [], [], gameDataDirectory: data);
		var snapshot = await fixture.Service.ReadSnapshotAsync();
		Assert.False(snapshot.HasUnappliedChanges);
		Assert.Equal(2, snapshot.Document.ModelDefinitions.Single().Pieces!.Count);
		var protocol = RuntimeArmorRegistryService.ParseProtocol(await File.ReadAllTextAsync(snapshot.ProtocolPath));
		Assert.Equal("0000000000000001", Assert.Single(protocol.ModelDefinitions.Single().Pieces!).Unit);
	}

	[Fact]
	public async Task CustomCatalog_UsesPersistentUnsignedIdsAndOnlyEnabledExplicitPieces()
	{
		await using var fixture = await Fixture.CreateAsync();
		await fixture.Service.SaveWorkspaceAsync(new(0, false, [], [
			new("virtual", null, [
				new(0, "fedcba9876543210", 2, 0),
				new(1, "fedcba9876543211", 2, 0),
				new(3, "fedcba9876543212", 3, 7),
				new(1, "fedcba9876543213", 3, 7, Enabled: false)
			], "测试素体"),
			new("alias", 42)
		]));
		var entry = Assert.Single(await fixture.Service.ReadCustomModelCatalogAsync());
		Assert.True(entry.IsCustomModel);
		Assert.Equal("custom-model:virtual", entry.ArchiveId);
		Assert.Equal("virtual", entry.CustomModelKey);
		Assert.Equal("测试素体", entry.DisplayName);
		Assert.Equal(3, entry.Parts.Count);
		Assert.Equal(0xfedcba9876543210UL, entry.Parts[0].UnitAssetKey.FileId);
		Assert.Equal(HD2ModAdaptation.Analysis.UnitMeshBodyVariant.Stocky, entry.Parts[0].BodyVariant);
		Assert.Equal(HD2ModAdaptation.Analysis.UnitMeshBodyVariant.Slim, entry.Parts[1].BodyVariant);
		Assert.Equal(HD2ModAdaptation.Analysis.UnitMeshPartKind.Pelvis, entry.Parts[2].PartKind);
		Assert.Equal(HD2ModAdaptation.Analysis.UnitMeshBodyVariant.Any, entry.Parts[2].BodyVariant);
		Assert.Equal("7", entry.Parts[2].PieceType);
	}

	[Fact]
	public void Protocol_RoundTripsNormalizedMappings()
	{
		var document = new RuntimeArmorRegistryDocument(
			7,
			true,
			[
				new RuntimeArmorRegistryMapping(30, 300),
				new RuntimeArmorRegistryMapping(10, 100),
			]);

		var text = RuntimeArmorRegistryService.SerializeProtocol(document);
		var parsed = RuntimeArmorRegistryService.ParseProtocol(text);

		Assert.Equal(
			"schema=2\nrevision=7\nenabled=true\nmap=10,100\nmap=30,300\n",
			text);
		Assert.Equal(7, parsed.Revision);
		Assert.True(parsed.Enabled);
		Assert.Equal([10u, 30u], parsed.Mappings.Select(mapping => mapping.TargetItemId));
	}

	[Theory]
	[InlineData("schema=1\nrevision=0\nenabled=false\nmodel=custom:test\n")]
	[InlineData("schema=1\nrevision=0\nenabled=false\nmap=10,10\n")]
	[InlineData("schema=1\nrevision=0\nenabled=false\nmap=10,20\nmap=10,30\n")]
	public void Protocol_RejectsUnsupportedOrAmbiguousDocuments(string text)
	{
		Assert.Throws<InvalidDataException>(() => RuntimeArmorRegistryService.ParseProtocol(text));
	}

	[Fact]
	public async Task Snapshot_MissingProtocolStartsAtRevisionZero()
	{
		await using var fixture = await Fixture.CreateAsync();

		var snapshot = await fixture.Service.ReadSnapshotAsync();

		Assert.False(snapshot.ProtocolExists);
		Assert.Equal("missing", snapshot.Fingerprint);
		Assert.Equal(0, snapshot.Document.Revision);
		Assert.False(snapshot.Document.Enabled);
		Assert.Empty(snapshot.Document.Mappings);
	}

	[Fact]
	public async Task Apply_IncrementsRevisionAndRejectsFingerprintDrift()
	{
		await using var fixture = await Fixture.CreateAsync();
		var initial = await fixture.Service.ReadSnapshotAsync();

		var applied = await fixture.Service.ApplyAsync(
			initial.Fingerprint,
			true,
			[new RuntimeArmorRegistryMapping(488691438, 820333399)]);

		Assert.Equal(1, applied.Document.Revision);
		Assert.True(applied.Document.Enabled);
		Assert.Single(applied.Document.Mappings);

		await File.AppendAllTextAsync(
			Path.Combine(fixture.RegistryRoot, RuntimeArmorRegistryService.ProtocolFileName),
			"# external edit\n");

		var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
			await fixture.Service.ApplyAsync(
				applied.Fingerprint,
				false,
				Array.Empty<RuntimeArmorRegistryMapping>()));
		Assert.Contains("其他操作修改", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Apply_WritesModLibraryWorkspaceAndSnapshotPrefersIt()
	{
		await using var fixture = await Fixture.CreateAsync();
		var initial = await fixture.Service.ReadSnapshotAsync();
		var model = new RuntimeArmorRegistryModel(
			"custom/coat",
			null,
			[new RuntimeArmorRegistryModelPiece(3, "0011223344556677", 1, 0, 0, false)]);

		await fixture.Service.ApplyAsync(
			initial.Fingerprint,
			true,
			[new RuntimeArmorRegistryMapping(488691438, null, "custom/coat")],
			[model]);

		Assert.True(File.Exists(fixture.Service.WorkspacePath));
		Assert.Contains("\"custom/coat\"", await File.ReadAllTextAsync(fixture.Service.WorkspacePath), StringComparison.Ordinal);

		var reloaded = await fixture.Service.ReadSnapshotAsync();
		Assert.Equal(1, reloaded.Document.Revision);
		Assert.Equal("custom/coat", Assert.Single(reloaded.Document.ModelDefinitions).Key);
		Assert.False(Assert.Single(Assert.Single(reloaded.Document.ModelDefinitions).Pieces!).Enabled);
		Assert.True(reloaded.Document.Mappings.Single().IsModelMapping);
	}

	[Fact]
	public async Task Snapshot_DoesNotTreatAStaleOrUnrelatedPidAsLiveRuntime()
	{
		await using var fixture = await Fixture.CreateAsync();
		await File.WriteAllTextAsync(
			Path.Combine(fixture.RegistryRoot, RuntimeArmorRegistryService.ProtocolFileName),
			"schema=1\nrevision=3\nenabled=true\nmap=10,20\n");
		await File.WriteAllTextAsync(
			Path.Combine(fixture.RegistryRoot, RuntimeArmorRegistryService.RuntimeStatusFileName),
			$"pid={Environment.ProcessId}\nstate=ready\nrevision=3\n");

		var snapshot = await fixture.Service.ReadSnapshotAsync();

		Assert.True(snapshot.RuntimeStatusExists);
		Assert.False(snapshot.RuntimeProcessRunning);
		Assert.False(snapshot.RuntimeAcknowledged);
	}

	[Fact]
	public async Task Catalog_ReturnsOnlyArmorEntriesInDisplayOrder()
	{
		await using var fixture = await Fixture.CreateAsync(withCatalog: true);

		var entries = await fixture.Service.ReadArmorCatalogAsync();

		Assert.Equal(2, entries.Count);
		Assert.Equal(["Alpha", "Zulu"], entries.Select(entry => entry.DisplayName));
		Assert.Equal([20u, 10u], entries.Select(entry => entry.ItemId));
	}

	[Fact]
	public async Task LuaDeployment_WithActiveBingus_MaterializesManagedAddonAndMigratesLegacyPackage()
	{
		await using var fixture = await Fixture.CreateAsync();
		var initial = await fixture.Service.ReadSnapshotAsync();
		await fixture.Service.ApplyAsync(initial.Fingerprint, true, [new RuntimeArmorRegistryMapping(10, 20)]);

		var loaderId = ModNodeId.New();
		var legacyId = ModNodeId.New();
		var loader = new ModNode(
			loaderId,
			"loader",
			new ModNodeMetadata("Bingus", null, DateTimeOffset.UtcNow, null, SourcePackageGuid: "612eaf70-d682-43c7-9efd-16dcc695f977"),
			[],
			[]);
		var legacy = new ModNode(
			legacyId,
			"legacy",
			new ModNodeMetadata("Legacy runtime", null, DateTimeOffset.UtcNow, null, SourcePackageGuid: "6dd5cb37-6b45-4f47-a57c-65e3f2f9e0ca"),
			[],
			[]);
		var profile = new Profile(
			ProfileId.New(),
			"test",
			DateTimeOffset.UtcNow,
			null,
			[new ProfileEntry(loaderId, 0), new ProfileEntry(legacyId, 1)]);
		var snapshot = new LibrarySnapshot(
			1,
			DateTimeOffset.UtcNow,
			new Dictionary<ModNodeId, ModNode> { [loaderId] = loader, [legacyId] = legacy },
			[profile],
			profile.Id);
		var legacyPatch = Path.Combine(fixture.Root, "legacy", "9ba626afa44a3aa3.patch_0");
		Directory.CreateDirectory(Path.GetDirectoryName(legacyPatch)!);
		await File.WriteAllBytesAsync(legacyPatch, [1]);
		var index = new PatchFileIndex(
			DateTimeOffset.UtcNow,
			new Dictionary<ModNodeId, IReadOnlyList<IndexedPatchFile>>
			{
				[legacyId] =
				[
					new IndexedPatchFile(
						legacyId,
						legacyPatch,
						Path.GetFileName(legacyPatch),
						"9ba626afa44a3aa3",
						0,
						0,
						PatchSidecarKind.Base,
						1,
						DateTimeOffset.UtcNow),
				],
			},
			[]);
		var modsRoot = Path.Combine(fixture.Root, "mods");
		Directory.CreateDirectory(modsRoot);

		var result = await new LuaDeploymentService(armorRegistry: fixture.Service).MaterializeAsync(
			profile,
			snapshot,
			index,
			modsRoot,
			Path.Combine(fixture.Root, "unused-game-data"),
			new Dictionary<ModNodeId, ModContentFacts>(),
			LuaDeploymentValidationMode.CompileOnly);

		Assert.False(result.HasErrors, string.Join(Environment.NewLine, result.Issues.Select(issue => $"{issue.Code}: {issue.Message}")));
		var group = Assert.Single(result.GeneratedPatchGroups);
		var patch = Assert.Single(group.Files, file => file.SidecarKind == PatchSidecarKind.Base);
		var entry = Assert.Single(await new AdaptationPatchTocScanner().ScanEntriesAsync(patch.FilePath));
		Assert.Equal(AdaptationLuaAssetKeys.LuaTypeId, entry.AssetKey.TypeId);
		Assert.Equal(0x0A2E0E3386517D4CUL, entry.AssetKey.FileId);
		var payload = await new AdaptationPatchEntryPayloadReader().ReadPayloadAsync(entry);
		Assert.StartsWith("-- HD2-Addon: mods/codex/runtime_armor_registry\n", System.Text.Encoding.UTF8.GetString(payload.TocData.AsSpan(8)));
		Assert.Empty(result.PatchIndex.FilesByNode[legacyId]);
	}

	[Fact]
	public async Task LuaDeployment_WithoutBingus_BuildsDirectBootPatchFromInstalledGameResource()
	{
		await using var fixture = await Fixture.CreateAsync();
		var initial = await fixture.Service.ReadSnapshotAsync();
		await fixture.Service.ApplyAsync(initial.Fingerprint, true, [new RuntimeArmorRegistryMapping(10, 20)]);
		var gameData = Path.Combine(fixture.Root, "game", "data");
		Directory.CreateDirectory(gameData);
		var bootBytecode = new byte[] { 0x1B, 0x4C, 0x4A, 0x02 };
		await File.WriteAllBytesAsync(
			Path.Combine(gameData, $"{AdaptationLuaAssetKeys.BootArchiveId:x16}"),
			CreateGameBootArchive(bootBytecode));
		var profile = new Profile(ProfileId.New(), "test", DateTimeOffset.UtcNow, null, []);
		var snapshot = new LibrarySnapshot(1, DateTimeOffset.UtcNow, new Dictionary<ModNodeId, ModNode>(), [profile], profile.Id);
		var modsRoot = Path.Combine(fixture.Root, "mods");
		Directory.CreateDirectory(modsRoot);

		var result = await new LuaDeploymentService(armorRegistry: fixture.Service).MaterializeAsync(
			profile,
			snapshot,
			new PatchFileIndex(DateTimeOffset.UtcNow, new Dictionary<ModNodeId, IReadOnlyList<IndexedPatchFile>>(), []),
			modsRoot,
			gameData,
			new Dictionary<ModNodeId, ModContentFacts>(),
			LuaDeploymentValidationMode.CompileOnly);

		Assert.False(result.HasErrors, string.Join(Environment.NewLine, result.Issues.Select(issue => $"{issue.Code}: {issue.Message}")));
		var group = Assert.Single(result.GeneratedPatchGroups);
		var patch = Assert.Single(group.Files, file => file.SidecarKind == PatchSidecarKind.Base);
		var document = await new AdaptationLuaPatchInspector().InspectAsync(patch.FilePath);
		Assert.Equal(bootBytecode, document.BootBytecode);
		var runtimeBlock = Assert.Single(document.Blocks, block => block.Name == "HD2 Runtime Armor Registry");
		Assert.Contains("@mods/codex/runtime_armor_registry", runtimeBlock.Body, StringComparison.Ordinal);
	}

	private static byte[] CreateGameBootArchive(byte[] bootBytecode)
	{
		var resource = new byte[bootBytecode.Length + 8];
		BinaryPrimitives.WriteUInt32LittleEndian(resource.AsSpan(0), checked((uint)bootBytecode.Length));
		BinaryPrimitives.WriteUInt32LittleEndian(resource.AsSpan(4), 2);
		bootBytecode.CopyTo(resource.AsSpan(8));
		const int typeOffset = 60;
		const int entryOffset = 92;
		const int payloadOffset = 176;
		var archive = new byte[payloadOffset + resource.Length];
		BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(0), 0xF0000011);
		BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(4), 1);
		BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(8), 1);
		BinaryPrimitives.WriteUInt64LittleEndian(archive.AsSpan(typeOffset + 8), AdaptationLuaAssetKeys.LuaTypeId);
		BinaryPrimitives.WriteUInt64LittleEndian(archive.AsSpan(typeOffset + 16), 1);
		BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(typeOffset + 24), 16);
		BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(typeOffset + 28), 64);
		BinaryPrimitives.WriteUInt64LittleEndian(archive.AsSpan(entryOffset), AdaptationLuaAssetKeys.BootFileId);
		BinaryPrimitives.WriteUInt64LittleEndian(archive.AsSpan(entryOffset + 8), AdaptationLuaAssetKeys.LuaTypeId);
		BinaryPrimitives.WriteUInt64LittleEndian(archive.AsSpan(entryOffset + 16), payloadOffset);
		BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(entryOffset + 56), checked((uint)resource.Length));
		resource.CopyTo(archive.AsSpan(payloadOffset));
		return archive;
	}

	private sealed class Fixture : IAsyncDisposable
	{
		private Fixture(string root, string registryRoot, RuntimeArmorRegistryService service)
		{
			Root = root;
			RegistryRoot = registryRoot;
			Service = service;
		}

		public string Root { get; }
		public string RegistryRoot { get; }
		public RuntimeArmorRegistryService Service { get; }

		public static async ValueTask<Fixture> CreateAsync(bool withCatalog = false)
		{
			var root = Path.Combine(Path.GetTempPath(), "hd2-armor-registry-tests", Guid.NewGuid().ToString("N"));
			var paths = new StoragePaths(Path.Combine(root, "app"));
			var registryRoot = Path.Combine(root, "registry");
			Directory.CreateDirectory(paths.IndexDirectory);
			Directory.CreateDirectory(registryRoot);

			if (withCatalog)
			{
				await using var connection = new SqliteConnection($"Data Source={paths.DbPath};Pooling=False");
				await connection.OpenAsync();
				await using var create = connection.CreateCommand();
				create.CommandText = """
					CREATE TABLE game_data_authority_kits (
						item_id INTEGER PRIMARY KEY,
						dlc_id INTEGER NOT NULL,
						set_id INTEGER NOT NULL,
						kit_kind INTEGER NOT NULL,
						archive_id TEXT NOT NULL,
						display_name TEXT NOT NULL
					);
					INSERT INTO game_data_authority_kits VALUES (10, 0, 0, 0, 'z', 'Zulu');
					INSERT INTO game_data_authority_kits VALUES (20, 0, 0, 0, 'a', 'Alpha');
					INSERT INTO game_data_authority_kits VALUES (30, 0, 0, 1, 'helmet', 'Helmet');
					""";
				await create.ExecuteNonQueryAsync();
			}

			return new Fixture(root, registryRoot, new RuntimeArmorRegistryService(paths, registryRoot));
		}

		public ValueTask DisposeAsync()
		{
			SqliteConnection.ClearAllPools();
			try { Directory.Delete(Root, recursive: true); }
			catch { }
			return ValueTask.CompletedTask;
		}
	}
}
