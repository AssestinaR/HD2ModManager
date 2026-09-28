using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace HD2ModCli.Tests;

[Collection("cli-console")]
public sealed class ArmorRegistryCliTests
{
	[Fact]
	public void RuntimeProtocol_DoesNotEmitLegacyEditorDefaultBaseField()
	{
		var document = CliArmorRegistryProtocol.ParseProtocol(
			"schema=2\nrevision=27\nenabled=true\nmodel=custom,\n"
			+ "model_piece=custom,0,044e0dee2a7b0002,2,0,0,true,true\nmap_model=42,custom\n");
		var text = CliArmorRegistryProtocol.SerializeProtocol(document);
		var line = Assert.Single(text.Split('\n'), l => l.StartsWith("model_piece="));
		Assert.Equal("model_piece=custom,0,044e0dee2a7b0002,2,0,0,true", line);
	}

	[Fact]
	public void LegacyAnyHelmetSlot_IsMigratedToNativeSlotZero()
	{
		var document = CliArmorRegistryProtocol.ParseProtocol(
			"schema=2\nrevision=1\nenabled=true\nmodel=custom,\n"
			+ "model_piece=custom,3,0000000000000001,1,0,0\n"
			+ "model_piece=custom,0,0000000000000002,1,0,0\n"
			+ "piece_override=43,3,0000000000000003,1,0,true\nmap_model=42,custom\n");
		Assert.Contains(document.Models![0].Pieces!, piece => piece.Body == 3 && piece.Slot == 0);
		Assert.Contains(document.Models[0].Pieces!, piece => piece.Body == 0 && piece.Slot == 1);
		Assert.Equal(1u, Assert.Single(document.PieceOverrides!).Slot);
	}

