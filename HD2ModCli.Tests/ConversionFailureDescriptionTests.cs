using HD2ModCli;
using Xunit;

namespace HD2ModCli.Tests;

/// <summary>
/// 转换失败的**可定位性**。
/// <para>
/// 装配器里的材质转换跑在 <c>Parallel.For</c> 里，任何异常都会被包成
/// <see cref="AggregateException"/>。旧代码直接写 <c>exception.Message</c>，于是用户看到的只有
/// <c>One or more errors occurred. (Arithmetic operation resulted in an overflow.)</c>
/// —— 没有类型、没有位置，整批只报"转换 0 个"（实测 2026-11-04 的
/// <c>牛牛rebone替换路易斯.vpk</c>）。这组用例钉住"必须摊平并说出类型与位置"。
/// </para>
/// </summary>
public sealed class ConversionFailureDescriptionTests
{
	[Fact]
	public void AggregateIsFlattenedWithExceptionType()
	{
		var aggregate = new AggregateException(
			new OverflowException("Arithmetic operation resulted in an overflow."),
			new OverflowException("Arithmetic operation resulted in an overflow."));

		var text = CliApplication.DescribeFailure(aggregate);

		Assert.Contains("OverflowException", text);
		Assert.Contains("2 处失败", text);
		// 同一句话重复两次不该在报告里出现两遍。
		Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "Arithmetic operation"));
	}

	[Fact]
	public void SingleInnerExceptionIsNotCountedAsMany()
	{
		var aggregate = new AggregateException(new InvalidDataException("贴图读不出来"));

		var text = CliApplication.DescribeFailure(aggregate);

		Assert.StartsWith("InvalidDataException：贴图读不出来", text);
		Assert.DoesNotContain("处失败", text);
	}

	[Fact]
	public void PlainExceptionKeepsItsType()
	{
		var text = CliApplication.DescribeFailure(new InvalidDataException("骨骼数量异常"));

		Assert.Contains("InvalidDataException", text);
		Assert.Contains("骨骼数量异常", text);
	}

	[Fact]
	public void NestedAggregatesAreFlattenedToo()
	{
		var inner = new AggregateException(new OverflowException("深一层的溢出"));
		var outer = new AggregateException("外层", inner);

		var text = CliApplication.DescribeFailure(outer);

		Assert.Contains("OverflowException", text);
		Assert.Contains("深一层的溢出", text);
	}
}
