using HD2SourceModel;

namespace HD2SourceMigration.VpkConversion;

/// <summary>
/// 眼球材质（<c>EyeRefract</c> / <c>Eyes</c>）的颜色图，以及它在 UV 上真正画着眼睛的那块矩形。
/// <para>
/// <b>为什么必须单独记这块矩形。</b>眼球颜色图（<c>$iris</c>）几乎都是<b>图集</b>：
/// 一张 1024² 的图里只有中间一小块画着眼睛，四周是平色的眼白/填充。而
/// <c>EyeRefract</c> 的采样坐标<b>不是模型 UV</b>——Source 的 <c>eyes.vsh</c> 写得很明确：
/// </para>
/// <para>
/// <c>oT1 = (dot($IrisU, worldPos), dot($IrisV, worldPos))</c>，注释也写着
/// 「Texture 1 is a planar projection used for the iris」（见 Valve <c>source-sdk-2013</c>
/// 的 <c>materialsystem/stdshaders/eyes.vsh</c>）。投影向量由引擎按眼球位置与
/// <c>$EyeballRadius</c> 每帧给出，模型自己的 UV 完全不参与——实测这份模组的眼球网格
/// 28 个顶点 UV <b>全是 (0,1)</b>。
/// </para>
/// <para>
/// 直接拿模型 UV 去采这张图 ⇒ 整个眼球采样同一个像素 ⇒ 一块纯色。所以要把眼睛那块矩形
/// 重新摊到眼球上：<b>U 跟模型的 Y（左右）、V 跟 Z（上下）</b>，与源侧那条平面投影同构。
/// </para>
/// </summary>
/// <param name="Texture">颜色图名（<c>$iris</c> 的值，已去掉目录与扩展名）。</param>
/// <param name="U0">内容区左边界（0..1）。</param>
/// <param name="V0">内容区上边界（0..1，<b>V=0 是图像顶部</b>）。</param>
/// <param name="U1">内容区右边界。</param>
/// <param name="V1">内容区下边界。</param>
/// <param name="Dilation">
/// <c>$dilation</c>（0..1，0 = 不放大）。源侧用它把虹膜**绕瞳孔放大**。
/// </param>
/// <param name="PupilU">
/// 瞳孔中心（UV）。<c>NaN</c> = 没测出来，退回内容区几何中心。
/// <para>
/// <b>为什么要单独测。</b>瞳孔不在内容区正中时，把"内容区中心"对到眼球中心会让瞳孔
/// 整体偏一侧 —— 两眼用同一张贴图，于是两眼都朝同一个方向偏（实测 3798231335 是
/// 斗鸡眼，瞳孔偏内侧约 12% 的内容区宽度）。以瞳孔为中心才是"看向前方"。
/// </para>
/// </param>
/// <param name="PupilV">瞳孔中心（UV，V=0 在图像顶部）。</param>
public sealed record EyeColorSource(
	string Texture,
	double U0,
	double V0,
	double U1,
	double V1,
	double Dilation = 0,
	double PupilU = double.NaN,
	double PupilV = double.NaN)
{
	/// <summary>投影中心 U：**瞳孔**优先，测不出来才用内容区几何中心。</summary>
	public double CenterU => double.IsNaN(PupilU) ? (U0 + U1) / 2 : PupilU;

	/// <summary>投影中心 V：同 <see cref="CenterU"/>。</summary>
	public double CenterV => double.IsNaN(PupilV) ? (V0 + V1) / 2 : PupilV;

	/// <summary>瞳孔偏离内容区几何中心多少（UV）。用来判断"这只眼睛画得正不正"。</summary>
	public (double U, double V) PupilOffset => (CenterU - ((U0 + U1) / 2), CenterV - ((V0 + V1) / 2));

	/// <summary>瞳孔偏了多少（占内容区的比例），报告里用。</summary>
	public (double U, double V) PupilOffsetRatio =>
		(PupilOffset.U / Math.Max(1e-6, (U1 - U0) / 2), PupilOffset.V / Math.Max(1e-6, (V1 - V0) / 2));

	/// <summary>
	/// 采样半径的缩放：<c>1 / (1 + $dilation)</c>。
	/// <para>
	/// 采样范围越小 = 屏幕上的虹膜/瞳孔越大。0.4 → 0.71（放大约 1.4 倍）。
	/// 源侧那条曲线没有公开的精确公式（着色器里是逐个像素算的），这里取的是
	/// "0 不动、1 放大一倍"的线性近似——能对上方向与量级，且可用 <c>HD2_EYE_DILATION</c> 调。
	/// </para>
	/// </summary>
	public double DilationZoom => 1.0 / (1.0 + Math.Clamp(Dilation * DilationGain, 0, 1));

	/// <summary>内容区半宽（UV）。= 1 表示整张图都是眼睛，没有填充边。</summary>
	public double HalfU => Math.Max(1e-6, (U1 - U0) / 2);

	/// <summary>内容区半高（UV）。</summary>
	public double HalfV => Math.Max(1e-6, (V1 - V0) / 2);

	/// <summary>
	/// <c>$dilation</c> 的倍率（环境变量 <c>HD2_EYE_DILATION</c>，默认 1.0）。
	/// <para>
	/// 留着这个旋钮是因为源侧的瞳孔放大曲线没有公开公式：0.4 这种常见取值到底该放大多少，
	/// 只能实机对比。改成 0 就等于忽略 <c>$dilation</c>（回到上一版的行为）。
	/// </para>
	/// </summary>
	public static double DilationGain { get; } =
		double.TryParse(Environment.GetEnvironmentVariable("HD2_EYE_DILATION"),
			System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var gain)
			&& double.IsFinite(gain) && gain >= 0
				? Math.Clamp(gain, 0, 3.0)
				: 1.0;
	/// <summary>这张图是不是"整张就是一只眼睛"（没有可识别的填充边）。</summary>
	public bool IsWholeTexture => U0 <= 1e-6 && V0 <= 1e-6 && U1 >= 1 - 1e-6 && V1 >= 1 - 1e-6;

	public string Describe() =>
		$"{Texture}（眼睛画在 U[{U0:F3},{U1:F3}] × V[{V0:F3},{V1:F3}]，面积 {((U1 - U0) * (V1 - V0)):P0}）";
}

