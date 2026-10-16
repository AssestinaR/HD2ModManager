using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2SourceModel;
using HD2SourceMigration.Retarget;
using HD2SourceMigration.Segmentation;
using HD2SourceMigration.VpkConversion;

namespace HD2SourceMigration.Tests;

/// <summary>
/// 写出的 Unit 必须满足 Python 侧 <c>review_unit_contract.py</c> 断言的**三条契约**。
/// <para>
/// 这三条都是<b>静态可检</b>的，本应在第一次上机前就跑通——事实上"上半身上移、手臂变长"
/// 和"什么都不显示"两个实机故障，分别对应第 1 条和第 3 条。把它们钉成测试，
/// 是为了后面移植材质时不再靠实机试错。
/// </para>
/// <list type="number">
/// <item><b>网格变换恒等</b>：Python 的 <c>armor-normalize</c> 会把外壳归一，顶点直接是世界坐标。
/// 不归一时 <c>CanonicalLodBonePaletteCompiler</c> 算出的 <c>mesh⁻¹ · bone⁻¹</c> 会多出一项，
/// 躯干再上移 1.17 m、手臂再外移 0.57 m、左右腿再镜像。</item>
/// <item><b>FK 自洽</b>：<c>M[k] = L[k] · M[parent]</c>（行向量，平移在最后一行）。父子链错了会让
/// 正运动学整体错位。</item>
/// <item><b>顶点落在包围盒内</b>：<c>MeshInfo</c> 那 7 个 float 的 <c>CullingBounds</c> 必须按
/// 自己的顶点重算；<c>CanonicalUnitRebuilder</c> 不碰它，不重算就留下外壳的盒子，游戏剔除会丢网格。</item>
/// </list>
/// <para>第 4 条（逆绑定 <c>B[i] · M[realIndices[i]] == 单位阵</c>）写在 GPU 缓冲里，
/// 只能读回来验，由 <c>RealUnitContractTests</c> 的往返用例覆盖。</para>
/// </summary>
public sealed class UnitContractTests
{
	private const string DefaultVpkPath =
		@"D:\SteamLibrary\steamapps\common\Left 4 Dead 2\left4dead2\addons\Lunalice_Zoey.vpk";

	private const string DefaultAvatarTocPath =
		@"E:\Data\source\repos\WpfApp1\tmp\rig-lab\avatar\unit_0x4d1c334d294dfa97.toc";

	private const string DefaultBoneHashPath =
		@"E:\Data\source\repos\WpfApp1\tmp\pb-ref\HD2PhysBone-ReferenceSource-main\kit\tools\bonehash.txt";

	private const string DefaultGameDataPath = @"D:\SteamLibrary\steamapps\common\Helldivers 2\data";
	private const string DefaultDonorArchive = "d923906f8b815987";
	private const ulong DefaultDonorFileId = 0xcae35ee15168145f;

	private static string? Resolve(string variable, string fallback, bool directory = false)
	{
		var configured = Environment.GetEnvironmentVariable(variable);
		if (!string.IsNullOrWhiteSpace(configured)
			&& (directory ? Directory.Exists(configured) : File.Exists(configured)))
			return configured;
		return (directory ? Directory.Exists(fallback) : File.Exists(fallback)) ? fallback : null;
	}

