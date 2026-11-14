using System.Buffers.Binary;
using System.Text;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModCore.Infrastructure.KitInstance;
using AdaptationLuaAssetKeys = HD2ModAdaptation.PatchReconstruction.Lua.LuaAssetKeys;
using AdaptationLua51Bytecode = HD2ModAdaptation.PatchReconstruction.Lua.Lua51Bytecode;
using AdaptationLuaResourceFormat = HD2ModAdaptation.PatchReconstruction.Lua.LuaResourceFormat;
using AdaptationPatchEntryPayloadReader = HD2ModAdaptation.PatchReconstruction.PatchEntryPayloadReader;
using AdaptationPatchTocScanner = HD2ModAdaptation.PatchReconstruction.PatchTocScanner;

namespace HD2ModCore.Tests;

// 「按实例隐藏」P1：规则协议、workspace 记录、编译器、规则文件、Hider 的部署（三种落地方式）。
// ⚠️ 部署用例改的是 LuaDeploymentService 上的静态设置 ⇒ 与同集合的其它用例串行。
[Collection(LuaDeploymentStateCollection.Name)]
public sealed class KitInstanceTests
{
	private const string Marker = "82595b5d94c57343";
	private const string HideA = "74b2e79c1c5af767";
	private const string HideB = "e15680e4c4a89420";
	private const string HiderChunk = "@mods/hd2modmanager/kit_instance_hider";

	private static KitInstanceRule Rule(string name = "aemeath_democracy_officer", double radius = 0.3)
		=> new(name, radius, [Marker], [], [HideA, HideB]);

	// ---- 协议 ---------------------------------------------------------------------------

	[Fact]
	public void Protocol_RoundTripsRulesIncludingNoneAndMultipleAll()
	{
		var document = new KitInstanceRulesDocument(7,
		[
			Rule(),
			new KitInstanceRule("multi", 0.5, [Marker, HideA], ["cccccccccccccccc"], [HideB]),
		]);

		var text = KitInstanceRulesProtocol.Serialize(document);

		Assert.StartsWith("schema=1\nrevision=7\n", text);
		Assert.Contains("rule=name:aemeath_democracy_officer;radius:0.3;all:82595b5d94c57343;none:;hide:74b2e79c1c5af767,e15680e4c4a89420\n", text);
		Assert.True(KitInstanceRulesProtocol.TryParse(text, out var parsed, out var error), error);
		Assert.Equal(7, parsed!.Revision);
		Assert.Equal(2, parsed.Rules.Count);
		Assert.Equal(document.Rules[0].Hide, parsed.Rules[0].Hide);
		Assert.Equal(0.3, parsed.Rules[0].Radius);
		Assert.Equal([Marker, HideA], parsed.Rules[1].All);
		Assert.Equal(["cccccccccccccccc"], parsed.Rules[1].None);
	}

	[Fact]
	public void Protocol_EmptyDocumentIsJustSchemaAndRevision()
	{
		var text = KitInstanceRulesProtocol.Serialize(KitInstanceRulesDocument.Empty(3));
		Assert.True(KitInstanceRulesProtocol.TryParse(text, out var parsed, out _));
		Assert.Equal(3, parsed!.Revision);
		Assert.Empty(parsed.Rules);
		Assert.DoesNotContain("rule=", text);
	}

	[Fact]
	public void Protocol_ParsesCommentsCrlfAndIgnoresUnknownKeysAndFields()
	{
		const string text = "# note\r\n\r\nschema=1\r\nfuture=x\r\nrevision=4\r\n"
			+ "rule=name:a;radius:1;all:82595b5d94c57343;hide:74b2e79c1c5af767;extra:zzz\r\n";
		Assert.True(KitInstanceRulesProtocol.TryParse(text, out var parsed, out var error), error);
		Assert.Equal(4, parsed!.Revision);
		Assert.Equal(1.0, Assert.Single(parsed.Rules).Radius);
	}

