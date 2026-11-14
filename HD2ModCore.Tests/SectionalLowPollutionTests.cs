using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModCore.Infrastructure.Sectional;
using Xunit.Abstractions;

namespace HD2ModCore.Tests;

/// <summary>
/// P6 S2：低污染模式的 sectional 压缩编译（规划 §10）。
/// 纯规划测试不依赖任何样本；真数据由环境变量给出（同 <c>SectionalPhysBoneMergeTests</c>：
/// <c>HD2_TEST_SECTIONAL_MOD</c>、<c>HD2_TEST_GAME_DATA</c>），缺样本静默跳过。
/// </summary>
public sealed class SectionalLowPollutionTests(ITestOutputHelper output)
{
	// ── 纯规划 ─────────────────────────────────────────────────────────────

	private static SectionalCompressionSource Src(string id, ArmorPart? part, string body = "Any")
		=> new(id, part, body);

	private static SectionalCompressionSource[] SevenSlots() =>
	[
		Src("a1", ArmorPart.Helmet), Src("a2", ArmorPart.Torso), Src("a3", ArmorPart.Hips),
		Src("a4", ArmorPart.LeftLeg), Src("a5", ArmorPart.RightLeg), Src("a6", ArmorPart.LeftArm), Src("a7", ArmorPart.RightArm),
	];

	[Fact]
	public void ThreeSlotMode_GroupsHeadBodyArms_AndKeepsOwnerIds()
	{
		var plan = SectionalCompressionPlanner.Plan(SevenSlots(), headMerge: false);

		Assert.True(plan.Supported);
		Assert.Equal("three-slot", plan.Mode);
		Assert.Equal(["a1", "a2", "a6"], plan.Groups.Select(group => group.UnitId));
		Assert.Equal(
			[SectionalCompressionRole.Head, SectionalCompressionRole.Body, SectionalCompressionRole.Arms],
			plan.Groups.Select(group => group.Role));
		Assert.Equal(["a2", "a3", "a4", "a5"], plan.Groups[1].Members.Select(member => member.SourceUnitId));
		Assert.Equal(["a6", "a7"], plan.Groups[2].Members.Select(member => member.SourceUnitId));
		Assert.True(plan.NeedsMerge);
		Assert.Empty(plan.Passthrough);
	}

	[Fact]
	public void TwoSlotMode_MergesHeadIntoBody_IdFromTorso()
	{
		var plan = SectionalCompressionPlanner.Plan(SevenSlots(), headMerge: true);

		Assert.True(plan.Supported);
		Assert.Equal("two-slot", plan.Mode);
		Assert.Equal(["a2", "a6"], plan.Groups.Select(group => group.UnitId));
		Assert.Equal(SectionalCompressionRole.HeadBody, plan.Groups[0].Role);
		Assert.Equal(["a2", "a3", "a4", "a5", "a1"], plan.Groups[0].Members.Select(member => member.SourceUnitId));
		Assert.Equal(ArmorPart.Torso, plan.Groups[0].IdFrom);
		// 被吸收的来源 ID 不留在产物里。
		Assert.Equal(["a2", "a6"], plan.SurvivingUnitIds);
	}

	[Fact]
	public void IdRule_FallsThroughWhenThePrimaryPartIsMissing_AndIsOrderIndependent()
	{
		// 没有躯干：身体槽 ID 取胯；没有左臂：手臂槽 ID 取右臂；输入顺序不影响结果。
		var source = SevenSlots().Where(unit => unit.Part is not (ArmorPart.Torso or ArmorPart.LeftArm)).Reverse().ToArray();
		var plan = SectionalCompressionPlanner.Plan(source, headMerge: false);

		Assert.Equal(["a1", "a3", "a7"], plan.Groups.Select(group => group.UnitId));
		Assert.Equal(plan.Groups.Select(group => group.UnitId), SectionalCompressionPlanner.Plan(SevenSlots().Where(unit => unit.Part is not (ArmorPart.Torso or ArmorPart.LeftArm)).ToArray(), false).Groups.Select(group => group.UnitId));
	}

