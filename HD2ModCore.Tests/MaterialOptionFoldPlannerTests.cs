using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 锁住「材质 Option 折进宿主」的只读计划。
/// <para>
/// 这是会移动用户库文件的迁移的第一步。计划必须能在**不动磁盘**的前提下说清：
/// 搬哪些、搬到哪个序号、哪个是当前启用项、以及有什么挡着。
/// 猜错"搬哪一个"的代价是整个材质变体错位且不可逆，所以边界情况都单独钉住。
/// </para>
/// </summary>
public sealed class MaterialOptionFoldPlannerTests : IDisposable
{
	private const string Archive = "9ba626afa44a3aa3";
	private readonly string _root = Path.Combine(
		Path.GetTempPath(), "hd2-material-fold-tests", Guid.NewGuid().ToString("N"));

	public MaterialOptionFoldPlannerTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch { }
	}

	private string NewPayload(string name, params string[] patchFileNames)
	{
		var dir = Path.Combine(_root, name, ModNodePayloadLayout.VpkPatchDirectoryName);
		Directory.CreateDirectory(dir);
		foreach (var fileName in patchFileNames)
		{
			File.WriteAllText(Path.Combine(dir, fileName), "toc");
			File.WriteAllText(Path.Combine(dir, fileName + ".gpu_resources"), "gpu");
		}
		return dir;
	}

	[Fact]
	public void Plan_AssignsSequentialIndicesAfterTheExistingOnes()
	{
		var host = NewPayload("host", $"{Archive}.patch_0");
		var a1 = NewPayload("a1", $"{Archive}.patch_0");
		var b1 = NewPayload("b1", $"{Archive}.patch_0");

		var plan = MaterialOptionFoldPlanner.Plan(host,
		[
			new MaterialFoldMember("n1", "A1主体", a1, Enabled: true, Order: 0),
			new MaterialFoldMember("n2", "B1服装色黑", b1, Enabled: false, Order: 1),
		]);

		Assert.True(plan.CanApply);
		Assert.Equal(Archive, plan.ArchiveHex16);
		Assert.Equal([1, 2], plan.Entries.Select(entry => entry.TargetPatchIndex).ToArray());
		Assert.Equal($"{Archive}.patch_1", plan.Entries[0].TargetPatchFileName);
		Assert.Equal($"{Archive}.patch_2", plan.Entries[1].TargetPatchFileName);
		Assert.Equal($"{Archive}.patch_1", plan.SelectedTargetPatchFileName);
		Assert.All(plan.Entries, entry => Assert.EndsWith(".patch_0", entry.SourcePatchPath));
	}

	[Fact]
	public void Plan_KeepsOptionOrder()
	{
		var host = NewPayload("host", $"{Archive}.patch_0");
		var late = NewPayload("late", $"{Archive}.patch_0");
		var early = NewPayload("early", $"{Archive}.patch_0");

		var plan = MaterialOptionFoldPlanner.Plan(host,
		[
			new MaterialFoldMember("n2", "Z", late, Enabled: false, Order: 5),
			new MaterialFoldMember("n1", "A", early, Enabled: true, Order: 1),
		]);

		Assert.Equal(["A", "Z"], plan.Entries.Select(entry => entry.ModName).ToArray());
		Assert.Equal($"{Archive}.patch_1", plan.SelectedTargetPatchFileName);
	}

	[Fact]
	public void Plan_StartsAfterAnAlreadyPresentMaterialPatch()
	{
		// 恋星milfy_bill 的真实形状：宿主里已经有 patch_0 和一条材质 patch_1。
		var host = NewPayload("host", $"{Archive}.patch_0", $"{Archive}.patch_1");
		var member = NewPayload("m", $"{Archive}.patch_0");

		var plan = MaterialOptionFoldPlanner.Plan(host,
			[new MaterialFoldMember("n1", "2K材质", member, Enabled: false, Order: 0)]);

		Assert.Equal(2, Assert.Single(plan.Entries).TargetPatchIndex);
	}

	[Fact]
	public void Plan_BlocksWhenHostHasNoUnitPatch()
	{
		var host = Path.Combine(_root, "host", ModNodePayloadLayout.VpkPatchDirectoryName);
		Directory.CreateDirectory(host);
		var member = NewPayload("m", $"{Archive}.patch_0");

		var plan = MaterialOptionFoldPlanner.Plan(host,
			[new MaterialFoldMember("n1", "A1", member, Enabled: true, Order: 0)]);

		Assert.False(plan.CanApply);
		Assert.Equal(MaterialFoldBlockerKind.HostHasNoUnitPatch, Assert.Single(plan.Blockers).Kind);
	}

	[Fact]
	public void Plan_BlocksMemberWithoutAnyPatch_AndStillPlansTheOthers()
	{
		var host = NewPayload("host", $"{Archive}.patch_0");
		var empty = Path.Combine(_root, "empty", ModNodePayloadLayout.VpkPatchDirectoryName);
		Directory.CreateDirectory(empty);
		var good = NewPayload("good", $"{Archive}.patch_0");

		var plan = MaterialOptionFoldPlanner.Plan(host,
		[
			new MaterialFoldMember("n1", "空的", empty, Enabled: false, Order: 0),
			new MaterialFoldMember("n2", "有用的", good, Enabled: true, Order: 1),
		]);

		Assert.False(plan.CanApply);
		var blocker = Assert.Single(plan.Blockers);
		Assert.Equal(MaterialFoldBlockerKind.MemberHasNoPatch, blocker.Kind);
		Assert.Equal("空的", blocker.MemberName);
		Assert.Equal("有用的", Assert.Single(plan.Entries).ModName);
	}

	[Fact]
	public void Plan_BlocksMemberWithSeveralPatches()
	{
		var host = NewPayload("host", $"{Archive}.patch_0");
		var ambiguous = NewPayload("ambiguous", $"{Archive}.patch_0", $"{Archive}.patch_1");

		var plan = MaterialOptionFoldPlanner.Plan(host,
			[new MaterialFoldMember("n1", "一堆", ambiguous, Enabled: false, Order: 0)]);

		Assert.False(plan.CanApply);
		Assert.Equal(MaterialFoldBlockerKind.MemberHasMultiplePatches, Assert.Single(plan.Blockers).Kind);
		Assert.Empty(plan.Entries);
	}

	[Fact]
	public void Plan_IgnoresSidecarsWhenCountingPatches()
	{
		var host = NewPayload("host", $"{Archive}.patch_0");
		var member = NewPayload("m", $"{Archive}.patch_0");
		// sidecar 不该让它看起来像"两份 patch"
		File.WriteAllText(Path.Combine(member, $"{Archive}.patch_0.stream"), "stream");

		var plan = MaterialOptionFoldPlanner.Plan(host,
			[new MaterialFoldMember("n1", "A1", member, Enabled: true, Order: 0)]);

		Assert.True(plan.CanApply);
	}

	[Fact]
	public void Plan_WithNoMembers_IsNotApplicable()
	{
		var host = NewPayload("host", $"{Archive}.patch_0");

		var plan = MaterialOptionFoldPlanner.Plan(host, []);

		Assert.False(plan.CanApply);
		Assert.Empty(plan.Entries);
		Assert.Empty(plan.Blockers);
		Assert.Null(plan.SelectedTargetPatchFileName);
	}
}
