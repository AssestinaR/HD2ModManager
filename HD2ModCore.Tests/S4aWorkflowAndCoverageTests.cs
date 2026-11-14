using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModCore.Infrastructure.Sectional;

namespace HD2ModCore.Tests;

public sealed class LowPollutionPrepTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "hd2-s4a-prep-" + Guid.NewGuid().ToString("N"));
	private const string Unit = "0000000000000001";

	public LowPollutionPrepTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
	}

	private string ModDir(string sections = "{\"a\":1}")
	{
		var dir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
		var payload = Path.Combine(dir, "sectional");
		Directory.CreateDirectory(payload);
		File.WriteAllText(Path.Combine(payload, "sections.json"), sections);
		File.WriteAllText(Path.Combine(payload, "0000000000000001.patch_0"), "x");
		return dir;
	}

	private static string Fp(string dir, VpkWardrobeFile wardrobe)
		=> LowPollutionFreshness.Fingerprint(
			ModNodePayloadLayout.TryGetExistingVpkPatchDirectory(dir)!, wardrobe.State.Disabled, wardrobe.HeadMergeIntoBody);

	private static void WriteLayout(string dir, string? fingerprint, bool headMerge = false)
	{
		var overwrite = ModNodePayloadLayout.GetOverwriteDirectory(dir);
		Directory.CreateDirectory(overwrite);
		var fp = fingerprint is null ? string.Empty : $",\"fingerprint\":\"{fingerprint}\"";
		File.WriteAllText(Path.Combine(overwrite, "low-pollution-layout.json"),
			$"{{\"schema\":\"hd2mod.low-pollution-layout/1\",\"mode\":\"three-slot\",\"headMerge\":{(headMerge ? "true" : "false")},\"units\":[{{\"role\":\"body\",\"unit\":\"{Unit}\"}}]{fp}}}");
	}

	private static VpkWardrobeFile Wardrobe(bool headMerge = false, string? target = null)
		=> new(false, VpkWardrobeState.Empty, TargetArmorArchiveId: target, HeadMergeIntoBody: headMerge);

	[Fact]
	public void SwitchOn_NeverCompressed_NeedsRecompile_AndAFreshLayoutIsSkipped()
	{
		var dir = ModDir();
		var wardrobe = Wardrobe();
		Assert.Equal(LowPollutionPrepStatus.NeedsRecompile, LowPollutionFreshness.Assess("k", dir, true, wardrobe).Status);

		WriteLayout(dir, Fp(dir, wardrobe));
		Assert.Equal(LowPollutionPrepStatus.UpToDate, LowPollutionFreshness.Assess("k", dir, true, wardrobe).Status);
	}

	[Fact]
	public void ChangedSectionsHeadMergeOrToggles_InvalidateTheFingerprint()
	{
		var dir = ModDir();
		var wardrobe = Wardrobe();
		WriteLayout(dir, Fp(dir, wardrobe));

		// 来源 sections.json 变了（重新转换）。
		File.WriteAllText(Path.Combine(dir, "sectional", "sections.json"), "{\"a\":2}");
		Assert.Equal(LowPollutionPrepStatus.NeedsRecompile, LowPollutionFreshness.Assess("k", dir, true, wardrobe).Status);

		// 勾选改了（关掉一个 section）。
		var dir2 = ModDir();
		WriteLayout(dir2, Fp(dir2, wardrobe));
		var toggled = wardrobe with
		{
			State = new VpkWardrobeState(new Dictionary<string, IReadOnlyCollection<int>> { ["u"] = [1, 2] }),
		};
		Assert.Equal(LowPollutionPrepStatus.NeedsRecompile, LowPollutionFreshness.Assess("k", dir2, true, toggled).Status);

		// 头身合并变了。
		var dir3 = ModDir();
		WriteLayout(dir3, Fp(dir3, wardrobe));
		var merged = Wardrobe(headMerge: true);
		var item = LowPollutionFreshness.Assess("k", dir3, true, merged);
		Assert.Equal(LowPollutionPrepStatus.NeedsRecompile, item.Status);
		Assert.Contains("头身合并", item.Reason);
	}

	[Fact]
	public void LayoutWithoutFingerprint_OrUnreadable_IsRecompiled()
	{
		var dir = ModDir();
		WriteLayout(dir, null);
		Assert.Equal(LowPollutionPrepStatus.NeedsRecompile, LowPollutionFreshness.Assess("k", dir, true, Wardrobe()).Status);
		File.WriteAllText(Path.Combine(ModNodePayloadLayout.GetOverwriteDirectory(dir), "low-pollution-layout.json"), "{ 坏的");
		Assert.Equal(LowPollutionPrepStatus.NeedsRecompile, LowPollutionFreshness.Assess("k", dir, true, Wardrobe()).Status);
	}

	[Fact]
	public void SwitchOff_RestoresCompressedModels_AndSkipsPlainOnes()
	{
		var plain = ModDir();
		Assert.Equal(LowPollutionPrepStatus.UpToDate, LowPollutionFreshness.Assess("k", plain, false, Wardrobe()).Status);

		var compressed = ModDir();
		WriteLayout(compressed, Fp(compressed, Wardrobe()));
		var item = LowPollutionFreshness.Assess("k", compressed, false, Wardrobe());
		Assert.Equal(LowPollutionPrepStatus.NeedsRecompile, item.Status);
		Assert.Contains("关闭", item.Reason);
	}

	[Fact]
	public void UnsupportedMarker_WithTheSameFingerprint_IsNotRecompiledForever()
	{
		var dir = ModDir();
		var wardrobe = Wardrobe();
		var payload = ModNodePayloadLayout.TryGetExistingVpkPatchDirectory(dir)!;
		Directory.CreateDirectory(ModNodePayloadLayout.GetOverwriteDirectory(dir));
		LowPollutionFreshness.WriteUnsupportedMarker(dir, payload, wardrobe.State.Disabled, false, "有分体");
		Assert.Equal(LowPollutionPrepStatus.UpToDate, LowPollutionFreshness.Assess("k", dir, true, wardrobe).Status);
		// 来源变了 ⇒ 重新试一次。
		File.WriteAllText(Path.Combine(dir, "sectional", "sections.json"), "{\"a\":9}");
		Assert.Equal(LowPollutionPrepStatus.NeedsRecompile, LowPollutionFreshness.Assess("k", dir, true, wardrobe).Status);
		// 关开关：标记也算压缩痕迹，要恢复。
		Assert.Equal(LowPollutionPrepStatus.NeedsRecompile, LowPollutionFreshness.Assess("k", dir, false, wardrobe).Status);
	}

	[Fact]
	public void TargetThatIsAGameArmor_IsBlocked_NotSilentlyRewritten()
	{
		var dir = ModDir();
		var item = LowPollutionFreshness.Assess("k", dir, true, Wardrobe(target: "abcdef"));
		Assert.Equal(LowPollutionPrepStatus.Blocked, item.Status);
		var own = LowPollutionFreshness.Assess("k", dir, true, Wardrobe(target: RuntimeArmorRegistryService.CustomModelArchiveId("k")));
		Assert.Equal(LowPollutionPrepStatus.NeedsRecompile, own.Status);
	}

	[Fact]
	public async Task Recompiler_OnlyTouchesNeedsRecompile_IsolatesFailures_AndStopsOnCancel()
	{
		LowPollutionPrepItem Item(string key, LowPollutionPrepStatus status) => new(key, "d/" + key, key, status, "r");
		var items = new[]
		{
			Item("a", LowPollutionPrepStatus.NeedsRecompile), Item("b", LowPollutionPrepStatus.UpToDate),
			Item("c", LowPollutionPrepStatus.NeedsRecompile), Item("d", LowPollutionPrepStatus.Blocked),
			Item("e", LowPollutionPrepStatus.NeedsRecompile),
		};
		var compiled = new List<string>();
		var result = await LowPollutionRecompiler.RunAsync(items, (item, _) =>
		{
			compiled.Add(item.ModelKey);
			return Task.FromResult(item.ModelKey == "c" ? (false, "炸了") : (true, "ok"));
		}, null);
		Assert.Equal(["a", "c", "e"], compiled);
		Assert.Equal(["a", "e"], result.Recompiled.Select(item => item.ModelKey));
		Assert.Equal("c", Assert.Single(result.Failed).Item.ModelKey);
		Assert.Equal(["b"], result.UpToDate.Select(item => item.ModelKey));
		Assert.Equal(["c", "d"], result.SkippedModelKeys.Order());
		Assert.False(result.Canceled);

		using var cancellation = new CancellationTokenSource();
		var seen = new List<string>();
		var canceled = await LowPollutionRecompiler.RunAsync(items, (item, _) =>
		{
			seen.Add(item.ModelKey);
			cancellation.Cancel();
			return Task.FromResult((true, "ok"));
		}, null, cancellation.Token);
		Assert.True(canceled.Canceled);
		Assert.Equal(["a"], seen);
	}

	[Fact]
	public async Task Recompiler_NothingToDo_CompilesNothing()
	{
		var result = await LowPollutionRecompiler.RunAsync(
			[new("a", "d", "a", LowPollutionPrepStatus.UpToDate, "r")],
			(_, _) => throw new InvalidOperationException("不该编译"), null);
		Assert.Equal(0, result.RecompileCount);
		Assert.Empty(result.SkippedModelKeys);
	}
}

public sealed class ArmorMappingPatchCoverageTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "hd2-s4a-" + Guid.NewGuid().ToString("N"));

	public ArmorMappingPatchCoverageTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
	}

	private static ArmorMappingCoverageTarget T(uint id, string name = "") => new(id, string.IsNullOrEmpty(name) ? "护甲" + id : name);

	[Fact]
	public void Classify_NoTargetsOrNoMappings_IsNone()
	{
		Assert.Equal(ArmorMappingCoverageKind.None, ArmorMappingPatchCoverage.Classify([], new HashSet<uint> { 1 }).Kind);
		Assert.Equal(ArmorMappingCoverageKind.None, ArmorMappingPatchCoverage.Classify([T(1)], new HashSet<uint>()).Kind);
		Assert.Equal(ArmorMappingCoverageKind.None, ArmorMappingPatchCoverage.Classify([T(1)], new HashSet<uint> { 2 }).Kind);
	}

	[Fact]
	public void Classify_AllMapped_IsCovered_SomeMapped_IsPartial()
	{
		var covered = ArmorMappingPatchCoverage.Classify([T(1), T(2)], new HashSet<uint> { 1, 2, 9 });
		Assert.Equal(ArmorMappingCoverageKind.Covered, covered.Kind);
		Assert.Empty(covered.Uncovered);

		var partial = ArmorMappingPatchCoverage.Classify([T(1), T(2)], new HashSet<uint> { 1 });
		Assert.Equal(ArmorMappingCoverageKind.Partial, partial.Kind);
		Assert.Equal([1u], partial.Covered.Select(target => target.ItemId));
		Assert.Equal([2u], partial.Uncovered.Select(target => target.ItemId));
	}

	[Fact]
	public void DeclaredTargets_ExclusiveUsesItsList_NonExclusiveUsesTheArchive_OwnBodyUsesNothing()
	{
		var exclusive = new VpkWardrobeFile(false, VpkWardrobeState.Empty, Exclusive: true,
			Targets: [new VpkWardrobeTarget(7, "A"), new VpkWardrobeTarget(8, null)]);
		Assert.Equal([7u, 8u], ArmorMappingPatchCoverage.DeclaredTargets(exclusive, _ => throw new InvalidOperationException()).Select(t => t.ItemId));

		var plain = new VpkWardrobeFile(false, VpkWardrobeState.Empty, TargetArmorArchiveId: "abc");
		var byArchive = ArmorMappingPatchCoverage.DeclaredTargets(plain, archive => archive == "abc" ? [T(5), T(6)] : []);
		Assert.Equal([5u, 6u], byArchive.Select(t => t.ItemId));

		var ownBody = new VpkWardrobeFile(false, VpkWardrobeState.Empty,
			TargetArmorArchiveId: RuntimeArmorRegistryService.CustomModelArchiveIdPrefix + "x");
		Assert.Empty(ArmorMappingPatchCoverage.DeclaredTargets(ownBody, _ => throw new InvalidOperationException()));
		Assert.Empty(ArmorMappingPatchCoverage.DeclaredTargets(VpkWardrobeFile.Default, _ => throw new InvalidOperationException()));
	}

	private string WriteProtocol(bool enabled, params uint[] targets)
	{
		var registry = Path.Combine(_root, "registry");
		Directory.CreateDirectory(registry);
		var document = new RuntimeArmorRegistryDocument(
			1, enabled, targets.Select(target => new RuntimeArmorRegistryMapping(target, target + 1000)).ToArray());
		File.WriteAllText(Path.Combine(registry, RuntimeArmorRegistryService.ProtocolFileName),
			RuntimeArmorRegistryService.SerializeProtocol(document));
		return registry;
	}

	[Fact]
	public async Task AppliedMappedTargets_ReadsOnlyAnEnabledProtocol()
	{
		Assert.Empty(await ArmorMappingPatchCoverage.ReadAppliedMappedTargetsAsync(Path.Combine(_root, "none")));
		Assert.Equal([3u, 4u], (await ArmorMappingPatchCoverage.ReadAppliedMappedTargetsAsync(WriteProtocol(true, 3, 4))).Order());
		Assert.Empty(await ArmorMappingPatchCoverage.ReadAppliedMappedTargetsAsync(WriteProtocol(false, 3, 4)));
		File.WriteAllText(Path.Combine(_root, "registry", RuntimeArmorRegistryService.ProtocolFileName), "垃圾");
		Assert.Empty(await ArmorMappingPatchCoverage.ReadAppliedMappedTargetsAsync(Path.Combine(_root, "registry")));
	}

	private string WriteMod(string name, string wardrobeJson)
	{
		var directory = Path.Combine(_root, name);
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, VpkModWardrobeService.StateFileName), wardrobeJson);
		return directory;
	}

	[Fact]
	public async Task AnalyzeNode_ReadsWardrobeJson_AndLooksUpTheIndexOnlyWhenItNeedsTo()
	{
		var mapped = new HashSet<uint> { 5, 6 };
		IReadOnlyDictionary<string, IReadOnlyList<ArmorMappingCoverageTarget>> Index() =>
			new Dictionary<string, IReadOnlyList<ArmorMappingCoverageTarget>>(StringComparer.OrdinalIgnoreCase)
			{
				["abc"] = [T(5, "TG-8"), T(6, "TG-8 NPC")],
				["def"] = [T(5, "TG-8"), T(7, "别的")],
			};

		var full = WriteMod("full", """{"componentTreeEnabled":false,"targetArmorArchiveId":"ABC","targetArmorName":"TG-8"}""");
		Assert.Equal(ArmorMappingCoverageKind.Covered, (await ArmorMappingPatchCoverage.AnalyzeNodeAsync(full, mapped, Index)).Kind);

		var partial = WriteMod("partial", """{"componentTreeEnabled":false,"targetArmorArchiveId":"def"}""");
		Assert.Equal(ArmorMappingCoverageKind.Partial, (await ArmorMappingPatchCoverage.AnalyzeNodeAsync(partial, mapped, Index)).Kind);

		var ownBody = WriteMod("body", """{"componentTreeEnabled":false,"targetArmorArchiveId":"custom-model:k"}""");
		Assert.Equal(ArmorMappingCoverageKind.None, (await ArmorMappingPatchCoverage.AnalyzeNodeAsync(
			ownBody, mapped, () => throw new InvalidOperationException("不该查索引"))).Kind);

		var none = Path.Combine(_root, "nowardrobe");
		Directory.CreateDirectory(none);
		Assert.Equal(ArmorMappingCoverageKind.None, (await ArmorMappingPatchCoverage.AnalyzeNodeAsync(
			none, mapped, () => throw new InvalidOperationException("不该查索引"))).Kind);
		Assert.Equal(ArmorMappingCoverageKind.None, (await ArmorMappingPatchCoverage.AnalyzeNodeAsync(
			full, new HashSet<uint>(), () => throw new InvalidOperationException("不该查索引"))).Kind);

		var broken = WriteMod("broken", "{ 不是 json");
		Assert.Equal(ArmorMappingCoverageKind.None, (await ArmorMappingPatchCoverage.AnalyzeNodeAsync(
			broken, mapped, Index)).Kind);
	}

	[Fact]
	public async Task AnalyzeNode_ExclusiveMultiTarget_PartialWhenOnlySomeAreMapped()
	{
		var directory = WriteMod("excl", """{"componentTreeEnabled":false,"exclusive":true,"targetItemId":5,"targets":[{"itemId":5,"name":"A"},{"itemId":9,"name":"B"}]}""");
		var coverage = await ArmorMappingPatchCoverage.AnalyzeNodeAsync(directory, new HashSet<uint> { 5 }, () => throw new InvalidOperationException());
		Assert.Equal(ArmorMappingCoverageKind.Partial, coverage.Kind);
		Assert.Equal("A", coverage.Covered.Single().Name);
		Assert.Equal("B", coverage.Uncovered.Single().Name);
	}
}