	[Fact]
	public void MissingParts_OmitTheEmptySlot()
	{
		var plan = SectionalCompressionPlanner.Plan(
			[Src("a1", ArmorPart.Helmet), Src("a2", ArmorPart.Torso)], headMerge: false);

		Assert.Equal(["a1", "a2"], plan.Groups.Select(group => group.UnitId));
		Assert.False(plan.NeedsMerge);   // 每槽单成员：不合并，也就不需要游戏 data
		Assert.Equal(["a2"], SectionalCompressionPlanner.Plan([Src("a2", ArmorPart.Torso), Src("a6", ArmorPart.LeftArm)], true).Groups.Select(group => group.UnitId).Take(1));
	}

	[Fact]
	public void Cape_PassesThroughUntouched_ShouldersJoinTheArmSlot()
	{
		var plan = SectionalCompressionPlanner.Plan(
			[.. SevenSlots(), Src("b1", ArmorPart.Cape), Src("b2", ArmorPart.LeftShoulder), Src("b3", ArmorPart.RightShoulder)],
			headMerge: false);

		Assert.True(plan.Supported);
		Assert.Equal(["b1"], plan.Passthrough.Select(member => member.SourceUnitId));
		// 手臂槽：ID 仍取左臂，肩排在两条臂之后。
		Assert.Equal(["a6", "a7", "b2", "b3"], plan.Groups[2].Members.Select(member => member.SourceUnitId));
		Assert.Equal(["a1", "a2", "a6", "b1"], plan.SurvivingUnitIds);
		Assert.Equal(["a2", "a6", "b1"],
			SectionalCompressionPlanner.Plan(
				[.. SevenSlots(), Src("b1", ArmorPart.Cape), Src("b2", ArmorPart.LeftShoulder)], headMerge: true).SurvivingUnitIds);
	}

	[Fact]
	public void ShouldersAlone_FormTheArmSlot_AndSupplyItsId()
	{
		var plan = SectionalCompressionPlanner.Plan(
			[Src("a2", ArmorPart.Torso), Src("s2", ArmorPart.RightShoulder), Src("s1", ArmorPart.LeftShoulder)], headMerge: false);

		Assert.Equal(["a2", "s1"], plan.Groups.Select(group => group.UnitId));
		Assert.Equal(["s1", "s2"], plan.Groups[1].Members.Select(member => member.SourceUnitId));
	}

	[Theory]
	[InlineData("Slim")]
	[InlineData("Stocky")]
	public void BodyVariantUnits_AreNotCompressed(string body)
	{
		var source = SevenSlots().ToList();
		source[1] = Src("a2", ArmorPart.Torso, body);
		var plan = SectionalCompressionPlanner.Plan(source, headMerge: false);

		Assert.False(plan.Supported);
		Assert.Contains("分体", plan.Reason, StringComparison.Ordinal);
	}

	[Fact]
	public void DuplicateOrUnknownParts_AreNotCompressed()
	{
		var duplicate = SectionalCompressionPlanner.Plan([.. SevenSlots(), Src("c1", ArmorPart.Torso)], headMerge: false);
		Assert.False(duplicate.Supported);
		Assert.Contains("多个来源", duplicate.Reason, StringComparison.Ordinal);

		var unknown = SectionalCompressionPlanner.Plan([.. SevenSlots(), Src("c2", null)], headMerge: false);
		Assert.False(unknown.Supported);

		Assert.False(SectionalCompressionPlanner.Plan([Src("b1", ArmorPart.Cape)], headMerge: false).Supported);
	}

	[Fact]
	public void LayoutFile_FingerprintIsOptional_RoundTrips_AndOldBytesAreUnchanged()
	{
		var plan = SectionalCompressionPlanner.Plan(SevenSlots(), headMerge: true);
		Assert.DoesNotContain("fingerprint", LowPollutionLayoutFile.Serialize(plan));
		var text = LowPollutionLayoutFile.Serialize(plan, "abc123");
		Assert.Equal("abc123", JsonNode.Parse(text)!["fingerprint"]!.GetValue<string>());
		Assert.Equal(LowPollutionLayoutFile.Serialize(plan), LowPollutionLayoutFile.Serialize(plan, null));
	}

