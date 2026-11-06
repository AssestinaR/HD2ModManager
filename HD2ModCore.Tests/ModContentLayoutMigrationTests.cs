using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 锁住「基底目录一次性改名」：只改目录名、不动文件；新旧同时存在时只报告不合并。
/// <para>
/// 这是整次重构里唯一会主动移动用户库内目录的操作，所以每条分支都单独钉住——
/// 尤其"冲突时不动"：猜错会让用户看到的观感静默改变。
/// </para>
/// </summary>
public sealed class ModContentLayoutMigrationTests : IDisposable
{
	private readonly string _root = Path.Combine(
		Path.GetTempPath(), "hd2-layout-migration-tests", Guid.NewGuid().ToString("N"));

	private const string PatchName = "9ba626afa44a3aa3.patch_0";

	public ModContentLayoutMigrationTests()
	{
		Directory.CreateDirectory(_root);
	}

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch { }
	}

	private string CreateMod(string name, string? layerName, string content = "toc")
	{
		var node = Path.Combine(_root, name);
		Directory.CreateDirectory(node);
		if (layerName is not null)
		{
			var layer = Path.Combine(node, layerName);
			Directory.CreateDirectory(layer);
			File.WriteAllText(Path.Combine(layer, PatchName), content);
		}
		return node;
	}

	[Fact]
	public async Task MigrateAsync_RenamesLegacyLayerAndKeepsFiles()
	{
		var node = CreateMod("Elise_Bill", ModNodePayloadLayout.LegacyVpkPatchDirectoryName, "payload");

		var result = await CoreServices.CreateModContentLayoutMigration().MigrateAsync(_root);

		Assert.Equal(1, result.Renamed);
		Assert.True(result.IsClean);
		Assert.False(Directory.Exists(Path.Combine(node, ModNodePayloadLayout.LegacyVpkPatchDirectoryName)));
		var migrated = Path.Combine(node, ModNodePayloadLayout.VpkPatchDirectoryName, PatchName);
		Assert.True(File.Exists(migrated));
		Assert.Equal("payload", File.ReadAllText(migrated));
	}

	[Fact]
	public async Task MigrateAsync_LeavesCurrentLayoutAlone()
	{
		var node = CreateMod("AlreadyNew", ModNodePayloadLayout.VpkPatchDirectoryName);

		var result = await CoreServices.CreateModContentLayoutMigration().MigrateAsync(_root);

		Assert.Equal(0, result.Renamed);
		Assert.Equal(1, result.AlreadyCurrent);
		Assert.True(Directory.Exists(Path.Combine(node, ModNodePayloadLayout.VpkPatchDirectoryName)));
	}

	[Fact]
	public async Task MigrateAsync_ReportsConflictAndTouchesNothing_WhenBothLayersExist()
	{
		var node = CreateMod("Both", ModNodePayloadLayout.LegacyVpkPatchDirectoryName, "legacy");
		var currentLayer = Path.Combine(node, ModNodePayloadLayout.VpkPatchDirectoryName);
		Directory.CreateDirectory(currentLayer);
		File.WriteAllText(Path.Combine(currentLayer, PatchName), "current");

		var result = await CoreServices.CreateModContentLayoutMigration().MigrateAsync(_root);

		Assert.Equal(0, result.Renamed);
		Assert.Equal(["Both"], result.Conflicts);
		Assert.False(result.IsClean);
		// 两个目录都还在，内容都没被碰过——猜错会静默改变观感，所以这里只报告。
		Assert.Equal("legacy", File.ReadAllText(
			Path.Combine(node, ModNodePayloadLayout.LegacyVpkPatchDirectoryName, PatchName)));
		Assert.Equal("current", File.ReadAllText(Path.Combine(currentLayer, PatchName)));
	}

	[Fact]
	public async Task MigrateAsync_SkipsManagerOwnedDirectories()
	{
		// .hd2modmanager/ 里放的是映射工作区；换载荷备份目录同理，都不参与改名。
		var manager = Path.Combine(_root, ".hd2modmanager");
		Directory.CreateDirectory(Path.Combine(manager, ModNodePayloadLayout.LegacyVpkPatchDirectoryName));

		var result = await CoreServices.CreateModContentLayoutMigration().MigrateAsync(_root);

		Assert.Equal(0, result.Renamed);
		Assert.True(Directory.Exists(Path.Combine(manager, ModNodePayloadLayout.LegacyVpkPatchDirectoryName)));
	}

	[Fact]
	public async Task MigrateAsync_OnMissingRoot_IsNoOp()
	{
		var result = await CoreServices.CreateModContentLayoutMigration()
			.MigrateAsync(Path.Combine(_root, "does-not-exist"));

		Assert.Equal(0, result.Renamed);
		Assert.True(result.IsClean);
	}

	[Fact]
	public async Task MigrateAsync_IsIdempotent()
	{
		CreateMod("Once", ModNodePayloadLayout.LegacyVpkPatchDirectoryName);
		var migration = CoreServices.CreateModContentLayoutMigration();

		var first = await migration.MigrateAsync(_root);
		var second = await migration.MigrateAsync(_root);

		Assert.Equal(1, first.Renamed);
		Assert.Equal(0, second.Renamed);
		Assert.True(second.IsClean);
	}

	[Fact]
	public async Task MigrateAsync_LeavesLegacyModsWithoutLayerUntouched()
	{
		// 纯 legacy Mod：根上直接放 patch，没有基底目录。改名不该给它造一个。
		var node = Path.Combine(_root, "LegacyMod");
		Directory.CreateDirectory(node);
		File.WriteAllText(Path.Combine(node, PatchName), "toc");

		var result = await CoreServices.CreateModContentLayoutMigration().MigrateAsync(_root);

		Assert.Equal(0, result.Renamed);
		Assert.False(Directory.Exists(Path.Combine(node, ModNodePayloadLayout.VpkPatchDirectoryName)));
		Assert.True(File.Exists(Path.Combine(node, PatchName)));
	}
}
