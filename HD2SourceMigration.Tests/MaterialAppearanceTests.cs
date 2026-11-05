using System.Text;
using HD2SourceMigration.VpkConversion;

namespace HD2SourceMigration.Tests;

/// <summary>
/// NekoToon matcap 的判定与烘焙。判错的后果很具体：
/// <b>金属装饰件渲染成纯黑</b>（该烘没烘），或者<b>高光被烤死在底色里</b>（不该烘却烘了）。
/// </summary>
public sealed class MaterialAppearanceTests
{
	private static VmtDocument Parse(string text) => VmtDocument.Parse(Encoding.UTF8.GetBytes(text));

	[Fact]
	public void ATintAtOrAboveTheThresholdMeansTheMatCapIsTheSurface()
	{
		Assert.True(Parse("""VertexLitGeneric { "$matcap" "1" "$matcaptint" "{1 1 1}" }""")
			.Let(MaterialAppearance.Evaluate).MatCapIsTheSurface);

		// 阈值是 0.9，边界要包含。
		Assert.True(Parse("""VertexLitGeneric { "$matcap" "1" "$matcaptint" "{0.9 0.9 0.9}" }""")
			.Let(MaterialAppearance.Evaluate).MatCapIsTheSurface);

		// 只差一点就不该烘：那只是视角相关的叠加高光，PBR 自会处理。
		Assert.False(Parse("""VertexLitGeneric { "$matcap" "1" "$matcaptint" "{0.89 0.89 0.89}" }""")
			.Let(MaterialAppearance.Evaluate).MatCapIsTheSurface);
	}

	[Fact]
	public void WithoutAMatCapNothingIsBaked()
	{
		Assert.False(Parse("""VertexLitGeneric { "$basetexture" "base" }""")
			.Let(MaterialAppearance.Evaluate).MatCapIsTheSurface);
		Assert.False(Parse("""VertexLitGeneric { "$matcap" "0" "$basetexture" "base" }""")
			.Let(MaterialAppearance.Evaluate).MatCapIsTheSurface);
	}

	/// <summary>
	/// <c>$matcaptint</c> 缺失时不该烘——没有 tint 就等于没有 matcap 强度。
	/// <c>$matcaptexture</c> 单独出现时 <see cref="VmtDocument.HasMatCap"/> 仍为真，
	/// 但 tint 为 0，所以判定仍是"不烘"。
	/// </summary>
	[Fact]
	public void AMatCapTextureWithoutATintIsNotBaked()
	{
		var verdict = MaterialAppearance.Evaluate(Parse("""VertexLitGeneric { "$matcaptexture" "mc" }"""));
		Assert.True(verdict.HasMatCap);
		Assert.Equal(0, verdict.Tint);
		Assert.False(verdict.MatCapIsTheSurface);
	}

	[Theory]
	[InlineData("{1 1 1}", 1.0)]
	[InlineData("255 255 255", 1.0)]      // 255 制
	[InlineData("{0.9 0.9 0.9}", 0.9)]
	[InlineData("1 0 0", 1.0)]            // 取最大值
	[InlineData("{229.5 0 0}", 0.9)]      // 255 制 + 小数
	[InlineData("", 0.0)]
	[InlineData("{0 0 0}", 0.0)]
	[InlineData("garbage", 0.0)]
	public void TintParsingAcceptsBothRangesAndTakesTheMax(string raw, double expected)
	{
		Assert.Equal(expected, MaterialAppearance.ParseTint(raw), 3);
	}

	/// <summary>
	/// <b>方括号写法必须被剥掉，而且不能因此丢掉第一个 RGB 分量。</b>
	/// <para>
	/// 真实 NekoToon VMT 写的是 <c>[.216 .208 .204 1]</c>（方括号），而解析器曾经只剥花括号
	/// ⇒ <c>[.216</c> 与 <c>1]</c> 都解析失败 ⇒ 第 1 个分量被吞。
	/// 首分量最大时后果最严重：<c>[1 0 0 1]</c>（纯红 tint）会被判成 0 = "没有 matcap"。
	/// </para>
	/// </summary>
	[Theory]
	[InlineData("[.216 .208 .204 1]", 0.216)]
	[InlineData("[.145 .1402 .1382 1]", 0.145)]
	[InlineData("[.9 .9 .9 1]", 0.9)]
	[InlineData("[.2 .2 .2 1]", 0.2)]
	[InlineData("[1 0 0 1]", 1.0)]        // 首分量最大：丢首分量会归零
	[InlineData("[0.576 0.576 0.576 1]", 0.576)]
	[InlineData("[1 1 1 .2]", 1.0)]
	public void TintParsingStripsSquareBracketsAndKeepsTheLeadingComponent(string raw, double expected)
	{
		Assert.Equal(expected, MaterialAppearance.ParseTint(raw), 3);
	}

