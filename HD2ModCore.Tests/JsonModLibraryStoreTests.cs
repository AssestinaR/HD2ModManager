using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

// 作用：验证 JSON 模组库持久化存储的读写一致性与库/Profile 拆分。
// Purpose: Verifies JSON mod library store save/load roundtrip and library/Profile split.
public sealed class JsonModLibraryStoreTests
{
	[Fact]
	public async Task SaveAndLoad_Roundtrip_Works()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);

		try
		{
			var paths = new StoragePaths(root);
			var store = new JsonModLibraryStore(paths);

			var nodeId = ModNodeId.New();
			var node = new ModNode(
				Id: nodeId,
				RelativePath: "obj",
				Metadata: new ModNodeMetadata("obj", "n", DateTimeOffset.UtcNow, null),
				PatchGroups: new[] { new PatchGroupKey("9ba626afa44a3aa3", 0) },
				Children: Array.Empty<ModNodeId>());

			var profile = new Profile(ProfileId.New(), "p", DateTimeOffset.UtcNow, null, new[] { new ProfileEntry(nodeId, 0) });
			var snapshot = new LibrarySnapshot(Version: 2, SavedUtc: DateTimeOffset.UtcNow, new Dictionary<ModNodeId, ModNode> { [nodeId] = node }, new[] { profile }, profile.Id);

			await store.SaveAsync(snapshot);
			var loaded = await store.TryLoadAsync();
			var libraryJson = await File.ReadAllTextAsync(paths.LibraryPath);
			var profilesJson = await File.ReadAllTextAsync(paths.ProfilesPath);

			Assert.NotNull(loaded);
			Assert.Single(loaded!.Nodes);
			Assert.Single(loaded.Profiles);
			Assert.Equal(profile.Id, loaded.ActiveProfileId);
			Assert.True(loaded.Nodes.ContainsKey(nodeId));
			Assert.Equal("p", loaded.Profiles[0].Name);
			Assert.Equal("obj", loaded.Nodes[nodeId].Metadata.Name);
			Assert.True(File.Exists(Path.Combine(paths.ModsDirectory, "library.json")));
			Assert.DoesNotContain("profiles", libraryJson, StringComparison.OrdinalIgnoreCase);
			Assert.Contains("\"p\"", profilesJson);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task SaveAsync_KeepsChineseReadable()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);

		try
		{
			var paths = new StoragePaths(root);
			var store = new JsonModLibraryStore(paths);
			var nodeId = ModNodeId.New();
			var snapshot = new LibrarySnapshot(
				Version: 2,
				SavedUtc: DateTimeOffset.UtcNow,
				Nodes: new Dictionary<ModNodeId, ModNode>
				{
					[nodeId] = new ModNode(
						nodeId,
						Path.Combine("中文包", "护甲"),
						new ModNodeMetadata("中文模组", null, DateTimeOffset.UtcNow, null),
						Array.Empty<PatchGroupKey>(),
						Array.Empty<ModNodeId>()),
				},
				Profiles: new[]
				{
					new Profile(ProfileId.New(), "中文配置", DateTimeOffset.UtcNow, null, Array.Empty<ProfileEntry>()),
				});

			await store.SaveAsync(snapshot);

			var libraryJson = await File.ReadAllTextAsync(paths.LibraryPath);
			var profilesJson = await File.ReadAllTextAsync(paths.ProfilesPath);

			Assert.Contains("中文模组", libraryJson);
			Assert.Contains("中文包", libraryJson);
			Assert.DoesNotContain("\\u4e2d", libraryJson, StringComparison.OrdinalIgnoreCase);
			Assert.Contains("中文配置", profilesJson);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task TryLoadAsync_UpgradesLegacyProfileListWithoutStartupFailure()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var paths = new StoragePaths(root);
			Directory.CreateDirectory(paths.ModsDirectory);
			var profileId = ProfileId.New();
			await File.WriteAllTextAsync(paths.ProfilesPath, $$"""
			[
			  {
			    "id": "{{profileId.Value:N}}",
			    "name": "旧配置",
			    "createdUtc": "2026-07-14T00:00:00+00:00",
			    "modifiedUtc": null,
			    "entries": []
			  }
			]
			""");
			var store = new JsonProfileStore(paths);

			var loaded = await store.TryLoadAsync();
			var rewritten = await File.ReadAllTextAsync(paths.ProfilesPath);

			Assert.Equal("旧配置", Assert.Single(loaded.Profiles).Name);
			Assert.Null(loaded.ActiveProfileId);
			Assert.Contains("\"version\": 2", rewritten);
			Assert.Contains("\"profiles\"", rewritten);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task TryLoadAsync_BacksUpInvalidProfileFileAndRecreatesEmptyState()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var paths = new StoragePaths(root);
			Directory.CreateDirectory(paths.ModsDirectory);
			const string invalidJson = "{ invalid";
			await File.WriteAllTextAsync(paths.ProfilesPath, invalidJson);
			var store = new JsonProfileStore(paths);

			var loaded = await store.TryLoadAsync();
			var backups = Directory.EnumerateFiles(paths.ModsDirectory, "profiles.json.invalid-*.bak").ToList();

			Assert.Empty(loaded.Profiles);
			var backup = Assert.Single(backups);
			Assert.Equal(invalidJson, await File.ReadAllTextAsync(backup));
			Assert.Contains("\"version\": 2", await File.ReadAllTextAsync(paths.ProfilesPath));
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task TryLoadAsync_WhenLegacyMigrationIsDisabled_DoesNotReadOrPersistLegacyLibrary()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		try
		{
			var managerRoot = Path.Combine(root, "manager");
			var libraryRoot = Path.Combine(root, "new-library");
			var paths = new StoragePaths(managerRoot, libraryRoot);
			var legacyPath = Path.Combine(paths.DataDirectory, "library", "library.json");
			Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
			await File.WriteAllTextAsync(legacyPath, """
			{
			  "version": 1,
			  "savedUtc": "2026-08-30T00:00:00+00:00",
			  "nodes": {},
			  "profiles": []
			}
			""");

			var store = new JsonModLibraryStore(paths, allowLegacyMigration: false);
			var loaded = await store.TryLoadAsync();

			Assert.Null(loaded);
			Assert.True(File.Exists(legacyPath));
			Assert.False(File.Exists(paths.LibraryPath));
			Assert.False(File.Exists(paths.ProfilesPath));
			Assert.False(Directory.Exists(paths.ModsDirectory));
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}
}
