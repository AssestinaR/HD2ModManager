using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 把 CLI 的失败翻译成"能读的一句话"。
/// <para>
/// 为什么值得单独钉：CLI 的失败有两种写法（整体失败看顶层 <c>error.message</c>、
/// 单个 VPK 失败看 <c>failures[]</c>），而 stderr 上只有进度行。两种都会被
/// <see cref="Hd2ModProcess.Explain"/> 判成"stderr 为空"→ 于是它把**整份 JSON** 拼进消息。
/// 2026-11-09 实测那一条是 1 046 个字符，用户看到的是
/// <c>重新转换失败（hd2mod 退出码 2）：{ "ok": false, …</c>。
/// </para>
/// </summary>
public sealed class Hd2ModProcessTests
{
	/// <summary>真实抓到的那份报告（`牛牛rebone替换路易斯.vpk`，21 张 VTF 7.1 全部读不出）。</summary>
	private const string FailuresJson = """
	{
	  "ok": false,
	  "partial": false,
	  "warnings": [],
	  "queue": ["c:\\users\\administrator\\downloads\\牛牛rebone替换路易斯.vpk"],
	  "patches": [],
	  "failures": [
	    { "vpk": "c:\\a.vpk", "error": "7 处失败 —— OverflowException：Arithmetic operation resulted in an overflow. @ ThrowHelpers.ThrowOverflowException" }
	  ]
	}
	""";

	[Fact]
	public void 单个Vpk失败时取出failures里的原因而不是整份JSON()
	{
		var message = Hd2ModProcess.DescribeFailure(new Hd2ModProcess.Result(2, FailuresJson, string.Empty), "重新转换");

		Assert.Contains("重新转换失败", message);
		Assert.Contains("OverflowException", message);
		// 关键：不是"把 JSON 贴出来"。
		Assert.DoesNotContain("\"ok\": false", message);
		Assert.DoesNotContain("\"patches\"", message);
		Assert.True(message.Length < 500, $"文案 {message.Length} 字，又把原文整段拼进来了");
	}

	[Fact]
	public void 整体失败时取顶层error的message()
	{
		const string json = """
		{ "ok": false, "error": { "code": "PrerequisitesMissing", "message": "转换缺少 2 项前置条件（一次列全，补齐后重跑同一条命令）：…" } }
		""";

		var message = Hd2ModProcess.DescribeFailure(new Hd2ModProcess.Result(2, json, string.Empty), "规划 VPK");

		Assert.Equal("规划 VPK失败：转换缺少 2 项前置条件（一次列全，补齐后重跑同一条命令）：…", message);
	}

	[Fact]
	public void 多个Vpk失败时最多列三条并说明还有多少()
	{
		var failures = string.Join(',', Enumerable.Range(1, 5)
			.Select(index => $"{{\"vpk\":\"c:\\\\{index}.vpk\",\"error\":\"第 {index} 个失败\"}}"));
		var json = $"{{\"ok\":false,\"patches\":[],\"failures\":[{failures}]}}";

		var message = Hd2ModProcess.DescribeFailure(new Hd2ModProcess.Result(2, json, string.Empty), "重新转换");

		Assert.Contains("第 1 个失败", message);
		Assert.Contains("第 3 个失败", message);
		Assert.DoesNotContain("第 4 个失败", message);
		Assert.Contains("共 5 个 VPK 失败", message);
	}

	[Fact]
	public void 不是JSON时退回stderr原文且超长要截断()
	{
		var stderr = new string('x', 1200);
		var message = Hd2ModProcess.DescribeFailure(new Hd2ModProcess.Result(1, "{}", stderr), "重新转换");

		Assert.Contains("退出码 1", message);
		Assert.Contains("已截断", message);
		Assert.True(message.Length < 600, $"文案 {message.Length} 字");
	}

	/// <summary>stderr 与 stdout 都空（进程直接崩）：必须仍给出一句带退出码的话。</summary>
	[Fact]
	public void 两段输出都空时仍给出退出码()
	{
		var message = Hd2ModProcess.DescribeFailure(new Hd2ModProcess.Result(3, string.Empty, string.Empty), "重新转换");

		Assert.Equal("重新转换失败（hd2mod 退出码 3）：", message);
	}
}
