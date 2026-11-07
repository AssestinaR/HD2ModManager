using System.Text;
using HD2SourceModel;
using HD2SourceMigration.VpkConversion;

namespace HD2SourceMigration.Tests;

/// <summary>
/// 眼球（<c>EyeRefract</c>）这条线：颜色从 <c>$iris</c> 取，UV 按位置重算。
/// <para>
/// <b>为什么这两件事必须一起钉住。</b>只接颜色不够：这份模组的眼球网格 28 个顶点 UV
/// 全是常量 <c>(0,1)</c>，直接采样只会得到同一个像素——上机是<b>一整颗纯色的球</b>，
/// 看着"有材质"但完全不是眼睛。两半都写错时的现象都是"眼睛不对"，而原因完全不同，
/// 所以这里分成两组断言。
/// </para>
/// </summary>
public sealed class EyeUvTests
{
	// ── ① VMT 侧：$iris 是 EyeRefract 的颜色来源 ────────────────────────────

	[Fact]
	public void IrisTextureIsReadFromTheEyeRefractVmt()
	{
		var vmt = VmtDocument.Parse(Encoding.UTF8.GetBytes("""
			"EyeRefract"
			{
				"$Iris"               "models/survivors/suimaid/eye"
				"$CorneaTexture"      "dev/flat_normal"
				"$AmbientOcclColor"   "[0.1 0.1 0.1]"
			}
			"""));

		Assert.Equal("EyeRefract", vmt.Shader);
		// 眼球材质**没有 $basetexture** —— 这正是它以前被判"没有底色"的原因。
		Assert.Null(vmt.BaseTexture);
		Assert.Equal("models/survivors/suimaid/eye", vmt.IrisTexture);
	}

	// ── ② 贴图侧：图集里"画着眼睛的那块矩形" ────────────────────────────────

	[Fact]
	public void ContentRectFindsTheEyeInsideAFlatAtlas()
	{
		// 64×64 平色底 + 中间 32×32 的"眼睛"（与原版 1024² 图集同构）。
		var rgba = Solid(64, 64, 200, 200, 200);
		for (var y = 16; y < 48; y++)
		for (var x = 16; x < 48; x++)
			Pixel(rgba, 64, x, y, 30, 30, 30);

		var rect = EyeUv.ContentRect(rgba, 64, 64);

		Assert.Equal(0.25, rect.U0, 3);
		Assert.Equal(0.25, rect.V0, 3);   // V=0 在图像顶部，与像素行同向
		Assert.Equal(0.75, rect.U1, 3);
		Assert.Equal(0.75, rect.V1, 3);
	}

	[Fact]
	public void ContentRectIgnoresCompressionNoise()
	{
		// DXT 的块噪声在 ±6 上下；阈值 24 必须把它当背景，否则内容区会涨到整张图。
		var rgba = Solid(32, 32, 200, 200, 200);
		for (var y = 0; y < 32; y++)
		for (var x = 0; x < 32; x++)
		{
			var jitter = ((x + y) % 3) == 0 ? 6 : -6;
			Pixel(rgba, 32, x, y, (byte)(200 + jitter), (byte)(200 - jitter), 200);
		}

		var rect = EyeUv.ContentRect(rgba, 32, 32);

		// 全是噪声 ⇒ "没有可辨认的内容" ⇒ 按整张图处理（不解也安全）。
		Assert.Equal((0d, 0d, 1d, 1d), rect);
	}

	[Fact]
	public void ContentRectReturnsTheWholeImageWhenThereIsNoPadding()
	{
		// 一张"整张就是眼睛"的图：每个像素都在变。
		var rgba = new byte[32 * 32 * 4];
		for (var y = 0; y < 32; y++)
		for (var x = 0; x < 32; x++)
			Pixel(rgba, 32, x, y, (byte)(x * 8), (byte)(y * 8), 128);

		var rect = EyeUv.ContentRect(rgba, 32, 32);

		Assert.Equal((0d, 0d, 1d, 1d), rect);
	}

	// ── ③ 几何侧：UV 重建 ─────────────────────────────────────────────────

