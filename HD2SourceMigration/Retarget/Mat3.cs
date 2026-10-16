namespace HD2SourceMigration.Retarget;

/// <summary>
/// 双精度 3x3 矩阵，<b>列向量</b>约定（<c>v' = M · v</c>），与 <see cref="HD2SourceModel.Mat4"/> 一致。
/// <para>
/// 为什么需要它：重定向管线里有一批运算只在<b>旋转</b>上做——骨骼局部系的共轭
/// <c>Rs⁻¹ · R · Rs</c>、最小弧旋转、T↔A 姿态的 basis。这些量在数学上必须是纯旋转
/// （det = +1、无平移），而用 4x4 表达它们会引入两个真实存在的坑：
/// </para>
/// <list type="number">
/// <item>把带平移的 4x4 当旋转用（<c>rest[b]</c> 而不是 <c>rest[b][:3,:3]</c>）会让骨骼
/// <b>绕世界原点</b>旋转而不是绕自己的骨头——实测会把整只手甩到一米开外。3x3 从类型上
/// 就不可能犯这个错。</item>
/// <item>对 4x4 求逆得到的是仿射逆，对 3x3 求逆得到的是旋转逆；两者在"这个矩阵是不是
/// 纯旋转"这件事上的诊断能力完全不同。</item>
/// </list>
/// <para>
/// A double-precision 3x3 matrix in the column-vector convention.  It exists because a whole
/// family of operations in the retarget pipeline is defined on <b>rotations alone</b> (the
/// local-frame conjugation, the minimum-arc solver, the T/A-pose basis), and expressing those
/// as 4x4 matrices invites a real, silent bug: passing a transform that carries translation
/// where a rotation is expected spins the limb about the world origin instead of about its own
/// joint.  A 3x3 makes that mistake unrepresentable.
/// </para>
/// </summary>
public readonly struct Mat3(
	double m11, double m12, double m13,
	double m21, double m22, double m23,
	double m31, double m32, double m33)
{
	public double M11 { get; } = m11;
	public double M12 { get; } = m12;
	public double M13 { get; } = m13;
	public double M21 { get; } = m21;
	public double M22 { get; } = m22;
	public double M23 { get; } = m23;
	public double M31 { get; } = m31;
	public double M32 { get; } = m32;
	public double M33 { get; } = m33;

	public static Mat3 Identity { get; } = new(1, 0, 0, 0, 1, 0, 0, 0, 1);

	public double Determinant =>
		(M11 * ((M22 * M33) - (M23 * M32)))
		- (M12 * ((M21 * M33) - (M23 * M31)))
		+ (M13 * ((M21 * M32) - (M22 * M31)));

	/// <summary>取出 <see cref="HD2SourceModel.Mat4"/> 的左上 3x3。丢弃平移与第四行/列。</summary>
	public static Mat3 FromMat4(HD2SourceModel.Mat4 matrix) => new(
		matrix.M11, matrix.M12, matrix.M13,
		matrix.M21, matrix.M22, matrix.M23,
		matrix.M31, matrix.M32, matrix.M33);

	/// <summary>按<b>列</b>组装：<paramref name="c0"/> 是变换后 X 轴指向的世界方向，依此类推。</summary>
	public static Mat3 FromColumns(HD2SourceModel.Vec3 c0, HD2SourceModel.Vec3 c1, HD2SourceModel.Vec3 c2) => new(
		c0.X, c1.X, c2.X,
		c0.Y, c1.Y, c2.Y,
		c0.Z, c1.Z, c2.Z);

	/// <summary>提升为纯旋转 4x4（平移为零、M44 = 1）。</summary>
	public HD2SourceModel.Mat4 ToMat4() => new(
		M11, M12, M13, 0,
		M21, M22, M23, 0,
		M31, M32, M33, 0,
		0, 0, 0, 1);

	public HD2SourceModel.Vec3 Transform(HD2SourceModel.Vec3 value) => new(
		(M11 * value.X) + (M12 * value.Y) + (M13 * value.Z),
		(M21 * value.X) + (M22 * value.Y) + (M23 * value.Z),
		(M31 * value.X) + (M32 * value.Y) + (M33 * value.Z));

	/// <summary>用<b>转置</b>变换：等价于 <c>M⁻¹ · v</c>（正交矩阵），但不需要求逆。</summary>
	public HD2SourceModel.Vec3 TransformTransposed(HD2SourceModel.Vec3 value) => new(
		(M11 * value.X) + (M21 * value.Y) + (M31 * value.Z),
		(M12 * value.X) + (M22 * value.Y) + (M32 * value.Z),
		(M13 * value.X) + (M23 * value.Y) + (M33 * value.Z));

	public Mat3 Transposed() => new(
		M11, M21, M31,
		M12, M22, M32,
		M13, M23, M33);

	/// <summary>
	/// 伴随矩阵法求逆。<b>刻意不用转置</b>：转置只在矩阵正交时才等于逆，而管线里会拿
	/// 它去处理"本该正交但被浮点误差磨过"的矩阵——那时静默返回一个错的逆比抛异常更糟。
	/// Adjugate inversion.  Deliberately <b>not</b> the transpose: the transpose is the inverse
	/// only for an orthogonal matrix, and silently returning a wrong inverse is worse than a
	/// diagnostic when the input has drifted.
	/// </summary>
	public bool TryInvert(out Mat3 inverse)
	{
		var det = Determinant;
		if (Math.Abs(det) < 1e-15)
		{
			inverse = Identity;
			return false;
		}

		var inv = 1.0 / det;
		inverse = new Mat3(
			((M22 * M33) - (M23 * M32)) * inv,
			((M13 * M32) - (M12 * M33)) * inv,
			((M12 * M23) - (M13 * M22)) * inv,
			((M23 * M31) - (M21 * M33)) * inv,
			((M11 * M33) - (M13 * M31)) * inv,
			((M13 * M21) - (M11 * M23)) * inv,
			((M21 * M32) - (M22 * M31)) * inv,
			((M12 * M31) - (M11 * M32)) * inv,
			((M11 * M22) - (M12 * M21)) * inv);
		return true;
	}

	/// <summary>求逆；奇异时抛异常。调用方若需要把"数据坏了"变成诊断，用 <see cref="TryInvert"/>。</summary>
	public Mat3 Inverse() =>
		TryInvert(out var inverse)
			? inverse
			: throw new InvalidOperationException("The matrix is singular and cannot be inverted.");

	public static Mat3 operator *(Mat3 left, Mat3 right) => new(
		(left.M11 * right.M11) + (left.M12 * right.M21) + (left.M13 * right.M31),
		(left.M11 * right.M12) + (left.M12 * right.M22) + (left.M13 * right.M32),
		(left.M11 * right.M13) + (left.M12 * right.M23) + (left.M13 * right.M33),
		(left.M21 * right.M11) + (left.M22 * right.M21) + (left.M23 * right.M31),
		(left.M21 * right.M12) + (left.M22 * right.M22) + (left.M23 * right.M32),
		(left.M21 * right.M13) + (left.M22 * right.M23) + (left.M23 * right.M33),
		(left.M31 * right.M11) + (left.M32 * right.M21) + (left.M33 * right.M31),
		(left.M31 * right.M12) + (left.M32 * right.M22) + (left.M33 * right.M32),
		(left.M31 * right.M13) + (left.M32 * right.M23) + (left.M33 * right.M33));
}