	[Fact]
	public void LayoutFile_ListsEverySourcePartOfEachUnit_AndIsStable()
	{
		var plan = SectionalCompressionPlanner.Plan(SevenSlots(), headMerge: true);
		var text = LowPollutionLayoutFile.Serialize(plan);

		Assert.Equal(text, LowPollutionLayoutFile.Serialize(SectionalCompressionPlanner.Plan(SevenSlots().Reverse().ToArray(), headMerge: true)));
		var root = JsonNode.Parse(text)!.AsObject();
		Assert.Equal(LowPollutionLayoutFile.Schema, root["schema"]!.GetValue<string>());
		Assert.Equal("two-slot", root["mode"]!.GetValue<string>());
		var units = root["units"]!.AsArray();
		Assert.Equal(2, units.Count);
		Assert.Equal("a2", units[0]!["unit"]!.GetValue<string>());
		Assert.Equal("head-body", units[0]!["role"]!.GetValue<string>());
		Assert.Equal("torso", units[0]!["idFrom"]!.GetValue<string>());
		Assert.Equal(
			["torso", "hips", "left_leg", "right_leg", "helmet"],
			units[0]!["parts"]!.AsArray().Select(part => part!["part"]!.GetValue<string>()));
		Assert.Equal("arms", units[1]!["role"]!.GetValue<string>());
	}

	// ── 真数据 ─────────────────────────────────────────────────────────────

	private static (string Mod, string Game, string Manager)? Locate()
	{
		var mod = Environment.GetEnvironmentVariable("HD2_TEST_SECTIONAL_MOD");
		var game = Environment.GetEnvironmentVariable("HD2_TEST_GAME_DATA");
		var manager = Environment.GetEnvironmentVariable("HD2_TEST_MANAGER_ROOT");
		if (string.IsNullOrWhiteSpace(mod) || string.IsNullOrWhiteSpace(game) || !Directory.Exists(game)) return null;
		if (!Directory.Exists(Path.Combine(mod, "sectional"))) return null;
		return (mod, game, manager ?? Path.GetTempPath());
	}

	/// <summary>把样本复制到临时目录（真库只读，编译会写 <c>Overwrite/</c>）。</summary>
	private static string CopyMod(string source)
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-lowpollution-tests", Guid.NewGuid().ToString("N"), "mod");
		Directory.CreateDirectory(root);
		foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
			Directory.CreateDirectory(Path.Combine(root, Path.GetRelativePath(source, directory)));
		foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
		{
			var destination = Path.Combine(root, Path.GetRelativePath(source, file));
			File.Copy(file, destination, overwrite: true);
			File.SetAttributes(destination, FileAttributes.Normal);
		}

