using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 锁住「生成sectional版」的导入侧：来源 patch 怎么挑、怎么重新编号、依据文件怎么写。
/// <para>
/// 这一层曾经做错两件事，都是 2026-11-10 在真实系列上撞出来的：
/// <b>①</b> 按原名平铺 ⇒ 系列里多个子级带同一个 <c>&lt;archive&gt;.patch_0</c> 时互相覆盖，
/// 实测把 21 个 unit 缩成 1 个；<b>②</b> 来源 patch 是只读的，<c>File.Copy</c> 把只读属性带进新条目，
/// 之后覆盖同名文件直接 Access denied。
/// 现在的规则：**只挑命中选择的、按来源顺序连续重新编号**，并抹掉只读属性。
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

	/// <summary>白名单只给一个材质 key：测试里的 patch 是假文件（读不出条目），按"命中"处理。</summary>
	private static SectionalSourceAssetKeys Keys()
		=> new(["eac0b497876adedf:0000000000000001"], [], []);

	private SectionalSourceCandidate Source(string name, int index = 0)
	{
		var dir = NewDir($"src-{name}");
		Touch(dir, $"{Archive}.patch_{index}");
		Touch(dir, $"{Archive}.patch_{index}.gpu_resources", "gpu");
		return new SectionalSourceCandidate(Guid.NewGuid().ToString("N"), name, dir);
	}

	private static Task<SectionalSourceAssemblyOutcome> Assemble(
		string target,
		IReadOnlyList<SectionalSourceCandidate> sources,
		SectionalSourceSelection? armor,
		SectionalSourceSelection? helmet,
		SectionalSourceAssetKeys keys)
		=> SectionalSourceAssembler.AssembleAsync(target, sources, armor, helmet, keys).AsTask();

	[Fact]
	public async Task 来源patch与sidecar一起平铺到条目根()
	{
		var target = NewDir("target");

		var outcome = await Assemble(
			target, [Source("主Mod")],
			new SectionalSourceSelection("8d3a01f4a44a3aa3", "来源护甲"), null, Keys());

		Assert.True(outcome.Ok);
		Assert.True(File.Exists(Path.Combine(target, $"{Archive}.patch_0")));
		Assert.True(File.Exists(Path.Combine(target, $"{Archive}.patch_0.gpu_resources")));
		Assert.True(SectionalSourceFile.Exists(target));
	}

	[Fact]
	public async Task 依据文件记下每个来源重新编号后的patch文件名()
	{
		var target = NewDir("target");

		Assert.True((await Assemble(
			target, [Source("主Mod")],
			null, new SectionalSourceSelection("c0ffee00a44a3aa3", "来源头盔"), Keys())).Ok);

		var document = await SectionalSourceFile.TryReadAsync(target);
		Assert.NotNull(document);
		var entry = Assert.Single(document!.Sources);
		Assert.Equal("主Mod", entry.Name);
		Assert.Equal([$"{Archive}.patch_0"], entry.PatchFiles);
		Assert.Null(document.Armor);
		Assert.NotNull(document.Helmet);
	}

	/// <summary>
	/// 装配层**不按白名单挑文件**。2026-11-10 真实事故：装饰件（上衣/内裤/翅膀/鞋子…）各带 1 个
	/// unit，而那些 unit 不在"选中那件护甲"的意图集合里 ⇒ 被整份跳过，用户只拿到 9 个材质组 +
	/// 1 个 unit 组，几何缺一大半。内容筛选归转换器，装配层只负责"收集 + 重命名"。
	/// </summary>
	[Fact]
	public async Task 不按白名单挑文件_来源里的patch全部收集()
	{
		var target = NewDir("target");
		var source = Source("装饰件");

		// 白名单给一个和这个 patch 完全无关的 key：按旧逻辑它会被判成"没有命中"而跳过。
		var unrelated = new SectionalSourceAssetKeys(["0eac0b497876ade0f:00000000000000ff"], [], []);
		var outcome = await Assemble(
			target, [source],
			new SectionalSourceSelection("8d3a01f4a44a3aa3", "护甲"), null, unrelated);

		Assert.True(outcome.Ok);
		Assert.True(File.Exists(Path.Combine(target, $"{Archive}.patch_0")), "来源 patch 不该因为白名单没命中就被丢掉。");
	}

	/// <summary>
	/// 系列里多个子级带同名 patch 是常态（同一套 armor 的"本体"与各装饰件）。
	/// 重新编号让它们各自占一个序号，交给转换器按 unit key 取并集、同 key 后来源覆盖。
	/// </summary>
	[Fact]
	public async Task 多个来源的同名patch按顺序连续重新编号()
	{
		var target = NewDir("target");

		var outcome = await Assemble(
			target, [Source("本体"), Source("翅膀"), Source("鞋子")],
			new SectionalSourceSelection("8d3a01f4a44a3aa3", "护甲"), null, Keys());

		Assert.True(outcome.Ok);
		Assert.True(File.Exists(Path.Combine(target, $"{Archive}.patch_0")));
		Assert.True(File.Exists(Path.Combine(target, $"{Archive}.patch_1")));
		Assert.True(File.Exists(Path.Combine(target, $"{Archive}.patch_2")));

		// 依据文件记的必须是**新**名字，转换器靠它把 patch 归到来源上（材质组按来源命名）。
		var document = await SectionalSourceFile.TryReadAsync(target);
		Assert.NotNull(document);
		Assert.Equal([$"{Archive}.patch_0"], document!.Sources[0].PatchFiles);
		Assert.Equal([$"{Archive}.patch_1"], document.Sources[1].PatchFiles);
		Assert.Equal([$"{Archive}.patch_2"], document.Sources[2].PatchFiles);
	}

	[Fact]
	public async Task 来源目录不存在时跳过并说明_不整体失败()
	{
		var target = NewDir("target");
		var missing = new SectionalSourceCandidate(Guid.NewGuid().ToString("N"), "已删除来源", Path.Combine(_root, "nope"));

		var outcome = await Assemble(
			target, [Source("可用来源"), missing],
			new SectionalSourceSelection("8d3a01f4a44a3aa3", "护甲"), null, Keys());

		Assert.True(outcome.Ok);
		Assert.Contains(outcome.Notes, note => note.Contains("目录不存在", StringComparison.Ordinal));
	}

	[Fact]
	public async Task 来源根上没有patch时整体失败()
	{
		// 来源不是 legacy（patch 在 payload 里）时，什么也收集不到 —— 必须挡在导入前。
		var target = NewDir("target");
		var empty = NewDir("src-empty");

		var outcome = await Assemble(
			target, [new SectionalSourceCandidate(Guid.NewGuid().ToString("N"), "转换产物", empty)],
			new SectionalSourceSelection("8d3a01f4a44a3aa3", "护甲"), null, Keys());

		Assert.False(outcome.Ok);
		Assert.Contains("不会有任何内容", outcome.Error!, StringComparison.Ordinal);
	}

	[Fact]
	public async Task 护甲与头盔都没选时拒绝()
	{
		var target = NewDir("target");

		var outcome = await Assemble(target, [Source("主Mod")], null, null, Keys());

		Assert.False(outcome.Ok);
		Assert.Contains("都没有选", outcome.Error!, StringComparison.Ordinal);
	}

	[Fact]
	public async Task 白名单没有unit时拒绝()
	{
		var target = NewDir("target");

		var outcome = await Assemble(
			target, [Source("主Mod")],
			new SectionalSourceSelection("8d3a01f4a44a3aa3", "护甲"), null,
			new SectionalSourceAssetKeys([], [], []));

		Assert.False(outcome.Ok);
		Assert.Contains("没有任何 unit", outcome.Error!, StringComparison.Ordinal);
	}

	/// <summary>
	/// 来源 patch 常带只读属性（导入 zip 时带来的），<c>File.Copy</c> 会把它带给目标，
	/// 之后覆盖同名文件就会 Access denied（线上就是这么失败的）。这里锁住"拷完必须可写"。
	/// </summary>
	[Fact]
	public async Task 来源是只读文件时拷进条目后必须可写()
	{
		var target = NewDir("target");
		var source = Source("只读来源");
		File.SetAttributes(Path.Combine(source.Directory, $"{Archive}.patch_0"), FileAttributes.ReadOnly);

		var outcome = await Assemble(
			target, [source],
			new SectionalSourceSelection("8d3a01f4a44a3aa3", "护甲"), null, Keys());

		Assert.True(outcome.Ok);
		var copied = new FileInfo(Path.Combine(target, $"{Archive}.patch_0"));
		Assert.True(copied.Exists);
		Assert.False(copied.IsReadOnly, "复制出来的来源 patch 不该继承只读属性。");
	}
}
