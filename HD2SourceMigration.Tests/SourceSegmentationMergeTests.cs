using HD2SourceModel;
using HD2SourceMigration.Retarget;
using HD2SourceMigration.Segmentation;

namespace HD2SourceMigration.Tests;

/// <summary>
/// 切分 + 合并的<b>结构</b>回归测试，用手搭的模型，不依赖任何真实 VPK。
/// <para>
/// 之所以要有一份纯结构的：这一类 bug 一次都不报错，产物也"看起来正常"——
/// 表现只是"某个部位不见了"。已经踩过三次，三次的形态各不相同：
/// </para>
/// <list type="number">
/// <item>把一个<b>空变体</b>（0 顶点的 model）当成"这个 bodypart 可切换"的证据，于是本体被降级成配件。</item>
/// <item>身体本身挂在<b>可切换 bodypart</b> 里（"胸大小""体型""衣服开/关"），该部位在固定 bodypart
/// 里一个三角形都没有，合并时"没有本体可挂"→ 整具身体被静默丢掉，只剩头。
/// （实测「L4N silent_code_shinano」：17 个 bodypart 里没有一个是固定躯干，
/// 26 份里 25 份被当配件丢掉，出来的 Unit 只有 Head。）</item>
/// <item>同一个 bodypart 的多个变体<b>全并进一个 Unit</b>：源侧它们互斥（同一时刻只画一个），
/// 叠着画就是重影。</item>
/// </list>
/// </summary>
public sealed class SourceSegmentationMergeTests
{
	private const int HipsBone = 0;
	private const int SpineBone = 1;
	private const int HeadBone = 2;

	/// <summary>最小的 HD2 骨架：只要那三根地标骨在，部位就能算出来。</summary>
	private static Hd2Rig Rig() => new(
	[
		new Hd2RigBone(HipsBone, "hips", -1, Mat4.Identity),
		new Hd2RigBone(SpineBone, "spine1", HipsBone, Mat4.Identity),
		new Hd2RigBone(HeadBone, "head", SpineBone, Mat4.Identity),
	]);

	private static Dictionary<int, int> Resolved() => new()
	{
		[HipsBone] = HipsBone,
		[SpineBone] = SpineBone,
		[HeadBone] = HeadBone,
	};

	/// <summary>搭一份几何：每个 bodypart 一个 model，三角形按传入的骨下标加权。</summary>
	private sealed class GeometryBuilder
	{
		private readonly List<SourceVertex> _vertices = [];
		private readonly List<SourceTriangle> _triangles = [];
		private readonly List<SourceGeometryPart> _parts = [];

		public void AddPart(string bodyPart, int bodyPartIndex, string model, int modelIndex, int triangles, int bone)
		{
			var vertexStart = _vertices.Count;
			var triangleStart = _triangles.Count;
			for (var t = 0; t < triangles; t++)
			{
				var baseIndex = _vertices.Count;
				for (var corner = 0; corner < 3; corner++)
				{
					_vertices.Add(new SourceVertex(
						new Vec3(corner, t, 0),
						new Vec3(0, 0, 1),
						0,
						0,
						new SourceVertexWeight(bone, 1),
						new SourceVertexWeight(bone, 0),
						new SourceVertexWeight(bone, 0)));
				}

				_triangles.Add(new SourceTriangle(baseIndex, baseIndex + 1, baseIndex + 2, 0));
			}

			_parts.Add(new SourceGeometryPart(
				bodyPart,
				model,
				vertexStart,
				_vertices.Count - vertexStart,
				triangleStart,
				triangles)
			{
				BodyPartIndex = bodyPartIndex,
				ModelIndex = modelIndex,
			});
		}

		/// <summary>一个三角形一根骨：用来造"同一组里横跨多个部位"的几何。</summary>
		public void AddPartMixed(string bodyPart, int bodyPartIndex, string model, int modelIndex, params int[] bonesPerTriangle)
		{
			var vertexStart = _vertices.Count;
			var triangleStart = _triangles.Count;
			for (var t = 0; t < bonesPerTriangle.Length; t++)
			{
				var bone = bonesPerTriangle[t];
				var baseIndex = _vertices.Count;
				for (var corner = 0; corner < 3; corner++)
				{
					_vertices.Add(new SourceVertex(
						new Vec3(corner, t, 0),
						new Vec3(0, 0, 1),
						0,
						0,
						new SourceVertexWeight(bone, 1),
						new SourceVertexWeight(bone, 0),
						new SourceVertexWeight(bone, 0)));
				}

				_triangles.Add(new SourceTriangle(baseIndex, baseIndex + 1, baseIndex + 2, 0));
			}

			_parts.Add(new SourceGeometryPart(
				bodyPart,
				model,
				vertexStart,
				_vertices.Count - vertexStart,
				triangleStart,
				bonesPerTriangle.Length)
			{
				BodyPartIndex = bodyPartIndex,
				ModelIndex = modelIndex,
			});
		}

		public SourceGeometry Build() => new(_vertices, _triangles, _parts);
	}

	private static SourceModelDocument Model(params SourceBodyPart[] bodyParts)
		=> new(49, string.Empty, 0, [], bodyParts, [], []);

