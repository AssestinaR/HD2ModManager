using HD2SourceModel;

namespace HD2SourceMigration.Retarget;

/// <summary>
/// 重定向管线的线性代数原语。全部是 <c>hd2_style.py</c> 里同名函数的逐式移植，包括
/// 那些看起来"多余"的 <c>max(‖v‖, 1e-12)</c> 保护——它们不是防御性编程，而是把
/// 退化输入变成"某个确定答案"而不是 NaN，而 NaN 一旦进了 <c>D</c> 递归会污染整条骨架。
/// <para>
/// Linear-algebra primitives for the retarget pipeline, ported term for term from the
/// Python.  The <c>max(‖v‖, 1e-12)</c> guards are not defensive decoration: they turn a
/// degenerate input into a definite answer instead of a NaN, and a single NaN inside the
/// parent-first <c>D</c> recursion poisons the whole skeleton.
/// </para>
/// </summary>
public static class RetargetMath
{
	/// <summary>Python 的 <c>v / max(‖v‖, eps)</c>。刻意<b>不</b>用 <see cref="Vec3.Normalized"/>：
	/// 后者在零向量上返回零向量，而这里要的是与 Python 完全一致的"除以 eps"。</summary>
	public static Vec3 SafeNormalize(Vec3 value, double epsilon = 1e-12)
	{
		var length = value.Length;
		return value / Math.Max(length, epsilon);
	}

	public static double Clamp(double value, double low, double high) =>
		value < low ? low : value > high ? high : value;

	/// <summary>把标量夹到 [-1, 1] 再取 acos——Python 里每一处 <c>acos</c> 都带这个夹取。</summary>
	public static double SafeAcos(double value) => Math.Acos(Clamp(value, -1.0, 1.0));

	/// <summary>平移矩阵（第 4 列）。</summary>
	public static Mat4 Trans(Vec3 value) => new(
		1, 0, 0, value.X,
		0, 1, 0, value.Y,
		0, 0, 1, value.Z,
		0, 0, 0, 1);

	/// <summary>只缩放平移列（下标 3/7/11 的 3x4 视角，即第 4 列的 X/Y/Z）。</summary>
	public static Mat4 ScaleTranslation(Mat4 matrix, double scale) => new(
		matrix.M11, matrix.M12, matrix.M13, matrix.M14 * scale,
		matrix.M21, matrix.M22, matrix.M23, matrix.M24 * scale,
		matrix.M31, matrix.M32, matrix.M33, matrix.M34 * scale,
		matrix.M41, matrix.M42, matrix.M43, matrix.M44);

	/// <summary>四元数 <c>(w, x, y, z)</c> → 4x4 旋转（列向量约定）。零四元数按 n = 1 处理，与 Python 的 <c>or 1.0</c> 一致。</summary>
	public static Mat4 QuaternionWxyzToMat4(double w, double x, double y, double z)
	{
		var n = Math.Sqrt((w * w) + (x * x) + (y * y) + (z * z));
		if (n == 0)
			n = 1.0;
		w /= n;
		x /= n;
		y /= n;
		z /= n;
		return new Mat4(
			1 - (2 * ((y * y) + (z * z))), 2 * ((x * y) - (z * w)), 2 * ((x * z) + (y * w)), 0,
			2 * ((x * y) + (z * w)), 1 - (2 * ((x * x) + (z * z))), 2 * ((y * z) - (x * w)), 0,
			2 * ((x * z) - (y * w)), 2 * ((y * z) + (x * w)), 1 - (2 * ((x * x) + (y * y))), 0,
			0, 0, 0, 1);
	}

	/// <summary>轴 + 角 → 3x3 旋转（罗德里格斯公式）。</summary>
	public static Mat3 RotAxisAngle(Vec3 axis, double radians)
	{
		var (x, y, z) = (axis.X, axis.Y, axis.Z);
		var c = Math.Cos(radians);
		var s = Math.Sin(radians);
		var cc = 1.0 - c;
		return new Mat3(
			c + (x * x * cc), (x * y * cc) - (z * s), (x * z * cc) + (y * s),
			(y * x * cc) + (z * s), c + (y * y * cc), (y * z * cc) - (x * s),
			(z * x * cc) - (y * s), (z * y * cc) + (x * s), c + (z * z * cc));
	}

	/// <summary>
	/// 3x3 旋转 → (角度°, 轴)。角度用迹公式，轴用反对称部分的差。轴长为零时返回零向量
	/// （角度也必然是 0 或 180，调用方按角度判分支）。
	/// </summary>
	public static (double Degrees, Vec3 Axis) AngleAxis(Mat3 rotation)
	{
		var trace = rotation.M11 + rotation.M22 + rotation.M33;
		var degrees = Math.Acos(Clamp((trace - 1.0) / 2.0, -1.0, 1.0)) * (180.0 / Math.PI);
		var w = new Vec3(
			rotation.M32 - rotation.M23,
			rotation.M13 - rotation.M31,
			rotation.M21 - rotation.M12);
		var n = w.Length;
		return (degrees, n > 1e-9 ? w / n : Vec3.Zero);
	}

