using System.IO.Compression;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

// 作用：验证"文件夹结构导出"的语义——无 manifest、主体在 zip 根、选项即同名文件夹、
// 备注写成 Description.txt、VPK 版节点只输出 patch、Overwrite 开关只换 patch。
// Purpose: Verifies the folder-structure export: no manifest, host at zip root, each Option as a
// same-named folder, notes as Description.txt, VPK nodes exporting only their patch payload, and
// the Overwrite switch replacing patches only.
public sealed class ModFolderTreeExporterTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "hd2-folder-export-" + Guid.NewGuid().ToString("N"));

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
				File.SetAttributes(file, FileAttributes.Normal);
			Directory.Delete(_root, true);
		}
	}

	[Fact]
	public async Task Export_PutsHostAtZipRootAndEachOptionInItsOwnFolder()
	{
		var library = Path.Combine(_root, "mods");
		Directory.CreateDirectory(Path.Combine(library, "host"));
		Directory.CreateDirectory(Path.Combine(library, "option-a"));
		WriteFile(Path.Combine(library, "host", "aaaa.patch_0"), "host-patch");
		WriteFile(Path.Combine(library, "host", "option.json"), "{ }");
		WriteFile(Path.Combine(library, "option-a", "bbbb.patch_0"), "option-patch");

		var host = Node("host", "我的 Mod", notes: "这是备注");
		var option = Node("option-a", "选项 A", kind: ModNodeKind.Option, parents: [host.Id]);
		var snapshot = Snapshot(host, option);

		var zipPath = Path.Combine(_root, "out.zip");
		var result = await new ModFolderTreeExporter(new StoragePaths(_root)).ExportAsync(
			host.Id, snapshot, zipPath, includeOverwrite: false);

		Assert.Equal(2, result.NodeCount);
		using var zip = ZipFile.OpenRead(zipPath);
		var names = zip.Entries.Select(entry => entry.FullName).OrderBy(name => name, StringComparer.Ordinal).ToArray();

		// 主体文件在 zip 根，选项在**同名文件夹**里——不存在 manifest.json。
		Assert.Contains("aaaa.patch_0", names);
		Assert.Contains("选项 A/bbbb.patch_0", names);
		Assert.Contains("Description.txt", names);
		Assert.DoesNotContain(names, name => name.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase));
		// 管理器自己的记账文件不进包。
		Assert.DoesNotContain(names, name => name.Equals("option.json", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task Export_TranslatesNestedOptionsIntoNestedFolders()
	{
		var library = Path.Combine(_root, "mods");
		foreach (var folder in new[] { "host", "outer", "inner" })
			Directory.CreateDirectory(Path.Combine(library, folder));
		WriteFile(Path.Combine(library, "host", "aaaa.patch_0"), "x");
		WriteFile(Path.Combine(library, "outer", "bbbb.patch_0"), "y");
		WriteFile(Path.Combine(library, "inner", "cccc.patch_0"), "z");

		var host = Node("host", "宿主");
		var outer = Node("outer", "外层", kind: ModNodeKind.Option, parents: [host.Id]);
		var inner = Node("inner", "内层", kind: ModNodeKind.Option, parents: [outer.Id]);
		var snapshot = Snapshot(host, outer, inner);

		var zipPath = Path.Combine(_root, "out.zip");
		await new ModFolderTreeExporter(new StoragePaths(_root)).ExportAsync(host.Id, snapshot, zipPath, includeOverwrite: false);

		using var zip = ZipFile.OpenRead(zipPath);
		Assert.Contains(zip.Entries, entry => entry.FullName == "外层/内层/cccc.patch_0");
	}

	[Fact]
	public async Task Export_OfVpkNodeWritesOnlyThePatchFromVpk2Patch()
	{
		var library = Path.Combine(_root, "mods");
		var node = Path.Combine(library, "vpk-mod");
		Directory.CreateDirectory(Path.Combine(node, "vpk2patch"));
		// 根上的来源 .vpk 不该进包；vpk2patch 里的 patch 才该进。
		WriteFile(Path.Combine(node, "big.vpk"), "source-vpk");
		WriteFile(Path.Combine(node, "vpk2patch.json"), "{ }");
		WriteFile(Path.Combine(node, "vpk2patch", "9ba6.patch_0"), "patch");
		WriteFile(Path.Combine(node, "vpk2patch", "9ba6.patch_0.gpu_resources"), "gpu");
		WriteFile(Path.Combine(node, "vpk2patch", "sections.json"), "{ }");

		var host = Node("vpk-mod", "VPK Mod");
		var zipPath = Path.Combine(_root, "out.zip");
		await new ModFolderTreeExporter(new StoragePaths(_root)).ExportAsync(
			host.Id, Snapshot(host), zipPath, includeOverwrite: false);

		using var zip = ZipFile.OpenRead(zipPath);
		var names = zip.Entries.Select(entry => entry.FullName).ToArray();
		Assert.Contains("9ba6.patch_0", names);
		Assert.Contains("9ba6.patch_0.gpu_resources", names);
		Assert.DoesNotContain("big.vpk", names);
		Assert.DoesNotContain("vpk2patch.json", names);
		Assert.DoesNotContain("sections.json", names);
	}

	[Fact]
	public async Task Export_WithOverwriteWritesOnlyTheOverwritePatch()
	{
		var library = Path.Combine(_root, "mods");
		var node = Path.Combine(library, "vpk-mod");
		Directory.CreateDirectory(Path.Combine(node, "vpk2patch"));
		Directory.CreateDirectory(Path.Combine(node, "Overwrite"));
		WriteFile(Path.Combine(node, "vpk2patch", "9ba6.patch_0"), "base-patch");
		WriteFile(Path.Combine(node, "Overwrite", "9ba6.patch_0"), "overwrite-patch");
		WriteFile(Path.Combine(node, "Overwrite", "9ba6.patch_0.gpu_resources"), "overwrite-gpu");

		var host = Node("vpk-mod", "VPK Mod");
		var zipPath = Path.Combine(_root, "out.zip");
		await new ModFolderTreeExporter(new StoragePaths(_root)).ExportAsync(
			host.Id, Snapshot(host), zipPath, includeOverwrite: true);

		using var zip = ZipFile.OpenRead(zipPath);
		Assert.Equal("overwrite-patch", ReadEntry(zip, "9ba6.patch_0"));
		Assert.Equal("overwrite-gpu", ReadEntry(zip, "9ba6.patch_0.gpu_resources"));
	}

	[Fact]
	public async Task Export_WithOverwriteFallsBackToBaseWhenTheNodeHasNoOverwrite()
	{
		var library = Path.Combine(_root, "mods");
		var node = Path.Combine(library, "vpk-mod");
		Directory.CreateDirectory(Path.Combine(node, "vpk2patch"));
		WriteFile(Path.Combine(node, "vpk2patch", "9ba6.patch_0"), "base-patch");

		var host = Node("vpk-mod", "VPK Mod");
		var zipPath = Path.Combine(_root, "out.zip");
		var result = await new ModFolderTreeExporter(new StoragePaths(_root)).ExportAsync(
			host.Id, Snapshot(host), zipPath, includeOverwrite: true);

		using var zip = ZipFile.OpenRead(zipPath);
		Assert.Equal("base-patch", ReadEntry(zip, "9ba6.patch_0"));
		// 勾了但没 Overwrite，必须说一声，而不是默默给基础版。
		Assert.Contains(result.Warnings, warning => warning.Contains("Overwrite", StringComparison.Ordinal));
	}

	[Fact]
	public async Task Export_DeduplicatesOptionFolderNamesInsteadOfMergingThem()
	{
		var library = Path.Combine(_root, "mods");
		foreach (var folder in new[] { "host", "a", "b" })
			Directory.CreateDirectory(Path.Combine(library, folder));
		WriteFile(Path.Combine(library, "a", "aaaa.patch_0"), "a");
		WriteFile(Path.Combine(library, "b", "bbbb.patch_0"), "b");

		var host = Node("host", "宿主");
		var first = Node("a", "同名", kind: ModNodeKind.Option, parents: [host.Id]);
		var second = Node("b", "同名", kind: ModNodeKind.Option, parents: [host.Id]);
		var snapshot = Snapshot(host, first, second);

		var zipPath = Path.Combine(_root, "out.zip");
		await new ModFolderTreeExporter(new StoragePaths(_root)).ExportAsync(host.Id, snapshot, zipPath, includeOverwrite: false);

		using var zip = ZipFile.OpenRead(zipPath);
		var patchEntries = zip.Entries
			.Where(entry => entry.FullName.EndsWith(".patch_0", StringComparison.OrdinalIgnoreCase))
			.Select(entry => entry.FullName)
			.ToArray();

		// 两个同名选项各自保留：两份 patch 必须在**不同的两个文件夹**里，不能合并成一个。
		Assert.Equal(2, patchEntries.Length);
		var folders = patchEntries.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		Assert.Equal(2, folders.Length);
		Assert.All(folders, folder => Assert.StartsWith("同名", folder, StringComparison.Ordinal));
	}

	private static ModNode Node(
		string relativePath,
		string name,
		ModNodeKind kind = ModNodeKind.Standard,
		string? notes = null,
		IReadOnlyList<ModNodeId>? parents = null)
		=> new(
			ModNodeId.New(),
			relativePath,
			new ModNodeMetadata(name, notes, DateTimeOffset.UtcNow, null)
			{
				Kind = kind,
				HostModGuids = parents?.Select(id => id.Value.ToString("D")).ToArray(),
			},
			Array.Empty<PatchGroupKey>(),
			Array.Empty<ModNodeId>());

	private static LibrarySnapshot Snapshot(params ModNode[] nodes)
		=> new(1, DateTimeOffset.UtcNow, nodes.ToDictionary(node => node.Id), Array.Empty<Profile>(), null);

	private static void WriteFile(string path, string content)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, content);
	}

	private static string ReadEntry(ZipArchive zip, string name)
	{
		var entry = zip.Entries.Single(item => item.FullName == name);
		using var reader = new StreamReader(entry.Open());
		return reader.ReadToEnd();
	}
}