	[Theory]
	[InlineData("revision=1\n", "schema missing")]
	[InlineData("schema=1\n", "revision missing")]
	[InlineData("schema=2\nrevision=1\n", "schema 2")]
	[InlineData("schema=1\nrevision=-1\n", "negative revision")]
	[InlineData("schema=1\nrevision=abc\n", "revision text")]
	[InlineData("schema=1\nrevision=1\nrevision=2\n", "duplicate revision")]
	[InlineData("schema=1\nrevision=1\nnot a line\n", "line without =")]
	[InlineData("schema=1\nrevision=1\nrule=name:Bad;radius:0.3;all:82595b5d94c57343;hide:74b2e79c1c5af767\n", "uppercase name")]
	[InlineData("schema=1\nrevision=1\nrule=name:a;radius:0;all:82595b5d94c57343;hide:74b2e79c1c5af767\n", "radius 0")]
	[InlineData("schema=1\nrevision=1\nrule=name:a;radius:1.01;all:82595b5d94c57343;hide:74b2e79c1c5af767\n", "radius over 1")]
	[InlineData("schema=1\nrevision=1\nrule=name:a;radius:0x1;all:82595b5d94c57343;hide:74b2e79c1c5af767\n", "radius hex")]
	[InlineData("schema=1\nrevision=1\nrule=name:a;radius:0.3;all:;hide:74b2e79c1c5af767\n", "all empty")]
	[InlineData("schema=1\nrevision=1\nrule=name:a;radius:0.3;all:82595b5d94c57343;hide:\n", "hide empty")]
	[InlineData("schema=1\nrevision=1\nrule=name:a;radius:0.3;all:82595B5D94C57343;hide:74b2e79c1c5af767\n", "uppercase id")]
	[InlineData("schema=1\nrevision=1\nrule=name:a;radius:0.3;all:1234;hide:74b2e79c1c5af767\n", "short id")]
	[InlineData("schema=1\nrevision=1\nrule=name:a;radius:0.3;all:82595b5d94c57343;hide:74b2e79c1c5af767,,e15680e4c4a89420\n", "empty list item")]
	[InlineData("schema=1\nrevision=1\nrule=name:a;radius:0.3;all:82595b5d94c57343\n", "missing hide")]
	[InlineData("schema=1\nrevision=1\nrule=name:a;radius:0.3;radius:0.4;all:82595b5d94c57343;hide:74b2e79c1c5af767\n", "duplicate field")]
	public void Protocol_RejectsInvalidDocuments(string text, string why)
	{
		Assert.False(KitInstanceRulesProtocol.TryParse(text, out var document, out var error), why);
		Assert.Null(document);
		Assert.NotEmpty(error);
	}

	[Fact]
	public void Protocol_RejectsDuplicateNamesAndLimitViolations()
	{
		var line = "rule=name:a;radius:0.3;all:82595b5d94c57343;hide:74b2e79c1c5af767\n";
		Assert.False(KitInstanceRulesProtocol.TryParse("schema=1\nrevision=1\n" + line + line, out _, out _));

		string Ids(int count, int offset) => string.Join(',', Enumerable.Range(1, count).Select(i => (offset + i).ToString("x16")));
		string One(int all, int none, int hide) =>
			$"schema=1\nrevision=1\nrule=name:x;radius:0.5;all:{Ids(all, 0)};none:{Ids(none, 100)};hide:{Ids(hide, 200)}\n";
		Assert.True(KitInstanceRulesProtocol.TryParse(One(8, 8, 256), out _, out var error), error);
		Assert.False(KitInstanceRulesProtocol.TryParse(One(9, 0, 1), out _, out _));
		Assert.False(KitInstanceRulesProtocol.TryParse(One(1, 9, 1), out _, out _));
		Assert.False(KitInstanceRulesProtocol.TryParse(One(1, 0, 257), out _, out _));

		var sixtyFive = "schema=1\nrevision=1\n" + string.Concat(Enumerable.Range(1, 1025)
			.Select(i => $"rule=name:r{i};radius:0.3;all:{Marker};hide:{HideA}\n"));
		Assert.False(KitInstanceRulesProtocol.TryParse(sixtyFive, out _, out _));
		Assert.False(KitInstanceRulesProtocol.TryParse("schema=1\nrevision=1\n# " + new string('x', 2 * 1024 * 1024) + "\n", out _, out _));
	}

	[Fact]
	public void Protocol_SerializeRefusesInvalidRules()
	{
		Assert.Throws<InvalidDataException>(() => KitInstanceRulesProtocol.Serialize(
			new KitInstanceRulesDocument(1, [new KitInstanceRule("BAD", 0.3, [Marker], [], [HideA])])));
		Assert.Throws<InvalidDataException>(() => KitInstanceRulesProtocol.Serialize(
			new KitInstanceRulesDocument(1, [new KitInstanceRule("a", 1.5, [Marker], [], [HideA])])));
		Assert.Throws<InvalidDataException>(() => KitInstanceRulesProtocol.Serialize(
			new KitInstanceRulesDocument(1, [new KitInstanceRule("a", 0.3, [], [], [HideA])])));
		Assert.Throws<InvalidDataException>(() => KitInstanceRulesProtocol.Serialize(
			new KitInstanceRulesDocument(1, [Rule(), Rule()])));
		Assert.Throws<InvalidDataException>(() => KitInstanceRulesProtocol.Serialize(
			new KitInstanceRulesDocument(-1, [])));
	}

	// ---- workspace ----------------------------------------------------------------------

	[Fact]
	public async Task Workspace_RoundTripsAtomicallyAndMissingFileIsEmpty()
	{
		var mods = CreateScratch();
		try
		{
			Assert.Empty((await KitInstanceWorkspaceStore.ReadAsync(mods)).Records);

			var node = ModNodeId.New();
			var workspace = KitInstanceWorkspace.Empty.With(node, new KitInstanceWorkspaceRecord(
				124623533, "aemeath_democracy_officer", 0.3, [Marker], [], [HideA, HideB], "abc123"));
			await KitInstanceWorkspaceStore.WriteAsync(mods, workspace);

			var path = KitInstanceWorkspaceStore.GetPath(mods);
			Assert.EndsWith(Path.Combine(".hd2modmanager", "kit-instance.workspace.json"), path);
			Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
			var read = await KitInstanceWorkspaceStore.ReadAsync(mods);
			var record = Assert.Single(read.Records).Value;
			Assert.Equal(node, Assert.Single(read.Records).Key);
			Assert.Equal(124623533u, record.TargetItemId);
			Assert.Equal("aemeath_democracy_officer", record.RuleName);
			Assert.Equal(0.3, record.Radius);
			Assert.Equal([Marker], record.All);
			Assert.Empty(record.None);
			Assert.Equal([HideA, HideB], record.Hide);
			Assert.Equal("abc123", record.ContentFingerprint);
			Assert.Empty(read.Without(node).Records);
		}
		finally { TryDelete(mods); }
	}