/// <summary>
/// 眼球投影用的"右"和"上"两个方向（单位向量），**由骨架推出**。
/// <para>
/// 为什么不用模型坐标轴：实测两套模型都把"左右"放在 X 上，但"朝前"一个是 Y、
/// 另一个也几乎是 Y（Y/Z 只差 3%），而"眼球最薄的那一维 = 朝前"这种判据在后者上会翻车
/// （把 X 当成上下，整只眼睛转 90°）。骨架是稳的：右 = 左锁骨 − 右锁骨、上 = 头 − 髋。
/// </para>
/// </summary>
public sealed record EyeFrame(Vec3 Right, Vec3 Up)
{
	/// <summary>从骨骼表推"右/上"；推不出来（缺关键骨）时返回 <c>null</c>，由调用方退回形状判据。</summary>
	public static EyeFrame? FromBones(IReadOnlyList<SourceBone>? bones)
	{
		if (bones is null || bones.Count == 0)
			return null;

		var head = Find(bones, "Head1") ?? Find(bones, "Head");
		var pelvis = Find(bones, "Pelvis") ?? Find(bones, "Hips");
		if (head is null || pelvis is null)
			return null;

		var up = (head.WorldOrigin - pelvis.WorldOrigin);
		if (up.Length <= 1e-6)
			return null;
		up = up.Normalized();

		var left = Find(bones, "L_Clavicle") ?? Find(bones, "L_UpperArm");
		var rightBone = Find(bones, "R_Clavicle") ?? Find(bones, "R_UpperArm");
		Vec3 right;
		if (left is not null && rightBone is not null && (left.WorldOrigin - rightBone.WorldOrigin).Length > 1e-6)
		{
			right = (left.WorldOrigin - rightBone.WorldOrigin).Normalized();
		}
		else
		{
			// 没有锁骨/肩：用"上"叉世界前/后兜底（Source 惯例 X 朝前）。
			right = Vec3.Cross(new Vec3(1, 0, 0), up);
			if (right.Length <= 1e-6)
				right = Vec3.Cross(new Vec3(0, 1, 0), up);
			if (right.Length <= 1e-6)
				return null;
			right = right.Normalized();
		}

		return new EyeFrame(right, up);
	}