	private static SourceBodyPart BodyPart(string name, params string[] models)
		=> new(name, models.Select(model => new SourceBodyPartModel(model, 1, 0, [new SourceMesh(0, 1, 0)])).ToArray());

	private static (SourceSegmentation.Result Split, SourceGeometry Geometry) Split(
		SourceModelDocument model, SourceGeometry geometry)
		=> (SourceSegmentation.Split(model, geometry, new BodyRegionResolver(Rig()), Resolved()), geometry);

	/// <summary>
	/// 本次的真实形态：**没有固定躯干 bodypart**，整具身体挂在"胸大小"这类可切换 bodypart 里。
	/// 合并必须把这个部位补出来，而不是"没有本体可挂"就丢掉。
	/// </summary>
	[Fact]
	public void Merge_SynthesizesBodyUnit_WhenBodyLivesInsideSwitchableBodyPart()
	{
		var model = Model(
			BodyPart("head", "head.smd"),
			BodyPart("⭐胸大小-素体", "body.smd", string.Empty));
		var builder = new GeometryBuilder();
		builder.AddPart("head", 0, "head.smd", 0, 4, HeadBone);
		builder.AddPart("⭐胸大小-素体", 1, "body.smd", 0, 10, SpineBone);
		var geometry = builder.Build();

		var (split, _) = Split(model, geometry);
		Assert.Single(split.BodyParts);
		Assert.Equal(BodyRegion.Head, split.BodyParts.Single().Region);

		var merge = SourceSegmentation.MergeComponentsIntoBodyParts(geometry, split.Groups);
		var byRegion = merge.Groups.ToDictionary(group => group.Region);

		Assert.True(byRegion.ContainsKey(BodyRegion.Chest), "躯干 Unit 必须被补出来，否则整具身体消失");
		Assert.Equal(10, byRegion[BodyRegion.Chest].TriangleCount);
		Assert.Equal(4, byRegion[BodyRegion.Head].TriangleCount);
		Assert.Contains(merge.Notes, note => note.Contains("Chest", StringComparison.Ordinal));
	}

	/// <summary>
	/// 空变体（0 顶点）是"作者把这块标成了可切换"的证据，不能让本体降级成配件，
	/// 也不能反过来把配件当成本体——判据是 MDL 里声明的 model 个数。
	/// </summary>
	[Fact]
	public void Split_CountsBlankVariantAsSwitch_ButOnlySwitchablePartsBecomeComponents()
	{
		var model = Model(
			BodyPart("head", "head.smd"),
			BodyPart("Base", "body.smd"),
			BodyPart("帽子", "hat.smd", string.Empty));
		var builder = new GeometryBuilder();
		builder.AddPart("head", 0, "head.smd", 0, 4, HeadBone);
		builder.AddPart("Base", 1, "body.smd", 0, 10, SpineBone);
		builder.AddPart("帽子", 2, "hat.smd", 0, 6, HeadBone);
		var geometry = builder.Build();

		var (split, _) = Split(model, geometry);
		Assert.Equal(["帽子"], split.Components.Select(group => group.Name));
		// 只有真的长了三角形的部位才成为本体：这里是 Head 与 Chest（Base）。
		Assert.Equal(
			new[] { BodyRegion.Chest, BodyRegion.Head }.OrderBy(region => region),
			split.BodyParts.Select(group => group.Region).OrderBy(region => region));

		var merge = SourceSegmentation.MergeComponentsIntoBodyParts(geometry, split.Groups);
		Assert.Equal(4 + 6, merge.Groups.Single(group => group.Region == BodyRegion.Head).TriangleCount);
		// 帽子只有一个有几何的变体，"放弃了变体"这句话不该出现。
		Assert.DoesNotContain(merge.Notes, note => note.Contains("只并入了默认变体", StringComparison.Ordinal));
	}

	/// <summary>
	/// 同一 bodypart 的两个变体互斥：merged 里只能有一个，另一个必须被明确报出来。
	/// </summary>
	[Fact]
	public void Merge_KeepsOnlyDefaultVariantOfOneBodyPart()
	{
		var model = Model(
			BodyPart("head", "head.smd"),
			BodyPart("Base", "body.smd"),
			BodyPart("口罩", "maskA.smd", "maskB.smd"));
		var builder = new GeometryBuilder();
		builder.AddPart("head", 0, "head.smd", 0, 4, HeadBone);
		builder.AddPart("Base", 1, "body.smd", 0, 10, SpineBone);
		builder.AddPart("口罩", 2, "maskA.smd", 0, 6, HeadBone);
		builder.AddPart("口罩", 2, "maskB.smd", 1, 20, HeadBone);
		var geometry = builder.Build();

		var (split, _) = Split(model, geometry);
		Assert.Equal(2, split.Components.Count(group => group.Name == "口罩"));

		var merge = SourceSegmentation.MergeComponentsIntoBodyParts(geometry, split.Groups);
		Assert.Equal(4 + 6, merge.Groups.Single(group => group.Region == BodyRegion.Head).TriangleCount);
		Assert.Contains(merge.Notes, note => note.Contains("口罩", StringComparison.Ordinal));
		// 变体 20 个三角形一个都不该并进去，也不能凭空丢掉不报。
		Assert.DoesNotContain(merge.Groups.SelectMany(group => group.TriangleIndices), index => index >= 20);
	}