	[Fact]
	public async Task Workspace_CorruptOrWrongSchemaThrowsInvalidData()
	{
		var mods = CreateScratch();
		try
		{
			var path = KitInstanceWorkspaceStore.GetPath(mods);
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			await File.WriteAllTextAsync(path, "{ not json");
			await Assert.ThrowsAsync<InvalidDataException>(async () => await KitInstanceWorkspaceStore.ReadAsync(mods));
			await File.WriteAllTextAsync(path, "{\"schema\":9,\"records\":{}}");
			await Assert.ThrowsAsync<InvalidDataException>(async () => await KitInstanceWorkspaceStore.ReadAsync(mods));
			await File.WriteAllTextAsync(path, "{\"schema\":1,\"records\":{\"not-a-guid\":{}}}");
			await Assert.ThrowsAsync<InvalidDataException>(async () => await KitInstanceWorkspaceStore.ReadAsync(mods));
		}
		finally { TryDelete(mods); }
	}

	// ---- 编译器 -------------------------------------------------------------------------

	private static KitInstanceWorkspaceRecord Record(string name = "aemeath_democracy_officer", string? fingerprint = null)
		=> new(124623533, name, 0.3, [Marker], [], [HideA, HideB], fingerprint);

	[Fact]
	public void Compiler_EnabledNodesProduceRulesAndDisabledNodesDoNot()
	{
		var enabled = ModNodeId.New();
		var disabled = ModNodeId.New();
		var workspace = KitInstanceWorkspace.Empty.With(enabled, Record("b_rule")).With(disabled, Record("a_rule"));

		var result = KitInstanceRulesCompiler.Compile([enabled], workspace, previousRevision: 6);

		Assert.Equal(7, result.Document.Revision);
		Assert.Equal("b_rule", Assert.Single(result.Document.Rules).Name);
		Assert.Empty(result.Issues);
	}

	[Fact]
	public void Compiler_NoEnabledRecordsYieldsEmptyDocumentAndRevisionStartsAtOne()
	{
		var workspace = KitInstanceWorkspace.Empty.With(ModNodeId.New(), Record());

		var none = KitInstanceRulesCompiler.Compile([], workspace, previousRevision: 0);
		Assert.Empty(none.Document.Rules);
		Assert.Equal(1, none.Document.Revision);

		var noWorkspace = KitInstanceRulesCompiler.Compile([ModNodeId.New()], KitInstanceWorkspace.Empty, 0);
		Assert.Empty(noWorkspace.Document.Rules);
	}

	[Fact]
	public void Compiler_OrdersByNameAndIsDeterministic()
	{
		var first = ModNodeId.New();
		var second = ModNodeId.New();
		var workspace = KitInstanceWorkspace.Empty.With(first, Record("zeta")).With(second, Record("alpha"));

		var a = KitInstanceRulesCompiler.Compile([first, second], workspace, 1);
		var b = KitInstanceRulesCompiler.Compile([second, first], workspace, 1);

		Assert.Equal(["alpha", "zeta"], a.Document.Rules.Select(rule => rule.Name));
		Assert.Equal(KitInstanceRulesProtocol.Serialize(a.Document), KitInstanceRulesProtocol.Serialize(b.Document));
	}

	[Fact]
	public void Compiler_SkipsInvalidDuplicateAndStaleRecordsWithWarnings()
	{
		var invalid = ModNodeId.New();
		var duplicate1 = ModNodeId.New();
		var duplicate2 = ModNodeId.New();
		var stale = ModNodeId.New();
		var fresh = ModNodeId.New();
		var workspace = KitInstanceWorkspace.Empty
			.With(invalid, Record("BAD NAME"))
			.With(duplicate1, Record("dup"))
			.With(duplicate2, Record("dup"))
			.With(stale, Record("stale_rule", fingerprint: "old"))
			.With(fresh, Record("fresh_rule", fingerprint: "same"));

		var result = KitInstanceRulesCompiler.Compile(
			[invalid, duplicate1, duplicate2, stale, fresh],
			workspace,
			0,
			node => node == stale ? "new" : node == fresh ? "SAME" : null);

		Assert.Equal(["dup", "fresh_rule"], result.Document.Rules.Select(rule => rule.Name));
		Assert.Contains(result.Issues, issue => issue.Code == "KitInstanceRuleInvalid" && issue.NodeId == invalid);
		Assert.Contains(result.Issues, issue => issue.Code == "KitInstanceRuleDuplicateName");
		var staleIssue = Assert.Single(result.Issues, issue => issue.Code == "KitInstanceRuleStale");
		Assert.Equal(stale, staleIssue.NodeId);
		Assert.All(result.Issues, issue => Assert.Equal(CoreIssueSeverity.Warning, issue.Severity));
	}

