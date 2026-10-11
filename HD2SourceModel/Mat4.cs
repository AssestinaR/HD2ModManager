namespace HD2SourceModel;

/// <summary>
/// 双精度 4x4 矩阵，采用<b>列向量</b>约定：点按 <c>v' = M · v</c> 变换，平移量位于第 4 列。
/// <para>
/// 之所以把约定写进类型名而不是留给读者，是因为两个引擎的存储方式不同，而且搞错不会报错：
/// Source 的 <c>mstudiobone_t.poseToBone</c> 是 3x4，12 个 float 已经就是本约定
/// （平移在平坦下标 3、7、11），可以原样载入。
/// </para>
/// <para>
/// 不转换的后果是静默的：每一段骨长都会被算成 0.0000，而不是抛异常。
/// </para>
/// <para>
/// HD2 那边的存储是行向量（平移在平坦下标 12/13/14），转换刻意<b>不</b>放在这里——
/// 本工程必须保持"不认识 HD2"，HD2 的存储细节属于 <c>HD2SourceMigration.Hd2RigReader</c>。
/// </para>
/// <para>
/// Double-precision 4x4 in the <b>column-vector</b> convention, so a point maps as
/// <c>v' = M · v</c> and the translation lives in the fourth column.  The convention is
/// named in the type because getting it wrong does not throw: it silently reports every
/// bone segment length as zero.  The bridge to HD2's row-vector storage deliberately lives
/// outside this project, so that this layer stays ignorant of HD2.
/// </para>
/// </summary>
public readonly struct Mat4(
	double m11, double m12, double m13, double m14,
	double m21, double m22, double m23, double m24,
	double m31, double m32, double m33, double m34,
	double m41, double m42, double m43, double m44)
{
	public double M11 { get; } = m11;
	public double M12 { get; } = m12;
	public double M13 { get; } = m13;
	public double M14 { get; } = m14;
	public double M21 { get; } = m21;
	public double M22 { get; } = m22;
	public double M23 { get; } = m23;
	public double M24 { get; } = m24;
	public double M31 { get; } = m31;
	public double M32 { get; } = m32;
	public double M33 { get; } = m33;
	public double M34 { get; } = m34;
	public double M41 { get; } = m41;
	public double M42 { get; } = m42;
	public double M43 { get; } = m43;
	public double M44 { get; } = m44;

	public static Mat4 Identity { get; } = new(
		1, 0, 0, 0,
		0, 1, 0, 0,
		0, 0, 1, 0,
		0, 0, 0, 1);

	/// <summary>平移量（第 4 列）。The translation, i.e. the fourth column.</summary>
	public Vec3 Origin => new(M14, M24, M34);

	/// <summary>
	/// 从 Source 的 3x4 布局（旋转 3x3 在前，平移在平坦下标 3/7/11）原样载入。
	/// Loads a Source 3x4 layout verbatim (rotation 3x3 first, translation at flat 3/7/11).
	/// </summary>
	public static Mat4 FromSource3x4(ReadOnlySpan<float> values)
	{
		if (values.Length < 12)
			throw new ArgumentException("A Source 3x4 transform needs 12 floats.", nameof(values));

		return new Mat4(
			values[0], values[1], values[2], values[3],
			values[4], values[5], values[6], values[7],
			values[8], values[9], values[10], values[11],
			0, 0, 0, 1);
	}

	public Mat4 Transposed() => new(
		M11, M21, M31, M41,
		M12, M22, M32, M42,
		M13, M23, M33, M43,
		M14, M24, M34, M44);

	public Vec3 TransformPoint(Vec3 point) => new(
		(M11 * point.X) + (M12 * point.Y) + (M13 * point.Z) + M14,
		(M21 * point.X) + (M22 * point.Y) + (M23 * point.Z) + M24,
		(M31 * point.X) + (M32 * point.Y) + (M33 * point.Z) + M34);

	public static Mat4 operator *(Mat4 left, Mat4 right) => new(
		(left.M11 * right.M11) + (left.M12 * right.M21) + (left.M13 * right.M31) + (left.M14 * right.M41),
		(left.M11 * right.M12) + (left.M12 * right.M22) + (left.M13 * right.M32) + (left.M14 * right.M42),
		(left.M11 * right.M13) + (left.M12 * right.M23) + (left.M13 * right.M33) + (left.M14 * right.M43),
		(left.M11 * right.M14) + (left.M12 * right.M24) + (left.M13 * right.M34) + (left.M14 * right.M44),
		(left.M21 * right.M11) + (left.M22 * right.M21) + (left.M23 * right.M31) + (left.M24 * right.M41),
		(left.M21 * right.M12) + (left.M22 * right.M22) + (left.M23 * right.M32) + (left.M24 * right.M42),
		(left.M21 * right.M13) + (left.M22 * right.M23) + (left.M23 * right.M33) + (left.M24 * right.M43),
		(left.M21 * right.M14) + (left.M22 * right.M24) + (left.M23 * right.M34) + (left.M24 * right.M44),
		(left.M31 * right.M11) + (left.M32 * right.M21) + (left.M33 * right.M31) + (left.M34 * right.M41),
		(left.M31 * right.M12) + (left.M32 * right.M22) + (left.M33 * right.M32) + (left.M34 * right.M42),
		(left.M31 * right.M13) + (left.M32 * right.M23) + (left.M33 * right.M33) + (left.M34 * right.M43),
		(left.M31 * right.M14) + (left.M32 * right.M24) + (left.M33 * right.M34) + (left.M34 * right.M44),
		(left.M41 * right.M11) + (left.M42 * right.M21) + (left.M43 * right.M31) + (left.M44 * right.M41),
		(left.M41 * right.M12) + (left.M42 * right.M22) + (left.M43 * right.M32) + (left.M44 * right.M42),
		(left.M41 * right.M13) + (left.M42 * right.M23) + (left.M43 * right.M33) + (left.M44 * right.M43),
		(left.M41 * right.M14) + (left.M42 * right.M24) + (left.M43 * right.M34) + (left.M44 * right.M44));

	/// <summary>
	/// 高斯-约当消元求逆（带部分主元）。奇异时返回 false，而不是抛异常——
	/// 源模型里出现不可逆的 bind 矩阵是"数据有问题"，调用方需要能把它变成一条诊断。
	/// Gauss-Jordan inversion with partial pivoting.  Returns false instead of throwing:
	/// a non-invertible bind matrix means the data is bad, and callers need to turn that
	/// into a diagnostic rather than an exception.
	/// </summary>
	public bool TryInvert(out Mat4 inverse)
	{
		var a = new[,]
		{
			{ M11, M12, M13, M14 },
			{ M21, M22, M23, M24 },
			{ M31, M32, M33, M34 },
			{ M41, M42, M43, M44 },
		};
		var inv = new double[4, 4];
		for (var i = 0; i < 4; i++)
			inv[i, i] = 1;

		for (var column = 0; column < 4; column++)
		{
			var pivotRow = column;
			for (var row = column + 1; row < 4; row++)
			{
				if (Math.Abs(a[row, column]) > Math.Abs(a[pivotRow, column]))
					pivotRow = row;
			}

			if (Math.Abs(a[pivotRow, column]) < 1e-12)
			{
				inverse = Identity;
				return false;
			}

			if (pivotRow != column)
			{
				for (var k = 0; k < 4; k++)
				{
					(a[column, k], a[pivotRow, k]) = (a[pivotRow, k], a[column, k]);
					(inv[column, k], inv[pivotRow, k]) = (inv[pivotRow, k], inv[column, k]);
				}
			}

			var divisor = a[column, column];
			for (var k = 0; k < 4; k++)
			{
				a[column, k] /= divisor;
				inv[column, k] /= divisor;
			}

			for (var row = 0; row < 4; row++)
			{
				if (row == column)
					continue;

				var factor = a[row, column];
				if (factor == 0)
					continue;

				for (var k = 0; k < 4; k++)
				{
					a[row, k] -= factor * a[column, k];
					inv[row, k] -= factor * inv[column, k];
				}
			}
		}

		inverse = new Mat4(
			inv[0, 0], inv[0, 1], inv[0, 2], inv[0, 3],
			inv[1, 0], inv[1, 1], inv[1, 2], inv[1, 3],
			inv[2, 0], inv[2, 1], inv[2, 2], inv[2, 3],
			inv[3, 0], inv[3, 1], inv[3, 2], inv[3, 3]);
		return true;
	}

	public Mat4 Inverse() =>
		TryInvert(out var inverse)
			? inverse
			: throw new InvalidOperationException("The matrix is singular and cannot be inverted.");
}