	/// <summary>没有可切换部件时，merge 就是原样的七份本体（不能凭空多出或少掉部位）。</summary>
	[Fact]
	public void Merge_WithoutComponents_ReturnsFixedBodyPartsUnchanged()
	{
		var model = Model(
			BodyPart("head", "head.smd"),
			BodyPart("Base", "body.smd"));
		var builder = new GeometryBuilder();
		builder.AddPart("head", 0, "head.smd", 0, 4, HeadBone);
		builder.AddPart("Base", 1, "body.smd", 0, 10, HipsBone);
		var geometry = builder.Build();

		var (split, _) = Split(model, geometry);
		var merge = SourceSegmentation.MergeComponentsIntoBodyParts(geometry, split.Groups);

		Assert.Empty(split.Components);
		Assert.Equal(split.BodyParts.Select(group => group.Region).OrderBy(region => region),
			merge.Groups.Select(group => group.Region).OrderBy(region => region));
		Assert.Empty(merge.Notes);
	}

	/// <summary>
	/// **可切换部件横跨多个部位时，必须按三角形归位，而不是整组算一个部位。**
	/// <para>
	/// 固定 bodypart 在 <see cref="SourceSegmentation.Split"/> 里已经按骨骼切好了；
	/// 但**可切换部件（多变体 bodypart）**整块只带一个"主导区域"。
	/// 实测事故（2026-11-03）<c>l4n silent_code_shinano信浓</c>：26 个 bodypart 是**整套衣服**
	/// （素体 / bodyacc / 手套 / 袖套 / 高跟鞋…），没有"左右腿"这种 bodypart，
	/// 于是 26 个组只落进 5 个槽，**右腿和右臂一个 Unit 都没有** —— 上机就是"身体转换不出来"。
	/// </para>
	/// <para>
	/// 这里用一个"多变体 bodypart，其默认变体的三角形分属左右大腿"的几何钉住它：
	/// 传 <c>vertexRegions</c> 时分出两个部位，不传时保留旧行为（一个部位）。
	/// </para>
	/// </summary>
	[Fact]
	public void SwitchablePartTrianglesArePlacedByTheirOwnRegionNotTheDominantOne()
	{
		// 骨架：胯 + 左右大腿（地标骨名见 BodyRegionResolver）。
		var rig = new Hd2Rig(
		[
			new Hd2RigBone(0, "hips", -1, Mat4.Identity),
			new Hd2RigBone(1, "l_thigh", 0, Mat4.Identity),
			new Hd2RigBone(2, "r_thigh", 0, Mat4.Identity),
		]);
		var resolved = new Dictionary<int, int> { [0] = 0, [1] = 1, [2] = 2 };

		var builder = new GeometryBuilder();
		// 两个变体 → 这个 bodypart 会被判成"可切换部件"。
		// 默认变体（modelIndex 更小）同时含左右大腿的三角形；另一个变体只占一个三角形。
		builder.AddPartMixed("garment", 0, "g0.smd", 0, 1, 1, 2, 2);
		builder.AddPartMixed("garment", 0, "g1.smd", 1, 1);
		var geometry = builder.Build();

		var split = SourceSegmentation.Split(
			Model(BodyPart("garment", "g0.smd", "g1.smd")), geometry, new BodyRegionResolver(rig), resolved);
		Assert.Empty(split.BodyParts);          // 多变体 → 不是身体本体
		Assert.Equal(2, split.Components.Count());          // 两个变体各成一个 group
		var chosen = split.Components.Single(group => group.SourceModelIndex == 0);
		Assert.Equal(4, chosen.TriangleCount);  // 默认变体（modelIndex 更小）

		// ① 不传 vertexRegions：旧行为 —— 整组落进它的主导区域。
		var legacy = SourceSegmentation.MergeComponentsIntoBodyParts(geometry, split.Groups);
		Assert.Single(legacy.Groups);

		// ② 传 vertexRegions：按三角形归位，左右腿各一份。
		var splitByRegion = SourceSegmentation.MergeComponentsIntoBodyParts(
			geometry, split.Groups, split.VertexRegions);
		Assert.Equal(2, splitByRegion.Groups.Count);
		Assert.Equal(
			new[] { BodyRegion.LeftLeg, BodyRegion.RightLeg },
			splitByRegion.Groups.Select(group => group.Region).ToArray());
		Assert.Equal(2, splitByRegion.Groups[0].TriangleCount);
		Assert.Equal(2, splitByRegion.Groups[1].TriangleCount);

		// 三角形一个都不能丢：被放弃的那个变体只有 1 个三角形，剩下 4 个都要在。
		var placed = splitByRegion.Groups.SelectMany(group => group.TriangleIndices).ToArray();
		Assert.Equal(4, placed.Length);
		Assert.Equal(4, placed.Distinct().Count());
	}
}

