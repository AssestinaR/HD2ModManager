using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 锁住「Mod 文件夹里的设置文件是权威」。
/// <para>
/// 名字/备注/持久 id 都存在 `<mod>/.hd2modmanager-mod.json` 里，library.json 只是索引。
/// 这条规矩让"把 mod 文件夹搬到另一个库"后仍然认得出同一个 Mod；反过来若让 library.json
/// 当权威，搬过去就只剩文件夹名，改名与备注全丢。
/// </para>
/// </summary>
public sealed class ModSettingsAuthorityTests : IDisposable
{
	private const string PatchName = "9ba626afa44a3aa3.patch_0";
	private readonly string _root = Path.Combine(
		Path.GetTempPath(), "hd2-settings-authority-tests", Guid.NewGuid().ToString("N"));

	public ModSettingsAuthorityTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch { }
	}

	private static LibrarySnapshot EmptySnapshot() => new(
		1,
		DateTimeOffset.UnixEpoch,
		new Dictionary<ModNodeId, ModNode>(),
		Array.Empty<Profile>());

	private string NewMod(string folderName)
	{
		var directory = Path.Combine(_root, folderName);
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, PatchName), "toc");
		return directory;
	}

	[Fact]
	public async Task Inspect_PrefersTheSettingsFileNameIdAndNotes()
	{
		var directory = NewMod("folder-name");
		var persistentId = ModNodeId.New();
		ModSettingsFile.TryWrite(directory, new ModSettingsFile.Document(
			ModSettingsFile.CurrentSchema, null, persistentId.Value.ToString("N"), "我的名字", "我的备注"));

		var inspection = await new ModLibrarySynchronizer(new PatchFileNameParser())
			.InspectAsync(EmptySnapshot(), _root);

		var discovered = Assert.Single(inspection.Directories);
		Assert.Equal("我的名字", discovered.Name);
		Assert.Equal("我的备注", discovered.Notes);
		Assert.Equal(persistentId, discovered.SettingsNodeId);
	}

	[Fact]
	public async Task Apply_GivesTheNewNodeThePersistentIdFromTheFolder()
	{
		// 这就是"文件夹搬到另一个库"之后引用不悬空的机制：id 跟着文件夹走。
		var directory = NewMod("moved-mod");
		var persistentId = ModNodeId.New();
		ModSettingsFile.TryWrite(directory, new ModSettingsFile.Document(
			ModSettingsFile.CurrentSchema, null, persistentId.Value.ToString("N"), "搬过来的", null));

		var synchronizer = new ModLibrarySynchronizer(new PatchFileNameParser());
		var inspection = await synchronizer.InspectAsync(EmptySnapshot(), _root);
		var result = synchronizer.ApplyInspection(EmptySnapshot(), inspection);

		var node = Assert.Single(result.Snapshot.Nodes.Values);
		Assert.Equal(persistentId, node.Id);
		Assert.Equal("搬过来的", node.Metadata.Name);
	}

	[Fact]
	public async Task Apply_WithoutSettingsFile_FallsBackToTheFolderName()
	{
		NewMod("plain-folder");

		var synchronizer = new ModLibrarySynchronizer(new PatchFileNameParser());
		var inspection = await synchronizer.InspectAsync(EmptySnapshot(), _root);
		var result = synchronizer.ApplyInspection(EmptySnapshot(), inspection);

		Assert.Equal("plain-folder", Assert.Single(result.Snapshot.Nodes.Values).Metadata.Name);
	}

	[Fact]
	public async Task Apply_RenamesTheNodeWhenTheFolderSaysSomethingElse()
	{
		var directory = NewMod("old-name");
		var persistentId = ModNodeId.New();
		ModSettingsFile.TryWrite(directory, new ModSettingsFile.Document(
			ModSettingsFile.CurrentSchema, null, persistentId.Value.ToString("N"), "新名字", "备注"));

		var synchronizer = new ModLibrarySynchronizer(new PatchFileNameParser());
		var inspection = await synchronizer.InspectAsync(EmptySnapshot(), _root);
		var first = synchronizer.ApplyInspection(EmptySnapshot(), inspection);
		// 第二次扫描必须稳定：不能被文件夹名覆盖回 "old-name"。
		var result = synchronizer.ApplyInspection(first.Snapshot, inspection);

		var node = Assert.Single(result.Snapshot.Nodes.Values);
		Assert.Equal("新名字", node.Metadata.Name);
		Assert.Empty(result.ChangedNodeIds);
	}

	// 没有设置文件的 mod（今天全部都是）不该被"读成备注为空"，否则下一次扫描就把备注抹了。
	[Fact]
	public async Task Apply_WithoutSettingsFile_DoesNotWipeExistingNotes()
	{
		NewMod("no-settings");
		var nodeId = ModNodeId.New();
		var existing = new ModNode(nodeId, "no-settings",
			new ModNodeMetadata("库里的名字", "库里的备注", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
			Array.Empty<PatchGroupKey>(), Array.Empty<ModNodeId>(), "fingerprint");
		var snapshot = EmptySnapshot() with
		{
			Nodes = new Dictionary<ModNodeId, ModNode> { [nodeId] = existing },
		};

		var synchronizer = new ModLibrarySynchronizer(new PatchFileNameParser());
		var inspection = await synchronizer.InspectAsync(snapshot, _root);
		var result = synchronizer.ApplyInspection(snapshot, inspection);

		var node = Assert.Single(result.Snapshot.Nodes.Values);
		Assert.Equal("库里的名字", node.Metadata.Name);
		Assert.Equal("库里的备注", node.Metadata.Notes);
	}

	[Fact]
	public async Task Migration_WritesTheSettingsFileForAnExistingNode()
	{
		var directory = NewMod("legacy-mod");
		var nodeId = ModNodeId.New();
		var node = new ModNode(nodeId, "legacy-mod",
			new ModNodeMetadata("库里的名字", "库里的备注", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
			Array.Empty<PatchGroupKey>(), Array.Empty<ModNodeId>(), "fingerprint");
		var snapshot = EmptySnapshot() with
		{
			Nodes = new Dictionary<ModNodeId, ModNode> { [nodeId] = node },
		};

		var result = await CoreServices.CreateModContentLayoutMigration()
			.MigrateAsync(_root, snapshot);

		Assert.Equal(1, result.Adopted);
		var settings = ModSettingsFile.TryRead(directory);
		Assert.NotNull(settings);
		Assert.Equal("库里的名字", settings!.Name);
		Assert.Equal("库里的备注", settings.Notes);
		Assert.Equal(nodeId.Value.ToString("N"), settings.Id);
	}
}