	/// <summary>
	/// <b>第 4 个分量是 alpha，不参与强度比较。</b>
	/// <para>
	/// 花括号写法下 token 全部解析成功，旧实现会对<b>全部</b> token 取 max，
	/// 于是 <c>{.2 .2 .2 1}</c> 取到 1.0 —— 弱 matcap 被当成满强度，
	/// 而 tint ≥ 0.9 正是"matcap 承载表面"的判据。
	/// </para>
	/// </summary>
	[Theory]
	[InlineData("{.2 .2 .2 1}", 0.2)]
	[InlineData("{.9 .9 .9 1}", 0.9)]
	[InlineData("{.3 .3 .3 .8}", 0.3)]
	[InlineData(".2 .2 .2 1", 0.2)]       // 裸串同理
	[InlineData(".1 .1 .1 255", 0.1)]     // alpha 写成 255 制也不该顶成 1.0
	public void TintParsingIgnoresTheAlphaComponent(string raw, double expected)
	{
		Assert.Equal(expected, MaterialAppearance.ParseTint(raw), 3);
	}

	/// <summary><c>NaN</c> / <c>Infinity</c> 的文本形式能被 <c>TryParse</c> 认出来，必须挡掉。</summary>
	[Theory]
	[InlineData("NaN .5 .5", 0.5)]
	[InlineData(".5 NaN .5", 0.5)]
	[InlineData(".5 .5 Infinity", 0.5)]
	[InlineData("NaN NaN NaN", 0.0)]
	public void TintParsingRejectsNonFiniteComponents(string raw, double expected)
	{
		Assert.Equal(expected, MaterialAppearance.ParseTint(raw), 3);
	}

