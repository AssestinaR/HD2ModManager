using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModCore.Infrastructure.KitInstance;
using HD2ModCore.Infrastructure.Sectional;

namespace HD2ModCore.Tests;

/// <summary>
/// 编译器在**真实产物**上跑一遍。
/// <para>
/// 合成测试只能证明"规则算对了"，证明不了"写出来的 patch 游戏会认"。这里用一份真 sectional
/// 产物（由 <c>vpk2patch</c> 转出来）跑完整流水线，断言几条硬性质：自身素体时 unit patch
/// 逐字节不变、换甲时披风一个键都不碰、隐藏壳真的只剩 1 个三角形。
/// </para>
/// <para>
/// 样本由环境变量给出（<c>HD2_TEST_SECTIONAL_MOD</c>；换甲那条另需 <c>HD2_TEST_MANAGER_ROOT</c>
/// 指向带 <c>data/indexes/asset-index.sqlite</c> 的管理器目录）。没有样本时静默跳过，
/// 符合本仓库"真数据测试缺样本就跳过"的约定。
/// </para>
/// </summary>
public sealed class SectionalCompilerRealDataTests
{
	private const string DefaultArmorArchive = "d923906f8b815987";

	[Fact]
	public async Task OwnModel_KeepsTheUnitPatchByteIdentical()
	{
		using var fixture = CompilerFixture.TryCreate();
		if (fixture is null) return;

		var result = await fixture.CompileAsync(targetArchiveId: null, headMerge: false);

		Assert.True(result.Ok, result.Error);
		Assert.Equal(SectionalCompileKind.OwnModel, result.Kind);

		var sectionalPatch = fixture.UnitPatchPath(Path.Combine(fixture.ModDirectory, "sectional"));
		var compiledPatch = fixture.UnitPatchPath(ModNodePayloadLayout.GetOverwriteDirectory(fixture.ModDirectory));
		Assert.NotNull(sectionalPatch);
		Assert.NotNull(compiledPatch);
		Assert.Equal(
			await File.ReadAllBytesAsync(sectionalPatch!),
			await File.ReadAllBytesAsync(compiledPatch!));

		// 表与组表必须落在生效层（否则"应用"就没有可用的偏移对照）。
		var overwrite = ModNodePayloadLayout.GetOverwriteDirectory(fixture.ModDirectory);
		Assert.True(File.Exists(Path.Combine(overwrite, WardrobeSectionMapFile.FileName)));
		Assert.False(File.Exists(Path.Combine(overwrite, VpkCustomModelFile.FileName)));
	}