	/// <summary>
	/// 把方向 <paramref name="from"/> 转到 <paramref name="to"/> 的最短旋转。
	/// <para>
	/// ⚠️ 反向平行分支<b>必须</b>返回一个真旋转（det = +1）。旧实现返回 <c>-I</c>，那是个
	/// <b>镜像</b>；任何随后与真旋转复合、或检查 det 的代码都会继承一个翻转的坐标系。
	/// 真正的 180° 需要一个与 <paramref name="from"/> 垂直的轴，取最稳的那个。
	/// </para>
	/// </summary>
	public static Mat3 MinArcRotation(Vec3 from, Vec3 to)
	{
		var a = SafeNormalize(from);
		var b = SafeNormalize(to);
		var axis = Vec3.Cross(a, b);
		var s = axis.Length;
		var c = Vec3.Dot(a, b);
		if (s < 1e-9)
		{
			if (c > 0.0)
				return Mat3.Identity;
			var helper = Math.Abs(a.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
			return RotAxisAngle(SafeNormalize(Vec3.Cross(a, helper)), Math.PI);
		}

		return RotAxisAngle(axis / s, Math.Atan2(s, c));
	}

	/// <summary>沿短弧混合两个单位方向（t = 0 → a，t = 1 → b）。</summary>
	public static Vec3 SlerpDirection(Vec3 a, Vec3 b, double t)
	{
		var d = Clamp(Vec3.Dot(a, b), -1.0, 1.0);
		var theta = Math.Acos(d);
		if (theta < 1e-9)
			return a;
		var k = Math.Sin(theta);
		if (Math.Abs(k) < 1e-12)
			return a;
		return (a * (Math.Sin((1.0 - t) * theta) / k)) + (b * (Math.Sin(t * theta) / k));
	}

	/// <summary>
	/// 把点集 <paramref name="a"/> 最佳刚体对齐到 <paramref name="b"/>（无缩放，det = +1）。
	/// 返回的旋转满足 <c>b ≈ R · a</c>。
	/// </summary>
	public static Mat3 Kabsch(IReadOnlyList<Vec3> a, IReadOnlyList<Vec3> b)
	{
		var ca = Centroid(a);
		var cb = Centroid(b);
		var h = new double[3, 3];
		for (var i = 0; i < a.Count; i++)
		{
			var pa = a[i] - ca;
			var pb = b[i] - cb;
			h[0, 0] += pa.X * pb.X; h[0, 1] += pa.X * pb.Y; h[0, 2] += pa.X * pb.Z;
			h[1, 0] += pa.Y * pb.X; h[1, 1] += pa.Y * pb.Y; h[1, 2] += pa.Y * pb.Z;
			h[2, 0] += pa.Z * pb.X; h[2, 1] += pa.Z * pb.Y; h[2, 2] += pa.Z * pb.Z;
		}

		OneSidedJacobi(h, 3, 3, out var u, out var v);
		// 反射修正：H = U Σ Vᵀ，所以 R = V · diag(1,1,d) · Uᵀ，d 由 det(V · Uᵀ) 的符号定。
		var flip = Determinant3(Multiply3(v, Transpose3(u))) > 0 ? 1.0 : -1.0;
		var s = new double[3, 3];
		s[0, 0] = 1.0;
		s[1, 1] = 1.0;
		s[2, 2] = flip;
		return ToMat3(Multiply3(Multiply3(v, s), Transpose3(u)));
	}

	/// <summary>
	/// 只绕 +Z 的最佳旋转（1 自由度拟合）。
	/// <para>
	/// 为什么不用自由的 3 自由度 <see cref="Kabsch"/>：两套骨架把关节放在不同高度（源侧
	/// <c>Pelvis</c> 在髋关节下方 12 cm，HD2 的 <c>hips</c> 就在髋关节上），所以那 20 个点
	/// 并不全等，最佳刚体运动会带着约 2.8° 的倾斜。绕一个偏离 +Z 2.8° 的轴转 180° 会把
	/// 0.94 m 的高度混进 Y，把脚踝推离髋部约 9.8 cm——也就是这个拟合会<b>凭空造出</b>我们
	/// 正要测量的腿部偏移。先投影到 XY 再拟合，旋转就严格绕 +Z，高度与竖直站姿都活下来。
	/// </para>
	/// </summary>
	public static Mat3 YawRotation(IReadOnlyList<Vec3> a, IReadOnlyList<Vec3> b)
	{
		var ca = Centroid2(a);
		var cb = Centroid2(b);
		var h = new double[2, 2];
		for (var i = 0; i < a.Count; i++)
		{
			var ax = a[i].X - ca.X;
			var ay = a[i].Y - ca.Y;
			var bx = b[i].X - cb.X;
			var by = b[i].Y - cb.Y;
			h[0, 0] += ax * bx; h[0, 1] += ax * by;
			h[1, 0] += ay * bx; h[1, 1] += ay * by;
		}

		OneSidedJacobi(h, 2, 2, out var u, out var v);
		var vu = Multiply2(v, Transpose2(u));
		var flip = ((vu[0, 0] * vu[1, 1]) - (vu[0, 1] * vu[1, 0])) > 0 ? 1.0 : -1.0;
		var diagonal = new double[2, 2];
		diagonal[0, 0] = 1.0;
		diagonal[1, 1] = flip;
		var r2 = Multiply2(Multiply2(v, diagonal), Transpose2(u));
		return new Mat3(
			r2[0, 0], r2[0, 1], 0,
			r2[1, 0], r2[1, 1], 0,
			0, 0, 1);
	}

	private static Vec3 Centroid(IReadOnlyList<Vec3> points)
	{
		var sum = Vec3.Zero;
		foreach (var point in points)
			sum += point;
		return sum / points.Count;
	}

	private static Vec3 Centroid2(IReadOnlyList<Vec3> points)
	{
		var sum = Vec3.Zero;
		foreach (var point in points)
			sum += point;
		return sum / points.Count;
	}

	/// <summary>
	/// 单侧 Jacobi SVD。矩阵最大 3x3（Kabsch 的 H 是 3x3，yaw 的是 2x2），所以固定扫描
	/// 次数即可，不需要收敛判据——这也让结果与"跑多少轮"无关，是可复现的。
	/// </summary>
	private static void OneSidedJacobi(double[,] source, int rows, int columns, out double[,] u, out double[,] v)
	{
		u = (double[,])source.Clone();
		v = new double[columns, columns];
		for (var i = 0; i < columns; i++)
			v[i, i] = 1.0;

		for (var sweep = 0; sweep < 60; sweep++)
		{
			var off = 0.0;
			for (var p = 0; p < columns - 1; p++)
			{
				for (var q = p + 1; q < columns; q++)
				{
					double alpha = 0, beta = 0, gamma = 0;
					for (var i = 0; i < rows; i++)
					{
						alpha += u[i, p] * u[i, p];
						beta += u[i, q] * u[i, q];
						gamma += u[i, p] * u[i, q];
					}

					var scale = Math.Sqrt(Math.Max(alpha * beta, 1e-300));
					off = Math.Max(off, Math.Abs(gamma) / Math.Max(scale, 1e-300));
					if (Math.Abs(gamma) <= 1e-18 * Math.Max(scale, 1e-300))
						continue;

					var zeta = (beta - alpha) / (2.0 * gamma);
					var t = zeta == 0.0
						? 1.0
						: Math.Sign(zeta) / (Math.Abs(zeta) + Math.Sqrt(1.0 + (zeta * zeta)));
					var c = 1.0 / Math.Sqrt(1.0 + (t * t));
					var s = c * t;
					for (var i = 0; i < rows; i++)
					{
						var up = u[i, p];
						var uq = u[i, q];
						u[i, p] = (c * up) - (s * uq);
						u[i, q] = (s * up) + (c * uq);
					}

					for (var i = 0; i < columns; i++)
					{
						var vp = v[i, p];
						var vq = v[i, q];
						v[i, p] = (c * vp) - (s * vq);
						v[i, q] = (s * vp) + (c * vq);
					}
				}
			}

			if (off < 1e-16)
				break;
		}

		for (var j = 0; j < columns; j++)
		{
			var norm = 0.0;
			for (var i = 0; i < rows; i++)
				norm += u[i, j] * u[i, j];
			norm = Math.Sqrt(norm);
			if (norm <= 1e-300)
				continue;
			for (var i = 0; i < rows; i++)
				u[i, j] /= norm;
		}
	}

	private static double Determinant3(double[,] m) =>		(m[0, 0] * ((m[1, 1] * m[2, 2]) - (m[1, 2] * m[2, 1])))
		- (m[0, 1] * ((m[1, 0] * m[2, 2]) - (m[1, 2] * m[2, 0])))
		+ (m[0, 2] * ((m[1, 0] * m[2, 1]) - (m[1, 1] * m[2, 0])));

	private static double[,] Multiply3(double[,] a, double[,] b)
	{
		var r = new double[3, 3];
		for (var i = 0; i < 3; i++)
			for (var j = 0; j < 3; j++)
				for (var k = 0; k < 3; k++)
					r[i, j] += a[i, k] * b[k, j];
		return r;
	}

	private static double[,] Multiply2(double[,] a, double[,] b)
	{
		var r = new double[2, 2];
		for (var i = 0; i < 2; i++)
			for (var j = 0; j < 2; j++)
				for (var k = 0; k < 2; k++)
					r[i, j] += a[i, k] * b[k, j];
		return r;
	}

	private static double[,] Transpose3(double[,] a) => new[,]
	{
		{ a[0, 0], a[1, 0], a[2, 0] },
		{ a[0, 1], a[1, 1], a[2, 1] },
		{ a[0, 2], a[1, 2], a[2, 2] },
	};

	private static double[,] Transpose2(double[,] a) => new[,]
	{
		{ a[0, 0], a[1, 0] },
		{ a[0, 1], a[1, 1] },
	};

	private static Mat3 ToMat3(double[,] m) => new(
		m[0, 0], m[0, 1], m[0, 2],
		m[1, 0], m[1, 1], m[1, 2],
		m[2, 0], m[2, 1], m[2, 2]);
}