		return root;
	}

	private static void DeleteTree(string modDirectory)
	{
		var root = Path.GetDirectoryName(modDirectory)!;
		try
		{
			foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
				File.SetAttributes(file, FileAttributes.Normal);
			Directory.Delete(root, recursive: true);
		}
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}

	private static string OverwriteDirectory(string mod) => ModNodePayloadLayout.GetOverwriteDirectory(mod);

	private static string Patch(string directory)
		=> Directory.EnumerateFiles(directory, "*.patch_0").OrderBy(path => path, StringComparer.OrdinalIgnoreCase).First();

	private static string Id(ulong fileId) => fileId.ToString("x16", CultureInfo.InvariantCulture);

	private static async Task<Dictionary<string, UnitMeshModel>> ReadUnitsAsync(string patch)
	{
		var scanned = await new HD2ModAdaptation.PatchReconstruction.PatchTocScanner().ScanEntriesAsync(patch, default);
		var reader = new PatchUnitMeshReader();
		var units = new Dictionary<string, UnitMeshModel>();
		foreach (var entry in scanned.Where(candidate => candidate.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId).OrderBy(candidate => candidate.AssetKey.FileId))
		{
			units[Id(entry.AssetKey.FileId)] = (await reader
				.ReadAsync(entry, scanned, PatchUnitDependencyPolicy.RequirePatchLocalComposite, default)).Model;
		}

		return units;
	}

	private static string HashOverwrite(string mod)
	{
		var overwrite = OverwriteDirectory(mod);
		using var sha = SHA256.Create();
		var lines = Directory.EnumerateFiles(overwrite, "*", SearchOption.AllDirectories)
			.OrderBy(path => path, StringComparer.Ordinal)
			.Select(path => Path.GetRelativePath(overwrite, path) + ":" + Convert.ToHexString(sha.ComputeHash(StableBytes(path))));
		return string.Join("\n", lines);
	}

	/// <summary>
	/// <c>sections.json</c> 里记了编译时临时目录的路径（每次都是新 GUID，普通编译也一样），比较前去掉那一行；其余文件按原字节。
	/// </summary>
	private static byte[] StableBytes(string path)
	{
		var bytes = File.ReadAllBytes(path);
		if (!string.Equals(Path.GetFileName(path), WardrobeSectionMapFile.FileName, StringComparison.OrdinalIgnoreCase)) return bytes;
		var text = System.Text.RegularExpressions.Regex.Replace(System.Text.Encoding.UTF8.GetString(bytes), "\"path\": \"[^\"]*\"", "\"path\": \"\"");
		return System.Text.Encoding.UTF8.GetBytes(text);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task RealMod_CompressesToExpectedSlots_StableIds_NoPhysBoneLost(bool headMerge)
	{
		if (Locate() is not { } located) return;
		var mod = CopyMod(located.Mod);
		try
		{
			var context = new VpkHostContext(located.Game, new StoragePaths(located.Manager)) { LowPollutionMode = true };
			var sectional = Path.Combine(mod, "sectional");
			var sourceMap = await WardrobeSectionMapFile.ReadAsync(Path.Combine(sectional, WardrobeSectionMapFile.FileName));
			var plan = SectionalCompressionPlanner.Plan(
				[.. sourceMap.Units.Select(unit => new SectionalCompressionSource(unit.FileId, ArmorStructure.TryParse(unit.Slot), unit.BodyType))],
				headMerge);
			Assert.True(plan.Supported, plan.Reason);

			async Task<SectionalCompileResult> Compile()
				=> await SectionalCompiler.CompileAsync(mod, context, null, null, headMerge, [], lowPollution: true);

			var first = await Compile();
			Assert.True(first.Ok, first.Error);
			// 编完的素体在同样设置下被判成「已是最新」，改头身合并才会重编（S4a 的指纹）。
			var wardrobe = new VpkWardrobeFile(false, VpkWardrobeState.Empty, HeadMergeIntoBody: headMerge);
			Assert.Equal(LowPollutionPrepStatus.UpToDate, LowPollutionFreshness.Assess("k", mod, true, wardrobe).Status);
			Assert.Equal(LowPollutionPrepStatus.NeedsRecompile,
				LowPollutionFreshness.Assess("k", mod, true, wardrobe with { HeadMergeIntoBody = !headMerge }).Status);
			var firstLayout = await File.ReadAllTextAsync(Path.Combine(OverwriteDirectory(mod), LowPollutionLayoutFile.FileName));
			var firstUnits = await ReadUnitsAsync(Patch(OverwriteDirectory(mod)));
			var firstHash = HashOverwrite(mod);

			// 件数：3 槽 / 2 槽（披风、肩原样保留，这两份样本没有）。
			Assert.Equal(headMerge ? 2 : 3, plan.Groups.Count);
			Assert.Equal(plan.SurvivingUnitIds.Order(StringComparer.Ordinal), firstUnits.Keys.Order(StringComparer.Ordinal));
			Assert.Equal(plan.Groups.Count + plan.Passthrough.Count, firstUnits.Count);
			Assert.Equal(headMerge ? SectionalHeadMergeOutcome.AppliedOwnModel : SectionalHeadMergeOutcome.Off, first.HeadMerge);

			// 重编两次：ID、描述文件与全部产物字节都不变。
			var second = await Compile();
			Assert.True(second.Ok, second.Error);
			Assert.Equal(firstLayout, await File.ReadAllTextAsync(Path.Combine(OverwriteDirectory(mod), LowPollutionLayoutFile.FileName)));
			Assert.Equal(firstUnits.Keys.Order(StringComparer.Ordinal), (await ReadUnitsAsync(Patch(OverwriteDirectory(mod)))).Keys.Order(StringComparer.Ordinal));
			Assert.Equal(firstHash, HashOverwrite(mod));

			// 来源物理骨一根不少：每个压缩 unit 的节点表 ⊇ 它所有来源部位的物理骨。
			var sources = await ReadUnitsAsync(Patch(sectional));
			var rigBones = await SectionalPhysBoneRig.ReadBoneHashesAsync(Patch(sectional), default);
			foreach (var group in plan.Groups)
			{
				var missing = SectionalPhysBoneRig.FindMissing(
					group.Members.Select(member => sources[member.SourceUnitId]), rigBones, firstUnits[group.UnitId]);
				output.WriteLine($"headMerge={headMerge} {LowPollutionLayoutFile.RoleName(group.Role)}=0x{group.UnitId} parts={group.Members.Count} rigBones={rigBones.Count} missing={missing.Count}");
				Assert.Empty(missing);
			}

			Assert.DoesNotContain(first.Warnings, warning => warning.Contains("自定义/物理骨", StringComparison.Ordinal));

			// 物理容器按骨名匹配：压缩后的 patch 里容器还在、rig 与来源一致（容器不用跟着合并）。
			Assert.Equal(
				rigBones.Order(),
				(await SectionalPhysBoneRig.ReadBoneHashesAsync(Patch(OverwriteDirectory(mod)), default)).Order());

			// 注册表：只剩压缩 unit 的注册件，没有悬空引用。
			var registry = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(OverwriteDirectory(mod), VpkCustomModelFile.FileName)))!;
			var registryUnits = registry["pieces"]!.AsArray().Select(piece => piece!["unit"]!.GetValue<string>()).Distinct().Order(StringComparer.Ordinal).ToArray();
			Assert.Equal(plan.SurvivingUnitIds.Order(StringComparer.Ordinal), registryUnits);
			output.WriteLine($"headMerge={headMerge}: {firstUnits.Count} units, overwrite files:\n{firstHash}");
		}
		finally
		{
			DeleteTree(mod);
		}
	}

	[Fact]
	public async Task RealMod_SwitchingOff_RestoresTheOrdinaryOutputByteForByte()
	{
		if (Locate() is not { } located) return;
		var mod = CopyMod(located.Mod);
		try
		{
			var paths = new StoragePaths(located.Manager);
			var off = new VpkHostContext(located.Game, paths);

			// 开关关：与不传该参数逐字节一致，且没有任何低污染文件。
			var defaultCompile = await SectionalCompiler.CompileAsync(mod, off, null, null, false, []);
			Assert.True(defaultCompile.Ok, defaultCompile.Error);
			var baseline = HashOverwrite(mod);
			var explicitOff = await SectionalCompiler.CompileAsync(mod, off, null, null, false, [], lowPollution: false);
			Assert.True(explicitOff.Ok, explicitOff.Error);
			Assert.Equal(baseline, HashOverwrite(mod));
			Assert.False(File.Exists(Path.Combine(OverwriteDirectory(mod), LowPollutionLayoutFile.FileName)));
			Assert.Equal(
				await File.ReadAllBytesAsync(Patch(Path.Combine(mod, "sectional"))),
				await File.ReadAllBytesAsync(Patch(OverwriteDirectory(mod))));

			// 开了再关：回到与从未开过时完全相同的产物，描述文件也被清掉。
			var on = await SectionalCompiler.CompileAsync(mod, off, null, null, false, [], lowPollution: true);
			Assert.True(on.Ok, on.Error);
			Assert.True(File.Exists(Path.Combine(OverwriteDirectory(mod), LowPollutionLayoutFile.FileName)));
			Assert.NotEqual(baseline, HashOverwrite(mod));
			var back = await SectionalCompiler.CompileAsync(mod, off, null, null, false, []);
			Assert.True(back.Ok, back.Error);
			Assert.Equal(baseline, HashOverwrite(mod));
		}
		finally
		{
			DeleteTree(mod);
		}
	}
}
