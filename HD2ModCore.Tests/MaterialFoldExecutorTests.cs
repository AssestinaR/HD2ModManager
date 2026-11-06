using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 锁住折并执行器：它只往宿主里搬，不动库、也不删成员目录。
/// <para>
/// 三件事必须分开——搬、核对、退役。任何一步失败都要停在"数据都还在"的状态，
/// 而不是"两边都没有"。所以这里既验"搬对了"，也验"失败时没有留下半成品"。
/// </para>
/// </summary>
public sealed class MaterialFoldExecutorTests : IDisposable
{
	private const string Archive = "9ba626afa44a3aa3";
	private readonly string _root = Path.Combine(
		Path.GetTempPath(), "hd2-material-fold-exec-tests", Guid.NewGuid().ToString("N"));

	public MaterialFoldExecutorTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		try
		{
			foreach (var path in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
				File.SetAttributes(path, System.IO.FileAttributes.Normal);
			Directory.Delete(_root, recursive: true);
		}
		catch { }
	}

	private string NewMod(string name, params string[] patchNames)
	{
		var root = Path.Combine(_root, name);
		var payload = Path.Combine(root, ModNodePayloadLayout.VpkPatchDirectoryName);
		Directory.CreateDirectory(payload);
		foreach (var patch in patchNames)
		{
			File.WriteAllText(Path.Combine(payload, patch), "toc-" + name);
			File.WriteAllText(Path.Combine(payload, patch + ".gpu_resources"), "gpu-" + name);
		}
		return root;
	}

	private string Payload(string modRoot)
		=> Path.Combine(modRoot, ModNodePayloadLayout.VpkPatchDirectoryName);

	private MaterialFoldMember Member(string name, string modRoot, bool enabled, int order)
		=> new(name, name, Payload(modRoot), enabled, order);

	[Fact]
	public void Execute_MovesTheMaterialPatchUnderItsNewIndex()
	{
		var host = NewMod("host", $"{Archive}.patch_0");
		var member = NewMod("m1", $"{Archive}.patch_0");
		var plan = MaterialOptionFoldPlanner.Plan(Payload(host),
			[Member("m1", member, enabled: true, order: 0)]);

		var result = MaterialFoldExecutor.Execute(plan, host);

		Assert.True(result.Succeeded, result.Error);
		Assert.True(File.Exists(Path.Combine(Payload(host), $"{Archive}.patch_1")));
		Assert.True(File.Exists(Path.Combine(Payload(host), $"{Archive}.patch_1.gpu_resources")));
		// 成员那边必须真的没了：否则它在部署里仍然生效，与宿主这份互相覆盖。
		Assert.False(File.Exists(Path.Combine(Payload(member), $"{Archive}.patch_0")));
	}

	[Fact]
	public void Execute_RenamesSidecarsTogetherWithThePatch()
	{
		var host = NewMod("host", $"{Archive}.patch_0");
		var member = NewMod("m1", $"{Archive}.patch_0");
		File.WriteAllText(Path.Combine(Payload(member), $"{Archive}.patch_0.stream"), "stream");
		var plan = MaterialOptionFoldPlanner.Plan(Payload(host),
			[Member("m1", member, enabled: false, order: 0)]);

		MaterialFoldExecutor.Execute(plan, host);

		Assert.True(File.Exists(Path.Combine(Payload(host), $"{Archive}.patch_1.stream")));
		Assert.False(File.Exists(Path.Combine(Payload(member), $"{Archive}.patch_0.stream")));
	}

	[Fact]
	public void Execute_TakesTheSourceVpkAlong_SoTheHostStaysSelfContained()
	{
		// 只搬 patch 而把 VPK 留下，宿主就再也没法重构建（重构建要从来源 VPK 重转）。
		var host = NewMod("host", $"{Archive}.patch_0");
		var member = NewMod("m1", $"{Archive}.patch_0");
		File.WriteAllText(Path.Combine(member, "material.vpk"), "vpk-bytes");
		var plan = MaterialOptionFoldPlanner.Plan(Payload(host),
			[Member("m1", member, enabled: false, order: 0)]);

		var result = MaterialFoldExecutor.Execute(plan, host);

		Assert.True(result.Succeeded, result.Error);
		Assert.True(File.Exists(Path.Combine(host, "material.vpk")));
		Assert.False(File.Exists(Path.Combine(member, "material.vpk")));
	}

