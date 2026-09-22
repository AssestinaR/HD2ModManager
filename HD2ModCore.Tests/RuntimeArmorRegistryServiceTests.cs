using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using Microsoft.Data.Sqlite;

namespace HD2ModCore.Tests;

public sealed class RuntimeArmorRegistryServiceTests
{
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
			"schema=1\nrevision=7\nenabled=true\nmap=10,100\nmap=30,300\n",
			text);
		Assert.Equal(7, parsed.Revision);
		Assert.True(parsed.Enabled);
		Assert.Equal([10u, 30u], parsed.Mappings.Select(mapping => mapping.TargetItemId));
	}

	[Theory]
	[InlineData("schema=2\nrevision=0\nenabled=false\n")]
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