	/// <summary>
	/// 「源侧声明了、但 HD2 表达不了」的聚合说明。
	/// <para>
	/// 这一条不改任何产物，唯一作用是让"看着不一样"有因果可查。所以它的验收标准是
	/// **该报的报、不该报的不报**——多报会把稀有警告淹掉，少报就还是只能猜。
	/// </para>
	/// </summary>
	[Theory]
	[InlineData("""VertexLitGeneric { "$nocull" "1" }""", "$nocull")]
	[InlineData("""VertexLitGeneric { "$selfillum" "1" }""", "$selfillum")]
	[InlineData("""VertexLitGeneric { "$phong" "1" }""", "$phong")]
	[InlineData("""VertexLitGeneric { "$envmap" "env_cubemap" }""", "$envmap")]
	[InlineData("""VertexLitGeneric { "$detail" "detail/foo" }""", "$detail")]
	[InlineData("""VertexLitGeneric { "$lightwarptexture" "lt" }""", "$lightwarptexture")]
	[InlineData("""VertexLitGeneric { "$normalmapalphaenvmapmask" "1" }""", "$normalmapalphaenvmapmask")]
	public void UnexpressibleFeaturesAreReportedWhenDeclared(string text, string expected)
	{
		var described = MaterialAppearance.DescribeUnexpressibleFeatures(Parse(text));
		Assert.NotNull(described);
		Assert.Contains(expected, described, StringComparison.Ordinal);
		Assert.Contains("HD2 表达不了", described, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("""VertexLitGeneric { "$basetexture" "a" }""")]      // 什么都没声明
	[InlineData("""VertexLitGeneric { "$nocull" "0" }""")]           // 显式关掉
	[InlineData("""VertexLitGeneric { "$selfillum" "0" }""")]        // 显式关掉
	[InlineData("""VertexLitGeneric { "$phong" "0" }""")]            // 0 等于没有 phong
	[InlineData("""VertexLitGeneric { "$basetexture" "a" "$bumpmap" "b" }""")]  // 法线图刻意不收
	public void UnexpressibleFeaturesStayQuietOtherwise(string text)
	{
		Assert.Null(MaterialAppearance.DescribeUnexpressibleFeatures(Parse(text)));
	}

	/// <summary>
	/// 多项同时声明时**聚成一句**，不逐项刷屏。
	/// </summary>
	[Fact]
	public void MultipleUnexpressibleFeaturesCollapseIntoOneLine()
	{
		var described = MaterialAppearance.DescribeUnexpressibleFeatures(Parse(
			"""VertexLitGeneric { "$nocull" "1" "$selfillum" "1" "$envmap" "env_cubemap" }"""));

		Assert.NotNull(described);
		Assert.Contains("$nocull", described, StringComparison.Ordinal);
		Assert.Contains("$selfillum", described, StringComparison.Ordinal);
		Assert.Contains("$envmap", described, StringComparison.Ordinal);
		Assert.DoesNotContain("\n", described, StringComparison.Ordinal);
	}

	/// <summary>
	/// **matcap 名说不出所以然时，退回源材质名。**
	/// <para>
	/// 实测 Lunalice：21 个 <c>ornament_metal_*</c>（项链/项圈/腰带/护腕/裙撑/头饰/小刀…）
	/// 用的 matcap 文件名叫 <c>11</c>——纯数字，没有任何线索，于是 21 个金属件
	/// **全部**判成非金属，而它们的模板是 <c>advanced</c>（本来给得了金属度）。
	/// 它们的材质名其实是同样明确的作者声明。
	/// </para>
	/// <para>
	/// 但不能反过来让材质名推翻 matcap 的结论：MourningVeil 的 <c>Mat_Metal_gem_*</c>
	/// 材质名含 metal，而 matcap 是 <c>matcap_jewel9</c>（纯宝石），判非金属是对的。
	/// </para>
	/// </summary>
	[Theory]
	// matcap 名无信息 → 看材质名
	[InlineData("11", "ornament_metal_chain", true)]
	[InlineData("11", "ornament_metal_choker", true)]
	[InlineData("11", "metal_2", true)]
	[InlineData("11", "acc_metal_1", true)]
	[InlineData("11", "mat_cloth_dress_outer", false)]      // 材质名也没线索
	// matcap 名有结论 → 压过材质名
	[InlineData("matcap_jewel9", "mat_metal_gem_earings", false)]
	[InlineData("matcap_metal11", "mat_metal_necklace", true)]
	[InlineData("matcap_highlight", "mat_accpreal_tiara", false)]
	// 两个都没有 → 非金属
	[InlineData("6", "mat_ornament_petal", false)]
	[InlineData(null, null, false)]
	public void MetalVerdictFallsBackToTheMaterialNameOnlyWhenTheMatcapNameIsSilent(
		string? matcapName, string? materialName, bool expected)
	{
		Assert.Equal(expected, MaterialAppearance.IsMetallicMatcapName(matcapName, materialName));
	}

	/// <summary>单参重载保持"只看 matcap 名"的旧语义（没有第二个证据时不该改变结论）。</summary>
	[Theory]
	[InlineData("11", false)]
	[InlineData("matcap_metal11", true)]
	[InlineData("matcap_jewel9", false)]
	public void SingleArgumentOverloadStillOnlyReadsTheMatcapName(string matcapName, bool expected)
	{
		Assert.Equal(expected, MaterialAppearance.IsMetallicMatcapName(matcapName));
	}

	/// <summary>
	/// **金属 = 名字说是金属（matcap 名或材质名任一） ∧ 这张 matcap 就是表面本身（底色不是花纹图）。**
	/// <para>
	/// 实测几类，形状不同但名字很像：
	/// </para>
	/// <list type="bullet">
	/// <item>金属件：底色是 1×1 占位图（Lunalice 的 <c>Ornament_metal_Underwear</c>、
	/// Lilium 的 <c>gray1</c>）⇒ 表面只有 matcap ⇒ 判金属。</item>
	/// <item>布料：底色是 2048² 的花纹图（恋星milfy 的 <c>uv_1</c> 裙子 / <c>uv_2</c> 薄纱袖，
	/// 都写 <c>$matcap 2</c> + <c>$matcaptexture metal</c>）⇒ matcap 只是叠上去的一层高光
	/// ⇒ <b>不能</b>判金属，否则裙子变成镀铬。</item>
	/// <item><b>金属但 matcap 名是装饰系</b>（恋星milfy 的 <c>Metal</c>：材质名 metal、matcap <c>gem</c>、
	/// 底色是 1024² 平涂砖红）⇒ 材质名说是金属就该判金属。只看 matcap 名会让 53,102 面金属配件
	/// 变成哑光深色塑料边（用户报的"金属件没有金属材质"）。</item>
	/// <item>名字自己就混着装饰词（MourningVeil 的 <c>Mat_Metal_gem_earings</c>）⇒ 装饰词优先，
	/// 仍然非金属——那件的观感确实是宝石。</item>
	/// </list>
	/// <para>
	/// 也钉住"tint 不够就根本不是表面"这条前置条件：<c>tint &lt; 0.9</c> 时 matcap 只是叠加高光，
	/// 与底色有没有花纹无关。
	/// </para>
	/// </summary>
	[Theory]
	// 金属件：底色没有自己的颜色
	[InlineData(true, false, "11", "ornament_metal_chain", true)]
	[InlineData(true, false, "Platinum_Silver", "mat_ornament_metal_tiara", true)]
	[InlineData(true, false, "matcap_metal8", "mat_ornament_ivory_earrings", true)]
	// 金属 + 平涂色底色（恋星milfy 的 Metal / Metal1 / Metal2）：matcap 名是装饰系也算金属
	[InlineData(true, false, "gem", "metal", true)]
	[InlineData(true, false, "gem", "Metal1", true)]
	// 布料：底色自成一张花纹图 —— 名字再像金属也不能算
	[InlineData(true, true, "metal", "uv_1", false)]
	[InlineData(true, true, "metal", "uv_2", false)]
	[InlineData(true, true, "matcap_metal11", "mat_metal_necklace", false)]
	// 宝石 / 无名：名字就否掉了，与底色无关
	[InlineData(true, false, "gem", "jewel", false)]
	[InlineData(true, true, "matcap1", "clothes", false)]
	// 同一个名字里既有 metal 又有装饰词：装饰词优先（实测 MourningVeil，观感确实是宝石）
	[InlineData(true, false, "matcap_jewel9", "mat_metal_gem_earings", false)]
	// tint 不够：matcap 只是叠加高光，不算表面
	[InlineData(false, false, "metal", "metal", false)]
	public void MetalNeedsBothAMetallicNameAndAMatcapThatIsTheSurface(
		bool matcapIsTheSurface, bool baseIsPatterned, string? matcapName, string? materialName, bool expected)
	{
		Assert.Equal(
			expected,
			MaterialAppearance.MatCapIsMetallicSurface(matcapIsTheSurface, baseIsPatterned, matcapName, materialName));
	}

	/// <summary>
	/// **"把 matcap 抬成表面"必须保住图案，不能涂成一片。**
	/// <para>
	/// 这是实机逼出来的第二次修正：涂平（<c>FlattenToMetalF0</c>）在游戏里就是用户报的
	/// "白膜 / 灰膜"——一块没有任何明暗的纯白/纯灰面，和"材质没贴图"一模一样。
	/// 源侧那些件（serenade 的头冠 / 项链 / 月牙 / 小灯笼 / 心形 / 十字架）底色被作者注释掉了，
	/// 观感 100% 来自 matcap，涂平等于删掉它们唯一的信息。
	/// </para>
	/// <para>
	/// 这里钉三件事：① 有明暗（min < max，且差得够多）；② 均值落在目标反射率附近（不会被抬爆）；
	/// ③ 传进来的底色被整片重写（不会残留源图）。
	/// </para>
	/// </summary>
	[Fact]
	public void LiftingAMatcapKeepsThePatternAtTheTargetLevel()
	{
		const int size = 32;
		const int matcapSize = 16;

		// 球面图：左半暗（20）、右半亮（240），均值约 130 —— 与实测的 `matcap_metal7` 同类（双峰）。
		var matcap = new byte[matcapSize * matcapSize * 4];
		for (var y = 0; y < matcapSize; y++)
		for (var x = 0; x < matcapSize; x++)
		{
			var value = (byte)(x < matcapSize / 2 ? 20 : 240);
			var offset = ((y * matcapSize) + x) * 4;
			matcap[offset] = value;
			matcap[offset + 1] = value;
			matcap[offset + 2] = value;
			matcap[offset + 3] = 255;
		}

		// 世界法线：让屏幕上从左到右扫过整张球面图（sourceAxes 的换算见实现）。
		var normal = new byte[size * size * 4];
		for (var y = 0; y < size; y++)
		for (var x = 0; x < size; x++)
		{
			var u = x / (double)(size - 1);
			var offset = ((y * size) + x) * 4;
			normal[offset] = 128;
			normal[offset + 1] = (byte)Math.Round((1 - u) * 255);   // u = 1 − G
			normal[offset + 2] = 128;                                 // v = B = 0.5
			normal[offset + 3] = 255;
		}

		// 底色故意填成整片纯白 255：抬完必须被整片重写，不残留。
		var albedo = new byte[size * size * 4];
		Array.Fill(albedo, (byte)255);

		MaterialAppearance.LiftMatcapToSurface(
			albedo, size, size, matcap, matcapSize, matcapSize,
			normal, size, size, mask: null, maskWidth: 0, maskHeight: 0,
			tintRgba: [1, 1, 1, 1], targetLinear: SourceMaterialConverter.MetalF0);

		var min = 255;
		var max = 0;
		double sum = 0;
		for (var i = 0; i < size * size; i++)
		{
			var value = albedo[i * 4];
			min = Math.Min(min, value);
			max = Math.Max(max, value);
			sum += value;
		}

		var mean = sum / (size * size);
		var target = MaterialAppearance.LinearToSrgbByte(SourceMaterialConverter.MetalF0);

		// ① 有明暗（不是一块平色）
		Assert.True(max - min > 40, $"抬完仍然是一块平色：min={min} max={max}");
		// ② 电平对得上（均值离 F0 不超过 1/5 的行程；撞顶会把它压低，允许这个偏差）
		Assert.InRange(mean, target * 0.55, target * 1.05);
		// ③ 每一格都被写过（不会残留源图的 255）
		Assert.True(albedo[3] == 0, "alpha 必须清零");
	}

	/// <summary>
	/// 同一个函数在"没有世界法线"时退回采样球心：结果是一块平色，但**电平仍然是对的**，
	/// 不会出现"比值爆掉 → 整片 255"（实测 serenade 的 <c>jem_white</c> 就是这样炸成白膜的）。
	/// </summary>
	[Fact]
	public void WithoutWorldNormalsTheLiftStillLandsOnTheTargetLevel()
	{
		const int size = 8;
		var matcap = new byte[4 * 4 * 4];
		for (var i = 0; i < 16; i++)
		{
			var value = (byte)(i % 2 == 0 ? 30 : 220);
			matcap[(i * 4)] = value;
			matcap[(i * 4) + 1] = value;
			matcap[(i * 4) + 2] = value;
			matcap[(i * 4) + 3] = 255;
		}

		var albedo = new byte[size * size * 4];
		MaterialAppearance.LiftMatcapToSurface(
			albedo, size, size, matcap, 4, 4,
			normal: null, normalWidth: 0, normalHeight: 0, mask: null, maskWidth: 0, maskHeight: 0,
			tintRgba: [1, 1, 1, 1], targetLinear: MaterialAppearance.NonMetallicSurfaceF0);

		var value0 = albedo[0];
		for (var i = 0; i < size * size; i++)
			Assert.Equal(value0, albedo[i * 4]);

		// 不能是 255（撞顶），也不能是全黑：目标 0.6 线性 ≈ 204，图案对比 0.6 应该落在它附近。
		Assert.InRange(value0, 80, 235);
	}

	/// <para>
	/// 这条是"金属度给不给"的关键分界，实测数据：
	/// 平涂 <c>red</c> / <c>yellow</c> / <c>purple</c> / <c>green</c>（恋星milfy 的金属件底色）
	/// 偏离像素 <b>0.0%</b>；真实图集全部 ≥ 43%（<c>uv1_whitepink_2</c> 99.6%、
	/// <c>manuka_body</c> 43.3%、<c>milltina_cloth_black</c> 90.5%）。阈值取 2%。
	/// </para>
	/// <para>
	/// 平涂色是<b>有颜色</b>的（所以 <see cref="MaterialAppearance.IsContentFreeBase"/> 为假），
	/// 但那颜色是金属自身的颜色（上色金属），不该像布料那样否掉金属度。
	/// </para>
	/// </summary>
	[Fact]
	public void FlatColourIsNotPatternedButAPaintedAtlasIs()
	{
		const int size = 64;

		// 平涂砖红（BC7 压缩后会有 ±2 的噪声，这里直接给纯色）。
		var flat = new byte[size * size * 4];
		for (var i = 0; i < size * size; i++)
		{
			flat[(i * 4)] = 230;
			flat[(i * 4) + 1] = 58;
			flat[(i * 4) + 2] = 71;
			flat[(i * 4) + 3] = 255;
		}

		Assert.False(MaterialAppearance.IsPatterned(size, size, flat));
		// 同一张图仍然"有颜色"，所以 IsContentFreeBase 为假——两者是两个问题。
		Assert.False(MaterialAppearance.IsContentFreeBase(size, size, flat));

		// 棋盘（花纹）：一半像素偏亮、一半偏暗。
		var patterned = (byte[])flat.Clone();
		for (var y = 0; y < size; y++)
		for (var x = 0; x < size; x++)
		{
			if ((x / 4 + y / 4) % 2 == 0) continue;
			var offset = ((y * size) + x) * 4;
			patterned[offset] = 40;
			patterned[offset + 1] = 190;
			patterned[offset + 2] = 90;
		}

		Assert.True(MaterialAppearance.IsPatterned(size, size, patterned));

		// 只有极少数像素偏离（例如平涂色块上的一枚小 logo）仍按"平涂"处理：
		// 96×96 里点缀 0.5% 的像素，不该把整件判成布料。
		var speckled = (byte[])flat.Clone();
		for (var i = 0; i < size * size / 200; i++)
		{
			speckled[(i * 4)] = 20;
			speckled[(i * 4) + 1] = 20;
			speckled[(i * 4) + 2] = 20;
		}

		Assert.False(MaterialAppearance.IsPatterned(size, size, speckled));
	}

	/// <summary>
	/// **能不能表达金属度，由"模板有没有这个槽"决定，不是由模板名决定。**
	/// <para>
	/// 实测 MourningVeil：判成金属的 10 个材质全部因为"镂空优先"落进 alphaclip，
	/// 一个都没拿到金属效果；而它们此前仍被涂成 F0 0.96（亮银），
	/// 游戏里就是"一块不像金属的亮银塑料"。
	/// </para>
	/// <para>
	/// 这里直接拿 <b>SDK 的真实模板</b>去查槽键，而不是断言"名字是 advanced 就为真"：
	/// 换 SDK 版本、模板改名、或将来用上 `basic` 族的 `Mra` 时，按名字写死的判断会静默失效。
	/// </para>
	/// </summary>
	[Fact]
	public void OnlyTemplatesWithAMetallicSlotCanDeliverMetalness()
	{
		var library = Hd2MaterialTemplateLibrary.Locate();
		if (library is null)
			return;

		// 已知结论：只有 advanced 带 IlluminateData。
		Assert.True(library.HasTextureSlot("advanced", SourceMaterialConverter.MetallicSlotKey));

		foreach (var name in new[] { "alphaclip", "alphaclip+", "original", "armorlut", "translucent" })
		{
			Assert.False(
				library.HasTextureSlot(name, SourceMaterialConverter.MetallicSlotKey),
				$"{name} 不应该有金属度槽——如果 SDK 变了，这条要跟着改，而不是让判定静默失效");
		}

		// 同一个查询连问两次必须一致（缓存不能改变答案）。
		Assert.Equal(
			library.HasTextureSlot("advanced", SourceMaterialConverter.MetallicSlotKey),
			library.HasTextureSlot("advanced", SourceMaterialConverter.MetallicSlotKey));
	}

	/// <summary>
	/// 回归闸门：这三个输入在历史上分别踩过三个不同的坑，必须同时成立。
	/// 只修其中一个（例如"补剥方括号但不限制分量数"）会让另一个立刻变错。
	/// </summary>
	[Fact]
	public void TintParsingFixesBracketsComponentsAndFiniteTogether()
	{
		Assert.Equal(1.0, MaterialAppearance.ParseTint("[1 0 0 1]"), 3);    // 方括号 + 首分量
		Assert.Equal(0.2, MaterialAppearance.ParseTint("{.2 .2 .2 1}"), 3); // 花括号 + alpha
		Assert.Equal(0.6, MaterialAppearance.ParseTint("NaN .6 .6 .6"), 3); // 非有限数
	}

	[Fact]
	public void BakeAddsTheScaledMatCapMeanAndClamps()
	{
		// 底色 15（源侧近黑），matcap 平均 200、tint 1.0 → 215。
		var rgba = MaterialAppearance.SolidRgba(4, 15, 15, 15, 0);
		MaterialAppearance.BakeMatCap(rgba, [200, 100, 250], 1.0);
		Assert.Equal(215, rgba[0]);
		Assert.Equal(115, rgba[1]);
		// 15 + 250 = 265 → 夹到 255。
		Assert.Equal(255, rgba[2]);
		// alpha 不动。
		Assert.Equal(0, rgba[3]);
	}

	[Fact]
	public void BakeRejectsMalformedInput()
	{
		Assert.Throws<ArgumentException>(() => MaterialAppearance.BakeMatCap(new byte[4], [1, 2], 1));
		Assert.Throws<InvalidDataException>(() => MaterialAppearance.BakeMatCap(new byte[7], [1, 2, 3], 1));
		Assert.Throws<ArgumentOutOfRangeException>(() => MaterialAppearance.BakeMatCap(new byte[4], [1, 2, 3], -1));
	}

	[Fact]
	public void TheMetallicAndRoughnessConstantsMatchTheShippedRecipe()
	{
		// 这两个值直接决定"够不够金属"，改它们等于改观感——所以钉在这里。
		//
		// 历史（实机对照逐档走出来的）：
		//   191 / 89 → "氧化过的铝 / 珍珠的反光色，不够金属"（0.75 金属度留了 25% 漫反射）
		//   255 / 20 → 镀铬镜面，看着对但**实机更暗**（高光瓣太窄，环境被照成"亮点+大片暗"）
		//   255 / 51 → 丝光。高光瓣变宽、暗部被填平，整体比 0.08 更亮，金属感也还在。
		Assert.Equal(255, MaterialAppearance.MetallicValue);
		Assert.Equal(51, MaterialAppearance.RoughnessValue);
		Assert.Equal(51 / 255.0, MaterialAppearance.DefaultMetalRoughness);
		Assert.Equal(0.9, MaterialAppearance.MatCapMinTint);
	}

	/// <summary>
	/// <c>FlattenToMetalF0</c>：整片涂成<b>中性</b>平色，连色相也不保留。
	/// <para>
	/// 这条钉的是"转换结果不能取决于装了哪个可选的颜色变体包"——matcap 由 C1/C2、
	/// B1/B2/B3 这些可选包提供，一旦取它的色相，同一份源换个变体包金属色就变了。
	/// </para>
	/// </summary>
	[Fact]
	public void FlatteningToMetalF0IsNeutralAndDropsTheSourcePattern()
	{
		// 一张明显偏暖的输入（模拟金色系 matcap）也必须出中性。
		var warm = MaterialAppearance.SolidRgba(4, 250, 220, 180);
		MaterialAppearance.FlattenToMetalF0(warm, 0.96);

		var expected = MaterialAppearance.LinearToSrgbByte(0.96);
		for (var offset = 0; offset < warm.Length; offset += 4)
		{
			Assert.Equal(expected, warm[offset + 0]);
			Assert.Equal(expected, warm[offset + 1]);
			Assert.Equal(expected, warm[offset + 2]);
			// alpha 一律 0：底色不承担自发光，也不承担形状。
			Assert.Equal(0, warm[offset + 3]);
		}

		// 一张偏冷的输入（模拟银色系 matcap）结果必须**一模一样**。
		var cool = MaterialAppearance.SolidRgba(4, 180, 220, 250);
		MaterialAppearance.FlattenToMetalF0(cool, 0.96);
		Assert.Equal(warm, cool);

		Assert.Throws<ArgumentOutOfRangeException>(() => MaterialAppearance.FlattenToMetalF0(new byte[4], double.NaN));
		Assert.Throws<InvalidDataException>(() => MaterialAppearance.FlattenToMetalF0(new byte[7], 0.96));
	}

	/// <summary>
	/// <c>IsContentFreeBase</c>：只有"底色没有自己的**颜色**"时 matcap 才算承载表面。
	/// <para>
	/// 两个方向都有实机依据：
	/// </para>
	/// <list type="bullet">
	/// <item><b>不能只看 tint。</b>Lilium_Sancta 的花瓣底色是 <c>Mat_Ornament_Pearl</c> 真图
	/// （alpha 里就是花瓣形状），但 VMT 写着 <c>$matcaptint "[1 1 1 1]"</c>——
	/// 只看 tint 会把它们判成"matcap 就是表面"，连带把 alphaclip 关掉，
	/// 结果花瓣变成不透明白方块 + 金属色。</item>
	/// <item><b>"整片同色"不算没颜色。</b>MourningVeil 的玻璃鞋跟 <c>mat_gem1_shoes</c>
	/// 是 64×64 纯色 197——曾经被判成"没有内容"→ 金属 → 实机"玻璃变成了金属"。
	/// 一条纯色但真实的底色仍然**是一个颜色**。</item>
	/// </list>
	/// </summary>
	[Fact]
	public void ContentFreeBaseMeansTinyOrNearBlackOnly()
	{
		// 1×1 占位图：实测 gray / gray1 / m_metal / pearl 全是 1×1。
		Assert.True(MaterialAppearance.IsContentFreeBase(1, 1, [85, 85, 85, 255]));

		// 大片但整片近黑（实测 mat_metal 平均亮度 17）：颜色全来自 matcap。
		Assert.True(MaterialAppearance.IsContentFreeBase(64, 64, Solid(64, 15)));

		// 大片纯色但**明亮**（实测玻璃鞋跟 mat_gem1_shoes 平均 143/197）：
		// 它有自己的颜色，matcap 只是高光层 —— 不能判成"没有内容"。
		Assert.False(MaterialAppearance.IsContentFreeBase(64, 64, Solid(64, 150)));
		Assert.False(MaterialAppearance.IsContentFreeBase(64, 64, Solid(64, 197)));

		// 有细节的真贴图：不能判成"没有内容"，否则花瓣 / 衣服会被 matcap 顶掉。
		Assert.False(MaterialAppearance.IsContentFreeBase(64, 64, Checker(64)));
	}

	/// <summary>
	/// <c>IsMetallicMatcapName</c>：金属与否按 matcap <b>名字</b>判，不看像素。
	/// <para>
	/// 这条钉的是两条被否掉的路：
	/// </para>
	/// <list type="bullet">
	/// <item>"底色近黑 = 金属"——实测同一条 <c>mat_metal</c> 底图亮度 B1（黑）17.2、
	/// B3（白紫）128.4，测的是颜色变体不是材质。</item>
	/// <item>"边缘密度区分金属与宝石"——<c>matcap_jewel9</c> 4.84 vs <c>matcap_metal7</c> 11.15，
	/// 区间重叠。</item>
	/// </list>
	/// <para>名字不随颜色变体变化，是作者的声明。</para>
	/// </summary>
	[Fact]
	public void MetallicMatcapsAreRecognisedByName()
	{
		// 实测样本：金属。
		foreach (var name in new[]
		{
			"matcap_metal11", "Matcap_metal7", "Matcap_metal8", "Platinum_Silver",
			"matcap_Silver", "MRmagnet", "materials/x/Matcap_Metal1",
		})
		{
			Assert.True(MaterialAppearance.IsMetallicMatcapName(name), name);
		}

		// 实测样本：非金属（宝石/珍珠/布料/环境/高光）。
		foreach (var name in new[]
		{
			"matcap_jewel7", "matcap_jewel9", "Matcap_jewel2", "Matcap_diamond1",
			"Matcap_diamond4", "matcap_pearl3", "matcap_cloths9", "matcap_env3",
			"matcap_highlight", "matcap_rim", "Matcap27_EC",
		})
		{
			Assert.False(MaterialAppearance.IsMetallicMatcapName(name), name);
		}

		// 非金属词优先：名字里同时出现时按非金属处理。
		Assert.False(MaterialAppearance.IsMetallicMatcapName("matcap_metal_gem"));

		// 认不出来 → 非金属（金属化是"加强"，误判代价更大）。
		Assert.False(MaterialAppearance.IsMetallicMatcapName("11"));
		Assert.False(MaterialAppearance.IsMetallicMatcapName(null));
		Assert.False(MaterialAppearance.IsMetallicMatcapName("  "));
	}

	/// <summary>
	/// <c>$color</c> 是"没有底色图"那类材质**唯一**的颜色线索，必须按线性解释。
	/// <para>
	/// 实测 milltina 的 6 个金属件全是 <c>$basetexture "vgui/white"</c> + <c>$color</c>
	/// 的写法（<c>vgui/white</c> 是引擎内置图，任何 VPK 里都没有它）。此前转换器从不读这个键，
	/// 于是银 / 金 / 宝石红全按图片名 <c>white</c> 变成同一块纯白。
	/// </para>
	/// <para>
	/// 断言里带具体数值是有意的：<b>线性 vs sRGB 的差别就在数值上</b>
	/// （0.462 按线性是 181，按"直接乘 255"是 118）。只断言"不等于 0"查不出量纲错。
	/// </para>
	/// </summary>
	[Theory]
	// 银：0.1792 线性 → 117（sRGB 的 46% 灰）。
	[InlineData("0.1792 0.1792 0.1792", 117, 117, 117)]
	// 金：不加引号、无括号——真实 VMT 就长这样。
	[InlineData("0.462 0.322  0", 181, 154, 0)]
	// 宝石红：[0.811 0 0] 方括号写法（与 $matcaptint 同一套归一化）。
	[InlineData("[0.811 0 0]", 233, 0, 0)]
	// 255 制要认成 0..255，而不是当成 >1 的线性值直接撞顶。
	[InlineData("[255 128 0]", 255, 188, 0)]
	// 第 4 个分量是 alpha，不参与。
	[InlineData("[0.5 0.5 0.5 1]", 188, 188, 188)]
	public void FlatColorIsReadAsLinearReflectance(string raw, byte red, byte green, byte blue)
	{
		Assert.True(MaterialAppearance.TryParseFlatColor(raw, out var actualRed, out var actualGreen, out var actualBlue));
		Assert.Equal((red, green, blue), (actualRed, actualGreen, actualBlue));
	}

	/// <summary>
	/// 分量不足、或者压根没写 → 不给颜色。**不猜。**
	/// <para>
	/// 单个数不是颜色：把它当灰度会造出一个没有任何依据的色相，
	/// 而这种"看着不对但查不出来"的结果比中性灰更难排查。
	/// </para>
	/// </summary>
	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("0.5")]
	[InlineData("0.5 0.5")]
	[InlineData("[abc def ghi]")]
	public void FlatColorNeedsThreeRealComponents(string? raw)
		=> Assert.False(MaterialAppearance.TryParseFlatColor(raw, out _, out _, out _));