	[Fact]
	public void ReprojectMapsModelYToUAndModelZToV()
	{
		// 一个 2×2 的四边面，UV 全是 (0,1)（= 原版眼球的常量 UV）。
		var geometry = Quad(width: 2, height: 2, u: 0f, v: 1f);
		var source = new EyeColorSource("eye", 0.25, 0.25, 0.75, 0.75);

		var (projected, notes) = EyeUv.Reproject(geometry, ["eye_ball"], _ => source);

		// 内容区是 0.25..0.75：四个角正好落在内容区四角。
		// 顶点顺序：0=(-Y,-Z) 1=(+Y,+Z) 2=(+Y,-Z) 3=(-Y,+Z)。
		AssertUv(projected, geometry, vertexIndex: 1, u: 0.75, v: 0.25);   // Y=+1 → U 右；Z=+1 → V 小（图像顶部）
		AssertUv(projected, geometry, vertexIndex: 0, u: 0.25, v: 0.75);   // Y=-1 → U 左；Z=-1 → V 大
		AssertUv(projected, geometry, vertexIndex: 2, u: 0.75, v: 0.75);
		AssertUv(projected, geometry, vertexIndex: 3, u: 0.25, v: 0.25);
		// 两条说明：先报"这个眼球用了哪三个轴"（横/竖/朝前），再报"UV 已重算"。
		Assert.Contains(notes, note => note.Contains("横=Y、竖=Z、朝前=X", StringComparison.Ordinal));
		Assert.Contains(notes, note => note.Contains("眼球 UV 已按 $iris 的平面投影重算", StringComparison.Ordinal));
	}

	[Fact]
	public void ReprojectKeepsTheEyeUndistortedOnANonSquareEyeball()
	{
		// 4 宽 × 2 高的眼球（真实眼球总是"宽 > 高"，这个比例接近 3798231335 的 1.69:1.07）。
		var geometry = Quad(width: 4, height: 2, u: 0f, v: 1f);
		var source = new EyeColorSource("eye", 0.25, 0.25, 0.75, 0.75);

		var (projected, _) = EyeUv.Reproject(geometry, ["eye_ball"], _ => source);

		// 比例按"整只眼睛装得进眼球"取：宽 4 需要 0.125/单位、高 2 需要 0.25/单位 ⇒ 取 0.25。
		// 于是 4 个单位摊 1.0、2 个单位摊 0.5 —— **两轴同一个比例**。
		// 旧实现按两个轴各撑满一次，会得到 1.0 / 0.5 之外的另一个组合（比例不一致 ⇒ 眼睛变形）。
		var uSpan = Math.Abs(projected.Vertices[1].U - projected.Vertices[0].U);
		var vSpan = Math.Abs(projected.Vertices[1].V - projected.Vertices[0].V);
		Assert.Equal(1.0, uSpan, 3);
		Assert.Equal(0.5, vSpan, 3);

		// 关键不变量：UV 的宽高比 = 模型的宽高比（4:2）⇒ 眼睛没有被拉长/压扁。
		Assert.Equal(4.0 / 2.0, uSpan / vSpan, 6);
	}

	[Fact]
	public void DilationShrinksTheSampledRegionSoThePupilGrows()
	{
		var geometry = Quad(width: 4, height: 2, u: 0f, v: 1f);
		var plain = EyeUv.Reproject(geometry, ["eye_ball"],
			_ => new EyeColorSource("eye", 0.25, 0.25, 0.75, 0.75)).Geometry;
		var dilated = EyeUv.Reproject(geometry, ["eye_ball"],
			_ => new EyeColorSource("eye", 0.25, 0.25, 0.75, 0.75, 0.4)).Geometry;

		var plainSpan = Math.Abs(plain.Vertices[1].V - plain.Vertices[0].V);
		var dilatedSpan = Math.Abs(dilated.Vertices[1].V - dilated.Vertices[0].V);

		// `$dilation 0.4` ⇒ 采样半径 ×(1/1.4)：同一段模型覆盖的贴图变小 ⇒ 虹膜/瞳孔放大。
		Assert.True(dilatedSpan < plainSpan);
		Assert.Equal(plainSpan / 1.4, dilatedSpan, 3);
	}
	[Fact]
	public void ReprojectLeavesMaterialsThatAreNotEyesAlone()
	{
		var geometry = Quad(2, 2, u: 0.11f, v: 0.22f);

		var (projected, notes) = EyeUv.Reproject(geometry, ["head"], _ => null);

		Assert.Empty(notes);
		Assert.Same(geometry, projected);   // 没有眼球时**原样返回**，一个顶点都不复制
	}

	[Fact]
	public void ReprojectSkipsVerticesSharedWithOtherMaterials()
	{
		// 四个顶点全部同时被"眼球"和"头"用到：UV 是逐顶点的，改了会毁掉头部的贴图，
		// 所以一个都不许改，并且要留一条说明。
		var geometry = Quad(2, 2, u: 0f, v: 1f);
		var shared = geometry with
		{
			Triangles =
			[
				new SourceTriangle(0, 1, 2, 0),
				new SourceTriangle(0, 2, 3, 1),
			],
		};

		var (projected, notes) = EyeUv.Reproject(
			shared, ["head", "eye_ball"], name => name == "eye_ball"
				? new EyeColorSource("eye", 0.25, 0.25, 0.75, 0.75)
				: null);

		// 顶点 0/1/2 与"头"共用 → 一律不动；只有 3 号是眼球独占的。
		Assert.Equal(1f, projected.Vertices[0].V);
		Assert.Equal(1f, projected.Vertices[1].V);
		Assert.Equal(1f, projected.Vertices[2].V);
		Assert.Equal(0.25, projected.Vertices[3].V, 3);
		Assert.Contains(notes, note => note.Contains("同时被别的材质用到", StringComparison.Ordinal));
	}