	[Fact]
	public void Compiler_EmptyFingerprintIsNotValidated()
	{
		var node = ModNodeId.New();
		var workspace = KitInstanceWorkspace.Empty.With(node, Record(fingerprint: null));
		var result = KitInstanceRulesCompiler.Compile([node], workspace, 0, _ => "anything");
		Assert.Single(result.Document.Rules);
		Assert.Empty(result.Issues);
	}

	[Fact]
	public async Task RulesFile_RevisionIsReadBackAndWritesAreAtomic()
	{
		var registry = CreateScratch();
		try
		{
			Assert.Equal(0, KitInstanceRulesFile.ReadRevision(registry));
			await KitInstanceRulesFile.WriteAsync(registry, new KitInstanceRulesDocument(5, [Rule()]));
			Assert.Equal(5, KitInstanceRulesFile.ReadRevision(registry));
			await KitInstanceRulesFile.WriteAsync(registry, KitInstanceRulesDocument.Empty(6));
			Assert.Equal(6, KitInstanceRulesFile.ReadRevision(registry));
			Assert.Empty(Directory.GetFiles(registry, "*.tmp"));
			// 坏文件里仍能取回 revision，取不回就从 0 起。
			await File.WriteAllTextAsync(KitInstanceRulesFile.GetPath(registry), "schema=1\nrevision=9\nrule=garbage\n");
			Assert.Equal(9, KitInstanceRulesFile.ReadRevision(registry));
			await File.WriteAllTextAsync(KitInstanceRulesFile.GetPath(registry), "garbage");
			Assert.Equal(0, KitInstanceRulesFile.ReadRevision(registry));
		}
		finally { TryDelete(registry); }
	}

	// ---- 部署 ---------------------------------------------------------------------------

	private sealed class Scratch : IDisposable
	{
		public string Root { get; } = CreateScratch();
		public string Mods => Path.Combine(Root, "mods");
		public string Registry => Path.Combine(Root, "registry");
		public string GameData => Path.Combine(Root, "game", "data");
		public Scratch()
		{
			Directory.CreateDirectory(Mods);
			Directory.CreateDirectory(GameData);
			File.WriteAllBytes(
				Path.Combine(GameData, $"{AdaptationLuaAssetKeys.BootArchiveId:x16}"),
				CreateGameBootArchive([0x1B, 0x4C, 0x4A, 0x02]));
		}
		public void Dispose() => TryDelete(Root);
	}

	private static (Profile Profile, LibrarySnapshot Snapshot) CreateProfile(params ModNodeId[] nodes)
	{
		var profile = new Profile(
			ProfileId.New(), "test", DateTimeOffset.UtcNow, null,
			nodes.Select((node, index) => new ProfileEntry(node, index)).ToArray());
		var snapshot = new LibrarySnapshot(
			1,
			DateTimeOffset.UtcNow,
			nodes.ToDictionary(
				node => node,
				node => new ModNode(node, node.Value.ToString("N"), new ModNodeMetadata("test", null, DateTimeOffset.UtcNow, null), [], [])),
			[profile],
			profile.Id);
		return (profile, snapshot);
	}

	private static PatchFileIndex EmptyIndex()
		=> new(DateTimeOffset.UtcNow, new Dictionary<ModNodeId, IReadOnlyList<IndexedPatchFile>>(), []);

	private static async Task<ModNodeId> EnableRuleAsync(Scratch scratch, string name = "aemeath_democracy_officer")
	{
		var node = ModNodeId.New();
		await KitInstanceWorkspaceStore.WriteAsync(
			scratch.Mods, KitInstanceWorkspace.Empty.With(node, Record(name)));
		return node;
	}

	private static Task<LuaDeploymentPreparation> MaterializeAsync(
		Scratch scratch, Profile profile, LibrarySnapshot snapshot, PatchFileIndex? index = null)
		=> new LuaDeploymentService(kitInstanceRegistryRoot: scratch.Registry).MaterializeAsync(
			profile,
			snapshot,
			index ?? EmptyIndex(),
			scratch.Mods,
			scratch.GameData,
			new Dictionary<ModNodeId, ModContentFacts>(),
			LuaDeploymentValidationMode.CompileOnly).AsTask();

	private static void SetModes(ArmorMappingMode mapping, PhysicsMode physics)
	{
		LuaDeploymentService.MappingMode = mapping;
		LuaDeploymentService.PhysicsMode = physics;
	}

	[Fact]
	public async Task Deployment_WritesRulesForEnabledNodesAndAnEmptyFileWhenThereAreNone()
	{
		SetModes(ArmorMappingMode.Off, PhysicsMode.Off);
		using var scratch = new Scratch();
		var node = await EnableRuleAsync(scratch);
		var rulesPath = KitInstanceRulesFile.GetPath(scratch.Registry);

		// 启用：写出规则，revision 从 1 起。
		var (profile, snapshot) = CreateProfile(node);
		var enabled = await MaterializeAsync(scratch, profile, snapshot);
		Assert.False(enabled.HasErrors, Describe(enabled.Issues));
		Assert.True(KitInstanceRulesProtocol.TryParse(await File.ReadAllTextAsync(rulesPath), out var first, out var error), error);
		Assert.Equal(1, first!.Revision);
		Assert.Equal("aemeath_democracy_officer", Assert.Single(first.Rules).Name);

		// 停用：照样写，写成空的，revision 加一（覆盖上一版，运行时才会撤掉规则）。
		var (emptyProfile, emptySnapshot) = CreateProfile();
		var disabled = await MaterializeAsync(scratch, emptyProfile, emptySnapshot);
		Assert.False(disabled.HasErrors, Describe(disabled.Issues));
		Assert.True(KitInstanceRulesProtocol.TryParse(await File.ReadAllTextAsync(rulesPath), out var second, out error), error);
		Assert.Equal(2, second!.Revision);
		Assert.Empty(second.Rules);
		Assert.Empty(disabled.GeneratedPatchGroups);   // 没有规则 ⇒ 不投放 Hider
		Assert.Empty(Directory.GetFiles(scratch.Registry, "*.tmp"));
	}

