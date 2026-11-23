using System.Globalization;
using System.Numerics;

namespace HD2ModAdaptation.Preview;

/// <summary>
/// 轨道相机（规划 §6）：拍摄中心 + 三个旋转角 + 缩放，别无其它自由度。
/// <para>
/// <b>这个类型同时负责"量化"</b>：渲染之前必须先把各分量 round 到文件名用的精度
/// （规划 §7 的不变量 1）。否则会出现"按 37.5043° 渲染、按 37.50 命名"，
/// 名字就不再是规格。
/// </para>
/// <para>
/// <b>坐标轴实测是 Z 向上</b>（P0：真实护甲 AABB 在 Z 上 2.10 m、Y 上 0.57 m）：
/// yaw 绕 +Z，pitch 绕相机的右轴，`yaw = 0` 时相机在 −Y 一侧朝 +Y 看。
/// 模型正面到底朝 −Y 还是 +Y 属 R1 待核；猜错只需要翻一个符号，文件名不用动。
/// </para>
/// </summary>
public sealed record PreviewCamera
{
	/// <summary>角度的小数位。文件名与渲染必须用同一个精度。</summary>
	public const int AngleDecimals = 2;

	/// <summary>pivot 的小数位（归一化后 0.001 ≈ 主体半径的千分之一）。</summary>
	public const int PivotDecimals = 3;

	/// <summary>缩放的小数位。</summary>
	public const int ZoomDecimals = 2;

	/// <summary>|pitch| 的上限。90° 时 up 向量与视线平行，会出 NaN。</summary>
	public const float MaximumPitchDegrees = 89.5f;

	/// <summary>默认竖直视场角（度）。</summary>
	public const float DefaultFieldOfViewDegrees = 35f;

	/// <summary>自动取景留的余量：把主体包围球撑到画面里再留 5%。</summary>
	public const float DefaultMargin = 1.05f;

	/// <summary>拍摄中心，相对主体包围球中心、以半径为单位的偏移。0 就是正对中心。</summary>
	public Vector3 Pivot { get; init; }

	/// <summary>绕 +Z 的方位角。0 = 相机在 −Y 一侧。</summary>
	public float YawDegrees { get; init; }

	/// <summary>仰角，+ 表示相机在上方往下看。</summary>
	public float PitchDegrees { get; init; }

	/// <summary>绕视线的滚转。</summary>
	public float RollDegrees { get; init; }

	/// <summary>相对自动取景的倍数，必须 &gt; 0。</summary>
	public float Zoom { get; init; } = 1f;

	/// <summary>竖直视场角（度）。</summary>
	public float FieldOfViewDegrees { get; init; } = DefaultFieldOfViewDegrees;

	/// <summary>自动取景余量。</summary>
	public float Margin { get; init; } = DefaultMargin;

	/// <summary>正视图：正对主体中心。</summary>
	public static PreviewCamera Front { get; } = new();

	/// <summary>
	/// 把相机规格量化并规范化。渲染器只接受经过这一步的实例。
	/// <para>规范化包含：负零归零、yaw 绕回 [0, 360)、pitch 夹到 ±89.5、roll 绕回 [−180, 180)。</para>
	/// </summary>
	public PreviewCamera Quantize()
		=> this with
		{
			Pivot = new Vector3(
				QuantizeComponent(Pivot.X, PivotDecimals),
				QuantizeComponent(Pivot.Y, PivotDecimals),
				QuantizeComponent(Pivot.Z, PivotDecimals)),
			YawDegrees = Wrap(QuantizeComponent(YawDegrees, AngleDecimals), 360f),
			PitchDegrees = Math.Clamp(
				QuantizeComponent(PitchDegrees, AngleDecimals), -MaximumPitchDegrees, MaximumPitchDegrees),
			RollDegrees = Wrap(QuantizeComponent(RollDegrees, AngleDecimals) + 180f, 360f) - 180f,
			Zoom = QuantizeComponent(Zoom, ZoomDecimals),
		};

	/// <summary>规格是否可用于渲染（量化之后才判定）。</summary>
	public bool IsRenderable
		=> Zoom > 0f
			&& float.IsFinite(Zoom)
			&& FieldOfViewDegrees is > 1f and < 179f
			&& float.IsFinite(Pivot.X) && float.IsFinite(Pivot.Y) && float.IsFinite(Pivot.Z);

