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

	/// <summary>
	/// 2026-11-10 改了这条规则：**同名 patch 来自不同来源时不再覆盖，直接拒绝**。
	/// <para>
	/// 原因（实测数据，见 水墨牛牛 系列）：同一个游戏档案下，「本体」的 patch 有 21 个 unit，
	/// 「翅膀」「鞋子」各只有 1 个，而且都是「本体」已包含的。旧行为"后者赢"会把产物缩成 1 个 unit，
	/// 表面成功、实际丢掉 20 个；依据文件还会写着每个来源各自贡献了哪个文件，与磁盘对不上。
	/// 这种冲突没有安全的自动答案（合并要重写 patch），所以挡在导入前并说清该怎么做。
	/// </para>
	/// </summary>
	[Fact]
	public void 同名patch来自不同来源时拒绝而不是静默覆盖()
	{
		var target = NewDir("target");
		var first = Source("本体", 0);
		var second = Source("翅膀", 0);

		var outcome = SectionalSourceAssembler.Assemble(
			target, [first, second],
			new SectionalSourceSelection("8d3a01f4a44a3aa3", "护甲"), null, Keys());

		Assert.False(outcome.Ok);
		Assert.Contains("同一个 patch 文件名", outcome.Error!, StringComparison.Ordinal);
		// 报错要能指导下一步：点出冲突的是哪个文件、各自来自谁。
		Assert.Contains("本体", outcome.Error!, StringComparison.Ordinal);
		Assert.Contains("翅膀", outcome.Error!, StringComparison.Ordinal);
	}

	/// <summary>
	/// 来源 patch 常带只读属性（导入 zip 时带来的），<c>File.Copy</c> 会把它带给目标，
	/// 之后覆盖同名文件就会 Access denied（线上就是这么失败的）。
	/// 这里锁住"拷完必须可写"。
	/// </summary>
	[Fact]
	public void 来源是只读文件时拷进条目后必须可写()
	{
		var target = NewDir("target");
		var source = Source("只读来源", 0);
		var patch = Path.Combine(source.Directory, $"{Archive}.patch_0");
		File.SetAttributes(patch, FileAttributes.ReadOnly);

		var outcome = SectionalSourceAssembler.Assemble(
			target, [source],
			new SectionalSourceSelection("8d3a01f4a44a3aa3", "护甲"), null, Keys());

		Assert.True(outcome.Ok);
		var copied = new FileInfo(Path.Combine(target, $"{Archive}.patch_0"));
		Assert.True(copied.Exists);
		Assert.False(copied.IsReadOnly, "复制出来的来源 patch 不该继承只读属性。");
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