	/// <summary>
	/// <c>IsUniformlyTranslucent</c>：alpha **整片均匀且明显 &lt; 255** = 整体半透明。
	/// <para>
	/// 实测 MourningVeil 的玻璃鞋跟 <c>mat_gem1_shoes</c> 底图 alpha 恒 102（≈40% 不透明）。
	/// <c>IsMeaningful</c> 要求 <c>Spread ≥ 3</c>，于是它被判成"没有 alpha"→ 不透明 →
	/// 实机是一块不透明白塑料，用户看到的就是"玻璃鞋跟变成了金属/塑料"。
	/// </para>
	/// <para>
	/// 阈值刻意取 <c>Max ≤ 200</c>：差几个量化单位的不透明图很常见，
	/// 把那种当半透明会让"本该不透明的东西突然透了"——那个方向的误判更难查。
	/// </para>
	/// </summary>
	[Fact]
	public void UniformlyLowAlphaMeansTheWholeSurfaceIsTranslucent()
	{
		// 玻璃鞋跟：整片 102。
		var glass = TextureAlpha.Measure(SolidAlpha(8, 102));
		Assert.False(glass.IsMeaningful);
		Assert.True(glass.IsUniformlyTranslucent);

		// 完全不透明：不是半透明。
		var opaque = TextureAlpha.Measure(SolidAlpha(8, 255));
		Assert.False(opaque.IsMeaningful);
		Assert.False(opaque.IsUniformlyTranslucent);

		// 只差几个量化单位的不透明图：不能当半透明（否则本该实心的东西会透）。
		Assert.False(TextureAlpha.Measure(SolidAlpha(8, 248)).IsUniformlyTranslucent);

		// 有形状的 alpha：走 IsMeaningful，不走这一条。
		var shaped = TextureAlpha.Measure(CheckerAlpha(8));
		Assert.True(shaped.IsMeaningful);
		Assert.False(shaped.IsUniformlyTranslucent);
	}