	/// <summary>
	/// 解算出一次渲染要用的相机参数。取景规则：让包围球在竖直方向上正好撑满（乘余量），再除以 zoom。
	/// </summary>
	public PreviewCameraSetup Resolve(PreviewBoundingSphere bounds, float aspectRatio)
	{
		ArgumentNullException.ThrowIfNull(bounds);
		if (aspectRatio <= 0f || !float.IsFinite(aspectRatio))
			throw new ArgumentOutOfRangeException(nameof(aspectRatio), "宽高比必须是有限正数。");

		var radius = bounds.Radius > 0f ? bounds.Radius : 1f;
		var target = bounds.Center + Pivot * radius;

		var yaw = YawDegrees * MathF.PI / 180f;
		var pitch = PitchDegrees * MathF.PI / 180f;
		// 目标 → 相机的方向（Z-up）。yaw = 0 ⇒ 相机在 **+Y** 一侧。
		// 实测（P0/P1，Elise_Bill 素体）：模型正面朝 +Y —— yaw=0 拍到正面，yaw=180 拍到背面。
		// 所以 +Y 是"前"，`direction.Y` 取 +cos。
		var direction = new Vector3(
			MathF.Sin(yaw) * MathF.Cos(pitch),
			MathF.Cos(yaw) * MathF.Cos(pitch),
			MathF.Sin(pitch));

		var halfFov = FieldOfViewDegrees * MathF.PI / 360f;
		var distance = radius / MathF.Sin(halfFov) * Margin / MathF.Max(Zoom, 1e-3f);
		var eye = target + direction * distance;

		var forward = Vector3.Normalize(target - eye);
		var worldUp = Vector3.UnitZ;
		var right = Vector3.Cross(forward, worldUp);
		if (right.LengthSquared() < 1e-12f)
		{
			// 视线与 +Z 平行：换一个参考轴（|pitch| ≤ 89.5 时不该发生，留着防御坏输入）。
			right = Vector3.Cross(forward, Vector3.UnitY);
		}

		right = Vector3.Normalize(right);
		var up = Vector3.Normalize(Vector3.Cross(right, forward));

		var roll = RollDegrees * MathF.PI / 180f;
		if (roll != 0f)
		{
			var sin = MathF.Sin(roll);
			var cos = MathF.Cos(roll);
			up = Vector3.Normalize(up * cos + right * sin);
			right = Vector3.Normalize(Vector3.Cross(forward, up));
		}

		// 近平面：贴着主体前缘，但绝不取 0（透视除法会炸）。
		var near = MathF.Max(distance - radius * 1.5f, radius * 1e-3f);

		return new PreviewCameraSetup(
			eye, target, forward, right, up,
			MathF.Tan(halfFov), near, aspectRatio);
	}

	/// <summary>日志/调试用的一行规格。</summary>
	public string Describe()
		=> string.Create(
			CultureInfo.InvariantCulture,
			$"pivot=({Pivot.X:0.###},{Pivot.Y:0.###},{Pivot.Z:0.###}) yaw={YawDegrees:0.##} "
			+ $"pitch={PitchDegrees:0.##} roll={RollDegrees:0.##} zoom={Zoom:0.##} fov={FieldOfViewDegrees:0.##}");

	private static float QuantizeComponent(float value, int decimals)
	{
		// 非有限值**不**在这里洗干净：让它原样留着，由 IsRenderable 明确拒掉。
		// 否则 NaN 会被悄悄变成 0°，图看起来「正常」，问题被藏起来。
		if (!float.IsFinite(value)) return value;
		var scale = MathF.Pow(10f, decimals);
		var rounded = MathF.Round(value * scale, MidpointRounding.AwayFromZero) / scale;
		// 负零在文件名里会变成 "-0.00"，和 "0.00" 是两个名字。
		return rounded == 0f ? 0f : rounded;
	}

	private static float Wrap(float value, float period)
	{
		var wrapped = value % period;
		if (wrapped < 0f) wrapped += period;
		return wrapped == 0f ? 0f : wrapped;
	}
}

/// <summary>一次渲染实际使用的相机基（已解算，不再变动）。</summary>
public readonly record struct PreviewCameraSetup(
	Vector3 Eye,
	Vector3 Target,
	Vector3 Forward,
	Vector3 Right,
	Vector3 Up,
	float TanHalfFov,
	float Near,
	float AspectRatio)
{
	/// <summary>世界坐标 → 相机坐标（x 右、y 上、z 向前）。</summary>
	public Vector3 ToCamera(Vector3 world)
	{
		var delta = world - Eye;
		return new Vector3(
			Vector3.Dot(delta, Right),
			Vector3.Dot(delta, Up),
			Vector3.Dot(delta, Forward));
	}
}
