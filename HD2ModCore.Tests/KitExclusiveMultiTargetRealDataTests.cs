using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModCore.Infrastructure.KitInstance;
using HD2ModCore.Infrastructure.Sectional;

namespace HD2ModCore.Tests;

/// <summary>
/// 专属多选（P5）在<b>真实产物</b>上跑一遍：雫（7 件 VPK 产物）同时专属到 DS-42（P4a 逐格）、CE-67（躯干合并）、神射手（左肩合并）。
/// <para>
/// 样本由环境变量给出（和 <c>SectionalCompilerRealDataTests</c> 同一套）：<c>HD2_TEST_SECTIONAL_MOD</c>（Mod 目录，测试会复制到临时目录再编译，
/// 真库只读）、<c>HD2_TEST_MANAGER_ROOT</c>（带 <c>data/indexes/asset-index.sqlite</c>）、<c>HD2_TEST_GAME_DATA</c>（游戏 data 目录）。
/// 缺任何一个就静默跳过。
/// </para>
/// </summary>
public sealed class KitExclusiveMultiTargetRealDataTests(Xunit.Abstractions.ITestOutputHelper output)
{
	private const uint Ce67 = 4081083564u;
	private const uint Sharpshooter = 3970674111u;

	private sealed class Env : IDisposable
	{
		public required string Root { get; init; }
		public required string GameData { get; init; }
		public required StoragePaths Paths { get; init; }
		public required GameDataAuthoritySnapshot Snapshot { get; init; }
		public required IReadOnlyList<SectionalSourceUnit> Source { get; init; }
		public required string SampleDirectory { get; init; }
		public string ModDirectory(string name) => Path.Combine(Root, name);

		public void Dispose()
		{
			try
			{
				foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
					File.SetAttributes(file, FileAttributes.Normal);
				Directory.Delete(Root, recursive: true);
			}
			catch (IOException) { }
			catch (UnauthorizedAccessException) { }
		}