	[Fact]
	public async Task Deployment_PlanDoesNotWriteTheRulesFile()
	{
		SetModes(ArmorMappingMode.Off, PhysicsMode.Off);
		using var scratch = new Scratch();
		var node = await EnableRuleAsync(scratch);
		var (profile, snapshot) = CreateProfile(node);

		var plan = await new LuaDeploymentService(kitInstanceRegistryRoot: scratch.Registry).PlanAsync(
			profile, snapshot, EmptyIndex(), scratch.Mods, scratch.GameData, new Dictionary<ModNodeId, ModContentFacts>());

		Assert.False(plan.HasErrors, Describe(plan.Issues));
		Assert.False(File.Exists(KitInstanceRulesFile.GetPath(scratch.Registry)));
		// 计划阶段也要如实报告会投放一个 Hider 补丁（供界面预览），但不产生文件。
		Assert.Single(plan.GeneratedPatchGroups);
		Assert.False(Directory.Exists(Path.Combine(scratch.Mods, ".runtime", "kit-instance-hider")));
	}

	[Fact]
	public async Task Deployment_WithoutRegistryRootDoesNotTouchDisk()
	{
		SetModes(ArmorMappingMode.Off, PhysicsMode.Off);
		using var scratch = new Scratch();
		var (profile, snapshot) = CreateProfile();
		var result = await new LuaDeploymentService().MaterializeAsync(
			profile, snapshot, EmptyIndex(), scratch.Mods, scratch.GameData,
			new Dictionary<ModNodeId, ModContentFacts>(), LuaDeploymentValidationMode.CompileOnly);
		Assert.False(result.HasErrors, Describe(result.Issues));
		Assert.False(Directory.Exists(scratch.Registry));
	}

	[Fact]
	public async Task Deployment_CorruptWorkspaceWarnsAndWritesEmptyRules()
	{
		SetModes(ArmorMappingMode.Off, PhysicsMode.Off);
		using var scratch = new Scratch();
		var path = KitInstanceWorkspaceStore.GetPath(scratch.Mods);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		await File.WriteAllTextAsync(path, "{ broken");
		var (profile, snapshot) = CreateProfile(ModNodeId.New());

		var result = await MaterializeAsync(scratch, profile, snapshot);

		Assert.False(result.HasErrors, Describe(result.Issues));
		Assert.Contains(result.Issues, issue => issue.Code == "KitInstanceWorkspaceUnreadable" && issue.Severity == CoreIssueSeverity.Warning);
		Assert.True(KitInstanceRulesProtocol.TryParse(
			await File.ReadAllTextAsync(KitInstanceRulesFile.GetPath(scratch.Registry)), out var document, out _));
		Assert.Empty(document!.Rules);
	}

	[Fact]
	public async Task Deployment_RecordWhoseModContentChangedSinceConversionIsDroppedWithAWarning()
	{
		SetModes(ArmorMappingMode.Off, PhysicsMode.Off);
		using var scratch = new Scratch();
		var node = ModNodeId.New();
		var directory = Path.Combine(scratch.Mods, node.Value.ToString("N"));
		Directory.CreateDirectory(directory);
		await File.WriteAllBytesAsync(Path.Combine(directory, "9ba626afa44a3aa3.patch_0"), [1, 2, 3]);
		var (profile, snapshot) = CreateProfile(node);
		var rulesPath = KitInstanceRulesFile.GetPath(scratch.Registry);

		// 指纹对得上：规则生效，没有 Warning。
		await KitInstanceWorkspaceStore.WriteAsync(scratch.Mods, KitInstanceWorkspace.Empty.With(
			node, Record() with { ContentFingerprint = KitInstanceContentFingerprint.Compute(directory) }));
		var fresh = await MaterializeAsync(scratch, profile, snapshot);
		Assert.False(fresh.HasErrors, Describe(fresh.Issues));
		Assert.DoesNotContain(fresh.Issues, issue => issue.Code == "KitInstanceRuleStale");
		Assert.True(KitInstanceRulesProtocol.TryParse(await File.ReadAllTextAsync(rulesPath), out var withRule, out var error), error);
		Assert.Single(withRule!.Rules);

		// Mod 被更新（基底 patch 变了）：这条记录本次不生效，规则文件写成空的，并给 Warning 让用户重新转换。
		await File.WriteAllBytesAsync(Path.Combine(directory, "9ba626afa44a3aa3.patch_0"), [1, 2, 3, 4, 5]);
		var stale = await MaterializeAsync(scratch, profile, snapshot);
		Assert.False(stale.HasErrors, Describe(stale.Issues));
		var warning = Assert.Single(stale.Issues, issue => issue.Code == "KitInstanceRuleStale");
		Assert.Equal(CoreIssueSeverity.Warning, warning.Severity);
		Assert.Equal(node, warning.NodeId);
		Assert.True(KitInstanceRulesProtocol.TryParse(await File.ReadAllTextAsync(rulesPath), out var withoutRule, out error), error);
		Assert.Empty(withoutRule!.Rules);
	}

