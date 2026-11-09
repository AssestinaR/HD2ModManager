using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 锁住「生成sectional版」的导入侧：来源 patch 怎么平铺进新条目、依据文件怎么写。
/// <para>
/// 这里有个容易做错的点：模块根上的 patch 是**平铺**的，同名会互相覆盖。
/// 覆盖本身是对的（与部署"同名后者赢"一致），但必须留下痕迹——
/// 否则用户看到产物少了一组，完全不知道是哪一步吃掉的。
/// </para>
/// </summary>
public sealed class SectionalSourceAssemblerTests : IDisposable
{
	private const string Archive = "9ba626afa44a3aa3";
	private readonly string _root = Path.Combine(
		Path.GetTempPath(), "hd2-source-assembler-tests", Guid.NewGuid().ToString("N"));

	public SectionalSourceAssemblerTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch { }
	}

	private string NewDir(string name)
	{
		var dir = Path.Combine(_root, name);
		Directory.CreateDirectory(dir);
		return dir;
	}

	private static void Touch(string dir, string name, string content = "toc")
		=> File.WriteAllText(Path.Combine(dir, name), content);

	private static SectionalSourceAssetKeys Keys()
		=> new(["eac0b497876adedf:0000000000000001"], [], []);

	private SectionalSourceCandidate Source(string name, int index)
	{
		var dir = NewDir($"src-{name}");
		Touch(dir, $"{Archive}.patch_{index}");
		Touch(dir, $"{Archive}.patch_{index}.gpu_resources", "gpu");
		return new SectionalSourceCandidate(Guid.NewGuid().ToString("N"), name, dir);
	}

	[Fact]
	public void 来源patch与sidecar一起平铺到条目根()
	{
		var target = NewDir("target");
		var source = Source("主Mod", 0);

		var outcome = SectionalSourceAssembler.Assemble(
			target, [source],
			new SectionalSourceSelection("8d3a01f4a44a3aa3", "来源护甲"), null, Keys());

		Assert.True(outcome.Ok);
		Assert.True(File.Exists(Path.Combine(target, $"{Archive}.patch_0")));
		Assert.True(File.Exists(Path.Combine(target, $"{Archive}.patch_0.gpu_resources")));
		Assert.True(SectionalSourceFile.Exists(target));
	}

	[Fact]
	public async Task 依据文件记下每个来源的patch文件名()
	{
		var target = NewDir("target");
		var source = Source("主Mod", 0);

		Assert.True(SectionalSourceAssembler.Assemble(
			target, [source],
			null, new SectionalSourceSelection("c0ffee00a44a3aa3", "来源头盔"), Keys()).Ok);

		var document = await SectionalSourceFile.TryReadAsync(target);
		Assert.NotNull(document);
		var entry = Assert.Single(document!.Sources);
		Assert.Equal("主Mod", entry.Name);
		Assert.Equal([$"{Archive}.patch_0"], entry.PatchFiles);
		Assert.Null(document.Armor);
		Assert.NotNull(document.Helmet);
	}

	[Fact]
	public void 同名patch后者覆盖前者并留下说明()
	{
		var target = NewDir("target");
		var first = Source("旧来源", 0);
		var second = Source("新来源", 0);

		var outcome = SectionalSourceAssembler.Assemble(
			target, [first, second],
			new SectionalSourceSelection("8d3a01f4a44a3aa3", "护甲"), null, Keys());

		Assert.True(outcome.Ok);
		Assert.Contains(outcome.Notes, note => note.Contains("覆盖了同名 patch", StringComparison.Ordinal));
	}

	[Fact]
	public void 来源目录不存在时跳过并说明_不整体失败()
	{
		var target = NewDir("target");
		var good = Source("可用来源", 0);
		var missing = new SectionalSourceCandidate(Guid.NewGuid().ToString("N"), "已删除来源", Path.Combine(_root, "nope"));

		var outcome = SectionalSourceAssembler.Assemble(
			target, [good, missing],
			new SectionalSourceSelection("8d3a01f4a44a3aa3", "护甲"), null, Keys());

		Assert.True(outcome.Ok);
		Assert.Contains(outcome.Notes, note => note.Contains("目录不存在", StringComparison.Ordinal));
	}

	[Fact]
	public void 来源根上没有patch时整体失败()
	{
		// 来源不是 legacy（patch 在 payload 里）时，拷贝只会得到一个空条目 —— 必须挡在导入前。
		var target = NewDir("target");
		var empty = NewDir("src-empty");

		var outcome = SectionalSourceAssembler.Assemble(
			target, [new SectionalSourceCandidate(Guid.NewGuid().ToString("N"), "转换产物", empty)],
			new SectionalSourceSelection("8d3a01f4a44a3aa3", "护甲"), null, Keys());

		Assert.False(outcome.Ok);
		Assert.Contains("不会有任何内容", outcome.Error!, StringComparison.Ordinal);
	}

	[Fact]
	public void 护甲与头盔都没选时拒绝()
	{
		var target = NewDir("target");

		var outcome = SectionalSourceAssembler.Assemble(target, [Source("主Mod", 0)], null, null, Keys());

		Assert.False(outcome.Ok);
		Assert.Contains("都没有选", outcome.Error!, StringComparison.Ordinal);
	}

	[Fact]
	public void 白名单没有unit时拒绝()
	{
		var target = NewDir("target");

		var outcome = SectionalSourceAssembler.Assemble(
			target, [Source("主Mod", 0)],
			new SectionalSourceSelection("8d3a01f4a44a3aa3", "护甲"), null,
			new SectionalSourceAssetKeys([], [], []));

		Assert.False(outcome.Ok);
		Assert.Contains("没有任何 unit", outcome.Error!, StringComparison.Ordinal);
	}
}