	[Fact]
	public async Task RealData_WrittenUnitSatisfiesThePythonUnitContract()
	{
		var vpkPath = Resolve("HD2_LEGACY_TEST_VPK", DefaultVpkPath);
		var tocPath = Resolve("HD2_AVATAR_TOC", DefaultAvatarTocPath);
		var boneHashPath = Resolve("HD2_BONEHASH", DefaultBoneHashPath);
		var gameData = Resolve("HD2_GAME_DATA", DefaultGameDataPath, directory: true);
		if (vpkPath is null || tocPath is null || boneHashPath is null || gameData is null)
			return;

		var inputs = SourceRetargetPipeline.LoadSource(vpkPath, "survivor_teenangst");
		var rig = SourceRetargetPipeline.LoadRig(tocPath, boneHashPath);
		var result = SourceRetargetPipeline.Run(inputs, rig);
		var avatar = UnitMeshReader.ReadTransformInfoFromUnitToc(File.ReadAllBytes(tocPath));
		var split = SourceSegmentation.Split(inputs.Model, inputs.Geometry, new BodyRegionResolver(rig), result.Resolved);

		var archive = Environment.GetEnvironmentVariable("HD2_M2_DONOR_ARCHIVE") ?? DefaultDonorArchive;
		var donorReader = new GameDataUnitMeshReader(new GameDataPackageResolver(gameData));
		var donor = await donorReader.ReadAsync(
			archive, new AssetKey(PatchUnitMeshReader.UnitTypeId, DefaultDonorFileId), allowGlobalDependencySearch: true);

		// 每个身体部位各验一次——网格变换的问题是按槽位出现的（躯干/手臂/腿各不相同）。
		foreach (var group in split.Groups.Where(item => item.Kind == MeshGroupKind.BodyPart))
		{
			var mesh = RetargetedMeshBuilder.Build(group, inputs.Geometry, result, avatar);
			var authored = new UnitAuthor().Author(new UnitAuthorRequest(
				donor.Model, donor.Payload.TocData, avatar, mesh,
				new UnitCustomizationInfo("HelldiverCustomizationBodyType_Any", "HelldiverCustomizationSlot_Torso", "HelldiverCustomizationWeight_Medium", "HelldiverCustomizationPieceType_Undergarment")));
			Assert.True(authored.IsValid, $"{group.Name} 写出失败：{string.Join("；", authored.Diagnostics)}");
			var model = authored.Model!;

			// ---- 契约 1：网格变换恒等 ----
			foreach (var meshInfo in model.Meshes)
			{
				var matrix = model.TransformInfo.Matrices[(int)meshInfo.TransformIndex].Values;
				var identity = new[] { 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f };
				for (var i = 0; i < 16; i++)
					Assert.True(Math.Abs(matrix[i] - identity[i]) < 1e-6,
						$"{group.Name} 网格 #{meshInfo.Index} 的变换不是恒等（第 {i} 项 = {matrix[i]}）");
			}

			// ---- 契约 2：FK 自洽 M[k] = L[k] · M[parent] ----
			// 只验**渲染真正会用到的那批骨**：各 LOD 调色板里的骨 + 它们的祖先链。
			// 外壳自带的装饰性节点（不在调色板里、也不是任何调色板骨的祖先）本来就可能不自洽，
			// 那不是我们引入的、也不参与蒙皮；全表硬验会把外壳的既有噪声当成回归。
			var info = model.TransformInfo;
			var relevant = new HashSet<int>();
			foreach (var boneInfo in model.BoneInfos)
			{
				foreach (var index in boneInfo.RealIndices)
				{
					var cursor = checked((int)index);
					while (cursor >= 0 && cursor < info.NameHashes.Count && relevant.Add(cursor))
						cursor = info.Entries[cursor].ParentIndex;
				}
			}

			Assert.NotEmpty(relevant);
			foreach (var k in relevant)
			{
				var local = info.LocalTransforms[k];
				var parent = info.Entries[k].ParentIndex;
				var world = Multiply(LocalMatrix(local), k == parent ? null : info.Matrices[parent].Values);
				var actual = info.Matrices[k].Values;
				for (var i = 0; i < 16; i++)
					Assert.True(Math.Abs(world[i] - actual[i]) < 1e-4,
						$"{group.Name} 第 {k} 根骨的 FK 不自洽（第 {i} 项：{world[i]} vs {actual[i]}）");
			}

			// ---- 契约 3：顶点落在包围盒内 ----
			foreach (var meshInfo in model.Meshes.Where(item => item.LodIndex >= 0))
			{
				var raw = model.RawMeshData.First(item => item.MeshInfoIndex == meshInfo.Index);
				var bounds = meshInfo.CullingBounds.Values;
				Assert.Equal(7, bounds.Count);
				foreach (var vertex in raw.Vertices)
				{
					var position = vertex.Components.First(component => component.Type == 0).FloatValues;
					Assert.True(position[0] >= bounds[0] - 1e-4 && position[0] <= bounds[3] + 1e-4
						&& position[1] >= bounds[1] - 1e-4 && position[1] <= bounds[4] + 1e-4
						&& position[2] >= bounds[2] - 1e-4 && position[2] <= bounds[5] + 1e-4,
						$"{group.Name} 网格 #{meshInfo.Index} 有顶点落在包围盒外：({position[0]}, {position[1]}, {position[2]})");
				}
			}
		}
	}

	/// <summary>行向量约定（平移在最后一行）的 4×4 乘；<paramref name="b"/> 为 null 表示父是自身。</summary>
	private static double[] Multiply(IReadOnlyList<double> a, IReadOnlyList<float>? b)
	{
		if (b is null)
			return a.ToArray();
		var output = new double[16];
		for (var row = 0; row < 4; row++)
		{
			for (var column = 0; column < 4; column++)
			{
				var sum = 0.0;
				for (var k = 0; k < 4; k++)
					sum += a[(row * 4) + k] * b[(k * 4) + column];
				output[(row * 4) + column] = sum;
			}
		}

		return output;
	}

	/// <summary>
	/// 把局部变换摊成行向量约定的 4×4。旋转按 <b>行</b> 缩放（等价 Python 的
	/// <c>np.diag(scale) @ rot.reshape(3,3)</c>），平移放最后一行。
	/// </summary>
	private static double[] LocalMatrix(UnitLocalTransform local)
	{
		var scale = local.Scale;
		var rotation = local.Rotation;
		var output = new double[16];
		for (var row = 0; row < 3; row++)
		{
			for (var column = 0; column < 3; column++)
				output[(row * 4) + column] = rotation[(row * 3) + column] * scale[row];
		}

		output[12] = local.Position[0];
		output[13] = local.Position[1];
		output[14] = local.Position[2];
		output[15] = 1.0;
		return output;
	}
}