	[Fact]
	public async Task OwnModelWithHeadMerge_WritesTheMergedRegistryOnly()
	{
		using var fixture = CompilerFixture.TryCreate();
		if (fixture is null) return;

		var registry = Path.Combine(fixture.ModDirectory, "sectional", VpkCustomModelFile.FileName);
		Assert.True(File.Exists(registry));
		var before = await File.ReadAllBytesAsync(registry);

		var result = await fixture.CompileAsync(targetArchiveId: null, headMerge: true);

		Assert.True(result.Ok, result.Error);
		Assert.Equal(SectionalHeadMergeOutcome.AppliedOwnModel, result.HeadMerge);

		// 权威留档一个字节都不许动。
		Assert.Equal(before, await File.ReadAllBytesAsync(registry));

		var merged = Path.Combine(ModNodePayloadLayout.GetOverwriteDirectory(fixture.ModDirectory), VpkCustomModelFile.FileName);
		Assert.True(File.Exists(merged));
		var text = await File.ReadAllTextAsync(merged);
		Assert.Contains("\"slot\": 2", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task OwnCustomModelIdIsIdentity_AForeignOneIsRejected()
	{
		using var fixture = CompilerFixture.TryCreate();
		if (fixture is null) return;

		var ownKey = VpkCustomModelFile.TryReadKey(fixture.ModDirectory);
		Assert.False(string.IsNullOrWhiteSpace(ownKey));

		var own = await fixture.CompileAsync(
			RuntimeArmorRegistryService.CustomModelArchiveId(ownKey!), headMerge: false);
		Assert.True(own.Ok, own.Error);
		Assert.Equal(SectionalCompileKind.OwnModel, own.Kind);

		// 选了别人的素体：必须明确拒绝，而不是静默按"自己"编译（那等于把目标忽略掉）。
		var foreign = await fixture.CompileAsync(
			RuntimeArmorRegistryService.CustomModelArchiveId("someone-else"), headMerge: false);
		Assert.False(foreign.Ok);
		Assert.Contains("不支持", foreign.Error!, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ArmorMode_NeverTouchesTheCapeAndHidesEverythingElse()
	{
		using var fixture = CompilerFixture.TryCreate();
		if (fixture is null) return;

		var managerRoot = Environment.GetEnvironmentVariable("HD2_TEST_MANAGER_ROOT");
		if (string.IsNullOrWhiteSpace(managerRoot)) return;
		var dbPath = Path.Combine(managerRoot, "data", "indexes", "asset-index.sqlite");
		if (!File.Exists(dbPath)) return;

		var archiveId = Environment.GetEnvironmentVariable("HD2_TEST_ARMOR_ARCHIVE") ?? DefaultArmorArchive;
		var result = await fixture.CompileAsync(archiveId, headMerge: false);

		Assert.True(result.Ok, result.Error);
		Assert.Equal(SectionalCompileKind.Armor, result.Kind);
		Assert.True(result.ReplacedUnits > 0);
		Assert.True(result.HiddenUnits > 0);
		// 隐藏壳必须只剩"每个 mesh 一个三角形"（minifyCullingMeshes: true 生效的证据）；
		// 真模型的同一件是几万个三角形。
		Assert.InRange(result.HiddenTriangles, 1, 16);

		// RE-2310 的形状是固定的（24 个 unit、躯干三层、双肩各两个身形）：
		// 替换 11 份（头 1 + 躯干 2 + 髋 2 + 双腿 1+1 + 双臂 2+2），披风 1 份豁免，其余 12 份隐藏。
		if (string.Equals(archiveId, DefaultArmorArchive, StringComparison.OrdinalIgnoreCase))
		{
			Assert.Equal(11, result.ReplacedUnits);
			Assert.Equal(12, result.HiddenUnits);
		}

		var compiled = fixture.ReadUnitKeys(ModNodePayloadLayout.GetOverwriteDirectory(fixture.ModDirectory));
		var source = fixture.ReadUnitKeys(Path.Combine(fixture.ModDirectory, "sectional"));

		// 来源 unit 一个都不该原样留在输出里（全部换了键）。
		Assert.Empty(compiled.Keys.Intersect(source.Keys));

		// 披风归档的 unit 一个都不该出现。
		var snapshot = await ArmorAuthorityIndexReader.TryReadAsync(new StoragePaths(managerRoot));
		Assert.NotNull(snapshot);
		var capeKeys = snapshot!.Kits
			.Where(kit => kit.Kind == GameDataEquipmentKitKind.Cape)
			.SelectMany(kit => kit.Pieces)
			.Select(piece => piece.UnitAssetKey.FileId)
			.ToHashSet();
		Assert.NotEmpty(capeKeys);
		Assert.Empty(compiled.Keys.Intersect(capeKeys));
	}

	/// <summary>
	/// 专属切换（P4a）真数据核对：写进 Overwrite 的原版 unit 必须全是目标记录独有的承载件——没有全局空壳、
	/// 没有被连带护甲的部件、没有披风。目标取 <c>HD2_TEST_EXCLUSIVE_ITEM</c>，缺省取下拉里第一条「可专属」的记录。
	/// </summary>
	[Fact]
	public async Task ExclusiveMode_WritesOnlyUnitsOwnedByTheTargetRecord()
	{
		using var fixture = CompilerFixture.TryCreate();
		if (fixture is null) return;
		var managerRoot = Environment.GetEnvironmentVariable("HD2_TEST_MANAGER_ROOT");
		if (string.IsNullOrWhiteSpace(managerRoot)) return;
		if (!File.Exists(Path.Combine(managerRoot, "data", "indexes", "asset-index.sqlite"))) return;

		var paths = new StoragePaths(managerRoot);
		var catalog = await KitExclusiveService.LoadCatalogAsync(fixture.ModDirectory, paths, headMerge: false);
		Assert.Null(catalog.Unavailable);
		var wanted = Environment.GetEnvironmentVariable("HD2_TEST_EXCLUSIVE_ITEM");
		var option = string.IsNullOrWhiteSpace(wanted)
			? catalog.Options.First(candidate => candidate.Available)
			: catalog.Options.Single(candidate => candidate.ItemId == uint.Parse(wanted));
		Assert.True(option.Available, option.Status);

		var context = new VpkHostContext(null, paths);
		var result = await SectionalCompiler.CompileAsync(
			fixture.ModDirectory, context, option.ArchiveId, null, headMergeIntoBody: false, [], exclusiveItemId: option.ItemId);

		Assert.True(result.Ok, result.Error);
		Assert.Equal(0, result.HiddenUnits);   // 不写任何全局空壳
		Assert.NotNull(result.Exclusive);
		Assert.True(result.Exclusive!.Hide.Count is > 0 and <= KitInstanceRulesProtocol.MaximumHide);

		var snapshot = await ArmorAuthorityIndexReader.TryReadAsync(paths);
		Assert.NotNull(snapshot);
		var owners = snapshot!.Kits
			.SelectMany(kit => kit.Pieces.Select(piece => (kit.ItemId, piece.UnitAssetKey.FileId)))
			.GroupBy(pair => pair.FileId)
			.ToDictionary(group => group.Key, group => group.Select(pair => pair.ItemId).Distinct().ToArray());
		var vanilla = owners.Keys.ToHashSet();

		var compiled = fixture.ReadUnitKeys(ModNodePayloadLayout.GetOverwriteDirectory(fixture.ModDirectory));
		var written = compiled.Keys.Where(vanilla.Contains).ToArray();
		Assert.NotEmpty(written);
		// 写出去的原版 unit 要么归目标记录独有，要么是目标同套头盔记录独有的头盔承载件。
		var setId = snapshot.Kits.Single(kit => kit.ItemId == option.ItemId).SetId;
		var allowed = snapshot.Kits
			.Where(kit => kit.ItemId == option.ItemId
				|| (kit.Kind == GameDataEquipmentKitKind.Helmet && kit.SetId == setId))
			.Select(kit => kit.ItemId)
			.ToHashSet();
		foreach (var file in written)
		{
			Assert.Single(owners[file]);
			Assert.Contains(owners[file][0], allowed);
		}

		var capeKeys = snapshot.Kits
			.Where(kit => kit.Kind == GameDataEquipmentKitKind.Cape)
			.SelectMany(kit => kit.Pieces).Select(piece => piece.UnitAssetKey.FileId).ToHashSet();
		Assert.Empty(compiled.Keys.Intersect(capeKeys));
		// 来源 unit 一个都不该原样留着（全部落到承载件的键上）。
		Assert.Empty(compiled.Keys.Intersect(fixture.ReadUnitKeys(Path.Combine(fixture.ModDirectory, "sectional")).Keys));
		// 隐藏集里没有承载件，也没有披风。
		var hide = result.Exclusive.Hide.Select(id => Convert.ToUInt64(id, 16)).ToHashSet();
		Assert.Empty(hide.Intersect(written));
		Assert.Empty(hide.Intersect(capeKeys));
	}

	/// <summary>FS-05 的三条记录一个身体格子都没有独有部件：专属切换必须整体失败并说明原因。</summary>
	[Fact]
	public async Task ExclusiveMode_Fs05FailsAsAWhole()
	{
		using var fixture = CompilerFixture.TryCreate();
		if (fixture is null) return;
		var managerRoot = Environment.GetEnvironmentVariable("HD2_TEST_MANAGER_ROOT");
		if (string.IsNullOrWhiteSpace(managerRoot)) return;
		if (!File.Exists(Path.Combine(managerRoot, "data", "indexes", "asset-index.sqlite"))) return;

		var paths = new StoragePaths(managerRoot);
		var catalog = await KitExclusiveService.LoadCatalogAsync(fixture.ModDirectory, paths, headMerge: false);
		foreach (var item in new[] { 124623533u, 3970674111u, 1490803405u })
		{
			var option = catalog.Options.SingleOrDefault(candidate => candidate.ItemId == item);
			if (option is null) return;   // 索引里没有这几条（别的游戏版本）：跳过
			Assert.False(option.Available, option.Label);
			Assert.StartsWith("不可：", option.Status, StringComparison.Ordinal);
		}

		var failed = await SectionalCompiler.CompileAsync(
			fixture.ModDirectory, new VpkHostContext(null, paths), "ccac0a57aca3ae92", null, headMergeIntoBody: false, [], exclusiveItemId: 3970674111);
		Assert.False(failed.Ok);
		Assert.Contains("不能专属切换", failed.Error!, StringComparison.Ordinal);
	}

	// ── 样本夹具 ────────────────────────────────────────────────────────────

	private sealed class CompilerFixture : IDisposable
	{
		private CompilerFixture(string root, string modDirectory, string pathsRoot)
		{
			Root = root;
			ModDirectory = modDirectory;
			PathsRoot = pathsRoot;
		}

		public string Root { get; }
		public string ModDirectory { get; }
		public string PathsRoot { get; }

		public static CompilerFixture? TryCreate()
		{
			var sample = Environment.GetEnvironmentVariable("HD2_TEST_SECTIONAL_MOD");
			if (string.IsNullOrWhiteSpace(sample) || !Directory.Exists(sample)) return null;

			var root = Path.Combine(Path.GetTempPath(), "hd2-compiler-tests", Guid.NewGuid().ToString("N"));
			var mod = Path.Combine(root, "mod");
			CopyTree(sample, mod);
			ClearReadOnly(mod);
			// 换甲那条要查索引库：优先用真管理器根目录，没有就退回临时目录（那条测试会自己跳过）。
			var pathsRoot = Environment.GetEnvironmentVariable("HD2_TEST_MANAGER_ROOT");
			return new CompilerFixture(root, mod, string.IsNullOrWhiteSpace(pathsRoot) ? root : pathsRoot);
		}

		public ValueTask<SectionalCompileResult> CompileAsync(string? targetArchiveId, bool headMerge)
		{
			var context = new VpkHostContext(null, new StoragePaths(PathsRoot));
			return SectionalCompiler.CompileAsync(ModDirectory, context, targetArchiveId, null, headMerge, []);
		}

		/// <summary>目录里的 unit patch（<c>&lt;archive&gt;.patch_0</c>）路径。</summary>
		public string? UnitPatchPath(string directory)
		{
			if (!Directory.Exists(directory)) return null;
			return Directory
				.EnumerateFiles(directory, "*.patch_0", SearchOption.TopDirectoryOnly)
				.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
				.FirstOrDefault();
		}

		/// <summary>读一个目录里 unit patch 的全部 unit 键（fileId → 是否 unit）。</summary>
		public IReadOnlyDictionary<ulong, string> ReadUnitKeys(string directory)
		{
			var path = UnitPatchPath(directory);
			if (path is null) return new Dictionary<ulong, string>();
			var bytes = File.ReadAllBytes(path);
			return new PatchTocScanner()
				.ScanEntries(bytes, Path.GetFileName(path))
				.Where(entry => entry.AssetKey.TypeId == 0xe0a48d0be9a7453f)
				.ToDictionary(entry => entry.AssetKey.FileId, entry => entry.AssetKey.FileId.ToString("x16"));
		}

		public void Dispose()
		{
			try
			{
				ClearReadOnly(Root);
				if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
			}
			catch (IOException) { }
			catch (UnauthorizedAccessException) { }
		}

		private static void CopyTree(string source, string destination)
		{
			Directory.CreateDirectory(destination);
			foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
				Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
			foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
				File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
		}

		private static void ClearReadOnly(string directory)
		{
			if (!Directory.Exists(directory)) return;
			foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
			{
				var attributes = File.GetAttributes(file);
				if ((attributes & FileAttributes.ReadOnly) != 0)
					File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
			}
		}
	}
}
