using HD2SourceMigration.VpkConversion;

namespace HD2SourceMigration.Tests;

/// <summary>
/// 「这张贴图读不出来」的判据（<see cref="SourceMaterialConverter.IsTextureReadFailure"/>）。
/// <para>
/// 这条判据决定了一件事的性质：**某一张贴图坏掉**时，是该材质降级（少一项），
/// 还是整个 VPK 转换失败（`patches: []`）。2026-11-09 的两个包（21 张 / 10 张 VTF 7.1）
/// 之所以"整包失败"，就是因为十处 catch 各自写着同样的三个异常类型、**都漏了
/// <c>OverflowException</c>** —— 头部字段被当成层数乘出 int 范围时抛的正是它。
/// </para>
/// <para>
/// 这里同时钉住"不许扩大"：我们自己的 bug（空引用、越界、内存不足）绝不能被当成
/// "贴图坏了"吞掉，那会把一个必须崩的错误变成一张静默的纯色贴图。
/// </para>
/// </summary>
public sealed class TextureReadFailureTests
{
	[Theory]
	[InlineData(typeof(InvalidDataException))]
	[InlineData(typeof(IOException))]
	[InlineData(typeof(InvalidOperationException))]
	[InlineData(typeof(OverflowException))]          // ← 7.1 那次的真身
	[InlineData(typeof(NotSupportedException))]
	[InlineData(typeof(ArgumentException))]
	public void 贴图层面的失败都算可降级(Type exceptionType)
	{
		var exception = (Exception)Activator.CreateInstance(exceptionType)!;

		Assert.True(SourceMaterialConverter.IsTextureReadFailure(exception), exceptionType.Name);
	}

	[Theory]
	[InlineData(typeof(NullReferenceException))]
	[InlineData(typeof(IndexOutOfRangeException))]
	[InlineData(typeof(OutOfMemoryException))]
	[InlineData(typeof(OperationCanceledException))]
	// 注意 `ObjectDisposedException` **不在**这一组：它继承 `InvalidOperationException`，
	// 而后者从第一版起就在判据里（解码器/流被提前释放属于"这张图用不了"）。
	// 想改这条语义的话，改的是 `InvalidOperationException` 那一条，不是这里加一行。
	public void 我们自己的bug不许被吞掉(Type exceptionType)
	{
		var exception = (Exception)Activator.CreateInstance(exceptionType)!;

		Assert.False(SourceMaterialConverter.IsTextureReadFailure(exception), exceptionType.Name);
	}

	/// <summary>
	/// <see cref="InvalidDataException"/> 是"头部坏了"的主出口；它必须**总是**可降级，
	/// 否则 <c>VtfFile</c> 里那条带版本号的报错就只能上抛成整包失败。
	/// </summary>
	[Fact]
	public void 头部损坏的主出口可降级()
	{
		Assert.True(SourceMaterialConverter.IsTextureReadFailure(
			new InvalidDataException("VTF 图像块越过文件末尾：v7.1 header=64 256×128 DXT5 mips=9 frames=1 depth=1 需要 43856 字节，实际只有 40000。")));
	}
}