	[Fact]
	public async Task Deployment_ManagerOwnsBoot_HiderBecomesTheLastBlockOfTheSameBootPatch()
	{
		SetModes(ArmorMappingMode.Off, PhysicsMode.ExclusiveLuaEntry);
		using var scratch = new Scratch();
		var node = await EnableRuleAsync(scratch);
		var (profile, snapshot) = CreateProfile(node);

		var result = await MaterializeAsync(scratch, profile, snapshot);

		Assert.False(result.HasErrors, Describe(result.Issues));
		// 仍然只有**一个** boot 补丁：Hider 并进它，不另占序号。
		var group = Assert.Single(result.GeneratedPatchGroups);
		Assert.Contains("LuaMergeV1", group.Description, StringComparison.Ordinal);
		var embedded = await ReadEmbeddedBootSourceAsync(group);
		Assert.Contains(HiderChunk, embedded, StringComparison.Ordinal);
		Assert.Contains("@" + LuaAddonCatalog.PhysicsAddonId, embedded, StringComparison.Ordinal);
		Assert.True(
			embedded.IndexOf(HiderChunk, StringComparison.Ordinal) > embedded.IndexOf("@" + LuaAddonCatalog.PhysicsAddonId, StringComparison.Ordinal),
			"Hider must be the last block");

		// 没有规则 ⇒ boot 里没有 Hider。
		var (emptyProfile, emptySnapshot) = CreateProfile();
		var without = await MaterializeAsync(scratch, emptyProfile, emptySnapshot);
		var bare = await ReadEmbeddedBootSourceAsync(Assert.Single(without.GeneratedPatchGroups));
		Assert.DoesNotContain(HiderChunk, bare, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Deployment_ManagerDoesNotOwnBoot_HiderIsDeployedAsAStandaloneAddon()
	{
		SetModes(ArmorMappingMode.Off, PhysicsMode.Off);
		using var scratch = new Scratch();
		var ruleNode = await EnableRuleAsync(scratch);
		// 库里有一个提供 boot 的 Mod（加载器）⇒ 不报缺加载器。
		var loaderNode = ModNodeId.New();
		var loaderPatch = Path.Combine(scratch.Root, "loader", "9ba626afa44a3aa3.patch_0");
		Directory.CreateDirectory(Path.GetDirectoryName(loaderPatch)!);
		await File.WriteAllBytesAsync(loaderPatch, BuildLuaPatch(AdaptationLuaAssetKeys.BootFileId, "return 1\n"));
		var (profile, snapshot) = CreateProfile(ruleNode, loaderNode);
		var index = new PatchFileIndex(
			DateTimeOffset.UtcNow,
			new Dictionary<ModNodeId, IReadOnlyList<IndexedPatchFile>>
			{
				[loaderNode] =
				[
					new IndexedPatchFile(loaderNode, loaderPatch, Path.GetFileName(loaderPatch), "9ba626afa44a3aa3", 0, 0,
						PatchSidecarKind.Base, new FileInfo(loaderPatch).Length, DateTimeOffset.UtcNow),
				],
			},
			[]);

		var result = await MaterializeAsync(scratch, profile, snapshot, index);

		Assert.False(result.HasErrors, Describe(result.Issues));
		Assert.DoesNotContain(result.Issues, issue => issue.Code == "KitInstanceHiderNeedsLoader");
		var group = Assert.Single(result.GeneratedPatchGroups);
		Assert.Contains("Kit Instance Hider", group.Description, StringComparison.Ordinal);
		Assert.Equal($"{AdaptationLuaAssetKeys.BootArchiveId:x16}", group.ArchiveHex16);
		// 序号规则：严格小于 boot（int.MaxValue）与隐藏 unit 补丁（int.MaxValue - 1），不与它们冲突。
		Assert.Equal(int.MaxValue - 2, group.PlacementOrder!.Value);
		var patch = Assert.Single(group.Files, file => file.SidecarKind == PatchSidecarKind.Base);
		Assert.Contains(Path.Combine(".runtime", "kit-instance-hider"), patch.FilePath);
		Assert.All(group.Files, file => Assert.True(File.Exists(file.FilePath), file.FilePath));

		// 一个 Lua 资源，没有隐藏 unit；首行带 addon 标记。
		var entries = await new AdaptationPatchTocScanner().ScanEntriesAsync(patch.FilePath);
		var entry = Assert.Single(entries);
		Assert.Equal(AdaptationLuaAssetKeys.LuaTypeId, entry.AssetKey.TypeId);
		var payload = await new AdaptationPatchEntryPayloadReader().ReadPayloadAsync(entry);
		var text = Encoding.UTF8.GetString(payload.TocData.AsSpan(8));
		Assert.StartsWith("-- HD2-Addon: mods/hd2modmanager/kit_instance_hider\n", text, StringComparison.Ordinal);
		Assert.Contains("local M = { version = '0.3.0'", text, StringComparison.Ordinal);
		Assert.Equal(BinaryPrimitives.ReadUInt32LittleEndian(payload.TocData.AsSpan(0)), (uint)(payload.TocData.Length - 8));
		Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(payload.TocData.AsSpan(4)));
	}

	[Fact]
	public async Task Deployment_NoBootOwnerAndNoLoader_WarnsThatHiderCannotRun()
	{
		SetModes(ArmorMappingMode.Off, PhysicsMode.Off);
		using var scratch = new Scratch();
		var node = await EnableRuleAsync(scratch);
		var (profile, snapshot) = CreateProfile(node);

		var result = await MaterializeAsync(scratch, profile, snapshot);

		Assert.False(result.HasErrors, Describe(result.Issues));
		var warning = Assert.Single(result.Issues, issue => issue.Code == "KitInstanceHiderNeedsLoader");
		Assert.Equal(CoreIssueSeverity.Warning, warning.Severity);
		Assert.Contains("Lua 加载器", warning.Message, StringComparison.Ordinal);
		Assert.Contains("独占入口", warning.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Deployment_NoRules_NeverProducesHiderPatchOrWarning()
	{
		SetModes(ArmorMappingMode.Off, PhysicsMode.Off);
		using var scratch = new Scratch();
		var (profile, snapshot) = CreateProfile(ModNodeId.New());

		var result = await MaterializeAsync(scratch, profile, snapshot);

		Assert.False(result.HasErrors, Describe(result.Issues));
		Assert.Empty(result.GeneratedPatchGroups);
		Assert.DoesNotContain(result.Issues, issue => issue.Code == "KitInstanceHiderNeedsLoader");
		Assert.False(Directory.Exists(Path.Combine(scratch.Mods, ".runtime", "kit-instance-hider")));
		Assert.True(File.Exists(KitInstanceRulesFile.GetPath(scratch.Registry)));
	}

	// ---- 辅助 ---------------------------------------------------------------------------

	// ---- P2：部署时的污染 Warning ---------------------------------------------------------

	private sealed class FakePollution(Func<KitPollutionResult?> answer) : IKitPollutionService
	{
		public int Calls;
		public Task? PendingMaterialBuild => null;
		public Task<KitPollutionResult?> AnalyzeAsync(
			IEnumerable<AssetKey> modAssets, string? gameDataDirectory, CancellationToken cancellationToken = default)
		{
			Interlocked.Increment(ref Calls);
			return Task.FromResult(answer());
		}
	}

	private static KitPollutionResult Polluted(int count)
		=> new(
			Enumerable.Range(1, count).Select(i => new KitPollutionAffectedKit((uint)i, $"K-{i}", [1UL], [])).ToArray(),
			0,
			false);

	private static async Task<IReadOnlyList<CoreIssue>> DeployWithPollutionAsync(
		Scratch scratch, FakePollution fake, Profile profile, LibrarySnapshot snapshot, params ModNodeId[] withFacts)
	{
		var facts = withFacts.ToDictionary(
			node => node,
			node => new ModContentFacts(node, "x", "g", DateTimeOffset.UtcNow, [], [], ModAssetStructureFacts.Empty("g", DateTimeOffset.UtcNow)));
		var result = await new LuaDeploymentService(kitInstanceRegistryRoot: scratch.Registry, kitPollution: fake).MaterializeAsync(
			profile, snapshot, EmptyIndex(), scratch.Mods, scratch.GameData, facts, LuaDeploymentValidationMode.CompileOnly);
		Assert.False(result.HasErrors, Describe(result.Issues));
		return result.Issues.Where(issue => issue.Code == "KitInstancePollution").ToArray();
	}

	[Fact]
	public async Task PollutionWarning_EnabledPollutingModWithoutRecordGetsOneNonBlockingWarning()
	{
		SetModes(ArmorMappingMode.Off, PhysicsMode.Off);
		using var scratch = new Scratch();
		var node = ModNodeId.New();
		var (profile, snapshot) = CreateProfile(node);

		var warnings = await DeployWithPollutionAsync(scratch, new FakePollution(() => Polluted(3)), profile, snapshot, node);

		var warning = Assert.Single(warnings);
		Assert.Equal(CoreIssueSeverity.Warning, warning.Severity);
		Assert.Equal(node, warning.NodeId);
		Assert.Equal("『test』会连带改变 K-1、K-2 等 3 套护甲，可在详情页转专属", warning.Message);
	}

	[Fact]
	public async Task PollutionWarning_ModWithAWorkspaceRecordIsNotReported()
	{
		SetModes(ArmorMappingMode.Off, PhysicsMode.Off);
		using var scratch = new Scratch();
		var node = await EnableRuleAsync(scratch);
		var (profile, snapshot) = CreateProfile(node);
		var fake = new FakePollution(() => Polluted(3));

		Assert.Empty(await DeployWithPollutionAsync(scratch, fake, profile, snapshot, node));
		Assert.Equal(0, fake.Calls);
	}

	[Fact]
	public async Task PollutionWarning_DisabledModIsNotReported()
	{
		SetModes(ArmorMappingMode.Off, PhysicsMode.Off);
		using var scratch = new Scratch();
		var disabled = ModNodeId.New();
		var (profile, snapshot) = CreateProfile();   // 方案里没有任何启用的 Mod
		var fake = new FakePollution(() => Polluted(3));

		Assert.Empty(await DeployWithPollutionAsync(scratch, fake, profile, snapshot, disabled));
		Assert.Equal(0, fake.Calls);
	}

	[Fact]
	public async Task PollutionWarning_NotPollutedOrFailedAnalysisStaysSilent()
	{
		SetModes(ArmorMappingMode.Off, PhysicsMode.Off);
		using var scratch = new Scratch();
		var node = ModNodeId.New();
		var (profile, snapshot) = CreateProfile(node);

		Assert.Empty(await DeployWithPollutionAsync(scratch, new FakePollution(() => Polluted(1)), profile, snapshot, node));
		Assert.Empty(await DeployWithPollutionAsync(scratch, new FakePollution(() => null), profile, snapshot, node));
		Assert.Empty(await DeployWithPollutionAsync(
			scratch, new FakePollution(() => throw new InvalidOperationException("boom")), profile, snapshot, node));
	}

	private static string Describe(IEnumerable<CoreIssue> issues)
		=> string.Join(Environment.NewLine, issues.Select(issue => $"{issue.Code}: {issue.Message}"));

	private static async Task<string> ReadEmbeddedBootSourceAsync(GeneratedPatchGroup group)
	{
		var patch = Assert.Single(group.Files, file => file.SidecarKind == PatchSidecarKind.Base);
		var bytes = await File.ReadAllBytesAsync(patch.FilePath);
		var chunk = AdaptationLua51Bytecode.Read(bytes.AsSpan(0xC0).ToArray());
		return Encoding.UTF8.GetString(
			chunk.Main.Constants
				.Where(constant => constant.Text is not null)
				.OrderByDescending(constant => constant.Text!.Length)
				.First().Text!);
	}

	private static byte[] CreateGameBootArchive(byte[] bootBytecode)
	{
		var resource = new byte[bootBytecode.Length + 8];
		BinaryPrimitives.WriteUInt32LittleEndian(resource.AsSpan(0), checked((uint)bootBytecode.Length));
		BinaryPrimitives.WriteUInt32LittleEndian(resource.AsSpan(4), 2);
		bootBytecode.CopyTo(resource.AsSpan(8));
		const int typeOffset = 60;
		const int entryOffset = 92;
		const int payloadOffset = 176;
		var archive = new byte[payloadOffset + resource.Length];
		BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(0), 0xF0000011);
		BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(4), 1);
		BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(8), 1);
		BinaryPrimitives.WriteUInt64LittleEndian(archive.AsSpan(typeOffset + 8), AdaptationLuaAssetKeys.LuaTypeId);
		BinaryPrimitives.WriteUInt64LittleEndian(archive.AsSpan(typeOffset + 16), 1);
		BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(typeOffset + 24), 16);
		BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(typeOffset + 28), 64);
		BinaryPrimitives.WriteUInt64LittleEndian(archive.AsSpan(entryOffset), AdaptationLuaAssetKeys.BootFileId);
		BinaryPrimitives.WriteUInt64LittleEndian(archive.AsSpan(entryOffset + 8), AdaptationLuaAssetKeys.LuaTypeId);
		BinaryPrimitives.WriteUInt64LittleEndian(archive.AsSpan(entryOffset + 16), payloadOffset);
		BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(entryOffset + 56), checked((uint)resource.Length));
		resource.CopyTo(archive.AsSpan(payloadOffset));
		return archive;
	}

	// 单资源 Lua patch（引导桩），形状同 LuaDeploymentLoaderPackageTests。
	private static byte[] BuildLuaPatch(ulong fileId, string source)
	{
		const int headerLength = 0x48;
		const int typeRecordSize = 32;
		const int entryRecordSize = 80;
		var payload = Encoding.UTF8.GetBytes(source);
		var entriesOffset = headerLength + typeRecordSize;
		var dataOffset = entriesOffset + entryRecordSize;
		var bytes = new byte[dataOffset + 8 + payload.Length];
		BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0), 0xF0000011);
		BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 1);
		BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 1);
		BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(headerLength + 8), AdaptationLuaAssetKeys.LuaTypeId);
		BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(headerLength + 16), 1);
		BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(entriesOffset), fileId);
		BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(entriesOffset + 8), AdaptationLuaAssetKeys.LuaTypeId);
		BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(entriesOffset + 16), (ulong)dataOffset);
		BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entriesOffset + 56), (uint)(8 + payload.Length));
		BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entriesOffset + 76), 1);
		BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(dataOffset), (uint)payload.Length);
		BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(dataOffset + 4), AdaptationLuaResourceFormat.LegacySourceVersion);
		payload.CopyTo(bytes.AsSpan(dataOffset + 8));
		return bytes;
	}

	private static string CreateScratch()
	{
		var directory = Path.Combine(Path.GetTempPath(), "hd2-kit-instance-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		return directory;
	}

	private static void TryDelete(string directory)
	{
		try { Directory.Delete(directory, recursive: true); }
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}
}