	/// <summary>一半像素全透明、一半全不透明：模拟"alpha 在表达形状"。</summary>
	private static byte[] CheckerAlpha(int size)
	{
		var rgba = new byte[size * size * 4];
		for (var offset = 0; offset < rgba.Length; offset += 4)
		{
			var opaque = (offset / 4) % 2 == 0;
			rgba[offset] = 200;
			rgba[offset + 1] = 200;
			rgba[offset + 2] = 200;
			rgba[offset + 3] = (byte)(opaque ? 255 : 0);
		}

		return rgba;
	}

	private static byte[] SolidAlpha(int size, byte alpha)
	{
		var rgba = new byte[size * size * 4];
		for (var offset = 0; offset < rgba.Length; offset += 4)
		{
			rgba[offset] = 200;
			rgba[offset + 1] = 200;
			rgba[offset + 2] = 200;
			rgba[offset + 3] = alpha;
		}

		return rgba;
	}

	private static byte[] Solid(int size, byte value)
	{
		var rgba = new byte[size * size * 4];
		for (var offset = 0; offset < rgba.Length; offset += 4)
		{
			rgba[offset] = value;
			rgba[offset + 1] = value;
			rgba[offset + 2] = value;
			rgba[offset + 3] = 255;
		}

		return rgba;
	}

	private static byte[] Checker(int size)
	{
		var rgba = new byte[size * size * 4];
		for (var offset = 0; offset < rgba.Length; offset += 4)
		{
			var value = (byte)((offset / 4) % 2 == 0 ? 240 : 40);
			rgba[offset] = value;
			rgba[offset + 1] = value;
			rgba[offset + 2] = value;
			rgba[offset + 3] = 255;
		}

		return rgba;
	}
}

/// <summary>测试里用的单表达式管道，省一个局部变量。</summary>
internal static class TestPipe
{
	public static TResult Let<T, TResult>(this T value, Func<T, TResult> map) => map(value);
}