	[Fact]
	public void Execute_DoesNotOverwriteAnExistingSourceOfTheSameName()
	{
		var host = NewMod("host", $"{Archive}.patch_0");
		File.WriteAllText(Path.Combine(host, "shared.vpk"), "host-copy");
		var member = NewMod("m1", $"{Archive}.patch_0");
		File.WriteAllText(Path.Combine(member, "shared.vpk"), "member-copy");
		var plan = MaterialOptionFoldPlanner.Plan(Payload(host),
			[Member("m1", member, enabled: false, order: 0)]);

		var result = MaterialFoldExecutor.Execute(plan, host);

		Assert.True(result.Succeeded, result.Error);
		Assert.Equal("host-copy", File.ReadAllText(Path.Combine(host, "shared.vpk")));
		// 没搬走的那份还在原地，并按警告说出来。
		Assert.True(File.Exists(Path.Combine(member, "shared.vpk")));
		Assert.Contains(result.Warnings, warning => warning.Contains("shared.vpk", StringComparison.Ordinal));
	}

	[Fact]
	public async Task Execute_RefreshesTheManifest()
	{
		var host = NewMod("host", $"{Archive}.patch_0");
		var member = NewMod("m1", $"{Archive}.patch_0");
		var plan = MaterialOptionFoldPlanner.Plan(Payload(host),
			[Member("m1", member, enabled: true, order: 0)]);

		MaterialFoldExecutor.Execute(plan, host);

		var manifest = await SectionalPatchManifestFile.TryReadAsync(Payload(host));
		Assert.NotNull(manifest);
		Assert.Equal([0, 1], manifest!.Patches.Select(entry => entry.PatchIndex).ToArray());
		Assert.Equal(SectionalPatchRole.Unit, manifest.Patches[0].Role);
		Assert.Equal(SectionalPatchRole.Material, manifest.Patches[1].Role);
	}

	[Fact]
	public void Execute_RefusesAPlanWithBlockers()
	{
		var host = NewMod("host", $"{Archive}.patch_0");
		var emptyPayload = Path.Combine(_root, "empty", ModNodePayloadLayout.VpkPatchDirectoryName);
		Directory.CreateDirectory(emptyPayload);
		var plan = MaterialOptionFoldPlanner.Plan(Payload(host),
			[new MaterialFoldMember("n1", "空包", emptyPayload, false, 0)]);

		var result = MaterialFoldExecutor.Execute(plan, host);

		Assert.False(result.Succeeded);
		Assert.Contains("阻断", result.Error);
	}

	[Fact]
	public void Execute_DetectsTargetCollisionsBeforeMovingAnything()
	{
		var host = NewMod("host", $"{Archive}.patch_0");
		// 宿主里已经有 patch_1：再折并就会撞名。
		File.WriteAllText(Path.Combine(Payload(host), $"{Archive}.patch_1"), "already-there");
		var member = NewMod("m1", $"{Archive}.patch_0");
		// 计划按 next = max+1 = 2 生成，所以手工构造一个会撞的计划来验证体检分支。
		var plan = new MaterialFoldPlan(Payload(host), Archive,
		[
			new MaterialFoldEntry("n1", "m1", Path.Combine(Payload(member), $"{Archive}.patch_0"),
				member, $"{Archive}.patch_1", 1, true, 0),
		], Array.Empty<MaterialFoldBlocker>());

		var result = MaterialFoldExecutor.Execute(plan, host);

		Assert.False(result.Succeeded);
		Assert.Contains("同名", result.Error);
		// 体检在动手之前，所以成员那份必须原封不动。
		Assert.True(File.Exists(Path.Combine(Payload(member), $"{Archive}.patch_0")));
	}

	[Fact]
	public void Execute_MovesAllMembersInOnePass()
	{
		var host = NewMod("host", $"{Archive}.patch_0");
		var a = NewMod("a", $"{Archive}.patch_0");
		var b = NewMod("b", $"{Archive}.patch_0");
		var plan = MaterialOptionFoldPlanner.Plan(Payload(host),
		[
			Member("a", a, enabled: true, order: 0),
			Member("b", b, enabled: false, order: 1),
		]);

		var result = MaterialFoldExecutor.Execute(plan, host);

		Assert.True(result.Succeeded, result.Error);
		Assert.True(File.Exists(Path.Combine(Payload(host), $"{Archive}.patch_1")));
		Assert.True(File.Exists(Path.Combine(Payload(host), $"{Archive}.patch_2")));
		Assert.Equal(2 + 2, result.Moves.Count);
	}
}