	/// <summary>
	/// 十二万个三角形 + 一个眼球材质：<c>Reproject</c> 必须跑得完。
	/// <para>
	/// <b>钉的是"循环体里 stackalloc"这个已经复发过一次的坑。</b>那个写法每一轮都再占一份栈，
	/// 直到方法返回才回收：12 万三角形 × 16 字节 ≈ 2 MB &gt; 默认 1 MB 栈，进程直接
	/// 0xC00000FD 消失，连异常都抓不到 —— 所以这条用例失败时不是断言失败，是整个测试宿主没了。
	/// </para>
	/// <para>
	/// 2026-10-28 <c>RetargetedMeshBuilder</c> 撞过一次（19 万三角形），
	/// 2026-11-04 写 <c>EyeUv.Reproject</c> 时又把同一个写法写了回来，
	/// 2026-11-07 由「可露凯-速度之星 埃利斯」这份样本第二次撞出来。
	/// </para>
	/// </summary>
	[Fact]
	public void ReprojectSurvivesAModelWithHundredsOfThousandsOfTriangles()
	{
		const int triangleCount = 120_000;
		SourceVertex At(double y, double z) => new(
			new Vec3(0, y, z),
			new Vec3(1, 0, 0),
			0f,
			1f,
			new SourceVertexWeight(0, 1),
			new SourceVertexWeight(0, 0),
			new SourceVertexWeight(0, 0));

		var triangles = new SourceTriangle[triangleCount];
		for (var i = 0; i < triangleCount; i++)
			triangles[i] = new SourceTriangle(0, 1, 2, 0);
		var geometry = new SourceGeometry([At(-1, -1), At(1, 1), At(1, -1)], triangles, []);

		var (projected, notes) = EyeUv.Reproject(geometry, ["eye_ball"],
			_ => new EyeColorSource("eye", 0.25, 0.25, 0.75, 0.75));

		// 三个顶点都只被眼球这一个材质用到 ⇒ 都会被重算（1=(+Y,+Z) → U 右、V 小）。
		Assert.Equal(0.75, projected.Vertices[1].U, 3);
		Assert.Equal(0.25, projected.Vertices[1].V, 3);
		Assert.Contains(notes, note => note.Contains("眼球 UV 已按 $iris 的平面投影重算", StringComparison.Ordinal));
	}

	private static void AssertUv(SourceGeometry projected, SourceGeometry original, int vertexIndex, double u, double v)
	{
		Assert.NotSame(original, projected);
		Assert.Equal(u, projected.Vertices[vertexIndex].U, 3);
		Assert.Equal(v, projected.Vertices[vertexIndex].V, 3);
	}

	/// <summary>
	/// 一个朝 +X 的矩形面（宽 = <paramref name="width"/> 沿 Y、高 = <paramref name="height"/> 沿 Z），
	/// 所有顶点 UV 都写成同一个值。顶点顺序：0=(-Y,-Z) 1=(+Y,+Z) 2=(+Y,-Z) 3=(-Y,+Z)。
	/// </summary>
	private static SourceGeometry Quad(double width, double height, float u, float v)
	{
		var halfWidth = width / 2;
		var halfHeight = height / 2;
		SourceVertex At(double y, double z) => new(
			new Vec3(0, y, z),
			new Vec3(1, 0, 0),
			u,
			v,
			new SourceVertexWeight(0, 1),
			new SourceVertexWeight(0, 0),
			new SourceVertexWeight(0, 0));

		return new SourceGeometry(
			[At(-halfWidth, -halfHeight), At(halfWidth, halfHeight), At(halfWidth, -halfHeight), At(-halfWidth, halfHeight)],
			[new SourceTriangle(0, 1, 2, 0), new SourceTriangle(0, 3, 1, 0)],
			[]);
	}

	private static byte[] Solid(int width, int height, byte red, byte green, byte blue)
	{
		var rgba = new byte[width * height * 4];
		for (var y = 0; y < height; y++)
		for (var x = 0; x < width; x++)
			Pixel(rgba, width, x, y, red, green, blue);
		return rgba;
	}

	private static void Pixel(byte[] rgba, int width, int x, int y, byte red, byte green, byte blue)
	{
		var offset = (y * width + x) * 4;
		rgba[offset] = red;
		rgba[offset + 1] = green;
		rgba[offset + 2] = blue;
		rgba[offset + 3] = 255;
	}
}