	[Fact]
	public async Task ArmorRegistry_PlanValidateApply_HotUpdatesOnlyManagerOwnedProtocol()
	{
		await using var fixture = await ArmorRegistryFixture.CreateAsync(withProtocol: true);
		var statusBefore = await File.ReadAllBytesAsync(fixture.RuntimeStatusPath);
		var cacheBefore = await File.ReadAllBytesAsync(fixture.LocatorCachePath);

		var status = await CliTestConsole.RunCapturedAsync(
			"armor-registry", "status",
			"--registry-root", fixture.RegistryRoot,
			"--json");
		Assert.Equal(0, status.ExitCode);
		using (var statusJson = status.ParseJson())
		{
			var registry = statusJson.RootElement.GetProperty("armorRegistry");
			Assert.Equal(3, registry.GetProperty("protocol").GetProperty("revision").GetInt32());
			Assert.Equal("ready", registry.GetProperty("runtimeStatus").GetProperty("state").GetString());
			Assert.Equal("411", registry.GetProperty("locatorCache").GetProperty("record_count").GetString());
			Assert.False(registry.GetProperty("runtimeProcessRunning").GetBoolean());
			Assert.False(registry.GetProperty("runtimeAcknowledged").GetBoolean());
		}

		Assert.Equal(0, await RunAsync(
			"armor-registry", "plan", fixture.SpecificationPath,
			"--registry-root", fixture.RegistryRoot,
			"--output", fixture.PlanPath,
			"--json"));
		using (var plan = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.PlanPath)))
		{
			Assert.Equal("hd2mod.armor-registry-plan", plan.RootElement.GetProperty("schema").GetString());
			Assert.Equal(4, plan.RootElement.GetProperty("desired").GetProperty("revision").GetInt32());
			Assert.Equal(2, plan.RootElement.GetProperty("desired").GetProperty("mappings").GetArrayLength());
		}

		Assert.Equal(0, await RunAsync(
			"armor-registry", "validate",
			"--plan", fixture.PlanPath,
			"--registry-root", fixture.RegistryRoot,
			"--json"));
		var apply = await CliTestConsole.RunCapturedAsync(
			"armor-registry", "apply",
			"--plan", fixture.PlanPath,
			"--registry-root", fixture.RegistryRoot,
			"--confirm",
			"--json");
		Assert.Equal(0, apply.ExitCode);
		using (var applyJson = apply.ParseJson())
		{
			var execution = applyJson.RootElement.GetProperty("execution");
			Assert.Equal("applied", execution.GetProperty("status").GetString());
			Assert.Equal(4, execution.GetProperty("revision").GetInt32());
			Assert.Equal(2, execution.GetProperty("activeMappingCount").GetInt32());
		}

		var protocol = await File.ReadAllTextAsync(fixture.ProtocolPath);
		Assert.Equal(
			"schema=2\nrevision=4\nenabled=true\nmap=488691438,820333399\nmap=718698115,820333399\n",
			protocol.Replace("\r\n", "\n", StringComparison.Ordinal));
		Assert.Equal(statusBefore, await File.ReadAllBytesAsync(fixture.RuntimeStatusPath));
		Assert.Equal(cacheBefore, await File.ReadAllBytesAsync(fixture.LocatorCachePath));
		Assert.Empty(Directory.EnumerateFiles(fixture.RegistryRoot, "*.tmp", SearchOption.TopDirectoryOnly));

		var secondApply = await CliTestConsole.RunCapturedAsync(
			"armor-registry", "apply",
			"--plan", fixture.PlanPath,
			"--registry-root", fixture.RegistryRoot,
			"--confirm",
			"--json");
		Assert.Equal(0, secondApply.ExitCode);
		using var secondJson = secondApply.ParseJson();
		Assert.Equal("already-applied", secondJson.RootElement.GetProperty("execution").GetProperty("status").GetString());
	}

	[Fact]
	public async Task ArmorRegistry_RejectsTamperedPlanAndProtocolDriftWithoutWriting()
	{
		await using var fixture = await ArmorRegistryFixture.CreateAsync(withProtocol: true);
		Assert.Equal(0, await RunAsync(
			"armor-registry", "plan", fixture.SpecificationPath,
			"--registry-root", fixture.RegistryRoot,
			"--output", fixture.PlanPath,
			"--json"));

		var forged = JsonNode.Parse(await File.ReadAllTextAsync(fixture.PlanPath))!.AsObject();
		forged["desired"]!["enabled"] = false;
		await File.WriteAllTextAsync(fixture.PlanPath, forged.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
		var forgedValidation = await CliTestConsole.RunCapturedAsync(
			"armor-registry", "validate",
			"--plan", fixture.PlanPath,
			"--registry-root", fixture.RegistryRoot,
			"--json");
		Assert.Equal(2, forgedValidation.ExitCode);
		using (var result = forgedValidation.ParseJson())
			Assert.Equal("ArmorRegistryPlanIntegrityMismatch", result.RootElement.GetProperty("error").GetProperty("code").GetString());

		Assert.Equal(0, await RunAsync(
			"armor-registry", "plan", fixture.SpecificationPath,
			"--registry-root", fixture.RegistryRoot,
			"--output", fixture.PlanPath,
			"--overwrite",
			"--json"));
		await File.WriteAllTextAsync(
			fixture.ProtocolPath,
			"schema=1\nrevision=9\nenabled=false\n");
		var driftedBytes = await File.ReadAllBytesAsync(fixture.ProtocolPath);

		var validation = await CliTestConsole.RunCapturedAsync(
			"armor-registry", "validate",
			"--plan", fixture.PlanPath,
			"--registry-root", fixture.RegistryRoot,
			"--json");
		Assert.Equal(2, validation.ExitCode);
		using (var validationJson = validation.ParseJson())
		{
			Assert.Equal("drifted", validationJson.RootElement.GetProperty("validation").GetProperty("status").GetString());
			Assert.Equal(
				"ArmorRegistryProtocolChanged",
				validationJson.RootElement.GetProperty("validation").GetProperty("diagnostics")[0].GetProperty("code").GetString());
		}

		var apply = await CliTestConsole.RunCapturedAsync(
			"armor-registry", "apply",
			"--plan", fixture.PlanPath,
			"--registry-root", fixture.RegistryRoot,
			"--confirm",
			"--json");
		Assert.Equal(2, apply.ExitCode);
		Assert.Equal(driftedBytes, await File.ReadAllBytesAsync(fixture.ProtocolPath));
	}

	[Fact]
	public async Task ArmorRegistry_FirstPlanCreatesProtocolAtRevisionOne()
	{
		await using var fixture = await ArmorRegistryFixture.CreateAsync(withProtocol: false);
		Assert.Equal(0, await RunAsync(
			"armor-registry", "plan", fixture.SpecificationPath,
			"--registry-root", fixture.RegistryRoot,
			"--output", fixture.PlanPath,
			"--json"));
		Assert.Equal(0, await RunAsync(
			"armor-registry", "apply",
			"--plan", fixture.PlanPath,
			"--registry-root", fixture.RegistryRoot,
			"--confirm",
			"--json"));

		var protocol = await File.ReadAllTextAsync(fixture.ProtocolPath);
		Assert.Contains("revision=1", protocol, StringComparison.Ordinal);
		Assert.Contains("map=488691438,820333399", protocol, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ArmorRegistry_RejectsDuplicateTargetsAndRootMismatch()
	{
		await using var fixture = await ArmorRegistryFixture.CreateAsync(withProtocol: true);
		await File.WriteAllTextAsync(
			fixture.SpecificationPath,
			"""
			{
			  "schema": "hd2mod.armor-registry-spec",
			  "enabled": true,
			  "mappings": [
			    { "targetItemId": 488691438, "sourceItemId": 820333399 },
			    { "targetItemId": 488691438, "sourceItemId": 718698115 }
			  ]
			}
			""");
		var duplicate = await CliTestConsole.RunCapturedAsync(
			"armor-registry", "plan", fixture.SpecificationPath,
			"--registry-root", fixture.RegistryRoot,
			"--output", fixture.PlanPath,
			"--json");
		Assert.Equal(2, duplicate.ExitCode);
		using (var duplicateJson = duplicate.ParseJson())
			Assert.Equal("ArmorRegistryMappingDuplicateTarget", duplicateJson.RootElement.GetProperty("error").GetProperty("code").GetString());

		await fixture.WriteValidSpecificationAsync();
		Assert.Equal(0, await RunAsync(
			"armor-registry", "plan", fixture.SpecificationPath,
			"--registry-root", fixture.RegistryRoot,
			"--output", fixture.PlanPath,
			"--json"));
		var otherRoot = Path.Combine(fixture.RootDirectory, "other-registry");
		var mismatch = await CliTestConsole.RunCapturedAsync(
			"armor-registry", "validate",
			"--plan", fixture.PlanPath,
			"--registry-root", otherRoot,
			"--json");
		Assert.Equal(2, mismatch.ExitCode);
		using var mismatchJson = mismatch.ParseJson();
		Assert.Equal("ArmorRegistryRootMismatch", mismatchJson.RootElement.GetProperty("error").GetProperty("code").GetString());
	}

	[Theory]
	[InlineData(
		"""
		{
		  "schema": "hd2mod.armor-registry-spec",
		  "enabled": true,
		  "models": [
		    {
		      "key": "custom/bad",
		      "pieces": [
		        { "body": 0, "unit": "0011223344556677", "slot": 0, "pieceType": 0, "tone": 0 }
		      ]
		    }
		  ],
		  "mappings": [
		    { "targetItemId": 488691438, "modelKey": "custom/bad" }
		  ]
		}
		""",
		"ArmorRegistryModelSlotInvalid")]
	[InlineData(
		"""
		{
		  "schema": "hd2mod.armor-registry-spec",
		  "enabled": true,
		  "models": [
		    {
		      "key": "custom/bad",
		      "pieces": [
		        { "body": 3, "unit": "0011223344556677", "slot": 1, "pieceType": 0, "tone": 0 },
		        { "body": 3, "unit": "8899aabbccddeeff", "slot": 1, "pieceType": 0, "tone": 0 }
		      ]
		    }
		  ],
		  "mappings": [
		    { "targetItemId": 488691438, "modelKey": "custom/bad" }
		  ]
		}
		""",
		"ArmorRegistryModelPieceDuplicate")]
	public async Task ArmorRegistry_RejectsUnsafeModelPieces(string specification, string expectedCode)
	{
		await using var fixture = await ArmorRegistryFixture.CreateAsync(withProtocol: true);
		await File.WriteAllTextAsync(fixture.SpecificationPath, specification);

		var result = await CliTestConsole.RunCapturedAsync(
			"armor-registry", "plan", fixture.SpecificationPath,
			"--registry-root", fixture.RegistryRoot,
			"--output", fixture.PlanPath,
			"--json");

		Assert.Equal(2, result.ExitCode);
		using var json = result.ParseJson();
		Assert.Equal(expectedCode, json.RootElement.GetProperty("error").GetProperty("code").GetString());
	}

	[Fact]
	public void ArmorRegistry_ModelPiecesRoundTripPreservesEnabledState()
	{
		var document = CliArmorRegistryProtocol.ParseProtocol(
			"""
			schema=2
			revision=7
			enabled=true
			model=custom/coat,
			model_piece=custom/coat,3,0011223344556677,0,0,0,true
			model_piece=custom/coat,3,8899aabbccddeeff,2,1,0,false
			map_model=488691438,custom/coat
			""");

		var model = Assert.Single(document.Models!);
		Assert.Equal([true, false], model.Pieces!.Select(piece => piece.Enabled).ToArray());
		var serialized = CliArmorRegistryProtocol.SerializeProtocol(document);
		Assert.Contains("model_piece=custom/coat,3,0011223344556677,0,0,0,true", serialized, StringComparison.Ordinal);
		Assert.Contains("model_piece=custom/coat,3,8899aabbccddeeff,2,1,0,false", serialized, StringComparison.Ordinal);
		var reparsed = CliArmorRegistryProtocol.ParseProtocol(serialized);
		Assert.Equal([true, false], reparsed.Models!.Single().Pieces!.Select(piece => piece.Enabled).ToArray());
	}

	private static async ValueTask<int> RunAsync(params string[] args)
		=> (await CliTestConsole.RunCapturedAsync(args)).ExitCode;

	private sealed class ArmorRegistryFixture : IAsyncDisposable
	{
		private ArmorRegistryFixture(string rootDirectory, string registryRoot)
		{
			RootDirectory = rootDirectory;
			RegistryRoot = registryRoot;
		}

		public string RootDirectory { get; }
		public string RegistryRoot { get; }
		public string ProtocolPath => Path.Combine(RegistryRoot, "armor-registry.protocol");
		public string RuntimeStatusPath => Path.Combine(RegistryRoot, "runtime.status");
		public string LocatorCachePath => Path.Combine(RegistryRoot, "locator.cache");
		public string SpecificationPath => Path.Combine(RootDirectory, "armor-registry-spec.json");
		public string PlanPath => Path.Combine(RootDirectory, "armor-registry-plan.json");

		public static async ValueTask<ArmorRegistryFixture> CreateAsync(bool withProtocol)
		{
			var root = Path.Combine(Path.GetTempPath(), "hd2-cli-armor-registry-" + Guid.NewGuid().ToString("N"));
			var registryRoot = Path.Combine(root, "registry");
			Directory.CreateDirectory(registryRoot);
			var fixture = new ArmorRegistryFixture(root, registryRoot);
			if (withProtocol)
			{
				await File.WriteAllTextAsync(
					fixture.ProtocolPath,
					"schema=1\nrevision=3\nenabled=true\nmap=488691438,820333399\n");
			}
			await File.WriteAllTextAsync(
				fixture.RuntimeStatusPath,
				"schema=1\nstate=ready\npid=2147483647\nrevision=3\nactive_mappings=1\n");
			await File.WriteAllTextAsync(
				fixture.LocatorCachePath,
				"schema=1\nrecord_count=411\nblob_bytes=384196\n");
			await fixture.WriteValidSpecificationAsync();
			return fixture;
		}

		public ValueTask WriteValidSpecificationAsync()
			=> new(File.WriteAllTextAsync(
				SpecificationPath,
				"""
				{
				  "schema": "hd2mod.armor-registry-spec",
				  "enabled": true,
				  "mappings": [
				    { "targetItemId": 718698115, "sourceItemId": 820333399 },
				    { "targetItemId": 488691438, "sourceItemId": 820333399 }
				  ]
				}
				"""));

		public ValueTask DisposeAsync()
		{
			try { Directory.Delete(RootDirectory, recursive: true); }
			catch { }
			return ValueTask.CompletedTask;
		}
	}
}