		public string NewCopy(string name)
		{
			var destination = ModDirectory(name);
			foreach (var directory in Directory.EnumerateDirectories(SampleDirectory, "*", SearchOption.AllDirectories))
				Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(SampleDirectory, directory)));
			Directory.CreateDirectory(destination);
			foreach (var file in Directory.EnumerateFiles(SampleDirectory, "*", SearchOption.AllDirectories))
				File.Copy(file, Path.Combine(destination, Path.GetRelativePath(SampleDirectory, file)), overwrite: true);
			foreach (var file in Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories))
				File.SetAttributes(file, FileAttributes.Normal);
			return destination;
		}

		public static async Task<Env?> TryCreateAsync()
		{
			var sample = Environment.GetEnvironmentVariable("HD2_TEST_SECTIONAL_MOD");
			var managerRoot = Environment.GetEnvironmentVariable("HD2_TEST_MANAGER_ROOT");
			var gameData = Environment.GetEnvironmentVariable("HD2_TEST_GAME_DATA");
			if (string.IsNullOrWhiteSpace(sample) || !Directory.Exists(sample)) return null;
			if (string.IsNullOrWhiteSpace(managerRoot) || string.IsNullOrWhiteSpace(gameData) || !Directory.Exists(gameData)) return null;
			if (!File.Exists(Path.Combine(managerRoot, "data", "indexes", "asset-index.sqlite"))) return null;
			var source = await SectionalCompiler.ReadSourceUnitsAsync(sample, default);
			if (source is null || source.Count != 7) return null;   // 只认「雫」这种 7 件 VPK 产物
			var paths = new StoragePaths(managerRoot);
			var snapshot = await ArmorAuthorityIndexReader.TryReadAsync(paths);
			if (snapshot is null) return null;
			var root = Path.Combine(Path.GetTempPath(), "hd2-multi-real", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(root);
			return new Env { Root = root, GameData = gameData, Paths = paths, Snapshot = snapshot, Source = source, SampleDirectory = sample };
		}
	}

	private static uint Ds42(GameDataAuthoritySnapshot snapshot)
		=> snapshot.Kits.First(kit => kit.Kind == GameDataEquipmentKitKind.Armor
			&& kit.DisplayName.StartsWith("DS-42", StringComparison.Ordinal)).ItemId;

	private static string Archive(GameDataAuthoritySnapshot snapshot, uint item)
		=> snapshot.Kits.First(kit => kit.ItemId == item && kit.Kind == GameDataEquipmentKitKind.Armor).ArchiveId;

	private static HashSet<ulong> WrittenUnits(string modDirectory)
	{
		var directory = ModNodePayloadLayout.GetOverwriteDirectory(modDirectory);
		var patch = Directory.EnumerateFiles(directory, "*.patch_0").OrderBy(path => path, StringComparer.OrdinalIgnoreCase).First();
		return new HD2ModAdaptation.PatchReconstruction.PatchTocScanner()
			.ScanEntries(File.ReadAllBytes(patch), Path.GetFileName(patch))
			.Where(entry => entry.AssetKey.TypeId == 0xe0a48d0be9a7453f)
			.Select(entry => entry.AssetKey.FileId)
			.ToHashSet();
	}

	[Fact]
	public async Task ThreeTargets_CompileIntoOneOverwrite_WithOnlyTheirCarriers_AndThreeRuleSets()
	{
		using var env = await Env.TryCreateAsync();
		if (env is null) return;
		var ds42 = Ds42(env.Snapshot);
		var ids = new[] { ds42, Ce67, Sharpshooter };
		var index = SectionalExclusivePlanner.BuildIndex(env.Snapshot.Kits);
		var plans = ids.Select(id => SectionalExclusivePlanner.BuildWithMerge(index, env.Source, id, headMerge: false)).ToArray();
		Assert.All(plans, plan => Assert.True(plan.Succeeded, plan.FailureText));
		var expected = SectionalExclusiveCombiner.Combine(plans);
		Assert.Empty(expected.Conflicts);

		var mod = env.NewCopy("multi");
		var result = await SectionalCompiler.CompileAsync(
			mod, new VpkHostContext(env.GameData, env.Paths), Archive(env.Snapshot, ids[0]), null, headMergeIntoBody: false, [],
			exclusiveItemIds: ids);

		Assert.True(result.Ok, result.Error);
		Assert.Empty(result.SkippedTargets);
		Assert.Equal(0, result.HiddenUnits);
		Assert.Equal(3, result.ExclusiveTargets.Count);
		Assert.Equal(expected.CarrierCount, result.CarrierCount);
		Assert.Equal(expected.Merges.Count, result.MergedModelCount);

		// Overwrite 里只有这三组的承载件：无来源 unit、无披风。
		var written = WrittenUnits(mod);
		var carriers = expected.Replacements.Select(item => item.Target.Key.FileId).ToHashSet();
		Assert.Equal(carriers.Order(), written.Order());
		var sourceIds = env.Source.Select(unit => Convert.ToUInt64(unit.FileId, 16)).ToHashSet();
		Assert.Empty(written.Intersect(sourceIds));
		var capes = env.Snapshot.Kits.Where(kit => kit.Kind == GameDataEquipmentKitKind.Cape).SelectMany(kit => kit.Pieces).Select(piece => piece.UnitAssetKey.FileId);
		Assert.Empty(written.Intersect(capes));

		// 规则：每个目标各自一组（每条记录 1 个主规则起，规则名 vpk-<item>-<节点前 8 位>），合成同一个节点记录。
		var node = new ModNodeId(Guid.NewGuid());
		var record = KitExclusiveWorkspace.BuildRecord(node, mod, result.ExclusiveTargets);
		Assert.NotNull(record);
		Assert.Equal(ids.Order(), record!.AllTargets.Select(item => item.TargetItemId).Order());
		var rules = record.ToRules().ToArray();
		Assert.True(rules.Length >= 3);
		Assert.All(ids, id => Assert.Contains(rules, rule => rule.Name.StartsWith(KitExclusiveWorkspace.RuleNameFor(node, id), StringComparison.Ordinal)));
		Assert.All(rules, rule => Assert.True(KitInstanceRulesProtocol.TryValidate(rule, out _)));
		Assert.Null(KitExclusiveWorkspace.CheckRuleLimits(node, mod, result.ExclusiveTargets, KitInstanceWorkspace.Empty));
		output.WriteLine($"3 targets: carriers={result.CarrierCount} merged={result.MergedModelCount} rules={rules.Length} units written={written.Count}");
	}

	[Fact]
	public async Task SingleTarget_ListOrLegacyParameter_ProducesTheSameOverwrite()
	{
		using var env = await Env.TryCreateAsync();
		if (env is null) return;
		var legacyMod = env.NewCopy("legacy");
		var listMod = env.NewCopy("list");
		var context = new VpkHostContext(env.GameData, env.Paths);
		var archive = Archive(env.Snapshot, Ce67);

		var legacy = await SectionalCompiler.CompileAsync(legacyMod, context, archive, null, false, [], exclusiveItemId: Ce67);
		var list = await SectionalCompiler.CompileAsync(listMod, context, archive, null, false, [], exclusiveItemIds: [Ce67]);

		Assert.True(legacy.Ok, legacy.Error);
		Assert.True(list.Ok, list.Error);
		Assert.Equal(legacy.ReplacedUnits, list.ReplacedUnits);
		Assert.Equal(legacy.Exclusive!.All, list.Exclusive!.All);
		Assert.Equal(legacy.Exclusive.ExtraAll, list.Exclusive.ExtraAll);
		Assert.Equal(legacy.Exclusive.Hide, list.Exclusive.Hide);
		Assert.Single(list.ExclusiveTargets);
		var patchOf = (string mod) => Directory.EnumerateFiles(ModNodePayloadLayout.GetOverwriteDirectory(mod), "*.patch_0").Single();
		Assert.Equal(File.ReadAllBytes(patchOf(legacyMod)), File.ReadAllBytes(patchOf(listMod)));
		// 单目标写出的记录没有目标列表（与 P4b 完全一致）。
		Assert.Null(KitExclusiveWorkspace.BuildRecord(new ModNodeId(Guid.NewGuid()), listMod, list.ExclusiveTargets)!.Targets);
	}

	[Fact]
	public async Task FailedTarget_AsksTheHandler_RollbackWritesNothing_SkipDropsOnlyThatTarget_AutoSkipDoesNotAsk()
	{
		using var env = await Env.TryCreateAsync();
		if (env is null) return;
		var catalog = await KitExclusiveService.LoadCatalogAsync(env.SampleDirectory, env.Paths, headMerge: false);
		var impossible = catalog.Options.FirstOrDefault(option => !option.Available);
		if (impossible is null) return;
		var context = new VpkHostContext(env.GameData, env.Paths);
		var archive = Archive(env.Snapshot, Ce67);
		uint[] ids = [Ce67, impossible.ItemId];

		// 回滚：处理器返回 false → 整体失败，Overwrite 根本没有生成。
		var rollbackMod = env.NewCopy("rollback");
		IReadOnlyList<SectionalTargetFailure>? asked = null;
		var rolled = await SectionalCompiler.CompileAsync(
			rollbackMod, context, archive, null, false, [], exclusiveItemIds: ids,
			onTargetFailures: (failures, succeeded) => { asked = failures; Assert.Equal(1, succeeded); return ValueTask.FromResult(false); });
		Assert.False(rolled.Ok);
		Assert.NotNull(asked);
		Assert.Equal(impossible.ItemId, Assert.Single(asked!).ItemId);
		// 回滚 = 什么都没动：Overwrite 的文件清单和字节与样本原样一致（样本里可能本来就有上一次编译留下的）。
		static string[] Snapshot(string mod)
		{
			var directory = ModNodePayloadLayout.GetOverwriteDirectory(mod);
			return Directory.Exists(directory)
				? [.. Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order()
					.Select(file => Path.GetRelativePath(directory, file) + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))))]
				: [];
		}

		Assert.Equal(Snapshot(env.SampleDirectory), Snapshot(rollbackMod));

		// 没有处理器也没有自动跳过：有失败就整体失败。
		var strict = await SectionalCompiler.CompileAsync(
			env.NewCopy("strict"), context, archive, null, false, [], exclusiveItemIds: ids);
		Assert.False(strict.Ok);
		Assert.Contains(impossible.ItemId.ToString(), strict.Error, StringComparison.Ordinal);

		// 继续：只跳过失败的那个，别的照常写。
		var skipMod = env.NewCopy("skip");
		var skipped = await SectionalCompiler.CompileAsync(
			skipMod, context, archive, null, false, [], exclusiveItemIds: ids,
			onTargetFailures: (_, _) => ValueTask.FromResult(true));
		Assert.True(skipped.Ok, skipped.Error);
		Assert.Equal(impossible.ItemId, Assert.Single(skipped.SkippedTargets).ItemId);
		Assert.Equal([Ce67], skipped.ExclusiveTargets.Select(seed => seed.ItemId));

		// 继续并记住：自动跳过，处理器一次都不会被问。
		var autoMod = env.NewCopy("auto");
		var asks = 0;
		var auto = await SectionalCompiler.CompileAsync(
			autoMod, context, archive, null, false, [], exclusiveItemIds: ids, autoSkipFailedTargets: true,
			onTargetFailures: (_, _) => { asks++; return ValueTask.FromResult(false); });
		Assert.True(auto.Ok, auto.Error);
		Assert.Equal(0, asks);
		Assert.Equal(WrittenUnits(skipMod).Order(), WrittenUnits(autoMod).Order());
	}

	[Fact]
	public async Task Preview_ListsEachTargetThenTotals_AndBlocksWhenATargetCannot()
	{
		using var env = await Env.TryCreateAsync();
		if (env is null) return;
		var ids = new[] { Ds42(env.Snapshot), Ce67, Sharpshooter };

		var ok = await KitExclusiveService.PreviewAsync(env.SampleDirectory, env.Paths, ids, false, new ModNodeId(Guid.NewGuid()), null);

		Assert.True(ok.Succeeded, string.Join("\n", ok.Lines));
		var text = string.Join("\n", ok.Lines);
		Assert.Contains("目标 1/3", text, StringComparison.Ordinal);
		Assert.Contains("目标 3/3", text, StringComparison.Ordinal);
		Assert.Contains("隐藏规则", text, StringComparison.Ordinal);
		Assert.Contains("承载件", text, StringComparison.Ordinal);
		Assert.Contains("合并模型", text, StringComparison.Ordinal);

		var catalog = await KitExclusiveService.LoadCatalogAsync(env.SampleDirectory, env.Paths, headMerge: false);
		var impossible = catalog.Options.FirstOrDefault(option => !option.Available);
		if (impossible is null) return;
		var blocked = await KitExclusiveService.PreviewAsync(env.SampleDirectory, env.Paths, [Ce67, impossible.ItemId], false, null, null);
		Assert.False(blocked.Succeeded);
		Assert.Contains(impossible.ItemId.ToString(), string.Join("\n", blocked.Lines), StringComparison.Ordinal);
	}
}
