namespace HD2SourceModel;

/// <summary>
/// 双精度三维向量。
/// <para>
/// 读取源模型用 float 就够了，但后续的骨架映射要把 bind 矩阵求逆再相乘，float32 会把
/// 毫米级的吻合度磨掉，所以这一层统一用 double。
/// </para>
/// <para>
/// A double-precision 3D vector.  Reading the source model alone would be fine in float,
/// but the rig mapping inverts and multiplies bind matrices; float32 would erode the
/// millimetre-level agreement the pipeline is verified against.
/// </para>
/// </summary>
public readonly record struct Vec3(double X, double Y, double Z)
{
	public static readonly Vec3 Zero = new(0, 0, 0);

	public static Vec3 operator +(Vec3 left, Vec3 right) => new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);

	public static Vec3 operator -(Vec3 left, Vec3 right) => new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);

	public static Vec3 operator *(Vec3 value, double scale) => new(value.X * scale, value.Y * scale, value.Z * scale);

	public static Vec3 operator /(Vec3 value, double scale) => new(value.X / scale, value.Y / scale, value.Z / scale);

	public double Length => Math.Sqrt((X * X) + (Y * Y) + (Z * Z));

	public static double Dot(Vec3 left, Vec3 right) => (left.X * right.X) + (left.Y * right.Y) + (left.Z * right.Z);

	public static Vec3 Cross(Vec3 left, Vec3 right) => new(
		(left.Y * right.Z) - (left.Z * right.Y),
		(left.Z * right.X) - (left.X * right.Z),
		(left.X * right.Y) - (left.Y * right.X));

	public Vec3 Normalized()
	{
		var length = Length;
		return length > 0 ? this / length : Zero;
	}

	public static double Distance(Vec3 left, Vec3 right) => (left - right).Length;
}