	private static SourceBone? Find(IReadOnlyList<SourceBone> bones, string fragment)
		=> bones.FirstOrDefault(bone =>
			bone.DisplayName.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
/// <summary>
/// 眼球 UV 重建：找出颜色图里"画着眼睛的那块矩形"，并把眼球网格的 UV 按
/// 模型的 Y/Z 重新投影上去。
/// <para>
/// <b>这是着色器语义，不是针对某个模组的补丁。</b>凡是 <c>$basetexture</c> 缺失、
/// 颜色写在 <c>$iris</c> 里的材质都走这条路（<see cref="SourceMaterialConverter"/> 的
/// <c>ShaderColorKeys</c>）；判据是"<b>VMT 的键 + 贴图的实际内容</b>"，与模组名、
/// 材质名、文件名都无关。
/// </para>
/// </summary>
public static class EyeUv
{
	/// <summary>判定"这个像素不是填充色"的每通道阈值（0..255）。</summary>
	/// <remarks>
	/// 24 是实测取的值：填充区在 DXT 压缩下会有 ±6 的块噪声，而睫毛/眼白/瞳孔的对比都在 40 以上。
	/// 取更小会把压缩噪声当成内容，内容区会涨到整张图。
	/// </remarks>
	public const int ContentThreshold = 24;

	/// <summary>内容区覆盖到这个比例以上，就认为"整张图都是眼睛"（没有填充边）。</summary>
	public const double WholeTextureCoverage = 0.92;

	/// <summary>内容区小于这个比例，就认为"没找到眼睛"（噪声），退回整张图。</summary>
	public const double MinimumCoverage = 0.002;

	/// <summary>
	/// 眼睛在眼球上的**大小**倍率（默认 1.0 = 内容区宽度正好铺满眼球宽度）。
	/// <para>
	/// 环境变量 <c>HD2_EYE_SCALE</c>：&gt;1 眼睛更大（贴图放大，边缘被眼皮盖住），
	/// &lt;1 更小（露出更多眼白）。形状不受它影响（U/V 始终同一个比例），只影响大小。
	/// </para>
	/// <para>
	/// 之所以做成旋钮：源侧把虹膜投到"半径 = <c>$EyeballRadius</c> 的虚拟球"上，
	/// 而这个参数在 VMT 里常常缺省（默认 0.5），与实际眼球网格的半径（实测 0.35）不一致 ——
	/// 也就是说"源侧那只眼睛在屏幕上该多大"没法从数据推出来，只能实机对。
	/// </para>
	/// </summary>
	/// <summary>
	/// 逐眼镜像开关（环境变量 <c>HD2_EYE_MIRROR</c>，默认 0 = 不镜像）。
	/// <para>
	/// 只在诊断时打开：真眼是镜像摆的（同一张图在左眼里左右翻转），但源侧这两份 VMT
	/// 只有一条中线 attachment，"引擎到底怎么给每只眼睛摆朝向"没法从数据推出来，只能实机比。
	/// </para>
	/// </summary>
	public static bool EyeMirror { get; } =
		Environment.GetEnvironmentVariable("HD2_EYE_MIRROR") is { Length: > 0 } value
		&& (value.Trim() is "1" or "on" or "true");
	public static double EyeScale { get; } =
		double.TryParse(Environment.GetEnvironmentVariable("HD2_EYE_SCALE"),
			System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var scale)
			&& double.IsFinite(scale) && scale > 0
				? Math.Clamp(scale, 0.25, 4.0)
				: 1.0;

	/// <summary>
	/// 一张贴图里"画着东西的那块矩形"（UV，V=0 在图像顶部）。
	/// <para>
	/// 判据两步：① 取外框 2 像素的平均色当填充色（眼球图集四周一定是平色）；
	/// ② 逐像素与填充色的最大通道差 &gt; <see cref="ContentThreshold"/> 就算内容，取内容的包围盒。
	/// 内容几乎铺满整张图时返回 (0,0,1,1)——那时"整张图就是眼睛"，没有可裁的边。
	/// </para>
	/// </summary>
	/// <summary>
	/// 瞳孔中心（UV）。测不出来时返回 <c>(NaN, NaN)</c>，由调用方退回内容区几何中心。
	/// <para>
	/// <b>为什么必须单独测。</b>瞳孔不在内容区正中时，"内容区中心对到眼球中心"会让瞳孔
	/// 整体偏一侧；而两只眼睛共用同一张贴图，于是**两眼都朝同一个方向偏** ——
	/// 实测 3798231335 就是斗鸡眼（瞳孔偏内侧约 12% 的内容区宽度）。
	/// 以瞳孔为中心才对得上"看向前方"。
	/// </para>
	/// <para>
	/// 两步：① 只用中间 70% 的行（上下边缘是睫毛/眼睑，会把重心拽偏），逐列算平均亮度，
	/// 最暗的那一列就是瞳孔的横坐标；② 在该列 ±25% 内容区宽的窗口里算二维亮度加权重心。
	/// </para>
	/// <para>
	/// <b>测错了怎么办。</b>偏移超过内容区 40% 直接判为不可信（多半是整块暗背景或纯色图），
	/// 退回几何中心 —— 那与上一版行为一致，不会比原来更差。
	/// </para>
	/// </summary>
	public static (double U, double V) PupilCenter(
		byte[] rgba, int width, int height, double u0, double v0, double u1, double v1)
	{
		var none = (double.NaN, double.NaN);
		if (rgba.Length < width * height * 4)
			return none;

		var x0 = (int)Math.Round(u0 * width);
		var x1 = (int)Math.Round(u1 * width);
		var y0 = (int)Math.Round(v0 * height);
		var y1 = (int)Math.Round(v1 * height);
		if (x1 - x0 < 8 || y1 - y0 < 8)
			return none;

		var margin = Math.Max(1, (y1 - y0) * 15 / 100);
		var ya = Math.Max(y0, y0 + margin);
		var yb = Math.Min(y1, y1 - margin);
		if (yb - ya < 4)
			return none;

		// ① 逐列平均亮度 → 最暗的列。
		var columns = new double[x1 - x0];
		for (var x = x0; x < x1; x++)
		{
			double sum = 0;
			for (var y = ya; y < yb; y++)
			{
				var offset = ((y * width) + x) * 4;
				sum += (0.299 * rgba[offset]) + (0.587 * rgba[offset + 1]) + (0.114 * rgba[offset + 2]);
			}

			columns[x - x0] = sum / (yb - ya);
		}

		var darkest = 0;
		for (var i = 1; i < columns.Length; i++)
		{
			if (columns[i] < columns[darkest])
				darkest = i;
		}

		// ② 最暗列 ±25% 内容区宽 的窗口里算二维加权重心。
		var window = Math.Max(2, (x1 - x0) * 25 / 100);
		var left = Math.Max(0, darkest - window);
		var right = Math.Min(columns.Length - 1, darkest + window);

		double sx = 0, sy = 0, sw = 0;
		for (var y = ya; y < yb; y++)
		{
			for (var i = left; i <= right; i++)
			{
				var x = x0 + i;
				var offset = ((y * width) + x) * 4;
				var lum = (0.299 * rgba[offset]) + (0.587 * rgba[offset + 1]) + (0.114 * rgba[offset + 2]);
				var weight = (255 - lum) * (255 - lum);
				sx += x * weight;
				sy += y * weight;
				sw += weight;
			}
		}

		if (sw <= 1e-6)
			return none;

		var pupilU = sx / sw / width;
		var pupilV = sy / sw / height;

		var centerU = (u0 + u1) / 2;
		var centerV = (v0 + v1) / 2;
		if (Math.Abs(pupilU - centerU) > (u1 - u0) * 0.4 || Math.Abs(pupilV - centerV) > (v1 - v0) * 0.4)
			return none;

		return (pupilU, pupilV);
	}
	public static (double U0, double V0, double U1, double V1) ContentRect(byte[] rgba, int width, int height)
	{
		var whole = (U0: 0d, V0: 0d, U1: 1d, V1: 1d);
		if (width < 4 || height < 4 || rgba.Length < width * height * 4)
			return whole;

		// ① 填充色 = 外框 2 像素的平均。
		long sumR = 0, sumG = 0, sumB = 0, sumA = 0;
		var samples = 0;
		for (var y = 0; y < height; y++)
		{
			var isBorderRow = y < 2 || y >= height - 2;
			for (var x = 0; x < width; x++)
			{
				if (!isBorderRow && x >= 2 && x < width - 2)
					continue;
				var offset = (y * width + x) * 4;
				sumR += rgba[offset];
				sumG += rgba[offset + 1];
				sumB += rgba[offset + 2];
				sumA += rgba[offset + 3];
				samples++;
			}
		}

		if (samples == 0)
			return whole;
		var backR = sumR / (double)samples;
		var backG = sumG / (double)samples;
		var backB = sumB / (double)samples;
		var backA = sumA / (double)samples;

		// ② 内容包围盒。
		int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
		long content = 0;
		for (var y = 0; y < height; y++)
		{
			for (var x = 0; x < width; x++)
			{
				var offset = (y * width + x) * 4;
				var diff = Math.Max(
					Math.Max(Math.Abs(rgba[offset] - backR), Math.Abs(rgba[offset + 1] - backG)),
					Math.Max(Math.Abs(rgba[offset + 2] - backB), Math.Abs(rgba[offset + 3] - backA)));
				if (diff <= ContentThreshold)
					continue;

				content++;
				if (x < minX) minX = x;
				if (x > maxX) maxX = x;
				if (y < minY) minY = y;
				if (y > maxY) maxY = y;
			}
		}

		var total = (double)width * height;
		if (content < total * MinimumCoverage || minX > maxX || minY > maxY)
			return whole;   // 没找到可辨认的内容：按"整张图就是眼睛"处理，不解也安全

		var boxArea = (double)(maxX - minX + 1) * (maxY - minY + 1) / total;
		if (boxArea >= WholeTextureCoverage)
			return whole;

		return (
			(double)minX / width,
			(double)minY / height,              // V=0 在图像顶部：与像素行同向
			(double)(maxX + 1) / width,
			(double)(maxY + 1) / height);
	}

	/// <summary>
	/// 把<paramref name="materialNames"/>里那几个"颜色来自 <c>$iris</c>"的材质的 UV 重算一遍。
	/// <para>
	/// <b>为什么必须整份几何一起处理。</b>UV 是<b>逐顶点</b>的：同一个顶点如果既被眼球三角形
	/// 用到、又被别处（头、脸）用到，改它就会毁掉另一处的贴图。所以只有"只被眼球材质用到"的
	/// 顶点才会被改，混用的顶点原样保留并记一条说明。
	/// </para>
	/// </summary>
	/// <param name="geometry">源几何。</param>
	/// <param name="materialNames">模型材质表（下标 = <c>SourceTriangle.MaterialIndex</c>）。</param>
	/// <param name="resolve">材质裸名 → 眼球颜色源；<c>null</c> 表示这个材质不是眼球。</param>
	public static (SourceGeometry Geometry, IReadOnlyList<string> Notes) Reproject(
		SourceGeometry geometry,
		IReadOnlyList<string>? materialNames,
		Func<string, EyeColorSource?> resolve,
		EyeFrame? eyeFrame = null)
	{
		ArgumentNullException.ThrowIfNull(geometry);
		ArgumentNullException.ThrowIfNull(resolve);
		if (materialNames is null || materialNames.Count == 0 || geometry.Triangles.Count == 0)
			return (geometry, []);

		// ① 哪些材质是眼球。
		var eyeByMaterial = new Dictionary<int, EyeColorSource>();
		for (var index = 0; index < materialNames.Count; index++)
		{
			var resolved = resolve(materialNames[index]);
			if (resolved is not null)
				eyeByMaterial[index] = resolved;
		}

		if (eyeByMaterial.Count == 0)
			return (geometry, []);

		// ② 两只眼球各自的中点，平均出一个"中线"：逐眼镜像时用它判断哪一侧。
		var eyeCenters = new Dictionary<int, Vec3>();
		foreach (var materialIndex in eyeByMaterial.Keys)
		{
			double lowX = double.MaxValue, highX = double.MinValue;
			double lowY = double.MaxValue, highY = double.MinValue;
			double lowZ = double.MaxValue, highZ = double.MinValue;
			foreach (var triangle in geometry.Triangles)
			{
				if (triangle.MaterialIndex != materialIndex) continue;
				foreach (var vertexIndex in new[] { triangle.A, triangle.B, triangle.C })
				{
					var p = geometry.Vertices[vertexIndex].Position;
					lowX = Math.Min(lowX, p.X); highX = Math.Max(highX, p.X);
					lowY = Math.Min(lowY, p.Y); highY = Math.Max(highY, p.Y);
					lowZ = Math.Min(lowZ, p.Z); highZ = Math.Max(highZ, p.Z);
				}
			}

			if (highX > lowX)
				eyeCenters[materialIndex] = new Vec3((lowX + highX) / 2, (lowY + highY) / 2, (lowZ + highZ) / 2);
		}

		var midline = eyeCenters.Count == 0
			? (Vec3?)null
			: new Vec3(
				eyeCenters.Values.Average(p => p.X),
				eyeCenters.Values.Average(p => p.Y),
				eyeCenters.Values.Average(p => p.Z));

		// ② 每个顶点被哪些材质用到（判"混用"）。
		var materialsByVertex = new Dictionary<int, HashSet<int>>();
		foreach (var triangle in geometry.Triangles)
		{
			foreach (var vertex in stackalloc[] { triangle.A, triangle.B, triangle.C })
			{
				if (!materialsByVertex.TryGetValue(vertex, out var set))
					materialsByVertex[vertex] = set = [];
				set.Add(triangle.MaterialIndex);
			}
		}

		var projected = new Dictionary<int, (double U, double V)>();
		var notes = new List<string>();
		// 每个眼球实际用了哪三个轴（横/竖/朝前）：报告里要写清楚，
		// 否则"眼睛为什么长这样"事后无从判断。
		var axesNotes = new List<string>();
		foreach (var (materialIndex, colorSource) in eyeByMaterial.OrderBy(pair => pair.Key))
		{
			var vertices = new HashSet<int>();
			foreach (var triangle in geometry.Triangles)
			{
				if (triangle.MaterialIndex != materialIndex)
					continue;
				vertices.Add(triangle.A);
				vertices.Add(triangle.B);
				vertices.Add(triangle.C);
			}

			if (vertices.Count == 0)
				continue;

			// ③ 眼球自己的包围盒：三个方向各量一次跨度。
			// ③ 眼球自己的包围盒：中心与三个方向的跨度。
			//    中心取包围盒中点而不是顶点均值：网格顶点疏密不均匀，均值会被密集的一侧拽偏。
			var low = new[] { double.MaxValue, double.MaxValue, double.MaxValue };
			var high = new[] { double.MinValue, double.MinValue, double.MinValue };
			foreach (var index in vertices)
			{
				var position = geometry.Vertices[index].Position;
				var values = new[] { position.X, position.Y, position.Z };
				for (var axis = 0; axis < 3; axis++)
				{
					low[axis] = Math.Min(low[axis], values[axis]);
					high[axis] = Math.Max(high[axis], values[axis]);
				}
			}

			var extent = new[] { high[0] - low[0], high[1] - low[1], high[2] - low[2] };
			var center = new Vec3(
				(low[0] + high[0]) / 2,
				(low[1] + high[1]) / 2,
				(low[2] + high[2]) / 2);

			// ④ **投影用哪两个方向，先问骨架，再退回"看眼球形状"。**
			//
			// 源侧 EyeRefract 是平面投影（eyes.vsh：`oT1 = (dot($IrisU, worldPos), dot($IrisV, worldPos))`），
			// 两条向量由引擎按眼球给出。我们在 HD2 里没有它们，只能自己找"右"和"上"——
			// 而**写死任何一个轴都会错**，实测两套模型就是反例：
			//   3798231335：眼球跨度 X 1.69 / Y 0.70 / Z 1.07 → 左右 X、上下 Z、朝前 Y
			//   3783088017：眼球跨度 X 1.65 / Y 0.65 / Z 0.64 → 左右 X、上下 Z，但 Y/Z 只差 3%，
			//               "最薄的那一维 = 朝前"这种判据在这份数据上会翻车（把X当上下，眼睛转 90°）
			// 骨架是稳的：右 = 左锁骨 − 右锁骨，上 = 头 − 髋 —— 两套模型上都成立，
			// 而且与作者在 VMT 里给的 EyeOrigin/IrisU/IrisV 是同一套语义（只是这里用默认值推）。
			var frameNote = string.Empty;
			Vec3 right;
			Vec3 up;
			if (eyeFrame is { } basis)
			{
				right = basis.Right;
				up = basis.Up;
				frameNote = $"轴取自骨架（右=({right.X:F2},{right.Y:F2},{right.Z:F2})"
					+ $"、上=({up.X:F2},{up.Y:F2},{up.Z:F2})）";
			}
			else
			{
				// 兜底：最薄的一维 = 朝向；剩下两个里最宽的 = 横向（眼睛总是"宽 > 高"）。
				var facing = 0;
				for (var axis = 1; axis < 3; axis++)
				{
					if (extent[axis] < extent[facing])
						facing = axis;
				}

				var horizontal = facing == 0 ? 1 : 0;
				var vertical = 3 - facing - horizontal;
				if (extent[vertical] > extent[horizontal])
					(horizontal, vertical) = (vertical, horizontal);

				right = AxisUnit(horizontal);
				up = AxisUnit(vertical);
				frameNote = $"轴从眼球形状推（横={AxisName(horizontal)}、竖={AxisName(vertical)}、"
					+ $"朝前={AxisName(facing)}，跨度 {extent[0]:F2}/{extent[1]:F2}/{extent[2]:F2}）";
			}

			// ⑤ 在 right/up 上量范围（不依赖轴对齐），再取**各向同性**的比例。
			//
			// 两个方向各算一次、取较大者（比例越大 = 贴图占的模型尺寸越小），
			// 这样整只眼睛都装得进眼球：装不下就会被轮廓切掉，屏幕上只剩中间一条。
			double minA = double.MaxValue, maxA = double.MinValue;
			double minB = double.MaxValue, maxB = double.MinValue;
			foreach (var index in vertices)
			{
				var offset = geometry.Vertices[index].Position - center;
				var a = (offset.X * right.X) + (offset.Y * right.Y) + (offset.Z * right.Z);
				var b = (offset.X * up.X) + (offset.Y * up.Y) + (offset.Z * up.Z);
				minA = Math.Min(minA, a);
				maxA = Math.Max(maxA, a);
				minB = Math.Min(minB, b);
				maxB = Math.Max(maxB, b);
			}

			var halfWidth = (maxA - minA) / 2;
			var halfHeight = (maxB - minB) / 2;
			if (halfWidth <= 1e-6 || halfHeight <= 1e-6)
			{
				notes.Add($"{materialNames[materialIndex]}：眼球在图像平面上没有跨度，UV 投影做不了，保持原 UV。");
				continue;
			}

			// `$dilation`：把采样半径按 1/(1+d) 收一次 —— 虹膜绕内容区中心放大，瞳孔随之变大。
			var scale = Math.Max(colorSource.HalfU / halfWidth, colorSource.HalfV / halfHeight)
				/ EyeScale * colorSource.DilationZoom;

			// 逐眼镜像（诊断开关 `HD2_EYE_MIRROR=1`）。
			// 真实的两只眼睛是"镜像摆"的：同一张图在两只眼里应当左右翻转。
			// 源侧只有一个中线 attachment（"eyes"），到底该不该镜像只能实机看：
			// 不镜像时，贴图里任何一点左右不对称（高光、瞳孔偏次）都会让两只眼睛"朝同一边"，
			// 看上去就是"一只看里、一只看外"。
			var mirror = 1.0;
			if (EyeMirror && midline is { } midlinePoint && eyeCenters.TryGetValue(materialIndex, out var eyeCenter))
				mirror = Vec3.Dot(eyeCenter - midlinePoint, right) >= 0 ? 1.0 : -1.0;

			var mixed = 0;
			foreach (var index in vertices)
			{
				if (materialsByVertex[index].Any(material => !eyeByMaterial.ContainsKey(material)))
				{
					mixed++;
					continue;
				}

				var offset = geometry.Vertices[index].Position - center;
				var a = (offset.X * right.X) + (offset.Y * right.Y) + (offset.Z * right.Z);
				var b = (offset.X * up.X) + (offset.Y * up.Y) + (offset.Z * up.Z);
				// V 取反：VTF/DDS 的 V=0 在图像顶部，而"上"方向该落到图像顶部。
				var u = colorSource.CenterU + (a * scale * mirror);
				var v = colorSource.CenterV - (b * scale);
				projected[index] = (u, v);
			}

			var (pupilU, pupilV) = colorSource.PupilOffsetRatio;
			axesNotes.Add($"{materialNames[materialIndex]}：{frameNote}"
				+ (colorSource.Dilation > 0
					? $"，$dilation={colorSource.Dilation:F2}（采样半径 ×{colorSource.DilationZoom:F2}）"
					: string.Empty)
				+ (double.IsNaN(colorSource.PupilU)
					? "，瞳孔未测出（按内容区中心）"
					: $"，瞳孔偏移 {pupilU:+0.0%;-0.0%;0%}/{pupilV:+0.0%;-0.0%;0%}"));

			if (mixed != 0)
				notes.Add(
					$"{materialNames[materialIndex]}：有 {mixed} 个顶点同时被别的材质用到（UV 是逐顶点的），"
					+ "这些顶点保持原 UV —— 眼球那部分可能仍是一整块纯色。");
		}

		if (projected.Count == 0)
			return (geometry, notes);

		var vertices2 = geometry.Vertices.ToArray();
		foreach (var (index, uv) in projected)
		{
			var source = vertices2[index];
			vertices2[index] = source with { U = (float)uv.U, V = (float)uv.V };
		}

		notes.AddRange(axesNotes);
		notes.Add(
			$"眼球 UV 已按 $iris 的平面投影重算（两轴**同一个比例**，共 {projected.Count} 个顶点）"
			+ "：源侧 EyeRefract 的采样坐标本来就不来自模型 UV（Valve eyes.vsh 的注释："
			+ "「Texture 1 is a planar projection used for the iris」），而这份网格的 UV 是常量。"
			+ (Math.Abs(EyeScale - 1.0) > 1e-9 ? $" 眼睛大小倍率 {EyeScale:F2}（HD2_EYE_SCALE）。" : string.Empty));

		return (geometry with { Vertices = vertices2 }, notes);
	}

	/// <summary>轴下标的可读名字（0=X / 1=Y / 2=Z），只用于报告。</summary>
	private static string AxisName(int axis) => axis switch { 0 => "X", 1 => "Y", _ => "Z" };

	/// <summary>单位轴向量（0=X / 1=Y / 2=Z）。</summary>
	private static Vec3 AxisUnit(int axis) => axis switch
	{
		0 => new Vec3(1, 0, 0),
		1 => new Vec3(0, 1, 0),
		_ => new Vec3(0, 0, 1),
	};
}
