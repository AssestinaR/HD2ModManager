using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 转换器 → 管理器的进度行。
/// <para>
/// 这条协议两端在两个程序集里（写的一方在 <c>hd2mod.exe</c>，读的一方在管理器），
/// 所以"格式漂移"不会有编译错误——症状是进度条不动，而那看起来像卡死，不像 bug。
/// 这里钉住三件事：能原样往返、文案里的空格不被吃掉、非进度行一律不算。
/// </para>
/// </summary>
public sealed class VpkProgressLineTests
{
	[Theory]
	[InlineData("queue", 0, 5, "正在转换 1/5：a.vpk")]
	[InlineData("materials", 24, 57, "材质 24/57")]
	[InlineData("units", 7, 7, "Unit 7/7")]
	public void 往返一致(string phase, int done, int total, string text)
	{
		var line = VpkProgressLine.Format(phase, done, total, text);

		Assert.True(VpkProgressLine.TryParse(line, out var parsedPhase, out var parsedDone, out var parsedTotal, out var parsedText));
		Assert.Equal(phase, parsedPhase);
		Assert.Equal(done, parsedDone);
		Assert.Equal(total, parsedTotal);
		Assert.Equal(text, parsedText);
	}

	/// <summary>文案里有空格（"正在转换 2/5：xxx.vpk"）——只切前三刀，后面整段都是文案。</summary>
	[Fact]
	public void 文案里的空格原样保留()
	{
		var line = VpkProgressLine.Format("queue", 1, 5, "正在转换 2/5：[huahua] 影鸢女武神 替换bill.vpk");

		Assert.True(VpkProgressLine.TryParse(line, out _, out _, out _, out var text));
		Assert.Equal("正在转换 2/5：[huahua] 影鸢女武神 替换bill.vpk", text);
	}

	/// <summary>换行会把"一行一条记录"这个前提打破，所以压成空格。</summary>
	[Fact]
	public void 文案里的换行被压平()
	{
		var line = VpkProgressLine.Format("materials", 1, 2, "第一行\r\n第二行");

		Assert.DoesNotContain('\n', line);
		Assert.DoesNotContain('\r', line);
		Assert.True(VpkProgressLine.TryParse(line, out _, out _, out _, out var text));
		Assert.Equal("第一行  第二行", text);
	}

	[Theory]
	[InlineData("⚠️ 解析集合里这个包打不开：x.vpk")]
	[InlineData("#progressx queue 1 2 文案")]
	[InlineData("#progress queue 1")]
	[InlineData("#progress queue a 2 文案")]
	[InlineData("#progress  queue 1 2 文案")]
	[InlineData("")]
	public void 不是进度行就不算(string line)
		=> Assert.False(VpkProgressLine.TryParse(line, out _, out _, out _, out _));

	[Fact]
	public void 空行不算()
		=> Assert.False(VpkProgressLine.TryParse(null, out _, out _, out _, out _));

	/// <summary>没有文案时退回阶段键，而不是空字符串——界面上宁可显示 <c>materials</c> 也别显示空白。</summary>
	[Fact]
	public void 缺文案时退回阶段键()
	{
		Assert.True(VpkProgressLine.TryParse("#progress units 3 7", out var phase, out var done, out var total, out var text));
		Assert.Equal("units", phase);
		Assert.Equal(3, done);
		Assert.Equal(7, total);
		Assert.Equal("units", text);
	}
}
