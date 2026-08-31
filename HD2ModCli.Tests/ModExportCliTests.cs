using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using Xunit;

namespace HD2ModCli.Tests;

[Collection("cli-console")]
public sealed class ModExportCliTests
{
	[Fact]
	public async Task ModExport_RoundTripsRecursiveOptionsAndNestedPayloads()
	{
		await using var fixture = await ExportFixture.CreateAsync();
		var planPath = Path.Combine(fixture.RootDirectory, "mod-export-plan.json");
		var outputDirectory = Path.Combine(fixture.RootDirectory, "exports");

		var planned = await CliTestConsole.RunCapturedAsync(
			"mod", "export", "plan", fixture.RootSelector,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--output", planPath,
			"--json");
		Assert.Equal(0, planned.ExitCode);
		using (var plan = planned.ParseJson())
		{
			var export = plan.RootElement.GetProperty("export");
			Assert.Equal("hd2mod.mod-export-plan", export.GetProperty("schema").GetString());
			var nodes = export.GetProperty("nodes").EnumerateArray().ToArray();
			Assert.Equal(3, nodes.Length);
			Assert.Equal(string.Empty, nodes[0].GetProperty("packagePath").GetString());
			Assert.Equal(1, nodes[0].GetProperty("childNodeIds").GetArrayLength());
			Assert.Contains(nodes.SelectMany(node => node.GetProperty("files").EnumerateArray()), file =>
				file.GetProperty("archiveRelativePath").GetString() == "options/" + fixture.Alpha.Id.Value.ToString("N") + "/nested/payload.bin");
		}

		var validation = await CliTestConsole.RunCapturedAsync(
			"mod", "export", "validate",
			"--plan", planPath,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--json");
		Assert.Equal(0, validation.ExitCode);

		var applied = await CliTestConsole.RunCapturedAsync(
			"mod", "export", "apply",
			"--plan", planPath,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--output", outputDirectory,
			"--confirm", "--json");
		Assert.Equal(0, applied.ExitCode);
		string zipPath;
		using (var result = applied.ParseJson())
		{
			var execution = result.RootElement.GetProperty("execution");
			Assert.True(execution.GetProperty("success").GetBoolean());
			Assert.False(execution.GetProperty("overwroteExisting").GetBoolean());
			zipPath = execution.GetProperty("zipPath").GetString()!;
		}

		Assert.True(File.Exists(zipPath));
		using (var zip = ZipFile.OpenRead(zipPath))
		{
			var names = zip.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
			Assert.Contains("manifest.json", names);
			Assert.Contains("aaaaaaaaaaaaaaaa.patch_0", names);
			Assert.Contains("options/" + fixture.Alpha.Id.Value.ToString("N") + "/bbbbbbbbbbbbbbbb.patch_0", names);
			Assert.Contains("options/" + fixture.Alpha.Id.Value.ToString("N") + "/nested/payload.bin", names);
			Assert.Contains("options/" + fixture.Beta.Id.Value.ToString("N") + "/cccccccccccccccc.patch_0", names);
			Assert.DoesNotContain(names, name => name.Contains("Overwrite", StringComparison.OrdinalIgnoreCase));

			using var manifest = JsonDocument.Parse(await ReadEntryAsync(zip, "manifest.json"));
			var root = manifest.RootElement;
			Assert.Equal(fixture.Root.Id.Value.ToString("D"), root.GetProperty("guid").GetString());
			Assert.Equal(fixture.Root.Metadata.Name, root.GetProperty("name").GetString());
			var option = Assert.Single(root.GetProperty("options").EnumerateArray());
			Assert.Equal(fixture.Alpha.Metadata.Name, option.GetProperty("name").GetString());
			var subOption = Assert.Single(option.GetProperty("subOptions").EnumerateArray());
			Assert.Equal(fixture.Beta.Metadata.Name, subOption.GetProperty("name").GetString());
		}

		var imported = await fixture.ImportIntoFreshLibraryAsync(zipPath);
		Assert.Contains(fixture.Root.Id, imported.Nodes.Keys);
		Assert.Contains(fixture.Alpha.Id, imported.Nodes.Keys);
		Assert.Contains(fixture.Beta.Id, imported.Nodes.Keys);
		var graph = OptionRelationshipGraph.Create(imported);
		Assert.True(
			graph.GetDirectOptions(fixture.Root.Id).Select(node => node.Id).SequenceEqual([fixture.Alpha.Id]),
			$"root={fixture.Root.Id}; alpha hosts={string.Join(',', imported.Nodes[fixture.Alpha.Id].Metadata.HostModGuids ?? [])}; imported ids={string.Join(',', imported.Nodes.Keys)}");
		Assert.Equal([fixture.Beta.Id], graph.GetDirectOptions(fixture.Alpha.Id).Select(node => node.Id));
	}

	[Fact]
	public async Task ModExport_RejectsSourceDriftAndRequiresExplicitOverwrite()
	{
		await using var fixture = await ExportFixture.CreateAsync();
		var planPath = Path.Combine(fixture.RootDirectory, "drift-plan.json");
		var outputDirectory = Path.Combine(fixture.RootDirectory, "exports");
		Assert.Equal(0, (await CliTestConsole.RunCapturedAsync(
			"mod", "export", "plan", fixture.RootSelector,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--output", planPath,
			"--json")).ExitCode);

		await File.AppendAllTextAsync(fixture.RootPatchPath, "drift");
		var validation = await CliTestConsole.RunCapturedAsync(
			"mod", "export", "validate", "--plan", planPath,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot, "--json");
		Assert.True(validation.ExitCode == 2, $"stdout={validation.StandardOutput}; stderr={validation.StandardError}");
		using (var validationJson = validation.ParseJson())
		{
			var codes = validationJson.RootElement.GetProperty("validation").GetProperty("diagnostics")
				.EnumerateArray().Select(item => item.GetProperty("code").GetString()).ToArray();
			Assert.Contains("ModExportInputChanged", codes);
		}

		// A fresh plan is needed after the source changed.
		Assert.Equal(0, (await CliTestConsole.RunCapturedAsync(
			"mod", "export", "plan", fixture.RootSelector,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--output", planPath,
			"--overwrite", "--json")).ExitCode);
		Assert.Equal(0, (await CliTestConsole.RunCapturedAsync(
			"mod", "export", "apply", "--plan", planPath,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--output", outputDirectory,
			"--confirm", "--json")).ExitCode);

		var withoutOverwrite = await CliTestConsole.RunCapturedAsync(
			"mod", "export", "apply", "--plan", planPath,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--output", outputDirectory,
			"--confirm", "--json");
		Assert.Equal(2, withoutOverwrite.ExitCode);
		using (var error = withoutOverwrite.ParseJson())
			Assert.Equal("ModExportOutputAlreadyExists", error.RootElement.GetProperty("error").GetProperty("code").GetString());

		var withOverwrite = await CliTestConsole.RunCapturedAsync(
			"mod", "export", "apply", "--plan", planPath,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--output", outputDirectory,
			"--overwrite", "--confirm", "--json");
		Assert.Equal(0, withOverwrite.ExitCode);
		using var overwritten = withOverwrite.ParseJson();
		Assert.True(overwritten.RootElement.GetProperty("execution").GetProperty("overwroteExisting").GetBoolean());
	}

	[Fact]
	public async Task ModExport_RoundTripsEmptyHostWithOptions()
	{
		await using var fixture = await ExportFixture.CreateAsync(includeRootPatch: false);
		var planPath = Path.Combine(fixture.RootDirectory, "empty-host-plan.json");
		var outputDirectory = Path.Combine(fixture.RootDirectory, "empty-host-export");

		Assert.Equal(0, (await CliTestConsole.RunCapturedAsync(
			"mod", "export", "plan", fixture.RootSelector,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--output", planPath, "--json")).ExitCode);
		Assert.Equal(0, (await CliTestConsole.RunCapturedAsync(
			"mod", "export", "apply", "--plan", planPath,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--output", outputDirectory, "--confirm", "--json")).ExitCode);

		var zipPath = Directory.EnumerateFiles(outputDirectory, "*.zip").Single();
		var imported = await fixture.ImportIntoFreshLibraryAsync(zipPath);
		Assert.Contains(fixture.Root.Id, imported.Nodes.Keys);
		Assert.Empty(imported.Nodes[fixture.Root.Id].PatchGroups);
		var graph = OptionRelationshipGraph.Create(imported);
		Assert.Equal([fixture.Alpha.Id], graph.GetDirectOptions(fixture.Root.Id).Select(node => node.Id));
	}

	[Fact]
	public async Task ModExport_RejectsPlanTamperingAndLibraryOutput()
	{
		await using var fixture = await ExportFixture.CreateAsync();
		var planPath = Path.Combine(fixture.RootDirectory, "integrity-plan.json");
		Assert.Equal(0, (await CliTestConsole.RunCapturedAsync(
			"mod", "export", "plan", fixture.RootSelector,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--output", planPath, "--json")).ExitCode);

		var plan = JsonNode.Parse(await File.ReadAllTextAsync(planPath))!.AsObject();
		plan["nodes"]![0]!["name"] = "forged";
		await File.WriteAllTextAsync(planPath, plan.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
		var tampered = await CliTestConsole.RunCapturedAsync(
			"mod", "export", "validate", "--plan", planPath,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot, "--json");
		Assert.Equal(2, tampered.ExitCode);
		using (var error = tampered.ParseJson())
			Assert.Equal("ModExportPlanIntegrityMismatch", error.RootElement.GetProperty("error").GetProperty("code").GetString());

		var freshPlanPath = Path.Combine(fixture.RootDirectory, "library-output-plan.json");
		Assert.Equal(0, (await CliTestConsole.RunCapturedAsync(
			"mod", "export", "plan", fixture.RootSelector,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--output", freshPlanPath, "--json")).ExitCode);
		var blocked = await CliTestConsole.RunCapturedAsync(
			"mod", "export", "apply", "--plan", freshPlanPath,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--output", Path.Combine(fixture.LibraryRoot, "export-out"),
			"--confirm", "--json");
		Assert.Equal(2, blocked.ExitCode);
		using var blockedJson = blocked.ParseJson();
		Assert.Equal("ModExportOutputInsideLibrary", blockedJson.RootElement.GetProperty("error").GetProperty("code").GetString());
	}

	private static async Task<string> ReadEntryAsync(ZipArchive archive, string name)
	{
		await using var stream = archive.GetEntry(name)!.Open();
		using var reader = new StreamReader(stream);
		return await reader.ReadToEndAsync();
	}

	private sealed class ExportFixture : IAsyncDisposable
	{
		private ExportFixture(string rootDirectory, string managerRoot, string libraryRoot, ModNode root, ModNode alpha, ModNode beta)
		{
			RootDirectory = rootDirectory;
			ManagerRoot = managerRoot;
			LibraryRoot = libraryRoot;
			Root = root;
			Alpha = alpha;
			Beta = beta;
		}

		public string RootDirectory { get; }
		public string ManagerRoot { get; }
		public string LibraryRoot { get; }
		public ModNode Root { get; }
		public ModNode Alpha { get; }
		public ModNode Beta { get; }
		public string RootSelector => Root.Id.Value.ToString("D");
		public string RootPatchPath => Path.Combine(LibraryRoot, Root.RelativePath, "aaaaaaaaaaaaaaaa.patch_0");

		public static async ValueTask<ExportFixture> CreateAsync(bool includeRootPatch = true)
		{
			var rootDirectory = Path.Combine(Path.GetTempPath(), "hd2-cli-mod-export-" + Guid.NewGuid().ToString("N"));
			var managerRoot = Path.Combine(rootDirectory, "manager");
			var libraryRoot = Path.Combine(rootDirectory, "library");
			Directory.CreateDirectory(managerRoot);
			Directory.CreateDirectory(libraryRoot);
			await File.WriteAllTextAsync(
				Path.Combine(managerRoot, "settings.json"),
				JsonSerializer.Serialize(new { ModLibraryFolder = libraryRoot }));

			var now = DateTimeOffset.UtcNow;
			var hostId = ModNodeId.New();
			var alphaId = ModNodeId.New();
			var betaId = ModNodeId.New();
			var root = new ModNode(
				hostId,
				"root-store",
				new ModNodeMetadata("Export Root", "root notes", now, now),
				[new PatchGroupKey("aaaaaaaaaaaaaaaa", 0)],
				[]);
			var alpha = new ModNode(
				alphaId,
				"alpha-store",
				new ModNodeMetadata("Alpha Option", "alpha notes", now, now, ModNodeKind.Option, [hostId.Value.ToString("N")], OptionOrder: 0),
				[new PatchGroupKey("bbbbbbbbbbbbbbbb", 0)],
				[]);
			var beta = new ModNode(
				betaId,
				"beta-store",
				new ModNodeMetadata("Beta Suboption", "beta notes", now, now, ModNodeKind.Option, [alphaId.Value.ToString("N")], OptionOrder: 0),
				[new PatchGroupKey("cccccccccccccccc", 0)],
				[]);
			var snapshot = new LibrarySnapshot(
				1,
				now,
				new Dictionary<ModNodeId, ModNode> { [root.Id] = root, [alpha.Id] = alpha, [beta.Id] = beta },
				Array.Empty<Profile>(),
				null);
			await new JsonModLibraryStore(new StoragePaths(managerRoot, libraryRoot), allowLegacyMigration: false).SaveAsync(snapshot);

			var rootDirectoryPath = Path.Combine(libraryRoot, root.RelativePath);
			var alphaDirectoryPath = Path.Combine(libraryRoot, alpha.RelativePath);
			var betaDirectoryPath = Path.Combine(libraryRoot, beta.RelativePath);
			Directory.CreateDirectory(Path.Combine(alphaDirectoryPath, "nested"));
			Directory.CreateDirectory(Path.Combine(rootDirectoryPath, "Overwrite"));
			Directory.CreateDirectory(betaDirectoryPath);
			if (includeRootPatch)
				await File.WriteAllBytesAsync(Path.Combine(rootDirectoryPath, "aaaaaaaaaaaaaaaa.patch_0"), [1, 2, 3]);
			await File.WriteAllBytesAsync(Path.Combine(rootDirectoryPath, "icon.png"), [4, 5, 6]);
			await File.WriteAllBytesAsync(Path.Combine(rootDirectoryPath, "Overwrite", "generated.patch_0"), [9]);
			await File.WriteAllBytesAsync(Path.Combine(alphaDirectoryPath, "bbbbbbbbbbbbbbbb.patch_0"), [7, 8]);
			await File.WriteAllBytesAsync(Path.Combine(alphaDirectoryPath, "nested", "payload.bin"), [9, 10, 11]);
			await File.WriteAllTextAsync(Path.Combine(alphaDirectoryPath, "option.json"), "{\"kind\":\"Option\"}");
			await File.WriteAllBytesAsync(Path.Combine(betaDirectoryPath, "cccccccccccccccc.patch_0"), [12]);

			return new ExportFixture(rootDirectory, managerRoot, libraryRoot, root, alpha, beta);
		}

		public async ValueTask<LibrarySnapshot> ImportIntoFreshLibraryAsync(string zipPath)
		{
			var freshManager = Path.Combine(RootDirectory, "fresh-manager");
			var freshLibrary = Path.Combine(RootDirectory, "fresh-library");
			Directory.CreateDirectory(freshManager);
			Directory.CreateDirectory(freshLibrary);
			await File.WriteAllTextAsync(Path.Combine(freshManager, "settings.json"), JsonSerializer.Serialize(new { ModLibraryFolder = freshLibrary }));
			var paths = new StoragePaths(freshManager, freshLibrary);
			await new JsonModLibraryStore(paths, allowLegacyMigration: false).SaveAsync(
				new LibrarySnapshot(1, DateTimeOffset.UtcNow, new Dictionary<ModNodeId, ModNode>(), Array.Empty<Profile>()));
			var result = await CoreServices.CreateModImportExecutionService(paths).ImportArchiveAsync(zipPath);
			return result.Snapshot;
		}

		public ValueTask DisposeAsync()
		{
			try { Directory.Delete(RootDirectory, recursive: true); } catch { }
			return ValueTask.CompletedTask;
		}
	}
}
