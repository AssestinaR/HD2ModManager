using System.Text;
using HD2SourceModel;
using HD2SourceMigration.VpkConversion;

namespace HD2SourceMigration.Tests;

/// <summary>
/// 眼球投影方向（<see cref="EyeFrame"/>）：从骨架推"右"和"上"。
/// <para>
/// <b>为什么不能写死坐标轴。</b>两套实测模型都把"左右"放在 X 上，但"朝前"在两套里
/// 一个是 Y、另一个的 Y/Z 只差 3%（0.652 vs 0.635）——按"眼球最薄的那一维 = 朝前"
/// 去判，后者会把 X 当成上下，整只眼睛被转 90°。骨架（右 = 左锁骨 − 右锁骨、
/// 上 = 头 − 髋）在两套上都成立，而且与作者在 VMT 里给的 EyeOrigin/IrisU/IrisV 同义。
/// </para>
/// </summary>
public sealed class EyeFrameTests
{
	[Fact]
	public void FrameComesFromTheSkeletonNotFromTheCoordinateAxes()
	{
		// 一个"Z 朝上、X 是左右"的骨架（与两套实测模型一致）。
		var bones = new[]
		{
			Bone(0, "ValveBiped.Bip01_Pelvis", new Vec3(0, 0, 34)),
			Bone(1, "ValveBiped.Bip01_R_Clavicle", new Vec3(-1.1, 0, 56)),
			Bone(2, "ValveBiped.Bip01_L_Clavicle", new Vec3(1.1, 0, 56)),
			Bone(3, "ValveBiped.Bip01_Head1", new Vec3(0, 0, 63)),
		};

		var frame = EyeFrame.FromBones(bones);

		Assert.NotNull(frame);
		// 上 = 头 − 髋 = +Z；右 = 左锁骨 − 右锁骨 = +X。
		Assert.Equal(1.0, frame!.Up.Z, 3);
		Assert.Equal(0.0, frame.Up.X, 3);
		Assert.Equal(1.0, frame.Right.X, 3);
		Assert.Equal(0.0, frame.Right.Z, 3);
	}

	[Fact]
	public void FrameIsNullWithoutTheBonesItNeeds()
	{
		Assert.Null(EyeFrame.FromBones(null));
		Assert.Null(EyeFrame.FromBones([]));
		// 只有头骨：推不出"上"（需要髋当参照）。
		Assert.Null(EyeFrame.FromBones([Bone(0, "ValveBiped.Bip01_Head1", new Vec3(0, 0, 63))]));
	}

	/// <summary>同一具骨架上的两个眼球，投影必须用同一个方向（右/上来自骨架，与眼球本身无关）。</summary>
	[Fact]
	public void BothEyesShareTheSameProjectionFrame()
	{
		var bones = new[]
		{
			Bone(0, "ValveBiped.Bip01_Pelvis", new Vec3(0, 0, 34)),
			Bone(1, "ValveBiped.Bip01_R_Clavicle", new Vec3(-1.1, 0, 56)),
			Bone(2, "ValveBiped.Bip01_L_Clavicle", new Vec3(1.1, 0, 56)),
			Bone(3, "ValveBiped.Bip01_Head1", new Vec3(0, 0, 63)),
		};
		var frame = EyeFrame.FromBones(bones)!;

		// 左眼在 +X、右眼在 −X（与实测一致），UV 的横/竖都按同一个 right/up 投影。
		var geometry = EyePair();
		var source = new EyeColorSource("eye", 0.25, 0.25, 0.75, 0.75);
		var (projected, _) = EyeUv.Reproject(geometry, ["eyeball_l", "eyeball_r"], _ => source, frame);

		// 两个眼球沿 +X 的顶点，u 必须朝同一个方向变（不是镜像），说明用的是同一套轴。
		for (var i = 0; i < 2; i++)
		{
			var v0 = projected.Vertices[i * 4];
			var v1 = projected.Vertices[(i * 4) + 1];
			Assert.True(v1.U > v0.U);
			Assert.True(v1.V < v0.V);   // +Z（上）→ 更小的 V（图像顶部）
		}
	}

	private static SourceBone Bone(int index, string name, Vec3 origin) => new(
		index, name, -1,
		Mat4.Identity,
		new Mat4(1, 0, 0, origin.X, 0, 1, 0, origin.Y, 0, 0, 1, origin.Z, 0, 0, 0, 1),
		null);

	/// <summary>两个眼球各四个顶点：0=(-X,-Z) 1=(+X,+Z) 2=(+X,-Z) 3=(-X,+Z)。</summary>
	private static SourceGeometry EyePair()
	{
		var vertices = new List<SourceVertex>();
		var triangles = new List<SourceTriangle>();
		foreach (var (centerX, materialIndex) in new[] { (-1.5, 0), (1.5, 1) })
		{
			var first = vertices.Count;
			foreach (var (dx, dz) in new[] { (-0.3, -0.3), (0.3, 0.3), (0.3, -0.3), (-0.3, 0.3) })
			{
				vertices.Add(new SourceVertex(
					new Vec3(centerX + dx, 0, 63 + dz),
					new Vec3(0, 1, 0),
					0f, 1f,
					new SourceVertexWeight(0, 1),
					new SourceVertexWeight(0, 0),
					new SourceVertexWeight(0, 0)));
			}

			triangles.Add(new SourceTriangle(first, first + 1, first + 2, materialIndex));
			triangles.Add(new SourceTriangle(first, first + 3, first + 1, materialIndex));
		}

		return new SourceGeometry(vertices, triangles, []);
	}
}
